# Base layout: fewer ways out

_Session 18 (2026-09-27): designed, approved by the user with §8's answers, built and checked in the game (§9, built
0 errors / 0 warnings, not committed). Replaces the first draft (halls only, now deleted) after the user's feedback._

## 1. Goal
The user: rooms get added "box after box", each with its own door to the outside. Instead:
- **Fewer doors to the outside world.** Every outside door lets heat or cold in, depending on the season, and a base
  with a door in every room is hard to defend.
- **Rooms open inside the base:** into a shared room, or into a neighbouring room that doesn't mind people walking
  through. That shared space isn't necessarily an empty hall. A big gap between rooms can become a **common room**
  (a rec room with dining tables, for example) that other rooms open into.
- **Rooms fine with foot traffic can serve as halls.**
- **The shape follows the base.** Zones are never touched (fields sit on the most fertile soil), so the shared room
  bends around them. There are no fixed widths, and oddly shaped rooms are fine.
- Double walls between rooms are fine as they are.

## 2. What the last run shows (`aipc_run17_day14`, the test case)
**Nothing in this design is specific to this map.** Everything works from what the current base has: its doors, walls,
rooms, zones and free ground. Run 17 is just the base the checks run on first, with a fresh quicktest colony second.

Read live from the game; north is up. `#` wall, `D` door, `w`/`d`/`f` blueprints or frames (wall, door, furniture),
`X` furniture, `,` roofed floor, `g` rice field, `s` stockpile, `t` tree, `c` rock chunk.
```
 143 .....wwwwwww......................   hospital (frames), door d opens west, outdoors
 138 .....d.....w......................
 137 .....####################.........
 136 .....#,,,,,#XXXXX##,XXXX#..t......   dining room | barracks | kitchen
 132 ...t.#,,,,,#,,,,,##,,,,,######.....
 131 .....##D########D##D#####XXXXX.....   3 doors in a row, all outdoors
 130 ....cc........gggggggggg#ssssX.....   the rice field starts right at the doors
 128 ...cc...ssssssggggggggggD,sssX.....   storeroom door, outdoors, onto the field
 126 ........ssssssgggggggggg######.....
 124 ....cwww##D#ssssssgggggggggg.......   workshop, door north, outdoors
 118 .....#######..........ccc..........
     4567890123456789012345678901234567    (x 144-177)
```
6 rooms, 6 doors, **6 ways out**. It's a 100-110°F desert: rooms are "too hot" and the colony had heatstroke. The minds'
field (x158-167, z121-130) and stockpile (x152-157, z123-128) fill the ground in front of the barracks, kitchen and
storeroom doors. West of the base is free apart from a tree and some chunks.

## 3. Vanilla facts this rests on (decompiled 1.6)
- **Doors and temperature:** while a door is open, the rooms on either side even out their temperature every 34 ticks
  (`Building_Door.Tick`, `doorTempEqualizeIntervalOpen`, rate 1). Closed doors leak every 375 ticks. A roofed room
  drifts toward the outdoor temperature about 14× slower than open ground (`RoomTempTracker`: thin roof 5e-5 against
  7e-4). So a door that opens into another room trades air with that room, not with the weather.
