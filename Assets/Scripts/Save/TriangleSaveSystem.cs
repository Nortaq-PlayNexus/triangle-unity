using System;
using System.Collections.Generic;
using System.IO;
using Triangle.Core;
using Triangle.Dream;
using Triangle.Player;
using Triangle.World;
using UnityEngine;

namespace Triangle.Save
{
    /// <summary>
    /// Owns save/load for the whole game.
    ///
    /// Writes synchronously to the project's persistent data dir. Slot names are
    /// namespaced so multiple runs and future chapter saves cannot collide.
    ///
    /// Port of UTriangleSaveSubsystem. Exactly one exists per session and it survives
    /// environment transitions, which is required for dream state that must persist
    /// across swaps.
    /// </summary>
    public class TriangleSaveSystem
    {
        public const string SaveSlotName = "Dream_Slot_0";

        private const int MaxSerializedEntries = 256;

        private readonly MonoBehaviour _host;

        private TriangleSaveData _pendingSnapshot;
        private TriangleDreamEnvironment _pendingEnvironment = TriangleDreamEnvironment.ChildhoodBedroom;

        /// <summary>Completion callback for callers; fired on every save attempt.</summary>
        public Action<bool> OnSaveCompleted;

        public TriangleSaveSystem(MonoBehaviour host)
        {
            _host = host;
        }

        private static string SlotPath => Path.Combine(Application.persistentDataPath, SaveSlotName + ".json");

        /// <summary>Clears the default slot so a new dream starts without a blank save file.</summary>
        public void ResetSave()
        {
            DeleteSaveData();
        }

        /// <summary>Explicitly deletes the default slot. Already-clear slots count as success.</summary>
        public bool DeleteSaveData()
        {
            if (!HasSaveData())
            {
                TriangleLog.Save($"Save slot '{SaveSlotName}' is already clear.");
                return true;
            }

            try
            {
                File.Delete(SlotPath);
                TriangleLog.Save($"Deleted save slot '{SaveSlotName}'.");
                return true;
            }
            catch (Exception e)
            {
                TriangleLog.SaveWarn($"Failed to delete save slot '{SaveSlotName}': {e.Message}");
                return false;
            }
        }

        public bool HasSaveData() => File.Exists(SlotPath);

        /// <summary>Loads the saved dream from disk. Null when none exists.</summary>
        public TriangleSaveData LoadNow()
        {
            if (!HasSaveData())
            {
                TriangleLog.SaveWarn($"No save data found in slot '{SaveSlotName}'.");
                return null;
            }

            TriangleSaveData loaded;
            try
            {
                string json = File.ReadAllText(SlotPath);
                loaded = JsonUtility.FromJson<TriangleSaveData>(json);
            }
            catch (Exception e)
            {
                TriangleLog.SaveWarn($"Failed to read save slot '{SaveSlotName}': {e.Message}");
                return null;
            }

            if (loaded == null)
            {
                TriangleLog.SaveWarn($"Save slot '{SaveSlotName}' contained unreadable data.");
                return null;
            }

            // Bound collection sizes before they reach gameplay code. Save files are
            // local, but malformed or hand-edited data should not create unbounded work.
            loaded.RecognizedPatternIds = BoundList(loaded.RecognizedPatternIds);
            loaded.NotebookPages = BoundList(loaded.NotebookPages);
            loaded.DiscoveredMemories = BoundList(loaded.DiscoveredMemories);
            loaded.RecognizedPatternIds.RemoveAll(string.IsNullOrEmpty);

            // Preserve the intent of legacy fields without pretending an exact count or
            // state can be reconstructed when the dedicated value did not exist yet.
            if (loaded.NotebookInteractionCount <= 0 && loaded.NotebookInteracted)
            {
                loaded.NotebookInteractionCount = 1;
            }

            loaded.NotebookInteractionCount = Mathf.Max(0, loaded.NotebookInteractionCount);
            loaded.NotebookInteracted = loaded.NotebookInteractionCount > 0;
            loaded.FalseAwakeningLayer = Mathf.Max(0, loaded.FalseAwakeningLayer);
            loaded.BedroomDoorOpen = loaded.GetEnvironmentFlag("BedroomDoorOpen", loaded.BedroomDoorOpen);

            if (loaded.SchemaVersion > TriangleSaveData.CurrentSchemaVersion)
            {
                TriangleLog.SaveWarn($"Save '{SaveSlotName}' uses newer schema {loaded.SchemaVersion} (current is {TriangleSaveData.CurrentSchemaVersion}); refusing to load it.");
                return null;
            }

            if (loaded.SchemaVersion <= 0)
            {
                if (loaded.RecognizedPatternIds.Count == 0 && loaded.TriangleEncounters > 0)
                {
                    // Legacy saves stored a count but no stable IDs. Preserve the fact
                    // that the catalogue was non-empty with an explicit migration ID.
                    loaded.RecognizedPatternIds.Add("LegacyTrianglePattern");
                }

                loaded.HasPlayerTransformInEnvironment = false;
                loaded.SchemaVersion = TriangleSaveData.CurrentSchemaVersion;
                TriangleLog.Save($"Migrated legacy pre-versioned save from slot '{SaveSlotName}'.");
            }
            else if (loaded.SchemaVersion == 1)
            {
                // Schema 1 stored only an absolute world transform. Keep it usable in
                // the same environment, but force cross-environment loads to the new
                // builder start rather than interpreting it in the wrong world.
                loaded.HasPlayerTransformInEnvironment = false;
                loaded.SchemaVersion = TriangleSaveData.CurrentSchemaVersion;
                TriangleLog.Save($"Migrated schema 1 save from slot '{SaveSlotName}' to environment-relative transform support.");
            }

            TriangleLog.Save(
                $"Loaded save (schema={loaded.SchemaVersion}, lucidity={loaded.Lucidity:0.0}, patterns={loaded.RecognizedPatternIds.Count}, environment={loaded.CurrentEnvironmentId}, wake-layer={loaded.FalseAwakeningLayer}, door-open={loaded.BedroomDoorOpen}).");
            return loaded;
        }

