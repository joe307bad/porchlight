# title: Deliver the Presents
# summary: It's Christmas Eve and you're Santa, high over the town in your sleigh behind nine reindeer. Fly through the golden ring over each house and the reindeer bring you down: onto the roof and down the chimney, or into the street and in at the front door, to leave a present under the tree. Five houses before morning!

// Everything this mission is lives in this file (see README.md in this
// folder): you as Santa, the sleigh, the rings over each house, the reindeer
// bringing you down, the chimney or the front door, and the tree to put a
// present under, out of the game's building blocks.
EXTERNAL objective(text)
EXTERNAL hint(when, role, text)
EXTERNAL clear_hints()
EXTERNAL place(role, kind, near, min, max, parking)
EXTERNAL look(model)
EXTERNAL your_plane(model, height)
EXTERNAL ring(role, height, size)
EXTERNAL land(role, at)
EXTERNAL chimney(role, way)
EXTERNAL tree(role, gift)
EXTERNAL put_you(role, at)
EXTERNAL guide(role, to, label)
EXTERNAL dot(role, to)

CONST SANTA = "people/santa"
CONST SLEIGH = "planes/santa-sleigh"
CONST PRESENT = "props/present"
// Where you start: high over the town (tiles up; the tallest towers are lower).
CONST START_HEIGHT = 16
// The ring over each house: how high it hangs (tiles: over the roofs, under
// the clouds) and how big it is across (an easy target).
CONST RING_HEIGHT = 6
CONST RING_SIZE = 4.5
// How many houses get a present. Every other one is down the chimney (the
// first, third and fifth); the others are in at the front door.
CONST STOPS = 5

VAR stop = 0
VAR street = ""

~ look(SANTA)
~ your_plane(SLEIGH, START_HEIGHT)
It's Christmas Eve, and the whole town is fast asleep.
You're Santa! Your sleigh's high over the rooftops, nine reindeer in harness and Rudolph's nose lighting the way.
The sack's full of presents, and there are five houses on the list tonight. Every one needs a present under its tree.
-> next_house

=== next_house ===
~ stop++
// The next house on the list: a few streets on from where you are, with
// room in the street outside to set the sleigh down.
~ street = place("home", "house", "", 10, 30, true)
~ tree("home", PRESENT)
~ ring("home", RING_HEIGHT, RING_SIZE)
~ guide("home", "ring", "the ring over {street}")
~ hint("always", "", "Fly through the golden ring: W climbs, S dives, A and D bank (on a pad: RT, LT and the stick)")
~ objective("Fly through the golden ring over {street} ({stop} of {STOPS})")
+ [@ring home] -> through

=== through ===
~ clear_hints()
~ guide("", "", "")
~ objective("")
{ stop % 2 == 1:
    -> roof
- else:
    -> front_door
}

// Down the chimney.

=== roof ===
~ land("home", "roof")
Through the ring! The reindeer take the reins from here, circling down onto the roof of the house on {street}.
+ [@landed home] -> down_chimney

=== down_chimney ===
you: Ho ho ho! Down the chimney I go.
// You jump out of the sleigh yourself (A, or Enter), and down you go.
~ objective("Jump out of the sleigh and down the chimney")
~ chimney("home", "down")
+ [@chimney home] -> chimney_tree

=== chimney_tree ===
~ dot("home", "tree")
~ hint("always", "", "Go up to the tree and press Enter (A on a pad) to leave a present")
~ objective("Put a present under the tree")
+ [@gift home] -> up_chimney

=== up_chimney ===
~ dot("", "")
~ clear_hints()
~ objective("")
A present under the tree, just where it should be. Back up the chimney!
~ chimney("home", "up")
+ [@chimney home] -> done

// In at the front door.

=== front_door ===
~ land("home", "kerb")
Through the ring! The reindeer take the reins from here, swooping down into the street outside the house on {street}.
+ [@landed home] -> out_of_sleigh

=== out_of_sleigh ===
// Out onto the pavement: you hop out of the sleigh yourself (A, or Enter).
~ put_you("home", "kerb")
No chimney on this one. Hop out, and in at the front door, quiet as a mouse!
~ guide("home", "door", "the front door")
~ hint("always", "", "In at the front door")
~ objective("Go in at the front door of the house on {street}")
+ [@inside home] -> door_tree

=== door_tree ===
~ guide("", "", "")
~ clear_hints()
~ dot("home", "tree")
~ hint("always", "", "Go up to the tree and press Enter (A on a pad) to leave a present")
~ objective("Put a present under the tree")
+ [@gift home] -> back_to_sleigh

=== back_to_sleigh ===
~ dot("", "")
~ clear_hints()
A present under the tree. Now tiptoe back out to the sleigh before anyone wakes!
~ guide("home", "car", "the sleigh")
~ hint("always", "", "Back out to the sleigh, and press G beside it (B on a pad) to climb in")
~ objective("Back to the sleigh")
+ [@aboard] -> done

=== done ===
~ clear_hints()
~ guide("", "", "")
~ objective("")
{ stop < STOPS:
    {~Ho ho ho!|Another Christmas morning sorted.|Rudolph snorts happily.|The reindeer stamp their hooves, ready for the next one.} {stop} down, {STOPS - stop} to go. Up and away!
    -> next_house
}
Five presents under five trees, and the sky's getting light. Back to the North Pole!
~ your_plane("", 0)
~ look("")
Merry Christmas! The whole town wakes up to presents under the tree.
-> END

// Stand-ins so the story plays in Inky (the game has the real ones).
=== function objective(text) ===
~ return
=== function hint(when, role, text) ===
~ return
=== function clear_hints() ===
~ return
=== function place(role, kind, near, min, max, parking) ===
~ return "Holly Lane"
=== function look(model) ===
~ return
=== function your_plane(model, height) ===
~ return
=== function ring(role, height, size) ===
~ return
=== function land(role, at) ===
~ return
=== function chimney(role, way) ===
~ return
=== function tree(role, gift) ===
~ return
=== function put_you(role, at) ===
~ return
=== function guide(role, to, label) ===
~ return
=== function dot(role, to) ===
~ return
