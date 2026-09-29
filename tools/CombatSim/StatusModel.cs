using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// Status — the **fourth way to win**, and the one no arrow of the RPS cycle touches
/// (class-system-ideal.md §8.1). A status is not negated by dodge, not short-circuited by parry, and
/// not saturated by defence, so a distribution measured without it is measuring three of four axes.
///
/// <para><b>No status math here.</b> The apply contest runs through the shipped
/// <see cref="ResistanceEvaluator"/> — the same object <c>StatusRuntime.Apply</c> drives — so the
/// delta, the potency split, the apply roll and both net factors are the real ones. What this file
/// owns is only the DoT's per-round bookkeeping, because neither engine has a
/// <c>StatusRuntime</c> instance to tick.</para>
///
/// <para><b>All three categories, treated as what they are.</b> A <c>dot</c> is damage on a schedule.
/// A <c>cc</c> costs the target its turn — modelled as lost rounds, which is the crude version of what
/// the readiness model owns, and it is flagged as crude rather than passed off as the real thing. A
/// <c>contagion</c> spreads to a second host and a 1v1 has none, so it is <b>structurally
/// unmeasurable here</b> and reads as a DoT with its interesting half removed.</para>
/// </summary>
public sealed class StatusProfile
{
    /// <summary>A real id from the locked catalog, in <c>StatusCategoryRegistry</c>. `wither` is a
    /// `dot`, so it reads `status.power.dot` / `status.resist.dot` — the channels the aptitude
    /// distribution actually feeds.</summary>
    public string StatusId { get; init; } = "wither";

    /// <summary>The L2b category, resolved from the shipped registry — never authored here.</summary>
    public string Category => FusionRpg.Core.Status.StatusCategoryRegistry.GetRequiredCategory(StatusId);

    public bool IsDot => Category == FusionRpg.Core.Status.StatusL2bCategory.Dot;
    public bool IsCc => Category == FusionRpg.Core.Status.StatusL2bCategory.Cc;

    /// <summary><b>Contagion is unmeasurable in a duel, by construction.</b> Its whole mechanic is
    /// spreading to a second host, and a 1v1 has none — so a contagion status here is a DoT with the
    /// interesting half removed. Structural, like Focus scoring zero: not a flaw in the status, a
    /// limit of the harness.</summary>
    public bool IsContagion => Category == FusionRpg.Core.Status.StatusL2bCategory.Contagion;

    public static IReadOnlyList<string> AllIds =>
        FusionRpg.Core.Status.StatusCategoryRegistry.AllStatusIds.OrderBy(x => x, StringComparer.Ordinal).ToList();

    public StatusProfile With(string id) => new()
    {
        StatusId = id, MagnitudeShareOfBase = MagnitudeShareOfBase,
        BaseDurationRounds = BaseDurationRounds, GrantChance = GrantChance
    };

    /// <summary>Fraction of the hit's base damage the DoT deals PER ROUND before potency scaling.</summary>
    public double MagnitudeShareOfBase { get; init; } = 0.25;

    /// <summary>Rounds, before <c>durationNetFactor</c> scales it.</summary>
    public double BaseDurationRounds { get; init; } = 3.0;

    /// <summary>Chance the attack even attempts the status, before the resist contest
    /// (<c>StatusApplyRequest.GrantChance</c>).</summary>
    public double GrantChance { get; init; } = 1.0;

    public static StatusProfile Default => new();
}

/// <summary>The apply contest, resolved once and reusable by both engines.</summary>
public static class StatusMath
{
    sealed class SeededRng : IStatusRng
    {
        readonly Random _r;
        public SeededRng(Random r) => _r = r;
        public double NextUnit() => _r.NextDouble();
    }

    static readonly ResistanceEvaluator Evaluator = new();

    static ActorDerivedSnapshot Snapshot(Archetype a) =>
        ActorDerivedSnapshot.FromValues(
            a.Stats.ToDictionary(kv => kv.Key, kv => (kv.Value.Min + kv.Value.Max) / 2.0, StringComparer.Ordinal));

    /// <summary>
    /// The DETERMINISTIC read: apply probability and, if it lands, its scaled magnitude and duration.
    /// Forcing the roll to succeed (<c>NextUnit() = 0</c>) then reading <c>PFinal</c> separately is
    /// what makes this usable by the closed form — the probability and the payload are needed apart,
    /// and <see cref="ResistanceEvaluator.Evaluate"/> returns both in one pass.
    /// </summary>
    public static (double PApply, double Magnitude, double DurationRounds) Expected(
        Archetype atk, Archetype def, StatusProfile profile, double baseDamage)
    {
        // The deterministic read is Core's own StatusUptime.Expected — same shipped ResistanceEvaluator,
        // same StatusApplyRequest, same "applied ? payload : zeros" shape. Delegating removes the second
        // copy of that request construction; only the Archetype -> snapshot adapter below is tool-local.
        var o = OutcomeOf(atk, def, profile, baseDamage);
        return (o.PFinal, o.Magnitude, o.DurationRounds);
    }