- **Rain:** "soaking wet" only happens on unroofed cells (`Pawn_MindState`, `weatherThought`).
- **Which rooms mind foot traffic** (a rule from vanilla's own effects, no room list):
  - **beds:** walking makes movement noise, and a sleeper who hears it gets "sleep disturbed"
    (`Pawn.HearClamor` → `CheckForDisturbedSleep`). This covers bedrooms, barracks and hospitals;
  - **cleanliness that matters:** pawns track filth in, and vanilla ties room cleanliness to food poisoning
    (`RoomStatDefOf.FoodPoisonChance`, where meals are made and eaten) and to infection (`InfectionChanceFactor`, where
    patients lie). This covers kitchens and hospitals;
  - everything else (storeroom, workshop, dining room, rec room, an empty room) is **walk-through**.
  - So a room is walk-through when it has no bed and no meal source. The dining room is walk-through (vanilla doesn't
    score it by traffic); the kitchen isn't.
- **Doors can't replace walls through vanilla's replace system:** a wall's `replaceTags` is `Wall`, a door's is
  `Door`. A door blueprint *can* go on a player wall (`canPlaceOverWall`). A wall can't go on a door without
  deconstructing it first.
- **Roof support:** up to 6.9 cells from a wall or column (`RoofCollapseUtility.RoofMaxSupportDistance`). Auto-roof
  covers any enclosed room.
- **Worldgen does it this way too:** `RoomLayoutGenerator` lays corridors first, rooms either side, one door per pair
  of neighbouring rooms, and only `entranceCount` doors outside. It spawns real things, so we copy only the rule.

## 4. The idea in one line
**Every room reaches the base's inside, and the base keeps only the ways out that walking needs.** Code measures the
ways out, and offers the minds the cheapest ways to bring doors inside. A mind picks one, as it picks a room today.

## 5. Design

### 5.1 The measure: ways out
A **way out** is a colony door between an indoor room and the outdoors. A room **reaches inside** when one of its
doors opens into another indoor room (walk-through or common), directly or through other rooms. Code keeps a live
count:
- the base line in `[Colony]`: `… · 6 ways out (every room has its own)`, or later `· 2 ways out`. It shows the state
  only; the minds weigh it (the user's rule: show the state);
- used by every score below.

### 5.2 Three ways to bring a door inside
Code looks for these; each becomes a Base call line with its real cost.

**(a) A door between neighbours.** Two rooms share a wall (one wall between them, not a double wall), and at least one
of them is walk-through. A door goes into the shared wall (a door blueprint on a player wall is vanilla). *"Connect
the kitchen to the dining room: 1 door."* It costs almost nothing, so it's the first thing offered.

**(b) A common room** in the gap between rooms, shaped to it (§5.3). The doors whose outside cell falls inside it
now open into it. What it's for depends on its size (§5.4): a plain hall, or a dining/rec room others walk through.

**(c) Close a way out** that nothing needs any more (§5.5).

End rooms (bed or meal source) only ever open onto a walk-through or common room, never *through* each other. A door
between two end rooms (kitchen ↔ barracks) is never offered.

### 5.3 A common room shaped to the base
New `Building/CommonRoomFinder.cs`, using `SiteFinder`'s grids:
1. **Seeds:** the outside cells of doors that open outdoors, among doors that could share one space (connected over
   free ground within the base's scan area).
2. **Free ground:** outdoors, walkable, no building, blueprint, frame or interaction spot, and **no zone. A cell next to
   a zone is free ground only if the wall doesn't have to go on the zone.** The wall ring has to go somewhere, so the
   room keeps one cell clear of a field and its wall runs along the field's edge. Trees and items count as clearing,
   as for rooms.
3. **Join the seeds:** the shortest paths between them over free ground (a BFS tree). This is the room's spine and
   may bend.
4. **Fill the pockets:** add any free cell next to the room that doesn't make the wall longer, i.e. it has at most
   one free neighbour outside the room. Repeat until nothing changes. This closes the notches between rooms and follows
   the base's shape, with no set width. It never grows into open ground (that would add walls).
5. **Wall ring:** every border cell that isn't already a player wall or door gets a new wall. It must be buildable
   (V6/V8 cell rules). If it isn't, drop the seed whose path caused it and try again.
6. **Roof:** every cell within 6.9 of the ring, else it's not offered. (Columns are a later option.)
7. **Pick the doors:** try the room with every seed, then drop the seed that saves the most walls per door, and keep
   the best score: `doors brought inside × insideDoor − new walls` (`insideDoor` default 8, `Tuning/building.txt`).
   Offered when it brings **2+ doors** inside.
8. **Way in:** if a door on its ring already leads outdoors (a walk-through room's outside door), none is added.
   Otherwise one new door on the ring, where the walk from the fields, stockpiles and base centre is shortest (the
   same rule as `ChooseDoor`).
9. **Validator:** `RoomValidator.CheckCommon`: enclosed (V2 flood fill over the shape), new cells buildable (V6),
   nothing cut off or on a long detour (V7's walk), and vanilla's placement check (V8).

### 5.4 What the common room becomes (the weighing)
Once the shape is known, code keeps its **walkways** clear: the shortest paths between every pair of its doors (they
must stay walkable). It then asks what the **free space left over** can hold, using the existing room kinds and the
placer (`RoomPlacer`, extended to a cell set instead of a rect):
1. **The ladder's next rung, if it's a walk-through kind** (a dining room, a workshop, a storeroom): if its items fit
   in the space, the common room is offered **as** that rung. *"Next for the base: a dining room — as a common room the
   barracks, kitchen and storeroom open into (table for 6 fits; 3 doors come inside)."* It's also offered as a plain
   box, as today, so the mind chooses.
2. Otherwise **a walk-through kind the colony lacks** ("Other rooms": a rec room once the recreation items are
   buildable, a dining room…). A combined dining + rec room is fine: vanilla picks one role from what's inside, and
   both uses keep working.
3. Otherwise a **hall** (no furniture). It can be upgraded later like any room, since the Upgrade call already sees it.

No walk-through kind is special-cased: walk-through comes from §3's rule, fit from the kind's items, and need from the
ladder or "Other rooms".

### 5.5 Closing ways out that aren't needed
Once a room reaches inside, its own outside door may be surplus. **Keep** a way out when closing it would make a
frequent walk (to a field, a stockpile, a work spot, or the base centre from outside) a long detour. That's the same
detour test as V7 (`DetourAllowance`), so no set number of entrances. Otherwise it's offered: *"close the kitchen's
outside door (it opens into the common room now)."*
How to close it is §8 (b). Only doors minds built are ever closed; the player's doors never are.

### 5.6 Growth: new rooms open inside
- **Door choice** (`ChooseDoor`): a door onto a walk-through or common room beats an outdoor one. That's
  `insideDoor` against walking distance, so a much longer walk can still win out. End rooms prefer walk-through
  neighbours.
- **Site score** (`Score`): `insideDoor` when the door opens inside, **`wayOut` (default −6) for a new way out**. A site
  against the common room's wall, or against the dining room's wall with the door put in it, now beats open ground.
  The site reuses that wall (the existing shared-wall terms).
- **A room whose door has to go outdoors** prefers a door next to other outdoor doors' outside cells, so a common room
  can take them in later (`hallReady`, default 3).
- V3 already lets a door open into a hall. It widens to "into any walk-through or common room".

### 5.7 How the minds see it
- **Base call, new group "Inside the base"**, at most 2 lines, the best by score:
  ```
  Inside the base (6 ways out now)
   2: connect the storeroom to the dining room (1 door; 4 wood)
   3: a common room the barracks, dining room and hospital open into (hall, 14 walls and a door; 95 wood or 95 granite blocks; 3 fewer ways out)
  ```
  A common room that fits the ladder's rung shows under "Next for the base" instead (§5.4). The reply takes a
  material as for rooms. Code finds it again on reply and places it as a normal project (`BuildProject`, kind
  `AIPC_Common` or the rung's kind). Missing materials get marked (`Supplies.MarkFor`).
- **Prompts:**
  - `guidance.txt`: `- Every door to the outside lets the weather in when someone walks through it, and every way in
    is one more to hold when raiders come. Rooms that open inside the base, into a shared room or a room people can
    walk through, keep their temperature and are easier to defend. Bedrooms, the kitchen and the hospital shouldn't be
    walked through.`
  - `base.txt`, last paragraph: "…better rooms lift everyone's mood; fewer ways out keep the heat or cold out and the
    base easier to defend."
  - Each line carries its numbers (doors brought in, ways out, cost), so the choice is concrete.

### 5.8 What stays as it is
Zones (never touched, and no new rule keeps ground free for later). Double walls. Existing rooms (no knocking down,
no moving). Room sizes and the ladder. Defense structures (out of scope).

## 6. Storyboard (Scenes 36-41; three minds, no player)

### Scene 36: two rooms, one door between them
Day 3, a fresh colony. The barracks and kitchen stand with their doors outdoors. Sab's Base call has "Next for the base:
a storeroom". Site A shares the kitchen's east wall. `ChooseDoor` can't use the kitchen (an end room), so the door
goes outdoors, next to the kitchen's door (`hallReady`). Laid out.
> Under the hood: §5.6. The storeroom is walk-through (no bed, no meal source).

### Scene 37: the gap becomes the dining room
Day 5. Three doors open onto the ground in front of the rooms; this colony's field lies further south. The ladder's
next rung is a dining room. Valentin's Base call:
```
Next for the base: a dining room.
 1: a dining room (5x5: table, 6 chairs; 80 wood)
 2: a dining room as a common room the barracks, kitchen and storeroom open into (odd-shaped, 26 cells; table, 6 chairs; 3 fewer ways out; 110 wood)
```
He picks 2: *"If everyone walks through the dining room, nobody lets the heat into the kitchen."*
```
 131 ..##D####D####D##...    barracks, kitchen, storeroom doors
 130 ..N,,,,,,,,,,,,,N...    , = the common room; its shape stops above the field
 129 ..N,,hhThh,,,,,,N...    table and chairs off the walkways
 128 ..NNNENNNNNNNNNNN...    E = its one door out; N = new walls
 127 ..gggggggggggggggg..    the field, untouched
```
> Under the hood: §5.3 (spine along the doors, pockets filled, one cell clear of the field), §5.4 (the rung fits in
> the space off the walkways).

### Scene 38: closing what isn't needed
Day 7. The kitchen's outside door is now surplus: the stove is 3 steps from the dining room door, and the field is
reached through the dining room's door out. The Base call offers "close the kitchen's outside door (it opens into the
dining room now)". Wehner takes it. The kitchen holds its temperature when the cook comes and goes (to be measured,
§7 build step 5).

### Scene 39: new rooms open inside
Day 9. Sab's hospital: site A is against the dining room's north wall, with its door put into that wall. It beats an
open site 4 tiles closer because of `insideDoor` (+8) and no new way out (−6 avoided).

### Scene 40: an old base, a wonky room
The run-17 base (§2), where the field reaches the barracks, kitchen and storeroom doors. Those three can't take part
(their outside cells are field), so the finder uses the doors it can: the dining room's (151,130), the hospital's
(148,138) and the workshop's (150,125). The spine runs from the workshop door north along the dining room's west wall
to the hospital door. It bends, and it keeps one cell clear of the stockpile at x152 so the wall runs along the
stockpile's edge. The tree and chunks just get cleared. The line reads: "a common room the dining room, hospital and
workshop open into (hall, odd-shaped; 3 doors come inside, 1 new door out: 2 fewer ways out)". The barracks and
kitchen stay as they are until a neighbour door (§5.2a) or a room north of them gives them a way inside. (Ground
checked live; the exact shape and wall count come from build step 1.)

### Scene 41: the player says no
The player cancels a wall blueprint of the common room. The project is off, as for rooms, and it's remembered. The
line can come back in a later Base call if it still fits.

## 7. Build steps (each checked before the next)
1. **Build step 1: the measure and the finders, dev only.** Ways out, walk-through rooms (§3's rule), neighbour doors
   (§5.2a), `CommonRoomFinder` (§5.3), text maps in the dev log, and a dev button "Layout options". Check on
   `aipc_run17_day14` and a quicktest colony: sensible shapes, the validator passes every one, nothing on zones.
2. **Build step 2: the Base call lines and placing.** "Inside the base" lines, neighbour doors and a plain-hall common
   room as projects. Check with "Base call now": pick → blueprints match the text map → built, roofed, and the ways-out
   count drops.
3. **Build step 3: what the common room becomes.** Walkways, the cell-set placer, the rung "as a common room" line.
   Check: the dining rung offered both ways; the common room finishes with vanilla's dining room role.
4. **Build step 4: growth.** `insideDoor`, `wayOut`, `hallReady` in `ChooseDoor`/`Score`; V3 widened. Check: new rooms
   open onto walk-through rooms when one is near.
5. **Build step 5: closing ways out, prompts, `[Colony]`,** then a short fresh-colony run with skip-time tools: are the
   lines picked, does the ways-out count go down, and does a room's temperature hold better behind an inside door (read
   `room.Temperature` of both rooms)?

## 8. Decisions (the user's answers, session 18)
Answered: (a) the walk-through rule is right; (c) one project at a time is fine; (d) no ground kept clear in front of
doors. Added by the user: **people must always have a way out**. (b) wasn't answered, so the recommendation was built
(wall it in, only doors our projects placed).
- **(a) Walk-through rule:** no beds and no meal source (from vanilla's sleep-disturbed and cleanliness effects). OK,
  or should the dining room count as an end room too (it's scored on cleanliness, but vanilla ties no sickness to it)?
- **(b) Closing a way out:** replace the door with a wall (a deconstruct designation, then a wall blueprint: two
  steps, the colony does both; best for defense), or just **forbid** it (vanilla's toggle: colonists stop using it, so
  it stops letting air in; free and reversible, but raiders still see a door)? Recommended: wall. Only doors a mind
  built.
- **(c) One project at a time:** a common room counts as the mind's project (like a room), so she can't also start the
  next room until it's done. OK?
- **(d) Removed from the first draft:** keeping two rows clear of new fields (you said fields go where the soil is
  best, so the shape adapts instead), and the 1-wide halls question (the shape decides).

## 9. Built and checked (session 18)
Everything in §5 is built except `hallReady` (not needed: with inside doors preferred, rooms already cluster, see the
runs below). Code: `Building/Layout.cs` (ways out, walk-through rule, doors between neighbours, surplus ways out, the
Base call lines), `Building/CommonRoomFinder.cs` (the common room, its furnished variant, text maps), plus changes in
`SiteFinder` (inside doors, the walk limit), `RoomValidator` (V3), `BuildManager` (layout projects, closing a door,
the door record), `BaseCall`, `Ladder.Line`, `RoomPlacer.Place(keepFree)`, `RoomKinds.xml` (`AIPC_CommonRoom`,
`AIPC_Doorway`, `AIPC_ClosedDoor`), `Tuning/building.txt` (`insideDoor` 8, `outdoorWalk` 20), both prompts.

**The way-out rule (the user's "people need to get out"):** `outdoorWalk` (20 tiles) is the most any room's door may be
from the outdoors, walking through the base but never through a room that shouldn't be walked through. It applies to:
- **a common room:** it gets a second (up to a third) door out when a far door would be over it;
- **closing a way out:** refused if any room would end up over it;
- **a new room's door:** it only goes into a walk-through room within it.

**Deviations from §5, found while testing:**
- **A common room's door out may sit at a corner** (a door needs no wall on both sides; a straight stretch is preferred).
  The street base's only plan needed it.
- **Scoring counts ways out saved** (doors brought in minus new doors out) × `insideDoor`, minus new walls, and only
  plans that score above 0 are offered (a 15-wall room to save one door isn't).
- **Doors between neighbours only go into a walk-through room** (the room whose outside door will close walks through
  the other), and the outdoor walk never passes through an end room. The first version offered "connect the dining room
  to the barracks", which would have sent the dining room's traffic through the barracks.
- **A durable record of our doors** (`BuildManager.ourDoors`, saved; filled from old projects on load): a project whose
  builder died is dropped on load, and with it the knowledge that the barracks door was ours.
- **Bug fixed (would hit real play):** a new room's door replacing an older, still-active project's wall made that
  project read the wall as "cancelled by the player" and abandon itself. A wall another room's door has taken counts
  as built now (`BuildProject.Built`).
- **1-cell common rooms** happen: two outside doors a cell apart get closed in with 1 wall and a door, which is an
  airlock. Kept: it's cheap, and airlocks are what RimWorld players build for temperature.

**Dev tools** (debug actions, "AI Pawn Control"): *Layout options* (ways out, rooms, doorways, surplus, common rooms
and their furnished variants with text maps and a try-by-try trace), *Build best layout option* / *Place best layout
option* (instant or as blueprints), *Grow test base* (the ladder's rooms, each finished at once) and *Grow test base
with layout* (the same, taking the rung as a common room and every layout option, as if a mind always picked them),
*Project status*. *Place site A now* now tries sites A, B, C in turn.

**Checks (all forced with the dev tools, no long runs):**
| Check | Result |
|---|---|
| Street base (`aipc_layout_boxes`: 8 boxes in 2 rows, all doors outdoors) | One common room along the street: **1 wall and 1 door, 8 doors inside, 11 → 4 ways out** (the other 3 are quicktest's pre-built ruin). Built instantly: an enclosed, roofed 37-cell room; screenshot checked. Its furnished variants don't fit (every door would be over 20 tiles from a way out), which is right |
| Fresh colony, grown with layout (`aipc_layout_fresh`, `aipc_layout_fresh2`; a third map stopped at 4 rooms because *Place site A now* only tried site A, fixed) | 9 rooms, **1 way out** each time. The storeroom is built *as* the common room the barracks and kitchen open into (walkway plus shelves), later rooms open into the dining room, workshop or storeroom, and a final airlock joins the last two outside doors. Screenshots checked: compact blocks, not boxes in a row |
| Project tracking | Every room, the furnished common room (vanilla role: storeroom, 28 cells), the doorway and the airlock reach "done"; outfitting gives the storeroom a 13-cell stockpile off the door fronts |
| Run-17 base (`aipc_run17_day14`) | 4 ways out; no common room fits (the minds' field and stockpile sit at the doors). Offered: "connect the barracks to the dining room; then its outside door can be closed". Built → the barracks' outside door became surplus → *Place best layout option* ordered it deconstructed; **Willis deconstructed it and the wall blueprint followed** (vanilla jobs). 3 → 2 ways out (the storeroom was still unfinished in that load) |
| Base call with a mind (Valentin, LLM) | The prompt shows `· 4 ways out`, an "Inside the base" group and "its door opens into the dining room" on site A. Valentin (downed, bleeding) picked the hospital at site B |

**After the user's first dev run (`aipc_run18_live`):** the minds offered and picked doorways into pockets, sealed
cells no room was meant to have (a roofed cell between the barracks and water; the 2-3 cells a rec room closed in front
of the workshop door). Fixed:
- **V9, no pockets:** a new room may not wall in open ground reached from outside today (`RoomValidator.WalledIn`;
  sites failing only V9 are dropped without the "site-finder bug" warning).
- **Doors only into real rooms** (`Layout.RealRoom`: a base room with a role, one our projects built, or one joining 2+
  doors), both for doorways and for a new room's door.
- **Costs on the layout lines** ("…: 25 wood or 25 granite blocks").
- The `[Work waiting]` materials sum skipped install blueprints (vanilla logged an error for each Reflect).
Checked: the grow test still gives 9 rooms with 1 way out; the pocket doorways are no longer offered on the live run.

**Not checked yet (for the next run):** a mind actually picking a layout line; a common room built by colonists in real
time (only instant builds so far); temperature behind an inside door vs an outside one (§7 build step 5's reading).

