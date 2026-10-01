using System.Collections.Generic;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// Procedurally generated PBR texture sets.
    ///
    /// The slice is built without content assets, and flat-coloured untextured boxes are
    /// the single biggest thing keeping it from reading as a real place: large surfaces have
    /// no variation, no surface relief and no roughness break-up, so they look like painted
    /// cardboard no matter how good the lighting is.
    ///
    /// Rather than ship image files, this generates albedo, normal, occlusion and smoothness
    /// from a height field at load time. The results are tiling 512x512 sets, which is enough
    /// to break up a plaster wall or a concrete platform convincingly at the distances this
    /// game is played at. Swapping in authored PBR assets later means replacing this class
    /// and nothing else.
    ///
    /// The height field is the single source for all four maps, which is what keeps them
    /// reading as one material: the albedo shades from it, the normal map is its gradient,
    /// the occlusion term is its local curvature, and the roughness is nudged by its slope.
    ///
    /// The noise is domain-warped. A plain fractal sum produces features that align with the
    /// texture axes and reads as a repeating motif; warping the sample position by another
    /// fractal breaks that alignment and is what makes the result read as erosion, grain or
    /// pitting rather than as noise.
    /// </summary>
    public static class TriangleProceduralTextures
    {
        /// <summary>
        /// Resolution of every generated map.
        ///
        /// 512 rather than the previous 256. At 256 a wall six metres across tiled at one
        /// repeat per metre showed about five texels over the whole surface, so all of the
        /// grain, pitting and relief this file generates was invisible in-game - the cost
        /// was being paid for nothing. 512 doubles the pixels and the generation time, and
        /// is still a few milliseconds per surface kind.
        /// </summary>
        private const int Size = 512;

        /// <summary>
        /// Multiplier applied to a normal map's gradient.
        ///
        /// Uniform per surface kind. A single constant rather than a per-octave one, because
        /// a constant is what makes the relief read as a physical depth rather than as a
        /// stylised ripple.
        /// </summary>
        private static readonly Dictionary<SurfaceKind, SurfaceSet> Cache = new Dictionary<SurfaceKind, SurfaceSet>();

        /// <summary>What a generated surface is made of. Drives the noise character.</summary>
        public enum SurfaceKind
        {
            /// <summary>Painted plaster: fine grain, gentle mottling, very matte.</summary>
            Plaster,

            /// <summary>Poured concrete: coarse blotching, pitting, slightly glossier.</summary>
            Concrete,

            /// <summary>Wood: directional grain along U, moderate roughness variation.</summary>
            Wood,

            /// <summary>Fabric: tight weave, soft, high roughness.</summary>
            Fabric,

            /// <summary>Grass: clumped blades, high-frequency, for terrain splat layers.</summary>
            Grass,

            /// <summary>Loose soil: fine crumb with scattered pebbles.</summary>
            Dirt,

            /// <summary>Bare rock: fractured, low-frequency, coarse normal relief.</summary>
            Rock
        }

        /// <summary>A generated albedo/normal/occlusion/smoothness quartet.</summary>
        public class SurfaceSet
        {
            public Texture2D Albedo { get; internal set; }
            public Texture2D Normal { get; internal set; }

            /// <summary>
            /// Baked cavity occlusion, derived from the height field's curvature.
            ///
            /// Screen-space occlusion cannot recover this. SSAO works on rendered depth, and
            /// a normal map implies relief the geometry does not have, so the pitting and
            /// grain that make a surface read as a material are invisible to it. This map is
            /// what puts the darkness back into those creases.
            /// </summary>
            public Texture2D Occlusion { get; internal set; }

            public Texture2D Smoothness { get; internal set; }

            /// <summary>
            /// The albedo as raw pixels, kept only for the surfaces that something downstream
            /// has to recolour.
            ///
            /// A terrain layer has no colour property, so tinting one means writing a new
            /// texture - and the uploaded albedo is deliberately not readable, so that copy
            /// cannot be made by reading it back. Holding the pixel array costs 1 MB of
            /// managed memory for the handful of surface kinds that need it, against several
            /// MB for keeping every map's CPU copy resident.
            /// </summary>
            public Color32[] AlbedoPixels { get; internal set; }
        }

        /// <summary>Builds, or returns cached, the texture set for a surface kind.</summary>
        public static SurfaceSet Get(SurfaceKind kind)
        {
            if (!Cache.TryGetValue(kind, out SurfaceSet cached) || cached == null)
            {
                // All of this lands on the main thread inside the environment builder's
                // Awake, so it shows up as a stutter on a frame the player is meant to be
                // looking at. Timed once per kind in development builds so the real cost on
                // the target machine is visible rather than guessed at.
                bool timed = Debug.isDebugBuild;
                long started = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

                cached = Build(kind);
                Cache[kind] = cached;

                if (timed)
                {
                    double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0
                                / System.Diagnostics.Stopwatch.Frequency;
                    TotalGeneratedMilliseconds += ms;
                    UnityEngine.Debug.Log(
                        $"[Triangle] Generated {kind} surface set in {ms:0.0} ms " +
                        $"(running total {TotalGeneratedMilliseconds:0.0} ms across {Cache.Count} kind(s)).");
                }
            }

            return cached;
        }

        /// <summary>
        /// Milliseconds spent generating surface sets this session. Development builds only;
        /// a lower bound on the dream's first-frame cost.
        /// </summary>
        public static double TotalGeneratedMilliseconds { get; private set; }

        /// <summary>The base smoothness a surface kind has with no smoothness map bound.</summary>
        public static float BaseSmoothnessFor(SurfaceKind kind) => BaseSmoothness(kind);

        /// <summary>
        /// Releases every generated texture.
        ///
        /// The cache is static, so it survives an editor play session entered without a
        /// domain reload and pins sixteen 512x512 textures per session. Called from
        /// TriangleShapes' cache reset.
        /// </summary>
        public static void ClearCache()
        {
            foreach (SurfaceSet set in Cache.Values)
            {
                if (set == null)
                {
                    continue;
                }

                DestroyTexture(set.Albedo);
                DestroyTexture(set.Normal);
                DestroyTexture(set.Occlusion);
                DestroyTexture(set.Smoothness);
            }

            Cache.Clear();
            LatticeCache.Clear();

            // Cleared with the rest of the per-session state, so a play session entered
            // without a domain reload does not carry last session's pending pixel-retention
            // requests into this one.
            _pixelRetained.Clear();
            TotalGeneratedMilliseconds = 0d;
        }

        private static void DestroyTexture(Object texture)
        {
            if (texture == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                Object.Destroy(texture);
            }
            else
            {
                Object.DestroyImmediate(texture);
            }
        }

        private static SurfaceSet Build(SurfaceKind kind)
        {
            // Height is the shared source for every map below.
            var height = new float[Size, Size];

            switch (kind)
            {
                case SurfaceKind.Plaster:
                    FillPlaster(height);
                    break;
                case SurfaceKind.Concrete:
                    FillConcrete(height);
                    break;
                case SurfaceKind.Wood:
                    FillWood(height);
                    break;
                case SurfaceKind.Grass:
                    FillGrass(height);
                    break;
                case SurfaceKind.Dirt:
                    FillDirt(height);
                    break;
                case SurfaceKind.Rock:
                    FillRock(height);
                    break;
                default:
                    FillFabric(height);
                    break;
            }

            Color32[] albedoPixels = SampleAlbedo(kind, height);

            return new SurfaceSet
            {
                Albedo = CreateTexture($"TriangleAlbedo_{kind}", albedoPixels, TextureWrapMode.Repeat, false),
                Normal = CreateTexture($"TriangleNormal_{kind}", SampleNormal(height, NormalStrength(kind)),
                    TextureWrapMode.Repeat, true),
                Occlusion = CreateTexture($"TriangleOcclusion_{kind}", SampleOcclusion(height),
                    TextureWrapMode.Repeat, true),
                Smoothness = CreateTexture($"TriangleSmooth_{kind}", SampleSmoothness(kind, height),
                    TextureWrapMode.Repeat, true),

                // Retained only for the surfaces a terrain layer recolours. This is the same
                // array the albedo above was built from, so keeping it costs no second pass
                // over the height field and no second allocation.
                AlbedoPixels = _pixelRetained.Contains(kind) ? albedoPixels : null
            };
        }

        private static readonly HashSet<SurfaceKind> _pixelRetained = new HashSet<SurfaceKind>();

        /// <summary>
        /// Asks for the next build of these surfaces to retain their albedo pixels.
        ///
        /// A per-surface request rather than a global switch, because the pixels are only
        /// worth holding for the surfaces a terrain layer actually recolours - and the slice
        /// spends most of its time in the bedroom, where retained arrays for a yard the
        /// player may never see are pure waste.
        ///
        /// Must be called before the surface is generated. Silently does nothing if it is
        /// already cached, because regenerating an unused surface to attach an array nothing
        /// has read yet would cost more than the memory it saves.
        /// </summary>
        public static void RequestTerrainPixels(SurfaceKind kind)
        {
            if (Cache.TryGetValue(kind, out SurfaceSet existing) && existing != null)
            {
                return;
            }

            _pixelRetained.Add(kind);
        }

        private static float NormalStrength(SurfaceKind kind)
        {
            switch (kind)
            {
                case SurfaceKind.Concrete: return 2.2f;
                case SurfaceKind.Rock: return 3.0f;
                case SurfaceKind.Dirt: return 1.9f;
                case SurfaceKind.Grass: return 1.4f;
                case SurfaceKind.Wood: return 1.2f;
                case SurfaceKind.Fabric: return 1.6f;
                default: return 0.8f; // plaster
            }
        }

        private static float BaseSmoothness(SurfaceKind kind)
        {
            switch (kind)
            {
                case SurfaceKind.Concrete: return 0.34f;
                case SurfaceKind.Wood: return 0.40f;
                case SurfaceKind.Fabric: return 0.12f;
                case SurfaceKind.Grass: return 0.10f;
                case SurfaceKind.Dirt: return 0.14f;

                // Rock is the one outdoor surface that reads as damp, which is what stops a
                // grey hillside from looking like grey plastic under moonlight.
                case SurfaceKind.Rock: return 0.30f;
                default: return 0.18f; // plaster
            }
        }

        // --- Height field generators -------------------------------------------
        //
        // Each generator resolves its octaves once, outside the pixel loop. Looking them up
        // per pixel cost a dictionary probe on every sample, which measured at roughly the
        // price of the hashes the lattice cache saves.
        //
        // Every period below divides Size exactly, so the generated textures tile without a
        // seam, and each period is comfortably finer than the texture it drives - that is
        // what makes an octave read as grain rather than as a repeating motif.

        private static void FillPlaster(float[,] height)
        {
            // Two octaves of fine grain plus a slow large-scale mottle, which is what stops a
            // wall reading as one flat value under a moving light.
            Lattice coarseGrain = GetLattice(64, 101);
            Lattice fineGrain = GetLattice(192, 202);
            Lattice mottle = GetLattice(8, 303);
            Lattice warpA = GetLattice(16, 304);
            Lattice warpB = GetLattice(32, 305);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    // Domain warp. The sample position is displaced by two more fractal
                    // octaves before the surface is read, which shears the features off the
                    // texture's own axes. Plaster applied by hand has no axis.
                    Vector2 warp = Warp(warpA, warpB, x, y, 12f);
                    float wx = x + warp.x;
                    float wy = y + warp.y;

                    float fine = coarseGrain.Sample(wx * 0.25f, wy * 0.25f) * 0.5f
                                 + fineGrain.Sample(wx * 0.75f, wy * 0.75f) * 0.25f;
                    float slow = mottle.Sample(wx * 1f / 32f, wy * 1f / 32f) * 0.6f;
                    height[x, y] = fine + slow * 0.35f;
                }
            }
        }

        private static void FillConcrete(float[,] height)
        {
            Lattice blotch = GetLattice(8, 404);
            Lattice grit = GetLattice(128, 505);
            Lattice pits = GetLattice(256, 606);
            Lattice warp = GetLattice(16, 407);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Vector2 offset = Warp(warp, null, x, y, 8f);
                    float wx = x + offset.x;
                    float wy = y + offset.y;

                    float coarse = blotch.Sample(wx * 1f / 32f, wy * 1f / 32f) * 0.7f;
                    float fine = grit.Sample(wx * 0.5f, wy * 0.5f) * 0.35f;

                    // Pits are a power of the noise, so only the top of the range registers
                    // as a pit and the rest stays as flat surface. Aggregate and blowholes
                    // in poured concrete are sparse, not uniformly distributed.
                    float pitted = Mathf.Pow(pits.Sample(wx, wy), 4f) * 0.6f;
                    height[x, y] = coarse + fine - pitted;
                }
            }
        }

        private static void FillWood(float[,] height)
        {
            // Grain runs along U. Stretched noise plus sharp ring lines reads as timber at a
            // glance, which is all a bed frame or a desk needs. The ring term is driven from
            // the noise itself rather than from a raw pixel sine, so it inherits the noise's
            // tiling period and cannot introduce a seam.
            Lattice stretchedGrain = GetLattice(16, 707);
            Lattice fibre = GetLattice(128, 808);
            Lattice warp = GetLattice(24, 709);
            float ringPhase = 2f * Mathf.PI / Size * 3f;

            for (int y = 0; y < Size; y++)
            {
                float ringBias = y * ringPhase;
                for (int x = 0; x < Size; x++)
                {
                    // Warped only across the grain. Rings run along U, so displacing the
                    // sample position warps the ring lines themselves and gives the slight
                    // wander that sawn timber has.
                    float wx = x + Warp(warp, null, x, y, 6f).x;

                    float stretched = stretchedGrain.Sample(wx * 1f / 16f, y);
                    float rings = Mathf.Sin(stretched * 22f + ringBias) * 0.5f + 0.5f;
                    float threads = fibre.Sample(wx * 0.5f, y) * 0.18f;
                    height[x, y] = rings * 0.55f + threads;
                }
            }
        }

        private static void FillFabric(float[,] height)
        {
            // Warp and weft run at an exact fraction of the texture, so the weave tiles. An
            // earlier version used a raw sin(x * 1.6f): over 512 pixels that is 819.2
            // radians, which is not a whole number of cycles, so the weave had a visible
            // discontinuity along one edge of every fabric surface.
            const float threads = 48f;
            float radiansPerThread = 2f * Mathf.PI / (Size / threads);
            Lattice slub = GetLattice(128, 909);
            Lattice wander = GetLattice(32, 910);

            for (int y = 0; y < Size; y++)
            {
                // The weft is constant along x, so it is hoisted out of the inner loop.
                float weft = Mathf.Sin(y * radiansPerThread) * 0.5f + 0.5f;
                for (int x = 0; x < Size; x++)
                {
                    // A slow wander on both thread sets. Perfectly regular weave is a
                    // graphic, not a textile.
                    Vector2 jitter = Warp(wander, null, x, y, 4f);
                    float warp = Mathf.Sin((x + jitter.x) * radiansPerThread) * 0.5f + 0.5f;
                    float weftHere = Mathf.Sin((y + jitter.y) * radiansPerThread) * 0.5f + 0.5f;
                    float weave = Mathf.Max(warp, weftHere);
                    float irregular = slub.Sample(x * 0.5f, y * 0.5f) * 0.2f;
                    height[x, y] = weave * 0.6f + irregular;
                }
            }
        }

        // --- Outdoor surfaces ---------------------------------------------------
        //
        // These exist for the terrain splat layers rather than for architecture, so they lean
        // harder on the low-frequency octaves than the interior surfaces do: a grass texture is
        // read at a glancing angle across many metres, where the interior's tight weave is
        // never seen.

        private static void FillGrass(float[,] height)
        {
            // Clumps, then blade-scale break-up. The clump octave is what stops a lawn reading
            // as a uniform green field, and it is deliberately the dominant term.
            Lattice clumps = GetLattice(16, 1101);
            Lattice patches = GetLattice(64, 1102);
            Lattice blades = GetLattice(256, 1103);
            Lattice warp = GetLattice(24, 1104);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Vector2 offset = Warp(warp, null, x, y, 16f);
                    float wx = x + offset.x;
                    float wy = y + offset.y;

                    float clump = clumps.Sample(wx * 1f / 16f, wy * 1f / 16f) * 0.6f;
                    float patch = patches.Sample(wx * 0.25f, wy * 0.25f) * 0.25f;
                    float blade = blades.Sample(wx, wy) * 0.15f;
                    height[x, y] = clump + patch + blade;
                }
            }
        }

        private static void FillDirt(float[,] height)
        {
            // Loose soil: a soft base, a crumb octave, and pebbles that read as distinct bumps
            // rather than as noise.
            Lattice baseOctave = GetLattice(8, 1201);
            Lattice crumb = GetLattice(64, 1202);
            Lattice pebble = GetLattice(128, 1203);
            Lattice warp = GetLattice(16, 1204);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Vector2 offset = Warp(warp, null, x, y, 10f);
                    float wx = x + offset.x;
                    float wy = y + offset.y;

                    float soft = baseOctave.Sample(wx * 1f / 8f, wy * 1f / 8f) * 0.4f;
                    float grain = crumb.Sample(wx * 0.25f, wy * 0.25f) * 0.45f;

                    // Raised to a power so only the top of the range registers as a pebble,
                    // leaving the rest of the surface as loose soil.
                    float stones = Mathf.Pow(pebble.Sample(wx * 0.5f, wy * 0.5f), 6f) * 0.6f;
                    height[x, y] = soft + grain + stones;
                }
            }
        }

        private static void FillRock(float[,] height)
        {
            // Fractured stone. The strata term is the important one: a pure noise field reads
            // as gravel at any distance, whereas a strong directional component reads as
            // bedded rock. Sampling the lattice with a different scale on each axis shears the
            // bedding off the texture's own axes.
            Lattice strata = GetLattice(8, 1301);
            Lattice fracture = GetLattice(32, 1302);
            Lattice detail = GetLattice(128, 1303);
            Lattice warp = GetLattice(16, 1304);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Vector2 offset = Warp(warp, null, x, y, 20f);
                    float wx = x + offset.x;
                    float wy = y + offset.y;

                    float bedded = strata.Sample(wx * 1f / 32f, wy * 1f / 8f) * 0.55f;

                    // Ridged, so the fracture lines are creases rather than blobs.
                    float cracked = Mathf.Pow(1f - Mathf.Abs(fracture.Sample(wx * 1f / 16f, wy * 1f / 16f) * 2f - 1f), 4f) * 0.5f;
                    float grainy = detail.Sample(wx * 0.5f, wy * 0.5f) * 0.2f;
                    height[x, y] = bedded + cracked + grainy;
                }
            }
        }

        /// <summary>
        /// Displaces a sample position by one or two fractal octaves.
        ///
        /// This is the whole reason the surfaces look like materials rather than like noise.
        /// A fractal sum evaluated at a uniform grid has features whose shapes correlate with
        /// the grid; feeding the result back as a coordinate offset removes that correlation,
        /// so the same octave budget produces something organic. It is also why this is one
        /// helper rather than being inlined per generator - every surface needs it, and the
        /// amount matters as much as the frequency.
        /// </summary>
        private static Vector2 Warp(Lattice first, Lattice second, int x, int y, float amount)
        {
            // Two octaves at different periods, incommensurate, so the warp itself does not
            // repeat at any scale the texture uses.
            float ax = first.Sample(x * 0.02f, y * 0.02f) - 0.5f;
            float ay = first.Sample(x * 0.02f + 37f, y * 0.02f + 11f) - 0.5f;

            float total = amount;
            if (second != null)
            {
                float bx = second.Sample(x * 0.06f, y * 0.06f) - 0.5f;
                float by = second.Sample(x * 0.06f + 19f, y * 0.06f + 53f) - 0.5f;

                ax += bx * 0.5f;
                ay += by * 0.5f;
                total *= 1.5f;
            }

            return new Vector2(ax * total, ay * total);
        }

        // --- Map builders -------------------------------------------------------

        /// <summary>
        /// Albedo, as a brightness variation around 1.0.
        ///
        /// The contrast here is the single most important number in this file for how the
        /// game looks. The previous version used ±16% for everything except wood, which is
        /// below the threshold of visibility on a surface that is already very dark and lit
        /// by a single weak source - the texture was generated, uploaded, sampled and
        /// multiplied into the albedo, and none of it reached the screen. Raised to ±34% for
        /// the interior surfaces, which is still a plausible amount of variation for a real
        /// material and is finally visible under this lighting.
        /// </summary>
        private static Color32[] SampleAlbedo(SurfaceKind kind, float[,] height)
        {
            var pixels = new Color32[Size * Size];
            float contrast = ContrastFor(kind);

            // Normalise the height field against its own range rather than assuming it
            // straddles 0.5. The plaster generator lands wholly above the midpoint, so a
            // fixed centre mapped it to 0.92..1.0 white - a variation of a few percent that
            // no amount of lighting could reveal. The extra pass costs nothing next to the
            // four map builds that follow.
            FindRange(height, out float min, out float max);
            float span = Mathf.Max(0.0001f, max - min);

            // Slightly warm in the highlights and slightly cool in the shadows, the way a real
            // diffuse surface behaves. A pure luminance ramp reads as greyscale; this reads as
            // a material.
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float normalised = (height[x, y] - min) / span;
                    float shade = 1f + (normalised - 0.5f) * contrast;

                    // Colour temperature drift across the same field.
                    float temperature = (normalised - 0.5f) * 0.10f;

                    byte r = (byte)(Mathf.Clamp01(shade * (1f + temperature)) * 255f);
                    byte g = (byte)(Mathf.Clamp01(shade) * 255f);
                    byte b = (byte)(Mathf.Clamp01(shade * (1f - temperature)) * 255f);
                    pixels[y * Size + x] = new Color32(r, g, b, 255);
                }
            }

            return pixels;
        }

        private static float ContrastFor(SurfaceKind kind)
        {
            switch (kind)
            {
                case SurfaceKind.Wood: return 0.44f;
                case SurfaceKind.Rock: return 0.40f;
                case SurfaceKind.Concrete: return 0.34f;
                case SurfaceKind.Dirt: return 0.38f;
                case SurfaceKind.Grass: return 0.42f;
                case SurfaceKind.Fabric: return 0.26f;
                default: return 0.30f; // plaster
            }
        }

        private static void FindRange(float[,] field, out float min, out float max)
        {
            min = float.MaxValue;
            max = float.MinValue;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float v = field[x, y];
                    if (v < min) min = v;
                    if (v > max) max = v;
                }
            }
        }

        /// <summary>
        /// A tangent-space normal map from the height field's gradient.
        ///
        /// Central differences on the wrapped height field, so no tangents are needed on the
        /// mesh to author the map. The gradient is computed at float precision from the field
        /// rather than read back from the quantised albedo, which avoids the 8-bit terracing
        /// that reading back an encoded map produces.
        /// </summary>
        private static Color32[] SampleNormal(float[,] height, float strength)
        {
            var pixels = new Color32[Size * Size];

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float left = height[(x - 1 + Size) % Size, y];
                    float right = height[(x + 1) % Size, y];
                    float down = height[x, (y - 1 + Size) % Size];
                    float up = height[x, (y + 1) % Size];

                    var normal = new Vector3((left - right) * strength, (down - up) * strength, 1f).normalized;
                    pixels[y * Size + x] = new Color32(
                        (byte)((normal.x * 0.5f + 0.5f) * 255f),
                        (byte)((normal.y * 0.5f + 0.5f) * 255f),
                        (byte)((normal.z * 0.5f + 0.5f) * 255f),
                        255);
                }
            }

            return pixels;
        }

        /// <summary>
        /// Baked cavity occlusion from the height field's second derivative.
        ///
        /// A point sitting in a crease is darker than one sitting on a ridge, and the sign of
        /// the Laplacian says which: positive on a ridge, negative in a valley. Multiplying
        /// the albedo by that term is what makes pitting, grain and the weave of fabric read
        /// as depth rather than as a normal-map detail you can only see at the right angle.
        ///
        /// This is the map that carries most of the realism, because screen-space occlusion
        /// cannot see any of it - the relief implied by these maps is not in the depth buffer.
        /// </summary>
        private static Color32[] SampleOcclusion(float[,] height)
        {
            var pixels = new Color32[Size * Size];

            FindRange(height, out float min, out float max);
            float span = Mathf.Max(0.0001f, max - min);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    // Four-neighbour Laplacian on the wrapped field.
                    float centre = height[x, y];
                    float laplacian =
                        height[(x - 1 + Size) % Size, y] +
                        height[(x + 1) % Size, y] +
                        height[x, (y - 1 + Size) % Size] +
                        height[x, (y + 1) % Size] -
                        centre * 4f;

                    // Scaled into 0..1 around a mid grey. The divisor is a multiple of the
                    // field's own span, so this behaves the same whatever the generator's
                    // absolute range happens to be.
                    float cavity = 1f - Mathf.Clamp01(-laplacian / span * 6f);

                    // Modulated by height so broad slopes also darken slightly at their base,
                    // which is what gives large flat surfaces a sense of settling.
                    float slopeShade = Mathf.Lerp(0.86f, 1f, (centre - min) / span);

                    byte v = (byte)(Mathf.Clamp01(cavity * slopeShade) * 255f);
                    pixels[y * Size + x] = new Color32(v, v, v, 255);
                }
            }

            return pixels;
        }

        private static Color32[] SampleSmoothness(SurfaceKind kind, float[,] height)
        {
            var pixels = new Color32[Size * Size];
            float baseSmoothness = BaseSmoothness(kind);
            float variation = kind == SurfaceKind.Concrete ? 0.28f : 0.16f;

            FindRange(height, out float min, out float max);
            float span = Mathf.Max(0.0001f, max - min);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float normalised = (height[x, y] - min) / span;

                    // Rougher in the pits, glossier on the high points: worn surfaces are not
                    // uniformly shiny. Normalising against the field's own range rather than
                    // assuming 0.5 matters here, because the plaster generator sits well above
                    // the midpoint and a fixed centre would have pinned its whole variation
                    // into the top of the range.
                    float s = Mathf.Clamp01(baseSmoothness + (normalised - 0.5f) * variation);

                    byte v = (byte)Mathf.RoundToInt(s * 255f);
                    pixels[y * Size + x] = new Color32(v, v, v, 255);
                }
            }

            return pixels;
        }

        /// <summary>
        /// Creates one generated texture.
        ///
        /// <paramref name="linear"/> marks the maps that carry data rather than colour -
        /// normal, occlusion and smoothness. Those must not be sRGB-decoded, because the
        /// shader reads them as raw values and a gamma decode silently rescales every one of
        /// them: a smoothness of 0.5 arrives as 0.21, and an occlusion of 0.8 as 0.6. Getting
        /// this backwards is a classic realism killer and is invisible in a still frame of a
        /// dark scene.
        /// </summary>
        private static Texture2D CreateTexture(string name, Color32[] pixels, TextureWrapMode wrap, bool linear)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, true, linear)
            {
                name = name,
                wrapMode = wrap,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 16,

                // Nothing ever reads these back. Without this the CPU-side copy of all four
                // maps per surface kind stays resident for the whole session - megabytes that
                // exist only because Apply was asked to keep readable data nobody can reach.
                hideFlags = HideFlags.HideAndDontSave
            };

            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        // --- Noise ---------------------------------------------------------------

        private static float Hash(int x, int y, int salt)
        {
            unchecked
            {
                int h = x * 374761393 + y * 668265263 + salt * 1274126177;
                h = (h ^ (h >> 13)) * 1274126177;
                return ((h ^ (h >> 16)) & 0x7fffffff) / (float)0x7fffffff;
            }
        }

        private static float Smooth(float t) => t * t * (3f - 2f * t);

        /// <summary>
        /// One octave's raw lattice values, precomputed.
        ///
        /// Value noise at a given frequency reads exactly four lattice corners per sample,
        /// and those corners depend only on the integer cell - never on the position inside
        /// it. Evaluating a whole texture from a period-8 lattice therefore performed
        /// 65536 * 4 hashes to produce what is mathematically an 8x8 grid of values, each
        /// reused across a 32x32 pixel cell.
        ///
        /// Caching the grid makes that 81 hashes plus two smoothstep lerps per pixel, and the
        /// result is bit-identical because the same four corners were being re-derived from
        /// the same cells anyway.
        /// </summary>
        private sealed class Lattice
        {
            public readonly int Period;
            public readonly int Stride;
            public readonly float[] Values;

            public Lattice(int period, int salt)
            {
                Period = Mathf.Max(1, period);

                // Period + 1 on each axis so the wrapped final cell has a real far corner to
                // interpolate towards.
                Stride = Period + 1;
                Values = new float[Stride * Stride];

                for (int y = 0; y < Stride; y++)
                {
                    for (int x = 0; x < Stride; x++)
                    {
                        // The last row and column repeat the first, so the octave tiles.
                        int wx = x == Period ? 0 : x;
                        int wy = y == Period ? 0 : y;
                        Values[y * Stride + x] = Hash(wx, wy, salt);
                    }
                }
            }

            /// <summary>Samples this octave at a continuous texture-space position.</summary>
            public float Sample(float x, float y)
            {
                int x0 = Mathf.FloorToInt(x);
                int y0 = Mathf.FloorToInt(y);
                float fx = Smooth(x - x0);
                float fy = Smooth(y - y0);

                int xa = Mod(x0, Period);
                int xb = xa + 1;
                int ya = Mod(y0, Period);
                int yb = ya + 1;

                float v00 = Values[ya * Stride + xa];
                float v10 = Values[ya * Stride + xb];
                float v01 = Values[yb * Stride + xa];
                float v11 = Values[yb * Stride + xb];

                return Mathf.Lerp(Mathf.Lerp(v00, v10, fx), Mathf.Lerp(v01, v11, fx), fy);
            }
        }

        /// <summary>
        /// Lattices for the octaves this surface set needs, keyed by period and salt.
        ///
        /// The bedroom's plaster and the station's concrete share their low-frequency octaves,
        /// and several kinds share grain octaves, so caching across a single environment build
        /// removes a good share of the work again. Bounded by ClearCache, which the shapes
        /// cache reset calls.
        /// </summary>
        private static readonly Dictionary<long, Lattice> LatticeCache = new Dictionary<long, Lattice>();

        private static Lattice GetLattice(int period, int salt)
        {
            long key = ((long)period << 32) | (uint)salt;
            if (!LatticeCache.TryGetValue(key, out Lattice lattice) || lattice == null)
            {
                lattice = new Lattice(period, salt);
                LatticeCache[key] = lattice;
            }

            return lattice;
        }

        private static int Mod(int value, int modulus)
        {
            int r = value % modulus;
            return r < 0 ? r + modulus : r;
        }
    }
}