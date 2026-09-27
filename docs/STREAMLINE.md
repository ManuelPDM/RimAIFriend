# Streamline: fewer, bigger choices for the minds

_Status: **built and checked in the game, session 14 (2026-09-27); not committed.** Written the same session from a
brainstorm with the user, who said "implement it". §15 has what was built differently from this spec, the check
results and the findings. Early game only: no power, no combat. Room improvement (furniture upgrades, temperature
targets, defenses) is **its own later build** (§11)._

## 1. Goal and the idea behind it
The minds spend their calls on minutiae vanilla or code could handle: walking to rooms (≈24% of 644 logged Acts),
schedules, per-work-type priorities, bill counts, zone sizes, crops, stockpile filters. Meanwhile the base stalls once
the basics exist, because nothing points onward.

**The split after this build:**
- **Vanilla does the work:** hauling, cleaning, repair, eating, sleep, recreation, construction, sowing, cooking.
- **Code does the bookkeeping:** sizes, sites, crops, bills and their counts, project materials, furnishing a new room.
- **The mind makes the high-level calls:** what the base gets next, where and in what material, what to stock up on and
  how much, whether to spend now or save, who to talk to and how, and (rarely) what work she refuses or loves.

**Trigger by gaps, not symptoms.** Low food with rice growing is a symptom, and a new field wouldn't help it. Code
measures capacity against the colony's size and the coming winter, and only then offers "expand the fields". The
loop the user described (bare minimum → hoard → improve → raid → rebuild → expand) falls out of this without a
stage machine: a raid or a new colonist simply reopens a gap.

## 2. Decisions (the user, 2026-09-27)
| # | Decision |
|---|---|
| D1 | **The Plan call goes**: schedule, work priorities, intent, `[My plan]`, "re-plan my day". Goals and "who I am lately" (Reflect) carry direction. |
| D2 | **Work priorities from passions, once** (§8), and **Reflect may change one work type** when a memory justifies it (the hauler who got shot). No menu line. |
| D3 | **The Colony call goes.** What's left of it moves into the Base call (§5). |
| D4 | **The ask board is deleted** (code, save data, `[Asks]`, the Act fields). Asking is plain talk. |
| D5 | **Act menu:** keep going · talk to someone · romance lines (only when vanilla allows them) · work on the base. "Take a break", "rest now", walking, go-to, recreation lines, "abandon my project" and the call-offs go. |
| D6 | **Talk = a person + a tone** (positive/negative); vanilla's weights pick the interaction (§4). |
| D7 | **Persona and opinion drive tone**, under one firm cooperative line in `system.txt` (§9). |
| D8 | **Mood in words**, not percentages. The `memory` field **stays** (it rests used memories and tracks who was told; PHASE3 decision 9). |
| D9 | **The ladder waits** on a blocked rung (research or anything else) and says what it's waiting on. It never skips. |
| D10 | **Research is the player's.** The mod never picks it. |
| D11 | **Stocking up is the mind's choice**, with amounts **labelled by purpose** ("enough for what's planned" / "a big stockpile"). Project materials stay automatic. |
| D12 | **Food looks ahead to winter** (§6). |
| D13 | **Auto-rebuild:** the player turns vanilla's toggle on. Code doesn't touch it. |
| D14 | **Rooms:** no "for whom". A bedroom is the designer's if she has no bed, else unclaimed. Code picks the size. Rooms come furnished and working (§7). |
| D15 | Out of scope: power, combat, trade, animals, and the room improvement build (§11). |

## 3. The Act menu
| Line | Offered when | Applies via |
|---|---|---|
| keep going | always | as now, one length: check back in 2 h (the most picked length: 87 of 117) |
| talk to someone | an awake, reachable colonist exists | `with` + `tone` fields (§4) |
| ask X out / propose to X / break up with X | vanilla's `RandomSelectionWeight > 0` for that pair and off cooldown (as now) | as now, own line each |
| work on the base (next: …) | `BuildManager.CantPlanReason == null` or anything in §5 is on offer | the Base call (§5) |

