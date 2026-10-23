using System;
using System.Collections.Generic;
using System.Linq;
using PixelHero.Js;
using UnityEngine;
using UnityEngine.UI;

namespace PixelHero.UI
{
    /// <summary>
    /// The in-game overlay of client/components/game.astro: the health and mana bars (menu/header.ts), the button
    /// bar (only chat works so far), the chat box (controllers/chat.ts) and the bottom notification
    /// (menu/notification.ts). Layout numbers are the original CSS at 1em = 16px.
    /// </summary>
    public sealed class GameHud
    {
        const float BarWidth = 63 * 4, BarHeight = 15 * 4;
        // CSS "ease" for width transitions: cubic-bezier(0.25, 0.1, 0.25, 1).
        const float BarTransition = 0.5f;

        readonly UiKit _kit;
        readonly RectTransform _root;

        // ---- header
        sealed class Bar
        {
            public RectTransform Mask;
            public Image Fill;
            public Text Text;
            public float From, To, Current, Elapsed = BarTransition;
        }

        Bar _health, _mana;
        Sprite _healthBase, _healthPoison, _healthWhite;
        float _flash;
        bool _poison;

        // ---- buttons
        Image _chatButton;
        Sprite[] _chatButtonFrames;

        // ---- chat
        RectTransform _chat, _chatLog, _chatLines;
        CanvasGroup _chatGroup, _frameGroup;
        InputField _chatInput;
        readonly List<Text> _messages = new List<Text>();
        float _chatTarget, _frameTarget, _hideAt = -1;
        bool _inputVisible;

        // ---- notification
        RectTransform _notification;
        Text _notificationTitle, _notificationDescription;
        float _notificationY = -6 * UiKit.Em, _notificationFrom, _notificationTo = -6 * UiKit.Em, _notificationElapsed = 0.7f;
        bool _notificationVisible;
        float _notificationHideAt = -1;
        (string title, string message, string colour)? _pendingNotification;
        float _pendingAt;

        /// <summary>The chat input was submitted (chat.ts send: Packets.Chat [text]).</summary>
        public event Action<string> ChatSubmitted;

        public GameHud(UiKit kit, RectTransform parent)
        {
            _kit = kit;
            _root = UiKit.Fill(UiKit.Box(parent, "#game-container"));
            BuildHeader();
            BuildButtons();
            BuildChat();
            BuildNotification();
            SetVisible(false);
        }

        public void SetVisible(bool visible) => _root.gameObject.SetActive(visible);

        /// <summary>For tests: the chat lines as rich text, the bar texts, whether a notification is up.</summary>
        public IReadOnlyList<string> ChatMessages => _messages.Select(m => m.text).ToList();
        public string HealthText => _health.Text.text;
        public string ManaText => _mana.Text.text;
        public bool NotificationVisible => _notificationVisible;

        // ---------------------------------------------------------------- header

        Sprite BarRow(int row) => _kit.Region("bars", 0, row * 15, 63, 15);

        void BuildHeader()
        {
            _healthBase = BarRow(2);
            _healthPoison = BarRow(4);
            _healthWhite = BarRow(5);
            _health = MakeBar("#health", 0.5f * UiKit.Em, BarRow(0), _healthBase, 0.75f * 20);
            _mana = MakeBar("#mana", 3.25f * UiKit.Em, BarRow(1), BarRow(3), 0.1f * 20);
        }

        Bar MakeBar(string name, float top, Sprite background, Sprite fill, float textTop)
        {
            var frame = UiKit.Box(_root, name).gameObject.AddComponent<Image>();
            frame.sprite = background;
            frame.raycastTarget = false;
            UiKit.Place(frame.rectTransform, UiKit.Em, top, BarWidth, BarHeight);

            var mask = UiKit.Box(frame.transform, "mask");
            mask.gameObject.AddComponent<RectMask2D>();
            UiKit.Place(mask, 0, 0, 0, BarHeight);

            var image = UiKit.Box(mask, "fill").gameObject.AddComponent<Image>();
            image.sprite = fill;
            image.raycastTarget = false;
            UiKit.Place(image.rectTransform, 0, 0, BarWidth, BarHeight);

            // .health-text / .mana-text: 1.25em white with the .stroke outline.
            var text = _kit.Label(frame.transform, "text", "", _kit.Body, 1.25f * UiKit.Em, "white", TextAnchor.UpperCenter, true);
            UiKit.Place(text.rectTransform, 0, textTop, BarWidth, 1.25f * UiKit.Em);

            return new Bar { Mask = mask, Fill = image, Text = text };
        }

