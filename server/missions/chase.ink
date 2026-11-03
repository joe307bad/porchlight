# title: Penguin on the run
# summary: You're a penguin who's had enough of the zoo. Waddle out of the gate, then keep clear of the police for a whole minute. Cars and planes are fair game.

// Everything this mission is lives in this file: that you're a penguin, the
// zoo you start in, the police who come after you, how long you must stay
// free. The game only knows the building blocks below (see README.md in this
// folder).
EXTERNAL objective(text)
EXTERNAL hint(when, role, text)
EXTERNAL clear_hints()
EXTERNAL setting(name, value)
EXTERNAL place(role, kind, near, min, max, parking)
EXTERNAL dot(role, to)
EXTERNAL look(model)
EXTERNAL put_you(role, at)
EXTERNAL chase(role, name, count, car, model)
EXTERNAL leave(role)
EXTERNAL countdown(seconds, label, when, role)
EXTERNAL standby(role, count, car, model)
EXTERNAL unlock(what)

// You, and who comes after you: the models the game draws.
CONST PENGUIN = "animals/animal-penguin"
CONST POLICE_CAR = "cars/police"
CONST OFFICER = "people/policeman-a-4-1"
// How many police cars wait all over town from the start (got ready now, so
// there's no pause when they set off), how many more come from nearby, and how
// long (seconds) you must stay out of their hands.
CONST WAITING = 14
CONST CARS = 3
CONST FREE_FOR = 60

VAR caught = 0

// How the town plays this mission. A penguin is slower than a person on foot
// (a run is about as fast as a police car's crawl); the police drive flat out
// after you, boost and all, faster still once you're driving, and cut across
// anywhere a car can go. A car pulls up a little way off, and its officer jumps
// out and runs at you: quicker than you waddle, only just slower than you run,
// and giving up once
// you're well away. One that stands by you for three seconds has you (in a
// car, that's your chance to drive off).
~ setting("your_pace", 0.62)
~ setting("chase_speed", 5.6)
~ setting("chase_speed_car", 8.0)
~ setting("pull_up", 1.6)
~ setting("officer_speed", 3.5)
~ setting("officer_reach", 5.0)
// Far off, each car makes for its own side of a ring this wide round you
// (tiles), so they close in from all round.
~ setting("ring", 4.5)
~ setting("catch_reach", 0.45)
~ setting("catch_time", 3.0)
~ place("zoo", "zoo", "", 0, 1000, false)
~ standby("police", WAITING, POLICE_CAR, OFFICER)
~ look(PENGUIN)
~ put_you("zoo", "inside")
You're a penguin, and you've had quite enough of the zoo.
you: Noot.
Someone's left the gate open...
-> escape

=== escape ===
~ clear_hints()
~ hint("always", "", "Waddle out through the gate · hold Shift (or RT) to run")
~ dot("zoo", "door")
~ objective("Get out of the zoo")
+ [@outside zoo] -> on_the_run

=== on_the_run ===
~ dot("", "")
~ clear_hints()
~ chase("police", "Officer Pike", CARS, POLICE_CAR, OFFICER)
~ countdown(FREE_FOR, "Free for", "outside", "zoo")
~ objective("Keep away from the police")
~ hint("always", "", "Don't let an officer catch up with you · any car or plane will do")
{ caught == 0:
    Sirens! Someone's seen a penguin in the street.
- else:
    Out again! The sirens start up.
}
-> loose

= loose
+ [@caught police] -> nabbed
+ [@countdown] -> free

=== nabbed ===
~ caught++
~ clear_hints()
police: Got you! Back to the zoo with you.
~ leave("police")
~ countdown(0, "", "", "")
~ put_you("zoo", "inside")
{ caught == 1:
    You're back by the pool. But nobody's shut the gate.
- else:
    Back by the pool again. The gate's still open.
}
-> escape

=== free ===
~ countdown(0, "", "", "")
~ objective("")
~ clear_hints()
police: We've lost it. Call it off, everyone.
~ leave("police")
you: Noot noot!
~ unlock("animals")
A whole minute on the loose<>
{ caught > 0:
    <>, and caught {caught} {caught == 1:time|times} along the way<>
}
<>. You're free!
~ look("")
You can be any of the zoo's animals now: choose one from the character box.
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
~ return "Zoo Lane"
=== function dot(role, to) ===
~ return
=== function look(model) ===
~ return
=== function put_you(role, at) ===
~ return
=== function chase(role, name, count, car, model) ===
~ return name
=== function leave(role) ===
~ return
=== function countdown(seconds, label, when, role) ===
~ return
=== function standby(role, count, car, model) ===
~ return
=== function unlock(what) ===
~ return
