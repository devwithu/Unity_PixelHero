using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// A .slice-container window (scss/game/impl/_slice.scss): container.png with a 1em border, centred on the
    /// screen (inset 0, margin auto), 32em × 16em by default, padding 1em; with the .close-container button
    /// (guilds/elements 11×11 at 64,0, 2×; hover and active 12px apart) 0.25em outside the top-right corner.
    /// </summary>
    public abstract class ContainerWindow : MenuPanel
    {
        protected readonly UiKit Kit;

        /// <summary>The padded area, top-left based.</summary>
        protected readonly RectTransform Content;
        protected readonly float ContentWidth, ContentHeight;

        protected ContainerWindow(UiKit kit, RectTransform parent, string id, float width = 32 * UiKit.Em, float height = 16 * UiKit.Em)
            : base(Frame(kit, parent, id, width, height))
        {
            Kit = kit;
            ContentWidth = width - 2 * UiKit.Em;
            ContentHeight = height - 2 * UiKit.Em;
            Content = UiKit.Box(Root, "content");
            UiKit.Place(Content, UiKit.Em, UiKit.Em, ContentWidth, ContentHeight);

            Sprite Close(int frame) => kit.Region("guilds/elements", 64 + frame * 12, 0, 11, 11);
            var close = UiKit.Box(Root, $"#close-{id.TrimStart('#')}").gameObject.AddComponent<Image>();
            close.sprite = Close(0);
            var rect = close.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1);
            rect.anchoredPosition = new Vector2(0.25f * UiKit.Em, 0.25f * UiKit.Em);
            rect.sizeDelta = new Vector2(22, 22);
            var button = close.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Close(1), pressedSprite = Close(2), selectedSprite = Close(0) };
            button.onClick.AddListener(Hide);
        }

        static RectTransform Frame(UiKit kit, RectTransform parent, string id, float width, float height)
        {
            var frame = kit.Sliced(parent, id, "container", UiKit.Em);
            frame.raycastTarget = true;
            return UiKit.Centre(frame.rectTransform, width, height);
        }
    }

    /// <summary>
    /// A vertical list of variable-height text blocks, as normal HTML flow inside a fixed-width box: each block is
    /// placed under the previous one with its bottom margin, and the total height is kept for scrolling.
    /// </summary>
    public sealed class TextFlow
    {
        readonly UiKit _kit;
        readonly RectTransform _parent;
        readonly float _width;
        readonly List<GameObject> _items = new List<GameObject>();

        public float Height { get; private set; }

        public TextFlow(UiKit kit, RectTransform parent, float width)
        {
            _kit = kit;
            _parent = parent;
            _width = width;
        }

        public void Clear()
        {
            foreach (var item in _items) Object.Destroy(item);
            _items.Clear();
            Height = 0;
        }

        /// <summary>Adds a wrapped text block (rich text from <see cref="MessageFormat.ToRichText"/>).</summary>
        public Text Add(string richText, Font font, float size, string colour, bool stroke = false, float marginBottom = 0,
            TextAnchor alignment = TextAnchor.UpperLeft)
        {
            var text = _kit.Label(_parent, "p", "", font, size, colour, alignment, stroke);
            text.supportRichText = true;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.text = richText;
            var height = UiKit.Height(text, _width);
            UiKit.Place(text.rectTransform, 0, Height, _width, height);
            Height += height + marginBottom;
            _items.Add(text.gameObject);
            return text;
        }
    }
}
