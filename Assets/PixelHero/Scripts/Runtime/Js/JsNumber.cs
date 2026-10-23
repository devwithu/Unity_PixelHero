using System;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace PixelHero.Js
{
    /// <summary>
    /// Exact decimal ↔ double conversion with JavaScript results, computed with BigInteger instead of the runtime.
    /// Unity's Mono double.Parse is not correctly rounded for some inputs (1.668664235941135e-210 comes back one
    /// unit off) and throws instead of returning Infinity, so neither parsing nor Number::toString may rely on it.
    /// </summary>
    public static class JsNumber
    {
        const long HiddenBit = 1L << 52;
        static readonly BigInteger Two53 = BigInteger.One << 53;
        static readonly BigInteger Two52 = BigInteger.One << 52;

        /// <summary>10^0 … 10^22, each exactly representable as a double.</summary>
        static readonly double[] PowersOfTen =
        {
            1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
            1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22
        };

        /// <summary>The double nearest to <paramref name="digits"/> × 10^<paramref name="exponent10"/> (ties to even).</summary>
        public static double FromDecimal(BigInteger digits, int exponent10)
        {
            if (digits.IsZero) return 0;
            if (digits.Sign < 0) return -FromDecimal(-digits, exponent10);

            // Clinger's fast path: an exact integer below 2^53 times or divided by an exact power of ten (≤ 10^22)
            // is a single correctly rounded IEEE operation.
            if (digits < Two53 && exponent10 >= -22 && exponent10 <= 22)
            {
                var exact = (double)(long)digits;
                return exponent10 >= 0 ? exact * PowersOfTen[exponent10] : exact / PowersOfTen[-exponent10];
            }

            // Position of the most significant digit: beyond these bounds the result is certainly Infinity or 0.
            var magnitude = exponent10 + digits.ToString(CultureInfo.InvariantCulture).Length;
            if (magnitude > 310) return double.PositiveInfinity;
            if (magnitude < -325) return 0;

            var numerator = digits;
            var denominator = BigInteger.One;
            if (exponent10 >= 0) numerator *= BigInteger.Pow(10, exponent10);
            else denominator = BigInteger.Pow(10, -exponent10);

            // Find e2 so that q = value / 2^e2 lies in [2^52, 2^53), or e2 = -1074 for subnormals.
            var e2 = (int)Math.Floor(BigInteger.Log(numerator, 2) - BigInteger.Log(denominator, 2)) - 52;
            BigInteger quotient, remainder, divisor;
            while (true)
            {
                if (e2 < -1074) e2 = -1074;
                var n = e2 >= 0 ? numerator : numerator << -e2;
                divisor = e2 >= 0 ? denominator << e2 : denominator;
                quotient = BigInteger.DivRem(n, divisor, out remainder);

                if (quotient >= Two53) { e2++; continue; }
                if (quotient < Two52 && e2 > -1074) { e2--; continue; }
                break;
            }

            var twice = remainder << 1;
            var compare = twice.CompareTo(divisor);
            if (compare > 0 || (compare == 0 && !quotient.IsEven)) quotient += 1;
            if (quotient == Two53)
            {
                quotient >>= 1;
                e2++;
            }

            long bits;
            if (quotient < Two52)
            {
                bits = (long)quotient; // subnormal (e2 == -1074)
            }
            else
            {
                var biased = e2 + 52 + 1023;
                if (biased >= 2047) return double.PositiveInfinity;
                bits = ((long)biased << 52) | ((long)quotient - HiddenBit);
            }

            return BitConverter.Int64BitsToDouble(bits);
        }

        /// <summary>JSON number text (already validated by the JSON grammar) to the double JSON.parse gives.</summary>
        public static double ParseJsonNumber(string text)
        {
            // Fast path: a plain integer of up to 15 digits is exact as a double.
            if (text.Length <= 16 && text.IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && (text[0] != '-' || text.Length > 1))
            {
                var digitsOnly = text[0] == '-' ? text.Length - 1 : text.Length;
                if (digitsOnly <= 15)
                {
                    var integer = long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    return integer == 0 && text[0] == '-' ? -0.0 : integer;
                }
            }

            var negative = text[0] == '-';
            var i = negative ? 1 : 0;
            var digits = new StringBuilder();
            var fractionDigits = 0;

            for (; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++) digits.Append(text[i]);
            if (i < text.Length && text[i] == '.')
                for (i++; i < text.Length && text[i] >= '0' && text[i] <= '9'; i++, fractionDigits++)
                    digits.Append(text[i]);

            var exponent = 0L;
            if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
            {
                i++;
                var expNegative = text[i] == '-';
                if (text[i] == '+' || text[i] == '-') i++;
                for (; i < text.Length; i++)
                    exponent = Math.Min(exponent * 10 + (text[i] - '0'), 100_000_000L); // clamps absurd exponents
                if (expNegative) exponent = -exponent;
            }

            var mantissa = BigInteger.Parse(digits.ToString(), CultureInfo.InvariantCulture);
            var exponent10 = (int)Math.Max(Math.Min(exponent - fractionDigits, 100_000_000L), -100_000_000L);
            var value = FromDecimal(mantissa, exponent10);

            return negative ? -value : value;
        }

        /// <summary>Number::toString: the shortest decimal that round-trips, the closest one if several (ties to even).</summary>
        public static string ToJsString(double value)
        {
            if (double.IsNaN(value)) return "NaN";
            if (double.IsPositiveInfinity(value)) return "Infinity";
            if (double.IsNegativeInfinity(value)) return "-Infinity";
            if (value == 0) return "0"; // also -0

            // Fast path: integers below 2^53 print as their digits (n ≤ 21), which is most of the wire.
            if (Math.Abs(value) < 9007199254740992.0 && value == Math.Floor(value))
                return ((long)value).ToString(CultureInfo.InvariantCulture);

            if (value < 0) return "-" + ToJsString(-value);

            // Exact decimal expansion: value = exact × 10^exactExponent.
            var bits = BitConverter.DoubleToInt64Bits(value);
            var field = (int)((bits >> 52) & 0x7FF);
            var mantissa = bits & (HiddenBit - 1);
            int e2;
            if (field == 0) e2 = -1074;
            else
            {
                mantissa |= HiddenBit;
                e2 = field - 1075;
            }

            BigInteger exact;
            int exactExponent;
            if (e2 >= 0)
            {
                exact = new BigInteger(mantissa) << e2;
                exactExponent = 0;
            }
            else
            {
                exact = new BigInteger(mantissa) * BigInteger.Pow(5, -e2);
                exactExponent = e2;
            }

            var exactLength = exact.ToString(CultureInfo.InvariantCulture).Length;
            BigInteger chosen = exact;
            var chosenExponent = exactExponent;

            for (var precision = 1; precision < exactLength; precision++)
            {
                var drop = exactLength - precision;
                var scale = BigInteger.Pow(10, drop);
                var low = BigInteger.DivRem(exact, scale, out var rest);
                var high = low + 1;
                var exponent10 = exactExponent + drop;

                var lowOk = FromDecimal(low, exponent10) == value;
                var highOk = !rest.IsZero && FromDecimal(high, exponent10) == value;
                if (!lowOk && !highOk) continue;

                if (lowOk && highOk)
                {
                    var compare = (rest << 1).CompareTo(scale);
                    chosen = compare < 0 ? low : compare > 0 ? high : (low.IsEven ? low : high);
                }
                else chosen = lowOk ? low : high;

                chosenExponent = exponent10;
                break;
            }

            var raw = chosen.ToString(CultureInfo.InvariantCulture);
            var n = raw.Length + chosenExponent; // value = 0.digits × 10^n
            var digits = raw.TrimEnd('0');
            return Format(digits, n);
        }

        /// <summary>
        /// Number.prototype.toFixed for |value| &lt; 1e21: the integer n nearest to value × 10^digits on the exact
        /// value of the double, the larger one on a tie (so halves go away from zero after the sign is taken off).
        /// </summary>
        public static string ToFixed(double value, int digits)
        {
            if (double.IsNaN(value)) return "NaN";
            if (Math.Abs(value) >= 1e21) return ToJsString(value);

            var negative = value < 0;
            if (negative) value = -value;

            // value = mantissa × 2^e2 exactly.
            var bits = BitConverter.DoubleToInt64Bits(value);
            var field = (int)((bits >> 52) & 0x7FF);
            var mantissa = bits & (HiddenBit - 1);
            int e2;
            if (field == 0) e2 = -1074;
            else
            {
                mantissa |= HiddenBit;
                e2 = field - 1075;
            }

            var numerator = new BigInteger(mantissa) * BigInteger.Pow(10, digits);
            var denominator = BigInteger.One;
            if (e2 >= 0) numerator <<= e2;
            else denominator <<= -e2;

            var n = BigInteger.DivRem(numerator, denominator, out var remainder);
            if ((remainder << 1) >= denominator) n += 1;

            var text = n.ToString(CultureInfo.InvariantCulture);
            if (digits > 0)
            {
                text = text.PadLeft(digits + 1, '0');
                text = text.Substring(0, text.Length - digits) + "." + text.Substring(text.Length - digits);
            }

            // (-0.04).toFixed(1) is "-0.0": the sign stays whenever the input was negative.
            return negative ? "-" + text : text;
        }

        /// <summary>The layout rules of Number::toString for digits s (k of them) and position n.</summary>
        static string Format(string digits, int n)
        {
            var k = digits.Length;
            var sb = new StringBuilder();

            if (k <= n && n <= 21)
            {
                sb.Append(digits).Append('0', n - k);
            }
            else if (0 < n && n <= 21)
            {
                sb.Append(digits, 0, n).Append('.').Append(digits, n, k - n);
            }
            else if (-6 < n && n <= 0)
            {
                sb.Append("0.").Append('0', -n).Append(digits);
            }
            else
            {
                var e = n - 1;
                sb.Append(digits[0]);
                if (k > 1) sb.Append('.').Append(digits, 1, k - 1);
                sb.Append('e').Append(e >= 0 ? "+" : "-").Append(Math.Abs(e).ToString(CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
