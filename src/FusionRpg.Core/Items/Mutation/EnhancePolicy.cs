using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// Everything an enhancement attempt reads. ⛔ <b>Like <see cref="Materials.RecipeContext"/> there is
/// nowhere here to put a player property</b> — D26 is enforced by the shape of the type, not by a
/// reviewer noticing. Every field is a property of the TARGET.
/// </summary>
/// <param name="RungIndex">0..9 on <see cref="RarityLadder.RungIds"/>. Never `rarity.ordinal`.</param>
/// <param name="ItemLevel">The content's number, not the player's.</param>
/// <param name="CurrentLevel">The item's current <c>+n</c>.</param>
/// <param name="PityCounter">Carried so the attempt can report the next counter value; it gates the
/// reroll tier guarantee, never the enhancement odds.</param>
/// <param name="WardLoaded">A <c>ward.enhance</c> on the attempt suppresses the downgrade half of a
/// peril failure.</param>
/// <param name="AssureLoaded">species-gear-chain T41 — how many `assurance.assure` charges are
/// loaded onto THIS attempt, exactly the same "player's per-attempt choice, not a persistent player
/// property" posture <see cref="WardLoaded"/> already holds (D26 is about identity/history, never
/// about what the player chose to spend on the one attempt this context describes). Never negative;
/// zero is the default, unloaded state.</param>
public readonly record struct EnhanceContext(
    int RungIndex, int ItemLevel, int CurrentLevel, int PityCounter, bool WardLoaded,
    int AssureLoaded = 0);

/// <summary>What one attempt decided, plus the counters it moves.</summary>
public readonly record struct EnhanceAttempt(
    EnhanceOutcome Outcome, int LevelAfter, int PityCounterAfter, int SuccessMilli, string BandId);

/// <summary>
/// spec-enhance-reroll.md §4/§4a — the scalar, the bands, the odds and the gain asymptote. Pure:
/// integer-only, no store, no file I/O, and every balance number arrives in
/// <see cref="EnhancementTuning"/>.
///
/// <para><b>⛔ There is no hard cap on <c>+X</c> anywhere in this class.</b> The cost curve (module
/// 14's) and the falling success curve are the cap, and both are configurable. The gain curve
/// <i>asymptotes</i> below one rung step rather than stopping at it (§4a), so a level is never
/// refused and <c>+1</c> at any <c>n</c> still buys something.</para>
/// </summary>
public static class EnhancePolicy
{
    static EnhancePolicy() => MutationRules.EnsureRegistered();

    /// <summary>
    /// §4's <c>ilvl_cap(ilvl) = max(floor, floor + ilvl/divisor)</c> — <b>a floor with no ceiling.</b>
    /// It bounds how far ONE item of a given level can be pushed; it bounds nothing about
    /// progression, because item level is itself unbounded (ilvl 128 → +36, ilvl 500 → +129). Both
    /// numbers are tunable.
    /// </summary>
    public static int MaxLevelForItemLevel(int itemLevel, EnhancementTuning t)
    {
        if (itemLevel < 0) throw new ArgumentOutOfRangeException(nameof(itemLevel), itemLevel, "item level cannot be negative");
        return Math.Max(t.IlvlCapFloor, t.IlvlCapFloor + itemLevel / t.IlvlCapDivisor);
    }

    /// <summary>The band a given target level falls in. The top band is open, so this never fails.</summary>
    public static EnhanceBand BandFor(int targetLevel, EnhancementTuning t)
    {
        if (targetLevel < 1) throw new ArgumentOutOfRangeException(nameof(targetLevel), targetLevel, "the first enhancement is +1");
        foreach (var band in t.Bands)
            if (targetLevel >= band.FromLevel && (band.ToLevel is not { } to || targetLevel <= to))
                return band;

        // Unreachable while the loader enforces contiguity and an open top band; kept as a throw
        // rather than a default so a loader regression is loud.
        throw new InvalidOperationException($"no enhancement band covers +{targetLevel}");
    }

