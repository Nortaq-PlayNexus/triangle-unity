using Triangle.Audio;
using Triangle.Dream;
using Triangle.Nightmare;
using Triangle.Save;
using Triangle.UI;
using UnityEngine;

namespace Triangle.Core
{
    /// <summary>
    /// The dream's service locator.
    ///
    /// Unreal resolved these through WorldSubsystem / GameInstanceSubsystem and
    /// FTriangleGameplayStatics. Unity has no equivalent engine-wide subsystem
    /// collection, so the bootstrap registers the same singletons here and every
    /// system reaches its peers through this one place. All entries are cleared on
    /// teardown so a play-mode exit cannot leave a stale scene behind.
    /// </summary>
    public static class TriangleRuntime
    {
        public static Player.TrianglePlayer Player;
        public static TriangleHUD Hud;
        public static TriangleGameFlow GameFlow;
        public static TriangleDreamDirector Director;
        public static TriangleAnomalySystem Anomalies;
        public static TriangleAudioDirector Audio;
        public static TriangleMotifSystem Motifs;
        public static TriangleFalseAwakeningManager Wake;
        public static TriangleSaveSystem Save;
        public static TriangleScheduler Scheduler;

        public static bool HasPlayer => Player != null;

        /// <summary>
        /// Clears the locator before the first scene loads.
        ///
        /// A static field survives "Enter Play Mode Options" with domain reload turned
        /// off, which is the default for anyone who ticks that box. The previous
        /// session's Player, Director and GameFlow would then still be referenced here
        /// while their Unity objects had been destroyed, and the first system to read
        /// the locator in that window would be holding a dead reference that still
        /// compares non-null in a plain C# sense.
        ///
        /// SubsystemRegistration is the documented hook for this: it runs after
        /// assemblies are loaded but before any Awake, so nothing has had a chance to
        /// read a stale entry yet.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            Reset();
        }

        public static void Reset()
        {
            Player = null;
            Hud = null;
            GameFlow = null;
            Director = null;
            Anomalies = null;
            Audio = null;
            Motifs = null;
            Wake = null;
            Save = null;
            Scheduler = null;
        }
    }
}
