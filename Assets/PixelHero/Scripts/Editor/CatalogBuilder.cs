using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PixelHero.Sprites;
using UnityEditor;
using UnityEngine;

namespace PixelHero.Editor
{
    /// <summary>
    /// Rebuilds the <see cref="SpriteCatalog"/> from the assets in Assets/PixelHero/Generated (committed: scenes
    /// reference their GUIDs) and applies their import settings: exact pixels for images, Galmuri fallback for fonts,
    /// streaming music. It also copies the client map from PixelHero-Data/client/maps/map.json. Add, replace or remove
    /// an asset in Generated (with its .meta), then rebuild.
    ///
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod PixelHero.Editor.CatalogBuilder.RebuildBatch -logFile catalog.log
    /// </summary>
    public static class CatalogBuilder
    {
        public const string GeneratedRoot = "Assets/PixelHero/Generated";
        public const string CatalogPath = GeneratedRoot + "/SpriteCatalog.asset";

        static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>The client map (tilesets, animations, version) in PixelHero-Data.</summary>
        static string MapJsonPath => Path.Combine(ProjectRoot, "PixelHero-Data", "client", "maps", "map.json");

        /// <summary>Folders below Generated/img that hold textures.</summary>
        static readonly string[] ImageFolders = { "sprites", "tilesets", "interface", "icons", "flags", "overlays" };

        [MenuItem("PixelHero/Rebuild Sprite Catalog")]
        public static void Rebuild()
        {
            var copied = CopyIfChanged(MapJsonPath, $"{GeneratedRoot}/data/map.json");
            if (copied) AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var images = Images();
            var reimported = 0;
            foreach (var path in images)
                if (ApplyPixelArtSettings(path)) reimported++;

            ApplyFontSettings();
            ApplyAudioSettings();
            BuildCatalog(images);
            AssetDatabase.SaveAssets();

            Debug.Log($"[CatalogBuilder] {images.Count} images ({reimported} reimported with pixel-art settings, map.json {(copied ? "updated" : "unchanged")}); catalog {CatalogPath}");
        }

        public static void RebuildBatch()
        {
            try
            {
                Rebuild();
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>The images in the catalog's order: each image folder (recursively), then the images directly in img/.</summary>
        static List<string> Images()
        {
            var images = new List<string>();
            var img = Path.Combine(ProjectRoot, GeneratedRoot, "img");

            foreach (var folder in ImageFolders)
            {
                var root = Path.Combine(img, folder);
                if (!Directory.Exists(root)) continue;
                images.AddRange(Directory.GetFiles(root, "*.png", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Select(AssetPath));
            }

            // Images directly in img/ (the intro background tile, cursors).
            images.AddRange(Directory.GetFiles(img, "*.png").OrderBy(f => f, StringComparer.Ordinal).Select(AssetPath));
            return images;
        }

        static string AssetPath(string file) => Path.GetRelativePath(ProjectRoot, file).Replace('\\', '/');

        public const string FontsRoot = GeneratedRoot + "/fonts";
        public const string AudioRoot = GeneratedRoot + "/audio";
        static readonly string[] AudioFolders = { "music", "sounds" };

        static IEnumerable<string> AudioFiles(string folder) =>
            Directory.GetFiles(Path.Combine(ProjectRoot, AudioRoot, folder), "*.mp3").OrderBy(f => f, StringComparer.Ordinal)
                .Select(file => $"{AudioRoot}/{folder}/{Path.GetFileName(file)}");

        /// <summary>
        /// Music streams (long tracks, loaded in the background); sounds are decompressed on load so they start at
        /// once. Returns how many were reimported.
        /// </summary>
        static int ApplyAudioSettings()
        {
            var reimported = 0;
            foreach (var folder in AudioFolders)
                foreach (var assetPath in AudioFiles(folder))
                {
                    var importer = (AudioImporter)AssetImporter.GetAtPath(assetPath);
                    var settings = importer.defaultSampleSettings;
                    var music = folder == "music";
                    var loadType = music ? AudioClipLoadType.Streaming : AudioClipLoadType.DecompressOnLoad;
                    if (settings.loadType == loadType && importer.loadInBackground == music) continue;

                    settings.loadType = loadType;
                    importer.defaultSampleSettings = settings;
                    importer.loadInBackground = music;
                    importer.SaveAndReimport();
                    reimported++;
                }
            return reimported;
        }
        public const string GalmuriPath = "Assets/PixelHero/Fonts/Galmuri/Galmuri11.ttf";

        /// <summary>
        /// The game fonts as dynamic fonts that fall back to Galmuri (OFL) for Hangul. Returns how many were
        /// reimported.
        /// </summary>
        static int ApplyFontSettings()
        {
            var galmuri = AssetDatabase.LoadAssetAtPath<Font>(GalmuriPath);
            if (galmuri == null) throw new FileNotFoundException($"{GalmuriPath} is missing");

            var reimported = 0;
            foreach (var file in Directory.GetFiles(Path.Combine(ProjectRoot, FontsRoot), "*.ttf").OrderBy(f => f, StringComparer.Ordinal))
            {
                var assetPath = $"{FontsRoot}/{Path.GetFileName(file)}";
                var importer = (TrueTypeFontImporter)AssetImporter.GetAtPath(assetPath);
                if (importer.fontTextureCase == FontTextureCase.Dynamic && importer.fontReferences.Length == 1 &&
                    importer.fontReferences[0] == galmuri) continue;

                importer.fontTextureCase = FontTextureCase.Dynamic;
                importer.fontReferences = new[] { galmuri };
                importer.SaveAndReimport();
                reimported++;
            }
            return reimported;
        }

        static bool CopyIfChanged(string source, string assetPath)
        {
            var target = Path.Combine(ProjectRoot, assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target));

            if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(source)))
                return false;

            File.Copy(source, target, true);
            return true;
        }

