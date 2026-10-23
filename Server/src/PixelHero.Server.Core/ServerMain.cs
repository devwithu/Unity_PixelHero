using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Game;
using PixelHero.Server.Info;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

namespace PixelHero.Server
{
    /// <summary>
    /// server/src/main.ts: the socket handler, the database, the level table, then the world once the database
    /// connection settles. Construct it inside a macrotask of the running <see cref="Node.Loop"/>.
    /// </summary>
    public sealed class ServerMain
    {
        public World world;
        public readonly SocketHandler socketHandler;
        public readonly IGameDatabase database;

        bool ready;

        /// <param name="store">The document store, or null to run without a database (the connection fails).</param>
        public ServerMain(IDocumentStore store)
        {
            // Field initializers.
            socketHandler = new SocketHandler();
            database = new GameDatabase(store);

            Log.Info($"Initializing {Config.Current.name} game engine...");

            socketHandler.OnConnection(HandleConnection);

            database.OnReady(() => HandleReady());
            database.OnFail(HandleFail);

            Loader.Load();
        }

        void LoadWorld()
        {
            Log.Info($"************** {Config.Current.name} World **************");

            world = new World(socketHandler, database);
        }

        void HandleConnection(Connection connection)
        {
            if (!ready || !(world?.allowConnections ?? false))
            {
                connection.Reject("disallowed");
                return;
            }

            if (world?.IsFull() ?? false)
            {
                Log.Notice("The world is currently full, connections are being rejected.");
                connection.Reject("worldfull");
                return;
            }

            world?.connectionCallback?.Invoke(connection);
        }

        void HandleReady(bool withoutDatabase = false)
        {
            ready = true;

            LoadWorld();

            if (withoutDatabase) Log.Notice("Running without database - Server is now accepting connections.");

            Log.Notice($"Server is now listening on port: {PixelHero.Js.JsValue.NumberToString(Config.Current.port)}.");
        }

        void HandleFail(Exception error)
        {
            // Proceed with no database and allow connections.
            if (Config.Current.skipDatabase)
            {
                HandleReady(true);
                return;
            }

            Log.Critical("Could not connect to the MongoDB server.");
            Log.Critical($"Error: {error.Message}");

            Log.Info("Attempting to reconnect in 10 seconds...");

            Node.SetTimeout(() => database.CreateConnection(), 10_000);
        }

        /// <summary>Saves every player (the original's SIGINT handler, without exiting).</summary>
        public void SaveAll()
        {
            Log.Info("Saving all players and closing process...");

            world?.Save();
        }

        /// <summary>
        /// Resets the module-level state of the original (what a fresh process or a fresh harness import starts with)
        /// and loads the data. Call before constructing a <see cref="ServerMain"/>.
        /// </summary>
        public static void ResetState(string dataDirectory, Config config)
        {
            Config.Current = config;
            Utils.Reset();
            Formulas.Reset();
            ServerData.Load(dataDirectory);
        }
    }
}
