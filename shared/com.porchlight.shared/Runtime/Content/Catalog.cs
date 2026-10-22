using System;
using System.Collections.Generic;
using Porchlight.Shared.Model;

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
            RoofStyle roof, Decoration decoration = Decoration.None, bool windowBands = false, bool listed = true)
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
            Listed = listed;
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

        /// <summary>Offered in the builder. Retired types stay in the catalog so saved towns still load.</summary>
        public bool Listed { get; }
    }

    /// <summary>A kind of tile surface the builder can paint (see Rules.TownTiles).</summary>
    public sealed class SurfaceType
    {
        public SurfaceType(TileSurface surface, string id, string displayName, string blurb, string color, bool paved)
        {
            Surface = surface;
            Id = id;
            DisplayName = displayName;
            Blurb = blurb;
            Color = color;
            Paved = paved;
        }

        public TileSurface Surface { get; }
        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>A few words for the builder's card.</summary>
        public string Blurb { get; }

        /// <summary>Its main colour, for cards and maps.</summary>
        public string Color { get; }

        /// <summary>Walked or driven on: buildings can't stand on it (plazas excepted), and buildings next to it count as connected.</summary>
        public bool Paved { get; }
    }

    public static class Catalog
    {
        public static readonly IReadOnlyList<BuildingType> Buildings = new[]
        {
            // Two storeys, to match its inside (see Rules.CottageInterior). Each new one gets a
            // furnished layout (Rules.CottageGenerator) and a house number.
            new BuildingType("cottage", "Cottage", 10f, 8f, 5.45f, population: 4, jobs: 0,
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

            // The first cottage, too small for a generated layout. Replaced by "cottage".
            new BuildingType("house_small", "Small cottage", 7f, 6f, 5.45f, population: 4, jobs: 0,
                "#F2E3C6", "#C0583F", RoofStyle.Gabled, listed: false),
        };

        /// <summary>The types the builder offers, in order.</summary>
        public static readonly IReadOnlyList<BuildingType> Buildable = new List<BuildingType>(Buildings).FindAll(b => b.Listed);

        /// <summary>The tile surfaces the builder paints, in the order it offers them.</summary>
        public static readonly IReadOnlyList<SurfaceType> Surfaces = new[]
        {
            new SurfaceType(TileSurface.Street, "street", "Street", "Lanes for cars, with kerbs", "#8A847C", paved: true),
            new SurfaceType(TileSurface.Path, "path", "Path", "Cobbles, with steps up hills", "#C9B79C", paved: true),
            new SurfaceType(TileSurface.Plaza, "plaza", "Plaza", "Paved squares to gather in", "#DCCDB0", paved: true),
            new SurfaceType(TileSurface.Garden, "garden", "Garden", "Flower beds and hedges", "#8E6A48", paved: false),
            new SurfaceType(TileSurface.Grass, "grass", "Grass", "Back to lawn", "#9DC183", paved: false),
        };

        private static readonly Dictionary<string, BuildingType> BuildingsById = Index(Buildings, b => b.Id);
        private static readonly Dictionary<string, SurfaceType> SurfacesById = Index(Surfaces, s => s.Id);

        public static bool TryGetBuilding(string id, out BuildingType type)
        {
            type = null;
            return id != null && BuildingsById.TryGetValue(id, out type);
        }

        public static bool TryGetSurface(string id, out SurfaceType type)
        {
            type = null;
            return id != null && SurfacesById.TryGetValue(id, out type);
        }

        public static SurfaceType Surface(TileSurface surface)
        {
            foreach (var type in Surfaces)
            {
                if (type.Surface == surface) return type;
            }

            return Surfaces[Surfaces.Count - 1];
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
