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
- Not yet verified: whether `Chosen(true, null)` accepts a null menu, and whether any provider reads
  `Find.Selector` or `UI.MouseCell`. The fallback is calling `option.action()` directly.