Chat and Reply keep their `act` field over the same menu, **minus "talk to someone"** (they're already talking).
The Act schema becomes `{reason, choice, say, memory, with, tone}`; `with`/`tone` are ignored unless the choice is the
talk line (`with` lists the up-to-5 nearest reachable colonists; `tone` is `positive|negative`).

## 4. Social
- **Positive:** a weighted roll over non-negative, non-life-changing interactions whose `RandomSelectionWeight > 0` for
  the pair (chitchat, deep talk, modded ones). **Negative:** the same over the negative ones (`IsNegative`: slight,
  insult). The roll uses vanilla's weights as they are.
  - Note: vanilla weights chitchat at 1 and deep talk at 0.075 × a compatibility curve, so deep talk comes up rarely
    (roughly 1 in 14 or less). Accepted by the user ("let vanilla pick"); revisit if talks feel flat.
  - If no negative interaction has weight for that pair (e.g. the Kind trait makes it 0), she falls back to positive.
    The decision line says so.
- `say` is her opening line. `act.txt` tells her the tone she picked so the words match.
- **One `[People]` section** replaces `[People nearby]`: every colonist, one short item each:
  `Valentin: friend (+76), nearby · Wehner: close friend (+100), lover, in the kitchen`. Grudges and loyalties need
  to be visible to form. For 10 colonists that's still one line.
- **Credit:** `[Rooms]` names who built each room (`dining room, built by Sab`), `[Others]` shows current projects.
- `[Others]` loses the intents and shows what each person is doing now and their project, plus yesterday's notable act.

## 5. The Base call ("work on the base")
Replaces the Project call and the Colony call. One LLM call: code scans first, then asks for **one choice**, plus a
`site` and `material` only when the choice is a room. Schema: `{reason, choice, site|"-", material|"-", say}`.

**What she sees** (Sab, society run 2, day 4; illustrative):
```
Stores: wood 233, steel 479 · planned projects need: wood 120
0: not now, let the stores build up
Next for the base
 1: a dining room (table, 4 chairs) · about 120 wood, affordable
Food
 2: more field before winter: ours grow ~80% of what 3 people eat, and winter is 12 days off · 20 cells of rice
Stock up
 3: wood, enough for what's planned: ~8 trees (+160)
 4: wood, a big stockpile: ~30 trees (+600)
 5: steel: the vein near the base (+~300)
 6: food for winter: hunt the deer herd (4 animals, ~300 meat)
 7: food: gather wild berries (~12 bushes)
Other rooms
 8: a rec room · 9: a laboratory · 10: my own bedroom
Add to a room (until the improvement build)
 11: add to the barracks: chess table (70 wood)
Sites (for a room; walls and door cost for 5x5): A … B … C …
```

**Groups and their rules:**
| Group | Offered when | Code decides | Applies via |
|---|---|---|---|
| Next for the base | the ladder's current rung (§5.1), unless someone already has it underway | size (§7), furniture, bills | `BuildManager.Place` |
| Food | capacity or the winter check (§6) falls short, and no field is still unsown | site (`ZoneSites`), crop (rice), size to close the gap | `Fields.Place` |
| Stock up | per resource: a source exists and isn't all marked | which trees/ore/animals/plants, nearest first | `TreeCutting`/`Mining`/`Hunting`/`WildFood` apply code |
| Other rooms | buildable kinds the ladder isn't asking for | as above | `BuildManager.Place` |
| Add to a room | `Furnishing.Options` as today (max 2), any room she built | as today | `Furnishing.Place` |
| 0: not now | always | nothing | a decision line |

**Stock-up amounts** (defaults, to tune): "enough for what's planned" = the shortfall of all active projects plus the
next rung's estimate, ×1.2; "a big stockpile" = 200 + 100 per colonist wood, 100 + 50 per colonist steel. Wild berries
need a mark in vanilla (`WorkGiver_PlantsCut` only takes `HarvestPlant`-designated wild plants), so they stay a choice.
Stone blocks only when a stonecutter's table exists (a bill "until N", N by the same purpose rule).

### 5.1 The ladder
Code walks the rungs in order and shows the **first unmet one** in `[Colony]` and as "Next for the base":
`Base: beds for 6 of 3 · next: a dining room`. A rung with a project underway counts as met (the next mind sees
the next rung). A rung that can't be built says why and **waits** (D9):
`next: a hospital (waiting on research: …)` or `(waiting: no site fits near the base)`.

| # | Rung | Met when (read from the map) |
|---|---|---|
| 1 | beds for everyone | bed slots ≥ sleepers (as the base line counts now) |
| 2 | a kitchen | a meal source inside a room |
| 3 | storage under a roof | the current `FoodUnderRoof` check |
| 4 | a dining room | an eating surface with a seat inside a room |
| 5 | a workshop | a production bench other than a stove inside a room |
| 6 | a hospital | a medical bed |
| 7 | private bedrooms | every sleeper owns a bed in a Bedroom-role room |
| 8 | walls and defenses | **not in this build** (needs site logic; §11). Past rung 7 the line reads `the base has what an early colony needs`. |

## 6. Food and winter
- **Need per day:** colonists × vanilla's hunger (`Need_Food.BaseHungerRate` × 60000 ≈ 1.6 nutrition/day for an
  adult). Cooked, a simple meal turns 0.5 raw into 0.9, so with a meal source the raw need is ≈ 0.9/day. _Verify both
  numbers in the source while building._
