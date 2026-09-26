using System;
using System.Collections.Generic;
using Triangle.Audio;
using Triangle.Core;
using Triangle.Lucidity;
using Triangle.Nightmare;
using Triangle.Player;
using Triangle.World;
using UnityEngine;

// Both System and UnityEngine are imported, so pin the RNG the slice actually uses.
using Random = UnityEngine.Random;

namespace Triangle.Dream
{
    /// <summary>
    /// The Dream Director. Controls The Triangle's presence in the dream and, after
    /// the design update, the Recognition-led loop that lets the player sense it
    /// before they see it.
    ///
    /// The Director owns a 0..1 presence ramp: presence rises while a manifestation
    /// is active and decays when nothing is present. The hum follows presence;
    /// anomalies (gaps, corners pushed apart, distant points) are its visible bodies;
    /// the recurring motifs (twelve, the train) arrive on their own schedule; and a
    /// completed false wake asks the Director to withdraw everything and start over.
    ///
    /// Port of ATriangleDreamDirector.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleDreamDirector : MonoBehaviour
    {
        // --- Tunables (values carried over from the Unreal director) -------------

        [Header("Watching the corner")]
        [Range(0.5f, 1f)] [SerializeField] private float watchFovDotThreshold = 0.90f;
        [SerializeField] private float watchRange = 15f;              // was 1500 cm
        [Range(0.5f, 30f)] [SerializeField] private float manifestInterval = 5.5f;
        [Range(0f, 20f)] [SerializeField] private float watchLucidityPerSecond = 1.5f;

        [Header("Presence")]
        [Range(0.05f, 2f)] [SerializeField] private float presenceRampRate = 0.35f;
        [Range(0.01f, 1f)] [SerializeField] private float presenceDecayRate = 0.12f;
        [SerializeField] private float humFadeSeconds = 0.8f;
        [Range(3f, 60f)] [SerializeField] private float masterSenseInterval = 9f;

        private const int MaxManifestations = 6;

        private readonly List<TriangleManifestationDefinition> _pool = new List<TriangleManifestationDefinition>();
        private readonly Dictionary<string, float> _manifestCooldowns = new Dictionary<string, float>();

        // --- Events visible/extensible to other systems -------------------------

        public event Action<float> OnPresenceStart;
        public event Action<float> OnPresenceIncrease;
        public event Action OnPresenceDisappear;
        public event Action<string> OnTriangleManifestation;
        public event Action OnTriangleRecognition;
        public event Action OnTriangleHumStart;
        public event Action OnTriangleHumStop;
        public event Action OnDreamBleed;
        public event Action<string> OnFalseAwakening;

        // --- State ---------------------------------------------------------------

        private TrianglePoint _cornerPoint;
        private Vector3 _cornerLocation;
        private TrianglePlayer _playerActor;
        private TriangleLucidity _playerLucidity;
        private TriangleRecognition _playerRecognition;

        private float _timeWatching;
        private float _timeSinceLastManifest;
        private float _timeSinceMasterSense;
        private float _trianglePresence;
        private float _lastAnnouncedPresence;
        private float _currentHumTarget;
        private float _transientHumTarget;
        private float _transientHumRemaining;
        private int _activeManifestationSerial;
        private bool _humActive;
        private bool _presenceActive;
        private bool _maxReachedHandled;
        private bool _boundEvents;
        private bool _manifestationActive;
        private bool _humEnabledForCurrentManifestation;
        private bool _maximumPointReapplied;
        private bool _schedulingStarted;

        private TriangleAnomalyActor _activeAnomaly;
        private TriangleManifestationDefinition _activeDefinition;
        private bool _currentAnomalyWasWitnessed;
        private bool _currentPointWasWitnessed;

        private bool _initialised;

        /// <summary>Current Triangle presence (0..1).</summary>
        public float TrianglePresence => _trianglePresence;

        public TriangleLucidityLevel CurrentLucidityLevel =>
            _playerLucidity != null ? _playerLucidity.GetLucidityLevel() : TriangleLucidityLevel.Low;

        public TriangleRecognitionLevel CurrentRecognitionLevel =>
            _playerRecognition != null ? _playerRecognition.GetRecognitionLevel() : TriangleRecognitionLevel.Unknowing;

        private void Awake()
        {
            BuildDefaultPool();
        }

        /// <summary>Called by the bootstrap once, after the world is ready.</summary>
        public void InitializeDream()
        {
            _timeSinceLastManifest = manifestInterval; // give the player a quiet first beat
            _initialised = true;
        }

        private void Update()
        {
            if (!_initialised)
            {
                return;
            }

            float deltaSeconds = Time.deltaTime;

            if (!AcquireReferences())
            {
                return;
            }

            _timeSinceLastManifest += deltaSeconds;
            _timeSinceMasterSense += deltaSeconds;
            TickCooldowns(deltaSeconds);

            if (_manifestationActive && _activeDefinition != null)
            {
                // Record witnessing *while the body is alive*. The point schedules its
                // own hide for the same instant the manifestation resolves, so checking
                // visibility at resolution time would always report "not seen" - the same
                // reason the anomaly path has always recorded this per frame.
                if (IsWitnessedNow(_activeDefinition))
                {
                    if (_activeDefinition.Kind == TriangleManifestationKind.Point)
                    {
                        _currentPointWasWitnessed = true;
                    }
                    else
                    {
                        _currentAnomalyWasWitnessed = true;
                    }
                }
            }

            WatchCorner(deltaSeconds);
            UpdatePresence(deltaSeconds);
            TryMasterSense();

            TriangleMotifSystem motifs = TriangleRuntime.Motifs;
            if (motifs != null)
            {
                motifs.Tick(deltaSeconds);

                if (!_schedulingStarted)
                {
                    // First references collected: the dream's motifs begin their schedule.
                    _schedulingStarted = true;
                    motifs.EnableAutomaticScheduling(true);
                }
            }
        }

        // --- Reference acquisition ------------------------------------------------

        private bool AcquireReferences()
        {
            TrianglePlayer currentPlayer = TriangleRuntime.Player;
            if (_playerActor != currentPlayer)
            {
                if (_playerActor != null || _manifestationActive || _presenceActive || _activeAnomaly != null)
                {
                    // A pending manifestation belongs to the old pawn. Reset it before
                    // rebinding so its timer cannot award recognition to the replacement.
                    WithdrawPresence();
                }

                _playerActor = currentPlayer;
                _playerLucidity = null;
                _playerRecognition = null;
                _boundEvents = false;
                _maxReachedHandled = false;
                _maximumPointReapplied = false;
            }

            if (_playerLucidity == null && _playerActor != null)
            {
                _playerLucidity = _playerActor.Lucidity;
            }

            if (_playerRecognition == null && _playerActor != null)
            {
                _playerRecognition = _playerActor.Recognition;
            }

            if (_playerLucidity == null || _playerRecognition == null)
            {
                _boundEvents = false;
            }

            if (_cornerPoint == null)
            {
                // The point hides itself by deactivating its GameObject, so the search
                // must include inactive objects or the director never finds it.
                _cornerPoint = TrianglePoint.FindInScene();
                _cornerLocation = _cornerPoint != null ? _cornerPoint.transform.position : Vector3.zero;
            }

            if (_playerLucidity != null && _playerRecognition != null && !_boundEvents)
            {
                _playerLucidity.OnLucidityLevelChanged += HandleLucidityLevelChanged;
                _playerRecognition.OnRecognitionLevelChanged += HandleRecognitionLevelChanged;
                _boundEvents = true;
            }

            TriangleMotifSystem motifs = TriangleRuntime.Motifs;
            if (motifs != null && !_motifsHooked)
            {
                motifs.RegisterMotif("ClockStops12", new TriangleMotifConfig(28f, 50f, 50f));
                motifs.RegisterMotif("Platform12Train", new TriangleMotifConfig(45f, 90f, 75f));
                motifs.OnMotif += HandleMotif;
                _motifsHooked = true;
            }

            TriangleFalseAwakeningManager wake = TriangleRuntime.Wake;
            if (wake != null && !_wakeHooked)
            {
                wake.OnFalseAwakening += HandleWakeCycle;
                wake.OnWakeResetRequested += WithdrawPresence;
                _wakeHooked = true;
            }

            bool ready = _playerActor != null && _playerLucidity != null && _playerRecognition != null && _cornerPoint != null;
            if (ready && !_maximumPointReapplied && _playerLucidity != null
                && _playerLucidity.GetLucidityLevel() == TriangleLucidityLevel.Maximum)
            {
                _cornerPoint.Manifest(9999f);
                _manifestationActive = true;
                _humEnabledForCurrentManifestation = true;
                _currentHumTarget = Mathf.Max(_currentHumTarget, 0.65f);
                _maximumPointReapplied = true;
            }

            return ready;
        }

        private bool _motifsHooked;
        private bool _wakeHooked;

        // --- Manifestation pool ---------------------------------------------------

        private void BuildDefaultPool()
        {
            if (_pool.Count > 0)
            {
                return;
            }

            _pool.Add(MakeDefinition("UpperCornerPoint", TriangleManifestationKind.Point, TriangleAnomalyKind.None,
                useHum: true, humPresence: 0.55f, minDuration: 4f, maxDuration: 7f,
                recognitionGain: 6f, minimumRecognition: 0f, weight: 5f, cooldown: 14f));

            _pool.Add(MakeDefinition("TheHumOnly", TriangleManifestationKind.HumOnly, TriangleAnomalyKind.None,
                useHum: true, humPresence: 0.85f, minDuration: 4f, maxDuration: 8f,
                recognitionGain: 4f, minimumRecognition: 75f, weight: 0.7f, cooldown: 20f));

            _pool.Add(MakeDefinition("GapInTheWall", TriangleManifestationKind.TriangularGap, TriangleAnomalyKind.TriangularGap,
                useHum: true, humPresence: 0.6f, minDuration: 5f, maxDuration: 9f,
                recognitionGain: 6f, minimumRecognition: 25f, weight: 1.2f, cooldown: 16f));

            _pool.Add(MakeDefinition("CornerPulled", TriangleManifestationKind.CornerSplit, TriangleAnomalyKind.CornerSplit,
                useHum: true, humPresence: 0.5f, minDuration: 5f, maxDuration: 8f,
                recognitionGain: 6f, minimumRecognition: 50f, weight: 1f, cooldown: 20f));

            _pool.Add(MakeDefinition("DistantPoint", TriangleManifestationKind.DistantBlackPoint, TriangleAnomalyKind.DistantBlackPoint,
                useHum: true, humPresence: 0.45f, minDuration: 5f, maxDuration: 10f,
                recognitionGain: 5f, minimumRecognition: 50f, weight: 0.8f, cooldown: 18f));

            _pool.Add(MakeDefinition("DoorwayCut", TriangleManifestationKind.TriangularDoorway, TriangleAnomalyKind.TriangularDoorway,
                useHum: true, humPresence: 0.75f, minDuration: 5f, maxDuration: 8f,
                recognitionGain: 7f, minimumRecognition: 75f, weight: 0.6f, cooldown: 24f));
        }

        /// <summary>
        /// Slice definitions are built in code rather than as .asset files, matching
        /// the Unreal director's constructor-time default subobjects.
        /// </summary>
        private static TriangleManifestationDefinition MakeDefinition(
            string id, TriangleManifestationKind kind, TriangleAnomalyKind anomalyKind,
            bool useHum, float humPresence, float minDuration, float maxDuration,
            float recognitionGain, float minimumRecognition, float weight, float cooldown)
        {
            var definition = ScriptableObject.CreateInstance<TriangleManifestationDefinition>();
            definition.name = id;
            definition.ManifestId = id;
            definition.Kind = kind;
            definition.AnomalyKind = anomalyKind;
            definition.UseHum = useHum;
            definition.HumPresence = humPresence;
            definition.MinDuration = minDuration;
            definition.MaxDuration = maxDuration;
            definition.RecognitionGainOnSight = recognitionGain;
            definition.MinimumRecognition = minimumRecognition;
            definition.SelectionWeight = weight;
            definition.CooldownSeconds = cooldown;
            definition.ObserverNote = "Default slice manifestation.";
            return definition;
        }

        private void TickCooldowns(float deltaSeconds)
        {
            if (_manifestCooldowns.Count == 0)
            {
                return;
            }

            // Snapshot the keys before writing back: mutating the dictionary during
            // its own enumeration throws.
            _cooldownKeys.Clear();
            foreach (KeyValuePair<string, float> pair in _manifestCooldowns)
            {
                _cooldownKeys.Add(pair.Key);
            }

            for (int i = 0; i < _cooldownKeys.Count; i++)
            {
                string key = _cooldownKeys[i];
                if (!_manifestCooldowns.TryGetValue(key, out float remaining))
                {
                    continue;
                }

                remaining -= deltaSeconds;
                if (remaining <= 0f)
                {
                    _manifestCooldowns.Remove(key);
                }
                else
                {
                    _manifestCooldowns[key] = remaining;
                }
            }

            _cooldownKeys.Clear();
        }

        private readonly List<string> _cooldownKeys = new List<string>();

        // --- Corner watching -------------------------------------------------------

        private void WatchCorner(float deltaSeconds)
        {
            Camera camera = _playerActor != null ? _playerActor.Camera : null;
            if (camera == null)
            {
                return;
            }

            Vector3 cameraLocation = camera.transform.position;
            Vector3 toCorner = _cornerLocation - cameraLocation;
            float distance = toCorner.magnitude;
            float forwardness = Vector3.Dot(camera.transform.forward, toCorner.normalized);

            bool occluded = false;
            if (distance > 0.01f)
            {
                Vector3 traceEnd = cameraLocation + toCorner - toCorner.normalized * Cm.M(12f);
                if (Physics.Raycast(cameraLocation, (traceEnd - cameraLocation).normalized, out RaycastHit hit,
                        Vector3.Distance(cameraLocation, traceEnd), ~0, QueryTriggerInteraction.Ignore))
                {
                    occluded = hit.distance < distance - Cm.M(24f);
                }
            }

            bool watching = !occluded && distance <= watchRange && forwardness >= watchFovDotThreshold;
            if (watching)
            {
                _timeWatching += deltaSeconds;
                _playerLucidity.AddLucidity(watchLucidityPerSecond * deltaSeconds);

                // The corner only yields its point once the gaze is held, at most once
                // per manifest interval.
                if (!_manifestationActive && _timeWatching >= 1.2f && _timeSinceLastManifest >= manifestInterval
                    && !(_cornerPoint != null && _cornerPoint.IsVisible))
                {
                    _timeSinceLastManifest = 0f;
                    _timeWatching = 0f;
                    ExecuteManifestation();
                }
            }
            else
            {
                _timeWatching = Mathf.Max(0f, _timeWatching - deltaSeconds);
            }
        }

        // --- Manifestation ---------------------------------------------------------

        private void ExecuteManifestation(bool senseOnly = false)
        {
            float recognition = _playerRecognition != null ? _playerRecognition.GetRecognition() : 0f;
            var candidates = new List<TriangleManifestationDefinition>();
            var weights = new List<float>();
            float totalWeight = 0f;

            foreach (TriangleManifestationDefinition definition in _pool)
            {
                if (definition == null || recognition < definition.MinimumRecognition)
                {
                    continue;
                }

                if (senseOnly && definition.Kind != TriangleManifestationKind.HumOnly)
                {
                    continue;
                }

                if (!senseOnly && definition.Kind == TriangleManifestationKind.HumanForm)
                {
                    continue; // reserved for later chapters
                }

                if (_manifestCooldowns.TryGetValue(definition.ManifestId, out float remaining) && remaining > 0f)
                {
                    continue;
                }

                float weight = float.IsNaN(definition.SelectionWeight) || float.IsInfinity(definition.SelectionWeight)
                    ? 0f
                    : Mathf.Max(0f, definition.SelectionWeight);
                candidates.Add(definition);
                weights.Add(weight);
                totalWeight += weight;
            }

            if (candidates.Count == 0 || totalWeight <= 0.00001f)
            {
                return;
            }

            float pick = Random.Range(0f, totalWeight);
            int selectedIndex = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                pick -= weights[i];
                if (pick <= 0f)
                {
                    selectedIndex = i;
                    break;
                }
            }

            if (selectedIndex < 0)
            {
                selectedIndex = candidates.Count - 1;
            }

            TriangleManifestationDefinition chosen = candidates[selectedIndex];
            float duration = Mathf.Max(2.5f, chosen.GetRandomDuration());
            CancelInvoke(nameof(ResolveManifestation));
            ++_activeManifestationSerial;
            int manifestationSerial = _activeManifestationSerial;

            _activeAnomaly = null;
            _activeDefinition = chosen;
            _currentAnomalyWasWitnessed = false;

            _currentPointWasWitnessed = false;
            _manifestationActive = true;

            bool started = false;
            if (chosen.Kind == TriangleManifestationKind.Point && _cornerPoint != null)
            {
                _cornerPoint.Manifest(duration);
                started = true;
                TriangleLog.Dream($"Manifest ({chosen.ManifestId}) as corner point for {duration:0.0}s.");
            }
            else if (chosen.Kind == TriangleManifestationKind.HumOnly)
            {
                started = true;
                TriangleLog.Dream($"Manifest ({chosen.ManifestId}): hum only, nothing to see. {duration:0.0}s.");
            }
            else if (TriangleRuntime.Anomalies != null)
            {
                Vector3 around = _playerActor != null ? _playerActor.transform.position : _cornerLocation;
                _activeAnomaly = TriangleRuntime.Anomalies.Spawn(chosen.AnomalyKind, duration, around);
                started = _activeAnomaly != null;
                if (started)
                {
                    OnDreamBleed?.Invoke();
                    TriangleLog.Dream($"Manifest ({chosen.ManifestId}) cut into the dream for {duration:0.0}s.");
                }
            }

            if (!started)
            {
                _manifestationActive = false;
                _humEnabledForCurrentManifestation = false;
                _activeAnomaly = null;
                _activeDefinition = null;
                return;
            }

            float safeCooldown = float.IsNaN(chosen.CooldownSeconds) || float.IsInfinity(chosen.CooldownSeconds)
                ? 14f
                : Mathf.Max(0.5f, chosen.CooldownSeconds);
            _manifestCooldowns[chosen.ManifestId] = safeCooldown;

            bool humWasActive = _humActive;
            _humEnabledForCurrentManifestation = chosen.UseHum;
            if (!_humEnabledForCurrentManifestation && humWasActive)
            {
                _humActive = false;
                OnTriangleHumStop?.Invoke();
            }

            _currentHumTarget = _humEnabledForCurrentManifestation
                ? (float.IsNaN(chosen.HumPresence) || float.IsInfinity(chosen.HumPresence) ? 0f : Mathf.Clamp01(chosen.HumPresence))
                : 0f;

            OnTriangleManifestation?.Invoke(chosen.ManifestId);

            // Resolving the manifestation is deferred through the same serial-guarded
            // callback the Unreal director used, so a newer manifestation always wins.
            _pendingDefinition = chosen;
            _pendingDuration = duration;
            _pendingSerial = manifestationSerial;
            Invoke(nameof(ResolveManifestation), duration);
        }

