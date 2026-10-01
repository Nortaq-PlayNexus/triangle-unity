using UnityEngine;

namespace Triangle.Core
{
    /// <summary>
    /// Central log sink. The Unreal module declared LogTriangle / LogTriangleDream /
    /// LogTriangleSave / LogTriangleInteraction categories; the port keeps the same
    /// prefixes so log output stays comparable between engines.
    /// </summary>
    public static class TriangleLog
    {
        private const string Prefix = "[Triangle]";

        public static void Log(string message) => Debug.Log($"{Prefix} {message}");

        public static void Warn(string message) => Debug.LogWarning($"{Prefix} {message}");
        public static void Error(string message) => Debug.LogError($"{Prefix} {message}");

        public static void Dream(string message) => Debug.Log($"{Prefix}[Dream] {message}");

        public static void Save(string message) => Debug.Log($"{Prefix}[Save] {message}");
        public static void SaveWarn(string message) => Debug.LogWarning($"{Prefix}[Save] {message}");

        public static void Interaction(string message) => Debug.Log($"{Prefix}[Interaction] {message}");

        /// <summary>
        /// Diagnostics that are only interesting while actively debugging.
        ///
        /// These used to call Debug.Log, so "verbose" output was identical to ordinary
        /// output and there was no way to mute it - which is why the console fills with
        /// per-manifestation chatter that buries the lines that matter.
        ///
        /// Off unless explicitly switched on. <c>Debug.isDebugBuild</c> is not a usable
        /// gate: it is true everywhere in the editor, including a headless test run, and
        /// several of these fire on the point's show/hide path. A run of the PlayMode
        /// suite produced a 21 MB log that way, which is slow enough to threaten the
        /// run and large enough to make the log unusable for finding the real failure.
        ///
        /// Toggle with the Triangle menu item, or set it from a debugger while chasing
        /// something specific.
        /// </summary>
        public static bool VerboseLoggingEnabled { get; set; }

        public static void Verbose(string message)
        {
            if (VerboseLoggingEnabled)
            {
                Debug.Log($"{Prefix} {message}");
            }
        }

        public static void DreamVerbose(string message)
        {
            if (VerboseLoggingEnabled)
            {
                Debug.Log($"{Prefix}[Dream] {message}");
            }
        }
    }
}
