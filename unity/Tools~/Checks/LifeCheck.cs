// The town's life (Life/), piece by piece: scenarios each set up to show one rule of
// the traffic and the townsfolk holding, run on the starter towns (and a generated
// one) at the morning rush. Each prints ok or FAIL and why.
//
//   dotnet run --project "unity/Tools~/Checks" -c Release -- life [towns dir] [only scenarios naming this]

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Gen;
using Porchlight.Life;
using Porchlight.Model;

static class LifeCheck
{
    /// <summary>`only`: just the units and scenarios whose name has it in, for working on one.</summary>
    public static int Run(string dir, string only = null)
    {
        var keys = new List<(string name, string key)>();
        foreach (var f in Directory.GetFiles(dir, "*.json").Where(f => !f.EndsWith(".roads.json")).OrderBy(f => f))
            keys.Add((Path.GetFileNameWithoutExtension(f), JsonDocument.Parse(File.ReadAllText(f)).RootElement.GetProperty("key").GetString()));
        int fails = 0;
        foreach (var (what, check) in Units())
        {
            if (only != null && !what.Contains(only)) continue;
            string err;
            try { err = check(); }
            catch (Exception e) { err = e.ToString(); }
            Console.WriteLine(err == null ? $"ok   {what}" : $"FAIL {what}: {err}");
            if (err != null) fails++;
        }
        foreach (var (name, key) in keys)
        {
            var (seed, p) = TownKey.Parse(key);
            var model = Generator.Generate(seed, p);
            var layout = Layout.Build(model);
            var zoning = Zoning.Plan(model, layout);
            foreach (var (what, check) in Scenarios())
            {
                if (only != null && !what.Contains(only)) continue;
                string err;
                try { err = check(model, layout, zoning); }
                catch (Exception e) { err = e.ToString(); }
                Console.WriteLine(err == null ? $"ok   {name}: {what}" : $"FAIL {name}: {what}: {err}");
                if (err != null) fails++;
            }
        }
        Console.WriteLine(fails == 0 ? "life: all ok" : $"life: {fails} failure(s)");
        return fails == 0 ? 0 : 1;
    }

    delegate string Check(TownModel model, IsoLayout layout, TownZoning zoning);

    static IEnumerable<(string, Check)> Scenarios()
    {
        yield return ("a car stops for a player standing in its way", StopsForPlayer);
        yield return ("nobody's run over: a player stepping out in front of any car, anywhere", NobodyRunOver);
        yield return ("Manage's focus: every person and car out explained, its queue in order, its way drawn from where it is", Focus);
        yield return ("Manage's focus only looks: a morning watched closely comes out the same as one not", FocusOnlyLooks);
        yield return ("turns are named the driver's way: right turns are the tight ones", TurnNames);
        yield return ("vans on their rounds at midday: out, calling, and saying so", VansAtMidday);
        yield return ("Manage's town table: everyone counted once, by who they are and where they are now", PopulationTable);
        yield return ("a parked car stays where it's parked: its driver gets out and walks off from it, and walks back up to it", ParkedStays);
        yield return ("Manage's congestion slider: each hour's hold-ups, and its worst streets worst first", CongestionByHour);
        yield return ("the morning rush travels smart: routes by the traffic, spots held, drivers let in, lights that change for a car", TravelsSmart);
        yield return ("no car drives through a player standing in a junction", JunctionPlayer);
        yield return ("a day: no car starts a turn on red, no two cars share a spot, buses dwell, everyone's somewhere, home by night", Day);
        yield return ("with parking everywhere, nobody hunts for a spot", InfiniteParking);
        yield return ("cars drive smoothly: they brake into a turn and pull away out of it, never lurching, jumping or stalling on the curve", Smooth);
    }

    static IEnumerable<(string, Func<string>)> Units()
    {
        yield return ("the event queue: in time order, ties by key, cancelled and moved events never fire", Events);
        yield return ("the wire: an empty slot goes as the hidden sentinel and comes back hidden; a shown one where it was", Wire);
        yield return ("the wire: a car going round a corner is drawn round it between the server's words, not cut across", WireCorner);
    }

    /// <summary>Given events scheduled out of order, some moved and some cancelled, when popped, then in time (and key) order, each once.</summary>
    static string Events()
    {
        var q = new EventQueue();
        var rng = new Rng("events");
        var want = new Dictionary<long, long>();
        for (int i = 0; i < 2000; i++)
        {
            long key = rng.Int(0, 300), at = rng.Int(0, 10_000);
            switch (rng.Int(0, 4))
            {
                case 0:
                    q.Cancel(key);
                    want.Remove(key);
                    break;
                default:
                    q.Schedule(key, at);
                    want[key] = at;
                    break;
            }
        }
        if (q.Count != want.Count) return $"{q.Count} due, {want.Count} scheduled";
        long lastAt = long.MinValue, lastKey = long.MinValue;
        int n = 0;
        while (q.TryPop(long.MaxValue, out long k, out long t))
        {
            if (!want.TryGetValue(k, out long w)) return $"key {k} fired but was cancelled";
            if (w != t) return $"key {k} fired at {t}, last moved to {w}";
            if (t < lastAt || (t == lastAt && k <= lastKey)) return $"key {k} at {t} after key {lastKey} at {lastAt}";
            lastAt = t;
            lastKey = k;
            want.Remove(k);
            n++;
        }
        if (want.Count > 0) return $"{want.Count} never fired";
        if (q.HeapSize > 0) return "left over in the heap";
        return n > 0 ? null : "nothing fired";
    }

