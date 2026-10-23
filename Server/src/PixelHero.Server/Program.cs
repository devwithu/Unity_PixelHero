// The C# PixelHero game server: a port of the original's packages/server (main.ts) on SuperSocket.
//
//   dotnet run --project Server/src/PixelHero.Server -- [--port 59011] [--skipDatabase true] [--<configKey> value ...]
//
// Configuration is read like the original's: PixelHero-Data/.env.defaults with PixelHero-Data/.env over it, then the
// command line overrides (camelCase keys as in common/config.ts, or the .env names). The data files are read from
// PixelHero-Data/server/data in place.
using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using PixelHero.Server;
using PixelHero.Server.Common;
using PixelHero.Server.Database;
using PixelHero.Server.Host;
using PixelHero.Server.Js;
using PixelHero.Server.Network;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuperSocket.Server.Host;
using SuperSocket.WebSocket;
using SuperSocket.WebSocket.Server;

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

ServerMain.ResetState(Path.Combine(data, "server", "data"), config);
Filter.Load(Path.Combine(data, "common", "text", "profanity.json"));
I18n.Load(Path.Combine(root, "Assets", "PixelHero", "Localization", "server.en.json"));

if (config.debugging) Log.ConsoleLevel = Log.Level.Debug;

var secure = SecureCertificate(config, root);

var runner = new WorldRunner();
runner.Start();

// database.ts: MongoDB unless SKIP_DATABASE (then there is no store and every player is an admin).
IDocumentStore store = config.skipDatabase
    ? null
    : new MongoDocumentStore(config.mongodbHost, config.mongodbPort, config.mongodbUser, config.mongodbPassword,
        config.mongodbDatabase, config.mongodbTls, config.mongodbSrv, config.mongodbAuthSource, runner.Post);

ServerMain main = null;
runner.Invoke(() => main = new ServerMain(store));

var sockets = new ConcurrentDictionary<string, SessionSocket>();

var host = WebSocketHostBuilder.Create(Array.Empty<string>())
    .UseSession<GameSession>()
    .UseSessionHandler(
        session =>
        {
            var socket = new SessionSocket((WebSocketSession)session, main.socketHandler.sockets);
            sockets[session.SessionID] = socket;
            runner.Post(() => main.socketHandler.sockets.HandleConnection(socket));
            return ValueTask.CompletedTask;
        },
        (session, _) =>
        {
            if (sockets.TryRemove(session.SessionID, out var socket))
                runner.Post(() =>
                {
                    // A close started by the server already ran the close handling inside end().
                    if (socket.Closed) return;
                    socket.Closed = true;
                    main.socketHandler.sockets.HandleClose(socket.Instance);
                });
            return ValueTask.CompletedTask;
        })
    .UseWebSocketMessageHandler((session, package) =>
    {
        // uWS hands every message over as a buffer that the server decodes as UTF-8, text or binary.
        var text = package.OpCode == OpCode.Binary ? Encoding.UTF8.GetString(package.Data.ToArray()) : package.Message;

        if (sockets.TryGetValue(session.SessionID, out var socket))
            runner.Post(() =>
            {
                if (!socket.Closed) main.socketHandler.sockets.HandleMessage(socket.Instance, text);
            });

        return ValueTask.CompletedTask;
    })
    .ConfigureSuperSocket<WebSocketPackage>(options =>
    {
        // The secure listener's certificate and its chain (Let's Encrypt's fullchain.pem sends the intermediate).
        if (secure == null) return;
        foreach (var listener in options.Listeners)
            if (listener.AuthenticationOptions != null && listener.Port == (int)config.sslPort)
                listener.AuthenticationOptions.ServerCertificateContext = secure;
    })
    .ConfigureAppConfiguration((_, builder) => builder.AddInMemoryCollection(ServerOptions(config, secure)))
    .ConfigureLogging((_, logging) => logging.SetMinimumLevel(LogLevel.Warning))
    .Build();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    runner.Invoke(() => main.SaveAll());
    Environment.Exit(0);
};

await host.RunAsync();
runner.Dispose();

// SuperSocket's options: the ws listener on PORT and, with a certificate, the wss listener on SSL_PORT (TLS 1.2 and
// 1.3; the certificate itself is attached in ConfigureSuperSocket). Both feed the same game; the second one is the C#
// server's own addition.
static Dictionary<string, string> ServerOptions(Config config, System.Net.Security.SslStreamCertificateContext secure)
{
    var options = new Dictionary<string, string>
    {
        ["serverOptions:name"] = config.name,
        ["serverOptions:listeners:0:ip"] = "Any",
        ["serverOptions:listeners:0:port"] = JsString.Of(config.port),
        // uWS maxPayloadLength: 32 MiB.
        ["serverOptions:maxPackageLength"] = (32 * 1024 * 1024).ToString()
    };

    if (secure == null) return options;

    options["serverOptions:listeners:1:ip"] = "Any";
    options["serverOptions:listeners:1:port"] = JsString.Of(config.sslPort);
    options["serverOptions:listeners:1:authenticationOptions:enabledSslProtocols"] = "Tls12, Tls13";
    return options;
}

// The wss listener's certificate (SSL_CERTIFICATE, SSL_CERTIFICATE_KEY, SSL_CERTIFICATE_PASSWORD), or null when the
// listener is off or the certificate cannot be loaded (the server then serves ws only).
static System.Net.Security.SslStreamCertificateContext SecureCertificate(Config config, string root)
{
    if (config.sslPort <= 0 || string.IsNullOrEmpty(config.sslCertificate)) return null;

    string Resolve(string path) => string.IsNullOrEmpty(path) || Path.IsPathRooted(path) ? path : Path.Combine(root, path);

    try
    {
        var context = TlsCertificate.Load(Resolve(config.sslCertificate), Resolve(config.sslCertificateKey), config.sslCertificatePassword);
        var certificate = context.TargetCertificate;
        Log.Notice($"Secure WebSocket (wss) listener on port: {JsString.Of(config.sslPort)} ({certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false)}, " +
            $"{context.IntermediateCertificates.Count} intermediate(s), valid until {certificate.NotAfter:yyyy-MM-dd}).");
        return context;
    }
    catch (Exception error)
    {
        Log.Warning($"The secure WebSocket listener is off: {error.Message}");
        return null;
    }
}

// The folder that holds PixelHero-Data (the repository root), above the current directory or the executable.
static string FindRepositoryRoot()
{
    foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "PixelHero-Data", "server", "data")))
                return dir.FullName;

    throw new DirectoryNotFoundException("PixelHero-Data was not found above " + Directory.GetCurrentDirectory());
}
