using System;
using Triangle.Player;
using Triangle.World;
using UnityEngine;

namespace Triangle.Core
{
    /// <summary>
    /// Owns the active procedural environment for the current dream slice.
    ///
    /// The bedroom and station are built at runtime rather than loaded as separate
    /// scenes. Keeping that ownership in one place gives the door, saves, director
    /// and debug tooling a single authoritative spot to request transitions.
    ///
    /// Port of UTriangleGameFlowSubsystem. The UE WorldSubsystem becomes a plain
    /// class hosted by the bootstrap; its world timers become scheduler callbacks.
    /// </summary>
    public class TriangleGameFlow
    {
        private const float TransitionSeconds = 0.45f;
        private const float TransitionInputUnlockSeconds = 1.0f;

        private const string IdChildhoodBedroom = "ChildhoodBedroom";
        private const string IdUndergroundStation = "UndergroundStation";
        private const string IdYardOutside = "YardOutside";

        // Scheduler keys. Stable keys mean re-requesting a transition replaces the
        // pending callback rather than stacking a second one.
        private const string KeyFinishTransition = "TriangleGameFlow.FinishTransition";
        private const string KeyUnlockInput = "TriangleGameFlow.UnlockInput";

        private readonly MonoBehaviour _host;

        private GameObject _activeBuilder;
        private string _currentEnvironmentId = IdChildhoodBedroom;
        private string _pendingEnvironmentId = IdChildhoodBedroom;
        private bool _transitioning;
        private bool _lastBedroomDoorOpen;

        public bool IsTransitioning => _transitioning;

        public bool HasActiveEnvironment => _activeBuilder != null;

        /// <summary>
        /// The live builder GameObject for the current environment, or null.
        ///
        /// Consumers that need the environment's generated start should ask here rather
        /// than scanning the scene for a builder. Scene scans can observe a builder that
        /// the previous frame's Object.Destroy has not yet collected, and the room
        /// builder was always checked first, so a bedroom start could be handed back
        /// while the player was standing in the station.
        /// </summary>
        public GameObject ActiveEnvironment => _activeBuilder;

        public string CurrentEnvironmentId => _currentEnvironmentId;

        public bool LastBedroomDoorOpen => _lastBedroomDoorOpen;

        public event Action<TriangleDreamEnvironment> OnEnvironmentTransitionStarted;
        public event Action<TriangleDreamEnvironment> OnEnvironmentReady;
        public event Action<TriangleDreamEnvironment> OnEnvironmentTransitionFailed;

        public TriangleGameFlow(MonoBehaviour host)
        {
            _host = host;
        }

        public static string IdFor(TriangleDreamEnvironment environment)
        {
            switch (environment)
            {
                case TriangleDreamEnvironment.UndergroundStation: return IdUndergroundStation;
                case TriangleDreamEnvironment.YardOutside: return IdYardOutside;
                default: return IdChildhoodBedroom;
            }
        }

        public static TriangleDreamEnvironment EnvironmentForId(string id)
        {
            switch (id)
            {
                case IdUndergroundStation: return TriangleDreamEnvironment.UndergroundStation;
                case IdYardOutside: return TriangleDreamEnvironment.YardOutside;
                default: return TriangleDreamEnvironment.ChildhoodBedroom;
            }
        }

        public static bool IsKnownEnvironmentId(string id)
        {
            return id == IdChildhoodBedroom || id == IdUndergroundStation || id == IdYardOutside;
        }

        /// <summary>Ensures the requested environment exists, swapping immediately.</summary>
        public bool EnsureEnvironment(TriangleDreamEnvironment environment)
        {
            if (_transitioning)
            {
                return false;
            }

            if (_currentEnvironmentId == IdFor(environment) && _activeBuilder != null)
            {
                return true;
            }

            return SwitchEnvironmentImmediate(environment);
        }

