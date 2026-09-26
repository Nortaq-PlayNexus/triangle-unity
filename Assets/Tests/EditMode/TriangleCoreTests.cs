using System.Collections.Generic;
using NUnit.Framework;
using Triangle.Core;
using Triangle.Dream;
using Triangle.Lucidity;
using Triangle.Save;
using UnityEngine;

namespace Triangle.Tests
{
    /// <summary>
    /// Coverage for the progression maths, the pattern catalogue and the save schema.
    ///
    /// These are direct ports of the four Unreal automation tests in
    /// <c>Source/Triangle/Tests/TriangleCoreTests.cpp</c>. The Unreal suite could
    /// never actually be executed because the editor build was blocked; running the
    /// same assertions under Unity's Edit Mode test runner is how this port gets the
    /// validation the original slice never had.
    /// </summary>
    public class TriangleCoreTests
    {
        [Test]
        public void ManifestationDurationStaysWithinItsWindow()
        {
            var definition = ScriptableObject.CreateInstance<TriangleManifestationDefinition>();
            definition.MinDuration = 4f;
            definition.MaxDuration = 7f;

            for (int i = 0; i < 100; i++)
            {
                float duration = definition.GetRandomDuration();
                Assert.GreaterOrEqual(duration, definition.MinDuration, "Duration respects its minimum");
                Assert.LessOrEqual(duration, definition.MaxDuration, "Duration respects its maximum");
            }

            Object.DestroyImmediate(definition);
        }

        [Test]
        public void InvertedManifestationBoundsAreNormalised()
        {
            var definition = ScriptableObject.CreateInstance<TriangleManifestationDefinition>();
            definition.MinDuration = 9f;
            definition.MaxDuration = 2f;

            Assert.AreEqual(9f, definition.GetRandomDuration(), 0.0001f, "Inverted duration bounds are normalized");

            Object.DestroyImmediate(definition);
        }

        [Test]
        public void RecognitionDeduplicatesSightingsAndRestoresTheCatalogue()
        {
            var recognition = new TriangleRecognition();
            const string firstPattern = "CornerPoint";
            const string secondPattern = "ClockTwelve";

            recognition.AddRecognition(10f, firstPattern);
            recognition.AddRecognition(10f, firstPattern);
            Assert.AreEqual(1, recognition.GetRecognizedPatternIds().Count, "Duplicate sightings are recorded once");

            var savedPatterns = new List<string> { firstPattern, secondPattern, firstPattern, string.Empty };
            recognition.RestoreFromSaveWithCatalogue(80f, savedPatterns);

            Assert.AreEqual(80f, recognition.GetRecognition(), 0.0001f, "Saved recognition is restored");
            Assert.AreEqual(2, recognition.GetRecognizedPatternIds().Count, "Catalogue is restored and deduplicated");
            Assert.IsTrue(recognition.CanAttemptWake(), "Anticipating threshold is restored");
        }

        [Test]
        public void RecognitionRestoreDoesNotReplayMilestones()
        {
            var recognition = new TriangleRecognition();
            int levelEvents = 0;
            int senseEvents = 0;
            recognition.OnRecognitionLevelChanged += _ => levelEvents++;
            recognition.OnSensePresence += _ => senseEvents++;

            recognition.RestoreFromSaveWithCatalogue(99f, new List<string> { "GapInTheWall" });

            Assert.AreEqual(0, levelEvents, "A load is state sync, not a progression milestone");
            Assert.AreEqual(0, senseEvents, "The pre-visual sense must not replay on load");
        }

        [Test]
        public void LucidityRestoreIsClampedAndDerivesLevelWithoutProgression()
        {
            var lucidity = new TriangleLucidity();
            int levelEvents = 0;
            lucidity.OnLucidityLevelChanged += _ => levelEvents++;

            lucidity.SetLucidity(95f);
            Assert.AreEqual(1, levelEvents, "Normal progression does cross a level band");

            levelEvents = 0;
            lucidity.RestoreFromSave(45f);
            Assert.AreEqual(45f, lucidity.GetLucidity(), 0.0001f, "Saved lucidity is clamped and restored");
            Assert.AreEqual(TriangleLucidityLevel.Medium, lucidity.GetLucidityLevel(), "Restored level is derived without progression");
            Assert.AreEqual(0, levelEvents, "Restore must not broadcast a level change");

            lucidity.RestoreFromSave(1000f);
            Assert.AreEqual(100f, lucidity.GetLucidity(), 0.0001f, "Out-of-range lucidity is clamped");
        }

