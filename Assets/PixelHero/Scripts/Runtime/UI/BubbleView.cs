using System.Collections.Generic;
using System.Linq;
using PixelHero.Entities;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// controllers/bubble.ts and renderer/bubbles/blob.ts: speech bubbles over entities (chat, NPC talk) or at a
    /// fixed spot, kept for their duration (5 s by default; a new text restarts the timer). The bubble is a .bubble
    /// box (0.9em text, 80% white on 90% dark grey, radius 0.5em, max 12em wide) with a triangle under it.
    ///
    /// Positions follow bubble.ts setTo in screen pixels, which are converted to the UI canvas's units (the canvas
    /// is scaled by an integer UI scale, the browser's DOM and canvas share one pixel unit).
    /// </summary>
    public sealed class BubbleView
    {
        const double DefaultZoom = 4; // camera.ts DEFAULT_ZOOM

        sealed class Blob
        {
            public string Instance;
            public RectTransform Root;
            public Text Text;
            public double Time, Duration;
            public bool Static;
            public double X, Y;
        }

        readonly UiKit _kit;
        readonly RectTransform _container;
        readonly Dictionary<string, Blob> _bubbles = new Dictionary<string, Blob>();
        static Sprite _box, _arrow;

        public BubbleView(UiKit kit, RectTransform parent)
        {
            _kit = kit;
            _container = UiKit.Fill(UiKit.Box(parent, "#bubbles"));
            _container.SetAsFirstSibling();
        }

        public IEnumerable<string> Instances => _bubbles.Keys;

        public string TextOf(string instance) => _bubbles.TryGetValue(instance, out var blob) ? blob.Text.text : null;

        /// <summary>bubble.ts create: a new bubble, or a new text (and timer) for an existing one.</summary>
        public void Create(string instance, string messageHtml, double now, double duration = 5000, (double x, double y)? position = null)
        {
            if (_bubbles.TryGetValue(instance, out var bubble))
            {
                bubble.Time = now;
                bubble.Text.text = MessageFormat.ToRichText(messageHtml);
                Layout(bubble);
                return;
            }

            var root = UiKit.Box(_container, instance);
            var background = root.gameObject.AddComponent<Image>();
            background.sprite = Box();
            background.type = Image.Type.Sliced;
            background.color = new Color32(45, 45, 45, 230);
            background.raycastTarget = false;

            var arrow = UiKit.Box(root, "after").gameObject.AddComponent<Image>();
            arrow.sprite = Arrow();
            arrow.color = new Color32(45, 45, 45, 230);
            arrow.raycastTarget = false;

            var text = _kit.Label(root, "p", "", _kit.Body, 0.9f * UiKit.Em, "rgba(255, 255, 255, 0.8)", TextAnchor.UpperCenter);
            text.supportRichText = true;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.text = MessageFormat.ToRichText(messageHtml);

            bubble = new Blob
            {
                Instance = instance, Root = root, Text = text, Time = now, Duration = duration,
                Static = position.HasValue, X = position?.x ?? -1, Y = position?.y ?? -1
            };
            _bubbles[instance] = bubble;
            Layout(bubble);
        }

        /// <summary>Box size from the text: padding 0.5em 0.55em of the 0.9em font, at most 12em wide.</summary>
        static void Layout(Blob bubble)
        {
            var em = 0.9f * UiKit.Em;
            float padX = 0.55f * em, padY = 0.5f * em, maxWidth = 12 * em;
            var text = bubble.Text;
            var settings = text.GetGenerationSettings(new Vector2(maxWidth - 2 * padX, 0));
            var width = Mathf.Min(maxWidth - 2 * padX, text.cachedTextGeneratorForLayout.GetPreferredWidth(text.text, text.GetGenerationSettings(Vector2.zero)) / text.pixelsPerUnit);
            settings = text.GetGenerationSettings(new Vector2(width, 0));
            var height = text.cachedTextGeneratorForLayout.GetPreferredHeight(text.text, settings) / text.pixelsPerUnit;

            var box = new Vector2(Mathf.Ceil(width + 2 * padX), Mathf.Ceil(height + 2 * padY));
            UiKit.TopLeft(bubble.Root);
            bubble.Root.sizeDelta = box;
            UiKit.Place(text.rectTransform, padX, padY, width, height);

            // ::after: border-width 0.75em 0.5em 0, left calc(50% - 0.75em), bottom -0.7em.
            var arrow = (RectTransform)bubble.Root.Find("after");
            UiKit.Place(arrow, box.x / 2 - 0.75f * em, box.y + 0.7f * em - 0.75f * em, UiKit.Em * 0.9f, 0.75f * em);
        }

        /// <summary>bubble.ts setTo: above the entity's tile, centred; clipped to the top of the screen.</summary>
        void SetTo(Blob bubble, double x, double y, Entities.Camera camera, double tileSize, float uiScale)
        {
            bubble.X = x;
            bubble.Y = y;

            var actualTileSize = tileSize * camera.ZoomFactor;
            var scale = DefaultZoom / camera.ZoomFactor; // bubble.ts resize
            var size = bubble.Root.sizeDelta * uiScale; // offsetWidth / offsetHeight in screen pixels
            var relativeX = (x - camera.X) * camera.ZoomFactor;
            var relativeY = (y - camera.Y) * camera.ZoomFactor - actualTileSize;
            var offsetX = size.x / 2 - actualTileSize / 2 - 6;
            var offsetY = size.y * scale;
            var boundaryY = relativeY / camera.ScreenHeight;

            var left = relativeX - offsetX;
            var top = boundaryY < 0.1 ? 0 : relativeY - offsetY;
            bubble.Root.anchoredPosition = new Vector2((float)(left / uiScale), (float)(-top / uiScale));
        }

        /// <summary>bubble.ts update: follow the entities, drop the bubbles whose time is over.</summary>
        public void Update(double now, World world, Entities.Camera camera, float uiScale)
        {
            foreach (var bubble in _bubbles.Values.ToList())
            {
                var entity = world.Entities.Get(bubble.Instance);
                if (entity != null) SetTo(bubble, entity.X, entity.Y, camera, world.TileSize, uiScale);
                if (bubble.Static) SetTo(bubble, bubble.X, bubble.Y, camera, world.TileSize, uiScale);
                if (now - bubble.Time > bubble.Duration) Destroy(bubble);
            }
        }

        public void Clear(string instance)
        {
            if (instance != null && _bubbles.TryGetValue(instance, out var bubble)) Destroy(bubble);
        }

        public void Clean()
        {
            foreach (var bubble in _bubbles.Values.ToList()) Destroy(bubble);
        }

        void Destroy(Blob bubble)
        {
            Object.Destroy(bubble.Root.gameObject);
            _bubbles.Remove(bubble.Instance);
        }

        /// <summary>A white rounded box (radius 8 of 16) as a 9-slice: the 0.5em border radius.</summary>
        static Sprite Box()
        {
            if (_box != null) return _box;
            const int size = 16, radius = 8;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                var distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                texture.SetPixel(x, y, new Color(1, 1, 1, Mathf.Clamp01(radius - distance + 0.5f)));
            }
            texture.Apply();
            return _box = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect,
                new Vector4(radius - 1, radius - 1, radius - 1, radius - 1));
        }

        /// <summary>The ::after triangle (pointing down).</summary>
        static Sprite Arrow()
        {
            if (_arrow != null) return _arrow;
            const int width = 16, height = 12;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                // Row 0 is the tip; the triangle widens to the full width at the top row.
                var half = (y + 0.5f) / height * width / 2;
                var inside = Mathf.Abs(x + 0.5f - width / 2f) <= half;
                texture.SetPixel(x, y, new Color(1, 1, 1, inside ? 1 : 0));
            }
            texture.Apply();
            return _arrow = Sprite.Create(texture, new Rect(0, 0, width, height), new Vector2(0.5f, 0.5f), 100);
        }
    }
}
