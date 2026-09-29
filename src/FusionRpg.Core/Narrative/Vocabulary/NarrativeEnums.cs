namespace FusionRpg.Core.Narrative.Vocabulary;

/// <summary>
/// npc-story-events `narrative-vocabulary`, map row 1 wave 0 (`spec-narrative-vocabulary.md` §3): the
/// runtime-only closed vocabularies. None of these is authored in a seed — a seed names a member and the
/// engine reads it — so they are C# declarations, and widening one is a reviewed change to this file plus
/// its pinned count, never a seed edit and never a code edit alone.
///
/// <para>Two rules hold for every enum in this file. (1) The member count is a DECLARATION pinned by
/// <c>NarrativeEnumsTests</c> with the reason it is closed. (2) Stored rows carry the member's wire id,
/// never the enum's ordinal: each enum has a <c>&lt;name&gt;Ids.ToId</c>/<c>TryParse</c> pair, so a member
/// appended later cannot renumber a saved row (the `CreaturePersonalityIds` shape,
/// `gk-core/src/FusionRpg.Core/Creatures/Contracts/ContractPolicy.cs:30-40`).</para>
/// </summary>
public enum HostClockKind
{
    /// <summary>`delve.room` — the Delve's own room-to-room clock.</summary>
    DelveRoom = 0,

    /// <summary>`world.turn` — a full world step (the world turn clock).</summary>
    WorldTurn = 1,

    /// <summary>`expedition.collect` — an expedition collect.</summary>
    ExpeditionCollect = 2,

    /// <summary>`sanctum.return` — homeworld on return.</summary>
    SanctumReturn = 3,
}

/// <summary>
/// `spec-narrative-vocabulary.md` §3, ideal §6.7: what a narrative row's scope is. A `Save`-scoped fact
/// belongs to the player's save (a mechanic first seen); a `World`-scoped fact belongs to one world and
/// outlives that world's attention and outcome (the round-4 ruling: nothing world-scoped is ever deleted).
/// Two members, pinned by <c>StoryScope_has_two</c>.
/// </summary>
public enum StoryScope
{
    Save = 0,
    World = 1,
}

/// <summary>
/// `spec-narrative-vocabulary.md` §3, ideal §6.3: the character's OWN story fate, written only by a
/// storylet outcome or a join. A world going dormant or frozen never writes <see cref="Departed"/> or
/// <see cref="Fallen"/> — that is <see cref="WorldNarrativePhase"/>, read BESIDE the fate (owner ruling
/// 2026-09-19, round 4). Four members, pinned by <c>CharacterFate_has_four</c>.
/// </summary>
public enum CharacterFate
{
    Present = 0,
    Joined = 1,
    Departed = 2,
    Fallen = 3,
}

/// <summary>
/// `spec-narrative-vocabulary.md` §3: <see cref="CharacterFate"/> plus whether a `met` fact exists —
/// derived, never stored. It is the value domain of the `CharacterStateIs` predicate leaf
/// (`spec-narrative-predicates.md`). Five members, pinned by <c>CharacterState_has_five</c>.
/// </summary>
public enum CharacterState
{
    Unmet = 0,
    Met = 1,
    Joined = 2,
    Departed = 3,
    Fallen = 4,
}

/// <summary>
/// `spec-narrative-vocabulary.md` §3 (owner ruling 2026-09-19, round 4): the narrative reading of
/// world-continuity's two stored axes — derived, never stored. <see cref="Live"/> = attention `active`
/// (drawn, written); <see cref="Dormant"/> = `hibernating` or `idle` (kept intact, never drawn, no writes
/// except the facts world-continuity's `CoarseStep` emits); <see cref="Frozen"/> = outcome `fallen`
/// (read-only history; the reserved `world-reclaim` may revive it). There is no "abandoned" member:
/// world-continuity deletes nothing and has no abandon state. Three members, pinned by
/// <c>WorldNarrativePhase_has_three</c>.
/// </summary>
public enum WorldNarrativePhase
{
    Live = 0,
    Dormant = 1,
    Frozen = 2,
}

