// One town's traffic and townsfolk, as the game runs them (ExploreView.Populate and
// its Tick), so the town here is the one every player's game makes from the same key:
// the same cars and people in the same order, started from the same seeds.
//
// Each tick it's stepped a tenth of a second (two 0.05 s slices, as the game steps),
// with the players in it as obstacles, and packed twice: in full (7 bytes a car or
// person, WorldSync's records) and as a delta of only what a client carrying each
// one on along its heading at its speed would now have wrong (Delta below).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Model;

namespace Porchlight.Sim
{
    public sealed class SimTown
    {
        /// <summary>As ExploreView: the most townsfolk a town has.</summary>
        const int MAX_PEDESTRIANS = 70;
        /// <summary>A tick's time, in the slices the game steps a town by.</summary>
        public const double TICK = 0.1, SLICE = 0.05;

        public readonly int id;
        public readonly string key;
        readonly IsoLayout layout;
        public readonly Traffic traffic;
        public readonly List<Pedestrian> people = new List<Pedestrian>();
        /// <summary>Townsfolk made with the town (the rest got out of cars someone took).</summary>
        public readonly int basePeople;
        /// <summary>For drivers put out of their cars (the game's own is "auto-walk" too).</summary>
        readonly Rng autoRng = new Rng("auto-walk");
        /// <summary>The cars someone's in ("t3"; the town's parked ones, "p…", aren't traffic).</summary>
        readonly HashSet<string> taken = new HashSet<string>();
        bool takenSynced;
        /// <summary>The players here (tiles), which cars stop for.</summary>
        List<Vec2> obstacles = new List<Vec2>();
        public readonly Delta delta = new Delta();
        /// <summary>The townsfolk's looks past `basePeople`, changed since last asked.</summary>
        public bool looksChanged;

        public SimTown(int id, string key)
        {
            this.id = id;
            this.key = key;
            var (seed, p) = ParseKey(key);
            var model = Generator.Generate(seed, p);
            layout = Layout.Build(model);
            // ExploreView.Populate, in the same order, so each car and person here is the game's.
            traffic = new Traffic(layout, new Rng($"{model.seed}::traffic"), Catalog.PickCarModel);
            var rng = new Rng($"{model.seed}::townsfolk");
            var paths = layout.sidewalks.paths;
            double walkable = paths.Sum(x => x.Length);
            int count = paths.Count > 0 ? Math.Min(MAX_PEDESTRIANS, (int)Math.Floor(walkable / 12)) : 0;
            for (int k = 0; k < count; k++)
            {
                people.Add(Pedestrian.Create(Catalog.RandomLook(rng), layout.sidewalks, rng));
                rng.Next(); // the game draws each one's avatar seed here
            }
            basePeople = people.Count;
        }

        /// <summary>A town's key (its room's name: "seed=porchlight&amp;width=256&amp;…") as the game reads it (PorchlightApp.ParseUrlState).</summary>
        public static (string seed, GenParams p) ParseKey(string key)
        {
            var q = new Dictionary<string, string>();
            foreach (var part in (key ?? "").Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = Uri.UnescapeDataString((eq < 0 ? part : part.Substring(0, eq)).Replace('+', ' '));
                string v = Uri.UnescapeDataString((eq < 0 ? "" : part.Substring(eq + 1)).Replace('+', ' '));
                if (!q.ContainsKey(k)) q[k] = v;
            }
            var partial = new Dictionary<string, double>();
            foreach (var spec in ParamSpec.All)
                if (q.TryGetValue(spec.key, out var raw)) partial[spec.key] = Numbers.ParseNumber(raw);
            q.TryGetValue("seed", out var s);
            s = s?.Trim();
            return (string.IsNullOrEmpty(s) ? null : s, GenParams.Normalize(partial));
        }

        /// <summary>Where the players here are now (tiles, the town's frame).</summary>
        public void SetObstacles(List<Vec2> at) => obstacles = at;

        /// <summary>
        /// The room's list of taken cars ("t3,p12,…"): out of the traffic, their drivers onto the
        /// pavement (as the host's game did), except those taken before this town was told.
        /// Empty again (the room's closed): back into the traffic, and the extra townsfolk gone.
        /// </summary>
        public void SetTaken(IEnumerable<string> list)
        {
            bool first = !takenSynced;
            takenSynced = true;
            var now = new HashSet<string>(list);
            if (now.Count == 0 && taken.Count > 0)
            {
                foreach (var c in traffic.cars) c.taken = false;
                taken.Clear();
                if (people.Count > basePeople)
                {
                    people.RemoveRange(basePeople, people.Count - basePeople);
                    looksChanged = true;
                }
                return;
            }
            foreach (var id in now)
            {
                if (id.Length < 2 || !taken.Add(id) || id[0] != 't' || !int.TryParse(id.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int i)) continue;
                if (i < 0 || i >= traffic.cars.Count || traffic.cars[i].taken) continue;
                var c = traffic.Take(i);
                if (!first && Catalog.IsCar(c.model) && layout.sidewalks.paths.Count > 0)
                {
                    // ExploreView.EjectDriver: out of the driver's door.
                    var look = Catalog.RandomLook(autoRng);
                    var door = new Vec2(c.x + Math.Sin(c.heading) * 0.45, c.y - Math.Cos(c.heading) * 0.45);
                    people.Add(Pedestrian.Create(look, layout.sidewalks, autoRng, door));
                    looksChanged = true;
                }
            }
        }

        /// <summary>The looks of the townsfolk past those the town came with (each client makes the same ones).</summary>
        public List<string> ExtraLooks() => people.Skip(basePeople).Select(p => p.look).ToList();

        /// <summary>A tick: a tenth of a second of the town.</summary>
        public void Step()
        {
            for (double left = TICK; left > 1e-9; left -= SLICE)
            {
                double dt = Math.Min(SLICE, left);
                foreach (var p in people) p.Update(dt);
                traffic.Update(dt, obstacles);
            }
        }

        /// <summary>
        /// Its streets, as the Manage map draws them (Checks roadmap writes the same for the towns that come
        /// with the game): [kind, width, x0, y0, x1, y1, …] in tiles, the town's frame, to a tenth.
        /// </summary>
        public List<double[]> Roads()
        {
            double R(double v) => Math.Round(v * 10) / 10;
            return layout.roads.Where(r => r.points != null && r.points.Count >= 2)
                .Select(r => new[] { (double)(int)r.kind, R(r.width) }.Concat(r.points.SelectMany(q => new[] { R(q.x), R(q.y) })).ToArray())
                .ToList();
        }

        public byte[] Cars() => WorldSync.EncodeCars(traffic.cars);
        public byte[] People() => WorldSync.EncodePeople(people);
    }
}