        private TriangleManifestationDefinition _pendingDefinition;
        private float _pendingDuration;
        private int _pendingSerial;

        private void ResolveManifestation()
        {
            TriangleManifestationDefinition definition = _pendingDefinition;
            if (definition == null || _activeManifestationSerial != _pendingSerial)
            {
                return;
            }

            if (_playerRecognition != null)
            {
                float configuredGain = definition.RecognitionGainOnSight > 0f
                    ? definition.RecognitionGainOnSight
                    : _playerRecognition.BaseGainPerSighting;
                float gain = float.IsNaN(configuredGain) || float.IsInfinity(configuredGain) ? 0f : Mathf.Max(0f, configuredGain);

                float gainToApply = 0f;
                if (definition.Kind == TriangleManifestationKind.HumOnly)
                {
                    // An unexplained hum teaches only a fragment until the player can
                    // reliably attribute it to the Triangle.
                    gainToApply = _playerRecognition.CanSensePresenceBeforeVisual() ? gain : gain * 0.2f;
                }
                else if (WasManifestationWitnessed(definition))
                {
                    gainToApply = gain;
                }

                if (gainToApply > 0f)
                {
                    _playerRecognition.AddRecognition(gainToApply, definition.ManifestId);
                }
            }

            _manifestationActive = false;
            _humEnabledForCurrentManifestation = false;
            _activeAnomaly = null;
            _activeDefinition = null;
            _pendingDefinition = null;
        }

