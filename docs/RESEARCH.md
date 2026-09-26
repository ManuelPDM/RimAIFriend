# Research: existing RimWorld + LLM mods

Code study done 2026-09-25. The clones live at `C:\Users\manue\Coding Projects\rimworld-reference\` (read-only
reference, outside the mod folder). **Licenses:** MIT = rimagent, RimBridgeServer, RimMind-*, PawnDiary.
GPL-3 = rimworld-mcp. CC BY-NC-SA = RimTalk. Only borrow *patterns*, not code, from the GPL and NC repos.

## Headline takeaways

1. **Threading:** take a snapshot on the main thread, run HTTP on a background thread, put results in a
   `ConcurrentQueue`, and drain them on the main thread. The best pump is a Harmony postfix on `Root.Update`,
   which runs even when paused or in menus (RimBridgeServer, rimagent). Work items should be cancellable,
   so a stale command never fires after it times out.
2. **Commands:** use `pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc)`, **never** bare
   `StartJob(..., InterruptForced)`. RimMind and rimworld-mcp use `StartJob`, which isn't player-forced, so
   vanilla overrides it right away.
3. **The 1.6 float-menu API can be called without the UI.** It gives every vanilla and modded action, with
   validation, and returns a "why not" label for disabled options:
   `FloatMenuMakerMap.GetOptions(List<Pawn>, Vector3 clickPos, out FloatMenuContext)` → `FloatMenuOption`
   (`Label`, `Disabled`, `action`, `Chosen(bool, FloatMenu)`). Around 60 `FloatMenuOptionProvider`s exist.
   (`ChoicesAtFor` was removed in 1.6.)
4. **Curated typed tools beat auto-generated tool floods.** RimMind and rimagent expose dozens to 100+
   generic tools, which bloats context for 27B models.
5. **Memory:** a three-tier design (event log, a nightly LLM consolidation into a pinned summary, and an
   identity anchor) with structured records (participants, topic) works well. Retrieval can start with
   metadata and ID matching; embeddings (Qdrant) are the upgrade.
6. **Speech bubbles:** write a custom `PlayLogEntry` into `Find.PlayLog`. Interaction Bubbles
   (`Bubbles.Core.Bubbler.Add(LogEntry)`) renders it, and it appears in the social log. This is how RimTalk
   does it (`RimTalk/Source/Compatibility/BubbleCompatibilityPatch.cs`).

## Per-repo notes

### rimagent (MIT): an LLM plays a whole colony. Python agent plus the RimBridge C# mod.
The C# bridge is a git submodule that's missing from the clone, so these notes come from the Python code
and the docs.
- **Situation packet** (`agent/rimagent/loop.py:409`), change first: wake trigger, open dialogs, tracked trends
  (↑↓), a diff since the last step (≥5% changes, capped at 40 lines), new events, rooms-as-objects, one line
  per colonist. Grids and images are fetched on demand, never sent by default.
- **Wake policy** (`runner.py:387`): critical events, watcher alerts, and game alerts (deduped for 24h), plus
  a scheduled wake that the model sets itself via `end_turn(notes, wake_in_hours, wake_on)`.
- **Think at speed** (pausing felt frozen). Pause only for danger. Urgent events mid-step get injected as
  a `## URGENT` message.
- **Local model tricks:** retry once with thinking off (Qwen sometimes returns empty output after a long
  think). Coerce stringified JSON args, remap aliased param names, and list accepted params in errors.
  Truncate tool results at 8k. Nudge the model if it replies without a tool call.
- **Memory:** `notebook.md` (6k chars, rewritten, always in the prompt), `journal.md` (append-only, last 12),
  skills picked by BM25.
- **Lessons:** a hung socket froze play for 30 min, so use no retries and a hard timeout. "Placed" doesn't
  mean "built", so verify from state. About 11% of tool calls errored, mostly on guessed defNames.

