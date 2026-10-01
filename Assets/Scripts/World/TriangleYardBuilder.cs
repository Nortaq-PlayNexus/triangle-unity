using System.Collections.Generic;
using Triangle.Core;
using Triangle.Dream;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// The yard outside the bedroom: the first environment with weather and distance.
    ///
    /// The bedroom and the station are both sealed boxes, so the slice had no exterior
    /// and therefore no terrain. This is that exterior - a hillside at night, the back
    /// of the house, and a treeline receding into fog.
    ///
    /// It is deliberately the wrong night sky. The design calls the moon on the wrong
    /// side of the window one of the false-awakening difference lines, and the only way
    /// for the player to have ever seen the sky is for there to be a sky to be wrong
    /// about. The moon sits low and behind the treeline, so from inside the bedroom it
    /// is visible in the wrong place.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleYardBuilder : MonoBehaviour
    {
        [Header("Ground")]
        [Tooltip("Terrain size in metres. Large enough that the treeline is never obviously at the edge.")]
        [SerializeField] private float terrainSize = 260f;

        [SerializeField] private float terrainHeight = 34f;

        [Header("House")]
        [Tooltip("The bedroom's back wall sits here, so the window looks out at the yard from the right angle.")]
        [SerializeField] private float houseOffsetZ = 96f;

        [SerializeField] private float houseHalfX = 3.1f;
        [SerializeField] private float houseHalfZ = 4.1f;
        [SerializeField] private float houseHeight = 3.0f;

        [Header("Fence")]
        [SerializeField] private int fencePostSpacing = 2;

        [Header("Scatter")]
        [SerializeField] private int rockCount = 60;

        private readonly List<GameObject> _spawned = new List<GameObject>();
        private readonly List<Material> _ownedMaterials = new List<Material>();

        private Transform _start;

        /// <summary>Where the player arrives in the yard.</summary>
        public Transform DreamStart => _start;

        /// <summary>World position of the moon, for the sky and for the false-awakening beat.</summary>
        public Vector3 MoonLocation { get; private set; }

        /// <summary>The terrain, so other systems can sample ground height.</summary>
        public TriangleTerrainBuilder Terrain { get; private set; }

        private void Awake()
        {
            BuildTerrain();
            BuildHouse();
            BuildFence();
            BuildRocks();
            BuildStart();
            BuildLighting();

            TriangleLog.Log("The yard assembled.");
        }

        private void OnDestroy()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                {
                    Destroy(_spawned[i]);
                }
            }

            _spawned.Clear();

            for (int i = 0; i < _ownedMaterials.Count; i++)
            {
                if (_ownedMaterials[i] != null)
                {
                    Destroy(_ownedMaterials[i]);
                }
            }

            _ownedMaterials.Clear();
        }

        // --- Terrain ----------------------------------------------------------------

        private void BuildTerrain()
        {
            GameObject terrainObject = new GameObject("YardTerrain");
            terrainObject.transform.SetParent(transform, false);
            Track(terrainObject);

            Terrain = terrainObject.AddComponent<TriangleTerrainBuilder>();

            // Placed before the build, not after. The splat blend and the tree scatter
            // are computed in the terrain's own local space, so the object has to be
            // where it is going to end up before those are generated - otherwise the
            // height field is laid out relative to a position the terrain never
            // occupies, and every tree lands somewhere the ground is not.
            //
            // Centred on the house rather than on the origin, so the house sits well
            // inside the footprint with ground extending past it in every direction.
            terrainObject.transform.position = new Vector3(0f, 0f, houseOffsetZ);
            Terrain.Build(terrainSize, terrainHeight);
        }

        // --- House ------------------------------------------------------------------

        /// <summary>
        /// The outside of the house, built from the same dimensions as the bedroom so
        /// the two agree. Only the two walls the player can see from the yard are
        /// built; the rest would be hidden by the building itself.
        /// </summary>
        private void BuildHouse()
        {
            var wallColor = new Color(0.152f, 0.145f, 0.138f);
            var trimColor = new Color(0.108f, 0.102f, 0.096f);

            float groundY = SampleGround(0f, houseOffsetZ + houseHalfZ + 6f);
            float baseZ = houseOffsetZ + houseHalfZ;

            // The back wall, with a window opening cut into it by building it as four
            // pieces around the gap rather than trying to boolean a hole.
            AddBox("HouseBackLeft",
                new Vector3((-houseHalfX - 1.1f) * 0.5f, houseHeight * 0.5f, baseZ),
                new Vector3(houseHalfX - 1.1f, houseHeight, 0.2f), wallColor);

            AddBox("HouseBackRight",
                new Vector3((houseHalfX + 1.1f) * 0.5f, houseHeight * 0.5f, baseZ),
                new Vector3(houseHalfX - 1.1f, houseHeight, 0.2f), wallColor);

            AddBox("HouseBackBelowWindow",
                new Vector3(0f, 0.44f, baseZ),
                new Vector3(2.2f, 0.88f, 0.2f), wallColor);

            AddBox("HouseBackAboveWindow",
                new Vector3(0f, 2.61f, baseZ),
                new Vector3(2.2f, 0.78f, 0.2f), wallColor);

            // Side walls running away from the player.
            float sideLength = 9f;
            AddBox("HouseSideLeft",
                new Vector3(-houseHalfX, houseHeight * 0.5f, baseZ + sideLength * 0.5f),
                new Vector3(0.2f, houseHeight, sideLength), wallColor);

            AddBox("HouseSideRight",
                new Vector3(houseHalfX, houseHeight * 0.5f, baseZ + sideLength * 0.5f),
                new Vector3(0.2f, houseHeight, sideLength), wallColor);

            // Roof pitch, two slabs.
            AddBox("HouseRoofLeft",
                new Vector3(-houseHalfX * 0.5f, houseHeight + 0.62f, baseZ + sideLength * 0.5f),
                new Vector3(houseHalfX * 1.15f, 0.14f, sideLength + 0.5f), trimColor,
                Quaternion.Euler(0f, 0f, 24f));

            AddBox("HouseRoofRight",
                new Vector3(houseHalfX * 0.5f, houseHeight + 0.62f, baseZ + sideLength * 0.5f),
                new Vector3(houseHalfX * 1.15f, 0.14f, sideLength + 0.5f), trimColor,
                Quaternion.Euler(0f, 0f, -24f));

            // A dark pane in the window, so from outside the house reads as closed and
            // faintly lit rather than as a hole. From inside it is the emissive panel the
            // bedroom already builds, so the two never disagree.
            GameObject pane = AddBox("HouseWindowPane",
                new Vector3(0f, 1.55f, baseZ - 0.06f),
                new Vector3(1.1f, 1.34f, 0.04f), new Color(0.115f, 0.145f, 0.205f));
            pane.GetComponent<MeshRenderer>().sharedMaterial =
                GetOwnedMaterial(new Color(0.115f, 0.150f, 0.220f), 0.6f, "HousePane");

            // A path from the back door to the treeline, which is what makes the yard
            // read as a place someone walks through rather than as a field.
            float pathY = SampleGround(0f, baseZ + 4f);
            AddBox("Path", new Vector3(0f, pathY + 0.03f, baseZ + 7f),
                new Vector3(1.1f, 0.06f, 13f), new Color(0.175f, 0.165f, 0.150f));
        }

        // --- Fence -------------------------------------------------------------------

        /// <summary>
        /// A run of fence along the far side of the yard.
        ///
        /// Its job is to give the eye something with a known size in the middle distance,
        /// so the treeline behind it has something to be behind. Without it the trees
        /// have no scale reference and the hillside reads as wallpaper.
        /// </summary>
        private void BuildFence()
        {
            float fenceZ = houseOffsetZ + houseHalfZ + 24f;
            float span = 34f;
            float postHeight = 1.1f;

            var wood = GetOwnedMaterial(new Color(0.128f, 0.108f, 0.088f), 0.1f, "Fence");

            int posts = Mathf.Max(2, Mathf.RoundToInt(span / Mathf.Max(1, fencePostSpacing)));
            for (int i = 0; i <= posts; i++)
            {
                float x = -span * 0.5f + span * (i / (float)posts);
                float y = SampleGround(x, fenceZ);
                AddBox("FencePost" + i, new Vector3(x, y + postHeight * 0.5f, fenceZ),
                    new Vector3(0.09f, postHeight, 0.09f), default(Color), null, wood);
            }

            // Two rails. The lower one is deliberately a little saggy in the middle,
            // which is the detail that stops a fence looking extruded.
            for (int rail = 0; rail < 2; rail++)
            {
                float railHeight = 0.42f + rail * 0.44f;
                float sag = rail == 0 ? 0.06f : 0.03f;
                for (int i = 0; i < posts; i++)
                {
                    float x0 = -span * 0.5f + span * (i / (float)posts);
                    float x1 = -span * 0.5f + span * ((i + 1) / (float)posts);
                    float y0 = SampleGround(x0, fenceZ) + railHeight;
                    float y1 = SampleGround(x1, fenceZ) + railHeight - sag;

                    var mid = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, fenceZ);
                    var dir = new Vector3(x1 - x0, y1 - y0, 0f);
                    float length = dir.magnitude;
                    if (length < 0.01f)
                    {
                        continue;
                    }

                    var railObject = TriangleShapes.CreateShape("FenceRail" + rail + "_" + i, TriangleShapes.Cube, transform);
                    railObject.transform.SetParent(transform, false);
                    railObject.transform.position = mid;
                    railObject.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up) * Quaternion.Euler(0f, 90f, 0f);
                    railObject.transform.localScale = new Vector3(0.06f, 0.09f, length);
                    railObject.GetComponent<MeshRenderer>().sharedMaterial = wood;
                    Track(railObject);
                }
            }
        }

        // --- Rocks ---------------------------------------------------------------------

        /// <summary>
        /// Scattered boulders, placed by hand rather than by the terrain's detail layer.
        ///
        /// Large enough to read as objects in their own right. The terrain's stone detail
        /// is the right size for something underfoot; these are the ones you can stand
        /// behind.
        /// </summary>
        private void BuildRocks()
        {
            var random = new System.Random(90210);
            var rockMaterials = new[]
            {
                GetOwnedMaterial(new Color(0.135f, 0.138f, 0.142f), 0.2f, "Rock"),
                GetOwnedMaterial(new Color(0.115f, 0.120f, 0.126f), 0.2f, "RockDark"),
                GetOwnedMaterial(new Color(0.155f, 0.152f, 0.145f), 0.15f, "RockLight")
            };

            for (int i = 0; i < rockCount; i++)
            {
                float x = (float)(random.NextDouble() - 0.5) * terrainSize * 0.75f;
                float z = houseOffsetZ + (float)random.NextDouble() * 70f - 20f;
                float y = SampleGround(x, z);

                float scale = Mathf.Lerp(0.6f, 2.8f, (float)random.NextDouble());
                var rock = TriangleShapes.CreateShape("Boulder" + i, TriangleShapes.Sphere, transform);
                rock.transform.SetParent(transform, false);
                rock.transform.position = new Vector3(x, y + scale * 0.18f, z);
                rock.transform.rotation = Quaternion.Euler(
                    (float)random.NextDouble() * 30f,
                    (float)random.NextDouble() * 360f,
                    (float)random.NextDouble() * 30f);

                // Squashed on Y. A perfect sphere reads as a ball; a boulder is wider
                // than it is tall and has a flatter base.
                rock.transform.localScale = new Vector3(
                    scale,
                    scale * Mathf.Lerp(0.5f, 0.8f, (float)random.NextDouble()),
                    scale * Mathf.Lerp(0.8f, 1.2f, (float)random.NextDouble()));

                rock.GetComponent<MeshRenderer>().sharedMaterial = rockMaterials[i % rockMaterials.Length];
                Track(rock);
            }
        }

        // --- Start and light -----------------------------------------------------------

        private void BuildStart()
        {
            float z = houseOffsetZ + houseHalfZ + 5.5f;
            float y = SampleGround(0f, z);

            GameObject start = new GameObject("DreamStart");
            start.transform.SetParent(transform, false);
            start.transform.position = new Vector3(0f, y + 0.9f, z);
            // Facing the house, so the first thing the player sees is the back of the
            // bedroom with its lit window - the view they have been looking at from the
            // inside for the whole first environment.
            start.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            start.tag = "DreamStart";
            Track(start);
            _start = start.transform;
        }

        /// <summary>
        /// Night lighting for an exterior.
        ///
        /// Deliberately not the bedroom's interior setup. Outdoors the key light is the
        /// moon, so it is a single cold directional light with shadows, plus a very low
        /// fill. The window glow is a local point light so the house reads as occupied
        /// from outside, which is the detail that makes the yard feel observed.
        /// </summary>
        private void BuildLighting()
        {
            GameObject moonObject = new GameObject("MoonLight");
            moonObject.transform.SetParent(transform, false);
            MoonLocation = new Vector3(-120f, 78f, 40f);
            moonObject.transform.position = MoonLocation;
            moonObject.transform.rotation = Quaternion.LookRotation((Vector3.zero - MoonLocation).normalized, Vector3.up);

            Light moon = moonObject.AddComponent<Light>();
            moon.type = LightType.Directional;
            moon.color = new Color(0.52f, 0.62f, 0.85f);
            moon.intensity = 0.42f;
            moon.shadows = LightShadows.Soft;
            moon.shadowStrength = 0.7f;
            moon.shadowBias = 0.04f;
            moon.shadowNearPlane = 0.1f;
            Track(moonObject);

            AddLight("WindowGlow",
                new Vector3(0f, SampleGround(0f, houseOffsetZ + houseHalfZ) + 1.55f, houseOffsetZ + houseHalfZ - 0.6f),
                new Color(0.72f, 0.78f, 0.95f), 1.1f, 7f);

            // The atmosphere owns ambient and fog for this environment, same as the others.
            GameObject atmosphereObject = new GameObject("YardAtmosphere");
            atmosphereObject.transform.SetParent(transform, false);
            Track(atmosphereObject);

            var atmosphere = atmosphereObject.AddComponent<TriangleEnvironmentAtmosphere>();
            atmosphere.Ambient = UnityEngine.Rendering.AmbientMode.Trilight;
            atmosphere.SkyColor = new Color(0.085f, 0.105f, 0.155f);
            atmosphere.EquatorColor = new Color(0.045f, 0.052f, 0.070f);
            atmosphere.GroundColor = new Color(0.012f, 0.013f, 0.017f);
            atmosphere.Intensity = 0.5f;
            atmosphere.FogEnabled = true;
            atmosphere.Fog = FogMode.ExponentialSquared;
            // Thicker than either interior. This is the depth cue that makes the
            // hillside read as a hillside rather than as a backdrop.
            atmosphere.FogDensity = 0.022f;
            atmosphere.FogColor = new Color(0.030f, 0.038f, 0.058f);
            atmosphere.Apply();
        }

        // --- Helpers --------------------------------------------------------------------

        private float SampleGround(float worldX, float worldZ)
        {
            return Terrain != null ? Terrain.SampleHeight(new Vector3(worldX, 0f, worldZ)) : 0f;
        }

        private GameObject AddBox(string name, Vector3 position, Vector3 scale, Color color,
            Quaternion? rotation = null, Material shared = null)
        {
            GameObject box = TriangleShapes.CreateShape(name, TriangleShapes.Cube, transform);
            box.transform.SetParent(transform, false);
            box.transform.position = position;
            box.transform.rotation = rotation ?? Quaternion.identity;
            box.transform.localScale = scale;

            box.GetComponent<MeshRenderer>().sharedMaterial = shared != null
                ? shared
                : GetOwnedMaterial(color, 0.05f, name);

            Track(box);
            return box;
        }

        private void AddLight(string name, Vector3 position, Color color, float intensity, float range)
        {
            GameObject lightObject = new GameObject(name);
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.position = position;
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = LightShadows.None;
            Track(lightObject);
        }

        private readonly Dictionary<int, Material> _materials = new Dictionary<int, Material>();

        private Material GetOwnedMaterial(Color color, float smoothness, string name)
        {
            int key = Mathf.RoundToInt(color.r * 255) << 16
                      | Mathf.RoundToInt(color.g * 255) << 8
                      | Mathf.RoundToInt(color.b * 255);

            if (_materials.TryGetValue(key, out Material cached) && cached != null)
            {
                return cached;
            }

            var material = new Material(TriangleShapes.LitShader)
            {
                name = $"TriangleYard_{name}_{color}",
                color = color
            };

            if (material.HasProperty("_Glossiness"))
            {
                material.SetFloat("_Glossiness", smoothness);
            }

            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", 0f);
            }

            _materials[key] = material;
            _ownedMaterials.Add(material);
            return material;
        }

        private GameObject Track(GameObject go)
        {
            _spawned.Add(go);
            return go;
        }
    }
}
