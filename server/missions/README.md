# Missions

Each `.ink` file here is a mission, written in [ink](https://github.com/inkle/ink/blob/master/Documentation/WritingWithInk.md), inkle's language for branching stories. The server compiles a file whenever it changes (`src/missions.ts`) and hands it to the game, which runs it with ink's own runtime (`unity/Assets/Ink`). So adding or changing a mission needs **no new build of the game**: deploy the server (or edit the file on the box: the folder is mounted into the container), and players press **Refresh** under **Missions** in the sidebar.

The file name is the mission's id (lowercase letters, digits and dashes: `delivery.ink` is `delivery`). Errors in a file show in the sidebar under its name, and in the server's log.

To try a mission outside the game, open it in [Inky](https://github.com/inkle/inky): the stand-in functions at the bottom of each file let it play there. `npm test` checks every mission compiles and uses only the building blocks and waits the game has, and plays `delivery` and `chase` through against a stand-in town that checks each block is used right. `node scripts/mission.ts <id>` prints the compiled story, which `dotnet run --project unity/Tools~/Checks -- mission <file>` plays through the game's runtime against a pretend town.

## Name and summary

Tags at the very top:

```ink
# title: Moving day
# summary: Three neighbours need boxes run across town.
```

## What the story says

Each line is shown on its own, and the player reads on with Enter or A. A line that starts with a role and a colon is spoken by whoever plays that role. `you:` is the player:

```ink
sender: Could you pull up right outside my house?
you: Sure.
The neighbourhood owes you one.
```

Ordinary choices are the player's to pick:

```ink
+ [Sure, I'll park there.] -> park
+ [Not right now.] -> meet
```

## Everything a mission is, is in its file

The game knows no missions. It knows a small set of **building blocks** (put a role in a building, put someone there, have them walk, hold things and put them on your car, mark a spot to park in, show the way, change what you look like, send cars after you, run a clock) and a few **waits**. A mission puts these together into whatever it is about. `delivery.ink` builds "carry boxes from one house to another" out of `walk`, `pause`, `hold`, `put_in_car` and so on, in its own helper functions (`carry_out`, `carry_in`). `chase.ink` builds "a penguin escapes the zoo" out of `place` (the zoo), `look`, `put_you`, `chase` and `countdown`, and the waits `@outside`, `@caught` and `@countdown`. Who's in a mission, their names, what the boxes look like, the hints you see, how precisely you must park: all of that is in the story. A new kind of mission needs no new build of the game as long as these blocks can make it. A new block does need a build: add it to `IMissionWorld` and `MissionScript` (`unity/Assets/Porchlight/Runtime/Shared/Missions`), `MissionPlay`, the stand-in towns in `unity/Tools~/Checks/MissionCheck.cs` and `test/missions.test.ts`, and this page.

Spelling it all out makes the files long. That's intended: the story is the mission.

## Waiting on the town

A choice whose text starts with `@` is a **wait**. It's never shown. The game picks it as soon as it's met. Put several side by side and whichever is met first wins. A wait the game doesn't know stops the mission with an error.

| Wait | Met when |
| --- | --- |
| `@talk role` | The player has gone up to the role's person (on foot, or in a car beside them) and pressed Enter or A. They get a light over their head while you're waited on. |
| `@parked role` | The player's car is stopped in the role's marked spot (`mark_spot`), lined up with the kerb, either way round. |
| `@idle role` | The role's person has done every errand queued for them (`walk`, `pause`, `hold` and so on). |
| `@inside role` | The player is in the role's building (or within the zoo's fence), on the ground: not flying over it. |
| `@outside role` | The player is anywhere but there. |
| `@caught role` | An officer from one of the role's chase cars (`chase`) has stood by the player (`catch_reach`) for `catch_time` seconds. The player on foot or in a car; never while flying. Each catch counts afresh. |
| `@countdown` | The clock (`countdown`) has run out. |
| `@wait 3` | Three seconds have passed. |

Use `+` (sticky) choices for waits inside anything that loops, or the wait is used up the first time.

## Building blocks

Declare the ones a mission uses with `EXTERNAL`. They take effect when the story reaches them, after the player has read the lines before. An `EXTERNAL` the game doesn't have stops the mission with an error (the stand-ins at the bottom of a file are only for Inky). Wrong values (a place or setting that doesn't exist, `hold` with your hands full) stop it too, saying what went wrong.

Roles are any names you like (`sender`, `drop`, `aunt`). Each is one building at a time.

### The story's voice

