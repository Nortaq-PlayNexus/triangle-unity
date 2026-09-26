using Triangle.Core;
using Triangle.Interaction;
using Triangle.World;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// The wrong train standing at Platform 12. Deliberately overlong and boxy, with
    /// door panels that hide a black interior aperture and a cold interior glow.
    ///
    /// Port of ATriangleTrain.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleTrain : MonoBehaviour
    {
        [SerializeField] private float maxInteractionRange = Cm.M(500f);

        private Transform _doorL;
        private Transform _doorR;
        private Transform _aperture;
        private Light _interiorLight;
        private BoxCollider _doorColliderL;
        private BoxCollider _doorColliderR;
        private TriangleInteractable _interactable;
        private bool _doorsOpen;

        private static readonly Color BodyColor = new Color(0.08f, 0.09f, 0.11f);
        private static readonly Color DoorColor = new Color(0.12f, 0.13f, 0.15f);
        private static readonly Color WindowColor = new Color(0.01f, 0.01f, 0.02f);
        private static readonly Color ApertureColor = new Color(0.002f, 0.002f, 0.004f);
        private static readonly Color InteriorLightColor = new Color(0.35f, 0.38f, 0.42f);

        private void Awake()
        {
            // Body: a long, low box. The wrong proportions - too tall, too boxy.
            GameObject body = TriangleShapes.CreateShape("Body", TriangleShapes.Cube, transform);
            body.transform.localScale = Cm.Scale(4f, 1.8f, 1.1f);
            body.transform.localPosition = new Vector3(0f, 0.4f, 0f);
            body.GetComponent<MeshRenderer>().sharedMaterial = TriangleShapes.GetMaterial(BodyColor);
            body.AddComponent<BoxCollider>().size = Vector3.one;

            // Doors: thin panels just proud of the body sides.
            _doorL = BuildPanel("DoorL", Cm.Scale(1.6f, 0.06f, 1.4f), new Vector3(1.65f, 0.93f, 0.4f), DoorColor, out _doorColliderL);
            _doorR = BuildPanel("DoorR", Cm.Scale(1.6f, 0.06f, 1.4f), new Vector3(-1.65f, -0.93f, 0.4f), DoorColor, out _doorColliderR);

            // Windows: a continuous dark strip just proud of the side.
            GameObject window = BuildPanel("Window", Cm.Scale(2.4f, 0.04f, 0.6f), new Vector3(0f, 0.93f, 0.6f), WindowColor, out _).gameObject;
            window.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // The body is a solid box, so a black inset supplies the visual depth of an
            // open carriage without requiring a bespoke train mesh.
            _aperture = BuildPanel("InteriorAperture", Cm.Scale(1.6f, 0.02f, 1.4f), new Vector3(1.65f, 0.915f, 0.4f), ApertureColor, out _);
            _aperture.gameObject.SetActive(false);

            // Interior light - a sickly cold glow that never quite illuminates.
            var lightObject = new GameObject("InteriorLight");
            lightObject.transform.SetParent(transform, false);
            lightObject.transform.localPosition = new Vector3(1.65f, 1.05f, 0.45f);
            _interiorLight = lightObject.AddComponent<Light>();
            _interiorLight.type = LightType.Point;
            _interiorLight.intensity = 6f;
            _interiorLight.color = InteriorLightColor;
            _interiorLight.range = Cm.M(350f);
            _interiorLight.shadows = LightShadows.None;
            _interiorLight.enabled = false;

            _interactable = gameObject.AddComponent<TriangleInteractable>();
            _interactable.InteractionName = "Open Wrong Train";
            _interactable.InteractionAnchor = body.transform;
            _interactable.MaxInteractionRange = maxInteractionRange;
            _interactable.OnInteracted += HandleInteracted;
        }

        private Transform BuildPanel(string partName, Vector3 localScale, Vector3 localPosition, Color color, out BoxCollider collider)
        {
            GameObject part = TriangleShapes.CreateShape(partName, TriangleShapes.Cube, transform);
            part.transform.localScale = localScale;
            part.transform.localPosition = localPosition;
            part.GetComponent<MeshRenderer>().sharedMaterial = TriangleShapes.GetMaterial(color);
            collider = part.AddComponent<BoxCollider>();
            collider.size = Vector3.one;
            return part.transform;
        }

        private void HandleInteracted(TriangleInteractable interactable, GameObject interactor)
        {
            ToggleDoors();
            TriangleLog.Log($"Wrong train doors toggled (open={_doorsOpen}).");
        }

        public void ToggleDoors()
        {
            SetDoorsOpen(!_doorsOpen);
        }

        public void SetDoorsOpen(bool open)
        {
            _doorsOpen = open;

            // Closed doors remain visible and block the opening. Opening removes both
            // the visual panels and their collision, revealing the dark interior.
            bool doorPanelsVisible = !open;
            if (_doorL != null)
            {
                _doorL.gameObject.SetActive(doorPanelsVisible);
            }

            if (_doorR != null)
            {
                _doorR.gameObject.SetActive(doorPanelsVisible);
            }

            if (_doorColliderL != null)
            {
                _doorColliderL.enabled = doorPanelsVisible;
            }

            if (_doorColliderR != null)
            {
                _doorColliderR.enabled = doorPanelsVisible;
            }

            if (_aperture != null)
            {
                _aperture.gameObject.SetActive(open);
            }

            if (_interiorLight != null)
            {
                _interiorLight.enabled = open;
            }

            if (_interactable != null)
            {
                _interactable.InteractionName = open ? "Close Wrong Train" : "Open Wrong Train";
            }
        }
    }
}
