using System;
using System.Collections.Generic;
using System.Text;
using PixelHero.Js;
using UnityEngine.Networking;

namespace PixelHero.Network
{
    /// <summary>
    /// The client's HTTP calls to the hub (the original's fetch in app.ts, reset.ts, network/socket.ts and
    /// menu/leaderboards.ts), on UnityWebRequest, which is the browser's fetch/XHR in a WebGL build and so has the
    /// same cross-origin rules. Like fetch, a request fails (the callback gets null) only when no response arrives;
    /// an HTTP error status still hands over its body. Callbacks run on the main thread.
    /// </summary>
    public static class HubRequests
    {
        /// <summary>fetch(url) → response text, or null when the request could not be made.</summary>
        public static void Get(string url, Action<string> done)
        {
            UnityWebRequest request;
            try
            {
                request = UnityWebRequest.Get(url);
            }
            catch (Exception)
            {
                done(null);
                return;
            }
            Send(request, done);
        }

        /// <summary>fetch(url, {method: 'POST', headers: {'Content-Type': 'application/json'}, body}).</summary>
        public static void PostJson(string url, string body, Action<string> done, bool acceptJson = false)
        {
            UnityWebRequest request;
            try
            {
                request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
                {
                    uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body)),
                    downloadHandler = new DownloadHandlerBuffer()
                };
                if (acceptJson) request.SetRequestHeader("Accept", "application/json");
                request.SetRequestHeader("Content-Type", "application/json");
            }
            catch (Exception)
            {
                done(null);
                return;
            }
            Send(request, done);
        }

        static void Send(UnityWebRequest request, Action<string> done)
        {
            request.SendWebRequest().completed += _ =>
            {
                // A connection or data processing error is fetch's rejection; a protocol error (4xx, 5xx) is a response.
                var failed = request.result == UnityWebRequest.Result.ConnectionError ||
                             request.result == UnityWebRequest.Result.DataProcessingError;
                var text = failed ? null : request.downloadHandler?.text ?? "";
                request.Dispose();
                done(text);
            };
        }

        /// <summary>
        /// response.json(): the parsed body (JsJson.Parse shapes); <paramref name="ok"/> is false where the original's
        /// promise rejects (no response, or a body that is not JSON).
        /// </summary>
        public static object Json(string text, out bool ok)
        {
            ok = false;
            if (text == null) return null;
            try
            {
                var value = JsJson.Parse(text);
                ok = true;
                return value;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>A property read on parsed JSON: undefined (<see cref="Undefined"/>) when it is missing.</summary>
        public static object Property(object value, string key) =>
            value is Dictionary<string, object> map && map.TryGetValue(key, out var property) ? property : Undefined.Value;

        /// <summary>JavaScript truthiness of a JSON value.</summary>
        public static bool Truthy(object value) => value switch
        {
            null => false,
            Undefined _ => false,
            bool b => b,
            double d => d != 0 && !double.IsNaN(d),
            string s => s.Length > 0,
            _ => true
        };

        /// <summary>A JSON value inside a template literal (`${value}`).</summary>
        public static string Template(object value) => value switch
        {
            null => "null",
            Undefined _ => "undefined",
            bool b => b ? "true" : "false",
            double d => JsValue.NumberToString(d),
            string s => s,
            List<object> list => string.Join(",", list.ConvertAll(item => item == null || item is Undefined ? "" : Template(item))),
            _ => "[object Object]"
        };

        /// <summary>
        /// network/socket.ts connect: <c>let { host, port } = server || (await this.getServer()) || this.config;
        /// host ||= this.config.host; port ||= this.config.port;</c> then <c>wss://host</c> or <c>ws://host:port</c>.
        /// The config's host and port are <paramref name="fallback"/>'s (the server URL the client would use
        /// without a hub). A wss fallback keeps its own port: the original's wss URL has no port (a proxy on 443),
        /// and the C# server's TLS listener is on its own port (SSL_PORT), not the one the hub reports.
        /// </summary>
        public static string ServerUrl(object server, string fallback)
        {
            Uri.TryCreate(fallback, UriKind.Absolute, out var config);
            var secure = config != null && config.Scheme == "wss";

            object host = Undefined.Value, port = Undefined.Value;
            if (Truthy(server) && !(server is string) && !(server is double) && !(server is bool))
            {
                host = Property(server, "host");
                port = Property(server, "port");
            }

            var hostText = Truthy(host) ? Template(host) : config?.Host ?? "undefined";
            var portText = Truthy(port) && !secure ? Template(port) : config != null ? config.Port.ToString() : "undefined";
            return $"{(secure ? "wss" : "ws")}://{hostText}:{portText}";
        }
    }
}
