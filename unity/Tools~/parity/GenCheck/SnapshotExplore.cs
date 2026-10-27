// The explore view's pure planning (layout, street names, signs) in the shape
// ../export.ts writes it.

using System.Collections.Generic;
using System.Text.Json.Nodes;
using Porchlight.Explore;
using Porchlight.Model;

static partial class Snapshot
{
    static partial void AddExplore(TownModel m, Dictionary<string, JsonNode> d)
    {
        var layout = Layout.Build(m);
        var houses = new JsonArray();
        foreach (var h in layout.houses)
        {
            var tiles = new JsonArray();
            foreach (var t in h.tiles) tiles.Add(new JsonObject { ["x"] = t.x, ["y"] = t.y });
            houses.Add(new JsonObject
            {
                ["x"] = h.x,
                ["y"] = h.y,
                ["tiles"] = tiles,
                ["facing"] = h.facing.ToString(),
                ["kind"] = Names.Of(h.kind),
                ["style"] = Layout.StyleName(h.style),
                ["floors"] = h.floors,
                ["buildingId"] = h.buildingId,
                ["blockId"] = h.blockId,
            });
        }
        var fences = new JsonArray();
        foreach (var f in layout.fences) fences.Add(new JsonObject { ["x"] = f.x, ["y"] = f.y, ["side"] = f.side.ToString() });
        var cars = new JsonArray();
        foreach (var c in layout.cars) cars.Add(new JsonObject { ["x"] = c.x, ["y"] = c.y, ["angle"] = c.angle, ["variant"] = c.variant });
        var roads = new JsonArray();
        foreach (var r in layout.roads) roads.Add(new JsonObject { ["kind"] = Names.Of(r.kind), ["from"] = r.from, ["to"] = r.to, ["points"] = Pts(r.points), ["width"] = r.width });
        var bridges = new JsonArray();
        foreach (var b in layout.bridges) bridges.Add(new JsonObject { ["kind"] = Names.Of(b.kind), ["points"] = Pts(b.points), ["width"] = b.width });
        var paths = new JsonArray();
        foreach (var p in layout.sidewalks.paths) paths.Add(new JsonObject { ["points"] = Pts(p.points), ["at"] = Nums(p.at), ["side"] = p.side, ["road"] = p.road });
        var links = new JsonArray();
        foreach (var l in layout.sidewalks.links) links.Add(Ints(l));
        d["layout"] = new JsonObject
        {
            ["houses"] = houses,
            ["use"] = Bytes(layout.use),
            ["fences"] = fences,
            ["cars"] = cars,
            ["roads"] = roads,
            ["bridges"] = bridges,
            ["sidewalks"] = new JsonObject { ["paths"] = paths, ["links"] = links },
        };

        var names = StreetNaming.Name(m, layout.roads);
        var streets = new JsonArray();
        foreach (var s in names.streets) streets.Add(new JsonObject { ["name"] = s.name, ["kind"] = Names.Of(s.kind), ["roads"] = Ints(s.roads) });
        d["names"] = new JsonObject { ["streets"] = streets, ["ofRoad"] = Ints(names.ofRoad) };

        var signs = new JsonArray();
        foreach (var s in Signs.Plan(m, layout, names))
        {
            var blades = new JsonArray();
            foreach (var b in s.blades) blades.Add(new JsonObject { ["street"] = b.street, ["along"] = V(b.along) });
            signs.Add(new JsonObject { ["at"] = V(s.at), ["blades"] = blades });
        }
        d["signs"] = signs;
    }
}
