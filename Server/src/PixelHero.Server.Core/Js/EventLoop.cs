namespace PixelHero.Server.Js
{
    /// <summary>A setTimeout/setInterval handle (Node's JsTimeout).</summary>
    public sealed class JsTimeout
    {
        internal readonly Action Callback;
        internal readonly double Delay;
        internal readonly bool Repeat;
        internal double Due;
        internal long Sequence;
        internal bool Cancelled;
        internal readonly EventLoop Loop;

        internal JsTimeout(EventLoop loop, Action callback, double delay, bool repeat)
        {
            Loop = loop;
            Callback = callback;
            Delay = delay;
            Repeat = repeat;
        }

        /// <summary>timeout.refresh(): restarts the countdown from now.</summary>
        public JsTimeout Refresh()
        {
            Loop.Schedule(this);
            return this;
        }
    }

    /// <summary>A setImmediate handle.</summary>
    public sealed class Immediate
    {
        internal readonly Action Callback;
        internal bool Cancelled;

        internal Immediate(Action callback) => Callback = callback;
    }

    /// <summary>
    /// Node's event loop for one server: timers, immediates, microtasks and the clock. The rules match the harness
    /// that records the original (Tools/fixtures/server/harness.mjs):
    /// - setTimeout/setInterval delays become 1 unless 1 &lt;= delay &lt;= 2^31-1. Timers run in (due, sequence) order;
    ///   an interval re-arms at due + delay with a new sequence number. Each callback is one macrotask.
    /// - After every macrotask: microtasks drain, then the queued immediates run (those queued meanwhile run in the next
    ///   round, with microtasks draining after each) until none are left.
    /// - An exception escaping a macrotask goes to <see cref="UncaughtException"/>.
    /// The owner drives it: tests advance virtual time with <see cref="RunUntil"/>; the server's world thread passes
    /// the real elapsed time.
    /// </summary>
    public sealed class EventLoop
    {
        const double TimeoutMax = 2147483647;

        readonly PriorityQueue<JsTimeout, (double Due, long Sequence)> _timers = new();
        readonly Queue<Action> _microtasks = new();
        List<Immediate> _immediates = new();
        long _sequence;

        /// <summary>Milliseconds since the loop started (virtual in tests).</summary>
        public double Now { get; private set; }

        /// <summary>Date.now() at <see cref="Now"/> = 0.</summary>
        public double Epoch { get; set; }

        /// <summary>performance.now() at <see cref="Now"/> = 0 (the harness uses 1000).</summary>
        public double PerformanceOrigin { get; set; } = 1000;

        /// <summary>
        /// Called when a callback is scheduled (timer, immediate, microtask); the action it returns runs right before
        /// the callback. Tests that run several servers on one loop use it to give each callback its own server's state
        /// (Config.Current, Utils' counters), as each original server has its own module state. Null: nothing.
        /// </summary>
        public Func<Action> CaptureContext { get; set; }

        /// <summary>Called with an exception that escaped a macrotask (the original has no handler and dies).</summary>
        public Action<Exception> UncaughtException { get; set; } = e => throw e;

        public EventLoop(double epoch = 0, double now = 0)
        {
            Epoch = epoch;
            Now = now;
        }

        public double DateNow => Epoch + Now;

        public double PerformanceNow => PerformanceOrigin + Now;

        public JsTimeout SetTimeout(Action callback, double delay) =>
            Schedule(new JsTimeout(this, InContext(callback), Coerce(delay), false));

        public JsTimeout SetInterval(Action callback, double delay) =>
            Schedule(new JsTimeout(this, InContext(callback), Coerce(delay), true));

        /// <summary>The callback, preceded by the context captured now (<see cref="CaptureContext"/>).</summary>
        Action InContext(Action callback)
        {
            var restore = CaptureContext?.Invoke();
            if (restore == null) return callback;
            return () =>
            {
                restore();
                callback();
            };
        }

        /// <summary>clearTimeout / clearInterval; null is ignored.</summary>
        public void Clear(JsTimeout timeout)
        {
            if (timeout != null) timeout.Cancelled = true;
        }

        public Immediate SetImmediate(Action callback)
        {
            var immediate = new Immediate(InContext(callback));
            _immediates.Add(immediate);
            return immediate;
        }

        public void ClearImmediate(Immediate immediate)
        {
            if (immediate != null) immediate.Cancelled = true;
        }

        /// <summary>queueMicrotask / a promise reaction.</summary>
        public void QueueMicrotask(Action callback) => _microtasks.Enqueue(InContext(callback));

        internal JsTimeout Schedule(JsTimeout timeout)
        {
            timeout.Cancelled = false;
            timeout.Due = Now + timeout.Delay;
            timeout.Sequence = _sequence++;
            _timers.Enqueue(timeout, (timeout.Due, timeout.Sequence));
            return timeout;
        }

        static double Coerce(double delay) => delay >= 1 && delay <= TimeoutMax ? delay : 1;

        /// <summary>The due time of the next live timer, or null.</summary>
        public double? NextDue
        {
            get
            {
                while (_timers.TryPeek(out var timeout, out var key) && IsStale(timeout, key)) _timers.Dequeue();
                return _timers.TryPeek(out _, out var next) ? next.Due : null;
            }
        }

        /// <summary>A queue entry is stale when its timer was cancelled or re-armed (refresh) after it was queued.</summary>
        static bool IsStale(JsTimeout timeout, (double Due, long Sequence) key) =>
            timeout.Cancelled || timeout.Sequence != key.Sequence;

        /// <summary>Runs one macrotask (a socket event, a connection) and everything it queued.</summary>
        public void Run(Action macrotask)
        {
            try
            {
                macrotask();
            }
            catch (Exception e)
            {
                UncaughtException(e);
            }
            Drain();
        }

        /// <summary>Runs every timer due at or before <paramref name="now"/>, then sets <see cref="Now"/> to it.</summary>
        public void RunUntil(double now)
        {
            while (_timers.TryPeek(out var timeout, out var key) && key.Due <= now)
            {
                _timers.Dequeue();
                if (IsStale(timeout, key)) continue;

                Now = Math.Max(Now, key.Due);
                if (timeout.Repeat)
                {
                    timeout.Due = key.Due + timeout.Delay;
                    timeout.Sequence = _sequence++;
                    _timers.Enqueue(timeout, (timeout.Due, timeout.Sequence));
                }
                else timeout.Cancelled = true;

                Run(timeout.Callback);
            }
            Now = Math.Max(Now, now);
        }

        void DrainMicrotasks()
        {
            while (_microtasks.Count > 0)
            {
                var task = _microtasks.Dequeue();
                try
                {
                    task();
                }
                catch (Exception e)
                {
                    UncaughtException(e);
                }
            }
        }

        void Drain()
        {
            DrainMicrotasks();
            while (_immediates.Count > 0)
            {
                var round = _immediates;
                _immediates = new List<Immediate>();
                foreach (var immediate in round)
                {
                    if (immediate.Cancelled) continue;
                    try
                    {
                        immediate.Callback();
                    }
                    catch (Exception e)
                    {
                        UncaughtException(e);
                    }
                    DrainMicrotasks();
                }
            }
        }
    }
}
