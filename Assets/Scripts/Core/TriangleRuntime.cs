using Triangle.Audio;
using Triangle.Dream;
using Triangle.Nightmare;
using Triangle.Save;
using Triangle.UI;

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