    /// <summary>Given a host with some car and walker slots empty, when packed and followed, then the empty ones are hidden there too and the rest where the host has them.</summary>
    static string Wire()
    {
        var cars = new List<TrafficCar>
        {
            new TrafficCar { x = 10, y = 20, heading = 1, speed = 2 },
            new TrafficCar { hidden = true, x = 5, y = 5 },
            new TrafficCar { x = 655.35, y = 655.35 }, // the far corner: shown, never mistaken for hidden
        };
        var people = new List<Walker> { new Walker("a", 3, 4), new Walker("b", 7, 8) { hidden = true } };
        var sync = new WorldSync();
        sync.Receive(WorldSync.EncodeCars(cars), WorldSync.EncodePeople(people));
        var theirCars = cars.Select(_ => new TrafficCar()).ToList();
        var theirPeople = people.Select(p => new Walker(p.look, 0, 0)).ToList();
        sync.Step(0.0, theirCars, theirPeople);
        for (int i = 0; i < cars.Count; i++)
        {
            if (theirCars[i].hidden != cars[i].hidden) return $"car {i}: hidden {theirCars[i].hidden}, the host's {cars[i].hidden}";
            if (!cars[i].hidden && (Math.Abs(theirCars[i].x - cars[i].x) > 0.02 || Math.Abs(theirCars[i].y - cars[i].y) > 0.02)) return $"car {i} at ({theirCars[i].x}, {theirCars[i].y})";
        }
        for (int i = 0; i < people.Count; i++)
            if (theirPeople[i].hidden != people[i].hidden) return $"person {i}: hidden {theirPeople[i].hidden}, the host's {people[i].hidden}";
        // Out again: shown, straight where they are (not eased from where the slot was).
        cars[1].hidden = false;
        cars[1].x = 40;
        cars[1].y = 41;
        sync.Receive(WorldSync.EncodeCars(cars), WorldSync.EncodePeople(people));
        sync.Step(0.0, theirCars, theirPeople);
        if (theirCars[1].hidden || Math.Abs(theirCars[1].x - 40) > 0.02) return $"car 1 out again at ({theirCars[1].x}, {theirCars[1].y}), hidden {theirCars[1].hidden}";
        return null;
    }

    /// <summary>
    /// Given a car going round a quarter circle (1.2 tiles' radius, at a turn's 1.3 tiles/s) and the server's word
    /// on it ten times a second, when the game draws it sixty times a second, then it stays on the curve and faces
    /// along it (drawn a moment behind, between the words either side), and its turn rate never jumps from one
    /// frame to the next.
    /// </summary>
    static string WireCorner()
    {
        const double R = 1.2, V = 1.3, SENT = 0.1, FRAME = 1 / 60.0;
        var host = new List<TrafficCar> { new TrafficCar() };
        var sync = new WorldSync();
        var theirs = new List<TrafficCar> { new TrafficCar() };
        var none = new List<Walker>();
        double worst = 0, sinceSent = SENT, worstFacing = 0, worstStep = 0, lastHeading = double.NaN, lastTurn = double.NaN;
        double end = Math.PI / 2 * R / V;
        for (double time = 0; time < end; time += FRAME)
        {
            if (sinceSent >= SENT - 1e-9)
            {
                // Round the centre (0, 0) from (R, 0), anticlockwise: facing along the circle.
                double a = time * V / R;
                host[0].x = 50 + R * Math.Cos(a);
                host[0].y = 50 + R * Math.Sin(a);
                host[0].heading = a + Math.PI / 2;
                host[0].speed = V;
                sync.Receive(WorldSync.EncodeCars(host), WorldSync.EncodePeople(none));
                sinceSent = 0;
            }
            sync.Step(FRAME, theirs, none);
            sinceSent += FRAME;
            // Judged once it's had two words to draw between (one alone can't say it's turning), and is drawn
            // between them; until the last word (past it, it's carried straight on, off the curve).
            if (time > 2 * SENT + WorldSync.DELAY && time < end - SENT)
            {
                worst = Math.Max(worst, Math.Abs(Numbers.Hypot(theirs[0].x - 50, theirs[0].y - 50) - R));
                double along = Math.Atan2(theirs[0].y - 50, theirs[0].x - 50) + Math.PI / 2;
                worstFacing = Math.Max(worstFacing, Math.Abs(Porchlight.Explore.Orbit.ShortestTurn(theirs[0].heading, along)));
                if (!double.IsNaN(lastHeading))
                {
                    double turn = Porchlight.Explore.Orbit.ShortestTurn(lastHeading, theirs[0].heading) / FRAME;
                    if (!double.IsNaN(lastTurn)) worstStep = Math.Max(worstStep, Math.Abs(turn - lastTurn));
                    lastTurn = turn;
                }
                lastHeading = theirs[0].heading;
            }
        }
        Console.WriteLine($"     corner: drawn at most {worst:0.000} tiles off it, facing at most {worstFacing * 180 / Math.PI:0.0}° off its way, its turn rate stepping at most {worstStep:0.00} rad/s a frame");
        if (worst > CORNER_OFF) return $"drawn {worst:0.000} tiles off the corner it's going round";
        if (worstFacing * 180 / Math.PI > FACING_OFF) return $"facing {worstFacing * 180 / Math.PI:0.0}° off the way it's going: sliding round sideways";
        if (worstStep > TURN_STEP) return $"its turn rate stepped {worstStep:0.00} rad/s from one frame to the next: a flick of its facing";
        return null;
    }

    /// <summary>
    /// How far a car drawn going round a corner may be off it (tiles), facing off its way (degrees), and its turn rate
    /// step from one frame to the next (rad/s; it turns at 1.08 throughout). Guessed ahead and eased back, as before:
    /// 15° off, and its turn rate stepping as each word came.
    /// </summary>
    const double CORNER_OFF = 0.02, FACING_OFF = 2, TURN_STEP = 0.1;