        /// <summary>Starts a fade-and-swap transition. False while another is active.</summary>
        public bool TransitionTo(TriangleDreamEnvironment environment)
        {
            if (_transitioning)
            {
                return false;
            }

            if (_currentEnvironmentId == IdFor(environment) && _activeBuilder != null)
            {
                return true;
            }

            if (_host == null)
            {
                return false;
            }

            _pendingEnvironmentId = IdFor(environment);
            TriangleScheduler scheduler = TriangleRuntime.Scheduler;
            if (scheduler == null)
            {
                TriangleLog.Error("No scheduler is registered; the environment cannot transition.");
                return false;
            }

            scheduler.Cancel(KeyUnlockInput);
            _transitioning = true;
            SetTransitionInputLocked(true);
            TriangleRuntime.Hud?.StartDreamTransition(0.20f, 0.50f, 0.30f, Color.black);
            OnEnvironmentTransitionStarted?.Invoke(environment);

            scheduler.Schedule(KeyFinishTransition, TransitionSeconds, FinishTimedTransition);
            scheduler.Schedule(KeyUnlockInput, TransitionInputUnlockSeconds, UnlockTransitionInput);
            return true;
        }

        /// <summary>Restores an environment id from a save. Unknown ids are rejected.</summary>
        public bool RestoreEnvironmentId(string environmentId)
        {
            if (!IsKnownEnvironmentId(environmentId))
            {
                TriangleLog.Warn($"Refusing unknown dream environment id '{environmentId}'.");
                return false;
            }

            return TransitionTo(EnvironmentForId(environmentId));
        }

        private bool SwitchEnvironmentImmediate(TriangleDreamEnvironment environment)
        {
            if (_host == null)
            {
                return false;
            }

            string targetId = IdFor(environment);

            // The caller is responsible for the "already there" case; this method always
            // tears down and rebuilds. It used to carry an empty if-branch to skip the
            // rebuild, which meant a same-environment request arriving through
            // FinishTimedTransition silently reused the old builder while still running
            // the "environment ready" path - so a redundant transition looked like it
            // had rebuilt the world when it had not.
            GameObject previousBuilder = _activeBuilder;
            if (previousBuilder != null)
            {
                if (_currentEnvironmentId == IdChildhoodBedroom)
                {
                    TriangleDoor door = previousBuilder.GetComponentInChildren<TriangleDoor>();
                    if (door != null)
                    {
                        _lastBedroomDoorOpen = door.IsOpen;
                    }
                }

                DetachDoorHandlers(previousBuilder);

                // Tell the outgoing environment to give up the global render settings
                // before it is torn down. Object.Destroy is deferred to the end of the
                // frame, so without this the incoming builder's Awake would apply its own
                // atmosphere and then the outgoing one's OnDestroy would run afterwards
                // and put the old room's ambient and fog back.
                TriangleEnvironmentAtmosphere outgoingAtmosphere =
                    previousBuilder.GetComponentInChildren<TriangleEnvironmentAtmosphere>(true);
                outgoingAtmosphere?.MarkReplaced();

                UnityEngine.Object.Destroy(previousBuilder);
                _activeBuilder = null;
            }

            _activeBuilder = BuildEnvironment(environment);

            if (_activeBuilder == null)
            {
                TriangleLog.Error($"Could not build dream environment '{targetId}'.");
                return false;
            }

            _currentEnvironmentId = targetId;

            // Builders create their own tagged DreamStart in Awake. Move the pawn only
            // after the new environment has finished building so startup and loaded
            // transitions use exactly the same path.
            Transform destination = FindDreamStart(_activeBuilder);
            TrianglePlayer player = TriangleRuntime.Player;
            if (player != null)
            {
                player.PlaceAt(destination);
            }

            TriangleRuntime.Director?.HandleEnvironmentChanged();

            if (_currentEnvironmentId == IdChildhoodBedroom)
            {
                TriangleDoor bedroomDoor = _activeBuilder.GetComponentInChildren<TriangleDoor>();
                if (bedroomDoor != null)
                {
                    _lastBedroomDoorOpen = bedroomDoor.IsOpen;
                    bedroomDoor.OnTransitionRequested += HandleDoorTransitionRequested;
                }
            }

            OnEnvironmentReady?.Invoke(environment);
            TriangleLog.Log($"Dream environment ready: {CurrentEnvironmentId}.");
            return true;
        }

