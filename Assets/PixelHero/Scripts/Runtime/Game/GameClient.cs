using System;
using System.Diagnostics;
using PixelHero.Audio;
using PixelHero.Common;
using PixelHero.Entities;
using PixelHero.Js;
using PixelHero.Maps;
using PixelHero.Network;
using PixelHero.Sprites;
using PixelHero.UI;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using System.Linq;
using UnityCamera = UnityEngine.Camera;

namespace PixelHero.Game
{
    /// <summary>Date.now(): whole milliseconds since the Unix epoch.</summary>
    public sealed class UnixClock : IGameClock
    {
        readonly double _start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        readonly Stopwatch _watch = Stopwatch.StartNew();

        public double Now => Math.Floor(_start + _watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// The playable client: one connection to the original server, the map, the entities, the camera and mouse /
    /// keyboard movement, run in the client's frame order (game.ts tick: timers, network, updater, renderer).
    ///
    /// Picture: background tiles, the #entities canvas, the #entities-fore canvas, foreground tiles, then #entities
    /// again at 20% (#entities-mask). Entity canvases are screen-sized render textures; tiles are tilemaps placed so
    /// that they sit one screen pixel right/down of the entities, as on the browser canvas. #text-canvas (names, levels,
    /// health bars, splats) goes on top of everything.
    /// </summary>
    public sealed class GameClient : MonoBehaviour
    {
        public SpriteCatalog Catalog;
        public string ServerUrl = "ws://127.0.0.1:59011";

        /// <summary>
        /// The secure (wss) server: the C# server's TLS listener (SSL_PORT). An https page must use wss, so a WebGL
        /// page served over https connects to this port on its own host.
        /// </summary>
        public string SecureServerUrl = "wss://localhost:59443";

        /// <summary>
        /// In a WebGL page the server is on the page's host, as the browser client reaches it: ws at
        /// <paramref name="fallback"/>'s port from an http page, wss at <paramref name="secureFallback"/>'s port from an
        /// https page (the fallback's port when there is none). <c>?server=ws://host:port</c> or
        /// <c>?server=wss://host:port</c> in the page URL overrides both.
        /// </summary>
        public static string WebServerUrl(string pageUrl, string fallback, string secureFallback = null)
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return fallback;
            foreach (var pair in page.Query.TrimStart('?').Split('&'))
                if (pair.StartsWith("server=")) return Uri.UnescapeDataString(pair.Substring(7));
            var secure = page.Scheme == "https";
            var source = secure && !string.IsNullOrEmpty(secureFallback) ? secureFallback : fallback;
            var port = Uri.TryCreate(source, UriKind.Absolute, out var server) ? server.Port : 59011;
            return $"{(secure ? "wss" : "ws")}://{page.Host}:{port}";
        }

        /// <summary>
        /// On PC and in the Editor: <c>-server &lt;url&gt;</c> on the command line, else the PIXELHERO_SERVER_URL
        /// environment variable, else <paramref name="fallback"/>. The URL may be ws:// or wss://.
        /// </summary>
        public static string DesktopServerUrl(string[] args, string environment, string fallback)
        {
            for (var i = 0; args != null && i + 1 < args.Length; i++)
                if (args[i] == "-server" && !string.IsNullOrEmpty(args[i + 1])) return args[i + 1];
            return string.IsNullOrEmpty(environment) ? fallback : environment;
        }

        /// <summary>
        /// The hub's HTTP address (globalConfig.hub: <c>config.hubEnabled &amp;&amp; http://host:port</c> in
        /// astro.config.ts), without a trailing slash; empty when there is no hub, and then the client behaves as it
        /// does without one (no world list, no leaderboards, no password reset, the server is <see cref="ServerUrl"/>).
        /// Set from <see cref="WebHubUrl"/> in a WebGL page and <see cref="DesktopHubUrl"/> elsewhere.
        /// </summary>
        public string HubUrl = "";

        /// <summary>The value of <c>name</c> in a page URL's query (<c>?name=value</c>), or null.</summary>
        public static string QueryParameter(string pageUrl, string name)
        {
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return null;
            foreach (var pair in page.Query.TrimStart('?').Split('&'))
                if (pair.StartsWith(name + "=")) return Uri.UnescapeDataString(pair.Substring(name.Length + 1).Replace('+', ' '));
            return null;
        }

        /// <summary>In a WebGL page: <c>?hub=http://host:port</c> in the page URL, else <paramref name="fallback"/>.</summary>
        public static string WebHubUrl(string pageUrl, string fallback) => TrimHub(QueryParameter(pageUrl, "hub") ?? fallback);

        /// <summary>
        /// On PC and in the Editor: <c>-hub &lt;url&gt;</c> on the command line, else the PIXELHERO_HUB_URL environment
        /// variable, else <paramref name="fallback"/>.
        /// </summary>
        public static string DesktopHubUrl(string[] args, string environment, string fallback)
        {
            for (var i = 0; args != null && i + 1 < args.Length; i++)
                if (args[i] == "-hub" && !string.IsNullOrEmpty(args[i + 1])) return TrimHub(args[i + 1]);
            return TrimHub(string.IsNullOrEmpty(environment) ? fallback : environment);
        }

        static string TrimHub(string url) => (url ?? "").Trim().TrimEnd('/');

        /// <summary>
        /// The password reset link's token and id (reset.ts reads <c>?token=…&amp;id=…</c> from the page URL). A
        /// WebGL page reads them from its own URL; on PC they come from <c>-resetToken &lt;token&gt; -resetId
        /// &lt;id&gt;</c> on the command line, else the PIXELHERO_RESET_TOKEN and PIXELHERO_RESET_ID environment variables.
        /// Both null when the client was not opened from a reset link.
        /// </summary>
        public static (string token, string id) ResetParameters(bool webGL, string pageUrl, string[] args, Func<string, string> environment)
        {
            if (webGL) return (QueryParameter(pageUrl, "token"), QueryParameter(pageUrl, "id"));

            string token = null, id = null;
            for (var i = 0; args != null && i + 1 < args.Length; i++)
            {
                if (args[i] == "-resetToken") token = args[i + 1];
                if (args[i] == "-resetId") id = args[i + 1];
            }
            token ??= environment("PIXELHERO_RESET_TOKEN");
            id ??= environment("PIXELHERO_RESET_ID");
            return (string.IsNullOrEmpty(token) ? null : token, string.IsNullOrEmpty(id) ? null : id);
        }

        bool HasHub => !string.IsNullOrEmpty(HubUrl);

        /// <summary>game.ts postLoad: camera.setZoom(storage zoom), 3 for a new browser profile.</summary>
        public double Zoom = 3;
        public UnityCamera ViewCamera;
        /// <summary>Assets/PixelHero/Localization/*.json, the default language (Korean) first.</summary>
        public TextAsset[] Languages;

        const int EntitiesSortingOrder = 0;
        const int ForeSortingOrder = 50;
        const int MaskSortingOrder = 150;
        /// <summary>#overlay comes after #cursor and before #text-canvas.</summary>
        const int OverlaySortingOrder = 175;
        const int TextSortingOrder = 200;
        const int UiSortingOrder = 1000;

        /// <summary>
        /// game.ts handleDisconnection reloads the page once in game; the scene is reloaded instead, and the
        /// reason is shown on the title screen (app.ts loadLogin shows the error kept in storage).
        /// </summary>
        static string _pendingError;

        UnixClock _clock;
        Stopwatch _performance;
        World _world;
        ClientMap _map;
        EntityPackets _packets;
        GameConnection _connection;
        MapView _mapView;
        Entities.Camera _camera;
        EntityDrawPlanner _planner;
        EntityCanvasRenderer _canvases;
        TextDrawPlanner _textPlanner;
        TextCanvasView _textView;
        MeshRenderer _entitiesLayer, _foreLayer, _maskLayer;
        MeshRenderer _overlayLayer;
        OverlayCanvasRenderer _overlayCanvas;
        int _screenWidth, _screenHeight;
        Canvas _uiCanvas;
        CanvasScaler _uiScaler;
        UiKit _uiKit;
        IntroView _intro;
        GameHud _hud;
        BubbleView _bubbles;
        InventoryView _inventory;
        ProfileView _profile;
        QuestsView _quests;
        LeaderboardsView _leaderboards;
        QuestDialogView _questDialog;
        AchievementsView _achievements;
        StoreView _store;
        BankView _bank;
        LootBagView _lootBag;
        EnchantView _enchant;
        EquipmentsView _equipments;
        InteractView _interact;
        FriendsView _friends;
        TradeView _trade;
        GuildsView _guilds;
        WarpView _warp;
        CraftingView _crafting;
        SettingsView _settings;
        WelcomeView _welcome;
        AudioController _audio;
        PointerView _pointerView;
        Image _brightness;
        MenuPanel[] _menus;
        bool _entered, _errorShown;

        public World World => _world;
        public GameConnection Connection => _connection;
        public Entities.Camera GameCamera => _camera;
        public IntroView Intro => _intro;
        public GameHud Hud => _hud;
        public InventoryView Inventory => _inventory;
        public ProfileView Profile => _profile;
        public QuestsView Quests => _quests;
        public LeaderboardsView Leaderboards => _leaderboards;
        public QuestDialogView QuestDialog => _questDialog;
        public AchievementsView Achievements => _achievements;
        public StoreView Store => _store;
        public BankView Bank => _bank;
        public LootBagView LootBag => _lootBag;
        public EnchantView Enchant => _enchant;
        public EquipmentsView Equipments => _equipments;
        public InteractView Interact => _interact;
        public FriendsView Friends => _friends;
        public TradeView Trade => _trade;
        public GuildsView Guilds => _guilds;
        public WarpView Warp => _warp;
        public CraftingView Crafting => _crafting;
        public SettingsView Settings => _settings;
        public WelcomeView Welcome => _welcome;
        public AudioController Audio => _audio;
        public PointerView PointerView => _pointerView;
        public float BrightnessAlpha => _brightness.color.a;
        public TextDrawPlanner TextPlanner => _textPlanner;
        public BubbleView Bubbles => _bubbles;
        /// <summary>The player is in the game (Welcome received).</summary>
        public bool Entered => _entered;
        public string Status { get; private set; } = "";

        void Start()
        {
            if (ViewCamera == null) ViewCamera = UnityCamera.main;
            ServerUrl = Application.platform == RuntimePlatform.WebGLPlayer
                ? WebServerUrl(Application.absoluteURL, ServerUrl, SecureServerUrl)
                : DesktopServerUrl(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable("PIXELHERO_SERVER_URL"), ServerUrl);
            HubUrl = Application.platform == RuntimePlatform.WebGLPlayer
                ? WebHubUrl(Application.absoluteURL, HubUrl)
                : DesktopHubUrl(Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable("PIXELHERO_HUB_URL"), HubUrl);

            _clock = new UnixClock();
            _performance = Stopwatch.StartNew();

            var info = MapInfo.Parse(Catalog.MapJson.text);
            _map = new ClientMap(info);

            _world = new World(_clock, _map, SpriteDefinitions.ParseAll(Catalog.SpritesJson.text, info.TileSize),
                key => Catalog.GetTexture("sprites/" + key) != null)
            {
                PerformanceNow = () => _performance.Elapsed.TotalMilliseconds,
                UserAgent = $"pixelhero-unity ({Application.platform})".ToLowerInvariant()
            };
            _world.Entities.Log = message => UnityEngine.Debug.LogWarning($"[PixelHero] {message}");

            _packets = new EntityPackets(_world);

            // controllers/audio.ts (the world's handlers pick the sounds).
            _audio = gameObject.AddComponent<AudioController>();
            _audio.Init(Catalog, _world);

            var mapObject = new GameObject("Map");
            _mapView = mapObject.AddComponent<MapView>();
            _mapView.Initialize(_map, Catalog, () => _clock.Now);

            _world.Send = (id, data) => _connection?.Send(id, data);

            LoadLanguages();
            CreateScreen();
            CreateUi();

            if (_pendingError != null)
            {
                _intro.ShowError(_pendingError);
                _pendingError = null;
            }
        }

        void LoadLanguages()
        {
            Localization.Clear();
            foreach (var table in Languages ?? Array.Empty<TextAsset>())
                if (table != null) Localization.Load(Localization.LanguageOf(table.name), table.text);
            Localization.SetLanguage(PlayerPrefs.GetString(IntroView.LanguageKey, Localization.DefaultLanguage));
        }

        /// <summary>The UI canvas (title screen, later the HUD) above the game, at an integer scale.</summary>
        void CreateUi()
        {
            UiKit.EnsureEventSystem();

            var ui = new GameObject("UI", typeof(RectTransform));
            _uiCanvas = ui.AddComponent<Canvas>();
            _uiCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _uiCanvas.worldCamera = ViewCamera;
            _uiCanvas.planeDistance = 1;
            _uiCanvas.sortingOrder = UiSortingOrder;
            _uiCanvas.pixelPerfect = true;
            _uiScaler = ui.AddComponent<CanvasScaler>();
            _uiScaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            _uiScaler.scaleFactor = UiKit.ScaleFor(_screenWidth, _screenHeight);
            ui.AddComponent<GraphicRaycaster>();

            _uiKit = new UiKit(Catalog);
            // The HUD first: the parchment (title screen, death) is drawn over it.
            _hud = new GameHud(_uiKit, (RectTransform)ui.transform);
            _hud.ChatSubmitted += text => _connection?.Send(Packets.Chat, new List<object> { text });
            var webGL = Application.platform == RuntimePlatform.WebGLPlayer;
            var (resetToken, resetId) = HasHub
                ? ResetParameters(webGL, Application.absoluteURL, Environment.GetCommandLineArgs(), Environment.GetEnvironmentVariable)
                : (null, null);
            _intro = new IntroView(_uiKit, (RectTransform)ui.transform, HubUrl, resetToken, resetId);
            _intro.LoginRequested += Login;
            _intro.RespawnRequested += Respawn;

            // menu/header.ts listens to the player from the start.
            _world.Player.HitPointsCallback = (hitPoints, maxHitPoints, decrease) => _hud.SetHitPoints(hitPoints, maxHitPoints, decrease);
            _world.Player.ManaCallback = (mana, maxMana) => _hud.SetMana(mana, maxMana);
            _world.DeathShown = _intro.ShowDeath;

            // The windows of controllers/menu.ts, drawn with the HUD; showing one hides the others.
            // #bubbles holds the quest arrows (pointer.ts) and the speech bubbles.
            _pointerView = new PointerView(_uiKit, _hud.Root);
            _bubbles = new BubbleView(_uiKit, _hud.Root);
            _world.ClearBubble = _bubbles.Clear;
            _world.CleanBubbles = _bubbles.Clean;
            _inventory = new InventoryView(_uiKit, _hud.Root);
            _profile = new ProfileView(_uiKit, _hud.Root, _world.Player);
            _quests = new QuestsView(_uiKit, _hud.Root, _world.Player);
            _questDialog = new QuestDialogView(_uiKit, _hud.Root, _world.Player);
            _achievements = new AchievementsView(_uiKit, _hud.Root, _world.Player);
            _store = new StoreView(_uiKit, _hud.Root, _inventory);
            _bank = new BankView(_uiKit, _hud.Root, _inventory);
            _lootBag = new LootBagView(_uiKit, _hud.Root, _inventory);
            _enchant = new EnchantView(_uiKit, _hud.Root, _inventory);
            _equipments = new EquipmentsView(_uiKit, _hud.Root, _world.Player, _world.Sprites.Get);
            _interact = new InteractView(_uiKit, _hud.Root, _world.Player);
            _friends = new FriendsView(_uiKit, _hud.Root, _world.Player, () => _world.Player.ServerId);
            _trade = new TradeView(_uiKit, _hud.Root, _inventory);
            _menus = new MenuPanel[]
            {
                _inventory, _profile, _quests, _questDialog, _achievements, _store, _bank, _lootBag, _enchant, _equipments, _interact,
                _friends, _trade, _guilds = new GuildsView(_uiKit, _hud.Root, _world.Player, () => _world.Player.ServerId),
                _leaderboards = new LeaderboardsView(_uiKit, _hud.Root, HubUrl)
            };
            _profile.GuildsRequested += _guilds.Toggle;
            // #leaderboards-button (the profile's; the HUD has none): only with a hub, which serves the lists.
            _profile.LeaderboardsRequested += _leaderboards.Toggle;
            if (HasHub) _profile.EnableLeaderboards();

            _warp = new WarpView(_uiKit, _hud.Root);
            _crafting = new CraftingView(_uiKit, _hud.Root, _world.Player);
            _settings = new SettingsView(_uiKit, _hud.Root, () => _world.Player.ServerId);
            _welcome = new WelcomeView(_uiKit, _hud.Root);
            _menus = _menus.Concat(new MenuPanel[] { _warp, _crafting, _settings, _welcome }).ToArray();
            foreach (var menu in new MenuPanel[] { _warp, _crafting, _settings, _welcome })
            {
                var shown = menu;
                menu.Showing = () =>
                {
                    foreach (var other in _menus)
                        if (other != shown && other.IsVisible) other.Hide();
                };
            }
            // controllers/menu.ts handleWarp, handleCraftingSelect, handleCraftingConfirm.
            _warp.Warp += id => Send(Packets.Warp, ("id", (double)id));
            _crafting.Selected += key => Send(Packets.Crafting, ("opcode", (double)(int)Opcodes.Crafting.Select), ("key", key));
            _crafting.Craft += (key, count) => Send(Packets.Crafting, ("opcode", (double)(int)Opcodes.Crafting.Craft), ("key", key), ("count", (double)count));
            _settings.Changed += key =>
            {
                ApplySettings();
                // settings.ts handleMusic and handleAudioEnabled.
                if (key == "settings.music" || key == "settings.audio") _audio.UpdateVolume();
            };
            // #logout-button: location.reload().
            _settings.Logout += () => SceneManager.LoadScene(gameObject.scene.name);
            CreateBrightness();
            ApplySettings();
            _guilds.Send += fields => Send(Packets.Guild, fields.ToArray());

            // controllers/menu.ts handleFriendConfirm, handleTradeSelect/Accept/Close; input.ts handleInteract.
            _friends.Confirmed += (username, remove) => Send(Packets.Friends,
                ("opcode", (double)(int)(remove ? Opcodes.Friends.Remove : Opcodes.Friends.Add)), ("username", username));
            _friends.Message += _hud.PrivateMessage;
            _friends.Notify += message => _hud.AddChat("WORLD", message, "#0dd9e0", true);
            _trade.Selected += (type, index, count) => Send(Packets.Trade,
                ("opcode", (double)(int)(type == (int)Modules.ContainerType.Inventory ? Opcodes.Trade.Add : Opcodes.Trade.Remove)),
                ("index", (double)index), ("count", count));
            _trade.Accepted += () => Send(Packets.Trade, ("opcode", (double)(int)Opcodes.Trade.Accept));
            _trade.Closed += () => Send(Packets.Trade, ("opcode", (double)(int)Opcodes.Trade.Close));
            _interact.Action += HandleInteract;

            // #equipment-button toggles the equipments window (which hides the profile); its slots unequip too.
            _profile.EquipmentRequested += _equipments.Toggle;
            _equipments.Unequip += type => Send(Packets.Equipment, ("opcode", (double)(int)Opcodes.Equipment.Unequip), ("type", (double)(int)type));

            // controllers/menu.ts handleStoreSelect, handleBankSelect, handleLootBagSelect, handleEnchantSelect/Confirm.
            _store.Selected += (opcode, key, index, count) => Send(Packets.Store,
                ("opcode", (double)(int)opcode), ("key", key), ("index", (double)index), ("count", count ?? 1));
            _bank.Selected += (fromContainer, fromIndex, toContainer, toIndex) => Send(Packets.Container,
                ("opcode", (double)(int)Opcodes.Container.Select), ("type", (double)(int)Modules.ContainerType.Bank),
                ("fromContainer", (double)fromContainer), ("fromIndex", (double)fromIndex), ("toContainer", (double)toContainer),
                ("value", toIndex.HasValue ? (double?)toIndex.Value : null));
            _lootBag.Take += index => Send(Packets.LootBag, ("opcode", (double)(int)Opcodes.LootBag.Take), ("index", (double)index));
            _enchant.Selected += index => Send(Packets.Enchant, ("opcode", (double)(int)Opcodes.Enchant.Select), ("index", (double)index));
            _enchant.Confirmed += (index, shard) => Send(Packets.Enchant,
                ("opcode", (double)(int)Opcodes.Enchant.Confirm), ("index", (double)index), ("shardIndex", (double)shard));
            foreach (var menu in _menus)
            {
                var shown = menu;
                menu.Showing = () =>
                {
                    foreach (var other in _menus)
                        if (other != shown && other.IsVisible) other.Hide();
                };
            }
            var buttons = new Dictionary<string, MenuPanel>
            {
                ["inventory"] = _inventory, ["profile"] = _profile, ["quests"] = _quests, ["achievements"] = _achievements,
                ["friends"] = _friends, ["warp"] = _warp, ["settings"] = _settings
            };
            foreach (var pair in buttons)
            {
                var id = pair.Key;
                pair.Value.ButtonActive = active => _hud.SetButtonActive(id, active);
            }
            _hud.ButtonClicked += id => buttons[id].Toggle();

            // controllers/menu.ts handleQuestAccept: Packets.Quest {key}.
            _questDialog.Accepted += key => _connection?.Send(Packets.Quest, new Dictionary<string, object> { ["key"] = key });

            // controllers/menu.ts handleInventorySelect, handleProfileUnequip, handleProfileAttackStyle.
            _inventory.Selected += (opcode, fromIndex, value) => _connection?.Send(Packets.Container, new Dictionary<string, object>
            {
                ["opcode"] = (double)(int)opcode, ["type"] = (double)(int)Modules.ContainerType.Inventory, ["fromIndex"] = (double)fromIndex,
                ["value"] = value
            });
            _profile.Unequip += type => _connection?.Send(Packets.Equipment, new Dictionary<string, object>
            {
                ["opcode"] = (double)(int)Opcodes.Equipment.Unequip, ["type"] = (double)(int)type
            });
            _profile.Style += style => _connection?.Send(Packets.Equipment, new Dictionary<string, object>
            {
                ["opcode"] = (double)(int)Opcodes.Equipment.Style, ["style"] = (double)(int)style
            });
        }

        /// <summary>app.ts loginCallback → game.ts connect: a new connection that logs in with the request.</summary>
        public void Login(LoginRequest request)
        {
            _connection?.Dispose();
            _connection = null;
            _errorShown = false;
            var attempt = ++_loginAttempt;

            if (!HasHub)
            {
                Connect(ServerUrl, request);
                return;
            }

            // socket.ts connect: the world picked in the world select (app.ts selectedServer), else the hub's /server.
            var selected = _intro?.SelectedServer;
            if (selected != null)
            {
                Connect(HubRequests.ServerUrl(selected, ServerUrl), request);
                return;
            }

            // socket.ts getServer: a failed request or a body that is not JSON gives undefined (the config's server).
            HubRequests.Get($"{HubUrl}/server", text =>
            {
                if (this == null || attempt != _loginAttempt) return;
                var server = HubRequests.Json(text, out var ok);
                Connect(HubRequests.ServerUrl(ok ? server : null, ServerUrl), request);
            });
        }

        int _loginAttempt;

        void Connect(string url, LoginRequest request)
        {
            var connection = new GameConnection(WebSocketTransportFactory.Create());
            _connection = connection;
            connection.StatusChanged += status => Status = status;
            connection.ConnectionFailed += message =>
            {
                if (_connection != connection) return;
                Status = $"Couldn't connect to {url} ({message})";
                ShowError($"Couldn't connect to {url}");
            };
            connection.Rejected += (reason, message) =>
            {
                if (_connection != connection) return;
                Status = message;
                ShowError(message);
            };
            connection.Disconnected += (code, reason) =>
            {
                // A connection replaced by a new login attempt is not a disconnection.
                if (_connection != connection) return;
                Status = $"Disconnected ({code} {reason})";
                if (_entered)
                {
                    // game.ts handleDisconnection: location.reload().
                    _pendingError = _errorShown ? _pendingError : CloseReasons.Describe("lost");
                    SceneManager.LoadScene(gameObject.scene.name);
                }
                else if (!_errorShown) ShowError(CloseReasons.Describe("lost"));
            };
            connection.PacketReceived += HandlePacket;
            connection.Connect(url, request);
        }

        void ShowError(string englishMessage)
        {
            _errorShown = true;
            if (_entered) _pendingError = englishMessage;
            else _intro.ShowError(englishMessage);
        }

        void HandlePacket(Packet packet)
        {
            if (packet.Id == Packets.Map)
            {
                _map.LoadRegions(MapPayload.Decode((string)packet.Arg(0)));
                return;
            }

            if (Array.IndexOf(EntityPackets.Handled, packet.Id) >= 0)
            {
                try
                {
                    _packets.Handle(packet.Id, packet.Args);
                }
                catch (Exception e)
                {
                    // The browser's message handler throws too; the session carries on.
                    UnityEngine.Debug.LogException(e);
                }
            }

            switch (packet.Id)
            {
                // connection.ts handleWelcome → app.fadeMenu.
                case Packets.Welcome:
                    _entered = true;
                    _intro.FadeOut();
                    _hud.SetVisible(true);
                    // game.ts postLoad: a new player gets the welcome window.
                    if (PlayerPrefs.GetInt(WelcomedKey, 0) == 0)
                    {
                        PlayerPrefs.SetInt(WelcomedKey, 1);
                        _welcome.Show();
                    }
                    break;
                case Packets.Chat:
                    HandleChat(packet.Arg(0) as Dictionary<string, object>);
                    break;
                case Packets.Quest:
                    // connection.ts handleQuest: Start opens the dialog; everything else goes to the quest list.
                    if (packet.Arg(0) is double questOpcode)
                    {
                        var key = Str(packet.Arg(1) as Dictionary<string, object>, "key");
                        if ((int)questOpcode == (int)Opcodes.Quest.Start) _questDialog.Handle(key);
                        else _quests.Handle((Opcodes.Quest)(int)questOpcode, key);
                    }
                    break;
                case Packets.Achievement:
                    if (packet.Arg(0) is double achievementOpcode)
                        _achievements.Handle((Opcodes.Achievement)(int)achievementOpcode, Str(packet.Arg(1) as Dictionary<string, object>, "key"));
                    break;
                case Packets.Friends:
                    if (packet.Arg(0) is double friendsOpcode)
                    {
                        var info = packet.Arg(1) as Dictionary<string, object> ?? new Dictionary<string, object>();
                        _friends.Handle((Opcodes.Friends)(int)friendsOpcode, Str(info, "username"),
                            info.TryGetValue("status", out var status) && status is bool online && online,
                            info.TryGetValue("serverId", out var server) ? server as double? : null);
                    }
                    break;
                case Packets.Crafting:
                    if (packet.Arg(0) is double craftingOpcode)
                        _crafting.Handle((Opcodes.Crafting)(int)craftingOpcode, packet.Arg(1) as Dictionary<string, object> ?? new Dictionary<string, object>());
                    break;
                case Packets.Guild:
                    if (packet.Arg(0) is double guildOpcode)
                        _guilds.Handle((Opcodes.Guild)(int)guildOpcode, packet.Arg(1) as Dictionary<string, object> ?? new Dictionary<string, object>());
                    break;
                case Packets.Trade:
                    if (packet.Arg(0) is double tradeOpcode) HandleTrade((Opcodes.Trade)(int)tradeOpcode, packet.Arg(1) as Dictionary<string, object> ?? new Dictionary<string, object>());
                    break;
                case Packets.Store:
                    // connection.ts handleStore
                    if (packet.Arg(0) is double storeOpcode && packet.Arg(1) is Dictionary<string, object> store)
                        switch ((Opcodes.Store)(int)storeOpcode)
                        {
                            case Opcodes.Store.Open: _store.Open(store); break;
                            case Opcodes.Store.Update: _store.Update(store); break;
                            case Opcodes.Store.Select: _store.Move(store); break;
                        }
                    break;
                case Packets.LootBag:
                    if (packet.Arg(0) is double lootOpcode)
                        _lootBag.Handle((Opcodes.LootBag)(int)lootOpcode, packet.Arg(1) as Dictionary<string, object> ?? new Dictionary<string, object>());
                    break;
                case Packets.Enchant:
                    // connection.ts handleEnchant: Select moves a slot into the item or shard place.
                    if (packet.Arg(0) is double enchantOpcode && (int)enchantOpcode == (int)Opcodes.Enchant.Select &&
                        packet.Arg(1) is Dictionary<string, object> enchant)
                        _enchant.Move(enchant.TryGetValue("index", out var i) && i is double d ? (int)d : -1,
                            enchant.TryGetValue("isShard", out var shard) && shard is bool s && s);
                    break;
                case Packets.Container:
                    HandleContainer(packet.Arg(0) is double containerOpcode ? (int)containerOpcode : -1, packet.Arg(1) as Dictionary<string, object>);
                    break;
                case Packets.Bubble:
                    HandleBubble(packet.Arg(0) is double bubbleOpcode ? (int)bubbleOpcode : -1, packet.Arg(1) as Dictionary<string, object>);
                    break;
                case Packets.NPC:
                    // connection.ts handleNPC: Talk, Bank and Enchant (Store opens through the Store packet).
                    if (packet.Arg(0) is double npcOpcode)
                        switch ((Opcodes.NPC)(int)npcOpcode)
                        {
                            case Opcodes.NPC.Talk:
                                HandleNpcTalk(packet.Arg(1) as Dictionary<string, object>);
                                break;
                            case Opcodes.NPC.Bank:
                                var slots = (packet.Arg(1) as Dictionary<string, object>)?.TryGetValue("slots", out var list) == true ? list as List<object> : null;
                                _bank.Open((slots ?? new List<object>()).Cast<Dictionary<string, object>>().Select(SlotData.Parse));
                                break;
                            case Opcodes.NPC.Enchant:
                                _enchant.Show();
                                break;
                        }
                    break;
                case Packets.Interface:
                    // connection.ts handleInterface (the original server never sends it).
                    if (packet.Arg(0) is double interfaceOpcode && packet.Arg(1) is Dictionary<string, object> interfaceInfo &&
                        interfaceInfo.TryGetValue("identifier", out var identifier) && identifier is double id &&
                        MenuFor((Modules.Interfaces)(int)id) is MenuPanel menu)
                    {
                        if ((Opcodes.Interface)(int)interfaceOpcode == Opcodes.Interface.Open) menu.Show();
                        else if ((Opcodes.Interface)(int)interfaceOpcode == Opcodes.Interface.Close) menu.Hide();
                    }
                    break;
                case Packets.Music:
                    // connection.ts handleMusic.
                    _audio.PlayMusic(packet.Arg(0) as string);
                    break;
                case Packets.Notification:
                    HandleNotification(packet.Arg(0) is double opcode ? (int)opcode : -1, packet.Arg(1) as Dictionary<string, object>);
                    break;
            }
        }

        static string Str(Dictionary<string, object> data, string key) =>
            data != null && data.TryGetValue(key, out var value) ? value as string : null;

        /// <summary>connection.ts handleChat (no bubbles or sounds yet).</summary>
        void HandleChat(Dictionary<string, object> info)
        {
            if (info == null) return;
            var message = MessageFormat.ParseMessage(Str(info, "message") ?? "undefined");
            var colour = Str(info, "colour") ?? "";

            // Messages with a source are static, added to the chat box directly.
            var source = Str(info, "source");
            if (!string.IsNullOrEmpty(source))
            {
                _hud.AddChat(source, message, colour, true);
                return;
            }

            if (!(_world.Entities.Get(Str(info, "instance")) is Player entity)) return;

            var name = entity.Name ?? "undefined";
            if (entity.Rank != (int)Modules.Ranks.None)
            {
                var titles = (Dictionary<string, object>)ModuleTables.RankTitles;
                var title = entity.Rank is double rank && titles.TryGetValue(JsValue.NumberToString(rank), out var t) ? (string)t : "undefined";
                name = $"[{title}] {name}";
            }

            _hud.AddChat(name, message, colour);

            // Draw the bubble (no sound yet).
            if (info.TryGetValue("withBubble", out var bubble) && bubble is bool withBubble && withBubble)
                _bubbles.Create(entity.Instance, message, _clock.Now);
        }

        /// <summary>connection.ts handleTrade.</summary>
        void HandleTrade(Opcodes.Trade opcode, Dictionary<string, object> info)
        {
            double Number(string key) => info.TryGetValue(key, out var value) && value is double d ? d : double.NaN;
            var mine = Str(info, "instance") == _world.Player.Instance;

            switch (opcode)
            {
                case Opcodes.Trade.Open:
                    if (_world.Entities.Get(Str(info, "instance")) is Player other) _trade.Open(_world.Player.Name, other.Name);
                    break;
                case Opcodes.Trade.Close:
                    _trade.Close(true);
                    break;
                case Opcodes.Trade.Add:
                    var count = Number("count");
                    _trade.Add((int)Number("index"), double.IsNaN(count) || count == 0 ? 0 : count, Str(info, "key"), !mine);
                    break;
                case Opcodes.Trade.Remove:
                    _trade.Remove((int)Number("index"), !mine);
                    break;
                case Opcodes.Trade.Accept:
                    _trade.Accept(MessageFormat.ParseMessage(MessageFormat.FormatNotification(Str(info, "message") ?? "undefined")));
                    break;
            }
        }

        /// <summary>connection.ts handleContainer, for the inventory (the bank comes later).</summary>
        void HandleContainer(int opcode, Dictionary<string, object> info)
        {
            if (info == null) return;
            // info.type === Inventory ? the inventory : the bank.
            var inventory = info.TryGetValue("type", out var type) && type is double t && (int)t == (int)Modules.ContainerType.Inventory;

            switch ((Opcodes.Container)opcode)
            {
                case Opcodes.Container.Batch:
                    var data = info["data"] as Dictionary<string, object>;
                    var slots = ((List<object>)data["slots"]).Cast<Dictionary<string, object>>().Select(SlotData.Parse).ToList();
                    if (inventory) _inventory.Batch(slots);
                    else
                        foreach (var slot in slots.Where(s => !string.IsNullOrEmpty(s.Key))) _bank.SetSlot(slot);
                    // inventory.ts batchCallback: the bank refreshes its copy.
                    if (inventory) _bank.Synchronize();
                    break;
                case Opcodes.Container.Add:
                case Opcodes.Container.Remove:
                    var changed = SlotData.Parse((Dictionary<string, object>)info["slot"]);
                    if (inventory) _inventory.SetSlot(changed);
                    else _bank.SetSlot(changed);
                    break;
            }

            SynchronizeMenus();
        }

        /// <summary>The keys of npc.en.json: dialogue the server sends as English text.</summary>
        const string NpcLines = "npc.";

        /// <summary>connection.ts handleBubble: over an entity, or at a tile when the bubble has no entity.</summary>
        void HandleBubble(int opcode, Dictionary<string, object> info)
        {
            if (info == null) return;
            var instance = Str(info, "instance");
            // Signs and boss lines come in English (npc.en.json).
            var text = Localization.TranslateEnglish(Str(info, "text"), NpcLines);
            if (string.IsNullOrEmpty(text))
            {
                _bubbles.Clear(instance);
                return;
            }

            var entity = _world.Entities.Get(instance);
            if (opcode == (int)Opcodes.Bubble.Entity && entity == null) return;

            (double, double)? position = null;
            if (entity == null)
                position = ((info.TryGetValue("x", out var x) && x is double px ? px : double.NaN) * _world.TileSize,
                    (info.TryGetValue("y", out var y) && y is double py ? py : double.NaN) * _world.TileSize);
            var duration = info.TryGetValue("duration", out var d) && d is double ms ? ms : 5000;
            _bubbles.Create(instance, text, _clock.Now, duration, position);
        }

        /// <summary>connection.ts handleNPC, Talk: the NPC's line in a bubble; no text ends the conversation.</summary>
        void HandleNpcTalk(Dictionary<string, object> info)
        {
            if (info == null || !(_world.Entities.Get(Str(info, "instance")) is Entity npc)) return;

            // The server sends the data's English line (npc.en.json).
            var text = Localization.TranslateEnglish(Str(info, "text"), NpcLines);
            // Which sound effect to play depending on if conversation is over or not.
            _audio.PlaySound(string.IsNullOrEmpty(text) ? "npc-end" : "npc", npc);

            _world.Player.DisableAction = true;

            if (string.IsNullOrEmpty(text))
            {
                _bubbles.Clear(npc.Instance);
                return;
            }

            _bubbles.Create(npc.Instance, text, _clock.Now);
        }

        /// <summary>connection.ts handleNotification: Text goes to the chat, Popup to the bottom notification.</summary>
        void HandleNotification(int opcode, Dictionary<string, object> info)
        {
            if (info == null) return;
            var message = Str(info, "message") ?? "undefined";
            var source = Str(info, "source");

            switch ((Opcodes.Notification)opcode)
            {
                case Opcodes.Notification.Text:
                    var text = !string.IsNullOrEmpty(source) ? message : MessageFormat.FormatNotification(message);
                    if (source == "TRADE") text = MessageFormat.FormatNotification(text);
                    _hud.AddChat(string.IsNullOrEmpty(source) ? "WORLD" : source, MessageFormat.ParseMessage(text), Str(info, "colour") ?? "", true);
                    break;
                case Opcodes.Notification.Popup:
                    var soundEffect = Str(info, "soundEffect");
                    if (!string.IsNullOrEmpty(soundEffect)) _audio.PlaySound(soundEffect, null);
                    _hud.ShowNotification(
                        MessageFormat.ParseMessage(MessageFormat.FormatNotification(Str(info, "title") ?? "undefined")),
                        MessageFormat.ParseMessage(MessageFormat.FormatNotification(message)),
                        Str(info, "colour"));
                    break;
            }
        }

        /// <summary>
        /// socket.send with an object literal: JSON.stringify leaves out the properties whose value is undefined
        /// (null here), keeping the others in order.
        /// </summary>
        void Send(Packets packet, params (string key, object value)[] fields)
        {
            var data = new Dictionary<string, object>();
            foreach (var (key, value) in fields)
                if (value != null) data[key] = value;
            _connection?.Send(packet, data);
        }

        /// <summary>
        /// controllers/menu.ts get(identifier): the window with that Modules.Interfaces identifier. Show is called
        /// without the packet's data, which the original's menus ignore for Open as well (they read it only in their
        /// own packets).
        /// </summary>
        MenuPanel MenuFor(Modules.Interfaces identifier) => identifier switch
        {
            Modules.Interfaces.Achievements => _achievements,
            Modules.Interfaces.Bank => _bank,
            Modules.Interfaces.Crafting => _crafting,
            Modules.Interfaces.Enchant => _enchant,
            Modules.Interfaces.Equipments => _equipments,
            Modules.Interfaces.Friends => _friends,
            Modules.Interfaces.Guilds => _guilds,
            Modules.Interfaces.Inventory => _inventory,
            Modules.Interfaces.Lootbag => _lootBag,
            Modules.Interfaces.Quest => _questDialog,
            Modules.Interfaces.Quests => _quests,
            Modules.Interfaces.Settings => _settings,
            Modules.Interfaces.Store => _store,
            Modules.Interfaces.Trade => _trade,
            Modules.Interfaces.Warp => _warp,
            Modules.Interfaces.Welcome => _welcome,
            // The leaderboards are filled from the hub; without one the window stays closed.
            Modules.Interfaces.Leaderboards => HasHub ? _leaderboards : null,
            _ => null
        };

        const string WelcomedKey = "pixelhero.welcomed";

        /// <summary>
        /// renderer.ts setBrightness: a CSS brightness filter on the game canvases, drawn as black over them (under
        /// the UI) at 1 - level / 100.
        /// </summary>
        void CreateBrightness()
        {
            var host = new GameObject("brightness", typeof(RectTransform));
            var canvas = host.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceCamera;
            canvas.worldCamera = ViewCamera;
            canvas.planeDistance = 1;
            canvas.sortingOrder = (TextSortingOrder + UiSortingOrder) / 2;
            _brightness = UiKit.Fill(UiKit.Box(host.transform, "filter")).gameObject.AddComponent<Image>();
            _brightness.raycastTarget = false;
        }

        /// <summary>settings.ts load / handlers: what the stored settings change in the client.</summary>
        public void ApplySettings()
        {
            _brightness.color = new Color(0, 0, 0, 1 - Mathf.Clamp(GameSettings.Brightness, 0, 100) / 100f);
            _textPlanner.DrawNames = GameSettings.ShowNames;
            _textPlanner.DrawLevels = GameSettings.ShowLevels;
            // handleFpsThrottle: off, 50 or 30 frames a second.
            Application.targetFrameRate = GameSettings.FpsThrottle switch { 1 => 50, 2 => 30, _ => -1 };
            // handleLowPower: the camera stops following the player.
            if (GameSettings.LowPowerMode) _camera.Decenter();
            else if (!_camera.IsCentered())
            {
                _camera.Center();
                if (_world.Player.Instance != "") _camera.CentreOn(_world.Player);
            }
        }

        /// <summary>controllers/menu.ts synchronize: every window that shows the inventory refreshes its copy.</summary>
        void SynchronizeMenus()
        {
            _store.Synchronize();
            _bank.Synchronize();
            _lootBag.Synchronize();
            _enchant.Synchronize();
            _trade.Synchronize();
        }

        /// <summary>input.ts handleInteract.</summary>
        void HandleInteract(string action, Entity entity)
        {
            switch (action)
            {
                case Modules.MenuActions.Attack:
                    _world.Input.Click(new WorldPosition { X = entity.X, Y = entity.Y, GridX = entity.GridX, GridY = entity.GridY });
                    break;
                case Modules.MenuActions.Trade:
                    _world.Player.Trade(entity);
                    break;
                case Modules.MenuActions.Follow:
                    if (entity is Character character) _world.Player.Pursue(character);
                    break;
                case Modules.MenuActions.Examine:
                    _connection?.Send(Packets.Examine, new List<object> { entity.Instance });
                    break;
                case Modules.MenuActions.AddFriend:
                    Send(Packets.Friends, ("opcode", (double)(int)Opcodes.Friends.Add), ("username", entity.Name));
                    break;
            }
        }

        /// <summary>game.ts respawn: hide the death scroll and ask the server (Packets.Respawn []).</summary>
        public void Respawn()
        {
            // game.ts respawn.
            _audio.PlaySound("revive", null);
            _intro.HideDeath();
            _connection?.Send(Packets.Respawn, new List<object>());
        }

        /// <summary>The entity canvases and the camera, sized to the screen.</summary>
        void CreateScreen()
        {
            _screenWidth = Screen.width;
            _screenHeight = Screen.height;

            _camera = new Entities.Camera(_map.Width, _map.Height, _map.Info.TileSize, _screenWidth, _screenHeight) { ZoomFactor = Zoom };
            _camera.Update();
            if (_world.Player.Instance != "") _camera.CentreOn(_world.Player);
            _world.Camera = _camera;

            _planner = new EntityDrawPlanner(_world, _camera);
            _textPlanner = new TextDrawPlanner(_world, _camera);
            _textPlanner.CanvasWidth = _screenWidth;
            _planner.TextCall = _textPlanner.Call;
            _camera.ZoomCallback = _textPlanner.UpdateFontSize;
            _canvases?.Dispose();
            _canvases = new EntityCanvasRenderer(Catalog, _screenWidth, _screenHeight);
            _overlayCanvas?.Dispose();
            _overlayCanvas = new OverlayCanvasRenderer(Catalog, _screenWidth, _screenHeight);
            _world.Lights.CanvasWidth = _screenWidth;
            _world.Lights.CanvasHeight = _screenHeight;
            _world.Pointers.CanvasWidth = _screenWidth;
            _world.Pointers.CanvasHeight = _screenHeight;
            // game.ts resize: pointer.resize.
            _world.Pointers.Resize();

            _entitiesLayer = Layer(_entitiesLayer, "#entities", _canvases.Entities, EntitiesSortingOrder, 1);
            _foreLayer = Layer(_foreLayer, "#entities-fore", _canvases.Fore, ForeSortingOrder, 1);
            _maskLayer = Layer(_maskLayer, "#entities-mask", _canvases.Entities, MaskSortingOrder, 0.2f);
            _overlayLayer = Layer(_overlayLayer, "#overlay", _overlayCanvas.Overlay, OverlaySortingOrder, 1);

            _textView?.Dispose();
            _textView = new TextCanvasView(Catalog, ViewCamera, TextSortingOrder, _screenWidth, _screenHeight);

            _mapView.ForceRedraw();
        }

        MeshRenderer Layer(MeshRenderer existing, string name, RenderTexture texture, int sortingOrder, float opacity)
        {
            var renderer = existing;
            if (renderer == null)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = name;
                Destroy(quad.GetComponent<Collider>());
                quad.transform.SetParent(ViewCamera.transform, false);
                renderer = quad.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = new Material(Shader.Find("PixelHero/CanvasLayer"));
            }

            renderer.sharedMaterial.mainTexture = texture;
            renderer.sharedMaterial.SetFloat("_Alpha", opacity);
            renderer.sortingOrder = sortingOrder;
            return renderer;
        }