    /// <summary>
    /// Given a town's whole day, then: no car starts a turn at a signal on red; no two parked cars share a
    /// spot, and each spot's holder is the car parked in it; a bus at a stop waits its dwell; at every minute
    /// everyone's indoors, off the map or out (the counts Manage shows add up); and at three in the morning
    /// everyone who lives in town is home.
    /// </summary>
    static string Day(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var life = new TownLife(model, layout, zoning);
        long start = TrafficDay.Morning - (8 * TownClock.HOUR + 15 * TownClock.MINUTE);
        life.Start(start);
        int L = life.net.lanes.Count;
        var seen = new int[life.cars.Count];
        for (int i = 0; i < seen.Length; i++) seen[i] = -1;
        var atStopSince = new Dictionary<int, long>();
        int turnsChecked = 0;
        for (long t = start; t <= start + 27 * TownClock.HOUR; t += 500)
        {
            life.RunUntil(t);
            foreach (var c in life.cars)
            {
                // Onto a turn since last look: at a signal, on its green.
                if (c.seg >= L && c.seg != seen[c.id])
                {
                    var m = life.net.movements[c.seg - L];
                    var j = life.net.junctions[m.node];
                    if (j.control == Control.Signal && !m.uturn)
                    {
                        // The moment it started: its interval's start, if that's still the turn's first.
                        long began = c.t0;
                        if (life.Front(c) < m.length && RoadNet.Stage(j, began).stage != m.stage)
                            return $"car {c.id} started turn {m.id} (stage {m.stage}) at {TownClock.Format(began)}, stage {RoadNet.Stage(j, began).stage} green";
                        turnsChecked++;
                    }
                }
                seen[c.id] = c.seg;
                if (c.bus >= 0)
                {
                    // Only stops it's seen arrive at: one it was already at when the day began started before.
                    if (c.state == CarState.AtStop) { if (!atStopSince.ContainsKey(c.id)) atStopSince[c.id] = t == start ? long.MinValue : t; }
                    else if (atStopSince.TryGetValue(c.id, out long since))
                    {
                        atStopSince.Remove(c.id);
                        // Seen at a stop at `since` (up to half a second after it got there), and gone by `t`.
                        if (since != long.MinValue && t - since + 500 < Life_DWELL)
                            return $"bus {c.bus} at a stop only {(t - since) / 1000.0:0.0} s, {TownClock.Format(since)} to {TownClock.Format(t)}";
                    }
                }
            }
            if (t % TownClock.MINUTE == 0)
            {
                int inside = life.inside.Values.Sum(), off = life.offMap?.Sum() ?? 0;
                if (inside + off + life.OutNow() != life.people.Count)
                    return $"at {TownClock.Format(t)}: {inside} indoors + {off} off the map + {life.OutNow()} out isn't the {life.people.Count} people";
            }
            if (t % TownClock.HOUR == 0)
            {
                var spots = new Dictionary<int, int>();
                foreach (var c in life.cars)
                {
                    if (c.state != CarState.Parked || c.spot < 0 || c.offMap) continue;
                    if (spots.TryGetValue(c.spot, out int other)) return $"cars {other} and {c.id} both parked in spot {c.spot}";
                    spots[c.spot] = c.id;
                    if (life.SpotHolder(c.spot) != c.id) return $"car {c.id} parked in spot {c.spot}, held by {life.SpotHolder(c.spot)}";
                }
            }
        }
        var away = life.people.Where(p => (p.kind == PersonKind.Worker || p.kind == PersonKind.Student || p.kind == PersonKind.AtHome) && !(p.where == Where.Inside && p.at.Same(p.home))).ToList();
        if (away.Count > life.people.Count / 200 + 1)
            return $"{away.Count} townsfolk not home at 03:00, e.g. #{away[0].id} ({away[0].kind}) {away[0].where} at {away[0].at}";
        if (life.net.junctions.Values.Any(j => j.control == Control.Signal) && turnsChecked == 0) return "no car went through a signal all day";
        return null;
    }

    const long Life_DWELL = TownLife.DWELL;

    /// <summary>Given the infinite-parking option, when a day's run, then no car hunts for a spot or finds none, and the day still ends with everyone home.</summary>
    static string InfiniteParking(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var life = new TownLife(model, layout, zoning, new LifeOptions { infiniteParking = true });
        long start = TrafficDay.Morning - (8 * TownClock.HOUR + 15 * TownClock.MINUTE);
        life.Start(start);
        life.RunUntil(start + 27 * TownClock.HOUR);
        if (life.stats.hunts > 0 || life.stats.noSpot > 0) return $"{life.stats.hunts} hunts, {life.stats.noSpot} found no spot";
        if (life.stats.finishedBy[1] == 0) return "nobody drove";
        if (life.OutNow() > Math.Max(2, life.people.Count / 500)) return $"{life.OutNow()} still out at 03:00";
        return null;
    }

    static Traffic Rush(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var t = new Traffic(model, layout, zoning, new Rng("life"), r => 0);
        t.StartAt(TrafficDay.Morning);
        return t;
    }

    /// <summary>
    /// Given the morning rush drawn ten times a second (as the server sends it), when every car's drawn speed is
    /// followed frame to frame, then speed changes as a driver's does (no more than HARSH tiles/s² but rarely),
    /// and a car that's started round a corner doesn't stop on it with nothing in its way.
    /// </summary>
    static string Smooth(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var t = Rush(model, layout, zoning);
        var life = t.life;
        for (int f = 0; f < 600; f++) t.Update(0.1, null);
        const double DT = 0.1;
        int n = Math.Min(t.cars.Count, life.busSlotsFrom);
        // Each slot's car as drawn last frame: who, where, and how fast.
        var who = new int[n];
        var x = new double[n];
        var y = new double[n];
        var v = new double[n];
        for (int i = 0; i < n; i++) who[i] = -1;
        int moments = 0, harsh = 0, slams = 0, jumps = 0, onTurns = 0, harshOnTurns = 0, stalls = 0;
        int L = life.net.lanes.Count;
        for (int f = 0; f < 3000; f++)
        {
            t.Update(DT, null);
            for (int i = 0; i < n; i++)
            {
                var c = t.cars[i];
                var p = life.carSlots[i];
                var car = p.shown ? life.cars[p.who] : null;
                // On the road (pulling into or out of a spot has its own way of moving).
                bool driving = car != null && !c.hidden && car.seg >= 0 && car.state != CarState.Parking && car.state != CarState.Unparking && car.state != CarState.Parked;
                if (!driving || who[i] != p.who)
                {
                    who[i] = driving ? p.who : -1;
                    x[i] = c.x;
                    y[i] = c.y;
                    v[i] = double.NaN;
                    continue;
                }
                double now = Numbers.Hypot(c.x - x[i], c.y - y[i]) / DT;
                x[i] = c.x;
                y[i] = c.y;
                if (now * DT > JUMPED) jumps++;
                if (!double.IsNaN(v[i]))
                {
                    double a = Math.Abs(now - v[i]) / DT;
                    moments++;
                    if (a > HARSH) harsh++;
                    if (a > SLAM) slams++;
                    if (car.seg >= L)
                    {
                        onTurns++;
                        if (a > HARSH) harshOnTurns++;
                        if (now < 0.02 && v[i] > 0.3) stalls++;
                    }
                }
                v[i] = now;
            }
        }
        string Share(int k, int of) => $"{100.0 * k / Math.Max(1, of):0.00}%";
        Console.WriteLine($"     smooth: {moments} moments driven; speed changing faster than {HARSH} tiles/s² {Share(harsh, moments)} (on turns {Share(harshOnTurns, onTurns)} of {onTurns}), faster than {SLAM} {Share(slams, moments)}; {jumps} jumps, {stalls} dead stops on a turn");
        if (moments < 1000) return $"only {moments} moments driven";
        if (jumps > 0) return $"{jumps} times a car jumped more than {JUMPED} tiles in a frame";
        if (harshOnTurns > onTurns * MOST_HARSH_ON_TURNS) return $"{Share(harshOnTurns, onTurns)} of moments on turns changing speed faster than {HARSH} tiles/s²";
        if (harsh > moments * MOST_HARSH) return $"{Share(harsh, moments)} of moments changing speed faster than {HARSH} tiles/s²";
        if (slams > moments * MOST_SLAMS) return $"{Share(slams, moments)} of moments changing speed faster than {SLAM} tiles/s² (no car can)";
        if (stalls > onTurns / 5000) return $"{stalls} times a car stopped dead on a turn";
        return null;
    }

