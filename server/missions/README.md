# Missions

Each `.ink` file here is a mission, written in [ink](https://github.com/inkle/ink/blob/master/Documentation/WritingWithInk.md), inkle's language for branching stories. The server compiles a file whenever it changes (`src/missions.ts`) and hands it to the game, which runs it with ink's own runtime (`unity/Assets/Ink`). So adding or changing a mission needs **no new build of the game**: deploy the server (or edit the file on the box: the folder is mounted into the container), and players press **Refresh** under **Missions** in the sidebar.

The file name is the mission's id (lowercase letters, digits and dashes: `delivery.ink` is `delivery`). Errors in a file show in the sidebar under its name, and in the server's log.

To try a mission outside the game, open it in [Inky](https://github.com/inkle/inky): the stand-in functions at the bottom of `delivery.ink` let it play there. `npm test` checks every mission compiles. `node scripts/mission.ts <id>` prints the compiled story, which `dotnet run --project unity/Tools~/Checks -- mission <file>` plays through the game's runtime against a pretend town.

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

## Waiting on the town

A choice whose text starts with `@` is a **wait**. It's never shown. The game picks it as soon as it's met. Put several side by side and whichever is met first wins.

| Wait | Met when |
| --- | --- |
| `@talk role` | The player has gone up to the role's person (on foot, or in a car beside them) and pressed Enter or A. They get a light over their head while you're waited on. |
| `@park role` | The player's car is stopped in the role's marked spot (see `parking`), lined up with the kerb, either way round. |
| `@loaded role` | The role's person has carried all the boxes from `load` to the car. |
| `@unloaded role` | The role's person has carried every box in the car into their house (`unload`). |
| `@wait 3` | Three seconds have passed. |

Use `+` (sticky) choices for waits inside anything that loops, or the wait is used up the first time.

## Changing the town

Declare the ones a mission uses with `EXTERNAL`. They take effect when the story reaches them, after the player has read the lines before.

| Function | What it does |
| --- | --- |
| `objective(text)` | The line at the top of the directions panel saying what to do next. `""` clears it. |
| `house(role, near, min, max)` | Chooses a house for a role. Its front door is `min` to `max` tiles from the player (`near` is `""`) or from another role's house, and it has room to park outside. Returns its street's name. Calling it again for the same role moves the role to a new house. |
| `npc(role)` | Someone waiting on the pavement outside the role's house. Returns their name. |
| `guide(role)` | Shows the player the way, with turn-by-turn directions and a dotted path. It leads to the role's marked spot if there is one, else their person, else their house. `guide("")` stops. |
| `parking(role)` | Marks a car-sized spot in the parking lane outside the role's house. |
| `load(role, boxes)` | The role's person goes indoors and brings out `boxes` boxes, one at a time, to the car's kerb side. The boxes ride on the roof. They wait if the car isn't in its spot. |
| `unload(role)` | The role's person takes every box off the car and into their house. |
| `dismiss(role)` | The role's person goes indoors and is gone, and their spot is cleared. |

Roles are any names you like (`sender`, `drop`, `aunt`). Each is one house at a time.

## Notes

- A mission's people and spots are the player's alone. Others in the town don't see them.
- Missions don't save. Leaving the town (a new seed, or regenerating) ends the one being played.
