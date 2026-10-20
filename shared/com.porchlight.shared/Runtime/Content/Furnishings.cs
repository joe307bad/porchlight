using System;
using System.Collections.Generic;

namespace Porchlight.Shared.Content
{
    /// <summary>Where a piece of furniture may go.</summary>
    public enum FurniturePlacement
    {
        /// <summary>Anywhere on the floor. Near a wall it snaps back against it.</summary>
        Floor,

        /// <summary>With its back against a wall, like a kitchen counter.</summary>
        AgainstWall,

        /// <summary>Standing on a dresser or hung on a wall (a TV).</summary>
        DresserOrWall,
    }

    /// <summary>What happens when a character walks into it.</summary>
    public enum FurnitureAction
    {
        None,
        Sit,
        LieDown,
    }

    /// <summary>
    /// A kind of furniture. Sizes are its footprint facing local +Z, chosen so half of each is a
    /// whole number of 5 cm steps; that way snapped positions survive rounding to
    /// <see cref="Model.TownLimits.PositionQuantum"/>. Ids are saved in towns, so never rename one.
    /// </summary>
    public sealed class FurnitureType
    {
        public FurnitureType(string id, string displayName, float width, float depth, float height,
            FurniturePlacement placement, FurnitureAction action, bool snapsToWalls, string color, string accent)
        {
            Id = id;
            DisplayName = displayName;
            Width = width;
            Depth = depth;
            Height = height;
            Placement = placement;
            Action = action;
            SnapsToWalls = snapsToWalls;
            Color = color;
            Accent = accent;
        }

        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>Side to side, along local X, in metres.</summary>
        public float Width { get; }

        /// <summary>Back to front, along local Z, in metres.</summary>
        public float Depth { get; }

        public float Height { get; }
        public FurniturePlacement Placement { get; }
        public FurnitureAction Action { get; }

        /// <summary>Near a wall, the builder turns it round and pushes its back against the wall.</summary>
        public bool SnapsToWalls { get; }

        /// <summary>Hex colours (#RRGGBB) for the placeholder look.</summary>
        public string Color { get; }
        public string Accent { get; }
    }

    /// <summary>A door or a window. Heights are above the floor it's on.</summary>
    public sealed class OpeningType
    {
        public OpeningType(string id, string displayName, float width, float bottom, float top)
        {
            Id = id;
            DisplayName = displayName;
            Width = width;
            Bottom = bottom;
            Top = top;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public float Width { get; }
        public float Bottom { get; }
        public float Top { get; }
        public bool IsDoor => Bottom <= 0f;
    }

    /// <summary>Everything that can go inside a cottage.</summary>
    public static class Furnishings
    {
        public const string Door = "door";
        public const string Window = "window";
        public const string Dresser = "dresser";
        public const string Tv = "tv";

        public static readonly IReadOnlyList<OpeningType> Openings = new[]
        {
            new OpeningType(Door, "Door", 0.9f, 0f, 2.1f),
            new OpeningType(Window, "Window", 1f, 0.9f, 2f),
        };

        public static readonly IReadOnlyList<FurnitureType> Furniture = new[]
        {
            new FurnitureType("couch", "Couch", 2f, 0.9f, 0.85f, FurniturePlacement.Floor, FurnitureAction.Sit, true, "#5E7FB8", "#86A3D4"),
            new FurnitureType(Dresser, "Dresser", 1.2f, 0.5f, 0.9f, FurniturePlacement.Floor, FurnitureAction.None, true, "#9A6B47", "#E2C08D"),
            new FurnitureType("dining", "Dining table", 1.4f, 1.8f, 0.75f, FurniturePlacement.Floor, FurnitureAction.Sit, false, "#B5835A", "#7E5638"),
            new FurnitureType("counter", "Counter", 1f, 0.6f, 0.9f, FurniturePlacement.AgainstWall, FurnitureAction.None, true, "#7F9DAE", "#EDE6D8"),
            new FurnitureType(Tv, "TV", 1f, 0.1f, 0.6f, FurniturePlacement.DresserOrWall, FurnitureAction.None, true, "#2A2B30", "#4F6D8F"),
            new FurnitureType("bed", "Bed", 1.4f, 2f, 0.55f, FurniturePlacement.Floor, FurnitureAction.LieDown, true, "#F4F0E6", "#D9726B"),
            new FurnitureType("toilet", "Toilet", 0.5f, 0.7f, 0.75f, FurniturePlacement.Floor, FurnitureAction.Sit, true, "#F7F7F4", "#C9D3D8"),
            new FurnitureType("shower", "Shower", 0.9f, 0.9f, 2.1f, FurniturePlacement.Floor, FurnitureAction.None, true, "#DCE8EE", "#9FB8C4"),
            new FurnitureType("sink", "Bathroom sink", 0.5f, 0.5f, 0.9f, FurniturePlacement.Floor, FurnitureAction.None, true, "#F7F7F4", "#A8B5BC"),
        };

        private static readonly Dictionary<string, FurnitureType> FurnitureById = Index(Furniture, f => f.Id);
        private static readonly Dictionary<string, OpeningType> OpeningsById = Index(Openings, o => o.Id);

        public static bool TryGetFurniture(string id, out FurnitureType type)
        {
            type = null;
            return id != null && FurnitureById.TryGetValue(id, out type);
        }

        public static bool TryGetOpening(string id, out OpeningType type)
        {
            type = null;
            return id != null && OpeningsById.TryGetValue(id, out type);
        }

        private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key)
        {
            var map = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var item in items) map.Add(key(item), item);
            return map;
        }
    }
}