        private static List<string> BoundList(List<string> source)
        {
            if (source == null)
            {
                return new List<string>();
            }

            return source.Count > MaxSerializedEntries ? source.GetRange(0, MaxSerializedEntries) : source;
        }

        /// <summary>Serializes the current dream state and fires OnSaveCompleted.</summary>
        public bool SaveNow(TriangleSaveData inSnapshot)
        {
            TriangleSaveData snapshot = inSnapshot ?? BuildSnapshot();
            if (snapshot == null)
            {
                TriangleLog.SaveWarn("Save aborted: no valid snapshot could be built.");
                OnSaveCompleted?.Invoke(false);
                return false;
            }

            // A pre-versioned object may still be re-saved by native code. Stamp only
            // old/unknown inputs; never downgrade a snapshot written by a newer build.
            if (snapshot.SchemaVersion <= 0)
            {
                snapshot.SchemaVersion = TriangleSaveData.CurrentSchemaVersion;
            }

            bool success;
            try
            {
                string json = JsonUtility.ToJson(snapshot, true);
                File.WriteAllText(SlotPath, json);
                success = true;
            }
            catch (Exception e)
            {
                TriangleLog.SaveWarn($"Save write failed: {e.Message}");
                success = false;
            }

            TriangleLog.Save($"Save {(success ? "succeeded" : "FAILED")} (schema={snapshot.SchemaVersion}, lucidity={snapshot.Lucidity:0.0}).");
            OnSaveCompleted?.Invoke(success);
            return success;
        }

        /// <summary>
        /// Applies player-owned snapshot state without replaying progression. A
        /// different procedural environment is queued until its fade/swap completes.
        /// </summary>
        public TriangleSaveApplyResult ApplySnapshot(TriangleSaveData snapshot)
        {
            if (snapshot == null)
            {
                TriangleLog.SaveWarn("Cannot apply an invalid save snapshot.");
                return TriangleSaveApplyResult.Failed;
            }

            TriangleGameFlow flow = TriangleRuntime.GameFlow;
            if (flow != null && !Name.IsNone(snapshot.CurrentEnvironmentId))
            {
                bool knownEnvironment = TriangleGameFlow.IsKnownEnvironmentId(snapshot.CurrentEnvironmentId);
                if (!knownEnvironment)
                {
                    if (flow.HasActiveEnvironment)
                    {
                        TriangleLog.SaveWarn($"Save contains unknown environment '{snapshot.CurrentEnvironmentId}' for a procedural world.");
                        return TriangleSaveApplyResult.Failed;
                    }
                }
                else if (snapshot.CurrentEnvironmentId != flow.CurrentEnvironmentId)
                {
                    if (flow.IsTransitioning)
                    {
                        TriangleLog.SaveWarn("Cannot load a snapshot while an environment transition is active.");
                        return TriangleSaveApplyResult.Failed;
                    }

                    _pendingSnapshot = snapshot;
                    _pendingEnvironment = TriangleGameFlow.EnvironmentForId(snapshot.CurrentEnvironmentId);

                    // The flow fires OnEnvironmentReady once the fade-and-swap lands;
                    // subscribing is the whole mechanism, so no polling is needed.
                    flow.OnEnvironmentReady -= HandleEnvironmentReady;
                    flow.OnEnvironmentTransitionFailed -= HandleEnvironmentTransitionFailed;
                    flow.OnEnvironmentReady += HandleEnvironmentReady;
                    flow.OnEnvironmentTransitionFailed += HandleEnvironmentTransitionFailed;

                    if (!flow.RestoreEnvironmentId(snapshot.CurrentEnvironmentId))
                    {
                        flow.OnEnvironmentReady -= HandleEnvironmentReady;
                        flow.OnEnvironmentTransitionFailed -= HandleEnvironmentTransitionFailed;
                        _pendingSnapshot = null;
                        return TriangleSaveApplyResult.Failed;
                    }

                    TriangleLog.Save($"Queued snapshot for environment '{snapshot.CurrentEnvironmentId}'.");
                    return TriangleSaveApplyResult.Queued;
                }
            }

            return ApplyPlayerState(snapshot) ? TriangleSaveApplyResult.Applied : TriangleSaveApplyResult.Failed;
        }

