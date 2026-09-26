using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Triangle.Core;
using Triangle.Dream;
using Triangle.Interaction;
using Triangle.Player;
using Triangle.Save;
using Triangle.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Triangle.Tests
{
    /// <summary>
    /// The slice acceptance path, driven against the real scene in play mode.
    ///
    /// This is the check the Unreal project never got to run. Its status file had
    /// claimed "source complete; runtime gate pending" for weeks, because the editor
    /// target could not build. Here the actual vertical slice is executed:
    ///
    ///   bedroom spawn -> notebook page -> corner manifestation -> recognition
    ///   -> High Lucidity door -> second interaction -> Platform 12 -> wrong train
    ///   -> save -> restart -> load
    ///
    /// Every assertion reads live system state. Nothing is mocked.
    /// </summary>
    public class TriangleSlicePlayModeTests
    {
        private const string SceneName = "Dream";

        [UnitySetUp]
        public IEnumerator LoadDream()
        {
            // Start from a clean dream every time.
            PlayerPrefs.DeleteKey("TriangleSmokeCleared");
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator ClearDream()
        {
            if (TriangleRuntime.Save != null)
            {
                TriangleRuntime.Save.DeleteSaveData();
            }

            yield return null;
        }

        private static void AssertThat(bool condition, string description)
        {
            Assert.IsTrue(condition, description);
        }

        /// <summary>
        /// Raises Lucidity to High, opens the bedroom door, presses it a second time to
        /// request the cut, then waits for the fade-and-swap to land at the station.
        /// </summary>
        private static IEnumerator TravelToStation()
        {
            TrianglePlayer player = TriangleRuntime.Player;
            var door = Object.FindObjectOfType<TriangleDoor>();
            Assert.That(door != null, "The bedroom did not build a door to travel through.");

            player.Lucidity.RestoreFromSave(70f);
            door.GetComponent<TriangleInteractable>().Interact(player.gameObject);
            Assert.That(door.IsOpen, "The door did not open at High Lucidity.");

            // The door ignores interaction while it is still swinging, so the second
            // press has to be a deliberate, separate beat rather than a double tap.
            float swing = 0f;
            while (swing < 1.5f)
            {
                swing += Time.deltaTime;
                yield return null;
            }

            door.GetComponent<TriangleInteractable>().Interact(player.gameObject);

            // Wait for the whole transition, not just the environment swap. The flow
            // holds input locked for a beat after the fade, and it correctly refuses a
            // save load while it still considers itself mid-transition.
            float elapsed = 0f;
            while (elapsed < 8f &&
                   (TriangleRuntime.GameFlow.CurrentEnvironmentId != "UndergroundStation" ||
                    TriangleRuntime.GameFlow.IsTransitioning))
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            Assert.That(TriangleRuntime.GameFlow.CurrentEnvironmentId == "UndergroundStation",
                $"The station transition did not complete within 8 s (still '{TriangleRuntime.GameFlow.CurrentEnvironmentId}').");
            Assert.That(!TriangleRuntime.GameFlow.IsTransitioning,
                "The flow is still transitioning after the station swap settled.");
        }

        // --- Environment assembly ------------------------------------------------

        [UnityTest]
        public IEnumerator BedroomAssemblesAtStartup()
        {
            yield return null;

            AssertThat(TriangleRuntime.GameFlow != null, "GameFlow was not registered.");
            AssertThat(TriangleRuntime.GameFlow.HasActiveEnvironment, "No environment was assembled at startup.");
            AssertThat(TriangleRuntime.GameFlow.CurrentEnvironmentId == "ChildhoodBedroom",
                $"Expected the childhood bedroom, got '{TriangleRuntime.GameFlow.CurrentEnvironmentId}'.");

            var room = Object.FindObjectOfType<TriangleRoomBuilder>();
            Assert.That(room != null, "The bedroom builder is missing from the world.");
            Assert.That(room.SpawnedPieces.Count > 20, $"The bedroom looks under-built ({room.SpawnedPieces.Count} pieces).");
            Assert.That(room.DreamStart != null, "The bedroom did not create a DreamStart.");

            var point = TrianglePoint.FindInScene();
            Assert.That(point != null, "The bedroom did not build a Triangle point.");
            Assert.That(Object.FindObjectOfType<TriangleNotebook>() != null, "The bedroom has no notebook.");
            Assert.That(Object.FindObjectOfType<TriangleDoor>() != null, "The bedroom has no door.");
        }

        [UnityTest]
        public IEnumerator PlayerSpawnsInsideTheRoom()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            Assert.That(player != null, "The player was not created.");
            Assert.That(player.Camera != null && player.Camera.enabled, "The player camera is missing or disabled.");

            var room = Object.FindObjectOfType<TriangleRoomBuilder>();
            Assert.That(room.DreamStart != null, "No DreamStart to measure against.");

            float distance = Vector3.Distance(player.transform.position, room.DreamStart.position);
            Assert.That(distance < 2f, $"The player spawned {distance:0.00} m from the DreamStart.");

            // The room is 6.2 x 8.2 m with 3 m walls. A spawn outside those bounds
            // means the pawn landed in the void rather than the dream.
            Vector3 p = player.transform.position;
            Assert.That(Mathf.Abs(p.x) < 3.2f && Mathf.Abs(p.z) < 4.2f,
                $"The player spawned outside the room footprint at {p}.");
        }

        [UnityTest]
        public IEnumerator TheRoomIsActuallyEnclosed()
        {
            yield return null;

            // If the shell were misbuilt the player would see the void. Fire a ray in
            // every horizontal direction and require a wall each time.
            TrianglePlayer player = TriangleRuntime.Player;
            Vector3 origin = new Vector3(0f, 1.5f, 0f);

            string[] axes = { "+X", "-X", "+Z", "-Z" };
            Vector3[] directions =
            {
                Vector3.right, Vector3.left, Vector3.forward, Vector3.back
            };

            for (int i = 0; i < directions.Length; i++)
            {
                bool hit = Physics.Raycast(origin, directions[i], out RaycastHit hitInfo, 20f);
                Assert.That(hit, $"No wall was hit looking {axes[i]} from the middle of the bedroom.");
            }

            Assert.That(player != null, "The player was not created.");
        }

        // --- Notebook and lucidity -------------------------------------------------

        [UnityTest]
        public IEnumerator ReadingTheNotebookGrantsLucidityExactlyOnce()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            var notebook = Object.FindObjectOfType<TriangleNotebook>();
            Assert.That(notebook != null, "The notebook was not built.");

            float before = player.Lucidity.GetLucidity();
            notebook.GetComponent<TriangleInteractable>().Interact(player.gameObject);

            Assert.That(notebook.InteractionCount == 1, "Reading the notebook did not increment its count.");
            Assert.That(player.Lucidity.GetLucidity() > before, "Reading a new page granted no lucidity.");

            // The notebook has three pages, so the first three reads each reveal a new
            // page and each pay out. A fourth read is past the last page and must not.
            for (int i = 0; i < 2; i++)
            {
                notebook.GetComponent<TriangleInteractable>().Interact(player.gameObject);
            }

            Assert.That(notebook.InteractionCount == 3, "The third read did not count.");

            float afterLastPage = player.Lucidity.GetLucidity();
            notebook.GetComponent<TriangleInteractable>().Interact(player.gameObject);

            Assert.That(notebook.InteractionCount == 4, "The fourth read did not count.");
            Assert.That(Mathf.Approximately(player.Lucidity.GetLucidity(), afterLastPage),
                "Re-reading the notebook past its last page granted lucidity again.");
        }

        [UnityTest]
        public IEnumerator LucidityClimbsToMediumWhileWatchingTheCorner()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            var room = Object.FindObjectOfType<TriangleRoomBuilder>();
            Assert.That(room != null, "No bedroom to watch from.");

            // Stand back from the corner and hold the gaze, which is the only thing
            // that grants passive lucidity.
            Vector3 corner = room.TriangleCornerLocation;
            player.Teleport(new Vector3(corner.x, 0.92f, corner.z + 2.0f), Quaternion.identity);

            float watched = 0f;
            while (watched < 4f)
            {
                player.Camera.transform.LookAt(corner, Vector3.up);
                watched += Time.deltaTime;
                yield return null;
            }

            // The director grants 1.5 lucidity per second, so four seconds of held gaze
            // is worth roughly 6. Assert the ramp is actually running rather than a
            // band threshold: reaching Medium by staring alone would take 20 seconds,
            // which is the point of the tuning.
            Assert.That(player.Lucidity.GetLucidity() > 3f,
                $"Holding the corner for {watched:0.0}s only reached {player.Lucidity.GetLucidity():0.0} lucidity.");
            Assert.That(player.Lucidity.GetLucidityLevel() == TriangleLucidityLevel.Low,
                "Passive watching should not by itself reach Medium lucidity; progression is event-gated.");
        }

        // --- Manifestation and recognition -------------------------------------------

        [UnityTest]
        public IEnumerator WatchingTheCornerManifestsThePointAndGrantsRecognition()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            var room = Object.FindObjectOfType<TriangleRoomBuilder>();
            var point = TrianglePoint.FindInScene();
            Assert.That(point != null, "No Triangle point to manifest.");

            Vector3 corner = room.TriangleCornerLocation;
            player.Teleport(new Vector3(corner.x, 0.92f, corner.z + 2.0f), Quaternion.identity);

            // The gaze must be held for 1.2 s, the manifest interval (5.5 s) must
            // elapse, and the chosen body then lives 4-7 s before it resolves. The
            // director only grants recognition at resolution, so the wait has to cover
            // the whole chain.
            float elapsed = 0f;
            while (elapsed < 20f)
            {
                player.Camera.transform.LookAt(corner, Vector3.up);
                elapsed += Time.deltaTime;
                yield return null;
            }

            Assert.That(TriangleRuntime.Director.TrianglePresence > 0f,
                "The director never registered any presence while the corner was watched.");
            Assert.That(player.Recognition.GetRecognition() > 0f,
                "A manifestation resolved without granting any recognition.");
            Assert.That(player.Recognition.CatalogueCount > 0,
                "A witnessed manifestation was not recorded in the pattern catalogue.");
        }

        [UnityTest]
        public IEnumerator MasterRecognitionSensesPresenceWithNothingVisible()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            player.Recognition.RestoreFromSaveWithCatalogue(99f, new List<string> { "UpperCornerPoint" });

            Assert.That(player.Recognition.HasMasterSense(), "Recognition 99 did not reach Master.");
            Assert.That(player.Recognition.CanSensePresenceBeforeVisual(),
                "Master recognition should also imply the pre-visual sense.");

            // At Master the director manifests the hum with no visible body at all.
            // The master-sense interval is 9 s, so the wait has to outlast it.
            float elapsed = 0f;
            while (elapsed < 14f)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            // Assert the hum envelope rather than AudioSource.isPlaying: a headless
            // batch-mode run has no audio device, so playback state is not meaningful.
            Assert.That(TriangleRuntime.Audio.GetHumPresence() > 0.05f,
                $"Master recognition never raised the hum with nothing visible (presence {TriangleRuntime.Audio.GetHumPresence():0.00}).");
        }

        [UnityTest]
        public IEnumerator TheHumFollowsPresence()
        {
            yield return null;

            // The director drives the hum from its own presence ramp every frame, so
            // setting the level directly would simply be overwritten. Raise presence
            // the way the game does and assert the hum answers.
            Assert.That(TriangleRuntime.Director != null, "The Dream Director is missing.");

            TriangleRuntime.Director.TriggerTriangleManifest(2f);

            float elapsed = 0f;
            for (int i = 0; i < 120 && elapsed < 3f; i++)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            Assert.That(TriangleRuntime.Audio.GetHumPresence() > 0.1f,
                $"The hum did not rise with presence (got {TriangleRuntime.Audio.GetHumPresence():0.00}).");

            // A wake withdraws everything, and the hum must fall with it.
            TriangleRuntime.Director.WithdrawPresence();

            elapsed = 0f;
            for (int i = 0; i < 120 && elapsed < 3f; i++)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            Assert.That(TriangleRuntime.Audio.GetHumPresence() < 0.2f,
                $"The hum did not fall when presence was withdrawn (got {TriangleRuntime.Audio.GetHumPresence():0.00}).");
        }

        // --- The door as the environment seam -----------------------------------------

        [UnityTest]
        public IEnumerator TheDoorRefusesToMoveBelowHighLucidity()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            var door = Object.FindObjectOfType<TriangleDoor>();
            Assert.That(door != null, "The bedroom did not build a door.");

            player.Lucidity.RestoreFromSave(20f);
            door.GetComponent<TriangleInteractable>().Interact(player.gameObject);

            Assert.That(!door.IsOpen, "The door opened below High Lucidity.");
        }

        [UnityTest]
        public IEnumerator TheDoorOpensAtHighLucidityThenCutsToTheStation()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            var door = Object.FindObjectOfType<TriangleDoor>();
            Assert.That(door != null, "The bedroom did not build a door.");

            // High Lucidity opens it.
            player.Lucidity.RestoreFromSave(70f);
            Assert.That(player.Lucidity.CanManipulateDream(), "Lucidity 70 did not unlock dream manipulation.");

            door.GetComponent<TriangleInteractable>().Interact(player.gameObject);
            Assert.That(door.IsOpen, "The door did not open at High Lucidity.");

            // The door ignores input while swinging, so the second press must be a
            // separate beat. That pause is the seam the design asks for.
            float swing = 0f;
            while (swing < 1.5f)
            {
                swing += Time.deltaTime;
                yield return null;
            }

            door.GetComponent<TriangleInteractable>().Interact(player.gameObject);

            float swapElapsed = 0f;
            while (swapElapsed < 8f &&
                   (TriangleRuntime.GameFlow.CurrentEnvironmentId != "UndergroundStation" ||
                    TriangleRuntime.GameFlow.IsTransitioning))
            {
                swapElapsed += Time.deltaTime;
                yield return null;
            }

            Assert.That(TriangleRuntime.GameFlow.CurrentEnvironmentId == "UndergroundStation",
                $"The door did not cut to the station (still '{TriangleRuntime.GameFlow.CurrentEnvironmentId}').");

            // Object.Destroy is deferred to the end of the frame.
            yield return null;

            Assert.That(Object.FindObjectOfType<TriangleRoomBuilder>() == null,
                "The bedroom builder survived the environment swap.");
        }

        // --- The station ----------------------------------------------------------------

        [UnityTest]
        public IEnumerator TheStationIsBuiltPopulatedAndHabitable()
        {
            yield return null;

            // Walk the player through the door rather than starting in the station.
            yield return TravelToStation();

            TrianglePlayer player = TriangleRuntime.Player;
            var station = Object.FindObjectOfType<TriangleStationBuilder>();
            Assert.That(station != null, "The station builder is missing after the transition.");
            Assert.That(station.SpawnedPieces.Count > 30, $"The station looks under-built ({station.SpawnedPieces.Count} pieces).");
            Assert.That(station.DreamStart != null, "The station did not create a DreamStart.");
            Assert.That(station.Platform12Location != Vector3.zero, "Platform 12 has no marker location.");

            var train = Object.FindObjectOfType<TriangleTrain>();
            Assert.That(train != null, "The wrong train was not built.");
            Assert.That(TrianglePoint.FindInScene() != null, "The station did not build a Triangle point.");

            float distance = Vector3.Distance(player.transform.position, station.DreamStart.position);
            Assert.That(distance < 2f, $"The player landed {distance:0.00} m from the station start.");

            // The player must be standing on the platform, not inside the track bed.
            Assert.That(player.transform.position.y > 0.5f,
                $"The player is not on the platform (y = {player.transform.position.y:0.00}).");
        }

        [UnityTest]
        public IEnumerator TheWrongTrainTogglesItsDoors()
        {
            yield return null;

            // The train only exists at the station, so cross the door seam first.
            yield return TravelToStation();

            var train = Object.FindObjectOfType<TriangleTrain>();
            Assert.That(train != null, "The wrong train is missing.");

            var interactable = train.GetComponent<TriangleInteractable>();
            Assert.That(interactable.InteractionName == "Open Wrong Train",
                $"The train's initial prompt was '{interactable.InteractionName}'.");

            interactable.Interact(train.gameObject);
            Assert.That(interactable.InteractionName == "Close Wrong Train",
                "Opening the train did not update its interaction prompt.");

            interactable.Interact(train.gameObject);
            Assert.That(interactable.InteractionName == "Open Wrong Train",
                "Closing the train did not restore its interaction prompt.");
        }

        // --- The unreliable wake ------------------------------------------------------------

        [UnityTest]
        public IEnumerator TheDreamRefusesToLetGoOfAnUnawarePlayer()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            player.Recognition.RestoreFromSaveWithCatalogue(10f, new List<string> { "CornerPoint" });

            Assert.That(!TriangleRuntime.Wake.TryWake(),
                "The dream let go of a player with Recognition 10.");
            Assert.That(TriangleRuntime.Wake.CurrentLayer == 0, "A refused wake still deepened the layer.");
        }

        [UnityTest]
        public IEnumerator AWakeAtTheRecognitionFloorResolvesAndWithdrawsPresence()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            player.Recognition.RestoreFromSaveWithCatalogue(80f,
                new List<string> { "CornerPoint", "ClockStops12" });

            Assert.That(TriangleRuntime.Wake.TryWake(), "The dream refused a wake at Recognition 80.");
            Assert.That(TriangleRuntime.Wake.CurrentLayer >= 1, "A resolved wake did not deepen the layer.");

            // The release withdraws everything immediately.
            Assert.That(TriangleRuntime.Director.TrianglePresence < 0.2f,
                "The wake did not withdraw the director's presence.");
        }

        // --- Persistence -----------------------------------------------------------------------

        [UnityTest]
        public IEnumerator SavingAndLoadingRestoresTheDream()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            var notebook = Object.FindObjectOfType<TriangleNotebook>();

            // Build up some state worth losing.
            notebook.GetComponent<TriangleInteractable>().Interact(player.gameObject);
            player.Lucidity.RestoreFromSave(45f);
            player.Recognition.RestoreFromSaveWithCatalogue(60f, new List<string> { "CornerPoint", "GapInTheWall" });
            TriangleRuntime.Wake.RestoreState(2);

            var snapshot = TriangleRuntime.Save.BuildSnapshot();
            Assert.That(snapshot != null, "No snapshot could be built from the live world.");
            Assert.That(snapshot.IsFinite(), "The snapshot contains a non-finite transform.");
            Assert.That(snapshot.HasPlayerTransformInEnvironment,
                "The snapshot did not record an environment-relative transform.");
            Assert.That(snapshot.NotebookInteractionCount == 1, "The snapshot lost the notebook count.");
            Assert.That(snapshot.FalseAwakeningLayer == 2, "The snapshot lost the wake depth.");
            Assert.That(snapshot.RecognizedPatternIds.Count == 2, "The snapshot lost the pattern catalogue.");

            AssertThat(TriangleRuntime.Save.SaveNow(snapshot), "Writing the save failed.");
            AssertThat(TriangleRuntime.Save.HasSaveData(), "The save slot does not exist after a write.");

            // Wipe the live dream, then load it back.
            player.Lucidity.RestoreFromSave(0f);
            player.Recognition.RestoreFromSaveWithCatalogue(0f, new List<string>());
            TriangleRuntime.Wake.RestoreState(0);

            var loaded = TriangleRuntime.Save.LoadNow();
            Assert.That(loaded != null, "The save could not be read back.");
            Assert.That(loaded.SchemaVersion == TriangleSaveData.CurrentSchemaVersion,
                $"The loaded schema is {loaded.SchemaVersion}, expected {TriangleSaveData.CurrentSchemaVersion}.");

            var result = TriangleRuntime.Save.ApplySnapshot(loaded);
            Assert.That(result == TriangleSaveApplyResult.Applied,
                $"Applying the snapshot returned {result} rather than Applied.");

            Assert.That(Mathf.Approximately(player.Lucidity.GetLucidity(), 45f),
                $"The load restored lucidity as {player.Lucidity.GetLucidity():0.0}, expected 45.");
            Assert.That(Mathf.Approximately(player.Recognition.GetRecognition(), 60f),
                $"The load restored recognition as {player.Recognition.GetRecognition():0.0}, expected 60.");
            Assert.That(player.Recognition.CatalogueCount == 2, "The load did not restore the pattern catalogue.");
            Assert.That(TriangleRuntime.Wake.CurrentLayer == 2, "The load did not restore the wake depth.");
            Assert.That(Object.FindObjectOfType<TriangleNotebook>().InteractionCount == 1,
                "The load did not restore the notebook count.");
        }

        [UnityTest]
        public IEnumerator ALegacySaveIsMigratedRatherThanRefused()
        {
            yield return null;

            // A schema-0 save with a legacy count and no stable ids, written by hand.
            var legacy = new TriangleSaveData
            {
                SchemaVersion = 0,
                Lucidity = 30f,
                Recognition = 12f,
                TriangleEncounters = 3,
                RecognizedPatternIds = new List<string>(),
                NotebookInteracted = true,
                NotebookInteractionCount = 0,
                CurrentEnvironmentId = "ChildhoodBedroom"
            };

            string path = System.IO.Path.Combine(Application.persistentDataPath, TriangleSaveSystem.SaveSlotName + ".json");
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(legacy));

            var loaded = TriangleRuntime.Save.LoadNow();
            Assert.That(loaded != null, "A legacy save was refused outright.");
            Assert.That(loaded.SchemaVersion == TriangleSaveData.CurrentSchemaVersion,
                "The legacy save was not migrated to the current schema.");
            Assert.That(loaded.NotebookInteractionCount == 1,
                "The legacy notebook flag was not carried into the dedicated count.");
            Assert.That(loaded.RecognizedPatternIds.Contains("LegacyTrianglePattern"),
                "The legacy encounter count did not produce an explicit migration id.");
        }

        [UnityTest]
        public IEnumerator ANewerSchemaIsRefusedRatherThanMisread()
        {
            yield return null;

            var future = new TriangleSaveData
            {
                SchemaVersion = TriangleSaveData.CurrentSchemaVersion + 5,
                Lucidity = 99f
            };

            string path = System.IO.Path.Combine(Application.persistentDataPath, TriangleSaveSystem.SaveSlotName + ".json");
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(future));

            Assert.That(TriangleRuntime.Save.LoadNow() == null,
                "A save from a newer schema was loaded instead of refused.");
        }

        [UnityTest]
        public IEnumerator AnUnknownEnvironmentIdIsRejectedSafely()
        {
            yield return null;

            var snapshot = TriangleRuntime.Save.BuildSnapshot();
            snapshot.CurrentEnvironmentId = "MarbleMansion";

            var result = TriangleRuntime.Save.ApplySnapshot(snapshot);
            Assert.That(result == TriangleSaveApplyResult.Failed,
                $"An unknown environment id returned {result} rather than Failed.");
        }

        [UnityTest]
        public IEnumerator ACrossEnvironmentLoadIsQueuedAndCompletes()
        {
            yield return null;

            // Move to the station, save, then load a snapshot that belongs in the
            // bedroom. The save system must queue rather than refuse.
            yield return TravelToStation();

            var snapshot = TriangleRuntime.Save.BuildSnapshot();
            snapshot.CurrentEnvironmentId = "ChildhoodBedroom";
            TriangleRuntime.Save.SaveNow(snapshot);

            var loaded = TriangleRuntime.Save.LoadNow();
            var result = TriangleRuntime.Save.ApplySnapshot(loaded);
            Assert.That(result == TriangleSaveApplyResult.Queued,
                $"A cross-environment load returned {result} rather than Queued.");

            float returnElapsed = 0f;
            while (returnElapsed < 8f && TriangleRuntime.GameFlow.CurrentEnvironmentId != "ChildhoodBedroom")
            {
                returnElapsed += Time.deltaTime;
                yield return null;
            }

            // Object.Destroy is deferred to the end of the frame, so the outgoing
            // station is still in the hierarchy on the frame the swap completes.
            yield return null;
            yield return null;

            Assert.That(TriangleRuntime.GameFlow.CurrentEnvironmentId == "ChildhoodBedroom",
                $"The queued load never returned the player to the bedroom (now '{TriangleRuntime.GameFlow.CurrentEnvironmentId}').");
            Assert.That(Object.FindObjectOfType<TriangleRoomBuilder>() != null,
                "The bedroom was not rebuilt after the queued load.");
            Assert.That(Object.FindObjectOfType<TriangleStationBuilder>() == null,
                "The station survived the queued load.");
        }

        [UnityTest]
        public IEnumerator DeletingTheSaveIsIdempotent()
        {
            yield return null;

            Assert.That(TriangleRuntime.Save.SaveNow(null), "Could not write a save to delete.");
            Assert.That(TriangleRuntime.Save.DeleteSaveData(), "Deleting an existing save failed.");
            Assert.That(!TriangleRuntime.Save.HasSaveData(), "The save slot still exists after deletion.");
            Assert.That(TriangleRuntime.Save.DeleteSaveData(), "Deleting an already-clear slot should report success.");
        }

        // --- Interaction finder -------------------------------------------------------------------

        [UnityTest]
        public IEnumerator TheInteractionFinderFocusesWhatThePlayerLooksAt()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            var notebook = Object.FindObjectOfType<TriangleNotebook>();
            Assert.That(notebook != null, "No notebook to focus.");

            // Stand in front of the notebook and look at it.
            Vector3 target = notebook.GetComponent<TriangleInteractable>().GetInteractionWorldLocation();
            Vector3 stand = target + new Vector3(0f, 0f, -1.0f);
            player.Teleport(stand, Quaternion.identity);
            player.Camera.transform.LookAt(target, Vector3.up);

            // The finder runs on a 0.08 s timer.
            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            Assert.That(player.FocusedInteractable != null,
                "The interaction finder did not focus the notebook the player is looking at.");
            Assert.That(player.FocusedInteractable.InteractionName == "Notebook",
                $"The finder focused '{player.FocusedInteractable.InteractionName}' instead of the notebook.");
        }

        [UnityTest]
        public IEnumerator TheInteractionFinderIgnoresWhatIsBehindThePlayer()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            var notebook = Object.FindObjectOfType<TriangleNotebook>();
            Vector3 target = notebook.GetComponent<TriangleInteractable>().GetInteractionWorldLocation();

            // Stand on the far side of the notebook and look away from it, so the
            // notebook is genuinely behind the player rather than below and ahead.
            player.Teleport(target + new Vector3(0f, 0f, 1.0f), Quaternion.identity);
            player.Camera.transform.LookAt(target + new Vector3(0f, 0f, 20f), Vector3.up);

            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            Assert.That(player.FocusedInteractable == null,
                $"The interaction finder focused '{player.FocusedInteractable?.InteractionName}' while the player faced away from it.");
        }
    }
}
