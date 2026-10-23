using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PixelHero.Common;
using PixelHero.Entities;
using PixelHero.Js;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// menu/interact.ts: the right-click menu (#interact.slice-dialog, 8em wide, a row of action buttons from
    /// main.png) at the pointer, kept inside the screen. Items can be examined; mobs attacked or examined; players
    /// followed (attacked in PvP), traded with and added as friends.
    /// </summary>
    public sealed class InteractView : MenuPanel
    {
        static readonly string[] SpriteRows =
        {
            "", Modules.MenuActions.Attack, Modules.MenuActions.Equip, Modules.MenuActions.DropOne, Modules.MenuActions.DropMany,
            Modules.MenuActions.Eat, Modules.MenuActions.Interact, Modules.MenuActions.Trade, Modules.MenuActions.Potion,
            Modules.MenuActions.Follow, Modules.MenuActions.Examine, Modules.MenuActions.AddFriend
        };

        readonly UiKit _kit;
        readonly Player _player;
        readonly List<GameObject> _buttons = new List<GameObject>();
        readonly List<string> _actions = new List<string>();

        /// <summary>The entity the menu was opened on (input.ts interactEntity).</summary>
        public Entity Entity { get; private set; }

        public event Action<string, Entity> Action;

        public InteractView(UiKit kit, RectTransform parent, Player player) : base(Frame(kit, parent))
        {
            _kit = kit;
            _player = player;
        }

        static RectTransform Frame(UiKit kit, RectTransform parent)
        {
            var dialog = kit.Sliced(parent, "#interact", "dialog", UiKit.Em);
            dialog.raycastTarget = true;
            UiKit.TopLeft(dialog.rectTransform);
            dialog.rectTransform.sizeDelta = new Vector2(8 * UiKit.Em, 2 * UiKit.Em + 1.675f * UiKit.Em);
            return dialog.rectTransform;
        }

        public IReadOnlyList<string> Actions => _actions;

        /// <summary>interact.ts show: at <paramref name="position"/> (canvas units, y down) when there are actions.</summary>
        public void Show(Vector2 position, Vector2 screen, Entity entity, bool pvp)
        {
            foreach (var button in _buttons) UnityEngine.Object.Destroy(button);
            _buttons.Clear();
            _actions.Clear();

            if (entity.IsItem) _actions.Add(Modules.MenuActions.Examine);
            if (entity.IsMob) _actions.AddRange(new[] { Modules.MenuActions.Attack, Modules.MenuActions.Examine });
            if (entity.IsPlayer)
            {
                _actions.Add(pvp ? Modules.MenuActions.Attack : Modules.MenuActions.Follow);
                _actions.Add(Modules.MenuActions.Trade);
                if (!_player.HasFriend(entity.Name)) _actions.Add(Modules.MenuActions.AddFriend);
            }

            if (_actions.Count == 0) return;
            Entity = entity;

            // flex row, space-evenly, inside the dialog's padding (1em 0.675em 0.675em).
            var inner = 8 * UiKit.Em - 1.35f * UiKit.Em;
            var gap = (inner - _actions.Count * 2 * UiKit.Em) / (_actions.Count + 1);
            for (var i = 0; i < _actions.Count; i++)
            {
                var action = _actions[i];
                var row = Array.IndexOf(SpriteRows, action);
                Sprite Frame(int f) => _kit.Region("main", 63 + f * 16, row * 16, 16, 16);
                var image = UiKit.Box(Root, $"action-{action}").gameObject.AddComponent<Image>();
                image.sprite = Frame(0);
                UiKit.Place(image.rectTransform, 0.675f * UiKit.Em + gap + i * (2 * UiKit.Em + gap), UiKit.Em, 2 * UiKit.Em, 2 * UiKit.Em);
                var button = image.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState { highlightedSprite = Frame(2), pressedSprite = Frame(1), selectedSprite = Frame(0) };
                button.onClick.AddListener(() => Press(action));
                _buttons.Add(image.gameObject);
            }

            var size = Root.sizeDelta;
            var x = position.x + size.x > screen.x ? screen.x - size.x : position.x;
            var y = position.y + size.y > screen.y ? screen.y - size.y : position.y;
            Root.anchoredPosition = new Vector2(x, -y);
            base.Show();
        }

        /// <summary>input.ts handleInteract, then hide.</summary>
        public void Press(string action)
        {
            if (Entity != null) Action?.Invoke(action, Entity);
            Hide();
        }

        /// <summary>interact.ts hide → input.ts onClose: forget the entity.</summary>
        public override void Hide()
        {
            base.Hide();
            Entity = null;
        }
    }

    /// <summary>
    /// menu/friends.ts: #friends-container (.slice-page, right 1em, 20em × 15em) with the friend list (name and
    /// world: green on this world, yellow on another, red offline) and the add / remove buttons that open the
    /// name popup. A click on an online friend starts a private message.
    /// </summary>
    public sealed class FriendsView : MenuPanel
    {
        readonly UiKit _kit;
        readonly Player _player;
        readonly Func<double?> _serverId;
        readonly RectTransform _list;
        readonly float _listWidth;
        readonly List<(string username, RectTransform row, Text world)> _rows = new List<(string, RectTransform, Text)>();
        readonly CanvasGroup _page;
        readonly RectTransform _popup;
        readonly InputField _input;
        bool _removeActive;

        /// <summary>(username, remove) → Packets.Friends {opcode: Add / Remove, username}.</summary>
        public event Action<string, bool> Confirmed;

        /// <summary>A click on an online friend (chat.ts privateMessage).</summary>
        public event Action<string> Message;

        /// <summary>A line for the chat (friends.ts notify: WORLD, #0dd9e0, bold).</summary>
        public event Action<string> Notify;

        public FriendsView(UiKit kit, RectTransform parent, Player player, Func<double?> serverId) : base(Frame(kit, parent))
        {
            _kit = kit;
            _player = player;
            _serverId = serverId;

            // #friends-page: column, gap 0.5em: the list (flex 1, .slice-list) and the buttons.
            var page = UiKit.Box(Root, "#friends-page");
            UiKit.Place(page, 0.675f * UiKit.Em, UiKit.Em, 20 * UiKit.Em - 1.35f * UiKit.Em, 15 * UiKit.Em - 1.675f * UiKit.Em);
            _page = page.gameObject.AddComponent<CanvasGroup>();
            var width = page.sizeDelta.x;
            var listHeight = page.sizeDelta.y - 34 - 0.5f * UiKit.Em;

            var list = kit.Sliced(page, "#friends-list", "list", UiKit.Em);
            UiKit.Place(list.rectTransform, 0, 0, width, listHeight);
            var inner = UiKit.Box(list.transform, "padding");
            UiKit.Place(inner, 0.5f * UiKit.Em, 0.5f * UiKit.Em, width - UiKit.Em, listHeight - UiKit.Em);
            _list = UiKit.ScrollView(inner, "ul");
            _listWidth = inner.sizeDelta.x;

            // .dialog-buttons: .add / .remove (characterdialogsheet 21×17 at y 274, one row each, 2×).
            Button(page, "#add-friend", 0, width / 3 - 21, listHeight + 0.5f * UiKit.Em, () => ShowPopup(false));
            Button(page, "#remove-friend", 1, width * 2 / 3 - 21, listHeight + 0.5f * UiKit.Em, () => ShowPopup(true));

            // dialog#popup-container.slice-dialog: the name input and confirm / cancel.
            var popup = kit.Sliced(Root, "#popup-container", "dialog", UiKit.Em);
            popup.raycastTarget = true;
            _popup = UiKit.Centre(popup.rectTransform, 12 * UiKit.Em, 5.5f * UiKit.Em);
            _input = kit.Input(_popup, "#popup-friend-input", "", 0.9f * UiKit.Em, false, 32);
            UiKit.Place((RectTransform)_input.transform, 0.675f * UiKit.Em, UiKit.Em, 12 * UiKit.Em - 1.35f * UiKit.Em, 2 * UiKit.Em);
            DialogButton("#popup-friend-confirm", 0, 3 * UiKit.Em - 21, HandleConfirm);
            DialogButton("#popup-friend-cancel", 1, 9 * UiKit.Em - 21, HidePopup);
            _popup.gameObject.SetActive(false);
        }

        // .slice-page: bottom 3.5em, 15em high, dialog.png; #friends-container right 1em, 20em wide.
        static RectTransform Frame(UiKit kit, RectTransform parent)
        {
            var page = kit.Sliced(parent, "#friends-container", "dialog", UiKit.Em);
            page.raycastTarget = true;
            var rect = page.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = new Vector2(-UiKit.Em, 3.5f * UiKit.Em);
            rect.sizeDelta = new Vector2(20 * UiKit.Em, 15 * UiKit.Em);
            return rect;
        }

        void Button(RectTransform parent, string name, int row, float left, float top, Action onClick)
        {
            Sprite Frame(int f) => _kit.Region("characterdialogsheet", f * 21, 274 + row * 17, 21, 17);
            var image = UiKit.Box(parent, name).gameObject.AddComponent<Image>();
            image.sprite = Frame(0);
            UiKit.Place(image.rectTransform, left, top, 42, 34);
            var button = image.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Frame(2), pressedSprite = Frame(1), selectedSprite = Frame(0) };
            button.onClick.AddListener(() => onClick());
        }

        void DialogButton(string name, int row, float left, Action onClick)
        {
            Sprite Frame(int f) => _kit.Region("characterdialogsheet", f * 21, 308 + row * 14, 21, 14);
            var image = UiKit.Box(_popup, name).gameObject.AddComponent<Image>();
            image.sprite = Frame(0);
            UiKit.Place(image.rectTransform, left, 3.25f * UiKit.Em, 42, 28);
            var button = image.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Frame(2), pressedSprite = Frame(1), selectedSprite = Frame(0) };
            button.onClick.AddListener(() => onClick());
        }

        /// <summary>friends.ts getMaxLength: 16 on large screens, else 10.</summary>
        static int MaxLength => Screen.width > 1000 ? 16 : 10;

        /// <summary>util.ts formatName: every word capitalised, the rest lower case.</summary>
        public static string FormatName(string name) =>
            Regex.Replace(name ?? "", @"\w\S*", m => char.ToUpperInvariant(m.Value[0]) + m.Value.Substring(1).ToLowerInvariant());

        /// <summary>friends.ts handle.</summary>
        public void Handle(Opcodes.Friends opcode, string username, bool status, double? serverId)
        {
            switch (opcode)
            {
                case Opcodes.Friends.List:
                    foreach (var friend in _player.Friends.Values) CreateElement(friend.Username, friend.Online, friend.ServerId);
                    break;
                case Opcodes.Friends.Add:
                    CreateElement(username, status, serverId);
                    break;
                case Opcodes.Friends.Remove:
                    RemoveElement(username);
                    break;
                case Opcodes.Friends.Status:
                    UpdateStatus(username, status, serverId);
                    break;
            }
        }

        static Color WorldColour(bool online, bool sameWorld) => UiKit.Css(!online ? "#e00a0a" : sameWorld ? "#00a800" : "#e0e000");

        void CreateElement(string username, bool online, double? serverId)
        {
            username ??= "undefined";
            var height = 1.2f * UiKit.Em + UiKit.Em;
            var top = _rows.Count * height;
            var row = _kit.Sliced(_list, "li", "list-item", 0.5f * UiKit.Em);
            row.raycastTarget = true;
            UiKit.Place(row.rectTransform, 0, top, _listWidth, height);
            var lower = username.ToLowerInvariant();
            row.gameObject.AddComponent<Button>().onClick.AddListener(() => HandleMessage(lower));

            var shown = username.Length > MaxLength ? username.Substring(0, MaxLength) + "..." : username;
            var name = _kit.Label(row.transform, "p", FormatName(shown), _kit.Body, 1.2f * UiKit.Em, "#eee", TextAnchor.MiddleLeft, true);
            UiKit.Place(name.rectTransform, 0.5f * UiKit.Em, 0, _listWidth / 2, height);
            var world = _kit.Label(row.transform, "p", "", _kit.Body, 1.2f * UiKit.Em, "#eee", TextAnchor.MiddleRight, true);
            UiKit.Place(world.rectTransform, _listWidth / 2, 0, _listWidth / 2 - 0.5f * UiKit.Em, height);
            world.text = online ? Localization.Get("friends.world", ("id", Js(serverId))) : Localization.Get("friends.offline");
            world.color = WorldColour(online, _serverId() == serverId);

            _rows.Add((lower, row.rectTransform, world));
            _list.sizeDelta = new Vector2(0, top + height);
        }

        static string Js(double? value) => value is double v ? JsValue.NumberToString(v) : "undefined";

        void RemoveElement(string username)
        {
            var key = (username ?? "undefined").ToLowerInvariant();
            var index = _rows.FindIndex(r => r.username == key);
            if (index >= 0)
            {
                UnityEngine.Object.Destroy(_rows[index].row.gameObject);
                _rows.RemoveAt(index);
                // The remaining rows close up (the list is flowing HTML).
                var height = 1.2f * UiKit.Em + UiKit.Em;
                for (var i = 0; i < _rows.Count; i++) _rows[i].row.anchoredPosition = new Vector2(0, -i * height);
                _list.sizeDelta = new Vector2(0, _rows.Count * height);
            }
            _player.Friends.Remove(username ?? "undefined");
        }

        /// <summary>friends.ts updateStatus: the world text and colour, and a chat line.</summary>
        void UpdateStatus(string username, bool online, double? serverId)
        {
            var friend = _player.Friends[username ?? "undefined"];
            if (friend == null) return;
            var row = _rows.FirstOrDefault(r => r.username == (username ?? "").ToLowerInvariant());
            if (row.world == null) return;

            if (online)
            {
                // The original only adds a class, so a red "Offline" colour stays under the new one.
                row.world.color = WorldColour(true, _serverId() == serverId);
                row.world.text = Localization.Get("friends.world", ("id", Js(friend.ServerId)));
                Notify?.Invoke(Localization.Get("friends.loggedIn", ("name", FormatName(username))));
            }
            else
            {
                row.world.color = WorldColour(false, false);
                row.world.text = Localization.Get("friends.offline");
                Notify?.Invoke(Localization.Get("friends.loggedOut", ("name", FormatName(username))));
            }
        }

        void HandleMessage(string username)
        {
            if (_player.Friends[username]?.Online != true) return;
            Message?.Invoke(username);
        }

        void ShowPopup(bool remove)
        {
            _removeActive = remove;
            ((Text)_input.placeholder).text = Localization.Get(remove ? "friends.toRemove" : "friends.toAdd");
            _popup.gameObject.SetActive(true);
            _popup.SetAsLastSibling();
            _page.alpha = 0.25f;
            _input.Select();
            _input.ActivateInputField();
        }

        public void HidePopup()
        {
            _removeActive = false;
            _popup.gameObject.SetActive(false);
            _page.alpha = 1;
        }

        public bool PopupActive => _popup.gameObject.activeSelf;

        /// <summary>friends.ts handleConfirm: 1–32 characters, then the packet.</summary>
        public void HandleConfirm()
        {
            var username = _input.text;
            var remove = _removeActive;
            _input.text = "";
            HidePopup();
            if (string.IsNullOrEmpty(username) || username.Length > 32) return;
            Confirmed?.Invoke(username, remove);
        }

        /// <summary>For tests.</summary>
        public IReadOnlyList<string> Usernames => _rows.Select(r => r.username).ToList();
        public string WorldText(string username) => _rows.FirstOrDefault(r => r.username == username).world?.text;
        public void Type(string text) => _input.text = text;
        public void OpenPopup(bool remove) => ShowPopup(remove);
    }

    /// <summary>
    /// menu/trade.ts: #trade (.slice-container, 37.875em × 21.5em): "Your Inventory" (5 columns; a click offers
    /// one, a right click asks for an amount), both players' offers (a click on your own takes it back), the status
    /// line and ACCEPT. The offer slots change only on the server's Add / Remove.
    /// </summary>
    public sealed class TradeView : ContainerWindow
    {
        readonly IInventorySource _inventory;
        readonly SlotGrid _inventoryGrid, _playerGrid, _otherGrid;
        readonly Text _playerName, _otherName, _status;
        readonly CountDialog _countDialog;
        readonly int[] _playerSlotIndex = Enumerable.Repeat(-1, InventoryView.Size).ToArray();
        int _inventoryIndex = -1;

        /// <summary>(container type, index, count) → Packets.Trade {opcode: Add / Remove, index, count}.</summary>
        public event Action<int, int, double> Selected;
        public event Action Accepted;
        public event Action Closed;

        public TradeView(UiKit kit, RectTransform parent, IInventorySource inventory)
            : base(kit, parent, "#trade", 37.875f * UiKit.Em, 21.5f * UiKit.Em)
        {
            _inventory = inventory;

            // #trade-inventory: the title (with the frame ornament) and the slots, centred vertically.
            const float left = 13 * UiKit.Em;
            var title = Kit.Label(Content, "title-decoration", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(title.rectTransform, 0, 0, left, UiKit.Em);
            Localization.LanguageChanged += () => title.text = Localization.Get("trade.yourInventory");
            title.text = Localization.Get("trade.yourInventory");
            var ornament = UiKit.Box(Content, "ornament").gameObject.AddComponent<Image>();
            ornament.sprite = Kit.Region("guilds/frames", 286, 8, 78, 6);
            UiKit.Place(ornament.rectTransform, (left - 156) / 2, UiKit.Em, 156, 12);

            var inventoryBox = Inner("#trade-inventory-slots", 0, 1.75f * UiKit.Em, left, ContentHeight - 1.75f * UiKit.Em);
            _inventoryGrid = new SlotGrid(kit, inventoryBox, InventoryView.Size, (int)Modules.ContainerType.Inventory, 5);
            var dimmed = inventoryBox.parent.gameObject.AddComponent<CanvasGroup>();
            _inventoryGrid.Clicked += index => Select((int)Modules.ContainerType.Inventory, index, null);
            _inventoryGrid.SecondaryClicked += ShowAmountDialog;

            // #trade-player: two offers side by side, then the status and ACCEPT.
            var right = left + 0.5f * UiKit.Em;
            var column = (ContentWidth - right - 0.5f * UiKit.Em) / 2;
            var offersHeight = ContentHeight - 0.75f * UiKit.Em - 2 * UiKit.Em - UiKit.Em;
            _playerName = Kit.Label(Content, "#trade-player-name", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(_playerName.rectTransform, right, 0, column, UiKit.Em);
            _otherName = Kit.Label(Content, "#trade-oplayer-name", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(_otherName.rectTransform, right + column + 0.5f * UiKit.Em, 0, column, UiKit.Em);
            _playerGrid = new SlotGrid(kit, Inner("#trade-player-slots", right, 1.5f * UiKit.Em, column, offersHeight - 1.5f * UiKit.Em),
                InventoryView.Size, (int)Modules.ContainerType.Trade, 5);
            _otherGrid = new SlotGrid(kit, Inner("#trade-oplayer-slots", right + column + 0.5f * UiKit.Em, 1.5f * UiKit.Em, column,
                offersHeight - 1.5f * UiKit.Em), InventoryView.Size, (int)Modules.ContainerType.Trade, 5);
            _playerGrid.Clicked += index => Select((int)Modules.ContainerType.Trade, index, null);

            var rightWidth = ContentWidth - right;
            _status = Kit.Label(Content, "#trade-status", "", Kit.Body, 0.75f * UiKit.Em, "#eee", TextAnchor.UpperCenter);
            _status.supportRichText = true;
            UiKit.Place(_status.rectTransform, right, offersHeight + 0.25f * UiKit.Em, rightWidth, 0.75f * UiKit.Em);
            var accept = Kit.Button(Content, "#trade-accept", "", () => Accepted?.Invoke());
            UiKit.Place((RectTransform)accept.transform, right + (rightWidth - 10 * UiKit.Em) / 2, ContentHeight - 2 * UiKit.Em, 10 * UiKit.Em, 2 * UiKit.Em);
            var label = accept.GetComponentInChildren<Text>();
            Localization.LanguageChanged += () => label.text = Localization.Get("trade.accept");
            label.text = Localization.Get("trade.accept");

            _countDialog = new CountDialog(kit, Root, "#trade-count", "trade.amount", dimmed);
            _countDialog.Accepted += SendAmount;
        }

        RectTransform Inner(string name, float left, float top, float width, float height)
        {
            var box = Kit.Sliced(Content, name, "inner-container", 2 * UiKit.Em);
            UiKit.Place(box.rectTransform, left, top, width, height);
            var inner = UiKit.Box(box.transform, "padding");
            UiKit.Place(inner, 0.75f * UiKit.Em, 0.75f * UiKit.Em, width - 1.5f * UiKit.Em, height - 1.5f * UiKit.Em);
            return inner;
        }

        public CountDialog CountDialog => _countDialog;

        /// <summary>trade.ts show (Trade Open): both names and the inventory copy.</summary>
        public void Open(string playerName, string otherName)
        {
            Show();
            Synchronize();
            _playerName.text = playerName ?? "undefined";
            _otherName.text = otherName ?? "undefined";
        }

        /// <summary>trade.ts hide: tells the server unless the server closed it; clears everything.</summary>
        public void Close(bool fromServer)
        {
            base.Hide();
            if (!fromServer) Closed?.Invoke();
            for (var i = 0; i < InventoryView.Size; i++)
            {
                _playerGrid.Set(i, "", null);
                _otherGrid.Set(i, "", null);
            }
            _inventoryIndex = -1;
            _status.text = "";
        }

        public override void Hide() => Close(false);

        public void Synchronize()
        {
            if (!IsVisible) return;
            for (var i = 0; i < _inventoryGrid.Count; i++)
            {
                var slot = _inventory.GetSlot(i);
                _inventoryGrid.Copy(i, slot.Key, Util.GetCount(slot.Count));
            }
        }

        void Select(int type, int index, double? count)
        {
            _inventoryIndex = index;
            // count || 1
            Selected?.Invoke(type, index, count is double c && c != 0 && !double.IsNaN(c) ? c : 1);
        }

        void ShowAmountDialog(int index)
        {
            _inventoryIndex = index;
            _countDialog.Show();
        }

        /// <summary>trade.ts sendTradeAmount: counts below 1 are ignored.</summary>
        void SendAmount(double count)
        {
            if (count < 1) return;
            Select((int)Modules.ContainerType.Inventory, _inventoryIndex, count);
        }

        static double InventoryCount(SlotData slot) => slot.Count ?? double.NaN;

        /// <summary>trade.ts add (Trade Add): your offer takes the image of the selected inventory slot.</summary>
        public void Add(int index, double count, string key, bool otherPlayer)
        {
            if (index < 0 || index >= InventoryView.Size) return;
            _status.text = "";

            if (otherPlayer)
            {
                _otherGrid.Copy(index, key, count > 1 ? Util.GetCount(count) : "");
                return;
            }

            _playerGrid.Copy(index, _inventoryGrid.KeyAt(_inventoryIndex), count > 1 ? Util.GetCount(count) : "");
            _playerSlotIndex[index] = _inventoryIndex;

            if (_inventoryIndex < 0) return;
            var actualCount = (_inventory.GetSlot(_inventoryIndex).Count ?? 0) - count;
            if (double.IsNaN(actualCount) || actualCount < 1) _inventoryGrid.Copy(_inventoryIndex, "", "");
            else _inventoryGrid.Copy(_inventoryIndex, _inventoryGrid.KeyAt(_inventoryIndex), Util.GetCount(actualCount));
        }

        /// <summary>trade.ts remove (Trade Remove): your offer goes back to its inventory slot.</summary>
        public void Remove(int index, bool otherPlayer)
        {
            if (index < 0 || index >= InventoryView.Size) return;
            var grid = otherPlayer ? _otherGrid : _playerGrid;

            if (!otherPlayer)
            {
                var inventoryIndex = _playerSlotIndex[index];
                if (inventoryIndex >= 0)
                    _inventoryGrid.Copy(inventoryIndex, grid.KeyAt(index), Util.GetCount(InventoryCount(_inventory.GetSlot(inventoryIndex))));
            }

            grid.Set(index, "", null);
            _status.text = "";
            _inventoryIndex = -1;
        }

        /// <summary>trade.ts accept (Trade Accept): the status message.</summary>
        public void Accept(string messageHtml) => _status.text = MessageFormat.ToRichText(messageHtml ?? "");

        /// <summary>For tests.</summary>
        public SlotGrid InventoryGrid => _inventoryGrid;
        public SlotGrid PlayerGrid => _playerGrid;
        public string Status => _status.text;
    }
}
