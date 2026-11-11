# Home Building and Customization: Game Design Patterns (survey as of Nov 2026)

Scope: the design and UX of building, decorating and furnishing player homes, as background for Porchlight, a cosy low-poly multiplayer town game in Unity (WebGL plus desktop, gamepad plus mouse/keyboard). Monetization and networking are out of scope.

Method note: this pass used about 20 web searches. Direct page fetches failed for icy-veins.com and pcgamer.com because those hosts are blocked in this session's network. So many findings come from search-result summaries of the cited pages, not full reads. Where a source was weak (SEO or mirror sites), it is flagged. Claims from general knowledge that I could not source are labeled "unsourced" in Inferences or moved to Gaps.

---

## 1. How do notable games handle interior decoration?

### Takeaway
The successful systems combine three things: a snap-by-default placement mode with an opt-in "free/advanced" mode, item placement on surfaces (tables, walls, ceilings), and a room/shell system that expands over time. World of Warcraft's 2025-26 housing is the current reference design: Basic mode has grid, snapping and 15° rotation steps, and Advanced mode is no-collision and free. Animal Crossing remains the reference for approachable gamepad decorating. The Sims and inZOI show the cost of maximal precision, which is tedium.

### Cited Findings

**World of Warcraft player housing (Midnight).** Early access opened Dec 2, 2025 for Midnight pre-purchasers. Midnight launched March 2, 2026.
- Early access opened in early December 2025 (Dec 2, some sources say Dec 3), for players who pre-purchased Midnight. Players without Midnight could visit Neighborhoods and collect decor but not claim a home — [Escapist](https://www.escapistmagazine.com/news-wow-player-housing-december-2025/); [GamesRadar](https://www.gamesradar.com/games/world-of-warcraft/world-of-warcraft-housing-finally-launches-in-early-access-this-december-for-midnight-expansion-owners-giving-blizzards-mmo-a-feature-seen-in-the-likes-of-final-fantasy-14-for-years); [Inven Global](https://www.invenglobal.com/articles/19943/world-of-warcraft-housing-nears-launch-with-early-access)
- Two decor modes, Basic and Advanced. Basic offers an optional visible grid with snap-to-grid points. The grid can be shrunk for precise arrangement on small surfaces such as tables. Items snap to surfaces by type (windows to walls, rugs to floors). Rotation is around the up axis in 15° snapping increments "so objects don't accidentally unalign" — [Warcraft Tavern decorating preview](https://www.warcrafttavern.com/wow/news/decorating-preview-for-player-housing-in-world-of-warcraft-midnight/); [Icy Veins Basic mode deep dive](https://icy-veins.com/wow/news/midnights-player-housing-a-deep-dive-into-basic-mode)
- Advanced mode turns off decor collision and allows free placement, scaling and floating objects. Per the in-game guide, "Snap to Grid" is the default, an "Enable Clipping" toggle lets decor pass through other decor, floors and walls, and Move/Rotate/Scale are separate gizmo toggles — [Icy Veins interior guide](https://www.icy-veins.com/wow/player-housing-interior-guide) (via search summary)
- Interior layout is room-based ("easily adjustable with just a few clicks"), and new room options unlock as house level rises. "Layouts" can be imported as a starting point. The day-one layout is intentionally simple: basic walls, some boxes, one or two lights — [Icy Veins housing guide](https://www.icy-veins.com/wow/player-housing-guide); [Icy Veins interior deep dive](https://icy-veins.com/wow/news/a-first-look-at-player-housing-interior-design)
- Budgets: interior decor budget grows with house level, from 910 points at level 1 to 2,050 at level 5. The exterior budget was raised from 200 to 250 at house level 3 after feedback. Blizzard said the 200 cap was set for performance — [Warcraft Tavern](https://www.warcrafttavern.com/wow/news/exterior-budget-increased-for-player-housing-in-world-of-warcraft/); [Icy Veins](https://www.icy-veins.com/wow/news/player-housing-exterior-budget-increased-by-25/); [Blizzard Watch, Dec 9 2025](https://blizzardwatch.com/2025/12/09/decorators-rejoiced-player-housing-exterior-budget-raised-250)
- The press framed the system as beating FFXIV's "dated furniture placement" — [PC Gamer headline](https://pcgamer.com/games/world-of-warcraft/wow-flexes-its-mmo-player-housing-system-in-a-new-blog-post-and-it-really-might-just-beat-ff14s-dated-furniture-placement-into-the-dirt). Blizzard saw players levitating whole houses through an unintended trick and kept it as a feature — [PC Gamer](https://www.pcgamer.com/games/world-of-warcraft/blizzard-sees-players-levitating-their-homes-in-wows-player-housing-even-though-there-werent-ui-controls-for-that-decides-its-now-a-feature-not-a-bug); [GamesRadar](https://www.gamesradar.com/games/mmo/world-of-warcraft-devs-were-going-to-fix-the-mmos-floating-houses-but-we-all-kinda-immediately-agreed-this-was-way-too-cool-to-change)
- A Blizzard API exists for per-category placement budgets (`C_HousingDecor.GetAllMaxPlacementBudgets`), which suggests budgets are tracked per category, not as one global count — [warcraft.wiki.gg](https://warcraft.wiki.gg/wiki/API_C_HousingDecor.GetAllMaxPlacementBudgets)

**Animal Crossing: New Horizons (2020; 2.0 and Happy Home Paradise DLC Nov 2021; Switch 2 Edition/3.0 2026)**
- Furniture can go on the floor, on walls, hung from the ceiling, outside on the ground, or on top of other furniture. Outdoor placement was new in New Horizons — [Nookipedia: Furniture](https://nookipedia.com/wiki/Furniture)
- You enter decorating mode by holding Down on the D-pad inside the house — [Game8](https://game8.co/games/Animal-Crossing-New-Horizons/archives/285206). Decorating uses an isometric-style overhead view that makes placement easier than in earlier games — [GamePro (DE)](https://www.gamepro.de/artikel/animal-crossing-moebel-platzieren-ganz-leicht,3354543.html). Hold A to grab and drag furniture. In the decorating view, A rotates — [Twinfinite](https://twinfinite.net/guides/animal-crossing-new-horizons-rotate-furniture-items/); [Hold to Reset](https://holdtoreset.com/animal-crossing-new-horizons-how-to-place-items)
- Happy Home Paradise added partition walls (reach the ceiling), decorative pillars (furniture can sit on them) and counters as room-structuring elements. Players use them for balconies, fake windows, window seats and open-plan dividers. Symmetry is hard with pillars because of their size. Counters unlock late (after about 15 vacation homes) and do not meet the wall — [Real Homes](https://www.realhomes.com/features/happy-home-paradise-pillars-counters-partition-walls); [Nintendo Life](https://www.nintendolife.com/guides/animal-crossing-partition-walls-how-to-add-partitions-counters-and-pillars-in-happy-home-paradise-dlc); [TheGamer](https://www.thegamer.com/acnh-happy-home-paradise-design-tips/)
- Ceiling lights came free to all players in update 2.0 — [Twinfinite](https://twinfinite.net/guides/animal-crossing-new-horizons-ceiling-lights-counters-partition-walls-pillars-soundscapes/)
- Home storage was 5,000 items before 3.0. Version 3.0 raised it to 9,000 — [Game8](https://game8.co/games/Animal-Crossing-New-Horizons/archives/574484) (via search summary; older pages still say 5,000)

**The Sims 4 (EA)**
- Per EA's official help, in Build Mode right-click rotates an object 45°, and the period/comma keys rotate clockwise/counter-clockwise. Free placement uses the cheats `testingcheats true` then `bb.moveobjects on` — [EA Help: How to rotate objects](https://help.ea.com/de/help/the-sims/the-sims-4/how-to-rotate-sims-4/)
- `bb.moveobjects` lets objects overlap and sit in spots that are normally blocked. Alt-drag is described as a modifier for finer, off-grid placement (sources conflict, see Gaps) — [weak source: SEO guide](https://upgrade.gitlab.khronos.org/?p=35285); [weak source](https://statamic.bigfinish.com/?p=64686)

**inZOI (Krafton; Early Access March 2025)**
- The build mode is praised for precision and detail: walls and partial walls at various heights. It is criticized as needlessly complex for basics. A YouTuber's comparison: a two-story house took about 40 minutes in Sims 4 and about 90 minutes in inZOI — [Mein-MMO](https://mein-mmo.de/en/fans-of-the-sims-are-trying-to-have-fun-at-inzoi-but-are-hitting-a-wall,1246391); [VGC review in progress](https://www.videogameschronicle.com/review/inzoi-review-in-progress/)
- Forum complaints: too many steps per action, tedious wall painting, requests for copy/paste of wall and floor materials, basements, wainscoting, more flexible roofs and smaller furniture. Fences vanish when other structures are built, so players place them last — [inZOI forum](https://forum.playinzoi.com/t/topic/9901); [inZOI forum](https://forum.playinzoi.com/t/topic/11903); [inZOI forum](https://forum.playinzoi.com/t/topic/13602); [Steam discussion](https://steamcommunity.com/app/2456740/discussions/0/599648491836300752)
- Builds can be saved as presets and imported into the live game — [The Games Wiki: home building guide](https://thegameswiki.com/inzoi/wiki/home-building-guide)

**Disney Dreamlight Valley (Gameloft)**
- Rooms and resizing unlock through Scrooge's quest "Dreamlight Valley Economy 101". The first interior upgrade expands the main room to 8×8. Up to 20 floors are possible, with up to 3 rooms branching from the central room on each floor, added through an elevator platform by the front door. Three exterior upgrades (about 2,000 / 20,000 / 75,000 Star Coins, sources disagree) also enlarge shared storage — [Nintendo Life](https://www.nintendolife.com/guides/disney-dreamlight-valley-how-to-upgrade-your-house); [GameSpot](https://www.gamespot.com/articles/how-to-upgrade-your-house-in-disney-dreamlight-valley/1100-6507184/); [Destructoid](https://www.destructoid.com/how-to-upgrade-your-house-in-disney-dreamlight-valley/)
- Players complain that "it feels as if every item has a grid of its own" during placement. Landscaping items can vanish if furniture or a character house is placed on top — [Game Rant](https://gamerant.com/disney-dreamlight-valley-furniture-problem/); [Dreamlight Valley Wiki: Furniture menu](https://dreamlightvalleywiki.com/Furniture_menu)

**Palia (Singularity 6, later Daybreak)**
- Housing is modular: pre-built room modules have connection points, with the Harvest House (11×11 m) as the hub. Rooms can be stacked into two floors. The build menu (H) has tabs for Preview, Building, Makers, Fence, Decor, Blueprints and Blocks. The plot item cap was raised to 3,500 (2026 guide). Extra plots cost gold — [HighGround Gaming](https://www.highgroundgaming.com/palia-housing-guide/); [Playnews 2026 guide](https://www.playnews.gg/en/guides/palia-building-your-dream-house-blueprints-decor-and-garden-2026); [Palia wiki: Housing Plot](https://palia.wiki.gg/wiki/Housing_Plot); [TechRaptor](https://Techraptor.net/gaming/guides/palia-building-guide)
- Reviewers enjoyed the decorating. The Switch version ran noticeably worse than PC — [Palia review blog](https://tickshok.blogspot.com/2025/05/palia-review.html)

**Stardew Valley** (weak sourcing)
- The first farmhouse upgrade unlocks renovations and exterior paint. Robin then sells the Furniture Catalogue — [Stardew wiki (HU)](https://hu.stardewvalleywiki.com/B%C3%BAtor); [design guide, weak](https://studio-asv.pages.dev/blog/stardew-valley/stardew-valley-house-upgrade-design-guide)
- Rotation is limited and many items (TVs, paintings, fireplaces, chests) cannot rotate. Sources disagree on rotation controls — [Gamezo](https://gamezo.gg/how-to-rotate-furniture-in-stardew-valley/)

**FFXIV housing**
- Item caps: forum posters cite roughly 150-300 items for small houses. Apartments are about 3/5 the size of a small plot but get half the item limit. Outdoor limits are a client rendering problem because every item in the ward is rendered. Wall partitions do not count as surfaces, and clicking a stack selects the wrong item first — [Square Enix forum](https://forum.square-enix.com/ffxiv/threads/443461-Housing-decoration-system-needs-adjustments); [Square Enix forum](https://forum.square-enix.com/ffxiv/threads/475242-Improve-Decorating-Features); [Massively OP 2018](https://massivelyop.com/2018/07/30/wisdom-of-nym-examining-final-fantasy-xivs-housing-issues-other-than-availability/); [Massively OP 2023](https://massivelyop.com/2023/02/02/some-assembly-required-my-journey-through-the-housing-system-of-final-fantasy-xiv/)
- A recent cap increase caused a bug that hid some indoor furnishings (display only) — [MMOHuts](https://mmohuts.com/news/final-fantasy-xiv-housing-bug-is-hiding-some-indoor-furnishings-after-the-item-cap-increase)

**Elder Scrolls Online housing**
- The housing editor has a Cursor Mode toggle (F) with three rotation rings for fine mouse rotation. With Cursor Mode off, the mouse wheel pushes or pulls the item. A newer "Precision Editing" mode (PTS Update 49) gives exact positioning. Item limits are per house size, with separate limits for normal and collectible items, and ESO+ subscribers get double — [UESP: Housing Editor](https://en.uesp.net/wiki/Online:Housing_Editor); [ESO forums PTS feedback](https://forums.elderscrollsonline.com/en/discussion/comment/6722842); [Cyberpost](https://cyberpost.co/how-do-i-get-to-the-housing-editor-in-eso/)

**Hello Kitty Island Adventure (Sunblink; Apple Arcade 2023, PC/Switch Jan 2025)**
- Putting items on tables arrived only in the first major content update. A reviewer called it "lifechanging" after the frustration of not being able to put a comic on a table — [PC Gamer](https://pcgamer.com/games/life-sim/hello-kitty-island-adventure-is-getting-its-first-major-content-update-today-including-a-new-area-more-visitors-and-the-ability-to-finally-place-items-on-tables)
- Decorating feeds gameplay. Visitor cabins have set-based goals (10 pieces of one furniture set per cabin for an achievement), and some visitors need specific furniture. A Furniture Customizer (recolor) arrived in update 1.4 — [HKIA wiki: Cabin Themes](https://hellokittyislandadventure.wiki.gg/wiki/Cabin_Themes_(Achievement)); [Siliconera](https://www.siliconera.com/how-to-customize-furniture-in-hello-kitty-island-adventure/); [Siliconera visitors guide](https://www.siliconera.com/hello-kitty-island-adventure-visitors-requirements-guide/)

**Bloxburg (Roblox)** (weak sourcing)
- In build mode, J changes grid size and K toggles object collision. The "Advanced Placing" gamepass adds micro-nudge and free rotation. Without it, off-grid items leave gaps — [Bloxburg fandom wiki](https://welcome-to-bloxburg.fandom.com/wiki/File:BasementsIcon.png); [fan guide, weak](https://welcome-to-bloxburg.pages.dev/controls/build-mode/)

### Inferences
- The industry has converged on "snap first, free on request": WoW Basic vs Advanced, Sims default vs `bb.moveobjects`, Bloxburg grid vs Advanced Placing, ESO standard vs Precision Editing. Porchlight should ship a snapped default with a "fine/free" toggle, not freeform-only.
- Surface placement (items on tables or shelves, wall-mounted, ceiling-hung) is a baseline expectation. HKIA's late addition of items on tables shows players notice its absence right away.
- Room-module interiors (WoW rooms, Palia modules, DDV rooms off a hub) suit a procedural town better than free wall-drawing (Sims, inZOI). They are faster, gamepad-friendly, and keep houses compatible with procedural exteriors.
- Unsourced background: ACNH's furniture grid is one tile per unit, with 45-degree-free 4-way rotation. Most items occupy 1×1, 1×2 or 2×2 tiles, and rugs sit on a separate layer. Verify on Nookipedia before quoting numbers.

### Gaps
- House Flipper (1 and 2), Unpacking, Minecraft, Adopt Me, Cozy Grove and Valheim were not researched in this pass because of the tool-call budget, so I have no sourced specifics for them.
- No sourced exact ACNH grid or rotation specs, and no Sims 4 specifics (grid tile size, 1/4-tile walls, Alt placement, multi-select).
- inZOI's specific build controls (grid sizes, rotation steps) are unconfirmed.

---

## 2. Grid vs freeform vs hybrid: snapping, surfaces, collision, and avoiding mess

### Takeaway
Hybrid wins: a grid plus angle snapping plus typed "sockets" (wall, floor, rug, surface, ceiling) keeps layouts tidy by default, and an explicit free/no-collision mode serves expert decorators. The "it just looks good" games (Townscaper, Tiny Glade) achieve beauty by removing low-level decisions: no grid, procedural detail, auto doors and windows. Some players find that restrictive.

### Cited Findings
- WoW: 15° rotation snapping prevents accidental misalignment. A shrinkable grid supports tabletop arrangement. Surface-type snapping applies (windows to walls, rugs to floors). Collision is on in Basic and off in Advanced — [Warcraft Tavern](https://www.warcrafttavern.com/wow/news/decorating-preview-for-player-housing-in-world-of-warcraft-midnight/); [Icy Veins interior guide](https://www.icy-veins.com/wow/player-housing-interior-guide)
- Sims 4: 45° default rotation. Overlap and blocked placement need the cheat `bb.moveobjects` — [EA Help](https://help.ea.com/de/help/the-sims/the-sims-4/how-to-rotate-sims-4/)
- Bloxburg: switchable grid size (J), collision toggle (K) — [Bloxburg wiki](https://welcome-to-bloxburg.fandom.com/wiki/File:BasementsIcon.png)
- FFXIV: placement-engine friction. Partitions are not surfaces, and overlapping items intercept clicks — [Square Enix forum](https://forum.square-enix.com/ffxiv/threads/443461-Housing-decoration-system-needs-adjustments)
- DDV: inconsistent per-item grids frustrate players — [Game Rant](https://gamerant.com/disney-dreamlight-valley-furniture-problem/)
- Tiny Glade (Pounce Light, Sept 2024): gridless drag-to-build walls (straight or curved). Doors usually appear automatically where paths meet walls, and windows merge when placed side by side — [PC Gamer review](https://pcgamer.com/games/city-builder/tiny-glade-review); [Casual Game Guides](https://casualgameguides.com/walkthroughs/tiny-glade/auto-connect-paths-walls-roofs). PC Gamer found it "can often feel restrictive rather than meditative", and Steam users ask for an optional snapping grid and more door and window control — [PC Gamer](https://pcgamer.com/games/city-builder/tiny-glade-review); [Steam discussion](https://steamcommunity.com/app/2198150/discussions/0/4844274022646200397/?ctp=3)
- The claim that Tiny Glade uses a customized wave-function-collapse variant is secondhand (an HN comment) and unconfirmed by the developers — [HN mirror](https://hn-4bl9qhoar-svelte.vercel.app/item/42190065)
- Townscaper (Oskar Stålberg): a reviewer said "no matter how hard I tried to... break the way my cities looked, everything still managed to look impressive". Stålberg calls it "more of a toy than a game". A Game Developer article explains the generation — [But Why Tho review](https://butwhytho.net/2021/09/05/review-townscaper-is-a-minimalists-dream-pc); [Game Developer: How Townscaper works](https://gamedeveloper.com/blogs/how-townscaper-works-a-story-four-games-in-the-making); [PC Gamer interview](https://pcgamer.com/townscapers-developer-on-how-its-radically-casual-design-is-inspiring-a-new-wave-of-low-stress-builders-to-adapt-the-blueprint)
- Enshrouded (Keen Games): a voxel building system praised as excellent. It automatically adds weathering and beams where stone and wood meet, a "procedural polish" pattern — [PC Gamer](https://pcgamer.com/mountain-keeps-hobbit-holes-and-sick-castles-enshrouded-players-are-hard-at-work-with-that-excellent-building-system); [GamePressure](https://www.gamepressure.com/newsroom/enshroudeds-base-building-system-looks-amazing/z25af6)
- LEGO Fortnite: snap mode "does half of the work for you" — [LEGO.com advanced building guide](https://www.lego.com/en-in/themes/fortnite/advanced-building). Yet reviewers call snapping "wonky" and not smooth, and players call building the weakest part of the game — [PCInvasion](https://www.pcinvasion.com/?p=405704); [GameSkinny](https://www.gameskinny.com/?p=139003); [Prima Games](https://primagames.com/?p=324542)

### Inferences
- For Porchlight, a practical hybrid: a 0.5 m floor grid (0.25 m or 0.125 m sub-grid when placing on surfaces, as WoW does), 15° rotation snaps (hold a modifier for 90° or free), and typed placement sockets (floor, wall, ceiling, surface-top, rug layer). Keep collision on by default and add a "free/clip" toggle for experienced decorators.
- "Procedural polish" (Enshrouded seams, Tiny Glade auto doors, Townscaper auto-detailing) matches a low-poly procedural town well. Auto-generate trim, eaves, porch steps and window frames so any player choice looks intentional.
- Rug and wall-art layers that never collide with floor furniture remove a big source of fiddliness.

### Gaps
- No primary developer source (GDC talk or post) on Tiny Glade's building algorithms was found.

---

## 3. Exterior customization: paint, part swaps, fences, yards, modular vs fixed shells, upgrades

### Takeaway
Most cosy games use fixed or semi-fixed shells with swappable styles plus decor budgets for yards. Exteriors are usually more constrained than interiors because they are rendered for everyone nearby (WoW, FFXIV). Upgrade tiers that unlock size, rooms and budget are the standard progression hook.

### Cited Findings
- WoW: exterior options were "fairly limited" at launch, with four themed decor kits at Midnight launch. Exterior lighting was disabled for client performance, and Blizzard planned to lift limits after 12.0. Some exteriors (e.g. elf) were not in early access — [Icy Veins housing guide](https://www.icy-veins.com/wow/player-housing-guide); [Warcraft Tavern](https://www.warcrafttavern.com/wow/news/charter-neighborhoods-elf-exteriors-not-available-for-housing-early-access/)
- WoW exterior budget: 200 rising to 250 at house level 3. Interior budget rises by house level. House level drives unlocks — [Blizzard Watch on house upgrades](https://blizzardwatch.com/2025/12/03/upgrade-house-world-warcraft/); [Warcraft Tavern](https://www.warcrafttavern.com/wow/news/exterior-budget-increased-for-player-housing-in-world-of-warcraft/)
- FFXIV: outdoor item limits are rendering-bound because the client renders every item in the ward — [Square Enix forum](https://forum.square-enix.com/ffxiv/threads/141090-Housing-Feedback)
- Palia: modular room pieces assembled on a plot, plus fences and a gardening layer — [HighGround Gaming](https://www.highgroundgaming.com/palia-housing-guide/); [Playnews](https://www.playnews.gg/en/guides/palia-building-your-dream-house-blueprints-decor-and-garden-2026)
- DDV: three paid exterior upgrades that also expand storage. Interiors grow through rooms and floors — [GameSpot](https://www.gamespot.com/articles/how-to-upgrade-your-house-in-disney-dreamlight-valley/1100-6507184/)
- Stardew: the first upgrade unlocks exterior painting and renovations — [design guide, weak](https://studio-asv.pages.dev/blog/stardew-valley/stardew-valley-house-upgrade-design-guide)
- ACNH: the house exterior is upgraded through Nook loans. Plot placement uses a housing kit footprint — [EssentiallySports](https://essentiallysports.com/animal-crossing-new-horizons-how-to-place-your-housing-kit-perfectly-esports-news)

### Inferences
- For a procedurally generated town, use a fixed lot and footprint with a parametric shell. Let players swap roof style, wall material and color, door, windows, porch and chimney from curated palettes, with procedural trim. Avoid free wall-drawing outside, which would break streetscape coherence and complicate the procedural layout.
- Separate exterior budgets (low, because everyone renders them) from interior budgets (higher, rendered only by visitors). This is WoW's and FFXIV's lesson, and it matters even more for WebGL.
- Yard and garden decor (fences, planters, paths, mailbox, porch light) is high-visibility social expression. Prioritize it in the catalog.

### Gaps
- No sourced details on ACNH exterior part swaps (roof, door, mailbox) or Sims 4 roof tools in this pass.

---

## 4. Gamepad vs mouse UX: cursor, camera, multi-select, copy/paste, blueprints

### Takeaway
Gamepad-first cosy games use a fixed or overhead decorating camera with grab-and-drag (ACNH). PC-first games add gizmos, precision rings and mouse-wheel depth (WoW, ESO). Presets, blueprints and layouts (WoW Layouts, inZOI presets, LEGO Fortnite blueprints, Palia Blueprints tab) are now common. Multi-select and copy/paste are the most-requested missing features.

### Cited Findings
- ACNH: hold Down to enter decorating mode, a near-isometric overhead view, hold A to grab, A to rotate — [Game8](https://game8.co/games/Animal-Crossing-New-Horizons/archives/285206); [GamePro](https://www.gamepro.de/artikel/animal-crossing-moebel-platzieren-ganz-leicht,3354543.html); [Twinfinite](https://twinfinite.net/guides/animal-crossing-new-horizons-rotate-furniture-items/)
- ESO: a cursor-mode toggle (F) with three rotation rings, and the mouse wheel pushes or pulls depth. Precision Editing added later — [UESP](https://en.uesp.net/wiki/Online:Housing_Editor)
- WoW: Move/Rotate/Scale gizmo toggles, plus importable Layouts — [Icy Veins interior guide](https://www.icy-veins.com/wow/player-housing-interior-guide)
- Sims 4: right-click or the ,/. keys rotate 45° — [EA Help](https://help.ea.com/de/help/the-sims/the-sims-4/how-to-rotate-sims-4/)
- inZOI: save builds as presets and import them into the live game. Players request copy/paste of materials — [The Games Wiki](https://thegameswiki.com/inzoi/wiki/home-building-guide); [inZOI forum](https://forum.playinzoi.com/t/topic/9901)
- LEGO Fortnite: blueprints guide construction. No edit mode at launch, so you destroy blocks with a pickaxe. Build mode is "two layers deep" and hard to exit — [LEGO.com sandbox guide](https://lego.com/themes/fortnite/getting-started-lego-fortnite-sandbox-mode); [PCInvasion](https://www.pcinvasion.com/?p=405271); [Prima Games](https://primagames.com/?p=324542)
- Palia: a dedicated Blueprints tab in the build menu — [HighGround Gaming](https://www.highgroundgaming.com/palia-housing-guide/)
- ACNH: players request multi-select for storage and inventory. Storing is one item at a time. Bulk crafting reportedly arrived with the Switch 2 Edition update — [iMore](https://www.imore.com/improvements-we-want-see-animal-crossing-new-horizons); [Twinfinite QoL list](https://twinfinite.net/2020/05/13-qol-changes-for-animal-crossing-new-horizson/); [Game8](https://game8.co/games/Animal-Crossing-New-Horizons/archives/574484)
- DDV players advise moving items one at a time for control — [Game Rant](https://gamerant.com/disney-dreamlight-valley-furniture-problem/)

### Inferences
- Recommended Porchlight decorate mode: enter from inside the house. Switch to a cutaway, three-quarter overhead camera (orbit in 45° or 90° steps, zoom), with walls facing the camera faded. Interior navigation in third person is awkward on gamepad.
- Gamepad: left stick moves a snapped cursor (a stick-driven reticle on the grid, not a virtual mouse). A grabs/places. LB/RB rotate in 15° steps (hold for 90°). Y opens the catalog. X stores to inventory. A d-pad press cycles stacked items under the cursor, which fixes FFXIV's click-priority problem. A trigger held enables fine/free mode.
- Mouse/keyboard: click-drag, wheel or Q/E rotate, Shift for free, Ctrl+Z / Ctrl+Y undo/redo, Alt-drag duplicates (Sims convention), box select for multi-move.
- Build in from day one: undo/redo history, "pick up a whole group or room", save/load room layouts as blueprints, and copy color/material (eyedropper). These answer the most consistent complaints across inZOI, ACNH and LEGO Fortnite.
- Keep the mode one layer deep with a single clear exit (LEGO Fortnite's lesson).

### Gaps
- WoW's gamepad support in housing and its undo implementation could not be confirmed, because Icy Veins and PC Gamer were blocked.
- No sourced detail on ACNH or Sims undo, or Sims multi-select ("move room" and "copy").

---

## 5. Common complaints and pitfalls; widely praised innovations

### Takeaway
The recurring complaints are item caps and budgets (FFXIV, WoW, ESO), storage limits (ACNH, WoW), inconsistent or invisible grids (DDV), clunky edit flows with no move/delete or too many steps (LEGO Fortnite, inZOI), and no surface placement (HKIA before its update). The praised innovations are dual-mode snapping (WoW), room partitions and pillars (ACNH HHP), procedural "always pretty" building (Townscaper, Tiny Glade, Enshrouded), and responsive tuning of budgets.

### Cited Findings
- FFXIV item caps are the top complaint (a "worst in genre" placement system per Massively OP). Apartments get half a small house's cap despite 3/5 of its size — [Massively OP](https://massivelyop.com/2023/02/02/some-assembly-required-my-journey-through-the-housing-system-of-final-fantasy-xiv/); [Square Enix forum](https://forum.square-enix.com/ffxiv/threads/475242-Improve-Decorating-Features)
- WoW: players called the exterior cap too low, so Blizzard raised it 25%. Room budgets vary by room size, and identical chairs can cost 1 vs 4 points, which reads as inconsistent. Players also complain about the 7,500-item storage cap — [Warcraft Tavern](https://www.warcrafttavern.com/wow/news/exterior-budget-increased-for-player-housing-in-world-of-warcraft/); [Blizzard forums: decor cap](https://us.forums.blizzard.com/en/wow/t/decor-cap-too-low-for-housing-budget/2342181); [Blizzard EU forums: storage 7500](https://eu.forums.blizzard.com/en/wow/t/housing-storage-limit-still-at-7500-ridiculous/602459); [Mein-MMO](https://mein-mmo.de/en/housing-in-wow-ist-ein-voller-erfolg-nur-eine-sache-nervt-hart,1541007)
- ESO: limits are too low, and devs cite performance — [ESO forums](https://forums.elderscrollsonline.com/en/discussion/comment/3705073/)
- ACNH: storage limits (5,000 rising to 9,000) and one-at-a-time actions — [Game8](https://game8.co/games/Animal-Crossing-New-Horizons/archives/574484); [iMore](https://www.imore.com/improvements-we-want-see-animal-crossing-new-horizons)
- DDV: placement grid inconsistencies, and landscaping is deleted silently under furniture — [Game Rant](https://gamerant.com/disney-dreamlight-valley-furniture-problem/)
- inZOI: precision costs speed (90 vs 40 minutes for the same house) — [Mein-MMO](https://mein-mmo.de/en/fans-of-the-sims-are-trying-to-have-fun-at-inzoi-but-are-hitting-a-wall,1246391)
- LEGO Fortnite: no edit/move mode, wonky snapping — [Prima Games](https://primagames.com/?p=324542); [PCInvasion](https://www.pcinvasion.com/?p=405704)
- HKIA: items on tables praised as "lifechanging" when added — [PC Gamer](https://pcgamer.com/games/life-sim/hello-kitty-island-adventure-is-getting-its-first-major-content-update-today-including-a-new-area-more-visitors-and-the-ability-to-finally-place-items-on-tables)
- WoW: press broadly praised the richness and sophistication of housing. Blizzard embraced an emergent exploit (floating houses) — [GamePressure](https://www.gamepressure.com/newsroom/blizzard-surprised-wow-players-with-richness-and-sophistication-o/z27b27); [GamesRadar](https://www.gamesradar.com/games/mmo/world-of-warcraft-devs-were-going-to-fix-the-mmos-floating-houses-but-we-all-kinda-immediately-agreed-this-was-way-too-cool-to-change)
- Tiny Glade and Townscaper are praised for stress-free building that always looks good. Their trade-off is limited control — [Aftermath](https://aftermath.site/tiny-glade-is-a-lovely-stress-free-builder/); [PC Gamer](https://pcgamer.com/games/city-builder/tiny-glade-review)

### Inferences (lessons for Porchlight)
1. **Budgets as weighted points, communicated clearly.** Use per-category budgets with a visible meter, with costs tied to visible complexity (mesh or draw-call cost), and make sure identical items cost the same. Be generous inside and stricter outside. Plan headroom for WebGL.
2. **Make storage effectively unlimited, or very large and categorized.** Storage caps are a constant irritant and have no design value in a cosy game. Use a catalog model: owned items are re-placeable and infinitely re-orderable, as in ACNH's catalog idea.
3. **Snap by default, free on demand,** with 15° rotation snaps and sub-grids on surfaces.
4. **Typed sockets and layers:** floor, wall, ceiling, surface-top, rug. Never silently delete items. Warn and return them to storage.
5. **Undo/redo, multi-select, group move, room-layout blueprints, and material eyedropper/copy-paste** from day one.
6. **Procedural polish** for exteriors and structural pieces, so low-poly homes look intentional (Townscaper and Tiny Glade lesson). Still offer optional control (the Tiny Glade complaint).
7. **Room modules on a fixed footprint** for interiors, unlocked through house upgrades (WoW, Palia, DDV). This fits the procedural town and gamepad.
8. **Give decorating a purpose:** set bonuses, visitor requests, neighbor showcases (HKIA cabins, HHP clients). This suits a multiplayer town with missions.
9. **One-layer-deep mode with a clear exit,** and a camera that switches to cutaway overhead for decorating.
10. **Starter layouts and templates** (WoW imported Layouts, inZOI presets) to defeat the blank-room problem for casual players.

### Gaps
- Not covered with sources: House Flipper's praised cleaning and painting tactility, Unpacking's narrative placement (items "belong" in certain places), Minecraft and Valheim free-building lessons (Valheim structural integrity), Adopt Me and Bloxburg community sentiment, Cozy Grove decorating, and Sims 4 specific complaints (e.g. lot size limits, the 64×64 maximum lot). These would need a follow-up pass.
- Blocked hosts (icy-veins.com, pcgamer.com) prevented full reading of the most detailed WoW sources. For deeper WoW specifics, those hosts would need to be allowed in the session's network settings.
