using System;
using System.Collections.Generic;
using System.Globalization;
using PixelHero.Entities;
using PixelHero.Sprites;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// #text-canvas: draws a frame's <see cref="TextDraw"/> calls with uGUI on a screen-space canvas in front of the
    /// entity layers, one canvas pixel per screen pixel. Glyphs come from the original's fonts (dynamic TTF, falling
    /// back to Galmuri for Hangul), so text is not pixel-identical to the browser; positions, sizes, colours,
    /// outlines and opacity follow the calls.
    ///
    /// A strokeText followed by the fillText of the same string at the same place (renderer.ts drawText) becomes one
    /// label. The canvas stroke is centred on the glyph edge and the fill covers its inner half; the label draws the
    /// stroke colour in 8 directions under the fill (uGUI Outline leaves gaps in pixel fonts).
    /// </summary>
    public sealed class TextCanvasView : IDisposable
    {
        static readonly Vector2[] OutlineDirections =
        {
            new Vector2(-1, -1), new Vector2(0, -1), new Vector2(1, -1), new Vector2(-1, 0),
            new Vector2(1, 0), new Vector2(-1, 1), new Vector2(0, 1), new Vector2(1, 1)
        };

        sealed class TextLabel
        {
            public RectTransform Root;
            public Text Fill;
            public Text[] Outline;
        }

        sealed class Frame
        {
            public RectTransform Root;
            public Image[] Sides;
        }

        readonly SpriteCatalog _catalog;
        readonly GameObject _host;
        readonly RectTransform _root;
        readonly List<Image> _fills = new List<Image>();
        readonly List<Frame> _frames = new List<Frame>();
        readonly List<TextLabel> _labels = new List<TextLabel>();
        readonly List<RawImage> _images = new List<RawImage>();
        readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();
        int _fillCount, _frameCount, _labelCount, _imageCount, _sibling;

        public TextCanvasView(SpriteCatalog catalog, UnityEngine.Camera camera, int sortingOrder, int width, int height)
        {
            _catalog = catalog;

            _host = new GameObject("#text-canvas", typeof(RectTransform));
            var canvas = _host.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = camera;
            canvas.planeDistance = 1;
            canvas.sortingOrder = sortingOrder;
            canvas.pixelPerfect = true;
            var scaler = _host.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1;

            _root = new GameObject("root", typeof(RectTransform)).GetComponent<RectTransform>();
            _root.SetParent(_host.transform, false);
            TopLeft(_root);
            _root.sizeDelta = new Vector2(width, height);
        }

        /// <summary>Anchored at the canvas's top-left; positions are canvas pixels with y down.</summary>
        static void TopLeft(RectTransform rect, float pivotX = 0)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(pivotX, 1);
        }

        static void Place(RectTransform rect, double x, double y, double width, double height)
        {
            rect.anchoredPosition = new Vector2((float)x, (float)-y);
            rect.sizeDelta = new Vector2((float)Math.Max(0, width), (float)Math.Max(0, height));
        }

        static float Opacity(TextDraw draw) => (float)Math.Clamp(draw.Alpha, 0, 1);

        static Color WithAlpha(Color32 colour, float alpha)
        {
            Color c = colour;
            c.a *= alpha;
            return c;
        }

        public void Render(IReadOnlyList<TextDraw> draws)
        {
            _fillCount = _frameCount = _labelCount = _imageCount = 0;
            _sibling = 0;

            for (var i = 0; i < draws.Count; i++)
            {
                var draw = draws[i];
                switch (draw.Kind)
                {
                    case TextDrawKind.StrokeRect:
                        StrokeRect(draw);
                        break;
                    case TextDrawKind.FillRect:
                        FillRect(draw);
                        break;
                    case TextDrawKind.StrokeText:
                        var fill = i + 1 < draws.Count ? draws[i + 1] : null;
                        if (fill != null && fill.Kind == TextDrawKind.FillText && fill.Text == draw.Text && fill.X == draw.X &&
                            fill.Y == draw.Y && fill.E == draw.E && fill.F == draw.F)
                        {
                            Label(fill, draw);
                            i++;
                        }
                        else Label(draw, draw);
                        break;
                    case TextDrawKind.FillText:
                        Label(draw, null);
                        break;
                    case TextDrawKind.DrawImage:
                        Image(draw);
                        break;
                }
            }

            for (var i = _fillCount; i < _fills.Count; i++) _fills[i].gameObject.SetActive(false);
            for (var i = _frameCount; i < _frames.Count; i++) _frames[i].Root.gameObject.SetActive(false);
            for (var i = _labelCount; i < _labels.Count; i++) _labels[i].Root.gameObject.SetActive(false);
            for (var i = _imageCount; i < _images.Count; i++) _images[i].gameObject.SetActive(false);
        }

        /// <summary>Activates a pooled element and moves it after the previous call's (later calls draw on top).</summary>
        void Use(Transform element)
        {
            element.gameObject.SetActive(true);
            element.SetSiblingIndex(_sibling++);
        }

        static T Create<T>(string name, Transform parent) where T : Graphic
        {
            var graphic = new GameObject(name, typeof(RectTransform)).AddComponent<T>();
            graphic.raycastTarget = false;
            graphic.transform.SetParent(parent, false);
            TopLeft(graphic.rectTransform);
            return graphic;
        }

        void FillRect(TextDraw draw)
        {
            if (_fillCount == _fills.Count) _fills.Add(Create<Image>("fillRect", _root));
            var image = _fills[_fillCount++];
            Use(image.transform);
            Place(image.rectTransform, draw.X + draw.E, draw.Y + draw.F, draw.W, draw.H);
            image.color = WithAlpha(CssColour.Parse(draw.Colour, new Color32(0, 0, 0, 255)), Opacity(draw));
        }

        /// <summary>A stroke centred on the rect's edges: four bars of the line width.</summary>
        void StrokeRect(TextDraw draw)
        {
            if (_frameCount == _frames.Count)
            {
                var root = new GameObject("strokeRect", typeof(RectTransform)).GetComponent<RectTransform>();
                root.SetParent(_root, false);
                TopLeft(root);
                var sides = new Image[4];
                for (var s = 0; s < 4; s++) sides[s] = Create<Image>("side", root);
                _frames.Add(new Frame { Root = root, Sides = sides });
            }

            var frame = _frames[_frameCount++];
            Use(frame.Root);

            var line = draw.LineWidth;
            double x = draw.X + draw.E - line / 2, y = draw.Y + draw.F - line / 2, w = draw.W + line, h = draw.H + line;
            Place(frame.Root, x, y, w, h);
            Place(frame.Sides[0].rectTransform, 0, 0, w, line);
            Place(frame.Sides[1].rectTransform, 0, h - line, w, line);
            Place(frame.Sides[2].rectTransform, 0, 0, line, h);
            Place(frame.Sides[3].rectTransform, w - line, 0, line, h);

            var colour = WithAlpha(CssColour.Parse(draw.Colour, new Color32(0, 0, 0, 255)), Opacity(draw));
            foreach (var side in frame.Sides) side.color = colour;
        }

        /// <param name="stroke">The strokeText drawn under it (null: fill only; the same draw as fill: stroke only).</param>
        void Label(TextDraw fill, TextDraw stroke)
        {
            if (_labelCount == _labels.Count)
            {
                var root = new GameObject("text", typeof(RectTransform)).GetComponent<RectTransform>();
                root.SetParent(_root, false);
                var outline = new Text[OutlineDirections.Length];
                for (var o = 0; o < outline.Length; o++) outline[o] = Create<Text>("outline", root);
                _labels.Add(new TextLabel { Root = root, Outline = outline, Fill = Create<Text>("fill", root) });
            }

            var label = _labels[_labelCount++];
            Use(label.Root);

            ParseFont(fill.Font, out var size, out var family);
            var font = Font(family);
            var pivotX = fill.TextAlign switch
            {
                "center" => 0.5f,
                "right" or "end" => 1f,
                _ => 0f
            };
            var anchor = pivotX == 0.5f ? TextAnchor.UpperCenter : pivotX == 1f ? TextAnchor.UpperRight : TextAnchor.UpperLeft;

            // fillText's y is the alphabetic baseline; the text's box starts at the font's ascent above it.
            var ascent = font != null && font.ascent > 0 && font.fontSize > 0 ? (double)font.ascent / font.fontSize * size : size * 0.8;
            TopLeft(label.Root, pivotX);
            Place(label.Root, fill.X + fill.E, fill.Y + fill.F - ascent, 0, 0);

            var alpha = Opacity(fill);
            // The minigame lines (renderer.ts drawMinigameGUI) are fixed English sentences; only those are translated.
            var text = Localization.TranslateEnglish(fill.Text, "minigame.") ?? "undefined";
            var strokeOnly = stroke == fill;
            var offset = stroke == null ? 0 : (float)Math.Max(1, Math.Floor(stroke.LineWidth / 2));
            var strokeColour = stroke == null ? default : WithAlpha(CssColour.Parse(stroke.Colour, new Color32(0, 0, 0, 255)), alpha);

            for (var o = 0; o < label.Outline.Length; o++)
            {
                var copy = label.Outline[o];
                copy.gameObject.SetActive(stroke != null);
                if (stroke == null) continue;
                Style(copy, text, font, size, anchor, pivotX, strokeColour);
                copy.rectTransform.anchoredPosition = OutlineDirections[o] * offset;
            }

            label.Fill.gameObject.SetActive(!strokeOnly);
            Style(label.Fill, text, font, size, anchor, pivotX, WithAlpha(CssColour.Parse(fill.Colour, new Color32(255, 255, 255, 255)), alpha));
            label.Fill.rectTransform.anchoredPosition = Vector2.zero;
        }

        static void Style(Text text, string value, Font font, double size, TextAnchor anchor, float pivotX, Color colour)
        {
            text.text = value;
            if (font != null) text.font = font;
            text.fontSize = (int)Math.Round(size);
            text.alignment = anchor;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            text.color = colour;
            var rect = text.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(pivotX, 1);
            rect.pivot = new Vector2(pivotX, 1);
            rect.sizeDelta = Vector2.zero;
        }

        void Image(TextDraw draw)
        {
            if (_imageCount == _images.Count) _images.Add(Create<RawImage>("drawImage", _root));
            var image = _images[_imageCount++];
            Use(image.transform);
            Place(image.rectTransform, draw.X + draw.E, draw.Y + draw.F, draw.W, draw.H);
            image.texture = draw.Sprite != null ? _catalog.GetTexture("sprites/" + draw.Sprite.Key) : null;
            image.color = new Color(1, 1, 1, Opacity(draw));
        }

        /// <summary>"21px Quaver" → 21, "quaver".</summary>
        static void ParseFont(string font, out double size, out string family)
        {
            size = 10;
            family = "";
            if (string.IsNullOrEmpty(font)) return;

            var space = font.IndexOf(' ');
            var sizeText = space < 0 ? font : font.Substring(0, space);
            if (sizeText.EndsWith("px", StringComparison.Ordinal) &&
                double.TryParse(sizeText.Substring(0, sizeText.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
                size = px;
            family = space < 0 ? "" : font.Substring(space + 1).Trim().Trim('"', '\'').ToLowerInvariant();
        }

        Font Font(string family)
        {
            if (_fonts.TryGetValue(family, out var font)) return font;

            font = _catalog.GetFont(family) ?? _catalog.GetFont("quaver");
            if (font != null)
            {
                // Pixel fonts: sample the glyph atlas without smoothing, also after it is rebuilt.
                void Sharpen()
                {
                    if (font.material != null && font.material.mainTexture != null) font.material.mainTexture.filterMode = FilterMode.Point;
                }

                Sharpen();
                UnityEngine.Font.textureRebuilt += rebuilt =>
                {
                    if (rebuilt == font) Sharpen();
                };
            }

            return _fonts[family] = font;
        }

        public void Dispose()
        {
            if (_host != null) UnityEngine.Object.Destroy(_host);
        }
    }
}