        /// <summary>header.ts setPoints: the mask's width and the "points/max" text; a decrease flashes the health bar.</summary>
        public void SetHitPoints(double? hitPoints, double? maxHitPoints, bool decrease)
        {
            SetPoints(_health, hitPoints, maxHitPoints);
            if (decrease) _flash = 0.15f;
        }

        public void SetMana(double? mana, double? maxMana) => SetPoints(_mana, mana, maxMana);

        public void SetPoison(bool poison) => _poison = poison;

        static string Number(double? value) => value is double v ? JsValue.NumberToString(v) : "undefined";

        void SetPoints(Bar bar, double? points, double? maxPoints)
        {
            var percentage = (points ?? double.NaN) / (maxPoints ?? double.NaN);
            // Math.floor(offsetWidth * percentage) px; NaN leaves the width as it was (an invalid CSS value).
            var width = Math.Floor(BarWidth * percentage);
            if (!double.IsNaN(width))
            {
                bar.From = bar.Current;
                bar.To = (float)Math.Max(0, width);
                bar.Elapsed = 0;
            }
            bar.Text.text = $"{Number(points)}/{Number(maxPoints)}";
        }

        static float Ease(float t)
        {
            // cubic-bezier(0.25, 0.1, 0.25, 1), solved for x by bisection.
            float lo = 0, hi = 1;
            for (var i = 0; i < 20; i++)
            {
                var mid = (lo + hi) / 2;
                if (Bezier(mid, 0.25f, 0.25f) < t) lo = mid;
                else hi = mid;
            }
            return Bezier((lo + hi) / 2, 0.1f, 1f);
        }

        static float Bezier(float s, float p1, float p2) => 3 * (1 - s) * (1 - s) * s * p1 + 3 * (1 - s) * s * s * p2 + s * s * s;

        void UpdateBar(Bar bar, float deltaTime)
        {
            bar.Elapsed = Math.Min(BarTransition, bar.Elapsed + deltaTime);
            bar.Current = Mathf.Lerp(bar.From, bar.To, Ease(bar.Elapsed / BarTransition));
            bar.Mask.sizeDelta = new Vector2(bar.Current, BarHeight);
        }

        // ---------------------------------------------------------------- buttons

        void BuildButtons()
        {
            // ul#buttons: bottom-right, a row of hud_buttons (22×25 frames at 2×: normal, hover, active), in the
            // order of game.astro. Each id's row in the sheet follows the scss selector list.
            var order = new[] { "inventory", "achievements", "quests", "warp", "profile", "friends", "chat", "settings" };
            var rows = new Dictionary<string, int>
            {
                ["inventory"] = 0, ["chat"] = 1, ["leaderboard"] = 2, ["warp"] = 3, ["settings"] = 4, ["profile"] = 5,
                ["quests"] = 6, ["guilds"] = 7, ["friends"] = 8, ["achievements"] = 9
            };

            var bar = UiKit.Box(_root, "#buttons");
            bar.anchorMin = bar.anchorMax = bar.pivot = new Vector2(1, 0);
            bar.anchoredPosition = Vector2.zero;
            bar.sizeDelta = new Vector2(order.Length * 44, 50);

            for (var i = 0; i < order.Length; i++)
            {
                var row = rows[order[i]];
                var frames = new[] { 0, 1, 2 }.Select(f => _kit.Region("hud_buttons", f * 22, row * 25, 22, 25)).ToArray();
                var image = UiKit.Box(bar, $"#{order[i]}-button").gameObject.AddComponent<Image>();
                image.sprite = frames[0];
                UiKit.Place(image.rectTransform, i * 44, 0, 44, 50);
                var button = image.gameObject.AddComponent<Button>();
                button.transition = Selectable.Transition.SpriteSwap;
                button.spriteState = new SpriteState { highlightedSprite = frames[1], pressedSprite = frames[2], selectedSprite = frames[0] };
                var id = order[i];
                _buttons[id] = (image, frames);
                if (id == "chat")
                {
                    _chatButton = image;
                    _chatButtonFrames = frames;
                    button.onClick.AddListener(ToggleChat);
                }
                else if (id == "inventory" || id == "profile" || id == "quests" || id == "achievements" || id == "friends" || id == "warp" || id == "settings")
                    button.onClick.AddListener(() => ButtonClicked?.Invoke(id));
                // The other windows come in later stages.
                else button.interactable = false;
            }
        }

