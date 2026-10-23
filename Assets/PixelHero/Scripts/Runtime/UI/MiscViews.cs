using System;
using System.Collections.Generic;
using System.Linq;
using PixelHero.Common;
using PixelHero.Entities;
using PixelHero.Js;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// menu/warp.ts: #map-frame (mapframe.png at 1.5×, centred) with the warp icons (mapicons 16×16 at 2×, one
    /// column each, hover row 1, active row 2); a click asks the server to warp (Packets.Warp {id}) and closes.
    /// </summary>
    public sealed class WarpView : MenuPanel
    {
        static readonly (float top, float left)[] Positions =
        {
            (3.75f, 11.25f), (5.15625f, 2.34375f), (6.5625f, 16.40625f), (11.25f, 12.65625f), (11.25f, 7.03125f), (13.125f, 1.875f)
        };

        public event Action<int> Warp;

        public WarpView(UiKit kit, RectTransform parent) : base(Frame(kit, parent))
        {
            for (var i = 0; i < Positions.Length; i++)
            {
                var id = i;
                Sprite Frame(int f) => kit.Region("mapicons", id * 16, f * 16, 16, 16);
                var icon = UiKit.Box(Root, $"#warp{i}").gameObject.AddComponent<Image>();
                icon.sprite = Frame(0);
                UiKit.Place(icon.rectTransform, Positions[i].left * UiKit.Em, Positions[i].top * UiKit.Em, 32, 32);
                var button = icon.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState { highlightedSprite = Frame(1), pressedSprite = Frame(2), selectedSprite = Frame(0) };
                button.onClick.AddListener(() => Select(id));
            }

            Sprite Close(int frame) => kit.Region("guilds/elements", 64 + frame * 12, 0, 11, 11);
            var close = UiKit.Box(Root, "#close-map-frame").gameObject.AddComponent<Image>();
            close.sprite = Close(0);
            var rect = close.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1);
            rect.anchoredPosition = new Vector2(0.25f * UiKit.Em, 0.25f * UiKit.Em);
            rect.sizeDelta = new Vector2(22, 22);
            var closeButton = close.gameObject.AddComponent<Button>();
            closeButton.transition = Selectable.Transition.SpriteSwap;
            closeButton.spriteState = new SpriteState { highlightedSprite = Close(1), pressedSprite = Close(2), selectedSprite = Close(0) };
            closeButton.onClick.AddListener(Hide);
        }

        static RectTransform Frame(UiKit kit, RectTransform parent)
        {
            var frame = UiKit.Box(parent, "#map-frame").gameObject.AddComponent<Image>();
            frame.sprite = kit.Region("mapframe", 0, 0, 270, 180);
            return UiKit.Centre(frame.rectTransform, 270 * 1.5f, 180 * 1.5f);
        }

        /// <summary>warp.ts handleWarp.</summary>
        public void Select(int id)
        {
            Warp?.Invoke(id);
            Hide();
        }
    }

    /// <summary>
    /// menu/crafting.ts: #crafting (crafting.png 208×126 at 2×, centred). The craftable items (greyed out above the
    /// player's skill level), the selected item's requirements, its result count and level, the 1 / 5 / 10 buttons
    /// and the craft button, named after the skill (Cook, Fletch, Smith, Smelt, Brew, else Craft).
    /// </summary>
    public sealed class CraftingView : MenuPanel
    {
        readonly UiKit _kit;
        readonly Player _player;
        readonly RectTransform _options, _requirements;
        readonly Image _resultIcon, _levelIcon;
        readonly Text _resultCount, _levelCount, _name, _craftLabel;
        readonly Image[] _amounts = new Image[3];
        readonly List<(Image frame, string key)> _optionSlots = new List<(Image, string)>();
        string _selectedKey = "";
        int _craftAmount = 1;
        int _type = (int)Modules.Skills.Crafting;

        public event Action<string> Selected;
        public event Action<string, int> Craft;

        public CraftingView(UiKit kit, RectTransform parent, Player player) : base(Frame(kit, parent))
        {
            _kit = kit;
            _player = player;
            var width = 416f;
            var height = 252f;

            // #crafting-options: top 1.375em, left 1.75em, 4 columns, 11.125em × 9.875em, scrolling.
            var optionsBox = UiKit.Box(Root, "#crafting-options");
            UiKit.Place(optionsBox, 1.75f * UiKit.Em, 1.375f * UiKit.Em, 11.125f * UiKit.Em, 9.875f * UiKit.Em);
            _options = UiKit.ScrollView(optionsBox, "ul");

            // #crafting-requirements: top 1.5em, right 1.5em, a row (gap 0.375em) 8.75em × 3em.
            _requirements = UiKit.Box(Root, "#crafting-requirements");
            UiKit.Place(_requirements, width - 1.5f * UiKit.Em - 8.75f * UiKit.Em, 1.5f * UiKit.Em, 8.75f * UiKit.Em, 3 * UiKit.Em);

            (Image, Text) Display(string name, float right)
            {
                var frame = OptionFrame(Root, name, true);
                UiKit.Place(frame.rectTransform, width - right * UiKit.Em - 42, height - 7 * UiKit.Em - 44, 42, 44);
                var icon = Icon(frame.transform);
                var count = Count(frame.transform);
                return (icon, count);
            }

            (_resultIcon, _resultCount) = Display("#crafting-result", 6.625f);
            (_levelIcon, _levelCount) = Display("#crafting-level", 2.625f);

            _name = kit.Label(Root, "#crafting-result-name", "", kit.Catalog.GetFont("kerriefont"), UiKit.Em, "white", TextAnchor.UpperCenter, true);
            UiKit.Place(_name.rectTransform, width - 1.375f * UiKit.Em - 9 * UiKit.Em, height - 4.875f * UiKit.Em - UiKit.Em, 9 * UiKit.Em, UiKit.Em);
            _name.supportRichText = true;

            // .crafting-buttons: bottom 1.75em, left 2em, gap 0.75em (crafting.png 16×16 at y 208, 2×).
            for (var i = 0; i < 3; i++)
            {
                var index = i;
                var amount = new[] { 1, 5, 10 }[i];
                var image = UiKit.Box(Root, new[] { "#craft-one", "#craft-five", "#craft-ten" }[i]).gameObject.AddComponent<Image>();
                UiKit.Place(image.rectTransform, 2 * UiKit.Em + i * (32 + 0.75f * UiKit.Em), height - 1.75f * UiKit.Em - 32, 32, 32);
                var button = image.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState { highlightedSprite = _kit.Region("crafting", index * 16, 208 + 32, 16, 16) };
                button.onClick.AddListener(() => HandleAmount(amount));
                _amounts[i] = image;
            }
            HandleAmount(1);

            // #craft-button: right 2.875em, bottom 1.5em (crafting.png 48×16 at 0,157, 2×; hover +32, active +16).
            Sprite CraftFrame(int f) => _kit.Region("crafting", 0, 157 + f * 16, 48, 16);
            var craft = UiKit.Box(Root, "#craft-button").gameObject.AddComponent<Image>();
            craft.sprite = CraftFrame(0);
            UiKit.Place(craft.rectTransform, width - 2.875f * UiKit.Em - 96, height - 1.5f * UiKit.Em - 32, 96, 32);
            var craftButton = craft.gameObject.AddComponent<Button>();
            craftButton.transition = Selectable.Transition.SpriteSwap;
            craftButton.spriteState = new SpriteState { highlightedSprite = CraftFrame(2), pressedSprite = CraftFrame(1), selectedSprite = CraftFrame(0) };
            craftButton.onClick.AddListener(() =>
            {
                Craft?.Invoke(_selectedKey, _craftAmount);
                Hide();
            });
            _craftLabel = kit.Label(craft.transform, "label", "", kit.Catalog.GetFont("kerriefont"), UiKit.Em, "white", TextAnchor.MiddleCenter, true);
            UiKit.Fill(_craftLabel.rectTransform);

            Sprite Close(int frame) => kit.Region("guilds/elements", 64 + frame * 12, 0, 11, 11);
            var close = UiKit.Box(Root, "#close-crafting").gameObject.AddComponent<Image>();
            close.sprite = Close(0);
            var rect = close.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1);
            rect.anchoredPosition = new Vector2(0.25f * UiKit.Em, 0.25f * UiKit.Em);
            rect.sizeDelta = new Vector2(22, 22);
            close.gameObject.AddComponent<Button>().onClick.AddListener(Hide);
        }

        static RectTransform Frame(UiKit kit, RectTransform parent)
        {
            var frame = UiKit.Box(parent, "#crafting").gameObject.AddComponent<Image>();
            frame.sprite = kit.Region("crafting", 0, 0, 208, 126);
            return UiKit.Centre(frame.rectTransform, 416, 252);
        }

        /// <summary>.crafting-option: crafting.png 21×22 at 48,128 (2×), hover +44, active +22 in x.</summary>
        Image OptionFrame(Transform parent, string name, bool disabled)
        {
            Sprite Frame(int f) => _kit.Region("crafting", 48 + f * 22, 128, 21, 22);
            var frame = UiKit.Box(parent, name).gameObject.AddComponent<Image>();
            frame.sprite = Frame(0);
            if (!disabled)
            {
                var button = frame.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState { highlightedSprite = Frame(2), pressedSprite = Frame(1), selectedSprite = Frame(0) };
            }
            return frame;
        }

        Image Icon(Transform parent)
        {
            // .crafting-option-icon: 2em with a 0.3125em margin.
            var icon = UiKit.Box(parent, "crafting-option-icon").gameObject.AddComponent<Image>();
            UiKit.Place(icon.rectTransform, 0.3125f * UiKit.Em, 0.3125f * UiKit.Em, 2 * UiKit.Em, 2 * UiKit.Em);
            icon.raycastTarget = false;
            icon.enabled = false;
            return icon;
        }

        Text Count(Transform parent)
        {
            // .crafting-option-count: top 2.8em, left 1em, KerrieFont.
            var count = _kit.Label(parent, "crafting-option-count", "", _kit.Catalog.GetFont("kerriefont"), UiKit.Em, "white", TextAnchor.UpperCenter, true);
            UiKit.Place(count.rectTransform, 0, 2.8f * UiKit.Em - 0.5f * UiKit.Em, 42, UiKit.Em);
            return count;
        }

        Sprite Active(int index) => _kit.Region("crafting", index * 16, 208 + 16, 16, 16);

        void HandleAmount(int amount)
        {
            _craftAmount = amount;
            for (var i = 0; i < 3; i++)
                _amounts[i].sprite = new[] { 1, 5, 10 }[i] == amount ? Active(i) : _kit.Region("crafting", i * 16, 208, 16, 16);
        }

        /// <summary>crafting.ts handle.</summary>
        public void Handle(Opcodes.Crafting opcode, Dictionary<string, object> info)
        {
            switch (opcode)
            {
                case Opcodes.Crafting.Open:
                    Open(info.TryGetValue("type", out var type) && type is double t ? (int)t : (int)Modules.Skills.Crafting,
                        info.TryGetValue("previews", out var previews) ? previews as List<object> : null);
                    break;
                case Opcodes.Crafting.Select:
                    HandleSelect(info);
                    break;
            }
        }

        /// <summary>crafting.ts show: the options, the first one selected, the craft button's word.</summary>
        void Open(int type, List<object> previews)
        {
            Show();
            foreach (var (frame, _) in _optionSlots) UnityEngine.Object.Destroy(frame.gameObject);
            _optionSlots.Clear();
            _type = type;

            var list = (previews ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
            var cell = 11.125f * UiKit.Em / 4;
            for (var i = 0; i < list.Count; i++)
            {
                var key = list[i].TryGetValue("key", out var k) ? k as string ?? "" : "";
                var level = list[i].TryGetValue("level", out var l) && l is double d ? d : 0;
                var frame = OptionFrame(_options, "crafting-option", false);
                UiKit.Place(frame.rectTransform, i % 4 * cell, i / 4 * 44, 42, 44);
                var icon = Icon(frame.transform);
                icon.sprite = _kit.ItemSprite(key);
                icon.enabled = icon.sprite != null;
                // Greyed out (.greyscale: half opacity, no colour) when the skill level is too low.
                if (_player.Skills.TryGetValue(_type, out var skill) && skill.Level < level) icon.color = new Color(0.5f, 0.5f, 0.5f, 0.5f);
                var index = i;
                frame.GetComponent<Button>().onClick.AddListener(() => SelectOption(index));
                _optionSlots.Add((frame, key));
            }
            _options.sizeDelta = new Vector2(0, (list.Count + 3) / 4 * 44);

            if (list.Count > 0) SelectOption(0);

            var text = (Modules.Skills)type switch
            {
                Modules.Skills.Cooking => "crafting.cook",
                Modules.Skills.Fletching => "crafting.fletch",
                Modules.Skills.Smithing => "crafting.smith",
                Modules.Skills.Smelting => "crafting.smelt",
                Modules.Skills.Alchemy => "crafting.brew",
                _ => "crafting.craft"
            };
            _craftLabel.text = Localization.Get(text);
        }

        /// <summary>A click on an option: it becomes active and the server is asked for its details.</summary>
        public void SelectOption(int index)
        {
            if (index < 0 || index >= _optionSlots.Count) return;
            for (var i = 0; i < _optionSlots.Count; i++)
                _optionSlots[i].frame.sprite = _kit.Region("crafting", 48 + (i == index ? 22 : 0), 128, 21, 22);
            Selected?.Invoke(_optionSlots[index].key);
        }

        /// <summary>crafting.ts handleSelect: result icon and count, level, name and the requirements.</summary>
        void HandleSelect(Dictionary<string, object> info)
        {
            for (var i = _requirements.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(_requirements.GetChild(i).gameObject);

            _selectedKey = info.TryGetValue("key", out var key) ? key as string ?? "" : "";
            _resultIcon.sprite = _kit.ItemSprite(_selectedKey);
            _resultIcon.enabled = _resultIcon.sprite != null;
            _levelIcon.sprite = _kit.Region("skills/" + (Enum.IsDefined(typeof(Modules.Skills), _type) ? ((Modules.Skills)_type).ToString().ToLowerInvariant() : "crafting"), 0, 0, 16, 16);
            _levelIcon.enabled = true;
            _resultCount.text = $"x{Js(info, "result")}";
            _levelCount.text = Js(info, "level");
            _name.text = MessageFormat.ToRichText(info.TryGetValue("name", out var name) ? name as string ?? "undefined" : "undefined");

            var requirements = (info.TryGetValue("requirements", out var r) ? r as List<object> : null)?.OfType<Dictionary<string, object>>().ToList()
                               ?? new List<Dictionary<string, object>>();
            for (var i = 0; i < requirements.Count; i++)
            {
                var frame = OptionFrame(_requirements, "crafting-option disabled", true);
                UiKit.Place(frame.rectTransform, i * (42 + 0.375f * UiKit.Em), 0, 42, 44);
                var icon = Icon(frame.transform);
                icon.sprite = _kit.ItemSprite(requirements[i].TryGetValue("key", out var k) ? k as string : null);
                icon.enabled = icon.sprite != null;
                var count = requirements[i].TryGetValue("count", out var c) && c is double n ? n : 0;
                if (count != 0) Count(frame.transform).text = $"x{JsValue.NumberToString(count)}";
            }
        }

        static string Js(Dictionary<string, object> info, string key) =>
            info.TryGetValue(key, out var value) && value is double d ? JsValue.NumberToString(d) : "undefined";

        /// <summary>For tests.</summary>
        public int OptionCount => _optionSlots.Count;
        public string SelectedKey => _selectedKey;
        public string CraftLabel => _craftLabel.text;
    }

    /// <summary>The settings kept between sessions (utils/storage.ts settings, with its defaults).</summary>
    public static class GameSettings
    {
        const string Prefix = "pixelhero.settings.";

        static int Int(string key, int fallback) => PlayerPrefs.GetInt(Prefix + key, fallback);
        static void Set(string key, int value) => PlayerPrefs.SetInt(Prefix + key, value);

        public static int MusicVolume { get => Int("musicVolume", 25); set => Set("musicVolume", value); }
        public static int SoundVolume { get => Int("soundVolume", 25); set => Set("soundVolume", value); }
        public static int Brightness { get => Int("brightness", 100); set => Set("brightness", value); }
        public static bool AudioEnabled { get => Int("audioEnabled", 1) == 1; set => Set("audioEnabled", value ? 1 : 0); }
        public static bool LowPowerMode { get => Int("lowPowerMode", 0) == 1; set => Set("lowPowerMode", value ? 1 : 0); }
        public static bool ShowNames { get => Int("showNames", 1) == 1; set => Set("showNames", value ? 1 : 0); }
        public static bool ShowLevels { get => Int("showLevels", 1) == 1; set => Set("showLevels", value ? 1 : 0); }
        public static bool DisableCaching { get => Int("disableCaching", 0) == 1; set => Set("disableCaching", value ? 1 : 0); }
        public static int FpsThrottle { get => Int("fpsThrottle", 0); set => Set("fpsThrottle", value); }
    }

    /// <summary>
    /// menu/settings.ts: #settings-page (.slice-container): the volume and brightness sliders and the option
    /// checkboxes in two columns, the game version and world, and Log Out. Joystick (mobile only), Debug Mode
    /// (hidden in production builds) and Enable WebGL (the port has one renderer) are hidden.
    /// </summary>
    public sealed class SettingsView : ContainerWindow
    {
        readonly Text _version, _world;
        readonly Func<double?> _serverId;
        readonly Text _fpsLabel;

        /// <summary>A setting changed (its label key, e.g. "settings.music").</summary>
        public event Action<string> Changed;
        public event Action Logout;

        public SettingsView(UiKit kit, RectTransform parent, Func<double?> serverId) : base(kit, parent, "#settings-page")
        {
            _serverId = serverId;

            // #input-settings (flex 6, padding 0.75em 1em, two columns 3em apart) | #action-settings (flex 2).
            var inputWidth = (ContentWidth - 0.5f * UiKit.Em) * 6 / 8;
            var input = Kit.Sliced(Content, "#input-settings", "inner-container", 2 * UiKit.Em);
            UiKit.Place(input.rectTransform, 0, 0, inputWidth, ContentHeight);
            var column = (inputWidth - 2 * UiKit.Em - 3 * UiKit.Em) / 2;
            var rows = (ContentHeight - 1.5f * UiKit.Em) / 6;

            float Top(int row) => 0.75f * UiKit.Em + row * rows;
            Slider(input.rectTransform, "settings.music", UiKit.Em, Top(0), column, () => GameSettings.MusicVolume, v => GameSettings.MusicVolume = v);
            Slider(input.rectTransform, "settings.sound", UiKit.Em, Top(1), column, () => GameSettings.SoundVolume, v => GameSettings.SoundVolume = v);
            Slider(input.rectTransform, "settings.brightness", UiKit.Em, Top(2), column, () => GameSettings.Brightness, v => GameSettings.Brightness = v);
            Check(input.rectTransform, "settings.audio", UiKit.Em, Top(3), column, () => GameSettings.AudioEnabled, v => GameSettings.AudioEnabled = v);
            Check(input.rectTransform, "settings.lowPower", UiKit.Em, Top(4), column, () => GameSettings.LowPowerMode, v => GameSettings.LowPowerMode = v);

            var right = UiKit.Em + column + 3 * UiKit.Em;
            Check(input.rectTransform, "settings.showNames", right, Top(0), column, () => GameSettings.ShowNames, v => GameSettings.ShowNames = v);
            Check(input.rectTransform, "settings.showLevels", right, Top(1), column, () => GameSettings.ShowLevels, v => GameSettings.ShowLevels = v);
            Check(input.rectTransform, "settings.disableCaching", right, Top(2), column, () => GameSettings.DisableCaching, v => GameSettings.DisableCaching = v);

            // FPS Throttle: OFF / 50 FPS / 30 FPS (the select cycles on click).
            var fpsTitle = Kit.Label(input.rectTransform, "frame-throttle-text", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleLeft, true);
            UiKit.Place(fpsTitle.rectTransform, right, Top(3), column * 2 / 3, rows);
            Localization.LanguageChanged += () => fpsTitle.text = Localization.Get("settings.fps");
            fpsTitle.text = Localization.Get("settings.fps");
            _fpsLabel = Kit.Link(input.rectTransform, "frame-throttle-select", "", UiKit.Em, "#c6c0a3", "#fcda5c", () =>
            {
                GameSettings.FpsThrottle = (GameSettings.FpsThrottle + 1) % 3;
                UpdateFps();
                Changed?.Invoke("settings.fps");
            });
            _fpsLabel.alignment = TextAnchor.MiddleRight;
            UiKit.Place(_fpsLabel.rectTransform, right + column * 2 / 3, Top(3), column / 3, rows);
            UpdateFps();

            // #action-settings: the game info box and Log Out.
            var actionLeft = inputWidth + 0.5f * UiKit.Em;
            var actionWidth = ContentWidth - actionLeft;
            var info = Kit.Sliced(Content, "#game-info", "inner-container", 2 * UiKit.Em);
            UiKit.Place(info.rectTransform, actionLeft, 0, actionWidth, ContentHeight - 35 - 0.5f * UiKit.Em);
            _version = Kit.Label(info.rectTransform, "#game-info-version", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperLeft, true);
            UiKit.Place(_version.rectTransform, 0.75f * UiKit.Em, 0.75f * UiKit.Em, actionWidth - 1.5f * UiKit.Em, 2 * UiKit.Em);
            _version.horizontalOverflow = HorizontalWrapMode.Wrap;
            _world = Kit.Label(info.rectTransform, "#game-info-world", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperLeft, true);
            UiKit.Place(_world.rectTransform, 0.75f * UiKit.Em, 3.25f * UiKit.Em, actionWidth - 1.5f * UiKit.Em, 2 * UiKit.Em);
            _world.horizontalOverflow = HorizontalWrapMode.Wrap;

            Sprite Frame(int f) => Kit.Region("guilds/elements", 73, 78 + f * 14, 28, 14);
            var logout = UiKit.Box(Content, "#logout-button").gameObject.AddComponent<Image>();
            logout.sprite = Frame(0);
            UiKit.Place(logout.rectTransform, actionLeft + (actionWidth - 70) / 2, ContentHeight - 35, 70, 35);
            var logoutButton = logout.gameObject.AddComponent<Button>();
            logoutButton.transition = Selectable.Transition.SpriteSwap;
            logoutButton.spriteState = new SpriteState { highlightedSprite = Frame(1), pressedSprite = Frame(2), selectedSprite = Frame(0) };
            logoutButton.onClick.AddListener(() => Logout?.Invoke());
            var label = Kit.Label(logout.transform, "label", "", Kit.Body, 0.75f * UiKit.Em, "#eee", TextAnchor.MiddleCenter, true);
            UiKit.Fill(label.rectTransform);
            Localization.LanguageChanged += () => label.text = Localization.Get("settings.logout");
            label.text = Localization.Get("settings.logout");
        }

        void UpdateFps() => _fpsLabel.text = Localization.Get(new[] { "settings.fpsOff", "settings.fps50", "settings.fps30" }[Math.Clamp(GameSettings.FpsThrottle, 0, 2)]);

        /// <summary>A labelled range input (0–100, step 10): the track (#4d4d4d, the filled part #c5c5c5) and the thumb.</summary>
        void Slider(RectTransform parent, string key, float left, float top, float width, Func<int> get, Action<int> set)
        {
            var title = Kit.Label(parent, key, "", Kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleLeft, true);
            UiKit.Place(title.rectTransform, left, top, width / 2, 1.5f * UiKit.Em);
            Localization.LanguageChanged += () => title.text = Localization.Get(key);
            title.text = Localization.Get(key);

            var track = UiKit.Box(parent, key + " range").gameObject.AddComponent<Image>();
            track.color = UiKit.Css("#4d4d4d");
            UiKit.Place(track.rectTransform, left + width / 2, top + 0.5f * UiKit.Em, width / 2, 0.5f * UiKit.Em);
            var fill = UiKit.Box(track.transform, "fill").gameObject.AddComponent<Image>();
            fill.color = UiKit.Css("#c5c5c5");
            fill.raycastTarget = false;
            var handle = UiKit.Box(track.transform, "thumb").gameObject.AddComponent<Image>();
            handle.color = UiKit.Css("#7f7f7f");
            var thumbInner = UiKit.Box(handle.transform, "inner").gameObject.AddComponent<Image>();
            thumbInner.color = UiKit.Css("#4d4d4d");
            thumbInner.raycastTarget = false;
            UiKit.Place(thumbInner.rectTransform, 2, 2, 0.75f * UiKit.Em - 4, UiKit.Em - 4);

            var slider = track.gameObject.AddComponent<UnityEngine.UI.Slider>();
            slider.minValue = 0;
            slider.maxValue = 10;
            slider.wholeNumbers = true;
            var fillRect = UiKit.Fill(fill.rectTransform);
            fillRect.anchorMax = new Vector2(0, 1);
            slider.fillRect = fillRect;
            var handleRect = handle.rectTransform;
            handleRect.anchorMin = handleRect.anchorMax = new Vector2(0, 0.5f);
            handleRect.pivot = new Vector2(0.5f, 0.5f);
            handleRect.sizeDelta = new Vector2(0.75f * UiKit.Em, UiKit.Em);
            slider.handleRect = handleRect;
            slider.targetGraphic = handle;
            slider.value = Mathf.Clamp(get() / 10, 0, 10);
            slider.onValueChanged.AddListener(v =>
            {
                set((int)v * 10);
                Changed?.Invoke(key);
            });
        }

        /// <summary>label.check: the text, then the checkmark on the right.</summary>
        void Check(RectTransform parent, string key, float left, float top, float width, Func<bool> get, Action<bool> set)
        {
            var toggle = Kit.Check(parent, key, "", out var label);
            UiKit.Place((RectTransform)toggle.transform, left, top + 0.25f * UiKit.Em, width, UiKit.Em);
            // The checkmark goes to the right edge, the label to the left.
            UiKit.Place((RectTransform)toggle.targetGraphic.transform, width - UiKit.Em, 0, UiKit.Em, UiKit.Em);
            UiKit.Place(label.rectTransform, 0, 0, width - 1.5f * UiKit.Em, UiKit.Em);
            Localization.LanguageChanged += () => label.text = Localization.Get(key);
            label.text = Localization.Get(key);
            toggle.isOn = get();
            toggle.onValueChanged.AddListener(v =>
            {
                set(v);
                Changed?.Invoke(key);
            });
        }

        public override void Show()
        {
            base.Show();
            // settings.ts handleInfo.
            _version.text = $"{Localization.Get("settings.version")} {GameVersion.Gver}";
            var world = _serverId();
            _world.text = $"{Localization.Get("settings.world")} {(world == -1 || world == null ? Localization.Get("settings.unknown") : JsValue.NumberToString(world.Value))}";
        }
    }

    /// <summary>
    /// menu/welcome.ts: #welcome (.slice-container, 1em lower), shown to a new player: the welcome title, the last
    /// login and time played labels, the change log of this version (common/text/updates.json), how to play with
    /// the animated controls, and the daily rewards slots.
    /// </summary>
    public sealed class WelcomeView : ContainerWindow
    {
        // common/text/updates.json for 0.1.1.
        static readonly string[] Changelog =
        {
            "PixelHero 0.1.1", "- Add language selection menu", "- Add FPS throttle option", "- New quests", "- Anti-cheating detection improvements"
        };

        readonly List<(Image image, int frames)> _controls = new List<(Image, int)>();
        readonly List<Sprite[]> _controlFrames = new List<Sprite[]>();

        public WelcomeView(UiKit kit, RectTransform parent) : base(kit, parent, "#welcome")
        {
            Root.anchoredPosition = new Vector2(0, -UiKit.Em);
            var half = (ContentWidth - 0.5f * UiKit.Em) / 2;

            // #welcome-info (font 0.8em, gap 0.75em): h1, the stats, the change log.
            var title = Kit.Label(Content, "h1", "", Kit.Heading, 1.75f * 0.8f * UiKit.Em, "#eee", TextAnchor.UpperLeft, true);
            UiKit.Place(title.rectTransform, 0, 0, half, 1.4f * UiKit.Em);
            Localization.LanguageChanged += () => title.text = Localization.Get("welcome.title");
            title.text = Localization.Get("welcome.title");
            var stats = Kit.Label(Content, "#welcome-stats", "", Kit.Body, 0.8f * UiKit.Em, "#eee", TextAnchor.UpperLeft, true);
            UiKit.Place(stats.rectTransform, 0, 2 * UiKit.Em, half, 2 * UiKit.Em);
            Localization.LanguageChanged += () => stats.text = $"{Localization.Get("welcome.lastLogin")}\n{Localization.Get("welcome.timePlayed")}";
            stats.text = $"{Localization.Get("welcome.lastLogin")}\n{Localization.Get("welcome.timePlayed")}";

            var log = Kit.Sliced(Content, "#welcome-changelog", "inner-container", 2 * UiKit.Em);
            UiKit.Place(log.rectTransform, 0, 4.25f * UiKit.Em, half, ContentHeight - 4.25f * UiKit.Em);
            var lines = Kit.Label(log.transform, "ul", string.Join("\n", Changelog), Kit.Body, 0.8f * UiKit.Em, "#eee", TextAnchor.UpperLeft, true);
            UiKit.Place(lines.rectTransform, 0.75f * UiKit.Em, 0.75f * UiKit.Em, half - 1.5f * UiKit.Em, ContentHeight - 6 * UiKit.Em);
            lines.lineSpacing = 1.2f;

            // #welcome-play (font 0.95em): how to play with the controls, then the daily rewards.
            var play = Kit.Sliced(Content, "#welcome-play", "inner-container", 2 * UiKit.Em);
            var playLeft = half + 0.5f * UiKit.Em;
            UiKit.Place(play.rectTransform, playLeft, 0, half, ContentHeight);
            var inner = UiKit.Box(play.transform, "padding");
            UiKit.Place(inner, 0.75f * UiKit.Em, 0.75f * UiKit.Em, half - 1.5f * UiKit.Em, ContentHeight - 1.5f * UiKit.Em);
            var width = inner.sizeDelta.x;

            var howTo = Kit.Label(inner, "h2", "", Kit.Heading, 1.375f * 0.95f * UiKit.Em, "#eee", TextAnchor.UpperLeft, true);
            UiKit.Place(howTo.rectTransform, 0, 0, width, 1.4f * UiKit.Em);
            var keyboard = Kit.Label(inner, "span", "", Kit.Body, 0.95f * UiKit.Em, "#eee", TextAnchor.UpperLeft, true);
            keyboard.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiKit.Place(keyboard.rectTransform, 0, 1.75f * UiKit.Em, width, 2 * UiKit.Em);
            Localization.LanguageChanged += () =>
            {
                howTo.text = Localization.Get("welcome.howToPlay");
                keyboard.text = Localization.Get("welcome.controls");
            };
            howTo.text = Localization.Get("welcome.howToPlay");
            keyboard.text = Localization.Get("welcome.controls");

            // .welcome-controls: wasd, arrows (48×29 frames, 2×), click (24×22 frames, 2×), two frames 1.25 s apart.
            var controls = new[] { ("controls/wasd", 48, 29), ("controls/arrows", 48, 29), ("controls/click", 24, 22) };
            var controlsWidth = controls.Sum(c => c.Item2 * 2);
            var gap = (width - controlsWidth) / 4;
            var x = gap;
            foreach (var (image, frameWidth, frameHeight) in controls)
            {
                var control = UiKit.Box(inner, image).gameObject.AddComponent<Image>();
                UiKit.Place(control.rectTransform, x, 4 * UiKit.Em, frameWidth * 2, frameHeight * 2);
                _controlFrames.Add(new[] { Kit.Region(image, 0, 0, frameWidth, frameHeight), Kit.Region(image, frameWidth, 0, frameWidth, frameHeight) });
                _controls.Add((control, 2));
                x += frameWidth * 2 + gap;
            }

            var rewards = Kit.Label(inner, "h2", "", Kit.Heading, 1.375f * 0.95f * UiKit.Em, "#eee", TextAnchor.UpperLeft, true);
            UiKit.Place(rewards.rectTransform, 0, 8.25f * UiKit.Em, width, 1.4f * UiKit.Em);
            Localization.LanguageChanged += () => rewards.text = Localization.Get("welcome.dailyRewards");
            rewards.text = Localization.Get("welcome.dailyRewards");
            for (var i = 0; i < 2; i++)
            {
                var slot = Kit.Sliced(inner, "slice-item-slot", "item-slot", 0.5f * UiKit.Em);
                UiKit.Place(slot.rectTransform, width * (i + 1) / 3 - 1.25f * UiKit.Em, 10 * UiKit.Em, 2.5f * UiKit.Em, 2.5f * UiKit.Em);
            }
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            // animation: 2.5s steps(2): each frame for 1.25 s.
            var frame = (int)(Time.unscaledTime / 1.25f) % 2;
            for (var i = 0; i < _controls.Count; i++) _controls[i].image.sprite = _controlFrames[i][frame];
        }
    }
}
