# title: Marathon runner
# summary: Run the town's longest race, right across the map from one side to the other, between the ribbons and the cheering crowds. The clock starts as you cross the start line. Every town keeps its own best times.
# unlocks: sprint

// Everything this mission is lives in this file: the course (the longest way
// right across the town (corner to corner, or gate to gate), along its streets, fenced with
// ribbon and lined with people), the clock, the town's best times and what
// finishing earns. The game only knows the building blocks below (see
// README.md in this folder).
EXTERNAL objective(text)
EXTERNAL hint(when, role, text)
EXTERNAL clear_hints()
EXTERNAL course(role, crowd)
EXTERNAL put_you(role, at)
EXTERNAL stopwatch(label, role)
EXTERNAL stopwatch_time()
EXTERNAL record(seconds)
EXTERNAL town_record()
EXTERNAL town_record_holder()
EXTERNAL unlock(what)

// How packed the crowd is along the ribbons (0 to 1).
CONST CROWD = 0.8

VAR metres = 0
VAR time = 0
// Your best here before this run, and the town's (0: none yet).
VAR before = 0
VAR best = 0

~ metres = course("race", CROWD)
~ best = town_record()
~ put_you("race", "start")
It's marathon day! The whole town has come out to watch.
The course runs {km(metres)} along the streets, right across town. Stay between the ribbons all the way.
{ best > 0:
    The record here is {clock(best)}, run by {town_record_holder()}.
- else:
    No one has run it in this town yet. Be the first!
}
The clock starts as soon as you cross the start line. On your marks...
-> race

=== race ===
~ stopwatch("Time", "race")
~ objective("Run to the finish")
~ clear_hints()
~ hint("off_course", "race", "Back between the ribbons! Only the course counts.")
~ hint("always", "", "Hold Shift (or RT) to sprint · it only counts on foot")
+ [@finished race] -> finish

=== finish ===
~ time = stopwatch_time()
~ objective("")
~ clear_hints()
~ before = record(time)
The crowd roars as you cross the line!
Your time: {clock(time)}.
{
- best == 0 || time < best:
    That's the new record for this town!
- before > 0 && time < before:
    That's your best here, {clock(before - time)} faster than before.
- before > 0:
    Your best here is still {clock(before)}.
}
~ unlock("sprint")
~ stopwatch("", "")
All that running has made you a faster sprinter, wherever you go.
-> END

// A time in seconds as the clock shows it: 3:07.25.
=== function clock(s) ===
~ temp cs = INT(s * 100 + 0.5)
~ temp m = cs / 6000
~ temp sec = (cs / 100) mod 60
~ temp hundredths = cs mod 100
~ return "{m}:{sec < 10:0}{sec}.{hundredths < 10:0}{hundredths}"

// Metres as kilometres to one place: 4.2 km.
=== function km(m) ===
~ temp tenths = INT(m / 100 + 0.5)
~ return "{tenths / 10}.{tenths mod 10} km"

// Stand-ins so the story plays in Inky (the game has the real ones).
=== function objective(text) ===
~ return
=== function hint(when, role, text) ===
~ return
=== function clear_hints() ===
~ return
=== function course(role, crowd) ===
~ return 4200
=== function put_you(role, at) ===
~ return
=== function stopwatch(label, role) ===
~ return
=== function stopwatch_time() ===
~ return 754.32
=== function record(seconds) ===
~ return 0
=== function town_record() ===
~ return 0
=== function town_record_holder() ===
~ return "Someone"
=== function unlock(what) ===
~ return
