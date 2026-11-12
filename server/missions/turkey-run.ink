# title: Turkey run
# summary: The pickup's piled high with frozen turkeys for the neighbours. Pull up outside each house and your helper hops out, takes a turkey off the pile, carries it in and comes back. Five turkeys and every table in town has Thanksgiving dinner on it.
# unlocks: feast

// Everything this mission is lives in this file (see README.md in this
// folder): the pickup, its load of frozen turkeys, and what your helper does
// at each house, out of the game's building blocks.
EXTERNAL objective(text)
EXTERNAL hint(when, role, text)
EXTERNAL clear_hints()
EXTERNAL setting(name, value)
EXTERNAL place(role, kind, near, min, max, parking)
EXTERNAL your_car(model)
EXTERNAL load_back(model, count, size)
EXTERNAL mark_spot(role)
EXTERNAL clear_spot(role)
EXTERNAL guide(role, to, label)
EXTERNAL get_out(role, name, look)
EXTERNAL walk(role, to)
EXTERNAL take_from_back(role)
EXTERNAL pause(role, seconds)
EXTERNAL put_down(role)
EXTERNAL get_in(role)
EXTERNAL unlock(what)

// The pickup, and what's piled in its bed: a big heap of frozen turkeys
// (tiles long: about 60 cm, bag and all; three by two fill a layer of the
// bed). Far more than the round needs: the church drive's whole load, heaped
// up over the sides, and still a heap at the last house.
CONST TRUCK = "cars/truck"
CONST TURKEY = "props/frozen-turkey"
CONST TURKEY_SIZE = 0.15
CONST LOADED = 20
CONST HELPER_LOOK = "people/character-5-2-3"
// How many houses get a turkey.
CONST STOPS = 5

VAR stop = 0
VAR helper = ""
VAR street = ""

// How the town plays this mission: the game's own defaults, spelled out so
// they're here to tune.
~ setting("park_reach", 0.45)
~ setting("park_speed", 0.12)
~ setting("park_angle", 0.45)
~ setting("car_reach", 0.8)
~ setting("walk_speed", 1.0)
~ your_car(TRUCK)
~ load_back(TURKEY, LOADED, TURKEY_SIZE)
~ helper = helper_name()
It's the week of Thanksgiving, and the pickup's bed is piled high with frozen turkeys from the church drive. {helper} is riding with you.
{helper}: Five houses on the list. You drive, I'll carry. Mind the bumps, these things are like bricks.
-> next_stop

=== next_stop ===
~ stop++
// The next house on the list, a few doors to a couple of streets on from where you are.
~ street = place("drop", "house", "", 4, 18, true)
~ mark_spot("drop")
~ guide("drop", "spot", "the house on {street}")
~ hint("no_car", "", "You'll need the pickup: click the Car box by the minimap")
~ hint("near_driving", "drop", "Pull into the marked spot outside the house and stop, lined up with the kerb")
~ objective("Take a turkey to the house on {street} ({stop} of {STOPS})")
+ [@parked drop] -> deliver

=== deliver ===
~ clear_hints()
~ guide("", "", "")
// Out of the cab, round to the tailgate, a turkey off the top of the pile, up
// the path and in at the door; a moment inside to hand it over; back out to
// the pickup and in.
~ get_out("drop", helper, HELPER_LOOK)
~ walk("drop", "back")
~ take_from_back("drop")
~ walk("drop", "inside")
~ pause("drop", 2.0)
~ put_down("drop")
~ walk("drop", "door")
~ get_in("drop")
~ objective("Wait while {helper} carries the turkey in")
+ [@idle drop] -> done

=== done ===
~ clear_spot("drop")
{ stop < STOPS:
    drop: {~They were thrilled.|"Twelve pounds of joy," they said.|They want us back for pie.|They had the oven going already.} {stop} down, {STOPS - stop} to go.
    -> next_stop
}
~ objective("")
~ your_car("")
drop: That's the list! Five turkeys, five happy kitchens.
All {STOPS} turkeys delivered. Look in through any window: the whole town's sitting down to Thanksgiving dinner.
~ unlock("feast")
-> END

=== function helper_name() ===
{ RANDOM(1, 8):
- 1: ~ return "Bea"
- 2: ~ return "Cal"
- 3: ~ return "Dot"
- 4: ~ return "Hal"
- 5: ~ return "June"
- 6: ~ return "Moe"
- 7: ~ return "Pearl"
- else: ~ return "Sol"
}

// Stand-ins so the story plays in Inky (the game has the real ones).
=== function objective(text) ===
~ return
=== function hint(when, role, text) ===
~ return
=== function clear_hints() ===
~ return
=== function setting(name, value) ===
~ return
=== function place(role, kind, near, min, max, parking) ===
~ return "Elm Street"
=== function your_car(model) ===
~ return
=== function load_back(model, count, size) ===
~ return
=== function mark_spot(role) ===
~ return
=== function clear_spot(role) ===
~ return
=== function guide(role, to, label) ===
~ return
=== function get_out(role, name, look) ===
~ return name
=== function walk(role, to) ===
~ return
=== function take_from_back(role) ===
~ return
=== function pause(role, seconds) ===
~ return
=== function put_down(role) ===
~ return
=== function get_in(role) ===
~ return
=== function unlock(what) ===
~ return
