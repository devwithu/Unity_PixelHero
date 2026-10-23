using System;

namespace PixelHero.Js
{
    /// <summary>
    /// Math.sin, Math.cos and Math.atan2 as V8 computes them (src/base/ieee754.cc, a port of fdlibm 5.3), so that
    /// results match the browser to the last bit (.NET's CRT functions differ in the last ulp). Arguments beyond
    /// ±2^19·π/2 would need fdlibm's large reduction (__kernel_rem_pio2) and fall back to .NET.
    /// </summary>
    public static class Fdlibm
    {
        static int High(double x) => (int)(BitConverter.DoubleToInt64Bits(x) >> 32);
        static uint Low(double x) => (uint)BitConverter.DoubleToInt64Bits(x);
        static double FromWords(int high, uint low) => BitConverter.Int64BitsToDouble(((long)high << 32) | low);

        // ---- k_sin.c / k_cos.c ----
        const double S1 = -1.66666666666666324348e-01, S2 = 8.33333333332248946124e-03, S3 = -1.98412698298579493134e-04,
            S4 = 2.75573137070700676789e-06, S5 = -2.50507602534068634195e-08, S6 = 1.58969099521155010221e-10;

        const double C1 = 4.16666666666666019037e-02, C2 = -1.38888888888741095749e-03, C3 = 2.48015872894767294178e-05,
            C4 = -2.75573143513906633035e-07, C5 = 2.08757232129817482790e-09, C6 = -1.13596475577881948265e-11;

        static double KernelSin(double x, double y, int iy)
        {
            var ix = High(x) & 0x7fffffff;
            if (ix < 0x3e400000 && (int)x == 0) return x;

            var z = x * x;
            var v = z * x;
            var r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
            if (iy == 0) return x + v * (S1 + z * r);
            return x - ((z * (0.5 * y - v * r) - y) - v * S1);
        }

        static double KernelCos(double x, double y)
        {
            var ix = High(x) & 0x7fffffff;
            if (ix < 0x3e400000 && (int)x == 0) return 1.0;

            var z = x * x;
            var r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
            if (ix < 0x3FD33333) return 1.0 - (0.5 * z - (z * r - x * y));

            var qx = ix > 0x3fe90000 ? 0.28125 : FromWords(ix - 0x00200000, 0);
            var hz = 0.5 * z - qx;
            var a = 1.0 - qx;
            return a - (hz - (z * r - x * y));
        }

        // ---- e_rem_pio2.c (small and medium arguments) ----
        const double InvPio2 = 6.36619772367581382433e-01, Pio2_1 = 1.57079632673412561417e+00, Pio2_1t = 6.07710050650619224932e-11,
            Pio2_2 = 6.07710050630396597660e-11, Pio2_2t = 2.02226624879595063154e-21, Pio2_3 = 2.02226624871116645580e-21,
            Pio2_3t = 8.47842766036889956997e-32;

        static readonly int[] Npio2Hw =
        {
            0x3FF921FB, 0x400921FB, 0x4012D97C, 0x401921FB, 0x401F6A7A, 0x4022D97C, 0x4025FDBB, 0x402921FB,
            0x402C463A, 0x402F6A7A, 0x4031475C, 0x4032D97C, 0x40346B9C, 0x4035FDBB, 0x40378FDB, 0x403921FB,
            0x403AB41B, 0x403C463A, 0x403DD85A, 0x403F6A7A, 0x40407E4C, 0x4041475C, 0x4042106C, 0x4042D97C,
            0x4043A28C, 0x40446B9C, 0x404534AC, 0x4045FDBB, 0x4046C6CB, 0x40478FDB, 0x404858EB, 0x404921FB
        };

