using System;
using PixelHero.Js;

namespace PixelHero.Entities
{
    /// <summary>A sprite animation frame (client entity/animation.ts Frame), in sheet pixels.</summary>
    public struct AnimationFrame
    {
        public int Index;
        public int X;
        public int Y;
    }

    /// <summary>
    /// client/src/entity/animation.ts. At most one frame per update, when <c>Date.now() - lastTime &gt; speed</c>;
    /// a one-frame animation "bops" (0→4→0) instead; at the last frame the count runs down, the end callback
    /// fires when it reaches 0, and the animation restarts (or holds the last frame when <c>withStop</c>).
    /// </summary>
    public sealed class Animation
    {
        readonly IGameClock _clock;

        public readonly string Name;
        public readonly int Length;
        public int Row;
        public readonly int Width;
        public readonly int Height;
        readonly bool _withStop;

        public AnimationFrame Frame;
        public int Count = 1;
        public int BopIndex;
        public readonly int BopCount = 4;
        public bool DecreaseBop;
        public bool Stopped;

        double _lastTime;
        double _speed = 100;
        Action<int> _bopCallback;
        Action _endCallback;

        public Animation(IGameClock clock, string name, int length, int row, int width, int height, bool withStop = false)
        {
            _clock = clock;
            Name = name;
            Length = length;
            Row = row;
            Width = width;
            Height = height;
            _withStop = withStop;
            _lastTime = clock.Now;
            Reset();
        }

        public double Speed => _speed;

        public void Update(double time)
        {
            if (!CanAnimate()) return;

            _lastTime = time;

            if (Length == 1)
            {
                if (DecreaseBop) BopIndex--;
                else BopIndex++;

                if (BopIndex >= BopCount) DecreaseBop = true;
                else if (BopIndex <= 0) DecreaseBop = false;

                _bopCallback?.Invoke(BopIndex);
                return;
            }

            if (Frame.Index >= Length - 1)
            {
                if (Count > 0) Count--;
                if (Count <= 0) _endCallback?.Invoke();

                if (_withStop)
                {
                    Stopped = true;
                    return;
                }

                Reset();
                return;
            }

            Frame.Index++;
            Frame.X = Frame.Index * Width;
            Frame.Y = Row * Height;
        }

        public void SetCount(int count, Action onEnd)
        {
            Count = count;
            _endCallback = onEnd;
        }

        public void SetSpeed(double speed) => _speed = speed;

        public void SetRow(int row) => Row = row;

        bool CanAnimate() => _clock.Now - _lastTime > _speed && !Stopped;

        /// <summary>Used by trees for the stump: the second frame of the row.</summary>
        public AnimationFrame GetSecondFrame() => new AnimationFrame { Index = 1, X = Width, Y = Row * Height };

        public void Reset()
        {
            _lastTime = _clock.Now;
            Frame = new AnimationFrame { Index = 0, X = 0, Y = Row * Height };
        }

        public void OnBop(Action<int> callback) => _bopCallback = callback;
    }

    /// <summary>
    /// client/src/utils/transition.ts: a value from start to end over duration ms, rounded to whole pixels.
    /// The stop callback fires on the step that reaches the duration or the end value.
    /// </summary>
    public sealed class Transition
    {
        double _startValue;
        public double EndValue;
        double _duration;
        public bool InProgress;
        double _startTime;
        Action<double> _update;
        Action _stop;

        public void Start(double currentTime, double startValue, double endValue, double duration, Action<double> update, Action stop)
        {
            _startTime = currentTime;
            _startValue = startValue;
            EndValue = endValue;
            _duration = duration;
            _update = update;
            _stop = stop;
            InProgress = true;
        }

        public void Step(double currentTime)
        {
            if (!InProgress) return;

            var elapsed = currentTime - _startTime;
            if (elapsed > _duration) elapsed = _duration;

            var diff = EndValue - _startValue;
            var interval = JsMath.Round(_startValue + diff / _duration * elapsed);

            if (elapsed == _duration || interval == EndValue)
            {
                InProgress = false;
                _stop?.Invoke();
            }
            else _update?.Invoke(interval);
        }
    }

    /// <summary>JavaScript Math functions whose results differ from .NET's.</summary>
    public static class JsMath
    {
        /// <summary>V8's Math.hypot: scale by the largest magnitude, Kahan-sum the squares, then sqrt × max.</summary>
        public static double Hypot(double a, double b)
        {
            var absA = Math.Abs(a);
            var absB = Math.Abs(b);
            if (double.IsInfinity(absA) || double.IsInfinity(absB)) return double.PositiveInfinity;
            if (double.IsNaN(absA) || double.IsNaN(absB)) return double.NaN;

            var max = Math.Max(absA, absB);
            if (max == 0) return 0;

            double sum = 0, compensation = 0;
            foreach (var value in new[] { absA, absB })
            {
                var n = value / max;
                var summand = n * n - compensation;
                var preliminary = sum + summand;
                compensation = preliminary - sum - summand;
                sum = preliminary;
            }

            return Math.Sqrt(sum) * max;
        }

        /// <summary>Math.round: halves round up (towards +∞), unlike Math.Round's banker's rounding.</summary>
        public static double Round(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return value;
            var floor = Math.Floor(value);
            var result = value - floor >= 0.5 ? floor + 1 : floor;
            // Values in [-0.5, 0) round to -0.
            return result == 0 && (value < 0 || double.IsNegative(value)) ? -0.0 : result;
        }
    }
}