    /// <summary>Hard braking or pulling away, tiles/s²: about 0.4 g (a tile's 3.97 m). Cars plan for 0.25–0.3 g.</summary>
    const double HARSH = 1.0;
    /// <summary>Faster than any car changes speed (tiles/s², about 1.2 g): a lurch, not driving.</summary>
    const double SLAM = 3;
    /// <summary>
    /// Shares of moments allowed over those: what's left is a car on the road through braking hard for a turn
    /// across it or someone stepping out in its last second, or behind a car that did (before: 3.6% over HARSH).
    /// </summary>
    const double MOST_HARSH = 0.01, MOST_HARSH_ON_TURNS = 0.005, MOST_SLAMS = 0.002;
    /// <summary>Further than this in a frame (tiles) isn't driving: a car put somewhere new.</summary>
    const double JUMPED = 0.6;

    /// <summary>Given a car going along a lane, when a player stands in front of it, then within two seconds it's stopped short of them, and goes again once they've gone.</summary>
    static string StopsForPlayer(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var t = Rush(model, layout, zoning);
        var life = t.life;
        for (int tries = 0; tries < 6000; tries++)
        {
            t.Update(0.1, null);
            for (int k = 0; k < life.busSlotsFrom; k++)
            {
                var c = t.cars[k];
                if (c.hidden || c.speed < 0.5) continue;
                var car = life.cars[life.carSlots[k].who];
                if (car.seg < 0 || car.seg >= life.net.lanes.Count) continue;
                var lane = life.net.lanes[car.seg];
                double front = life.Front(car);
                if (front + 3 > lane.Span) continue;
                // On the lane, 1.5 ahead of its nose.
                var stand = life.net.LanePoint(lane, lane.start + front + 1.5).at;
                var you = new List<Vec2> { stand };
                int who = car.id;
                for (int f = 0; f < 20; f++) t.Update(0.1, you);
                double nose = life.Front(car);
                if (car.seg != lane.id) return $"car {who} drove on through the player (now on segment {car.seg})";
                if (nose > front + 1.5 - 0.2) return $"car {who} at {nose:0.00} reached the player at {front + 1.5:0.00}";
                if (c.speed > 0.05) return $"car {who} still going at {c.speed:0.00} tiles a second";
                for (int f = 0; f < 50; f++) t.Update(0.1, null);
                // (Or it's stopped where it was going anyway: in a queue, at the junction, or pulling into its spot.)
                if (car.seg == lane.id && life.Front(car) <= nose + 1e-6 && car.state != CarState.Queued && car.state != CarState.Waiting && car.state != CarState.Parking) return $"car {who} didn't go again ({car.state})";
                return null;
            }
        }
        return "no car on the move to stand in front of";
    }

