# Missions

Each `.ink` file here is a mission, written in [ink](https://github.com/inkle/ink/blob/master/Documentation/WritingWithInk.md), inkle's language for branching stories. The server compiles a file whenever it changes (`src/missions.ts`) and hands it to the game, which runs it with ink's own runtime (`unity/Assets/Ink`). So adding or changing a mission needs **no new build of the game**: deploy the server (or edit the file on the box: the folder is mounted into the container), and players press **Refresh** under **Missions** in the sidebar.

The file name is the mission's id (lowercase letters, digits and dashes: `delivery.ink` is `delivery`). Errors in a file show in the sidebar under its name, and in the server's log.

To try a mission outside the game, open it in [Inky](https://github.com/inkle/inky): the stand-in functions at the bottom of each file let it play there. `npm test` checks every mission compiles and uses only the building blocks and waits the game has, and plays `delivery`, `chase`, `firefighter`, `marathon`, `trash-day`, `turkey-run` and `deliver-the-presents` through against a stand-in town that checks each block is used right. `node scripts/mission.ts <id>` prints the compiled story, which `dotnet run --project unity/Tools~/Checks -- mission <file>` plays through the game's runtime against a pretend town.

## Name and summary

Tags at the very top:

```ink
# title: Moving day
# summary: Three neighbours need boxes run across town.
```

A mission that earns something says what, so the Journal can show the reward before it's won (the story still calls `unlock`):

```ink
# unlocks: animals
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

The game knows no missions. It knows a small set of **building blocks** (put a role in a building, put someone there, have them walk, hold things and put them on your car, mark a spot to park in, show the way, change what you look like, give you a car or a plane, send cars after you, run a clock, set a building alight and give you a water turret to put it out, lay out a race course and time you along it, keep a best time, hang rings in the sky, bring your plane down on a roof or in the street, take you down a chimney, stand a Christmas tree indoors, give a reward) and a few **waits**. A mission puts these together into whatever it is about. `delivery.ink` builds "carry boxes from one house to another" out of `walk`, `pause`, `hold`, `put_in_car` and so on, in its own helper functions (`carry_out`, `carry_in`). `chase.ink` builds "a penguin escapes the zoo" out of `place` (the zoo), `look`, `put_you`, `standby`, `chase`, `countdown` and `unlock`, and the waits `@outside`, `@caught` and `@countdown`. `firefighter.ink` builds "save three burning houses" out of `look`, `your_car` (the fire engine), `place`, `fire`, `mark_spot`, `turret` and `unlock`, and the waits `@parked`, `@doused` and `@out`. `marathon.ink` builds "run the town's longest race" out of `course`, `put_you`, `stopwatch`, `record`, `town_record` and `unlock`, and the wait `@finished`. `trash-day.ink` builds "drive the garbage truck round five bins" out of `your_car` (the truck), `place`, `bin`, `mark_spot` and the crew's errands `get_out`, `walk` (to `"bin"` and `"back"`), `take_bin`, `dump`, `return_bin` and `get_in`, and the waits `@parked` and `@idle`. `turkey-run.ink` builds "carry frozen turkeys from the pickup into five houses" out of `your_car` (the pickup), `load_back` (its bed piled with turkeys), `place`, `mark_spot`, the helper's errands `get_out`, `walk` (to `"back"`, `"inside"` and `"door"`), `take_from_back`, `pause`, `put_down` and `get_in`, and `unlock`. `deliver-the-presents.ink` builds "Santa flies to five houses with presents" out of `look` (Santa), `your_plane` (the sleigh), `place`, `tree`, `ring`, `land` (on the roof at every other house, in the street at the rest), `chimney` (down and back up), `put_you`, `guide` (to the ring, the door and the sleigh) and `dot` (the tree), and the waits `@ring`, `@landed`, `@chimney`, `@inside`, `@gift` and `@aboard`. Who's in a mission, their names, what the boxes look like, the hints you see, how precisely you must park: all of that is in the story. A new kind of mission needs no new build of the game as long as these blocks can make it. A new block does need a build: add it to `IMissionWorld` and `MissionScript` (`unity/Assets/Porchlight/Runtime/Shared/Missions`), `MissionPlay`, the stand-in towns in `unity/Tools~/Checks/MissionCheck.cs` and `test/missions.test.ts`, and this page.

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
| `@doused role` | One of the role's burning windows or its door (`fire`) has gone out. Each counts afresh, so a loop hears of every one. |
| `@out role` | Every one of the role's burning windows, and its door, is out. Put it before `@doused` where both wait: the last window meets both. |
| `@finished role` | The player has run the role's course (`course`) all the way to its finish line, in order, on foot. The stopwatch on it stops there. |
| `@ring role` | The player has flown their plane through the role's ring (`ring`), either way, inside its rim (a near miss just past it counts). |
| `@landed role` | The story has brought the player's plane down at the role (`land`). They're still in their seat. |
| `@chimney role` | The player's trip down (or back up) the role's chimney (`chimney`) is done: they're inside by the tree, or back in their seat. Each counts afresh. |
| `@gift role` | The player has gone up to the role's tree (`tree`) and pressed Enter or A: the present's under it. A light hangs over the tree while this waits. |
| `@aboard` | The player is at the controls of their own car or plane (back in it with G, or B on a pad). |
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
| `hint(when, role, text)` | A prompt at the bottom of the screen, shown while `when` holds: `"always"`; `"no_car"` (the player has no car of their own); `"near"` (they're within 3 tiles of the role: its marked spot, else its person, else the pavement outside); `"near_driving"` (the same, driving their own car); `"off_course"` (they've strayed from the role's course, `course`). The first that holds shows. "Press Enter to talk to …" shows over all of them. |
| `clear_hints()` | Every hint goes. |
| `guide(role, to, label)` | Shows the player the way, with turn-by-turn directions and a dotted path, to the role's `"spot"`, `"person"`, `"house"` (the pavement outside), `"door"` (just outside it), `"ring"` (the ground under its ring), `"tree"` (where you stand to put the present under its tree) or `"car"` (your own car or plane: your seat in it), called `label`. `guide("", "", "")` stops. |
| `dot(role, to)` | A blue dot on the ground at the same places, with no way shown to it: for somewhere close by. `dot("", "")` clears it. |
| `setting(name, value)` | Tunes how the town plays the mission. `talk_reach` (1.1 tiles) and `talk_reach_car` (1.9): how close you come to someone to talk. `park_reach` (0.45 tiles), `park_speed` (0.12) and `park_angle` (0.45 radians): how close, how still and how straight a car must be to count as parked. `car_reach` (0.8 tiles): how far your car may stand from a spot for someone to walk to it. `walk_speed` (1.0 tiles a second): how fast a mission's people walk. `your_pace` (1.0): how fast you are on foot, as a share of a person's pace. `chase_speed` (4.4 tiles a second) and `chase_speed_car` (6.4): how fast chase cars go after you on foot, and once you're in a car or plane (past a car's top speed, they use their boost). `pull_up` (1.6 tiles): how far from you a chase car stops. `officer_speed` (3.0 tiles a second): how fast its driver, out of the car, runs at you, and `officer_reach` (5.0 tiles): how far off you must get for them to give up and drive after you again. `ring` (4.5 tiles): from further off than this, a chase's cars each make for their own side of a ring round you, so they close in from all round (0 sends them straight at you). `catch_reach` (0.45 tiles from you) and `catch_time` (1.0 seconds): how close an officer stands, for how long, to have you. `douse_time` (2.5 seconds): how long water must play on a burning window to put it out. `water_speed` (5.0 tiles a second): how fast the turret throws water (so how far it reaches). `aim_speed` (1.3 radians a second): how fast the turret turns at full stick. |
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
| `look(model)` | The player looks like `model` while on foot: a character (`"people/firefighter"` and `"people/santa"` too) or an animal (`"animals/animal-penguin"`; also `lion`, `tiger`, `elephant`, `giraffe`, `panda`, `polar`, `monkey`, `koala`, `parrot`). `""` is themselves again. It ends with the mission. |
| `put_you(role, at)` | The player is put at the role's building: `"inside"`, `"door"` or `"kerb"`, facing out. Out of any car or plane first. In the plane the story gave them (`your_plane`), set down in the street (`land(role, "kerb")`), `"kerb"` waits for them to hop out themselves (A, or Enter): out of the seat in a leap over its side, onto the pavement, and on to the kerb, turned to face the house, the camera coming round behind them. |
| `your_car(model)` | The player's own car becomes a `model` car (`"cars/firetruck"`; cars only, not boats or planes), brought beside them, and they're put at its wheel (at the wheel of a car already, it's swapped in under them). When the mission's over, or with `your_car("")`, it turns back into the car they had; if they had none, they keep it. |
| `your_plane(model, height)` | The player's own vehicle becomes a `model` plane (`"planes/santa-sleigh"`, Santa's sleigh behind nine reindeer; any of the airport's planes too), and they're put at its controls, flying `height` (1 to 24) tiles up where they are, the way they face. It flies as any plane does, and while they're at its controls the camera's in one place only: straight behind them, a little above, the plane in the middle of the view. When the mission's over, or with `your_plane("", 0)`, it's gone: they're set down on the ground and the car they had is brought back beside them. |

### Chases

| Function | What it does |
| --- | --- |
| `chase(role, name, count, car, look)` | `count` cars (1 to 8) of model `car` (`"cars/police"`) come after the player from streets nearby, each with a driver who looks like `look` (`""` for any). A car pulls up near the player (`pull_up`), and its driver gets out and runs to stand by them; get far enough away (`officer_reach`) and they run back and drive after you again. `role` is the chase's own name, not a building's: `@caught role` waits for it, `role:` lines are `name`'s. They follow the streets, cut across open ground when close, and drive round anything that blocks them. Returns `name`. Calling it again for the same role starts the chase afresh. |

| `standby(role, count, car, look)` | Gets a chase's cars ready ahead of it: `count` (1 to 24) of model `car`, each with a driver looking like `look`, parked all over the town (each as far from the others, and the player, as it can be). Call it at the start, so nothing's built at the moment the chase begins. Its `chase` sends them all in as well as its own `count` (which can then be 0). Called off, they go back to wait for the next. |

`leave(role)` calls a chase off: its cars are gone (those on standby go back to wait).

### Fires

| Function | What it does |
| --- | --- |
| `fire(role, windows)` | The role's building is alight: up to `windows` (1 to 8) of the windows a fire engine parked out front can reach, those facing the street first, and its front door, each with flames and a glowing pane and smoke pouring out of its top, with a column of smoke over the roof that can be seen from streets away. Water on a burning window or the door for `douse_time` seconds puts it out (it dies down as it's doused, and steams). `@doused role` hears of each one, `@out role` of the last. It returns how many are burning (those windows and the door), which can be fewer than asked for plus one: a house can have fewer windows facing the street. `fire(role, 0)` puts it all out at once. Calling it again for the same role lights it afresh (after `place` has moved the role, a new building). |
| `turret(role, crew, model)` | A water turret on the roof of the player's car, theirs to climb up to: at the wheel, stopped, they press A (or Enter). Up there the camera is behind the turret; the stick (or the arrows, or WASD) turns it all the way round and tips it up and down, and holding A, RT or RB (or Space) sprays (A and RB because Firefox gives some pads no triggers); a reticle shows where the water will land, orange when that's a burning window; B (or G) climbs back down to the wheel. It points at the role's building to begin with. `crew` (0 to 6) people looking like `model` (`""` for the firefighter) stand round the car with hoses and spray wherever the player's water lands; each puts a little more water on a window. `turret("", 0, "")` takes it all away (and the player down from it). |

### Races

| Function | What it does |
| --- | --- |
| `course(role, crowd)` | A race course as a role: a long way along the town's streets from near one corner of the map to near the opposite one (or one gate to the opposite gate), planned for running: long straight stretches with few turns, main roads over narrow streets, then lengthened round the block where that adds straight running without passing a junction twice (of the two diagonals and the two ways gate to gate, the better of those that go most of the way across). Ribbon on posts runs along both kerbs and across behind the start and past the finish, and on foot it holds you in: there's no leaving the course until the race is over. An arch stands over the start line and a chequered one over the finish, and people line the road, `crowd` (0 to 1) how packed: thickest by the start and the finish, with a few dozen by the ribbon wherever the player is, turning to watch and waving them on. `put_you(role, "start")` puts the player on the start line (`"finish"` just short of the finish). Progress counts only along the course, stretch by stretch, on foot and on the ground: a short cut to a later stretch, or a car, gains nothing. Returns its length in metres. |
| `stopwatch(label, role)` | A stopwatch in the directions panel (`label m:ss.t`, and with a course, how far along it you are): with a course's role, it starts as the player crosses its start line and stops as they finish; with `""` it starts now. It doesn't run while the player reads. `stopwatch("", "")` takes it away. |
| `stopwatch_time()` | The stopwatch's time, in seconds (to the hundredth). |
| `record(seconds)` | Keeps a time as the player's for this mission in this town (the town's key: its seed and settings), if it's their account's best there. Returns their best there before it (0: none). Kept on the server (`/mp/records`), one per account per town, under the name of the character who ran it; in dev mode, until the page closes. |
| `town_record()` | This town's best time for this mission, by anyone, as the mission began (0: none yet). |
| `town_record_holder()` | Who ran it (`""`: no one). |

### Flying

| Function | What it does |
| --- | --- |
| `ring(role, height, size)` | A golden ring `size` tiles across (up to 12) hung `height` (1 to 24) tiles up, out over the street in front of the role's building, facing the player as it's hung, to fly through (`@ring`), or just near (within a tile and a half of its rim). It's seen from right across the town (never fogged or clipped away). It goes, growing and fading, once they're through. While it's out of sight an arrow at the edge of the screen points the way to it, up, down or round. `ring(role, 0, 0)` takes it down. |
| `land(role, at)` | The story takes the player's plane from them, gently (carrying straight on from just where it is, as fast and the same way), and brings it down as a pilot would: round in smooth turns onto a straight run-in, slowing and gliding down it over whatever's in the way, flaring level as it touches down and sliding to a stop, at the role's building: `"roof"`, on its roof along the ridge, read off the building itself, its runners resting on the roof and the team on along it (the reindeer stand on whatever roof is under them), with a brick chimney beside the seat on the street's side; or `"kerb"`, in the street outside (in the role's parking spot if it has one), its runners on the road, so it lifts away again when they fly on. `@landed` once it's down; it's held still while they read. The player must be flying a plane. |
| `chimney(role, way)` | `"down"`: once the player presses A (or Enter), a leap out of their seat on the role's roof (`land(role, "roof")` first) right onto the chimney's top, a moment stood there turned to the street, then a hop and down inside it in a puff of soot, the camera swinging round to watch from out over the street, to come out inside by the role's tree (or just in at the door). The sleigh's held where it is till they jump. `"up"`: popping back up out of the chimney from inside and a leap back into their seat, the camera coming round behind the sleigh. `@chimney` once they're there. |
| `tree(role, gift)` | A Christmas tree in a corner of the role's building, clear of its furniture and doors. While the story waits on `@gift role`, a light hangs over it; going up to it and pressing Enter (or A) puts `gift` (a model, `"props/present"`) under it. A tree with its present stays where it is when `place` moves the role on, until the mission's over. |

### Rewards

| Function | What it does |
| --- | --- |
| `unlock(what)` | Something the player keeps for good: `"animals"`, so the character box offers the zoo's animals after the people; `"firefighter"`, so the character box offers the firefighter and the car box the fire engine; `"sprint"`, so the player sprints faster on foot; `"feast"`, so every house's dining table, in every town, has Thanksgiving dinner on it (a roast turkey on its platter, mash, cranberry sauce, green beans, stuffing and a pumpkin pie). Kept with the account, on the server: every one of its characters has it, in every town, and playing the mission again doesn't earn it twice. In dev mode (no server) it lasts until the page closes. |

### Errands

These queue up: the role's person does them one after another, while the story goes on. `@idle role` waits for them all to be done.

| Function | What it does |
| --- | --- |
| `walk(role, to)` | Walks to `"inside"` (in at the door and out of sight), `"door"` (just outside it), `"kerb"` (the pavement in front), `"car"`, `"bin"` or `"back"`. They pass the places in between. `"car"` is your car's kerb side: they walk there only while your car is stopped in the role's marked spot (or by the kerb, with no spot marked), and wait at the kerb till it is. `"bin"` is the role's bin's handle (`bin`), facing it. `"back"` is just behind your car, facing it (far enough back for a bin they're wheeling to clear it; close up to the tailgate otherwise): like `"car"`, only while your car's there. |
| `pause(role, seconds)` | Waits where they are (out of sight, if inside). |
| `hold(role, model, size)` | Something in their arms: a model from the game's library (`"furniture/cardboardBoxClosed"`), `size` tiles across. One thing at a time. |
| `put_in_car(role)` | What they hold goes onto your car's roof. |
| `take_from_car(role)` | They take the last thing put on your car into their arms. |
| `put_down(role)` | What they hold is put down, and gone. |
| `take_from_back(role)` | Standing behind your car (`"back"`), they lean in to the tailgate, take the top thing off the pile in its back (`load_back`) by its sides and lift it out into their arms, holding it in both hands wherever they walk. They wait while your car isn't there; with nothing left on the pile, there's nothing to take. |
| `take_bin(role)` | They take the role's bin by its handle (walk to `"bin"` first) and tip it back onto its wheels, to wheel it wherever they walk. |
| `dump(role)` | Standing behind your car (`"back"`), they step up to it, lift the bin they're wheeling by its sides to head height and tip it over the tailgate: the lid falls open, rubbish drops in, they shake it and bring it back down onto its wheels. They wait while your car isn't there. |
| `return_bin(role)` | They stand the bin they're wheeling back where it was (walk to `"bin"` first) and let go. |
| `get_in(role)` | They walk to your car's door (waiting at the kerb while it isn't stopped by the role) and hop up into it, out of sight. |

### The back of your car

| Function | What it does |
| --- | --- |
| `load_back(model, count, size)` | `count` (0 to 40) of a model from the game's library (`"props/frozen-turkey"`), each `size` tiles across, piled in the back of your car: in a pickup's bed (`"cars/truck"`), on the roof towards the back of any other car. They're heaped as a pile is, the bottom layer as small as holds them all and each layer up sitting in the gaps of the one under it. It replaces any load before. Someone behind your car takes the top one with `take_from_back`. The pile goes when the mission ends. |

### Bins and your crew

| Function | What it does |
| --- | --- |
| `bin(role, model, size)` | A wheelie bin out on the pavement (its yard side) outside the role's building, a little along from the way to its door, front to the house and handle to the street: `"props/wheelie-bin-trash"` (green) or `"props/wheelie-bin-recycle"` (blue), `size` tiles across (0.2 is about 80 cm). It stays out when `place` moves the role on. Its lid opens on a hinge when it's tipped. |
| `get_out(role, name, look)` | Someone called `name`, looking like `look` (`""` for any), hops down out of your car's door on the kerb side, as the role's person: their errands are then the role's (`walk`, `take_bin`, `dump` and so on), and `role:` lines are theirs. Call it once your car's stopped by the role (`@parked`). Returns their name. If they `get_in` again, they ride with you, out of sight, till the role moves on. |

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

- A mission's people, spots, fires and courses are the player's alone. Others in the town don't see them. Best times (`record`) are shared: everyone in the town sees its record.
- A mission in progress doesn't save. Leaving the town ends the one being played.
- Reaching the end of the story (with no error) counts as finishing the mission: it's kept with the account, for the Journal. Abandoned missions don't count. Every mission's only ending is the win today; a mission that can be lost will need a new block to say so.
