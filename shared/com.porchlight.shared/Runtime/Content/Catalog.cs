using System;
using System.Collections.Generic;

namespace Porchlight.Shared.Content
{
    public enum RoofStyle
    {
        Flat,

        /// <summary>Pitched roof with the ridge along the local X axis, so gabled types need Width >= Depth.</summary>
        Gabled,
    }

    public enum Decoration
    {
        None,
        Trees,
        Chimney,
    }

    /// <summary>
    /// The gameplay definition of a building. Clients look up visuals by <see cref="Id"/>; the server
    /// uses the footprint and stats for validation and metrics. Ids are stored in saved towns, so never
    /// rename one. Add a new type instead.
    /// </summary>
    public sealed class BuildingType
    {
        public BuildingType(
            string id, string displayName,
            float width, float depth, float height,
            int population, int jobs,
            string wallColor, string roofColor,
            RoofStyle roof, Decoration decoration = Decoration.None, bool windowBands = false)
        {
            Id = id;
            DisplayName = displayName;
            Width = width;
            Depth = depth;
            Height = height;
            Population = population;
            Jobs = jobs;
            WallColor = wallColor;
            RoofColor = roofColor;
            Roof = roof;
            Decoration = decoration;
            WindowBands = windowBands;
        }

        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>Footprint along local X, in metres.</summary>
        public float Width { get; }

        /// <summary>Footprint along local Z (front to back), in metres.</summary>
        public float Depth { get; }

        public float Height { get; }
        public int Population { get; }
        public int Jobs { get; }

        /// <summary>Hex colours (#RRGGBB) for the placeholder low-poly look.</summary>
        public string WallColor { get; }
        public string RoofColor { get; }

        public RoofStyle Roof { get; }
        public Decoration Decoration { get; }
        public bool WindowBands { get; }
    }

    public sealed class RoadType
    {
        public RoadType(string id, string displayName, float width, string color)
        {
            Id = id;
            DisplayName = displayName;
            Width = width;
            Color = color;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public float Width { get; }
        public string Color { get; }
    }

    public static class Catalog
    {
        public static readonly IReadOnlyList<BuildingType> Buildings = new[]
        {
            // Two storeys, to match its inside (see Rules.CottageInterior).
            new BuildingType("house_small", "Cottage", 7f, 6f, 5.45f, population: 4, jobs: 0,
                "#F2E3C6", "#C0583F", RoofStyle.Gabled),
            new BuildingType("house_large", "Townhouse", 10f, 8f, 6f, population: 10, jobs: 0,
                "#E9D8B4", "#5F6F82", RoofStyle.Gabled, Decoration.Chimney),
            new BuildingType("apartment", "Apartments", 14f, 12f, 15f, population: 48, jobs: 0,
                "#D9A47A", "#8C6E5A", RoofStyle.Flat, windowBands: true),
            new BuildingType("shop", "Corner Shop", 10f, 8f, 4.5f, population: 0, jobs: 8,
                "#9CC5B9", "#5E7F78", RoofStyle.Flat),
            new BuildingType("office", "Office Tower", 14f, 14f, 24f, population: 0, jobs: 80,
                "#A9B8C9", "#6A7787", RoofStyle.Flat, windowBands: true),
            new BuildingType("factory", "Workshop", 20f, 14f, 8f, population: 0, jobs: 30,
                "#B8B0A2", "#7D746A", RoofStyle.Gabled, Decoration.Chimney),
            new BuildingType("park", "Park", 14f, 14f, 0.3f, population: 0, jobs: 0,
                "#7FB069", "#8BC173", RoofStyle.Flat, Decoration.Trees),
        };

        public static readonly IReadOnlyList<RoadType> Roads = new[]
        {
            new RoadType("street", "Street", 6f, "#4B4E57"),
            new RoadType("avenue", "Avenue", 10f, "#43464E"),
        };

        private static readonly Dictionary<string, BuildingType> BuildingsById = Index(Buildings, b => b.Id);
        private static readonly Dictionary<string, RoadType> RoadsById = Index(Roads, r => r.Id);

        public static bool TryGetBuilding(string id, out BuildingType type)
        {
            type = null;
            return id != null && BuildingsById.TryGetValue(id, out type);
        }

        public static bool TryGetRoad(string id, out RoadType type)
        {
            type = null;
            return id != null && RoadsById.TryGetValue(id, out type);
        }

        private static Dictionary<string, T> Index<T>(IEnumerable<T> items, Func<T, string> key)
        {
            var map = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                map.Add(key(item), item);
            }

            return map;
        }
    }
}
