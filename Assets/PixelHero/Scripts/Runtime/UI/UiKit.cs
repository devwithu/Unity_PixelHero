using System;
using System.Collections.Generic;
using PixelHero.Sprites;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// Builds the original HTML/CSS screens with uGUI in code. Sizes are the original CSS pixels at its smallest
    /// font scale (1em = 16px); the canvas multiplies them by an integer UI scale so pixel art stays sharp.
    /// Positions are top-left based (y down), like CSS.
    /// </summary>
    public sealed class UiKit
    {
        public const float Em = 16;

        static readonly Dictionary<string, float> SlicePercent = new Dictionary<string, float>
        {
            ["input"] = 25, ["input-active"] = 25, ["input-error"] = 25, ["inner-container"] = 42, ["dialog"] = 42
        };

        readonly SpriteCatalog _catalog;
        readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();

        public UiKit(SpriteCatalog catalog)
        {
            _catalog = catalog;
            PixelText.Korean = catalog.GetFont("galmuri11");
            PixelText.KoreanBold = catalog.GetFont("galmuri11-bold");
        }

        static readonly HashSet<Font> Sharpened = new HashSet<Font>();

        /// <summary>Pixel fonts: sample the glyph atlas without smoothing, also after it is rebuilt.</summary>
        public static void Sharpen(Font font)
        {
            if (font == null) return;
            if (font.material != null && font.material.mainTexture != null) font.material.mainTexture.filterMode = FilterMode.Point;
            if (!Sharpened.Add(font)) return;
            Font.textureRebuilt += rebuilt =>
            {
                if (rebuilt == font && font.material != null && font.material.mainTexture != null)
                    font.material.mainTexture.filterMode = FilterMode.Point;
            };
        }

        public SpriteCatalog Catalog => _catalog;

        /// <summary>
        /// The original scales the whole UI with its font size: 16px up to 1000px wide, then 24px and 30px. The
        /// port uses whole multiples of the 16px layout: 1, or 2 on screens that fit the 2× parchment.
        /// </summary>
        public static int ScaleFor(int width, int height) => width > 1000 && height > 640 ? 2 : 1;

        public Font Body => _catalog.GetFont("monogram");
        public Font Heading => _catalog.GetFont("fibberish");

        // ---------------------------------------------------------------- sprites

        Texture2D Texture(string key)
        {
            var texture = _catalog.GetTexture(key);
            if (texture == null) throw new ArgumentException($"no image {key} in the catalog");
            return texture;
        }

        /// <summary>A 9-slice sprite of <c>img/interface/slices/&lt;name&gt;.png</c> (border-image-slice in percent).</summary>
        public Sprite Slice(string name)
        {
            var key = "interface/slices/" + name;
            if (_sprites.TryGetValue(key, out var sprite)) return sprite;

            var texture = Texture(key);
            var percent = SlicePercent.TryGetValue(name, out var p) ? p : 44;
            var x = Mathf.Round(texture.width * percent / 100);
            var y = Mathf.Round(texture.height * percent / 100);
            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100, 0,
                SpriteMeshType.FullRect, new Vector4(x, y, x, y));
            sprite.name = name;
            return _sprites[key] = sprite;
        }

        /// <summary>A region of an interface sheet, in the sheet's pixels from the top-left (CSS background-position).</summary>
        public Sprite Region(string image, int x, int y, int width, int height)
        {
            var key = $"{image}@{x},{y},{width},{height}";
            if (_sprites.TryGetValue(key, out var sprite)) return sprite;

            var texture = Texture("interface/" + image);
            sprite = Sprite.Create(texture, new Rect(x, texture.height - y - height, width, height), new Vector2(0.5f, 0.5f), 100);
            sprite.name = key;
            return _sprites[key] = sprite;
        }

        public Texture2D Image(string key) => Texture(key);

        /// <summary>An item's icon: util.ts getImageURL (the last block of the key, under sprites/items); null if missing.</summary>
        public Sprite ItemSprite(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            var path = "sprites/items/" + Util.ItemImageKey(key);
            if (_sprites.TryGetValue(path, out var sprite)) return sprite;
            var texture = _catalog.GetTexture(path);
            sprite = texture == null ? null : Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100);
            return _sprites[path] = sprite;
        }

        // ---------------------------------------------------------------- layout

        public static RectTransform Box(Transform parent, string name)
        {
            var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            TopLeft(rect);
            return rect;
        }

        public static void TopLeft(RectTransform rect)
        {
            rect.anchorMin = rect.anchorMax = new Vector2(0, 1);
            rect.pivot = new Vector2(0, 1);
        }

        /// <summary>CSS-style placement in the parent: left/top offsets and size, y down.</summary>
        public static RectTransform Place(RectTransform rect, float left, float top, float width, float height)
        {
            TopLeft(rect);
            rect.anchoredPosition = new Vector2(left, -top);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        public static RectTransform Fill(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
            return rect;
        }

        /// <summary>Centred in the parent (CSS inset 0 + margin auto), then moved by the offset.</summary>
        public static RectTransform Centre(RectTransform rect, float width, float height, float offsetX = 0, float offsetY = 0)
        {
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = new Vector2(offsetX, -offsetY);
            rect.sizeDelta = new Vector2(width, height);
            return rect;
        }

        // ---------------------------------------------------------------- elements

        /// <summary>A border-image box: the slice drawn with its border at <paramref name="border"/> CSS pixels.</summary>
        public Image Sliced(Transform parent, string name, string slice, float border)
        {
            var image = Box(parent, name).gameObject.AddComponent<Image>();
            SetSlice(image, slice, border);
            image.raycastTarget = false;
            return image;
        }

        public void SetSlice(Image image, string slice, float border)
        {
            var sprite = Slice(slice);
            image.sprite = sprite;
            image.type = UnityEngine.UI.Image.Type.Sliced;
            image.fillCenter = true;
            image.pixelsPerUnitMultiplier = sprite.border.x / border;
        }

        public static Color Css(string colour) => CssColour.Parse(colour, new Color32(255, 255, 255, 255));

        /// <summary>A line of text; <paramref name="stroke"/> adds the original's <c>.stroke</c> outline.</summary>
        public Text Label(Transform parent, string name, string text, Font font, float size, string colour,
            TextAnchor alignment = TextAnchor.UpperLeft, bool stroke = false)
        {
            var label = Box(parent, name).gameObject.AddComponent<PixelText>();
            label.OriginalFont = font ?? Body;
            label.CssSize = size;
            label.Bold = font != null && font == Heading;
            label.font = label.OriginalFont;
            label.fontSize = Mathf.RoundToInt(size);
            Sharpen(label.font);
            label.color = Css(colour);
            label.alignment = alignment;
            label.text = text;
            label.supportRichText = false;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.lineSpacing = 1;
            label.raycastTarget = false;
            if (stroke) label.gameObject.AddComponent<PixelStroke>();
            return label;
        }

        /// <summary><c>.slice-button</c>: button.png with hover and active images, a stroked label.</summary>
        public Button Button(Transform parent, string name, string text, Action onClick)
        {
            var image = Sliced(parent, name, "button", Em);
            image.raycastTarget = true;
            var button = image.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState
            {
                highlightedSprite = Slice("button-hover"),
                selectedSprite = Slice("button"),
                pressedSprite = Slice("button-active"),
                disabledSprite = Slice("button")
            };
            button.onClick.AddListener(() => onClick?.Invoke());

            var label = Label(image.transform, "label", text, Body, Em, "#eee", TextAnchor.MiddleCenter, true);
            Fill(label.rectTransform);
            return button;
        }

        /// <summary><c>.slice-input</c>: input.png (input-active.png while focused), padding 0.625em 0.75em.</summary>
        public InputField Input(Transform parent, string name, string placeholder, float fontSize, bool password, int maxLength)
        {
            var image = Sliced(parent, name, "input", Em / 2);
            image.raycastTarget = true;
            var field = image.gameObject.AddComponent<InputField>();
            field.targetGraphic = image;
            field.transition = Selectable.Transition.SpriteSwap;
            field.spriteState = new SpriteState
            {
                highlightedSprite = Slice("input"),
                selectedSprite = Slice("input-active"),
                pressedSprite = Slice("input-active"),
                disabledSprite = Slice("input")
            };

            var padX = 0.75f * fontSize;
            var area = Box(image.transform, "text area");
            area.anchorMin = Vector2.zero;
            area.anchorMax = Vector2.one;
            area.offsetMin = new Vector2(padX, 0);
            area.offsetMax = new Vector2(-padX, 0);

            var text = Label(area, "text", "", Body, fontSize, "#eee", TextAnchor.MiddleLeft, true);
            Fill(text.rectTransform);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            var hint = Label(area, "placeholder", placeholder, Body, fontSize, "#757575", TextAnchor.MiddleLeft);
            Fill(hint.rectTransform);

            field.textComponent = text;
            field.placeholder = hint;
            field.characterLimit = maxLength;
            field.lineType = InputField.LineType.SingleLine;
            field.contentType = password ? InputField.ContentType.Password : InputField.ContentType.Standard;
            field.caretColor = Css("#eee");
            field.customCaretColor = true;
            return field;
        }

        /// <summary>
        /// <c>.check</c>: the checkmark from characterdialogsheet.png (8×8 at 109,240; hover, active, checked and
        /// checked-hover frames 9px apart), 2× scale, then the label 0.5em to its right.
        /// </summary>
        public Toggle Check(Transform parent, string name, string text, out Text label)
        {
            var root = Box(parent, name);
            var box = Box(root, "checkmark").gameObject.AddComponent<Image>();
            Place(box.rectTransform, 0, 0, Em, Em);
            box.sprite = Region("characterdialogsheet", 109, 240, 8, 8);

            var toggle = root.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = box;
            toggle.transition = Selectable.Transition.SpriteSwap;
            toggle.spriteState = new SpriteState
            {
                highlightedSprite = Region("characterdialogsheet", 118, 240, 8, 8),
                pressedSprite = Region("characterdialogsheet", 127, 240, 8, 8),
                selectedSprite = Region("characterdialogsheet", 109, 240, 8, 8)
            };

            var checkedMark = Box(box.transform, "checked").gameObject.AddComponent<Image>();
            Fill(checkedMark.rectTransform);
            checkedMark.sprite = Region("characterdialogsheet", 136, 240, 8, 8);
            checkedMark.raycastTarget = false;
            toggle.graphic = checkedMark;
            toggle.isOn = false;

            label = Label(root, "label", text, Body, Em, "#eee", TextAnchor.MiddleLeft, true);
            Place(label.rectTransform, Em * 1.5f, 0, 0, Em);
            // The whole row is clickable, like the original's invisible checkbox over the label.
            var hit = root.gameObject.AddComponent<Image>();
            hit.color = Color.clear;
            return toggle;
        }

        /// <summary>Clickable text (links and footer items): <paramref name="hover"/> colour under the pointer.</summary>
        public Text Link(Transform parent, string name, string text, float size, string colour, string hover, Action onClick)
        {
            var label = Label(parent, name, text, Body, size, colour);
            label.raycastTarget = true;
            var link = label.gameObject.AddComponent<HoverLink>();
            link.Normal = Css(colour);
            link.Hover = Css(hover);
            link.Clicked = onClick;
            return label;
        }

        /// <summary>The width of a single line of text in canvas units.</summary>
        public static float Width(Text text)
        {
            var settings = text.GetGenerationSettings(Vector2.zero);
            return text.cachedTextGeneratorForLayout.GetPreferredWidth(text.text, settings) / text.pixelsPerUnit;
        }

        /// <summary>The height of wrapped text at a width, in canvas units (line-height 1 fonts).</summary>
        public static float Height(Text text, float width)
        {
            var settings = text.GetGenerationSettings(new Vector2(width, 0));
            return text.cachedTextGeneratorForLayout.GetPreferredHeight(text.text, settings) / text.pixelsPerUnit;
        }

        /// <summary>
        /// overflow: auto: a clipped viewport filling <paramref name="parent"/> that scrolls its content vertically
        /// with the mouse wheel. The content is top-anchored; set its height to what it holds.
        /// </summary>
        public static RectTransform ScrollView(RectTransform parent, string name)
        {
            var viewport = Fill(Box(parent, name));
            viewport.gameObject.AddComponent<RectMask2D>();
            // A transparent graphic so that the wheel and drags over empty space reach the ScrollRect.
            viewport.gameObject.AddComponent<Image>().color = Color.clear;

            var content = Box(viewport, "content");
            content.anchorMin = new Vector2(0, 1);
            content.anchorMax = new Vector2(1, 1);
            content.pivot = new Vector2(0.5f, 1);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = Vector2.zero;

            var scroll = viewport.gameObject.AddComponent<ScrollRect>();
            scroll.viewport = viewport;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = Em;
            scroll.inertia = false;
            return content;
        }

        /// <summary>An EventSystem with the Input System module, once per scene.</summary>
        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null)
            {
                if (!EventSystem.current.TryGetComponent<WebTextInput>(out _)) EventSystem.current.gameObject.AddComponent<WebTextInput>();
                return;
            }
            var go = new GameObject("EventSystem", typeof(EventSystem));
            go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
            go.AddComponent<WebTextInput>();
        }
    }
}
