using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Porchlight.County;
using Porchlight.Explore;
using Porchlight.Gen;

/// <summary>Everything in a town and its layout, as one short hash: the same hash, the same town.</summary>
static class Fingerprint
{
    static readonly JsonSerializerOptions Options = new JsonSerializerOptions { IncludeFields = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public static string Of(object o)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o, o.GetType(), Options)));
        return Convert.ToHexString(bytes).Substring(0, 16);
    }

    /// <summary>Every space of the county: its town (grown, or built for a road space), its layout, street names and signposts.</summary>
    public static int Run(int towns = 0)
    {
        // And the towns the `towns` check grows: every kind of town, not only the county's.
        for (int i = 0; i < towns; i++)
        {
            var p = Porchlight.Model.GenParams.Normalize(new Porchlight.Model.GenParams { width = 64 + (i % 4) * 32, height = 64 + ((i / 4) % 4) * 32, roadDensity = 0.5 + (i % 5) * 0.1, frontSetback = (i / 2) % 4 });
            var m = Generator.Generate($"check-{i}", p);
            var layout = Layout.Build(m);
            var names = StreetNaming.Name(m, layout.roads);
            Console.WriteLine($"check-{i}: model {Of(m)} layout {Of(layout)} signs {Of(Signs.Plan(m, layout, names))}");
        }
        var map = CountyMap.Build(CountyCheck.PublicTowns(), "acct");
        foreach (var s in map.spaces)
        {
            Porchlight.Model.TownModel m;
            if (s.kind == SpaceKind.Town)
            {
                var (seed, p) = Program.ParseKey(s.townKey);
                m = Generator.Generate(seed, p);
            }
            else m = RoadSpaces.Build(map, s);
            var layout = Layout.Build(m);
            var names = StreetNaming.Name(m, layout.roads);
            var signs = Signs.Plan(m, layout, names);
            Console.WriteLine($"{s.kind} {s.name} {s.x0},{s.y0}: model {Of(m)} layout {Of(layout)} names {Of(names)} signs {Of(signs)}");
        }
        return 0;
    }
}