    /// <summary>The CombatSim-shaped part: an <see cref="Archetype"/>'s mid-range stats as the
    /// <see cref="CombatActorSnapshot"/> Core's status read takes. This tool has no per-actor element
    /// typing, so both sides read Neutral — the status evaluator reads only <c>.Derived</c>.</summary>
    static StatusUptime.StatusOutcome OutcomeOf(
        Archetype atk, Archetype def, StatusProfile profile, double baseDamage) =>
        StatusUptime.Expected(
            profile.StatusId, profile.MagnitudeShareOfBase, profile.BaseDurationRounds, profile.GrantChance,
            new CombatActorSnapshot(Snapshot(atk), ActorElementTypes.Neutral),
            new CombatActorSnapshot(Snapshot(def), ActorElementTypes.Neutral),
            baseDamage);

    /// <summary>The rolled read, for the simulator. Same evaluator, a real draw.</summary>
    public static (bool Applied, double Magnitude, double DurationRounds) Roll(
        Archetype atk, Archetype def, StatusProfile profile, double baseDamage, Random rng)
    {
        var r = Evaluator.Evaluate(
            new StatusApplyRequest(
                StatusId: profile.StatusId,
                HostPtr: "def",
                AttackerPtr: "atk",
                BaseMagnitude: baseDamage * profile.MagnitudeShareOfBase,
                BaseDuration: profile.BaseDurationRounds,
                GrantChance: profile.GrantChance),
            Snapshot(atk), Snapshot(def), new SeededRng(rng));
        return (r.Applied, r.EffectiveMagnitude, r.EffectiveDuration);
    }

    /// <summary>
    /// Expected DoT damage PER ROUND, from steady-state uptime.
    ///
    /// <para><b>Corrected 2026-08-25 — the previous version over-counted badly.</b> It returned
    /// <c>p × magnitude × duration</c> per SWING, attributing a whole tail to every swing. But
    /// <c>StatusRuntime</c> semantics are <b>refresh, not stack</b> — and so is the duel runner — so
    /// only ONE instance is ever active. Over a 24-round fight the old form counted 24 overlapping
    /// applications of a 4-round DoT.</para>
    ///
    /// <para>With refresh, the DoT is active whenever it was applied within the last
    /// <c>duration</c> rounds, so its steady-state uptime is <c>1 − (1−p)^duration</c> and it deals
    /// <c>magnitude</c> per round while up:</para>
    ///
    /// <code>damage per round = (1 − (1 − p·pHit)^duration) × magnitude</code>
    ///
    /// <para>The over-count was written down in this file's own comment before it was used — and then
    /// a search optimised an allocation against it anyway. The simulator cross-check caught the
    /// reversal (30.4% residual, one arrow flipped), which is precisely the job claimed for it.</para>
    /// </summary>
    public static double ExpectedDotPerRound(
        Archetype atk, Archetype def, StatusProfile profile, double baseDamage, double pHit)
    {
        if (profile.IsCc) return 0;                       // cc costs turns, not hp
        // The uptime composition is Core's StatusUptime.ExpectedDotPerRound — one declaration of
        // 1-(1-p)^d for the whole repo. This method is now the IsCc veto plus the adapter.
        return StatusUptime.ExpectedDotPerRound(OutcomeOf(atk, def, profile, baseDamage), pHit);
    }

    /// <summary>
    /// CC, as the fraction of the TARGET's rounds it removes.
    ///
    /// <para>Each landed application disables the target for <c>duration</c> rounds, and applications
    /// arrive at <c>p</c> per swing, so in steady state the disabled share is the same refresh uptime
    /// Core's <see cref="StatusUptime.CcDisabledShare"/> declares.</para>
    ///
    /// <para><b>A lock is still reachable and still not clamped away.</b> Uptime approaches 1 as
    /// <c>p</c> or <c>duration</c> grows, so perma-CC remains expressible — it just now requires the
    /// numbers that actually produce it in play, rather than arriving at <c>p=0.34, dur=3</c> from an
    /// accumulation the runtime never performs.</para>
    /// </summary>
    public static double CcDisabledShare(Archetype atk, Archetype def, StatusProfile profile, double baseDamage)
    {
        if (!profile.IsCc) return 0;
        // Refresh, not stack — same correction as the DoT above, and Core owns the formula now.
        return StatusUptime.CcDisabledShare(OutcomeOf(atk, def, profile, baseDamage));
    }
}
