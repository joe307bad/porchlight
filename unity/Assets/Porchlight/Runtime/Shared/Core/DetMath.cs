// Deterministic transcendental functions (fdlibm's), so the generator gives
// the same doubles on every platform Unity builds for, whatever its C library
// rounds to: atan, atan2, acos, tanh and expm1. (sin and cos come from the
// platform; the places that use them don't depend on the last bit.)
//
// fdlibm: Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
// Developed at SunSoft, a Sun Microsystems, Inc. business. Permission to use,
// copy, modify, and distribute this software is freely granted, provided that
// this notice is preserved.

using System;

namespace Porchlight.Core
{
    public static class DetMath
    {
        static int High(double x) => (int)(BitConverter.DoubleToInt64Bits(x) >> 32);
        static uint Low(double x) => (uint)BitConverter.DoubleToInt64Bits(x);

        static double FromWords(int hi, uint lo) => BitConverter.Int64BitsToDouble(((long)hi << 32) | lo);

        static double WithLow(double x, uint lo) => FromWords(High(x), lo);

        static double WithHigh(double x, int hi) => FromWords(hi, Low(x));

        static readonly double[] atanhi =
        {
            4.63647609000806093515e-01,
            7.85398163397448278999e-01,
            9.82793723247329054082e-01,
            1.57079632679489655800e+00,
        };

        static readonly double[] atanlo =
        {
            2.26987774529616870924e-17,
            3.06161699786838301793e-17,
            1.39033110312309984516e-17,
            6.12323399573676603587e-17,
        };

        static readonly double[] aT =
        {
            3.33333333333329318027e-01,
            -1.99999999998764832476e-01,
            1.42857142725034663711e-01,
            -1.11111104054623557880e-01,
            9.09088713343650656196e-02,
            -7.69187620504482999495e-02,
            6.66107313738753120669e-02,
            -5.83357013379057348645e-02,
            4.97687799461593236017e-02,
            -3.65315727442169155270e-02,
            1.62858201153657823623e-02,
        };

