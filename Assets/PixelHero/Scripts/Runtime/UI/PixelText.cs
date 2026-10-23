using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// A uGUI Text that keeps the original's font for Latin text and switches to Galmuri for text with Hangul.
    /// The original fonts have no Hangul, and as a fallback Galmuri comes out at the original font's odd sizes, so a
    /// Korean line is drawn wholly in Galmuri at a whole multiple of its 12px design size (16px CSS → 12, 28px → 24).
    /// </summary>
    public sealed class PixelText : Text
    {
        public static Font Korean, KoreanBold;

        public Font OriginalFont;
        public float CssSize = 16;
        public bool Bold;

        public override string text
        {
            get => base.text;
            set
            {
                Choose(value);
                base.text = value;
            }
        }

        public static bool HasHangul(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (var c in value)
                if ((c >= '가' && c <= '힣') || (c >= 'ᄀ' && c <= 'ᇿ') || (c >= '㄰' && c <= '㆏'))
                    return true;
            return false;
        }

        /// <summary>Galmuri11's size for a CSS size: 12px steps, at least 12.</summary>
        public static int KoreanSize(float cssSize) => 12 * Mathf.Max(1, Mathf.RoundToInt(cssSize / UiKit.Em));

        void Choose(string value)
        {
            if (OriginalFont == null) return;

            var korean = HasHangul(value) && Korean != null;
            var target = korean ? (Bold && KoreanBold != null ? KoreanBold : Korean) : OriginalFont;
            var size = korean ? KoreanSize(CssSize) : Mathf.RoundToInt(CssSize);
            if (font != target) font = target;
            if (fontSize != size) fontSize = size;
            UiKit.Sharpen(target);
        }
    }
}
