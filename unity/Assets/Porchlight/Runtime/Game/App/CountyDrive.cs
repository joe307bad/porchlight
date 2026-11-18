#if (UNITY_WEBGL && !UNITY_EDITOR) || PORCHLIGHT_NO_THREADS
// No threads: the web build, or a desktop player built to run as the web build does (Setup: PORCHLIGHT_NO_THREADS=1).
#define NO_THREADS
#endif
// Driving the county (Shared/County): from Porchlight out along its highways to
// the cities and to your own towns north of it, and flying anywhere over the
// fields between them, with no loading screen.
//
// The county is a set of spaces (each town, the junction, your empty plots,
// each stretch of highway), each with its own tile grid. You're in one at a
// time, and the view runs just as it does in a town: the space you're in sits
// at the scene's origin, and the ones built round it sit where they are
// beside it. As you go, the spaces ahead are built a few milliseconds a frame
// (a town's growing on a worker thread off the web), the ones well behind are
// let go, and once you're over the line into the next one (and it's ready)
// the view moves into it and every built space shifts by the same amount, so
// nothing on screen moves. If the next one isn't ready, you wait at its edge.
//
// The rooms follow the ground: each town's room has its town and the near half
// of each of its highways, the north room your junction and plots, and the
// countryside goes to the room nearest it (CountyMap.RoomAt; the server agrees,
// server/src/county.ts). Over the line into another room's ground, this tab
// leaves its room and joins that one, carrying on from where it is; positions
// go to each room in that room's terms (NetFrame).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Porchlight.Core;
using Porchlight.County;
using Porchlight.Gen;
using Porchlight.Model;
using Porchlight.Net;
using Porchlight.Rendering;
using UnityEngine;
using Space = Porchlight.County.Space;

namespace Porchlight.App
{
    public sealed class CountyDrive
    {
        /// <summary>Road spaces (stretches of highway, the junction, plots) are built this close to you (tiles)...</summary>
        const double ROAD_REACH = 240;
        /// <summary>...a town once you're this close to its edge: the time it takes to grow and build, at a car's top speed and more.</summary>
        const double TOWN_REACH = 330;
        /// <summary>A space you've left is let go this far off (more than it's built from, so nothing comes and goes).</summary>
        const double RELEASE = 400;
        /// <summary>Towns built at once (they're the big ones): the one you're in and the next.</summary>
        const int MAX_TOWNS = 2;
        /// <summary>Building, each frame, while you play (ms).</summary>
        const double BUDGET_MS = 5;
        /// <summary>...and while you wait at the edge of a space that isn't ready: more, still a frame at a time (the loading bar moves).</summary>
        const double WAIT_BUDGET_MS = 28;
        /// <summary>A space that failed to build is tried again after this long (ms).</summary>
        const double RETRY_MS = 5000;
        /// <summary>Over the line into another room's ground this long (ms) before this tab moves rooms.</summary>
        const double ROOM_SETTLE_MS = 400;
        /// <summary>The countryside's ground, just under the towns'.</summary>
        const float COUNTRY_Y = -0.03f;
        const int GRASS = 0x86bd5b;

        sealed class Built
        {
            public Space space;
            public TownModel model;
            public TownWorld world;
            public IEnumerator<string> steps;
            public IEnumerator<string> growing;
            /// <summary>Its layout, street names and signposts being worked out a share at a time (where there are no threads).</summary>
            public IEnumerator<string> preparingTown;
            public Task<(TownModel model, TownWorld.Prepared prepared)> thread;
            public TownWorld.Prepared prepared;
            /// <summary>The part of its world last built (TownWorld.Build), for how far along it is.</summary>
            public string part;
            /// <summary>Where you can walk, drive and fly in it, worked out once it's built (so driving in costs nothing).</summary>
            public ExploreView.PreparedWays ways;
            public Task preparingThread;
            public IEnumerator<string> preparing;
            public bool ready;
            public bool failed;
            /// <summary>When it was begun, how much work it's been (ms, every frame's share), and its longest single step and what that was (for the log).</summary>
            public double startedAt, work, longest;
            public string longestAt;
            /// <summary>Work by part (ms), for the log.</summary>
            public readonly Dictionary<string, double> byPart = new Dictionary<string, double>();
            public double failedAt;
            /// <summary>Where there are no threads: the town's model and layout, grown (or growing) once and kept.</summary>
            public Grown grownFrom;
        }

        /// <summary>
        /// A town's model and its layout, names and signposts (TownWorld.Prepared): what takes longest to make
        /// (Porchlight's is nine-tenths of the work of building it), and the same every time from its key.
        /// </summary>
        sealed class Grown
        {
            public TownModel model;
            public TownWorld.Prepared prepared;
            /// <summary>Grows it and works out its layout, a share at a time (no threads).</summary>
            public IEnumerator<string> steps;
            public bool Ready => model != null && prepared != null;
        }

        /// <summary>
        /// Where there are no threads (the web): every town grown, kept by its key for as long as the county is,
        /// the one you start in from the start. Coming back to a town builds only its world (Porchlight's 13 s
        /// of work, a share a frame, was 1.7 s of it); and while nothing else is being built, the towns not
        /// grown yet are grown a share a frame (PreGrow), so the one ahead is ready by the time you get to it.
        /// </summary>
        readonly Dictionary<string, Grown> kept = new Dictionary<string, Grown>();

