using System.Collections.Generic;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// Procedurally generated PBR texture sets.
    ///
    /// The slice is built without content assets, and flat-coloured untextured boxes
    /// are the single biggest thing keeping it from reading as a real place: large
    /// surfaces have no variation, no surface relief and no roughness break-up, so they
    /// look like painted cardboard no matter how good the lighting is.
    ///
    /// Rather than ship image files, this generates albedo, normal and smoothness maps
    /// from value noise at load time. The results are tiling 256x256 sets, which is
    /// enough to break up a plaster wall or a concrete platform convincingly at the
    /// distances this game is played at. Swapping in authored PBR assets later means
    /// replacing this class and nothing else.
    /// </summary>
    public static class TriangleProceduralTextures
    {
        private const int Size = 256;

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

        /// <summary>A generated albedo/normal/smoothness triplet.</summary>
        public class SurfaceSet
        {
            public Texture2D Albedo { get; internal set; }
            public Texture2D Normal { get; internal set; }
            public Texture2D Smoothness { get; internal set; }

            /// <summary>
            /// The albedo as raw pixels, kept only for the surfaces that something
            /// downstream has to recolour.
            ///
            /// A terrain layer has no colour property, so tinting one means writing a
            /// new texture - and the uploaded albedo is deliberately not readable, so
            /// that copy cannot be made by reading it back. Holding the pixel array
            /// costs 256 KB of managed memory for the handful of surface kinds that
            /// need it, against several MB for keeping every map's CPU copy resident.
            /// </summary>
            public Color32[] AlbedoPixels { get; internal set; }
        }

        /// <summary>Builds (or returns cached) the texture set for a surface kind.</summary>
        public static SurfaceSet Get(SurfaceKind kind)
        {
            if (!Cache.TryGetValue(kind, out SurfaceSet cached) || cached == null)
            {
                // All of this lands on the main thread inside the environment builder's
                // Awake, so it shows up as a stutter on a frame the player is meant to
                // be looking at. Timed once per kind in development builds so the real
                // cost on the target machine is visible rather than guessed at.
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
        /// Milliseconds spent generating surface sets this session. Development builds
        /// only; a lower bound on the dream's first-frame cost.
        /// </summary>
        public static double TotalGeneratedMilliseconds { get; private set; }

        /// <summary>
        /// Releases every generated texture.
        ///
        /// The cache is static, so it survives an editor play session entered without a
        /// domain reload and pins twelve 256x256 textures per session. Called from
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
                DestroyTexture(set.Smoothness);
            }

            Cache.Clear();
            LatticeCache.Clear();

            // Cleared with the rest of the per-session state, so a play session that
            // entered without a domain reload does not carry last session's pending
            // pixel-retention requests into this one.
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
            // Height is the shared source: albedo shades from it, the normal map is
            // differentiated from it, and roughness is nudged by its local slope. That
            // correlation is what stops the maps looking like unrelated noise.
            float[,] height = new float[Size, Size];

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
                Albedo = CreateTexture($"TriangleAlbedo_{kind}", albedoPixels, TextureWrapMode.Repeat),
                Normal = CreateTexture($"TriangleNormal_{kind}", SampleNormal(height, NormalStrength(kind)), TextureWrapMode.Repeat),
                Smoothness = CreateTexture($"TriangleSmooth_{kind}", SampleSmoothness(kind, height), TextureWrapMode.Repeat),

                // Retained only for the surfaces a terrain layer recolours. This is the
                // same array the texture above was just built from, so keeping it costs
                // no second pass over the height field and no second allocation.
                AlbedoPixels = _pixelRetained.Contains(kind) ? albedoPixels : null
            };
        }

        private static readonly HashSet<SurfaceKind> _pixelRetained = new HashSet<SurfaceKind>();

        /// <summary>
        /// Asks for the next build of these surfaces to retain their albedo pixels.
        ///
        /// A per-surface request rather than a global switch, because the pixels are only
        /// worth holding for the surfaces a terrain layer actually recolours - and the
        /// slice spends most of its time in the bedroom, where 768 KB of retained pixel
        /// arrays for a yard the player may never see is pure waste.
        ///
        /// Must be called before the surface is generated. Silently does nothing if it is
        /// already cached, because regenerating an unused surface to attach an array
        /// nothing has read yet would cost more than the memory it saves. The caller can
        /// check <see cref="SurfaceSet.AlbedoPixels"/> to see whether it got them.
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
                case SurfaceKind.Concrete: return 2.6f;
                case SurfaceKind.Rock: return 3.4f;
                case SurfaceKind.Dirt: return 2.2f;
                case SurfaceKind.Grass: return 1.6f;
                case SurfaceKind.Wood: return 1.4f;
                case SurfaceKind.Fabric: return 1.8f;
                default: return 0.9f; // plaster
            }
        }

        private static float BaseSmoothness(SurfaceKind kind)
        {
            switch (kind)
            {
                case SurfaceKind.Concrete: return 0.32f;
                case SurfaceKind.Wood: return 0.38f;
                case SurfaceKind.Fabric: return 0.10f;
                case SurfaceKind.Grass: return 0.08f;
                case SurfaceKind.Dirt: return 0.12f;
                // Rock is the one outdoor surface that reads as damp, which is what stops
                // a grey hillside from looking like grey plastic under moonlight.
                case SurfaceKind.Rock: return 0.28f;
                default: return 0.16f; // plaster
            }
        }

        // --- Height field generators -------------------------------------------
        //
        // Each generator resolves its octaves once, outside the pixel loop. Looking
        // them up per pixel cost a dictionary probe on every sample, which measured at
        // roughly the price of the hashes the lattice cache saves, and would have made
        // the whole optimisation a wash.
        //
        // Every period below divides Size exactly, so the generated textures tile
        // without a seam, and each period is comfortably finer than the texture it
        // drives - that is what makes an octave read as grain rather than as a
        // repeating motif.

        private static void FillPlaster(float[,] height)
        {
            // Two octaves of fine grain plus a slow large-scale mottle, which is what
            // stops a wall reading as one flat value under a moving light.
            Lattice coarseGrain = GetLattice(64, 101);
            Lattice fineGrain = GetLattice(192, 202);
            Lattice mottle = GetLattice(8, 303);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float fine = coarseGrain.Sample(x * 0.25f, y * 0.25f) * 0.5f
                               + fineGrain.Sample(x * 0.75f, y * 0.75f) * 0.25f;
                    float slow = mottle.Sample(x * 1f / 32f, y * 1f / 32f) * 0.6f;
                    height[x, y] = fine + slow * 0.35f;
                }
            }
        }

        private static void FillConcrete(float[,] height)
        {
            Lattice blotch = GetLattice(8, 404);
            Lattice grit = GetLattice(128, 505);
            Lattice pits = GetLattice(256, 606);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float coarse = blotch.Sample(x * 1f / 32f, y * 1f / 32f) * 0.7f;
                    float fine = grit.Sample(x * 0.5f, y * 0.5f) * 0.35f;
                    float pitted = Mathf.Pow(pits.Sample(x, y), 3f) * 0.5f;
                    height[x, y] = coarse + fine - pitted;
                }
            }
        }

        private static void FillWood(float[,] height)
        {
            // Grain runs along U. Stretched noise plus sharp ring lines reads as timber
            // at a glance, which is all a wooden bed frame or desk needs. The ring term
            // is driven from the noise itself rather than from a raw pixel sine, so it
            // inherits the noise's tiling period and cannot introduce a seam.
            Lattice stretchedGrain = GetLattice(16, 707);
            Lattice fibre = GetLattice(128, 808);
            float ringPhase = 2f * Mathf.PI / Size * 3f;

            for (int y = 0; y < Size; y++)
            {
                float ringBias = y * ringPhase;
                for (int x = 0; x < Size; x++)
                {
                    float stretched = stretchedGrain.Sample(x * 1f / 16f, y);
                    float rings = Mathf.Sin(stretched * 22f + ringBias) * 0.5f + 0.5f;
                    float threads = fibre.Sample(x * 0.5f, y) * 0.18f;
                    height[x, y] = rings * 0.55f + threads;
                }
            }
        }

        private static void FillFabric(float[,] height)
        {
            // Warp and weft run at an exact fraction of the texture, so the weave tiles.
            // The previous version used a raw sin(x * 1.6f): over 256 pixels that is
            // 409.6 radians, which is not a whole number of cycles, so the weave had a
            // visible discontinuity along one edge of every fabric surface.
            const float threads = 32f;
            float radiansPerThread = 2f * Mathf.PI / (Size / threads);
            Lattice slub = GetLattice(128, 909);

            for (int y = 0; y < Size; y++)
            {
                // The weft is constant along x, so it is hoisted out of the inner loop.
                float weft = Mathf.Sin(y * radiansPerThread) * 0.5f + 0.5f;
                for (int x = 0; x < Size; x++)
                {
                    float warp = Mathf.Sin(x * radiansPerThread) * 0.5f + 0.5f;
                    float weave = Mathf.Max(warp, weft);
                    float irregular = slub.Sample(x * 0.5f, y * 0.5f) * 0.2f;
                    height[x, y] = weave * 0.6f + irregular;
                }
            }
        }

        // --- Outdoor surfaces ---------------------------------------------------
        //
        // These exist for the terrain splat layers rather than for architecture, so
        // they lean harder on the low-frequency octaves than the interior surfaces do:
        // a grass texture is read at a glancing angle across many metres, where the
        // interior's tight weave is never seen.

        private static void FillGrass(float[,] height)
        {
            // Clumps, then blade-scale break-up. The clump octave is what stops a lawn
            // reading as a uniform green field, and it is deliberately the dominant term.
            Lattice clumps = GetLattice(16, 1101);
            Lattice patches = GetLattice(64, 1102);
            Lattice blades = GetLattice(256, 1103);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float clump = clumps.Sample(x * 1f / 16f, y * 1f / 16f) * 0.6f;
                    float patch = patches.Sample(x * 0.25f, y * 0.25f) * 0.25f;
                    float blade = blades.Sample(x, y) * 0.15f;
                    height[x, y] = clump + patch + blade;
                }
            }
        }

        private static void FillDirt(float[,] height)
        {
            // Loose soil: a soft base, a crumb octave, and pebbles that read as
            // distinct bumps rather than as noise.
            Lattice baseOctave = GetLattice(8, 1201);
            Lattice crumb = GetLattice(64, 1202);
            Lattice pebble = GetLattice(128, 1203);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float soft = baseOctave.Sample(x * 1f / 8f, y * 1f / 8f) * 0.4f;
                    float grain = crumb.Sample(x * 0.25f, y * 0.25f) * 0.45f;
                    // Raised to a power so only the top of the range registers as a
                    // pebble, leaving the rest of the surface as loose soil.
                    float stones = Mathf.Pow(pebble.Sample(x * 0.5f, y * 0.5f), 6f) * 0.6f;
                    height[x, y] = soft + grain + stones;
                }
            }
        }

        private static void FillRock(float[,] height)
        {
            // Fractured stone. The strata term is the important one: a pure noise field
            // reads as gravel at any distance, whereas a strong directional component
            // reads as bedded rock. Sampling the lattice with a different scale on each
            // axis shears the bedding off the texture's own axes.
            Lattice strata = GetLattice(8, 1301);
            Lattice fracture = GetLattice(32, 1302);
            Lattice detail = GetLattice(128, 1303);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float bedded = strata.Sample(x * 1f / 32f, y * 1f / 8f) * 0.55f;
                    float cracked = Mathf.Pow(fracture.Sample(x * 1f / 16f, y * 1f / 16f), 3f) * 0.5f;
                    float grainy = detail.Sample(x * 0.5f, y * 0.5f) * 0.2f;
                    height[x, y] = bedded + cracked + grainy;
                }
            }
        }

        // --- Map builders -------------------------------------------------------
        private static Color32[] SampleAlbedo(SurfaceKind kind, float[,] height)
        {
            var pixels = new Color32[Size * Size];
            float contrast = kind == SurfaceKind.Wood ? 0.30f : 0.16f;

            // Normalise the height field against its own range rather than assuming it
            // straddles 0.5. The plaster generator lands wholly above the midpoint, so
            // a fixed centre mapped it to 0.92..1.0 white - a variation of a few
            // percent that no amount of lighting could reveal. The extra pass over the
            // field costs nothing next to the three map builds that follow.
            FindRange(height, out float min, out float max);
            float span = Mathf.Max(0.0001f, max - min);

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    // Centred and scaled into a multiplicative brightness variation
                    // around 1.0. Keeping the mean at 1.0 means the builder's authored
                    // colour still decides the surface's value.
                    float shade = 1f + ((height[x, y] - min) / span - 0.5f) * contrast;

                    byte v = (byte)(Mathf.Clamp01(shade) * 255f);
                    pixels[y * Size + x] = new Color32(v, v, v, 255);
                }
            }

            return pixels;
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

        private static Color32[] SampleNormal(float[,] height, float strength)
        {
            var pixels = new Color32[Size * Size];

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    // Central differences on the wrapped height field give a tangent
                    // space normal without needing tangents on the mesh.
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

        private static Color32[] SampleSmoothness(SurfaceKind kind, float[,] height)
        {
            var pixels = new Color32[Size * Size];
            float baseSmoothness = BaseSmoothness(kind);
            float variation = kind == SurfaceKind.Concrete ? 0.22f : 0.10f;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    // Rougher in the pits, glossier on the high points: worn surfaces
                    // are not uniformly shiny.
                    float s = Mathf.Clamp01(baseSmoothness + (height[x, y] - 0.5f) * variation);
                    byte v = (byte)Mathf.RoundToInt(s * 255f);
                    pixels[y * Size + x] = new Color32(v, v, v, 255);
                }
            }

            return pixels;
        }

        private static Texture2D CreateTexture(string name, Color32[] pixels, TextureWrapMode wrap)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, true)
            {
                name = name,
                wrapMode = wrap,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,

                // Nothing ever reads these back. Without this the CPU-side copy of all
                // three maps per surface kind stays resident for the whole session -
                // megabytes of memory that exist only because Apply was asked to keep
                // readable data nobody can reach.
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
        /// Value noise at a given frequency reads exactly four lattice corners per
        /// sample, and those corners depend only on the integer cell - never on the
        /// position inside it. Evaluating a whole 256x256 texture from a period-8
        /// lattice therefore performed 65536 * 4 = 262144 hashes to produce what is
        /// mathematically an 8x8 grid of values, each reused across a 32x32 pixel cell.
        ///
        /// Caching the grid makes that 81 hashes plus two smoothstep lerps per pixel, and
        /// the result is bit-identical because the same four corners were being
        /// re-derived from the same cells anyway. Across all four surface kinds this cut
        /// the noise work roughly 3.2x, which is the difference between a visible hitch
        /// on the first frame of the dream and none at all.
        /// </summary>
        private sealed class Lattice
        {
            public readonly int Period;
            public readonly int Stride;
            public readonly float[] Values;

            public Lattice(int period, int salt)
            {
                Period = Mathf.Max(1, period);

                // Period + 1 on each axis so the wrapped final cell has a real far
                // corner to interpolate towards.
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
        /// The bedroom's plaster and the station's concrete share their low-frequency
        /// octave, and all four kinds share several grain octaves, so caching across a
        /// single environment build removes a good share of the work again. Bounded by
        /// ClearCache, which the shapes cache reset calls.
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

        /// <summary>
        /// Tiling value noise in 0..1.
        ///
        /// The wrap period and the hash salt are separate parameters on purpose. They
        /// used to be the same value, which tied the lattice size to the seed: plaster
        /// asked for seed 4, so its "fine grain" repeated every four cells - about
        /// eleven pixels across a 256px texture - and read as a regular checkerboard
        /// rather than grain. Keeping the period independent lets a surface be both
        /// fine-grained and properly tiled.
        ///
        /// Prefer <see cref="GetLattice"/> and sampling the returned object directly in
        /// any per-pixel loop. This overload pays a dictionary lookup on every call,
        /// which measured at roughly the cost of the hashes it saves.
        /// </summary>
        private static float ValueNoise(float x, float y, int period, int salt)
        {
            return GetLattice(period, salt).Sample(x, y);
        }

        private static int Mod(int value, int modulus)
        {
            int r = value % modulus;
            return r < 0 ? r + modulus : r;
        }
    }
}
