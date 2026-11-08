// Every generator tunable lives here with a default. The UI builds its sliders
// from ParamSpecs, and the URL hash stores any value that differs from
// default. Heights are 0..1; slopes are height change per tile.

using System;
using System.Collections.Generic;
using System.Globalization;
using Porchlight.Core;

namespace Porchlight.Model
{
    public sealed class GenParams
    {
        // map
        public double width = 128;
        public double height = 128;
        // terrain
        public double noiseScale = 96;
        public double noiseOctaves = 4;
        public double edgeFalloff = 0.15;
        public double waterFraction = 0.08;
        public double contourSteps = 4;
        public double rockSlope = 0.055;
        // rivers
        public double riverCount = 2;
        public double riverMinLength = 24;
        public double riverMaxWidth = 4;
        // town
        public double townRadius = 0.36;
        public double maxRoadSlope = 0.04;
        public double bridgeCost = 12;
        public double mainRoadWidth = 2;
        // streets
        public double roadDensity = 0.85;
        public double streetSegment = 5;
        public double streetSpacing = 9;
        public double streetWidth = 1;
        /// <summary>How often a dead end that could be joined up is kept as a cul-de-sac instead.</summary>
        public double culDeSacs = 0.5;
        /// <summary>How far ahead a dead end looks for a road to join, in street segments.</summary>
        public double deadEndReach = 1.8;
        // lots and buildings
        public double lotMinSize = 8;
        public double lotMaxSize = 30;
        public double buildingDensity = 0.85;
        public double maxBuildingSize = 7;
        public double frontSetback = 0;
        /// <summary>Within this share of the town radius buildings stand right on the sidewalk, setback or not.</summary>
        public double innerCity = 0.5;
        /// <summary>Within this share of the town radius is the city centre: shops, and offices and towers among them (1: the whole town).</summary>
        public double downtown = 0.3;
        /// <summary>How many floors the tallest towers in the middle of the centre reach.</summary>
        public double towerFloors = 5;
        /// <summary>The share of the centre's buildings that are shops, offices and towers (the rest, houses); 1 for towers only.</summary>
        public double towerShare = 0.6;
        /// <summary>The share of the centre's lots left open as green courtyards among the towers.</summary>
        public double courtyards = 0;
        // yards
        public double driveways = 0.6;
        public double gardens = 0.5;

        public static GenParams Defaults => new GenParams();

        public GenParams Clone() => (GenParams)MemberwiseClone();

        public double this[string key]
        {
            get => key switch
            {
                "width" => width,
                "height" => height,
                "noiseScale" => noiseScale,
                "noiseOctaves" => noiseOctaves,
                "edgeFalloff" => edgeFalloff,
                "waterFraction" => waterFraction,
                "contourSteps" => contourSteps,
                "rockSlope" => rockSlope,
                "riverCount" => riverCount,
                "riverMinLength" => riverMinLength,
                "riverMaxWidth" => riverMaxWidth,
                "townRadius" => townRadius,
                "maxRoadSlope" => maxRoadSlope,
                "bridgeCost" => bridgeCost,
                "mainRoadWidth" => mainRoadWidth,
                "roadDensity" => roadDensity,
                "streetSegment" => streetSegment,
                "streetSpacing" => streetSpacing,
                "streetWidth" => streetWidth,
                "culDeSacs" => culDeSacs,
                "deadEndReach" => deadEndReach,
                "lotMinSize" => lotMinSize,
                "lotMaxSize" => lotMaxSize,
                "buildingDensity" => buildingDensity,
                "maxBuildingSize" => maxBuildingSize,
                "frontSetback" => frontSetback,
                "innerCity" => innerCity,
                "downtown" => downtown,
                "towerFloors" => towerFloors,
                "towerShare" => towerShare,
                "courtyards" => courtyards,
                "driveways" => driveways,
                "gardens" => gardens,
                _ => throw new ArgumentException("unknown param " + key),
            };
            set
            {
                switch (key)
                {
                    case "width": width = value; break;
                    case "height": height = value; break;
                    case "noiseScale": noiseScale = value; break;
                    case "noiseOctaves": noiseOctaves = value; break;
                    case "edgeFalloff": edgeFalloff = value; break;
                    case "waterFraction": waterFraction = value; break;
                    case "contourSteps": contourSteps = value; break;
                    case "rockSlope": rockSlope = value; break;
                    case "riverCount": riverCount = value; break;
                    case "riverMinLength": riverMinLength = value; break;
                    case "riverMaxWidth": riverMaxWidth = value; break;
                    case "townRadius": townRadius = value; break;
                    case "maxRoadSlope": maxRoadSlope = value; break;
                    case "bridgeCost": bridgeCost = value; break;
                    case "mainRoadWidth": mainRoadWidth = value; break;
                    case "roadDensity": roadDensity = value; break;
                    case "streetSegment": streetSegment = value; break;
                    case "streetSpacing": streetSpacing = value; break;
                    case "streetWidth": streetWidth = value; break;
                    case "culDeSacs": culDeSacs = value; break;
                    case "deadEndReach": deadEndReach = value; break;
                    case "lotMinSize": lotMinSize = value; break;
                    case "lotMaxSize": lotMaxSize = value; break;
                    case "buildingDensity": buildingDensity = value; break;
                    case "maxBuildingSize": maxBuildingSize = value; break;
                    case "frontSetback": frontSetback = value; break;
                    case "innerCity": innerCity = value; break;
                    case "downtown": downtown = value; break;
                    case "towerFloors": towerFloors = value; break;
                    case "towerShare": towerShare = value; break;
                    case "courtyards": courtyards = value; break;
                    case "driveways": driveways = value; break;
                    case "gardens": gardens = value; break;
                    default: throw new ArgumentException("unknown param " + key);
                }
            }
        }

