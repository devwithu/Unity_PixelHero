using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// A list of item slots as util.ts createSlot builds them for the bank, store, enchant and loot windows: the
    /// .item-slot frame (main.png at 0,42, 2×, hover and active frames) with the item image inside and the count
    /// line under it, laid out in rows that fill the width (grid / flex-wrap with a 0.25em gap). Slots can be
    /// clicked, and dragged onto a slot of any grid (the bank moves items that way).
    /// </summary>
    public sealed class SlotGrid
    {
        public const float SlotSize = 2 * UiKit.Em;
        const float CountHeight = 0.8f * UiKit.Em;
        const float Gap = 0.25f * UiKit.Em;

        readonly UiKit _kit;
        readonly RectTransform _content;
        readonly List<(Image icon, Text count, string key)> _slots = new List<(Image, Text, string)>();
        readonly float _width;
        readonly int _columns;

        /// <summary>The container type this grid shows (Modules.ContainerType), for drops between grids.</summary>
        public readonly int Type;

        public event Action<int> Clicked;

        /// <summary>A right click (util.ts createSlot secondaryCallback, onSecondaryPress).</summary>
        public event Action<int> SecondaryClicked;

        /// <summary>(from type, from index, to type, to index) when a slot is dropped onto another slot.</summary>
        public event Action<int, int, int, int> Dropped;

        /// <param name="area">The box the grid fills; it scrolls when the slots do not fit.</param>
        public SlotGrid(UiKit kit, RectTransform area, int count, int type, int columns = 0)
        {
            _kit = kit;
            Type = type;
            _width = area.sizeDelta.x - Gap; // padding-right 0.25em
            _columns = columns > 0 ? columns : Math.Max(1, (int)((_width + Gap) / (SlotSize + Gap)));
            _content = UiKit.ScrollView(area, "ul");
            for (var i = 0; i < count; i++) Add();
        }

        public int Count => _slots.Count;

        float CellWidth => (_width - (_columns - 1) * Gap) / _columns;

        Vector2 Position(int index) =>
            new Vector2(index % _columns * (CellWidth + Gap) + (CellWidth - SlotSize) / 2, index / _columns * (SlotSize + CountHeight + Gap));

        /// <summary>Adds a slot at the end (loot bags build theirs from the packet).</summary>
        public int Add()
        {
            var index = _slots.Count;
            var position = Position(index);

            var frame = UiKit.Box(_content, $"item-slot {index}").gameObject.AddComponent<Image>();
            UiKit.Place(frame.rectTransform, position.x, position.y, SlotSize, SlotSize);
            frame.sprite = _kit.Region("main", 0, 42, 16, 16);
            var button = frame.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState
            {
                highlightedSprite = _kit.Region("main", 16, 42, 16, 16),
                pressedSprite = _kit.Region("main", 32, 42, 16, 16),
                selectedSprite = _kit.Region("main", 0, 42, 16, 16)
            };

            var icon = UiKit.Box(frame.transform, "item-image").gameObject.AddComponent<Image>();
            UiKit.Place(icon.rectTransform, 0.125f * UiKit.Em, 0.125f * UiKit.Em, SlotSize - 0.25f * UiKit.Em, SlotSize - 0.25f * UiKit.Em);
            icon.raycastTarget = false;
            icon.enabled = false;

            var count = _kit.Label(_content, "item-count", "", _kit.Body, CountHeight, "white", TextAnchor.UpperCenter);
            UiKit.Place(count.rectTransform, position.x, position.y + SlotSize, SlotSize, CountHeight);

            var handle = frame.gameObject.AddComponent<GridSlot>();
            handle.Grid = this;
            handle.Index = index;

            _slots.Add((icon, count, ""));
            _content.sizeDelta = new Vector2(0, position.y + SlotSize + CountHeight);
            return index;
        }

        /// <summary>Removes every slot (loot bags reload theirs).</summary>
        public void Reset()
        {
            for (var i = _content.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_content.GetChild(i).gameObject);
            _slots.Clear();
            _content.sizeDelta = Vector2.zero;
            _content.anchoredPosition = Vector2.zero;
        }

        /// <summary>The slot's image (an item key, or "" to empty it) and count text (util.ts getCount).</summary>
        public void Set(int index, string key, double? count)
        {
            if (index < 0 || index >= _slots.Count) return;
            var slot = _slots[index];
            var sprite = string.IsNullOrEmpty(key) ? null : _kit.ItemSprite(key);
            slot.icon.sprite = sprite;
            slot.icon.enabled = sprite != null;
            slot.count.text = string.IsNullOrEmpty(key) ? "" : Util.GetCount(count);
            _slots[index] = (slot.icon, slot.count, key ?? "");
        }

        /// <summary>Copies the image and the count text as they are (the windows that mirror the inventory).</summary>
        public void Copy(int index, string key, string countText)
        {
            Set(index, key, null);
            var slot = _slots[index];
            slot.count.text = countText ?? "";
        }

        public string KeyAt(int index) => index >= 0 && index < _slots.Count ? _slots[index].key : "";

        public string CountTextAt(int index) => index >= 0 && index < _slots.Count ? _slots[index].count.text : "";

        public bool IsEmpty(int index) => string.IsNullOrEmpty(KeyAt(index));

        /// <summary>Scrolls back to the top (scrollTop = 0).</summary>
        public void ScrollTop() => _content.anchoredPosition = Vector2.zero;

        /// <summary>A click on a slot, as the pointer does it.</summary>
        public void Click(int index) => Clicked?.Invoke(index);

        public void SecondaryClick(int index) => SecondaryClicked?.Invoke(index);

        sealed class GridSlot : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
        {
            public SlotGrid Grid;
            public int Index;
            RectTransform _ghost;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.dragging) return;
                if (eventData.button == PointerEventData.InputButton.Left) Grid.Clicked?.Invoke(Index);
                else if (eventData.button == PointerEventData.InputButton.Right) Grid.SecondaryClicked?.Invoke(Index);
            }

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (Grid.IsEmpty(Index)) return;
                var ghost = new GameObject("drag", typeof(RectTransform)).AddComponent<Image>();
                ghost.sprite = Grid._slots[Index].icon.sprite;
                ghost.raycastTarget = false;
                _ghost = ghost.rectTransform;
                _ghost.SetParent(GetComponentInParent<Canvas>().rootCanvas.transform, false);
                _ghost.sizeDelta = new Vector2(SlotSize, SlotSize);
                OnDrag(eventData);
            }

            public void OnDrag(PointerEventData eventData)
            {
                if (_ghost == null) return;
                RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)_ghost.parent, eventData.position,
                    eventData.pressEventCamera, out var point);
                _ghost.localPosition = point;
            }

            public void OnEndDrag(PointerEventData eventData)
            {
                if (_ghost != null) Destroy(_ghost.gameObject);
                _ghost = null;
            }

            public void OnDrop(PointerEventData eventData)
            {
                if (eventData.pointerDrag == null || !eventData.pointerDrag.TryGetComponent<GridSlot>(out var from) || from._ghost == null) return;
                if (from.Grid == Grid && from.Index == Index) return;
                from.Grid.Dropped?.Invoke(from.Grid.Type, from.Index, Grid.Type, Index);
            }
        }
    }

    /// <summary>
    /// The "Input … Amount" &lt;dialog class="slice-dialog"&gt; of the store, trade and drop: a title, a number field
    /// starting at 1, and the confirm / cancel buttons (characterdialogsheet 21×14 at y 308, 2×). The page under it
    /// is dimmed (.dimmed: brightness 25%) while it is open. Enter accepts, Escape cancels.
    /// </summary>
    public sealed class CountDialog
    {
        readonly RectTransform _root;
        readonly InputField _count;
        readonly CanvasGroup _dimmed;

        public event Action<double> Accepted;

        public CountDialog(UiKit kit, RectTransform parent, string name, string titleKey, CanvasGroup dimmed)
        {
            _dimmed = dimmed;
            var dialog = kit.Sliced(parent, name, "dialog", UiKit.Em);
            dialog.raycastTarget = true;
            _root = UiKit.Centre(dialog.rectTransform, 8 * UiKit.Em, 5 * UiKit.Em);

            var title = kit.Label(_root, "dialog-title", "", kit.Body, 0.75f * UiKit.Em, "#e3e3e3", TextAnchor.UpperCenter);
            UiKit.Place(title.rectTransform, 0, UiKit.Em, 8 * UiKit.Em, UiKit.Em);
            Localization.LanguageChanged += () => title.text = Localization.Get(titleKey);
            title.text = Localization.Get(titleKey);

            _count = kit.Input(_root, "dialog-count", "", 0.75f * UiKit.Em, false, 11);
            _count.contentType = InputField.ContentType.IntegerNumber;
            ((Image)_count.targetGraphic).color = new Color(0, 0, 0, 0.5f);
            UiKit.Place((RectTransform)_count.transform, 0.5f * UiKit.Em, 2 * UiKit.Em, 7 * UiKit.Em, 1.25f * UiKit.Em);

            Button(kit, "confirm dialog-accept", 0, 1.5f * UiKit.Em, Accept);
            Button(kit, "cancel dialog-cancel", 1, 8 * UiKit.Em - 1.5f * UiKit.Em - 42, Hide);
            _root.gameObject.SetActive(false);
        }

        void Button(UiKit kit, string name, int row, float left, Action onClick)
        {
            Sprite Frame(int f) => kit.Region("characterdialogsheet", f * 21, 308 + row * 14, 21, 14);
            var image = UiKit.Box(_root, name).gameObject.AddComponent<Image>();
            image.sprite = Frame(0);
            UiKit.Place(image.rectTransform, left, 3.25f * UiKit.Em, 42, 28);
            var button = image.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Frame(2), pressedSprite = Frame(1), selectedSprite = Frame(0) };
            button.onClick.AddListener(() => onClick());
        }

        public bool Visible => _root.gameObject.activeSelf;

        public void Show()
        {
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            if (_dimmed != null) _dimmed.alpha = 0.25f;
            _count.text = "1";
            _count.Select();
            _count.ActivateInputField();
        }

        public void Hide()
        {
            _root.gameObject.SetActive(false);
            if (_dimmed != null) _dimmed.alpha = 1;
        }

        /// <summary>The count as input.valueAsNumber (NaN when empty), then the dialog closes.</summary>
        public void Accept()
        {
            var count = double.TryParse(_count.text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
            Hide();
            Accepted?.Invoke(count);
        }
    }
}
