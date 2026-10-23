using System;
using System.Collections.Generic;

namespace PixelHero.Js
{
    /// <summary>The client's wall clock: <c>Date.now()</c> in milliseconds.</summary>
    public interface IGameClock
    {
        double Now { get; }
    }

    /// <summary>A clock the caller sets (tests, replays).</summary>
    public sealed class ManualClock : IGameClock
    {
        public double Now { get; set; }
    }

    /// <summary>
    /// setTimeout / setInterval / clearTimeout on a <see cref="IGameClock"/>. Timers run from <see cref="RunDue"/>
    /// (once per frame), earliest due time first and then in creation order — the order the client code created
    /// them matters, because callbacks with the same due time run in that order. An interval reschedules itself
    /// before its callback runs, like a browser.
    /// </summary>
    public sealed class JsTimers
    {
        sealed class Timer
        {
            public int Id;
            public double Due;
            public Action Callback;
            public double? Interval;
        }

        readonly IGameClock _clock;
        readonly List<Timer> _timers = new List<Timer>();
        int _nextId = 1;

        public JsTimers(IGameClock clock) => _clock = clock;

        public IGameClock Clock => _clock;

        public int SetTimeout(Action callback, double delay = 0) => Add(callback, delay, null);

        public int SetInterval(Action callback, double delay) => Add(callback, delay, delay);

        public void Clear(int id)
        {
            if (id == 0) return;
            _timers.RemoveAll(timer => timer.Id == id);
        }

        public int Count => _timers.Count;

        int Add(Action callback, double delay, double? interval)
        {
            var id = _nextId++;
            _timers.Add(new Timer { Id = id, Due = _clock.Now + Math.Max(0, double.IsNaN(delay) ? 0 : delay), Callback = callback, Interval = interval });
            return id;
        }

        /// <summary>Runs every timer that is due, including ones that become due while running.</summary>
        public void RunDue()
        {
            while (true)
            {
                Timer next = null;
                foreach (var timer in _timers)
                    if (timer.Due <= _clock.Now && (next == null || timer.Due < next.Due || (timer.Due == next.Due && timer.Id < next.Id)))
                        next = timer;

                if (next == null) return;

                if (next.Interval == null) _timers.Remove(next);
                else next.Due += Math.Max(1, next.Interval.Value);

                next.Callback();
            }
        }
    }
}