    /// <summary>
    /// Given the morning rush, when every person on a trip and every car on the road is focused on, a minute at a
    /// time, then each has a decision tree whose way-of-getting-there answer is the mode the trip really took (and
    /// a waiting car's tree has the junction's own questions), a queue with the step under way first and the
    /// day's plan in time order after, and a way ahead that starts where they are and (a car's) ends where it's
    /// going; and the slot each is drawn in leads back to them.
    /// </summary>
    static string Focus(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var t = Rush(model, layout, zoning);
        var life = t.life;
        int people = 0, carsSeen = 0, waiting = 0;
        var modes = new Dictionary<Mode, string> { [Mode.Walk] = "By foot", [Mode.Drive] = "By car", [Mode.Bus] = "By bus" };
        for (int minute = 0; minute < 20; minute++)
        {
            for (int f = 0; f < 600; f++) t.Update(0.1, null);
            foreach (var p in life.people)
            {
                var o = life.FocusPerson(p.id);
                if (o == null) return $"person {p.id}: no focus";
                Json(o);
                var tree = (List<Dictionary<string, object>>)o["tree"];
                if (tree.Count == 0) return $"person {p.id}: an empty decision tree";
                var queue = (List<Dictionary<string, object>>)o["queue"];
                if (p.trip == null) continue;
                people++;
                // Their day: the trip under way once, the next at most once, all in time order; the hover's line agrees.
                var day = (Dictionary<string, object>)o["day"];
                var entries = (List<Dictionary<string, object>>)day["entries"];
                if (entries.Count(e => (string)e["status"] == "now") != 1) return $"person {p.id}: on a trip, but their day has {entries.Count(e => (string)e["status"] == "now")} under way";
                if (entries.Count(e => (string)e["status"] == "next") > 1) return $"person {p.id}: two next trips in their day";
                for (int k = 1; k < entries.Count; k++)
                    if ((long)entries[k]["at"] < (long)entries[k - 1]["at"] && (string)entries[k]["status"] != "next" && (string)entries[k]["status"] != "later")
                        return $"person {p.id}: their day's out of order at {entries[k]["time"]}";
                if (string.IsNullOrEmpty((string)day["decides"])) return $"person {p.id}: nothing says what decides what's next";
                string brief = life.BriefPerson(p.id);
                if (!brief.Contains(p.trip.mode == Mode.Drive ? "by car" : p.trip.mode == Mode.Bus ? "by bus" : "by foot")) return $"person {p.id}: the hover says \"{brief}\"";
                var how = tree.FirstOrDefault(w => (string)w["q"] == "How are they getting there?");
                if (how == null) return $"person {p.id}: no way-of-getting-there in their tree";
                if ((string)how["a"] != modes[p.trip.mode]) return $"person {p.id}: the tree says \"{how["a"]}\" but the trip is by {p.trip.mode}";
                if (queue.Count == 0 || !(bool)queue[0]["now"]) return $"person {p.id}: the queue doesn't start with what they're doing now";
                long last = long.MinValue;
                foreach (var item in queue)
                {
                    if (!(item["at"] is long at)) continue;
                    if (at < last) return $"person {p.id}: the queue's out of order ({item["what"]} at {item["time"]})";
                    last = at;
                }
                if (p.slot >= 0 && life.personSlots[p.slot].shown && life.PersonInSlot(p.slot) != p.id) return $"person {p.id}: slot {p.slot} leads to {life.PersonInSlot(p.slot)}";
                if (p.slot >= 0 && life.personSlots[p.slot].shown && p.walk != null)
                {
                    var path = (List<double[]>)o["path"];
                    if (path.Count == 0) return $"person {p.id}: walking, but no way ahead";
                    var here = life.PersonPoint(p).at;
                    if (Numbers.Hypot(path[0][0] - here.x, path[0][1] - here.y) > 0.05) return $"person {p.id}: their way starts {Numbers.Hypot(path[0][0] - here.x, path[0][1] - here.y):0.00} tiles from them";
                }
            }
            foreach (var c in life.cars)
            {
                if (c.seg < 0) continue;
                var o = life.FocusCar(c.id);
                Json(o);
                carsSeen++;
                var tree = (List<Dictionary<string, object>>)o["tree"];
                long before = long.MinValue;
                foreach (var item in (List<Dictionary<string, object>>)o["queue"])
                {
                    if (!(item["at"] is long at)) continue;
                    if (at < before) return $"car {c.id}: the queue's out of order ({item["what"]} at {item["time"]})";
                    before = at;
                }
                if (c.state == CarState.Waiting && c.seg < life.net.lanes.Count)
                {
                    waiting++;
                    var may = tree.FirstOrDefault(w => (string)w["q"] == "May it take its turn?");
                    if (may == null || !may.ContainsKey("under")) return $"car {c.id}: waiting, but its tree doesn't have the junction's questions";
                }
                var path = (List<double[]>)o["path"];
                if (c.route != null && path.Count > 0)
                {
                    var here = life.CarPoint(c).at;
                    double off = Numbers.Hypot(path[0][0] - here.x, path[0][1] - here.y);
                    if (off > 0.05) return $"car {c.id}: its way starts {off:0.00} tiles from it";
                    if (o["dest"] is double[] d && path.Count < 400)
                    {
                        double end = Numbers.Hypot(path[^1][0] - d[0], path[^1][1] - d[1]);
                        if (end > 1.5) return $"car {c.id}: its way ends {end:0.00} tiles from where it's going";
                    }
                }
                if (c.slot >= 0 && life.carSlots[c.slot].shown && life.CarInSlot(c.slot) != c.id) return $"car {c.id}: slot {c.slot} leads to {life.CarInSlot(c.slot)}";
            }
        }
        if (people < 50 || carsSeen < 10) return $"too few looked at ({people} people on trips, {carsSeen} cars)";
        return null;
    }

    static void Json(object o) => JsonSerializer.Serialize(o);

    /// <summary>
    /// Given the same town twice through the same two hours of the morning, when one is focused on (every person
    /// and car on the road, every ten seconds) and the other isn't, then both end the same: the same trips made,
    /// the same people out, every car where the other's is.
    /// </summary>
    static string FocusOnlyLooks(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var a = Rush(model, layout, zoning);
        var b = Rush(model, layout, zoning);
        for (int s = 0; s < 720; s++)
        {
            for (int f = 0; f < 100; f++)
            {
                a.Update(0.1, null);
                b.Update(0.1, null);
            }
            foreach (var p in b.life.people) if (p.trip != null) b.life.FocusPerson(p.id);
            foreach (var c in b.life.cars) if (c.seg >= 0) b.life.FocusCar(c.id);
        }
        if (a.life.stats.started != b.life.stats.started || a.life.stats.finished != b.life.stats.finished)
            return $"looked at, {b.life.stats.started} trips started and {b.life.stats.finished} made; not, {a.life.stats.started} and {a.life.stats.finished}";
        for (int i = 0; i < a.life.cars.Count; i++)
        {
            var x = a.life.cars[i];
            var y = b.life.cars[i];
            if (x.seg != y.seg || Math.Abs(a.life.Front(x) - b.life.Front(y)) > 1e-9 || x.state != y.state)
                return $"car {i}: {y.state} on {y.seg} at {b.life.Front(y):0.000} looked at, {x.state} on {x.seg} at {a.life.Front(x):0.000} not";
        }
        return null;
    }

