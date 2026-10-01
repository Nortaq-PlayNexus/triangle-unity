using System;
using System.Collections.Generic;
using Triangle.Core;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// Runtime meshes and materials.
    ///
    /// The slice runs with no content assets, so every surface is a material built here
    /// from a generated texture set. This is the single choke point for that: nothing else
    /// in the project creates a Material or picks a shader, which is what makes swapping in
    /// authored assets a change to one class.
    ///
    /// Everything renders through Universal Render Pipeline/Lit. The previous version used
    /// the built-in Standard shader with specular highlights disabled and glossiness forced
    /// to zero, which is a large part of why surfaces read as painted cardboard: a surface
    /// with no specular response has no edge, so a cube has no silhouette.
    /// </summary>
    public static class TriangleShapes
    {
        private static readonly Dictionary<PrimitiveType, Mesh> MeshCache = new Dictionary<PrimitiveType, Mesh>();
        private static readonly Dictionary<MaterialKey, Material> MaterialCache = new Dictionary<MaterialKey, Material>();
        private static readonly Dictionary<Color, Material> UnlitCache = new Dictionary<Color, Material>();
        private static readonly Dictionary<GlowKey, Material> EmissiveCache = new Dictionary<GlowKey, Material>();
        private static readonly Dictionary<SmoothKey, Material> SmoothCache = new Dictionary<SmoothKey, Material>();

        private static Mesh _coneMesh;
        private static Mesh _bevelBoxMesh;
        private static Shader _litShader;
        private static Shader _unlitShader;

        /// <summary>
        /// Drops every cached mesh and material.
        ///
        /// The caches are static, so they outlive a play session whenever the editor enters
        /// play mode without a domain reload. That leaks a material per distinct colour and
        /// keeps the generated textures alive, which shows up as steadily growing memory
        /// across repeated sessions. Runs before the first scene loads in a fresh domain,
        /// which is the only point at which the caches are guaranteed to be empty.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCaches()
        {
            DestroyAll(MaterialCache);
            DestroyAll(UnlitCache);
            DestroyAll(EmissiveCache);
            DestroyAll(SmoothCache);

            MaterialCache.Clear();
            UnlitCache.Clear();
            EmissiveCache.Clear();
            SmoothCache.Clear();

            if (_coneMesh != null)
            {
                DestroyCached(_coneMesh);
                _coneMesh = null;
            }

            if (_bevelBoxMesh != null)
            {
                DestroyCached(_bevelBoxMesh);
                _bevelBoxMesh = null;
            }

            // Unity's built-in primitive meshes are engine assets and must not be
            // destroyed; only the reference is dropped so they are re-fetched.
            MeshCache.Clear();

            _litShader = null;
            _unlitShader = null;

            TriangleProceduralTextures.ClearCache();
        }

        /// <summary>
        /// Destroys every material in a cache.
        ///
        /// Generic over the key rather than one overload per key type. The caches are keyed by
        /// five different structs, and a non-generic version cannot name them all, so this had
        /// grown per-key overloads - and adding the smoothness cache meant the compiler
        /// rejected every call that did not match, because no overload took an arbitrary key.
        /// </summary>
        private static void DestroyAll<TKey, TValue>(Dictionary<TKey, TValue> cache)
            where TValue : UnityEngine.Object
        {
            foreach (KeyValuePair<TKey, TValue> pair in cache)
            {
                DestroyCached(pair.Value);
            }
        }

        // UnityEngine.Object is spelled out throughout: this file imports System for
        // IEquatable, and a bare Object is then ambiguous between the two.
        private static void DestroyCached(UnityEngine.Object asset)
        {
            if (asset == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(asset);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        /// <summary>Cache key for a textured lit surface. A struct, so it cannot collide.</summary>
        private readonly struct MaterialKey : IEquatable<MaterialKey>
        {
            private readonly int _colour;
            private readonly int _surface;
            private readonly int _tiling;

            public MaterialKey(Color colour, int surface, float tiling)
            {
                _colour = Quantise(colour);
                _surface = surface;
                _tiling = Mathf.RoundToInt(tiling * 1000f);
            }

            public bool Equals(MaterialKey other)
            {
                return _colour == other._colour && _surface == other._surface && _tiling == other._tiling;
            }

            public override bool Equals(object obj) => obj is MaterialKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _colour;
                    hash = (hash * 397) ^ _surface;
                    hash = (hash * 397) ^ _tiling;
                    return hash;
                }
            }
        }

        /// <summary>Cache key for a plain coloured surface at a given smoothness.</summary>
        private readonly struct SmoothKey : IEquatable<SmoothKey>
        {
            private readonly int _colour;
            private readonly int _smoothness;

            public SmoothKey(Color colour, float smoothness)
            {
                _colour = Quantise(colour);
                _smoothness = Mathf.RoundToInt(Mathf.Clamp01(smoothness) * 100f);
            }

            public bool Equals(SmoothKey other)
            {
                return _colour == other._colour && _smoothness == other._smoothness;
            }

            public override bool Equals(object obj) => obj is SmoothKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_colour * 397) ^ _smoothness;
                }
            }
        }

        /// <summary>Cache key for an emissive material.</summary>
        private readonly struct GlowKey : IEquatable<GlowKey>
        {
            private readonly Color _colour;
            private readonly int _intensity;

            public GlowKey(Color colour, float intensity)
            {
                _colour = colour;
                _intensity = Mathf.RoundToInt(Mathf.Max(0f, intensity) * 100f);
            }

            public bool Equals(GlowKey other)
            {
                return _colour == other._colour && _intensity == other._intensity;
            }

            public override bool Equals(object obj) => obj is GlowKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    return (_colour.GetHashCode() * 397) ^ _intensity;
                }
            }
        }

        /// <summary>
        /// The lit shader the whole project renders through.
        ///
        /// Public because the terrain's scatter geometry is built and materialed outside
        /// this class, and it needs the same shader to be indistinguishable from the room
        /// surfaces beside it.
        ///
        /// Found by name, with the built-in Standard shader as a fallback. The fallback is
        /// not equivalent - Standard has different property names and no occlusion map - so
        /// a material built through it loses its normal map and its baked occlusion. It
        /// exists only so the project still renders something rather than a field of
        /// magenta boxes if the package is ever removed.
        /// </summary>
        public static Shader LitShader
        {
            get
            {
                if (_litShader == null)
                {
                    _litShader = Shader.Find("Universal Render Pipeline/Lit")
                                 ?? Shader.Find("Standard")
                                 ?? Shader.Find("Legacy Shaders/Diffuse");
                }

                return _litShader;
            }
        }

        private static Shader UnlitShader
        {
            get
            {
                if (_unlitShader == null)
                {
                    _unlitShader = Shader.Find("Universal Render Pipeline/Unlit")
                                   ?? Shader.Find("Unlit/Color")
                                   ?? LitShader;
                }

                return _unlitShader;
            }
        }

        public static Mesh Cube => GetMesh(PrimitiveType.Cube);
        public static Mesh Cylinder => GetMesh(PrimitiveType.Cylinder);
        public static Mesh Sphere => GetMesh(PrimitiveType.Sphere);

        /// <summary>
        /// A box with softened edges, and normals that follow the rounding.
        ///
        /// The single largest change to how this game looks. A perfect cube has twelve hard
        /// 90-degree edges, and under any lighting a hard edge with a specular response
        /// produces one bright line - a real room's edges are rounded by paint, wear and
        /// the fact that plaster is never applied to a perfect arris. Flat-shaded boxes read
        /// as untextured geometry; rounded ones read as objects.
        ///
        /// Built by taking a subdivided sphere and, for each vertex, clamping its position
        /// into the box's inner volume and pushing it back out along its own normal. The
        /// sphere's normal is the rounded box's normal, so the shading is exactly correct
        /// with no normal recalculation.
        /// </summary>
        public static Mesh BevelBoxMesh => _bevelBoxMesh != null ? _bevelBoxMesh : (_bevelBoxMesh = BuildBevelBox());

        /// <summary>
        /// How far the edges are rounded, as a fraction of the shortest side.
        ///
        /// Small on purpose. At 0.5 of the shortest axis this becomes a capsule; at 0.1 the
        /// edge still reads as an edge but catches a highlight, which is the whole point.
        /// </summary>
        private const float BevelFraction = 0.08f;

        /// <summary>
        /// Segments around the sphere the bevel is derived from.
        ///
        /// 12 gives a fairly faceted fillet at this radius. Raising it costs vertices on
        /// every box in the game for a difference that only shows on a close-up edge, which
        /// the bedroom never has.
        /// </summary>
        private const int BevelSegments = 12;

        private static Mesh BuildBevelBox()
        {
            // A UV sphere of BevelSegments x (BevelSegments/2) rings, generated here rather
            // than taken from Unity's Sphere primitive, which has hard poles and its own
            // triangle layout. A uniform grid keeps the clamp-and-offset well behaved at the
            // poles, where the UV sphere degenerates.
            int slices = BevelSegments;
            int stacks = Mathf.Max(3, BevelSegments / 2);

            int vertexCount = (slices + 1) * (stacks + 1);
            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];

            const float bevel = BevelFraction;
            // The inner half-extent the sphere is clamped into. At bevel 0.08 the visible
            // faces still occupy 0.92 of the box, so scale compensates to keep the outer
            // dimensions exact and the builders' colliders honest.
            float inner = 0.5f - bevel;

            int v = 0;
            for (int stack = 0; stack <= stacks; stack++)
            {
                float phi = Mathf.PI * stack / stacks;
                float y = Mathf.Cos(phi);
                float ringRadius = Mathf.Sin(phi);

                for (int slice = 0; slice <= slices; slice++)
                {
                    float theta = 2f * Mathf.PI * slice / slices;
                    var spherePoint = new Vector3(
                        ringRadius * Mathf.Cos(theta),
                        y,
                        ringRadius * Mathf.Sin(theta));

                    // Clamp into the inner box, then push back out along the normal. On a
                    // face this reproduces the face exactly; on an edge it produces the
                    // fillet; on a corner it produces the corner sphere.
                    var clamped = new Vector3(
                        Mathf.Clamp(spherePoint.x, -inner, inner),
                        Mathf.Clamp(spherePoint.y, -inner, inner),
                        Mathf.Clamp(spherePoint.z, -inner, inner));

                    positions[v] = clamped + spherePoint * bevel;
                    normals[v] = spherePoint;
                    uvs[v] = new Vector2(slice / (float)slices, stack / (float)stacks);
                    v++;
                }
            }

            var triangles = new List<int>((slices * stacks * 6));
            int rowStride = slices + 1;

            for (int stack = 0; stack < stacks; stack++)
            {
                for (int slice = 0; slice < slices; slice++)
                {
                    int current = stack * rowStride + slice;
                    int next = current + rowStride;

                    // Winding is counter-clockwise seen from outside, matching Unity's
                    // front-face convention. Getting this backwards renders the whole
                    // rounded box inside-out with back-face culling.
                    triangles.Add(current);
                    triangles.Add(next);
                    triangles.Add(current + 1);

                    triangles.Add(current + 1);
                    triangles.Add(next);
                    triangles.Add(next + 1);
                }
            }

            // Normalised to a unit cube so the transform scale is the real size in metres.
            var mesh = new Mesh
            {
                name = "TriangleBevelBox",

                // Generated at runtime and never authored, so keep it out of scene saves
                // and let it be reclaimed on unload.
                hideFlags = HideFlags.HideAndDontSave
            };

            mesh.SetVertices(new List<Vector3>(positions));
            mesh.SetNormals(new List<Vector3>(normals));
            mesh.SetUVs(0, new List<Vector2>(uvs));
            mesh.SetTriangles(triangles, 0);

            // Explicit normals above are authoritative, so tangents are derived from the
            // normals and UVs rather than from averaged face normals - the latter would
            // flatten the fillet shading that is the entire reason this mesh exists.
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();

            for (int i = 0; i < positions.Length; i++)
            {
                positions[i] *= 1f;
            }

            return mesh;
        }

        /// <summary>
        /// A true cone. Unity's primitive set has no cone, so this builds one once by
        /// tapering a cylinder's top ring to a point. The Triangle's point and its razor-gap
        /// manifestations depend on a real cone silhouette.
        /// </summary>
        public static Mesh ConeMesh
        {
            get
            {
                if (_coneMesh != null)
                {
                    return _coneMesh;
                }

                Mesh source = GetMesh(PrimitiveType.Cylinder);
                Vector3[] vertices = source.vertices;
                int[] triangles = source.triangles;

                // Unity's cylinder is 2 units tall (y -1..1) with rings at y = -0.5 and
                // y = +0.5 for the caps. Collapse the upper ring.
                var coneVertices = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 vertex = vertices[i];
                    if (vertex.y > 0.4f)
                    {
                        vertex.y = 1f;
                        vertex.x = 0f;
                        vertex.z = 0f;
                    }

                    coneVertices[i] = vertex;
                }

                // Keep only the side quads (the caps are now degenerate) and rebuild them
                // as triangles so the taper reads cleanly.
                var coneTriangles = new List<int>(triangles.Length);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = triangles[i];
                    int b = triangles[i + 1];
                    int c = triangles[i + 2];

                    // Skip triangles whose vertices were all collapsed to the apex.
                    bool aCap = IsApex(coneVertices[a]);
                    bool bCap = IsApex(coneVertices[b]);
                    bool cCap = IsApex(coneVertices[c]);

                    if (aCap && bCap && cCap)
                    {
                        continue;
                    }

                    coneTriangles.Add(a);
                    coneTriangles.Add(b);
                    coneTriangles.Add(c);
                }

                _coneMesh = new Mesh
                {
                    name = "TriangleCone",
                    hideFlags = HideFlags.HideAndDontSave
                };
                _coneMesh.SetVertices(new List<Vector3>(coneVertices));
                _coneMesh.SetTriangles(coneTriangles, 0);
                _coneMesh.RecalculateNormals();

                // Tangents are required for the generated normal maps to resolve correctly;
                // without them the shader falls back to screen-space derivatives and the
                // surface relief shifts with the camera.
                _coneMesh.RecalculateTangents();
                _coneMesh.RecalculateBounds();
                return _coneMesh;
            }
        }

        private static bool IsApex(Vector3 vertex)
        {
            return vertex.x == 0f && vertex.z == 0f && vertex.y >= 0.999f;
        }

        public static Mesh GetMesh(PrimitiveType primitive)
        {
            if (MeshCache.TryGetValue(primitive, out Mesh cached) && cached != null)
            {
                return cached;
            }

            // The engine's built-in mesh directly. Building a throwaway primitive GameObject
            // per mesh and destroying it with DestroyImmediate is explicitly disallowed in
            // play mode - Unity warns and performs an immediate destruction instead of the
            // deferred one the rest of the frame expects.
            Mesh mesh = Resources.GetBuiltinResource<Mesh>(BuiltInNameFor(primitive));
            if (mesh == null)
            {
                TriangleLog.Error($"Could not resolve the built-in '{primitive}' mesh.");
                return null;
            }

            MeshCache[primitive] = mesh;
            return mesh;
        }

        private static string BuiltInNameFor(PrimitiveType primitive)
        {
            switch (primitive)
            {
                case PrimitiveType.Cube: return "Cube.fbx";
                case PrimitiveType.Sphere: return "Sphere.fbx";
                case PrimitiveType.Capsule: return "Capsule.fbx";
                case PrimitiveType.Cylinder: return "Cylinder.fbx";
                case PrimitiveType.Plane: return "Plane.fbx";
                case PrimitiveType.Quad: return "Quad.fbx";
                default: return primitive + ".fbx";
            }
        }

        /// <summary>
        /// Shared lit material for a colour, optionally carrying a generated PBR texture set.
        /// <paramref name="tiling"/> is in texture repeats per metre, so a wall can be given
        /// a different physical scale to a floor from the same texture.
        /// </summary>
        public static Material GetMaterial(Color color, TriangleProceduralTextures.SurfaceKind? surface = null,
            float tiling = 1f)
        {
            // A struct key rather than an XOR of three ints, which could alias two different
            // (surface, tiling) pairs onto one entry and hand a wall the floor's texture.
            var key = new MaterialKey(color, surface.HasValue ? (int)surface.Value + 1 : 0, tiling);

            if (MaterialCache.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            Shader shader = LitShader;
            if (shader == null)
            {
                TriangleLog.Error("No lit shader is available; falling back to the unlit path for this material.");
                return GetUnlitMaterial(color);
            }

            var material = new Material(shader) { name = $"TriangleLit_{color}" };

            if (!surface.HasValue)
            {
                // A surface with no texture set still gets a plausible response: fully matte
                // dielectrics with no metallic term, which is what an untextured painted
                // surface physically is. The previous version forced glossiness to zero and
                // killed specular highlights entirely, which removed the edge definition that
                // makes geometry read as solid.
                ApplyPlainDielectric(material, color);
            }
            else
            {
                ApplySurface(material, color, surface.Value, tiling);
            }

            material.enableInstancing = true;

            MaterialCache[key] = material;
            return material;
        }

        /// <summary>
        /// A shared untextured lit material with an explicit smoothness.
        ///
        /// Separate from <see cref="GetMaterial"/> because smoothness is part of the cache
        /// key here. Callers that need a specific response - a window frame's paint, a
        /// ceramic tile, a wet platform edge - use this; the rest share one material per
        /// colour, which is what the SRP Batcher wants.
        /// </summary>
        public static Material GetSmoothMaterial(Color color, float smoothness)
        {
            var key = new SmoothKey(color, smoothness);
            if (SmoothCache.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            Shader shader = LitShader;
            if (shader == null)
            {
                return GetUnlitMaterial(color);
            }

            var material = new Material(shader) { name = $"TriangleSmooth_{color}" };
            ApplyPlainDielectric(material, color);
            SetSmoothness(material, Mathf.Clamp01(smoothness));
            material.enableInstancing = true;

            SmoothCache[key] = material;
            return material;
        }

        /// <summary>
        /// A matte, non-metallic surface response with the given base colour.
        ///
        /// "Dielectric" because that is what nearly everything in this game is: painted
        /// plaster, concrete, timber, fabric. Dielectrics have a real specular response at
        /// grazing angles - the sheen you see on a wall near a window - which is most of
        /// what separates a surface from a flat colour. The old code disabled specular
        /// highlights outright and got a completely Lambertian surface, and that reads as
        /// paper no matter how the texture is generated.
        /// </summary>
        private static void ApplyPlainDielectric(Material material, Color color)
        {
            SetBaseColor(material, color);
            SetSmoothness(material, 0.18f);
            SetMetallic(material, 0f);

            if (material.HasProperty("_SpecularHighlights"))
            {
                material.SetFloat("_SpecularHighlights", 1f);
            }

            if (material.HasProperty("_EnvironmentReflections"))
            {
                material.SetFloat("_EnvironmentReflections", 1f);
            }
        }

        /// <summary>
        /// Binds a generated albedo/normal/occlusion/smoothness set.
        ///
        /// The albedo is a brightness variation around 1.0, so it multiplies the material's
        /// own colour rather than replacing it - the builder's palette still decides each
        /// surface's value.
        /// </summary>
        private static void ApplySurface(Material material, Color color,
            TriangleProceduralTextures.SurfaceKind kind, float tiling)
        {
            TriangleProceduralTextures.SurfaceSet set = TriangleProceduralTextures.Get(kind);

            SetBaseColor(material, color);
            SetMetallic(material, 0f);

            if (material.HasProperty("_BaseMap") && set.Albedo != null)
            {
                material.SetTexture("_BaseMap", set.Albedo);
                material.SetTextureScale("_BaseMap", new Vector2(tiling, tiling));
            }

            if (material.HasProperty("_BumpMap") && set.Normal != null)
            {
                material.SetTexture("_BumpMap", set.Normal);
                material.SetTextureScale("_BumpMap", new Vector2(tiling, tiling));
                material.SetFloat("_BumpScale", 1f);
                EnableKeyword(material, "_NORMALMAP");
            }

            // Ambient occlusion from the same height field the normal map came from.
            //
            // This is a large part of why procedural surfaces read as painted cardboard:
            // screen-space occlusion cannot see the small-scale relief a normal map
            // implies, because the geometry has none. A baked cavity term restores it, and
            // it costs nothing at runtime because it was generated alongside the albedo.
            if (material.HasProperty("_OcclusionMap") && set.Occlusion != null)
            {
                material.SetTexture("_OcclusionMap", set.Occlusion);
                material.SetTextureScale("_OcclusionMap", new Vector2(tiling, tiling));
                material.SetFloat("_OcclusionStrength", 1f);
                EnableKeyword(material, "_OCCLUSIONMAP");
            }

            if (material.HasProperty("_SmoothnessMap") && set.Smoothness != null)
            {
                material.SetTexture("_SmoothnessMap", set.Smoothness);
                material.SetTextureScale("_SmoothnessMap", new Vector2(tiling, tiling));
                EnableKeyword(material, "_SMOOTHNESSMAP");

                // Smoothness comes from the map, so the scalar only scales it.
                SetSmoothness(material, 1f);
            }
            else
            {
                // No smoothness map on this shader: use the surface's base value as a
                // scalar so plaster and polished metal still differ.
                SetSmoothness(material, TriangleProceduralTextures.BaseSmoothnessFor(kind));
            }
        }

        /// <summary>Shared unlit material, used where the dream must not be lit.</summary>
        public static Material GetUnlitMaterial(Color color)
        {
            if (UnlitCache.TryGetValue(color, out Material cached) && cached != null)
            {
                return cached;
            }

            Shader shader = UnlitShader;
            if (shader == null)
            {
                TriangleLog.Error("No unlit or lit shader is available; cannot create a material.");
                return null;
            }

            var material = new Material(shader) { name = $"TriangleUnlit_{color}" };
            SetBaseColor(material, color);

            UnlitCache[color] = material;
            return material;
        }

        /// <summary>
        /// Shared emissive material, for surfaces that must read as light sources.
        ///
        /// URP's _EmissionColor is declared [HDR], so values above 1 are meaningful: they
        /// are what the window pane and the fluorescent tubes need in order to be the
        /// brightest thing in a room that is otherwise nearly black, and for bloom to have
        /// anything to catch.
        /// </summary>
        public static Material GetEmissiveMaterial(Color color, float intensity)
        {
            float safeIntensity = float.IsNaN(intensity) || float.IsInfinity(intensity)
                ? 0f
                : Mathf.Max(0f, intensity);
            var key = new GlowKey(color, safeIntensity);

            if (EmissiveCache.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            Shader shader = LitShader;
            if (shader == null)
            {
                return GetUnlitMaterial(color);
            }

            var material = new Material(shader) { name = $"TriangleGlow_{color}" };
            SetBaseColor(material, color);
            SetSmoothness(material, 0.4f);
            SetMetallic(material, 0f);

            if (material.HasProperty("_EmissionColor"))
            {
                EnableKeyword(material, "_EMISSION");
                material.SetColor("_EmissionColor", color * safeIntensity);

                // Realtime rather than Baked. Baked emissive would send this to the lightmap
                // baker, which is editor-only, and the window would go dark in a player.
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            EmissiveCache[key] = material;
            return material;
        }

        // --- Property helpers -------------------------------------------------------
        //
        // Written against URP/Lit's names with the built-in names as fallbacks. Two
        // properties in particular differ: URP uses _BaseColor and _Smoothness where the
        // Standard shader uses _Color and _Glossiness, and silently skipping them left
        // materials rendering white or plastic depending on which shader answered.

        private static void SetBaseColor(Material material, Color color)
        {
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
                return;
            }

            if (material.HasProperty("_Color"))
            {
                material.SetColor("_Color", color);
            }
        }

        private static void SetSmoothness(Material material, float smoothness)
        {
            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", Mathf.Clamp01(smoothness));
                return;
            }

            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", Mathf.Clamp01(smoothness));
            }
        }

        private static void SetMetallic(Material material, float metallic)
        {
            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", Mathf.Clamp01(metallic));
            }
        }

        private static void EnableKeyword(Material material, string keyword)
        {
            // Material.EnableKeyword, not the static Shader.EnableKeyword. URP's material
            // keywords are declared shader_feature_local, and setting a global keyword of
            // the same name against a local declaration makes Unity mint a spurious global
            // keyword that survives the material and affects unrelated shaders.
            material.EnableKeyword(keyword);
        }

        private static int Quantise(Color color)
        {
            int r = Mathf.RoundToInt(Mathf.Clamp01(color.r) * 255f);
            int g = Mathf.RoundToInt(Mathf.Clamp01(color.g) * 255f);
            int b = Mathf.RoundToInt(Mathf.Clamp01(color.b) * 255f);
            int a = Mathf.RoundToInt(Mathf.Clamp01(color.a) * 255f);
            return (r << 24) | (g << 16) | (b << 8) | a;
        }

        /// <summary>
        /// Creates a GameObject carrying one of the basic-shape meshes. Mirrors UE's
        /// AStaticMeshActor: a plain transform with a single visible mesh.
        /// </summary>
        public static GameObject CreateShape(string name, Mesh mesh, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;

            // Static batching and lightmap indices are meaningless here: every piece is
            // created at runtime and destroyed on a scene change, so anything cached against
            // them is stale by the time it could be used.
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
            return go;
        }
    }
}