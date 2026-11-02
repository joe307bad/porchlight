# title: Moving day
# summary: Three neighbours need boxes run across town. Find each one, park where they ask, and drive their boxes to the drop-off.

// Everything this mission is lives in this file: who's in it, where they
// live, what they say, how the boxes get from one house to another. The game
// only knows the building blocks below (see README.md in this folder).
EXTERNAL objective(text)
EXTERNAL hint(when, role, text)
EXTERNAL clear_hints()
EXTERNAL setting(name, value)
EXTERNAL place(role, kind, near, min, max, parking)
EXTERNAL spawn(role, name, look)
EXTERNAL walk(role, to)
EXTERNAL pause(role, seconds)
EXTERNAL hold(role, model, size)
EXTERNAL put_in_car(role)
EXTERNAL take_from_car(role)
EXTERNAL put_down(role)
EXTERNAL leave(role)
EXTERNAL mark_spot(role)
EXTERNAL clear_spot(role)
EXTERNAL guide(role, to, label)

// The boxes: the model the game draws, and how big (tiles across: about 40 cm).
CONST BOX = "furniture/cardboardBoxClosed"
CONST BOX_SIZE = 0.1
// How long someone is indoors fetching a box, or putting one down (seconds).
CONST INDOORS = 1.1

VAR run = 0
VAR runs = 3
VAR boxes = 2
// How many boxes are on the car right now.
VAR in_car = 0
VAR sender = ""
VAR sender_street = ""
VAR recipient = ""
VAR drop_street = ""

// How the town plays this mission. These are the game's own defaults, spelled
// out so they're here to tune.
~ setting("talk_reach", 1.1)
~ setting("talk_reach_car", 1.9)
~ setting("park_reach", 0.45)
~ setting("park_speed", 0.12)
~ setting("park_angle", 0.45)
~ setting("car_reach", 0.8)
~ setting("walk_speed", 1.0)
-> next_run

=== next_run ===
~ run++
~ boxes = RANDOM(2, 3)
~ in_car = 0
~ sender_street = place("sender", "house", "", 6, 40, true)
~ sender = spawn("sender", pick_name(""), "")
~ drop_street = place("drop", "house", "sender", 15, 60, true)
~ guide("sender", "person", sender)
~ objective("Find {sender} on {sender_street}")
{ run == 1:
    Someone on {sender_street} needs a hand with a delivery.
- else:
    Another neighbour is waving you down on {sender_street}.
}
-> meet

=== meet ===
+ [@talk sender]
    sender: {run == 1:Oh, hello! You've got a car, haven't you?|Hi! Are you the one doing deliveries?}
    sender: I've got {boxes} boxes for my sister on {drop_street}, and they won't fit on the bus.
    sender: Could you pull up right outside my house? I've marked the spot.
    -> ask

