using PixelHero.Server.Js;

namespace PixelHero.Server.Common
{
    /// <summary>
    /// common/util/log.ts. Output goes to <see cref="Sink"/> (the console by default; tests collect warnings and
    /// errors like the harness does). Log files are not written.
    /// </summary>
    public static class Log
    {
        public enum Level
        {
            Debug,
            Info,
            Notice,
            Warning,
            Error,
            Critical,
            Bug
        }

        /// <summary>Where messages go. Null drops them.</summary>
        public static Action<Level, string> Sink { get; set; } = WriteConsole;

        /// <summary>The lowest level written to the console.</summary>
        public static Level ConsoleLevel { get; set; } = Level.Info;

        static void WriteConsole(Level level, string message)
        {
            if (level < ConsoleLevel) return;
            var writer = level >= Level.Warning ? Console.Error : Console.Out;
            writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level.ToString().ToUpperInvariant()}] {message}");
        }

        static void Write(Level level, object[] data) => Sink?.Invoke(level, string.Join(" ", data.Select(Describe)));

        static string Describe(object value) => value switch
        {
            Exception e => e is JsTypeError ? "TypeError: " + e.Message : $"{e.GetType().Name}: {e.Message}",
            _ => JsString.Of(value)
        };

        public static void Info(params object[] data) => Write(Level.Info, data);
        public static void Debug(params object[] data) => Write(Level.Debug, data);
        public static void Notice(params object[] data) => Write(Level.Notice, data);
        public static void Trace(params object[] data) => Write(Level.Debug, data);
        public static void Warning(params object[] data) => Write(Level.Warning, data);
        public static void Error(params object[] data) => Write(Level.Error, data);
        public static void Critical(params object[] data) => Write(Level.Critical, data);
        public static void Bug(params object[] data) => Write(Level.Bug, data);
        public static void Chat(params object[] data) => Write(Level.Info, data);
        public static void Drop(params object[] data) => Write(Level.Info, data);
        public static void General(params object[] data) => Write(Level.Info, data);
        public static void Stores(params object[] data) => Write(Level.Info, data);
        public static void Trade(params object[] data) => Write(Level.Info, data);

        public static void Assert(bool assertion, params object[] data)
        {
            if (!assertion) Write(Level.Error, data);
        }
    }

    /// <summary>A TypeError the original would throw at this point (calling a missing method, reading from undefined).</summary>
    public sealed class JsTypeError : Exception
    {
        public JsTypeError(string message) : base(message) { }
    }
}