        /// <summary>
        /// Every town of the county grown (and its layout, names and signposts worked out) on a worker
        /// thread, the nearest first, from the moment the county starts: getting to one, only its world
        /// is left to build. Off the web only (no threads there).
        /// </summary>
        readonly Dictionary<string, Lazy<(TownModel, TownWorld.Prepared)>> grown = new Dictionary<string, Lazy<(TownModel, TownWorld.Prepared)>>();

        /// <summary>The town you're heading for next (a trip's next town on the way), built before any other; null: the nearest.</summary>
        public Func<Space> Heading;

        /// <summary>Waiting at the edge of a space still being built: its name and how far along it is (0-1); null if not.</summary>
        public (string name, double progress)? Waiting { get; private set; }

        readonly ExploreView view;
        public readonly CountyMap map;
        readonly Func<string, (string seed, GenParams p)> parseKey;
        readonly Dictionary<int, Built> built = new Dictionary<int, Built>();
        Built job;
        /// <summary>Spaces let go, being taken apart a share a frame (TownWorld.DisposeSteps).</summary>
        readonly List<IEnumerator<string>> retiring = new List<IEnumerator<string>>();
        int retireNext;
        const double RETIRE_MS = 3;
        Space active;
        readonly GameObject countryside;
        string wantRoom;
        double wantSince;

        /// <summary>Into another space: (the space, its town if it's a town).</summary>
        public Action<Space, TownModel> OnSpace;
        /// <summary>Told when a space is begun and when it's ready (the smoke run logs it).</summary>
        public Action<string> Log;
        public Multiplayer Net;
        NetFrame frame;
        /// <summary>The network's view of the game, in its room's terms: told which space you're in.</summary>
        public NetFrame Frame
        {
            get => frame;
            set
            {
                frame = value;
                if (value != null) value.county = this;
            }
        }
        /// <summary>A mission's on: stay in the space you're in (its people and places are this one's).</summary>
        public bool Paused;

        public Space Active => active;
        /// <summary>How many spaces are built or building (the smoke run watches it).</summary>
        public int BuiltCount => built.Count;
        /// <summary>The space being built now, if any (the smoke run says what a long frame was spent on).</summary>
        public string Building => job == null ? null : job.space.name ?? job.space.kind.ToString();
        static double Now => Time.realtimeSinceStartupAsDouble * 1000;

