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
        private static readonly Dictionary<int, Material> MaterialCache = new Dictionary<int, Material>();
        private static readonly Dictionary<int, Material> UnlitCache = new Dictionary<int, Material>();
        private static readonly Dictionary<int, Material> EmissiveCache = new Dictionary<int, Material>();

        private static Shader _litShader;
        private static Shader _unlitShader;

        private static Shader LitShader
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

                _coneMesh = new Mesh { name = "TriangleCone" };
                _coneMesh.SetVertices(new List<Vector3>(coneVertices));
                _coneMesh.SetTriangles(coneTriangles, 0);
                _coneMesh.RecalculateNormals();
                _coneMesh.RecalculateBounds();
                return _coneMesh;
            }
        }

        private static Mesh _coneMesh;

        public static Mesh GetMesh(PrimitiveType primitive)
        {
            if (MeshCache.TryGetValue(primitive, out Mesh cached) && cached != null)
            {
                return cached;
            }

            GameObject temp = GameObject.CreatePrimitive(primitive);
            Mesh mesh = temp.GetComponent<MeshFilter>()?.sharedMesh;
            Object.DestroyImmediate(temp);
            MeshCache[primitive] = mesh;
            return mesh;
        }

        /// <summary>Shared lit material for a colour, standing in for UE vertex tinting.</summary>
        public static Material GetMaterial(Color color)
        {
            int key = Quantise(color);
            if (MaterialCache.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            var material = new Material(LitShader) { name = $"TriangleLit_{color}" };
            if (material.HasProperty("_Color"))
            {
                material.color = color;
            }

            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", 0.12f);
            }

            MaterialCache[key] = material;
            return material;
        }

        /// <summary>Shared unlit material, used where the dream must not be lit.</summary>
        public static Material GetUnlitMaterial(Color color)
        {
            int key = Quantise(color);
            if (UnlitCache.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            var material = new Material(UnlitShader) { name = $"TriangleUnlit_{color}" };
            if (material.HasProperty("_Color"))
            {
                material.color = color;
            }

            UnlitCache[key] = material;
            return material;
        }

        /// <summary>Shared emissive material, for the Triangle's wrong-coloured glow.</summary>
        public static Material GetEmissiveMaterial(Color color, float intensity)
        {
            int key = Quantise(color) ^ (Mathf.RoundToInt(intensity * 100f) * 7919);
            if (EmissiveCache.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            var material = new Material(LitShader) { name = $"TriangleGlow_{color}" };
            if (material.HasProperty("_Color"))
            {
                material.color = color;
            }

            if (material.HasProperty("_EmissionColor"))
            {
                material.EnableKeyword("_EMISSION");
                material.SetColor("_EmissionColor", color * intensity);
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