        /// <returns>n, with x - n·π/2 = y0 + y1; or int.MinValue when the argument is too large.</returns>
        static int RemPio2(double x, out double y0, out double y1)
        {
            var hx = High(x);
            var ix = hx & 0x7fffffff;
            double z;

            if (ix <= 0x3fe921fb)
            {
                y0 = x;
                y1 = 0;
                return 0;
            }

            if (ix < 0x4002d97c)
            {
                if (hx > 0)
                {
                    z = x - Pio2_1;
                    if (ix != 0x3ff921fb)
                    {
                        y0 = z - Pio2_1t;
                        y1 = (z - y0) - Pio2_1t;
                    }
                    else
                    {
                        z -= Pio2_2;
                        y0 = z - Pio2_2t;
                        y1 = (z - y0) - Pio2_2t;
                    }
                    return 1;
                }

                z = x + Pio2_1;
                if (ix != 0x3ff921fb)
                {
                    y0 = z + Pio2_1t;
                    y1 = (z - y0) + Pio2_1t;
                }
                else
                {
                    z += Pio2_2;
                    y0 = z + Pio2_2t;
                    y1 = (z - y0) + Pio2_2t;
                }
                return -1;
            }

            if (ix <= 0x413921fb)
            {
                var t = Math.Abs(x);
                var n = (int)(t * InvPio2 + 0.5);
                var fn = (double)n;
                var r = t - fn * Pio2_1;
                var w = fn * Pio2_1t;

                if (n < 32 && ix != Npio2Hw[n - 1])
                {
                    y0 = r - w;
                }
                else
                {
                    var j = ix >> 20;
                    y0 = r - w;
                    var i = j - ((High(y0) >> 20) & 0x7ff);
                    if (i > 16)
                    {
                        t = r;
                        w = fn * Pio2_2;
                        r = t - w;
                        w = fn * Pio2_2t - ((t - r) - w);
                        y0 = r - w;
                        i = j - ((High(y0) >> 20) & 0x7ff);
                        if (i > 49)
                        {
                            t = r;
                            w = fn * Pio2_3;
                            r = t - w;
                            w = fn * Pio2_3t - ((t - r) - w);
                            y0 = r - w;
                        }
                    }
                }

                y1 = (r - y0) - w;
                if (hx < 0)
                {
                    y0 = -y0;
                    y1 = -y1;
                    return -n;
                }
                return n;
            }

            y0 = y1 = 0;
            return int.MinValue;
        }

        public static double Sin(double x)
        {
            var ix = High(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb) return KernelSin(x, 0, 0);
            if (ix >= 0x7ff00000) return double.NaN;

            var n = RemPio2(x, out var y0, out var y1);
            if (n == int.MinValue) return Math.Sin(x);

            switch (n & 3)
            {
                case 0: return KernelSin(y0, y1, 1);
                case 1: return KernelCos(y0, y1);
                case 2: return -KernelSin(y0, y1, 1);
                default: return -KernelCos(y0, y1);
            }
        }

        public static double Cos(double x)
        {
            var ix = High(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb) return KernelCos(x, 0);
            if (ix >= 0x7ff00000) return double.NaN;

            var n = RemPio2(x, out var y0, out var y1);
            if (n == int.MinValue) return Math.Cos(x);

            switch (n & 3)
            {
                case 0: return KernelCos(y0, y1);
                case 1: return -KernelSin(y0, y1, 1);
                case 2: return -KernelCos(y0, y1);
                default: return KernelSin(y0, y1, 1);
            }
        }

        // ---- s_atan.c ----
        static readonly double[] AtanHi = { 4.63647609000806093515e-01, 7.85398163397448278999e-01, 9.82793723247329054082e-01, 1.57079632679489655800e+00 };
        static readonly double[] AtanLo = { 2.26987774529616870924e-17, 3.06161699786838301793e-17, 1.39033110312309984516e-17, 6.12323399573676603587e-17 };
        static readonly double[] AT =
        {
            3.33333333333329318027e-01, -1.99999999998764832476e-01, 1.42857142725034663711e-01, -1.11111104054623557880e-01,
            9.09088713343650656196e-02, -7.69187620504482999495e-02, 6.66107313738753120669e-02, -5.83357013379057348645e-02,
            4.97687799461593236017e-02, -3.65315727442169155270e-02, 1.62858201153657823623e-02
        };

