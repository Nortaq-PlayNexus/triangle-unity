using System.Collections.Generic;
using System.IO;
using Triangle.Core;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Triangle.EditorTools
{
    /// <summary>
    /// Creates the single playable scene for the slice.
    ///
    /// The vertical slice is code-driven, so the scene contains exactly one object:
    /// the <see cref="TriangleBootstrap"/> that assembles the dream at runtime. This
    /// is the Unity equivalent of Unreal shipping an almost-empty map whose GameMode
    /// spawns the director and builds the environment.
    ///
    /// Callable from the menu or from the command line via
    /// <c>-executeMethod Triangle.EditorTools.TriangleSceneBuilder.RebuildPlayableScene</c>.
    /// </summary>
    public static class TriangleSceneBuilder
    {
        public const string ScenePath = "Assets/Scenes/Dream.unity";

        [MenuItem("Triangle/Rebuild Playable Scene")]
        public static void RebuildPlayableScene()
        {
            // NewSceneMode.Single discards whatever is open, and the editor prompts to
            // save it when interactive. Without this, picking the menu item with unsaved
            // work queued up a modal nobody read and then threw the work away anyway.
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                TriangleLog.Warn("Playable scene rebuild cancelled; the open scene was left untouched.");
                return;
            }

            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Scenes"));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var bootstrapObject = new GameObject("TriangleBootstrap");
            Undo.RegisterCreatedObjectUndo(bootstrapObject, "Create TriangleBootstrap");
            bootstrapObject.AddComponent<TriangleBootstrap>();

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                TriangleLog.Error($"Playable scene could not be written to {ScenePath}.");
                return;
            }

            AddSceneToBuildSettings();
            AssetDatabase.SaveAssets();

            TriangleLog.Log($"Playable scene written to {ScenePath}");
        }

        [MenuItem("Triangle/Rebuild Playable Scene", true)]
        private static bool ValidateRebuildPlayableScene()
        {
            // Never let this run during a play session: it would replace the live scene
            // out from under the running dream.
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        private static void AddSceneToBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            for (int i = 0; i < scenes.Count; i++)
            {
                if (scenes[i].path == ScenePath)
                {
                    return;
                }
            }

            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }
    }
}