        private void HandleEnvironmentReady(TriangleDreamEnvironment environment)
        {
            TriangleSaveData snapshot = _pendingSnapshot;
            _pendingSnapshot = null;
            DetachFlowHandlers();

            if (snapshot != null && environment == _pendingEnvironment)
            {
                bool applied = ApplyPlayerState(snapshot);
                TriangleLog.Save($"Post-transition snapshot application {(applied ? "succeeded" : "failed")}.");
            }
            else if (snapshot != null)
            {
                TriangleLog.SaveWarn("Discarded queued snapshot because the ready environment did not match the requested environment.");
            }
        }

        private void HandleEnvironmentTransitionFailed(TriangleDreamEnvironment environment)
        {
            _pendingSnapshot = null;
            DetachFlowHandlers();
            TriangleLog.SaveWarn("Queued snapshot discarded because environment transition failed.");
            TriangleRuntime.Hud?.PushSubtitle("The room will not open for that memory.", 2.5f);
        }

        private void DetachFlowHandlers()
        {
            TriangleGameFlow flow = TriangleRuntime.GameFlow;
            if (flow != null)
            {
                flow.OnEnvironmentReady -= HandleEnvironmentReady;
                flow.OnEnvironmentTransitionFailed -= HandleEnvironmentTransitionFailed;
            }
        }

        private bool ApplyPlayerState(TriangleSaveData snapshot)
        {
            bool appliedState = false;
            TriangleRuntime.Director?.ResetForSaveLoad();

            TrianglePlayer player = TriangleRuntime.Player;
            bool canApplyTransform = true;

            Vector3 desiredPosition = snapshot.PlayerPosition;
            Quaternion desiredRotation = Quaternion.Euler(snapshot.PlayerEulerAngles);

            if (snapshot.HasPlayerTransformInEnvironment)
            {
                Transform environmentStart = FindActiveEnvironmentStart();
                if (environmentStart != null)
                {
                    desiredPosition = environmentStart.TransformPoint(snapshot.PlayerPositionInEnvironment);
                    Vector3 relativeForward = snapshot.PlayerForwardInEnvironment.sqrMagnitude > 0.0001f
                        ? snapshot.PlayerForwardInEnvironment.normalized
                        : Vector3.forward;
                    desiredRotation = environmentStart.rotation * Quaternion.LookRotation(relativeForward, Vector3.up);
                }
                else
                {
                    canApplyTransform = false;
                }
            }
            else
            {
                // Schema 1 saves contain only an absolute transform. It is unsafe to
                // apply that position in a different procedural world, so retain the
                // destination start instead.
                TriangleGameFlow flow = TriangleRuntime.GameFlow;
                bool crossEnvironment = flow != null
                                        && flow.HasActiveEnvironment
                                        && !Name.IsNone(snapshot.CurrentEnvironmentId)
                                        && snapshot.CurrentEnvironmentId != flow.CurrentEnvironmentId;
                if (crossEnvironment)
                {
                    canApplyTransform = false;
                }
            }

            if (player != null && canApplyTransform)
            {
                if (!snapshot.IsFinite())
                {
                    TriangleLog.SaveWarn("Save snapshot contains an invalid player transform; transform was not applied.");
                }
                else
                {
                    player.Teleport(desiredPosition, desiredRotation);
                    appliedState = true;
                }
            }

            if (player != null)
            {
                player.Lucidity.RestoreFromSave(snapshot.Lucidity);
                appliedState = true;

                player.Recognition.RestoreFromSaveWithCatalogue(snapshot.Recognition, snapshot.RecognizedPatternIds);
                appliedState = true;
            }

            if (TriangleRuntime.Wake != null)
            {
                TriangleRuntime.Wake.RestoreState(snapshot.FalseAwakeningLayer);
                appliedState = true;
            }

            TriangleNotebook notebook = UnityEngine.Object.FindObjectOfType<TriangleNotebook>();
            if (notebook != null)
            {
                notebook.SetInteractionCount(snapshot.NotebookInteractionCount);
                appliedState = true;
            }

            TriangleDoor door = UnityEngine.Object.FindObjectOfType<TriangleDoor>();
            if (door != null)
            {
                if (snapshot.BedroomDoorOpen)
                {
                    if (!door.IsOpen)
                    {
                        door.Open(true);
                    }
                }
                else if (door.IsOpen)
                {
                    door.Close(true);
                }

                appliedState = true;
            }

            TriangleRuntime.GameFlow?.SetLastBedroomDoorOpen(snapshot.BedroomDoorOpen);

            if (!appliedState)
            {
                TriangleLog.SaveWarn("Save snapshot was valid, but no world state could be applied.");
            }

            return appliedState;
        }

