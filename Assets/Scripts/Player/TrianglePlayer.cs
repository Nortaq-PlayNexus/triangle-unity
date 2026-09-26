using System.Collections.Generic;
using Triangle.Core;
using Triangle.Dream;
using Triangle.Interaction;
using Triangle.Lucidity;
using Triangle.Save;
using Triangle.UI;
using UnityEngine;

namespace Triangle.Player
{
    /// <summary>
    /// First-person dream walker.
    ///
    /// Holds the interaction finder (a short forward sphere-overlap that locates
    /// TriangleInteractables), the Lucidity and Recognition stats, and input. All of
    /// it is configured in code so no content assets are required to run the slice.
    ///
    /// Port of ATriangleCharacter. Unreal's Enhanced Input actions become the legacy
    /// Input Manager (the project is configured for activeInputHandler 0), which
    /// covers keyboard, mouse and gamepad from the same bindings the Unreal version
    /// authored.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class TrianglePlayer : MonoBehaviour
    {
        [Header("Capsule (metres)")]
        [SerializeField] private float capsuleRadius = 0.34f; // was Unreal 34 cm
        [SerializeField] private float capsuleHeight = 1.76f; // was Unreal 88 cm half-height
        [SerializeField] private float eyesHeight = 0.73f;    // above the capsule centre

        [Header("Movement")]
        [SerializeField] private float walkSpeed = 3.2f;       // was Unreal 320 cm/s
        [SerializeField] private float gravity = -9.81f;
        [SerializeField] private float jumpHeight = 0.45f;
        [SerializeField] private float mouseSensitivity = 1.4f;
        [SerializeField] private float gamepadSensitivity = 120f;
        [SerializeField] private float maxPitch = 85f;

        [Header("Interaction")]
        [Tooltip("Range used when an interactable does not specify its own (metres).")]
        [SerializeField] private float defaultInteractionRange = Cm.M(240f);

        [Tooltip("Time between interaction scans (seconds).")]
        [SerializeField] private float interactionScanInterval = 0.08f;

        [Tooltip("Layers the interaction scan considers.")]
        [SerializeField] private LayerMask interactionLayers = ~0;

        public Camera Camera { get; private set; }
        public TriangleLucidity Lucidity { get; } = new TriangleLucidity();
        public TriangleRecognition Recognition { get; } = new TriangleRecognition();

        /// <summary>The currently focused interactable (if any).</summary>
        public TriangleInteractable FocusedInteractable { get; private set; }

        /// <summary>Set by the game flow while a fade-and-swap transition is running.</summary>
        public bool InputLocked { get; set; }

        private CharacterController _controller;
        private float _verticalVelocity;
        private float _scanTimer;
        private Collider[] _selfColliders = new Collider[0];

        /// <summary>Signed look angles in degrees. See <see cref="ApplyLook"/>.</summary>
        private float _yaw;

        private float _pitch;

        private void Awake()
        {
            _controller = GetComponent<CharacterController>();
            _controller.radius = capsuleRadius;
            _controller.height = capsuleHeight;
            _controller.center = Vector3.zero;
            _controller.slopeLimit = 50f;
            _controller.stepOffset = 0.35f;
            _controller.skinWidth = 0.03f;

            Camera = GetComponentInChildren<Camera>();
            if (Camera == null)
            {
                var cameraObject = new GameObject("FirstPersonCamera");
                cameraObject.transform.SetParent(transform, false);
                cameraObject.transform.localPosition = new Vector3(0f, eyesHeight, 0f);
                Camera = cameraObject.AddComponent<Camera>();
                Camera.nearClipPlane = 0.02f;
                Camera.farClipPlane = 300f;
                CameraObject = cameraObject;
            }
            else
            {
                Camera.transform.localPosition = new Vector3(0f, eyesHeight, 0f);
                Camera.nearClipPlane = 0.02f;
                Camera.farClipPlane = 300f;
            }

            _selfColliders = _controller.GetComponentsInChildren<Collider>(true);
        }

        private GameObject CameraObject { get; set; }

        private void Update()
        {
            float deltaTime = Time.deltaTime;

            if (!InputLocked)
            {
                HandleLook();
                HandleMove();
            }

            if (!InputLocked)
            {
                if (Input.GetKeyDown(KeyCode.Space) || Input.GetButtonDown("Jump"))
                {
                    HandleJump();
                }

                if (Input.GetKeyDown(KeyCode.F) || Input.GetButtonDown("Interact"))
                {
                    PressInteract();
                }

                if (Input.GetKeyDown(KeyCode.Q) || Input.GetButtonDown("Wake"))
                {
                    TriangleRuntime.Wake?.TryWake();
                }

                if (Input.GetKeyDown(KeyCode.F5) || Input.GetButtonDown("QuickSave"))
                {
                    QuickSave();
                }

                if (Input.GetKeyDown(KeyCode.F9) || Input.GetButtonDown("QuickLoad"))
                {
                    QuickLoad();
                }
            }

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetButtonDown("Pause"))
            {
                TogglePause();
            }

            AdvanceInteractionScan(deltaTime);
        }