    /// <summary>
    /// Given every turn in the town, when each is named the driver's way, then the right turns are the tight ones
    /// (shorter on average than the left: traffic keeps right), and there are some of each.
    /// </summary>
    /// <summary>
    /// Given a town at noon, then vans are out on their rounds (a car on the road with no owner), each van going to
    /// a call it'll drop off at without parking, and Manage's focus and hover say it's a van on its rounds.
    /// </summary>
    static string VansAtMidday(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var life = new TownLife(model, layout, zoning);
        if (life.vans.Count == 0) return "no vans";
        life.Start(TrafficDay.Morning + 4 * TownClock.HOUR);
        var out_ = life.vans.Where(v => v.seg >= 0).ToList();
        if (out_.Count < life.vans.Count / 4) return $"only {out_.Count} of {life.vans.Count} vans out at noon";
        foreach (var v in out_)
        {
            if (v.owner >= 0) return $"van {v.van} has an owner";
            if (v.target >= 0) return $"van {v.van} is holding a parking spot";
            var f = life.FocusCar(v.id);
            var tree = (List<Dictionary<string, object>>)f["tree"];
            if (!((string)tree[0]["a"]).StartsWith("A van on its rounds")) return $"van {v.van}'s focus says \"{tree[0]["a"]}\"";
            if (!life.BriefCar(v.id).StartsWith($"Van #{v.van} ")) return $"van {v.van}'s hover says \"{life.BriefCar(v.id)}\"";
        }
        // An hour on, they've moved on to other calls, and none's stuck.
        var calls = out_.ToDictionary(v => v.id, v => v.dest.id);
        life.RunUntil(life.Now + TownClock.HOUR);
        int moved = out_.Count(v => v.seg >= 0 && v.dest.id != calls[v.id]);
        if (moved < out_.Count / 2) return $"only {moved} of {out_.Count} vans on to another call an hour on";
        return null;
    }

    /// <summary>
    /// Given the morning rush, when cars park in a spot, then each is drawn standing in it, facing along its lane, for
    /// as long as it keeps its slot (it gives it up only to a car coming out on the road with none free); and when a
    /// driver walks out to their parked car, it's drawn there as they walk up to it.
    /// </summary>
    static string ParkedStays(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var life = new TownLife(model, layout, zoning);
        life.Start(TrafficDay.Morning);
        var wasOnRoad = new bool[life.cars.Count];
        int parkedShown = 0, parkedHidden = 0, walksUp = 0, walksUpShown = 0;
        for (int f = 0; f < 6000; f++)
        {
            life.RunUntil(life.Now + 100);
            life.Draw();
            for (int i = 0; i < life.carSlots.Length; i++)
            {
                var pose = life.carSlots[i];
                if (!pose.shown) continue;
                var c = life.cars[pose.who];
                if (c.seg >= 0) continue;
                if (c.spot < 0) return $"car {c.id} drawn off the road with no spot to stand in";
                var spot = life.net.spots[c.spot];
                if (Geo.Dist(new Vec2(pose.x, pose.y), spot.at) > 1e-6) return $"car {c.id} drawn {Geo.Dist(new Vec2(pose.x, pose.y), spot.at):0.00} tiles from its spot";
            }
            for (int id = 0; id < life.cars.Count && id < wasOnRoad.Length; id++)
            {
                var c = life.cars[id];
                bool onRoad = c.seg >= 0;
                // Just parked in a spot: still drawn (unless a car coming out on the road took its slot this moment).
                if (wasOnRoad[id] && !onRoad && c.spot >= 0 && c.state == CarState.Parked && c.bus < 0 && c.van < 0)
                {
                    if (c.slot >= 0 && life.carSlots[c.slot].shown) parkedShown++;
                    else parkedHidden++;
                }
                wasOnRoad[id] = onRoad;
            }
            foreach (var p in life.people)
            {
                var t = p.trip;
                if (t == null || t.leg < 0 || t.leg + 1 >= t.legs.Count || t.legs[t.leg].kind != LegKind.Walk || t.legs[t.leg + 1].kind != LegKind.Drive) continue;
                if (p.car < 0 || life.cars[p.car].spot < 0 || life.cars[p.car].seg >= 0) continue;
                walksUp++;
                var car = life.cars[p.car];
                if (car.slot >= 0 && life.carSlots[car.slot].shown) walksUpShown++;
            }
        }
        Console.WriteLine($"     parked: {parkedShown} cars parked and stayed drawn, {parkedHidden} not; {walksUpShown} of {walksUp} moments walking up to a parked car it was there");
        if (parkedShown == 0) return "no car parked and stayed drawn";
        if (parkedHidden > parkedShown / 10) return $"{parkedHidden} cars vanished as they parked (and {parkedShown} stayed)";
        if (walksUp == 0) return "nobody walked out to a parked car";
        if (walksUpShown < walksUp * 0.95) return $"a driver walked up to a car that wasn't there {walksUp - walksUpShown} times of {walksUp}";
        return null;
    }

    /// <summary>
    /// Given the morning rush run on, when Manage asks for the last day's congestion, then there are 24 hours, oldest
    /// first, a whole hour apart, the last this one; this hour's streets come worst first, held cars up for a time and
    /// add up to the hour's hold-ups in all; and an hour before the town started has none.
    /// </summary>
    static string CongestionByHour(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var life = new TownLife(model, layout, zoning);
        life.Start(TrafficDay.Morning);
        life.RunUntil(TrafficDay.Morning + 30 * TownClock.MINUTE);
        var hours = life.HoldsByHour();
        if (hours.Length != TownLife.HOURS_KEPT) return $"{hours.Length} hours, not {TownLife.HOURS_KEPT}";
        for (int i = 1; i < hours.Length; i++) if (hours[i].from - hours[i - 1].from != TownClock.HOUR) return $"hour {i} isn't an hour after the one before";
        if (life.Now - hours[hours.Length - 1].from >= TownClock.HOUR || life.Now < hours[hours.Length - 1].from) return "the last hour isn't this one";
        var now = hours[hours.Length - 1];
        if (now.cars == 0) return "no cars counted leaving a lane this hour, in the rush";
        var all = life.MostHeldUp(0, int.MaxValue);
        if (all.Count == 0) return "no street held anyone up, in the rush";
        for (int i = 1; i < all.Count; i++) if (all[i].hold > all[i - 1].hold) return "the streets aren't worst first";
        double sum = all.Sum(r => r.hold);
        if (Math.Abs(sum - now.hold) > 1e-6 * Math.Max(1, now.hold)) return $"the streets' hold-ups add up to {sum:0.0} s, the hour's are {now.hold:0.0} s";
        if (life.MostHeldUp(0, 5).Count > 5) return "more than the 5 streets asked for";
        if (life.MostHeldUp(TownLife.HOURS_KEPT - 1, 5).Count != 0 || hours[0].cars != 0) return "an hour before the town started has hold-ups";
        Console.WriteLine($"     congestion: this hour {now.cars} cars left a lane, {now.hold / 60:0} min held up in all; the worst street {all[0].hold / 60:0.0} min ({all[0].cars} cars)");
        return null;
    }

