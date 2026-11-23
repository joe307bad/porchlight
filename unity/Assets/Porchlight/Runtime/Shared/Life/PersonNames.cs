// Everyone in a town has a name: picked from faker-js's English first and last
// names (PersonNames.Lists.cs, written by Tools~/names/generate.mjs), seeded by
// the town and who they are, so the same person has the same name in every
// player's game, on the server and in Manage, and keeps it day after day. The
// town's people (TownLife.people) by their id; its wanderers, out for a walk
// and nobody's record in the town's life, by their place among them. Pure.

using Porchlight.Core;

namespace Porchlight.Life
{
    public static partial class PersonNames
    {
        /// <summary>One of the town's people (TownLife.people[id]): "Ada Brekke".</summary>
        public static string Of(string townSeed, int person) => Pick($"{townSeed}::person::{person}");

        /// <summary>The town's k-th wanderer (the first records of the townsfolk, before its people's).</summary>
        public static string Wanderer(string townSeed, int k) => Pick($"{townSeed}::wanderer::{k}");

        /// <summary>What a wanderer is doing, whenever you meet one.</summary>
        public const string WANDERING = "Out for a walk";

        public static int FirstCount => FIRST.Length;
        public static int LastCount => LAST.Length;

        static string Pick(string key)
        {
            var r = new Rng(key);
            string first = FIRST[(int)(r.Next() * FIRST.Length)];
            return $"{first} {LAST[(int)(r.Next() * LAST.Length)]}";
        }
    }
}