        /// <summary>
        /// Unhooks the flow from a builder's door.
        ///
        /// The door is destroyed with its environment, so the stale subscription died
        /// with it. Unsubscribing explicitly matters because a destroyed component
        /// still keeps the flow alive as a delegate target, and the same door can be
        /// re-found while its GameObject is pending destruction.
        /// </summary>
        private void DetachDoorHandlers(GameObject builder)
        {
            if (builder == null)
            {
                return;
            }

            TriangleDoor door = builder.GetComponentInChildren<TriangleDoor>();
            if (door != null)
            {
                door.OnTransitionRequested -= HandleDoorTransitionRequested;
            }
        }

        private GameObject BuildEnvironment(TriangleDreamEnvironment environment)
        {
            GameObject host;
            switch (environment)
            {
                case TriangleDreamEnvironment.UndergroundStation:
                    host = new GameObject("TriangleStationBuilder");
                    break;
                case TriangleDreamEnvironment.YardOutside:
                    host = new GameObject("TriangleYardBuilder");
                    break;
                default:
                    host = new GameObject("TriangleRoomBuilder");
                    break;
            }

            host.transform.SetParent(_host.transform, false);

            if (environment == TriangleDreamEnvironment.UndergroundStation)
            {
                host.AddComponent<TriangleStationBuilder>();
            }
            else if (environment == TriangleDreamEnvironment.YardOutside)
            {
                host.AddComponent<TriangleYardBuilder>();
            }
            else
            {
                host.AddComponent<TriangleRoomBuilder>();
            }

            return host;
        }

        private static Transform FindDreamStart(GameObject builder)
        {
            TriangleRoomBuilder room = builder.GetComponent<TriangleRoomBuilder>();
            if (room != null && room.DreamStart != null)
            {
                return room.DreamStart;
            }

            TriangleStationBuilder station = builder.GetComponent<TriangleStationBuilder>();
            if (station != null && station.DreamStart != null)
            {
                return station.DreamStart;
            }

            TriangleYardBuilder yard = builder.GetComponent<TriangleYardBuilder>();
            if (yard != null && yard.DreamStart != null)
            {
                return yard.DreamStart;
            }

            // Fall back to any tagged start the builder may have produced.
            Transform[] all = builder.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].CompareTag("DreamStart"))
                {
                    return all[i];
                }
            }

            return null;
        }

        private void FinishTimedTransition()
        {
            TriangleDreamEnvironment environmentToBuild = EnvironmentForId(_pendingEnvironmentId);
            if (SwitchEnvironmentImmediate(environmentToBuild))
            {
                return;
            }

            OnEnvironmentTransitionFailed?.Invoke(environmentToBuild);
            TriangleRuntime.Hud?.StopDreamTransition();
            UnlockTransitionInput();
        }

        private void SetTransitionInputLocked(bool locked)
        {
            TrianglePlayer player = TriangleRuntime.Player;
            if (player != null)
            {
                player.InputLocked = locked;
            }
        }

        private void UnlockTransitionInput()
        {
            TriangleRuntime.Scheduler?.Cancel(KeyUnlockInput);
            SetTransitionInputLocked(false);
            _transitioning = false;
        }

        private void HandleDoorTransitionRequested(TriangleDoor door)
        {
            if (door == null || _transitioning)
            {
                return;
            }

            _lastBedroomDoorOpen = door.IsOpen;
            TransitionTo(TriangleDreamEnvironment.UndergroundStation);
        }

        public void SetLastBedroomDoorOpen(bool open)
        {
            _lastBedroomDoorOpen = open;
        }

        public void Shutdown()
        {
            TriangleScheduler scheduler = TriangleRuntime.Scheduler;
            if (scheduler != null)
            {
                scheduler.Cancel(KeyFinishTransition);
                scheduler.Cancel(KeyUnlockInput);
            }

            DetachDoorHandlers(_activeBuilder);

            SetTransitionInputLocked(false);
            _transitioning = false;
            _activeBuilder = null;
        }
    }
}