        public static double Atan(double x)
        {
            var hx = High(x);
            var ix = hx & 0x7fffffff;
            int id;

            if (ix >= 0x44100000)
            {
                if (ix > 0x7ff00000 || (ix == 0x7ff00000 && Low(x) != 0)) return x + x;
                return hx > 0 ? AtanHi[3] + AtanLo[3] : -AtanHi[3] - AtanLo[3];
            }

            if (ix < 0x3fdc0000)
            {
                if (ix < 0x3e400000 && 1.0e300 + x > 1.0) return x;
                id = -1;
            }
            else
            {
                x = Math.Abs(x);
                if (ix < 0x3ff30000)
                {
                    if (ix < 0x3fe60000)
                    {
                        id = 0;
                        x = (2.0 * x - 1.0) / (2.0 + x);
                    }
                    else
                    {
                        id = 1;
                        x = (x - 1.0) / (x + 1.0);
                    }
                }
                else if (ix < 0x40038000)
                {
                    id = 2;
                    x = (x - 1.5) / (1.0 + 1.5 * x);
                }
                else
                {
                    id = 3;
                    x = -1.0 / x;
                }
            }

            var z = x * x;
            var w = z * z;
            var s1 = z * (AT[0] + w * (AT[2] + w * (AT[4] + w * (AT[6] + w * (AT[8] + w * AT[10])))));
            var s2 = w * (AT[1] + w * (AT[3] + w * (AT[5] + w * (AT[7] + w * AT[9]))));
            if (id < 0) return x - x * (s1 + s2);

            z = AtanHi[id] - ((x * (s1 + s2) - AtanLo[id]) - x);
            return hx < 0 ? -z : z;
        }

        // ---- e_atan2.c ----
        const double Tiny = 1.0e-300, PiO4 = 7.8539816339744827900E-01, PiO2 = 1.5707963267948965580E+00,
            Pi = 3.1415926535897931160E+00, PiLo = 1.2246467991473531772E-16;

        public static double Atan2(double y, double x)
        {
            if (double.IsNaN(x) || double.IsNaN(y)) return x + y;

            int hx = High(x), hy = High(y);
            uint lx = Low(x), ly = Low(y);
            int ix = hx & 0x7fffffff, iy = hy & 0x7fffffff;

            if (((hx - 0x3ff00000) | (int)lx) == 0) return Atan(y);

            var m = ((hy >> 31) & 1) | ((hx >> 30) & 2);

            if ((iy | (int)ly) == 0)
            {
                switch (m)
                {
                    case 0:
                    case 1: return y;
                    case 2: return Pi + Tiny;
                    default: return -Pi - Tiny;
                }
            }

            if ((ix | (int)lx) == 0) return hy < 0 ? -PiO2 - Tiny : PiO2 + Tiny;

            if (ix == 0x7ff00000)
            {
                if (iy == 0x7ff00000)
                {
                    switch (m)
                    {
                        case 0: return PiO4 + Tiny;
                        case 1: return -PiO4 - Tiny;
                        case 2: return 3.0 * PiO4 + Tiny;
                        default: return -3.0 * PiO4 - Tiny;
                    }
                }

                switch (m)
                {
                    case 0: return 0.0;
                    case 1: return -0.0;
                    case 2: return Pi + Tiny;
                    default: return -Pi - Tiny;
                }
            }

            if (iy == 0x7ff00000) return hy < 0 ? -PiO2 - Tiny : PiO2 + Tiny;

            var k = (iy - ix) >> 20;
            double z;
            if (k > 60)
            {
                z = PiO2 + 0.5 * PiLo;
                m &= 1;
            }
            else if (hx < 0 && k < -60) z = 0.0;
            else z = Atan(Math.Abs(y / x));

            switch (m)
            {
                case 0: return z;
                case 1: return -z;
                case 2: return Pi - (z - PiLo);
                default: return (z - PiLo) - Pi;
            }
        }
    }
}
