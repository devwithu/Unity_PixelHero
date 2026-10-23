using System.Net;
using System.Text;
using PixelHero.Js;
using PixelHero.Server.Common;
using PixelHero.Server.Js;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PixelHero.Server.Network
{
    /// <summary>An Express request as the original's handlers read it: the parsed query and body.</summary>
    public sealed class ExpressRequest
    {
        public string method;
        public string path;

        /// <summary>request.query (qs): a string per key, or a list of strings for a repeated key.</summary>
        public JsObject query = new();

        /// <summary>request.body after the body parsers: {} unless a parser read the body.</summary>
        public object body = new JsObject();

        /// <summary>The raw body (express.raw).</summary>
        public byte[] rawBody = Array.Empty<byte>();

        public string contentType;

        public Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An Express response: json(), send() and status(). Completes once; later calls are ignored.</summary>
    public sealed class ExpressResponse
    {
        internal readonly TaskCompletionSource<(int status, string type, string body)> Done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        int _status = 200;

        /// <summary>Called with the body when the response completes (the test harness's virtual network).</summary>
        public Action<string> Completed;

        public ExpressResponse Status(int status)
        {
            _status = status;
            return this;
        }

        /// <summary>response.json(value): JSON.stringify(value).</summary>
        public void Json(object value)
        {
            var body = JsJson.Stringify(value) ?? "";
            if (Done.TrySetResult((_status, "application/json; charset=utf-8", body))) Completed?.Invoke(body);
        }

        /// <summary>response.send(text) (text/html, like Express for a string) or response.send() (empty).</summary>
        public void Send(string text = null)
        {
            if (Done.TrySetResult((_status, text == null ? null : "text/html; charset=utf-8", text ?? ""))) Completed?.Invoke(text ?? "");
        }
    }

    /// <summary>
    /// The part of Express the original's APIs use (server network/api.ts, hub controllers/api.ts): GET and POST
    /// routes, express.urlencoded, express.json, cors(), JSON responses, Express's 404 page and its error handler.
    /// It listens with Kestrel; every handler runs on the event loop (<see cref="Node.Post"/>), as the original's run
    /// on Node's single thread.
    /// </summary>
    public sealed class Express
    {
        readonly List<(string method, string path, Action<ExpressRequest, ExpressResponse> handler)> _routes = new();

        /// <summary>app.use(cors()).</summary>
        public bool cors;

        /// <summary>Whether express.urlencoded and express.json run (both APIs use them).</summary>
        public bool bodyParsers = true;

        WebApplication _app;

        /// <summary>
        /// C# only: an https listener on <see cref="securePort"/> next to the plain one, with this certificate and its
        /// intermediates (TlsCertificate.LoadCertificate). Both serve the same routes. Off when either is unset.
        /// </summary>
        public double securePort;
        public System.Security.Cryptography.X509Certificates.X509Certificate2 certificate;
        public System.Security.Cryptography.X509Certificates.X509Certificate2Collection intermediates;

        /// <summary>
        /// The test harness's virtual network: when set, listen() registers the app there instead of opening a port and
        /// calls back at once (as the harness's Express stand-in for the original does).
        /// </summary>
        public static Action<double, Express> VirtualListen;

        /// <summary>The handler of a route (exact path), for the virtual network.</summary>
        public Action<ExpressRequest, ExpressResponse> Find(string method, string path) =>
            _routes.FirstOrDefault(route => route.method == method && route.path == path).handler;

        public Express Get(string path, Action<ExpressRequest, ExpressResponse> handler)
        {
            _routes.Add(("GET", path, handler));
            return this;
        }

        public Express Post(string path, Action<ExpressRequest, ExpressResponse> handler)
        {
            _routes.Add(("POST", path, handler));
            return this;
        }

        /// <summary>app.listen(port, callback): the callback runs on the event loop once the port is bound.</summary>
        public void Listen(double port, Action callback = null)
        {
            if (VirtualListen != null)
            {
                VirtualListen(port, this);
                callback?.Invoke();
                return;
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            // Express (Node's http) listens on every address, IPv6 and IPv4.
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP((int)port);
                options.AddServerHeader = false;

                if (certificate != null && securePort > 0)
                    options.ListenAnyIP((int)securePort, listen => listen.UseHttps(https =>
                    {
                        https.ServerCertificate = certificate;
                        https.ServerCertificateChain = intermediates;
                        https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                             System.Security.Authentication.SslProtocols.Tls13;
                    }));
            });

            _app = builder.Build();
            _app.Run(Handle);

            _app.StartAsync().ContinueWith(task =>
            {
                if (task.IsFaulted)
                    Post(() => throw new InvalidOperationException($"Failed to listen on port {JsNumber.ToJsString(port)}",
                        task.Exception?.GetBaseException()));
                else if (callback != null) Post(callback);
            });
        }

        public void Stop() => _app?.StopAsync().Wait(TimeSpan.FromSeconds(2));

        static void Post(Action action)
        {
            if (Node.Post == null) throw new InvalidOperationException("No event loop runs the HTTP handlers (Node.Post).");
            Node.Post(action);
        }

        async Task Handle(HttpContext context)
        {
            var request = new ExpressRequest
            {
                method = context.Request.Method,
                path = context.Request.Path.HasValue ? context.Request.Path.Value : "/",
                contentType = context.Request.ContentType
            };

            foreach (var header in context.Request.Headers) request.headers[header.Key] = header.Value.ToString();

            context.Response.Headers["X-Powered-By"] = "Express";

            if (cors)
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";

                // cors(): preflight requests end here with 204.
                if (request.method == "OPTIONS")
                {
                    context.Response.Headers["Access-Control-Allow-Methods"] = "GET,HEAD,PUT,PATCH,POST,DELETE";
                    if (request.headers.TryGetValue("Access-Control-Request-Headers", out var requested))
                    {
                        context.Response.Headers["Access-Control-Allow-Headers"] = requested;
                        context.Response.Headers.Append("Vary", "Access-Control-Request-Headers");
                    }
                    context.Response.StatusCode = 204;
                    context.Response.ContentLength = 0;
                    return;
                }
            }

            request.query = ParseQuery(context.Request.QueryString.HasValue ? context.Request.QueryString.Value.Substring(1) : "");

            using (var memory = new MemoryStream())
            {
                await context.Request.Body.CopyToAsync(memory);
                request.rawBody = memory.ToArray();
            }

            (int status, string type, string body) result;

            try
            {
                if (bodyParsers) ParseBody(request);
                result = await Dispatch(request);
            }
            catch (BodyParserError error)
            {
                result = (error.status, "text/html; charset=utf-8", ErrorPage(error.Message));
            }

            context.Response.StatusCode = result.status;
            if (result.type != null) context.Response.ContentType = result.type;

            var bytes = Encoding.UTF8.GetBytes(result.body);
            context.Response.ContentLength = bytes.Length;
            if (request.method != "HEAD") await context.Response.Body.WriteAsync(bytes);
        }

        Task<(int status, string type, string body)> Dispatch(ExpressRequest request)
        {
            var method = request.method == "HEAD" ? "GET" : request.method;
            var route = _routes.FirstOrDefault(r => r.method == method && SamePath(r.path, request.path));

            // Express's final handler.
            if (route.handler == null)
                return Task.FromResult((404, "text/html; charset=utf-8",
                    ErrorPage($"Cannot {request.method} {WebUtility.HtmlEncode(request.path)}")));

            var response = new ExpressResponse();

            Post(() =>
            {
                try
                {
                    route.handler(request, response);
                }
                catch (Exception error)
                {
                    // Express's error handler answers a synchronous throw with 500.
                    Log.Error(error.ToString());
                    response.Status(500);
                    response.Done.TrySetResult((500, "text/html; charset=utf-8",
                        ErrorPage(WebUtility.HtmlEncode($"{ErrorName(error)}: {error.Message}"))));
                }
            });

            return response.Done.Task;
        }

        static string ErrorName(Exception error) => error is JsTypeError ? "TypeError" : "Error";

        /// <summary>Express routes match case-insensitively and allow one trailing slash.</summary>
        static bool SamePath(string route, string path)
        {
            if (path.Length > 1 && path.EndsWith("/")) path = path.Substring(0, path.Length - 1);
            return string.Equals(route, path, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>finalhandler's page.</summary>
        static string ErrorPage(string message) =>
            "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>Error</title>\n</head>\n<body>\n<pre>" +
            message + "</pre>\n</body>\n</html>\n";

        sealed class BodyParserError : Exception
        {
            public readonly int status;

            public BodyParserError(int status, string message) : base(message) => this.status = status;
        }

        // body-parser's default limit (100kb).
        const int BodyLimit = 100 * 1024;

        /// <summary>express.urlencoded({extended: true}) then express.json(), each by content type.</summary>
        static void ParseBody(ExpressRequest request)
        {
            var type = (request.contentType ?? "").Split(';')[0].Trim().ToLowerInvariant();
            var parse = type == "application/x-www-form-urlencoded" || type == "application/json" || type.EndsWith("+json");

            if (!parse || request.rawBody.Length == 0) return;
            if (request.rawBody.Length > BodyLimit) throw new BodyParserError(413, "PayloadTooLargeError: request entity too large");

            var text = Encoding.UTF8.GetString(request.rawBody);

            if (type == "application/x-www-form-urlencoded")
            {
                request.body = ParseQuery(text);
                return;
            }

            // strict: only objects and arrays.
            var first = text.TrimStart(' ', '\t', '\n', '\r');
            if (first.Length > 0 && first[0] != '{' && first[0] != '[')
                throw new BodyParserError(400, WebUtility.HtmlEncode($"SyntaxError: Unexpected token {first[0]} in JSON at position 0"));

            try
            {
                request.body = JsData.Parse(text);
            }
            catch (Exception error)
            {
                throw new BodyParserError(400, WebUtility.HtmlEncode($"SyntaxError: {error.Message}"));
            }
        }

        /// <summary>qs.parse for flat keys: a string per key, a list for repeated keys ('+' is a space).</summary>
        public static JsObject ParseQuery(string text)
        {
            var result = new JsObject();
            if (string.IsNullOrEmpty(text)) return result;

            foreach (var part in text.Split('&'))
            {
                if (part.Length == 0) continue;

                var equals = part.IndexOf('=');
                var key = Decode(equals == -1 ? part : part.Substring(0, equals));
                var value = equals == -1 ? "" : Decode(part.Substring(equals + 1));

                if (key.Length == 0) continue;

                switch (result[key])
                {
                    case null:
                        result.Set(key, value);
                        break;
                    case List<object> list:
                        list.Add(value);
                        break;
                    case var existing:
                        result.Set(key, new List<object> { existing, value });
                        break;
                }
            }

            return result;
        }

        static string Decode(string text)
        {
            try
            {
                return Uri.UnescapeDataString(text.Replace('+', ' '));
            }
            catch
            {
                return text;
            }
        }
    }
}