= ask
+ [Sure, I'll park there.]
    you: Sure, I'll park right there.
    sender: Brilliant. I'll bring them out to you.
    -> park
+ [Not right now.]
    you: Sorry, not right now.
    sender: No worries, I'll be here when you're ready.
    ~ objective("Talk to {sender} on {sender_street} when you're ready")
    -> meet

=== park ===
~ mark_spot("sender")
~ guide("sender", "spot", "{sender}'s parking spot")
~ parking_hints("sender")
~ objective("Park in the marked spot outside {sender}'s house")
+ [@parked sender] -> loading

=== loading ===
~ clear_hints()
~ guide("", "", "")
~ carry_out("sender", boxes)
~ objective("Wait while {sender} loads the boxes into your car")
+ [@idle sender]
    sender: That's the lot. Mind the corners!
    ~ leave("sender")
    ~ clear_spot("sender")
    -> deliver

=== deliver ===
~ recipient = spawn("drop", pick_name(sender), "")
~ mark_spot("drop")
~ guide("drop", "spot", "{recipient}'s parking spot")
~ parking_hints("drop")
~ objective("Take the boxes to {recipient} on {drop_street}")
+ [@parked drop] -> unloading

=== unloading ===
~ clear_hints()
~ guide("", "", "")
~ carry_in("drop", in_car)
~ objective("{recipient} is unloading the boxes")
+ [@idle drop]
    drop: These are from {sender}? Oh, thank you!
    ~ leave("drop")
    ~ clear_spot("drop")
    -> done

=== done ===
{ run < runs:
    Delivery {run} of {runs} done.
    -> next_run
}
~ objective("")
All {runs} deliveries done. The neighbourhood owes you one.
-> END

// Helpers: the pieces of the mission that happen more than once.

// While you're on your way to a role's spot: what to do if you've no car, and
// how to park once you're there.
=== function parking_hints(role) ===
~ hint("no_car", "", "You'll need a car: click the Car box by the minimap")
~ hint("near_driving", role, "Pull into the marked spot and stop, lined up with the kerb")

// The role's person brings n boxes out to your car, one at a time: indoors,
// back out with a box, to your car's kerb side (they wait at the kerb while
// your car isn't in their spot), onto the roof. Then back to the kerb.
=== function carry_out(role, n) ===
{ n > 0:
    ~ walk(role, "inside")
    ~ pause(role, INDOORS)
    ~ hold(role, BOX, BOX_SIZE)
    ~ walk(role, "car")
    ~ put_in_car(role)
    ~ in_car++
    ~ carry_out(role, n - 1)
- else:
    ~ walk(role, "kerb")
}

// The role's person takes n boxes off your car and into their house, one at
// a time. Then back to the kerb.
=== function carry_in(role, n) ===
{ n > 0:
    ~ walk(role, "car")
    ~ take_from_car(role)
    ~ in_car--
    ~ walk(role, "inside")
    ~ pause(role, INDOORS)
    ~ put_down(role)
    ~ carry_in(role, n - 1)
- else:
    ~ walk(role, "kerb")
}

// A neighbour's name, any but avoid.
=== function pick_name(avoid) ===
~ temp name = random_name()
{ name == avoid:
    ~ return pick_name(avoid)
}
~ return name

=== function random_name() ===
{ RANDOM(1, 32):
- 1: ~ return "Ada"
- 2: ~ return "Bea"
- 3: ~ return "Cal"
- 4: ~ return "Dot"
- 5: ~ return "Eli"
- 6: ~ return "Fay"
- 7: ~ return "Gus"
- 8: ~ return "Hal"
- 9: ~ return "Ivy"
- 10: ~ return "Jo"
- 11: ~ return "Kit"
- 12: ~ return "Lou"
- 13: ~ return "Mae"
- 14: ~ return "Ned"
- 15: ~ return "Oona"
- 16: ~ return "Pip"
- 17: ~ return "Ray"
- 18: ~ return "Sam"
- 19: ~ return "Tess"
- 20: ~ return "Vi"
- 21: ~ return "Wes"
- 22: ~ return "Zara"
- 23: ~ return "Ines"
- 24: ~ return "Omar"
- 25: ~ return "Priya"
- 26: ~ return "Kenji"
- 27: ~ return "Lena"
- 28: ~ return "Mateo"
- 29: ~ return "Nia"
- 30: ~ return "Rosa"
- 31: ~ return "Theo"
- else: ~ return "Yusuf"
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
=== function spawn(role, name, look) ===
~ return name
=== function walk(role, to) ===
~ return
=== function pause(role, seconds) ===
~ return
=== function hold(role, model, size) ===
~ return
=== function put_in_car(role) ===
~ return
=== function take_from_car(role) ===
~ return
=== function put_down(role) ===
~ return
=== function leave(role) ===
~ return
=== function mark_spot(role) ===
~ return
=== function clear_spot(role) ===
~ return
=== function guide(role, to, label) ===
~ return
