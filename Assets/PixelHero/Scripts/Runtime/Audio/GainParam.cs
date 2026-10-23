using System.Collections.Generic;

namespace PixelHero.Audio
{
    /// <summary>
    /// The part of a Web Audio AudioParam that audio.ts uses: an intrinsic value plus a timeline of
    /// linearRampToValueAtTime events, and cancelScheduledValues.
    /// <list type="bullet">
    /// <item>A ramp runs from the previous event (its time and value) to its own. With no previous event, it starts
    /// where the param stands when the ramp is scheduled.</item>
    /// <item>After the last event the value holds. cancelScheduledValues(t) drops every event at or after t, so a
    /// ramp cut short falls back to the event before it.</item>
    /// </list>
    /// </summary>
    public sealed class GainParam
    {
        struct Event
        {
            public double Time;
            public double Value;
        }

        readonly List<Event> _events = new List<Event>();
        double _intrinsic;

        public GainParam(double value) => _intrinsic = value;

        /// <summary>AudioParam.value read at <paramref name="time"/> (the context's currentTime).</summary>
        public double Value(double time)
        {
            if (_events.Count == 0) return _intrinsic;

            var next = _events.FindIndex(e => e.Time > time);
            if (next == -1) return _events[_events.Count - 1].Value;
            if (next == 0) return _intrinsic;

            Event from = _events[next - 1], to = _events[next];
            return from.Value + (to.Value - from.Value) * (time - from.Time) / (to.Time - from.Time);
        }

        public void LinearRampToValueAtTime(double value, double endTime, double now)
        {
            // A ramp with nothing before it starts from the value at the time it is scheduled.
            if (_events.Count == 0) _events.Add(new Event { Time = now, Value = _intrinsic });

            // Events are kept in time order; one at the same time as an existing ramp comes after it.
            var index = _events.FindLastIndex(e => e.Time <= endTime) + 1;
            _events.Insert(index, new Event { Time = endTime, Value = value });
        }

        public void CancelScheduledValues(double time, double now)
        {
            // What the param holds once its whole timeline is gone: the value it had reached.
            var held = Value(now);
            _events.RemoveAll(e => e.Time >= time);
            if (_events.Count == 0) _intrinsic = held;
        }
    }
}