### RimMind Core / Actions / Advisor (MIT): the LLM picks actions for idle pawns
- **Good:** `RequestQueue` → `RequestCompletionInbox` (a ConcurrentQueue) → drained in a GameComponent tick.
  One in-flight request per local endpoint. Stale-callback checks. Native tool calls with a capped result
  loop. Idle detection: `curJob == null`, or a non-player-forced Wait/Wander job. A danger-scaled cooldown
  with a per-pawn stagger.
- **Bugs to avoid:** `HttpClient` timeout never set (100s default kills slow local gens). Tick-based timeouts
  (they freeze when paused). Context providers read game state off the main thread. `StartJob` instead of
  ordered jobs. The pawn id is taken from LLM args (a hallucinated id moves the wrong pawn). The schema and
  the dispatcher disagree. Integer args get parsed into `Dictionary<string,string>`.
- Bundles Newtonsoft 13.0.4. Watch for version clashes if we bundle it too.

### RimMind Memory / Personality / Dialogue (MIT)
- **Memory entry:** `{content, type, tick, importance, pinned, pawnId, targetPawnId}`. The active tier holds
  30, and archive holds 50 sorted by importance.
- **"Dark memory":** a daily LLM merge of the day into a few pinned impressions. Good idea, but too small
  (3 × 50 chars).
- **Retrieval score:** `importance × 1/(1+0.33·days) × typeWeight × relevance(target pawn match)`.
- **Events patched:** `AddHediff`, `AddDirectRelation`, `TryStartMentalState`, `Pawn.Kill`,
  `SkillRecord.Learn`, `IncidentWorker.TryExecute`. `StartJob` is aggregated into work sessions ("hauled ×12").
- **Feedback into the game:** a closed enum mapped to fixed-slot ThoughtDefs with clamped mood and duration.
  Never pass free-form numbers into the game.

### RimTalk (CC BY-NC-SA; study only): the most popular LLM dialogue mod, tuned for local models
- Tiered detail: the speaker gets full context, others get top-3 traits and thoughts. Skills are grouped
  by tier. Sections are labelled (`[P1]`, `[Environment]`, `[Events]`). The prompt is blunt and
  anti-purple-prose, with 1–2 sentence outputs.
- The persona is one generated sentence (style, attitude, quirk), saved on the pawn.
- History holds 2 turns and is **wiped daily** to avoid degraded, repetitive output.
- Latency: one global request, streamed JSONL parsed line by line, skipped if the pawn's state is unchanged,
  and turned off at high game speed.
- **Hooks:** `PlayLog.Add`, `Archive.Add` (letters), `BattleLog.Add`, `MemoryThoughtHandler.TryGainMemory`
  (|mood| ≥ 3).

### PawnDiary (MIT): an LLM diary. Heavily over-engineered (80k lines).
- Worth borrowing: structured memory records (participants, subject, topic), a reuse cooldown so the same
  memory isn't recalled endlessly, a psychotype "outlook rule" set once per pawn, an anti-repetition check,
  "grounded only in supplied facts", and a circuit breaker around each Harmony patch.
- Situational need thoughts (hunger and similar) don't go through `TryGainMemory`, so they must be polled.
- About 60 patches, including `TaleRecorder.RecordTale`, `InspirationHandler`, `Quest.Accept/End`, and
  `HistoryEventsManager.RecordEvent`.

### rimworld-mcp (GPL-3; study only): HTTP bridge plus a Python MCP server
- A `ConcurrentQueue` drained in `GameComponentUpdate`. Timed-out requests are **never cancelled**, so stale
  commands fire later. It doesn't work at the main menu.
- Weak validation (no `CanReach`/`CanReserve`). Equipping teleports the item. Compact serialization with
  rounding and `Take(N)` caps is a decent pattern.

