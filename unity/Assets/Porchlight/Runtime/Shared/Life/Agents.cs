// Who's in the town's life: its people, their cars, their trips, and the queues
// the cars wait in. Plain data; TownLife moves it. Pure.

using System.Collections.Generic;
using Porchlight.Core;

namespace Porchlight.Life
{
    public enum CarState
    {
        /// <summary>Parked (or off the map, or not yet made): not on the road.</summary>
        Parked,
        /// <summary>Pulling out of its spot: standing in the lane a moment.</summary>
        Unparking,
        /// <summary>Under way along a lane or round a turn.</summary>
        Crossing,
        /// <summary>Behind another car, or at the end of its lane waiting for one ahead to clear.</summary>
        Queued,
        /// <summary>At the front of its lane, asking the junction to let it go.</summary>
        Waiting,
        /// <summary>Pulling into a spot: standing in the lane a moment.</summary>
        Parking,
        /// <summary>A bus at a stop.</summary>
        AtStop,
    }

    public sealed class Car
    {
        public int id;
        /// <summary>Whose it is (-1: a bus).</summary>
        public int owner = -1;
        public double length;
        public CarState state;
        /// <summary>Where it is: a lane (id) or a movement (lanes + id). -1 off the road.</summary>
        public int seg = -1;
        /// <summary>
        /// Its interval: its front goes from s0 at t0 to s1 at t1 along `seg`'s queue (tiles from its start,
        /// ms), and stays at s1 after; never closer than FOLLOWING behind the car ahead.
        /// </summary>
        public long t0, t1;
        public double s0, s1;
        /// <summary>
        /// How it drives its interval, as a driver does: from speed `va` at `a1` (tiles/s², either sign) for `p1`
        /// seconds, steady at `vp` for `p2`, then at `a3` for `p3` down (or up) to `vb`, the speed it means to be
        /// doing at s1 (Cars.SetInterval).
        /// </summary>
        public double va, vp, vb, a1, a3, p1, p2, p3;
        /// <summary>Where it means to stop short of s1, behind a car ahead that's stopped or stopping (NaN: none).</summary>
        public double stopAt = double.NaN;
        /// <summary>The segment its back is still in (it's that one's laggy head), or -1.</summary>
        public int laggy = -1;
        /// <summary>The way it's going (segments), and which of them it's on.</summary>
        public List<int> route;
        public int step;
        /// <summary>Where on the route's last segment it stops (park, or a bus stop).</summary>
        public double goal;
        /// <summary>The spot it's parked in or heading for (-1: none, or parked without one).</summary>
        public int spot = -1;
        /// <summary>The building it's parked at (infinite parking, or no spot left).</summary>
        public int parkedBy = -1;
        /// <summary>Off the map (at a border).</summary>
        public bool offMap;
        /// <summary>When it last got to the front of a junction and began waiting, and served its stop sign there.</summary>
        public long arrived = -1;
        public bool stopped;
        /// <summary>When it came onto the lane it's on, and how far along (for the lane's measured hold-up).</summary>
        public long laneSince;
        public double laneFrom;
        /// <summary>When its way was last changed for the traffic, and how many times this drive.</summary>
        public long rerouted = -1;
        public int reroutes;
        /// <summary>Where it was when it last moved (segment, front), and when: a car that hasn't in a long while gives up.</summary>
        public int seenSeg = -1;
        public double seenFront;
        public long movedAt;
        /// <summary>A van on its rounds: which one (-1: not a van), and when it set off for the call it's making.</summary>
        public int van = -1;
        public long callFrom = -1;
        /// <summary>A service vehicle (TownLife.services: a bin lorry, an ambulance, a fire engine), or -1.</summary>
        public int service = -1;
        /// <summary>Its siren's going (an emergency vehicle on its way to a call): it goes on red, and drivers make way.</summary>
        public bool siren;
        /// <summary>Pulled over for a siren: creeping along by the kerb till it's past.</summary>
        public bool pulledOver;
        /// <summary>When it last pulled over or pulled out again (drawn easing towards the kerb or back over PULL_MS).</summary>
        public long pullAt = long.MinValue / 2;
        /// <summary>When it last went on red (for a siren, or clearing the way for one), or -1.</summary>
        public long wentOnRed = -1;
        /// <summary>A bus: which one (-1: not a bus), and the stop it's at or last left.</summary>
        public int bus = -1;
        public int busStop;
        /// <summary>The wire slot it's drawn in, or -1.</summary>
        public int slot = -1;
        /// <summary>How many times it's hunted for a spot this trip.</summary>
        public int hunts;
        /// <summary>Hunted and found nowhere: it stops by the door anyway.</summary>
        public bool huntFailed;
        /// <summary>Where it's driving to.</summary>
        public Place dest;
        /// <summary>The spot it's holding to park in (-1: none yet), and whether it was held as it set off (not hunted for).</summary>
        public int target = -1;
        public bool reserved;
        /// <summary>Its way's free-flow time as the router reckoned it (s), and when it was worked out.</summary>
        public double routeS;
        public long routed = -1;
        /// <summary>Where it gets onto the road: a lane, and how far along its queue.</summary>
        public int startLane;
        public double startD;
    }

