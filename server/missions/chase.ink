# title: Penguin on the run
# summary: You're a penguin, and you want to see the town! Waddle out of the zoo gate, then stay away from the police for one whole minute. You can hop in a car or a plane to get away.
# unlocks: animals

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
You're a penguin who lives at the zoo.
you: Noot!
You really want to see the town. And look: someone left the gate open!
-> escape

=== escape ===
~ clear_hints()
~ hint("always", "", "Waddle to the gate · hold Shift (or RT) to run")
~ dot("zoo", "door")
~ objective("Get out of the zoo")
+ [@outside zoo] -> on_the_run

=== on_the_run ===
~ dot("", "")
~ clear_hints()
~ chase("police", "Officer Pike", CARS, POLICE_CAR, OFFICER)
~ countdown(FREE_FOR, "Free for", "outside", "zoo")
~ objective("Keep away from the police")
~ hint("always", "", "Don't let a police officer stand next to you · hop in a car or a plane to zoom away")
{ caught == 0:
    Uh oh! Someone saw a penguin in the street. Here come the police!
- else:
    You're out again! Here come the police.
}
-> loose

= loose
+ [@caught police] -> nabbed
+ [@countdown] -> free

=== nabbed ===
~ caught++
~ clear_hints()
police: Got you, little penguin! Back to the zoo you go.
~ leave("police")
~ countdown(0, "", "", "")
~ put_you("zoo", "inside")
{ caught == 1:
    You're back by the pool. But the gate is still open!
- else:
    Back by the pool again. That gate is still open!
}
-> escape

=== free ===
~ countdown(0, "", "", "")
~ objective("")
~ clear_hints()
police: Where did that penguin go? Okay, everyone, let's go home.
~ leave("police")
you: Noot noot!
~ unlock("animals")
You stayed free for a whole minute<>
{ caught > 0:
    <>, even though you got caught {caught == 1:once|{caught} times}<>
}
<>. You did it!
~ look("")
Now you can play as any animal from the zoo! Pick one in the character box.
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
