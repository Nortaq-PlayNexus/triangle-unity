using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Triangle.EditorTools
{
    /// <summary>
    /// Builds a standalone Windows player for playtesting.
    ///
    /// The slice is content-free, so the player is small: every mesh, material and
    /// sound is generated at runtime. A development build is used deliberately so the
    /// run writes a Player.log, which is how a headless test pass confirms the dream
    /// actually assembled on a real machine rather than only in the test runner.
    ///
    /// Callable from the menu or the command line via
    /// <c>-executeMethod Triangle.EditorTools.TrianglePlayerBuilder.BuildWindows</c>.
    /// </summary>
    public static class TrianglePlayerBuilder
    {
        private const string OutputDirectory = "Builds/Windows";
        private const string ExecutableName = "Triangle.exe";

        [MenuItem("Triangle/Build Windows Player")]
        public static void BuildWindows()
        {
            string outputPath = Path.Combine(OutputDirectory, ExecutableName);
            Directory.CreateDirectory(OutputDirectory);

            var options = new BuildPlayerOptions
            {
                // Take the scene list from the build settings, which the scene builder
                // maintains, rather than repeating the path here and letting the two
                // drift apart.
                scenes = EditorBuildSettings.scenes
                    .Where(scene => scene.enabled)
                    .Select(scene => scene.path)
                    .ToArray(),
                locationPathName = outputPath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.Development
            };

            if (options.scenes.Length == 0)
            {
                Debug.LogError("[TriangleBuild] No enabled scenes in Build Settings. Run Triangle > Rebuild Playable Scene first.");
                ExitBatchMode(1);
                return;
            }

            Debug.Log($"[TriangleBuild] Starting Windows build -> {outputPath}");
            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result != BuildResult.Succeeded)
            {
                Debug.LogError(
                    $"[TriangleBuild] FAILED: {summary.result}, {summary.totalErrors} errors, " +
                    $"{summary.totalWarnings} warnings, {summary.totalSize} bytes.");

                foreach (BuildStep step in report.steps)
                {
                    foreach (BuildStepMessage message in step.messages)
                    {
                        if (message.type == LogType.Error || message.type == LogType.Exception)
                        {
                            Debug.LogError($"[TriangleBuild] {message.content}");
                        }
                    }
                }

                ExitBatchMode(1);
                return;
            }

            Debug.Log(
                $"[TriangleBuild] SUCCEEDED: {summary.totalSize} bytes, " +
                $"{summary.totalWarnings} warnings, {summary.totalErrors} errors, " +
                $"{summary.totalTime.TotalSeconds:0.0}s -> {outputPath}");

            ExitBatchMode(0);
        }

        /// <summary>
        /// Closes the editor with a status code, but only when running headless.
        ///
        /// This was called unconditionally on success, so building from the
        /// <c>Triangle/Build Windows Player</c> menu item quit the whole editor out from
        /// under the user. A batchmode invocation still needs the exit code, because
        /// that is how CI reads the result.
        /// </summary>
        private static void ExitBatchMode(int exitCode)
        {
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(exitCode);
            }
        }
    }
}
