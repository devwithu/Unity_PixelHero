using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using PixelHero.Common;
using PixelHero.Js;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>An inventory slot as the server serializes it (common/types/slot.ts SlotData).</summary>
    public sealed class SlotData
    {
        public int Index;
        public string Key = "";
        public double? Count;
        public string Name, Description;
        public bool Edible, Equippable, Interactable;
        public Dictionary<string, object> AttackStats, DefenseStats, Bonuses, Enchantments;

        public static SlotData Parse(Dictionary<string, object> data)
        {
            object Get(string key) => data.TryGetValue(key, out var value) ? value : null;
            return new SlotData
            {
                Index = (int)(double)data["index"],
                Key = Get("key") as string ?? "",
                Count = Get("count") as double?,
                Name = Get("name") as string,
                Description = Get("description") as string,
                Edible = Get("edible") is bool e && e,
                Equippable = Get("equippable") is bool q && q,
                Interactable = Get("interactable") is bool i && i,
                AttackStats = Get("attackStats") as Dictionary<string, object>,
                DefenseStats = Get("defenseStats") as Dictionary<string, object>,
                Bonuses = Get("bonuses") as Dictionary<string, object>,
                Enchantments = Get("enchantments") as Dictionary<string, object>
            };
        }
    }

    /// <summary>
    /// menu/inventory.ts and menu/actions.ts: 25 slots (main.png item-slot frames at 2×) in a .slice-dialog at the
    /// bottom right; a click opens the action menu (eat, interact, equip, drop one, drop many with a count dialog),
    /// a double click eats or equips at once, dragging a slot onto another swaps them. Every action goes to the
    /// server as a Container packet; the slots change only when the server answers.
    /// </summary>
    public sealed class InventoryView : MenuPanel, IInventorySource
    {
        public const int Size = 25; // Modules.Constants.INVENTORY_SIZE

        sealed class Slot
        {
            public SlotData Data = new SlotData();
            public Image Frame;
            public Image Icon;
            public Text Count;
        }

        readonly UiKit _kit;
        readonly Slot[] _slots = new Slot[Size];
        readonly ActionsView _actions;
        int _selectedSlot = -1;

        /// <summary>inventory.ts selectCallback → menu controller: Packets.Container {opcode, type, fromIndex, value}.</summary>
        public event Action<Opcodes.Container, int, double?> Selected;

        public InventoryView(UiKit kit, RectTransform parent) : base(Panel(kit, parent))
        {
            _kit = kit;
            _actions = new ActionsView(kit, parent);
            _actions.Action += HandleAction;
            _actions.Drop += count => Selected?.Invoke(Opcodes.Container.Remove, _selectedSlot, count);
            Load();
        }

        public ActionsView Actions => _actions;

        // #inventory.slice-dialog: right 1em, bottom 4em, 12em × 15em, padding 1em 0.675em 0.675em.
        static RectTransform Panel(UiKit kit, RectTransform parent)
        {
            var dialog = kit.Sliced(parent, "#inventory", "dialog", UiKit.Em);
            dialog.raycastTarget = true;
            var rect = dialog.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = new Vector2(-UiKit.Em, 4 * UiKit.Em);
            rect.sizeDelta = new Vector2(12 * UiKit.Em, 15 * UiKit.Em);
            return rect;
        }

        /// <summary>
        /// The slot list: ul flex row wrap, space-between. Each li is the 2em slot and a 0.8em count line under it.
        /// </summary>
        void Load()
        {
            float padX = 0.675f * UiKit.Em, padTop = UiKit.Em;
            var width = 12 * UiKit.Em - 2 * padX;
            const int perRow = 5;
            var slotSize = 2 * UiKit.Em;
            var countHeight = 0.8f * UiKit.Em; // height 1em at font-size 0.8em
            var gap = (width - perRow * slotSize) / (perRow - 1);

            for (var i = 0; i < Size; i++)
            {
                var x = padX + i % perRow * (slotSize + gap);
                var y = padTop + i / perRow * (slotSize + countHeight);

                var frame = UiKit.Box(Root, $"slot {i}").gameObject.AddComponent<Image>();
                UiKit.Place(frame.rectTransform, x, y, slotSize, slotSize);
                frame.sprite = _kit.Region("main", 0, 42, 16, 16);
                var button = frame.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState
                {
                    highlightedSprite = _kit.Region("main", 16, 42, 16, 16),
                    pressedSprite = _kit.Region("main", 32, 42, 16, 16),
                    selectedSprite = _kit.Region("main", 0, 42, 16, 16)
                };

                // .item-slot padding 0.125em; .item-image covers the rest.
                var icon = UiKit.Box(frame.transform, "item-image").gameObject.AddComponent<Image>();
                UiKit.Place(icon.rectTransform, 0.125f * UiKit.Em, 0.125f * UiKit.Em, slotSize - 0.25f * UiKit.Em, slotSize - 0.25f * UiKit.Em);
                icon.raycastTarget = false;
                icon.enabled = false;

                var count = _kit.Label(Root, "item-count", "", _kit.Body, 0.8f * UiKit.Em, "white", TextAnchor.UpperCenter);
                UiKit.Place(count.rectTransform, x, y + slotSize, slotSize, countHeight);

                var handle = frame.gameObject.AddComponent<SlotHandle>();
                handle.Index = i;
                handle.Clicked = (index, doubleClick) => Select(index, doubleClick);
                handle.Dropped = Swap;
                handle.CanDrag = index => !IsEmpty(index);
                handle.Icon = icon;

                _slots[i] = new Slot { Frame = frame, Icon = icon, Count = count };
            }
        }

        // ---------------------------------------------------------------- server data

        /// <summary>Container Batch: every slot with a key.</summary>
        public void Batch(IEnumerable<SlotData> slots)
        {
            foreach (var slot in slots)
                if (!string.IsNullOrEmpty(slot.Key)) SetSlot(slot);
        }

        /// <summary>Container Add / Remove: the slot's new state.</summary>
        public void SetSlot(SlotData data)
        {
            if (data.Index < 0 || data.Index >= Size) return;
            var slot = _slots[data.Index];
            slot.Data = data;

            slot.Count.text = Util.GetCount(data.Count);
            var texture = string.IsNullOrEmpty(data.Key) ? null : _kit.Catalog.GetTexture("sprites/items/" + Util.ItemImageKey(data.Key));
            slot.Icon.enabled = texture != null;
            slot.Icon.sprite = texture != null ? Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100) : null;
        }

        public SlotData GetSlot(int index) => _slots[index].Data;

        /// <summary>inventory.ts isEmpty: no item image.</summary>
        public bool IsEmpty(int index) => string.IsNullOrEmpty(_slots[index].Data.Key);

        // ---------------------------------------------------------------- actions

        /// <summary>A click (or double click) on a slot, as the pointer does it.</summary>
        public void Click(int index, bool doubleClick = false) => Select(index, doubleClick);

        /// <summary>inventory.ts select.</summary>
        void Select(int index, bool doubleClick)
        {
            var slot = _slots[index].Data;
            if (IsEmpty(index))
            {
                _actions.Hide();
                return;
            }

            _selectedSlot = index;

            if (doubleClick)
            {
                if (slot.Edible) HandleAction(Modules.MenuActions.Eat);
                else if (slot.Equippable || slot.Interactable) HandleAction(Modules.MenuActions.Equip);
                _actions.Hide();
                return;
            }

            var actions = new List<string>();
            if (slot.Edible) actions.Add(Modules.MenuActions.Eat);
            if (slot.Interactable) actions.Add(Modules.MenuActions.Interact);
            if (slot.Equippable) actions.Add(Modules.MenuActions.Equip);
            actions.Add(Modules.MenuActions.DropOne);
            if (slot.Count > 1) actions.Add(Modules.MenuActions.DropMany);

            _actions.Show(actions, slot.Name, slot.AttackStats, slot.DefenseStats, slot.Bonuses, slot.Enchantments,
                FormatDescription(slot.Name, slot.Count, slot.Description));
        }

        /// <summary>inventory.ts handleAction.</summary>
        void HandleAction(string action)
        {
            if (action == Modules.MenuActions.DropMany)
            {
                _actions.ShowDropDialog();
                return;
            }

            if (Util.ContainerAction(action) is Opcodes.Container opcode) Selected?.Invoke(opcode, _selectedSlot, 1);
            _actions.Hide();
        }

        void Swap(int fromIndex, int toIndex)
        {
            if (fromIndex == toIndex) return;
            Selected?.Invoke(Opcodes.Container.Swap, fromIndex, toIndex);
            _selectedSlot = -1;
        }

        /// <summary>inventory.ts formatDescription: stacks of 1000 or more name their exact count.</summary>
        static string FormatDescription(string name, double? count, string description)
        {
            if (!(count >= 1000)) return description;
            var amount = count.Value.ToString("#,0.###", CultureInfo.InvariantCulture);
            return $"{description} {Localization.Get("item.stack", ("count", amount), ("name", (name ?? "undefined").ToLowerInvariant()))} ";
        }

        public override void Hide()
        {
            base.Hide();
            _selectedSlot = -1;
            _actions.Hide();
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            _actions.Update(deltaTime);
        }

        /// <summary>A slot's pointer handling: click, double click, drag onto another slot.</summary>
        sealed class SlotHandle : MonoBehaviour, IPointerClickHandler, IBeginDragHandler, IDragHandler, IEndDragHandler, IDropHandler
        {
            public int Index;
            public Action<int, bool> Clicked;
            public Action<int, int> Dropped;
            public Func<int, bool> CanDrag;
            public Image Icon;
            RectTransform _ghost;

            public void OnPointerClick(PointerEventData eventData)
            {
                if (eventData.button != PointerEventData.InputButton.Left || eventData.dragging) return;
                Clicked?.Invoke(Index, eventData.clickCount >= 2);
            }

            public void OnBeginDrag(PointerEventData eventData)
            {
                if (!CanDrag(Index)) return;
                var ghost = new GameObject("drag", typeof(RectTransform)).AddComponent<Image>();
                ghost.sprite = Icon.sprite;
                ghost.raycastTarget = false;
                _ghost = ghost.rectTransform;
                _ghost.SetParent(GetComponentInParent<Canvas>().transform, false);
                _ghost.sizeDelta = Icon.rectTransform.rect.size;
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
                if (eventData.pointerDrag != null && eventData.pointerDrag.TryGetComponent<SlotHandle>(out var from) && from._ghost != null)
                    Dropped?.Invoke(from.Index, Index);
            }
        }
    }

    /// <summary>
    /// menu/actions.ts: #action-container (right 13em, bottom 5em, 8em × 10em, no right border), the item name,
    /// its description (or attack/defense stats, bonuses and enchantments), a row of action buttons (main.png,
    /// x 63, one row per action, hover frame +32, active +16), and the drop-count dialog.
    /// </summary>
    public sealed class ActionsView : MenuPanel
    {
        static readonly string[] SpriteRows =
        {
            "", Modules.MenuActions.Attack, Modules.MenuActions.Equip, Modules.MenuActions.DropOne,
            Modules.MenuActions.DropMany, Modules.MenuActions.Eat, Modules.MenuActions.Interact, Modules.MenuActions.Trade,
            Modules.MenuActions.Potion, Modules.MenuActions.Follow, Modules.MenuActions.Examine, Modules.MenuActions.AddFriend
        };

        readonly UiKit _kit;
        readonly Text _name, _description;
        readonly RectTransform _list, _page;
        readonly CanvasGroup _pageGroup;
        readonly RectTransform _dropDialog;
        readonly InputField _dropCount;
        readonly List<GameObject> _buttons = new List<GameObject>();

        public event Action<string> Action;
        public event Action<double> Drop;

        public ActionsView(UiKit kit, RectTransform parent) : base(Panel(kit, parent))
        {
            _kit = kit;
            // padding 1em 0 0.675em 0.675em (slice-dialog, padding-right 0).
            _page = UiKit.Box(Root, "#action-page");
            UiKit.Place(_page, 0.675f * UiKit.Em, UiKit.Em, 8 * UiKit.Em - 0.675f * UiKit.Em, 10 * UiKit.Em - 1.675f * UiKit.Em);
            _pageGroup = _page.gameObject.AddComponent<CanvasGroup>();
            var width = _page.sizeDelta.x;
            var height = _page.sizeDelta.y;

            _name = _kit.Label(_page, "action-item-name", "", _kit.Body, UiKit.Em, "#fff", TextAnchor.UpperCenter, true);
            UiKit.Place(_name.rectTransform, 0, 0, width, UiKit.Em);
            _name.supportRichText = true;

            _description = _kit.Label(_page, "action-item-description", "", _kit.Body, 0.9f * UiKit.Em, "#fff");
            UiKit.Place(_description.rectTransform, 0, UiKit.Em, width, height - 3 * UiKit.Em);
            _description.horizontalOverflow = HorizontalWrapMode.Wrap;
            _description.verticalOverflow = VerticalWrapMode.Truncate;
            _description.supportRichText = true;

            _list = UiKit.Box(_page, "ul");
            UiKit.Place(_list, 0, height - 2 * UiKit.Em, width, 2 * UiKit.Em);

            // <dialog id="action-drop" class="slice-dialog">: title, number input, confirm / cancel.
            var dialog = _kit.Sliced(Root, "#action-drop", "dialog", UiKit.Em);
            dialog.raycastTarget = true;
            _dropDialog = UiKit.Centre(dialog.rectTransform, 8 * UiKit.Em, 5 * UiKit.Em);
            var title = _kit.Label(_dropDialog, "dialog-title", "", _kit.Body, 0.75f * UiKit.Em, "#e3e3e3", TextAnchor.UpperCenter);
            UiKit.Place(title.rectTransform, 0, UiKit.Em, 8 * UiKit.Em, UiKit.Em);
            Localization.LanguageChanged += () => title.text = Localization.Get("item.dropAmount");
            title.text = Localization.Get("item.dropAmount");

            _dropCount = _kit.Input(_dropDialog, "dialog-count", "", 0.75f * UiKit.Em, false, 11);
            _dropCount.contentType = InputField.ContentType.IntegerNumber;
            ((Image)_dropCount.targetGraphic).color = new Color(0, 0, 0, 0.5f);
            UiKit.Place((RectTransform)_dropCount.transform, 0.5f * UiKit.Em, 2 * UiKit.Em, 7 * UiKit.Em, 1.25f * UiKit.Em);

            DialogButton("confirm dialog-accept", 0, 1.5f * UiKit.Em, HandleDrop);
            DialogButton("cancel dialog-cancel", 1, 8 * UiKit.Em - 1.5f * UiKit.Em - 42, HideDropDialog);
            _dropDialog.gameObject.SetActive(false);
        }

        // #action-container: right 13em, bottom 5em, 8em × 10em, border-image-width 1em 0 1em 1em.
        static RectTransform Panel(UiKit kit, RectTransform parent)
        {
            var dialog = kit.Sliced(parent, "#action-container", "dialog", UiKit.Em);
            dialog.raycastTarget = true;
            var rect = dialog.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = new Vector2(-13 * UiKit.Em, 5 * UiKit.Em);
            rect.sizeDelta = new Vector2(8 * UiKit.Em, 10 * UiKit.Em);
            return rect;
        }

        /// <summary>.confirm / .cancel: characterdialogsheet 21×14 at y 308 (one row each), hover +2 frames, active +1.</summary>
        void DialogButton(string name, int row, float left, Action onClick)
        {
            Sprite Frame(int f) => _kit.Region("characterdialogsheet", f * 21, 308 + row * 14, 21, 14);
            var image = UiKit.Box(_dropDialog, name).gameObject.AddComponent<Image>();
            image.sprite = Frame(0);
            UiKit.Place(image.rectTransform, left, 3.25f * UiKit.Em, 42, 28);
            var button = image.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Frame(2), pressedSprite = Frame(1), selectedSprite = Frame(0) };
            button.onClick.AddListener(() => onClick());
        }

        public void Show(List<string> actions, string name, Dictionary<string, object> attackStats,
            Dictionary<string, object> defenseStats, Dictionary<string, object> bonuses, Dictionary<string, object> enchantments,
            string description)
        {
            foreach (var button in _buttons) UnityEngine.Object.Destroy(button);
            _buttons.Clear();

            // ul: flex row, justify-content space-evenly; each li 2em square.
            var width = _list.sizeDelta.x;
            var gap = (width - actions.Count * 2 * UiKit.Em) / (actions.Count + 1);
            for (var i = 0; i < actions.Count; i++) _buttons.Add(ActionButton(actions[i], gap + i * (2 * UiKit.Em + gap)));

            _name.text = MessageFormat.ToRichText(name ?? "undefined");
            _description.text = !string.IsNullOrEmpty(description)
                ? MessageFormat.ToRichText(description)
                : StatsText(attackStats, defenseStats, bonuses, enchantments);

            base.Show();
        }

        GameObject ActionButton(string action, float left)
        {
            var row = Array.IndexOf(SpriteRows, action);
            Sprite Frame(int f) => _kit.Region("main", 63 + f * 16, row * 16, 16, 16);
            var image = UiKit.Box(_list, $"action-{action}").gameObject.AddComponent<Image>();
            image.sprite = Frame(0);
            UiKit.Place(image.rectTransform, left, 0, 2 * UiKit.Em, 2 * UiKit.Em);
            var button = image.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Frame(2), pressedSprite = Frame(1), selectedSprite = Frame(0) };
            button.onClick.AddListener(() => Action?.Invoke(action));
            return image.gameObject;
        }

        static string Stat(Dictionary<string, object> stats, string key) =>
            stats != null && stats.TryGetValue(key, out var value) ? value is double d ? JsValue.NumberToString(d) : Convert.ToString(value, CultureInfo.InvariantCulture) : "undefined";

        /// <summary>actions.ts show without a description: attack and defense stats, bonuses, enchantments.</summary>
        static string StatsText(Dictionary<string, object> attack, Dictionary<string, object> defense, Dictionary<string, object> bonuses,
            Dictionary<string, object> enchantments)
        {
            var text = new StringBuilder();
            void Block(string titleKey, Dictionary<string, object> stats, params string[] keys)
            {
                text.Append(Localization.Get(titleKey)).Append('\n');
                foreach (var key in keys) text.Append(Localization.Get("item.stat." + key)).Append(": ").Append(Stat(stats, key)).Append('\n');
            }

            Block("item.attackStats", attack ?? new Dictionary<string, object>(), "crush", "slash", "stab", "archery", "magic");
            Block("item.defenseStats", defense ?? new Dictionary<string, object>(), "crush", "slash", "stab", "archery", "magic");
            Block("item.bonuses", bonuses ?? new Dictionary<string, object>(), "accuracy", "strength", "archery", "magic");

            if (enchantments != null && enchantments.Count > 0)
            {
                text.Append(Localization.Get("item.enchantments")).Append('\n');
                foreach (var pair in enchantments)
                {
                    var level = pair.Value is Dictionary<string, object> e && e.TryGetValue("level", out var l) && l is double d ? JsValue.NumberToString(d) : "undefined";
                    var name = int.TryParse(pair.Key, out var id) && Enum.IsDefined(typeof(Modules.Enchantment), id)
                        ? ((Modules.Enchantment)id).ToString() : "undefined";
                    text.Append(name).Append(": ").Append(level).Append('\n');
                }
            }

            return text.ToString().TrimEnd('\n');
        }

        public override void Hide()
        {
            base.Hide();
            HideDropDialog();
        }

        /// <summary>A click on an action button.</summary>
        public void Press(string action) => Action?.Invoke(action);

        /// <summary>actions.ts showDropDialog: the page dims, the count starts at 1 and takes the focus.</summary>
        public void ShowDropDialog()
        {
            _dropDialog.gameObject.SetActive(true);
            _pageGroup.alpha = 0.5f;
            _dropCount.text = "1";
            _dropCount.Select();
            _dropCount.ActivateInputField();
        }

        public void HideDropDialog()
        {
            _dropDialog.gameObject.SetActive(false);
            _pageGroup.alpha = 1;
        }

        public bool DropDialogVisible => _dropDialog.gameObject.activeSelf;

        /// <summary>actions.ts handleDrop: the count (NaN when empty), then hide everything.</summary>
        public void HandleDrop()
        {
            var count = double.TryParse(_dropCount.text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
            _dropCount.text = "";
            HideDropDialog();
            Hide();
            Drop?.Invoke(count);
        }
    }

    /// <summary>The parts of client/src/utils/util.ts the menus use.</summary>
    public static class Util
    {
        /// <summary>util.ts getCount: 1M+ → "nM", 10K+ → "nK", more than 1 → the number, else nothing.</summary>
        public static string GetCount(double? count)
        {
            if (!(count is double c)) return "";
            if (c > 999_999) return $"{JsValue.NumberToString(Math.Floor(c / 1_000_000))}M";
            if (c > 9999) return $"{JsValue.NumberToString(Math.Floor(c / 1000))}K";
            return c > 1 ? JsValue.NumberToString(c) : "";
        }

        /// <summary>util.ts getImageURL: the last block of a key path, under sprites/items.</summary>
        public static string ItemImageKey(string key)
        {
            var blocks = key.Split('/');
            return blocks[blocks.Length - 1];
        }

        /// <summary>util.ts getContainerAction.</summary>
        public static Opcodes.Container? ContainerAction(string action)
        {
            switch (action)
            {
                case Modules.MenuActions.Interact:
                case Modules.MenuActions.Equip:
                case Modules.MenuActions.Eat:
                case Modules.MenuActions.Potion:
                    return Opcodes.Container.Select;
                case Modules.MenuActions.DropOne:
                    return Opcodes.Container.Remove;
                default:
                    return null;
            }
        }

    }
}
