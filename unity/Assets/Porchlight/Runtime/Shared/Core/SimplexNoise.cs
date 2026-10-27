// 2D simplex noise, ported line for line from simplex-noise 4.0.3 (Jonas
// Wagner, MIT; after Stefan Gustavson's Java), so a seeded stream builds the
// same permutation table and the same field on every platform.

using System;

namespace Porchlight.Core
{
    public sealed class SimplexNoise2D
    {
        static readonly double SQRT3 = Math.Sqrt(3.0);
        static readonly double F2 = 0.5 * (SQRT3 - 1.0);
        static readonly double G2 = (3.0 - SQRT3) / 6.0;

        static readonly double[] Grad2 =
        {
            1, 1, -1, 1, 1, -1,
            -1, -1, 1, 0, -1, 0,
            1, 0, -1, 0, 0, 1,
            0, -1, 0, 1, 0, -1,
        };

        readonly byte[] perm;
        readonly double[] permGrad2x;
        readonly double[] permGrad2y;

        public SimplexNoise2D(Func<double> random)
        {
            perm = BuildPermutationTable(random);
            permGrad2x = new double[perm.Length];
            permGrad2y = new double[perm.Length];
            for (int i = 0; i < perm.Length; i++)
            {
                permGrad2x[i] = Grad2[(perm[i] % 12) * 2];
                permGrad2y[i] = Grad2[(perm[i] % 12) * 2 + 1];
            }
        }

        static byte[] BuildPermutationTable(Func<double> random)
        {
            const int tableSize = 512;
            var p = new byte[tableSize];
            for (int i = 0; i < tableSize / 2; i++) p[i] = (byte)i;
            for (int i = 0; i < tableSize / 2 - 1; i++)
            {
                // ~~x truncates toward zero, as (int) does.
                int r = i + (int)(random() * (256 - i));
                byte aux = p[i];
                p[i] = p[r];
                p[r] = aux;
            }
            for (int i = 256; i < tableSize; i++) p[i] = p[i - 256];
            return p;
        }

        static int FastFloor(double x) => (int)Math.Floor(x);

        public double Noise(double x, double y)
        {
            double n0 = 0, n1 = 0, n2 = 0;
            double s = (x + y) * F2;
            int i = FastFloor(x + s);
            int j = FastFloor(y + s);
            double t = (i + j) * G2;
            double X0 = i - t;
            double Y0 = j - t;
            double x0 = x - X0;
            double y0 = y - Y0;
            int i1, j1;
            if (x0 > y0)
            {
                i1 = 1;
                j1 = 0;
            }
            else
            {
                i1 = 0;
                j1 = 1;
            }
            double x1 = x0 - i1 + G2;
            double y1 = y0 - j1 + G2;
            double x2 = x0 - 1.0 + 2.0 * G2;
            double y2 = y0 - 1.0 + 2.0 * G2;
            int ii = i & 255;
            int jj = j & 255;
            double t0 = 0.5 - x0 * x0 - y0 * y0;
            if (t0 >= 0)
            {
                int gi0 = ii + perm[jj];
                t0 *= t0;
                n0 = t0 * t0 * (permGrad2x[gi0] * x0 + permGrad2y[gi0] * y0);
            }
            double t1 = 0.5 - x1 * x1 - y1 * y1;
            if (t1 >= 0)
            {
                int gi1 = ii + i1 + perm[jj + j1];
                t1 *= t1;
                n1 = t1 * t1 * (permGrad2x[gi1] * x1 + permGrad2y[gi1] * y1);
            }
            double t2 = 0.5 - x2 * x2 - y2 * y2;
            if (t2 >= 0)
            {
                int gi2 = ii + 1 + perm[jj + 1];
                t2 *= t2;
                n2 = t2 * t2 * (permGrad2x[gi2] * x2 + permGrad2y[gi2] * y2);
            }
            return 70.0 * (n0 + n1 + n2);
        }
    }
}