/// <summary>
/// `spec-narrative-vocabulary.md` §3: the closed set of durable fact kinds the story ledger records —
/// the map's list plus the facts a ledger needs to derive fate, pins and scene eligibility without a
/// second store. Grouped exactly as §3 groups them. Twenty-seven members, pinned by
/// <c>StoryFactKind_has_twenty_seven</c>; `quest-sources` and `failure-branches` may add members as
/// reviewed changes.
/// </summary>
public enum StoryFactKind
{
    // relation (5)
    Met = 0,
    Helped = 1,
    Refused = 2,
    Betrayed = 3,
    Spared = 4,

    // story (3)
    FlagSet = 5,
    ChapterReached = 6,
    ArcStarted = 7,

    // engine (2)
    StoryletSeen = 8,
    ChoicePicked = 9,

    // scene (1)
    SceneAcknowledged = 10,

    // host (1)
    SanctumReturned = 11,

    // tutorial (1)
    MechanicFirstSeen = 12,

    // reward (2)
    RewardOwed = 13,
    RewardPaid = 14,

    // character (3)
    CharacterJoined = 15,
    CharacterDeparted = 16,
    CharacterFell = 17,

    // quest (6)
    QuestOffered = 18,
    QuestCompleted = 19,
    QuestFailed = 20,
    QuestAbandoned = 21,
    QuestExpired = 22,
    QuestProgressed = 23,

    // failure (3)
    SectorLost = 24,
    DelveWiped = 25,
    SiegeFailed = 26,
}

