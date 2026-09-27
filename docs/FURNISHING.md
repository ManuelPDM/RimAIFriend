# Furnishing build: storage, rooms from vanilla concepts, room upgrades, work by colony need

_Approved by the user (session 15, 2026-09-27); being built. Replaces STREAMLINE.md §11 (room improvement)._

## 1. Goal
The early game, done properly and future-proof: the base grows by the villagers' own choices, and it keeps up as
research unlocks things and mods add furniture, **with no item lists to maintain**. Scope: storage, what goes in a room,
upgrading rooms, and minds steering work priorities. Out: special rooms, ladder levels, defense, research choices,
plumbing (a later "complex systems" phase). **No power rule anywhere:** code picks the best available option; power is
the user's.

## 2. Decisions (the user, session 15)
- Temperature: the best available heater/cooler, powered or not. No pre/post-power split.
- Stone blocks: no code fix. Minds steer it through work priorities and talk (§6).
- Upgrades are automated in the *doing* (code picks and places), but each one is a mind's choice (§5).
- "What's outside": only uncollected items that matter, summarised; never a list of every corpse or item (§3).

## 3. Neat storage
- **The ladder's storage rung needs a real storeroom** (vanilla's Storeroom role, `RoomRoleWorker_StoreRoom`), not
  "any roofed stockpile that takes food". Run 3: the kitchen's own food stockpile passed the old check, so no storeroom
  was ever built and everything else sat outside.
- **Stockpiles only in storerooms** (the user, session 15): `Outfitting` no longer puts a food stockpile in the kitchen
  (food piled on the kitchen floor doesn't work). A finished storeroom gets its shelves plus a stockpile on its free
  floor, automatically. Before the first storeroom, the Base call's existing Storage line ("a stockpile for
  everything", only while the colony has none) stays.
  Vanilla only calls a room a storeroom when it holds `Building_Storage` (shelves; `RoomRoleWorker_StoreRoom`), so the
  storeroom kind keeps its shelves; a floor stockpile on the free cells is extra.
- **`[Colony]` gets one "waiting to be hauled" line**, from vanilla's `map.listerHaulables.ThingsPotentiallyNeedingHauling()`
  (what vanilla itself thinks needs hauling: not already in its best storage, not forbidden, so a raid's dropped gear
  stays out until someone unforbids it), on home-area cells only:
  - resources and food **by name with amounts**: `waiting to be hauled: wood 240, steel 75, rice 30`;
  - everything else **one count per vanilla top-level category**: `…, 12 corpses, 9 apparel, 4 weapons`;
  - left out when empty. Enough for a mind to say "someone should haul" or raise its own hauling (§6).

## 4. Room kinds from what things do
Today `RoomKinds.xml` names items by defName (`Bed`, `EndTable`, `FueledStove`, `Shelf`…), so research and mods never
reach the minds. Instead, a room item names a **need**; code finds every def that meets it:

| Need | Vanilla concept |
|---|---|
| bed | `building.bed_humanlike`, not medical |
| bed / bench accessories | the anchor's `CompProperties_AffectedByFacilities.linkableFacilities` (end table, dresser, tool cabinet, modded ones) |
| seat, table | `building.isSittable`, `surfaceType == Eat` |
| meal source, butchering | `building.isMealSource`; recipes by `WorkOrders.GoalOf` |
| recreation | `building.joyKind`; prefer a kind the colony lacks (vanilla's "recreation variety") |
| storage | `Building_Storage` (shelves, modded racks) |
| light | `CompProperties_Glower` |
| decor | `StatDefOf.Beauty` |
| heat / cold | `CompProperties_HeatPusher.heatPerSecond` (campfire +21, passive cooler −11) or `CompProperties_TempControl` (heater, cooler), against the users' `ComfyTemperatureMin/Max`. A cooler goes *in* a wall with its hot side outdoors: its own placement rule |
| floor | `TerrainDef`s in the Floors category, by beauty and cost |

**Available** = buildable (`Buildable`: research done, designator allowed) and its materials can be had. Among
candidates code ranks by the stat that matters for the need, for the cost. The fitting, sites and blueprint code stay;
only `RoomItem.defs` becomes a need (a defName list stays allowed as an override). `KeepsRole` (vanilla role workers)
still guards that an item keeps the room's role.

## 5. "Upgrade a room" (replaces the `Furnishing` lines in the Base call)
**Two steps, like Act → Base** (the user, session 15: one line per room; no upgrade categories).
- **Base call:** an "Upgrade a room" group, **one line per room** with at least one upgrade, showing only its state:
  `my bedroom (awful, 102°F)`, `the dining room (awful)`. Any colony room (proper, roofed, indoors); a room with an
  upgrade already underway is left out. Same gate as today's furnishing: `CantPlanReason` (no running project, the 2 h
  cooldown).
- **Upgrade call** (new, `Prompts/upgrade.txt`), only when a room was picked: the room (its things, impressiveness,
  temperature against her comfortable range) and **up to 3 concrete upgrades plus "0: never mind"**. Reply
  `{reason, choice, say}`. Code re-checks the pick on reply (still fits, still buildable) and places it.
- **Candidates** (code, `Building/Upgrades.cs`), all from vanilla defs, so research and mods flow in:
  - *add an item*: any buildable furniture/decor/recreation/temperature building that keeps the room's role (vanilla
    role workers, today's `KeepsRole`), in a free slot (`RoomPlacer.PlaceOne`);
  - *a better version*: an item vanilla lets replace one in the room in place (`GenConstruct.CanReplace`: replace tags
    Bed/Chair/Table, or the same item in a better material), placed over it; vanilla does the swap;
  - *a floor*: the best buildable floor over the whole free interior.
  Materials: stuff from the colony's wall materials (`Supplies.WallMaterials`: storage plus what can be had, marked by
  `Supplies.MarkFor`); anything that isn't stuff (steel, components, gold) must be in storage.
