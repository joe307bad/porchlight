# title: Moving day
# summary: Three neighbours need boxes run across town. Find each one, park where they ask, and drive their boxes to the drop-off.

// The game's side of the story: see README.md in this folder.
EXTERNAL objective(text)
EXTERNAL house(role, near, min, max)
EXTERNAL npc(role)
EXTERNAL guide(role)
EXTERNAL parking(role)
EXTERNAL load(role, boxes)
EXTERNAL unload(role)
EXTERNAL dismiss(role)

VAR run = 0
VAR runs = 3
VAR boxes = 2
VAR sender = ""
VAR sender_street = ""
VAR recipient = ""
VAR drop_street = ""

-> next_run

=== next_run ===
~ run++
~ boxes = RANDOM(2, 3)
~ sender_street = house("sender", "", 6, 40)
~ sender = npc("sender")
~ drop_street = house("drop", "sender", 15, 60)
~ guide("sender")
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
~ parking("sender")
~ guide("sender")
~ objective("Park in the marked spot outside {sender}'s house")
+ [@park sender] -> loading

=== loading ===
~ guide("")
~ load("sender", boxes)
~ objective("Wait while {sender} loads the boxes into your car")
+ [@loaded sender]
    sender: That's the lot. Mind the corners!
    ~ dismiss("sender")
    -> deliver

=== deliver ===
~ recipient = npc("drop")
~ parking("drop")
~ guide("drop")
~ objective("Take the boxes to {recipient} on {drop_street}")
+ [@park drop] -> unloading

=== unloading ===
~ guide("")
~ unload("drop")
~ objective("{recipient} is unloading the boxes")
+ [@unloaded drop]
    drop: These are from {sender}? Oh, thank you!
    ~ dismiss("drop")
    -> done

=== done ===
{ run < runs:
    Delivery {run} of {runs} done.
    -> next_run
}
~ objective("")
All {runs} deliveries done. The neighbourhood owes you one.
-> END

// Stand-ins so the story plays in Inky (the game has the real ones).
=== function objective(text) ===
~ return
=== function house(role, near, min, max) ===
~ return "Elm Street"
=== function npc(role) ===
~ return "Sam"
=== function guide(role) ===
~ return
=== function parking(role) ===
~ return
=== function load(role, n) ===
~ return
=== function unload(role) ===
~ return
=== function dismiss(role) ===
~ return