        readonly Dictionary<string, (Image image, Sprite[] frames)> _buttons = new Dictionary<string, (Image, Sprite[])>();

        /// <summary>A HUD button was clicked ("inventory", "profile", "quests", "achievements").</summary>
        public event Action<string> ButtonClicked;

        /// <summary>menu.ts show / hide: the button's active frame while its window is open.</summary>
        public void SetButtonActive(string id, bool active)
        {
            if (_buttons.TryGetValue(id, out var button)) button.image.sprite = button.frames[active ? 2 : 0];
        }

        /// <summary>The overlay's root (#game-container), for the windows and bubbles drawn with it.</summary>
        public RectTransform Root => _root;

        // ---------------------------------------------------------------- chat

        void BuildChat()
        {
            // #chat: bottom 3em, left 1em, 24em wide; the log is 10em tall below a 1em margin.
            _chat = UiKit.Box(_root, "#chat");
            _chat.anchorMin = _chat.anchorMax = _chat.pivot = new Vector2(0, 0);
            _chat.anchoredPosition = new Vector2(UiKit.Em, 3 * UiKit.Em);
            _chat.sizeDelta = new Vector2(24 * UiKit.Em, 11 * UiKit.Em);
            _chatGroup = _chat.gameObject.AddComponent<CanvasGroup>();

            // #chat-frame: chatbox.png at 1.5×, from the box's top-left.
            var frame = UiKit.Box(_chat, "#chat-frame").gameObject.AddComponent<RawImage>();
            frame.texture = _kit.Image("interface/chatbox");
            frame.raycastTarget = false;
            UiKit.Place(frame.rectTransform, 0, 0, 264 * 1.5f, 135 * 1.5f);
            _frameGroup = frame.gameObject.AddComponent<CanvasGroup>();

            // #chat-log: margin 1em 0.375em 0 0.675em, overflow hidden, scrolled to the bottom.
            _chatLog = UiKit.Box(_chat, "#chat-log");
            _chatLog.gameObject.AddComponent<RectMask2D>();
            UiKit.Place(_chatLog, 0.675f * UiKit.Em, UiKit.Em, (24 - 0.675f - 0.375f) * UiKit.Em, 10 * UiKit.Em);
            _chatLines = UiKit.Box(_chatLog, "lines");
            _chatLines.anchorMin = _chatLines.anchorMax = _chatLines.pivot = new Vector2(0, 0);
            _chatLines.anchoredPosition = Vector2.zero;
            _chatLines.sizeDelta = new Vector2(_chatLog.sizeDelta.x, 0);

            // #chat-input: bottom -1.35em, margin-left 1em, 22em × 1.25em, transparent, white.
            _chatInput = _kit.Input(_chat, "#chat-input", "", UiKit.Em, false, 256);
            var background = (Image)_chatInput.targetGraphic;
            background.color = Color.clear;
            _chatInput.transition = Selectable.Transition.None;
            var input = (RectTransform)_chatInput.transform;
            input.anchorMin = input.anchorMax = input.pivot = new Vector2(0, 0);
            input.anchoredPosition = new Vector2(UiKit.Em, -1.35f * UiKit.Em);
            input.sizeDelta = new Vector2(22 * UiKit.Em, 1.25f * UiKit.Em);
            var area = (RectTransform)_chatInput.textComponent.transform.parent;
            area.offsetMin = area.offsetMax = Vector2.zero;
            _chatInput.textComponent.color = Color.white;
            _chatInput.gameObject.SetActive(false);

            _chatGroup.alpha = 0;
            _frameGroup.alpha = 0;
            _chat.gameObject.SetActive(false);
        }

        /// <summary>The chat input is open (chat.ts inputVisible); keys go to it instead of the game.</summary>
        public bool ChatInputVisible => _inputVisible;

        /// <summary>chat.ts add: "source » message" in the colour (default white), bold for notifications.</summary>
        public void AddChat(string source, string messageHtml, string colour = "", bool notify = false)
        {
            var line = _kit.Label(_chatLines, "p", "", _kit.Body, 1.25f * UiKit.Em, string.IsNullOrEmpty(colour) ? "white" : colour);
            line.supportRichText = true;
            line.horizontalOverflow = HorizontalWrapMode.Wrap;
            if (notify) line.fontStyle = FontStyle.Bold;
            line.text = MessageFormat.ToRichText($"{source} » {messageHtml}");
            if (!CssColour.TryParse(colour, out _)) line.color = Color.white;

            // text-shadow: 0.125em 0.125em 0.125em black (2.5px at 1.25em; blur drawn as an offset copy).
            var shadow = line.gameObject.AddComponent<PixelStroke>();
            shadow.Colour = Color.black;
            shadow.Offsets = new[] { new Vector2(2, -2) };

            _messages.Add(line);
            if (_messages.Count > 200)
            {
                UnityEngine.Object.Destroy(_messages[0].gameObject);
                _messages.RemoveAt(0);
            }
            LayoutChat();

            Display(false);
            HideChatBoxLater();
        }

