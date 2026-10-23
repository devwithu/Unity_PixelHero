using System;
using System.Collections.Generic;
using System.Linq;
using PixelHero.Common;
using PixelHero.Js;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>The windows that show a copy of the inventory read it from the inventory view (menu.ts synchronize).</summary>
    public interface IInventorySource
    {
        SlotData GetSlot(int index);
    }

    /// <summary>
    /// menu/store.ts: #store (.slice-container) with a copy of the inventory (5 columns) and the sell row on the
    /// left, the store's items on the right. Clicking an inventory slot asks the server to move it to the sell slot
    /// (Store Select); the check sells it; clicking a store item opens the "Input Buy Amount" dialog.
    /// </summary>
    public sealed class StoreView : ContainerWindow
    {
        readonly IInventorySource _inventory;
        readonly SlotGrid _inventoryGrid;
        readonly RectTransform _storeList;
        readonly float _storeWidth;
        readonly Image _sellSlot, _returnSlot;
        readonly Text _sellText, _returnText;
        readonly CountDialog _buyDialog;
        readonly List<GameObject> _items = new List<GameObject>();
        string _key = "", _currency = "gold";
        int _selectedIndex = -1, _selectedBuyIndex = -1;
        double? _selectedCount = -1;

        /// <summary>(opcode, store key, index, count) → Packets.Store.</summary>
        public event Action<Opcodes.Store, string, int, double?> Selected;

        public StoreView(UiKit kit, RectTransform parent, IInventorySource inventory) : base(kit, parent, "#store")
        {
            _inventory = inventory;

            var page = UiKit.Box(Content, "#store-content");
            UiKit.Place(page, 0, 0, ContentWidth, ContentHeight);
            var dimmed = page.gameObject.AddComponent<CanvasGroup>();

            // #store-inventory (13em: five slots and the paddings) | gap 0.5em | #store-slots (the rest).
            const float inventoryWidth = 13 * UiKit.Em, sellHeight = 3 * UiKit.Em;
            var slots = Inner(page, "#store-inventory-slots", 0, 0, inventoryWidth, ContentHeight - sellHeight - 0.5f * UiKit.Em);
            _inventoryGrid = new SlotGrid(kit, slots, InventoryView.Size, (int)Modules.ContainerType.Inventory, 5);
            _inventoryGrid.Clicked += index => Selected?.Invoke(Opcodes.Store.Select, _key, index, null);

            // #store-sell: [sell slot + text] [return slot + text] [ok-check], space-evenly.
            var sellTop = ContentHeight - sellHeight;
            _sellSlot = ItemSlot(page, "#store-sell-slot", inventoryWidth * 0.25f - UiKit.Em, sellTop, out _sellText);
            _returnSlot = ItemSlot(page, "#store-sell-return-slot", inventoryWidth * 0.5f - UiKit.Em, sellTop, out _returnText);
            var check = UiKit.Box(page, "#store-sell-confirm").gameObject.AddComponent<Image>();
            check.sprite = kit.Region("main", 0, 119, 14, 14);
            UiKit.Place(check.rectTransform, inventoryWidth * 0.75f - 14, sellTop + 2, 28, 28);
            var ok = check.gameObject.AddComponent<Button>();
            ok.transition = Selectable.Transition.SpriteSwap;
            ok.spriteState = new SpriteState { highlightedSprite = kit.Region("main", 14, 119, 14, 14), pressedSprite = kit.Region("main", 28, 119, 14, 14) };
            ok.onClick.AddListener(Sell);
            _sellSlot.gameObject.AddComponent<Button>().onClick.AddListener(Synchronize);

            var list = Inner(page, "#store-slots-content", inventoryWidth + 0.5f * UiKit.Em, 0, ContentWidth - inventoryWidth - 0.5f * UiKit.Em, ContentHeight);
            _storeList = UiKit.ScrollView(list, "ul");
            _storeWidth = list.sizeDelta.x - 0.25f * UiKit.Em;

            _buyDialog = new CountDialog(kit, Root, "#store-buy", "store.buyAmount", dimmed);
            _buyDialog.Accepted += count => Selected?.Invoke(Opcodes.Store.Buy, _key, _selectedBuyIndex, count);
        }

        public CountDialog BuyDialog => _buyDialog;

        RectTransform Inner(RectTransform parent, string name, float left, float top, float width, float height)
        {
            var box = Kit.Sliced(parent, name, "inner-container", 2 * UiKit.Em);
            UiKit.Place(box.rectTransform, left, top, width, height);
            var inner = UiKit.Box(box.transform, "padding");
            UiKit.Place(inner, 0.75f * UiKit.Em, 0.75f * UiKit.Em, width - 1.5f * UiKit.Em, height - 1.5f * UiKit.Em);
            return inner;
        }

        /// <summary>.slice-item-slot (item-slot.png, 2em, padding 0.375em) with a 1em text line under it.</summary>
        Image ItemSlot(RectTransform parent, string name, float left, float top, out Text text)
        {
            var frame = Kit.Sliced(parent, "slice-item-slot", "item-slot", 0.5f * UiKit.Em);
            frame.raycastTarget = true;
            UiKit.Place(frame.rectTransform, left, top, 2 * UiKit.Em, 2 * UiKit.Em);
            var image = UiKit.Box(frame.transform, name).gameObject.AddComponent<Image>();
            UiKit.Place(image.rectTransform, 0.375f * UiKit.Em, 0.375f * UiKit.Em, 1.25f * UiKit.Em, 1.25f * UiKit.Em);
            image.enabled = false;
            image.raycastTarget = false;
            text = Kit.Label(parent, name + "-text", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter);
            UiKit.Place(text.rectTransform, left - UiKit.Em, top + 2 * UiKit.Em, 4 * UiKit.Em, UiKit.Em);
            return image;
        }

        static void SetImage(Image image, Sprite sprite)
        {
            image.sprite = sprite;
            image.enabled = sprite != null;
        }

        /// <summary>store.ts show (Store Open).</summary>
        public void Open(Dictionary<string, object> info)
        {
            Show();
            Update(info);
            Synchronize();
        }

        /// <summary>store.ts update: the store's key, currency and items.</summary>
        public void Update(Dictionary<string, object> info)
        {
            Clear();
            _key = info.TryGetValue("key", out var key) ? key as string ?? "" : "";
            _currency = info.TryGetValue("currency", out var currency) ? currency as string ?? "gold" : "gold";
            if (info.TryGetValue("items", out var items) && items is List<object> list)
                for (var i = 0; i < list.Count; i++) CreateStoreItem((Dictionary<string, object>)list[i], i);
        }

        /// <summary>li.slice-list-item: item slot, name (flex 3), "xN" (flex 1), price + currency initial (flex 1).</summary>
        void CreateStoreItem(Dictionary<string, object> item, int index)
        {
            const float height = 2.5f * UiKit.Em;
            var top = _items.Count * (height + 0.25f * UiKit.Em);
            var row = Kit.Sliced(_storeList, "li", "list-item", 0.5f * UiKit.Em);
            row.raycastTarget = true;
            UiKit.Place(row.rectTransform, 0, top, _storeWidth, height);
            var button = row.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Kit.Slice("list-item-hover"), selectedSprite = Kit.Slice("list-item") };
            button.onClick.AddListener(() =>
            {
                _selectedBuyIndex = index;
                _buyDialog.Show();
            });

            var slot = Kit.Sliced(row.transform, "slice-item-slot", "item-slot", 0.5f * UiKit.Em);
            UiKit.Place(slot.rectTransform, 0.25f * UiKit.Em, 0.25f * UiKit.Em, 2 * UiKit.Em, 2 * UiKit.Em);
            var image = UiKit.Box(slot.transform, "store-item-image").gameObject.AddComponent<Image>();
            UiKit.Place(image.rectTransform, 0.375f * UiKit.Em, 0.375f * UiKit.Em, 1.25f * UiKit.Em, 1.25f * UiKit.Em);
            image.raycastTarget = false;
            SetImage(image, Kit.ItemSprite(item.TryGetValue("key", out var key) ? key as string : null));

            var rest = _storeWidth - 2.5f * UiKit.Em - 2 * 0.5f * UiKit.Em - 0.25f * UiKit.Em;
            var x = 2.75f * UiKit.Em;
            Text Column(string name, string text, float share)
            {
                var label = Kit.Label(row.transform, name, text, Kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleLeft, true);
                UiKit.Place(label.rectTransform, x, 0, rest * share, height);
                x += rest * share + 0.5f * UiKit.Em;
                return label;
            }

            var count = item.TryGetValue("count", out var c) && c is double n ? n : double.NaN;
            var price = item.TryGetValue("price", out var p) && p is double v ? JsValue.NumberToString(v) : "undefined";
            Column("store-item-name", item.TryGetValue("name", out var itemName) ? itemName as string ?? "undefined" : "undefined", 0.6f);
            Column("store-item-count", count != -1 ? $"x{JsValue.NumberToString(count)}" : "", 0.2f);
            Column("store-item-price", $"{price}{(_currency.Length > 0 ? _currency[0].ToString() : "undefined")}", 0.2f);

            _items.Add(row.gameObject);
            _storeList.sizeDelta = new Vector2(0, top + height);
        }

        /// <summary>store.ts move (Store Select): the selected inventory item goes to the sell slot with its price.</summary>
        public void Move(Dictionary<string, object> info)
        {
            if ((info.TryGetValue("key", out var key) ? key as string : null) != _key) return;
            Synchronize();

            if (!(info.TryGetValue("item", out var value) && value is Dictionary<string, object> item)) return;
            var index = item.TryGetValue("index", out var i) && i is double d ? (int)d : -1;
            if (index < 0 || index >= _inventoryGrid.Count) return;

            SetImage(_sellSlot, Kit.ItemSprite(_inventoryGrid.KeyAt(index)));
            _sellText.text = _inventoryGrid.CountTextAt(index);
            SetImage(_returnSlot, Kit.ItemSprite(_currency));
            _returnText.text = item.TryGetValue("price", out var price) && price is double p ? JsValue.NumberToString(p) : "";

            _inventoryGrid.Set(index, "", null);
            _selectedIndex = index;
            _selectedCount = item.TryGetValue("count", out var count) ? count as double? : null;
        }

        void Sell() => Selected?.Invoke(Opcodes.Store.Sell, _key, _selectedIndex, _selectedCount);

        /// <summary>store.ts synchronize: the sell slot empties and the inventory copy is refreshed.</summary>
        public void Synchronize()
        {
            if (!IsVisible) return;
            ClearSellSlot();
            _inventoryGrid.ScrollTop();
            for (var i = 0; i < _inventoryGrid.Count; i++)
            {
                var slot = _inventory.GetSlot(i);
                _inventoryGrid.Copy(i, slot.Key, Util.GetCount(slot.Count));
            }
        }

        public override void Hide()
        {
            base.Hide();
            Clear();
            ClearSellSlot();
            _buyDialog.Hide();
        }

        void Clear()
        {
            _key = "";
            _currency = "gold";
            foreach (var item in _items) UnityEngine.Object.Destroy(item);
            _items.Clear();
            _storeList.sizeDelta = Vector2.zero;
            _storeList.anchoredPosition = Vector2.zero;
        }

        void ClearSellSlot()
        {
            _selectedIndex = -1;
            _selectedCount = -1;
            SetImage(_sellSlot, null);
            _sellText.text = "";
            SetImage(_returnSlot, null);
            _returnText.text = "";
        }

        /// <summary>For tests.</summary>
        public string Key => _key;
        public int ItemCount => _items.Count;
        public int SelectedIndex => _selectedIndex;
        public SlotGrid InventoryGrid => _inventoryGrid;
        public void ClickItem(int index)
        {
            _selectedBuyIndex = index;
            _buyDialog.Show();
        }
        public void ClickSell() => Sell();
    }

    /// <summary>
    /// menu/bank.ts: #bank (37em × 21em, centred) with the inventory (bank.png 128×126 at 0,0, 2×) and the bank
    /// (168×166 at 132,0, 2×), each padded 2.75em 1.75em. A click moves a slot to the other side, a drag moves it
    /// onto a chosen slot (Container Select with the bank's type).
    /// </summary>
    public sealed class BankView : MenuPanel
    {
        public const int Size = 420; // Modules.Constants.BANK_SIZE

        readonly IInventorySource _inventory;
        readonly SlotGrid _inventoryGrid, _bankGrid;

        /// <summary>(from container, from index, to container, to index or null).</summary>
        public event Action<int, int, int, int?> Selected;

        public BankView(UiKit kit, RectTransform parent, IInventorySource inventory) : base(Frame(parent))
        {
            _inventory = inventory;

            var left = Panel(kit, "#bank-inventory-container", 0, 0, 128, 126);
            var right = Panel(kit, "#bank-container", 256, 132, 168, 166);
            _inventoryGrid = new SlotGrid(kit, left, InventoryView.Size, (int)Modules.ContainerType.Inventory);
            _bankGrid = new SlotGrid(kit, right, Size, (int)Modules.ContainerType.Bank);

            _inventoryGrid.Clicked += index => Selected?.Invoke((int)Modules.ContainerType.Inventory, index, (int)Modules.ContainerType.Bank, null);
            _bankGrid.Clicked += index => Selected?.Invoke((int)Modules.ContainerType.Bank, index, (int)Modules.ContainerType.Inventory, null);
            _inventoryGrid.Dropped += (fromType, fromIndex, toType, toIndex) => Selected?.Invoke(fromType, fromIndex, toType, toIndex);
            _bankGrid.Dropped += (fromType, fromIndex, toType, toIndex) => Selected?.Invoke(fromType, fromIndex, toType, toIndex);

            Sprite Close(int frame) => kit.Region("guilds/elements", 64 + frame * 12, 0, 11, 11);
            var close = UiKit.Box(Root, "#close-bank").gameObject.AddComponent<Image>();
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

        static RectTransform Frame(RectTransform parent) => UiKit.Centre(UiKit.Box(parent, "#bank"), 37 * UiKit.Em, 21 * UiKit.Em);

        RectTransform Panel(UiKit kit, string name, float left, int x, int width, int height)
        {
            var image = UiKit.Box(Root, name).gameObject.AddComponent<Image>();
            image.sprite = kit.Region("bank", x, 0, width, height);
            UiKit.Place(image.rectTransform, left, 0, width * 2, height * 2);
            var inner = UiKit.Box(image.transform, "ul");
            UiKit.Place(inner, 1.75f * UiKit.Em, 2.75f * UiKit.Em, width * 2 - 3.5f * UiKit.Em, height * 2 - 5.5f * UiKit.Em);
            return inner;
        }

        /// <summary>bank.ts show (NPC Bank): every bank slot from the packet, then the inventory copy.</summary>
        public void Open(IEnumerable<SlotData> slots)
        {
            Show();
            _bankGrid.ScrollTop();
            Synchronize();
            foreach (var slot in slots) _bankGrid.Set(slot.Index, slot.Key, slot.Count);
        }

        /// <summary>Container Batch / Add / Remove for the bank.</summary>
        public void SetSlot(SlotData slot) => _bankGrid.Set(slot.Index, slot.Key, slot.Count);

        public void Synchronize()
        {
            if (!IsVisible) return;
            _inventoryGrid.ScrollTop();
            for (var i = 0; i < _inventoryGrid.Count; i++)
            {
                var slot = _inventory.GetSlot(i);
                _inventoryGrid.Copy(i, slot.Key, Util.GetCount(slot.Count));
            }
        }

        /// <summary>For tests.</summary>
        public SlotGrid InventoryGrid => _inventoryGrid;
        public SlotGrid BankGrid => _bankGrid;
    }

    /// <summary>
    /// menu/lootbag.ts: #lootbag (.slice-container) with the bag's items and the inventory's images side by side;
    /// clicking an item takes it (LootBag Take). The server answers Take (the slot goes) or Close.
    /// </summary>
    public sealed class LootBagView : ContainerWindow
    {
        readonly IInventorySource _inventory;
        readonly SlotGrid _items, _inventoryGrid;
        readonly List<int> _itemIndexes = new List<int>();

        public event Action<int> Take;

        public LootBagView(UiKit kit, RectTransform parent, IInventorySource inventory) : base(kit, parent, "#lootbag")
        {
            _inventory = inventory;
            var half = (ContentWidth - 0.5f * UiKit.Em) / 2;
            _items = new SlotGrid(kit, Inner("#lootbag-items", 0, half), 0, (int)Modules.ContainerType.LootBag);
            _inventoryGrid = new SlotGrid(kit, Inner("#lootbag-inventory", half + 0.5f * UiKit.Em, half), InventoryView.Size,
                (int)Modules.ContainerType.LootBag);
            _items.Clicked += position =>
            {
                if (position >= 0 && position < _itemIndexes.Count && _itemIndexes[position] >= 0) Take?.Invoke(_itemIndexes[position]);
            };
        }

        RectTransform Inner(string name, float left, float width)
        {
            var box = Kit.Sliced(Content, name, "inner-container", 2 * UiKit.Em);
            UiKit.Place(box.rectTransform, left, 0, width, ContentHeight);
            var inner = UiKit.Box(box.transform, "padding");
            UiKit.Place(inner, 0.75f * UiKit.Em, 0.75f * UiKit.Em, width - 1.5f * UiKit.Em, ContentHeight - 1.5f * UiKit.Em);
            return inner;
        }

        /// <summary>lootbag.ts handle.</summary>
        public void Handle(Opcodes.LootBag opcode, Dictionary<string, object> info)
        {
            switch (opcode)
            {
                case Opcodes.LootBag.Open:
                    LoadItems(info.TryGetValue("items", out var items) && items is List<object> list
                        ? list.Cast<Dictionary<string, object>>().Select(SlotData.Parse) : Enumerable.Empty<SlotData>());
                    Show();
                    Synchronize();
                    break;
                case Opcodes.LootBag.Take:
                    var index = info.TryGetValue("index", out var i) && i is double d ? (int)d : -1;
                    var position = _itemIndexes.IndexOf(index);
                    if (position >= 0)
                    {
                        // element.remove(): the slot disappears; the grid keeps its place empty and unclickable.
                        _items.Set(position, "", null);
                        _itemIndexes[position] = -1;
                    }
                    break;
                case Opcodes.LootBag.Close:
                    Hide();
                    break;
            }
        }

        void LoadItems(IEnumerable<SlotData> items)
        {
            _items.Reset();
            _itemIndexes.Clear();
            foreach (var item in items)
            {
                var position = _items.Add();
                _items.Set(position, item.Key, null);
                _itemIndexes.Add(item.Index);
            }
        }

        /// <summary>lootbag.ts synchronize: the inventory's images (no counts).</summary>
        public void Synchronize()
        {
            if (!IsVisible) return;
            for (var i = 0; i < _inventoryGrid.Count; i++) _inventoryGrid.Copy(i, _inventory.GetSlot(i).Key, "");
        }

        /// <summary>For tests.</summary>
        public SlotGrid Items => _items;
    }

    /// <summary>
    /// menu/enchant.ts: #enchant (.slice-container, 16em × 16em): the inventory (5 columns) above the item slot,
    /// the shard slot with its count and the check. A click asks the server to select the slot (Enchant Select);
    /// it answers with the slot to move into the item or shard place; the check confirms (Enchant Confirm).
    /// </summary>
    public sealed class EnchantView : ContainerWindow
    {
        readonly IInventorySource _inventory;
        readonly SlotGrid _grid;
        readonly Image _selectedItem, _selectedShards;
        readonly Text _shardsCount;
        int _selectedSlot = -1, _selectedShard = -1;
        Sprite _itemSprite, _shardSprite;

        public event Action<int> Selected;
        public event Action<int, int> Confirmed;

        public EnchantView(UiKit kit, RectTransform parent, IInventorySource inventory) : base(kit, parent, "#enchant", 16 * UiKit.Em, 16 * UiKit.Em)
        {
            _inventory = inventory;

            // #enchant-inventory (flex 1) above #enchant-item (2em slots, the selected one with margin-bottom 1em).
            const float itemRow = 4 * UiKit.Em;
            var box = Kit.Sliced(Content, "#enchant-inventory", "inner-container", 2 * UiKit.Em);
            UiKit.Place(box.rectTransform, 0, 0, ContentWidth, ContentHeight - itemRow - UiKit.Em);
            var inner = UiKit.Box(box.transform, "padding");
            UiKit.Place(inner, 0.75f * UiKit.Em, 0.75f * UiKit.Em, ContentWidth - 1.5f * UiKit.Em, box.rectTransform.sizeDelta.y - 1.5f * UiKit.Em);
            _grid = new SlotGrid(kit, inner, InventoryView.Size, (int)Modules.ContainerType.Inventory, 5);
            _grid.Clicked += index => Selected?.Invoke(index);

            var top = ContentHeight - itemRow;
            var centre = ContentWidth / 2;
            _selectedItem = Slot("#enchant-item-selected", centre - 4 * UiKit.Em, top);
            _selectedShards = Slot("#enchant-item-shards", centre - UiKit.Em, top);
            _shardsCount = Kit.Label(Content, "#enchant-item-count", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter);
            UiKit.Place(_shardsCount.rectTransform, centre - UiKit.Em, top + 2 * UiKit.Em, 2 * UiKit.Em, UiKit.Em);

            var check = UiKit.Box(Content, "#enchant-item-confirm").gameObject.AddComponent<Image>();
            check.sprite = kit.Region("main", 0, 119, 14, 14);
            UiKit.Place(check.rectTransform, centre + 2 * UiKit.Em, top + 2, 28, 28);
            var ok = check.gameObject.AddComponent<Button>();
            ok.transition = Selectable.Transition.SpriteSwap;
            ok.spriteState = new SpriteState { highlightedSprite = kit.Region("main", 14, 119, 14, 14), pressedSprite = kit.Region("main", 28, 119, 14, 14) };
            ok.onClick.AddListener(Confirm);
        }

        Image Slot(string name, float left, float top)
        {
            var frame = Kit.Sliced(Content, name, "item-slot", 0.5f * UiKit.Em);
            frame.raycastTarget = true;
            UiKit.Place(frame.rectTransform, left, top, 2 * UiKit.Em, 2 * UiKit.Em);
            frame.gameObject.AddComponent<Button>().onClick.AddListener(Clear);
            var image = UiKit.Box(frame.transform, "div").gameObject.AddComponent<Image>();
            UiKit.Place(image.rectTransform, 0.375f * UiKit.Em, 0.375f * UiKit.Em, 1.25f * UiKit.Em, 1.25f * UiKit.Em);
            image.raycastTarget = false;
            image.enabled = false;
            return image;
        }

        public override void Show()
        {
            base.Show();
            Synchronize();
        }

        void Confirm()
        {
            if (_selectedSlot == -1 || _selectedShard == -1) return;
            Confirmed?.Invoke(_selectedSlot, _selectedShard);
        }

        /// <summary>enchant.ts move (Enchant Select): the slot goes to the item place, or to the shard place.</summary>
        public void Move(int index, bool isShard)
        {
            if (index < 0 || index >= _grid.Count) return;

            // An item (or shards) already selected goes back to its inventory slot.
            if (_selectedSlot != -1 && !isShard) _grid.Copy(_selectedSlot, KeyOf(_itemSprite), _grid.CountTextAt(_selectedSlot));
            if (_selectedShard != -1 && isShard) _grid.Copy(_selectedShard, KeyOf(_shardSprite), _shardsCount.text);

            if (isShard) _selectedShard = index;
            else _selectedSlot = index;

            var sprite = Kit.ItemSprite(_grid.KeyAt(index));
            var target = isShard ? _selectedShards : _selectedItem;
            target.sprite = sprite;
            target.enabled = sprite != null;
            if (isShard)
            {
                _shardSprite = sprite;
                _shardsCount.text = _grid.CountTextAt(index);
            }
            else _itemSprite = sprite;

            _keys[isShard ? 1 : 0] = _grid.KeyAt(index);
            _grid.Copy(index, "", "");
        }

        // The keys of the item and the shards placed in the selection slots (the original keeps them as images).
        readonly string[] _keys = { "", "" };

        string KeyOf(Sprite sprite) => sprite == null ? "" : sprite == _itemSprite ? _keys[0] : _keys[1];

        void Clear()
        {
            ClearSelections();
            Synchronize();
        }

        void ClearSelections()
        {
            _selectedSlot = _selectedShard = -1;
            _selectedItem.enabled = _selectedShards.enabled = false;
            _itemSprite = _shardSprite = null;
            _keys[0] = _keys[1] = "";
            _shardsCount.text = "";
        }

        public void Synchronize()
        {
            if (!IsVisible) return;
            ClearSelections();
            for (var i = 0; i < _grid.Count; i++)
            {
                var slot = _inventory.GetSlot(i);
                _grid.Copy(i, slot.Key, Util.GetCount(slot.Count));
            }
        }

        /// <summary>For tests.</summary>
        public SlotGrid Grid => _grid;
        public int SelectedSlot => _selectedSlot;
        public int SelectedShard => _selectedShard;
    }
}