        /// <summary>
        /// True when the player could see this body right now. Used per frame to
        /// record witnessing; use <see cref="WasManifestationWitnessed"/> at resolution.
        /// </summary>
        private bool IsWitnessedNow(TriangleManifestationDefinition definition)
        {
            if (definition == null || _playerActor == null)
            {
                return false;
            }

            Camera camera = _playerActor.Camera;
            if (camera == null)
            {
                return false;
            }

            Vector3 target;
            if (definition.Kind == TriangleManifestationKind.Point)
            {
                if (_cornerPoint == null || !_cornerPoint.IsVisible)
                {
                    return false;
                }

                target = _cornerPoint.transform.position;
            }
            else if (_activeAnomaly != null)
            {
                target = _activeAnomaly.transform.position;
            }
            else
            {
                return false;
            }

            Vector3 cameraLocation = camera.transform.position;
            Vector3 toTarget = target - cameraLocation;
            float distance = toTarget.magnitude;
            if (distance <= 0.01f || distance > 40f)
            {
                return false;
            }

            if (Vector3.Dot(camera.transform.forward, toTarget / distance) < 0.35f)
            {
                return false;
            }

            Vector3 traceEnd = target - toTarget.normalized * Cm.M(8f);
            Vector3 traceDirection = (traceEnd - cameraLocation).normalized;
            if (Physics.Raycast(cameraLocation, traceDirection, out RaycastHit hit,
                    Vector3.Distance(cameraLocation, traceEnd), ~0, QueryTriggerInteraction.Ignore))
            {
                // A hit on the player itself is not an occluder.
                bool hitPlayer = hit.collider != null && hit.collider.transform.IsChildOf(_playerActor.transform);
                if (!hitPlayer && hit.distance < distance - Cm.M(16f))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// True when the current manifestation was actually witnessed by the player.
        ///
        /// Resolved from the flags recorded per frame while the body was alive, so a
        /// player who looked at the point and then looked away still gets credit, and a
        /// body that hid before the resolution instant is not unfairly rejected.
        /// </summary>
        private bool WasManifestationWitnessed(TriangleManifestationDefinition definition)
        {
            if (definition == null)
            {
                return false;
            }

            if (definition.Kind == TriangleManifestationKind.HumOnly)
            {
                return _playerRecognition != null && _playerRecognition.CanSensePresenceBeforeVisual();
            }

            if (definition.Kind == TriangleManifestationKind.Point)
            {
                return _currentPointWasWitnessed;
            }

            return _currentAnomalyWasWitnessed;
        }

        // --- Presence and hum --------------------------------------------------------

        private void UpdatePresence(float deltaSeconds)
        {
            bool cornerVisible = _cornerPoint != null && _cornerPoint.IsVisible;
            bool wantsPresence = _manifestationActive || cornerVisible;

            if (wantsPresence)
            {
                _trianglePresence = Mathf.Min(1f, _trianglePresence + presenceRampRate * deltaSeconds);
            }
            else
            {
                _trianglePresence = Mathf.Max(0f, _trianglePresence - presenceDecayRate * deltaSeconds);
            }

            bool hasTransientHum = _transientHumRemaining > 0f;
            bool wantsHum = wantsPresence || hasTransientHum;
            if (wantsHum)
            {
                _currentHumTarget = 0f;
                if (wantsPresence && _humEnabledForCurrentManifestation)
                {
                    _currentHumTarget = Mathf.Max(_currentHumTarget, _trianglePresence * 0.65f);
                }

                if (hasTransientHum)
                {
                    _currentHumTarget = Mathf.Max(_currentHumTarget, _transientHumTarget);
                }
            }
            else
            {
                _currentHumTarget = 0f;
            }

            if (_transientHumRemaining > 0f)
            {
                _transientHumRemaining = Mathf.Max(0f, _transientHumRemaining - deltaSeconds);
                if (_transientHumRemaining <= 0f)
                {
                    _transientHumTarget = 0f;
                }
            }

            TriangleRuntime.Audio?.SetHumPresence(_currentHumTarget, humFadeSeconds);

            // Presence events describe the world intrusion, independently of a brief
            // motif hum that may be audible without a visible manifestation.
            if (!_presenceActive && _trianglePresence >= 0.05f)
            {
                _presenceActive = true;
                _lastAnnouncedPresence = _trianglePresence;
                OnPresenceStart?.Invoke(_trianglePresence);
            }
            else if (_presenceActive)
            {
                if (_trianglePresence >= _lastAnnouncedPresence + 0.15f)
                {
                    _lastAnnouncedPresence = _trianglePresence;
                    OnPresenceIncrease?.Invoke(_trianglePresence);
                }
                else if (_trianglePresence <= 0.02f)
                {
                    _presenceActive = false;
                    OnPresenceDisappear?.Invoke();
                }
            }

            bool shouldHum = wantsHum && _currentHumTarget > 0.01f;
            if (shouldHum && !_humActive)
            {
                _humActive = true;
                OnTriangleHumStart?.Invoke();
            }
            else if (!shouldHum && _humActive)
            {
                _humActive = false;
                OnTriangleHumStop?.Invoke();
            }
        }

        private void RequestTransientHum(float target, float duration)
        {
            _transientHumTarget = float.IsNaN(target) || float.IsInfinity(target) ? 0f : Mathf.Clamp01(target);
            _transientHumRemaining = float.IsNaN(duration) || float.IsInfinity(duration) ? 0f : Mathf.Max(0f, duration);
        }

        private void TryMasterSense()
        {
            if (_playerRecognition == null || !_playerRecognition.HasMasterSense())
            {
                return;
            }

            if (_manifestationActive || _timeSinceMasterSense < masterSenseInterval)
            {
                return;
            }

            // At mastery the player can know The Triangle is here with nothing visible.
            // The hum answers, briefly, and recognition keeps a low-grade presence so
            // the player is never quite alone even between manifestations.
            _timeSinceMasterSense = 0f;
            ExecuteManifestation(true);
        }

        // --- Milestones ---------------------------------------------------------------

        private void OnRecognitionMilestone(TriangleRecognitionLevel newLevel)
        {
            switch (newLevel)
            {
                case TriangleRecognitionLevel.Noticing:
                    PushSubtitle("You have seen that shape before. Not remembered - seen.", 3.5f);
                    break;

                case TriangleRecognitionLevel.Recognizing:
                    PushSubtitle("You know what is here before you look.", 3.5f);
                    TriangleRuntime.Motifs?.RequestMotif("ClockStops12");
                    break;

                case TriangleRecognitionLevel.Anticipating:
                    PushSubtitle("The hum is not in the room. It is listening with you.", 4f);
                    RequestTransientHum(0.35f, 2f);
                    // Do not announce Platform 12 before the station exists. The motif is
                    // requested when the player actually arrives there.
                    if (FindObjectOfType<TriangleTrain>() != null)
                    {
                        TriangleRuntime.Motifs?.RequestMotif("Platform12Train");
                    }

                    break;

                case TriangleRecognitionLevel.Master:
                    PushSubtitle("It is here. It has always been here.", 4f);
                    break;
            }

            OnTriangleRecognition?.Invoke();
        }

        /// <summary>Forces the corner point to manifest for the given duration.</summary>
        public void TriggerTriangleManifest(float duration)
        {
            if (_cornerPoint == null || _pool.Count == 0 || _pool[0] == null)
            {
                return;
            }

            TriangleManifestationDefinition definition = _pool[0];
            float safeDuration = float.IsNaN(duration) || float.IsInfinity(duration) ? 1f : Mathf.Max(0.25f, duration);

            if (_manifestationActive)
            {
                TriangleRuntime.Anomalies?.DespawnAll();
                _cornerPoint.Hide();
            }

            CancelInvoke(nameof(ResolveManifestation));
            ++_activeManifestationSerial;
            int manifestationSerial = _activeManifestationSerial;

            _cornerPoint.Manifest(safeDuration);
            _activeAnomaly = null;
            _activeDefinition = definition;
            _currentAnomalyWasWitnessed = false;

            _currentPointWasWitnessed = false;
            _manifestationActive = true;

            bool humWasActive = _humActive;
            _humEnabledForCurrentManifestation = definition.UseHum;
            if (!_humEnabledForCurrentManifestation && humWasActive)
            {
                _humActive = false;
                OnTriangleHumStop?.Invoke();
            }

            _currentHumTarget = _humEnabledForCurrentManifestation
                ? (float.IsNaN(definition.HumPresence) || float.IsInfinity(definition.HumPresence) ? 0f : Mathf.Clamp01(definition.HumPresence))
                : 0f;

            OnTriangleManifestation?.Invoke(definition.ManifestId);

            float safeCooldown = float.IsNaN(definition.CooldownSeconds) || float.IsInfinity(definition.CooldownSeconds)
                ? 14f
                : Mathf.Max(0.5f, definition.CooldownSeconds);
            _manifestCooldowns[definition.ManifestId] = safeCooldown;

            _pendingDefinition = definition;
            _pendingDuration = safeDuration;
            _pendingSerial = manifestationSerial;
            Invoke(nameof(ResolveManifestation), safeDuration);

            TriangleLog.Dream($"Triangle manifest triggered (lucidity={(_playerLucidity != null ? _playerLucidity.GetLucidity().ToString("0.0") : "-1")}).");
        }

        /// <summary>Withdraws all current presence and manifestations (wake / reset).</summary>
        public void WithdrawPresence()
        {
            ++_activeManifestationSerial;
            CancelInvoke(nameof(ResolveManifestation));
            CancelInvoke(nameof(CloseTrainDoors));

            bool hadPresence = _presenceActive;
            bool hadHum = _humActive;

            _manifestationActive = false;
            _humEnabledForCurrentManifestation = false;
            _presenceActive = false;
            _humActive = false;
            _trianglePresence = 0f;
            _lastAnnouncedPresence = 0f;
            _transientHumTarget = 0f;
            _transientHumRemaining = 0f;
            _currentHumTarget = 0f;
            _manifestCooldowns.Clear();
            _timeWatching = 0f;
            _timeSinceLastManifest = manifestInterval;
            _timeSinceMasterSense = 0f;
            _activeAnomaly = null;
            _activeDefinition = null;
            _pendingDefinition = null;
            _currentAnomalyWasWitnessed = false;

            _currentPointWasWitnessed = false;
            _maximumPointReapplied = false;

            TriangleRuntime.Audio?.SetHumPresence(0f, 0.8f);
            TriangleRuntime.Anomalies?.DespawnAll();

            TriangleTrain train = FindObjectOfType<TriangleTrain>();
            if (train != null)
            {
                train.SetDoorsOpen(false);
            }

            if (_cornerPoint != null)
            {
                _cornerPoint.Hide();
            }

            if (hadHum)
            {
                OnTriangleHumStop?.Invoke();
            }

            if (hadPresence)
            {
                OnPresenceDisappear?.Invoke();
            }

            TriangleLog.Dream("All presence withdrawn.");
        }

        /// <summary>Clears environment-bound references after a room/station transition.</summary>
        public void HandleEnvironmentChanged()
        {
            // Environment changes use the same complete withdrawal path as a wake:
            // anomalies are world-owned, so builder teardown alone cannot remove them.
            WithdrawPresence();
            _cornerPoint = null;
            _cornerLocation = Vector3.zero;
            _maximumPointReapplied = false;

            // If the player reaches the station after already anticipating the motif,
            // deliver the station beat at the point where its target actually exists.
            if (TriangleRuntime.Motifs != null && _playerRecognition != null && _playerRecognition.CanAttemptWake()
                && FindObjectOfType<TriangleTrain>() != null)
            {
                TriangleRuntime.Motifs.RequestMotif("Platform12Train");
            }
        }

        /// <summary>Invalidates active manifestation/audio state before applying a loaded snapshot.</summary>
        public void ResetForSaveLoad()
        {
            WithdrawPresence();
            // Loading is state synchronisation, not a new progression milestone. The
            // next acquire pass will reapply the maximum point if the restored value
            // warrants it, without replaying the original story event.
            _maxReachedHandled = false;
            _maximumPointReapplied = false;
        }

        // --- Handlers ------------------------------------------------------------------

        private void HandleLucidityLevelChanged(TriangleLucidityLevel newLevel)
        {
            switch (newLevel)
            {
                case TriangleLucidityLevel.Medium:
                    PushSubtitle("Something in this room is too familiar.", 3f);
                    break;

                case TriangleLucidityLevel.High:
                    PushSubtitle("You remember this place. You have always been here.", 3.5f);
                    break;

                case TriangleLucidityLevel.Maximum:
                    if (_maxReachedHandled)
                    {
                        break;
                    }

                    if (_cornerPoint == null)
                    {
                        // The pawn can cross an environment boundary while the level
                        // event is in flight. Let the next acquire pass reapply the
                        // maximum point instead of consuming the one-shot flag.
                        _maximumPointReapplied = false;
                        break;
                    }

                    CancelInvoke(nameof(ResolveManifestation));
                    TriangleRuntime.Anomalies?.DespawnAll();
                    _activeAnomaly = null;
                    ++_activeManifestationSerial;
                    _maxReachedHandled = true;
                    _maximumPointReapplied = true;
                    PushSubtitle("The dream knows you are awake.", 4f);
                    // The point stops hiding at full lucidity.
                    _cornerPoint.Manifest(9999f);
                    _manifestationActive = true;
                    _humEnabledForCurrentManifestation = true;
                    _currentHumTarget = Mathf.Max(_currentHumTarget, 0.65f);
                    break;
            }
        }

        private void HandleRecognitionLevelChanged(TriangleRecognitionLevel newLevel)
        {
            OnRecognitionMilestone(newLevel);
        }

        private void HandleMotif(string motifId)
        {
            if (motifId == "ClockStops12")
            {
                PushSubtitle("The clock stops at twelve.", 3.5f);
                if (_playerRecognition != null)
                {
                    _playerRecognition.AddRecognition(_playerRecognition.BaseGainPerMotif, "ClockStops12");
                }
            }
            else if (motifId == "Platform12Train")
            {
                TriangleTrain train = FindObjectOfType<TriangleTrain>();
                if (train == null)
                {
                    // Defer the motif instead of consuming a station beat in the bedroom.
                    TriangleRuntime.Motifs?.QueueAutomaticMotif(motifId);
                    return;
                }

                PushSubtitle("Somewhere far off, a train pulls into platform twelve.", 4f);
                if (_playerRecognition != null)
                {
                    _playerRecognition.AddRecognition(_playerRecognition.BaseGainPerMotif * 1.5f, "Platform12Train");
                }

                // A distant horn condensed into the hum, independent of the presence
                // ramp so the motif remains audible even when nothing is visible.
                RequestTransientHum(0.5f, 4f);
                train.SetDoorsOpen(true);
                CancelInvoke(nameof(CloseTrainDoors));
                Invoke(nameof(CloseTrainDoors), 8f);
            }
        }

        private void CloseTrainDoors()
        {
            TriangleTrain currentTrain = FindObjectOfType<TriangleTrain>();
            if (currentTrain != null)
            {
                currentTrain.SetDoorsOpen(false);
            }
        }

        private void HandleWakeCycle(int layerIndex)
        {
            TriangleLog.Dream($"False wake layer {layerIndex}.");
            OnFalseAwakening?.Invoke($"Wake_{layerIndex}");
        }

        private void PushSubtitle(string text, float duration)
        {
            TriangleRuntime.Hud?.PushSubtitle(text, duration);
        }

        private void OnDestroy()
        {
            if (_playerLucidity != null)
            {
                _playerLucidity.OnLucidityLevelChanged -= HandleLucidityLevelChanged;
            }

            if (_playerRecognition != null)
            {
                _playerRecognition.OnRecognitionLevelChanged -= HandleRecognitionLevelChanged;
            }

            // TriangleRuntime may already have been cleared by the bootstrap's
            // OnDestroy, so these must be null-checked rather than assumed live.
            TriangleMotifSystem motifs = TriangleRuntime.Motifs;
            if (_motifsHooked && motifs != null)
            {
                motifs.OnMotif -= HandleMotif;
            }

            TriangleFalseAwakeningManager wake = TriangleRuntime.Wake;
            if (_wakeHooked && wake != null)
            {
                wake.OnFalseAwakening -= HandleWakeCycle;
                wake.OnWakeResetRequested -= WithdrawPresence;
            }

            for (int i = 0; i < _pool.Count; i++)
            {
                if (_pool[i] != null)
                {
                    Destroy(_pool[i]);
                }
            }

            _pool.Clear();
        }
    }
}
