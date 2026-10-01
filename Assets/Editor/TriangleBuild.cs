using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Triangle.EditorTools
{
    /// <summary>
    /// Builds a standalone player, and reports the result as an exit code.
    ///
    /// The build is the only way to catch the class of defect that batchmode tests cannot
    /// see. This project creates every material at runtime and calls URP's shaders by name,
    /// so a variant that only exists because of a runtime keyword is present in the editor
    /// and absent from the player - the editor and the build can disagree completely about
    /// what the game looks like, with nothing but a screenshot to tell you.
    ///
    /// <c>-executeMethod Triangle.EditorTools.TriangleBuild.BuildWindows</c>
    /// </summary>
    public static class TriangleBuild
    {
        private const string OutputPath = "Builds/Windows/Triangle.exe";

        /// <summary>
        /// Runs a build and exits with a non-zero code on failure, so it can gate CI.
        ///
        /// EditorApplication.Exit is called only in batch mode. The earlier version called it
        /// unconditionally, so choosing Build from the menu quit the whole editor.
        /// </summary>
        public static void BuildWindows()
        {
            bool succeeded = Run();
            if (Application.isBatchMode)
            {
                EditorApplication.Exit(succeeded ? 0 : 1);
            }
        }

        [MenuItem("Triangle/Build Windows Player")]
        public static void BuildWindowsFromMenu()
        {
            Run();
        }

        private static bool Run()
        {
            string directory = Path.GetDirectoryName(OutputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new BuildPlayerOptions
            {
                scenes = new[] { TriangleSceneBuilder.ScenePath },
                locationPathName = OutputPath,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,

                // Development builds keep the [Triangle] diagnostics and the generation
                // timings, which are the only way to see how the procedural content is
                // behaving on the target machine rather than in the editor.
                options = BuildOptions.Development
            };

            BuildReport report = BuildPipeline.BuildPlayer(options);
            BuildSummary summary = report.summary;

            if (summary.result == BuildResult.Succeeded)
            {
                Debug.Log(
                    $"[Triangle] Build succeeded: {summary.totalSize / (1024f * 1024f):0.0} MB in " +
                    $"{summary.totalTime.TotalSeconds:0.0}s, {summary.totalErrors} error(s).");
                return true;
            }

            Debug.LogError(
                $"[Triangle] Build {summary.result}: {summary.totalErrors} error(s), " +
                $"{summary.totalWarnings} warning(s).");

            // The individual messages are in the report, not in the summary, and the count
            // alone is useless for diagnosing anything.
            foreach (BuildStep step in report.steps)
            {
                foreach (BuildStepMessage message in step.messages)
                {
                    if (message.type == LogType.Error || message.type == LogType.Exception)
                    {
                        Debug.LogError($"[Triangle] build: {message.content}");
                    }
                }
            }

            return false;
        }
    }
}