using Triangle.Core;
using UnityEditor;
using UnityEngine;
namespace Triangle.EditorTools
{
    /// <summary>
    /// Editor toggles for the dream's own diagnostics.
    ///
    /// <see cref="TriangleLog.VerboseLoggingEnabled"/> is off by default, which is what
    /// keeps a headless test log readable. These menu items are how a developer turns it
    /// back on, rather than the default being on and a flag to turn it off.
    /// </summary>
    public static class TriangleDebugMenu
    {
        private const string Menu = "Triangle/";

        // Environment jumping, deliberately separate from the game's own progression.
        // The bedroom door reaches the station and nothing else, so without this there
        // is no way to inspect a new environment except by editing the bootstrap's start
        // field, which is a scene change and a save.
        [MenuItem(Menu + "Jump To/Bedroom", false, 100)]
        private static void JumpToBedroom() => JumpTo(TriangleDreamEnvironment.ChildhoodBedroom, "ChildhoodBedroom");

        [MenuItem(Menu + "Jump To/Station", false, 100)]
        private static void JumpToStation() => JumpTo(TriangleDreamEnvironment.UndergroundStation, "UndergroundStation");

        [MenuItem(Menu + "Jump To/Yard", false, 100)]
        private static void JumpToYard() => JumpTo(TriangleDreamEnvironment.YardOutside, "YardOutside");

        private static void JumpTo(TriangleDreamEnvironment environment, string label)
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[Triangle] Enter play mode first; the dream does not exist in edit mode.");
                return;
            }

            if (TriangleRuntime.GameFlow == null)
            {
                Debug.LogError("[Triangle] No game flow is registered. Is the scene holding a TriangleBootstrap?");
                return;
            }

            // Immediate rather than a fade. This is a developer shortcut, and waiting out
            // a transition to look at something would be pointless.
            bool ok = TriangleRuntime.GameFlow.EnsureEnvironment(environment);
            Debug.Log($"[Triangle] Jump to {label}: {(ok ? "ok" : "FAILED")}.");
        }

        // The label carries the current state rather than a tick. Unity has no public API
        // for a checked menu item - Menu.SetChecked is internal - and a label that states
        // the value is legible at a glance anyway.
        [MenuItem(Menu + "Toggle Verbose Logging", false, 200)]
        private static void ToggleVerboseLogging()
        {
            TriangleLog.VerboseLoggingEnabled = !TriangleLog.VerboseLoggingEnabled;
            Debug.Log($"[Triangle] Verbose logging {(TriangleLog.VerboseLoggingEnabled ? "enabled" : "disabled")}.");
        }

        [MenuItem(Menu + "Report Runtime State", false, 201)]
        private static void ReportRuntimeState()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[Triangle] Runtime state is only meaningful while playing.");
                return;
            }

            Debug.Log(
                "[Triangle] state report\n" +
                $"  environment : {TriangleRuntime.GameFlow?.CurrentEnvironmentId ?? "(none)"}\n" +
                $"  transitioning: {TriangleRuntime.GameFlow?.IsTransitioning.ToString() ?? "-"}\n" +
                $"  presence    : {TriangleRuntime.Director?.TrianglePresence:0.00}\n" +
                $"  lucidity    : {TriangleRuntime.Player?.Lucidity.GetLucidity():0.0} " +
                $"({TriangleRuntime.Player?.Lucidity.GetLucidityLevel()})\n" +
                $"  recognition : {TriangleRuntime.Player?.Recognition.GetRecognition():0.0} " +
                $"({TriangleRuntime.Player?.Recognition.GetRecognitionLevel()})\n" +
                $"  patterns    : {TriangleRuntime.Player?.Recognition.CatalogueCount ?? 0}\n" +
                $"  wake layer  : {TriangleRuntime.Wake?.CurrentLayer ?? 0}\n" +
                $"  hum         : {TriangleRuntime.Audio?.GetHumPresence():0.00}\n" +
                $"  bed         : {TriangleRuntime.Audio?.CurrentState} @ {TriangleRuntime.Audio?.CurrentVolume:0.00}\n" +
                $"  motifs due  : {CountScheduledMotifs()}\n" +
                $"  textures    : {Triangle.World.TriangleProceduralTextures.TotalGeneratedMilliseconds:0.0} ms generated");
        }

        private static string CountScheduledMotifs()
        {
            // The motif system's registry is private by design; the scheduler's pending
            // count is the honest, public measure of deferred work outstanding.
            return TriangleRuntime.Scheduler != null
                ? $"{TriangleRuntime.Scheduler.PendingCount} pending callback(s)"
                : "(no scheduler)";
        }
    }
}