        /// <summary>The county, with you in `start` (the town on screen, already built, at the origin).</summary>
        public CountyDrive(ExploreView view, CountyMap map, Space start, Func<string, (string seed, GenParams p)> parseKey)
        {
            this.view = view;
            this.map = map;
            this.parseKey = parseKey;
            active = start;
            var here = built[start.id] = new Built { space = start, model = view.world.model, world = view.world, ready = true };
            if (start.kind == SpaceKind.Town && start.townKey != null)
            {
                // The town on screen was grown already: kept, so driving back into it builds only its world.
                var w = view.world;
                var startPrepared = new TownWorld.Prepared { layout = w.layout, names = w.names, signs = w.signs };
                kept[start.townKey] = new Grown { model = w.model, prepared = startPrepared };
                var startModel = w.model;
                grown[start.townKey] = new Lazy<(TownModel, TownWorld.Prepared)>(() => (startModel, startPrepared));
            }
#if !NO_THREADS
            foreach (var s in map.spaces)
            {
                if (s.kind != SpaceKind.Town || s == start || grown.ContainsKey(s.townKey)) continue;
                var (seed, p) = parseKey(s.townKey);
                grown[s.townKey] = new Lazy<(TownModel, TownWorld.Prepared)>(() =>
                {
                    var m = Generator.Generate(seed, p);
                    return (m, TownWorld.Prepared.Of(m));
                }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
            }
            var order = map.spaces.Where(s => s.kind == SpaceKind.Town && grown.ContainsKey(s.townKey))
                .OrderBy(s => CountyMap.DistanceTo(s, new Vec2(start.x0 + start.width / 2.0, start.y0 + start.height / 2.0)))
                .Select(s => grown[s.townKey]).ToList();
            Task.Run(() =>
            {
                foreach (var g in order)
                {
                    try { _ = g.Value; }
                    catch (Exception e) { Debug.LogWarning("county: a town didn't grow: " + e.Message); }
                }
            });
#endif
            Terrain = CountyTerrain.Build(map);
#if !NO_THREADS
            // The town you start in is built already, but not its ways in the county's terms (its hills): worked
            // out meanwhile, for when you drive back into it.
            BeginWays(here);
#endif
            countryside = Countryside();
            Hills(countryside.transform);
            Place(countryside.transform, 0, 0);
            view.Countryside = (x, y) => OpenCountry(active.ToWorld(new Vec2(x, y)));
            view.Rise = (x, y) =>
            {
                var w = active.ToWorld(new Vec2(x, y));
                return Terrain.Height(w.x, w.y);
            };
            view.Neighbours = () => built.Values.Where(b => b.world != null).Select(b => b.world);
            view.Built = (x, y) =>
            {
                var w = active.ToWorld(new Vec2(x, y));
                foreach (var b in built.Values)
                    if (b.ready && b.world != null && b.space.Contains(w.x, w.y)) return true;
                return false;
            };
        }

        /// <summary>The hills between the towns.</summary>
        public readonly CountyTerrain Terrain;

        /// <summary>
        /// The way across a space ahead along its own streets, from `a` to `b` (world tiles: a trip's way
        /// through a town between the gate in and the gate out, CountyRoute.Ahead); null while the space
        /// isn't built (its streets unknown).
        /// </summary>
        public List<Vec2> Through(Space s, Vec2 a, Vec2 b)
        {
            if (s == null || !built.TryGetValue(s.id, out var got) || !got.ready || got.world == null) return null;
            var way = Porchlight.Explore.StreetRoute.Between(got.world.layout.roads, s.ToLocal(a), s.ToLocal(b));
            if (way == null || way.points.Count == 0) return null;
            var o = new List<Vec2> { a };
            foreach (var q in way.points) o.Add(s.ToWorld(q));
            o.Add(b);
            return o;
        }

        /// <summary>Back out of the county (to a town loaded afresh): every space but the one on screen goes.</summary>
        public void Dispose()
        {
            foreach (var b in built.Values)
                if (b.world != null && b.world != view.world) b.world.Dispose();
            built.Clear();
            foreach (var r in retiring) while (r.MoveNext()) { }
            retiring.Clear();
            job = null;
            foreach (var f in countryside.GetComponentsInChildren<MeshFilter>()) UnityEngine.Object.Destroy(f.sharedMesh);
            UnityEngine.Object.Destroy(countryside);
            view.Countryside = null;
            view.Rise = null;
            view.Neighbours = null;
            view.Built = null;
            if (Frame != null) Frame.county = null;
        }

        /// <summary>
        /// Open ground (world tiles) for a plane to come down on and a car to drive over: anywhere but
        /// another space's own ground (that's its to say, once you're in it, and until then you wait).
        /// </summary>
        bool OpenCountry(Vec2 w)
        {
            foreach (var s in map.spaces) if (s != active && s.Contains(w.x, w.y)) return false;
            return true;
        }

        /// <summary>Put this at a world corner (tiles), as seen from the space you're in.</summary>
        void Place(Transform t, double x0, double y0) => t.localPosition = Coords.Map(x0 - active.x0, y0 - active.y0);

        /// <summary>A spot (world tiles) you're on your way to without driving there (joining someone out of town), and what's done once you're in its space (with the spot in its terms).</summary>
        Vec2? sendTo;
        Action<Vec2> sent;

        /// <summary>
        /// Off to this spot (world tiles) in another space: it's built first (a loading bar meanwhile, as at
        /// the edge of a space that isn't ready), then the view moves into it and you into its room, and
        /// `arrived` puts you there (given the spot in the space's terms). Null: you stay where you are.
        /// </summary>
        public void SendTo(Vec2? world, Action<Vec2> arrived)
        {
            sendTo = world != null && map.SpaceAt(world.Value) != null ? world : null;
            sent = sendTo != null ? arrived : null;
        }

        /// <summary>On the way to a spot (SendTo) and its space isn't built yet.</summary>
        public bool Sending => sendTo != null;

        public void Update()
        {
            var local = view.PlayerPosition;
            if (local == null || view.world == null) return;
            var at = active.ToWorld(local.Value);
            Plan(at);
            if (sendTo != null && Send()) return;
            // Over the line into a space that isn't ready: you wait at its edge, and it's built faster meanwhile.
            var ahead = Paused ? null : map.SpaceAt(at, active);
            Built wanted = null;
            if (ahead != null && ahead != active) built.TryGetValue(ahead.id, out wanted);
            bool waiting = ahead != null && ahead != active && (wanted == null || !wanted.ready || wanted.world == null);
            Waiting = waiting ? (ahead.name, wanted == null ? 0 : Progress(wanted)) : ((string, double)?)null;
            Step(waiting ? WAIT_BUDGET_MS : BUDGET_MS);
#if NO_THREADS
            if (job == null) PreGrow(BUDGET_MS);
#endif
            Retire();
            if (!waiting && wanted != null) Switch(wanted);
            Rooms(active.ToWorld(view.PlayerPosition ?? local.Value));
        }

        /// <summary>Into the space you're being sent to, if it's ready (built faster till it is): true once you're there.</summary>
        bool Send()
        {
            var to = map.SpaceAt(sendTo.Value);
            Built there = null;
            if (to != null && to != active) built.TryGetValue(to.id, out there);
            if (to != null && to != active && (there == null || !there.ready || there.world == null))
            {
                Waiting = (to.name ?? map.Where(to), there == null ? 0 : Progress(there));
                Step(WAIT_BUDGET_MS);
                return false;
            }
            var world = sendTo.Value;
            var arrived = sent;
            sendTo = null;
            sent = null;
            Waiting = null;
            if (there != null) Switch(there);
            arrived?.Invoke(active.ToLocal(world));
            // Into its room now, from where you've been put: left in the room you were in, the jump there
            // would be a move it puts back.
            var at = view.PlayerPosition is Vec2 l ? active.ToWorld(l) : world;
            string want = map.RoomAt(at);
            wantRoom = want;
            wantSince = Now;
            if (Net != null && Net.RoomKey != null && want != Net.RoomKey) EnterRoomAt(want, at);
            return true;
        }

        // Building --------------------------------------------------------------------------------

        /// <summary>What should be built from here: the road spaces near you, the nearest towns, and the one you're in.</summary>
        void Plan(Vec2 at)
        {
            var towns = new List<(Space s, double d)>();
            var want = new HashSet<int> { active.id };
            var head = Heading?.Invoke();
            // Being sent somewhere: its space before anything else.
            var send = sendTo != null ? map.SpaceAt(sendTo.Value) : null;
            if (send != null && send != active)
            {
                if (send.kind == SpaceKind.Town) head = send;
                else want.Add(send.id);
            }
            foreach (var s in map.spaces)
            {
                double d = CountyMap.DistanceTo(s, at);
                if (s.kind == SpaceKind.Town)
                {
                    // The town you're heading for comes first, however far.
                    if (s == head && s != active) towns.Add((s, -1));
                    else if (s != active && d < TOWN_REACH) towns.Add((s, d));
                }
                else if (d < ROAD_REACH) want.Add(s.id);
            }
            // One that failed is tried again a while later.
            foreach (var id in built.Where(kv => kv.Value.failed && Now - kv.Value.failedAt > RETRY_MS).Select(kv => kv.Key).ToList()) built.Remove(id);
            towns.Sort((a, b) => a.d.CompareTo(b.d));
            int room = MAX_TOWNS - (active.kind == SpaceKind.Town ? 1 : 0);
            for (int i = 0; i < towns.Count && i < room; i++) want.Add(towns[i].s.id);

            // Let go of what's well behind (and of a town, when there'd be more than MAX_TOWNS).
            int townsBuilt = 0;
            foreach (var b in built.Values) if (b.space.kind == SpaceKind.Town) townsBuilt++;
            var drop = new List<int>();
            foreach (var kv in built)
            {
                var s = kv.Value.space;
                if (s == active || want.Contains(s.id) || kv.Value == job) continue;
                bool far = CountyMap.DistanceTo(s, at) > RELEASE;
                bool tooMany = s.kind == SpaceKind.Town && townsBuilt > MAX_TOWNS;
                if (far || tooMany)
                {
                    drop.Add(kv.Key);
                    if (s.kind == SpaceKind.Town) townsBuilt--;
                }
            }
            foreach (int id in drop)
            {
                LetGo(built[id]);
                built.Remove(id);
            }
            // A job no longer wanted (you turned round) stops, so the one you want starts.
            if (job != null && !want.Contains(job.space.id))
            {
                LetGo(job);
                built.Remove(job.space.id);
                job = null;
            }
            if (job != null) return;
            // Next: the nearest wanted space not built yet.
            Space best = null;
            double bestD = double.PositiveInfinity;
            foreach (int id in want)
            {
                if (built.ContainsKey(id)) continue;
                var s = map.spaces[id];
                double d = s == head ? -1 : CountyMap.DistanceTo(s, at);
                if (d < bestD)
                {
                    bestD = d;
                    best = s;
                }
            }
            if (best == null) return;
            // Room for another town: let go of the farthest one you're not in first.
            if (best.kind == SpaceKind.Town && townsBuilt >= MAX_TOWNS)
            {
                Built farthest = null;
                foreach (var b in built.Values)
                    if (b.space.kind == SpaceKind.Town && b.space != active && b.space != head && (farthest == null || CountyMap.DistanceTo(b.space, at) > CountyMap.DistanceTo(farthest.space, at))) farthest = b;
                if (farthest == null || best != head && CountyMap.DistanceTo(farthest.space, at) < CountyMap.DistanceTo(best, at)) return;
                LetGo(farthest);
                built.Remove(farthest.space.id);
            }
            job = new Built { space = best, startedAt = Now };
            Log?.Invoke($"building {best.name ?? best.kind.ToString()} ({best.kind}) {CountyMap.DistanceTo(best, at):0} tiles off, from {active.name ?? active.kind.ToString()}");
            built[best.id] = job;
            if (best.kind == SpaceKind.Town) Grow(job);
#if !NO_THREADS
            // A stretch of highway is a long thin town of its own (half a second's work for the longest): on a worker thread too.
            else
            {
                var road = best;
                job.thread = Task.Run(() =>
                {
                    var m = RoadSpaces.Build(map, road);
                    return (m, TownWorld.Prepared.Of(m));
                });
            }
#else
            // No threads on the web: a share of its tiles a frame.
            else
            {
                var b = job;
                b.growing = RoadSpaces.BuildSteps(map, best, m => b.model = m).GetEnumerator();
            }
#endif
        }

        /// <summary>A space you've no more use for: taken apart over the next frames (all at once stalls the game).</summary>
        void LetGo(Built b)
        {
            if (b.world == null || b.world == view.world) return;
            retiring.Add(b.world.DisposeSteps().GetEnumerator());
            b.world = null;
        }

        /// <summary>A share of what's being let go: a few milliseconds' worth a frame, each in turn.</summary>
        void Retire()
        {
            // A share of one at a time, round them all: several let go at once (a town and the stretches of road
            // round it), each its share every frame, were a frame of well over 100 ms. Only walking a town's tree
            // goes on for a few milliseconds more: what's destroyed is paid for at the end of the frame, where
            // timing here doesn't see it, so one share of that a frame.
            double t0 = Now;
            string last = null;
            while (retiring.Count > 0 && (last == null || last == "walk" && Now - t0 < RETIRE_MS))
            {
                retireNext %= retiring.Count;
                if (!retiring[retireNext].MoveNext())
                {
                    retiring.RemoveAt(retireNext);
                    last = "palette";
                }
                else last = retiring[retireNext++].Current;
            }
            if (Now - t0 > 20) Log?.Invoke($"slow step: letting go ({last}) {Now - t0:0} ms");
        }

        /// <summary>Where you can walk, drive and fly in a built space, in its terms (its hills too): off the main thread where there are threads.</summary>
        void BeginWays(Built b)
        {
            b.ways = new ExploreView.PreparedWays();
            var space = b.space;
            var prepare = ExploreView.PreparedWays.Prepare(b.world, (x, y) => Terrain.Height(space.x0 + x, space.y0 + y), b.ways);
#if NO_THREADS
            b.preparing = prepare.GetEnumerator();
#else
            b.preparingThread = Task.Run(() => { foreach (var _ in prepare) { } });
#endif
        }

        /// <summary>A space's ways, if they're worked out (else the view works them out as you go in).</summary>
        static ExploreView.PreparedWays WaysOf(Built b) =>
            b.ways != null && (b.preparingThread != null ? b.preparingThread.Status == TaskStatus.RanToCompletion : b.preparing == null) ? b.ways : null;

        /// <summary>A town grows from its key: on a worker thread where there are threads, else a stage a step.</summary>
        void Grow(Built b)
        {
            var (seed, p) = parseKey(b.space.townKey);
#if NO_THREADS
            b.grownFrom = GrownOf(b.space.townKey);
#else
            // Grown already (or growing) since the county started: waits for that rather than growing it twice.
            if (!grown.TryGetValue(b.space.townKey, out var g))
                grown[b.space.townKey] = g = new Lazy<(TownModel, TownWorld.Prepared)>(() =>
                {
                    var m = Generator.Generate(seed, p);
                    return (m, TownWorld.Prepared.Of(m));
                }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
            b.thread = g.IsValueCreated ? Task.FromResult(g.Value) : Task.Run(() => g.Value);
#endif
        }

        /// <summary>A town's kept model, or one begun (grown a share at a time by whoever needs it first).</summary>
        Grown GrownOf(string key)
        {
            if (kept.TryGetValue(key, out var g)) return g;
            var (seed, p) = parseKey(key);
            g = kept[key] = new Grown();
            g.steps = GrowSteps(g, seed, p).GetEnumerator();
            return g;
        }

        static IEnumerable<string> GrowSteps(Grown g, string seed, GenParams p)
        {
            foreach (var part in Generator.GenerateSteps(seed, p, m => g.model = m)) yield return "growing " + part;
            if (g.model == null) throw new Exception("the town didn't grow");
            foreach (var part in TownWorld.Prepared.Steps(g.model, pr => g.prepared = pr)) yield return "preparing " + part;
        }

        /// <summary>A town that failed to grow ahead isn't tried again till the county is.</summary>
        readonly HashSet<string> wontGrow = new HashSet<string>();

        /// <summary>
        /// Nothing being built: a share of the next town not grown yet, so it's only its world to build when you
        /// get there. The one you're heading for first, then Porchlight (every way out of your own towns comes
        /// through it, and it's the biggest), then the nearest.
        /// </summary>
        void PreGrow(double budget)
        {
            Grown next = null;
            string nextKey = null;
            double best = double.PositiveInfinity;
            var head = Heading?.Invoke();
            var at = view.PlayerPosition is Vec2 local ? active.ToWorld(local) : new Vec2(active.x0, active.y0);
            foreach (var s in map.spaces)
            {
                if (s.kind != SpaceKind.Town || s.townKey == null || wontGrow.Contains(s.townKey)) continue;
                if (kept.TryGetValue(s.townKey, out var have) && have.Ready) continue;
                double d = s == head ? -2 : s.place == County.Place.Porchlight ? -1 : CountyMap.DistanceTo(s, at);
                if (d < best)
                {
                    best = d;
                    nextKey = s.townKey;
                }
            }
            if (nextKey == null) return;
            next = GrownOf(nextKey);
            double until = Now + budget;
            try
            {
                while (!next.Ready && Now < until)
                    if (!next.steps.MoveNext() && !next.Ready) throw new Exception("the town didn't grow");
                if (next.Ready) Log?.Invoke($"grown ahead: {map.spaces.Find(s => s.townKey == nextKey)?.name}");
            }
            catch (Exception e)
            {
                Debug.LogWarning("county: a town didn't grow: " + e.Message);
                kept.Remove(nextKey);
                wontGrow.Add(nextKey);
            }
        }

        /// <summary>How far along a space's building is (0-1): growing, then its world's parts in turn.</summary>
        static double Progress(Built b)
        {
            if (b.ready) return 1;
            if (b.world == null) return b.model == null ? 0.05 : 0.15;
            string part = (b.part ?? "").Replace(TownWorld.WAITING, "");
            if (part == "ways") return 0.995;
            int rooms = Math.Max(1, b.world.rooms.Count);
            double Through(string prefix, double from, double to, double count)
            {
                int.TryParse(part.Substring(prefix.Length), out int k);
                return from + (to - from) * Math.Min(1, (k + 1) / Math.Max(1, count));
            }
            if (part.StartsWith("furnishing")) return Through("furnishing", 0.35, 0.45, rooms / 48.0);
            if (part.StartsWith("doors")) return Through("doors", 0.45, 0.5, rooms / 64.0);
            if (part.StartsWith("interiors") && part != "interiors") return Through("interiors", 0.5, 0.7, rooms / 32.0);
            if (part.StartsWith("signs")) return Through("signs", 0.82, 0.9, Math.Max(1, b.world.signs.Count / 12.0));
            if (part.StartsWith("batching") && part != "batching") return Through("batching", 0.93, 0.99, 8);
            // Trees, lamps and the survey in several parts each: as far as the part they're in.
            switch (part.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9'))
            {
                case "ground": return 0.2;
                case "water": return 0.22;
                case "roads": return 0.27;
                case "houses": return 0.35;
                case "screens": return 0.45;
                case "rooms": return 0.5;
                case "interiors": return 0.7;
                case "yards": return 0.73;
                case "trees": return 0.75;
                case "plazas": return 0.77;
                case "parked": return 0.78;
                case "airport": return 0.781;
                case "zoo": return 0.782;
                case "ports": return 0.783;
                case "sheds": return 0.785;
                case "lamps": return 0.79;
                case "places": return 0.8;
                case "signposts": return 0.82;
                case "signs": return 0.9;
                case "survey": return 0.93;
                default: return 0.18;
            }
        }

        /// <summary>The job in hand, a few milliseconds' worth.</summary>
        void Step(double budget)
        {
            double until = Now + budget;
            while (job != null && Now < until)
            {
                var b = job;
                double t0 = Now;
                string phase = b.model == null ? "growing" : b.world == null ? "preparing" : b.steps != null ? null : "ways";
                try
                {
                    if (b.model == null && b.grownFrom != null)
                    {
                        var g = b.grownFrom;
                        if (!g.Ready)
                        {
                            if (!g.steps.MoveNext() && !g.Ready) throw new Exception("the town didn't grow");
                            continue;
                        }
                        b.model = g.model;
                        b.prepared = g.prepared;
                        continue;
                    }
                    if (b.model == null)
                    {
                        if (b.thread != null)
                        {
                            if (!b.thread.IsCompleted) return;
                            (b.model, b.prepared) = b.thread.Result;
                        }
                        else if (b.growing != null)
                        {
                            if (!b.growing.MoveNext() && b.model == null) throw new Exception("the town didn't grow");
                            continue;
                        }
                        else b.model = RoadSpaces.Build(map, b.space);
                        continue;
                    }
                    if (b.world == null)
                    {
                        // Its layout, street names and signposts: worked out on a worker thread where there are
                        // threads (Grow), else here a share a frame (all at once, TownWorld stalled the web build).
                        if (b.prepared == null)
                        {
                            b.preparingTown ??= TownWorld.Prepared.Steps(b.model, pr => b.prepared = pr).GetEnumerator();
                            if (b.preparingTown.MoveNext()) continue;
                            b.preparingTown = null;
                        }
                        b.world = new TownWorld(b.model, view.Library, stepwise: true, inCounty: true, prepared: b.prepared);
                        Place(b.world.root.transform, b.space.x0, b.space.y0);
                        b.steps = b.world.Build().GetEnumerator();
                        continue;
                    }
                    if (b.steps != null)
                    {
                        if (b.steps.MoveNext())
                        {
                            b.part = b.steps.Current;
                            // Waiting on a worker thread: nothing more to do this frame.
                            if (b.part.EndsWith(TownWorld.WAITING)) return;
                            continue;
                        }
                        // Built: now where you can walk, drive and fly in it, in its terms (its hills too).
                        b.steps = null;
                        b.part = "ways";
                        BeginWays(b);
                        continue;
                    }
                    if (b.preparingThread != null)
                    {
                        if (!b.preparingThread.IsCompleted) return;
                        if (b.preparingThread.IsFaulted) throw b.preparingThread.Exception;
                    }
                    else if (b.preparing != null)
                    {
                        if (b.preparing.MoveNext()) continue;
                        b.preparing = null;
                    }
                    b.ready = true;
                    job = null;
                    Log?.Invoke($"built {b.space.name ?? b.space.kind.ToString()} in {(Now - b.startedAt) / 1000:0.0} s ({b.work:0} ms of work), the longest step {b.longest:0} ms ({b.longestAt}); by part: {string.Join(", ", b.byPart.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{kv.Key} {kv.Value:0}"))}; {(b.world != null ? $"{b.world.rooms.Count} rooms, {b.world.root.GetComponentsInChildren<Transform>(true).Length} objects, {b.world.root.GetComponentsInChildren<MeshFilter>(true).Length} mesh filters" : "")}");
                }
                catch (Exception err)
                {
                    Debug.LogException(err);
                    if (b.grownFrom != null && !b.grownFrom.Ready) kept.Remove(b.space.townKey);
                    b.world?.Dispose();
                    b.world = null;
                    b.failed = true;
                    b.failedAt = Now;
                    job = null;
                }
                finally
                {
                    double ms = Now - t0;
                    b.work += ms;
                    if (ms > 20) Log?.Invoke($"slow step: {b.space.name ?? b.space.kind.ToString()} {phase ?? b.part}{(phase == "ways" ? " " + b.preparing?.Current : phase == "growing" && b.grownFrom != null && !b.grownFrom.Ready ? " " + b.grownFrom.steps.Current : "")} {ms:0} ms");
                    if (Log != null)
                    {
                        string k = (phase ?? b.part ?? "?").Replace(TownWorld.WAITING, "").TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
                        b.byPart[k] = (b.byPart.TryGetValue(k, out var had) ? had : 0) + ms;
                    }
                    if (ms > b.longest)
                    {
                        b.longest = ms;
                        b.longestAt = phase ?? b.part;
                    }
                }
            }
        }

        /// <summary>Into the next space: the view moves into it, and every built space shifts so nothing on screen moves.</summary>
        void Switch(Built next)
        {
            double dx = next.space.x0 - active.x0, dy = next.space.y0 - active.y0;
            // The new space is the one you're in before the view takes it on: its walk mask and sky floor
            // ask the county's hills (view.Rise) in its terms. Set after, they were asked where the space
            // you'd left lies, so a town driven into was mostly "hillside": lots and building floors closed,
            // only the paved streets walkable, and no way into any building on foot.
            double t0 = Now;
            active = next.space;
            view.SwitchSpace(next.world, dx, dy, WaysOf(next));
            double t1 = Now;
            foreach (var b in built.Values)
                if (b.world != null) Place(b.world.root.transform, b.space.x0, b.space.y0);
            Place(countryside.transform, 0, 0);
            double t2 = Now;
            Frame?.Refresh();
            OnSpace?.Invoke(active, active.kind == SpaceKind.Town ? next.model : null);
            double t3 = Now;
            if (t3 - t0 > 20) Log?.Invoke($"slow step: into {active.name ?? active.kind.ToString()} {t3 - t0:0} ms (the view {t1 - t0:0}, moving the spaces {t2 - t1:0}, the rest {t3 - t2:0})");
        }

        /// <summary>
        /// The fields between the towns: one ground over the whole county, lower than the towns' (with
        /// holes where the towns and plots are, so their lakes and rivers show), the highways laid on it.
        /// </summary>
        GameObject Countryside()
        {
            const double MARGIN = 1200;
            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity, x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
            var holes = new List<Space>();
            foreach (var s in map.spaces)
            {
                x0 = Math.Min(x0, s.x0); y0 = Math.Min(y0, s.y0);
                x1 = Math.Max(x1, s.x0 + s.width); y1 = Math.Max(y1, s.y0 + s.height);
                if (s.kind != SpaceKind.Highway) holes.Add(s);
            }
            x0 -= MARGIN; y0 -= MARGIN; x1 += MARGIN; y1 += MARGIN;
            // Cut the county into a grid along every hole's edges; every cell no hole covers is ground.
            var xs = new SortedSet<double> { x0, x1 };
            var ys = new SortedSet<double> { y0, y1 };
            foreach (var s in holes)
            {
                xs.Add(s.x0); xs.Add(s.x0 + s.width);
                ys.Add(s.y0); ys.Add(s.y0 + s.height);
            }
            var xl = new List<double>(xs);
            var yl = new List<double>(ys);
            var verts = new List<Vector3>();
            var tris = new List<int>();
            for (int j = 0; j + 1 < yl.Count; j++)
                for (int i = 0; i + 1 < xl.Count; i++)
                {
                    double cx = (xl[i] + xl[i + 1]) / 2, cy = (yl[j] + yl[j + 1]) / 2;
                    bool hole = false;
                    foreach (var s in holes) if (s.Contains(cx, cy)) hole = true;
                    if (hole) continue;
                    int v = verts.Count;
                    verts.Add(Coords.Map(xl[i], yl[j], COUNTRY_Y));
                    verts.Add(Coords.Map(xl[i + 1], yl[j], COUNTRY_Y));
                    verts.Add(Coords.Map(xl[i + 1], yl[j + 1], COUNTRY_Y));
                    verts.Add(Coords.Map(xl[i], yl[j + 1], COUNTRY_Y));
                    tris.AddRange(new[] { v, v + 2, v + 1, v, v + 3, v + 2 });
                }
            var mesh = new Mesh { name = "countryside", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            var go = new GameObject("countryside");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = Materials.Lit(GRASS);
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>The hills' mesh is cut into squares of this many grid cells, so the ones out of view aren't drawn.</summary>
        const int HILL_CHUNK = 48;

        /// <summary>
        /// The hills (CountyTerrain) over the countryside, in squares: just a hair over the flat ground
        /// where they meet it, so the two never flicker through each other.
        /// </summary>
        void Hills(Transform parent)
        {
            var t = Terrain;
            var material = Materials.Lit(GRASS);
            for (int cj = 0; cj < t.ny - 1; cj += HILL_CHUNK)
                for (int ci = 0; ci < t.nx - 1; ci += HILL_CHUNK)
                {
                    int w = Math.Min(HILL_CHUNK, t.nx - 1 - ci), h = Math.Min(HILL_CHUNK, t.ny - 1 - cj);
                    var verts = new List<Vector3>((w + 1) * (h + 1));
                    for (int j = 0; j <= h; j++)
                        for (int i = 0; i <= w; i++)
                        {
                            double x = t.x0 + (ci + i) * CountyTerrain.CELL, y = t.y0 + (cj + j) * CountyTerrain.CELL;
                            verts.Add(Coords.Map(x, y, COUNTRY_Y + 0.006 + t.At(ci + i, cj + j)));
                        }
                    var tris = new List<int>();
                    for (int j = 0; j < h; j++)
                        for (int i = 0; i < w; i++)
                        {
                            float top = Math.Max(Math.Max(t.At(ci + i, cj + j), t.At(ci + i + 1, cj + j)), Math.Max(t.At(ci + i, cj + j + 1), t.At(ci + i + 1, cj + j + 1)));
                            if (top <= 0) continue;
                            int v = j * (w + 1) + i;
                            tris.AddRange(new[] { v, v + w + 1, v + 1, v + 1, v + w + 1, v + w + 2 });
                        }
                    if (tris.Count == 0) continue;
                    var mesh = new Mesh { name = "hills" };
                    mesh.SetVertices(verts);
                    mesh.SetTriangles(tris, 0);
                    mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    var go = new GameObject("hills");
                    go.transform.SetParent(parent, false);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = material;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
        }

        // Rooms -----------------------------------------------------------------------------------

        /// <summary>The room for where you are (its town's key, or the north room's), and its size for joining.</summary>
        TownKey KeyOf(string room)
        {
            foreach (var s in map.spaces)
                if (s.kind == SpaceKind.Town && s.townKey == room) return new TownKey { key = room, width = s.width, height = s.height };
            return new TownKey { key = room, width = CountyMap.JUNCTION, height = CountyMap.JUNCTION };
        }

        /// <summary>Into the room of the space you're in (on arriving in the county: its town's).</summary>
        public void EnterRoom() => Net?.Enter(KeyOf(map.RoomOf(active)));

        void Rooms(Vec2 at)
        {
            if (Net == null || Net.RoomKey == null) return;
            string want = map.RoomAt(at);
            if (want != wantRoom)
            {
                wantRoom = want;
                wantSince = Now;
            }
            if (want == Net.RoomKey || Now - wantSince < ROOM_SETTLE_MS) return;
            EnterRoomAt(want, at);
        }

        /// <summary>Over into this room's ground: there, carrying on from here (world tiles; in its terms).</summary>
        void EnterRoomAt(string want, Vec2 at)
        {
            var o = map.RoomOrigin(want);
            var s = view.GetLocalState();
            double height = s?.y ?? 0, yaw = s?.yaw ?? 0;
            Net.Enter(KeyOf(want), new[] { at.x - o.x, height, at.y - o.y, yaw });
            Frame?.Refresh();
        }

        /// <summary>From the room's terms to the space you're in: add this (tiles).</summary>
        public Vec2 RoomToView(string room)
        {
            var o = map.RoomOrigin(room ?? map.RoomOf(active));
            return new Vec2(o.x - active.x0, o.y - active.y0);
        }

        /// <summary>The space you're in is the room's own town (so its traffic, townsfolk and parked cars are the room's).</summary>
        public bool InRoomTown(string room) => active.kind == SpaceKind.Town && room == active.townKey;
    }

    /// <summary>
    /// The network's view of the game, in the room's terms: positions go out less, and come in plus,
    /// the corner of the room's town as seen from the space you're in; and the room's town's traffic,
    /// townsfolk and cars are only shared while you're in that town. Outside the county, as it is.
    /// </summary>
    public sealed class NetFrame : INetHost
    {
        readonly ExploreView view;
        public CountyDrive county;
        public Func<string> Room;
        bool roomHost = true;

        public NetFrame(ExploreView view) => this.view = view;

        Vec2 Shift => county?.RoomToView(Room?.Invoke()) ?? new Vec2(0, 0);
        bool Shared => county == null || county.InRoomTown(Room?.Invoke());

        /// <summary>The space you're in has changed (or the room): who runs its traffic.</summary>
        public void Refresh() => view.SetHost(roomHost || !Shared);

        /// <summary>A spot in the space you're in, in the room's terms (a minimap jump).</summary>
        public (double x, double z) ToRoom(double x, double z)
        {
            var o = Shift;
            return (x - o.x, z - o.y);
        }

        public LocalState? GetLocalState()
        {
            var s = view.GetLocalState();
            if (s == null) return null;
            var o = Shift;
            var v = s.Value;
            v.x -= o.x;
            v.z -= o.y;
            return v;
        }

        public void ApplySpawn(double[] at, string look)
        {
            if (at != null)
            {
                var o = Shift;
                at = new[] { at[0] + o.x, at[1] + o.y, at[2] };
            }
            view.ApplySpawn(at, look);
        }

        public void PlacePlayer(double x, double z, bool teleported)
        {
            var o = Shift;
            view.PlacePlayer(x + o.x, z + o.y, teleported);
        }

        public void SyncRemotes(List<RemoteState> players, string me)
        {
            var o = Shift;
            if (o.x != 0 || o.y != 0)
                for (int i = 0; i < players.Count; i++)
                {
                    var p = players[i];
                    p.x += o.x;
                    p.z += o.y;
                    players[i] = p;
                }
            view.SyncRemotes(players, me);
        }

        public void ClearRemotes() => view.ClearRemotes();

        public void SetOthers(List<OtherPlayer> others)
        {
            var o = Shift;
            for (int i = 0; i < others.Count; i++)
            {
                var p = others[i];
                p.x += o.x;
                p.y += o.y;
                others[i] = p;
            }
            view.SetOthers(others);
        }

        public void Notify(string text) => view.Notify(text);

        public void SetHost(bool host)
        {
            roomHost = host;
            Refresh();
        }

        public WorldState? World() => Shared ? view.World() : null;

        public void ApplyWorld(byte[] cars, byte[] people, List<string> looks)
        {
            if (Shared) view.ApplyWorld(cars, people, looks);
        }

        public void SetTaken(string list)
        {
            if (Shared) view.SetTaken(list);
        }

        public void SetTownNight(bool on) => view.SetTownNight(on);

        public void SyncParked(List<RemoteParked> cars, string me)
        {
            var o = Shift;
            for (int i = 0; i < cars.Count; i++)
            {
                var c = cars[i];
                c.x += o.x;
                c.y += o.y;
                cars[i] = c;
            }
            view.SyncParked(cars, me);
        }

        public ParkedState OwnCar()
        {
            var c = view.OwnCar();
            var o = Shift;
            c.x -= o.x;
            c.z -= o.y;
            return c;
        }
    }
}