        void Update()
        {
            if (Screen.width != _screenWidth || Screen.height != _screenHeight)
            {
                CreateScreen();
                _uiScaler.scaleFactor = UiKit.ScaleFor(_screenWidth, _screenHeight);
            }

            _intro.Update(Time.unscaledDeltaTime, ((RectTransform)_uiCanvas.transform).rect.size);
            _hud.Update(Time.unscaledDeltaTime, _world.Player.Poison);
            foreach (var menu in _menus) menu.Update(Time.unscaledDeltaTime);

            // game.ts tick: timers and network events first, then updater.update and renderer.render.
            _world.Timers.RunDue();
            _connection?.Update();
            HandleInput();

            _world.Time = _clock.Now;
            if (!_world.Player.Dead)
            {
                _packets.UpdateEntities();
                _packets.UpdateKeyboard();
                _packets.UpdateAnimations();
                _packets.UpdateInfos();
                // updater.ts updateBubbles: pointer.update.
                _world.Pointers.Update();
                _world.Lights.UpdateLights(_world.Time);
            }

            // renderer.ts render: drawOverlays, then drawEntities (with names and health bars on the text canvas), then drawInfos.
            _overlayCanvas.Render(_world.Lights);
            _textPlanner.BeginFrame();
            _canvases.Render(_planner.Draw());
            _textPlanner.DrawInfos();
            _textPlanner.DrawMinigameGUI();
            _textPlanner.EndFrame();
            _textView.Render(_textPlanner.Draws);
            _mapView.SetWindow(_camera.GridX, _camera.GridY, _camera.GridWidth, _camera.GridHeight);
            _mapView.Tick();
            // updater.ts updateBubbles (game.time).
            _bubbles.Update(_world.Time, _world, _camera, _uiScaler.scaleFactor);
            _pointerView.Update(_world.Pointers.All, _uiScaler.scaleFactor, Time.unscaledDeltaTime);
            PlaceView();
        }

