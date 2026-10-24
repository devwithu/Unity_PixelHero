using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using PixelHero.Js;
using PixelHero.Server.Js;

namespace PixelHero.Server.Common
{
    /// <summary>
    /// common/config.ts: the .env file over .env.defaults (dotenv-extended), each value converted like
    /// dotenv-parse-variables (`backticks` -> string, trailing * -> string, true/false, numbers, comma lists), keys
    /// converted to camelCase. Fields keep the original's names.
    /// </summary>
    public sealed class Config
    {
        public string name;
        public string host;
        public double port;
        public bool ssl;

        // C# server only (the original has no TLS listener; its `ssl` only makes the client use wss behind a proxy):
        // a second, secure WebSocket listener on sslPort. sslCertificate is a .pfx/.p12, or a PEM certificate chain
        // such as Let's Encrypt's fullchain.pem with its key in sslCertificateKey (privkey.pem); paths are relative to
        // the repository root, or absolute. sslCertificatePassword opens a PFX or an encrypted key. Off when sslPort
        // is 0 or no certificate is set (see Network/TlsCertificate).
        public double sslPort;
        public string sslCertificate;
        public string sslCertificateKey;
        public string sslCertificatePassword;

        public double serverId;
        public string accessToken;
        public bool apiEnabled;
        public double apiPort;

        public bool hubEnabled;
        public string hubHost;
        public string hubWsHost;
        public double hubPort;
        public double hubWsPort;

        // C# hub only: an https listener for the hub's HTTP API on hubSslPort, with the game server's certificate
        // (sslCertificate, sslCertificateKey, sslCertificatePassword), for clients on https pages. Off when 0.
        public double hubSslPort;
        public string hubAccessToken;
        public string adminHost;
        public double adminPort;
        public string remoteServerHost;
        public string remoteApiHost;

        public string clientRemoteHost;
        public object clientRemotePort;

        public double cleanupThreshold;
        public double cleanupTime;

        public string database;
        public bool skipDatabase;

        public string mongodbHost;
        public double mongodbPort;
        public string mongodbUser;
        public string mongodbPassword;
        public string mongodbDatabase;
        public bool mongodbSrv;
        public bool mongodbTls;
        public string mongodbAuthSource;
        public double aggregateThreshold;

        public bool tutorialEnabled;

        // C# server only: the item keys a new character gets when the tutorial is off, since the tutorial is what
        // hands out the first tools (a comma list, or one key; empty gives nothing). See Player.GiveStarterItems.
        public object starterItems;
        public bool overrideAuth;
        public bool disableRegister;
        public double maxPlayers;
        public double updateTime;
        public string gver;
        public string minor;
        public bool regionCache;
        public double saveInterval;
        public double messageLimit;

        public string smtpHost;
        public double smtpPort;
        public bool smtpUseSecure;
        public string smtpUser;
        public string smtpPassword;

        public string sentryDsn;

        public string stripeEndpoint;
        public string stripeKeyLocal;
        public string stripeSecretKey;

        public bool discordEnabled;
        public string discordChannelId;
        public string discordBotToken;
        public bool debugging;
        public string debugLevel;
        public bool fsDebugging;

        /// <summary>Every converted value by camelCase key, including the ones without a field.</summary>
        public readonly JsObject Values = new();

        /// <summary>The running server's config (common/config.ts' default export).</summary>
        public static Config Current { get; set; } = new();

        /// <summary>Loads <paramref name="defaultsPath"/> with <paramref name="envPath"/> over it (either may be missing).</summary>
        public static Config Load(string defaultsPath, string envPath = null, JsObject overrides = null)
        {
            var env = new Dictionary<string, string>();
            if (defaultsPath != null && File.Exists(defaultsPath))
                foreach (var pair in Dotenv.Parse(File.ReadAllText(defaultsPath))) env[pair.Key] = pair.Value;
            if (envPath != null && File.Exists(envPath))
                foreach (var pair in Dotenv.Parse(File.ReadAllText(envPath))) env[pair.Key] = pair.Value;

            var config = new Config();
            foreach (var pair in env) config.Assign(CamelCase(pair.Key), ParseVariable(pair.Value));

            config.hubHost = Or(config.hubHost, config.host);
            config.hubWsHost = Or(config.hubWsHost, config.hubHost);
            config.adminHost = Or(config.adminHost, config.hubHost);
            config.remoteServerHost = Or(config.remoteServerHost, config.host);

            if (overrides != null)
                foreach (var pair in overrides)
                    config.Assign(pair.Key, pair.Value);

            return config;
        }

