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
            Directory.CreateDirectory(Path.Combine(Application.dataPath, "Scenes"));

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var bootstrapObject = new GameObject("TriangleBootstrap");
            bootstrapObject.AddComponent<TriangleBootstrap>();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);

            AddSceneToBuildSettings();
            AssetDatabase.SaveAssets();

            Debug.Log($"[Triangle] Playable scene written to {ScenePath}");
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
