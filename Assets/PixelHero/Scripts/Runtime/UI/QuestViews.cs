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
    /// menu/quests.ts: #quests (.slice-container) with the quest list (flex 3) and the log of the selected quest
    /// (flex 4), both .slice-inner-container boxes (inner-container.png, border 2em, padding 0.75em). Names are
    /// green when finished, yellow when started. The log shows the tutorial after every update.
    /// </summary>
    public sealed class QuestsView : ContainerWindow
    {
        readonly Player _player;
        readonly RectTransform _list;
        readonly float _listWidth;
        readonly List<(Task task, Text name)> _items = new List<(Task, Text)>();
        readonly RectTransform _log;
        readonly TextFlow _logFlow;
        Task _shown;

        public QuestsView(UiKit kit, RectTransform parent, Player player) : base(kit, parent, "#quests")
        {
            _player = player;

            // #quests-container: row, gap 0.5em; #quests-list flex 3, #quests-logs flex 4.
            var share = (ContentWidth - 0.5f * UiKit.Em) / 7;
            var listBox = Inner(Content, "#quests-list", 0, 3 * share);
            var logBox = Inner(Content, "#quests-logs", 3 * share + 0.5f * UiKit.Em, 4 * share);

            _list = UiKit.ScrollView(listBox, "ul");
            _listWidth = listBox.sizeDelta.x - 0.25f * UiKit.Em; // padding-right 0.25em
            _log = UiKit.ScrollView(logBox, "#quests-logs-container");
            _logFlow = new TextFlow(kit, _log, logBox.sizeDelta.x);
            Localization.LanguageChanged += () => BuildLog(_shown);
        }

        /// <summary>A .slice-inner-container column; returns its padded inner area.</summary>
        RectTransform Inner(RectTransform parent, string name, float left, float width)
        {
            var box = Kit.Sliced(parent, name, "inner-container", 2 * UiKit.Em);
            UiKit.Place(box.rectTransform, left, 0, width, ContentHeight);
            var inner = UiKit.Box(box.transform, "padding");
            UiKit.Place(inner, 0.75f * UiKit.Em, 0.75f * UiKit.Em, width - 1.5f * UiKit.Em, ContentHeight - 1.5f * UiKit.Em);
            return inner;
        }

        /// <summary>quests.ts handle: Batch builds the list, Progress recolours one quest; then the tutorial's log.</summary>
        public void Handle(Opcodes.Quest opcode, string key)
        {
            switch (opcode)
            {
                case Opcodes.Quest.Batch:
                    foreach (var task in _player.Quests.Values) CreateElement(task);
                    break;
                case Opcodes.Quest.Progress:
                    HandleProgress(key);
                    return;
            }

            BuildLog(_player.Quests["tutorial"]);
        }

        void HandleProgress(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            var quest = _player.Quests[key];
            if (quest == null || quest.Id >= _items.Count) return;
            Colour(_items[quest.Id].name, quest);
        }

        static void Colour(Text name, Task quest) =>
            name.color = UiKit.Css(quest.IsFinished() ? "#00a800" : quest.IsStarted() ? "#e0e000" : "#eee");

        /// <summary>li.slice-list-item (list-item.png, border 0.5em, padding 0.5em) with the stroked name; gap 0.25em.</summary>
        void CreateElement(Task quest)
        {
            var top = _items.Count * (2 * UiKit.Em + 0.25f * UiKit.Em);
            var item = Kit.Sliced(_list, "li", "list-item", 0.5f * UiKit.Em);
            item.raycastTarget = true;
            UiKit.Place(item.rectTransform, 0, top, _listWidth, 2 * UiKit.Em);
            var button = item.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Kit.Slice("list-item-hover"), selectedSprite = Kit.Slice("list-item") };
            button.onClick.AddListener(() => BuildLog(quest));

            var name = Kit.Label(item.transform, "p", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleLeft, true);
            UiKit.Place(name.rectTransform, 0.5f * UiKit.Em, 0, _listWidth - UiKit.Em, 2 * UiKit.Em);
            name.supportRichText = true;
            name.text = MessageFormat.ToRichText(quest.Name ?? "undefined");
            Colour(name, quest);

            _items.Add((quest, name));
            _list.sizeDelta = new Vector2(0, top + 2 * UiKit.Em);
        }

        /// <summary>quests.ts buildLog: title, short description, description, rewards and requirements.</summary>
        void BuildLog(Task quest)
        {
            _shown = quest;
            if (quest == null) return;

            _logFlow.Clear();
            _log.anchoredPosition = Vector2.zero; // scrollTop = 0

            // quest.description.split('|'): the short description, then the description.
            var parts = (quest.Description ?? "undefined").Split('|');
            string Rich(string html) => MessageFormat.ToRichText(html ?? "undefined");

            _logFlow.Add(Rich(quest.Name), Kit.Heading, 1.75f * UiKit.Em, "#eee", true, 0.25f * 1.75f * UiKit.Em);
            _logFlow.Add(Rich(parts[0]), Kit.Body, UiKit.Em, "#eee");
            _logFlow.Add(Localization.Get("quests.description"), Kit.Heading, 1.25f * UiKit.Em, "#eee", true, 0.25f * 1.25f * UiKit.Em);
            _logFlow.Add(Rich(parts.Length > 1 ? parts[1] : null), Kit.Body, UiKit.Em, "#eee");
            _logFlow.Add(Localization.Get("quests.reward"), Kit.Heading, 1.25f * UiKit.Em, "#eee", true, 0.25f * 1.25f * UiKit.Em);
            _logFlow.Add(quest.Rewards != null ? string.Join("\n", quest.Rewards.Select(Rich)) : "", Kit.Body, UiKit.Em, "#eee");
            _logFlow.Add(Localization.Get("quests.requirements"), Kit.Heading, 1.25f * UiKit.Em, "#eee", true, 0.25f * 1.25f * UiKit.Em);
            _logFlow.Add(Requirements(quest), Kit.Body, UiKit.Em, "#eee");

            _log.sizeDelta = new Vector2(0, _logFlow.Height);
        }

        string Requirements(Task quest)
        {
            if (quest.SkillRequirements.Count == 0 && quest.QuestRequirements.Count == 0) return Localization.Get("quests.none");

            var lines = new List<string>();
            foreach (var key in quest.QuestRequirements)
                lines.Add(Localization.Get("quests.requiresQuest", ("name", _player.Quests[key ?? ""]?.Name ?? "undefined")));
            foreach (var skill in quest.SkillRequirements.Keys)
            {
                var level = quest.SkillRequirements[skill] is double d ? JsValue.NumberToString(d) : Convert.ToString(quest.SkillRequirements[skill]);
                var name = skill.Length > 0 ? char.ToUpperInvariant(skill[0]) + skill.Substring(1) : skill;
                lines.Add(Localization.Get("quests.requiresSkill", ("skill", name), ("level", level)));
            }
            return MessageFormat.ToRichText(string.Join("\n", lines));
        }

        /// <summary>For tests: the quest names in list order and the title of the shown log.</summary>
        public IReadOnlyList<string> Names => _items.Select(i => i.name.text).ToList();
        public string ShownName => _shown?.Name;
        public void Select(string key) => BuildLog(_player.Quests[key]);
    }

    /// <summary>
    /// menu/quest.ts: #quest (.slice-container, 22em × 20em, centred column) offered by an NPC: the quest's name
    /// (h3), its description and "Start Quest", which accepts it (Packets.Quest {key}).
    /// </summary>
    public sealed class QuestDialogView : ContainerWindow
    {
        readonly Player _player;
        readonly Text _title, _description;
        string _activeQuest = "";

        public event Action<string> Accepted;

        public QuestDialogView(UiKit kit, RectTransform parent, Player player) : base(kit, parent, "#quest", 22 * UiKit.Em, 20 * UiKit.Em)
        {
            _player = player;

            _title = Kit.Label(Content, "quest-title", "", Kit.Heading, 1.25f * UiKit.Em, "#eee", TextAnchor.UpperCenter);
            _title.supportRichText = true;
            _description = Kit.Label(Content, "quest-description", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter);
            _description.supportRichText = true;
            _description.horizontalOverflow = HorizontalWrapMode.Wrap;

            // #quest-button: .slice-button, 12em wide, "Start Quest".
            var button = Kit.Button(Content, "#quest-button", "", () =>
            {
                if (string.IsNullOrEmpty(_activeQuest)) return;
                Hide();
                Accepted?.Invoke(_activeQuest);
            });
            UiKit.Place((RectTransform)button.transform, (ContentWidth - 12 * UiKit.Em) / 2, ContentHeight - 2 * UiKit.Em, 12 * UiKit.Em, 2 * UiKit.Em);
            var label = button.GetComponentInChildren<Text>();
            Localization.LanguageChanged += () => label.text = Localization.Get("quests.start");
            label.text = Localization.Get("quests.start");
        }

        public string ActiveQuest => _activeQuest;

        /// <summary>quest.ts handle (Quest Start).</summary>
        public void Handle(string key)
        {
            var quest = _player.Quests[key ?? ""];
            if (quest == null) return;

            _activeQuest = key;
            var parts = (quest.Description ?? "undefined").Split('|');

            _title.text = MessageFormat.ToRichText(quest.Name ?? "undefined");
            _description.text = MessageFormat.ToRichText(parts.Length > 1 ? parts[1] : "undefined");

            // A centred column: title (margin-bottom 0.25em), description, then the button.
            var titleHeight = UiKit.Height(_title, ContentWidth);
            var descriptionHeight = UiKit.Height(_description, ContentWidth);
            var total = titleHeight + 0.25f * 1.25f * UiKit.Em + descriptionHeight;
            var top = Math.Max(0, (ContentHeight - 2 * UiKit.Em - total) / 2);
            UiKit.Place(_title.rectTransform, 0, top, ContentWidth, titleHeight);
            UiKit.Place(_description.rectTransform, 0, top + titleHeight + 0.25f * 1.25f * UiKit.Em, ContentWidth, descriptionHeight);

            Show();
        }
    }

    /// <summary>
    /// menu/achievements.ts: #achievements (.slice-container) with a region tab above it (tab.png, Fibberish,
    /// arrows to switch regions) and the achievements of the current region in a .slice-inner-container list:
    /// the coin slot (gold coin when finished), the title (gold when finished), the description and the progress.
    ///
    /// A Progress update finds its row by the achievement's key (port fix, ANALYSIS.md §9 #8: the original used the
    /// index among all achievements, task.id, so it could touch the wrong row of the shown region).
    /// </summary>
    public sealed class AchievementsView : ContainerWindow
    {
        readonly Player _player;
        readonly Text _tabText;
        readonly RectTransform _list;
        readonly float _listWidth;
        readonly List<Row> _rows = new List<Row>();
        readonly List<string> _regions = new List<string>();
        string _currentRegion = "Mudwich";

        sealed class Row
        {
            public string Key;
            public RectTransform Root;
            public Image Coin;
            public Text Title, Description, Progress;
        }

        public AchievementsView(UiKit kit, RectTransform parent, Player player) : base(kit, parent, "#achievements")
        {
            _player = player;

            // #achievements-tab: .slice-tab, 11em × 3em, 3em above the window, centred.
            var tab = Kit.Sliced(Root, "#achievements-tab", "tab", UiKit.Em);
            var tabRect = tab.rectTransform;
            tabRect.anchorMin = tabRect.anchorMax = tabRect.pivot = new Vector2(0.5f, 1);
            tabRect.anchoredPosition = new Vector2(0, 3 * UiKit.Em);
            tabRect.sizeDelta = new Vector2(11 * UiKit.Em, 3 * UiKit.Em);
            _tabText = Kit.Label(tab.transform, "#achievements-tab-text", "", Kit.Heading, 1.2f * UiKit.Em, "#eee", TextAnchor.MiddleCenter);
            UiKit.Fill(_tabText.rectTransform);
            Arrow(tab.rectTransform, true);
            Arrow(tab.rectTransform, false);

            // #achievements-content: .slice-inner-container, padding 0.75em; the list scrolls, gap 0.5em.
            var content = Kit.Sliced(Content, "#achievements-content", "inner-container", 2 * UiKit.Em);
            UiKit.Place(content.rectTransform, 0, 0, ContentWidth, ContentHeight);
            var inner = UiKit.Box(content.transform, "padding");
            UiKit.Place(inner, 0.75f * UiKit.Em, 0.75f * UiKit.Em, ContentWidth - 1.5f * UiKit.Em, ContentHeight - 1.5f * UiKit.Em);
            _list = UiKit.ScrollView(inner, "ul");
            _listWidth = inner.sizeDelta.x - 0.25f * UiKit.Em;
        }

        /// <summary>
        /// The tab arrows: .slice-list-item boxes (border 1em on three sides) 0.375em from the top, 1.6875em outside
        /// the tab, holding the colour-button arrows (guilds/elements 9×16 at 105,78 and 115,78, 2×).
        /// </summary>
        void Arrow(RectTransform tab, bool left)
        {
            var box = Kit.Sliced(tab, left ? "#achievements-tab-arrow-left" : "#achievements-tab-arrow-right", "list-item", UiKit.Em);
            box.raycastTarget = true;
            var size = new Vector2(18 + 0.5625f * UiKit.Em, 32 + 0.625f * UiKit.Em);
            var rect = box.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(left ? 0 : 1, 1);
            rect.pivot = new Vector2(left ? 0 : 1, 1);
            rect.anchoredPosition = new Vector2(left ? -1.6875f * UiKit.Em : 1.6875f * UiKit.Em, -0.375f * UiKit.Em);
            rect.sizeDelta = size;

            var arrow = UiKit.Box(box.transform, "colour-button-arrow").gameObject.AddComponent<Image>();
            arrow.sprite = Kit.Region("guilds/elements", left ? 105 : 115, 78, 9, 16);
            arrow.raycastTarget = false;
            UiKit.Centre(arrow.rectTransform, 18, 32);

            var button = box.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Kit.Slice("list-item-hover"), selectedSprite = Kit.Slice("list-item") };
            button.onClick.AddListener(left ? (UnityEngine.Events.UnityAction)TabLeft : TabRight);
        }

        /// <summary>achievements.ts handle.</summary>
        public void Handle(Opcodes.Achievement opcode, string key)
        {
            if (opcode == Opcodes.Achievement.Batch)
            {
                DisplayAchievements();
                return;
            }

            var task = _player.Achievements[key ?? ""];
            if (task == null) return;

            // Port fix (ANALYSIS.md §9 #8): the row is found by key. A secret achievement gets its row when it is
            // discovered and is updated afterwards (the original added another row on every update).
            var row = _rows.Find(candidate => candidate.Key == key);

            if (task.Secret && row == null)
            {
                CreateAchievement(task, key);
                return;
            }

            Update(row, task);
        }

        void TabLeft()
        {
            var index = _regions.IndexOf(_currentRegion);
            index = index == 0 ? _regions.Count - 1 : index - 1;
            _currentRegion = _regions[index];
            DisplayAchievements();
        }

        void TabRight()
        {
            var index = _regions.IndexOf(_currentRegion);
            index = index == _regions.Count - 1 ? 0 : index + 1;
            _currentRegion = _regions[index];
            DisplayAchievements();
        }

        /// <summary>achievements.ts displayAchievements: the current region's achievements; Miscellaneous goes last.</summary>
        void DisplayAchievements()
        {
            foreach (var row in _rows) UnityEngine.Object.Destroy(row.Root.gameObject);
            _rows.Clear();
            _list.anchoredPosition = Vector2.zero;
            _list.sizeDelta = Vector2.zero;

            foreach (var key in _player.Achievements.Keys)
            {
                var task = _player.Achievements[key];
                var region = string.IsNullOrEmpty(task.Region) ? "Miscellaneous" : task.Region;
                if (!_regions.Contains(region)) _regions.Add(region);
                if (region == _currentRegion) CreateAchievement(task, key);
            }

            // regions.splice(regions.indexOf('Miscellaneous'), 1): -1 removes the last region.
            var misc = _regions.IndexOf("Miscellaneous");
            if (misc >= 0) _regions.RemoveAt(misc);
            else if (_regions.Count > 0) _regions.RemoveAt(_regions.Count - 1);
            _regions.Add("Miscellaneous");

            _tabText.text = _currentRegion;
        }

        /// <summary>li.achievement-element.slice-list: coin slot, title (flex 1), description (flex 2), progress.</summary>
        void CreateAchievement(Task task, string key)
        {
            const float rowHeight = 3.5f * UiKit.Em; // coin slot 40px + the list's 0.5em padding above and below
            var top = _rows.Count * (rowHeight + 0.5f * UiKit.Em);
            var row = Kit.Sliced(_list, "li", "list", UiKit.Em).rectTransform;
            UiKit.Place(row, 0, top, _listWidth, rowHeight);

            var slot = UiKit.Box(row, "coin-slot").gameObject.AddComponent<Image>();
            slot.sprite = Kit.Region("achievements", 0, 0, 20, 20);
            slot.raycastTarget = false;
            UiKit.Place(slot.rectTransform, 0.5f * UiKit.Em, 0.5f * UiKit.Em, 40, 40);
            var coin = UiKit.Box(slot.transform, "coin").gameObject.AddComponent<Image>();
            coin.sprite = Kit.Region("achievements", 21, 2, 16, 16);
            coin.raycastTarget = false;
            UiKit.Place(coin.rectTransform, 0.25f * UiKit.Em, 0.25f * UiKit.Em, 32, 32);
            coin.enabled = false;

            // flex 1 / flex 2 of the width after the slot and the gaps; progress at the right.
            var rest = _listWidth - UiKit.Em - 40 - 1.5f * UiKit.Em - 3 * UiKit.Em;
            var title = Kit.Label(row, "achievement-title", "", Kit.Heading, 1.1f * UiKit.Em, "#fff", TextAnchor.MiddleLeft, true);
            UiKit.Place(title.rectTransform, 0.5f * UiKit.Em + 40 + 0.5f * UiKit.Em, 0, rest / 3, rowHeight);
            title.horizontalOverflow = HorizontalWrapMode.Wrap;
            title.supportRichText = true;
            var description = Kit.Label(row, "achievement-description", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleLeft, true);
            UiKit.Place(description.rectTransform, title.rectTransform.anchoredPosition.x + rest / 3 + 0.5f * UiKit.Em, 0, rest * 2 / 3, rowHeight);
            description.horizontalOverflow = HorizontalWrapMode.Wrap;
            description.supportRichText = true;
            var progress = Kit.Label(row, "achievement-progress", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleRight, true);
            UiKit.Place(progress.rectTransform, _listWidth - 3.5f * UiKit.Em, 0, 3 * UiKit.Em, rowHeight);

            title.text = MessageFormat.ToRichText(task.Name ?? "undefined");
            description.text = MessageFormat.ToRichText(task.Description ?? "undefined");

            if (task.IsFinished())
            {
                // createAchievement colours a finished title #f4b41b (update uses #fcda1d).
                title.color = UiKit.Css("#f4b41b");
                coin.enabled = !task.Secret; // coin-<key> images for secret achievements are not in the port yet
            }
            else if (task.IsStarted()) progress.text = ProgressText(task);

            _rows.Add(new Row { Key = key, Root = row, Coin = coin, Title = title, Description = description, Progress = progress });
            _list.sizeDelta = new Vector2(0, top + rowHeight);
        }

        static string ProgressText(Task task) =>
            task.IsStarted() ? $"{Js(task.Stage - 1)}/{Js(task.StageCount - 1)}" : "";

        static string Js(double? value) => value is double v ? JsValue.NumberToString(v) : "NaN";

        /// <summary>achievements.ts update.</summary>
        void Update(Row row, Task task)
        {
            if (row == null) return;

            row.Title.text = MessageFormat.ToRichText(task.Name ?? "undefined");
            row.Description.text = MessageFormat.ToRichText(task.Description ?? "undefined");

            if (task.IsFinished())
            {
                row.Title.color = UiKit.Css("#fcda1d");
                row.Progress.text = "";
                row.Coin.enabled = true;
            }
            else
            {
                row.Title.color = UiKit.Css("#fff");
                row.Coin.enabled = false;
                row.Progress.text = ProgressText(task);
            }
        }

        /// <summary>For tests: the shown region and the titles of its rows.</summary>
        public string CurrentRegion => _currentRegion;
        public IReadOnlyList<string> Titles => _rows.Select(r => r.Title.text).ToList();
        public IReadOnlyList<string> Regions => _regions;
    }
}
