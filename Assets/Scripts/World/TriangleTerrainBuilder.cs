using System.Collections.Generic;
using Triangle.Core;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// Generates a terrain at runtime: heightmap, splat layers and scatter.
    ///
    /// The project carries no content assets, so the terrain is synthesised the same way
    /// the rooms are - from code, with every texture coming from
    /// <see cref="TriangleProceduralTextures"/>. The cost is that the height field has
    /// to be built here rather than authored, which is why the shape is driven by
    /// deliberate noise shaping rather than left to a plain fBm and hoped for.
    ///
    /// The shaping is what makes a hillside read as terrain rather than as a lumpy
    /// plane:
    ///   - a ridged multifractal, so slopes have crests instead of domes;
    ///   - a valley mask, so the low ground reads as walkable;
    ///   - a height-and-slope driven surface blend, so rock appears on the steep ground
    ///     and grass takes the flats. A terrain painted with one layer looks like a
    ///     green wedge; the blend is what makes it read as a hillside.
    ///
    /// The whole build is timed in development builds. A 257 heightmap with four splat
    /// layers and ~900 trees is a few hundred milliseconds of main-thread work, and it
    /// lands inside an environment build where it is easiest to hide.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleTerrainBuilder : MonoBehaviour
    {
        [Header("Shape")]
        [Tooltip("Terrain size in metres on the X and Z axes.")]
        [SerializeField] private float terrainSize = 260f;

        [Tooltip("Heightmap resolution. 257 keeps generation fast; 513 is noticeably smoother.")]
        [SerializeField] private int heightmapResolution = 257;

        [SerializeField] private float maxHeight = 34f;

        [Tooltip("Low ground below this fraction of max height is flattened toward walkable ground.")]
        [Range(0f, 0.6f)] [SerializeField] private float valleyFlattening = 0.22f;

        [Header("Surfaces")]
        [Tooltip("Alphamap resolution. Lower is faster and the blend is no sharper to the eye.")]
        [Range(32, 1024)] [SerializeField] private int alphamapResolution = 256;

        [SerializeField] private float splatTileSize = 12f;

        [Header("Scatter")]
        [SerializeField] private int treeCount = 900;

        [Tooltip("Trees are thinned out on steep ground, which is what stops them growing out of cliffs.")]
        [Range(0f, 1f)] [SerializeField] private float maxTreeSlope = 0.42f;

        [Header("Seed")]
        [Tooltip("Change for a different hillside. Fixed by default so the slice is reproducible.")]
        [SerializeField] private int seed = 20250927;

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<Material> _ownedMaterials = new List<Material>();
        private readonly List<Texture2D> _ownedTextures = new List<Texture2D>();
        private readonly Dictionary<int, Material> _scatterMaterials = new Dictionary<int, Material>();
        private readonly Dictionary<TriangleProceduralTextures.SurfaceKind, Dictionary<int, Texture2D>>
            _tintedTextures = new Dictionary<TriangleProceduralTextures.SurfaceKind, Dictionary<int, Texture2D>>();

        private TerrainData _data;

        /// <summary>The live terrain, once built.</summary>
        public Terrain Terrain { get; private set; }

        /// <summary>Terrain height in metres at a world position.</summary>
        public float SampleHeight(Vector3 worldPosition)
        {
            if (Terrain != null)
            {
                return Terrain.SampleHeight(worldPosition);
            }

            // Before the Terrain component exists, fall back to the data. The yard calls
            // this while it is still placing the house, and a silent zero would sink
            // everything onto a flat plane.
            return _data != null ? _data.GetInterpolatedHeight(worldPosition.x, worldPosition.z) : 0f;
        }

        /// <summary>The terrain's vertical extent in metres.</summary>
        public float VerticalSize
        {
            get
            {
                if (Terrain != null && Terrain.terrainData != null)
                {
                    return Terrain.terrainData.size.y;
                }

                return _data != null ? _data.size.y : maxHeight;
            }
        }

        /// <summary>
        /// Terrain height normalised to 0..1, at a normalised 0..1 position across the
        /// terrain.
        ///
        /// Deliberately divided by the vertical size. Unity's height sampling answers in
        /// metres, and every caller of this method wants a 0..1 blend weight rather than
        /// a distance - feeding metres into those produced a splat map that was fully
        /// saturated, and tree positions of height * maxHeight, which is an altitude of
        /// over a kilometre. The two units are kept apart on purpose: use
        /// <see cref="SampleHeight"/> when the answer is a position.
        ///
        /// Read from <see cref="TerrainData"/> in the terrain's own local space rather
        /// than through the Terrain component, for two reasons. Generation needs to
        /// sample before the component exists, and a local-space read stays correct no
        /// matter where the container later moves the terrain to - which the yard does,
        /// after the terrain has already been generated.
        /// </summary>
        public float SampleHeight01(float nx, float nz)
        {
            if (_data == null)
            {
                return 0f;
            }

            float vertical = _data.size.y;
            if (vertical < 0.0001f)
            {
                return 0f;
            }

            int last = _data.heightmapResolution - 1;
            return _data.GetInterpolatedHeight(nx * last, nz * last) / vertical;
        }

        /// <summary>
        /// Builds the terrain at an explicit size.
        ///
        /// Not built in Awake, and this is not an oversight. AddComponent runs Awake
        /// synchronously, so a component that generated its terrain there would be
        /// finished before the caller could configure it - and the caller's settings
        /// would be silently discarded. Building on demand makes the container the one
        /// that decides the size, and makes a builder added to a scene by hand inert
        /// until it is asked for, which is the safer default for something this
        /// expensive.
        /// </summary>
        public void Build(float size, float height)
        {
            if (Terrain != null)
            {
                return; // already built; a second build would orphan the first terrain
            }

            terrainSize = Mathf.Max(16f, size);
            maxHeight = Mathf.Max(1f, height);
            Build();
        }

        private void OnDestroy()
        {
            // TerrainData, the tinted albedo copies and the scatter materials are not
            // scene objects, so nothing else reclaims them. Every environment rebuild
            // would otherwise leave a heightmap and a dozen textures behind.
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                {
                    Destroy(_spawned[i]);
                }
            }

            _spawned.Clear();

            for (int i = 0; i < _ownedTextures.Count; i++)
            {
                if (_ownedTextures[i] != null)
                {
                    Destroy(_ownedTextures[i]);
                }
            }

            _ownedTextures.Clear();
            _tintedTextures.Clear();

            for (int i = 0; i < _ownedMaterials.Count; i++)
            {
                if (_ownedMaterials[i] != null)
                {
                    Destroy(_ownedMaterials[i]);
                }
            }

            _ownedMaterials.Clear();
            _scatterMaterials.Clear();

            // TerrainData is a plain object rather than a scene object, so nothing else
            // will reclaim it - not even when the terrain object above is destroyed,
            // because the component only clears its reference.
            if (_data != null)
            {
                Destroy(_data);
                _data = null;
            }

            Terrain = null;
        }

        private void Build()
        {
            bool timed = Debug.isDebugBuild;
            long started = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            _data = new TerrainData
            {
                heightmapResolution = Mathf.Clamp(heightmapResolution, 33, 1025),
                size = new Vector3(terrainSize, maxHeight, terrainSize),
                alphamapResolution = Mathf.Clamp(alphamapResolution, 32, 1024),
                baseMapResolution = 512
            };

            // Order matters. The splat blend and the tree scatter both decide where to
            // put things by reading the ground height, and the height field has to be in
            // place before either of them runs. It is a plain array, so it does not
            // matter that the Terrain component itself is not attached yet - which is
            // also why the height sampler reads TerrainData rather than the component.
            GenerateHeights(_data);
            GenerateSurfaces(_data);
            Scatter(_data);

            GameObject terrainObject = Terrain.CreateTerrainGameObject(_data);
            terrainObject.name = "TriangleTerrain";
            terrainObject.transform.SetParent(transform, false);
            _spawned.Add(terrainObject);
            Terrain = terrainObject.GetComponent<Terrain>();

            if (Terrain != null)
            {
                Terrain.heightmapPixelError = 3f;
                Terrain.basemapDistance = 300f;
                Terrain.treeDistance = 180f;
                Terrain.detailObjectDistance = 60f;
                Terrain.drawInstanced = true;
                Terrain.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
            }

            if (timed)
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0
                            / System.Diagnostics.Stopwatch.Frequency;
                TriangleLog.Log(
                    $"Generated {terrainSize:0} m terrain ({_data.heightmapResolution} heightmap, " +
                    $"{_data.alphamapResolution} alphamap, {_data.treeInstanceCount} trees) in {ms:0.0} ms.");
            }
        }

        // --- Height ---------------------------------------------------------------

        private void GenerateHeights(TerrainData data)
        {
            int res = data.heightmapResolution;
            int cells = res - 1;
            var heights = new float[res, res];

            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    float wx = cells == 0 ? 0f : x / (float)cells;
                    float wz = cells == 0 ? 0f : z / (float)cells;
                    heights[z, x] = ShapeHeight(wx, wz);
                }
            }

            data.SetHeights(0, 0, heights);
        }

        /// <summary>
        /// The height field, normalised to 0..1.
        ///
        /// <see cref="TriangleTerrainNoise.RidgedFbm"/> rather than plain fBm. Ridging
        /// folds each octave through 1 - |2n - 1|, which puts a crease along every zero
        /// crossing, and those creases are what the eye reads as ridgelines. Plain fBm
        /// gives rounded domes, which read as a lumpy surface.
        ///
        /// The noise frequency is expressed in cycles across the whole terrain, not per
        /// sample, so changing heightmapResolution changes the detail and not the shape.
        /// </summary>
        private float ShapeHeight(float nx, float nz)
        {
            float x = nx * terrainSize * 0.011f;
            float z = nz * terrainSize * 0.011f;

            float ridge = TriangleTerrainNoise.RidgedFbm(x, z, seed, 6);

            // Flatten the low ground. Without this the terrain is walkable everywhere,
            // which is both wrong for a landscape and useless as a play space.
            float floor = valleyFlattening;
            if (ridge < floor)
            {
                float t = ridge / Mathf.Max(0.0001f, floor);
                ridge = Mathf.Lerp(ridge * 0.35f, ridge, t * t);
            }

            // A gentle basin at the centre, so the yard the player spawns in has
            // somewhere natural to stand rather than being pinned to a slope.
            float dx = nx - 0.5f;
            float dz = nz - 0.5f;
            float centre = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dz * dz) * 3.2f);
            ridge -= centre * 0.18f;

            return Mathf.Clamp01(ridge);
        }

        // --- Surfaces ---------------------------------------------------------------

        private void GenerateSurfaces(TerrainData data)
        {
            int res = Mathf.Clamp(alphamapResolution, 32, 1024);
            int cells = res - 1;

            data.terrainLayers = new[]
            {
                BuildLayer("Grass", TriangleProceduralTextures.SurfaceKind.Grass, new Color(0.148f, 0.168f, 0.108f)),
                BuildLayer("Dirt", TriangleProceduralTextures.SurfaceKind.Dirt, new Color(0.196f, 0.164f, 0.126f)),
                BuildLayer("Rock", TriangleProceduralTextures.SurfaceKind.Rock, new Color(0.205f, 0.208f, 0.215f)),
                BuildLayer("Moss", TriangleProceduralTextures.SurfaceKind.Grass, new Color(0.104f, 0.140f, 0.112f))
            };

            float[,,] maps = data.GetAlphamaps(0, 0, res, res);

            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    float wx = cells == 0 ? 0f : x / (float)cells;
                    float wz = cells == 0 ? 0f : z / (float)cells;

                    float h01 = SampleHeight01(wx, wz);
                    float slope = SlopeAt(wx, wz, 0.01f);

                    // Rock takes the steep ground, grass the flats, dirt the middling
                    // band, moss the damp low ground. The bands deliberately overlap so
                    // there is no hard line between them.
                    float rock = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.18f, 0.52f, slope));
                    float high = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.55f, 0.88f, h01));
                    float low = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.10f, 0.34f, h01));
                    float grass = Mathf.Clamp01(1f - rock - high * 0.8f);

                    // Break the bands up so the transitions follow the ground instead of
                    // forming contour lines around the hill.
                    float jitter = TriangleTerrainNoise.Noise(wx * 9f, wz * 9f, seed + 7717);
                    rock = Mathf.Clamp01(rock + (jitter - 0.5f) * 0.35f);
                    grass = Mathf.Clamp01(grass + (jitter - 0.5f) * 0.25f);

                    float total = grass + rock + low * 0.6f;
                    if (total < 0.0001f)
                    {
                        grass = 1f;
                        rock = 0f;
                        total = 1f;
                    }

                    grass /= total;
                    rock /= total;
                    float dirt = Mathf.Max(0f, 1f - grass - rock) / total;
                    float moss = Mathf.Clamp01(low * 0.6f) / total;

                    maps[z, x, 0] = grass;
                    maps[z, x, 1] = dirt;
                    maps[z, x, 2] = rock;
                    maps[z, x, 3] = moss;
                }
            }

            data.SetAlphamaps(0, 0, maps);
        }

        /// <summary>
        /// Terrain gradient magnitude at a normalised position, as rise per metre of
        /// horizontal run.
        ///
        /// A true slope, so the thresholds that gate rock coverage and tree placement
        /// can be read as degrees of lean: 0.4 is about 22 degrees.
        ///
        /// The conversion is needed because <see cref="SampleHeight01"/> works in
        /// normalised units on both axes, so its raw gradient is dimensionless and
        /// depends on how tall and how wide the terrain happen to be. Scaling by
        /// height/width makes the number mean the same thing on any terrain shape.
        /// </summary>
        private float SlopeAt(float nx, float nz, float epsilon)
        {
            float dx = SampleHeight01(nx + epsilon, nz) - SampleHeight01(nx - epsilon, nz);
            float dz = SampleHeight01(nx, nz + epsilon) - SampleHeight01(nx, nz - epsilon);

            float risePerMetre = VerticalSize / Mathf.Max(0.0001f, terrainSize);
            return Mathf.Sqrt(dx * dx + dz * dz) / (2f * epsilon) * risePerMetre;
        }

        private TerrainLayer BuildLayer(
            string layerName,
            TriangleProceduralTextures.SurfaceKind kind,
            Color tint)
        {
            // Requested before the surface is fetched, because the retained pixels only
            // exist if the request arrived first - the generator keeps them only for
            // surfaces that have been asked for.
            TriangleProceduralTextures.RequestTerrainPixels(kind);
            TriangleProceduralTextures.SurfaceSet set = TriangleProceduralTextures.Get(kind);

            // TerrainLayer has no per-layer colour property, so the palette is pushed
            // into a tinted copy of the generated albedo. Grass and moss share one
            // generator, so the copy is the only way for them to differ.
            return new TerrainLayer
            {
                name = layerName,
                diffuseTexture = Tinted(kind, set, tint),
                normalMapTexture = set.Normal,
                metallic = 0f,
                smoothness = 0.08f,
                tileSize = new Vector2(splatTileSize, splatTileSize)
            };
        }

        /// <summary>
        /// A colour-multiplied copy of a generated albedo.
        ///
        /// A copy rather than an in-place tint, because the base texture is shared with
        /// the room surfaces and must not change underneath them. Cached, because grass
        /// and moss both ask for tinted copies of the same generator output.
        /// </summary>
        private Texture2D Tinted(
            TriangleProceduralTextures.SurfaceKind kind,
            TriangleProceduralTextures.SurfaceSet set,
            Color tint)
        {
            if (set == null || set.Albedo == null)
            {
                return null;
            }

            int key = (Mathf.RoundToInt(tint.r * 255) << 16)
                      | (Mathf.RoundToInt(tint.g * 255) << 8)
                      | Mathf.RoundToInt(tint.b * 255);

            if (!_tintedTextures.TryGetValue(kind, out Dictionary<int, Texture2D> variants))
            {
                variants = new Dictionary<int, Texture2D>();
                _tintedTextures[kind] = variants;
            }

            if (variants.TryGetValue(key, out Texture2D cached) && cached != null)
            {
                return cached;
            }

            if (set.AlbedoPixels == null)
            {
                // The surface was generated before the request reached it, so there is no
                // pixel array to tint. Falling back to the untinted map is wrong
                // visually but does not throw, which is the better failure here: a grey
                // grass layer is recoverable, a null reference in Awake is not.
                TriangleLog.Warn(
                    $"Surface '{kind}' was generated before the terrain asked for its pixels; " +
                    "the layer will be untinted.");
                return set.Albedo;
            }

            // Multiplied in linear space rather than on the stored bytes. A byte-level
            // multiply would darken by the wrong amount - the albedo is written as sRGB
            // bytes, so scaling those scales gamma-encoded values, and a 0.15 tint
            // applied that way comes out much lighter than intended and washes the whole
            // hillside out.
            var pixels = new Color32[set.AlbedoPixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color source = set.AlbedoPixels[i];
                pixels[i] = new Color(
                Mathf.Clamp01(source.r * tint.r),
                Mathf.Clamp01(source.g * tint.g),
                Mathf.Clamp01(source.b * tint.b),
                source.a);
            }

            var copy = new Texture2D(set.Albedo.width, set.Albedo.height, set.Albedo.format, true)
            {
                name = "TriangleTerrainLayer_" + kind,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8,
                hideFlags = HideFlags.HideAndDontSave
            };
            copy.SetPixels32(pixels);
            copy.Apply(true, true);

            variants[key] = copy;
            _ownedTextures.Add(copy);
            return copy;
        }

        // --- Scatter -----------------------------------------------------------------

        private void Scatter(TerrainData data)
        {
            // Three conifer sizes and two dead-tree sizes, rather than one of each with
            // a per-instance scale.
            //
            // Two reasons. TreeInstance exposes no per-tree scale in Unity's API, and
            // more usefully, terrain tree instancing batches by prototype - so variety
            // across prototypes both looks better and draws fewer calls than variety
            // across instances of one prefab ever could.
            data.treePrototypes = new[]
            {
                new TreePrototype { prefab = BuildConifer(0.72f), bendFactor = 0.4f },
                new TreePrototype { prefab = BuildConifer(1.00f), bendFactor = 0.4f },
                new TreePrototype { prefab = BuildConifer(1.38f), bendFactor = 0.4f },
                new TreePrototype { prefab = BuildDeadTree(0.80f), bendFactor = 0.6f },
                new TreePrototype { prefab = BuildDeadTree(1.20f), bendFactor = 0.6f }
            };

            const int PrototypeConiferSmall = 0;
            const int PrototypeConiferLarge = 2;
            const int PrototypeDead = 3;

            var instances = new List<TreeInstance>(treeCount);
            var random = new System.Random(seed);
            int placed = 0;
            int attempts = 0;
            int maxAttempts = treeCount * 12;

            while (placed < treeCount && attempts < maxAttempts)
            {
                attempts++;
                float nx = (float)random.NextDouble();
                float nz = (float)random.NextDouble();

                float h01 = SampleHeight01(nx, nz);
                if (h01 < 0.18f)
                {
                    continue; // not in the flood plain
                }

                // Sampled with a wide epsilon on purpose. A narrow one measures the
                // individual ripples in the height field rather than the lean of the
                // hillside, and the top octave of a six-octave fBm has a wavelength of
                // about two metres - narrow enough that a one-metre probe lands on
                // whichever side of a ripple it happens to be on.
                if (SlopeAt(nx, nz, 0.02f) > maxTreeSlope)
                {
                    continue; // not growing out of a cliff
                }

                // A wetter, lower band carries most of the conifers; the high, dry ground
                // gets more of the dead ones. That gives the hillside a story rather than
                // a uniform sprinkle. Size falls off with height as well, so the tall
                // ground reads as exposed.
                double roll = random.NextDouble();
                bool isConifer = roll < Mathf.Lerp(0.88f, 0.30f, h01);
                int prototype;
                if (isConifer)
                {
                    prototype = roll < 0.22f ? PrototypeConiferSmall
                               : roll < 0.55f ? PrototypeConiferSmall + 1
                               : PrototypeConiferLarge;
                }
                else
                {
                    prototype = roll < 0.30f ? PrototypeDead : PrototypeDead + 1;
                }

                instances.Add(new TreeInstance
                {
                    // An index into treePrototypes, not a reference to the prototype.
                    prototypeIndex = prototype,
                    position = new Vector3(nx * terrainSize, h01 * maxHeight, nz * terrainSize),

                    // TreeInstance stores rotation as a single float of degrees about Y.
                    rotation = (float)random.NextDouble() * 360f,
                    color = Color.white,
                    lightmapColor = Color.white
                });

                placed++;
            }

            data.SetTreeInstances(instances.ToArray(), true);
            TriangleLog.Verbose($"Scattered {placed} trees over the terrain.");
        }

        // --- Prototype geometry --------------------------------------------------------

        /// <summary>
        /// A low-poly conifer: three stacked cones over a short trunk.
        ///
        /// Built from the shared cone mesh rather than authored, for the same reason the
        /// rooms are built from boxes. A conifer is one of the few silhouettes that still
        /// reads as a tree at silhouette-only distance, which is all the fog and the
        /// moonlit palette ever actually give you.
        /// </summary>
        private GameObject BuildConifer(float scale)
        {
            var root = new GameObject("Conifer");
            root.transform.localScale = Vector3.one * scale;

            GameObject trunk = TriangleShapes.CreateShape("Trunk", TriangleShapes.Cylinder, root.transform);
            trunk.transform.localPosition = new Vector3(0f, 1.1f, 0f);
            trunk.transform.localScale = new Vector3(0.28f, 2.2f, 0.28f);
            ApplyScatterMaterial(trunk, new Color(0.115f, 0.088f, 0.068f));

            // Three stacked cones, each smaller and higher, so the silhouette tapers
            // instead of ending in a single point.
            float[] widths = { 3.4f, 2.5f, 1.5f };
            float[] heights = { 2.1f, 4.0f, 5.6f };
            for (int i = 0; i < widths.Length; i++)
            {
                GameObject canopy = TriangleShapes.CreateShape("Canopy" + i, TriangleShapes.ConeMesh, root.transform);
                canopy.transform.localPosition = new Vector3(0f, heights[i], 0f);
                canopy.transform.localScale = new Vector3(widths[i], widths[i] * 0.85f, widths[i]);
                ApplyScatterMaterial(canopy, new Color(0.062f, 0.094f, 0.070f));
            }

            return root;
        }

        /// <summary>A bare, leaning trunk. The trees that should not be there.</summary>
        private GameObject BuildDeadTree(float scale)
        {
            var root = new GameObject("DeadTree");
            root.transform.localScale = Vector3.one * scale;

            GameObject trunk = TriangleShapes.CreateShape("Trunk", TriangleShapes.Cylinder, root.transform);
            trunk.transform.localPosition = new Vector3(0f, 2.2f, 0f);
            trunk.transform.localScale = new Vector3(0.24f, 4.4f, 0.24f);
            ApplyScatterMaterial(trunk, new Color(0.145f, 0.130f, 0.112f));

            for (int i = 0; i < 3; i++)
            {
                float angle = i * 120f;
                GameObject branch = TriangleShapes.CreateShape("Branch" + i, TriangleShapes.ConeMesh, root.transform);
                branch.transform.localPosition = new Vector3(
                    Mathf.Cos(angle * Mathf.Deg2Rad) * 0.45f,
                    2.6f + i * 0.7f,
                    Mathf.Sin(angle * Mathf.Deg2Rad) * 0.45f);
                branch.transform.localRotation = Quaternion.Euler(48f, angle, 0f);
                branch.transform.localScale = new Vector3(0.16f, 1.5f, 0.16f);
                ApplyScatterMaterial(branch, new Color(0.132f, 0.118f, 0.100f));
            }

            return root;
        }

        // --- Materials -----------------------------------------------------------------

        private void ApplyScatterMaterial(GameObject target, Color colour)
        {
            target.GetComponent<MeshRenderer>().sharedMaterial = GetScatterMaterial(colour);
        }

        /// <summary>
        /// A shared, cached material for scatter geometry.
        ///
        /// The tree prototypes are instantiated by the terrain rather than by this
        /// component, so their materials are held here and reclaimed on teardown. Cached
        /// by colour, because nine hundred conifers must not be nine hundred materials.
        /// </summary>
        private Material GetScatterMaterial(Color colour)
        {
            int key = (Mathf.RoundToInt(colour.r * 255) << 16)
                      | (Mathf.RoundToInt(colour.g * 255) << 8)
                      | Mathf.RoundToInt(colour.b * 255);

            if (_scatterMaterials.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            var material = new Material(TriangleShapes.LitShader)
            {
                name = "TriangleScatter_" + colour,
                color = colour,
                enableInstancing = true
            };

            // The same matte response as the room surfaces, so the scatter does not read
            // as plastic next to the plaster.
            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", 0.05f);
            }

            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", 0f);
            }

            _scatterMaterials[key] = material;
            _ownedMaterials.Add(material);
            return material;
        }
    }
}