        private void HandleLook()
        {
            float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;
            float stickX = Input.GetAxis("Right Stick X") * gamepadSensitivity * Time.deltaTime;
            float stickY = Input.GetAxis("Right Stick Y") * gamepadSensitivity * Time.deltaTime;

            _yaw += mouseX + stickX;

            // Unity's positive X euler pitches the camera *down*, so a mouse pushed
            // forward (positive Mouse Y) must subtract to look up.
            _pitch = Mathf.Clamp(_pitch - (mouseY + stickY), -maxPitch, maxPitch);

            ApplyLook();
        }

        /// <summary>
        /// Writes the tracked look angles onto the transform.
        ///
        /// Yaw and pitch are kept as explicit signed fields rather than read back from
        /// <c>transform.eulerAngles</c>. Unity normalises euler angles into 0..360, so
        /// reading the pitch back after looking up a few degrees yields ~350, and
        /// clamping that to a sane range slams the view to the floor. Holding the angles
        /// ourselves keeps the clamp meaningful at both ends.
        /// </summary>
        private void ApplyLook()
        {
            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }

        /// <summary>Re-derives the tracked look angles from the transform's world rotation.</summary>
        private void SyncLookFromTransform()
        {
            Vector3 euler = transform.eulerAngles;
            _yaw = euler.y;

            // Fold the 0..360 range back into -180..180 so a downward-looking transform
            // does not read as +350 degrees of pitch.
            float pitch = euler.x;
            if (pitch > 180f)
            {
                pitch -= 360f;
            }

            _pitch = Mathf.Clamp(pitch, -maxPitch, maxPitch);
        }

        /// <summary>
        /// Aims the view at a world point.
        ///
        /// Gameplay and tests should aim through this rather than rotating the camera
        /// transform directly: the camera is a child, and the player rewrites its own
        /// rotation every frame from the tracked angles.
        /// </summary>
        public void AimAt(Vector3 worldPoint)
        {
            if (Camera == null)
            {
                return;
            }

            Vector3 origin = Camera.transform.position;
            Vector3 toTarget = worldPoint - origin;
            if (toTarget.sqrMagnitude < 0.000001f)
            {
                return;
            }

            toTarget.Normalize();

            _yaw = Mathf.Atan2(toTarget.x, toTarget.z) * Mathf.Rad2Deg;
            _pitch = -Mathf.Asin(Mathf.Clamp(toTarget.y, -1f, 1f)) * Mathf.Rad2Deg;
            _pitch = Mathf.Clamp(_pitch, -maxPitch, maxPitch);
            ApplyLook();
        }

        private void HandleMove()
        {
            float horizontal = Input.GetAxisRaw("Horizontal");
            float vertical = Input.GetAxisRaw("Vertical");
            if (Mathf.Abs(horizontal) < 0.0001f && Mathf.Abs(vertical) < 0.0001f)
            {
                return;
            }

            Vector3 direction = (transform.right * horizontal + transform.forward * vertical);
            direction.y = 0f;
            if (direction.sqrMagnitude > 1f)
            {
                direction.Normalize();
            }

            _controller.Move(direction * (walkSpeed * Time.deltaTime));

            if (_controller.isGrounded && _verticalVelocity < 0f)
            {
                _verticalVelocity = -2f;
            }

            _verticalVelocity += gravity * Time.deltaTime;
            _controller.Move(Vector3.up * (_verticalVelocity * Time.deltaTime));
        }

