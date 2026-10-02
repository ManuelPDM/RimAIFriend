using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace AIPawnControl
{
    /// <summary>One numbered line of a choice: a plain label, and what picking it does (the result line, or null if it couldn't).</summary>
    public class ChoiceOption
    {
        public string id; // stable across scans (the label's counts may change while she thinks)
        public string label;
        public Func<Pawn, string> apply;
    }

    /// <summary>
    /// Something the game asks the colony (WORLD.md §2): a joiner or lodger who wants in, a ransom demand, a quest offer on the
    /// home map, beggars. Found fresh on every scan; the key says which one it is across scans.
    /// </summary>
    public class ColonyChoice
    {
        public string key;
        public ChoiceLetter letter;
        public Quest quest;
        public QuestPart_BegForItems beg;

        private static readonly AccessTools.FieldRef<DiaOption, string> OptionText = AccessTools.FieldRefAccess<DiaOption, string>("text");

        /// <summary>Hours left before vanilla opens its window for the player, or -1 for no deadline.</summary>
        public int HoursLeft
        {
            get
            {
                if (letter != null)
                    return letter.TimeoutActive ? (letter.disappearAtTick - Find.TickManager.TicksGame) / GenDate.TicksPerHour : -1;
                if (quest != null && beg == null)
                    return quest.TicksUntilExpiry >= 0 ? quest.TicksUntilExpiry / GenDate.TicksPerHour : -1;
                return -1;
            }
        }

        /// <summary>Whether the game still asks it (the player may have answered, the beggars may have left).</summary>
        public bool Open
        {
            get
            {
                if (letter != null)
                    return Find.LetterStack.LettersListForReading.Contains(letter) && letter.CanShowInLetterStack;
                if (beg != null)
                    return quest.State == QuestState.Ongoing && beg.target != null && beg.target.Spawned && GiveItemsToPawnUtility.ItemCountLeftToCollect(beg.target) > 0;
                return quest.State == QuestState.NotYetAccepted;
            }
        }

        private List<Pawn> Pawns()
        {
            var pawns = new List<Pawn>();
            if (letter is ChoiceLetter_AcceptVisitors visitors)
                pawns.AddRange(visitors.pawns);
            else if (letter is ChoiceLetter_RansomDemand ransom)
                pawns.Add(ransom.kidnapped);
            else if (letter != null)
            {
                pawns.AddRange(letter.lookTargets?.targets.Select(t => t.Thing).OfType<Pawn>() ?? Enumerable.Empty<Pawn>());
                pawns.AddRange(letter.quest?.QuestLookTargets.Select(t => t.Thing).OfType<Pawn>().Where(p => !p.IsFreeColonist) ?? Enumerable.Empty<Pawn>());
            }
            return pawns.Where(p => p != null).Distinct().ToList();
        }

        private string Names => Pawns().Count > 0 ? string.Join(" and ", Pawns().Select(p => p.LabelShort)) : "them";

        /// <summary>The choice as the player reads it, plus who the people are and the colony's charity belief.</summary>
        public string Describe()
        {
            var lines = new List<string>();
            if (letter != null)
                lines.Add($"{letter.Label.Resolve().StripTags()}: {letter.Text.Resolve().StripTags()}");
            else
            {
                lines.Add($"{quest.name}: {quest.description.Resolve().StripTags()}");
                lines.AddRange(quest.PartsListForReading.Select(p => p.DescriptionPart).Where(d => !d.NullOrEmpty()).Select(d => d.StripTags()));
            }
            foreach (var pawn in Pawns())
                lines.Add(PawnLine(pawn));
            if (beg != null)
                lines.Add($"They ask for {GiveItemsToPawnUtility.ItemCountLeftToCollect(beg.target)} {beg.thingDef.label}. We have {Stock(beg.target.Map, beg.thingDef)}.");
            // A joiner's letter already says who holds charity.
            bool charity = (letter as ChoiceLetter_AcceptVisitors)?.charity == true || (letter == null && quest.charity);
            var believers = charity ? IdeoUtility.AllColonistsWithCharityPrecept().Select(p => p.LabelShort).ToList() : new List<string>();
            if (believers.Count > 0)
                lines.Add($"Charity is a belief of {string.Join(", ", believers)}: turning people away upsets them.");
            int hours = HoursLeft;
            if (hours >= 0)
                lines.Add($"It must be decided within {hours} hours.");
            return string.Join("\n", lines);
        }

        /// <summary>"Willis, 34, male. Traits: kind, lazy. Skills: Social 8, Cooking 5. Can't do: dumb labor."</summary>
        private static string PawnLine(Pawn pawn)
        {
            var disabled = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(pawn.WorkTypeIsDisabled).Select(w => w.labelShort).ToList();
            return $"{pawn.LabelShort}, {pawn.ageTracker.AgeBiologicalYears}, {pawn.gender.GetLabel()}. " +
                   $"Traits: {string.Join(", ", pawn.story?.traits?.allTraits.Select(t => t.LabelCap.ToString()) ?? Enumerable.Empty<string>())}. " +
                   $"Skills: {SnapshotBuilder.Skills(pawn)}." + (disabled.Count > 0 ? $" Can't do: {string.Join(", ", disabled)}." : "");
        }

        private static int Stock(Map map, ThingDef def) =>
            map.listerThings.ThingsOfDef(def).Where(t => !t.IsForbidden(Faction.OfPlayer)).Sum(t => t.stackCount);

        /// <summary>The lines possible right now, for the decider. Built fresh each time; Answer matches them by index and label.</summary>
        public List<ChoiceOption> Options(Pawn by)
        {
            var options = new List<ChoiceOption>();
            if (letter != null)
            {
                var dia = letter.Choices.ToList();
                for (int i = 0; i < dia.Count; i++)
                {
                    string label = LetterLabel(i);
                    if (label == null || dia[i].disabled)
                        continue;
                    int index = i;
                    options.Add(new ChoiceOption { id = "letter" + i, label = label, apply = _ => AnswerLetter(index, label) });
                }
                return options;
            }
            if (beg != null)
            {
                int left = GiveItemsToPawnUtility.ItemCountLeftToCollect(beg.target);
                int have = Stock(beg.target.Map, beg.thingDef);
                if (have >= left && GiveItemsToPawnUtility.FindItemToGive(by, beg.thingDef) != null)
                    options.Add(new ChoiceOption { id = "give", label = $"give them {left} {beg.thingDef.label} (we have {have})", apply = Give });
                options.Add(new ChoiceOption { id = "refuse", label = "turn them away", apply = _ => "Turned the beggars away." });
                return options;
            }
            if (QuestUtility.CanAcceptQuest(quest))
            {
                var part = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault(p => p.choices.Count >= 2);
                if (part != null)
                    for (int i = 0; i < part.choices.Count; i++)
                    {
                        var choice = part.choices[i];
                        options.Add(new ChoiceOption { id = "accept" + i, label = "accept, for " + Rewards(choice.rewards), apply = p => Accept(p, part, choice) });
                    }
                else
                {
                    var rewards = quest.PartsListForReading.OfType<QuestPart_Choice>().SelectMany(p => p.choices).SelectMany(c => c.rewards).ToList();
                    options.Add(new ChoiceOption { id = "accept", label = rewards.Count > 0 ? "accept, for " + Rewards(rewards) : "accept", apply = p => Accept(p, null, null) });
                }
            }
            if (options.Count > 0)
                options.Add(new ChoiceOption { id = "pass", label = "let it pass", apply = _ => $"Let \"{quest.name}\" pass." });
            return options;
        }

        /// <summary>Our label for the letter's i-th button, or null for the ones a mind doesn't get (jump, postpone, view quest).</summary>
        private string LetterLabel(int i)
        {
            switch (letter)
            {
                case ChoiceLetter_AcceptJoiner _:
                    return i == 0 ? $"take {Names} in" : i == 1 ? $"turn {Names} away" : null;
                case ChoiceLetter_AcceptVisitors _:
                    return i == 0 ? $"take {Names} in" : i == 1 ? $"say no to {Names}" : null;
                case ChoiceLetter_RansomDemand ransom:
                    return i == 0 ? $"pay {ransom.fee} silver for {Names} (we have {Stock(ransom.map, ThingDefOf.Silver)})" : i == 1 ? "refuse to pay" : null;
            }
            return null;
        }

        private static string Rewards(List<Reward> rewards) => string.Join(", ", rewards.Select(RewardText).Where(t => t != null).DefaultIfEmpty("nothing listed"));

        private static string RewardText(Reward reward)
        {
            switch (reward)
            {
                case Reward_Items items:
                    return string.Join(", ", items.ItemsListForReading.Select(t => t.LabelNoParenthesis));
                case Reward_Goodwill goodwill:
                    return $"{goodwill.amount:+#;-#} goodwill with {goodwill.faction?.Name}";
                case Reward_RoyalFavor favor:
                    return $"{favor.amount} royal favor with {favor.faction?.Name}";
                case Reward_Pawn pawn:
                    return pawn.pawn != null ? $"{pawn.pawn.LabelShort} joins us ({pawn.pawn.KindLabel})" : "someone joins us";
            }
            return reward.GetDescription(default).StripTags(); // vanilla's own tooltip text (it passes default too)
        }

        private string AnswerLetter(int index, string label)
        {
            var dia = letter.Choices.ToList();
            if (index >= dia.Count || dia[index].disabled)
                return null;
            // AcceptVisitors' reject asks the player to confirm when the colony holds charity: do what the confirmation does.
            if (letter is ChoiceLetter_AcceptVisitors visitors && index == 1)
            {
                if (!visitors.rejectedSignal.NullOrEmpty())
                {
                    object arg = visitors.pawns.Count == 1 ? (object)visitors.pawns[0] : visitors.pawns;
                    Find.SignalManager.SendSignal(new Signal(visitors.rejectedSignal, arg.Named("SUBJECT")));
                }
                Find.LetterStack.RemoveLetter(letter);
            }
            else
                dia[index].action?.Invoke(); // never Activate(): it closes a dialog that isn't there
            if (Find.LetterStack.LettersListForReading.Contains(letter))
                ModLog.Warning($"Choice \"{letter.Label.Resolve()}\": the letter is still there after \"{OptionText(dia[index])}\".");
            return label.CapitalizeFirst() + ".";
        }

        private string Accept(Pawn by, QuestPart_Choice part, QuestPart_Choice.Choice choice)
        {
            if (!QuestUtility.CanAcceptQuest(quest))
                return null;
            Pawn accepter = QuestUtility.CanPawnAcceptQuest(by, quest) ? by
                : PawnsFinder.AllMaps_FreeColonistsSpawned.FirstOrDefault(p => QuestUtility.CanPawnAcceptQuest(p, quest));
            if (accepter == null && quest.RequiresAccepter)
                return null;
            if (part != null)
                part.Choose(choice);
            quest.Accept(accepter);
            return $"Accepted \"{quest.name}\".";
        }

        /// <summary>Vanilla's own give job (GiveItemsToPawnUtility's float menu); it keeps fetching until they have it all.</summary>
        private string Give(Pawn by)
        {
            Thing item = GiveItemsToPawnUtility.FindItemToGive(by, beg.thingDef);
            if (item == null)
                return null;
            Job job = JobMaker.MakeJob(JobDefOf.GiveToPawn, item, beg.target);
            job.haulMode = HaulMode.ToContainer;
            job.lord = beg.target.GetLord();
            var mind = MindManager.Instance?.MindOf(by);
            bool ordered = mind != null ? MindActions.Order(mind, job) : by.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            return ordered ? $"Went to give the beggars {GiveItemsToPawnUtility.ItemCountLeftToCollect(beg.target)} {beg.thingDef.label}." : null;
        }
    }

    /// <summary>
    /// Finds the colony's open choices and has a mind decide each one (WORLD.md). The decider is the mind with the best
    /// negotiation ability among those who can think now; the Decide call picks an option, code applies it, and the decision
    /// goes into the group chat as the decider's message, so everyone knows. The player can still answer first.
    /// </summary>
    public class ColonyChoices : GameComponent
    {
        private const float CheckSeconds = 2f; // real time: letters often pause the game, and the answer should come anyway
        private const int LeaveToPlayerHours = 2; // this close to the deadline, vanilla's window is the player's

        // Saved: quests let pass and beggars answered, so they're not asked again (letters go away once answered).
        private HashSet<string> handled = new HashSet<string>();

        // Not saved: the choice each deciding mind is thinking about. After a reload it's asked again.
        private readonly Dictionary<string, PawnMind> deciding = new Dictionary<string, PawnMind>();
        private float lastCheck;

        private static ColonyChoices instance;
        private readonly Game game;

        public static ColonyChoices Instance => instance != null && instance.game == Current.Game ? instance : null;

        public ColonyChoices(Game game)
        {
            this.game = game;
            instance = this;
        }

        /// <summary>Every open choice a mind may answer: not the player's own (world sites, pawn picks, babies, growth moments).</summary>
        public static List<ColonyChoice> All()
        {
            var choices = new List<ColonyChoice>();
            var letters = Find.LetterStack.LettersListForReading.OfType<ChoiceLetter>()
                .Where(l => l is ChoiceLetter_AcceptJoiner || l is ChoiceLetter_AcceptVisitors || l is ChoiceLetter_RansomDemand).ToList();
            foreach (var letter in letters)
                choices.Add(new ColonyChoice { key = "letter:" + letter.ID, letter = letter });
            foreach (var quest in Find.QuestManager.QuestsListForReading)
            {
                if (quest.hidden || quest.dismissed)
                    continue;
                if (quest.State == QuestState.NotYetAccepted && !letters.Any(l => l.quest == quest) && LeaveReason(quest) == null)
                    choices.Add(new ColonyChoice { key = "quest:" + quest.id, quest = quest });
                if (quest.State == QuestState.Ongoing && quest.PartsListForReading.OfType<QuestPart_BegForItems>().FirstOrDefault() is QuestPart_BegForItems beg)
                    choices.Add(new ColonyChoice { key = "beg:" + quest.id, quest = quest, beg = beg });
            }
            return choices.Where(c => c.Open).ToList();
        }

        /// <summary>Why a quest needs someone to leave the map (it stays the player's), or null. Also for the dev report.</summary>
        public static string LeaveReason(Quest quest)
        {
            if (quest.PartsListForReading.Any(p => p is QuestPart_SpawnWorldObject))
                return "a world site";
            if (quest.PartsListForReading.Any(p => p is QuestPart_LendColonistsToFaction))
                return "colonists leave by shuttle";
            var elsewhere = quest.QuestLookTargets.FirstOrDefault(t => t.HasWorldObject && !(t.WorldObject is RimWorld.Planet.MapParent m && m.HasMap && m.Map.IsPlayerHome));
            return elsewhere.HasWorldObject ? "away at " + elsewhere.WorldObject.LabelCap : null;
        }

        /// <summary>Every frame, also while paused; scans every 2 real seconds.</summary>
        public override void GameComponentUpdate()
        {
            if (UnityEngine.Time.realtimeSinceStartup - lastCheck < CheckSeconds || !AIPawnControlMod.Settings.answerChoices || Find.CurrentMap == null)
                return;
            lastCheck = UnityEngine.Time.realtimeSinceStartup;
            foreach (var key in deciding.Where(d => !d.Value.Thinking).Select(d => d.Key).ToList())
                deciding.Remove(key); // the call ended without an answer (failed, replaced): asked again below
            var minds = MindManager.Instance?.Minds;
            if (minds == null || minds.Count == 0)
                return;
            foreach (var choice in All())
            {
                if (handled.Contains(choice.key) || deciding.ContainsKey(choice.key))
                    continue;
                int hours = choice.HoursLeft;
                if (hours >= 0 && hours < LeaveToPlayerHours)
                    continue;
                var decider = Decider(minds);
                if (decider == null)
                    return; // the best negotiator is in another call, or nobody can think now: the next scan tries again
                Decide(decider, choice);
            }
        }

        /// <summary>
        /// The best negotiator among the minds that can think now. Null while she's in another call (Send would cancel it):
        /// the choice waits for her rather than going to someone else.
        /// </summary>
        private static PawnMind Decider(List<PawnMind> minds)
        {
            var best = minds.Where(m => m.persona != null && m.PausedReason() == null && !m.Unreachable)
                .OrderByDescending(m => m.pawn.GetStatValue(StatDefOf.NegotiationAbility)).FirstOrDefault();
            return best != null && !best.Thinking ? best : null;
        }

        /// <summary>Sends the Decide call. Returns false if the choice has nothing to pick.</summary>
        public bool Decide(PawnMind mind, ColonyChoice choice)
        {
            Pawn pawn = mind.pawn;
            var options = choice.Options(pawn);
            if (options.Count < 2)
                return false;
            string text = string.Join("\n", options.Select((o, i) => $"{i + 1}: {o.label}"));
            var messages = PromptBuilder.Build("decide", mind, new Dictionary<string, string>
            {
                ["choice"] = choice.Describe(),
                ["options"] = text,
            });
            var schema = Schema.Obj(new Dictionary<string, object>
            {
                ["reason"] = Schema.Reason(),
                ["choice"] = Schema.IntEnum(Enumerable.Range(1, options.Count)),
                ["say"] = Schema.Say(),
            });
            deciding[choice.key] = mind;
            ModLog.Message($"{pawn.LabelShort} decides \"{choice.key}\" with {options.Count} options:\n{text}");
            mind.Send("decide", messages, schema, reply => OnReply(mind, choice, options, reply),
                stillValid: () => !choice.Open ? "the choice is gone" : mind.PausedReason(ignoreSleep: true));
            return true;
        }

        private void OnReply(PawnMind mind, ColonyChoice choice, List<ChoiceOption> asked, Dictionary<string, object> reply)
        {
            deciding.Remove(choice.key);
            Pawn pawn = mind.pawn;
            int pick = reply.Int("choice", -1);
            if (pick < 1 || pick > asked.Count)
            {
                ModLog.Warning($"{pawn.LabelShort}: decide reply chose {pick}, which isn't on the list.");
                return;
            }
            string label = asked[pick - 1].label;
            string result = Answer(choice, asked[pick - 1].id, pawn);
            if (result == null)
                return;
            string say = SpeechLog.Clean(reply.Str("say"));
            GroupChat.Instance?.Add(pawn.LabelShort, $"(decided for the colony: {label})" + (say != null ? $" {say}" : ""));
            mind.AddDecision($"Decided for the colony: {result}" + (say != null ? $" Said: \"{say}\"" : ""), importance: 0); // the group chat recorded it
            ModLog.Message($"{pawn.LabelShort} decided \"{choice.key}\": {label} | {result} | Reason: {mind.lastReason}");
        }

        /// <summary>Applies the option with this id if the choice is still open and the option still possible. Null if not.</summary>
        public string Answer(ColonyChoice choice, string id, Pawn by)
        {
            var option = choice.Open ? choice.Options(by).FirstOrDefault(o => o.id == id) : null;
            string result = option != null ? MindActions.Safely(by, option.label, () => option.apply(by)) : null;
            if (result == null)
            {
                ModLog.Message($"Choice \"{choice.key}\": \"{id}\" isn't possible any more.");
                return null;
            }
            if (choice.letter == null)
                handled.Add(choice.key);
            return result;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref handled, "handled", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                handled = handled ?? new HashSet<string>();
        }
    }
}