        static string Or(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

        /// <summary>config[key] = value, converted to the field's type the way JavaScript would hold it.</summary>
        public void Assign(string key, object value)
        {
            Values.Set(key, value);
            var field = typeof(Config).GetField(key, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) return;
            if (field.FieldType == typeof(string)) field.SetValue(this, value as string ?? JsString.Of(value));
            else if (field.FieldType == typeof(double))
                field.SetValue(this, value is double d ? d : value is bool b ? (b ? 1.0 : 0.0) : JsString.ParseFloat(JsString.Of(value)));
            else if (field.FieldType == typeof(bool)) field.SetValue(this, JsData.Truthy(value));
            else field.SetValue(this, value);
        }

        /// <summary>common/config.ts camelCase.</summary>
        public static string CamelCase(string key) =>
            Regex.Replace(key.ToLowerInvariant(), "[_-][a-z]", match => match.Value.Substring(1).ToUpperInvariant());

        /// <summary>dotenv-parse-variables parseKey.</summary>
        public static object ParseVariable(string value)
        {
            if (value.StartsWith("`") && value.LastIndexOf('`') == value.Length - 1)
                return value.Substring(1, value.Length - 1 - 1 >= 0 ? value.Length - 2 : 0);

            if (value.Length > 0 && value.LastIndexOf('*') == value.Length - 1 && !value.Contains(','))
                return value.Substring(0, Math.Max(0, value.Length - 1));

            var lower = value.ToLowerInvariant();
            if (lower == "true" || lower == "false") return lower == "true";

            if (value.Trim().Length > 0)
            {
                var number = JsNumberValue(value);
                if (!double.IsNaN(number)) return number;
            }

            if (value.Contains(','))
                return value.Split(',').Where(part => part != "").Select(part => ParseVariable(part)).ToList<object>();

            return value;
        }

        /// <summary>Number(string).</summary>
        static double JsNumberValue(string value)
        {
            var text = JsString.Trim(value);
            if (text.Length == 0) return 0;
            if (text.StartsWith("0x") || text.StartsWith("0X"))
                return long.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex) ? hex : double.NaN;
            if (text == "Infinity" || text == "+Infinity") return double.PositiveInfinity;
            if (text == "-Infinity") return double.NegativeInfinity;
            if (!Regex.IsMatch(text, @"^[+-]?([0-9]+\.?[0-9]*|\.[0-9]+)([eE][+-]?[0-9]+)?\z")) return double.NaN;
            return JsString.ParseFloat(text);
        }
    }

    /// <summary>
    /// The parser of dotenv 8.6.0, which dotenv-extended 2.9.0 uses in the original: one KEY=VAL per line; single or
    /// double quotes are removed (double quotes also turn \n into a newline), anything else is trimmed. Backticks stay
    /// (dotenv-parse-variables removes them and keeps the value a string), and a # after a value is part of it.
    /// </summary>
    public static class Dotenv
    {
        // JavaScript's \s and the characters `.` does not match.
        const string JsSpace = @"\t\n\v\f\r    -     　﻿";

        // RE_INI_KEY_VAL = /^\s*([\w.-]+)\s*=\s*(.*)?\s*$/
        static readonly Regex Line = new(
            "^[" + JsSpace + "]*([A-Za-z0-9_.-]+)[" + JsSpace + "]*=[" + JsSpace + "]*([^\\n\\r\\u2028\\u2029]*)?[" + JsSpace + "]*\\z",
            RegexOptions.CultureInvariant);

        public static Dictionary<string, string> Parse(string source)
        {
            var result = new Dictionary<string, string>();

            // src.split(/\n|\r|\r\n/)
            foreach (var line in Regex.Split(source, "\n|\r|\r\n"))
            {
                var match = Line.Match(line);
                if (!match.Success) continue;

                var key = match.Groups[1].Value;
                // default undefined or missing values to empty string
                var value = match.Groups[2].Success ? match.Groups[2].Value : "";
                var end = value.Length - 1;
                var isDoubleQuoted = value.Length > 0 && value[0] == '"' && value[end] == '"';
                var isSingleQuoted = value.Length > 0 && value[0] == '\'' && value[end] == '\'';

                // if single or double quoted, remove quotes
                if (isSingleQuoted || isDoubleQuoted)
                {
                    // (a lone quote stays: substring(1, 0) swaps its arguments)
                    value = end > 0 ? value.Substring(1, end - 1) : value;

                    // if double quoted, expand newlines
                    if (isDoubleQuoted) value = value.Replace("\\n", "\n");
                }
                // remove surrounding whitespace
                else value = JsString.Trim(value);

                result[key] = value;
            }

            return result;
        }
    }
}
