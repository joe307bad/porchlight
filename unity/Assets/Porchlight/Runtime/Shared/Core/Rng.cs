// Seeded randomness. Every random number in the generator comes from here;
// System.Random is never used. The same stream on every platform, so everyone in a room grows the same town.

using System;
using System.Collections.Generic;

namespace Porchlight.Core
{
    public sealed class Rng
    {
        uint a;

        public Rng(string seed)
        {
            a = HashString(seed);
        }

        /// <summary>32-bit string hash (xmur3 finalizer), used to turn seeds into PRNG state.</summary>
        public static uint HashString(string str)
        {
            unchecked
            {
                uint h = 1779033703u ^ (uint)str.Length;
                for (int i = 0; i < str.Length; i++)
                {
                    h = (h ^ str[i]) * 3432918353u;
                    h = (h << 13) | (h >> 19);
                }
                h = (h ^ (h >> 16)) * 2246822507u;
                h = (h ^ (h >> 13)) * 3266489909u;
                return h ^ (h >> 16);
            }
        }

        /// <summary>Uniform double in [0, 1) (mulberry32).</summary>
        public double Next()
        {
            unchecked
            {
                a += 0x6d2b79f5u;
                uint t = a;
                t = (t ^ (t >> 15)) * (t | 1u);
                t ^= t + (t ^ (t >> 7)) * (t | 61u);
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }

        /// <summary>Uniform double in [min, max).</summary>
        public double Range(double min, double max) => min + Next() * (max - min);

        /// <summary>Uniform integer in [min, max] (inclusive).</summary>
        public int Int(int min, int max) => min + (int)Math.Floor(Next() * (max - min + 1));

        public bool Chance(double p) => Next() < p;

        public T Pick<T>(IReadOnlyList<T> items)
        {
            if (items.Count == 0) throw new InvalidOperationException("pick from empty list");
            return items[(int)Math.Floor(Next() * items.Count)];
        }

        public List<T> Shuffle<T>(List<T> items)
        {
            for (int i = items.Count - 1; i > 0; i--)
            {
                int j = (int)Math.Floor(Next() * (i + 1));
                (items[i], items[j]) = (items[j], items[i]);
            }
            return items;
        }

        /// <summary>Independent stream for one pipeline stage, so one stage's appetite never reshuffles the others.</summary>
        public static Rng Stage(string seed, string stage) => new Rng(seed + "::" + stage);
    }
}