        /// <summary>input.ts: a left click moves (or targets), WASD/arrows walk tile by tile.</summary>
        void HandleInput()
        {
            // Nothing to move before Welcome has given the player a sprite, nor while the menu is up.
            if (_world.Player.Sprite == null || _intro.Visible) return;

            // input.ts handleKeyDown: keys go to the chat while its input is open, or to the drop dialog; Enter opens the chat.
            var keys = Keyboard.current;
            if (_friends.PopupActive)
            {
                // friends.ts keyDown.
                if (keys != null && keys.escapeKey.wasPressedThisFrame) _friends.HidePopup();
                else if (keys != null && (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame)) _friends.HandleConfirm();
                return;
            }
            if (_trade.CountDialog.Visible)
            {
                if (keys != null && (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame)) _trade.CountDialog.Accept();
                else if (keys != null && keys.escapeKey.wasPressedThisFrame) _trade.CountDialog.Hide();
                return;
            }
            if (_store.BuyDialog.Visible)
            {
                // store.ts keyDown.
                if (keys != null && (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame)) _store.BuyDialog.Accept();
                else if (keys != null && keys.escapeKey.wasPressedThisFrame) _store.BuyDialog.Hide();
                return;
            }
            if (_inventory.Actions.DropDialogVisible)
            {
                if (keys != null && (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame)) _inventory.Actions.HandleDrop();
                else if (keys != null && keys.escapeKey.wasPressedThisFrame) _inventory.Actions.HideDropDialog();
                return;
            }
            // input.ts handleKeyDown: key presses go to leaderboards.ts keyDown while the leaderboards are open (before
            // the chat); key releases still reach the player.
            var leaderboardsKeys = _leaderboards.IsVisible;
            if (leaderboardsKeys)
            {
                if (keys != null) _leaderboards.KeyDown(keys.escapeKey.wasPressedThisFrame);
            }
            else if (_hud.ChatInputVisible)
            {
                if (keys != null) _hud.ChatKey(keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame, keys.escapeKey.wasPressedThisFrame);
                return;
            }
            if (!leaderboardsKeys && keys != null && (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame))
            {
                // guilds.ts keyDown takes Enter while the guilds window is open.
                if (_guilds.IsVisible) _guilds.KeyDown(true);
                else _hud.ToggleChat();
            }

            // A click on the HUD (buttons, chat) is not a click on the map.
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;

            // input.ts handleRightClick: the interact menu for the entity under the pointer.
            var pointer = Mouse.current;
            if (pointer != null && pointer.rightButton.wasPressedThisFrame)
            {
                var screen = pointer.position.ReadValue();
                var canvasX = Mathf.Clamp(screen.x, 0, _screenWidth - 1);
                var canvasY = Mathf.Clamp(_screenHeight - screen.y, 0, _screenHeight - 1);
                var entity = _world.SearchForEntityAt(_world.Input.GetCoords(Math.Floor(canvasX), Math.Floor(canvasY)));
                if (entity == null || entity.Instance == _world.Player.Instance) _interact.Hide();
                else
                {
                    var scale = _uiScaler.scaleFactor;
                    _interact.Show(new Vector2(canvasX / scale, canvasY / scale), new Vector2(_screenWidth / scale, _screenHeight / scale), entity, _world.Pvp);
                }
            }

            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                var position = mouse.position.ReadValue();
                // Canvas pixels: origin top-left.
                var x = Mathf.Clamp(position.x, 0, _screenWidth - 1);
                var y = Mathf.Clamp(_screenHeight - position.y, 0, _screenHeight - 1);
                _world.Input.Click(_world.Input.GetCoords(Math.Floor(x), Math.Floor(y)));
            }

            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            Key(keyboard.wKey, keyboard.upArrowKey, Modules.Orientation.Up, !leaderboardsKeys);
            Key(keyboard.aKey, keyboard.leftArrowKey, Modules.Orientation.Left, !leaderboardsKeys);
            Key(keyboard.sKey, keyboard.downArrowKey, Modules.Orientation.Down, !leaderboardsKeys);
            Key(keyboard.dKey, keyboard.rightArrowKey, Modules.Orientation.Right, !leaderboardsKeys);
        }