| Function | What it does |
| --- | --- |
| `objective(text)` | The line at the top of the directions panel saying what to do next. `""` clears it. |
| `hint(when, role, text)` | A prompt at the bottom of the screen, shown while `when` holds: `"always"`; `"no_car"` (the player has no car of their own); `"near"` (they're within 3 tiles of the role: its marked spot, else its person, else the pavement outside); `"near_driving"` (the same, driving their own car). The first that holds shows. "Press Enter to talk to …" shows over all of them. |
| `clear_hints()` | Every hint goes. |
| `guide(role, to, label)` | Shows the player the way, with turn-by-turn directions and a dotted path, to the role's `"spot"`, `"person"`, `"house"` (the pavement outside) or `"door"` (just outside it), called `label`. `guide("", "", "")` stops. |
| `dot(role, to)` | A blue dot on the ground at the same places, with no way shown to it: for somewhere close by. `dot("", "")` clears it. |
| `setting(name, value)` | Tunes how the town plays the mission. `talk_reach` (1.1 tiles) and `talk_reach_car` (1.9): how close you come to someone to talk. `park_reach` (0.45 tiles), `park_speed` (0.12) and `park_angle` (0.45 radians): how close, how still and how straight a car must be to count as parked. `car_reach` (0.8 tiles): how far your car may stand from a spot for someone to walk to it. `walk_speed` (1.0 tiles a second): how fast a mission's people walk. `your_pace` (1.0): how fast you are on foot, as a share of a person's pace. `chase_speed` (4.4 tiles a second) and `chase_speed_car` (6.4): how fast chase cars go after you on foot, and once you're in a car or plane (past a car's top speed, they use their boost). `pull_up` (1.6 tiles): how far from you a chase car stops. `officer_speed` (3.0 tiles a second): how fast its driver, out of the car, runs at you, and `officer_reach` (5.0 tiles): how far off you must get for them to give up and drive after you again. `catch_reach` (0.45 tiles from you) and `catch_time` (1.0 seconds): how close an officer stands, for how long, to have you. |
| `countdown(seconds, label, when, role)` | A clock in the directions panel (`label m:ss`) that runs while `when` holds: `"always"`, or `"inside"`/`"outside"` the role's building. It stops (and says so) while it doesn't, and while the player reads. `@countdown` is met when it runs out. `countdown(0, "", "", "")` stops it. One at a time. |

### Places and people

| Function | What it does |
| --- | --- |
| `place(role, kind, near, min, max, parking)` | Chooses a building for a role: a `"house"`, `"workshop"`, `"shop"`, `"office"`, `"hall"` or `"church"` (`""` for any), or `"zoo"` (every big enough town has one; its gate is the door). Its front door is `min` to `max` tiles from the player (`near` is `""`) or from another role's door. It's by a pavement, and if `parking` is `true`, it has room to park outside. Returns its street's name. Calling it again for the same role moves the role to a new building. |
| `spawn(role, name, look)` | Someone called `name` on the pavement outside the role's building, facing the street. `look` is a character model (`"people/character-1-2-2"`, `"people/policeman-a-4-1"`) or an animal (`"animals/animal-lion"`), `""` for any. Returns their name. Lines starting `role:` are theirs. If the role already has someone, they go indoors first. |
| `leave(role)` | The role's person drops what they're doing (and whatever they hold), goes indoors and is gone. Their name still voices `role:` lines. |
| `mark_spot(role)` | Marks a car-sized spot in the parking lane outside the role's building (`place` it with `parking` true). |
| `clear_spot(role)` | The role's spot is no longer marked. |

### You

| Function | What it does |
| --- | --- |
| `look(model)` | The player looks like `model` while on foot: a character or an animal (`"animals/animal-penguin"`; also `lion`, `tiger`, `elephant`, `giraffe`, `panda`, `polar`, `monkey`, `koala`, `parrot`). `""` is themselves again. It ends with the mission. |
| `put_you(role, at)` | The player is put at the role's building: `"inside"`, `"door"` or `"kerb"`, facing out. Out of any car or plane first. |

### Chases

| Function | What it does |
| --- | --- |
| `chase(role, name, count, car, look)` | `count` cars (1 to 8) of model `car` (`"cars/police"`) come after the player from streets nearby, each with a driver who looks like `look` (`""` for any). A car pulls up near the player (`pull_up`), and its driver gets out and runs to stand by them; get far enough away (`officer_reach`) and they run back and drive after you again. `role` is the chase's own name, not a building's: `@caught role` waits for it, `role:` lines are `name`'s. They follow the streets, cut across open ground when close, and drive round anything that blocks them. Returns `name`. Calling it again for the same role starts the chase afresh. |

`leave(role)` calls a chase off: its cars are gone.

### Errands

These queue up: the role's person does them one after another, while the story goes on. `@idle role` waits for them all to be done.

| Function | What it does |
| --- | --- |
| `walk(role, to)` | Walks to `"inside"` (in at the door and out of sight), `"door"` (just outside it), `"kerb"` (the pavement in front) or `"car"`. They pass the places in between. `"car"` is your car's kerb side: they walk there only while your car is stopped in the role's marked spot (or by the kerb, with no spot marked), and wait at the kerb till it is. |
| `pause(role, seconds)` | Waits where they are (out of sight, if inside). |
| `hold(role, model, size)` | Something in their arms: a model from the game's library (`"furniture/cardboardBoxClosed"`), `size` tiles across. One thing at a time. |
| `put_in_car(role)` | What they hold goes onto your car's roof. |
| `take_from_car(role)` | They take the last thing put on your car into their arms. |
| `put_down(role)` | What they hold is put down, and gone. |

For example, someone fetching a box from indoors and putting it on your car:

```ink
~ walk("sender", "inside")
~ pause("sender", 1.1)
~ hold("sender", "furniture/cardboardBoxClosed", 0.1)
~ walk("sender", "car")
~ put_in_car("sender")
~ walk("sender", "kerb")
+ [@idle sender] -> loaded
```

Ink functions that call these, recursively for a count, keep a mission tidy (see `carry_out` in `delivery.ink`).

## Notes

- A mission's people and spots are the player's alone. Others in the town don't see them.
- Missions don't save. Leaving the town (a new seed, or regenerating) ends the one being played.
