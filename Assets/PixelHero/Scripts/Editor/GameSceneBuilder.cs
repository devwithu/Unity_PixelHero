using System;
using System.Linq;
using PixelHero.Game;
using PixelHero.Sprites;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace PixelHero.Editor
{
    /// <summary>
    /// Builds Assets/PixelHero/Scenes/Game.unity: an orthographic camera and a <see cref="GameClient"/> wired to the
    /// imported catalog, and makes it the first scene of the build.
    ///
    /// Batch: Unity.exe -batchmode -quit -projectPath . -executeMethod PixelHero.Editor.GameSceneBuilder.BuildBatch -logFile scene.log
    /// </summary>
    public static class GameSceneBuilder
    {
        public const string ScenePath = "Assets/PixelHero/Scenes/Game.unity";

        [MenuItem("PixelHero/Build Game Scene")]
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Main Camera") { tag = "MainCamera" };
            var camera = cameraObject.AddComponent<Camera>();
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.transform.position = new Vector3(0, 0, -10);

            var game = new GameObject("Game").AddComponent<GameClient>();
            game.Catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);
            game.ViewCamera = camera;
            game.Languages = new[] { "ko", "en", "server.ko", "server.en" }
                .Select(language => AssetDatabase.LoadAssetAtPath<TextAsset>($"Assets/PixelHero/Localization/{language}.json"))
                .ToArray();

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ScenePath));
            EditorSceneManager.SaveScene(scene, ScenePath);

            var scenes = EditorBuildSettings.scenes.Where(s => s.path != ScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            Debug.Log($"[GameSceneBuilder] wrote {ScenePath}");
        }

        public static void BuildBatch()
        {
            try
            {
                Build();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
