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
        public static void Verbose(string message) => Debug.Log($"{Prefix} {message}");
        public static void Warn(string message) => Debug.LogWarning($"{Prefix} {message}");
        public static void Error(string message) => Debug.LogError($"{Prefix} {message}");

        public static void Dream(string message) => Debug.Log($"{Prefix}[Dream] {message}");
        public static void DreamVerbose(string message) => Debug.Log($"{Prefix}[Dream] {message}");

        public static void Save(string message) => Debug.Log($"{Prefix}[Save] {message}");
        public static void SaveWarn(string message) => Debug.LogWarning($"{Prefix}[Save] {message}");

        public static void Interaction(string message) => Debug.Log($"{Prefix}[Interaction] {message}");
    }
}
