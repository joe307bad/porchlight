// The C# town in the same JSON shape ../export.ts writes the TypeScript one in.

using System.Collections.Generic;
using System.Text.Json.Nodes;
using Porchlight.Core;
using Porchlight.Model;

static partial class Snapshot
{
    public static JsonNode V(Vec2 p) => new JsonObject { ["x"] = p.x, ["y"] = p.y };

    public static JsonArray Pts(IEnumerable<Vec2> pts)
    {
        var a = new JsonArray();
        foreach (var p in pts) a.Add(V(p));
        return a;
    }

    public static JsonArray Nums(IEnumerable<double> xs)
    {
        var a = new JsonArray();
        foreach (var x in xs) a.Add(x);
        return a;
    }

    public static JsonArray Floats(float[] xs, bool infAsMinusOne = false)
    {
        var a = new JsonArray();
        foreach (var x in xs) a.Add(infAsMinusOne && float.IsInfinity(x) ? -1.0 : (double)x);
        return a;
    }

    public static JsonArray Ints(IEnumerable<int> xs)
    {
        var a = new JsonArray();
        foreach (var x in xs) a.Add((double)x);
        return a;
    }

    public static JsonArray Bytes(byte[] xs)
    {
        var a = new JsonArray();
        foreach (var x in xs) a.Add((double)x);
        return a;
    }

    public static Dictionary<string, JsonNode> Of(TownModel m)
    {
        var d = new Dictionary<string, JsonNode>();
        var p = new JsonObject();
        foreach (var s in ParamSpec.All) p[s.key] = m.@params[s.key];
        d["params"] = p;
        d["heightmap"] = Floats(m.heightmap);
        d["waterLevel"] = m.waterLevel;
        d["center"] = V(m.center);
        d["townRadius"] = m.townRadius;
        d["gates"] = Pts(m.gates);
        var rivers = new JsonArray();
        foreach (var r in m.rivers) rivers.Add(new JsonObject { ["id"] = r.id, ["points"] = Pts(r.points), ["widthAt"] = Nums(r.widthAt) });
        d["rivers"] = rivers;
        var nodes = new JsonArray();
        foreach (var n in m.roads.nodes) nodes.Add(new JsonObject { ["id"] = n.id, ["at"] = V(n.at) });
        var edges = new JsonArray();
        foreach (var e in m.roads.edges)
            edges.Add(new JsonObject { ["id"] = e.id, ["from"] = e.from, ["to"] = e.to, ["kind"] = Names.Of(e.kind), ["points"] = Pts(e.points) });
        d["roads"] = new JsonObject { ["nodes"] = nodes, ["edges"] = edges };
        var bridges = new JsonArray();
        foreach (var b in m.bridges) bridges.Add(new JsonObject { ["id"] = b.id, ["roadEdgeId"] = b.roadEdgeId, ["from"] = V(b.from), ["to"] = V(b.to) });
        d["bridges"] = bridges;
        var blocks = new JsonArray();
        foreach (var b in m.blocks)
        {
            var holes = new JsonArray();
            foreach (var h in b.holes) holes.Add(Pts(h));
            blocks.Add(new JsonObject { ["id"] = b.id, ["polygon"] = Pts(b.polygon), ["holes"] = holes });
        }
        d["blocks"] = blocks;
        var lots = new JsonArray();
        foreach (var l in m.lots) lots.Add(new JsonObject { ["id"] = l.id, ["blockId"] = l.blockId, ["polygon"] = Pts(l.polygon), ["frontageEdgeId"] = l.frontageEdgeId });
        d["lots"] = lots;
        var buildings = new JsonArray();
        foreach (var b in m.buildings)
        {
            var tiles = new JsonArray();
            foreach (var t in b.tiles) tiles.Add(new JsonObject { ["x"] = t.x, ["y"] = t.y });
            buildings.Add(new JsonObject
            {
                ["id"] = b.id,
                ["lotId"] = b.lotId,
                ["footprint"] = Pts(b.footprint),
                ["tiles"] = tiles,
                ["kind"] = Names.Of(b.kind),
                ["floors"] = b.floors,
                ["facing"] = Names.Of(b.facing),
            });
        }
        d["buildings"] = buildings;
        var landmarks = new JsonArray();
        foreach (var l in m.landmarks) landmarks.Add(new JsonObject { ["id"] = l.id, ["kind"] = Names.Of(l.kind), ["at"] = V(l.at), ["lotIds"] = Ints(l.lotIds) });
        d["landmarks"] = landmarks;
        d["terrain"] = Bytes(m.tiles.terrain);
        d["overlay"] = Bytes(m.tiles.overlay);
        d["buildingId"] = Ints(m.tiles.buildingId);
        d["walkable"] = Bytes(m.tiles.walkable);
        d["spawn"] = V(m.spawn);
        d["flow"] = Floats(m.fields.flow);
        d["roadCost"] = Floats(m.fields.roadCost, true);
        AddExplore(m, d);
        return d;
    }

    static partial void AddExplore(TownModel m, Dictionary<string, JsonNode> d);
}
