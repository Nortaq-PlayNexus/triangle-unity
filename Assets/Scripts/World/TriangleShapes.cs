using System;
using System.Collections.Generic;
using Triangle.Core;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// Runtime "basic shapes" and materials.
    ///
    /// The Unreal slice used /Engine/BasicShapes (Cube, Cylinder, Cone, Sphere) with
    /// per-component vertex tint so the whole vertical slice ran with zero content
    /// assets. Unity has no equivalent built-in mesh library that can be handed to a
    /// MeshFilter, so this factory extracts the primitive meshes once and keeps a
    /// shared material cache in place of vertex colour.
    /// </summary>
    public static class TriangleShapes
    {
        private static readonly Dictionary<PrimitiveType, Mesh> MeshCache = new Dictionary<PrimitiveType, Mesh>();
        private static readonly Dictionary<MaterialKey, Material> MaterialCache = new Dictionary<MaterialKey, Material>();
        private static readonly Dictionary<Color, Material> UnlitCache = new Dictionary<Color, Material>();
        private static readonly Dictionary<GlowKey, Material> EmissiveCache = new Dictionary<GlowKey, Material>();

        private static Mesh _coneMesh;
        private static Shader _litShader;
        private static Shader _unlitShader;

        /// <summary>
        /// Drops every cached mesh, material and shader.
        ///
        /// The caches are static, so they outlive a play session whenever the editor
        /// enters play mode without a domain reload. That leaks a material per distinct
        /// colour and keeps the generated textures alive, which shows up as steadily
        /// growing memory across repeated play sessions. This runs before the first
        /// scene loads in a fresh domain, which is the only point at which the caches
        /// are guaranteed to be empty.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetCaches()
        {
            foreach (KeyValuePair<MaterialKey, Material> pair in MaterialCache)
            {
                DestroyCached(pair.Value);
            }

            foreach (KeyValuePair<Color, Material> pair in UnlitCache)
            {
                DestroyCached(pair.Value);
            }

            foreach (KeyValuePair<GlowKey, Material> pair in EmissiveCache)
            {
                DestroyCached(pair.Value);
            }

            MaterialCache.Clear();
            UnlitCache.Clear();
            EmissiveCache.Clear();

            if (_coneMesh != null)
            {
                DestroyCached(_coneMesh);
                _coneMesh = null;
            }

            // Unity's built-in primitive meshes are engine assets and must not be
            // destroyed; only the reference is dropped so they are re-fetched.
            MeshCache.Clear();

            _litShader = null;
            _unlitShader = null;

            TriangleProceduralTextures.ClearCache();
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

        /// <summary>Cache key for a lit surface. A struct key, so it cannot collide.</summary>
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

        /// <summary>Cache key for an emissive material.</summary>
        private readonly struct GlowKey : IEquatable<GlowKey>
        {
            private readonly Color _colour;
            private readonly int _intensity;

            public GlowKey(Color colour, float intensity)
            {
                _colour = colour;
                _intensity = Mathf.RoundToInt(intensity * 100f);
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
        /// Public because the terrain's scatter geometry is built and materialed
        /// outside this class, and it needs the same shader to be indistinguishable
        /// from the room surfaces beside it.
        /// </summary>
        public static Shader LitShader
        {
            get
            {
                if (_litShader == null)
                {
                    _litShader = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
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
                    _unlitShader = Shader.Find("Unlit/Color") ?? LitShader;
                }

                return _unlitShader;
            }
        }

        public static Mesh Cube => GetMesh(PrimitiveType.Cube);
        public static Mesh Cylinder => GetMesh(PrimitiveType.Cylinder);
        public static Mesh Sphere => GetMesh(PrimitiveType.Sphere);

        /// <summary>
        /// A true cone. Unity's primitive set has no cone, so this builds one once by
        /// tapering a cylinder's top ring to a point. The Triangle's point and its
        /// razor-gap manifestations depend on a real cone silhouette.
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

                // Unity's cylinder is 2 units tall (y -1..1) with rings at
                // y = -0.5 and y = +0.5 for the caps. Collapse the upper ring.
                var coneVertices = new Vector3[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 v = vertices[i];
                    if (v.y > 0.4f)
                    {
                        v.y = 1f;
                        v.x = 0f;
                        v.z = 0f;
                    }

                    coneVertices[i] = v;
                }

                // Keep only the side quads (the caps are now degenerate) and rebuild
                // them as triangles so the taper reads cleanly.
                var coneTriangles = new List<int>(triangles.Length);
                for (int i = 0; i + 2 < triangles.Length; i += 3)
                {
                    int a = triangles[i];
                    int b = triangles[i + 1];
                    int c = triangles[i + 2];

                    // Skip triangles whose vertices were all collapsed to the apex.
                    bool aCap = coneVertices[a].x == 0f && coneVertices[a].z == 0f && coneVertices[a].y >= 0.999f;
                    bool bCap = coneVertices[b].x == 0f && coneVertices[b].z == 0f && coneVertices[b].y >= 0.999f;
                    bool cCap = coneVertices[c].x == 0f && coneVertices[c].z == 0f && coneVertices[c].y >= 0.999f;

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

                    // Generated at runtime and never authored, so keep it out of scene
                    // saves and let it be reclaimed on unload.
                    hideFlags = HideFlags.HideAndDontSave
                };
                _coneMesh.SetVertices(new List<Vector3>(coneVertices));
                _coneMesh.SetTriangles(coneTriangles, 0);
                _coneMesh.RecalculateNormals();

                // Tangents are required for the generated normal maps to resolve
                // correctly; without them the shader falls back to screen-space
                // derivatives and the surface relief shifts with the camera.
                _coneMesh.RecalculateTangents();
                _coneMesh.RecalculateBounds();
                return _coneMesh;
            }
        }

        public static Mesh GetMesh(PrimitiveType primitive)
        {
            if (MeshCache.TryGetValue(primitive, out Mesh cached) && cached != null)
            {
                return cached;
            }

            // Reach for the engine's built-in mesh directly. The previous version built a
            // throwaway primitive GameObject per mesh and destroyed it with
            // DestroyImmediate, which is explicitly disallowed in play mode: the engine
            // logs a warning and the teardown is a full immediate destruction rather
            // than the deferred one the rest of the frame expects.
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
        /// Shared lit material for a colour, optionally carrying a generated PBR texture
        /// set. <paramref name="tiling"/> is in texture repeats per metre, so a wall can
        /// be given a different physical scale to a floor from the same texture.
        /// </summary>
        public static Material GetMaterial(Color color, TriangleProceduralTextures.SurfaceKind? surface = null,
            float tiling = 1f)
        {
            // A struct key rather than the previous XOR of three ints. The XOR could
            // alias two different (surface, tiling) pairs onto one cache entry, which
            // would hand a wall the floor's texture.
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
            if (material.HasProperty("_Color"))
            {
                material.color = color;
            }

            // Nothing in this game is shiny, and Unity's Standard shader's default
            // smoothness put a plastic highlight on every wall, floor and ceiling, which
            // read as flat "pastey" white under the ambient term. Forcing a matte
            // response keeps large flat surfaces looking like painted plaster, and the
            // generated smoothness map then varies that matte finish across the surface.
            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", 0f);
            }

            if (material.HasProperty("_SpecColor"))
            {
                material.SetColor("_SpecColor", Color.black);
            }

            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", 0f);
            }

            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");

            if (surface.HasValue)
            {
                ApplySurface(material, surface.Value, tiling);
            }

            MaterialCache[key] = material;
            return material;
        }

        /// <summary>
        /// Binds a generated albedo/normal/smoothness set and enables the normal map
        /// shader features. The albedo is a brightness variation around 1.0, so it is
        /// applied as a multiplier on the material's own colour rather than replacing
        /// it - the builder's authored palette still decides each surface's value.
        /// </summary>
        private static void ApplySurface(Material material, TriangleProceduralTextures.SurfaceKind kind, float tiling)
        {
            TriangleProceduralTextures.SurfaceSet set = TriangleProceduralTextures.Get(kind);

            if (material.HasProperty("_BumpMap"))
            {
                material.SetTexture("_BumpMap", set.Normal);
                material.SetTextureScale("_BumpMap", new Vector2(tiling, tiling));
                material.EnableKeyword("_NORMALMAP");
            }

            if (material.HasProperty("_SmoothnessMap") || material.HasProperty("_GlossinessMap"))
            {
                string property = material.HasProperty("_SmoothnessMap") ? "_SmoothnessMap" : "_GlossinessMap";
                material.SetTexture(property, set.Smoothness);
                material.SetTextureScale(property, new Vector2(tiling, tiling));
                // The texture supplies the actual glossiness, so the scalar must let it
                // through rather than overriding it with zero.
                if (material.HasProperty("_Glossiness"))
                {
                    material.SetFloat("_Glossiness", 1f);
                }

                material.EnableKeyword("_SMOOTHNESSMAP");
            }

            if (material.HasProperty("_MainTex"))
            {
                material.SetTexture("_MainTex", set.Albedo);
                material.SetTextureScale("_MainTex", new Vector2(tiling, tiling));
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
            if (material.HasProperty("_Color"))
            {
                material.color = color;
            }

            UnlitCache[color] = material;
            return material;
        }

        /// <summary>Shared emissive material, for the Triangle's wrong-coloured glow.</summary>
        public static Material GetEmissiveMaterial(Color color, float intensity)
        {
            float safeIntensity = float.IsNaN(intensity) || float.IsInfinity(intensity) ? 0f : Mathf.Max(0f, intensity);
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
            if (material.HasProperty("_Color"))
            {
                material.color = color;
            }

            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * safeIntensity);
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }

            EmissiveCache[key] = material;
            return material;
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
            return go;
        }
    }
}