        public bool SameAs(GenParams o)
        {
            foreach (var s in ParamSpec.All) if (this[s.key] != o[s.key]) return false;
            return true;
        }

        /// <summary>Fill in defaults, snap to each spec's step and clamp to its range.</summary>
        public static GenParams Normalize(IReadOnlyDictionary<string, double> partial)
        {
            var o = new GenParams();
            foreach (var spec in ParamSpec.All)
            {
                if (partial == null || !partial.TryGetValue(spec.key, out var v) || !Numbers.IsFinite(v)) continue;
                double stepped = Numbers.Round((v - spec.min) / spec.step) * spec.step + spec.min;
                double fixedV = double.Parse(Numbers.ToFixed(stepped, 6), CultureInfo.InvariantCulture);
                o[spec.key] = Math.Min(spec.max, Math.Max(spec.min, fixedV));
            }
            if (o.lotMaxSize < o.lotMinSize * 2) o.lotMaxSize = o.lotMinSize * 2;
            return o;
        }

        public static GenParams Normalize(GenParams p)
        {
            var d = new Dictionary<string, double>();
            foreach (var s in ParamSpec.All) d[s.key] = p[s.key];
            return Normalize(d);
        }
    }

    public sealed class ParamSpec
    {
        public string key;
        public string label;
        public string group;
        public double min;
        public double max;
        public double step;

        static ParamSpec S(string key, string label, string group, double min, double max, double step) =>
            new ParamSpec { key = key, label = label, group = group, min = min, max = max, step = step };

        public static readonly string[] Groups = { "Map", "Terrain", "Rivers", "Town & roads", "Streets", "Lots & buildings", "Yards" };

        public static readonly ParamSpec[] All =
        {
            S("width", "Map width", "Map", 32, 256, 16),
            S("height", "Map height", "Map", 32, 256, 16),
            S("noiseScale", "Feature size", "Terrain", 16, 160, 4),
            S("noiseOctaves", "Octaves", "Terrain", 4, 6, 1),
            S("edgeFalloff", "Edge falloff", "Terrain", 0, 0.4, 0.01),
            S("waterFraction", "Water fraction", "Terrain", 0, 0.3, 0.01),
            S("contourSteps", "Contour steps", "Terrain", 2, 8, 1),
            S("rockSlope", "Rock slope", "Terrain", 0.01, 0.1, 0.001),
            S("riverCount", "Rivers", "Rivers", 0, 3, 1),
            S("riverMinLength", "Min length", "Rivers", 8, 80, 1),
            S("riverMaxWidth", "Max width", "Rivers", 1, 8, 0.5),
            S("townRadius", "Town radius", "Town & roads", 0.15, 0.5, 0.01),
            S("maxRoadSlope", "Max road slope", "Town & roads", 0.02, 0.1, 0.001),
            S("bridgeCost", "Bridge cost", "Town & roads", 2, 60, 1),
            S("mainRoadWidth", "Main road width", "Town & roads", 1, 3, 0.25),
            S("roadDensity", "Street density", "Streets", 0, 1, 0.01),
            S("streetSegment", "Segment length", "Streets", 3, 10, 0.5),
            S("streetSpacing", "Side street spacing", "Streets", 5, 20, 1),
            S("streetWidth", "Street width", "Streets", 0.5, 2, 0.25),
            S("culDeSacs", "Cul-de-sacs", "Streets", 0, 1, 0.05),
            S("deadEndReach", "Dead ends reach", "Streets", 1, 8, 0.1),
            S("lotMinSize", "Lot min size", "Lots & buildings", 4, 40, 1),
            S("lotMaxSize", "Lot max size", "Lots & buildings", 12, 120, 1),
            S("buildingDensity", "Building density", "Lots & buildings", 0, 1, 0.01),
            S("maxBuildingSize", "Max building size", "Lots & buildings", 2, 12, 1),
            S("frontSetback", "Front setback", "Lots & buildings", 0, 3, 1),
            S("innerCity", "Inner city (no setback)", "Lots & buildings", 0, 1, 0.05),
            S("downtown", "City centre", "Lots & buildings", 0.1, 1, 0.05),
            S("towerFloors", "Tallest towers (floors)", "Lots & buildings", 5, 16, 1),
            S("towerShare", "Towers in the centre", "Lots & buildings", 0, 1, 0.05),
            S("courtyards", "Courtyards in the centre", "Lots & buildings", 0, 0.6, 0.05),
            S("driveways", "Driveways", "Yards", 0, 1, 0.01),
            S("gardens", "Gardens", "Yards", 0, 1, 0.01),
        };

        public static ParamSpec Get(string key)
        {
            foreach (var s in All) if (s.key == key) return s;
            throw new ArgumentException("unknown param " + key);
        }
    }
}
