using System;
using System.Collections.Generic;
using System.Linq;
using PixelHero.Common;
using PixelHero.Network;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// menu/leaderboards.ts: #leaderboards (.slice-container .stroke) with #leaderboards-search (the search input
    /// and the list of leaderboards: total experience, PvP kills, every skill in Modules.SkillsOrder, then the mobs
    /// the hub names) and #leaderboards-results (one row per player: name, then experience or kills; red for
    /// cheaters). Both are .slice-inner-container columns (flex 1, gap 0.5em). The lists come from the hub's
    /// /leaderboards; with no hub configured the window is never filled.
    /// </summary>
    public sealed class LeaderboardsView : ContainerWindow
    {
        /// <summary>leaderboards.ts SearchElement: the name shown, the URL term and its type.</summary>
        sealed class SearchElement
        {
            /// <summary>A localization key for the port's own names (null for a mob name from the hub).</summary>
            public string NameKey;
            public string Name;
            public string Key;
            /// <summary>'skill', 'mob', 'pvp' or ''.</summary>
            public string Type;

            public string DisplayName => NameKey != null ? Localization.Get(NameKey) : Name;
        }

        readonly string _hub;
        readonly InputField _search;
        readonly RectTransform _searchList, _resultsList;
        readonly float _searchWidth, _resultsWidth;
        readonly List<SearchElement> _searchElements = new List<SearchElement>();
        readonly List<(SearchElement element, RectTransform row, Text name)> _searchRows = new List<(SearchElement, RectTransform, Text)>();
        readonly List<GameObject> _resultRows = new List<GameObject>();
        Dictionary<string, object> _availableMobs = new Dictionary<string, object>();

        public LeaderboardsView(UiKit kit, RectTransform parent, string hub) : base(kit, parent, "#leaderboards")
        {
            _hub = hub;

            // #leaderboards-search, #leaderboards-results: flex 1 each, gap 0.5em.
            var width = (ContentWidth - 0.5f * UiKit.Em) / 2;
            var searchBox = Inner(Content, "#leaderboards-search", 0, width);
            var resultsBox = Inner(Content, "#leaderboards-results", width + 0.5f * UiKit.Em, width);

            // #leaderboards-search-input.slice-input (font-size 0.875em, padding 0.625em 0.75em), then the ul (gap 0.5em).
            var inputFont = 0.875f * UiKit.Em;
            var inputHeight = inputFont * (1 + 2 * 0.625f);
            _search = kit.Input(searchBox, "#leaderboards-search-input", "", inputFont, false, 32);
            UiKit.Place((RectTransform)_search.transform, 0, 0, searchBox.sizeDelta.x, inputHeight);
            var placeholder = (Text)_search.placeholder;
            placeholder.text = Localization.Get("leaderboards.search");
            _search.onValueChanged.AddListener(HandleInput);

            var searchUl = UiKit.Box(searchBox, "ul");
            UiKit.Place(searchUl, 0, inputHeight + 0.5f * UiKit.Em, searchBox.sizeDelta.x, searchBox.sizeDelta.y - inputHeight - 0.5f * UiKit.Em);
            _searchList = UiKit.ScrollView(searchUl, "ul");
            _searchWidth = searchBox.sizeDelta.x - 0.25f * UiKit.Em; // padding-right 0.25em

            _resultsList = UiKit.ScrollView(resultsBox, "ul");
            _resultsWidth = resultsBox.sizeDelta.x - 0.25f * UiKit.Em;

            // The default search elements.
            _searchElements.Add(new SearchElement { NameKey = "leaderboards.total", Key = "", Type = "" });
            _searchElements.Add(new SearchElement { NameKey = "leaderboards.pvp", Key = "pvp", Type = "pvp" });

            // Append all the skills to the search elements
            foreach (var index in ((List<object>)ModuleTables.SkillsOrder).Select(o => (int)(double)o))
                _searchElements.Add(new SearchElement
                {
                    NameKey = "skill." + (Modules.Skills)index, Key = Js.JsValue.NumberToString(index), Type = "skill"
                });

            Localization.LanguageChanged += () =>
            {
                placeholder.text = Localization.Get("leaderboards.search");
                foreach (var (element, _, name) in _searchRows) name.text = element.DisplayName;
                HandleInput(_search.text);
            };

            // Hub is not enabled, stop here.
            if (string.IsNullOrEmpty(_hub)) return;

            // Fetch the available mobs from the hub
            HubRequests.Get($"{_hub}/leaderboards", text =>
            {
                if (Root == null) return; // the scene was reloaded meanwhile
                var data = HubRequests.Json(text, out var ok);
                if (!ok)
                {
                    Debug.LogWarning($"[PixelHero] Leaderboards: {(text == null ? "Failed to fetch" : "invalid JSON")}");
                    return;
                }

                var availableMobs = HubRequests.Property(data, "availableMobs");
                if (!HubRequests.Truthy(availableMobs)) return;

                // Save the available mobs.
                _availableMobs = availableMobs as Dictionary<string, object> ?? new Dictionary<string, object>();

                // Append the name of the mobs to the search elements.
                foreach (var mob in _availableMobs)
                    _searchElements.Add(new SearchElement { Name = HubRequests.Template(mob.Value), Key = mob.Key, Type = "mob" });

                Load();
            });
        }

        /// <summary>A .slice-inner-container column (inner-container.png, border 2em, padding 0.75em); returns its padded area.</summary>
        RectTransform Inner(RectTransform parent, string name, float left, float width)
        {
            var box = Kit.Sliced(parent, name, "inner-container", 2 * UiKit.Em);
            UiKit.Place(box.rectTransform, left, 0, width, ContentHeight);
            var inner = UiKit.Box(box.transform, "padding");
            UiKit.Place(inner, 0.75f * UiKit.Em, 0.75f * UiKit.Em, width - 1.5f * UiKit.Em, ContentHeight - 1.5f * UiKit.Em);
            return inner;
        }

        /// <summary>
        /// Loads the available search elements into the search list, then selects the first one.
        /// </summary>
        void Load()
        {
            // Clear the search list
            foreach (var (_, row, _) in _searchRows) UnityEngine.Object.Destroy(row.gameObject);
            _searchRows.Clear();

            // Create a search element for each search element.
            foreach (var element in _searchElements) CreateSearchElement(element);
            Layout(_searchList, _searchRows.Select(r => r.row));

            // Select the first element in the list.
            HandleSearchElement(_searchElements[0]);
        }

        /// <summary>
        /// The search input: every element whose name does not contain the text is hidden (the ul's flex column
        /// closes the gaps).
        /// </summary>
        void HandleInput(string value)
        {
            var input = value.ToLowerInvariant();

            foreach (var (_, row, name) in _searchRows)
                row.gameObject.SetActive(name.text.ToLowerInvariant().Contains(input));
            Layout(_searchList, _searchRows.Select(r => r.row));
        }

        /// <summary>
        /// A click on a search element: ask the hub for that leaderboard and list the results.
        /// </summary>
        void HandleSearchElement(SearchElement searchElement)
        {
            var url = $"{_hub}/leaderboards";

            // If we have a search type then we append that onto the URL.
            if (searchElement.Type.Length > 0) url += $"?{searchElement.Type}={searchElement.Key}";

            // Fetch the results from the hub.
            HubRequests.Get(url, text =>
            {
                if (Root == null) return; // the scene was reloaded meanwhile
                var data = HubRequests.Json(text, out var ok);
                if (!ok)
                {
                    Debug.LogWarning($"[PixelHero] Leaderboards: {(text == null ? "Failed to fetch" : "invalid JSON")}");
                    return;
                }

                var list = HubRequests.Property(data, "list");
                if (!HubRequests.Truthy(list))
                {
                    Debug.Log("[PixelHero] No results found.");
                    return;
                }

                // Clear the results list.
                foreach (var row in _resultRows) UnityEngine.Object.Destroy(row);
                _resultRows.Clear();

                // Create a result element for each result.
                if (list is List<object> results)
                    foreach (var result in results) CreateResultElement(result, searchElement.Type);
                Layout(_resultsList, _resultRows.Select(r => (RectTransform)r.transform));
            });
        }

        /// <summary>menu.ts show, then scroll both lists back to the top.</summary>
        public override void Show()
        {
            base.Show();

            _searchList.anchoredPosition = Vector2.zero;
            _resultsList.anchoredPosition = Vector2.zero;
        }

        /// <summary>
        /// menu.ts hide, and the input field is cleared. As in the original (setting value fires no input event),
        /// the filter stays applied until the player types again.
        /// </summary>
        public override void Hide()
        {
            base.Hide();

            _search.SetTextWithoutNotify("");
        }

        /// <summary>leaderboards.ts keyDown: only Escape does something (it hides the window).</summary>
        public void KeyDown(bool escape)
        {
            if (!escape) return;

            Hide();
        }

        /// <summary>li.slice-list-item (list-item.png, border 0.5em, padding 0.5em) with the stroked name.</summary>
        void CreateSearchElement(SearchElement searchElement)
        {
            var item = Kit.Sliced(_searchList, "li", "list-item", 0.5f * UiKit.Em);
            item.raycastTarget = true;
            var button = item.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Kit.Slice("list-item-hover"), selectedSprite = Kit.Slice("list-item") };
            button.onClick.AddListener(() => HandleSearchElement(searchElement));

            var name = Kit.Label(item.transform, "p", searchElement.DisplayName, Kit.Body, UiKit.Em, "#fff", TextAnchor.MiddleLeft, true);
            UiKit.Place(name.rectTransform, 0.5f * UiKit.Em, 0, _searchWidth - UiKit.Em, 2 * UiKit.Em);

            _searchRows.Add((searchElement, item.rectTransform, name));
        }

        /// <summary>
        /// A result row: the username on the left and, on the right, the experience (total and skill leaderboards)
        /// or the kills (mob and PvP); both red (.text-red) for a cheater.
        /// </summary>
        void CreateResultElement(object result, string type)
        {
            var item = Kit.Sliced(_resultsList, "li", "list-item", 0.5f * UiKit.Em);
            var hover = item.gameObject.AddComponent<Button>();
            item.raycastTarget = true;
            hover.transition = Selectable.Transition.SpriteSwap;
            hover.spriteState = new SpriteState { highlightedSprite = Kit.Slice("list-item-hover"), selectedSprite = Kit.Slice("list-item") };

            var colour = HubRequests.Truthy(HubRequests.Property(result, "cheater")) ? "#e00a0a" : "#eee";
            var value = type == "skill" || type == "" ? HubRequests.Property(result, "experience") : HubRequests.Property(result, "kills");

            var name = Kit.Label(item.transform, "p", HubRequests.Template(HubRequests.Property(result, "_id")), Kit.Body, UiKit.Em, colour,
                TextAnchor.MiddleLeft, true);
            UiKit.Place(name.rectTransform, 0.5f * UiKit.Em, 0, _resultsWidth - UiKit.Em, 2 * UiKit.Em);
            var info = Kit.Label(item.transform, "p", HubRequests.Template(value), Kit.Body, UiKit.Em, colour, TextAnchor.MiddleRight, true);
            UiKit.Place(info.rectTransform, 0.5f * UiKit.Em, 0, _resultsWidth - UiKit.Em, 2 * UiKit.Em);

            _resultRows.Add(item.gameObject);
        }

        /// <summary>The ul's flex column: visible rows 2em high, 0.25em apart.</summary>
        void Layout(RectTransform list, IEnumerable<RectTransform> rows)
        {
            var width = list == _searchList ? _searchWidth : _resultsWidth;
            var top = 0f;
            var any = false;
            foreach (var row in rows)
            {
                if (!row.gameObject.activeSelf) continue;
                if (any) top += 0.25f * UiKit.Em;
                UiKit.Place(row, 0, top, width, 2 * UiKit.Em);
                top += 2 * UiKit.Em;
                any = true;
            }
            list.sizeDelta = new Vector2(0, top);
        }

        /// <summary>For tests: the names of the visible search elements and the result rows' texts.</summary>
        public IReadOnlyList<string> SearchNames => _searchRows.Where(r => r.row.gameObject.activeSelf).Select(r => r.name.text).ToList();
        public IReadOnlyList<string> Results =>
            _resultRows.Select(r => string.Join(" ", r.GetComponentsInChildren<Text>().Select(t => t.text))).ToList();
        public void Search(string text) => _search.text = text;
        public void Select(int index) => HandleSearchElement(_searchElements[index]);
    }
}