        void LayoutChat()
        {
            // Paragraphs stacked top-down (margin-bottom 0.1em), the log scrolled to the bottom.
            var width = _chatLines.sizeDelta.x;
            var y = 0f;
            var heights = new List<float>();
            foreach (var line in _messages)
            {
                var settings = line.GetGenerationSettings(new Vector2(width, 0));
                var height = line.cachedTextGeneratorForLayout.GetPreferredHeight(line.text, settings) / line.pixelsPerUnit;
                heights.Add(height);
                y += height + 0.1f * 1.25f * UiKit.Em;
            }

            _chatLines.sizeDelta = new Vector2(width, y);
            var top = 0f;
            for (var i = 0; i < _messages.Count; i++)
            {
                UiKit.Place(_messages[i].rectTransform, 0, top, width, heights[i]);
                top += heights[i] + 0.1f * 1.25f * UiKit.Em;
            }
            // anchored at the bottom: content taller than the log scrolls up.
            _chatLines.anchoredPosition = Vector2.zero;
            _chatLines.pivot = new Vector2(0, 0);
        }

        /// <summary>chat.ts keyDown while the input is open: Enter sends (or closes when empty), Escape closes.</summary>
        public void ChatKey(bool enter, bool escape)
        {
            if (enter && _chatInput.text != "")
            {
                ChatSubmitted?.Invoke(_chatInput.text);
                HideChat();
                return;
            }
            if (escape || enter) ToggleChat();
        }

        /// <summary>chat.ts privateMessage: opens the input with "/pm *username* " typed.</summary>
        public void PrivateMessage(string username)
        {
            if (_inputVisible) return;
            ToggleChat();
            _chatInput.text = $"/pm *{username}* ";
            _chatInput.caretPosition = _chatInput.text.Length;
        }

        /// <summary>chat.ts toggle.</summary>
        public void ToggleChat()
        {
            _hideAt = -1;
            if (_inputVisible) HideChat();
            else Display(true);
        }

        void Display(bool withInput)
        {
            _chatButton.sprite = _chatButtonFrames[2];
            _chat.gameObject.SetActive(true);
            _chatTarget = 1;

            if (withInput)
            {
                _frameTarget = 1;
                _inputVisible = true;
                _chatInput.gameObject.SetActive(true);
                _chatInput.text = "";
                _chatInput.Select();
                _chatInput.ActivateInputField();
            }
            else _frameTarget = 0;
        }

        void HideChat()
        {
            _chatButton.sprite = _chatButtonFrames[0];
            HideChatBoxLater();
            _chatInput.text = "";
            _chatInput.DeactivateInputField();
            _chatInput.gameObject.SetActive(false);
            _inputVisible = false;
            _frameTarget = 0;
        }

        // chat.ts hideChatBox: fade the box out 5 s later unless the input is open.
        void HideChatBoxLater() => _hideAt = Time.unscaledTime + 5;

        void UpdateChat(float deltaTime)
        {
            if (_hideAt >= 0 && Time.unscaledTime >= _hideAt)
            {
                _hideAt = -1;
                if (!_inputVisible) _chatTarget = 0;
            }

            // util.ts fadeIn: +0.1 per animation frame; fadeOut: -0.2 per frame (60 frames a second).
            _chatGroup.alpha = Step(_chatGroup.alpha, _chatTarget, deltaTime);
            _frameGroup.alpha = Step(_frameGroup.alpha, _frameTarget, deltaTime);
            if (_chatTarget == 0 && _chatGroup.alpha <= 0) _chat.gameObject.SetActive(false);
            _chatGroup.blocksRaycasts = _inputVisible;
        }

        static float Step(float value, float target, float deltaTime) =>
            Mathf.MoveTowards(value, target, (target > value ? 0.1f : 0.2f) * 60 * deltaTime);

        // ---------------------------------------------------------------- notification