        private void HandleJump()
        {
            if (_controller.isGrounded)
            {
                _verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
        }

        private void PressInteract()
        {
            TriangleInteractable interactable = FocusedInteractable;
            if (interactable != null && interactable.IsUsable)
            {
                interactable.Interact(gameObject);
            }
        }

        private void TogglePause()
        {
            if (TriangleRuntime.GameFlow != null && TriangleRuntime.GameFlow.IsTransitioning)
            {
                return;
            }

            bool paused = !TriangleRuntime.Hud.IsPaused;
            Time.timeScale = paused ? 0f : 1f;
            Cursor.lockState = paused ? CursorLockMode.None : CursorLockMode.Locked;
            Cursor.visible = paused;
            TriangleRuntime.Hud?.SetPauseVisible(paused);
        }

        private void QuickSave()
        {
            if (TriangleRuntime.GameFlow != null && TriangleRuntime.GameFlow.IsTransitioning)
            {
                PushSubtitle("The dream is still changing.", 1.5f);
                return;
            }

            TriangleSaveSystem save = TriangleRuntime.Save;
            if (save == null)
            {
                return;
            }

            bool saved = save.SaveNow(null);
            PushSubtitle(
                saved ? "The dream remembers what you saw." : "The dream cannot hold that memory yet.",
                2f);
        }

        private void QuickLoad()
        {
            if (TriangleRuntime.GameFlow != null && TriangleRuntime.GameFlow.IsTransitioning)
            {
                PushSubtitle("Wait for the dream to settle.", 1.5f);
                return;
            }

            TriangleSaveSystem save = TriangleRuntime.Save;
            TriangleSaveData snapshot = save != null ? save.LoadNow() : null;
            if (snapshot == null)
            {
                PushSubtitle("There is nothing to wake into yet.", 2f);
                return;
            }

            // The save system owns environment restoration as well as meters. A
            // different environment is queued until the procedural swap is complete.
            TriangleSaveApplyResult result = save.ApplySnapshot(snapshot);
            if (result == TriangleSaveApplyResult.Queued)
            {
                PushSubtitle("The memory folds the room around you...", 2f);
                return;
            }

            if (result != TriangleSaveApplyResult.Applied)
            {
                PushSubtitle("The dream refuses that memory.", 2f);
                return;
            }

            PushSubtitle("You wake where you left the dream.", 2f);
        }

        private void PushSubtitle(string text, float duration)
        {
            TriangleRuntime.Hud?.PushSubtitle(text, duration);
        }

        // --- Interaction scanning ------------------------------------------------

        private void AdvanceInteractionScan(float deltaTime)
        {
            _scanTimer -= deltaTime;
            if (_scanTimer > 0f)
            {
                return;
            }

            // Clamp so a long frame cannot leave the player without a prompt.
            float safeInterval = Mathf.Clamp(
                float.IsNaN(interactionScanInterval) || float.IsInfinity(interactionScanInterval) ? 0.08f : interactionScanInterval,
                0.05f, 1f);
            _scanTimer = safeInterval;
            ScanForInteractable();
        }

        private void ScanForInteractable()
        {
            // The scan is the validity boundary for focus. This also clears a disabled
            // component before input can reach it.
            if (FocusedInteractable == null)
            {
                FocusedInteractable = null;
            }
            else if (!FocusedInteractable.IsUsable)
            {
                SetFocusedInteractable(null);
            }

            if (Camera == null)
            {
                if (FocusedInteractable != null)
                {
                    SetFocusedInteractable(null);
                }

                return;
            }

            TriangleInteractable best = null;
            float bestScore = float.MaxValue;
            Vector3 cameraLocation = Camera.transform.position;
            Vector3 cameraForward = Camera.transform.forward;

            // Keep the scan broad enough for authored long-range props (the train
            // intentionally has a 5 m interaction range) while the per-component
            // distance test below remains the real gameplay gate.
            float safeDefaultRange = float.IsNaN(defaultInteractionRange) || float.IsInfinity(defaultInteractionRange)
                ? Cm.M(240f)
                : Mathf.Max(Cm.M(40f), defaultInteractionRange);
            float searchRadius = Mathf.Max(safeDefaultRange + Cm.M(60f), Cm.M(700f));

            // The allocating overload is used deliberately: the scan is a 0.08 s timer
            // over a small closed room, and the garbage is collected long before it
            // becomes a frame cost. A pooled variant would need careful invalidation.
            Collider[] overlaps = Physics.OverlapSphere(
                cameraLocation, searchRadius, interactionLayers, QueryTriggerInteraction.Collide);

            HashSet<TriangleInteractable> seen = new HashSet<TriangleInteractable>();

            for (int i = 0; i < overlaps.Length; i++)
            {
                Collider overlap = overlaps[i];
                if (overlap == null)
                {
                    continue;
                }

                TriangleInteractable candidate = overlap.GetComponentInParent<TriangleInteractable>();
                if (candidate == null || !candidate.IsUsable || !seen.Add(candidate))
                {
                    continue;
                }

                float componentRange = candidate.MaxInteractionRange > 0f && !float.IsInfinity(candidate.MaxInteractionRange)
                    ? candidate.MaxInteractionRange
                    : safeDefaultRange;

                Vector3 toTarget = candidate.GetInteractionWorldLocation() - cameraLocation;
                float distance = toTarget.magnitude;
                if (distance > componentRange)
                {
                    continue;
                }

                // Favour targets inside the view cone.
                float forwardness = Vector3.Dot(cameraForward, toTarget / Mathf.Max(distance, 0.0001f));
                if (forwardness < 0.35f)
                {
                    continue;
                }

                // Do not focus through a wall. Stop just before the target so its own
                // collider does not count as an occluder.
                Vector3 traceEnd = cameraLocation + toTarget - (toTarget / Mathf.Max(distance, 0.0001f)) * 0.08f;
                if (Physics.Raycast(cameraLocation, (traceEnd - cameraLocation).normalized, out RaycastHit hit,
                        Vector3.Distance(cameraLocation, traceEnd), ~0, QueryTriggerInteraction.Ignore))
                {
                    bool hitSelf = false;
                    for (int s = 0; s < _selfColliders.Length; s++)
                    {
                        if (_selfColliders[s] != null && _selfColliders[s] == hit.collider)
                        {
                            hitSelf = true;
                            break;
                        }
                    }

                    bool hitTarget = hit.collider != null &&
                                     hit.collider.transform.IsChildOf(candidate.transform);

                    if (!hitSelf && !hitTarget && hit.distance < distance - 0.16f)
                    {
                        continue;
                    }
                }

                // Scoring = closeness weighted by how much we are facing it.
                float forwardBonus = Mathf.Max(0.5f, forwardness);
                float score = distance * (2f - forwardBonus);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best != FocusedInteractable)
            {
                SetFocusedInteractable(best);
            }
        }

        private void SetFocusedInteractable(TriangleInteractable newFocus)
        {
            TriangleInteractable validFocus = newFocus;
            if (validFocus != null && !validFocus.IsUsable)
            {
                validFocus = null;
            }

            TriangleInteractable current = FocusedInteractable;
            if (validFocus == current)
            {
                return;
            }

            if (current != null)
            {
                current.NotifyFocusEnd();
            }

            FocusedInteractable = validFocus;
            if (FocusedInteractable != null)
            {
                FocusedInteractable.NotifyFocusBegin();
            }

            UpdateInteractionPrompt();
        }

        private void UpdateInteractionPrompt()
        {
            TriangleHUD hud = TriangleRuntime.Hud;
            if (hud == null)
            {
                return;
            }

            TriangleInteractable current = FocusedInteractable;
            if (current != null && current.IsUsable)
            {
                hud.SetInteractionPrompt(current.InteractionName, "[F / X]");
            }
            else
            {
                hud.SetInteractionPrompt(string.Empty, string.Empty);
            }
        }

        /// <summary>Snaps the pawn onto an environment's generated start.</summary>
        public void PlaceAt(Transform destination)
        {
            if (destination == null)
            {
                return;
            }

            Teleport(destination.position, destination.rotation);
        }

        /// <summary>
        /// Moves the pawn without collision. The controller is disabled for the move
        /// so a save restore or environment swap cannot be blocked by geometry.
        /// </summary>
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            bool wasEnabled = _controller != null && _controller.enabled;
            if (_controller != null)
            {
                _controller.enabled = false;
            }

            transform.SetPositionAndRotation(position, rotation);
            SyncLookFromTransform();
            _verticalVelocity = 0f;

            if (_controller != null)
            {
                _controller.enabled = wasEnabled;
            }
        }

        private void OnDestroy()
        {
            SetFocusedInteractable(null);
        }
    }
}
