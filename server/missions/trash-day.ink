# title: Trash day
# summary: You're driving the garbage truck. Pull up by each wheelie bin on the round, and your crew hops out, wheels it to the back, tips it in, puts it back and hops back on. Five bins and the round's done.

// Everything this mission is lives in this file (see README.md in this
// folder): the truck, the round, and what the crew does at each bin, out of
// the game's building blocks.
EXTERNAL objective(text)
EXTERNAL hint(when, role, text)
EXTERNAL clear_hints()
EXTERNAL setting(name, value)
EXTERNAL place(role, kind, near, min, max, parking)
EXTERNAL your_car(model)
EXTERNAL mark_spot(role)
EXTERNAL clear_spot(role)
EXTERNAL guide(role, to, label)
EXTERNAL bin(role, model, size)
EXTERNAL get_out(role, name, look)
EXTERNAL walk(role, to)
EXTERNAL take_bin(role)
EXTERNAL dump(role)
EXTERNAL return_bin(role)
EXTERNAL get_in(role)

// The truck, the crew's look, and the bins (tiles across: about 80 cm deep).
CONST TRUCK = "cars/garbage-truck"
CONST CREW_LOOK = "people/character-4-1-1"
CONST BIN_SIZE = 0.2
// How many bins make a round.
CONST STOPS = 5

VAR stop = 0
VAR crew = ""
VAR street = ""
VAR colour = ""

// How the town plays this mission: the game's own defaults, spelled out so
// they're here to tune.
~ setting("park_reach", 0.45)
~ setting("park_speed", 0.12)
~ setting("park_angle", 0.45)
~ setting("car_reach", 0.8)
~ setting("walk_speed", 1.0)
~ your_car(TRUCK)
~ crew = crew_name()
It's trash day, and you're at the wheel of the garbage truck. {crew} is riding with you.
-> next_stop

=== next_stop ===
~ stop++
// The next house on the round, a few doors to a couple of streets on from where you are.
~ street = place("stop", "house", "", 4, 18, true)
{ RANDOM(1, 2):
- 1: ~ colour = "trash"
- else: ~ colour = "recycle"
}
~ bin("stop", "props/wheelie-bin-{colour}", BIN_SIZE)
~ mark_spot("stop")
~ guide("stop", "spot", "the {bin_name()} on {street}")
~ hint("no_car", "", "You'll need the truck: click the Car box by the minimap")
~ hint("near_driving", "stop", "Pull into the marked spot by the bin and stop, lined up with the kerb")
~ objective("Pull up by the {bin_name()} on {street} ({stop} of {STOPS})")
+ [@parked stop] -> collect

=== collect ===
~ clear_hints()
~ guide("", "", "")
// Out of the cab, over to the bin, wheel it round the back, tip it in, wheel it
// back, and back in the cab.
~ get_out("stop", crew, CREW_LOOK)
~ walk("stop", "bin")
~ take_bin("stop")
~ walk("stop", "back")
~ dump("stop")
~ walk("stop", "bin")
~ return_bin("stop")
~ get_in("stop")
~ objective("Wait while {crew} empties the {bin_name()}")
+ [@idle stop] -> done

=== done ===
~ clear_spot("stop")
{ stop < STOPS:
    stop: {~That's one more.|Lovely, next!|Done. Onwards!|In it goes.} {stop} down, {STOPS - stop} to go.
    -> next_stop
}
~ objective("")
~ your_car("")
stop: That's the round! Five bins, not a scrap dropped.
All {STOPS} bins emptied. The street's clean for another week.
-> END

// The bin at this stop, by its colour.
=== function bin_name() ===
{ colour == "recycle":
    ~ return "blue recycling bin"
- else:
    ~ return "green trash bin"
}

=== function crew_name() ===
{ RANDOM(1, 8):
- 1: ~ return "Dot"
- 2: ~ return "Gus"
- 3: ~ return "Ines"
- 4: ~ return "Kenji"
- 5: ~ return "Mae"
- 6: ~ return "Omar"
- 7: ~ return "Rosa"
- else: ~ return "Wes"
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
=== function mark_spot(role) ===
~ return
=== function clear_spot(role) ===
~ return
=== function guide(role, to, label) ===
~ return
=== function bin(role, model, size) ===
~ return
=== function get_out(role, name, look) ===
~ return name
=== function walk(role, to) ===
~ return
=== function take_bin(role) ===
~ return
=== function dump(role) ===
~ return
=== function return_bin(role) ===
~ return
=== function get_in(role) ===
~ return