### RimBridgeServer (MIT, by Pardeike): an in-game MCP server that simulates UI clicks
- The best main-thread pump: a `Root.Update` postfix, work items backed by a `TaskCompletionSource`,
  interlocked cancellation, and a readiness gate (`ProgramState`, `LongEventHandler`, map exists).
- Runs float-menu options via `option.Chosen(...)` after checking `!Disabled`. For an in-process mod we can
  call `GetOptions` directly instead of faking clicks.

## Confirmed RimWorld 1.6 APIs (checked by reflection on Assembly-CSharp.dll)
- `FloatMenuMakerMap.GetOptions(List<Pawn>, Vector3, out FloatMenuContext)`, `GetAutoTakeOption`, `providers`
- `FloatMenuContext(List<Pawn>, Vector3, Map)`: `ClickedCell`, `ClickedThings`, `ClickedPawns`, `FirstSelectedPawn`
- `FloatMenuOption`: `Chosen(bool colonistOrdering, FloatMenu)`, `action`, `Label`, `Disabled`, `revalidateClickTarget`
- `Pawn_JobTracker.TryTakeOrderedJob(Job, JobTag?, bool requestQueueing)`, `ClearQueuedJobs`, `EndCurrentJob`
- `Job.playerForced`, `expiryInterval`, `checkOverrideOnExpire`; `Pawn_DraftController.Drafted`