        public static double Atan(double x)
        {
            int hx = High(x);
            int ix = hx & 0x7fffffff;
            int id;
            if (ix >= 0x44100000)
            {
                // |x| >= 2^66
                uint low = Low(x);
                if (ix > 0x7ff00000 || (ix == 0x7ff00000 && low != 0)) return x + x;
                return hx > 0 ? atanhi[3] + atanlo[3] : -atanhi[3] - atanlo[3];
            }
            if (ix < 0x3fdc0000)
            {
                // |x| < 0.4375
                if (ix < 0x3e400000) return x; // |x| < 2^-27
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
            double z = x * x;
            double w = z * z;
            double s1 = z * (aT[0] + w * (aT[2] + w * (aT[4] + w * (aT[6] + w * (aT[8] + w * aT[10])))));
            double s2 = w * (aT[1] + w * (aT[3] + w * (aT[5] + w * (aT[7] + w * aT[9]))));
            if (id < 0) return x - x * (s1 + s2);
            z = atanhi[id] - ((x * (s1 + s2) - atanlo[id]) - x);
            return hx < 0 ? -z : z;
        }

        const double tiny = 1.0e-300;
        const double pi_o_4 = 7.8539816339744827900E-01;
        const double pi_o_2 = 1.5707963267948965580E+00;
        const double pi = 3.1415926535897931160E+00;
        const double pi_lo = 1.2246467991473531772E-16;

        public static double Atan2(double y, double x)
        {
            int hx = High(x);
            uint lx = Low(x);
            int ix = hx & 0x7fffffff;
            int hy = High(y);
            uint ly = Low(y);
            int iy = hy & 0x7fffffff;
            if ((ix | (int)((lx | (uint)-(int)lx) >> 31)) > 0x7ff00000 || (iy | (int)((ly | (uint)-(int)ly) >> 31)) > 0x7ff00000) return x + y;
            if (((hx - 0x3ff00000) | (int)lx) == 0) return Atan(y);
            int m = ((hy >> 31) & 1) | ((hx >> 30) & 2);

            if ((iy | (int)ly) == 0)
            {
                switch (m)
                {
                    case 0:
                    case 1: return y;
                    case 2: return pi + tiny;
                    default: return -pi - tiny;
                }
            }
            if ((ix | (int)lx) == 0) return hy < 0 ? -pi_o_2 - tiny : pi_o_2 + tiny;

            if (ix == 0x7ff00000)
            {
                if (iy == 0x7ff00000)
                {
                    switch (m)
                    {
                        case 0: return pi_o_4 + tiny;
                        case 1: return -pi_o_4 - tiny;
                        case 2: return 3.0 * pi_o_4 + tiny;
                        default: return -3.0 * pi_o_4 - tiny;
                    }
                }
                switch (m)
                {
                    case 0: return 0.0;
                    case 1: return -0.0;
                    case 2: return pi + tiny;
                    default: return -pi - tiny;
                }
            }
            if (iy == 0x7ff00000) return hy < 0 ? -pi_o_2 - tiny : pi_o_2 + tiny;

            int k = (iy - ix) >> 20;
            double z;
            if (k > 60)
            {
                z = pi_o_2 + 0.5 * pi_lo;
                m &= 1;
            }
            else if (hx < 0 && k < -60) z = 0.0;
            else z = Atan(Math.Abs(y / x));
            switch (m)
            {
                case 0: return z;
                case 1: return -z;
                case 2: return pi - (z - pi_lo);
                default: return (z - pi_lo) - pi;
            }
        }

        const double pio2_hi = 1.57079632679489655800e+00;
        const double pio2_lo = 6.12323399573676603587e-17;
        const double pS0 = 1.66666666666666657415e-01;
        const double pS1 = -3.25565818622400915405e-01;
        const double pS2 = 2.01212532134862925881e-01;
        const double pS3 = -4.00555345006794114027e-02;
        const double pS4 = 7.91534994289814532176e-04;
        const double pS5 = 3.47933107596021167570e-05;
        const double qS1 = -2.40339491173441421878e+00;
        const double qS2 = 2.02094576023350569471e+00;
        const double qS3 = -6.88283971605453293030e-01;
        const double qS4 = 7.70381505559019352791e-02;

        public static double Acos(double x)
        {
            int hx = High(x);
            int ix = hx & 0x7fffffff;
            if (ix >= 0x3ff00000)
            {
                uint lx = Low(x);
                if (((ix - 0x3ff00000) | (int)lx) == 0)
                {
                    if (hx > 0) return 0.0;
                    return pi + 2.0 * pio2_lo;
                }
                return (x - x) / (x - x);
            }
            double z, p, q, r, w, s;
            if (ix < 0x3fe00000)
            {
                if (ix <= 0x3c600000) return pio2_hi + pio2_lo;
                z = x * x;
                p = z * (pS0 + z * (pS1 + z * (pS2 + z * (pS3 + z * (pS4 + z * pS5)))));
                q = 1.0 + z * (qS1 + z * (qS2 + z * (qS3 + z * qS4)));
                r = p / q;
                return pio2_hi - (x - (pio2_lo - x * r));
            }
            if (hx < 0)
            {
                z = (1.0 + x) * 0.5;
                p = z * (pS0 + z * (pS1 + z * (pS2 + z * (pS3 + z * (pS4 + z * pS5)))));
                q = 1.0 + z * (qS1 + z * (qS2 + z * (qS3 + z * qS4)));
                s = Math.Sqrt(z);
                r = p / q;
                w = r * s - pio2_lo;
                return pi - 2.0 * (s + w);
            }
            z = (1.0 - x) * 0.5;
            s = Math.Sqrt(z);
            double df = WithLow(s, 0);
            double c = (z - df * df) / (s + df);
            p = z * (pS0 + z * (pS1 + z * (pS2 + z * (pS3 + z * (pS4 + z * pS5)))));
            q = 1.0 + z * (qS1 + z * (qS2 + z * (qS3 + z * qS4)));
            r = p / q;
            w = r * s + c;
            return 2.0 * (df + w);
        }

        const double huge = 1.0e+300;
        const double o_threshold = 7.09782712893383973096e+02;
        const double ln2_hi = 6.93147180369123816490e-01;
        const double ln2_lo = 1.90821492927058770002e-10;
        const double invln2 = 1.44269504088896338700e+00;
        const double Q1 = -3.33333333333331316428e-02;
        const double Q2 = 1.58730158725481460165e-03;
        const double Q3 = -7.93650757867487942473e-05;
        const double Q4 = 4.00821782732936239552e-06;
        const double Q5 = -2.01099218183624371326e-07;

        public static double Expm1(double x)
        {
            int hxs0 = High(x);
            uint hx = (uint)hxs0;
            uint xsb = hx & 0x80000000;
            hx &= 0x7fffffff;
            double y, hi, lo, c = 0, t, e, hxs, hfx, r1, twopk;
            int k;

            if (hx >= 0x4043687A)
            {
                if (hx >= 0x40862E42)
                {
                    if (hx >= 0x7ff00000)
                    {
                        uint low = Low(x);
                        if (((hx & 0xfffff) | low) != 0) return x + x;
                        return xsb == 0 ? x : -1.0;
                    }
                    if (x > o_threshold) return double.PositiveInfinity;
                }
                if (xsb != 0)
                {
                    if (x + tiny < 0.0) return tiny - 1.0;
                }
            }

            if (hx > 0x3fd62e42)
            {
                if (hx < 0x3FF0A2B2)
                {
                    if (xsb == 0)
                    {
                        hi = x - ln2_hi;
                        lo = ln2_lo;
                        k = 1;
                    }
                    else
                    {
                        hi = x + ln2_hi;
                        lo = -ln2_lo;
                        k = -1;
                    }
                }
                else
                {
                    k = (int)(invln2 * x + (xsb == 0 ? 0.5 : -0.5));
                    t = k;
                    hi = x - t * ln2_hi;
                    lo = t * ln2_lo;
                }
                x = hi - lo;
                c = (hi - x) - lo;
            }
            else if (hx < 0x3c900000)
            {
                t = huge + x;
                return x - (t - (huge + x));
            }
            else k = 0;

            hfx = 0.5 * x;
            hxs = x * hfx;
            r1 = 1.0 + hxs * (Q1 + hxs * (Q2 + hxs * (Q3 + hxs * (Q4 + hxs * Q5))));
            t = 3.0 - r1 * hfx;
            e = hxs * ((r1 - t) / (6.0 - x * t));
            if (k == 0) return x - (x * e - hxs);
            twopk = FromWords(0x3ff00000 + (k << 20), 0);
            e = (x * (e - c) - c);
            e -= hxs;
            if (k == -1) return 0.5 * (x - e) - 0.5;
            if (k == 1)
            {
                if (x < -0.25) return -2.0 * (e - (x + 0.5));
                return 1.0 + 2.0 * (x - e);
            }
            if (k <= -2 || k > 56)
            {
                y = 1.0 - (e - x);
                if (k == 1024) y = y * 2.0 * 8.98846567431158e307;
                else y = y * twopk;
                return y - 1.0;
            }
            if (k < 20)
            {
                t = FromWords(0x3ff00000 - (0x200000 >> k), 0);
                y = t - (e - x);
                y = y * twopk;
            }
            else
            {
                t = FromWords((0x3ff - k) << 20, 0);
                y = x - (e + t);
                y += 1.0;
                y = y * twopk;
            }
            return y;
        }

        public static double Tanh(double x)
        {
            int jx = High(x);
            int ix = jx & 0x7fffffff;
            if (ix >= 0x7ff00000)
            {
                if (jx >= 0) return 1.0 / x + 1.0;
                return 1.0 / x - 1.0;
            }
            double t, z;
            if (ix < 0x40360000)
            {
                if (ix < 0x3e300000) return x;
                if (ix >= 0x3ff00000)
                {
                    t = Expm1(2.0 * Math.Abs(x));
                    z = 1.0 - 2.0 / (t + 2.0);
                }
                else
                {
                    t = Expm1(-2.0 * Math.Abs(x));
                    z = -t / (t + 2.0);
                }
            }
            else z = 1.0 - tiny;
            return jx >= 0 ? z : -z;
        }

        /// <summary>The platform's sin.</summary>
        public static double Sin(double x) => Math.Sin(x);

        public static double Cos(double x) => Math.Cos(x);
    }
}