        /// <summary>The gzipped region data (static tiles per region), kept as a committed asset.</summary>
        public const string RegionCachePath = GeneratedRoot + "/data/regions.json.gz.bytes";

        static List<SpriteCatalog.AudioEntry> AudioEntries(string folder) =>
            AudioFiles(folder)
                .Select(path => new SpriteCatalog.AudioEntry
                {
                    Name = Path.GetFileNameWithoutExtension(path),
                    Clip = AssetDatabase.LoadAssetAtPath<AudioClip>(path)
                })
                .ToList();

        /// <summary>
        /// Canvas-like sampling: exact pixels (Point, no compression, no mipmaps, no resizing of non-power-of-two
        /// sheets). Returns true when the settings had to change.
        /// </summary>
        static bool ApplyPixelArtSettings(string assetPath)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            if (importer == null) throw new InvalidOperationException($"No texture importer for {assetPath}");

            var changed =
                importer.textureType != TextureImporterType.Default ||
                importer.filterMode != FilterMode.Point ||
                importer.textureCompression != TextureImporterCompression.Uncompressed ||
                importer.mipmapEnabled ||
                importer.npotScale != TextureImporterNPOTScale.None ||
                importer.wrapMode != TextureWrapMode.Clamp ||
                !importer.alphaIsTransparency ||
                importer.isReadable ||
                importer.maxTextureSize < 2048;

            if (!changed) return false;

            importer.textureType = TextureImporterType.Default;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.npotScale = TextureImporterNPOTScale.None;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.alphaIsTransparency = true;
            importer.isReadable = false;
            importer.maxTextureSize = 2048;
            importer.SaveAndReimport();
            return true;
        }

        static void BuildCatalog(List<string> images)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<SpriteCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<SpriteCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
            }

            catalog.SpritesJson = AssetDatabase.LoadAssetAtPath<TextAsset>($"{GeneratedRoot}/data/sprites.json");
            catalog.MapJson = AssetDatabase.LoadAssetAtPath<TextAsset>($"{GeneratedRoot}/data/map.json");
            catalog.RegionCacheGzip = AssetDatabase.LoadAssetAtPath<TextAsset>(RegionCachePath);
            catalog.Textures = images
                .Select(path => new SpriteCatalog.TextureEntry
                {
                    Key = path.Substring($"{GeneratedRoot}/img/".Length, path.Length - $"{GeneratedRoot}/img/".Length - ".png".Length),
                    Texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path)
                })
                .ToList();
            catalog.Fonts = Directory.GetFiles(Path.Combine(ProjectRoot, FontsRoot), "*.ttf")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new SpriteCatalog.FontEntry
                {
                    Name = name,
                    Font = AssetDatabase.LoadAssetAtPath<Font>($"{FontsRoot}/{name}.ttf")
                })
                .ToList();
            catalog.Music = AudioEntries("music");
            catalog.Sounds = AudioEntries("sounds");
            // Galmuri (OFL) for Hangul: the fallback of the game fonts, and the font of Korean UI text.
            foreach (var name in new[] { "Galmuri11", "Galmuri11-Bold", "Galmuri9" })
                catalog.Fonts.Add(new SpriteCatalog.FontEntry
                {
                    Name = name.ToLowerInvariant(),
                    Font = AssetDatabase.LoadAssetAtPath<Font>($"Assets/PixelHero/Fonts/Galmuri/{name}.ttf")
                });

            EditorUtility.SetDirty(catalog);
        }
    }
}
