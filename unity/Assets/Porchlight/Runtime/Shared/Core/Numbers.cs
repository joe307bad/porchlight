// Number rules the generator relies on, the same on every platform so that
// everyone in a room grows the same town from the same seed: rounding with
// halves up, sign, 32-bit multiply, an exactly specified hypot, number
// formatting and parsing that don't depend on culture, and a stable sort
// (Array.Sort isn't stable).

using System;
using System.Collections.Generic;

namespace Porchlight.Core
{
    public static class Numbers
    {
        public const double Infinity = double.PositiveInfinity;

        /// <summary>Round to the nearest whole number, halves toward +infinity.</summary>
        public static double Round(double x) => Math.Floor(x + 0.5);

        public static int RoundInt(double x) => (int)Math.Floor(x + 0.5);

        /// <summary>Math.sign (0 for 0 and NaN for NaN, as a double).</summary>
        public static double Sign(double x) => x > 0 ? 1 : x < 0 ? -1 : x;

        public static int Imul(int a, int b) => unchecked(a * b);

        public static int Imul(uint a, uint b) => unchecked((int)(a * b));

        public static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        /// <summary>sqrt(a² + b²) by a normalised Kahan sum, so distances agree to the last bit everywhere.</summary>
        public static double Hypot(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b))
            {
                if (double.IsInfinity(a) || double.IsInfinity(b)) return double.PositiveInfinity;
                return double.NaN;
            }
            double x = Math.Abs(a), y = Math.Abs(b);
            double max = x > y ? x : y;
            if (double.IsPositiveInfinity(max)) return double.PositiveInfinity;
            if (max == 0) return 0;
            double sum = 0, compensation = 0;
            double n = x / max;
            double summand = n * n - compensation;
            double preliminary = sum + summand;
            compensation = (preliminary - sum) - summand;
            sum = preliminary;
            n = y / max;
            summand = n * n - compensation;
            preliminary = sum + summand;
            sum = preliminary;
            return Math.Sqrt(sum) * max;
        }

        public static double Hypot(double a, double b, double c)
        {
            double x = Math.Abs(a), y = Math.Abs(b), z = Math.Abs(c);
            if (double.IsNaN(x) || double.IsNaN(y) || double.IsNaN(z))
            {
                if (double.IsInfinity(x) || double.IsInfinity(y) || double.IsInfinity(z)) return double.PositiveInfinity;
                return double.NaN;
            }
            double max = Math.Max(x, Math.Max(y, z));
            if (double.IsPositiveInfinity(max)) return double.PositiveInfinity;
            if (max == 0) return 0;
            double sum = 0, compensation = 0;
            foreach (var v in new[] { x, y, z })
            {
                double n = v / max;
                double summand = n * n - compensation;
                double preliminary = sum + summand;
                compensation = (preliminary - sum) - summand;
                sum = preliminary;
            }
            return Math.Sqrt(sum) * max;
        }

        /// <summary>Stable sort in place by a JS-style comparator (negative, zero or positive; NaN counts as zero).</summary>
        public static void StableSort<T>(List<T> list, Func<T, T, double> compare)
        {
            if (list.Count < 2) return;
            var a = list.ToArray();
            var tmp = new T[a.Length];
            MergeSort(a, tmp, 0, a.Length, compare);
            for (int i = 0; i < a.Length; i++) list[i] = a[i];
        }

        public static void StableSort<T>(T[] a, Func<T, T, double> compare)
        {
            if (a.Length < 2) return;
            MergeSort(a, new T[a.Length], 0, a.Length, compare);
        }

        static void MergeSort<T>(T[] a, T[] tmp, int lo, int hi, Func<T, T, double> compare)
        {
            if (hi - lo < 2) return;
            if (hi - lo <= 12)
            {
                // Insertion sort: stable, and quick on short runs.
                for (int i = lo + 1; i < hi; i++)
                {
                    var v = a[i];
                    int j = i - 1;
                    while (j >= lo && compare(a[j], v) > 0)
                    {
                        a[j + 1] = a[j];
                        j--;
                    }
                    a[j + 1] = v;
                }
                return;
            }
            int mid = (lo + hi) >> 1;
            MergeSort(a, tmp, lo, mid, compare);
            MergeSort(a, tmp, mid, hi, compare);
            if (!(compare(a[mid - 1], a[mid]) > 0)) return;
            int l = lo, r = mid, k = lo;
            while (l < mid && r < hi) tmp[k++] = compare(a[r], a[l]) < 0 ? a[r++] : a[l++];
            while (l < mid) tmp[k++] = a[l++];
            while (r < hi) tmp[k++] = a[r++];
            Array.Copy(tmp, lo, a, lo, hi - lo);
        }

        /// <summary>Float32Array.prototype.sort (numeric ascending; NaN last).</summary>
        public static void SortNumeric(float[] values) => Array.Sort(values, (p, q) => p.CompareTo(q));

        /// <summary>JS `a % n`, then made non-negative: ((a % n) + n) % n.</summary>
        public static double Mod(double a, double n) => ((a % n) + n) % n;

        /// <summary>Number.prototype.toFixed for the small, finite values the app formats.</summary>
        public static string ToFixed(double v, int digits) =>
            v.ToString("F" + digits, System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>String(number) for the values the app writes into URLs (shortest round-trip form).</summary>
        public static string NumberToString(double v)
        {
            if (double.IsNaN(v)) return "NaN";
            if (double.IsPositiveInfinity(v)) return "Infinity";
            if (double.IsNegativeInfinity(v)) return "-Infinity";
            if (v == Math.Floor(v) && Math.Abs(v) < 1e21) return ((long)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
            // Shortest digits that read back as the same double.
            for (int p = 1; p <= 17; p++)
            {
                var s = v.ToString("G" + p, System.Globalization.CultureInfo.InvariantCulture);
                if (double.Parse(s, System.Globalization.CultureInfo.InvariantCulture) == v)
                {
                    if (s.Contains("E")) break;
                    return s;
                }
            }
            return v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Number(string) for plain decimal input (NaN if it isn't a number).</summary>
        public static double ParseNumber(string raw)
        {
            if (raw == null) return double.NaN;
            var s = raw.Trim();
            if (s.Length == 0) return 0;
            return double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : double.NaN;
        }
    }
}
