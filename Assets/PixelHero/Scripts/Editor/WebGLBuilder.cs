using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

namespace PixelHero.Editor
{
    /// <summary>
    /// The WebGL player build (S11): the Game scene, the shaders the port finds by name at run time, gzip with the
    /// decompression fallback (so any static server works; Tools/serve_webgl.mjs also sends the gzip headers).
    /// Build files are named by content hash, so a new index.html never loads a stale cached build, and the PWA template
    /// (Assets/WebGLTemplates/PixelHeroPWA) has a service worker that lets a new deploy through (live.md §9).
    /// Output: Builds/WebGL (not committed).
    /// </summary>
    public static class WebGLBuilder
    {
        public const string OutputPath = "Builds/WebGL";

        /// <summary>Shaders looked up with Shader.Find, which a player only has when they are always included.</summary>
        static readonly string[] RuntimeShaders =
        {
            "PixelHero/CanvasImage", "PixelHero/CanvasLayer", "Universal Render Pipeline/2D/Sprite-Unlit-Default"
        };

        [MenuItem("PixelHero/Build WebGL")]
        public static void Build()
        {
            IncludeRuntimeShaders();

            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Gzip;
            PlayerSettings.WebGL.decompressionFallback = true;
            PlayerSettings.WebGL.dataCaching = true;
            PlayerSettings.WebGL.nameFilesAsHashes = true;
            PlayerSettings.WebGL.template = "PROJECT:PixelHeroPWA";
            PlayerSettings.WebGL.exceptionSupport = WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            PlayerSettings.runInBackground = true;

            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/PixelHero/Scenes/Game.unity" },
                locationPathName = OutputPath,
                target = BuildTarget.WebGL,
                options = BuildOptions.None
            };

            var report = BuildPipeline.BuildPlayer(options);
            var summary = report.summary;
            Debug.Log($"[WebGLBuilder] {summary.result}: {summary.totalSize / (1024 * 1024)} MB, {summary.totalErrors} errors, {summary.totalTime}");
            if (summary.result != BuildResult.Succeeded) throw new Exception($"WebGL build {summary.result}");
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

        static void IncludeRuntimeShaders()
        {
            var settings = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            var serialized = new SerializedObject(settings);
            var list = serialized.FindProperty("m_AlwaysIncludedShaders");

            foreach (var name in RuntimeShaders)
            {
                var shader = Shader.Find(name) ?? throw new FileNotFoundException($"Shader {name} not found");
                var present = Enumerable.Range(0, list.arraySize).Any(i => list.GetArrayElementAtIndex(i).objectReferenceValue == shader);
                if (present) continue;

                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            }

            serialized.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }
    }
}
