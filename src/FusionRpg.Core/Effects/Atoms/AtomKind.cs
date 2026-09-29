using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// The seam an atom kind hooks into. Seven as of E41, guarded by the "Atom attach points" row in
/// decisions.md (that row did not exist before E35 — <c>AtomKindRegistryTests.cs</c> is the guard
/// test it names) — this is the list that keeps the vocabulary finite and auditable.
/// </summary>
public enum AttachPoint
{
    Stat,
    Resource,
    Status,
    Shield,
    Board,

    /// <summary>
    /// E35 (spec-match-modify.md §2.1): a rule the whole match is played under, naming no cell —
    /// distinct from <see cref="Board"/>, which acts on a cell or an entity within the running match.
    /// `match.modify` is the first (and, on this attach point, still only) kind.
    /// </summary>
    Match,

    /// <summary>
    /// E41 (spec-ui-attach-point.md §2a): read-only by construction, not by convention. A kind on
    /// this attach point shows the player a number, a banner or a meter — it never writes stat,
    /// resource, status, shield, board, currency or Unity combat state, and it never reads state
    /// back into content (a HUD is an output). `ui.present` is the first (and, today, only) kind —
    /// see <c>EffectBag.FireGrant</c>'s own bag-side branch for the sink separation this guarantees.
    /// </summary>
    Ui,

    /// <summary>
    /// base-defense `siege-construction` (decision 27, 2026-09-06): acts on the TACTICAL SIEGE BOARD —
    /// `FusionRpg.Core.Battle.Board.BoardState`/`GridSpec` — never the PvZ lawn. Distinct from
    /// <see cref="Board"/> on purpose: every existing `Board`-attached kind (`spawn.entity`,
    /// `board.action`, `grid.spawn`, `grid.clear`, `box.set`) is Battle=<see cref="RuntimeState.None"/>
    /// and Lawn=Full/Partial — they only ever reach Unity `GridItem`/`Plant`/`Zombie` objects through
    /// `InjectorEffectActionSink`. This attach point is the reverse: Lawn=None always (there is no
    /// tactical board there), Battle carries the real executor. `structure.place` is the first (and,
    /// today, only) kind.
    /// </summary>
    Siege,

    /// <summary>
    /// passive-tree `element-conversion` (D56, spec-element-conversion.md §2a): writes a combat-math
    /// INPUT STRUCTURE — a weighted <c>ElementPayload</c> component list — never a channel value
    /// (<see cref="Stat"/>) and never a cell/entity action (<see cref="Board"/>, which every existing
    /// kind on it acts against). A kind here reweights the ATTACKING ACTOR'S OWN in-flight payload,
    /// naming no cell, the same "no cell" shape that already separated <see cref="Match"/> from
    /// <see cref="Board"/>. `element.convert` is the first (and, today, only) kind.
    /// </summary>
    Element,
}

/// <summary>
/// How well one runtime can execute a kind. **Four states, not a boolean** — the audited matrix
/// has more than yes/no in it, and collapsing <see cref="PlanOnly"/> into <see cref="Full"/>
/// would make sim silently accept bindings it cannot execute.
/// </summary>
public enum RuntimeState
{
    /// <summary>No consumer. Binding here is a rejection.</summary>
    None = 0,

    /// <summary>Executes end to end.</summary>
    Full,

    /// <summary>Executes only through a named side path — e.g. status.apply in battle, setup only.</summary>
    Partial,

    /// <summary>Produces a plan, applies nothing. Accepted only by a host that declares itself a planner.</summary>
    PlanOnly,
}

/// <summary>Per-runtime support. Audited fact, re-verified against code before a cell changes.</summary>
public readonly record struct RuntimeSupportMatrix(RuntimeState Lawn, RuntimeState Battle, RuntimeState Sim)
{
    public RuntimeState For(RuntimeId runtime) => runtime switch
    {
        RuntimeId.Lawn => Lawn,
        RuntimeId.Battle => Battle,
        RuntimeId.Sim => Sim,
        _ => RuntimeState.None,
    };
}

public enum RuntimeId
{
    Lawn,
    Battle,
    Sim,
}