        void BuildNotification()
        {
            // #notification.slice-dialog: 14em × 6em, centred, bottom -6em (hidden) / 0 (shown), opacity 0.8,
            // padding 1.5em 1em 1em, border-image-width 1em 1em 0 1em.
            var dialog = _kit.Sliced(_root, "#notification", "dialog", UiKit.Em);
            _notification = dialog.rectTransform;
            _notification.anchorMin = _notification.anchorMax = _notification.pivot = new Vector2(0.5f, 0);
            _notification.sizeDelta = new Vector2(14 * UiKit.Em, 6 * UiKit.Em);
            _notification.anchoredPosition = new Vector2(0, _notificationY);
            dialog.gameObject.AddComponent<CanvasGroup>().alpha = 0.8f;
            dialog.raycastTarget = false;

            // Title: Fibberish, #45ebfc by default, shadow 0.125em 0.125em #0e0a00.
            _notificationTitle = _kit.Label(_notification, "#notification-text-title", "", _kit.Heading, UiKit.Em, "#45ebfc",
                TextAnchor.UpperCenter);
            UiKit.Place(_notificationTitle.rectTransform, UiKit.Em, 1.5f * UiKit.Em, 12 * UiKit.Em, UiKit.Em);
            _notificationTitle.supportRichText = true;
            Shadow(_notificationTitle, new Vector2(2, -2));

            // Description: 1em #eee, shadow 0.05em 0.075em #0e0a00, 0.2em below the title.
            _notificationDescription = _kit.Label(_notification, "#notification-text-description", "", _kit.Body, UiKit.Em, "#eee",
                TextAnchor.UpperCenter);
            UiKit.Place(_notificationDescription.rectTransform, UiKit.Em, 2.7f * UiKit.Em, 12 * UiKit.Em, 2.3f * UiKit.Em);
            _notificationDescription.horizontalOverflow = HorizontalWrapMode.Wrap;
            _notificationDescription.supportRichText = true;
            Shadow(_notificationDescription, new Vector2(1, -1));
        }

        static void Shadow(Text text, Vector2 offset)
        {
            var shadow = text.gameObject.AddComponent<PixelStroke>();
            shadow.Colour = UiKit.Css("#0e0a00");
            shadow.Offsets = new[] { offset };
        }

        /// <summary>notification.ts show (title and message already formatted HTML).</summary>
        public void ShowNotification(string titleHtml, string messageHtml, string colour)
        {
            if (_notificationVisible)
            {
                HideNotification();
                _pendingNotification = (titleHtml, messageHtml, colour);
                _pendingAt = Time.unscaledTime + 0.7f;
                return;
            }

            _notificationVisible = true;
            MoveNotification(0);
            if (CssColour.TryParse(colour, out var c)) _notificationTitle.color = c;
            _notificationTitle.text = MessageFormat.ToRichText(titleHtml);
            _notificationDescription.text = MessageFormat.ToRichText(messageHtml);
            _notificationHideAt = Time.unscaledTime + 7;
        }

        void HideNotification()
        {
            _notificationVisible = false;
            _notificationHideAt = -1;
            MoveNotification(-6 * UiKit.Em);
        }

        void MoveNotification(float bottom)
        {
            _notificationFrom = _notificationY;
            _notificationTo = bottom;
            _notificationElapsed = 0;
        }

        void UpdateNotification(float deltaTime)
        {
            if (_notificationHideAt >= 0 && Time.unscaledTime >= _notificationHideAt) HideNotification();
            if (_pendingNotification is { } pending && Time.unscaledTime >= _pendingAt)
            {
                _pendingNotification = null;
                ShowNotification(pending.title, pending.message, pending.colour);
            }

            // transition: 0.7s bottom ease.
            _notificationElapsed = Math.Min(0.7f, _notificationElapsed + deltaTime);
            _notificationY = Mathf.Lerp(_notificationFrom, _notificationTo, Ease(_notificationElapsed / 0.7f));
            _notification.anchoredPosition = new Vector2(0, _notificationY);
        }

        // ---------------------------------------------------------------- frame

        public void Update(float deltaTime, bool poison)
        {
            _poison = poison;
            UpdateBar(_health, deltaTime);
            UpdateBar(_mana, deltaTime);

            // health-mask-white for 150 ms after a decrease; health-mask-poison while poisoned.
            if (_flash > 0) _flash -= deltaTime;
            _health.Fill.sprite = _flash > 0 ? _healthWhite : _poison ? _healthPoison : _healthBase;

            UpdateChat(deltaTime);
            UpdateNotification(deltaTime);
        }
    }
}
