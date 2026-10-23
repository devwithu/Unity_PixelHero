using System.Collections.Generic;
using PixelHero.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// The quest arrows of #bubbles: each <see cref="PointerArrow"/> as a .pointer div (4.5rem square,
    /// img/pointer.png) placed by its inline styles.
    /// <list type="bullet">
    /// <item>left / top / right / bottom are canvas pixels, which are screen pixels; the UI is scaled by
    /// <c>uiScale</c>.</item>
    /// <item>The transform (scale, rotate) turns about the centre. A CSS rotation is clockwise.</item>
    /// <item>The blink follows util.ts fadeOut (−0.2 opacity a frame, then display none) and fadeIn (display, +0.1 a
    /// frame), counted in 60 Hz animation frames.</item>
    /// </list>
    /// </summary>
    public sealed class PointerView
    {
        const float Size = 4.5f * UiKit.Em;

        sealed class Arrow
        {
            public RectTransform Root;
            public Image Image;
            public double Opacity = 1;
            public bool Displayed = true;
            public bool LastVisible = true;
        }

        readonly RectTransform _root;
        readonly Sprite _sprite;
        readonly Dictionary<PointerArrow, Arrow> _arrows = new Dictionary<PointerArrow, Arrow>();
        float _frames;

        public PointerView(UiKit kit, RectTransform parent)
        {
            _root = UiKit.Fill(UiKit.Box(parent, "pointers"));
            var texture = kit.Image("pointer");
            _sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100);
        }

        /// <summary>For tests: how many arrows are shown.</summary>
        public int Count => _arrows.Count;

        public void Update(IReadOnlyList<PointerArrow> pointers, float uiScale, float deltaTime)
        {
            foreach (var gone in new List<PointerArrow>(_arrows.Keys))
                if (!Contains(pointers, gone))
                {
                    Object.Destroy(_arrows[gone].Root.gameObject);
                    _arrows.Remove(gone);
                }

            // requestAnimationFrame steps of the fades.
            _frames += deltaTime * 60;
            var steps = (int)_frames;
            _frames -= steps;

            var size = _root.rect.size;
            foreach (var pointer in pointers)
            {
                if (!_arrows.TryGetValue(pointer, out var arrow)) _arrows[pointer] = arrow = Create(pointer);

                Fade(arrow, pointer.Visible, steps);
                Place(arrow, pointer, size, uiScale);
            }
        }

        static bool Contains(IReadOnlyList<PointerArrow> pointers, PointerArrow pointer)
        {
            foreach (var other in pointers)
                if (other == pointer) return true;
            return false;
        }

        Arrow Create(PointerArrow pointer)
        {
            var root = UiKit.Box(_root, $"pointer {pointer.Instance}");
            root.anchorMin = root.anchorMax = new Vector2(0, 1);
            root.pivot = new Vector2(0.5f, 0.5f);
            root.sizeDelta = new Vector2(Size, Size);
            var image = root.gameObject.AddComponent<Image>();
            image.sprite = _sprite;
            image.raycastTarget = false;
            return new Arrow { Root = root, Image = image };
        }

        static void Fade(Arrow arrow, bool visible, int steps)
        {
            if (visible != arrow.LastVisible)
            {
                arrow.LastVisible = visible;
                if (visible) arrow.Displayed = true;
            }

            for (var i = 0; i < steps; i++)
            {
                if (visible)
                {
                    var next = arrow.Opacity + 0.1;
                    if (next <= 1) arrow.Opacity = next;
                }
                else if (arrow.Displayed)
                {
                    var next = arrow.Opacity - 0.2;
                    if (next >= 0) arrow.Opacity = next;
                    else arrow.Displayed = false;
                }
            }

            arrow.Image.enabled = arrow.Displayed;
            arrow.Image.color = new Color(1, 1, 1, (float)arrow.Opacity);
        }

        static void Place(Arrow arrow, PointerArrow pointer, Vector2 container, float uiScale)
        {
            float? Length(string style) =>
                style.EndsWith("px") ? float.Parse(style.Substring(0, style.Length - 2), System.Globalization.CultureInfo.InvariantCulture) / uiScale
                : style == "0" ? 0 : (float?)null;

            var left = Length(pointer.Left);
            var right = Length(pointer.Right);
            var top = Length(pointer.Top);
            var bottom = Length(pointer.Bottom);

            // The box's top-left corner; with neither side set, an absolute box stays at its static position (0).
            var x = left ?? (right.HasValue ? container.x - right.Value - Size : 0);
            var y = top ?? (bottom.HasValue ? container.y - bottom.Value - Size : 0);
            arrow.Root.anchoredPosition = new Vector2(x + Size / 2, -(y + Size / 2));

            var scale = 1f;
            var rotation = 0f;
            foreach (var part in pointer.Transform.Split(' '))
            {
                if (part.StartsWith("scale(")) scale = float.Parse(part.Substring(6, part.Length - 7), System.Globalization.CultureInfo.InvariantCulture);
                if (part.StartsWith("rotate(")) rotation = float.Parse(part.Substring(7, part.Length - 11), System.Globalization.CultureInfo.InvariantCulture);
            }
            arrow.Root.localScale = new Vector3(scale, scale, 1);
            arrow.Root.localEulerAngles = new Vector3(0, 0, -rotation);
        }
    }
}