    /// <summary>A queue: a lane's or a movement's, cars in order front first.</summary>
    public sealed class Queue
    {
        public readonly List<int> cars = new List<int>();
        /// <summary>A car gone on whose back is still in this one.</summary>
        public int laggy = -1;
        public double length;
        /// <summary>Lanes: the cars on it or let into it (each's length and the gap behind).</summary>
        public double committed;
        /// <summary>Lanes: the last car let in (whatever fills the lane, for blocked-by).</summary>
        public int lastIn = -1;
        /// <summary>Lanes: a player standing in it, how far along, and the first car behind them.</summary>
        public double blockAt = double.NaN;
        public int blockFirst = -1;
        /// <summary>Lanes: where that first car's front stops for them: short of them if it can brake to it, never behind where it is.</summary>
        public double blockStop = double.NaN;
        /// <summary>Lanes: a car being let in from the kerb (-1: none), the car held back for it, and how far along that one stops.</summary>
        public int letting = -1;
        public int held = -1;
        public double heldAt;
    }

    public enum Mode { Walk, Drive, Bus }

    public enum Purpose { Home, Work, School, Shop, Lunch, Visit, Leave, Through, Market, DropOff, PickUp }

    /// <summary>
    /// Which days a worker works (Calendar.cs, docs/town-calendar.md): Monday to Friday; the shops' week (five days of
    /// the seven, a weekday and most Sundays off); shifts (five on, two off, a week of earlies then a week of lates);
    /// or the school's days (its staff).
    /// </summary>
    public enum Roster { Office, Shop, Shifts, School }

    public enum PersonKind
    {
        /// <summary>Works (in town or out of it) and lives in town.</summary>
        Worker,
        /// <summary>Goes to school.</summary>
        Student,
        /// <summary>At home in the day: errands and strolls.</summary>
        AtHome,
        /// <summary>Lives out of town, works here.</summary>
        CommuterIn,
        /// <summary>Drives through, gate to gate.</summary>
        Through,
        /// <summary>Lives out of town, keeps a stall at the market: in on market day to set up, home after.</summary>
        Stallholder,
    }

    /// <summary>Somewhere a trip starts or ends: a building, or (border) a gate.</summary>
    public struct Place
    {
        public int id;
        public bool border;

        public static Place Building(int b) => new Place { id = b };
        public static Place Gate(int g) => new Place { id = g, border = true };
        public bool Same(Place o) => id == o.id && border == o.border;
        public override string ToString() => border ? $"gate {id}" : $"building {id}";
    }

    public struct Planned
    {
        /// <summary>When to set off: ms since the day's midnight.</summary>
        public long depart;
        public Place to;
        public Purpose purpose;
        /// <summary>Who they go with (a parent taking a child to school), plus one: 0, alone.</summary>
        public int with;
    }

    public enum Where { Inside, OnTrip, OffMap }

    /// <summary>A leg: on foot, driving, waiting for the bus, on the bus, or a passenger in someone else's car.</summary>
    public enum LegKind { Walk, Drive, Wait, Ride, Passenger }

