using Triangle.Core;
using Triangle.Interaction;
using Triangle.Lucidity;
using Triangle.World;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// The childhood bedroom's door. It opens at High Lucidity, stays interactable
    /// afterwards, and requests a deliberate second interaction before the dream
    /// will cut to the station.
    ///
    /// Port of ATriangleDoor. The UE tick that ran only while swinging becomes an
    /// Update that early-outs unless a swing is in progress.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleDoor : MonoBehaviour
    {
        [SerializeField] private float swingDuration = 0.8f;
        [SerializeField] private float openYawDegrees = -95f;
        [SerializeField] private float maxInteractionRange = Cm.M(230f);

        /// <summary>Stable reference destination beyond the door, in Unreal centimetres.</summary>
        private static readonly Vector3 BeyondOffset = new Vector3(180f, 0f, 0f);

        private TriangleInteractable _interactable;
        private Transform _doorMesh;
        private Quaternion _closedRotation;
        private Quaternion _openRotation;
        private bool _isOpen;
        private bool _swinging;
        private float _swingAlpha;

        public bool IsOpen => _isOpen;

        /// <summary>Raised when the door wants the dream to cut to the next environment.</summary>
        public event System.Action<TriangleDoor> OnTransitionRequested;

        private void Awake()
        {
            _closedRotation = transform.rotation;
            _openRotation = _closedRotation * Quaternion.Euler(0f, openYawDegrees, 0f);

            GameObject panel = TriangleShapes.CreateShape("DoorMesh", TriangleShapes.Cube, transform);
            // Door panel ~15 cm thick, ~100 cm wide, ~210 cm tall - matching the
            // front-wall gap built by the room builder. The panel stands in the +Z
            // wall, so its thin axis is Z.
            panel.transform.localScale = new Vector3(1.0f, 2.1f, 0.15f);
            MeshRenderer panelRenderer = panel.GetComponent<MeshRenderer>();
            panelRenderer.sharedMaterial = TriangleShapes.GetMaterial(new Color(0.20f, 0.16f, 0.13f));
            BoxCollider panelCollider = panel.AddComponent<BoxCollider>();
            panelCollider.size = Vector3.one;
            _doorMesh = panel.transform;

            _interactable = gameObject.AddComponent<TriangleInteractable>();
            _interactable.InteractionName = "Door";
            _interactable.InteractionAnchor = _doorMesh;
            _interactable.MaxInteractionRange = maxInteractionRange;
            _interactable.OnInteracted += HandleInteracted;
        }

        private void HandleInteracted(TriangleInteractable interactable, GameObject interactor)
        {
            if (_swinging)
            {
                return;
            }

            if (_isOpen)
            {
                // Keep the component enabled after opening. A second deliberate press
                // is the seam where the environment flow requests the station cut.
                RequestTransition();
                return;
            }

            TriangleLucidity lucidity = TriangleRuntime.Player != null ? TriangleRuntime.Player.Lucidity : null;
            bool lucidEnough = lucidity != null && lucidity.CanManipulateDream();

            if (lucidEnough)
            {
                Open(false);
                TriangleLog.Dream("The door relented and opened into darkness.");
                TriangleRuntime.Hud?.PushSubtitle("The door opens into darkness.", 2.5f);
            }
            else
            {
                TriangleRuntime.Hud?.PushSubtitle("It won't move. The dream is not done with you.", 3f);
            }
        }

        public void Open(bool instant)
        {
            if (_isOpen)
            {
                if (instant)
                {
                    transform.rotation = _openRotation;
                    _swinging = false;
                }

                return;
            }

            _isOpen = true;
            float configuredDuration = float.IsNaN(swingDuration) || float.IsInfinity(swingDuration) ? 0f : swingDuration;
            if (instant || configuredDuration <= 0.0001f)
            {
                transform.rotation = _openRotation;
                _swinging = false;
                return;
            }

            _swinging = true;
            _swingAlpha = 0f;
        }

        public void Close(bool instant)
        {
            _isOpen = false;
            _swinging = false;
            transform.rotation = _closedRotation;
        }

        public void RequestTransition()
        {
            if (!_isOpen || _swinging)
            {
                return;
            }

            TriangleRuntime.Hud?.PushSubtitle("The darkness waits beyond.", 2.5f);
            Vector3 beyond = GetBeyondLocation();
            TriangleLog.Dream($"Door transition requested; beyond location is {beyond}.");
            OnTransitionRequested?.Invoke(this);
        }

        /// <summary>
        /// Use the closed orientation as the stable reference. The door rotates around
        /// its own origin, so this point remains a useful destination after the swing.
        /// </summary>
        public Vector3 GetBeyondLocation()
        {
            Vector3 offsetMetres = Cm.Dir(BeyondOffset) * Cm.ToMetres;
            return transform.position + _closedRotation * offsetMetres;
        }

        private void Update()
        {
            if (!_swinging)
            {
                return;
            }

            float safeDuration = Mathf.Max(0.0001f, float.IsNaN(swingDuration) ? 0f : swingDuration);
            _swingAlpha = Mathf.Min(1f, _swingAlpha + Time.deltaTime / safeDuration);
            transform.rotation = Quaternion.Slerp(_closedRotation, _openRotation, _swingAlpha);

            if (_swingAlpha >= 1f)
            {
                _swinging = false;
            }
        }
    }
}