- **Scoring: three kinds of gain, one pick per kind**, each divided by cost (market value of the cost list):
  - *looks*: vanilla's impressiveness formula run on the room's wealth, beauty, space and cleanliness with the
    candidate applied (+value, +beauty, −space it covers; a floor changes every cell). Shown as `awful → dull`.
  - *comfort*: the facility offsets vanilla gives the anchor (an end table on the bed) or the comfort stat gained by
    a better seat or bed. Shown in vanilla's words (`rest effectiveness +5%`).
  - *temperature*: only when the room is outside her comfortable range; heat pushers and temperature controls that push
    the right way, by strength. Coolers that sit in a wall (`PlaceWorker_Cooler`) wait for the bigger/walls step.
- **Each upgrade is one mind's choice**, tracked as today's one-item project (`BuildProject.furnishing`), with a remark
  and a memory ("I laid a wooden floor in the dining room").
- **Bigger** is a follow-up build (push one free wall out to the next vanilla space label; furniture stays). Consent for
  a shared room: code-only (she built it, or its builder's opinion of her is positive); no vote.

## 6. Work priorities by colony need
- Reflect's `work` field gains **raise / lower** (one step), justified by the colony's need, next to love / dislike /
  refuse. Still one change a night; refusing the only capable colonist is still ignored.
- Reflect's prompt gets a **who-does-what line** for work types with work waiting (a bill not met, marked jobs,
  blueprints, things to haul): `Crafting (stone blocks 0 of 100): Wehner 3, Sab off, Valentin 4`.
- **Asking goes through talk:** a guidance line: "if work the colony needs isn't getting done, ask someone who could,
  or take it on yourself tonight." The asked mind's Reflect sees the talk and may raise it.

## 7. Checks (GABS + RimBridgeServer, saves, no long runs)
1. A fresh colony: the storage rung is only met by a storeroom; the "waiting to be hauled" line groups corpses/apparel.
2. Every room kind fits on `aipc_society3_day6` with needs instead of defNames; each finished room gets its vanilla role.
3. A researched or dev-unlocked better bed shows up as an upgrade without code changes.
4. "Upgrade a room": one line per room; the Upgrade call offers up to 3 upgrades of different kinds; a floor, an
   accessory, a better bed (replaced in place) and a heater or passive cooler get placed and tracked.
5. The looks gain predicted for an item matches vanilla's impressiveness once it's built (within a point).
6. Reflect raises crafting when blocks are short and nobody crafts; a talk asking for it precedes the change.

## 8. Build order
Storage (§3) → needs (§4) → improve a room (§5) → work by need (§6), each checked before the next.

## 9. Open questions
- None open. Later: bigger rooms (§5), coolers in walls, room kinds' tables and workshop benches still named by defName.
