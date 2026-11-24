// Who lives here, household by household, and where they work (docs/town-life.md §6).
//
// Households. Every home zoning makes is filled with households drawn from a fixed
// mix of the kinds American households come in (single, couple, families with one
// to three children, a single parent, sharers, retirees alone or as a couple),
// seeded by the town and the building, until the building has its people (zoning's
// 2.5 a home, times CROWD). Each member has a role and an age: a child of 5-17 goes
// to school, a younger one and a retiree are at home in the day, and the building's
// workers (zoning's 1.2 a home, which the roads were laid out for) are its
// households' working-age adults; the other adults are at home in the day too.
// Filling each building to zoning's figure keeps the totals where zoning has them,
// so no rebalancing (PopulationSim's) is needed.
//
// Jobs, by a doubly-constrained gravity model (NCHRP Report 716 §4.5): the town in
// zones of ZONE tiles a side, and its gates as zones for the towns beyond. A worker
// living in zone i works in zone j with odds ∝ jobsⱼ · F(tᵢⱼ), each zone's workers
// all placed and each job filled once (Furness's balancing); F is NCHRP 716's
// home-based-work friction, e^(−0.123 t) a minute of driving, the minutes those of a
// real town of this many people (a starter town is drawn far smaller: RealScale). Residents who work out
// of town work at a gate, and the town's surplus jobs go to commuters from beyond
// it, nearest gate first by the same odds.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    /// <summary>Who someone is in their household.</summary>
    public enum Role { Adult, Retiree, Child }

    public sealed partial class TownLife
    {
        /// <summary>
        /// The mix of households (percent; adults, retirees, children from-to), rounded from the Census Bureau's
        /// America's Families and Living Arrangements: 2023 (CPS): about 29% live alone (a third of them 65 or
        /// over), about a quarter are couples on their own, about a quarter couples with children under 18, and
        /// a twelfth single parents. The shares are rounded, not checked line by line. About 2.3 people a household.
        /// </summary>
        static readonly (string name, int percent, int adults, int retirees, int childrenMin, int childrenMax)[] HOUSEHOLDS =
        {
            ("single", 18, 1, 0, 0, 0),
            ("single retiree", 11, 0, 1, 0, 0),
            ("couple", 17, 2, 0, 0, 0),
            ("retired couple", 12, 0, 2, 0, 0),
            ("family with one child", 11, 2, 0, 1, 1),
            ("family with two children", 10, 2, 0, 2, 2),
            ("family with three children", 6, 2, 0, 3, 3),
            ("single parent", 8, 1, 0, 1, 2),
            ("sharers", 7, 2, 0, 0, 0),
        };
        /// <summary>School age: children this old go to school.</summary>
        const int SCHOOL_FROM = 5;
        /// <summary>The gravity model's zones (tiles a side).</summary>
        const double ZONE = 12;
        /// <summary>NCHRP 716's home-based-work friction, e^(c·t), t in minutes (its gamma function's power term, −0.02, left out: it's all but flat).</summary>
        const double FRICTION_C = -0.123;
        /// <summary>Driving across town for the gravity model's times: tiles a second, and how much longer the roads are than a straight line.</summary>
        const double ZONE_SPEED = 3, CIRCUITY = 1.3;
        /// <summary>Minutes on from a gate to the town beyond, for a commute in or out.</summary>
        const double BEYOND_MIN = 15;
        /// <summary>
        /// People a square kilometre in an American town (Census 2020 urban areas average about 2,500 a square mile;
        /// rounded). A starter town is drawn far smaller than a real town of its people would be, so the gravity
        /// model's minutes are those of a real one (RealScale): the friction is calibrated for real trip lengths.
        /// </summary>
        const double REAL_DENSITY = 1000;
        /// <summary>Furness's balancing: this many rounds.</summary>
        const int FURNESS_ROUNDS = 40;

        /// <summary>The town's households: their kind (HOUSEHOLDS' name) and their members (person ids).</summary>
        public readonly List<(string kind, List<int> members)> households = new List<(string, List<int>)>();

        /// <summary>A household member about to be made: their role and age, and whether they're one of the building's workers.</summary>
        struct Member
        {
            public Role role;
            public int age;
            public bool worker;
        }

        /// <summary>
        /// Building `b`'s households: drawn from the mix until there are `residents` people, each with roles and
        /// ages; its first `workers` working-age adults (in a seeded order) are its workers.
        /// </summary>
        List<(string kind, List<Member> members)> Households(int b, int residents, int workers)
        {
            var rng = new Rng($"{seed}::households::{b}");
            var o = new List<(string, List<Member>)>();
            int people = 0;
            while (people < residents)
            {
                int roll = rng.Int(0, 99), k = 0;
                while (k < HOUSEHOLDS.Length - 1 && roll >= HOUSEHOLDS[k].percent)
                {
                    roll -= HOUSEHOLDS[k].percent;
                    k++;
                }
                var h = HOUSEHOLDS[k];
                var members = new List<Member>();
                int adults = h.name == "sharers" ? rng.Int(2, 3) : h.adults;
                for (int i = 0; i < adults; i++) members.Add(new Member { role = Role.Adult, age = rng.Int(20, 64) });
                for (int i = 0; i < h.retirees; i++) members.Add(new Member { role = Role.Retiree, age = rng.Int(65, 90) });
                int children = rng.Int(h.childrenMin, h.childrenMax);
                for (int i = 0; i < children; i++) members.Add(new Member { role = Role.Child, age = rng.Int(0, 17) });
                o.Add((h.name, members));
                people += members.Count;
            }
            // The workers: working-age adults, in a seeded order.
            var adultsAt = new List<(int h, int m)>();
            for (int h = 0; h < o.Count; h++)
                for (int m = 0; m < o[h].Item2.Count; m++)
                    if (o[h].Item2[m].role == Role.Adult) adultsAt.Add((h, m));
            rng.Shuffle(adultsAt);
            for (int i = 0; i < Math.Min(workers, adultsAt.Count); i++)
            {
                var (h, m) = adultsAt[i];
                var member = o[h].Item2[m];
                member.worker = true;
                o[h].Item2[m] = member;
            }
            return o;
        }

        /// <summary>
        /// How much further apart places would be in a real town of this many people than in this one as drawn: a
        /// real town's width (its `population` at REAL_DENSITY) over the drawn town's (its built-up circle, a tile ~4 m).
        /// At least 1.
        /// </summary>
        public double RealScale(int population)
        {
            double radius = model?.townRadius ?? 0;
            if (radius <= 0 || population <= 0) return 1;
            double drawnKm2 = Math.PI * radius * radius * TILE_M * TILE_M / 1e6;
            double realKm2 = population / REAL_DENSITY;
            return Math.Max(1, Math.Sqrt(realKm2 / drawnKm2));
        }

        /// <summary>A tile, in metres.</summary>
        const double TILE_M = 3.97;

        /// <summary>
        /// Each worker's job by the doubly-constrained gravity model: `workers` (home building, or a gate for a
        /// commuter in) to `jobs` (a job's building, or a gate for a resident working out of town), as many of each,
        /// in a town of `population` (RealScale);
        /// workers and jobs in the order given, a job for each worker in return (a building id, or −1 − gate).
        /// </summary>
        List<int> Gravity(Rng rng, List<int> workers, List<int> jobs, int population)
        {
            // Zones: by ZONE-tile cell for buildings, one each for the gates.
            var zoneOf = new Dictionary<long, int>();
            var zoneAt = new List<Vec2>();
            var zoneGate = new List<bool>();
            int Zone(int place)
            {
                bool gate = place < 0;
                var at = gate ? net.borders[-1 - place].at : Door(Place.Building(place));
                long key = gate ? long.MinValue + (-1 - place) : ((long)Math.Floor(at.x / ZONE) << 32) ^ (uint)(int)Math.Floor(at.y / ZONE);
                if (!zoneOf.TryGetValue(key, out int z))
                {
                    zoneOf[key] = z = zoneAt.Count;
                    zoneAt.Add(gate ? at : new Vec2((Math.Floor(at.x / ZONE) + 0.5) * ZONE, (Math.Floor(at.y / ZONE) + 0.5) * ZONE));
                    zoneGate.Add(gate);
                }
                return z;
            }
            var originOf = new int[workers.Count];
            for (int i = 0; i < workers.Count; i++) originOf[i] = Zone(workers[i]);
            // Each zone's jobs, in the order given.
            var jobsIn = new Dictionary<int, List<int>>();
            foreach (int place in jobs)
            {
                int z = Zone(place);
                if (!jobsIn.TryGetValue(z, out var list)) jobsIn[z] = list = new List<int>();
                list.Add(place);
            }
            int n = zoneAt.Count;
            var O = new double[n];
            var D = new double[n];
            foreach (int z in originOf) O[z]++;
            foreach (var kv in jobsIn) D[kv.Key] = kv.Value.Count;
            // Friction between zones: minutes of driving, the town beyond a gate BEYOND_MIN on; none from a gate to a gate.
            double scale = RealScale(population);
            var F = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                if (O[i] == 0) continue;
                for (int j = 0; j < n; j++)
                {
                    if (D[j] == 0 || (zoneGate[i] && zoneGate[j])) continue;
                    double minutes = Geo.Dist(zoneAt[i], zoneAt[j]) * CIRCUITY / ZONE_SPEED / 60 * scale + (zoneGate[i] || zoneGate[j] ? BEYOND_MIN : 0);
                    F[i, j] = DetMath.Expm1(FRICTION_C * Math.Max(0.5, minutes)) + 1;
                }
            }
            // Furness: balancing factors so each zone's trips out are its workers and its trips in its jobs.
            var a = new double[n];
            var bal = new double[n];
            for (int j = 0; j < n; j++) bal[j] = 1;
            for (int round = 0; round < FURNESS_ROUNDS; round++)
            {
                for (int i = 0; i < n; i++)
                {
                    if (O[i] == 0) continue;
                    double s = 0;
                    for (int j = 0; j < n; j++) s += bal[j] * D[j] * F[i, j];
                    a[i] = s > 0 ? 1 / s : 0;
                }
                for (int j = 0; j < n; j++)
                {
                    if (D[j] == 0) continue;
                    double s = 0;
                    for (int i = 0; i < n; i++) s += a[i] * O[i] * F[i, j];
                    bal[j] = s > 0 ? 1 / s : 0;
                }
            }
            var T = new double[n, n];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    T[i, j] = a[i] * O[i] * bal[j] * D[j] * F[i, j];
            // Each worker in turn: a zone by what's left of their zone's trips to each, among those with jobs left;
            // then that zone's next job.
            var left = new int[n];
            foreach (var kv in jobsIn) left[kv.Key] = kv.Value.Count;
            var taken = new int[n];
            var o = new List<int>(workers.Count);
            for (int w = 0; w < workers.Count; w++)
            {
                int i = originOf[w];
                double sum = 0;
                for (int j = 0; j < n; j++) if (left[j] > 0) sum += Math.Max(0, T[i, j]);
                int pick = -1;
                if (sum > 1e-12)
                {
                    double r = rng.Next() * sum;
                    for (int j = 0; j < n; j++)
                    {
                        if (left[j] <= 0) continue;
                        double t = Math.Max(0, T[i, j]);
                        if (r < t || pick < 0) pick = j;
                        if (r < t) break;
                        r -= t;
                    }
                }
                else
                {
                    // Nothing left by the model (rounding): the jobs left, nearest first.
                    double best = -1;
                    for (int j = 0; j < n; j++)
                        if (left[j] > 0 && F[i, j] * left[j] > best)
                        {
                            best = F[i, j] * left[j];
                            pick = j;
                        }
                }
                if (pick < 0)
                {
                    o.Add(int.MinValue);
                    continue;
                }
                o.Add(jobsIn[pick][taken[pick]++]);
                left[pick]--;
                T[i, pick] = Math.Max(0, T[i, pick] - 1);
            }
            return o;
        }
    }
}
