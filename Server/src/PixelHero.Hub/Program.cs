// The C# PixelHero hub: a port of the original's packages/hub (main.ts). Game servers connect to its WebSocket
// (HUB_WS_PORT); clients and websites use its HTTP API (HUB_PORT, only with API_ENABLED or HUB_ENABLED, as in the
// original).
//
//   dotnet run --project Server/src/PixelHero.Hub -- [--hubEnabled true] [--<configKey> value ...]
//
// Configuration is read like the game server's: PixelHero-Data/.env.defaults with PixelHero-Data/.env over it, then the
// command line (camelCase keys as in common/config.ts, or the .env names).
using PixelHero.Hub;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Js;
using PixelHero.Server.Network;

var root = FindRepositoryRoot();
var data = Path.Combine(root, "PixelHero-Data");

var overrides = new JsObject();
for (var i = 0; i + 1 < args.Length; i += 2)
{
    if (!args[i].StartsWith("--")) continue;
    var key = args[i].Substring(2);
    if (key.Contains('_') || key.ToUpperInvariant() == key) key = Config.CamelCase(key);
    overrides.Set(key, Config.ParseVariable(args[i + 1]));
}

var config = Config.Load(Path.Combine(data, ".env.defaults"), Path.Combine(data, ".env"), overrides);

Config.Current = config;
Utils.Reset();

if (config.debugging) Log.ConsoleLevel = Log.Level.Debug;

var mobs = JsData.ParseObject(File.ReadAllText(Path.Combine(data, "server", "data", "mobs.json")));

var runner = new WorldRunner();
runner.Start();

// database.ts: the hub always opens the configured database (MongoDB), even with SKIP_DATABASE.
IDocumentStore store = null;
if (config.database is "mongo" or "mongodb")
    store = new MongoDocumentStore(config.mongodbHost, config.mongodbPort, config.mongodbUser, config.mongodbPassword,
        config.mongodbDatabase, config.mongodbTls, config.mongodbSrv, config.mongodbAuthSource, runner.Post);
else
{
    Log.Error($"The database {config.database} could not be found.");
    Log.Error("[Database] No database is currently present. It is advised against proceeding in this state.");
}

// C# hub only: the HTTP API over https on HUB_SSL_PORT, with the game server's certificate settings.
if (config.hubSslPort > 0 && !string.IsNullOrEmpty(config.sslCertificate))
{
    string Resolve(string path) => string.IsNullOrEmpty(path) || Path.IsPathRooted(path) ? path : Path.Combine(root, path);

    try
    {
        PixelHero.Hub.Controllers.Api.Secure = TlsCertificate.LoadCertificate(Resolve(config.sslCertificate),
            Resolve(config.sslCertificateKey), config.sslCertificatePassword);
        var certificate = PixelHero.Hub.Controllers.Api.Secure.certificate;
        Log.Notice($"Hub https listener on port {JsString.Of(config.hubSslPort)} " +
            $"({certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)}, valid until {certificate.NotAfter:yyyy-MM-dd}).");
    }
    catch (Exception error)
    {
        Log.Warning($"The hub's https listener is off: {error.Message}");
    }
}

Main main = null;
runner.Invoke(() => main = new Main(store, mobs));

using var exit = new ManualResetEventSlim();
System.Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    exit.Set();
};

exit.Wait();
main.Stop();
runner.Dispose();

// The folder that holds PixelHero-Data (the repository root), above the current directory or the executable.
static string FindRepositoryRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "PixelHero-Data", "server", "data")))
                return dir.FullName;

    throw new DirectoryNotFoundException("PixelHero-Data was not found above " + Directory.GetCurrentDirectory());
}
