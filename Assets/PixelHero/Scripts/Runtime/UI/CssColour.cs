using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PixelHero.UI
{
    /// <summary>
    /// The CSS colour strings the original uses on canvases and in styles: #rgb, #rgba, #rrggbb, #rrggbbaa,
    /// rgb()/rgba() (comma or space separated, alpha as a number or a percentage) and basic named colours.
    /// Channel values are kept as bytes, the way a canvas stores them.
    /// </summary>
    public static class CssColour
    {
        static readonly Dictionary<string, Color32> Named = new Dictionary<string, Color32>(StringComparer.OrdinalIgnoreCase)
        {
            ["white"] = new Color32(255, 255, 255, 255), ["black"] = new Color32(0, 0, 0, 255),
            ["red"] = new Color32(255, 0, 0, 255), ["blue"] = new Color32(0, 0, 255, 255),
            ["green"] = new Color32(0, 128, 0, 255), ["yellow"] = new Color32(255, 255, 0, 255),
            ["orange"] = new Color32(255, 165, 0, 255), ["purple"] = new Color32(128, 0, 128, 255),
            ["gray"] = new Color32(128, 128, 128, 255), ["grey"] = new Color32(128, 128, 128, 255),
            ["transparent"] = new Color32(0, 0, 0, 0),
            // CSS named colours used by the server's messages (and other common ones).
            ["crimson"] = Rgb(220, 20, 60), ["aquamarine"] = Rgb(127, 255, 212), ["goldenrod"] = Rgb(218, 165, 32),
            ["darkblue"] = Rgb(0, 0, 139), ["lime"] = Rgb(0, 255, 0), ["cyan"] = Rgb(0, 255, 255), ["aqua"] = Rgb(0, 255, 255),
            ["magenta"] = Rgb(255, 0, 255), ["fuchsia"] = Rgb(255, 0, 255), ["pink"] = Rgb(255, 192, 203), ["gold"] = Rgb(255, 215, 0),
            ["silver"] = Rgb(192, 192, 192), ["brown"] = Rgb(165, 42, 42), ["navy"] = Rgb(0, 0, 128), ["teal"] = Rgb(0, 128, 128),
            ["olive"] = Rgb(128, 128, 0), ["maroon"] = Rgb(128, 0, 0), ["violet"] = Rgb(238, 130, 238), ["indigo"] = Rgb(75, 0, 130),
            ["tomato"] = Rgb(255, 99, 71), ["orchid"] = Rgb(218, 112, 214), ["salmon"] = Rgb(250, 128, 114), ["khaki"] = Rgb(240, 230, 140),
            ["coral"] = Rgb(255, 127, 80), ["turquoise"] = Rgb(64, 224, 208), ["lightgreen"] = Rgb(144, 238, 144),
            ["lightblue"] = Rgb(173, 216, 230), ["darkred"] = Rgb(139, 0, 0), ["darkgreen"] = Rgb(0, 100, 0),
            ["orangered"] = Rgb(255, 69, 0), ["yellowgreen"] = Rgb(154, 205, 50), ["lawngreen"] = Rgb(124, 252, 0),
            ["deepskyblue"] = Rgb(0, 191, 255), ["dodgerblue"] = Rgb(30, 144, 255), ["royalblue"] = Rgb(65, 105, 225),
            ["slategray"] = Rgb(112, 128, 144), ["lightgray"] = Rgb(211, 211, 211), ["darkgray"] = Rgb(169, 169, 169),
            ["beige"] = Rgb(245, 245, 220), ["ivory"] = Rgb(255, 255, 240), ["chocolate"] = Rgb(210, 105, 30),
            ["skyblue"] = Rgb(135, 206, 235), ["limegreen"] = Rgb(50, 205, 50), ["firebrick"] = Rgb(178, 34, 34),
            ["darkorange"] = Rgb(255, 140, 0), ["springgreen"] = Rgb(0, 255, 127), ["mediumpurple"] = Rgb(147, 112, 219)
        };

        static Color32 Rgb(byte r, byte g, byte b) => new Color32(r, g, b, 255);

        static readonly Regex Function = new Regex(@"^\s*rgba?\(\s*([^)]*)\)\s*$", RegexOptions.IgnoreCase);

        public static bool TryParse(string text, out Color32 colour)
        {
            colour = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();

            if (Named.TryGetValue(text, out colour)) return true;

            if (text[0] == '#') return TryParseHex(text.Substring(1), out colour);

            var match = Function.Match(text);
            if (!match.Success) return false;

            var parts = match.Groups[1].Value.Split(new[] { ',', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3 && parts.Length != 4) return false;

            var channels = new byte[4];
            channels[3] = 255;
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                var percent = part.EndsWith("%", StringComparison.Ordinal);
                if (!double.TryParse(percent ? part.Substring(0, part.Length - 1) : part, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out var value)) return false;

                // Alpha is 0–1 (or a percentage); colour channels are 0–255 (or a percentage).
                var unit = i == 3 ? (percent ? value / 100 : value) * 255 : percent ? value / 100 * 255 : value;
                channels[i] = (byte)Math.Round(Math.Clamp(unit, 0, 255), MidpointRounding.AwayFromZero);
            }

            colour = new Color32(channels[0], channels[1], channels[2], channels[3]);
            return true;
        }

        /// <summary>The colour, or <paramref name="fallback"/> when the string is not one.</summary>
        public static Color32 Parse(string text, Color32 fallback) => TryParse(text, out var colour) ? colour : fallback;

        static bool TryParseHex(string hex, out Color32 colour)
        {
            colour = default;
            if (hex.Length == 3 || hex.Length == 4)
                hex = string.Concat(Array.ConvertAll(hex.ToCharArray(), c => new string(c, 2)));
            if (hex.Length != 6 && hex.Length != 8) return false;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return false;

            if (hex.Length == 6) value = (value << 8) | 0xff;
            colour = new Color32((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }
    }
}
