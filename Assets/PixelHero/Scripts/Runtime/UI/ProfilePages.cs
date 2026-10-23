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
    /// menu/profile/impl/skills.ts: the profile's second page. One row per skill in Modules.SkillsOrder (the skill
    /// image, "level/120" and the experience bar); a click shows #skill-info to the left of the window with the
    /// level, experience, next experience and percentage.
    /// </summary>
    public sealed class SkillsPage
    {
        const float RowHeight = 3 * UiKit.Em;

        readonly UiKit _kit;
        readonly Player _player;
        readonly RectTransform _page, _list, _info;
        readonly float _listWidth;
        readonly Dictionary<int, (Text level, RectTransform progress)> _rows = new Dictionary<int, (Text, RectTransform)>();
        readonly Image _infoImage;
        readonly Text _infoTitle, _infoDetails;
        int _selected = -1;
        bool _loaded;

        public SkillsPage(UiKit kit, RectTransform window, Player player)
        {
            _kit = kit;
            _player = player;

            // #skills-page: padding 1.5em 1em 0.5em, 12em high; #skills-list fills it.
            _page = UiKit.Box(window, "#skills-page");
            UiKit.Place(_page, UiKit.Em, 1.5f * UiKit.Em, 22 * UiKit.Em, 10 * UiKit.Em);
            _list = UiKit.ScrollView(_page, "#skills-list");
            _listWidth = 22 * UiKit.Em - 0.25f * UiKit.Em;

            // #skill-info.slice-dialog: right 100% (outside the window's left edge), 8em × 10em, no right border.
            var info = kit.Sliced(window, "#skill-info", "dialog", UiKit.Em);
            info.raycastTarget = true;
            _info = info.rectTransform;
            _info.anchorMin = _info.anchorMax = new Vector2(0, 1);
            _info.pivot = new Vector2(1, 1);
            _info.anchoredPosition = new Vector2(0, -2.5f * UiKit.Em);
            _info.sizeDelta = new Vector2(8 * UiKit.Em, 10 * UiKit.Em);

            var slot = kit.Sliced(_info, "slice-item-slot", "item-slot", 0.5f * UiKit.Em);
            UiKit.Place(slot.rectTransform, 0.675f * UiKit.Em, UiKit.Em, 2 * UiKit.Em, 2 * UiKit.Em);
            _infoImage = UiKit.Box(slot.transform, "skill-info-image").gameObject.AddComponent<Image>();
            UiKit.Place(_infoImage.rectTransform, 0, 0, 2 * UiKit.Em, 2 * UiKit.Em);
            _infoImage.raycastTarget = false;
            _infoTitle = kit.Label(_info, "skill-info-title", "", kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleLeft, true);
            UiKit.Place(_infoTitle.rectTransform, 3.175f * UiKit.Em, UiKit.Em, 4.5f * UiKit.Em, 2 * UiKit.Em);
            _infoDetails = kit.Label(_info, "skill-info-details", "", kit.Body, UiKit.Em, "#eee", TextAnchor.UpperLeft, true);
            UiKit.Place(_infoDetails.rectTransform, 0.675f * UiKit.Em + 0.25f * UiKit.Em, 3.5f * UiKit.Em, 7 * UiKit.Em, 6 * UiKit.Em);
            _infoDetails.lineSpacing = 1.5f;
            _info.gameObject.SetActive(false);

            _page.gameObject.SetActive(false);
        }

        public bool Visible => _page.gameObject.activeSelf;
        public bool InfoVisible => _info.gameObject.activeSelf;
        public int RowCount => _rows.Count;

        /// <summary>skills.ts show: the first showing builds the rows.</summary>
        public void Show()
        {
            _page.gameObject.SetActive(true);
            if (_loaded) return;
            _loaded = true;
            Synchronize();
        }

        public void Hide()
        {
            _page.gameObject.SetActive(false);
            HideInfo();
        }

        public void HideInfo() => _info.gameObject.SetActive(false);

        static string Name(int type) => Enum.IsDefined(typeof(Modules.Skills), type) ? ((Modules.Skills)type).ToString() : "undefined";

        Sprite SkillImage(int type) => _kit.Region("skills/" + Name(type).ToLowerInvariant(), 0, 0, 16, 16);

        /// <summary>skills.ts synchronize: rows in SkillsOrder, created once and updated after.</summary>
        public void Synchronize()
        {
            if (_player.Skills.Count == 0) return;

            foreach (var id in ((List<object>)ModuleTables.SkillsOrder).Select(o => (int)(double)o))
            {
                if (!_player.Skills.TryGetValue(id, out var skill)) continue;
                if (!_rows.ContainsKey(id)) CreateElement(id);
                Update(id, skill);
            }

            if (InfoVisible && _selected >= 0) ShowInfo(_selected);
        }

        /// <summary>li.slice-list-item: the skill image in its slot, then the level and the experience bar.</summary>
        void CreateElement(int type)
        {
            var top = _rows.Count * (RowHeight + 0.25f * UiKit.Em);
            var row = _kit.Sliced(_list, "li", "list-item", 0.5f * UiKit.Em);
            row.raycastTarget = true;
            UiKit.Place(row.rectTransform, 0, top, _listWidth, RowHeight);
            var button = row.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = _kit.Slice("list-item-hover"), selectedSprite = _kit.Slice("list-item") };
            button.onClick.AddListener(() => ShowInfo(type));

            var slot = _kit.Sliced(row.transform, "slice-item-slot", "item-slot", 0.5f * UiKit.Em);
            UiKit.Place(slot.rectTransform, 0.5f * UiKit.Em, 0.5f * UiKit.Em, 2 * UiKit.Em, 2 * UiKit.Em);
            var image = UiKit.Box(slot.transform, $"skill-image-{Name(type).ToLowerInvariant()}").gameObject.AddComponent<Image>();
            image.sprite = SkillImage(type);
            image.raycastTarget = false;
            UiKit.Place(image.rectTransform, 0, 0, 2 * UiKit.Em, 2 * UiKit.Em);

            // .skill-content: column, space-evenly, centred; level 1.3em; the bar 17.25em × 0.5em with a 0.125em border.
            var contentLeft = 2.75f * UiKit.Em;
            var contentWidth = _listWidth - contentLeft - 0.5f * UiKit.Em;
            var level = _kit.Label(row.transform, "skill-level", "", _kit.Body, 1.3f * UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(level.rectTransform, contentLeft, 0.35f * UiKit.Em, contentWidth, 1.3f * UiKit.Em);

            var barWidth = Math.Min(17.25f * UiKit.Em, contentWidth);
            var bar = UiKit.Box(row.transform, "skill-experience").gameObject.AddComponent<Image>();
            bar.color = new Color(0, 0, 0, 0.75f);
            bar.raycastTarget = false;
            UiKit.Place(bar.rectTransform, contentLeft + (contentWidth - barWidth) / 2, 2 * UiKit.Em, barWidth, 0.75f * UiKit.Em);
            var inside = UiKit.Box(bar.transform, "background").gameObject.AddComponent<Image>();
            inside.color = new Color(0, 0, 0, 0.5f);
            inside.raycastTarget = false;
            UiKit.Place(inside.rectTransform, 0.125f * UiKit.Em, 0.125f * UiKit.Em, barWidth - 0.25f * UiKit.Em, 0.5f * UiKit.Em);
            var progress = UiKit.Box(inside.transform, "skill-experience-progress").gameObject.AddComponent<RawImage>();
            progress.texture = Gradient();
            progress.raycastTarget = false;
            UiKit.Place(progress.rectTransform, 0, 0, 0, 0.5f * UiKit.Em);

            _rows[type] = (level, progress.rectTransform);
            _list.sizeDelta = new Vector2(0, top + RowHeight);
        }

        static Texture2D _gradient;

        /// <summary>linear-gradient(to left, #4f4, #080): #080 on the left, #4f4 on the right.</summary>
        static Texture2D Gradient()
        {
            if (_gradient != null) return _gradient;
            _gradient = new Texture2D(64, 1, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (var x = 0; x < 64; x++) _gradient.SetPixel(x, 0, Color.Lerp(new Color32(0, 0x88, 0, 255), new Color32(0x44, 0xff, 0x44, 255), x / 63f));
            _gradient.Apply();
            return _gradient;
        }

        /// <summary>skills.ts update: "level/MAX_LEVEL" and the bar at percentage × 100%.</summary>
        void Update(int type, Player.SkillState skill)
        {
            var (level, progress) = _rows[type];
            level.text = $"{Js(skill.Level)}/{Constants.MAX_LEVEL}";
            var full = ((RectTransform)progress.parent).sizeDelta.x;
            var width = (float)Math.Clamp((skill.Percentage ?? 0) * full, 0, full);
            progress.sizeDelta = new Vector2(width, progress.sizeDelta.y);
        }

        static string Js(double? value) => value is double v ? JsValue.NumberToString(v) : "undefined";

        /// <summary>skills.ts showInfo.</summary>
        public void ShowInfo(int type)
        {
            if (!_player.Skills.TryGetValue(type, out var skill)) return;
            _selected = type;
            _info.gameObject.SetActive(true);
            _infoImage.sprite = SkillImage(type);
            _infoTitle.text = Localization.Get("skill." + Name(type));
            var percent = skill.Percentage is double p ? JsNumber.ToFixed(p * 100, 3) : "NaN";
            _infoDetails.text = string.Join("\n",
                $"{Localization.Get("skills.level")} {Js(skill.Level)}",
                $"{Localization.Get("skills.exp")} {Js(skill.Experience)}",
                $"{Localization.Get("skills.nextExp")} {Js(skill.NextExperience)}",
                $"{Localization.Get("skills.percent")} {percent}%");
        }

        public string InfoTitle => _infoTitle.text;
    }

    /// <summary>
    /// menu/equipments.ts: #equipments (.slice-container), opened from the profile's equipment button. The player's
    /// idle frame with the drawn equipment over it (turned with previous / next), the 12 equipment slots around it
    /// (a click unequips) and the summed attack and defense stats and bonuses of everything worn.
    /// </summary>
    public sealed class EquipmentsView : ContainerWindow
    {
        static readonly (Modules.Equipment type, float left, float top)[] SlotPositions =
        {
            (Modules.Equipment.Helmet, 6, 0), (Modules.Equipment.Pendant, 0.25f, 1.25f), (Modules.Equipment.Arrows, 11.75f, 1.25f),
            (Modules.Equipment.Chestplate, 2.75f, 2.5f), (Modules.Equipment.Weapon, 9.25f, 2.5f), (Modules.Equipment.Shield, 11, 6),
            (Modules.Equipment.Ring, 1, 6), (Modules.Equipment.ArmourSkin, 2.75f, 9.5f), (Modules.Equipment.WeaponSkin, 9.25f, 9.5f),
            (Modules.Equipment.Legplates, 0.25f, 10.75f), (Modules.Equipment.Cape, 11.75f, 10.75f), (Modules.Equipment.Boots, 6, 12)
        };

        static readonly Modules.Orientation[] Orientations =
            { Modules.Orientation.Left, Modules.Orientation.Up, Modules.Orientation.Right, Modules.Orientation.Down };

        readonly Player _player;
        readonly Func<string, Entities.EntitySprite> _sprites;
        readonly Dictionary<Modules.Equipment, Image> _slots = new Dictionary<Modules.Equipment, Image>();
        readonly Text _arrowsCount, _attack, _defense, _bonuses;
        readonly RectTransform _canvas;
        readonly List<RawImage> _layers = new List<RawImage>();
        Modules.Orientation _orientation = Modules.Orientation.Down;

        public event Action<Modules.Equipment> Unequip;

        public EquipmentsView(UiKit kit, RectTransform parent, Player player, Func<string, Entities.EntitySprite> sprites)
            : base(kit, parent, "#equipments")
        {
            _player = player;
            _sprites = sprites;

            // #equipments-player-visual: the slots (14em × 14em) with the image (8em × 4em canvas and the navigator)
            // centred over them.
            var visual = UiKit.Box(Content, "#equipments-player-visual");
            UiKit.Place(visual, 0, 0, 14 * UiKit.Em, 14 * UiKit.Em);

            _canvas = UiKit.Box(visual, "#equipments-player-image-canvas");
            UiKit.Place(_canvas, 3 * UiKit.Em, 4.5f * UiKit.Em, 8 * UiKit.Em, 4 * UiKit.Em);
            Navigator(visual, 0, 7 * UiKit.Em - 21 - 6, -1);
            Navigator(visual, 1, 7 * UiKit.Em + 6, 1);

            foreach (var (type, left, top) in SlotPositions)
            {
                var slot = kit.Sliced(visual, $"equipment-slot-{type.ToString().ToLowerInvariant()}", "list-item", 0.5f * UiKit.Em);
                slot.raycastTarget = true;
                UiKit.Place(slot.rectTransform, left * UiKit.Em, top * UiKit.Em, 2.5f * UiKit.Em, 2.5f * UiKit.Em);
                var button = slot.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState { highlightedSprite = kit.Slice("list-item-hover"), selectedSprite = kit.Slice("list-item") };
                var equipment = type;
                button.onClick.AddListener(() => Unequip?.Invoke(equipment));

                var image = UiKit.Box(slot.transform, "equipment-slot-image").gameObject.AddComponent<Image>();
                UiKit.Place(image.rectTransform, 0.25f * UiKit.Em, 0.25f * UiKit.Em, 2 * UiKit.Em, 2 * UiKit.Em);
                image.raycastTarget = false;
                _slots[type] = image;
            }

            _arrowsCount = kit.Label(visual, "equipment-slot-arrows-count", "", kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(_arrowsCount.rectTransform, 11.75f * UiKit.Em, 0.25f * UiKit.Em, 2.5f * UiKit.Em, UiKit.Em);

            // #equipments-player-stats: two columns (attack, defense), bonuses under attack; gap 0.5em.
            var statsLeft = 14.5f * UiKit.Em;
            var column = (ContentWidth - statsLeft - 0.5f * UiKit.Em) / 2;
            _attack = Stats("#attack-stats", statsLeft, 0, column);
            _defense = Stats("#defense-stats", statsLeft + column + 0.5f * UiKit.Em, 0, column);
            _bonuses = Stats("#bonuses", statsLeft, 7 * UiKit.Em, column);
        }

        Text Stats(string name, float left, float top, float width)
        {
            var text = Kit.Label(Content, name, "", Kit.Body, UiKit.Em, "#eee");
            UiKit.Place(text.rectTransform, left, top, width, 7 * UiKit.Em);
            text.supportRichText = true;
            text.lineSpacing = 1.15f;
            return text;
        }

        /// <summary>#equipments-player-image-navigator: previous / next at 0.75em (characterdialogsheet 21×17).</summary>
        void Navigator(RectTransform parent, int column, float left, int direction)
        {
            var image = UiKit.Box(parent, column == 0 ? "previous" : "next").gameObject.AddComponent<Image>();
            image.sprite = Kit.Region("characterdialogsheet", column * 21, 223, 21, 17);
            UiKit.Place(image.rectTransform, left, 8.75f * UiKit.Em, 21, 17);
            image.gameObject.AddComponent<Button>().onClick.AddListener(() => Navigate(direction));
        }

        /// <summary>equipments.ts handleNavigation: left → up → right → down, wrapping.</summary>
        public void Navigate(int direction)
        {
            var index = Array.IndexOf(Orientations, _orientation);
            index = (index + direction + Orientations.Length) % Orientations.Length;
            _orientation = Orientations[index];
            UpdatePlayerImage();
        }

        public override void Show()
        {
            base.Show();
            Synchronize();
        }

        /// <summary>equipments.ts synchronize: stats, the player image, the slot images and the arrow count.</summary>
        public void Synchronize()
        {
            LoadStats();
            UpdatePlayerImage();

            foreach (var pair in _slots)
            {
                var equipment = _player.Equipments[(int)pair.Key];
                pair.Value.sprite = equipment.Exists()
                    ? Kit.ItemSprite(equipment.Key)
                    : Kit.Region("equipment/" + pair.Key.ToString().ToLowerInvariant(), 0, 0, 16, 16);
                pair.Value.enabled = pair.Value.sprite != null;
            }

            var arrows = _player.Equipments[(int)Modules.Equipment.Arrows].Count ?? -1;
            _arrowsCount.text = arrows < 1 ? "" : $"+{JsValue.NumberToString(arrows)}";
        }

        static double Stat(Dictionary<string, object> stats, string key) =>
            stats != null && stats.TryGetValue(key, out var value) && value is double d ? d : 0;

        /// <summary>equipments.ts loadStats: the sums over every worn equipment, positive ones with a '+'.</summary>
        void LoadStats()
        {
            var keys = new[] { "crush", "slash", "stab", "archery", "magic" };
            var bonusKeys = new[] { "accuracy", "strength", "archery", "magic" };
            var attack = keys.ToDictionary(k => k, _ => 0.0);
            var defense = keys.ToDictionary(k => k, _ => 0.0);
            var bonuses = bonusKeys.ToDictionary(k => k, _ => 0.0);

            foreach (var equipment in _player.Equipments.Values)
            {
                if (!equipment.Exists()) continue;
                foreach (var key in keys)
                {
                    attack[key] += Stat(equipment.AttackStats, key);
                    defense[key] += Stat(equipment.DefenseStats, key);
                }
                foreach (var key in bonusKeys) bonuses[key] += Stat(equipment.Bonuses, key);
            }

            string Line(string key, double value) =>
                $"{Localization.Get("item.stat." + key)}: {(value > 0 ? "+" : "")}{JsValue.NumberToString(value)}";

            // .title: underlined, 0.5em below.
            _attack.text = $"<b>{Localization.Get("item.attackStats").TrimEnd(':')}</b>\n" + string.Join("\n", keys.Select(k => Line(k, attack[k])));
            _defense.text = $"<b>{Localization.Get("item.defenseStats").TrimEnd(':')}</b>\n" + string.Join("\n", keys.Select(k => Line(k, defense[k])));
            _bonuses.text = $"<b>{Localization.Get("item.bonuses").TrimEnd(':')}</b>\n" + string.Join("\n", bonusKeys.Select(k => Line(k, bonuses[k])));
        }

        /// <summary>
        /// equipments.ts updatePlayerImage: the idle frame of the orientation (left is right, flipped), drawn on a
        /// 300 × 150 canvas shown at 8em × 4em: the base sprite fills 64 × 64 CSS pixels in the middle, and each
        /// drawn equipment of a different size is scaled and moved by its offsets as the original does.
        /// </summary>
        void UpdatePlayerImage()
        {
            foreach (var layer in _layers) UnityEngine.Object.Destroy(layer.gameObject);
            _layers.Clear();

            var sprite = _player.Sprite;
            if (sprite == null || sprite.Key == "death") return;

            var flip = _orientation == Modules.Orientation.Left;
            var frame = flip ? "right" : _orientation.ToString().ToLowerInvariant();
            if (!sprite.Animations.TryGetValue("idle_" + frame, out var idle)) return;

            // Canvas units → CSS pixels: x × 128/300, y × 64/150; the draws are scaled by ratio 0.5 in x and moved by 150.
            const float sx = 128f / 300, sy = 64f / 150, ratio = 0.5f;
            void Draw(Entities.EntitySprite layerSprite, float dx, float dy, float width, float height)
            {
                var texture = Kit.Catalog.GetSheet(layerSprite.Info);
                if (texture == null) return;
                var image = UiKit.Box(_canvas, layerSprite.Key).gameObject.AddComponent<RawImage>();
                image.texture = texture;
                image.raycastTarget = false;
                var rows = texture.height / (float)layerSprite.Height;
                var u = layerSprite.Width / (float)texture.width;
                var v = 1 / rows;
                // Facing left: the canvas is flipped (translate(width, 0), scale(-1, 1)), so mirror the place and the image.
                image.uvRect = flip ? new Rect(u, 1 - (idle.Row + 1) * v, -u, v) : new Rect(0, 1 - (idle.Row + 1) * v, u, v);

                var x = (150 + dx) * ratio * sx;
                var w = width * ratio * sx;
                if (flip) x = 128 - x - w;
                UiKit.Place(image.rectTransform, x, dy * sy, w, height * sy);
                _layers.Add(image);
            }

            Draw(sprite, 0, 0, 300, 150);

            foreach (var type in _player.EquipmentOrder)
            {
                var equipment = _player.Equipments[(int)type];
                if (!equipment.Drawable || !equipment.Exists()) continue;
                var layer = _sprites(equipment.Key);
                if (layer == null) continue;
                if (!layer.Loaded) layer.Load();

                float scaleW = 1, scaleH = 1, dx = 0, dy = 0;
                if (layer.Width != sprite.Width || layer.Height != sprite.Height)
                {
                    scaleW = layer.Width / (float)sprite.Width;
                    scaleH = layer.Height / (float)sprite.Height;
                    dx = (float)layer.OffsetX * 4 - 8;
                    dy = (float)layer.OffsetY - 8;
                    if (layer.Width == 64) dx -= 48;
                    if (layer.Height == 64) dy -= 28;
                }
                Draw(layer, dx, dy, 300 * scaleW, 150 * scaleH);
            }
        }

        /// <summary>For tests.</summary>
        public Modules.Orientation Orientation => _orientation;
        public int LayerCount => _layers.Count;
        public void ClickSlot(Modules.Equipment type) => Unequip?.Invoke(type);
    }
}
