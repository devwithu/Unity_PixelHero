using System;
using System.Collections.Generic;
using UnityEngine;

namespace PixelHero.Sprites
{
    /// <summary>
    /// The client assets in Assets/PixelHero/Generated/ (PixelHero.Editor.CatalogBuilder writes it): the
    /// data files as text, and the sheet textures by path. Sprite metadata is resolved at load time with
    /// <see cref="SpriteDefinitions"/>, and frames are cut from the sheets at run time like the canvas client does.
    /// </summary>
    public sealed class SpriteCatalog : ScriptableObject
    {
        [Serializable]
        public sealed class TextureEntry
        {
            /// <summary>Path below client/public/img, without ".png" (e.g. "sprites/mobs/rat", "tilesets/tilesheet-1").</summary>
            public string Key;
            public Texture2D Texture;
        }

        /// <summary>client/data/sprites.json.</summary>
        public TextAsset SpritesJson;

        /// <summary>client/data/maps/map.json (tile size, tilesets, high tiles, animated tiles).</summary>
        public TextAsset MapJson;

        /// <summary>
        /// gzip of the original server's cache/regions.json (static region tiles), for PixelHero.Maps.RegionCache.
        /// </summary>
        public TextAsset RegionCacheGzip;

        public List<TextureEntry> Textures = new List<TextureEntry>();

        [Serializable]
        public sealed class FontEntry
        {
            /// <summary>The CSS font-family the original uses (the file name in client/public/fonts, e.g. "quaver").</summary>
            public string Name;
            public Font Font;
        }

        /// <summary>The original fonts (dynamic TTF); each falls back to Galmuri for Hangul.</summary>
        public List<FontEntry> Fonts = new List<FontEntry>();

        public Font GetFont(string name)
        {
            foreach (var entry in Fonts)
                if (string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)) return entry.Font;
            return null;
        }

        [Serializable]
        public sealed class AudioEntry
        {
            /// <summary>The file name in client/public/audio/{music,sounds} without .mp3 (e.g. "hit1").</summary>
            public string Name;
            public AudioClip Clip;
        }

        /// <summary>client/public/audio/music: the songs the Music packet names.</summary>
        public List<AudioEntry> Music = new List<AudioEntry>();

        /// <summary>client/public/audio/sounds: the sound effects.</summary>
        public List<AudioEntry> Sounds = new List<AudioEntry>();

        public AudioClip GetMusic(string name) => Find(Music, name);
        public AudioClip GetSound(string name) => Find(Sounds, name);

        static AudioClip Find(List<AudioEntry> entries, string name)
        {
            foreach (var entry in entries)
                if (entry.Name == name) return entry.Clip;
            return null;
        }

        Dictionary<string, Texture2D> _textures;

        public Texture2D GetTexture(string key)
        {
            if (_textures == null)
            {
                _textures = new Dictionary<string, Texture2D>();
                foreach (var entry in Textures) _textures[entry.Key] = entry.Texture;
            }

            return _textures.TryGetValue(key, out var texture) ? texture : null;
        }

        /// <summary>The sheet of a sprite (null when the original has no image for it).</summary>
        public Texture2D GetSheet(SpriteInfo sprite) => GetTexture("sprites/" + sprite.Key);
    }
}