/// <summary>
/// The 13 triggers an atom's <c>when</c> may name. E1 owns this because E4 validates against it at
/// load and nothing else did. <c>OnTimer</c> is included even though `effect-system.md` never gave
/// it an FT number — it is real in code and in `effect-data.md`. <c>OnActivate</c> is the eighth,
/// added A18b (spec-on-activate-trigger.md) — a cross-program vocabulary change, reviewed via that
/// spec, not a unilateral addition. <c>OnWave</c>/<c>OnMatchStart</c>/<c>OnMatchEnd</c>/
/// <c>OnSunCollect</c>/<c>OnGridPlace</c> are E34's five (spec-trigger-vocabulary.md) — match-scoped
/// or board-economy input, no kind or executor of their own.
/// </summary>
public static class AtomTriggers
{
    // ⛔ ONE DECLARATION PER TRIGGER. Owner ruling 2026-09-16: "we only have atom ssot" — the atom
    // vocabulary is the single source of truth for what an atom's `when` may name, and THIS class is
    // that surface. The string literals themselves live one assembly down, in
    // `FusionRpg.Contracts.EffectTriggers`, for a structural reason and not a stylistic one:
    // `EffectBag` matches triggers BY STRING across the Core/Contracts boundary, `FusionRpg.Core`
    // references `FusionRpg.Contracts`, and Contracts references nothing at all — so Contracts is the
    // only assembly both sides can see, and a literal declared here could never be the one the wire
    // and the DTOs use.
    //
    // ⚠️ These used to be 13 literals here and 13 more in `EffectTriggers`, each list carrying a
    // comment telling the next reader that "the two constants must be byte-identical." Two hand-kept
    // copies of a closed vocabulary is exactly the shape this repo bans; a pairwise parity test
    // (`TriggerVocabularyTests.Every_AtomTrigger_has_an_ordinally_equal_EffectTrigger_pairwise_and_no_others_exist`)
    // was holding them equal, which is the right guard for a duplicate that cannot be removed, and
    // the wrong answer when it can. Aliasing removes the duplicate outright: there is now exactly one
    // literal per trigger in the whole tree, so the two lists cannot disagree even in principle, and
    // that parity test now passes by construction rather than by vigilance. It is kept — it is what
    // fails if someone re-introduces a literal here.
    public const string OnSpawn = EffectTriggers.OnSpawn;
    public const string OnDamageDealt = EffectTriggers.OnDamageDealt;
    public const string OnDamageTaken = EffectTriggers.OnDamageTaken;
    public const string OnDeath = EffectTriggers.OnDeath;
    public const string OnGranted = EffectTriggers.OnGranted;
    public const string OnRemoved = EffectTriggers.OnRemoved;
    public const string OnTimer = EffectTriggers.OnTimer;
    public const string OnActivate = EffectTriggers.OnActivate;
    public const string OnWave = EffectTriggers.OnWave;
    public const string OnMatchStart = EffectTriggers.OnMatchStart;
    public const string OnMatchEnd = EffectTriggers.OnMatchEnd;
    public const string OnSunCollect = EffectTriggers.OnSunCollect;
    public const string OnGridPlace = EffectTriggers.OnGridPlace;

    public static readonly string[] All =
        {
            OnSpawn, OnDamageDealt, OnDamageTaken, OnDeath, OnGranted, OnRemoved, OnTimer, OnActivate,
            OnWave, OnMatchStart, OnMatchEnd, OnSunCollect, OnGridPlace
        };

    /// <summary>The four that fire from a board event.</summary>
    public static readonly string[] Events = { OnSpawn, OnDamageDealt, OnDamageTaken, OnDeath };

    /// <summary>
    /// Grant attach / detach. These are <b>runtime lifecycle states, not authorable triggers</b>
    /// (definitions.md §14.2). A permanent modifier is applied when granted and reverted when
    /// removed — the bag injects the revert itself. Letting content name only the OnGranted half
    /// was how a permanent buff could leak, so no kind carries these.
    /// </summary>
    public static readonly string[] Lifecycle = { OnGranted, OnRemoved };

    /// <summary>
    /// An actor's own decision to act, independent of any board event or grant lifecycle — the third
    /// category `OnActivate` starts (A18b). Not a board event (no target has necessarily been
    /// damaged, spawned, or killed) and not a lifecycle transition (the grant that owns this atom was
    /// already bound, possibly turns ago, at loadout compile — A18a).
    /// </summary>
    public static readonly string[] Actions = { OnActivate };

    /// <summary>E34 (spec-trigger-vocabulary.md §2.1): match-scoped — no actor, no target. A kind
    /// carrying one of these must be able to act with no entity in hand.</summary>
    public static readonly string[] MatchEvents = { OnWave, OnMatchStart, OnMatchEnd };

    /// <summary>E34: board-economy events. They carry a ptr sometimes and must never require one.</summary>
    public static readonly string[] BoardEconomyEvents = { OnSunCollect, OnGridPlace };

    /// <summary>A permanent modifier declares no trigger at all — it is not event-driven.</summary>
    public static readonly string[] None = Array.Empty<string>();

    public static bool IsKnown(string? trigger) =>
        trigger is not null && Array.Exists(All, t => string.Equals(t, trigger, StringComparison.Ordinal));
}

/// <summary>
/// Which power categories a kind can contribute to — where a price lands, never how it is computed.
/// The cost function itself is E9's; a kind carries no hook, because a singular "magnitude" cannot
/// describe a kind holding several value specs (spawn.entity uses count as a multiplier and
/// hp/atk as the spawned body).
/// </summary>
[Flags]
public enum PowerCategory
{
    None = 0,
    Offense = 1,
    Survivability = 2,
    Control = 4,
    Utility = 8,
    Economy = 16,
}

/// <summary>
/// One kind: the mechanism, never a magnitude and never a content id. Adding a kind is a reviewed
/// code change because a kind without an executor is dead on arrival — the same rule that keeps
/// <c>status.expose.*</c> from being repeated.
/// </summary>
public sealed record AtomKind(
    string KindId,
    AttachPoint Attach,
    ParamSchema Params,
    RuntimeSupportMatrix Support,
    IReadOnlyList<string> Triggers,
    PowerCategory Categories,
    string Note = "",
    // A18e (spec-battle-live-stat-modifiers.md §4): every kind before stat.modify was either
    // Triggers.Count == 0 (no trigger allowed, none required -- the permanent-modifier case) or
    // Count > 0 (some triggers allowed, one required -- AtomRowValidator.ValidateWhen's own "mirror
    // case" inference). stat.modify's OnActivate widen needed a THIRD case neither binary covered:
    // triggers allowed, but still not required, since "permanent, no-trigger" must keep working
    // exactly as it did when Triggers was empty. Defaults false so every other kind's existing
    // Count>0-implies-required inference is completely unchanged.
    bool TriggerOptional = false)
{
    public RuntimeState SupportIn(RuntimeId runtime) => Support.For(runtime);

    /// <summary>True when this kind may carry that trigger. Unknown triggers are never "allowed".</summary>
    public bool AllowsTrigger(string? trigger) =>
        trigger is not null && Triggers.Contains(trigger, StringComparer.Ordinal);
}
