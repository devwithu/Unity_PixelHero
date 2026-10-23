using System;
using System.IO;
using PixelHero.Maps;
using PixelHero.Sprites;
using UnityEditor;
using UnityEngine;

namespace PixelHero.Editor
{
    /// <summary>
    /// Renders a window of the map with <see cref="MapView"/> through a camera into a texture: 1 texture pixel per
    /// map pixel, black background, the whole region cache loaded (no server needed). Used by MapRenderTests and
    /// for screenshots to look at.
    ///
    /// Batch: Unity.exe -batchmode -quit -projectPath . -executeMethod PixelHero.Editor.MapSnapshot.RenderBatch
    ///        -snapshot "117 550 32 24" -snapshotOut Snapshots/map.png
    /// </summary>
    public static class MapSnapshot
    {
        public static Texture2D Render(int x0, int y0, int cols, int rows, bool animateTiles = false)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogBuilder.CatalogPath);
            var map = new ClientMap(MapInfo.Parse(catalog.MapJson.text));
            map.LoadRegions(RegionCache.FromGzip(catalog.RegionCacheGzip.bytes).GetAllRegions());

            var tileSize = map.Info.TileSize;
            var root = new GameObject("MapSnapshot");
            var cameraObject = new GameObject("SnapshotCamera");
            var texture = new RenderTexture(cols * tileSize, rows * tileSize, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);

            try
            {
                var view = root.AddComponent<MapView>();
                view.Initialize(map, catalog, () => 0);
                view.Planner.AnimateTiles = animateTiles;
                // Window as canvas.ts sees it: gridX/gridY are the top-left visible tile.
                view.SetWindow(x0, y0, cols, rows);
                view.Tick();

                var camera = cameraObject.AddComponent<Camera>();
                camera.orthographic = true;
                camera.orthographicSize = rows / 2f;
                camera.transform.position = MapCoords.TileCorner(x0 + cols / 2f, y0 + rows / 2f) + new Vector3(0, 0, -10);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.targetTexture = texture;
                camera.Render();

                var previous = RenderTexture.active;
                RenderTexture.active = texture;
                var image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                image.Apply();
                RenderTexture.active = previous;
                camera.targetTexture = null;
                return image;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(cameraObject);
                texture.Release();
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        public static void RenderBatch()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                string Arg(string name, string fallback)
                {
                    var index = Array.IndexOf(args, name);
                    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
                }

                var window = Array.ConvertAll(Arg("-snapshot", "117 550 32 24").Split(' '), int.Parse);
                var output = Arg("-snapshotOut", $"Snapshots/map_{window[0]}_{window[1]}.png");

                var image = Render(window[0], window[1], window[2], window[3], Array.IndexOf(args, "-animate") >= 0);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
                File.WriteAllBytes(output, image.EncodeToPNG());
                Debug.Log($"[MapSnapshot] wrote {output}");
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
