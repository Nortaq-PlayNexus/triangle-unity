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
        [SerializeField] private float maxPitch = 85f;

        // Look sensitivity, invert-Y and head bob are not inspector fields: they live
        // in TriangleAccessibility so a player can change them at runtime, and having a
        // second copy here would mean two sources of truth for the same setting.

        [Header("Camera")]
        [Tooltip("Vertical bob amplitude in metres at full walk speed. Off unless the player enables it.")]
        [SerializeField] private float headBobHeight = 0.035f;
        [SerializeField] private float headBobFrequency = 8.5f;

        [Tooltip("Downward speed cap, so a long fall cannot tunnel through the floor colliders.")]
        [SerializeField] private float terminalVelocity = 25f;

        [Tooltip("Downward speed held while grounded, so the pawn stays seated on slopes and steps.")]
        [SerializeField] private float groundedStickVelocity = -2f;
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

        // Reused across scans so the 0.08 s focus timer does not allocate a fresh
        // collider array and hash set on every tick.
        private Collider[] _overlapBuffer = new Collider[0];
        private readonly HashSet<TriangleInteractable> _seenBuffer = new HashSet<TriangleInteractable>();

        /// <summary>Signed look angles in degrees. See <see cref="ApplyLook"/>.</summary>
        private float _yaw;

        private float _pitch;

        // Look-modifier state, refreshed from TriangleAccessibility. Not read per frame:
        // these are PlayerPrefs lookups, and unlike the mouse delta above nothing about
        // them changes frame to frame.
        private bool _invertLookY;
        private bool _headBobEnabled;
        private float _smoothedMouseX;
        private float _smoothedMouseY;
        private Vector3 _cameraRestLocalPosition;
        private float _bobPhase;
        private float _bobOffset;

        /// <summary>
        /// Re-reads the look and camera settings.
        ///
        /// Called on spawn and whenever the accessibility settings change, so a change
        /// made in the pause menu takes effect on the next frame without the player
        /// having to restart.
        /// </summary>
        public void ApplyAccessibilitySettings()
        {
            _invertLookY = TriangleAccessibility.InvertLookY;
            _headBobEnabled = TriangleAccessibility.HeadBob;

            if (!_headBobEnabled)
            {
                // Snap the bob back to neutral rather than leaving a residual offset
                // frozen into the camera's local position.
                _bobPhase = 0f;
                _bobOffset = 0f;
                ApplyCameraBob();
            }
        }

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
                // No authored camera exists, so the pawn builds one. It is parented to
                // the pawn, which means the environment teardown that destroys the
                // pawn also reclaims it; there is nothing extra to track.
                var cameraObject = new GameObject("FirstPersonCamera");
                cameraObject.transform.SetParent(transform, false);
                Camera = cameraObject.AddComponent<Camera>();
            }

            EnsureAudioListener(Camera);

            // Remember the rest position so head bob is applied as an offset from it and
            // cannot accumulate drift over a long session.
            _cameraRestLocalPosition = new Vector3(0f, eyesHeight, 0f);
            Camera.transform.localPosition = _cameraRestLocalPosition;
            Camera.nearClipPlane = 0.02f;
            Camera.farClipPlane = 300f;

            _selfColliders = _controller.GetComponentsInChildren<Collider>(true);

            ApplyAccessibilitySettings();
        }

        /// <summary>
        /// Guarantees the pawn's camera is the thing that hears the dream.
        ///
        /// AddComponent&lt;Camera&gt; does not bring an AudioListener with it, and the
        /// playable scene ships no camera of its own - the pawn builds the whole rig at
        /// runtime. So the project had no listener anywhere: every AudioSource in the
        /// game played into a void, and the hum the design calls "a vibration the mind
        /// files as wrong before it consciously hears it" had never once been audible.
        /// Unity does warn about it, once per frame, which is how a batch test run
        /// produced a 21 MB log of the same line.
        ///
        /// Checked rather than assumed, because a scene that later authors its own camera
        /// with a listener would otherwise end up with two. Two listeners is not a
        /// duplicate of the audio - it is a warning and an arbitrary choice of which one
        /// wins.
        /// </summary>
        private static void EnsureAudioListener(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            if (camera.GetComponent<AudioListener>() == null)
            {
                camera.gameObject.AddComponent<AudioListener>();
                TriangleLog.Verbose("Added the pawn's audio listener; the dream had none.");
                return;
            }

            // A listener somewhere else in the scene would compete with this one.
            AudioListener[] listeners = FindObjectsOfType<AudioListener>();
            if (listeners.Length > 1)
            {
                TriangleLog.Warn(
                    $"{listeners.Length} audio listeners are active; the dream's will be ambiguous. " +
                    "Only one listener should exist in a scene.");
            }
        }

        private void OnEnable()
        {
            // Settings can change while the pawn is disabled, and Awake does not re-run.
            if (_controller != null)
            {
                TriangleAccessibility.OnSettingsChanged -= ApplyAccessibilitySettings;
                TriangleAccessibility.OnSettingsChanged += ApplyAccessibilitySettings;
                ApplyAccessibilitySettings();
            }
        }

        private void OnDisable()
        {
            TriangleAccessibility.OnSettingsChanged -= ApplyAccessibilitySettings;
        }

        private void Update()
        {
            float deltaTime = Time.deltaTime;

            if (!InputLocked)
            {
                HandleLook();
                HandleMove();
                HandleActions();
            }

            if (Input.GetKeyDown(KeyCode.Escape) || Input.GetButtonDown("Pause"))
            {
                TogglePause();
            }

            AdvanceInteractionScan(deltaTime);
        }

        private void HandleActions()
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

        private void HandleLook()
        {
            float mouseX = Input.GetAxis("Mouse X");
            float mouseY = Input.GetAxis("Mouse Y");
            float stickX = Input.GetAxis("Right Stick X") * Time.deltaTime;
            float stickY = Input.GetAxis("Right Stick Y") * Time.deltaTime;

            // Sensitivity and smoothing are read per frame rather than cached. The reads
            // are PlayerPrefs lookups, which are cheap but not free, and reading live
            // means a settings change applies on the very next frame with no plumbing to
            // push the new value in and no chance of the cache drifting from the setting.
            float mouseSensitivity = TriangleAccessibility.MouseSensitivity;
            float gamepadSensitivity = TriangleAccessibility.GamepadSensitivity;
            float smoothing = TriangleAccessibility.MouseSmoothing;

            // Exponential smoothing on the raw mouse delta. Aiming by feel needs a little
            // inertia; 0 is the unmodified input, so this stays opt-in rather than
            // changing how the game feels by default.
            if (smoothing > 0f)
            {
                _smoothedMouseX = Mathf.Lerp(_smoothedMouseX, mouseX, 1f - smoothing);
                _smoothedMouseY = Mathf.Lerp(_smoothedMouseY, mouseY, 1f - smoothing);
                mouseX = _smoothedMouseX;
                mouseY = _smoothedMouseY;
            }
            else
            {
                _smoothedMouseX = 0f;
                _smoothedMouseY = 0f;
            }

            _yaw += mouseX * mouseSensitivity + stickX * gamepadSensitivity;

            // Unity's positive X euler pitches the camera *down*, so a mouse pushed
            // forward (positive Mouse Y) must subtract to look up. The invert flag
            // flips that convention for players who fly the stick the other way.
            float pitchDelta = mouseY * mouseSensitivity + stickY * gamepadSensitivity;
            float signedPitch = _invertLookY ? pitchDelta : -pitchDelta;

            _pitch = Mathf.Clamp(_pitch + signedPitch, -maxPitch, maxPitch);

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
            // Fold the 0..360 range Unity reports back into -180..180. Pitch needs it or
            // a downward-looking transform reads as +350 degrees; yaw needs it so the
            // tracked angle cannot drift without bound across a long session.
            _yaw = FoldToSigned(transform.eulerAngles.y);
            _pitch = Mathf.Clamp(FoldToSigned(transform.eulerAngles.x), -maxPitch, maxPitch);
        }

        private static float FoldToSigned(float eulerDegrees)
        {
            float folded = Mathf.Repeat(eulerDegrees, 360f);
            return folded > 180f ? folded - 360f : folded;
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
            // ReadMoveAxis has already clamped the direction to unit length or less.
            Vector3 horizontalInput = ReadMoveAxis();

            // Gravity is integrated every frame regardless of input. The previous
            // version early-returned when nothing was pressed, which meant the pawn
            // stopped falling the instant the player released WASD: a jump made with
            // no movement input never came back down, and isGrounded was never
            // re-evaluated, so the ground check latched stale.
            _verticalVelocity = Mathf.Max(_verticalVelocity + gravity * Time.deltaTime, -terminalVelocity);
            if (_controller.isGrounded && _verticalVelocity < 0f)
            {
                _verticalVelocity = groundedStickVelocity;
            }

            Vector3 motion = horizontalInput * (walkSpeed * Time.deltaTime)
                             + Vector3.up * (_verticalVelocity * Time.deltaTime);
            _controller.Move(motion);

            UpdateHeadBob(horizontalInput);
        }

        /// <summary>
        /// Advances the walk bob.
        ///
        /// Phase is driven by distance travelled rather than by time, so the bob stays
        /// locked to the gait at any speed and does not keep oscillating after the
        /// player stops. Amplitude scales with planar speed for the same reason.
        ///
        /// Off unless the player turns it on, which is the default: a bob that cannot be
        /// disabled is a real accessibility problem, and this game already leans hard on
        /// camera motion for its effect.
        /// </summary>
        private void UpdateHeadBob(Vector3 horizontalInput)
        {
            if (!_headBobEnabled)
            {
                return;
            }

            float planarSpeed = horizontalInput.magnitude * walkSpeed;
            if (planarSpeed > 0.05f && _controller.isGrounded)
            {
                _bobPhase += horizontalInput.magnitude * walkSpeed * Time.deltaTime * headBobFrequency;
            }

            float target = Mathf.Sin(_bobPhase) * headBobHeight * Mathf.Clamp01(planarSpeed / walkSpeed);

            // Smooth toward the target so the bob eases in and out of motion rather than
            // snapping, and so a direction change does not jolt the camera.
            _bobOffset = Mathf.Lerp(_bobOffset, target, Time.deltaTime * 12f);
            ApplyCameraBob();
        }

        private void ApplyCameraBob()
        {
            if (Camera == null)
            {
                return;
            }

            Camera.transform.localPosition = _cameraRestLocalPosition + Vector3.up * _bobOffset;
        }

        /// <summary>
        /// Seats the camera at the pawn's eye height with no bob offset.
        ///
        /// Called after any teleport, because a bob offset frozen into the camera's
        /// local position would otherwise be carried into the new location and read as
        /// a one-frame camera jolt on arrival.
        /// </summary>
        private void ResetCameraOffset()
        {
            _bobPhase = 0f;
            _bobOffset = 0f;
            ApplyCameraBob();
        }

        /// <summary>Camera-relative planar movement input, already normalised if longer than one.</summary>
        private Vector3 ReadMoveAxis()
        {
            float horizontal = Input.GetAxisRaw("Horizontal");
            float vertical = Input.GetAxisRaw("Vertical");

            Vector3 direction = transform.right * horizontal + transform.forward * vertical;
            direction.y = 0f;
            return direction.sqrMagnitude > 1f ? direction.normalized : direction;
        }

        private void HandleJump()
        {
            if (!_controller.isGrounded)
            {
                return;
            }

            // v = sqrt(2 g h). Both terms are inspector-tunable, so a zero or
            // positive gravity reaches Math.Sqrt with a non-positive argument. That
            // yields NaN, which propagates into the position and drops the pawn
            // through the floor with no way back.
            float rise = jumpHeight * -2f * gravity;
            if (rise <= 0f || float.IsNaN(rise) || float.IsInfinity(rise))
            {
                return;
            }

            _verticalVelocity = Mathf.Sqrt(rise);
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

            // The HUD owns the pause state, the timescale and the cursor together.
            TriangleRuntime.Hud?.TogglePause();
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
            // Dropping a destroyed or disabled focus is the validity boundary: a
            // destroyed Unity object compares equal to null here, so the first branch
            // both clears it and avoids dereferencing a missing native object.
            if (FocusedInteractable != null && !FocusedInteractable.IsUsable)
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
            _overlapBuffer = Physics.OverlapSphere(
                cameraLocation, searchRadius, interactionLayers, QueryTriggerInteraction.Collide);

            _seenBuffer.Clear();

            for (int i = 0; i < _overlapBuffer.Length; i++)
            {
                Collider overlap = _overlapBuffer[i];
                if (overlap == null)
                {
                    continue;
                }

                TriangleInteractable candidate = overlap.GetComponentInParent<TriangleInteractable>();
                if (candidate == null || !candidate.IsUsable || !_seenBuffer.Add(candidate))
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
                hud.SetInteractionPrompt(current.InteractionName, hud.InteractionHint);
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
            ResetCameraOffset();

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