    /// <summary>
    /// The success chance for the attempt that would reach <paramref name="targetLevel"/>, in
    /// per-mille. Linear inside the band, then held at the band's own end value — which the loader
    /// forces above zero, so <b>the odds never reach a luck wall</b> (D7).
    /// </summary>
    public static int SuccessMilli(int targetLevel, EnhancementTuning t)
    {
        var band = BandFor(targetLevel, t);
        if (band.SpanLevels == 0 || band.SuccessStartMilli == band.SuccessEndMilli) return band.SuccessStartMilli;

        var into = Math.Min(targetLevel - band.FromLevel, band.SpanLevels);
        var drop = (long)(band.SuccessStartMilli - band.SuccessEndMilli) * into / band.SpanLevels;
        return checked((int)(band.SuccessStartMilli - drop));
    }

    /// <summary>
    /// §4a's curve in per-MILLION: <c>gain(n) = enhance_cap × n / (n + K)</c>. Micro rather than
    /// milli because the milli form ties under integer division at high <c>n</c> and a tie reads
    /// like a stop; the exact question "is +1 worth anything" is answered by
    /// <see cref="GainIsStrictlyIncreasing"/>, which never rounds at all.
    /// </summary>
    public static long GainMicro(int level, int enhanceCapMilli, EnhancementTuning t)
    {
        if (level < 0) throw new ArgumentOutOfRangeException(nameof(level), level, "enhancement level cannot be negative");
        if (enhanceCapMilli < 0) throw new ArgumentOutOfRangeException(nameof(enhanceCapMilli), enhanceCapMilli, "enhance_cap cannot be negative");
        // Widen before multiplying; the single divide happens once, last.
        return checked((long)enhanceCapMilli * 1000L * level) / (level + t.AsymptoteK);
    }

    /// <summary>The same curve in per-mille, for display and for the horizon report.</summary>
    public static long GainMilli(int level, int enhanceCapMilli, EnhancementTuning t) =>
        GainMicro(level, enhanceCapMilli, t) / 1000;

    /// <summary>
    /// <b>The no-hard-stop property, decided exactly.</b> <c>cap·a/(a+K) &lt; cap·b/(b+K)</c> for
    /// <c>a &lt; b</c> is compared by cross-multiplication in <c>long</c> — no rounding, so the
    /// answer is the mathematical one at every <c>n</c>, not the one a per-mille render can tie at.
    /// </summary>
    public static bool GainIsStrictlyIncreasing(int a, int b, int enhanceCapMilli, EnhancementTuning t)
    {
        if (enhanceCapMilli <= 0) return false;
        var left = checked((long)enhanceCapMilli * a * (b + t.AsymptoteK));
        var right = checked((long)enhanceCapMilli * b * (a + t.AsymptoteK));
        return left < right;
    }

    /// <summary>
    /// I6 §3.3's NAIVE linear track — <c>+scalarPerLevelMilli</c> per level, never compounded. Kept
    /// because §4b's crafting-horizon table is computed against it and because it is the shape §4a
    /// replaced; <b>it is not what the shipped item gains.</b>
    /// </summary>
    public static long LinearGainMilli(int level, EnhancementTuning t) =>
        checked((long)t.ScalarPerLevelMilli * level);

    /// <summary>
    /// One origin magnitude carried to its enhanced value. <c>long</c> throughout, widened before
    /// multiplying, divided by 1,000,000 exactly once at the end; overflow throws rather than wraps
    /// (a +129 t5 affix at ilvl 500 is not an <c>int</c>).
    /// </summary>
    public static long ScaledValue(long originValue, int level, int enhanceCapMilli, EnhancementTuning t)
    {
        var gainMicro = GainMicro(level, enhanceCapMilli, t);
        return checked(originValue + originValue * gainMicro / 1_000_000L);
    }

    /// <summary>True when this level draws a milestone atom — every stride-th level, forever.</summary>
    public static bool IsMilestoneLevel(int level, EnhancementTuning t) =>
        level > 0 && level % t.MilestoneStride == 0;

