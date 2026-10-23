using System.Collections.Concurrent;
using System.Diagnostics;
using PixelHero.Server.Common;
using PixelHero.Server.Js;

namespace PixelHero.Server.Network
{
    /// <summary>
    /// The server's single game thread, like Node's event loop: timers run on real (monotonic) time and work posted
    /// from socket threads runs one macrotask at a time, so game code needs no locks. Date.now() follows the system
    /// clock at start plus the monotonic elapsed time; performance.now() is the elapsed time.
    /// </summary>
    public sealed class WorldRunner : IDisposable
    {
        readonly BlockingCollection<Action> _queue = new();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Thread _thread;
        volatile bool _stopped;

        public readonly EventLoop Loop;

        public WorldRunner()
        {
            Loop = new EventLoop(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            {
                PerformanceOrigin = 0,
                // The original dies on an uncaught exception; the server logs it and keeps running.
                UncaughtException = error => Log.Error($"Uncaught exception: {error}")
            };

            _thread = new Thread(Run) { IsBackground = true, Name = "world" };
        }

        public void Start() => _thread.Start();

        /// <summary>Queues a macrotask (a socket event) for the game thread.</summary>
        public void Post(Action action)
        {
            if (!_stopped) _queue.Add(action);
        }

        /// <summary>Runs an action on the game thread and waits for it (startup and shutdown).</summary>
        public void Invoke(Action action)
        {
            using var done = new ManualResetEventSlim();
            Exception error = null;
            Post(() =>
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    error = e;
                }
                finally
                {
                    done.Set();
                }
            });
            done.Wait();
            if (error != null) throw new InvalidOperationException("World action failed", error);
        }

        double Elapsed => _clock.Elapsed.TotalMilliseconds;

        void Run()
        {
            Node.Install(Loop, new SystemRandom());
            Node.TimeZone = TimeZoneInfo.Local;
            Node.Post = Post;

            while (!_stopped)
            {
                Loop.RunUntil(Elapsed);

                var next = Loop.NextDue;
                var wait = next.HasValue ? Math.Max(0, next.Value - Elapsed) : 1000;

                if (_queue.TryTake(out var action, TimeSpan.FromMilliseconds(Math.Min(wait, 1000))))
                {
                    do
                    {
                        // Date.now() in the handler is the current time.
                        Loop.RunUntil(Elapsed);
                        Loop.Run(action);
                    } while (!_stopped && _queue.TryTake(out action));
                }
            }
        }

        public void Dispose()
        {
            _stopped = true;
            _queue.Add(() => { });
            if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
