using Triangle.Core;
using Triangle.World;
using UnityEngine;

namespace Triangle.Dream
{
    /// <summary>
    /// The upper-corner point: the first shape the player learns. A small cone with
    /// a faint, wrong-coloured glow that the Dream Director manifests and hides.
    ///
    /// Port of ATriangleTrianglePoint. The UE point light becomes a Unity point light
    /// with an emissive material, because a light alone is invisible against a dark
    /// room without a visible surface to catch it.
    /// </summary>
    [DisallowMultipleComponent]
    public class TrianglePoint : MonoBehaviour
    {
        [Tooltip("Stable id recorded when the player witnesses this point.")]
        public string ManifestId = "UpperCornerPoint";

        [SerializeField] private float glowIntensity = 6f;
        [SerializeField] private float glowRadiusCm = 350f;
        [SerializeField] private float scale = 0.16f;

        private static readonly Color GlowColor = new Color(0.25f, 0.45f, 0.50f);

        private Transform _mesh;
        private Light _glow;
        private bool _pointVisible;

        public bool IsVisible => _pointVisible;

        /// <summary>
        /// Finds the corner point anywhere in the loaded scene, hidden or not.
        ///
        /// The point hides itself by deactivating its GameObject, so the ordinary
        /// FindObjectOfType overloads skip it entirely. Anything that needs to know
        /// *where* the point is - the Dream Director, tests, tooling - must ask for
        /// inactive objects too, which is what this helper centralises.
        /// </summary>
        public static TrianglePoint FindInScene()
        {
            return FindObjectOfType<TrianglePoint>(true);
        }

        private void Awake()
        {
            GameObject meshObject = TriangleShapes.CreateShape("TriangleMesh", TriangleShapes.ConeMesh, transform);
            meshObject.transform.localScale = Vector3.one * scale;
            MeshRenderer meshRenderer = meshObject.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = TriangleShapes.GetEmissiveMaterial(GlowColor, glowIntensity * 0.35f);
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mesh = meshObject.transform;

            var lightObject = new GameObject("GlowLight");
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.localPosition = new Vector3(0f, 0.3f, 0f);
            _glow = lightObject.AddComponent<Light>();
            _glow.type = LightType.Point;
            _glow.intensity = glowIntensity;
            _glow.color = GlowColor;
            _glow.range = Cm.M(glowRadiusCm);
            _glow.shadows = LightShadows.None;

            SetPointVisible(false);
        }

        private void Start()
        {
            SetPointVisible(_pointVisible);
        }

        /// <summary>Shows the point for the given duration, then hides it again.</summary>
        public void Manifest(float duration)
        {
            SetPointVisible(true);
            TriangleLog.DreamVerbose($"Triangle point '{ManifestId}' manifesting for {duration:0.0}s.");

            CancelInvoke(nameof(Hide));
            if (duration > 0f && float.IsNaN(duration) == false)
            {
                Invoke(nameof(Hide), Mathf.Max(0.05f, duration));
            }
        }

        public void Hide()
        {
            CancelInvoke(nameof(Hide));
            SetPointVisible(false);
            TriangleLog.DreamVerbose($"Triangle point '{ManifestId}' receded.");
        }

        private void SetPointVisible(bool visible)
        {
            _pointVisible = visible;
            gameObject.SetActive(visible);
            if (_mesh != null)
            {
                _mesh.gameObject.SetActive(visible);
            }

            if (_glow != null)
            {
                _glow.enabled = visible;
            }
        }
    }
}
