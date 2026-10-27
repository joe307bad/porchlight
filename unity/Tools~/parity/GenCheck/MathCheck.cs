// Compares JsMath (and the platform's Math) with Node's, from ../math.mjs.

using System;
using System.IO;
using Porchlight.Core;

static class MathCheck
{
    public static int Run(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var d = new double[bytes.Length / 8];
        Buffer.BlockCopy(bytes, 0, d, 0, bytes.Length);
        var names = new[] { "atan2", "acos", "tanh", "sin", "cos", "atan", "exp", "log", "pow" };
        var fns = new Func<double, double, double>[]
        {
            JsMath.Atan2, (a, _) => JsMath.Acos(a), (a, _) => JsMath.Tanh(a), (a, _) => JsMath.Sin(a), (a, _) => JsMath.Cos(a),
            (a, _) => JsMath.Atan(a), (a, _) => Math.Exp(a), (a, _) => Math.Log(a), Math.Pow,
        };
        var bad = new int[names.Length];
        var platform = new Func<double, double, double>[] { Math.Atan2, (a, _) => Math.Acos(a), (a, _) => Math.Tanh(a) };
        var platformBad = new int[3];
        int samples = d.Length / 3 / names.Length;
        for (int i = 0, k = 0; i < samples; i++)
        {
            for (int f = 0; f < names.Length; f++, k += 3)
            {
                double a = d[k], b = d[k + 1], want = d[k + 2];
                double got = fns[f](a, b);
                if (BitConverter.DoubleToInt64Bits(got) != BitConverter.DoubleToInt64Bits(want) && !(double.IsNaN(got) && double.IsNaN(want)))
                {
                    if (bad[f]++ < 3) Console.WriteLine($"{names[f]}({a:R}, {b:R}) = {got:R}, node {want:R}");
                }
                if (f < 3 && platform[f](a, b) != want) platformBad[f]++;
            }
        }
        for (int f = 0; f < names.Length; f++) Console.WriteLine($"{names[f]}: {bad[f]} of {samples} differ");
        for (int f = 0; f < 3; f++) Console.WriteLine($"(platform {names[f]}: {platformBad[f]} differ)");
        return 0;
    }
}
