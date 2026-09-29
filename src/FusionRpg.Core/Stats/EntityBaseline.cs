namespace FusionRpg.Core.Stats;

/// <summary>Immutable game baseline Y0 for one entity instance.</summary>
public sealed class EntityBaseline
{
    public long Hp { get; init; }
    public long MaxHp { get; init; }
    public long Atk { get; init; }
    public long Arm1 { get; init; }
    public long Arm1Max { get; init; }
    public long Arm2 { get; init; }
    public long Arm2Max { get; init; }

    /// <summary>
    /// E16's three, as the game reports them. Zero means "this entity has no such stat" — a zombie
    /// has no produce interval — and composing from a zero baseline yields zero, which the writer
    /// reads as "leave the field alone".
    /// </summary>
    public double AttackInterval { get; init; }
    public double ProduceInterval { get; init; }
    public double ZombieSpeed { get; init; }

    /// <summary>
    /// E38's twelve (spec-entity-fields-12plus.md), as the game reports them. Unlike E16's three
    /// above, every one of these is captured from a genuine live field on ITS OWN side
    /// (`EntityApply.cs` — a plant baseline always reads all eight plant fields, a zombie baseline
    /// always reads all four zombie fields), so a zero here is an ordinary value — "no shield yet",
    /// "no attack-speed adder applied" — never an absent stat. <see cref="StatComposer"/>'s
    /// <c>RealAlways</c>/<c>IntervalAlways</c> helpers compose these unconditionally for exactly
    /// that reason, deliberately not reusing <c>Real</c>/<c>Interval</c>'s "zero baseline is absent"
    /// early return.
    /// </summary>
    public long PlantShield { get; init; }
    public double AttackCountdown { get; init; }
    public double AttackSpeedAdder { get; init; }
    public double ProduceCountdown { get; init; }
    public double PlantSpeed { get; init; }
    public double PlantMoveSpeed { get; init; }
    public long PlantLevel { get; init; }
    public long ShootingLevel { get; init; }
    public double ArmorFlat { get; init; }
    public double TakeDmgMultiplier { get; init; }
    public double ZombieSpeedCurrent { get; init; }
    public double ZombieOriginSpeed { get; init; }
}

/// <summary>Resolved final Y — write-only to the game/sim.</summary>
public sealed class EntityFinal
{
    public long Hp { get; init; }
    public long MaxHp { get; init; }
    public long Atk { get; init; }
    public long Arm1 { get; init; }
    public long Arm1Max { get; init; }
    public long Arm2 { get; init; }
    public long Arm2Max { get; init; }

    /// <summary>Composed defense view for ScaleIncoming (percent like legacy StatMod, flat).</summary>
    public float DefensePercent { get; init; } = 1f;
    public long DefenseFlat { get; init; }

    /// <summary>
    /// E16's three. Composed like any other channel, then floored: an interval of zero is a
    /// divide-by-zero or an infinite fire rate depending on where it is read.
    /// </summary>
    public double AttackInterval { get; init; }
    public double ProduceInterval { get; init; }
    public double ZombieSpeed { get; init; }

    /// <summary>
    /// E38's twelve — see <see cref="EntityBaseline"/>'s own fields of the same names for why these
    /// compose unconditionally rather than through E16's "zero baseline is absent" path.
    /// <c>PlantShield</c>, <c>PlantLevel</c> and <c>ShootingLevel</c> are magnitudes (<c>long</c>,
    /// clamped to <c>int</c> only at the Unity write boundary); the rest are ratios/timers
    /// (<c>double</c>). <c>AttackCountdown</c>/<c>ProduceCountdown</c> get the same structural floor
    /// as <c>AttackInterval</c>/<c>ProduceInterval</c> — never zero or negative.
    /// </summary>
    public long PlantShield { get; init; }
    public double AttackCountdown { get; init; }
    public double AttackSpeedAdder { get; init; }
    public double ProduceCountdown { get; init; }
    public double PlantSpeed { get; init; }
    public double PlantMoveSpeed { get; init; }
    public long PlantLevel { get; init; }
    public long ShootingLevel { get; init; }
    public double ArmorFlat { get; init; }
    public double TakeDmgMultiplier { get; init; }
    public double ZombieSpeedCurrent { get; init; }
    public double ZombieOriginSpeed { get; init; }

    public IReadOnlyList<StatModifier> Contributions { get; init; } = Array.Empty<StatModifier>();

    /// <summary>
    /// True when composition produced anything the game does not already have — the **source-agnostic**
    /// replacement for asking each feature "did you contribute?".
    ///
    /// <para>Why this exists (2026-08-30): <c>EntityApply</c> decided whether to write by enumerating
    /// the contributors it knew about — Tab A scales, Tab B absolutes, PvzStats, effect-session mods.
    /// A contributor missing from that list composed correctly and was then silently dropped on the
    /// floor, with no error and no telemetry. Commander aptitudes were such a contributor: a 222-point
    /// <c>Might</c> allocation resolved to <c>appliedAtk = 31010</c> and wrote nothing. The list is
    /// unmaintainable by construction — every future producer (auras, atoms, items, injuries) has to
    /// remember to add itself, and nothing fails when it forgets.</para>
    ///
    /// <para><see cref="Derived.ActorHub"/> already answers the real question: <c>AppliedCombat</c> is
    /// the Writer input (actor-hub-ssot.md §7, stat-system.md — both state it unconditionally, with no
    /// contributor gate). So the write decision is one value comparison against the immutable game
    /// baseline: the RPG layer wants this entity to differ from vanilla, or it does not.</para>
    ///
    /// <para>Current <c>Hp</c> is compared against Y0, not against live Unity HP — both sides here are
    /// composed from the same baseline, so a damaged entity does not read as "changed" and this never
    /// fights Unity for ownership of current HP (effect-funnel.md §3).</para>
    /// </summary>
    public bool DiffersFrom(EntityBaseline y0)
    {
        if (y0 is null) throw new ArgumentNullException(nameof(y0));
        return Hp != y0.Hp
               || MaxHp != y0.MaxHp
               || Atk != y0.Atk
               || Arm1 != y0.Arm1
               || Arm1Max != y0.Arm1Max
               || Arm2 != y0.Arm2
               || Arm2Max != y0.Arm2Max
               || AttackInterval != y0.AttackInterval
               || ProduceInterval != y0.ProduceInterval
               || ZombieSpeed != y0.ZombieSpeed
               // E38: without these twelve here, a resolve where ONLY e.g. plantShield changed would
               // report "no change" and EntityWriteGate.ShouldWrite would never call the writer —
               // composing correctly and then silently dropping it on the floor, the exact class of
               // bug DiffersFrom itself exists to prevent (see this class's own header comment).
               || PlantShield != y0.PlantShield
               || AttackCountdown != y0.AttackCountdown
               || AttackSpeedAdder != y0.AttackSpeedAdder
               || ProduceCountdown != y0.ProduceCountdown
               || PlantSpeed != y0.PlantSpeed
               || PlantMoveSpeed != y0.PlantMoveSpeed
               || PlantLevel != y0.PlantLevel
               || ShootingLevel != y0.ShootingLevel
               || ArmorFlat != y0.ArmorFlat
               || TakeDmgMultiplier != y0.TakeDmgMultiplier
               || ZombieSpeedCurrent != y0.ZombieSpeedCurrent
               || ZombieOriginSpeed != y0.ZombieOriginSpeed
               // Baseline carries no defense fields: vanilla is the identity view (×1, +0).
               || DefensePercent != 1f
               || DefenseFlat != 0;
    }
}
