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
    /// menu/guilds.ts: #guilds (.slice-container, padding-left 8.5em for the banner). Without a guild it lists the
    /// guilds (a click asks to join; the list is requested on show and every 12 s) and has the create form (banner
    /// colour, outline style and colour, name). In a guild it shows the name, the members (rank icon, name, world;
    /// a click on a lower-ranked member opens promote / demote / kick), the guild chat and leave (disband for the
    /// owner). The banner on the left shows the decoration.
    /// </summary>
    public sealed class GuildsView : ContainerWindow
    {
        // Sheet orders from scss/game/impl/_guilds.scss.
        static readonly string[] BannerOrder =
        {
            Modules.BannerColour.Grey, Modules.BannerColour.Green, Modules.BannerColour.Fuchsia, Modules.BannerColour.Red,
            Modules.BannerColour.Brown, Modules.BannerColour.Cyan, Modules.BannerColour.DarkGrey, Modules.BannerColour.Teal,
            Modules.BannerColour.GoldenYellow
        };

        static readonly string[] OutlineOrder =
        {
            Modules.BannerColour.GoldenYellow, Modules.BannerColour.Fuchsia, Modules.BannerColour.Red, Modules.BannerColour.Brown,
            Modules.BannerColour.Cyan, Modules.BannerColour.DarkGrey, Modules.BannerColour.Teal, Modules.BannerColour.Green,
            Modules.BannerColour.Grey
        };

        // The colour buttons: two rows of swatches (165,160 and 165,181, 21px apart).
        static readonly string[][] SwatchRows =
        {
            new[] { Modules.BannerColour.Teal, Modules.BannerColour.Green, Modules.BannerColour.Grey, Modules.BannerColour.DarkGrey, Modules.BannerColour.Red },
            new[] { Modules.BannerColour.GoldenYellow, Modules.BannerColour.Fuchsia, Modules.BannerColour.Brown, Modules.BannerColour.Cyan }
        };

        static readonly Modules.GuildRank[] RankIcons =
        {
            Modules.GuildRank.Landlord, Modules.GuildRank.Master, Modules.GuildRank.Elite, Modules.GuildRank.Veteran,
            Modules.GuildRank.Adept, Modules.GuildRank.Established, Modules.GuildRank.Emergent, Modules.GuildRank.Fledgling
        };

        const float SlotWidth = 125 * 2.5f, SlotHeight = 22 * 2.5f;

        readonly Player _player;
        readonly Func<double?> _serverId;
        readonly Image _banner, _outline;
        readonly RectTransform _listContainer, _create, _info;
        readonly RectTransform _guildList, _memberList;
        readonly Text _listInfo, _createError, _guildName, _leaveLabel;
        readonly InputField _nameInput, _chatInput;
        readonly RectTransform _chat, _chatLog, _memberContainer;
        readonly TextFlow _chatFlow;
        readonly Image _colourButton, _outlineButton;
        readonly RectTransform _memberDialog;
        readonly Text _memberName;
        readonly CanvasGroup _memberListGroup;
        readonly List<(string name, RectTransform row, Text server, Image icon)> _members = new List<(string, RectTransform, Text, Image)>();
        readonly List<GameObject> _guildRows = new List<GameObject>();
        readonly Image[] _sidebar = new Image[2];

        string _bannerColour = Modules.BannerColour.Grey, _bannerOutlineColour = Modules.BannerColour.GoldenYellow, _bannerCrest = Modules.BannerCrests.None;
        int _bannerOutline;
        string _colourSelection = "banner", _currentSidebar = "sidebar-members", _selectedMember;
        int _from, _to = 10;
        float _nextListRequest;

        /// <summary>Packets.Guild with these fields.</summary>
        public event Action<IReadOnlyList<(string key, object value)>> Send;

        public GuildsView(UiKit kit, RectTransform parent, Player player, Func<double?> serverId) : base(kit, parent, "#guilds")
        {
            _player = player;
            _serverId = serverId;

            // The content starts after padding-left 8.5em.
            var left = 7.5f * UiKit.Em;
            var width = ContentWidth - left;

            // .banner: absolute top -1.375em, left 1em; 50×128 columns of guilds/banners at 2.25×; the outline over it.
            _banner = UiKit.Box(Root, "banner").gameObject.AddComponent<Image>();
            UiKit.Place(_banner.rectTransform, UiKit.Em, -1.375f * UiKit.Em, 50 * 2.25f, 128 * 2.25f);
            _banner.raycastTarget = false;
            _outline = UiKit.Box(_banner.transform, "#banner-outline").gameObject.AddComponent<Image>();
            UiKit.Place(_outline.rectTransform, 0, 0.875f * UiKit.Em, 50 * 2.25f, 128 * 2.25f);
            _outline.raycastTarget = false;

            // ---- the list of guilds
            _listContainer = UiKit.Box(Content, "#guilds-list-container");
            UiKit.Place(_listContainer, left, 0, width, ContentHeight);
            Title(_listContainer, "guilds.title", width, 0);
            var listBox = Inner(_listContainer, "#guilds-list", 0, 1.75f * UiKit.Em, width, ContentHeight - 1.75f * UiKit.Em);
            _listInfo = Kit.Label(listBox, "#guilds-info", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(_listInfo.rectTransform, 0, 0, listBox.sizeDelta.x, UiKit.Em);
            var listArea = UiKit.Box(listBox, "ul area");
            UiKit.Place(listArea, 0, 0, listBox.sizeDelta.x, listBox.sizeDelta.y);
            _guildList = UiKit.ScrollView(listArea, "ul");
            var createGuild = ElementButton(Root, "#create-guild", "guilds.create", false, HandleCreate);
            UiKit.Place((RectTransform)createGuild.transform, 2.25f * UiKit.Em, 6 * UiKit.Em, 70, 35);
            _createGuildButton = createGuild.gameObject;

            // ---- the create form
            _create = Inner(Content, "#guilds-create", left, 0, width, ContentHeight);
            BuildCreate(width - 1.5f * UiKit.Em, ContentHeight - 1.5f * UiKit.Em, out _colourButton, out _outlineButton, out _nameInput, out _createError);
            _create.parent.gameObject.SetActive(false);

            // ---- the guild information
            _info = UiKit.Box(Content, "#guilds-info-container");
            UiKit.Place(_info, left, 0, width, ContentHeight);
            var leave = ElementButton(_info, "#guilds-leave", "guilds.leave", true, () => Send?.Invoke(new[] { ("opcode", (object)(double)(int)Opcodes.Guild.Leave) }));
            UiKit.Place((RectTransform)leave.transform, 0, 0, 70, 35);
            _leaveLabel = leave.GetComponentInChildren<Text>();
            _guildName = Kit.Label(_info, "#guilds-name", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(_guildName.rectTransform, 70 + 1.5f * UiKit.Em, 0.5f * UiKit.Em, width - 70 - 1.5f * UiKit.Em, UiKit.Em);
            var nameOrnament = UiKit.Box(_info, "ornament").gameObject.AddComponent<Image>();
            nameOrnament.sprite = Kit.Region("guilds/frames", 286, 8, 78, 6);
            UiKit.Place(nameOrnament.rectTransform, 70 + 1.5f * UiKit.Em + (width - 70 - 1.5f * UiKit.Em - 156) / 2, 1.5f * UiKit.Em, 156, 12);

            var bodyTop = 35 + 0.5f * UiKit.Em;
            var members = Inner(_info, "#member-list-container", 0, bodyTop, width, ContentHeight - bodyTop);
            _memberContainer = (RectTransform)members.parent;
            var memberArea = UiKit.Box(members, "ul area");
            UiKit.Place(memberArea, 0, 0, members.sizeDelta.x, members.sizeDelta.y);
            _memberListGroup = memberArea.gameObject.AddComponent<CanvasGroup>();
            _memberList = UiKit.ScrollView(memberArea, "ul");

            // dialog#guild-member-dialog: the member's name (h2) and promote / demote / kick.
            var dialog = Kit.Sliced(members, "#guild-member-dialog", "dialog", UiKit.Em);
            dialog.raycastTarget = true;
            _memberDialog = UiKit.Centre(dialog.rectTransform, 15 * UiKit.Em, 6 * UiKit.Em);
            _memberName = Kit.Label(_memberDialog, "#guild-member-selected", "", Kit.Heading, 1.375f * UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(_memberName.rectTransform, 0, UiKit.Em, 15 * UiKit.Em, 1.375f * UiKit.Em);
            var actions = new[] { ("#guild-member-promote", "guilds.promote", false, Opcodes.Guild.Promote),
                ("#guild-member-demote", "guilds.demote", true, Opcodes.Guild.Demote), ("#guild-member-kick", "guilds.kick", true, Opcodes.Guild.Kick) };
            for (var i = 0; i < actions.Length; i++)
            {
                var (id, key, red, opcode) = actions[i];
                var button = ElementButton(_memberDialog, id, key, red, () => MemberAction(opcode));
                UiKit.Place((RectTransform)button.transform, 0.5f * UiKit.Em + i * (70 + 0.5f * UiKit.Em) + (i == 2 ? 0.5f * UiKit.Em : 0), 3 * UiKit.Em, 70, 35);
            }
            _memberDialog.gameObject.SetActive(false);

            // #guilds-chat: the log over the input; hidden until the chat sidebar is chosen.
            var chat = Inner(_info, "#guilds-chat", 0, bodyTop, width, ContentHeight - bodyTop);
            _chat = (RectTransform)chat.parent;
            var logArea = UiKit.Box(chat, "#guilds-chat-log");
            UiKit.Place(logArea, 0, 0, chat.sizeDelta.x, chat.sizeDelta.y - 2.25f * UiKit.Em);
            _chatLog = UiKit.ScrollView(logArea, "log");
            _chatFlow = new TextFlow(kit, _chatLog, chat.sizeDelta.x);
            _chatInput = kit.Input(chat, "#guilds-chat-input", "", 0.9f * UiKit.Em, false, 256);
            UiKit.Place((RectTransform)_chatInput.transform, 0, chat.sizeDelta.y - 2 * UiKit.Em, chat.sizeDelta.x, 2 * UiKit.Em);
            Localization.LanguageChanged += () => ((Text)_chatInput.placeholder).text = Localization.Get("guilds.chatPlaceholder");
            ((Text)_chatInput.placeholder).text = Localization.Get("guilds.chatPlaceholder");
            _chat.gameObject.SetActive(false);

            // #sidebar-list: right -4.25em, top 6em: members and chat tabs (guilds/elements 43×22 at 0,57, 2×).
            for (var i = 0; i < 2; i++)
            {
                var id = i == 0 ? "sidebar-members" : "sidebar-chat";
                var tab = UiKit.Box(Root, "#" + id).gameObject.AddComponent<Image>();
                var rect = tab.rectTransform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1, 1);
                rect.anchoredPosition = new Vector2(4.25f * UiKit.Em, -(6 * UiKit.Em + i * 44));
                rect.sizeDelta = new Vector2(86, 44);
                var icon = UiKit.Box(tab.transform, i == 0 ? "members-icon" : "chat-icon").gameObject.AddComponent<Image>();
                icon.sprite = Kit.Region("guilds/elements", 242 + i * 14, 0, 12, 12);
                icon.raycastTarget = false;
                UiKit.Place(icon.rectTransform, UiKit.Em, 10, 24, 24);
                tab.gameObject.AddComponent<Button>().onClick.AddListener(() => HandleSidebar(id));
                _sidebar[i] = tab;
            }
            UpdateSidebar();

            _info.gameObject.SetActive(false);
            UpdateBanner();
            Localization.LanguageChanged += UpdateLeaveLabel;
        }

        readonly GameObject _createGuildButton;

        void Title(RectTransform parent, string key, float width, float top)
        {
            var title = Kit.Label(parent, "title-decoration", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            UiKit.Place(title.rectTransform, 0, top, width, UiKit.Em);
            Localization.LanguageChanged += () => title.text = Localization.Get(key);
            title.text = Localization.Get(key);
            var ornament = UiKit.Box(parent, "ornament").gameObject.AddComponent<Image>();
            ornament.sprite = Kit.Region("guilds/frames", 286, 8, 78, 6);
            UiKit.Place(ornament.rectTransform, (width - 156) / 2, top + UiKit.Em, 156, 12);
        }

        RectTransform Inner(RectTransform parent, string name, float left, float top, float width, float height)
        {
            var box = Kit.Sliced(parent, name, "inner-container", 2 * UiKit.Em);
            UiKit.Place(box.rectTransform, left, top, width, height);
            var inner = UiKit.Box(box.transform, "padding");
            UiKit.Place(inner, 0.75f * UiKit.Em, 0.75f * UiKit.Em, width - 1.5f * UiKit.Em, height - 1.5f * UiKit.Em);
            return inner;
        }

        /// <summary>
        /// .element-button(-red): guilds/elements 28×14 at 45,78 (red at 73,78), 2.5×; hover and active frames 14px
        /// lower; the label at line-height 1.75em.
        /// </summary>
        Button ElementButton(RectTransform parent, string name, string key, bool red, Action onClick)
        {
            Sprite Frame(int f) => Kit.Region("guilds/elements", red ? 73 : 45, 78 + f * 14, 28, 14);
            var image = UiKit.Box(parent, name).gameObject.AddComponent<Image>();
            image.sprite = Frame(0);
            var button = image.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Frame(1), pressedSprite = Frame(2), selectedSprite = Frame(0) };
            button.onClick.AddListener(() => onClick());
            var label = Kit.Label(image.transform, "label", "", Kit.Body, 0.75f * UiKit.Em, "#eee", TextAnchor.MiddleCenter, true);
            UiKit.Fill(label.rectTransform);
            Localization.LanguageChanged += () => label.text = Localization.Get(key);
            label.text = Localization.Get(key);
            return button;
        }

        void BuildCreate(float width, float height, out Image colourButton, out Image outlineButton, out InputField name, out Text error)
        {
            // #banner-select: the banner colour button over the outline button (22×22 at 0,124 / 22,124 …, 2×).
            Image SelectButton(string id, int row, float top, Action onClick)
            {
                var image = UiKit.Box(_create, id).gameObject.AddComponent<Image>();
                image.sprite = Kit.Region("guilds/elements", 0, 124 + row * 22, 22, 22);
                UiKit.Place(image.rectTransform, 1.5f * UiKit.Em, top, 44 + 0.75f * UiKit.Em, 44 + 0.75f * UiKit.Em);
                image.gameObject.AddComponent<Button>().onClick.AddListener(() => onClick());
                return image;
            }

            colourButton = SelectButton("#banner-colour-button", 0, height / 2 - 4 * UiKit.Em, () => HandleColourSelection("banner"));
            outlineButton = SelectButton("#banner-outline-button", 1, height / 2 + UiKit.Em, () => HandleColourSelection("outline"));
            foreach (var (arrowLeft, x, direction) in new[] { (true, 105, -1), (false, 115, 1) })
            {
                var arrow = UiKit.Box(outlineButton.transform, arrowLeft ? "colour-button-arrow-left" : "colour-button-arrow-right").gameObject.AddComponent<Image>();
                arrow.sprite = Kit.Region("guilds/elements", x, 78, 9, 16);
                UiKit.Place(arrow.rectTransform, arrowLeft ? -1.5f * UiKit.Em : 44 + 0.75f * UiKit.Em + 1.5f * UiKit.Em - 18, 0.375f * UiKit.Em, 18, 32);
                var step = direction;
                arrow.gameObject.AddComponent<Button>().onClick.AddListener(() => HandleOutlineStyle(step));
            }

            // #banner-options: the name, the colour grid (3 columns), the error and the back / create buttons.
            var optionsLeft = 1.5f * UiKit.Em + 44 + 0.75f * UiKit.Em + 1.5f * UiKit.Em;
            var optionsWidth = width - optionsLeft;
            name = Kit.Input(_create, "#guilds-name-input", "", 0.9f * UiKit.Em, false, 16);
            // #banner-options: a column centred vertically, gap 0.25em: input (2em), the grid (3 rows of 40px),
            // the error (0.675em) and the button row (.element-button at font-size 0.75em: 52.5 × 26.25).
            const float gap = 0.25f * UiKit.Em, gridHeight = 3 * 40 + 2 * gap, errorHeight = 0.675f * UiKit.Em, buttonWidth = 70 * 0.75f, buttonHeight = 35 * 0.75f;
            var top = (height - (2 * UiKit.Em + gridHeight + errorHeight + buttonHeight + 3 * gap)) / 2;
            UiKit.Place((RectTransform)name.transform, optionsLeft, top, optionsWidth, 2 * UiKit.Em);
            var placeholder = (Text)name.placeholder;
            Localization.LanguageChanged += () => placeholder.text = Localization.Get("guilds.namePlaceholder");
            placeholder.text = Localization.Get("guilds.namePlaceholder");

            var swatches = SwatchRows.SelectMany((row, r) => row.Select((colour, c) => (colour, r, c))).ToList();
            var columnWidth = optionsWidth / 3;
            for (var i = 0; i < swatches.Count; i++)
            {
                var (colour, r, c) = swatches[i];
                var swatch = UiKit.Box(_create, $"banner-colour-button-{colour}").gameObject.AddComponent<Image>();
                swatch.sprite = Kit.Region("guilds/elements", 165 + c * 21, 160 + r * 21, 20, 20);
                UiKit.Place(swatch.rectTransform, optionsLeft + i % 3 * columnWidth + (columnWidth - 40) / 2, top + 2 * UiKit.Em + gap + i / 3 * (40 + gap), 40, 40);
                var frame = UiKit.Box(swatch.transform, "banner-colour-button-outline").gameObject.AddComponent<Image>();
                frame.sprite = Kit.Region("guilds/elements", 144, 160, 20, 20);
                frame.raycastTarget = false;
                UiKit.Fill(frame.rectTransform);
                var picked = colour;
                swatch.gameObject.AddComponent<Button>().onClick.AddListener(() =>
                {
                    if (_colourSelection == "banner") _bannerColour = picked;
                    else _bannerOutlineColour = picked;
                    UpdateBanner();
                });
            }

            var errorTop = top + 2 * UiKit.Em + gap + gridHeight + gap;
            error = Kit.Label(_create, "#guilds-create-error", "", Kit.Body, 0.675f * UiKit.Em, "rgb(233, 31, 31)", TextAnchor.UpperCenter);
            error.supportRichText = true;
            error.horizontalOverflow = HorizontalWrapMode.Wrap;
            UiKit.Place(error.rectTransform, optionsLeft, errorTop, optionsWidth, errorHeight);

            var back = ElementButton(_create, "#guilds-back-button", "guilds.back", true, HandleBackButton);
            var buttonTop = errorTop + errorHeight + gap;
            UiKit.Place((RectTransform)back.transform, optionsLeft, buttonTop, buttonWidth, buttonHeight);
            var create = ElementButton(_create, "#guilds-create-button", "guilds.create", false, HandleCreateConfirm);
            UiKit.Place((RectTransform)create.transform, optionsLeft + optionsWidth - buttonWidth, buttonTop, buttonWidth, buttonHeight);
        }

        // ---------------------------------------------------------------- server

        /// <summary>guilds.ts handle.</summary>
        public void Handle(Opcodes.Guild opcode, Dictionary<string, object> info)
        {
            string Str(string key) => info.TryGetValue(key, out var value) ? value as string : null;
            double? Num(string key) => info.TryGetValue(key, out var value) ? value as double? : null;

            switch (opcode)
            {
                case Opcodes.Guild.Join:
                    HandleMemberJoin(Str("username"), Num("serverId") ?? -1);
                    break;
                case Opcodes.Guild.Login:
                    HandleConnect(info);
                    break;
                case Opcodes.Guild.Leave:
                    var username = Str("username");
                    if (!string.IsNullOrEmpty(username) && username != Username)
                    {
                        _player.RemoveGuildMember(username);
                        HandleMemberLeave(username);
                        break;
                    }
                    _player.SetGuild();
                    HandleBackButton();
                    break;
                case Opcodes.Guild.Rank:
                    SetRank(Str("username"), Num("rank"));
                    break;
                case Opcodes.Guild.Update:
                    LoadMembers(Player.ParseMembers(info.TryGetValue("members", out var members) ? members : null));
                    break;
                case Opcodes.Guild.List:
                    LoadList(info.TryGetValue("guilds", out var guilds) ? guilds as List<object> : null, Num("total") ?? 0);
                    break;
                case Opcodes.Guild.Error:
                    SetError(Str("message"));
                    break;
                case Opcodes.Guild.Chat:
                    HandleChat(Str("username"), Num("serverId"), Str("message"));
                    break;
            }
        }

        string Username => (_player.Name ?? "undefined").ToLowerInvariant();

        void HandleCreate()
        {
            _listContainer.gameObject.SetActive(false);
            _info.gameObject.SetActive(false);
            _createGuildButton.SetActive(false);
            _create.parent.gameObject.SetActive(true);
        }

        /// <summary>guilds.ts handleBackButton: reset the banner, clear the lists and show the guild list again.</summary>
        void HandleBackButton()
        {
            _bannerColour = Modules.BannerColour.Grey;
            _bannerOutline = (int)Modules.BannerOutline.StyleOne;
            _bannerOutlineColour = Modules.BannerColour.GoldenYellow;
            _bannerCrest = Modules.BannerCrests.None;
            UpdateBanner();
            SetError();

            foreach (var member in _members) UnityEngine.Object.Destroy(member.row.gameObject);
            _members.Clear();
            _chatFlow.Clear();

            _create.parent.gameObject.SetActive(false);
            _info.gameObject.SetActive(false);
            _listContainer.gameObject.SetActive(true);
            _createGuildButton.SetActive(true);
            RequestList();
        }

        /// <summary>guilds.ts handleCreateConfirm: 3 to 16 characters.</summary>
        void HandleCreateConfirm()
        {
            SetError();
            var name = _nameInput.text;
            if (name.Length < 3 || name.Length > 16)
            {
                SetErrorText(Localization.Get("guilds.nameLength"));
                return;
            }

            Send?.Invoke(new (string, object)[]
            {
                ("opcode", (double)(int)Opcodes.Guild.Create), ("name", name), ("colour", _bannerColour), ("outline", (double)_bannerOutline),
                ("outlineColour", _bannerOutlineColour), ("crest", _bannerCrest)
            });
        }

        void MemberAction(Opcodes.Guild opcode)
        {
            _memberListGroup.alpha = 1;
            _memberDialog.gameObject.SetActive(false);
            Send?.Invoke(new (string, object)[] { ("opcode", (double)(int)opcode), ("username", _selectedMember) });
        }

        /// <summary>guilds.ts handleConnect (Guild Login).</summary>
        void HandleConnect(Dictionary<string, object> info)
        {
            SetError();
            _listContainer.gameObject.SetActive(false);
            _createGuildButton.SetActive(false);
            _create.parent.gameObject.SetActive(false);
            _info.gameObject.SetActive(true);

            var decoration = info.TryGetValue("decoration", out var d) ? d as Dictionary<string, object> : null;
            string Decoration(string key) => decoration != null && decoration.TryGetValue(key, out var value) ? value as string : null;
            _bannerColour = string.IsNullOrEmpty(Decoration("banner")) ? Modules.BannerColour.Grey : Decoration("banner");
            _bannerOutline = decoration != null && decoration.TryGetValue("outline", out var outline) && outline is double o && o != 0
                ? (int)o : (int)Modules.BannerOutline.StyleOne;
            _bannerOutlineColour = string.IsNullOrEmpty(Decoration("outlineColour")) ? Modules.BannerColour.GoldenYellow : Decoration("outlineColour");
            _bannerCrest = Decoration("crest");
            UpdateBanner();

            foreach (var member in _members) UnityEngine.Object.Destroy(member.row.gameObject);
            _members.Clear();

            var name = info.TryGetValue("name", out var n) ? n as string : null;
            _guildName.text = MessageFormat.ToRichText(string.IsNullOrEmpty(name) ? "Unknown" : name);

            _owner = info.TryGetValue("owner", out var owner) && owner as string == Username;
            UpdateLeaveLabel();

            foreach (var member in Player.ParseMembers(info.TryGetValue("members", out var members) ? members : null))
                CreateMember(member.Rank, member.Username);
        }

        bool _owner;

        /// <summary>The leave button says Disband for the owner (once set, the original never resets it).</summary>
        void UpdateLeaveLabel()
        {
            if (_owner) _leaveLabel.text = Localization.Get("guilds.disband");
        }

        void HandleMemberJoin(string username, double serverId)
        {
            if (username == Username) return;
            CreateMember((int)Modules.GuildRank.Fledgling, username);
            LoadMembers(new List<Player.GuildMember> { new Player.GuildMember { Username = username, ServerId = serverId } });
        }

        void HandleMemberLeave(string username)
        {
            var index = _members.FindIndex(m => m.name == username);
            if (index < 0) return;
            UnityEngine.Object.Destroy(_members[index].row.gameObject);
            _members.RemoveAt(index);
            LayoutMembers();
        }

        void HandleSidebar(string menu)
        {
            // Only the clicked tab is active, even when nothing else changes (guilds.ts loadSidebar).
            _activeTab = menu;
            UpdateSidebar();
            if (_player.Guild == null || _currentSidebar == menu) return;

            _memberContainer.gameObject.SetActive(menu == "sidebar-members");
            _chat.gameObject.SetActive(menu == "sidebar-chat");
            _currentSidebar = menu;
        }

        string _activeTab = "sidebar-members";

        /// <summary>.sidebar-element: the active tab uses the active frame (44px lower) and sits 1em further in.</summary>
        void UpdateSidebar()
        {
            for (var i = 0; i < 2; i++)
            {
                var active = _activeTab == (i == 0 ? "sidebar-members" : "sidebar-chat");
                _sidebar[i].sprite = Kit.Region("guilds/elements", 0, 57 + (active ? 44 : 0), 43, 22);
            }
        }

        /// <summary>guilds.ts handleChat: "[Wn] Name » message".</summary>
        void HandleChat(string username, double? serverId, string message)
        {
            if (string.IsNullOrEmpty(username) || !(serverId is double server) || server == 0) return;
            var source = $"[W{JsValue.NumberToString(server)}] {FriendsView.FormatName(username)}";
            _chatFlow.Add(MessageFormat.ToRichText($"{source} » {message ?? "undefined"}"), Kit.Body, UiKit.Em, "#eee", true);
            _chatLog.sizeDelta = new Vector2(0, _chatFlow.Height);
        }

        void HandleColourSelection(string type) => _colourSelection = type;

        /// <summary>guilds.ts handleBannerOutlineStyle: the five styles wrap around.</summary>
        void HandleOutlineStyle(int direction)
        {
            _bannerOutline += direction;
            if (_bannerOutline < 0) _bannerOutline = 4;
            else if (_bannerOutline >= 5) _bannerOutline = 0;
            _outlineButton.sprite = Kit.Region("guilds/elements", 0, 124 + (_bannerOutline + 1) * 22, 22, 22);
            UpdateBanner();
        }

        /// <summary>guilds.ts keyDown: Enter sends the guild chat when the chat tab is open.</summary>
        public void KeyDown(bool enter)
        {
            if (!enter || _currentSidebar != "sidebar-chat" || _chatInput.text.Length == 0) return;
            SendChat();
        }

        void SendChat()
        {
            if (_chatInput.text.Length == 0) return;
            Send?.Invoke(new (string, object)[] { ("opcode", (double)(int)Opcodes.Guild.Chat), ("message", _chatInput.text) });
            _chatInput.text = "";
        }

        /// <summary>guilds.ts requestList: only without a guild and while visible.</summary>
        void RequestList()
        {
            if (_player.Guild != null || !IsVisible) return;
            Send?.Invoke(new (string, object)[] { ("opcode", (double)(int)Opcodes.Guild.List), ("from", (double)_from), ("to", (double)_to) });
        }

        /// <summary>guilds.ts loadList.</summary>
        void LoadList(List<object> guilds, double total)
        {
            foreach (var row in _guildRows) UnityEngine.Object.Destroy(row);
            _guildRows.Clear();
            _listInfo.text = total == 0 ? Localization.Get("guilds.none") : "";

            foreach (var guild in (guilds ?? new List<object>()).OfType<Dictionary<string, object>>())
            {
                var name = guild.TryGetValue("name", out var n) ? n as string ?? "undefined" : "undefined";
                var count = guild.TryGetValue("members", out var m) && m is double c ? c : 0;
                CreateGuild(name, count);
            }
        }

        RectTransform SlotElement(RectTransform list, int index, out Image icon, out Text name, Action onClick)
        {
            Sprite Frame(int f) => Kit.Region("guilds/elements", 128, 80 + f * 23, 125, 22);
            var row = UiKit.Box(list, "slot-element").gameObject.AddComponent<Image>();
            row.sprite = Frame(0);
            var width = Mathf.Min(SlotWidth, ((RectTransform)list.parent).rect.width > 0 ? ((RectTransform)list.parent).rect.width : SlotWidth);
            UiKit.Place(row.rectTransform, 0, index * (SlotHeight + 0.5f * UiKit.Em), width, SlotHeight);
            var button = row.gameObject.AddComponent<Button>();
            button.transition = Selectable.Transition.SpriteSwap;
            button.spriteState = new SpriteState { highlightedSprite = Frame(1), pressedSprite = Frame(2), selectedSprite = Frame(0) };
            if (onClick != null) button.onClick.AddListener(() => onClick());
            else button.interactable = false;

            icon = UiKit.Box(row.transform, "slot-image").gameObject.AddComponent<Image>();
            icon.raycastTarget = false;
            UiKit.Place(icon.rectTransform, 0.875f * UiKit.Em + 2, (SlotHeight - 24) / 2, 28, 24);
            icon.enabled = false;

            name = Kit.Label(row.transform, "name", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleLeft, true);
            UiKit.Place(name.rectTransform, 0.875f * UiKit.Em + 2 * UiKit.Em + 1.5f * UiKit.Em, 0, width / 2, SlotHeight);
            list.sizeDelta = new Vector2(0, (index + 1) * (SlotHeight + 0.5f * UiKit.Em));
            return row.rectTransform;
        }

        void CreateGuild(string name, double count)
        {
            var row = SlotElement(_guildList, _guildRows.Count, out _, out var label, () =>
                Send?.Invoke(new (string, object)[] { ("opcode", (double)(int)Opcodes.Guild.Join), ("identifier", name.ToLowerInvariant()) }));
            // The original appends the name (and count) only when the guild has members.
            if (count > 0)
            {
                label.text = name;
                var countLabel = Kit.Label(row, "count", $"{JsValue.NumberToString(count)}/{Constants.MAX_GUILD_MEMBERS}", Kit.Body, UiKit.Em, "#eee",
                    TextAnchor.MiddleRight, true);
                UiKit.Place(countLabel.rectTransform, row.sizeDelta.x / 2, 0, row.sizeDelta.x / 2 - 2 * UiKit.Em, SlotHeight);
            }
            _guildRows.Add(row.gameObject);
        }

        Sprite RankIcon(double? rank)
        {
            var index = rank is double r ? Array.IndexOf(RankIcons, (Modules.GuildRank)(int)r) : -1;
            return index < 0 ? null : Kit.Region("guilds/elements", 144 + index * 16, 48, 14, 12);
        }

        /// <summary>guilds.ts createElement for a member: rank icon, name (14 characters), world.</summary>
        void CreateMember(double? rank, string username)
        {
            username ??= "undefined";
            var isPlayer = Username == username;
            var row = SlotElement(_memberList, _members.Count, out var icon, out var name, null);
            icon.sprite = RankIcon(rank);
            icon.enabled = icon.sprite != null;
            name.text = FriendsView.FormatName(username.Length > 14 ? username.Substring(0, 14) + "..." : username);

            var server = Kit.Label(row, "server", "", Kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleRight, true);
            UiKit.Place(server.rectTransform, row.sizeDelta.x / 2, 0, row.sizeDelta.x / 2 - 2 * UiKit.Em, SlotHeight);
            server.text = isPlayer ? Localization.Get("guilds.world", ("id", JsValue.NumberToString(_player.ServerId))) : Localization.Get("friends.offline");
            server.color = UiKit.Css(isPlayer ? "#00a800" : "#e00a0a");

            var playerMember = _player.GetGuildMember(Username);
            var otherMember = _player.GetGuildMember(username);
            var lowerRank = (otherMember?.Rank ?? 0) > (playerMember?.Rank ?? 0);
            if (!isPlayer && !lowerRank)
            {
                var button = row.GetComponent<Button>();
                button.interactable = true;
                button.onClick.AddListener(() =>
                {
                    _selectedMember = username;
                    _memberName.text = FriendsView.FormatName(username.Length > 14 ? username.Substring(0, 14) + "..." : username);
                    _memberListGroup.alpha = 0.25f;
                    _memberDialog.gameObject.SetActive(true);
                });
            }

            _members.Add((username, row, server, icon));
        }

        void LayoutMembers()
        {
            for (var i = 0; i < _members.Count; i++) _members[i].row.anchoredPosition = new Vector2(0, -i * (SlotHeight + 0.5f * UiKit.Em));
            _memberList.sizeDelta = new Vector2(0, _members.Count * (SlotHeight + 0.5f * UiKit.Em));
        }

        /// <summary>guilds.ts loadMembers: world text and colour by the member's server.</summary>
        void LoadMembers(List<Player.GuildMember> members)
        {
            foreach (var member in members)
            {
                var row = _members.FirstOrDefault(m => m.name == member.Username);
                if (row.server == null) continue;
                var offline = member.ServerId == -1;
                row.server.color = UiKit.Css(offline ? "#e00a0a" : member.ServerId == _serverId() ? "#00a800" : "#e0e000");
                row.server.text = offline ? Localization.Get("friends.offline")
                    : Localization.Get("guilds.world", ("id", member.ServerId is double s ? JsValue.NumberToString(s) : "undefined"));
            }
        }

        void SetRank(string username, double? rank)
        {
            var row = _members.FirstOrDefault(m => m.name == username);
            if (row.icon == null) return;
            row.icon.sprite = RankIcon(rank);
            row.icon.enabled = row.icon.sprite != null;
        }

        public override void Show()
        {
            base.Show();
            RequestList();
            _nextListRequest = Time.unscaledTime + 12;
        }

        public override void Hide()
        {
            base.Hide();
            _from = 0;
            _to = 10;
            if (_player.Guild == null) HandleBackButton();
        }

        public override void Update(float deltaTime)
        {
            base.Update(deltaTime);
            // setInterval(() => this.requestList(), 12_000)
            if (Time.unscaledTime >= _nextListRequest)
            {
                _nextListRequest = Time.unscaledTime + 12;
                RequestList();
            }
        }

        /// <summary>guilds.ts updateBanner: the banner colour, the outline style and its colour.</summary>
        void UpdateBanner()
        {
            var colour = Array.IndexOf(BannerOrder, _bannerColour);
            _banner.sprite = colour < 0 ? null : Kit.Region("guilds/banners", colour * 50, 0, 50, 128);
            _banner.enabled = _banner.sprite != null;
            var outlineColour = Array.IndexOf(OutlineOrder, _bannerOutlineColour);
            _outline.sprite = outlineColour < 0 || _bannerOutline < 0 || _bannerOutline > 4
                ? null : Kit.Region($"guilds/outline{_bannerOutline + 1}", outlineColour * 50, 0, 50, 128);
            _outline.enabled = _outline.sprite != null;
        }

        /// <summary>guilds.ts setError: a server message (an i18n key) or empty.</summary>
        void SetError(string text = "") =>
            SetErrorText(MessageFormat.ParseMessage(MessageFormat.FormatNotification(text ?? "undefined")));

        void SetErrorText(string html) => _createError.text = MessageFormat.ToRichText(html);

        /// <summary>For tests.</summary>
        public bool ListShown => _listContainer.gameObject.activeSelf;
        public bool CreateShown => _create.parent.gameObject.activeSelf;
        public bool InfoShown => _info.gameObject.activeSelf;
        public string GuildName => _guildName.text;
        public IReadOnlyList<string> Members => _members.Select(m => m.name).ToList();
        public string Error => _createError.text;
        public void OpenCreate() => HandleCreate();
        public void TypeName(string name) => _nameInput.text = name;
        public void ConfirmCreate() => HandleCreateConfirm();
        public void ChooseColour(string colour)
        {
            _bannerColour = colour;
            UpdateBanner();
        }
        public void NextOutline() => HandleOutlineStyle(1);
        public void Leave() => Send?.Invoke(new[] { ("opcode", (object)(double)(int)Opcodes.Guild.Leave) });
        public void OpenChat() => HandleSidebar("sidebar-chat");
        public void TypeChat(string text) => _chatInput.text = text;
        public void SendChatMessage() => SendChat();
        public int ChatLines => _chatLog.childCount;
    }
}
