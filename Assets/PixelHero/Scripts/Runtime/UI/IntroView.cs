using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PixelHero.Network;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// The title screen (client/components/intro.astro, scss/app, app.ts): the tiled background, the parchment with
    /// its scrolls (login, new character, credits, about), the footer, the language selector and the version.
    /// Layout numbers are the original CSS at 1em = 16px.
    ///
    /// With a hub configured it also has the world select (app.ts loadWorlds, the #world-select-button), the password
    /// reset request (#reset-password, the footer's "Reset Password") and the reset page itself (pages/reset.astro,
    /// reset.ts), which the original serves as its own page and the port shows as a scroll when the client was opened
    /// with a reset link's token and id.
    ///
    /// Differences: no Discord, Patreon or privacy links; without a hub no world select or password reset either;
    /// the footer has a link to the original and the language selector switches between the port's
    /// languages; Remember Me keeps the username only (the original also stores the password in plain text).
    /// </summary>
    public sealed class IntroView
    {
        public const string OriginalUrl = "https://github.com/PixelHero/PixelHero";

        const float ArticleWidth = 24 * UiKit.Em, ArticleHeight = 16.5f * UiKit.Em, ArticlePadding = 0.25f * UiKit.Em;
        const float ContentWidth = ArticleWidth - 2 * ArticlePadding;
        const float InputFont = 0.9f * UiKit.Em;
        const string RememberKey = "pixelhero.remember", UsernameKey = "pixelhero.username";

        static readonly (string id, string url)[] CreditLinks =
        {
            ("littleWorkshop", "https://www.littleworkshop.fr/"), ("buch", "https://opengameart.org/users/buch"),
            ("shikashi", "https://shikashiassets.itch.io/shikashis-fantasy-icons-pack"),
            ("kyrise", "https://opengameart.org/content/kyrises-free-16x16-rpg-icon-pack"), ("gameIcons", "https://game-icons.net"),
            ("terrel", "https://soundcloud.com/gyrowolf"), ("joel", "https://blog.dayjo.org"), ("dan", "https://www.dantilden.com"),
            ("aaron", "https://soundcloud.com/aaron-anderson-11"), ("pixelhero", OriginalUrl), ("github", OriginalUrl)
        };

        static readonly Dictionary<string, string> LinkNames = new Dictionary<string, string>
        {
            ["littleWorkshop"] = "Little Workshop", ["buch"] = "Buch", ["shikashi"] = "Shikashi", ["kyrise"] = "Kyrise",
            ["gameIcons"] = "Game-Icons.net", ["terrel"] = "Terrel O'Brien", ["joel"] = "Joel Day", ["dan"] = "Dan Tilden",
            ["aaron"] = "Aaron Krogh", ["pixelhero"] = "PixelHero"
        };

        static readonly Regex Email = new Regex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$");

        /// <summary>util.ts isEmail (the password reset request checks the address with it).</summary>
        static readonly Regex UtilEmail = new Regex(
            @"^(([^\s""(),.:;<>@[\\\]]+(\.[^\s""(),.:;<>@[\\\]]+)*)|("".+""))@((\[(?:[0-9]{1,3}\.){3}[0-9]{1,3}])|(([0-9A-Za-z-]+\.)+[A-Za-z]{2,}))$",
            RegexOptions.CultureInvariant);

        // The hub (globalConfig.hub); null or empty when there is none.
        readonly string _hub;
        // reset.ts: the token and id from the reset link.
        readonly string _resetToken, _resetId;
        InputField _emailReset, _resetPasswordField, _resetPasswordConfirm;
        RectTransform _worldsList, _worldSelectButton;
        float _worldsListWidth;
        Text _worldButtonName, _worldButtonPlayers;
        readonly List<(Image row, Text name, Text players, int index)> _worldRows = new List<(Image, Text, Text, int)>();
        float _redirectAt = -1;

        /// <summary>app.ts selectedServer: the world picked in the world select (the hub's SerializedServer), or null.</summary>
        public object SelectedServer { get; private set; }

        readonly UiKit _kit;
        readonly RectTransform _root;
        readonly CanvasGroup _group;
        readonly RectTransform _parchment;
        readonly Dictionary<string, CanvasGroup> _scrolls = new Dictionary<string, CanvasGroup>();
        readonly List<Text> _validations = new List<Text>();
        readonly List<Action> _relabel = new List<Action>();
        RawImage _background;
        // Shown on the title screen only (body.intro); in game the parchment comes back alone for the death scroll.
        readonly List<GameObject> _introOnly = new List<GameObject>();
        Button _closeArea;

        InputField _loginName, _loginPassword, _registerName, _registerPassword, _registerConfirm, _registerEmail;
        Toggle _remember, _guest;
        Button _loginButton, _newAccountButton, _playButton;
        Text _languageLabel;

        string _current = "load-character";
        bool _loggingIn, _hidden;
        float _fade = 1, _fadeTarget = 1;
        string _loaderMessage;

        /// <summary>The player asked to log in, register or play as a guest.</summary>
        public event Action<LoginRequest> LoginRequested;

        /// <summary>The RESPAWN button (app.ts respawnCallback → game.ts respawn).</summary>
        public event Action RespawnRequested;

        public bool Visible => !_hidden;

        public IntroView(UiKit kit, RectTransform parent, string hub = null, string resetToken = null, string resetId = null)
        {
            _kit = kit;
            _hub = string.IsNullOrEmpty(hub) ? null : hub;
            _resetToken = resetToken;
            _resetId = resetId;
            _root = UiKit.Fill(UiKit.Box(parent, "#intro"));
            _group = _root.gameObject.AddComponent<CanvasGroup>();

            BuildBackground();

            _parchment = _kit.Sliced(_root, "#parchment", "menu-fancy", 4 * UiKit.Em).rectTransform;
            UiKit.Centre(_parchment, 38 * UiKit.Em, 19 * UiKit.Em, 0, -3 * UiKit.Em);

            // Clicking the parchment closes the credits and about scrolls.
            var close = UiKit.Fill(UiKit.Box(_parchment, "close area")).gameObject.AddComponent<Image>();
            close.color = Color.clear;
            _closeArea = close.gameObject.AddComponent<Button>();
            _closeArea.transition = Selectable.Transition.None;
            _closeArea.onClick.AddListener(() => OpenScroll("load-character"));

            BuildLogin();
            BuildRegister();
            if (_hub != null)
            {
                BuildResetPassword();
                BuildWorldSelect();
            }
            //BuildCredits();
            BuildAbout();
            if (_hub != null && (_resetToken != null || _resetId != null)) BuildResetPage();
            BuildFooter();
            BuildLanguageSelect();
            BuildDeath();

            Localization.LanguageChanged += Relabel;
            Relabel();
            ChangeScroll("load-character", false);

            if (PlayerPrefs.GetInt(RememberKey, 0) == 1)
            {
                _remember.isOn = true;
                _loginName.text = PlayerPrefs.GetString(UsernameKey, "");
            }

            if (_hub == null) return;

            // app.ts ready: loadWorlds.
            LoadWorlds();

            // pages/reset.astro: the reset link opens the reset page (reset.ts constructor).
            if (_resetToken != null || _resetId != null)
            {
                ChangeScroll("reset", false);
                // If we don't have tokens we don't do anything.
                if (_resetToken == null || _resetId == null) SetValidation("validation-error", "reset.invalidLink");
            }
        }

        // ---------------------------------------------------------------- building

        void BuildBackground()
        {
            // body: url('/img/tile.png') center, under linear-gradient(to bottom, transparent, transparent, 60% black).
            _background = UiKit.Fill(UiKit.Box(_root, "background")).gameObject.AddComponent<RawImage>();
            var tile = _kit.Image("tile");
            tile.wrapMode = TextureWrapMode.Repeat;
            _background.texture = tile;
            _introOnly.Add(_background.gameObject);

            var gradient = UiKit.Fill(UiKit.Box(_root, "gradient")).gameObject.AddComponent<RawImage>();
            var texture = new Texture2D(1, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (var y = 0; y < 64; y++)
            {
                // Row 0 is the bottom of the screen.
                var t = 1 - (y + 0.5f) / 64;
                var alpha = t <= 0.5f ? 0 : (t - 0.5f) / 0.5f * 0.6f;
                texture.SetPixel(0, y, new Color(0, 0, 0, alpha));
            }
            texture.Apply();
            gradient.texture = texture;
            gradient.raycastTarget = false;
            _introOnly.Add(gradient.gameObject);
        }

        /// <summary>One scroll of the parchment: 24em × 16.5em, centred, padding 0 0.25em.</summary>
        RectTransform Scroll(string id)
        {
            var scroll = UiKit.Box(_parchment, "#" + id);
            UiKit.Centre(scroll, ArticleWidth, ArticleHeight);
            _scrolls[id] = scroll.gameObject.AddComponent<CanvasGroup>();

            var content = UiKit.Box(scroll, "content");
            UiKit.Place(content, ArticlePadding, 0, ContentWidth, ArticleHeight);
            return content;
        }

        /// <summary>h1.title-decoration: Fibberish 1.75em, then the frame ornament (78×6 at 286,8 of guilds/frames, 2×).</summary>
        float Title(RectTransform parent, string key)
        {
            var title = _kit.Label(parent, "h1", "", _kit.Heading, 1.75f * UiKit.Em, "#eee", TextAnchor.UpperCenter);
            UiKit.Place(title.rectTransform, 0, 0, ContentWidth, 1.75f * UiKit.Em);
            _relabel.Add(() => title.text = Localization.Get(key));

            var ornament = UiKit.Box(parent, "title-decoration").gameObject.AddComponent<Image>();
            ornament.sprite = _kit.Region("guilds/frames", 286, 8, 78, 6);
            ornament.raycastTarget = false;
            UiKit.Place(ornament.rectTransform, (ContentWidth - 156) / 2, 1.75f * UiKit.Em, 156, 12);
            return 1.75f * UiKit.Em + 12;
        }

        InputField Input(RectTransform parent, string name, string key, bool password, int maxLength, float top)
        {
            var field = _kit.Input(parent, name, "", InputFont, password, maxLength);
            UiKit.Place((RectTransform)field.transform, 0, top, ContentWidth, InputHeight);
            _relabel.Add(() => ((Text)field.placeholder).text = Localization.Get(key));
            field.onValueChanged.AddListener(_ => ClearFieldError(field));
            return field;
        }

        // font 0.9em with padding 0.625em above and below (line-height 1).
        static float InputHeight => InputFont * (1 + 2 * 0.625f);

        Text Validation(RectTransform parent, float top)
        {
            var text = _kit.Label(parent, "validation-summary", "", _kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleCenter);
            UiKit.Place(text.rectTransform, 0, top, ContentWidth, 2 * UiKit.Em);
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            _validations.Add(text);
            return text;
        }

        Button ButtonAt(RectTransform parent, string name, string key, float left, float top, Action onClick)
        {
            var button = _kit.Button(parent, name, "", onClick);
            UiKit.Place((RectTransform)button.transform, left, top, 8 * UiKit.Em, 2 * UiKit.Em);
            var label = button.GetComponentInChildren<Text>();
            _relabel.Add(() => label.text = Localization.Get(key));
            return button;
        }

        /// <summary>
        /// Positions of a flex column with justify-content: space-between: the free height is shared by the gaps.
        /// </summary>
        static float[] SpaceBetween(float top, float height, params float[] items)
        {
            var used = 0f;
            foreach (var item in items) used += item;
            var gap = items.Length > 1 ? (height - used) / (items.Length - 1) : 0;
            var positions = new float[items.Length];
            var y = top;
            for (var i = 0; i < items.Length; i++)
            {
                positions[i] = y;
                y += items[i] + gap;
            }
            return positions;
        }

        void BuildLogin()
        {
            var content = Scroll("load-character");
            var formTop = Title(content, "intro.title");
            var y = SpaceBetween(formTop, ArticleHeight - formTop, InputHeight, InputHeight, 2 * UiKit.Em, UiKit.Em, 2 * UiKit.Em);

            _loginName = Input(content, "login-name-input", "intro.username", false, 32, y[0]);
            _loginPassword = Input(content, "login-password-input", "intro.password", true, 64, y[1]);
            Validation(content, y[2]);

            _remember = _kit.Check(content, "remember-me", "", out var rememberLabel);
            UiKit.Place((RectTransform)_remember.transform, 0, y[3], ContentWidth / 2, UiKit.Em);
            _relabel.Add(() => rememberLabel.text = Localization.Get("intro.rememberMe"));

            _guest = _kit.Check(content, "guest", "", out var guestLabel);
            _relabel.Add(() =>
            {
                guestLabel.text = Localization.Get("intro.guest");
                // justify-content: space-between puts the second checkbox against the right edge.
                var width = UiKit.Em * 1.5f + UiKit.Width(guestLabel);
                UiKit.Place((RectTransform)_guest.transform, ContentWidth - width, y[3], width, UiKit.Em);
            });

            _loginButton = ButtonAt(content, "login", "intro.login", 0, y[4], Login);
            _newAccountButton = ButtonAt(content, "new-account", "intro.newAccount", ContentWidth - 8 * UiKit.Em, y[4],
                () => OpenScroll("create-character"));
        }

        void BuildRegister()
        {
            var content = Scroll("create-character");
            var formTop = Title(content, "intro.newCharacter");
            var y = SpaceBetween(formTop, ArticleHeight - formTop, InputHeight, InputHeight, InputHeight, InputHeight, 2 * UiKit.Em,
                2 * UiKit.Em);

            _registerName = Input(content, "register-name-input", "intro.username", false, 32, y[0]);
            _registerPassword = Input(content, "register-password-input", "intro.password", true, 64, y[1]);
            _registerConfirm = Input(content, "register-password-confirmation-input", "intro.confirmPassword", true, 64, y[2]);
            _registerEmail = Input(content, "register-email-input", "intro.email", false, 64, y[3]);
            Validation(content, y[4]);

            ButtonAt(content, "cancel-register", "intro.cancel", 0, y[5], () => OpenScroll("load-character"));
            _playButton = ButtonAt(content, "play", "intro.play", ContentWidth - 8 * UiKit.Em, y[5], Login);
        }

        /// <summary>
        /// An h1 with the empty .left-ornament / .right-ornament spans (no images): Fibberish 1.75em, centred,
        /// returns its height.
        /// </summary>
        float PlainTitle(RectTransform parent, string key)
        {
            var title = _kit.Label(parent, "h1", "", _kit.Heading, 1.75f * UiKit.Em, "#eee", TextAnchor.UpperCenter);
            UiKit.Place(title.rectTransform, 0, 0, ContentWidth, 1.75f * UiKit.Em);
            _relabel.Add(() => title.text = Localization.Get(key));
            return 1.75f * UiKit.Em;
        }

        /// <summary>#reset-password: the email input, the validation summary, CANCEL and CONTINUE.</summary>
        void BuildResetPassword()
        {
            var content = Scroll("reset-password");
            var formTop = PlainTitle(content, "intro.resetPassword");
            var y = SpaceBetween(formTop, ArticleHeight - formTop, InputHeight, 2 * UiKit.Em, 2 * UiKit.Em);

            _emailReset = Input(content, "email-reset-input", "intro.email", false, 32, y[0]);
            // Enter in the input submits the form, whose submit button is CONTINUE (app.ts handleKeyDown itself
            // ignores keys on this scroll).
            _emailReset.onEndEdit.AddListener(_ =>
            {
                var keyboard = Keyboard.current;
                if (keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)) ForgotPassword();
            });
            Validation(content, y[1]);

            ButtonAt(content, "cancel-forget", "intro.cancel", 0, y[2], () => OpenScroll("load-character"));
            ButtonAt(content, "continue-forget", "intro.continue", ContentWidth - 8 * UiKit.Em, y[2], ForgotPassword);
        }

        /// <summary>#world-select: ol#worlds (scrolls), then CANCEL and CONTINUE (both go back to the login).</summary>
        void BuildWorldSelect()
        {
            var content = Scroll("world-select");
            var formTop = PlainTitle(content, "intro.worldSelect");
            var buttonsTop = ArticleHeight - 2 * UiKit.Em;

            var list = UiKit.Box(content, "#worlds");
            UiKit.Place(list, 0, formTop, ContentWidth, buttonsTop - formTop);
            _worldsList = UiKit.ScrollView(list, "ol");
            _worldsListWidth = ContentWidth;

            ButtonAt(content, "cancel-worlds", "intro.cancel", 0, buttonsTop, () => OpenScroll("load-character"));
            ButtonAt(content, "continue-worlds", "intro.continue", ContentWidth - 8 * UiKit.Em, buttonsTop, () => OpenScroll("load-character"));

            // #world-select-button (hidden until the hub lists two worlds or more): a .slice-button at the bottom left
            // (margin 1em), the world's name (strong) over its player count, both stroked.
            var button = _kit.Button(_root, "#world-select-button", "", () => OpenScroll("world-select"));
            _worldSelectButton = (RectTransform)button.transform;
            _worldSelectButton.anchorMin = _worldSelectButton.anchorMax = _worldSelectButton.pivot = new Vector2(0, 0);
            _worldSelectButton.anchoredPosition = new Vector2(UiKit.Em, UiKit.Em);
            UnityEngine.Object.Destroy(button.GetComponentInChildren<Text>().gameObject);
            _worldButtonName = _kit.Label(_worldSelectButton, "strong", "", _kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            _worldButtonPlayers = _kit.Label(_worldSelectButton, "span", "", _kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter, true);
            _worldSelectButton.gameObject.SetActive(false);
            _introOnly.Add(_worldSelectButton.gameObject);
            _relabel.Add(() =>
            {
                if (SelectedServer != null) SelectServer(SelectedServer);
                foreach (var (_, _, players, index) in _worldRows) players.text = PlayersText(_servers[index], false);
            });
        }

        /// <summary>
        /// pages/reset.astro (#reset-password on its own page): the new password twice, the validation summary and
        /// ACCEPT, centred.
        /// </summary>
        void BuildResetPage()
        {
            var content = Scroll("reset");
            var formTop = Title(content, "intro.resetPassword");
            var y = SpaceBetween(formTop, ArticleHeight - formTop, InputHeight, InputHeight, 2 * UiKit.Em, 2 * UiKit.Em);

            _resetPasswordField = Input(content, "password-input", "intro.password", true, 64, y[0]);
            _resetPasswordConfirm = Input(content, "password-confirm-input", "intro.confirmPassword", true, 64, y[1]);
            Validation(content, y[2]);

            // If we don't have tokens the button does nothing.
            ButtonAt(content, "confirm-reset", "intro.accept", (ContentWidth - 8 * UiKit.Em) / 2, y[3], () =>
            {
                if (_resetToken != null && _resetId != null) Reset();
            });
        }

        void BuildCredits()
        {
            var content = Scroll("credits");
            var top = Title(content, "intro.credits");

            // <p> with <br /> before and between the lines (line-height 1), then the close hint.
            var paragraph = UiKit.Box(content, "p");
            UiKit.Place(paragraph, 0, top, ContentWidth, ArticleHeight - top);
            var hint = _kit.Label(content, "close-about", "", _kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter);
            _relabel.Add(() =>
            {
                var height = Paragraph(paragraph, new[] { "credits.thanks", "credits.asky", "credits.pixel", "credits.music", "credits.port" });
                hint.text = Localization.Get("intro.closeHint");
                UiKit.Place(hint.rectTransform, 0, top + height, ContentWidth, UiKit.Em);
            });
        }

        void BuildAbout()
        {
            var content = Scroll("about");
            var top = Title(content, "intro.about");
            var paragraph = UiKit.Box(content, "p");
            UiKit.Place(paragraph, 0, top, ContentWidth, ArticleHeight - top);
            var hint = _kit.Label(content, "close-about", "", _kit.Body, UiKit.Em, "#eee", TextAnchor.UpperCenter);
            _relabel.Add(() =>
            {
                var height = Paragraph(paragraph, new[] { "about.text" }, false);
                hint.text = Localization.Get("intro.closeHint");
                UiKit.Place(hint.rectTransform, 0, top + height, ContentWidth, UiKit.Em);
            });
        }

        /// <summary>
        /// Lays out centred, wrapped lines of text with links (the {{placeholders}} of each line), one empty line
        /// before each entry when <paramref name="spaced"/> (the original's &lt;br /&gt;s). Returns the height.
        /// </summary>
        float Paragraph(RectTransform parent, string[] keys, bool spaced = true)
        {
            for (var i = parent.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(parent.GetChild(i).gameObject);

            var y = 0f;
            var line = new List<(Text text, float width)>();

            void Flush()
            {
                var total = 0f;
                foreach (var (_, width) in line) total += width;
                var x = (ContentWidth - total) / 2;
                foreach (var (text, width) in line)
                {
                    UiKit.Place(text.rectTransform, x, y, width, UiKit.Em);
                    x += width;
                }
                line.Clear();
                y += UiKit.Em;
            }

            for (var k = 0; k < keys.Length; k++)
            {
                var key = keys[k];
                // The original's <br />s: an empty line before each of its four lines (not before the port's own line).
                if (spaced && k < 4) y += UiKit.Em;
                var lineWidth = 0f;

                foreach (var (literal, placeholder) in Localization.Split(Localization.Get(key)))
                {
                    var words = new List<(string word, string url)>();
                    if (literal != null)
                        foreach (Match match in Regex.Matches(literal, @"\S+\s*|\s+")) words.Add((match.Value, null));
                    else
                    {
                        var url = Array.Find(CreditLinks, l => l.id == placeholder).url;
                        var name = LinkNames.TryGetValue(placeholder, out var n) ? n : Localization.Get(key.Split('.')[0] + "." + placeholder);
                        words.Add((name, url));
                    }

                    foreach (var (word, url) in words)
                    {
                        var text = url == null
                            ? _kit.Label(parent, "text", word, _kit.Body, UiKit.Em, "#eee")
                            : _kit.Link(parent, "a", word, UiKit.Em, "#0000ee", "#551a8b", () => Application.OpenURL(url));
                        var width = UiKit.Width(text);
                        if (lineWidth > 0 && lineWidth + width - TrailingSpace(text, word) > ContentWidth)
                        {
                            Flush();
                            lineWidth = 0;
                        }
                        line.Add((text, width));
                        lineWidth += width;
                    }
                }
                Flush();
            }

            return y;
        }

        static float TrailingSpace(Text text, string word)
        {
            var trimmed = word.TrimEnd();
            if (trimmed.Length == word.Length) return 0;
            var full = UiKit.Width(text);
            var original = text.text;
            text.text = trimmed;
            var width = UiKit.Width(text);
            text.text = original;
            return full - width;
        }

        void BuildFooter()
        {
            // footer: bottom 1em, centred, #c6c0a3 with a 0 1px 0 black shadow; items padded 0 0.3125em, "–" between.
            var footer = UiKit.Box(_root, "footer");
            _introOnly.Add(footer.gameObject);
            footer.anchorMin = new Vector2(0, 0);
            footer.anchorMax = new Vector2(1, 0);
            footer.pivot = new Vector2(0.5f, 0);
            footer.anchoredPosition = new Vector2(0, UiKit.Em);
            footer.sizeDelta = new Vector2(0, UiKit.Em);

            var row = UiKit.Box(footer, "row");
            row.anchorMin = row.anchorMax = row.pivot = new Vector2(0.5f, 1);

            var items = new List<Text>
            {
                FooterLink(row, "about-link", "intro.about", () => OpenScroll("about")),
                //FooterText(row, "–"),
                //FooterLink(row, "credits-link", "intro.credits", () => OpenScroll("credits")),
                //FooterText(row, "–"),
                //FooterLink(row, "pixelhero-link", "intro.source", () => Application.OpenURL(OriginalUrl))
            };
            // #reset (#toggle-reset-password) needs the hub, which sends the reset mail.
            if (_hub != null)
            {
                items.Add(FooterText(row, "–"));
                items.Add(FooterLink(row, "reset", "intro.resetPassword", () => OpenScroll("reset-password")));
            }

            _relabel.Add(() =>
            {
                var padding = 0.3125f * UiKit.Em;
                var x = 0f;
                foreach (var item in items)
                {
                    var link = item.GetComponent<HoverLink>() != null;
                    var width = UiKit.Width(item);
                    if (link) x += padding;
                    UiKit.Place(item.rectTransform, x, 0, width, UiKit.Em);
                    x += width + (link ? padding : 0);
                }
                row.sizeDelta = new Vector2(x, UiKit.Em);
                row.anchoredPosition = Vector2.zero;
            });

            // #game-version: bottom-right, padding 1em, #eee.
            var version = _kit.Label(_root, "#game-version", GameVersion.Gver, _kit.Body, UiKit.Em, "#eee", TextAnchor.LowerRight);
            _introOnly.Add(version.gameObject);
            var versionRect = version.rectTransform;
            versionRect.anchorMin = versionRect.anchorMax = versionRect.pivot = new Vector2(1, 0);
            versionRect.anchoredPosition = new Vector2(-UiKit.Em, UiKit.Em);
            versionRect.sizeDelta = new Vector2(0, UiKit.Em);
        }

        /// <summary>The language select, top right; built on its own because the footer is off.</summary>
        void BuildLanguageSelect()
        {
            // #lang-select: top-right 1em; select styling (#c6c0a3 on black, padding 0.25em, radius 0.25em).
            var select = _kit.Sliced(_root, "#lang-select", "menu", 0.25f * UiKit.Em);
            _introOnly.Add(select.gameObject);
            select.sprite = null;
            select.type = Image.Type.Simple;
            select.color = Color.black;
            var selectRect = select.rectTransform;
            selectRect.anchorMin = selectRect.anchorMax = selectRect.pivot = new Vector2(1, 1);
            selectRect.anchoredPosition = new Vector2(-UiKit.Em, -UiKit.Em);
            _languageLabel = _kit.Link(selectRect, "language", "", UiKit.Em, "#c6c0a3", "#fcda5c", NextLanguage);
            _languageLabel.alignment = TextAnchor.MiddleCenter;
            UiKit.Fill(_languageLabel.rectTransform);
            _relabel.Add(() =>
            {
                _languageLabel.text = Localization.Get("language.name");
                selectRect.sizeDelta = new Vector2(UiKit.Width(_languageLabel) + 0.5f * UiKit.Em, 1.5f * UiKit.Em);
            });
        }

        /// <summary>
        /// #death: h1 with margin-top 20% (of the scroll's content width), then #respawn: a full-width .slice-button
        /// 1em below, font-size 1.5em (padding 0.5em 1em).
        /// </summary>
        void BuildDeath()
        {
            var content = Scroll("death");
            var top = 0.2f * ContentWidth;
            var title = _kit.Label(content, "h1", "", _kit.Heading, 1.75f * UiKit.Em, "#eee", TextAnchor.UpperCenter);
            UiKit.Place(title.rectTransform, 0, top, ContentWidth, 1.75f * UiKit.Em);
            _relabel.Add(() => title.text = Localization.Get("death.title"));

            var respawn = _kit.Button(content, "#respawn", "", () => RespawnRequested?.Invoke());
            UiKit.Place((RectTransform)respawn.transform, 0, top + 1.75f * UiKit.Em + UiKit.Em, ContentWidth, 3 * UiKit.Em);
            var label = (PixelText)respawn.GetComponentInChildren<Text>();
            label.CssSize = 1.5f * UiKit.Em;
            _relabel.Add(() => label.text = Localization.Get("death.respawn"));
        }

        /// <summary>connection.ts handleDeath, after the death animation: body.classList.add('death').</summary>
        public void ShowDeath()
        {
            foreach (var item in _introOnly) item.SetActive(false);
            _hidden = false;
            _fadeTarget = 1;
            ChangeScroll("death", true);
        }

        /// <summary>game.ts respawn: body.classList.remove('death').</summary>
        public void HideDeath()
        {
            if (_current != "death") return;
            _hidden = true;
            _fadeTarget = 0;
        }

        Text FooterLink(RectTransform row, string name, string key, Action onClick)
        {
            var link = _kit.Link(row, name, "", UiKit.Em, "#c6c0a3", "#fcda5c", onClick);
            Shadow(link);
            _relabel.Add(() => link.text = Localization.Get(key));
            return link;
        }

        Text FooterText(RectTransform row, string text)
        {
            var label = _kit.Label(row, "separator", text, _kit.Body, UiKit.Em, "#c6c0a3");
            Shadow(label);
            return label;
        }

        static void Shadow(Text text)
        {
            var shadow = text.gameObject.AddComponent<PixelStroke>();
            shadow.Colour = Color.black;
            shadow.Offsets = new[] { new Vector2(0, -1) };
        }

        void NextLanguage()
        {
            var languages = Localization.Languages;
            var index = 0;
            for (var i = 0; i < languages.Count; i++)
                if (languages[i] == Localization.Language) index = i;
            var next = languages[(index + 1) % languages.Count];
            Localization.SetLanguage(next);
            PlayerPrefs.SetString(LanguageKey, next);
        }

        public const string LanguageKey = "pixelhero.language";

        void Relabel()
        {
            foreach (var relabel in _relabel) relabel();
            if (_validationKey != null || _validationEnglish != null)
                ShowValidation(_validationKey, _validationArgs, _validationColour, _validationShadow, _validationEnglish);
        }

        // ---------------------------------------------------------------- behaviour

        /// <summary>Per frame: background tiling, scroll and menu fades, the loader dots, Enter to log in.</summary>
        public void Update(float deltaTime, Vector2 canvasSize)
        {
            // background-position: center: a tile's centre is the screen's centre.
            var tile = _background.texture;
            var w = canvasSize.x / tile.width;
            var h = canvasSize.y / tile.height;
            _background.uvRect = new Rect(0.5f - w / 2, 0.5f - h / 2, w, h);

            foreach (var pair in _scrolls)
            {
                var target = pair.Key == _current ? 1f : 0f;
                // transition: 0.5s opacity linear when shown, none when hidden.
                pair.Value.alpha = target == 0 ? 0 : Mathf.MoveTowards(pair.Value.alpha, 1, deltaTime / 0.5f);
                pair.Value.interactable = pair.Value.blocksRaycasts = target == 1;
            }
            _closeArea.gameObject.SetActive(_current == "credits" || _current == "about");

            // #intro-container: 0.4s opacity linear.
            _fade = Mathf.MoveTowards(_fade, _fadeTarget, deltaTime / 0.4f);
            _group.alpha = _fade;
            _group.blocksRaycasts = _group.interactable = _fadeTarget > 0;
            _root.gameObject.SetActive(_fade > 0 || _fadeTarget > 0);

            if (_loggingIn && _loaderMessage != null) ShowLoader();

            var keyboard = Keyboard.current;
            var enter = keyboard != null && (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame);
            if (!_hidden && enter && (_current == "load-character" || _current == "create-character" || _current == "world-select"))
                Login();

            // reset.ts: setTimeout(() => (window.location.href = '/'), 5000): back to the login.
            if (_redirectAt >= 0 && Time.unscaledTime >= _redirectAt)
            {
                _redirectAt = -1;
                OpenScroll("load-character");
            }
        }

        public void OpenScroll(string destination)
        {
            if (_loggingIn) return;
            ClearErrors();
            ChangeScroll(destination, true);
        }

        void ChangeScroll(string destination, bool animate)
        {
            _current = destination;
            if (!animate && _scrolls.TryGetValue(destination, out var group)) group.alpha = 1;
        }

        bool IsGuest => _guest.isOn;
        bool IsRegistering => _current == "create-character";

        string Username => (IsRegistering ? _registerName : _loginName).text.Trim();
        string Password => (IsRegistering ? _registerPassword : _loginPassword).text;

        void Login()
        {
            if (_loggingIn || _hidden || !VerifyForm()) return;

            ClearErrors();
            ToggleLogin(true);

            LoginRequested?.Invoke(IsGuest ? LoginRequest.Guest()
                : IsRegistering ? LoginRequest.Register(Username, Password, _registerEmail.text.Trim())
                : LoginRequest.Login(Username, Password));
        }

        bool VerifyForm()
        {
            if (IsGuest) return true;

            var nameField = IsRegistering ? _registerName : _loginName;
            var passwordField = IsRegistering ? _registerPassword : _loginPassword;

            if (Username.Length == 0) return SendError("validation.username", nameField);
            if (Password.Length == 0) return SendError("validation.password", passwordField);

            if (IsRegistering)
            {
                if (Password.Length < 3) return SendError("validation.passwordShort", passwordField);
                if (Password.Length > 64) return SendError("validation.passwordLong", passwordField);
                if (Password != _registerConfirm.text) return SendError("validation.passwordMatch", _registerConfirm);
                if (!Email.IsMatch(_registerEmail.text.Trim())) return SendError("validation.email", _registerEmail);
            }

            return true;
        }

        readonly HashSet<InputField> _errorFields = new HashSet<InputField>();

        bool SendError(string key, InputField field = null, params (string, string)[] args)
        {
            ClearErrors();
            ShowValidation(key, args, "#e00a0a", "#800");
            if (field == null) return false;

            _kit.SetSlice((Image)field.targetGraphic, "input-error", UiKit.Em / 2);
            _errorFields.Add(field);
            field.Select();
            field.ActivateInputField();
            return false;
        }

        /// <summary>An error from the connection (an English sentence, translated when the table knows it).</summary>
        public void ShowError(string englishMessage)
        {
            ToggleLogin(false);
            ClearErrors();
            ShowValidation(null, null, "#e00a0a", "#800", englishMessage);
        }

        string _validationKey;
        (string, string)[] _validationArgs;
        string _validationColour, _validationShadow, _validationEnglish;

        void ShowValidation(string key, (string, string)[] args, string colour, string shadow, string english = null)
        {
            _validationKey = key;
            _validationArgs = args;
            _validationColour = colour;
            _validationShadow = shadow;
            _validationEnglish = english;

            var message = key != null ? Localization.Get(key, args ?? Array.Empty<(string, string)>()) : Localization.TranslateEnglish(english);
            foreach (var text in _validations)
            {
                text.text = message;
                text.color = UiKit.Css(colour);
                if (!text.TryGetComponent<PixelStroke>(out var stroke)) stroke = text.gameObject.AddComponent<PixelStroke>();
                stroke.enabled = shadow != null;
                if (shadow != null)
                {
                    // text-shadow: 0.125em 0.125em 0.125em (blur drawn as a plain offset copy).
                    stroke.Colour = UiKit.Css(shadow);
                    stroke.Offsets = new[] { new Vector2(2, -2) };
                }
                text.SetVerticesDirty();
            }
        }

        void ClearFieldError(InputField field)
        {
            if (!_errorFields.Remove(field)) return;
            _kit.SetSlice((Image)field.targetGraphic, "input", UiKit.Em / 2);
            ClearErrors();
        }

        void ClearErrors()
        {
            _validationKey = null;
            _validationEnglish = null;
            foreach (var text in _validations) text.text = "";
            foreach (var field in _errorFields) _kit.SetSlice((Image)field.targetGraphic, "input", UiKit.Em / 2);
            _errorFields.Clear();
        }

        /// <summary>app.ts toggleLogin: the loader, read-only fields and disabled buttons while connecting.</summary>
        void ToggleLogin(bool toggle)
        {
            _loggingIn = toggle;
            _loaderMessage = toggle ? "intro.connecting" : null;
            foreach (var field in new[] { _loginName, _loginPassword, _registerName, _registerPassword, _registerConfirm, _registerEmail })
                field.readOnly = toggle;
            _loginButton.interactable = _newAccountButton.interactable = _playButton.interactable = !toggle;
            if (!toggle) ClearErrors();
        }

        void ShowLoader()
        {
            // "Connecting" and three dots blinking a quarter second apart (scss .loader-dot).
            var time = Time.unscaledTime;
            var dots = "";
            for (var i = 0; i < 3; i++) dots += (time - i * 0.25f) % 1f < 0.5f ? "." : " ";
            foreach (var text in _validations)
            {
                text.text = Localization.Get(_loaderMessage) + dots;
                text.color = UiKit.Css("#eee");
                if (text.TryGetComponent<PixelStroke>(out var stroke)) stroke.enabled = false;
            }
        }

        // ---------------------------------------------------------------- hub

        /// <summary>app.ts setValidation: a message of a type (status is blue, validation-error red) in every summary.</summary>
        void SetValidation(string type, string key, params (string, string)[] args)
        {
            switch (type)
            {
                case "status":
                    ShowValidation(key, args, "#03c", "#39f");
                    break;
                case "validation-warning":
                    ShowValidation(key, args, "rgb(168 162 16)", "#fc6");
                    break;
                default:
                    ShowValidation(key, args, "#e00a0a", "#800");
                    break;
            }
        }

        List<object> _servers = new List<object>();

        /// <summary>
        /// app.ts loadWorlds: the hub's /all; the first world is selected, and with two or more the world select
        /// button appears and ol#worlds lists them all.
        /// </summary>
        void LoadWorlds()
        {
            if (_hub == null) return;

            // Fetch a list of servers from the hub
            HubRequests.Get($"{_hub}/all", text =>
            {
                if (_root == null) return; // the scene was reloaded meanwhile

                if (text == null)
                {
                    SetValidation("validation-error", "validation.worldList");
                    return;
                }

                // res.json() and the destructuring reject on a body that is not a JSON list (nothing is shown).
                if (!(HubRequests.Json(text, out var ok) is List<object> servers) || !ok) return;
                var firstServer = servers.Count > 0 ? servers[0] : Js.Undefined.Value;

                // Check if there are no servers
                if (!HubRequests.Truthy(firstServer))
                {
                    // Display an error message.
                    SetValidation("validation-error", "validation.noServers");
                    return;
                }

                _servers = servers;

                // Select the first server
                SelectServer(firstServer);

                // If there is only one server, then hide the world select button
                if (servers.Count < 2) return;

                if (_current != "death") _worldSelectButton.gameObject.SetActive(true);

                for (var i = 0; i < servers.Count; i++) CreateWorldElement(i);
                _worldsList.sizeDelta = new Vector2(0, servers.Count * WorldHeight);
            });
        }

        // li: padding 0.5em 0, a 0.125em border, line-height 1.
        static float WorldHeight => 2.25f * UiKit.Em;

        string PlayersText(object server, bool button) => Localization.Get(button ? "intro.worldPlayersButton" : "intro.worldPlayers",
            ("players", HubRequests.Template(HubRequests.Property(server, "players"))),
            ("maxPlayers", HubRequests.Template(HubRequests.Property(server, "maxPlayers"))));

        static string WorldName(object server) =>
            $"{HubRequests.Template(HubRequests.Property(server, "name"))} {HubRequests.Template(HubRequests.Property(server, "id"))}";

        /// <summary>
        /// app.ts selectServer: the world to connect to, its name and player count on the world select button.
        /// </summary>
        void SelectServer(object server)
        {
            SelectedServer = server;
            if (_worldSelectButton == null) return;

            _worldButtonName.text = WorldName(server);
            _worldButtonPlayers.text = PlayersText(server, true);

            // .slice-button: padding 0.5em 1em around the column of the two lines.
            var width = Math.Max(UiKit.Width(_worldButtonName), UiKit.Width(_worldButtonPlayers)) + 2 * UiKit.Em;
            _worldSelectButton.sizeDelta = new Vector2(width, 3 * UiKit.Em);
            UiKit.Place(_worldButtonName.rectTransform, 0, 0.5f * UiKit.Em, width, UiKit.Em);
            UiKit.Place(_worldButtonPlayers.rectTransform, 0, 1.5f * UiKit.Em, width, UiKit.Em);
        }

        /// <summary>
        /// One li of ol#worlds: a row (space-evenly) with the name and the player count. Odd rows are darkened 25%,
        /// 50% under the pointer, 75% while pressed; the active world is 75% with a #fcda5c border and text.
        /// </summary>
        void CreateWorldElement(int index)
        {
            var server = _servers[index];
            var row = UiKit.Box(_worldsList, "li").gameObject.AddComponent<Image>();
            UiKit.Place(row.rectTransform, 0, index * WorldHeight, _worldsListWidth, WorldHeight);
            var button = row.gameObject.AddComponent<Button>();
            button.targetGraphic = row;

            var name = _kit.Label(row.transform, "strong", WorldName(server), _kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleLeft);
            var players = _kit.Label(row.transform, "span", PlayersText(server, false), _kit.Body, UiKit.Em, "#eee", TextAnchor.MiddleLeft);

            // border: 0.125em solid (transparent unless active): four edges along the row's sides.
            var border = UiKit.Fill(UiKit.Box(row.transform, "border"));
            var thickness = 0.125f * UiKit.Em;
            foreach (var (min, max, pivot, size) in new[]
                     {
                         (new Vector2(0, 1), new Vector2(1, 1), new Vector2(0.5f, 1), new Vector2(0, thickness)),
                         (new Vector2(0, 0), new Vector2(1, 0), new Vector2(0.5f, 0), new Vector2(0, thickness)),
                         (new Vector2(0, 0), new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(thickness, 0)),
                         (new Vector2(1, 0), new Vector2(1, 1), new Vector2(1, 0.5f), new Vector2(thickness, 0))
                     })
            {
                var edge = UiKit.Box(border, "edge").gameObject.AddComponent<Image>();
                edge.raycastTarget = false;
                edge.color = UiKit.Css("#fcda5c");
                edge.rectTransform.anchorMin = min;
                edge.rectTransform.anchorMax = max;
                edge.rectTransform.pivot = pivot;
                edge.rectTransform.anchoredPosition = Vector2.zero;
                edge.rectTransform.sizeDelta = size;
            }

            _worldRows.Add((row, name, players, index));
            // If this is the first server in the list, select it and mark it as active
            StyleWorldRow(_worldRows.Count - 1, index == 0);

            // When the <li> element is clicked, select the server and update the active class
            button.onClick.AddListener(() =>
            {
                SelectServer(server);
                for (var i = 0; i < _worldRows.Count; i++) StyleWorldRow(i, _worldRows[i].row == row);
            });

            _relabel.Add(() => PlaceWorldRow(name, players));
            PlaceWorldRow(name, players);
        }

        void PlaceWorldRow(Text name, Text players)
        {
            // justify-content: space-evenly.
            var nameWidth = UiKit.Width(name);
            var playersWidth = UiKit.Width(players);
            var gap = (_worldsListWidth - nameWidth - playersWidth) / 3;
            UiKit.Place(name.rectTransform, gap, 0.625f * UiKit.Em, nameWidth, UiKit.Em);
            UiKit.Place(players.rectTransform, 2 * gap + nameWidth, 0.625f * UiKit.Em, playersWidth, UiKit.Em);
        }

        /// <summary>A world row's colours: li (odd rows darker), li:hover, li:active and li.active.</summary>
        void StyleWorldRow(int position, bool isActive)
        {
            var (row, name, players, index) = _worldRows[position];
            var button = row.GetComponent<Button>();
            var normal = isActive ? 0.75f : index % 2 == 0 ? 0.25f : 0;
            row.color = Color.white;
            button.colors = new ColorBlock
            {
                normalColor = new Color(0, 0, 0, normal),
                highlightedColor = new Color(0, 0, 0, isActive ? 0.75f : 0.5f),
                pressedColor = new Color(0, 0, 0, 0.75f),
                selectedColor = new Color(0, 0, 0, normal),
                disabledColor = new Color(0, 0, 0, normal),
                colorMultiplier = 1,
                fadeDuration = 0
            };
            name.color = players.color = UiKit.Css(isActive ? "#fcda5c" : "#eee");
            row.transform.Find("border").gameObject.SetActive(isActive);
        }

        /// <summary>
        /// app.ts forgotPassword: checks the email address and asks the hub to send the reset mail. Any response
        /// counts as sent.
        /// </summary>
        void ForgotPassword()
        {
            if (_hub == null)
            {
                SetValidation("validation-error", "validation.noHub");
                return;
            }

            // Grab the input field value for the email address.
            var email = _emailReset.text;

            // Validate the input email.
            if (email.Length == 0 || !UtilEmail.IsMatch(email))
            {
                SetValidation("validation-error", "validation.validEmail");
                return;
            }

            // Send a post request to the hub to request a password reset.
            HubRequests.PostJson($"{_hub}/api/v1/requestReset", Js.JsJson.Stringify(new Dictionary<string, object> { ["email"] = email }), text =>
            {
                if (_root == null) return;

                // If we do not get a response, then we display an error message.
                if (text == null)
                {
                    SetValidation("validation-error", "validation.contactAdmin");
                    return;
                }

                // If we receive a response then just let the player know that the request was sent.
                SetValidation("status", "validation.resetSent");
            }, acceptJson: true);
        }

        /// <summary>
        /// reset.ts reset: checks the new password and sends it with the link's id and token to the hub; on success
        /// the login comes back after 5 seconds (the original goes to '/').
        /// </summary>
        void Reset()
        {
            var password = _resetPasswordField.text;
            var passwordConfirm = _resetPasswordConfirm.text;

            // Check if the password is empty.
            if (password.Length == 0)
            {
                SetValidation("validation-error", "reset.empty");
                return;
            }

            // Check that the password is at least 3 characters long.
            if (password.Length < 3)
            {
                SetValidation("validation-error", "validation.passwordShort");
                return;
            }

            // Check that the password is no longer than 64 characters long.
            if (password.Length > 64)
            {
                SetValidation("validation-error", "reset.long");
                return;
            }

            // Check if the passwords match.
            if (password != passwordConfirm)
            {
                SetValidation("validation-error", "validation.passwordMatch");
                return;
            }

            // Create the POST request that we send to the hub.
            var body = Js.JsJson.Stringify(new Dictionary<string, object> { ["id"] = _resetId, ["token"] = _resetToken, ["password"] = password });
            HubRequests.PostJson($"{_hub}/api/v1/resetPassword", body, text =>
            {
                if (_root == null) return;

                // If we didn't get a response, we don't do anything.
                if (text == null)
                {
                    SetValidation("validation-error", "reset.failed");
                    return;
                }

                // response.json() rejects on a body that is not JSON (nothing is shown).
                var json = HubRequests.Json(text, out var ok);
                if (!ok) return;

                // Generic response error.
                if (!(HubRequests.Property(json, "status") is string status) || status != "success")
                {
                    SetValidation("validation-error", "reset.expired");
                    return;
                }

                SetValidation("status", "reset.success");

                // Redirect to the login page after 5 seconds.
                _redirectAt = Time.unscaledTime + 5;
            });
        }

        /// <summary>For tests: the world rows (name, players) and the reset scroll's state.</summary>
        public IReadOnlyList<string> Worlds => _worldRows.ConvertAll(r => $"{r.name.text} {r.players.text}");
        public string CurrentScroll => _current;
        public void SelectWorld(int index) => _worldRows[index].row.GetComponent<Button>().onClick.Invoke();

        /// <summary>The game has started: fade the menu out (app.ts fadeMenu) and remember the login.</summary>
        public void FadeOut()
        {
            if (_hidden) return;
            _hidden = true;
            _fadeTarget = 0;
            _loggingIn = false;
            _loaderMessage = null;

            if (!IsGuest)
            {
                PlayerPrefs.SetInt(RememberKey, _remember.isOn ? 1 : 0);
                PlayerPrefs.SetString(UsernameKey, _remember.isOn ? Username : "");
            }
        }

        public void Dispose() => Localization.LanguageChanged -= Relabel;
    }
}