- **Field output per day:** for each growing zone, cells × crop yield × nutrition per unit ÷ days to grow, using
  vanilla's growth factors (fertility, light, night rest; `Plant.GrowthRate`, `Plant.Resting`). _Verify the night-rest
  factor rather than guessing it._
- **Winter:** `GenTemperature.TwelfthsInAverageTemperatureRange(tile, crop min, crop max)` (what vanilla's
  "Outdoor growing period" uses) gives the growing twelfths. The days without growing, × need, is what the stores
  plus the harvest before the season ends must cover.
- **The food line in `[Colony]`:** `fields feed ~2.4 of 3 · winter in 12 days, 20 days without growing; stores and
  the coming harvest cover about 11`. On a map with no winter, only the first half.
- The Food group offers a field only when the fields fall short, and never while a field is still unsown (the fix for
  "food is low, so another field"). Spoilage is ignored in this build (winter cold freezes stores outdoors). It
  belongs to the temperature work in §11.

## 7. Rooms come furnished and working
Sizes by code (defaults, to tune): bedroom 4×4, barracks sized to the bed gap (min 2 beds), kitchen, dining room,
workshop, storeroom, rec room and hospital 5×5, laboratory 4×4. The minds no longer pick a size.

| Kind | Furniture (the def) | Added by code when it's done |
|---|---|---|
| bedroom | bed, end table | the bed is hers if she had none; otherwise unclaimed (vanilla assigns it) |
| barracks | beds | unclaimed |
| kitchen | fueled stove, **+ butcher table** | cooking bill "simple meal, until colonists × 4" (~2 days); butchering "forever"; a food stockpile on the free floor |
| dining room | table, chairs (up to 4) | nothing |
| workshop | the first buildable bench in its list | that bench's default bill from a new `<bills>` list in `RoomKinds.xml` (e.g. stonecutter: blocks until what's planned needs; tailoring: tribalwear until 1 per colonist) |
| storeroom | shelves | a stockpile on the free floor, everything except corpses and chunks |
| hospital | 3 medical beds | nothing (a medicine shelf is §11 material) |

**Project materials:** placing a room marks enough trees (wood) or ore (steel) for its shortfall ×1.2, nearest first,
through the existing chore code. A material code can't get isn't offered in the Base call.

**The first stockpile:** when any mind is on and the map has no stockpile or shelf, code lays one out (the
`Stockpiles` site scan, everything except corpses and chunks), so `[Colony stores]` never says "unknown" for long.