### Checked in the decompiled source (2026-09-25, Step 1)
Paths are relative to `rimworld-reference\decompiled\Assembly-CSharp\`. Key claims were spot-checked by hand.

#### Schedule (`RimWorld\Pawn_TimetableTracker.cs`)
- `SetAssignment(int hour, TimeAssignmentDef)` (L56) is just `times[hour] = ta`. It has no bounds or null
  check and no dirty flag, and no refresh is needed. The UI does the same (`PawnColumnWorker_Timetable.cs:96-100`).
  `times` is saved automatically (L37).
- `TimeAssignmentDefOf`: `Anything`, `Work`, `Joy`, `Sleep`, and `Meditate`, which is **null without Royalty**.
  **Never store null or a custom def.** `JobGiver_Work.GetPriority` throws `NotImplementedException` on
  anything else (`JobGiver_Work.cs` ~L25-47), and the UI dereferences every entry.
- `timetable` exists only for humanlike pawns of the player faction (`PawnComponentsUtility.cs:280-299`), so
  it's null for prisoners and guests. `CurrentAssignment` returns Anything unless `IsColonist` (L12-22).

#### Work priorities (`RimWorld\Pawn_WorkSettings.cs`)
- 1 is the highest priority, 4 the lowest (`LowestPriority = 4`), and 0 is off.
- `SetPriority` (L140-158) marks the work-giver cache dirty by itself. It **logs a red error and does
  nothing** if the work type is disabled, and it doesn't clamp out-of-range values (it only logs). So check
  `pawn.WorkTypeIsDisabled(wt)` (`Pawn.cs:4526`) and clamp to 0-4 ourselves.
- **1-4 only differ when manual priorities are on** (`Find.PlaySettings.useWorkPriorities`, a saved global
  that defaults to off). When it's off, `GetPriority` returns 3 for any value above 0 (L160-169), but the stored
  1/2/4 values are kept. If we flip the flag in code, call `workSettings.Notify_UseWorkPrioritiesChanged()`
  on every player pawn, like the Work tab does (`MainTabWindow_Work.cs:41-52`).
- **Never call `EnableAndInitialize`** (L89-129), because it overwrites every priority. Guard with
  `workSettings?.EverWork`.

#### Social interactions (`RimWorld\Pawn_InteractionsTracker.cs`)
- **`TryInteractWith(recipient, def)` (L176+)**
  - It calls `CanInteractNowWith` (L152-174), which checks:
    - the 120-tick cooldown (L147-150);
    - that the recipient is spawned, within 6 cells and in line of sight (`SocialInteractionUtility.cs:143-155`);
    - that both pawns are awake, can talk, aren't burning, and aren't blocked by mental states, hediffs or lords
      (`PawnUtility.IsInteractionBlocked`, L43-77).
  - `ignoreTimeSinceLastInteraction` does **not** bypass the cooldown.
  - It doesn't check downed, drafted, hostility, humanlike status or whether the def suits the pawns. We must
    check those ourselves.
- **On success:**
  - initiator and recipient thoughts plus the opinion change (L285-298);
  - skill XP;
  - a **social-fight roll** from `intDef.socialFightBaseChance` (L214-218, 434-486). If a fight starts, the
    worker is skipped.
  - `Worker.Interacted`;
  - an interaction mote;
  - `lastInteractionTime` set on **both** pawns;
  - a new `PlayLogEntry_Interaction` passed to `Find.PlayLog.Add` (L248-249);
  - an optional letter.
- **Safe to trigger on demand: Chitchat, DeepTalk, KindWords only.**
  - Insult and Slight can start social fights.
  - RomanceAttempt, MarriageProposal and Breakup change relations and send letters.
  - Recruit, Enslave, ReduceWill, Convert, Suppress and the Spark*, Trial*, Counsel* and Speech* defs involve
    prisoners, slaves, rituals or rebellions.
  - KindWords, Slight and Breakup have no `DefOf` entry, so look them up with `DefDatabase<InteractionDef>`.
- **Vanilla random chatter keeps running alongside ours.** It uses an MTB roll every 60 ticks and needs more
  than 320 ticks since the last interaction (L106-145). It shares the cooldown fields with our calls.
  Suppressing it would need a patch on `TryInteractRandomly`.

#### Speech entries and Interaction Bubbles
Bubbles is Workshop 1516158345, packageId `Jaxe.Bubbles`, "© Jaxe" with no license, so read it but don't copy.
- **How Bubbles picks up entries:**
  - Bubbles postfixes `PlayLog.Add` and accepts any `PlayLogEntry_Interaction` or subclass, and
    `PlayLogEntry_InteractionSinglePawn`. It ignores every other `LogEntry`.
  - It reads `initiator`/`recipient` by reflection and gets the text with
    `ToGameStringFromPOV(initiator)` the first time it draws the bubble.
  - **It needs no reference or call from us:** `Find.PlayLog.Add` is enough.
- **Bubbles filters:**
  - **Drafted pawns get no bubble** by default (`DoDrafted = false`). RimTalk flips this by reflection around
    `Bubbler.Add`.
  - No bubble for pawns off the current map, in fog, or at game speeds above its auto-hide setting.
  - A bubble is at most 256 px wide and fades after 500 + 100 ticks (about 10 s at 1x).
- **The PlayLog** (`Verse\PlayLog.cs`) is one global list capped at **150** entries and saved deep. The Social
  tab shows the 12 newest entries for the pawn (`SocialCardUtility.cs:177`). It is not memory storage.
- **Save risk with a custom entry class:**
  - If the mod is removed, the class can't be resolved. `ScribeExtractor` returns null
    (`ScribeExtractor.cs:105-150`), `Scribe_Collections` still adds that null to the list (L224-225), and
    `PlayLog` then throws a NullReferenceException in the Social and Log tabs until 150 newer entries push it
    out. A mod-only `InteractionDef` has the same problem.
  - RimTalk works around this by converting its entries to vanilla ones in a prefix on
    `GameDataSaveLoader.SaveGame`.
- **Recommended pattern for us: no custom class.**
  1. Use a plain vanilla `PlayLogEntry_Interaction`, either the one `TryInteractWith` creates (capture it
     with a flagged `PlayLog.Add` prefix) or our own with a vanilla def.
  2. Keep our text in a GameComponent dictionary keyed by `entry.GetUniqueLoadID()` (stable:
     `LogEntry_{ticksAbs}_{logID}`, `LogEntry.cs:158-160`). Register the text before `PlayLog.Add`.
  3. Postfix `LogEntry.ToGameStringFromPOV` to swap our text in.
  4. Prune the dictionary to the entries still in the log.

  If the mod is removed, the lines revert to vanilla chitchat text. `PlayLog.Add` applies no thoughts; if we
  want any, apply them ourselves.

#### Player orders vs ours
- **`Pawn_JobTracker.TryTakeOrderedJob` (`Verse.AI\Pawn_JobTracker.cs:889-959`)**
  - It sets `job.playerForced = true` on every call, so the flag doesn't prove the player gave the order.
  - It has no notify or event.
  - **Shift-queue trap:** it reads `KeyBindingDefOf.QueueOrder.IsDownEvent` (L904), which checks
    `Event.current`, then `Input.GetKey`. If we call it while `Event.current` is non-null and the player is
    holding Shift, our order gets **appended to the queue** instead of replacing the current job. Calling from
    our `Root.Update` pump avoids that.
- **Every player order path goes through it:** float-menu options, the auto-take goto (`Selector.cs:219`),
  drag-goto (`MultiPawnGotoController` → `FloatMenuOptionProvider_DraftedMove.PawnGotoAction`), "Prioritize",
  gizmos, targeter force-attack (`Verb.OrderForceTarget`), and abilities.
- **Game-internal callers (false positives):**
  - `Toils_Haul.cs:123` (a follow-up store job);
  - `RestoreCapturedJobs` (`Pawn_JobTracker.cs:544`, called after `PawnFlyer` jumps);
  - `JobDriver_GetReimplanted.cs:31` and `JobDriver_ResurrectMech.cs:32`;
  - other mods.
  - Mental states, lords and the think tree use `StartJob`, not this method.
- **The `Drafted` setter** (`Pawn_DraftController.cs:19-102`) is also called automatically, from:
  - `AutoUndrafter` (after 10,000 ticks with no threat);
  - mental states and `MakeDowned`;
  - faction changes;
  - rituals;
  - caravan, transporter and portal arrival (auto-draft);
  - `PawnFlyer` restoring the previous state.
- **Recommended hooks:**
  1. **A prefix on `TryTakeOrderedJob`** for our pawn: skip when our `[ThreadStatic]` "our order" flag is set,
     and count it as the player only when `Event.current != null`. Also wrap `RestoreCapturedJobs` in a flag.
  2. **Drafting:** a postfix on `Pawn_DraftController.GetGizmos` that wraps the draft toggle's `toggleAction`
     (L162-170) for our pawn. That catches only the player's click, not auto-undraft, downing or mental states.
  3. **Optional:** a prefix on `FloatMenuOption.Chosen` with `colonistOrdering == true`, to catch picks that
     open a dialog or targeter without ordering a job.

#### Float menu without the UI (answers the open questions)
- **Calling `Chosen(false, null)` is safe with a null menu.** `floatMenu` is only used as
  `floatMenu?.PreOptionChosen` (`FloatMenuOption.cs:294-312`), and the game itself passes null
  (`Selector.cs:219`). `colonistOrdering: false` skips the order sound. `Chosen` has no tutor checks; those
  live in `DoGUI` (L443-456). Don't pass a `FloatMenuMap`, because its revalidation uses `Find.Selector`.
- **`FloatMenuMakerMap.GetOptions` (`FloatMenuMakerMap.cs:27-71`)**
  - It **doesn't filter by the selector**. It uses the list we pass, but it mutates it (so pass a fresh list).
  - It **requires `pawn.Map == Find.CurrentMap`** (L31, 35, 135-138), so it only works on the map the player
    is viewing.
  - It rejects downed or deathresting pawns and posts a visible `Messages.Message` saying why.
- **Things under the "click" come from `GenUI.ThingsUnderMouse`.** It hit-tests at `clickPos` but sorts, and
  includes adjacent stacked items, using the **real mouse** (`GenUI.cs:447, 467, 494, 509-511`). The options
  for the clicked cell are right, but their order and any adjacent items may vary. Use
  `cell.ToVector3Shifted()`.
- **Providers that misbehave when called from code:**
  - `DraftedMove` for multi-select (uses `Selector.gotoController`); single-pawn is fine.
  - `DressOtherPawn` (starts a targeter).
  - Options that open a dialog, count window or submenu: Equip confirm, HackAncientTerminal, Mechanitor,
    Xenogerm, LoadCaravan, LoadOntoPackAnimal, PickUpItem count, and `FloatMenuUtility` submenus.

  Blacklist these, or detect a change in `Find.WindowStack`. Thing and Comp `GetFloatMenuOptions` don't read
  the mouse or selector.
- **For plain moves, skip the float menu:** call the public static
  `FloatMenuOptionProvider_DraftedMove.PawnGotoAction(cell, pawn, RCellFinder.BestOrderedGotoDestNear(cell, pawn))`.

#### Snapshot sources (read everything on the main thread; many getters fill caches or shared static lists)
- **Needs:** `pawn.needs.AllNeeds`, `Need.LabelCap`, `CurLevelPercentage`, `ShowOnNeedList`.
- **Mood:**
  - `needs.mood.CurLevel` and `MoodString`;
  - `mindState.mentalBreaker.BreakThresholdMinor/Major/Extreme` and `Break*IsImminent`;
  - `pawn.InMentalState` and `MentalStateDef`.
- **Thoughts:**
  - `needs.mood.thoughts.GetDistinctMoodThoughtGroups(list)` and `MoodOffsetOfGroup(t)`. These use static temp
    lists and are O(n²).
  - Memories are `thoughts.memories.Memories` (`Thought_Memory`: `age`, `otherPawn`).
  - Situational thoughts are `thoughts.situational`.
- **Health:**
  - `health.hediffSet.hediffs` (`Visible`, `LabelCap`, `Part`, `Severity`, `BleedRate`);
  - `hediffSet.BleedRateTotal` and `PainTotal`;
  - `summaryHealth.SummaryHealthPercent`;
  - `health.Downed`, `HasHediffsNeedingTend()`;
  - `HealthUtility.GetGeneralConditionLabel`.
- **Skills:** `skills.skills` (`Level`, `passion`, `TotallyDisabled`).
- **Relations:**
  - `relations.DirectRelations` and `PawnRelationUtility.GetMostImportantRelation`;
  - `relations.OpinionOf(other)`, which is costly, so only call it for nearby or important pawns;
  - `OpinionExplanation`.
- **Job:** `pawn.GetJobReport()` (`Pawn.cs:3118`; uses the lord's report if there is one, and is guarded
  against exceptions), and `jobs.jobQueue`.
- **Identity:**
  - traits: `story.traits.allTraits`;
  - backstory: `story.Childhood/Adulthood.TitleCapFor(gender)` and `FullDescriptionFor(pawn).Resolve()`;
  - ideology: `pawn.Ideo`, which is null without Ideology;
  - age: `ageTracker.AgeBiologicalYears`.
- **Place and time:**
  - `pawn.GetRoom()` and `Room.GetRoomRoleLabel()`. `Room.Role` can recalculate, which is expensive in huge
    rooms.
  - `GenLocalDate.HourOfDay/Season(map)`;
  - `map.weatherManager.CurWeatherPerceived`;
  - `map.mapTemperature.OutdoorTemp` and `pawn.AmbientTemperature`.
- **Surroundings and danger:**
  - `GenRadial.RadialDistinctThingsAround` (keep the radius small);
  - `map.mapPawns.FreeColonistsSpawned` (a shared list, so copy it);
  - `GenHostility.AnyHostileActiveThreatToPlayer(map)`;
  - `map.dangerWatcher.DangerRating`.
  - Alerts: `AlertsReadout.activeAlerts` is private, so we'd need reflection to read it.
