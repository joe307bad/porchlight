# The town's calendar: weekends, school runs and market day

Each town now has a calendar. A day is a weekday or a weekend day, perhaps a
public holiday, a school day or not, and on Saturdays market day in the plaza.
It's worked out from the town's seed and the date alone, as the clock is, so the
server, every player's game and Manage agree on it without a word sent. The
town's people plan their days by it. This is §5's weekends, shifts and school
drop-offs and the first of §6's calendar in
[city-research.md](city-research.md).

The code: `Life/Calendar.cs` (the calendar), `Explore/Market.cs` (the market's
stalls), `Life/People.cs` (rosters, days off, the school run, the market),
`Game/Rendering/MarketStalls.cs` (the stalls drawn), and `sim/src/SimTown.cs`
(`calendar` and `market` on `GET /mp/sim/life`). The check is
`dotnet run --project "unity/Tools~/Checks" -c Release -- calendar`.

## The calendar

| What | Rule | Where it comes from |
| --- | --- | --- |
| Weekday | 1 Jan 1970 was a Thursday; the county's day (`TownClock.DayOf`) | |
| Public holidays | The eleven US federal holidays; a Saturday's kept on the Friday before, a Sunday's on the Monday after (New Year's Day on a Saturday falls on 31 December) | 5 U.S.C. 6103, OPM's observance rule [C1] |
| Offices | Open Monday to Friday, shut on holidays but Columbus Day and Veterans Day (most firms stay open) | *assumed*, from BLS paid-holiday counts [C2] |
| Shops | Open every day but Thanksgiving, Christmas Day and New Year's Day; 9 to 5:30, Sundays 11 to 4 | *assumed* |
| School year | 180 days, starting the third Monday of August or a week later (each town its own) and ending on its 180th school day, early to mid June | NCES: most states set 180 days [C3] |
| School breaks | Thanksgiving Wednesday to Friday; winter break 23 December to 2 January; a spring break week in March (the 2nd, 3rd or 4th week: each town its own); in-service days on a Friday in October, a Monday in February and the Friday before Memorial Day; Veterans Day off in half the towns | *assumed*, typical district calendars |
| Bells | First bell at 7:45, 8, 8:15 or 8:30 (each town its own), last bell 6¾ hours later | *assumed* |
| Market day | Every Saturday but Christmas Day and New Year's Day; 8 am to 1 pm, 9 to noon November to March | *assumed*, typical farmers' market hours |
| Stalls taken | 55% of pitches in winter, 70% in spring and autumn, 85% in summer, ±15% a week | *assumed* |

Each town's draws are its own (`Rng("{seed}::calendar::…")`), so neighbouring
towns' spring breaks and bells differ. Bin day was already by weekday
(`Services.cs`); it now asks the calendar.

## Who works when

A worker's roster comes from where they work (`People.Rostered`). It's drawn
from its own random numbers, so the town's people are as before.

| Roster | Who | Days |
| --- | --- | --- |
| Monday to Friday | offices, civic buildings, 60% of industry, 65% of jobs beyond the gates | weekdays the offices are open |
| The shops' week | shops and mixed use, 20% of jobs beyond the gates | five days of seven: a weekday off, and Sunday too for three in five (else a second weekday) |
| Shifts | the fire station, 40% of industry, 15% of jobs beyond the gates | five on, two off, holidays or not; a week of earlies (6 to 2) then a week of lates (2 to 10) |
| The school's days | the school's staff | school days, in an hour before the bell |

ATUS has 87% of full-time workers working on an average weekday and 29% on a
weekend day [T36]; BLS has about 16% working shifts outside the day [C4]. Our
towns come out at 12–22% working on a Saturday: their jobs are mostly offices
and the town centre's shops are few. A town with more shops works more weekends.

## Days off

A weekend, a holiday, a rest day from a roster or a day with no school
(`People.DayOff`):

- **The market** on market day: 15% of grown-ups, 10% of children, sometime
  while it's open, for 25 minutes to an hour.
- **The week's shop** late in the morning: from 10 (11:30 on Sunday) to 2, for
  55% of grown-ups (35% if they've been to the market) and 20% of children.
- **Someone to see** in the afternoon, for half.
- **The evening out** on a Saturday or a weekday holiday, for a quarter of
  grown-ups.

Through traffic is lighter at the weekend (60% on Saturday, 45% on Sunday,
from mid-morning). Vans make half a weekday's rounds on a Saturday, a third
on Sunday and 40% on a holiday, with no rush hours to sit out (FHWA's
day-of-week factors for trucks [C5]). Commuters beyond the gates spend their
days off there.

## The school run

On a school day each household with children under 11 at school has a school
run in three households of four (`People.RunOf`). It's drawn from the
household and the day, so everyone in it plans the same one.

- **Who takes them:** a grown-up at home in the day, or else a worker with a car
  who works Monday to Friday (or at the school) and is working that day, on their
  way in.
- **When:** 20–45 minutes before the bell (breakfast club, or the bell). The children's plans have the same
  departure as the grown-up's and say who they go with (`Planned.with`).
- **Fetching them:** a grown-up at home fetches them in four cases of five. They
  leave 15 minutes before they're let out (the last bell, or up to an hour later
  after a club) and set off home together 10 minutes after it.
- **Together:** if the grown-up drives, the children get in. They're
  passengers (a `Passenger` leg, unseen as anyone in a car is) and get out when
  the car's parked. If they walk, the children walk alongside at the grown-up's
  pace. Whoever's ready first waits for the other. A child waits up to 30
  minutes (`K_RIDE`), then goes on alone. Nobody is left waiting or sitting in
  a parked car.
- **The rest** walk or take the bus to school on their own, 10–55 minutes
  before the bell, and go home in the hour after the last (clubs, friends, hanging about). Bunched
  any tighter, a town's whole school is out at once, more than the people slots
  can draw.

Most under-11s in our towns are walked to school. Their schools are near and the
towns are walkable (10–16% of trips drive). The NHTS has about half of school
trips by family car nationally [C6]; a car-built town would drive more of them.

## The market

The plaza's market (`MarketPlace.Of`) stands its stalls on the plaza's inner
tiles. The planters, statues and parasols stand only on its edge. Stalls stand
at least 1.4 tiles apart and face the plaza's middle, nearest the middle first,
with at most 12. Each stall has a front, where a shopper stands, and a keeper
spot behind it, for its stallholder. The way in is the plaza tile beside a
street nearest the middle, joined to the road like a building's door
(`TownLife.MARKET`, a place of its own).

- **Stallholders** (`PersonKind.Stallholder`, one a pitch, from beyond the
  gates) drive in 45–75 minutes before it opens and stand behind their stalls.
  They leave 12–30 minutes after it shuts. Only as many come as the day has
  pitches taken: the first so many stalls, the same ones the game draws.
- **Shoppers** are drawn browsing: a stall at a time, 20 seconds to a minute
  and a half at each, strolling between them at 0.15 tiles a second
  (`MarketPlace.Browse`, worked out from when they came, so it costs nothing a
  tick). At most 16 are drawn at once, and never more than a quarter of the
  town's people slots. The rest are there but not drawn, as indoors.
- **The game** draws the stalls (`MarketStalls`) from half an hour before
  opening to half an hour after closing. Each is a cloth-covered table under a
  parasol with carrots and turnips, melons and pumpkins, flowers or a box of odds
  and ends.

## Manage

- **The town view's Calendar section:** today and the week ahead, a line a day,
  with the holiday, market day, school hours (or why there's none), the shops
  shut and the bin round. Weekends and holidays are marked. At the top, the
  market: open with how many stalls and people, packing up, or when it's next on.
- **The map** has a kind for people at the market (`person:m`, in the People
  tab), so they can be shown, hidden and listed.
- **Explanations** say what day it is to each person: a working day or a day off
  on their roster, school hours or why there's no school, market day for a
  stallholder. A child being driven says whose car they're in, and a child
  waiting says who for.

## The check

`calendar` checks, first the calendar alone:

- weekdays as the system has them, 2025 to 2027;
- 2026's holidays on the days they're kept;
- the shops shut on Thanksgiving;
- 180 school days in eight towns' years, never at a weekend or on a holiday;
- market day every Saturday with its hours;
- the same calendar every time, but not in every town.

Then each starter town on Friday 20 November 2026 and the Saturday after:

- young children are taken to school (at least a quarter of them; the rest of
  the households have nobody free);
- nobody's left in a parked car;
- the children are at school by ten;
- Saturday's 7–9 am trips are at most 60% of Friday's (they're 3–8%);
- a tenth to a half of workers work;
- the stallholders are at their stalls when it opens;
- shoppers come, and everyone has gone by 3.

## Left

- A venue with match days, parades, incidents and shared weather (§6).
- Seasonal events: the Christmas market, the Fourth of July.
- Saturday sports and church on Sunday morning.
- Overnight shifts (we have earlies and lates).
- Children driven by a parent who then drives on to work across town.

## References

- **[C1]** OPM, federal holidays and the observance rule: https://www.opm.gov/policy-data-oversight/pay-leave/federal-holidays/
- **[C2]** BLS, *Employee Benefits in the United States*, paid holidays: https://www.bls.gov/ebs/
- **[C3]** NCES, *State Education Practices*, Table 5.14 (instructional days and hours): https://nces.ed.gov/programs/statereform/tab5_14.asp
- **[C4]** BLS, *Job flexibilities and work schedules*: https://www.bls.gov/news.release/flex2.nr0.htm
- **[C5]** FHWA, *Traffic Monitoring Guide*, day-of-week factors: https://www.fhwa.dot.gov/policyinformation/tmguide/
- **[C6]** NHTS, *Children's Travel to School* (2017 NHTS brief): https://nhts.ornl.gov/briefs/
- **[T36]** in [city-research.md](city-research.md): BLS ATUS.