## 8. Work priorities
- **From passions, once:** when a mind is turned on, and for her when a colonist joins or leaves: major passion → 1,
  minor → 2, others → 3. Disabled and protected work types are untouched. Only with manual priorities on; in simple
  (checkbox) mode code sets nothing, since there are no numbers to rank.
- **Reflect may change one work type a night**, only when an event from the period justifies it. It's a new optional
  field `work: {type, change: none|love|dislike|refuse, why}`: love → 1, dislike → 4, refuse → off (checkbox mode:
  refuse → unchecked, love → checked). A refusal is ignored when she's the only colonist who can do that work.
  It's recorded as a memory event, and `[Me]` shows it: `won't haul (got shot hauling outside the walls)`.

## 9. What the model sees
| Section | Change |
|---|---|
| system prompt | + one firm line: *the colony survives only if you work together; disagreements are fine, drama for its own sake isn't.* |
| `[Me]` | + "good at: construction, melee" (the old `[Skills]`, passions only) and any work she refuses or loves |
| `[Time]` | − "Schedule now" |
| `[Condition]` | mood in words, relative to her break threshold: good / okay / low / near breaking |
| `[Needs]` | only needs vanilla calls low (e.g. Hungry, Tired), by vanilla's category label; else left out |
| `[Feelings]` | top 3 by size |
| `[Skills]` | gone (into `[Me]`) |
| `[My plan]`, `[Asks]` | gone |
| `[People nearby]` | becomes `[People]` (§4) |
| `[Others]` | doing now + project + one notable act; no intents |
| `[Colony]` | alerts, danger, the ladder line (§5.1), the food line (§6) |
| `[Colony work]` | short: `3 rice fields (~40% grown) · marked: 12 trees · orders: simple meal until 12` |
| `[Rooms]` | + who built each |
| `[Recent]`, `[Since yesterday]` | unchanged (they overlap; merging them is a later idea) |

**Prompts:** `act.txt` rewritten (no asks, one keep going, the tone rule); `guidance.txt` cut to what's still true
(no sleep hours, work hours, asks or "someone must set up fields"; + "the base grows when someone works on it"); `reply.txt`
and `chat.txt` lose the talk line; **new `base.txt`** replaces `project.txt` and `colony.txt`; **`plan.txt` and
`colony.txt` deleted**; `reflect.txt` gets the work field.

## 10. Code map
| File | Change |
|---|---|
| `Chores/Asks.cs` | delete |
| `Chores/ColonyCall.cs`, `Prompts/colony.txt`, `Prompts/plan.txt` | delete |
| `Building/ProjectCall.cs` → `Building/BaseCall.cs` | the Base call (§5): groups, one choice, site/material for rooms, code sizes |
| `Building/Ladder.cs` (new) | the rungs, met checks, "waiting on" reasons, the `[Colony]` line |
| `Chores/FoodOutlook.cs` (new) | §6: need, field output, winter, the food line, the Food group's field size |
| `Chores/ChoreOptions.cs` | shrinks to the stock-up producers with purpose amounts; sizes, `ColonyChoice`, `Stops`, `MenuLabel` go |
| `Chores/Fields.cs`, `Stockpiles.cs`, `WorkOrders.cs` | apply without a model choice (code site/crop/count); `WorkOrders` becomes room outfitting |
| `Building/BuildManager.cs` | on done: outfit the room (§7); on place: mark materials; `occupant` rule per D14 |
| `Defs/RoomKinds.xml` + `RoomKindDef` | a default size per kind, butcher table in the kitchen, `<bills>` for the workshop and kitchen |
| `Actions/ActionCatalog.cs` | the new menu and schema; Plan schema, schedule helpers, walk/go-to/recreation, `NotableRooms` go |
| `Actions/MindActions.cs` | `Talk(mind, with, tone, say)` with the weighted roll |
| `Mind/PawnMind.cs` | Plan scheduling, `intent`, extra plans, ask posting go; one keep-going length; passion priorities on enable |
| `Mind/SnapshotBuilder.cs`, `PromptBuilder.cs` | §9; the `plan` and `colony` recipes go, `base` added |
| `Memory/Reflection.cs` | the work field (§8) |
| `Chores/ChoreManager.cs` | `asks` go; chores stay (they drive `[Colony work]` and "already marked") |
| `Mind/MindDevTools.cs`, `Chores/ChoreDevTools.cs`, `Building/BuildDevTools.cs` | "Plan now" and "Colony call now" go; add "Base call now", "Ladder now" (each rung and its verdict), "Food outlook now", "Outfit room now" |
| `UI/ITab_Mind.cs` | the intent section goes |

Save compatibility: removed `Scribe` fields (`intent`, `intentTick`, `asks`) are just left unread. Check that
`aipc_society2_day4` loads with no errors.

## 11. Later: the room improvement build (not this one)
One unified system for making existing rooms better, which the user wants designed on its own:
- **Target temperature per room:** code adds whatever heating or cooling is available to hit it (campfire, heater,
  cooler, passive vent), with spoilage and freezers as a consequence.
- Furniture upgrades (a better bed, the stove's upgrade), floors, lights, beauty and impressiveness, replacing
  today's `Furnishing` lines.
- Walls and defenses (ladder rung 8), with their own site logic.
- Power and combat stay out until mid-game is in scope.

## 12. Done when (through GABS + RimBridgeServer, no long runs)
1. The build has 0 errors. `aipc_society2_day4` loads with no errors in `Player.log`.
2. `Show context now` (Sab): no `[My plan]`, `[Asks]` or `[Skills]`; mood in words; `[People]` lists all three; the
   ladder line reads `next: a dining room`; the food line has numbers; `no_coords.py` is clean.
3. The Act prompt (`last_call.py act 1 --prompt`) has ≤ 4 kinds of line; its schema has `with`/`tone` and no `ask`.
4. Talk: force an Act that picks the talk line with `positive`, then `negative`. The log shows chitchat/deep talk,
   then slight/insult; a Kind colonist's negative falls back to positive.
5. `Base call now`: the groups match §5 for this save; pick the dining room → placed at the code size, with trees
   marked for its shortfall; pick a stock-up line → that many trees marked; pick 0 → nothing changes.
6. `Outfit room now` on a finished kitchen: cooking bill (until colonists × 4), butchering bill, a food stockpile.
   A finished bedroom by a bedless mind is hers.
7. `Ladder now`: each rung's verdict; force a blocked rung (dev) → "waiting on …", and it doesn't skip.
8. `Food outlook now`: the numbers match a hand calculation from the zone's crop and cells; skip to near winter
   (`skip.lua`) → the Food group offers a field.
9. Enabling a mind sets passion priorities (manual mode). A fake "got shot while hauling" event, then `Reflect now`,
   can produce `refuse hauling` → hauling off and shown in `[Me]`. As the only hauler, it's ignored.
10. A fresh quicktest with no stockpile gets one within an hour of the minds turning on.
11. A short society run: fresh quicktest, 3 minds, Fast, **stop at the first of day 2 or a kitchen placed**. Count
    Acts by kind (no walking), Base calls and what they chose, talks by tone.

## 13. Build order
1. **Removals:** Plan, asks, Colony call, the dropped menu lines, one keep-going. The game runs and minds still Act.
   → verify: checks 1 and 3 (minus `with`/`tone`).
2. **Context and prompts:** §9, the cooperative line. → verify: check 2.
3. **Social:** the talk line, tone roll, romance lines. → verify: check 4.
4. **Ladder + food outlook** (read-only lines first). → verify: checks 7 and 8 (the lines).
5. **The Base call:** groups, stock-up amounts, code sizes, materials. → verify: check 5.
6. **Automation:** room outfitting, first stockpile, bedroom owner. → verify: checks 6 and 10.
7. **Work priorities:** passions + Reflect. → verify: check 9.
8. **The short run** (check 11), then HISTORY.md and NEXT_STEPS.md.

## 14. Open questions
1. **Phase 5 and 6:** this replaces the Colony call that Phase 5's self-sufficiency run was meant to test, and the
   ask checks (5b, 5d) in Phase 6. Proposal: close Phase 6 on its results so far, and do Phase 5's run on the new
   design after this build.
2. The stock-up amounts and room sizes in §5 and §7 are first guesses to tune in the run.

## 15. Build results (session 14)
0 build errors and 0 warnings. Checked on `aipc_society2_day4` (loads cleanly) and in four short runs from
`aipc_society2_start` at Fast (about 0.2 of a day each).

**Built differently from the spec (all small):**
- The workshop's default bill comes from a code rule (meals, butchering, stone blocks, then clothes, by recipe
  goal), not a `<bills>` list in `RoomKinds.xml`. Only the kitchen got XML: an optional butcher table.
- Priorities from passions run **once per mind** (flag `prioritiesSet`), not again when colonists join or leave. In
  checkbox mode nothing changes and the manual-priorities warning shows (both test saves have manual priorities off).
- Bedroom owner: hers if she has no **room** of her own (a barracks bed doesn't count); else the first colonist
  without a room of their own (so the "private bedrooms" rung can be met); else unclaimed. `BuildProject.unclaimed`
  is new; `OccupantGone` now means "has a room of their own elsewhere".
- Talk cooldown for ordinary talks: once per person every 4 hours (was a day). There's only one talk line now.
- **Wall materials never include silver or anything worth more than 3 silver a unit** (`SiteFinder.Materials`): a
  fresh colony's silver became a 2,700-silver barracks in run 2.
- Project materials count **every active project** on the map, minus storage and what's already marked (run 1:
  per project, two projects each "covered" by the same 479 steel marked nothing).
- A room reply re-checks: if another mind started the same kind while she was thinking, she leaves it to them; sites
  are found again and the one nearest her pick is used (run 3: two Base calls for the barracks at once, the second
  failed with about 80 validator warnings).
- The ladder's next rung says on its own line "whatever it's missing gets marked when you lay it out", plus a line in
  `base.txt` (runs 1-2: minds stocked up wood or steel "for" the rung instead of laying it out).
- The Base call shows at most 2 stock-up lines per kind and 7 in all; "Other rooms" only lists kinds the colony has no
  room for (plus "my own bedroom" and a new workshop bench); a barracks only comes from the ladder.
- `Recall.Plan` is still used, by "Show context now" only.

**Checks (§12):**
| # | Result |
|---|---|
| 1 | ✓ 0 errors; `aipc_society2_day4` loads with no errors (its kitchen was outfitted on load: cooking bill until 12, a 17-cell food stockpile). |
| 2 | ✓ Sab: user 3,705 chars (was 5,087), system 3,074 (was 3,520); no `[My plan]`/`[Asks]`/`[Skills]`; "Mood okay"; `[People]`; "next: a dining room"; the food line; "built by". |
| 3 | ✓ 3 menu lines (was 15); schema `{reason, choice, with, tone, say, memory}`. |
| 4 | Part: positive talks roll chitchat or deep talk (dev and live); a Reply followed. **Negative not seen yet.** |
| 5 | ✓ Live: Sab chose the dining room (site A, steel), Valentin the workshop; stock-ups of wood, steel and wild food applied; materials marked for a steel shortfall (dev) and for a wood barracks (live, 15 and 17 trees). "0: not now" not seen. |
| 6 | ✓ Kitchen and workshop (tribalwear until 3) outfitted. A bedroom's owner not checked. |
| 7 | ✓ Rungs and verdicts; the next rung moves on when one is underway. "Waiting on research" not checked (no research-gated rung early). |
| 8 | Part: the numbers check out (3 × 36 rice cells, 5.5-day cycle, 1.98/day each; need 2.67/day cooked). The test map grows all year, so winter isn't checked. |
| 9 | Part: Reflect's `work` field runs ("none", no change). Passion priorities not seen with manual priorities on. |
| 10 | ✓ The first stockpile (6×6) comes within the first in-game hour. |
| 11 | ✓ Short runs: first stockpile, the barracks from the ladder with its wood marked, a talk and a Reply, no exceptions. |

**Findings still open:**
- Valentin picked "work on the base" but filled `with: Sab` and spoke to Sab: the talk fields may pull a mind towards
  talking when it meant something else (one case).
- `[Colony stores]` reads 0 right after the first stockpile appears, until haulers fill it (vanilla only counts
  stored things).
- "wood, a big stockpile" is limited by the trees near the base (6 trees on the day-4 map).
- Deep talks are rare with vanilla's weights, as expected.

**After the user's longer run (arid map, session 14; compiled, not yet deployed or checked in the game):**
- **No automatic first stockpile** (the user: "a weird thing to add"). Instead the Base call offers "a stockpile for
  everything" in a Storage group, only while the colony has no stockpile or shelf. Kitchens and storerooms still get
  their own.
- **Wood marking cut young cacti for about 5 wood each** (a saguaro gives 15 grown): trees now need 90% growth and at
  least 8 wood. Vanilla clears plants off blueprints by itself.
- **Wall materials by what can be had:** `Supplies.WallMaterials` ranks them by storage plus what's nearby (grown
  trees for wood, the nearest veins for steel), and the Base call shows both ("steel 480 in storage (~600 more to mine
  nearby)"). On that map every room was wood while steel was plentiful.
- A room's marking says when it falls short ("About 150 wood short: not enough grown trees near the base").
- **Walls are wood or stone blocks only** (the user: steel is for other things). Stone "to be had" counts reachable
  chunks, only once a stonecutter's table exists or can be built (Stonecutting research); the workshop's first bench
  is now the stonecutter's table when it can be built. Until then, walls are wood.
- **One rule for what's worth marking** (`ChoreOptions.WorthHarvesting`, trees and wild food): vanilla lets it be
  harvested now and it gives at least 75% of its full yield (vanilla's own yield formula: 50-100% by growth past its
  harvest point, times health). Replaces "90% grown and 8 wood".
- **The chore search covers the whole reachable, explored map** (was 60 steps from the base; the user: we'll use the
  whole map for wood and stone). Site search still prefers places near the base. On the arid save that had no trees in
  reach: 23 trees (+395 wood), 25 wild food plants, steel, gold and silver veins; 42 ms.
- **Wild food only needs to be ripe** (the user: food is ripe or it isn't); the 75% rule is for trees only.
- **Decided, not built:** no "dead end" line in `[Colony]`; no auto-cancel for a project waiting on materials (with
  the whole map searched, it gets built once they arrive).

**Society run 3 (session 15, `aipc_society2_start` at Fast to day 6; saved as `aipc_society3_day6`):**
- **The ladder carries the base past the first rooms:** barracks (day 2), kitchen, dining room, workshop (stonecutter's
  table), then a hospital and private bedrooms underway by day 6. By day 6 the ladder says "nothing; it has what an
  early colony needs" and the "Need defenses" alert shows. A 49-cell rice field (Wehner) covers ~3.6 of 3. No exceptions.
- **Wood is the bottleneck:** `wood 0` in stores from day 3 to day 6, with 35-50 trees marked and ~1,800 more to be had.
  Rooms wait 150-190 wood each and fill slowly (felling labour, not marking). Every room was wood, even with ~15,000
  stone blocks to be had from chunks and a stonecutter's table built (its bill: stone blocks until 10, 0 made).
- **Fixed:** "Other rooms" offered a hospital while Wehner's was underway; Valentin picked it and got "already
  started, so I left it to them". `BaseCall.OtherRoom` now skips kinds with an active project on the map (owned
  bedrooms excepted). Checked on the day-6 save: no hospital line.
- **Seen:** two minds picked "0: not now" saying "marking trees now" when no wood stock-up line was shown (the
  trees were already marked, or the stock-up limits hid the line). Social: talks and Replies run; replies were friendly,
  no negative talk. Stale act replies dropped ~10 times (one request at a time).