        void Key(UnityEngine.InputSystem.Controls.KeyControl key, UnityEngine.InputSystem.Controls.KeyControl arrow, Modules.Orientation direction,
            bool presses = true)
        {
            if (presses && (key.wasPressedThisFrame || arrow.wasPressedThisFrame)) _world.Input.SetMovementKey(direction, true);
            if (key.wasReleasedThisFrame || arrow.wasReleasedThisFrame) _world.Input.SetMovementKey(direction, false);
        }

        /// <summary>
        /// Puts the Unity camera where the canvas view is: entities are drawn at ~~(x × zoom) + ~~(-camera.x × zoom)
        /// screen pixels, tiles one pixel further right and down.
        /// </summary>
        void PlaceView()
        {
            var unit = _map.Info.TileSize * _camera.ZoomFactor; // screen pixels per tile
            var translateX = JsValue.ToInt32(-_camera.X * _camera.ZoomFactor);
            var translateY = JsValue.ToInt32(-_camera.Y * _camera.ZoomFactor);

            ViewCamera.orthographic = true;
            ViewCamera.orthographicSize = (float)(_screenHeight / (2 * unit));
            ViewCamera.transform.position = new Vector3(
                (float)((_screenWidth / 2.0 - translateX) / unit),
                (float)(-(_screenHeight / 2.0 - translateY) / unit),
                -10);

            _mapView.transform.position = new Vector3((float)(1 / unit), (float)(-1 / unit), 0);

            var width = (float)(_screenWidth / unit);
            var height = (float)(_screenHeight / unit);
            foreach (var layer in new[] { _entitiesLayer, _foreLayer, _maskLayer, _overlayLayer })
            {
                layer.transform.localPosition = new Vector3(0, 0, 5);
                layer.transform.localScale = new Vector3(width, height, 1);
            }
        }

        void OnDestroy()
        {
            _connection?.Dispose();
            _canvases?.Dispose();
            _overlayCanvas?.Dispose();
            _textView?.Dispose();
            _intro?.Dispose();
        }
    }
}