    public sealed class Leg
    {
        public LegKind kind;
        /// <summary>Walk: from, to.</summary>
        public Vec2 from, to;
        /// <summary>Drive: where it parks (a building) or leaves (a gate).</summary>
        public Place dest;
        /// <summary>Wait and Ride: the bus route, the stop to get on at and off at.</summary>
        public int route = -1, board, alight;
    }

    /// <summary>
    /// How a trip's way of getting there was chosen (People.StartTrip), the figures it went by: kept with the
    /// trip so Manage's focus view can say why, long after (the bus's wait was the timetable's at the time).
    /// </summary>
    public struct ModeWhy
    {
        /// <summary>From or to a gate (that needs a car), and whether one was waiting there for them because they had none.</summary>
        public bool gate, given;
        public bool hasCar;
        /// <summary>Their car's parked where they're setting off from (not off the map, not elsewhere).</summary>
        public bool carHere;
        /// <summary>Door to door, as the crow flies (tiles).</summary>
        public double dist;
        /// <summary>The bus was weighed against walking (else: no route, or the same stop both ends).</summary>
        public bool busWeighed;
        /// <summary>Walking all the way, and by bus: to the stop, the wait by the timetable, the ride, from the stop (s).</summary>
        public double walkS, toStopS, waitS, rideS, fromStopS;
        public int board, alight;
    }

    public sealed class Trip
    {
        public int id;
        public Place from, to;
        public Mode mode;
        public Purpose purpose;
        public readonly List<Leg> legs = new List<Leg>();
        public int leg = -1;
        public long started;
        public ModeWhy why;
        /// <summary>Who's riding along (children being taken to school), or null.</summary>
        public List<int> riders;
        /// <summary>A passenger's trip: whose car they're in.</summary>
        public int driver = -1;
        /// <summary>When it ended (-1: under way), whether it was made, and if not why.</summary>
        public long ended = -1;
        public bool made;
        public string failed;
    }

    public sealed class Person
    {
        public int id;
        public PersonKind kind;
        public Place home, work;
        /// <summary>Their household (TownLife.households; -1: none, from beyond the gates), who they are in it, and how old.</summary>
        public int household = -1;
        public Role role;
        public int age;
        public bool hasCar;
        /// <summary>Their car (Car id), -1 if none.</summary>
        public int car = -1;
        public Where where;
        /// <summary>Where they are when not on a trip.</summary>
        public Place at;
        public readonly List<Planned> plan = new List<Planned>();
        /// <summary>The lanes they took last time on each trip they drive, by where from and where to (Congestion.HabitualRoute).</summary>
        public Dictionary<long, HashSet<int>> habits;
        public int next;
        /// <summary>The day `plan` is for (TownClock.DayOf).</summary>
        public long day;
        public Trip trip;
        /// <summary>A trip came due while they were still on the last one.</summary>
        public bool delayed;
        /// <summary>Walking pace (tiles a second).</summary>
        public double pace;
        /// <summary>Which days they work (a worker), and their own place in it (which days off, which week of shifts).</summary>
        public Roster roster;
        public int rosterOffset;
        /// <summary>Waiting at home (or school) for who's taking them (-1: nobody), to go where, and why.</summary>
        public int waitingFor = -1;
        public Place rideTo;
        public Purpose ridePurpose;
        /// <summary>Who's waiting for them to set off, to go along (children), or null.</summary>
        public List<int> waiters;
        /// <summary>At the market since when (browsing, or at their stall), or -1; a stallholder's stall.</summary>
        public long browseSince = -1;
        public int stall = -1;

        // On foot ------------------------------------------------------------------------------
        public List<WalkPiece> walk;
        public int piece;
        public long w0;
        /// <summary>This piece's speed (crowding slows it).</summary>
        public double wSpeed;
        /// <summary>Waiting at a junction to cross since when (-1: not waiting).</summary>
        public long crossWait = -1;
        /// <summary>Crossing at a junction (its node), or -1.</summary>
        public int crossing = -1;
        /// <summary>Standing at a bus stop (its point), waiting.</summary>
        public Vec2 standing;
        /// <summary>The wire slot they're drawn in, or -1.</summary>
        public int slot = -1;
        /// <summary>Their last few trips, newest last (Manage's focus view); made when the first ends.</summary>
        public List<Trip> recent;
    }
}