    /// <summary>
    /// Given a town at 10:00 and again at 03:00, when Manage asks for its population table, then each row's places add
    /// up to its people; the rows add up to the town's people, its out (walking, driving and on the bus) to those on a
    /// trip, and its off the map to those off it. At 10:00 workers and students are mostly at work or school; at 03:00
    /// residents are all at home.
    /// </summary>
    static string PopulationTable(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var life = new TownLife(model, layout, zoning);
        foreach (long at in new[] { TrafficDay.Morning + 2 * TownClock.HOUR, TrafficDay.Morning + 19 * TownClock.HOUR })
        {
            if (!life.Started) life.Start(at);
            else life.RunUntil(at);
            var rows = life.Population();
            var cols = TownLife.POPULATION_COLUMNS;
            long Col(object[] row, string c) => (long)row[1 + Array.IndexOf(cols, c)];
            long Sum(string c) => rows.Sum(row => Col(row, c));
            string when = TownClock.Format(at);
            foreach (var row in rows)
            {
                long places = cols.Skip(1).Take(7).Sum(c => Col(row, c));
                if (places != Col(row, "people")) return $"{when}: {row[0]}'s places add up to {places}, not its {Col(row, "people")} people";
                if (Col(row, "ownCar") > Col(row, "people")) return $"{when}: more {row[0]} own a car than there are";
            }
            if (Sum("people") != life.people.Count) return $"{when}: the rows add up to {Sum("people")}, the town has {life.people.Count}";
            long onTrip = life.people.Count(p => p.where == Where.OnTrip), off = life.people.Count(p => p.where == Where.OffMap);
            if (Sum("walking") + Sum("driving") + Sum("onBus") != onTrip) return $"{when}: {Sum("walking") + Sum("driving") + Sum("onBus")} out in the table, {onTrip} on a trip";
            if (Sum("offMap") != off) return $"{when}: {Sum("offMap")} off the map in the table, {off} off it";
            var workers = rows.Where(row => (string)row[0] == "Worker" || (string)row[0] == "Student").ToList();
            long residents = rows.Where(row => (string)row[0] != "CommuterIn" && (string)row[0] != "Through").Sum(row => Col(row, "people"));
            if (at < TrafficDay.Morning + 12 * TownClock.HOUR)
            {
                long busy = workers.Sum(row => Col(row, "workOrSchool")), all = workers.Sum(row => Col(row, "people"));
                if (all > 0 && busy < all / 2) return $"{when}: only {busy} of {all} workers and students at work or school";
            }
            else
            {
                long home = rows.Where(row => (string)row[0] != "CommuterIn" && (string)row[0] != "Through").Sum(row => Col(row, "home"));
                if (home != residents) return $"{when}: {home} of {residents} residents at home";
            }
        }
        return null;
    }

    /// <summary>
    /// Given a town's morning rush (07:30-09:30), then: the router's been told the lanes' hold-ups; drivers going to a
    /// building hold a kerb spot near its door, none more than RESERVE_REACH away; a driver waiting to pull out was let
    /// in; a light changed for a car waiting at red with nobody coming the other way (if the town has lights); and in a
    /// town big enough to queue, a held-up driver took a quicker way.
    /// </summary>
    static string TravelsSmart(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var life = new TownLife(model, layout, zoning);
        life.Start(TrafficDay.Morning - 45 * TownClock.MINUTE);
        life.RunUntil(TrafficDay.Morning + 75 * TownClock.MINUTE);
        var s = life.stats;
        if (s.reserved == 0) return "nobody held a spot as they set off";
        foreach (var c in life.cars)
        {
            if (c.seg < 0 || c.target < 0 || !c.reserved || c.dest.border || c.van >= 0) continue;
            var spot = life.net.spots[c.target];
            if (spot.kind != SpotKind.Kerb || !life.net.access.TryGetValue(c.dest.id, out var a)) continue;
            double d = Geo.Dist(spot.at, a.door);
            if (d > 30 + 1e-6) return $"car {c.id} holds a spot {d:0.0} tiles from the door";
        }
        bool lights = life.net.junctions.Values.Any(j => j.control == Control.Signal);
        if (lights && s.lightsChanged == 0) return "no light changed for a car waiting with nobody coming the other way";
        if (life.people.Count > 10000 && s.rerouted == 0) return "in a town this big, no held-up driver took another way";
        if (life.people.Count > 10000 && s.letIn == 0) return "in a town this big, no driver let anyone pull out";
        return null;
    }

    static string TurnNames(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var life = new TownLife(model, layout, zoning);
        var right = new List<double>();
        var left = new List<double>();
        foreach (var m in life.net.movements)
        {
            string name = life.TurnText(m);
            if (name == "turn right") right.Add(m.length);
            else if (name == "turn left") left.Add(m.length);
        }
        if (right.Count == 0 || left.Count == 0) return $"{right.Count} right turns and {left.Count} left";
        if (right.Average() >= left.Average()) return $"right turns average {right.Average():0.00} tiles, left {left.Average():0.00}: named the wrong way round";
        return null;
    }

