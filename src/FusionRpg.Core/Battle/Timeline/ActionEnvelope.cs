namespace FusionRpg.Core.Battle.Timeline;

/// <summary>Cooldown families. No global cooldown — no mechanic in this game needs a GCD.</summary>
public enum CooldownClass
{
    None,
    /// <summary>Shared across a named group; the group is <see cref="ActionEnvelope.CooldownKey"/>.</summary>
    Category,
    Specific
}

/// <summary>When a cooldown starts counting. Three games, three answers — so it is declared.</summary>
public enum CooldownStart
{
    Commit,
    Resolve,
    RecoveryEnd
}

/// <summary>What can break a committed action before it resolves.</summary>
public enum Interruptible
{
    Never,
    OnCC,
    OnDamage
}

/// <summary>When the action's request is snapshotted.</summary>
public enum Commitment
{
    /// <summary>Snapshotted at commit. If the target is gone at resolve, the action fizzles.</summary>
    EarlyBound,
    /// <summary>Re-read at resolve — the action-game convention.</summary>
    LateBound,
    /// <summary>Early-bound, but retargets instead of fizzling when the target is gone.</summary>
    EarlyBoundWithFallback
}

/// <summary>
/// The timing envelope of an action — the seam the combat action program plugs into. It says
/// *when* an action occupies the timeline and *how* it is scheduled; it says nothing about what
/// the action does. Damage, targeting shapes, and effects belong to the next program.
///
/// Naming note: <see cref="TimeCostTicks"/> is a **time** quantum feeding readiness. It is not a
/// resource cost — affordability is a selectability question and lives in the intent source. The
/// field was called `Cost` in an earlier draft, which invited reading it as mana.
/// </summary>
public sealed record ActionEnvelope
{
    /// <summary>Identity. Without it a report cannot name what fired.</summary>
    public string ActionId { get; init; } = "noop";

    /// <summary>Pre-speed time quantum consumed before the actor is ready again.</summary>
    public long TimeCostTicks { get; init; }

    /// <summary>
    /// Which derived channel readiness divides by. Naming it makes "moves fast, attacks slowly"
    /// expressible; hardcoding `turn.speed` would not.
    /// </summary>
    public string SpeedChannel { get; init; } = DerivedTurnChannels.Speed;

    /// <summary>
    /// Which <c>skill.cooldown.{category}</c> channel this action's cooldown reads for its reduction
    /// (spec-skill-modifiers.md §1.1 — closes action-map.md:177's gap; D3 repointed here rather than
    /// inventing a second cooldown-reduction mechanism on the envelope). A <b>reference</b>, mirroring
    /// <see cref="SpeedChannel"/> — no new channel is declared here, and unlike <c>SpeedChannel</c>
    /// there is no single universal default: which of the five categories applies is the action's own
    /// choice, so an action that declares none reads no reduction (<c>null</c>, not a guessed category).
    /// </summary>
    public string? CooldownChannel { get; init; }

    /// <summary>
    /// Which <c>skill.effectiveness.{category}</c> channel this action's output reads, mirroring
    /// <see cref="CooldownChannel"/> exactly (combat-unification `species-skills` S3). <c>null</c>
    /// means the action does not opt in and its damage is unscaled — the neutral path, and the
    /// default.
    /// </summary>
    public string? EffectivenessChannel { get; init; }

    public long WindupTicks { get; init; }

    /// <summary>
    /// Bounds a duration a stat could otherwise drive to zero (action-todo.md T12, decision D3).
    /// Null means no bound. Additive and inert until a resolver actually scales a duration by a
    /// stat — `A14`'s job — so these fields carry no behavior yet; they only reserve the columns.
    /// </summary>
    public long? DurationMinTicks { get; init; }
    public long? DurationMaxTicks { get; init; }

    /// <summary>Single hit at wind-up end. Genuinely immutable, and shared rather than re-allocated.</summary>
    static readonly IReadOnlyList<long> SingleResolve =
        new System.Collections.ObjectModel.ReadOnlyCollection<long>(new long[] { 0 });

    /// <summary>
    /// Offsets from the end of wind-up at which the action applies. Default is a single hit at 0;
    /// a multi-hit combo declares several rather than being modelled as several actions (which
    /// would triple its economy, cooldown, and slot accounting).
    ///
    /// The default is a <c>ReadOnlyCollection</c>, not a bare array: <see cref="NoOp"/> is a shared
    /// static, and an <c>IReadOnlyList</c> backed by <c>long[]</c> can be cast back and mutated —
    /// which would corrupt it for every caller at once.
    /// </summary>
    public IReadOnlyList<long> ResolveOffsets { get; init; } = SingleResolve;

    public long RecoveryTicks { get; init; }

    public CooldownClass Class { get; init; } = CooldownClass.None;
    public string? CooldownKey { get; init; }
    public long CooldownTicks { get; init; }
    public CooldownStart StartsAt { get; init; } = CooldownStart.Resolve;

    /// <summary>
    /// False for movement and periodic pulses. Without this every mid-action actor holds a slot,
    /// so at `W = 1` only one actor could ever move and a slot-free regeneration tick would be
    /// inexpressible.
    /// </summary>
    public bool SlotConsuming { get; init; } = true;

