using System;
using Triangle.Core;
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
    /// class hosted by the bootstrap; its world timers become Invoke calls.
    /// </summary>
    public class TriangleGameFlow
    {
        private const float TransitionSeconds = 0.45f;
        private const float TransitionInputUnlockSeconds = 1.0f;

        private const string IdChildhoodBedroom = "ChildhoodBedroom";
        private const string IdUndergroundStation = "UndergroundStation";

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
            return environment == TriangleDreamEnvironment.UndergroundStation ? IdUndergroundStation : IdChildhoodBedroom;
        }

        public static TriangleDreamEnvironment EnvironmentForId(string id)
        {
            return id == IdUndergroundStation ? TriangleDreamEnvironment.UndergroundStation : TriangleDreamEnvironment.ChildhoodBedroom;
        }

        public static bool IsKnownEnvironmentId(string id)
        {
            return id == IdChildhoodBedroom || id == IdUndergroundStation;
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

            GameObject previousBuilder = _activeBuilder;
            string targetId = IdFor(environment);
            bool wantsStation = environment == TriangleDreamEnvironment.UndergroundStation;

            // Tear the old environment down before building the replacement, so the
            // two never overlap. If the build then fails the player is left in an
            // empty room rather than a half-swapped one.
            if (previousBuilder != null && _currentEnvironmentId == targetId)
            {
                // Same environment already built.
            }
            else
            {
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

                    UnityEngine.Object.Destroy(previousBuilder);
                    _activeBuilder = null;
                }

                _activeBuilder = BuildEnvironment(wantsStation);
            }

            if (_activeBuilder == null)
            {
                TriangleLog.Error($"Could not build dream environment '{targetId}'.");
                return false;
            }

            _currentEnvironmentId = targetId;

            // Builders create their own tagged DreamStart in Start. Move the pawn only
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

        private GameObject BuildEnvironment(bool station)
        {
            var host = new GameObject(station ? "TriangleStationBuilder" : "TriangleRoomBuilder");
            host.transform.SetParent(_host.transform, false);
            if (station)
            {
                host.AddComponent<TriangleStationBuilder>();
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

            SetTransitionInputLocked(false);
            _transitioning = false;
        }
    }
}