        [Test]
        public void LucidityIgnoresNonFiniteValues()
        {
            var lucidity = new TriangleLucidity();
            lucidity.SetLucidity(50f);
            lucidity.SetLucidity(float.NaN);
            Assert.AreEqual(50f, lucidity.GetLucidity(), 0.0001f, "NaN is rejected");
            lucidity.AddLucidity(float.PositiveInfinity);
            Assert.AreEqual(50f, lucidity.GetLucidity(), 0.0001f, "Infinity is rejected");
        }

        [Test]
        public void NewSaveUsesTheLegacySentinelAndFiniteDefaults()
        {
            var save = new TriangleSaveData();

            Assert.AreEqual(0, save.SchemaVersion, "New save objects use the legacy sentinel until the save boundary");
            Assert.IsTrue(save.IsFinite(), "New save transforms are finite");
            Assert.IsFalse(save.HasPlayerTransformInEnvironment, "New saves defer environment-relative transform authority");
            Assert.AreEqual(0, save.FalseAwakeningLayer, "New saves start without a wake layer");
        }

        [Test]
        public void EnvironmentFlagsRoundTrip()
        {
            var save = new TriangleSaveData();
            Assert.IsFalse(save.GetEnvironmentFlag("BedroomDoorOpen", false));
            save.SetEnvironmentFlag("BedroomDoorOpen", true);
            Assert.IsTrue(save.GetEnvironmentFlag("BedroomDoorOpen", false));
            save.SetEnvironmentFlag("BedroomDoorOpen", false);
            Assert.IsFalse(save.GetEnvironmentFlag("BedroomDoorOpen", true), "Re-setting a flag overwrites rather than appends");
        }

        [Test]
        public void LucidityLevelBandsMatchTheDesign()
        {
            Assert.AreEqual(TriangleLucidityLevel.Low, TriangleLucidity.LevelFor(0f));
            Assert.AreEqual(TriangleLucidityLevel.Low, TriangleLucidity.LevelFor(29.9f));
            Assert.AreEqual(TriangleLucidityLevel.Medium, TriangleLucidity.LevelFor(30f));
            Assert.AreEqual(TriangleLucidityLevel.Medium, TriangleLucidity.LevelFor(59.9f));
            Assert.AreEqual(TriangleLucidityLevel.High, TriangleLucidity.LevelFor(60f));
            Assert.AreEqual(TriangleLucidityLevel.High, TriangleLucidity.LevelFor(89.9f));
            Assert.AreEqual(TriangleLucidityLevel.Maximum, TriangleLucidity.LevelFor(90f));
        }

        [Test]
        public void RecognitionLevelBandsMatchTheDesign()
        {
            Assert.AreEqual(TriangleRecognitionLevel.Unknowing, TriangleRecognition.LevelFor(0f));
            Assert.AreEqual(TriangleRecognitionLevel.Noticing, TriangleRecognition.LevelFor(25f));
            Assert.AreEqual(TriangleRecognitionLevel.Recognizing, TriangleRecognition.LevelFor(50f));
            Assert.AreEqual(TriangleRecognitionLevel.Anticipating, TriangleRecognition.LevelFor(75f));
            Assert.AreEqual(TriangleRecognitionLevel.Master, TriangleRecognition.LevelFor(95f));
        }

        [Test]
        public void EnvironmentIdMappingIsStable()
        {
            Assert.AreEqual("ChildhoodBedroom", TriangleGameFlow.IdFor(TriangleDreamEnvironment.ChildhoodBedroom));
            Assert.AreEqual("UndergroundStation", TriangleGameFlow.IdFor(TriangleDreamEnvironment.UndergroundStation));
            Assert.AreEqual(TriangleDreamEnvironment.UndergroundStation, TriangleGameFlow.EnvironmentForId("UndergroundStation"));
            Assert.IsTrue(TriangleGameFlow.IsKnownEnvironmentId("ChildhoodBedroom"));
            Assert.IsFalse(TriangleGameFlow.IsKnownEnvironmentId("MarbleMansion"), "Unknown ids are rejected safely");
        }

