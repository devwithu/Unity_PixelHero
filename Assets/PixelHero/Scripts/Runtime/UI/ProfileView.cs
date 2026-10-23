using System;
using System.Collections.Generic;
using PixelHero.Common;
using PixelHero.Entities;
using PixelHero.Js;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// The profile window's state page (menu/profile/profile.ts, impl/state.ts): level, total experience, the
    /// weapon's attack styles and the 12 equipment slots; a slot click asks the server to unequip it, a style click
    /// to switch to it. The skills and abilities pages and the guild / leaderboard / equipment buttons come later.
    /// </summary>
    public sealed class ProfileView : MenuPanel
    {
        // #profile-equipment-slots positions (left, top) in em; ring has no left (0).
        static readonly (Modules.Equipment type, float left, float top)[] SlotPositions =
        {
            (Modules.Equipment.Helmet, 4, 0), (Modules.Equipment.Pendant, 0.25f, 1.25f), (Modules.Equipment.Arrows, 7.75f, 1.25f),
            (Modules.Equipment.Chestplate, 2.75f, 2.5f), (Modules.Equipment.Weapon, 5.25f, 2.5f), (Modules.Equipment.Shield, 8, 3.75f),
            (Modules.Equipment.Ring, 0, 3.75f), (Modules.Equipment.ArmourSkin, 2.75f, 5), (Modules.Equipment.WeaponSkin, 5.25f, 5),
            (Modules.Equipment.Legplates, 0.25f, 6.25f), (Modules.Equipment.Cape, 7.75f, 6.25f), (Modules.Equipment.Boots, 4, 7.5f)
        };

        // The .attack-style-* icons' order in the sheet (scss/game/impl/_profile.scss).
        static readonly Modules.AttackStyle[] StyleIcons =
        {
            Modules.AttackStyle.Stab, Modules.AttackStyle.Slash, Modules.AttackStyle.Defensive, Modules.AttackStyle.Crush,
            Modules.AttackStyle.Shared, Modules.AttackStyle.Hack, Modules.AttackStyle.Chop, Modules.AttackStyle.Accurate,
            Modules.AttackStyle.Fast, Modules.AttackStyle.Focused, Modules.AttackStyle.LongRange
        };

        readonly UiKit _kit;
        readonly Player _player;
        readonly Text _level, _experience;
        readonly RectTransform _styles;
        readonly Dictionary<Modules.Equipment, (Image image, Text count)> _slots = new Dictionary<Modules.Equipment, (Image, Text)>();
        readonly Dictionary<string, Sprite> _icons = new Dictionary<string, Sprite>();
        string _stylesShown;

        /// <summary>state.ts unequipCallback → Packets.Equipment {opcode: Unequip, type}.</summary>
        public event Action<Modules.Equipment> Unequip;

        /// <summary>state.ts styleCallback → Packets.Equipment {opcode: Style, style}.</summary>
        public event Action<Modules.AttackStyle> Style;

        /// <summary>#equipment-button: opens the equipments window.</summary>
        public event Action EquipmentRequested;

        /// <summary>#guilds-button: opens the guilds window.</summary>
        public event Action GuildsRequested;

        /// <summary>#leaderboards-button: toggles the leaderboards window (menu.ts toggleButton).</summary>
        public event Action LeaderboardsRequested;

        Button _leaderboardsButton;

        /// <summary>
        /// The leaderboards button works only with a hub configured (the lists come from the hub); without one it
        /// stays disabled, as before the hub was ported.
        /// </summary>
        public void EnableLeaderboards() => _leaderboardsButton.interactable = true;

        readonly RectTransform _statePage;
        readonly SkillsPage _skills;
        int _activePage;
        Image _previous, _next;
        Button _previousButton, _nextButton;

        /// <summary>
        /// #profile-navigator: previous / next (characterdialogsheet 21×17 at 0,223 and 21,223, 2×; the disabled
        /// frames 17px lower), centred 1em above the bottom.
        /// </summary>
        void BuildNavigator()
        {
            (Image, Button) Arrow(int column, float left)
            {
                var image = UiKit.Box(Root, column == 0 ? "previous" : "next").gameObject.AddComponent<Image>();
                var rect = image.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0);
                rect.anchoredPosition = new Vector2(left, UiKit.Em);
                rect.sizeDelta = new Vector2(42, 34);
                var button = image.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.None;
                button.onClick.AddListener(() => Navigate(column == 0 ? -1 : 1));
                return (image, button);
            }

            (_previous, _previousButton) = Arrow(0, -21);
            (_next, _nextButton) = Arrow(1, 21);
            UpdatePages();
        }

        /// <summary>profile.ts handleNavigation: disabled arrows do nothing.</summary>
        public void Navigate(int direction)
        {
            if ((direction < 0 && _activePage <= 0) || (direction > 0 && _activePage >= 1)) return;
            _activePage += direction;
            UpdatePages();
        }

        /// <summary>profile.ts update: clamp the page, show it, disable the arrow at either end.</summary>
        void UpdatePages()
        {
            _activePage = Math.Clamp(_activePage, 0, 1);
            _statePage.gameObject.SetActive(_activePage == 0);
            if (_activePage == 1) _skills.Show();
            else _skills.Hide();

            _previous.sprite = _kit.Region("characterdialogsheet", 0, _activePage <= 0 ? 240 : 223, 21, 17);
            _next.sprite = _kit.Region("characterdialogsheet", 21, _activePage >= 1 ? 240 : 223, 21, 17);
            _previousButton.interactable = _activePage > 0;
            _nextButton.interactable = _activePage < 1;
        }

        public int ActivePage => _activePage;
        public SkillsPage Skills => _skills;

        public ProfileView(UiKit kit, RectTransform parent, Player player) : base(Panel(kit, parent))
        {
            _kit = kit;
            _player = player;
            _statePage = UiKit.Fill(UiKit.Box(Root, "#state-page"));
            _skills = new SkillsPage(kit, Root, player);
            BuildNavigator();

            // #state-page: padding 1.5em 1em 0.5em, height 12em; stats on the left, the slots (10em × 9.5em) on the right.
            const float contentLeft = UiKit.Em, contentTop = 1.5f * UiKit.Em, contentHeight = 10 * UiKit.Em;
            var slotsLeft = contentLeft + 12 * UiKit.Em;
            var slotsTop = contentTop + (contentHeight - 9.5f * UiKit.Em) / 2;
            var statsWidth = 11.5f * UiKit.Em;

            var y = contentTop + 1.5f * UiKit.Em;
            _level = _kit.Label(_statePage, "#profile-level", "", _kit.Body, 1.1f * UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(_level.rectTransform, contentLeft, y, statsWidth, 1.1f * UiKit.Em);
            y += 1.1f * UiKit.Em + 0.5f * UiKit.Em;
            _experience = _kit.Label(_statePage, "#profile-experience", "", _kit.Body, 1.1f * UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(_experience.rectTransform, contentLeft, y, statsWidth, 1.1f * UiKit.Em);
            y += 1.1f * UiKit.Em + 0.75f * UiKit.Em;

            // #attack-style-list: row, gap 0.75em, space-evenly, 3em high.
            _styles = UiKit.Box(_statePage, "#attack-style-list");
            UiKit.Place(_styles, contentLeft, y, statsWidth, 3 * UiKit.Em);

            // .button-row: 12em, gap 0.55em, space-evenly: guilds, leaderboards, equipment (frame_buttons 19×19, 2×).
            y += 3 * UiKit.Em + 0.5f * UiKit.Em;
            var row = UiKit.Box(_statePage, "button-row");
            UiKit.Place(row, contentLeft + (statsWidth - 12 * UiKit.Em) / 2, y, 12 * UiKit.Em, 38);
            var rowGap = (12 * UiKit.Em - 3 * 38) / 4;
            for (var i = 0; i < 3; i++)
            {
                var index = i;
                Sprite Frame(int f) => _kit.Region("frame_buttons", f * 19, index * 19, 19, 19);
                var image = UiKit.Box(row, new[] { "#guilds-button", "#leaderboards-button", "#equipment-button" }[i]).gameObject.AddComponent<Image>();
                image.sprite = Frame(0);
                UiKit.Place(image.rectTransform, rowGap + i * (38 + rowGap), 0, 38, 38);
                var rowButton = image.gameObject.AddComponent<Button>();
                rowButton.transition = Selectable.Transition.SpriteSwap;
                rowButton.spriteState = new SpriteState { highlightedSprite = Frame(1), pressedSprite = Frame(2), selectedSprite = Frame(0) };
                if (i == 2) rowButton.onClick.AddListener(() => EquipmentRequested?.Invoke());
                else if (i == 0) rowButton.onClick.AddListener(() => GuildsRequested?.Invoke());
                else
                {
                    // Leaderboards need the hub (EnableLeaderboards).
                    rowButton.onClick.AddListener(() => LeaderboardsRequested?.Invoke());
                    rowButton.interactable = false;
                    _leaderboardsButton = rowButton;
                }
            }

            foreach (var (type, left, top) in SlotPositions)
            {
                // li.equipment-slot.slice-list-item: 2em plus 0.25em padding, list-item.png (border 0.5em).
                var slot = _kit.Sliced(_statePage, $"equipment-slot-{type.ToString().ToLowerInvariant()}", "list-item", 0.5f * UiKit.Em);
                slot.raycastTarget = true;
                UiKit.Place(slot.rectTransform, slotsLeft + left * UiKit.Em, slotsTop + top * UiKit.Em, 2.5f * UiKit.Em, 2.5f * UiKit.Em);
                var button = slot.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState { highlightedSprite = _kit.Slice("list-item-hover"), selectedSprite = _kit.Slice("list-item") };
                var equipment = type;
                button.onClick.AddListener(() => Unequip?.Invoke(equipment));

                var image = UiKit.Box(slot.transform, "equipment-slot-image").gameObject.AddComponent<Image>();
                UiKit.Place(image.rectTransform, 0.25f * UiKit.Em, 0.25f * UiKit.Em, 2 * UiKit.Em, 2 * UiKit.Em);
                image.raycastTarget = false;

                Text count = null;
                if (type == Modules.Equipment.Arrows)
                {
                    count = _kit.Label(slot.transform, "equipment-slot-arrows-count", "", _kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
                    UiKit.Place(count.rectTransform, 0, -UiKit.Em, 2 * UiKit.Em, UiKit.Em);
                }

                _slots[type] = (image, count);
            }
        }

        /// <summary>A click on an equipment slot.</summary>
        public void ClickSlot(Modules.Equipment type) => Unequip?.Invoke(type);

        // .slice-page#profile-container: right 0.5em, bottom 3.5em, 24em × 15em, dialog.png (border 1em).
        static RectTransform Panel(UiKit kit, RectTransform parent)
        {
            var page = kit.Sliced(parent, "#profile-container", "dialog", UiKit.Em);
            page.raycastTarget = true;
            var rect = page.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 0);
            rect.anchoredPosition = new Vector2(-0.5f * UiKit.Em, 3.5f * UiKit.Em);
            rect.sizeDelta = new Vector2(24 * UiKit.Em, 15 * UiKit.Em);
            return rect;
        }

        Sprite Icon(string key)
        {
            if (_icons.TryGetValue(key, out var sprite)) return sprite;
            var texture = _kit.Catalog.GetTexture(key);
            sprite = texture == null ? null : Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100);
            return _icons[key] = sprite;
        }

        /// <summary>state.ts synchronize.</summary>
        public void Synchronize()
        {
            _level.text = Localization.Get("profile.level", ("level", _player.Level is double l ? JsValue.NumberToString(l) : "undefined"));
            _experience.text = $"{Localization.Get("profile.totalExp")} {JsValue.NumberToString(_player.GetTotalExperience())}";

            foreach (var pair in _slots)
            {
                var equipment = _player.Equipments[(int)pair.Key];
                // Util.getImageURL(key) || Util.getEquipmentPlaceholderURL(type).
                var sprite = !string.IsNullOrEmpty(equipment.Key)
                    ? Icon("sprites/items/" + Util.ItemImageKey(equipment.Key))
                    : Icon("interface/equipment/" + pair.Key.ToString().ToLowerInvariant());
                pair.Value.image.sprite = sprite;
                pair.Value.image.enabled = sprite != null;
                if (pair.Value.count != null) pair.Value.count.text = equipment.Count > 0 ? JsValue.NumberToString(equipment.Count.Value) : "";
            }

            LoadAttackStyles();
        }

        /// <summary>
        /// state.ts loadAttackStyles: one .attack-style-box per weapon style (characterdialogsheet 23×23 at 384,256,
        /// 1.5×, hover and active frames 25px apart; the current style is active) with its icon (17×17 at 384,224,
        /// 19px apart) 0.875em from the top.
        /// </summary>
        void LoadAttackStyles()
        {
            var weapon = _player.GetWeapon();
            var shown = $"{weapon.AttackStyle}:{string.Join(",", weapon.AttackStyles)}";
            if (shown == _stylesShown) return;
            _stylesShown = shown;

            for (var i = _styles.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_styles.GetChild(i).gameObject);

            var count = weapon.AttackStyles.Count;
            var size = 23 * 1.5f;
            var gap = count > 0 ? (_styles.sizeDelta.x - count * size) / (count + 1) : 0;
            for (var i = 0; i < count; i++)
            {
                var style = (Modules.AttackStyle)weapon.AttackStyles[i];
                var active = weapon.AttackStyle == (int)style;
                Sprite Box(int frame) => _kit.Region("characterdialogsheet", 384 + frame * 25, 256, 23, 23);

                var box = UiKit.Box(_styles, "attack-style-box").gameObject.AddComponent<Image>();
                box.sprite = Box(active ? 2 : 0);
                UiKit.Place(box.rectTransform, gap + i * (size + gap), 0, size, size);
                var button = box.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState { highlightedSprite = Box(1), pressedSprite = Box(2), selectedSprite = Box(active ? 2 : 0) };
                button.onClick.AddListener(() => Style?.Invoke(style));

                var index = Array.IndexOf(StyleIcons, style);
                if (index < 0) continue;
                var icon = UiKit.Box(box.transform, "attack-style").gameObject.AddComponent<Image>();
                icon.sprite = _kit.Region("characterdialogsheet", 384 + index * 19, 224, 17, 17);
                icon.raycastTarget = false;
                UiKit.Place(icon.rectTransform, (size - 17 * 1.5f) / 2, 0.875f * UiKit.Em * 0.5f, 17 * 1.5f, 17 * 1.5f);
            }
        }

        public override void Show()
        {
            Synchronize();
            base.Show();
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            if (!IsVisible) return;
            Synchronize();
            _skills.Synchronize();
        }

        public override void Hide()
        {
            base.Hide();
            _skills.HideInfo();
        }
    }
}
