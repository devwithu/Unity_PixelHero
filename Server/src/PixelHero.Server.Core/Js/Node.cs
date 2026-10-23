namespace PixelHero.Server.Js
{
    /// <summary>Math.random's source. Tests use <see cref="ParkMiller"/>, the server <see cref="SystemRandom"/>.</summary>
    public interface IRandomSource
    {
        /// <summary>A double in [0, 1).</summary>
        double Next();
    }

    /// <summary>The harness generator: seed = seed * 16807 % 2147483647, value = seed / 2147483647.</summary>
    public sealed class ParkMiller : IRandomSource
    {
        public long Seed;

        public ParkMiller(long seed) => Seed = seed;

        public double Next()
        {
            Seed = Seed * 16807 % 2147483647;
            return Seed / 2147483647.0;
        }
    }

    public sealed class SystemRandom : IRandomSource
    {
        readonly Random _random = new();

        public double Next() => _random.NextDouble();
    }

    /// <summary>
    /// The JavaScript globals the original uses (setTimeout, Date.now, performance.now, Math.random), bound to the
    /// running server's <see cref="EventLoop"/> and random source. Like the original's module-level state there is one
    /// server per process; tests install a fresh loop for each scenario with <see cref="Install"/>.
    /// </summary>
    public static class Node
    {
        public static EventLoop Loop { get; private set; } = new();

        public static IRandomSource RandomSource { get; private set; } = new SystemRandom();

        public static void Install(EventLoop loop, IRandomSource random)
        {
            Loop = loop;
            RandomSource = random;
        }

        public static JsTimeout SetTimeout(Action callback, double delay) => Loop.SetTimeout(callback, delay);

        public static JsTimeout SetInterval(Action callback, double delay) => Loop.SetInterval(callback, delay);

        public static void ClearTimeout(JsTimeout timeout) => Loop.Clear(timeout);

        public static void ClearInterval(JsTimeout timeout) => Loop.Clear(timeout);

        public static Immediate SetImmediate(Action callback) => Loop.SetImmediate(callback);

        public static void QueueMicrotask(Action callback) => Loop.QueueMicrotask(callback);

        /// <summary>
        /// Hands work from another thread (a socket, an HTTP request, a driver callback) to the event loop as a
        /// macrotask, like an I/O callback in Node. Set by the host's runner (WorldRunner); null when nothing runs the
        /// loop on its own thread (tests), where the hub, the HTTP API and Discord are off.
        /// </summary>
        public static Action<Action> Post { get; set; }

        /// <summary>Date.now().</summary>
        public static double DateNow() => Loop.DateNow;

        /// <summary>performance.now().</summary>
        public static double PerformanceNow() => Loop.PerformanceNow;

        /// <summary>Math.random().</summary>
        public static double Random() => RandomSource.Next();

        /// <summary>The time zone of new Date() (the harness runs with TZ=UTC; the server uses the machine's zone).</summary>
        public static TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

        /// <summary>new Date() in <see cref="TimeZone"/>.</summary>
        public static DateTimeOffset Date() =>
            TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(DateNow())), TimeZone);
    }
}