    /// <summary>
    /// Resolve one attempt. The die comes from the op's own named stream
    /// (<see cref="MutationOpKinds.StreamName"/>), so an extra roll in reroll never shifts
    /// enhancement's sequence.
    ///
    /// <para>Refuses — by name, never silently — an attempt past the target's own
    /// <see cref="MaxLevelForItemLevel"/>. That is a property of the ITEM, not a progression
    /// ceiling: the same player enhances a higher-ilvl item further, without limit.</para>
    ///
    /// <para>species-gear-chain T41: <paramref name="assureBonusMilli"/> is
    /// <c>CraftAssuranceTuning.AssureBonusMilli</c>, handed in by the caller (Core never reads a
    /// tuning file) — the per-mille bonus ONE <see cref="EnhanceContext.AssureLoaded"/> charge
    /// grants. <c>effectiveSuccess = min(1000, SuccessMilli + AssureLoaded × assureBonusMilli)</c> is
    /// applied BEFORE the roll and is what <see cref="EnhanceAttempt.SuccessMilli"/> reports, so the
    /// odds shown are the odds rolled. The <c>min(1000, …)</c> bounds a PROBABILITY, never a
    /// magnitude (a bounded ratio, exempt from the no-hard-ceiling rule by construction) — but
    /// loading PAST certainty refuses by name (<c>enhance.assure-overloaded</c>) rather than silently
    /// wasting the excess charges the clamp would otherwise discard.</para>
    /// </summary>
    public static EnhanceAttempt Resolve(
        EnhanceContext ctx, SeededRng rng, EnhancementTuning t, out AtomRejection refusal,
        int assureBonusMilli = 0)
    {
        refusal = AtomRejection.Ok;
        var target = ctx.CurrentLevel + 1;

        if (ctx.CurrentLevel < 0)
            throw new ArgumentOutOfRangeException(nameof(ctx), ctx.CurrentLevel, "current enhancement level cannot be negative");
        if (ctx.RungIndex < 0 || ctx.RungIndex >= RarityLadder.RungIds.Count)
            throw new ArgumentOutOfRangeException(nameof(ctx), ctx.RungIndex,
                $"RungIndex is outside 0..{RarityLadder.RungIds.Count - 1} — this is the rung INDEX, never rarity.ordinal");
        if (ctx.AssureLoaded < 0)
            throw new ArgumentOutOfRangeException(nameof(ctx), ctx.AssureLoaded, "assure charge count cannot be negative");

        var max = MaxLevelForItemLevel(ctx.ItemLevel, t);
        if (target > max)
        {
            refusal = MutationRules.Violated("enhance.item-level-cap",
                $"+{target} is past this item's own level cap of +{max} at item level {ctx.ItemLevel} — " +
                "raise the item level (unbounded), this is a property of the target and not a progression ceiling");
            return new EnhanceAttempt(EnhanceOutcome.Failure, ctx.CurrentLevel, ctx.PityCounter, 0, BandFor(target, t).Id);
        }

        var baseSuccess = SuccessMilli(target, t);
        var band = BandFor(target, t);

        // Widen before multiplying: AssureLoaded x assureBonusMilli, both caller-controlled ints.
        var boosted = (long)baseSuccess + (long)ctx.AssureLoaded * assureBonusMilli;
        if (boosted > 1000)
        {
            refusal = MutationRules.Violated("enhance.assure-overloaded",
                $"loading {ctx.AssureLoaded} assure charge(s) at {assureBonusMilli}‰ each would push success to " +
                $"{boosted}‰, past certainty (1000‰) — load fewer charges");
            return new EnhanceAttempt(EnhanceOutcome.Failure, ctx.CurrentLevel, ctx.PityCounter, baseSuccess, band.Id);
        }
        var success = (int)Math.Min(1000L, boosted);

        var roll = (int)rng.NextUInt(1000) + 1; // 1..1000

        if (roll <= success)
            return new EnhanceAttempt(EnhanceOutcome.Success, target, ctx.PityCounter, success, band.Id);

        var downgrades = band.CanDowngrade && target >= t.DowngradeFromLevel && !ctx.WardLoaded && ctx.CurrentLevel > 0;
        return new EnhanceAttempt(
            downgrades ? EnhanceOutcome.FailureWithDowngrade : EnhanceOutcome.Failure,
            downgrades ? ctx.CurrentLevel - 1 : ctx.CurrentLevel,
            checked(ctx.PityCounter + 1),
            success,
            band.Id);
    }
}
