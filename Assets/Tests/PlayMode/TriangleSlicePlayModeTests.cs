using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Triangle.Audio;
using Triangle.Core;
using Triangle.Dream;
using Triangle.Interaction;
using Triangle.Nightmare;
using Triangle.Player;
using Triangle.Save;
using Triangle.UI;
using Triangle.World;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UI;

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

        /// <summary>
        /// Yields a fixed number of frames, depending on neither clock.
        ///
        /// Every wait in this suite is expressed in frames for one reason: several tests
        /// pause the game, and a wait measured in <c>Time.deltaTime</c> while
        /// <c>timeScale</c> is zero never terminates. That is not a slow test, it is a
        /// hung one - and a hung PlayMode run produces no results file at all, so the
        /// failure surfaces as a missing artifact rather than as a looping test.
        ///
        /// Frames also stop a test being coupled to a frame rate it does not control.
        /// </summary>
        private static IEnumerator WaitFrames(int frames)
        {
            for (int i = 0; i < frames; i++)
            {
                yield return null;
            }
        }

        /// <summary>
        /// Waits until a condition holds or a frame budget runs out.
        ///
        /// Frame-bounded for the same reason as <see cref="WaitFrames"/>, and so a
        /// condition that never becomes true reports as a readable assertion failure
        /// instead of hanging the run.
        /// </summary>
        private static IEnumerator WaitUntil(System.Func<bool> condition, int maxFrames)
        {
            for (int i = 0; i < maxFrames && !condition(); i++)
            {
                yield return null;
            }
        }

        [UnitySetUp]
        public IEnumerator LoadDream()
        {
            // Start from a clean dream every time.
            yield return SceneManager.LoadSceneAsync(SceneName, LoadSceneMode.Single);
            yield return null;
            yield return null;

            // Guard the precondition every pause-related test depends on. A test that
            // pauses and never lifts it leaves the timescale at zero, and the next test
            // in the run then measures a frozen game and fails for the wrong reason.
            // Unconditional, because a test that failed mid-body may well have left the
            // game paused on its way out.
            if (Time.timeScale <= 0f)
            {
                Time.timeScale = 1f;
                TriangleLog.Warn("A previous test left the game paused; the timescale was reset.");
            }

            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;

            // Accessibility settings live in PlayerPrefs, which outlives a dream and
            // therefore outlives a test. Reset to the shipped defaults so a test that
            // changes a volume or a subtitle size cannot leak into the next one.
            TriangleAccessibility.ResetToDefaults();
        }

        [UnityTearDown]
        public IEnumerator ClearDream()
        {
            // Always unpause, whatever the test did. Leaving the timescale at zero
            // silently poisons every subsequent test in the run.
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            if (TriangleRuntime.Save != null)
            {
                // Clears the slot, the staging file and the retained backup.
                TriangleRuntime.Save.DeleteSaveData();
            }

            yield return null;
        }

        /// <summary>
        /// Assertion wrapper that reads as prose at the call site.
        ///
        /// Named AssertThat rather than using NUnit's Assert.That directly: the NUnit
        /// overload has the same bool+string signature, so the two were
        /// interchangeable and the indirection bought nothing while making every call
        /// site read ambiguously.
        /// </summary>
        private static void Check(bool condition, string description)
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

            Check(TriangleRuntime.GameFlow != null, "GameFlow was not registered.");
            Check(TriangleRuntime.GameFlow.HasActiveEnvironment, "No environment was assembled at startup.");
            Check(TriangleRuntime.GameFlow.CurrentEnvironmentId == "ChildhoodBedroom",
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

            TrianglePlayer player = TriangleRuntime.Player;
            Check(player != null, "The player was not created.");

            // If the shell were misbuilt the player would see the void. Fire a ray in
            // every horizontal direction and require a wall each time.
            Vector3 origin = new Vector3(0f, 1.5f, 0f);

            string[] axes = { "+X", "-X", "+Z", "-Z" };
            Vector3[] directions =
            {
                Vector3.right, Vector3.left, Vector3.forward, Vector3.back
            };

            for (int i = 0; i < directions.Length; i++)
            {
                bool hit = Physics.Raycast(origin, directions[i], out RaycastHit hitInfo, 20f);
                Check(hit, $"No wall was hit looking {axes[i]} from the middle of the bedroom.");
            }
        }

        /// <summary>
        /// Every inspectable the bedroom ships has to be reachable.
        ///
        /// The interaction finder only ever enumerates colliders, so an inspectable on
        /// a collider-free object - or one whose collider had been disabled - is
        /// invisible to the player no matter how well-authored its payoff is. The clock
        /// had no collider at all and the window pane's was switched off, which quietly
        /// removed two of the slice's progression objects.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryInspectableIsReachableByTheInteractionFinder()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            Check(player != null, "The player was not created.");

            TriangleInspectable[] inspectables = Object.FindObjectsOfType<TriangleInspectable>(true);
            Check(inspectables.Length >= 3,
                $"Expected the window, the bed and the clock to be inspectable, found {inspectables.Length}.");

            for (int i = 0; i < inspectables.Length; i++)
            {
                TriangleInspectable inspectable = inspectables[i];
                var interactable = inspectable.GetComponent<TriangleInteractable>();
                Check(interactable != null, $"'{inspectable.InspectionName}' has no interactable.");

                // The prompt name has to come from the authored value. Awake ran before
                // the builder could assign the fields, so this used to read "Look".
                Check(interactable.InteractionName == inspectable.InspectionName,
                    $"'{inspectable.InspectionName}' prompts as '{interactable.InteractionName}' instead of its own name.");

                // A collider is what puts the object into the finder's overlap set.
                Collider[] colliders = inspectable.GetComponentsInChildren<Collider>(true);
                bool hasEnabledCollider = false;
                for (int c = 0; c < colliders.Length; c++)
                {
                    if (colliders[c] != null && colliders[c].enabled)
                    {
                        hasEnabledCollider = true;
                        break;
                    }
                }

                Check(hasEnabledCollider,
                    $"'{inspectable.InspectionName}' has no enabled collider, so the interaction finder can never see it.");
            }
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
                player.AimAt(corner);
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
                player.AimAt(corner);
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

            // The player must be standing on a platform, not in the track bed. Height
            // alone does not prove it - the spawn used to sit at x = 0, the middle of
            // the rails, at a y that satisfied any height threshold. Assert the lateral
            // position against the actual track gap as well.
            Vector3 p = player.transform.position;
            Assert.That(p.y > 0.5f, $"The player is not up on the platform (y = {p.y:0.00}).");
            Assert.That(Mathf.Abs(p.x) > 1.5f,
                $"The player spawned over the tracks at x = {p.x:0.00}; the platforms start beyond 2.1 m.");
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

            Check(TriangleRuntime.Save.SaveNow(snapshot), "Writing the save failed.");
            Check(TriangleRuntime.Save.HasSaveData(), "The save slot does not exist after a write.");

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
            // Note the absent signature: pre-signature saves are legitimately
            // markerless, which is why the load path tolerates it for old schemas.
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
            Assert.That(loaded.Signature == TriangleSaveData.ExpectedSignature,
                "The migrated save was not stamped with the format marker.");
            Assert.That(loaded.NotebookInteractionCount == 1,
                "The legacy notebook flag was not carried into the dedicated count.");
            Assert.That(loaded.RecognizedPatternIds.Contains("LegacyTrianglePattern"),
                "The legacy encounter count did not produce an explicit migration id.");
        }

        /// <summary>
        /// A file that parses but is not one of ours must be refused, not adopted.
        ///
        /// JsonUtility will happily deserialise almost any JSON object into this
        /// snapshot type, leaving every field at its default. Because a default object
        /// also passes the schema, range and collection checks, an unrelated or
        /// truncated file at the slot path would previously load as a legitimate save
        /// and quietly reset the player's progress to zero.
        /// </summary>
        [UnityTest]
        public IEnumerator AParseableFileWithoutTheFormatMarkerIsRefused()
        {
            yield return null;

            string path = System.IO.Path.Combine(Application.persistentDataPath, TriangleSaveSystem.SaveSlotName + ".json");
            System.IO.File.WriteAllText(path,
                $"{{\"SchemaVersion\":{TriangleSaveData.CurrentSchemaVersion},\"Lucidity\":99}}");

            Assert.That(TriangleRuntime.Save.LoadNow() == null,
                "A markerless file at the current schema was adopted as a real save instead of being refused.");
        }

        /// <summary>
        /// A damaged primary save falls back to the retained backup.
        ///
        /// The atomic write rules out a half-written primary, but not a cleanly written
        /// one that is somehow unusable. The backup is the last line of defence between
        /// that and total loss.
        /// </summary>
        [UnityTest]
        public IEnumerator AnUnreadableSaveFallsBackToItsBackup()
        {
            yield return null;

            // Write twice: the second write rotates the first into the backup slot.
            TriangleRuntime.Save.SaveNow(null);
            var keeper = TriangleRuntime.Save.BuildSnapshot();
            keeper.Lucidity = 42f;
            TriangleRuntime.Save.SaveNow(keeper);

            Assert.That(TriangleRuntime.Save.HasBackup, "No backup was retained after a second save.");

            // Corrupt the primary. Readable as a file, useless as a save.
            string path = System.IO.Path.Combine(Application.persistentDataPath, TriangleSaveSystem.SaveSlotName + ".json");
            System.IO.File.WriteAllText(path, "{ this is not json");

            var recovered = TriangleRuntime.Save.LoadNow();
            Assert.That(recovered != null, "A damaged save did not fall back to its backup.");
            Assert.That(recovered.Lucidity > 0f,
                "The recovered backup was blank, so the fallback restored nothing.");
        }

        [UnityTest]
        public IEnumerator ANewerSchemaIsRefusedRatherThanMisread()
        {
            yield return null;

            var future = new TriangleSaveData
            {
                SchemaVersion = TriangleSaveData.CurrentSchemaVersion + 5,
                Lucidity = 99f,
                Signature = TriangleSaveData.ExpectedSignature
            };

            string path = System.IO.Path.Combine(Application.persistentDataPath, TriangleSaveSystem.SaveSlotName + ".json");
            System.IO.File.WriteAllText(path, JsonUtility.ToJson(future));

            Assert.That(TriangleRuntime.Save.LoadNow() == null,
                "A save from a newer schema was loaded instead of refused.");
        }

        /// <summary>
        /// A real save written by this build must come back with its marker intact.
        ///
        /// Guards the round trip: if the signature were dropped on serialise, or
        /// stamped onto the object but not the file, the load path would refuse saves
        /// the game had just written.
        /// </summary>
        [UnityTest]
        public IEnumerator ASavedDreamRoundTripsWithItsMarker()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            player.Lucidity.RestoreFromSave(55f);
            player.Recognition.RestoreFromSaveWithCatalogue(70f, new List<string> { "CornerPoint" });

            Assert.That(TriangleRuntime.Save.SaveNow(null), "Could not write the save to read back.");

            var loaded = TriangleRuntime.Save.LoadNow();
            Assert.That(loaded != null, "A save this build just wrote was refused on load.");
            Assert.That(loaded.Signature == TriangleSaveData.ExpectedSignature,
                "The format marker did not survive the save/load round trip.");
            Assert.That(Mathf.Approximately(loaded.Lucidity, 55f),
                $"Lucidity round-tripped as {loaded.Lucidity:0.0}, expected 55.");
            Assert.That(Mathf.Approximately(loaded.Recognition, 70f),
                $"Recognition round-tripped as {loaded.Recognition:0.0}, expected 70.");
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
        public IEnumerator LookingUpDoesNotSnapTheViewToTheFloor()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            Assert.That(player != null, "The player was not created.");
            Assert.That(player.Camera != null, "The player has no camera.");

            // Aim well above the horizon. The bug this guards against read pitch back
            // from transform.eulerAngles, which Unity reports in 0..360, so looking up
            // produced ~350 and the clamp slammed the view to the floor.
            player.AimAt(new Vector3(0f, 6f, 0f));
            yield return null;

            Vector3 forward = player.Camera.transform.forward;
            Assert.That(forward.y > 0.2f,
                $"Aiming above the horizon left the camera pointing at y={forward.y:0.00} (downwards) instead of up.");

            // Sweeping further up must stay clamped and keep looking up, never flip.
            for (int i = 0; i < 40; i++)
            {
                player.AimAt(new Vector3(0f, 40f, 0f));
                yield return null;
            }

            forward = player.Camera.transform.forward;
            Assert.That(forward.y > 0.5f,
                $"Sustained upward aim ended up pointing at y={forward.y:0.00}; the pitch clamp is inverted or wrapping.");
            Assert.That(forward.y <= 1.001f, "The camera forward vector is not normalised.");

            // And looking back down must work too.
            player.AimAt(new Vector3(0f, -6f, 0f));
            yield return null;

            forward = player.Camera.transform.forward;
            Assert.That(forward.y < -0.2f,
                $"Aiming below the horizon left the camera pointing at y={forward.y:0.00} instead of down.");
        }

        [UnityTest]
        public IEnumerator TeleportingResyncsTheViewDirection()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;

            // The bedroom's DreamStart faces 180 degrees, i.e. looking at the window
            // wall. After a teleport the tracked look angles must match, or the next
            // mouse movement would snap the view somewhere unrelated.
            var room = Object.FindObjectOfType<TriangleRoomBuilder>();
            Assert.That(room != null && room.DreamStart != null, "The bedroom has no DreamStart.");

            player.Teleport(room.DreamStart.position, room.DreamStart.rotation);
            yield return null;

            Vector3 forward = player.Camera.transform.forward;
            Assert.That(forward.z < -0.8f,
                $"After teleporting to the DreamStart the player should face -Z, but faces {forward}.");

            // A frame of idle input must not drift the view.
            Vector3 before = player.Camera.transform.forward;
            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            Assert.That(Vector3.Angle(before, player.Camera.transform.forward) < 0.5f,
                "The view drifted while no input was given.");
        }

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
            player.AimAt(target);

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

        // --- Deferred work and the pause menu ------------------------------------

        /// <summary>
        /// A scheduled callback must not be frozen by the pause menu.
        ///
        /// The scheduler ran on Time.deltaTime, and pausing sets timeScale to zero, so
        /// Time.deltaTime is zero and nothing pending could advance. A wake's
        /// difference line scheduled 2.2 s out would stall on the "You wake up" frame
        /// for as long as the player sat in the menu.
        /// </summary>
        [UnityTest]
        public IEnumerator ScheduledWorkSurvivesThePauseMenu()
        {
            yield return null;

            TriangleScheduler scheduler = TriangleRuntime.Scheduler;
            Assert.That(scheduler != null, "No scheduler is registered.");

            bool fired = false;
            scheduler.Schedule("PauseProbe", 0.25f, () => fired = true);

            TriangleRuntime.Hud.SetPaused(true);
            Assert.That(Time.timeScale <= 0f, "The pause menu did not actually stop scaled time.");

            yield return WaitFrames(90);

            TriangleRuntime.Hud.SetPaused(false);

            Assert.That(fired, "A 0.25 s scheduled callback did not fire while the game was paused.");
        }

        /// <summary>
        /// A scaled callback is still available for work that should respect the pause.
        /// </summary>
        [UnityTest]
        public IEnumerator AScaledCallbackIsHeldByThePauseMenu()
        {
            yield return null;

            TriangleScheduler scheduler = TriangleRuntime.Scheduler;
            bool fired = false;
            scheduler.Schedule("ScaledProbe", 0.2f, () => fired = true, scaled: true);

            TriangleRuntime.Hud.SetPaused(true);

            // Frame-counted, not time-counted. This test pauses the game, so any wait
            // measured in scaled time can never elapse - a scaled loop here spins
            // forever and takes the whole suite down with it, which is exactly how this
            // test was written the first time.
            yield return WaitFrames(60);

            Assert.That(!fired, "A scaled callback fired while the game was paused.");

            TriangleRuntime.Hud.SetPaused(false);

            yield return WaitFrames(60);

            Assert.That(fired, "The scaled callback did not fire after the pause was lifted.");
        }

        /// <summary>
        /// The HUD owns the pause state outright, so nothing else can leave the game
        /// frozen or the flag disagreeing with the timescale.
        /// </summary>
        [UnityTest]
        public IEnumerator TheHudOwnsThePauseState()
        {
            yield return null;

            TriangleHUD hud = TriangleRuntime.Hud;
            Assert.That(hud != null, "No HUD is registered.");

            hud.SetPaused(true);
            Assert.That(hud.IsPaused, "The HUD did not record that it is paused.");
            Assert.That(Time.timeScale <= 0f, "Pausing did not stop scaled time.");
            Assert.That(Cursor.visible, "The cursor was not released while paused.");

            hud.SetPaused(false);
            Assert.That(!hud.IsPaused, "The HUD did not clear its paused state.");
            Assert.That(Mathf.Approximately(Time.timeScale, 1f),
                $"Unpausing left the timescale at {Time.timeScale:0.00}.");

            // Toggling twice must land back where it started.
            hud.TogglePause();
            hud.TogglePause();
            Assert.That(!hud.IsPaused, "Two pause toggles did not cancel out.");
            Assert.That(Mathf.Approximately(Time.timeScale, 1f), "Two pause toggles left the timescale altered.");
        }

        // --- Awake cooldown -------------------------------------------------------

        /// <summary>
        /// The wake cooldown is measured in real time.
        ///
        /// On scaled time the player could pause to freeze the cooldown clock and then
        /// mash the wake key, so the four-second gap between wakes became however long
        /// they wanted it to be.
        /// </summary>
        [UnityTest]
        public IEnumerator TheWakeCooldownCannotBeCheatedByPausing()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            player.Recognition.RestoreFromSaveWithCatalogue(80f, new List<string> { "CornerPoint" });

            // Pause first. Every attempt below therefore runs on a frame where scaled
            // time is frozen, so this is exactly the exploit: on the old scaled cooldown
            // the clock never advanced and every one of these would be accepted.
            TriangleRuntime.Hud.SetPaused(true);

            bool leaked = false;
            int attempts = 0;
            for (int frame = 0; frame < 120; frame++)
            {
                attempts++;
                if (TriangleRuntime.Wake.TryWake())
                {
                    leaked = true;
                    break;
                }

                yield return null;
            }

            TriangleRuntime.Hud.SetPaused(false);

            Assert.That(!leaked,
                $"The wake cooldown was shortened by pausing: a wake was accepted after {attempts} " +
                "attempt(s) inside a four second window.");

            // And the cooldown still functions normally once the game is running, so the
            // fix is a clock change rather than a disabled gate.
            Assert.That(!TriangleRuntime.Wake.TryWake(),
                "A second wake was accepted immediately with no cooldown at all.");
        }

        /// <summary>
        /// The interaction prompt must name a key, and must come from the HUD's own
        /// configured hint rather than a literal baked into the interaction scan.
        /// </summary>
        [UnityTest]
        public IEnumerator TheInteractionPromptNamesAKey()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            var notebook = Object.FindObjectOfType<TriangleNotebook>();
            Assert.That(notebook != null, "No notebook to focus.");

            Vector3 target = notebook.GetComponent<TriangleInteractable>().GetInteractionWorldLocation();
            player.Teleport(target + new Vector3(0f, 0f, -1.0f), Quaternion.identity);
            player.AimAt(target);

            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            Assert.That(player.FocusedInteractable != null, "The finder did not focus the notebook.");

            string hint = TriangleRuntime.Hud.InteractionHint;
            Assert.That(!string.IsNullOrEmpty(hint), "The HUD has no interaction hint configured.");
            Assert.That(hint.IndexOf('[') >= 0 && hint.IndexOf(']') > hint.IndexOf('['),
                $"The interaction hint '{hint}' is not presented as a key hint.");

            // And the prompt on screen must be the HUD's hint, not a copy of it.
            var actionText = GameObject.Find("InteractionAction");
            Assert.That(actionText != null, "The HUD built no interaction action label.");
            var label = actionText.GetComponent<Text>();
            Assert.That(label.text == hint,
                $"The prompt shows '{label.text}' but the HUD's hint is '{hint}'.");
        }

        // --- Environment ownership and teardown -----------------------------------

        /// <summary>
        /// Ambient and fog are process-wide, so an environment has to hand them back
        /// when it is torn down. Previously the builders wrote them and walked away,
        /// which left the project render settings permanently mutated after play mode
        /// exited - and because Object.Destroy is deferred to the end of the frame,
        /// the outgoing environment's teardown would land *after* the incoming one had
        /// already applied its own, putting the bedroom's fog on in the station.
        /// </summary>
        [UnityTest]
        public IEnumerator AnEnvironmentOwnsTheGlobalRenderSettings()
        {
            yield return null;

            var room = Object.FindObjectOfType<TriangleEnvironmentAtmosphere>();
            Assert.That(room != null, "The bedroom declared no atmosphere.");
            Assert.That(ReferenceEquals(TriangleEnvironmentAtmosphere.Owner, room),
                "The bedroom's atmosphere is not the current owner of the render settings.");

            // The declared values must actually be in force, not just stored.
            Assert.That(RenderSettings.fog, "Fog is off in an environment that declared it on.");
            Assert.That(RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Trilight,
                "The ambient mode does not match what the environment declared.");
            Assert.That(Mathf.Approximately(RenderSettings.fogDensity, room.FogDensity),
                $"Fog density is {RenderSettings.fogDensity:0.000} but the environment declared {room.FogDensity:0.000}.");
        }

        /// <summary>
        /// After a swap the incoming environment's atmosphere must be the one in force.
        /// This is the regression guard for the deferred-destroy ordering described
        /// above: a naive save-and-restore in OnDestroy fails exactly here.
        /// </summary>
        [UnityTest]
        public IEnumerator AnEnvironmentSwapLeavesTheNewAtmosphereInCharge()
        {
            yield return null;

            float bedroomFog = RenderSettings.fogDensity;

            yield return TravelToStation();

            var station = Object.FindObjectOfType<TriangleEnvironmentAtmosphere>();
            Assert.That(station != null, "The station declared no atmosphere.");
            Assert.That(ReferenceEquals(TriangleEnvironmentAtmosphere.Owner, station),
                "The bedroom's atmosphere still owns the render settings after the swap.");
            Assert.That(!Mathf.Approximately(RenderSettings.fogDensity, bedroomFog),
                $"The station's fog density is still the bedroom's {bedroomFog:0.000}.");

            // Let the deferred destroy land, then confirm nothing overwrote it.
            yield return null;
            yield return null;

            Assert.That(Mathf.Approximately(RenderSettings.fogDensity, station.FogDensity),
                $"The outgoing environment's teardown overwrote the fog density with {RenderSettings.fogDensity:0.000} " +
                $"(expected {station.FogDensity:0.000}).");
        }

        /// <summary>
        /// Intrusions must live inside the hierarchy the teardown can find.
        ///
        /// They used to be created at the scene root, so nothing that shuts the dream
        /// down could reach them and an anomaly would outlive the world it was cut into.
        /// </summary>
        [UnityTest]
        public IEnumerator IntrusionsAreParentedIntoTheDream()
        {
            yield return null;

            // The intrusion root is created lazily, on the first spawn. The bedroom's
            // manifest is the corner point, which spawns no anomaly, so ask for a body
            // that actually cuts something into the world.
            TriangleRuntime.Anomalies.Spawn(TriangleAnomalyKind.TriangularGap, 3f,
                TriangleRuntime.Player.transform.position);

            var root = GameObject.Find("TriangleAnomalies");
            Assert.That(root != null, "No intrusion root was created for a spawned anomaly.");

            Assert.That(root.transform.parent != null,
                "The intrusion root is at the scene root, where the teardown cannot find it.");
            Assert.That(ReferenceEquals(root.transform.parent, TriangleRuntime.Scheduler.transform),
                "The intrusion root is not parented to the dream host.");

            // And the anomaly itself must be under that root, not beside it.
            var actor = Object.FindObjectOfType<TriangleAnomalyActor>();
            Assert.That(actor != null, "The spawned anomaly does not exist.");
            Assert.That(actor.transform.IsChildOf(root.transform),
                "The anomaly is not parented under the intrusion root.");
        }

        // --- Accessibility and audio wiring ---------------------------------------

        /// <summary>
        /// The accessibility settings must survive a change and actually reach the
        /// systems that consume them.
        ///
        /// Subtitle scale and opacity existed on the HUD as inspector-only values, so
        /// the affordance was authored but unreachable - a player could not change
        /// either at runtime. They now live in TriangleAccessibility and the HUD
        /// re-applies on change.
        /// </summary>
        [UnityTest]
        public IEnumerator SubtitleSettingsReachTheHud()
        {
            yield return null;

            TriangleHUD hud = TriangleRuntime.Hud;
            Assert.That(hud != null, "No HUD is registered.");

            float originalScale = TriangleAccessibility.SubtitleScale;
            float originalOpacity = TriangleAccessibility.SubtitleOpacity;

            var subtitle = GameObject.Find("Subtitle");
            Assert.That(subtitle != null, "The HUD built no subtitle object.");

            var text = subtitle.GetComponent<Text>();
            Assert.That(text != null, "The subtitle has no Text component.");
            int baseSize = text.fontSize;

            try
            {
                TriangleAccessibility.SubtitleScale = 2f;
                TriangleAccessibility.SubtitleOpacity = 0.5f;

                // The change event is synchronous, so this does not need a frame.
                Assert.That(text.fontSize > baseSize,
                    $"Subtitle scale did not reach the HUD (still {text.fontSize}px, expected larger than {baseSize}px).");
                Assert.That(text.color.a < 1f,
                    $"Subtitle opacity did not reach the HUD (alpha still {text.color.a:0.00}).");
            }
            finally
            {
                // PlayerPrefs is not part of dream state, so restore it explicitly or the
                // next test inherits a 2x subtitle.
                TriangleAccessibility.SubtitleScale = originalScale;
                TriangleAccessibility.SubtitleOpacity = originalOpacity;
            }

            Assert.That(text.fontSize == baseSize,
                $"Restoring the subtitle scale did not return the font to {baseSize}px (got {text.fontSize}px).");
        }

        /// <summary>
        /// The subtitle backdrop has to follow the text's visibility, or it sits as a
        /// dark rectangle in the middle of an empty screen.
        /// </summary>
        [UnityTest]
        public IEnumerator TheSubtitleBackdropFollowsTheText()
        {
            yield return null;

            bool original = TriangleAccessibility.SubtitleBackdrop;
            var backdrop = GameObject.Find("SubtitleBackdrop");
            var subtitle = GameObject.Find("Subtitle");
            Assert.That(backdrop != null, "The HUD built no subtitle backdrop.");

            try
            {
                TriangleAccessibility.SubtitleBackdrop = true;
                Assert.That(backdrop.activeSelf, "Enabling the backdrop did not show it.");

                // No subtitle is showing, so the plate must be hidden even when enabled.
                Assert.That(!backdrop.activeSelf,
                    "The backdrop is visible with no subtitle on screen.");
            }
            finally
            {
                TriangleAccessibility.SubtitleBackdrop = original;
            }
        }

        /// <summary>
        /// The dream bed has to react to the dream.
        ///
        /// The state machine was fully implemented - four states, a fade, a silence
        /// override - and nothing ever changed its state, so it sat at Calm for the whole
        /// game while the hum rose over an unchanged bed.
        /// </summary>
        [UnityTest]
        public IEnumerator TheDreamBedReactsToTheTriangle()
        {
            yield return null;

            TriangleRuntime.Audio.EnsureFallbackBed();
            yield return null;

            string calmState = TriangleRuntime.Audio.CurrentState;
            Assert.That(calmState == TriangleAudioDirector.StateCalm,
                $"The dream bed did not start calm (it is '{calmState}').");

            // Force a manifestation, which drives presence up and the bed down with it.
            TriangleRuntime.Director.TriggerTriangleManifest(4f);

            // Frame-bounded. The bed's state change is driven by a director event rather
            // than a timer, so it lands on a frame rather than after a span of time, and
            // a condition that never fires has to report as an assertion rather than run
            // the suite's clock down.
            yield return WaitUntil(
                () => TriangleRuntime.Audio.CurrentState != calmState,
                maxFrames: 300);

            Assert.That(TriangleRuntime.Audio.CurrentState != TriangleAudioDirector.StateCalm,
                "The dream bed never left its calm state while The Triangle was present.");
            Assert.That(TriangleRuntime.Audio.CurrentVolume < 0.4f,
                $"The bed did not duck for the presence (volume {TriangleRuntime.Audio.CurrentVolume:0.00}).");

            // And it must come back when the presence withdraws. The recovery fade is
            // real time, so this one does need a generous budget.
            TriangleRuntime.Director.WithdrawPresence();

            yield return WaitUntil(
                () => TriangleRuntime.Audio.CurrentState == TriangleAudioDirector.StateCalm,
                maxFrames: 900);

            Assert.That(TriangleRuntime.Audio.CurrentState == TriangleAudioDirector.StateCalm,
                "The dream bed did not return to calm after the presence withdrew.");
        }

        /// <summary>
        /// Music and hum are separate sliders.
        ///
        /// The hum is a sustained tone around 43/47 Hz, which is the register people find
        /// uncomfortable, so it needs its own control. One combined volume would force a
        /// choice between the bed and the presence cue.
        /// </summary>
        [UnityTest]
        public IEnumerator HumAndMusicVolumesAreIndependent()
        {
            yield return null;

            float originalMusic = TriangleAccessibility.MusicVolume;
            float originalHum = TriangleAccessibility.HumVolume;

            try
            {
                TriangleAccessibility.MusicVolume = 1f;
                TriangleAccessibility.HumVolume = 0f;
                Assert.That(TriangleRuntime.Audio.HumVolume <= 0.001f, "The hum volume was not muted.");
                Assert.That(TriangleRuntime.Audio.MusicBaseVolume > 0.9f, "Muting the hum also muted the bed.");

                TriangleAccessibility.MusicVolume = 0f;
                TriangleAccessibility.HumVolume = 1f;
                Assert.That(TriangleRuntime.Audio.MusicBaseVolume <= 0.001f, "The bed volume was not muted.");
                Assert.That(TriangleRuntime.Audio.HumVolume > 0.9f, "Muting the bed also muted the hum.");
            }
            finally
            {
                TriangleAccessibility.MusicVolume = originalMusic;
                TriangleAccessibility.HumVolume = originalHum;
            }
        }

        /// <summary>
        /// Head bob is off unless asked for, and turning it off mid-walk must not leave
        /// a residual offset frozen into the camera.
        /// </summary>
        [UnityTest]
        public IEnumerator HeadBobIsOptInAndCleansUpAfterItself()
        {
            yield return null;

            TrianglePlayer player = TriangleRuntime.Player;
            Vector3 restPosition = player.Camera.transform.localPosition;

            bool original = TriangleAccessibility.HeadBob;

            try
            {
                TriangleAccessibility.HeadBob = false;
                Assert.That(Vector3.Distance(player.Camera.transform.localPosition, restPosition) < 0.001f,
                    "Disabling head bob left the camera displaced from its rest position.");
            }
            finally
            {
                TriangleAccessibility.HeadBob = original;
            }
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
            player.AimAt(target + new Vector3(0f, 0f, 20f));

            for (int i = 0; i < 20; i++)
            {
                yield return null;
            }

            Assert.That(player.FocusedInteractable == null,
                $"The interaction finder focused '{player.FocusedInteractable?.InteractionName}' while the player faced away from it.");
        }
    }
}