        [Test]
        public void CentimetreConversionMapsTheAuthoredBedroomToMetres()
        {
            // The Unreal room used 410/310/300 cm half-extents. Verify the basis
            // mapping and the unit scale that the ported builders depend on.
            Assert.AreEqual(0.01f, Cm.ToMetres, 0.000001f);

            Vector3 backWall = Cm.Pos(-410f, 0f, 150f);
            Assert.AreEqual(0f, backWall.x, 0.0001f, "Unreal Y maps to Unity X");
            Assert.AreEqual(1.5f, backWall.y, 0.0001f, "Unreal Z (up) maps to Unity Y in metres");
            Assert.AreEqual(-4.1f, backWall.z, 0.0001f, "Unreal X maps to Unity Z");

            Vector3 dir = Cm.Dir(1f, 0f, 0f);
            Assert.AreEqual(0f, dir.x, 0.0001f);
            Assert.AreEqual(0f, dir.y, 0.0001f);
            Assert.AreEqual(1f, dir.z, 0.0001f, "Unreal forward is Unity forward");

            // A yaw of 180 in Unreal faces the back wall; the converted rotation must
            // therefore face -Z in Unity.
            Vector3 facing = Cm.Rot(0f, 180f) * Vector3.forward;
            Assert.AreEqual(0f, facing.x, 0.001f);
            Assert.AreEqual(0f, facing.y, 0.001f);
            Assert.AreEqual(-1f, facing.z, 0.001f);
        }

        // --- Motif scheduling ---------------------------------------------------

        [Test]
        public void MotifClockReschedulesInsteadOfFiringWhenRecognitionIsTooLow()
        {
            // Regression test. ClockTick reschedules a due motif whose recognition gate
            // is unmet, writing the entry back into the table. Doing that while
            // enumerating the table threw "Collection was modified", but only on frames
            // where a scheduled motif actually came due - roughly once every 30 s in
            // play, so the timing-dependent PlayMode suite kept missing it. Driving the
            // clock directly makes the branch deterministic.
            var motifs = new TriangleMotifSystem();
            int fired = 0;
            motifs.OnMotif += _ => fired++;

            motifs.RegisterMotif("GatedMotif", new TriangleMotifConfig(0.1f, 0.2f, 99f));
            motifs.EnableAutomaticScheduling(true);

            // No player exists, so recognition reads 0 and the gate can never be met.
            for (int i = 0; i < 50; i++)
            {
                motifs.Tick(1f);
            }

            Assert.AreEqual(0, fired, "A motif fired despite its recognition gate never being met.");
            Assert.Greater(motifs.DreamClock, 40f, "The dream clock did not advance.");
        }

        [Test]
        public void AHandlerMayReRequestMotifsWithoutCorruptingTheTable()
        {
            var motifs = new TriangleMotifSystem();
            var fired = new List<string>();
            motifs.OnMotif += id =>
            {
                fired.Add(id);
                if (id == "First")
                {
                    // Mutating the table from inside a handler must be safe.
                    motifs.RequestMotif("Second");
                }
            };

            // "First" comes due in 30 s. "Second" is given a very long window so it can
            // only ever fire because the handler asked for it, which keeps the
            // assertion unambiguous.
            motifs.RegisterMotif("First", new TriangleMotifConfig(30f, 30f, 0f));
            motifs.RegisterMotif("Second", new TriangleMotifConfig(600f, 600f, 0f));
            motifs.EnableAutomaticScheduling(true);

            // Registering schedules each motif a full window out, so nothing is due yet.
            motifs.Tick(1f);
            Assert.AreEqual(0, fired.Count, "A motif fired before its first window elapsed.");

            // Advance to the point where "First" comes due. Its handler re-requests
            // "Second", which fires immediately and reschedules itself.
            for (int i = 0; i < 31; i++)
            {
                motifs.Tick(1f);
            }

            Assert.AreEqual(2, fired.Count, "The due motif and the handler-requested motif should both have fired.");
            Assert.AreEqual("First", fired[0]);
            Assert.AreEqual("Second", fired[1]);

            // Both were rescheduled a window out by their own dispatch, so the table
            // mutation from inside the handler must not have left either due again.
            for (int i = 0; i < 10; i++)
            {
                motifs.Tick(1f);
            }

            Assert.AreEqual(2, fired.Count, "A motif fired twice inside its cooldown window.");
        }
    }
}
