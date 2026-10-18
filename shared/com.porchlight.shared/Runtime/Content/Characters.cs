using System;
using System.Collections.Generic;

namespace Porchlight.Shared.Content
{
    public enum HairStyle
    {
        /// <summary>Shoulder-length curls with a little top knot.</summary>
        CurlyTopKnot,

        /// <summary>Short, straight hair with a sprout of a ponytail on top.</summary>
        ShortSprout,
    }

    public enum ShirtPattern
    {
        /// <summary>Tiny flowers all over, on a tank top.</summary>
        Flowers,

        /// <summary>A giraffe-print panel down the front, with cap sleeves.</summary>
        GiraffePanel,
    }

    /// <summary>
    /// A playable character. Clients build the low-poly model from these colours; the server will use
    /// <see cref="Id"/> once players can see each other. Ids get saved, so never rename one.
    /// All colours are hex (#RRGGBB).
    /// </summary>
    public sealed class CharacterType
    {
        public CharacterType(
            string id, string displayName, string tagline, float height,
            HairStyle hair, string hairColor, string hairTieColor,
            string skinColor, string shirtColor, ShirtPattern pattern, string shirtPatternColor,
            string[] shortsStripes, string shoeColor, string glassesColor = null)
        {
            Id = id;
            DisplayName = displayName;
            Tagline = tagline;
            Height = height;
            Hair = hair;
            HairColor = hairColor;
            HairTieColor = hairTieColor;
            SkinColor = skinColor;
            ShirtColor = shirtColor;
            Pattern = pattern;
            ShirtPatternColor = shirtPatternColor;
            ShortsStripes = shortsStripes;
            ShoeColor = shoeColor;
            GlassesColor = glassesColor;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Tagline { get; }

        /// <summary>Standing height in metres, top of the hair excluded.</summary>
        public float Height { get; }

        public HairStyle Hair { get; }
        public string HairColor { get; }
        public string HairTieColor { get; }
        public string SkinColor { get; }
        public string ShirtColor { get; }
        public ShirtPattern Pattern { get; }

        /// <summary>The little flowers or giraffe spots on the shirt.</summary>
        public string ShirtPatternColor { get; }

        /// <summary>Horizontal bands from the waistband down. One entry for plain shorts.</summary>
        public IReadOnlyList<string> ShortsStripes { get; }

        /// <summary>Null means barefoot.</summary>
        public string ShoeColor { get; }

        /// <summary>Null means no glasses.</summary>
        public string GlassesColor { get; }
    }

    public static class Characters
    {
        public static readonly IReadOnlyList<CharacterType> All = new[]
        {
            new CharacterType("ellie", "Ellie", "Curls, glasses and a big grin", height: 1.25f,
                HairStyle.CurlyTopKnot, hairColor: "#A8743F", hairTieColor: "#F4D9E4",
                skinColor: "#F3C9AE", shirtColor: "#F7F3EA", ShirtPattern.Flowers, shirtPatternColor: "#B9A2E0",
                shortsStripes: new[] { "#8FB3DA" }, shoeColor: "#F08FB0", glassesColor: "#B0628F"),
            new CharacterType("evie", "Evie", "Ready to sing for the whole town", height: 1.0f,
                HairStyle.ShortSprout, hairColor: "#E6C987", hairTieColor: "#F5D547",
                skinColor: "#F6CDB6", shirtColor: "#F7A8C8", ShirtPattern.GiraffePanel, shirtPatternColor: "#E8458B",
                shortsStripes: new[] { "#F7A6C4", "#FFD98A", "#B6E3A8", "#A9D4F5", "#D7B8F0" }, shoeColor: null),
        };

        private static readonly Dictionary<string, CharacterType> ById = Index();

        public static bool TryGet(string id, out CharacterType type)
        {
            type = null;
            return id != null && ById.TryGetValue(id, out type);
        }

        private static Dictionary<string, CharacterType> Index()
        {
            var map = new Dictionary<string, CharacterType>(StringComparer.Ordinal);
            foreach (var character in All)
            {
                map.Add(character.Id, character);
            }

            return map;
        }
    }
}