        private static Transform FindActiveEnvironmentStart()
        {
            TriangleGameFlow flow = TriangleRuntime.GameFlow;
            if (flow == null || !flow.HasActiveEnvironment)
            {
                return null;
            }

            foreach (TriangleRoomBuilder room in UnityEngine.Object.FindObjectsOfType<TriangleRoomBuilder>())
            {
                if (room.DreamStart != null)
                {
                    return room.DreamStart;
                }
            }

            foreach (TriangleStationBuilder station in UnityEngine.Object.FindObjectsOfType<TriangleStationBuilder>())
            {
                if (station.DreamStart != null)
                {
                    return station.DreamStart;
                }
            }

            return null;
        }

        /// <summary>Builds a fresh snapshot from the live world.</summary>
        public TriangleSaveData BuildSnapshot()
        {
            var save = new TriangleSaveData();

            TrianglePlayer player = TriangleRuntime.Player;
            if (player != null)
            {
                save.PlayerPosition = player.transform.position;
                save.PlayerEulerAngles = player.transform.eulerAngles;

                Transform environmentStart = FindActiveEnvironmentStart();
                if (environmentStart != null)
                {
                    save.PlayerPositionInEnvironment = environmentStart.InverseTransformPoint(player.transform.position);
                    save.PlayerForwardInEnvironment = environmentStart.InverseTransformDirection(player.transform.forward).normalized;
                    save.HasPlayerTransformInEnvironment = true;
                }
            }

            if (player != null)
            {
                save.Lucidity = player.Lucidity.GetLucidity();
                save.Recognition = player.Recognition.GetRecognition();
                save.RecognizedPatternIds = player.Recognition.GetRecognizedPatternIds();
                save.TriangleEncounters = save.RecognizedPatternIds.Count;
            }

            save.CurrentEnvironmentId = DiscoverCurrentEnvironmentId();

            if (TriangleRuntime.Wake != null)
            {
                save.FalseAwakeningLayer = Mathf.Max(0, TriangleRuntime.Wake.CurrentLayer);
            }

            // Actor-owned slice state is captured through explicit persistence hooks.
            TriangleNotebook notebook = UnityEngine.Object.FindObjectOfType<TriangleNotebook>();
            if (notebook != null)
            {
                save.NotebookInteractionCount = Mathf.Max(0, notebook.InteractionCount);
                save.NotebookInteracted = save.NotebookInteractionCount > 0;
                int persistedPageCount = Mathf.Clamp(save.NotebookInteractionCount, 0, 3);
                for (int pageIndex = 0; pageIndex < persistedPageCount; pageIndex++)
                {
                    string pageId = $"NotebookPage_{pageIndex + 1}";
                    save.NotebookPages.Add(pageId);
                    save.DiscoveredMemories.Add(pageId);
                }
            }

            TriangleDoor door = UnityEngine.Object.FindObjectOfType<TriangleDoor>();
            if (door != null)
            {
                save.BedroomDoorOpen = door.IsOpen;
                TriangleRuntime.GameFlow?.SetLastBedroomDoorOpen(save.BedroomDoorOpen);
            }
            else if (TriangleRuntime.GameFlow != null)
            {
                save.BedroomDoorOpen = TriangleRuntime.GameFlow.LastBedroomDoorOpen;
            }

            save.SetEnvironmentFlag("BedroomDoorOpen", save.BedroomDoorOpen);
            return save;
        }

        private string DiscoverCurrentEnvironmentId()
        {
            // The flow object is authoritative when procedural environment swapping
            // is available. Builder checks keep snapshots useful in simpler worlds.
            TriangleGameFlow flow = TriangleRuntime.GameFlow;
            if (flow != null && flow.HasActiveEnvironment)
            {
                return flow.CurrentEnvironmentId;
            }

            if (UnityEngine.Object.FindObjectOfType<TriangleRoomBuilder>() != null)
            {
                return "ChildhoodBedroom";
            }

            if (UnityEngine.Object.FindObjectOfType<TriangleStationBuilder>() != null)
            {
                return "UndergroundStation";
            }

            return string.Empty;
        }
    }
}