/// <summary>Wire ids for <see cref="HostClockKind"/>. These four strings ARE the clock keys a tuning file
/// names (`cooldown.perStorylet.{clock}` / `cooldown.perKind.{clock}`,
/// `spec-narrative-vocabulary.md` §4), so a rename here is a data migration, not a refactor.</summary>
public static class HostClockKindIds
{
    public static string ToId(this HostClockKind kind) => kind switch
    {
        HostClockKind.DelveRoom => "delve.room",
        HostClockKind.WorldTurn => "world.turn",
        HostClockKind.ExpeditionCollect => "expedition.collect",
        HostClockKind.SanctumReturn => "sanctum.return",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryParse(string? value, out HostClockKind kind)
    {
        switch (value)
        {
            case "delve.room": kind = HostClockKind.DelveRoom; return true;
            case "world.turn": kind = HostClockKind.WorldTurn; return true;
            case "expedition.collect": kind = HostClockKind.ExpeditionCollect; return true;
            case "sanctum.return": kind = HostClockKind.SanctumReturn; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>Wire ids for <see cref="StoryScope"/> — the value stored on a fact row.</summary>
public static class StoryScopeIds
{
    public static string ToId(this StoryScope scope) => scope switch
    {
        StoryScope.Save => "save",
        StoryScope.World => "world",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };

    public static bool TryParse(string? value, out StoryScope scope)
    {
        switch (value)
        {
            case "save": scope = StoryScope.Save; return true;
            case "world": scope = StoryScope.World; return true;
            default: scope = default; return false;
        }
    }
}

/// <summary>Wire ids for <see cref="CharacterFate"/> — the value stored on a character row.</summary>
public static class CharacterFateIds
{
    public static string ToId(this CharacterFate fate) => fate switch
    {
        CharacterFate.Present => "present",
        CharacterFate.Joined => "joined",
        CharacterFate.Departed => "departed",
        CharacterFate.Fallen => "fallen",
        _ => throw new ArgumentOutOfRangeException(nameof(fate), fate, null)
    };

    public static bool TryParse(string? value, out CharacterFate fate)
    {
        switch (value)
        {
            case "present": fate = CharacterFate.Present; return true;
            case "joined": fate = CharacterFate.Joined; return true;
            case "departed": fate = CharacterFate.Departed; return true;
            case "fallen": fate = CharacterFate.Fallen; return true;
            default: fate = default; return false;
        }
    }
}

/// <summary>Wire ids for <see cref="CharacterState"/>. Derived from the fate plus the `met` fact, so the
/// two `Unmet`/`Met` members have no fate of their own to store — the ids exist for a predicate's value
/// and a wire report, never for a column.</summary>
public static class CharacterStateIds
{
    public static string ToId(this CharacterState state) => state switch
    {
        CharacterState.Unmet => "unmet",
        CharacterState.Met => "met",
        CharacterState.Joined => "joined",
        CharacterState.Departed => "departed",
        CharacterState.Fallen => "fallen",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static bool TryParse(string? value, out CharacterState state)
    {
        switch (value)
        {
            case "unmet": state = CharacterState.Unmet; return true;
            case "met": state = CharacterState.Met; return true;
            case "joined": state = CharacterState.Joined; return true;
            case "departed": state = CharacterState.Departed; return true;
            case "fallen": state = CharacterState.Fallen; return true;
            default: state = default; return false;
        }
    }
}

/// <summary>Wire ids for <see cref="WorldNarrativePhase"/>.</summary>
public static class WorldNarrativePhaseIds
{
    public static string ToId(this WorldNarrativePhase phase) => phase switch
    {
        WorldNarrativePhase.Live => "live",
        WorldNarrativePhase.Dormant => "dormant",
        WorldNarrativePhase.Frozen => "frozen",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    public static bool TryParse(string? value, out WorldNarrativePhase phase)
    {
        switch (value)
        {
            case "live": phase = WorldNarrativePhase.Live; return true;
            case "dormant": phase = WorldNarrativePhase.Dormant; return true;
            case "frozen": phase = WorldNarrativePhase.Frozen; return true;
            default: phase = default; return false;
        }
    }
}

/// <summary>Wire ids for <see cref="StoryFactKind"/> — the value stored in a story-ledger row's `kind`
/// column, so these 27 strings are a data contract.</summary>
public static class StoryFactKindIds
{
    public static string ToId(this StoryFactKind kind) => kind switch
    {
        StoryFactKind.Met => "met",
        StoryFactKind.Helped => "helped",
        StoryFactKind.Refused => "refused",
        StoryFactKind.Betrayed => "betrayed",
        StoryFactKind.Spared => "spared",
        StoryFactKind.FlagSet => "flag.set",
        StoryFactKind.ChapterReached => "chapter.reached",
        StoryFactKind.ArcStarted => "arc.started",
        StoryFactKind.StoryletSeen => "storylet.seen",
        StoryFactKind.ChoicePicked => "choice.picked",
        StoryFactKind.SceneAcknowledged => "scene.acknowledged",
        StoryFactKind.SanctumReturned => "sanctum.returned",
        StoryFactKind.MechanicFirstSeen => "mechanic.first-seen",
        StoryFactKind.RewardOwed => "reward.owed",
        StoryFactKind.RewardPaid => "reward.paid",
        StoryFactKind.CharacterJoined => "character.joined",
        StoryFactKind.CharacterDeparted => "character.departed",
        StoryFactKind.CharacterFell => "character.fell",
        StoryFactKind.QuestOffered => "quest.offered",
        StoryFactKind.QuestCompleted => "quest.completed",
        StoryFactKind.QuestFailed => "quest.failed",
        StoryFactKind.QuestAbandoned => "quest.abandoned",
        StoryFactKind.QuestExpired => "quest.expired",
        StoryFactKind.QuestProgressed => "quest.progressed",
        StoryFactKind.SectorLost => "sector.lost",
        StoryFactKind.DelveWiped => "delve.wiped",
        StoryFactKind.SiegeFailed => "siege.failed",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    public static bool TryParse(string? value, out StoryFactKind kind)
    {
        switch (value)
        {
            case "met": kind = StoryFactKind.Met; return true;
            case "helped": kind = StoryFactKind.Helped; return true;
            case "refused": kind = StoryFactKind.Refused; return true;
            case "betrayed": kind = StoryFactKind.Betrayed; return true;
            case "spared": kind = StoryFactKind.Spared; return true;
            case "flag.set": kind = StoryFactKind.FlagSet; return true;
            case "chapter.reached": kind = StoryFactKind.ChapterReached; return true;
            case "arc.started": kind = StoryFactKind.ArcStarted; return true;
            case "storylet.seen": kind = StoryFactKind.StoryletSeen; return true;
            case "choice.picked": kind = StoryFactKind.ChoicePicked; return true;
            case "scene.acknowledged": kind = StoryFactKind.SceneAcknowledged; return true;
            case "sanctum.returned": kind = StoryFactKind.SanctumReturned; return true;
            case "mechanic.first-seen": kind = StoryFactKind.MechanicFirstSeen; return true;
            case "reward.owed": kind = StoryFactKind.RewardOwed; return true;
            case "reward.paid": kind = StoryFactKind.RewardPaid; return true;
            case "character.joined": kind = StoryFactKind.CharacterJoined; return true;
            case "character.departed": kind = StoryFactKind.CharacterDeparted; return true;
            case "character.fell": kind = StoryFactKind.CharacterFell; return true;
            case "quest.offered": kind = StoryFactKind.QuestOffered; return true;
            case "quest.completed": kind = StoryFactKind.QuestCompleted; return true;
            case "quest.failed": kind = StoryFactKind.QuestFailed; return true;
            case "quest.abandoned": kind = StoryFactKind.QuestAbandoned; return true;
            case "quest.expired": kind = StoryFactKind.QuestExpired; return true;
            case "quest.progressed": kind = StoryFactKind.QuestProgressed; return true;
            case "sector.lost": kind = StoryFactKind.SectorLost; return true;
            case "delve.wiped": kind = StoryFactKind.DelveWiped; return true;
            case "siege.failed": kind = StoryFactKind.SiegeFailed; return true;
            default: kind = default; return false;
        }
    }
}

/// <summary>
/// The one pure derivation from world-continuity's two stored columns to <see cref="WorldNarrativePhase"/>
/// (`spec-narrative-vocabulary.md` §3 and its interface table, which writes the call as
/// <c>WorldNarrativePhase.Of(attention, outcome)</c> — a C# enum cannot carry a static method, so the
/// operation lives on this plural static class, the same split `Disposition`/`DispositionCatalog` uses).
///
/// <para>The two input vocabularies are world-continuity's, closed by `decisions.md`'s "World lifecycle —
/// world-continuity (2026-09-19)" row and `world-continuity-map.md:55`: attention `active | hibernating |
/// idle`, outcome `contested | won | fallen`. They are validated here as INPUT GUARDS only — this class
/// declares no vocabulary of its own, and when `world-state-vocabulary` lands its C# type this method takes
/// that type and the six string consts below go. An unknown attention or outcome throws rather than
/// defaulting (§5's "reject, never default"): a world row this cannot read is a defect, and guessing
/// `Live` for it would draw story at a world the player cannot see.</para>
/// </summary>
public static class WorldNarrativePhases
{
    const string AttentionActive = "active";
    const string AttentionHibernating = "hibernating";
    const string AttentionIdle = "idle";
    const string OutcomeContested = "contested";
    const string OutcomeWon = "won";
    const string OutcomeFallen = "fallen";

    /// <summary>
    /// `active` → <see cref="WorldNarrativePhase.Live"/>, `hibernating`/`idle` →
    /// <see cref="WorldNarrativePhase.Dormant"/>, and outcome `fallen` →
    /// <see cref="WorldNarrativePhase.Frozen"/> **wins over attention** (a fallen world is read-only
    /// history whatever its attention column says — round-4 ruling).
    /// </summary>
    public static WorldNarrativePhase Of(string attention, string outcome)
    {
        var phase = attention switch
        {
            AttentionActive => WorldNarrativePhase.Live,
            AttentionHibernating or AttentionIdle => WorldNarrativePhase.Dormant,
            _ => throw new ArgumentException($"Unknown world attention '{attention}'.", nameof(attention))
        };

        return outcome switch
        {
            OutcomeFallen => WorldNarrativePhase.Frozen,   // fallen wins over attention
            OutcomeContested or OutcomeWon => phase,
            _ => throw new ArgumentException($"Unknown world outcome '{outcome}'.", nameof(outcome))
        };
    }
}
