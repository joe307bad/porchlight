// What the town's life counts as it goes: trips by hour and how they went, how
// busy each road is by the hour, how long cars and walkers wait at each junction,
// and the problems worth a look (a long wait at a junction, a walker over a main
// road, a crowded sidewalk). Also who's indoors where, and how many are off the
// map at each gate, kept up to date by the trips starting and ending, so Manage
// can show buildings rather than thousands of dots. Pure.

using System;
using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    public sealed class LifeStats
    {
        /// <summary>Waits at a junction worth calling a problem: a car's, and a walker's.</summary>
        public const long CAR_DELAY_PROBLEM = 30_000, WALKER_DELAY_PROBLEM = 15_000;
        const int MOST_PROBLEMS = 200;

        public long started, finished, failed, delayedTrips;
        /// <summary>Trips started each hour of the day, by mode (Mode's order).</summary>
        public readonly long[,] startedByHour = new long[24, 3];
        /// <summary>Trips finished by mode, and their total time (ms).</summary>
        public readonly long[] finishedBy = new long[3], durationBy = new long[3];
        public readonly Dictionary<string, long> failures = new Dictionary<string, long>();
        /// <summary>Cars onto each road, each hour of the day.</summary>
        public readonly Dictionary<int, long[]> flow = new Dictionary<int, long[]>();
        public readonly Dictionary<int, Wait> carWaits = new Dictionary<int, Wait>(), walkerWaits = new Dictionary<int, Wait>();
        public readonly List<Problem> problems = new List<Problem>();
        public long problemCount;
        public long retimed, habits, keptWay, binStops, binSkipped, callsAnswered, pulledOver, sirenWaits, sirenOnRed, gridlockBroken, gaveUp, rerouted, lightsChanged, letIn, reserved, waitedTooLong, noGap, hunts, noSpot, turnedRound, undrawnCars, undrawnPeople, taken, boarded, crowded, mainCrossings;
        /// <summary>The longest any trip took (ms), and which.</summary>
        public long longestTrip;

        public sealed class Wait
        {
            public long total, count, longest;
            public double Mean => count == 0 ? 0 : total / (double)count;
        }

        public struct Problem
        {
            public string kind;
            public long at;
            public int node;
            public long value;
        }

        public void Started(Trip t, long now)
        {
            started++;
            startedByHour[(int)Math.Min(23, TownClock.Hours(now)), (int)t.mode]++;
        }

        public void Finished(Trip t, long now)
        {
            finished++;
            long took = now - t.started;
            finishedBy[(int)t.mode]++;
            durationBy[(int)t.mode] += took;
            longestTrip = Math.Max(longestTrip, took);
        }

        public void Failed(Trip t, string why, long now)
        {
            failed++;
            failures[why] = failures.TryGetValue(why, out long n) ? n + 1 : 1;
        }

        public void Flow(int road, long now)
        {
            if (!flow.TryGetValue(road, out var hours)) flow[road] = hours = new long[24];
            hours[(int)Math.Min(23, TownClock.Hours(now))]++;
        }

        public void Delay(int node, long ms, bool car)
        {
            ms = Math.Max(0, ms);
            var map = car ? carWaits : walkerWaits;
            if (!map.TryGetValue(node, out var w)) map[node] = w = new Wait();
            w.total += ms;
            w.count++;
            w.longest = Math.Max(w.longest, ms);
            if (ms > (car ? CAR_DELAY_PROBLEM : WALKER_DELAY_PROBLEM)) AddProblem(car ? "car waited at a junction" : "walker waited to cross", node, ms);
        }

        void AddProblem(string kind, int node, long value)
        {
            problemCount++;
            if (problems.Count >= MOST_PROBLEMS) problems.RemoveAt(0);
            problems.Add(new Problem { kind = kind, node = node, value = value });
        }

        public double MeanCarWait()
        {
            long total = 0, count = 0;
            foreach (var w in carWaits.Values)
            {
                total += w.total;
                count += w.count;
            }
            return count == 0 ? 0 : total / (double)count;
        }
    }

    public sealed partial class TownLife
    {
        public readonly LifeStats stats = new LifeStats();
        /// <summary>People inside each building now.</summary>
        public readonly Dictionary<int, int> inside = new Dictionary<int, int>();
        /// <summary>People off the map at each gate now.</summary>
        public int[] offMap;

        void Count(Person p, int by)
        {
            if (p.where == Where.OnTrip) return;
            if (p.at.border)
            {
                if (offMap == null) offMap = new int[net.borders.Count];
                if (p.at.id < offMap.Length) offMap[p.at.id] += by;
                return;
            }
            inside.TryGetValue(p.at.id, out int n);
            n += by;
            if (n <= 0) inside.Remove(p.at.id);
            else inside[p.at.id] = n;
        }

        /// <summary>How many are out now: driving, walking, waiting or riding.</summary>
        public int OutNow()
        {
            int n = 0;
            foreach (var p in people) if (p.where == Where.OnTrip) n++;
            return n;
        }

        /// <summary>Cars on the road now (buses too).</summary>
        /// <summary>
        /// How many of the town's blocks (a street between two junctions, either way) have a car on them now, of
        /// how many there are: how busy the streets look, where CarsOnRoad is how many cars there are.
        /// </summary>
        public int BlocksInUse(out int blocks)
        {
            var all = new HashSet<long>();
            var used = new HashSet<long>();
            foreach (var l in net.lanes) all.Add(BlockKey(l));
            foreach (var c in cars)
            {
                if (c.seg < 0) continue;
                int lane = c.seg < Lanes ? c.seg : net.movements[c.seg - Lanes].to;
                used.Add(BlockKey(net.lanes[lane]));
            }
            blocks = all.Count;
            return used.Count;
        }

        static long BlockKey(Lane l) => ((long)l.road << 40) | ((long)Math.Min(l.fromNode, l.toNode) << 20) | (uint)Math.Max(l.fromNode, l.toNode);

        /// <summary>
        /// The town's traffic lights now, for Manage to draw: each signal's stage showing green and when it ends (ms;
        /// a light changing early for a car waiting the other way moves that), and a lamp on the kerb by the stop line
        /// of each lane into it (x, y, the stage it goes on), where the cars waiting there don't hide it. With
        /// `withStops`, the stop signs too (they don't change): one on the kerb by each lane that stops (x, y, 1 if all-way).
        /// </summary>
        public Dictionary<string, object> Signals(bool withStops = false)
        {
            // The lamps and signs where the game's models stand (StreetFurniture), junctions in node order.
            var byNode = new Dictionary<int, List<double[]>>();
            var order = new List<int>();
            foreach (var p in StreetFurniture.Signals(net))
            {
                if (!byNode.TryGetValue(p.node, out var lamps))
                {
                    byNode[p.node] = lamps = new List<double[]>();
                    order.Add(p.node);
                }
                lamps.Add(new[] { Math.Round(p.at.x, 2), Math.Round(p.at.y, 2), p.stage });
            }
            var lights = new List<object>();
            foreach (int node in order)
            {
                var (stage, ends) = RoadNet.Stage(net.junctions[node], Now);
                var j = net.junctions[node];
                lights.Add(new Dictionary<string, object> { ["node"] = node, ["stage"] = stage, ["ends"] = ends, ["greens"] = new[] { j.green[0], j.green[1] }, ["lamps"] = byNode[node] });
            }
            var answer = new Dictionary<string, object> { ["now"] = Now, ["stageMs"] = RoadNet.STAGE_MS, ["lights"] = lights };
            if (withStops)
                answer["stops"] = StreetFurniture.Stops(net).ConvertAll(s => new[] { Math.Round(s.at.x, 2), Math.Round(s.at.y, 2), s.allWay ? 1 : 0 });
            return answer;
        }

        public int CarsOnRoad()
        {
            int n = 0;
            foreach (var c in cars) if (c.seg >= 0) n++;
            return n;
        }

        /// <summary>The longest any car on the road has been on this trip (ms).</summary>
        public long LongestOut()
        {
            long most = 0;
            foreach (var p in people) if (p.trip != null) most = Math.Max(most, Now - p.trip.started);
            return most;
        }

        /// <summary>Kerb spots taken, and how many there are (scenery's left out).</summary>
        public (int taken, int total) Parking()
        {
            int taken = 0, total = 0;
            for (int i = 0; i < net.spots.Count; i++)
            {
                var s = net.spots[i];
                if (s.kind != SpotKind.Kerb || s.scenery) continue;
                total++;
                if (spotHolder[i] >= 0) taken++;
            }
            return (taken, total);
        }

        /// <summary>Who's spot it is now (-1: free), for the checks.</summary>
        public int SpotHolder(int spot) => spotHolder[spot];

        /// <summary>The figures for Manage: a small summary of the day so far.</summary>
        public Dictionary<string, object> Summary()
        {
            var (taken, total) = Parking();
            var hours = new List<long[]>();
            for (int h = 0; h < 24; h++) hours.Add(new[] { stats.startedByHour[h, 0], stats.startedByHour[h, 1], stats.startedByHour[h, 2] });
            var busiest = new List<int[]>();
            foreach (var kv in stats.flow)
            {
                long sum = 0;
                foreach (long x in kv.Value) sum += x;
                busiest.Add(new[] { kv.Key, (int)Math.Min(int.MaxValue, sum) });
            }
            busiest.Sort((a, b) => b[1] != a[1] ? b[1].CompareTo(a[1]) : a[0].CompareTo(b[0]));
            if (busiest.Count > 10) busiest.RemoveRange(10, busiest.Count - 10);
            var waits = new List<object>();
            foreach (var kv in stats.carWaits)
                if (kv.Value.longest > LifeStats.CAR_DELAY_PROBLEM / 2)
                {
                    var at = net.nodeAt[kv.Key];
                    waits.Add(new Dictionary<string, object> { ["x"] = Math.Round(at.x, 1), ["y"] = Math.Round(at.y, 1), ["mean"] = Math.Round(kv.Value.Mean / 1000, 1), ["longest"] = kv.Value.longest / 1000 });
                }
            return new Dictionary<string, object>
            {
                ["clock"] = TownClock.Format(Now),
                ["people"] = people.Count,
                ["out"] = OutNow(),
                ["cars"] = CarsOnRoad(),
                ["trips"] = new Dictionary<string, object> { ["started"] = stats.started, ["finished"] = stats.finished, ["failed"] = stats.failed },
                ["byHour"] = hours,
                ["meanTrip"] = new[] { Mean(0), Mean(1), Mean(2) },
                ["parking"] = new[] { taken, total },
                ["busiestRoads"] = busiest,
                ["waits"] = waits,
                ["problems"] = stats.problemCount,
                ["gridlockBroken"] = stats.gridlockBroken,
                ["gaveUp"] = stats.gaveUp,
                ["mainCrossings"] = stats.mainCrossings,
                ["hunts"] = stats.hunts,
                ["population"] = Population(),
            };
        }

        /// <summary>The columns of Population's rows, after the kind of person.</summary>
        public static readonly string[] POPULATION_COLUMNS = { "people", "home", "workOrSchool", "elsewhere", "walking", "driving", "onBus", "offMap", "ownCar" };

        /// <summary>
        /// Who the town's people are and where they are now, for Manage's town table: a row a kind of person (its
        /// name, then POPULATION_COLUMNS: how many, indoors at home, at work or school, indoors anywhere else, out on
        /// foot, driving, on (or waiting for) the bus, off the map, and how many have a car).
        /// </summary>
        public List<object[]> Population()
        {
            var kinds = (PersonKind[])Enum.GetValues(typeof(PersonKind));
            var rows = new long[kinds.Length, POPULATION_COLUMNS.Length];
            foreach (var p in people)
            {
                int k = (int)p.kind, c;
                if (p.where == Where.OffMap) c = 7;
                else if (p.where == Where.OnTrip)
                {
                    var leg = p.trip != null && p.trip.leg >= 0 && p.trip.leg < p.trip.legs.Count ? p.trip.legs[p.trip.leg].kind : LegKind.Walk;
                    c = leg == LegKind.Drive ? 5 : leg == LegKind.Wait || leg == LegKind.Ride ? 6 : 4;
                }
                else if (p.at.Same(p.home)) c = 1;
                else if (p.at.Same(p.work)) c = 2;
                else c = 3;
                rows[k, 0]++;
                rows[k, c]++;
                if (p.hasCar) rows[k, 8]++;
            }
            var o = new List<object[]>();
            for (int k = 0; k < kinds.Length; k++)
            {
                if (rows[k, 0] == 0) continue;
                var row = new object[POPULATION_COLUMNS.Length + 1];
                row[0] = kinds[k].ToString();
                for (int c = 0; c < POPULATION_COLUMNS.Length; c++) row[c + 1] = rows[k, c];
                o.Add(row);
            }
            return o;
        }

        double Mean(int mode) => stats.finishedBy[mode] == 0 ? 0 : Math.Round(stats.durationBy[mode] / (double)stats.finishedBy[mode] / 1000, 1);

        /// <summary>Who's in a building, and when each next goes out (for Manage's hover).</summary>
        public List<Dictionary<string, object>> Inside(int building)
        {
            var o = new List<Dictionary<string, object>>();
            foreach (var p in people)
            {
                if (p.where != Where.Inside || p.at.border || p.at.id != building) continue;
                long next = events.When(Key(K_TRIP, p.id));
                string to = p.next < p.plan.Count && p.plan[p.next].depart >= 0 ? p.plan[p.next].to.ToString() : null;
                o.Add(new Dictionary<string, object> { ["id"] = p.id, ["kind"] = p.kind.ToString(), ["next"] = next >= 0 ? TownClock.Format(next) : null, ["to"] = to, ["purpose"] = p.next < p.plan.Count ? p.plan[p.next].purpose.ToString() : null });
                if (o.Count >= 50) break;
            }
            return o;
        }

        /// <summary>All about one car slot's car, for the debug view: its state, trip and what it's waiting on.</summary>
        public Dictionary<string, object> AboutCar(int slot)
        {
            if (slot < 0 || slot >= carSlots.Length || carSlots[slot].who < 0) return null;
            var c = cars[carSlots[slot].who];
            var o = new Dictionary<string, object>
            {
                ["state"] = c.state.ToString(),
                ["bus"] = c.bus >= 0,
                ["blockedBy"] = BlockedBy(c.id).ConvertAll(id => cars[id].slot),
                ["route"] = c.route == null ? 0 : c.route.Count - c.step,
                ["nextEvent"] = events.When(Key(K_CAR, c.id)) is long t && t >= 0 ? TownClock.Format(t) : null,
            };
            if (c.owner >= 0) o["person"] = AboutPerson(people[c.owner]);
            return o;
        }

        public Dictionary<string, object> AboutPersonSlot(int slot)
        {
            if (slot < 0 || slot >= personSlots.Length || personSlots[slot].who < 0) return null;
            return AboutPerson(people[personSlots[slot].who]);
        }

        Dictionary<string, object> AboutPerson(Person p)
        {
            var o = new Dictionary<string, object> { ["id"] = p.id, ["kind"] = p.kind.ToString(), ["where"] = p.where.ToString(), ["home"] = p.home.ToString() };
            if (p.trip != null)
            {
                o["trip"] = new Dictionary<string, object>
                {
                    ["from"] = p.trip.from.ToString(), ["to"] = p.trip.to.ToString(), ["mode"] = p.trip.mode.ToString(), ["purpose"] = p.trip.purpose.ToString(),
                    ["legs"] = p.trip.legs.ConvertAll(l => l.kind.ToString()), ["leg"] = p.trip.leg, ["since"] = TownClock.Format(p.trip.started),
                };
            }
            return o;
        }
    }
}