    /// <summary>
    /// Given the morning rush, when a player steps out just in front of a moving car or the bus as it's drawn
    /// (wherever it is: on a lane, on a turn, about to go round one, at the mouth of a junction), then over the
    /// next three seconds no car or bus drawn comes within a car's half-width of them. Tried on many at once over
    /// the morning.
    /// </summary>
    static string NobodyRunOver(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var t = Rush(model, layout, zoning);
        var life = t.life;
        int tried = 0;
        for (int round = 0; round < 400 && tried < 200; round++)
        {
            for (int f = 0; f < 15; f++) t.Update(0.1, null);
            int k = -1;
            // Every few rounds the bus (if it's moving), the rest cars.
            int pick = round % 5 == 4 ? life.carSlots.Length - life.busSlotsFrom : life.busSlotsFrom, from = round % 5 == 4 ? life.busSlotsFrom : 0;
            for (int i = 0; i < pick; i++)
            {
                int n = from + (round * 7 + i) % pick;
                if (!t.cars[n].hidden && t.cars[n].speed > 0.5) { k = n; break; }
            }
            if (k < 0) continue;
            tried++;
            var c = t.cars[k];
            // Just in front of its front bumper (its middle's half its length back: a bus is longer than a car).
            double ahead = life.cars[life.CarInSlot(k)].length / 2 + 0.55;
            var stand = new Vec2(c.x + Math.Cos(c.heading) * ahead, c.y + Math.Sin(c.heading) * ahead);
            // Only somewhere clear: not on (or just behind) another car's back bumper.
            bool clear = true;
            for (int i = 0; i < life.carSlots.Length && clear; i++)
                if (i != k && !t.cars[i].hidden && Numbers.Hypot(t.cars[i].x - stand.x, t.cars[i].y - stand.y) < 1.2) clear = false;
            if (!clear) { tried--; continue; }
            var you = new List<Vec2> { stand };
            int who = life.CarInSlot(k);
            string at = Describe(life, life.cars[who]);
            for (int f = 0; f < 30; f++)
            {
                t.Update(0.1, you);
                for (int i = 0; i < life.carSlots.Length; i++)
                {
                    var o = t.cars[i];
                    if (o.hidden || o.taken) continue;
                    double d = Numbers.Hypot(o.x - stand.x, o.y - stand.y);
                    // Driving into them: close, moving, and with them ahead (not a car already past).
                    double along = (stand.x - o.x) * Math.Cos(o.heading) + (stand.y - o.y) * Math.Sin(o.heading);
                    if (d < 0.45 && o.speed > 0.2 && along > 0)
                        return $"{TownClock.Format(life.Now)}: car {life.CarInSlot(i)} drawn {d:0.00} tiles from a player at {o.speed:0.0} tiles a second " +
                               $"(they stood {ahead:0.0} ahead of car {who}, then {at}; now {Describe(life, life.cars[life.CarInSlot(i)])})";
                }
            }
            for (int f = 0; f < 5; f++) t.Update(0.1, null);
        }
        return tried == 0 ? "no car on the move to step out in front of" : null;
    }

    static string Describe(TownLife life, Car c)
    {
        int L = life.net.lanes.Count;
        string seg = c.seg < 0 ? "off the road" : c.seg < L ? $"lane {c.seg} (span {life.net.lanes[c.seg].Span:0.00})" : $"turn {c.seg - L} at node {life.net.movements[c.seg - L].node}";
        return $"car {c.id} {c.state} on {seg} front {life.Front(c):0.00} laggy {c.laggy} step {c.step}/{c.route?.Count}";
    }

    /// <summary>
    /// Given a player standing in the middle of the town's busiest junction through the morning rush, then
    /// no car on a turn there comes within a car's half-width of them, cars do wait for them, and once
    /// they've gone the waiting cars carry on through.
    /// </summary>
    static string JunctionPlayer(TownModel model, IsoLayout layout, TownZoning zoning)
    {
        var t = Rush(model, layout, zoning);
        var life = t.life;
        // The junction most cars went through in five minutes of it.
        for (int f = 0; f < 3000; f++) t.Update(0.1, null);
        if (life.stats.carWaits.Count == 0) return "no car went through a junction";
        int busiest = life.stats.carWaits.OrderByDescending(kv => kv.Value.count).ThenBy(kv => kv.Key).First().Key;
        var j = life.net.junctions[busiest];
        var you = new List<Vec2> { j.at };
        var waited = new HashSet<int>();
        int L = life.net.lanes.Count;
        // Where each car on a lane into it was a second ago: one that hasn't moved since is waiting for them too
        // (held short of the junction's mouth, where they stand in the way of the lane coming in; or queued behind
        // a car that was already on its turn when they stepped in, and has stopped short of them on it).
        var lastAt = new Dictionary<int, double>();
        for (int f = 0; f < 6000 && waited.Count < 3; f++)
        {
            t.Update(0.1, you);
            foreach (var car in life.cars)
            {
                if (car.seg < 0) continue;
                if (car.seg >= L && life.net.movements[car.seg - L].node == j.node)
                {
                    var m = life.net.movements[car.seg - L];
                    var nose = life.net.MovementPoint(m, life.Front(car)).at;
                    if (Geo.Dist(nose, j.at) < 0.25) return $"car {car.id} drove into the player at {TownClock.Format(life.Now)}";
                }
                if (car.seg < L && life.net.lanes[car.seg].toNode == j.node)
                {
                    if (car.state == CarState.Waiting) waited.Add(car.id);
                    else if (f % 10 == 0)
                    {
                        double front = life.Front(car);
                        if (lastAt.TryGetValue(car.id, out double was) && Math.Abs(front - was) < 0.01 && (car.state == CarState.Crossing || car.state == CarState.Queued)) waited.Add(car.id);
                        lastAt[car.id] = front;
                    }
                }
            }
        }
        if (waited.Count == 0) return "no car came to the junction to wait";
        for (int f = 0; f < 1200; f++) t.Update(0.1, null);
        var still = waited.Where(id => life.cars[id].seg >= 0 && life.cars[id].seg < L && life.net.lanes[life.cars[id].seg].toNode == j.node && life.cars[id].state == CarState.Waiting && life.cars[id].arrived < life.Now - 110_000).ToList();
        if (still.Count > 0) return $"cars {string.Join(", ", still)} still waiting two minutes after the player went";
        return null;
    }
}
