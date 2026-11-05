# title: Fire on the street
# summary: You're a firefighter! Drive the fire engine to three burning houses, park out front, and climb up to the water turret on the roof to put the fires out.

// Everything this mission is lives in this file: that you're a firefighter
// at the wheel of the fire engine, which houses burn and how badly, your crew,
// and what you earn for saving them. The game only knows the building blocks
// below (see README.md in this folder).
EXTERNAL objective(text)
EXTERNAL hint(when, role, text)
EXTERNAL clear_hints()
EXTERNAL setting(name, value)
EXTERNAL place(role, kind, near, min, max, parking)
EXTERNAL mark_spot(role)
EXTERNAL clear_spot(role)
EXTERNAL guide(role, to, label)
EXTERNAL look(model)
EXTERNAL your_car(model)
EXTERNAL fire(role, windows)
EXTERNAL turret(role, crew, model)
EXTERNAL unlock(what)

// You, your engine and your crew: the models the game draws.
CONST FIREFIGHTER = "people/firefighter"
CONST ENGINE = "cars/firetruck"
// How many houses to save, how many windows burn in each, and how many of the
// crew get out with hoses to help.
CONST HOUSES = 3
CONST WINDOWS = 3
CONST CREW = 3
// How far away (tiles) each fire is, from wherever you are when the call comes.
CONST NEAR = 20
CONST FAR = 35

VAR saved = 0
VAR doused = 0
// How many windows (and the door) are burning at this house: some have fewer windows
// that face the street.
VAR burning = 0
VAR street = ""

// How the town plays this mission. A window needs water on it for two and a
// half seconds to go out; the turret throws water a good way and turns
// quickly. Parking is a little kinder than usual: a fire engine is a big thing.
~ setting("douse_time", 2.5)
~ setting("water_speed", 5.0)
~ setting("aim_speed", 1.3)
~ setting("park_reach", 0.6)
~ setting("park_angle", 0.55)
~ look(FIREFIGHTER)
~ your_car(ENGINE)
You're a firefighter today! You're sitting in the big red fire engine.
-> call

=== call ===
~ doused = 0
~ street = place("fire", "house", "", NEAR, FAR, true)
~ burning = fire("fire", WINDOWS)
~ mark_spot("fire")
~ guide("fire", "spot", "the fire on {street}")
~ objective("Drive to the fire on {street}")
~ clear_hints()
~ hint("near_driving", "fire", "Pull into the orange spot in front of the house and stop")
{ saved == 0:
    Ring ring! A house is on fire on {street}. Look for the smoke, and follow the blue dots!
- else:
    Ring ring! Another house is on fire, this time on {street}. Off you go!
}
-> drive

= drive
+ [@parked fire] -> arrive

=== arrive ===
~ guide("", "", "")
~ clear_hints()
~ turret("fire", CREW, FIREFIGHTER)
~ hint("always", "", "Press A (or Enter) to climb up to the water turret on the roof")
~ objective("Put out the fire: 0 of {burning}")
You're here! Your crew jump out and grab their hoses.
-> fight

= fight
+ [@out fire] -> saved_it
+ [@doused fire]
    ~ doused++
    ~ objective("Put out the fire: {doused} of {burning}")
    -> fight

=== saved_it ===
~ saved++
~ objective("")
~ clear_hints()
~ turret("", 0, "")
~ clear_spot("fire")
The fire is out! Everyone is safe.
{ saved < HOUSES:
    That's {saved == 1:one house|{saved} houses} saved. Back in the engine!
    -> call
}
~ fire("fire", 0)
~ unlock("firefighter")
You saved all three houses! The whole town is cheering.
~ your_car("")
~ look("")
Now you can be a firefighter whenever you like, and drive the fire engine too. Find them in the character box and the car box!
-> END

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
~ return "Oak Street"
=== function mark_spot(role) ===
~ return
=== function clear_spot(role) ===
~ return
=== function guide(role, to, label) ===
~ return
=== function look(model) ===
~ return
=== function your_car(model) ===
~ return
=== function fire(role, windows) ===
~ return windows + 1
=== function turret(role, crew, model) ===
~ return
=== function unlock(what) ===
~ return
