using PixelHero.Hub.Controllers;
using PixelHero.Hub.Network;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Js;

namespace PixelHero.Hub
{
    // Discord.Net's root namespace is also called Discord.
    using Discord = PixelHero.Server.Network.Discord;

    /// <summary>main.ts: the hub. Game servers connect to its WebSocket; players and websites use its HTTP API.</summary>
    public sealed class Main
    {
        readonly Cache cache;
        readonly Handler handler; // The network handler.
        readonly Models models = new();
        readonly Discord discord;
        readonly Mailer mailer;

        /// <param name="store">The database's store, or null when there is none.</param>
        /// <param name="mobs">server/data/mobs.json (the leaderboards' mob list).</param>
        public Main(IDocumentStore store, JsObject mobs)
        {
            // Field initializers, in the original's order.
            cache = new Cache(store, mobs);
            handler = new Handler();
            discord = new Discord();
            mailer = new Mailer();

            Log.Notice($"Initializing {Config.Current.name} Hub {Config.Current.gver}.");

            // Relay global messages from the Discord server to all servers.
            discord.OnMessage(models.Global);

            // Callbacks for the web socket server.
            handler.OnConnection(models.Connect);
            handler.OnDisconnect(models.Remove);

            // Callbacks for the server handler.
            models.OnAdd(HandleAdd);
            models.OnRemove(HandleRemove);
            models.OnPlayer(HandlePlayer);
            models.OnMessage((source, message, serverName, withArrow) => discord.SendMessage(source, message, serverName, withArrow));

            if (handler.ready) Log.Notice($"Hub is now listening on port: {JsString.Of(Config.Current.hubWsPort)}.");

            _ = new Console(models);
            _ = new Api(models, mailer, cache, mobs);
        }

        /// <summary>Callback handler for when a new server is added.</summary>
        void HandleAdd(double id, string name)
        {
            Log.Notice($"{name} {JsString.Of(id)} has come online!");

            discord.SendRawMessage($":white_check_mark: **{name} {JsString.Of(id)} is now online!**");
        }

        /// <summary>Callback handler for when a server times out.</summary>
        void HandleRemove(double id, string name)
        {
            Log.Notice($"{name} {JsString.Of(id)} has disconnected.");

            discord.SendRawMessage($":octagonal_sign: **{name} {JsString.Of(id)} has gone offline!**");
        }

        /// <summary>
        /// Callback for when a player logs out or logs into any of the servers. We use this to send a message to the
        /// Discord bot and to update the population.
        /// </summary>
        void HandlePlayer(string username, double serverId, bool logout, double population)
        {
            discord.SendMessage(
                Utils.FormatName(username),
                logout ? "has logged out!" : "has logged in!",
                $"{Config.Current.name} {JsString.Of(serverId)}",
                false);

            // Update the population of the Discord server.
            discord.SetTopic($"Currently {JsString.Of(population)} player{(population == 1 ? "" : "s")} online.");
        }

        public void Stop() => handler.Stop();
    }
}