    /// <summary>
    /// Scheduling override for always-first effects. Part of the sort key, so it cannot be
    /// retrofitted later without moving every golden.
    /// </summary>
    public int PriorityBand { get; init; }

    public Interruptible Interruptible { get; init; } = Interruptible.OnCC;

    /// <summary>Per-mille of accrued readiness returned when an interrupt breaks this action.</summary>
    public int InterruptRefundMilli { get; init; }

    /// <summary>
    /// Per-mille of <see cref="CooldownTicks"/> an interrupt still charges (action-todo.md T12,
    /// decision D3). Defaults to <c>1000‰</c> — full cooldown — replacing
    /// <see cref="ActionRunner.Interrupt"/>'s previous behaviour of starting none at all. Inert for
    /// any action with <see cref="CooldownTicks"/> at zero, which is every action adopted so far.
    /// </summary>
    public int InterruptCooldownMilli { get; init; } = 1000;

    /// <summary>
    /// `battle-tempo` `commitment-binding` (spec §2.1, D6/D11): <c>null</c> means "no override — read
    /// the active profile's <see cref="BattleModeProfile.DefaultCommitment"/>", never a third
    /// magnitude of its own. Nullable specifically so an action that does not care can be
    /// distinguished from one deliberately locked to a value — the same shape
    /// <see cref="Timeline.TimelineProfileTuning.MaxRounds"/>/<c>RoundDurationMs</c> already use for
    /// "inherit the ruleset". Resolution order is envelope first, profile default second — the same
    /// precedence every other envelope field with a profile-level fallback uses.
    /// </summary>
    public Commitment? Commitment { get; init; }

    /// <summary>
    /// The zero-length action. Proves FSM plumbing, and nothing else — with every field at zero,
    /// wind-up, commitment, and slot contention are all unobservable, so it must never be the only
    /// thing the seam is validated against.
    /// </summary>
    public static readonly ActionEnvelope NoOp = new();

    // Records give value equality for free, but a compiler-generated Equals compares
    // ResolveOffsets by REFERENCE. That produced a trap where two structurally identical
    // envelopes were unequal, while two default ones were equal (they share SingleResolve) — so
    // equality silently changed meaning depending on whether the caller touched one field. Any
    // dedup, cache key, or golden comparison over envelopes would have inherited that.
    public bool Equals(ActionEnvelope? other) =>
        other is not null &&
        ActionId == other.ActionId &&
        TimeCostTicks == other.TimeCostTicks &&
        SpeedChannel == other.SpeedChannel &&
        CooldownChannel == other.CooldownChannel &&
        WindupTicks == other.WindupTicks &&
        DurationMinTicks == other.DurationMinTicks &&
        DurationMaxTicks == other.DurationMaxTicks &&
        RecoveryTicks == other.RecoveryTicks &&
        Class == other.Class &&
        CooldownKey == other.CooldownKey &&
        CooldownTicks == other.CooldownTicks &&
        StartsAt == other.StartsAt &&
        SlotConsuming == other.SlotConsuming &&
        PriorityBand == other.PriorityBand &&
        Interruptible == other.Interruptible &&
        InterruptRefundMilli == other.InterruptRefundMilli &&
        InterruptCooldownMilli == other.InterruptCooldownMilli &&
        Commitment == other.Commitment &&
        OffsetsEqual(ResolveOffsets, other.ResolveOffsets);

    /// <summary>Hand-rolled rather than <c>SequenceEqual</c>: LINQ allocates an enumerator, and
    /// kernel code is held to a zero-allocation standard by a source guard.</summary>
    static bool OffsetsEqual(IReadOnlyList<long> a, IReadOnlyList<long> b)
    {
        if (ReferenceEquals(a, b)) return true;      // also covers null == null
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>
    /// Value-based hash matching <see cref="Equals(ActionEnvelope?)"/>.
    ///
    /// <b>Not stable across processes.</b> <c>System.HashCode</c> is seeded per process, so this
    /// value differs run to run. That is fine for dictionary keying and caches — its only intended
    /// use — but it must never reach a golden, a persisted record, or anything else the
    /// byte-identical replay contract covers.
    /// </summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ActionId);
        hash.Add(TimeCostTicks);
        hash.Add(SpeedChannel);
        hash.Add(CooldownChannel);
        hash.Add(WindupTicks);
        hash.Add(DurationMinTicks);
        hash.Add(DurationMaxTicks);
        hash.Add(RecoveryTicks);
        hash.Add((int)Class);
        hash.Add(CooldownKey);
        hash.Add(CooldownTicks);
        hash.Add((int)StartsAt);
        hash.Add(SlotConsuming);
        hash.Add(PriorityBand);
        hash.Add((int)Interruptible);
        hash.Add(InterruptRefundMilli);
        hash.Add(InterruptCooldownMilli);
        hash.Add(Commitment); // nullable now (D6) -- HashCode.Add handles Commitment? directly
        // Indexed, not foreach: iterating an IReadOnlyList<long> boxes its enumerator — 32 bytes
        // per call, on the operation a cache key uses most. Same reason OffsetsEqual is a manual
        // loop rather than SequenceEqual.
        for (var i = 0; i < ResolveOffsets.Count; i++) hash.Add(ResolveOffsets[i]);
        return hash.ToHashCode();
    }
}
