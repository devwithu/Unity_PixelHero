namespace PixelHero.Server.Js
{
    /// <summary>
    /// Math functions with V8's results. Math.max/min handle NaN and -0 like JavaScript.
    /// </summary>
    public static class JsMath
    {
        public static double Max(params double[] values)
        {
            var result = double.NegativeInfinity;
            foreach (var value in values)
            {
                if (double.IsNaN(value)) return double.NaN;
                if (value > result || (value == 0 && result == 0 && !IsNegativeZero(value))) result = value;
            }
            return result;
        }

        public static double Min(params double[] values)
        {
            var result = double.PositiveInfinity;
            foreach (var value in values)
            {
                if (double.IsNaN(value)) return double.NaN;
                if (value < result || (value == 0 && result == 0 && IsNegativeZero(value))) result = value;
            }
            return result;
        }

        static bool IsNegativeZero(double value) => value == 0 && BitConverter.DoubleToInt64Bits(value) < 0;

        /// <summary>Math.round: floor(x + 0.5), keeping -0 and exact halves like JavaScript.</summary>
        public static double Round(double x)
        {
            if (double.IsNaN(x) || double.IsInfinity(x)) return x;
            var floor = Math.Floor(x);
            var result = x - floor >= 0.5 ? floor + 1 : floor;
            if (result == 0 && (x < 0 || IsNegativeZero(x))) return -0.0;
            return result;
        }

        /// <summary>
        /// Math.pow(x, y). The C runtime's pow plus the ES special cases (y NaN, |x| = 1 with infinite y give NaN).
        /// Checked against Node on 60,456 cases (Tools/fixtures/server/gen_pow_cases.mjs): 19 differ in the last bit.
        /// fdlibm's __ieee754_pow was tried and differed on 4,912, so V8 does not use it here.
        /// </summary>
        public static double Pow(double x, double y)
        {
            if (double.IsNaN(y)) return double.NaN;
            if (double.IsInfinity(y) && Math.Abs(x) == 1) return double.NaN;
            return Math.Pow(x, y);
        }
    }
}
