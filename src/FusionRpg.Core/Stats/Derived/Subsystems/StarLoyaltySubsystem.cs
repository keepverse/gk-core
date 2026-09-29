using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Creatures.Fusion;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>One actor's star/loyalty/level inputs for <see cref="StarLoyaltySubsystem"/>.</summary>
public readonly record struct StarLoyaltyContribution(int Star, int Loyalty, int Level)
{
    public static StarLoyaltyContribution None => new(0, 0, 1);
}

/// <summary>
/// channelmods-hub (Wave 1a) — the ONE formula for the two squad-build producers that used to
/// invent combat magnitudes privately in <c>WebMatchService.StarChannelMods</c> /
/// <c>LoyaltyChannelMods</c>. Star ranks (spec-creature-fusion.md F8) and contract loyalty
/// (spec-creature-contracts.md G7) are flat per-mille shares of the actor's level base stats; the
/// arithmetic is identical whether it reaches battle as an ordinary ChannelMod (shim until
/// <c>battle-hub-fuse</c>) or a Hub derived contribution (sheet / post-fuse battle).
///
/// <para><b>Re-home only, no formula change</b> (plan Ask-first: "baseline seed formulas —
/// re-home only"). The two expressions below are byte-for-byte the pre-migration ones, including
/// the <c>star</c>/<c>rank-step</c> floor, so every battle and expedition golden is untouched.</para>
///
/// <para><b>SourceId grammar.</b> GG-49 has no star/loyalty family; per the plan's Ask-first default
/// ("reuse GG-49 families already in use") these mint <c>grant:star:{n}</c> / <c>grant:loyalty:{n}</c>
/// through <see cref="ContributionSourceIds.Grant"/>. A dedicated family is a tracked follow-up, not
/// invented here.</para>
/// </summary>
public static class StarLoyaltyBonus
{
    public readonly record struct ChannelBonus(long Power, long Defense);

    /// <summary>Star share, or null when the actor has no stars (the pre-migration empty case).</summary>
    public static ChannelBonus? Star(int star, int level)
    {
        if (star <= 0) return null;
        // The per-mille bonus is a CURVE indexed on the triangular sacrifice cost, not `star` times
        // a flat rate -- so `star` must not be multiplied in again. Floored at `star` so low-level
        // stars still register. Unchanged from WebMatchService.StarChannelMods.
        var power = Math.Max(star, BattleRuleset.BaseAtk(level) * StarPolicy.StarPowerMilli(star) / 1000);
        var defense = Math.Max(star, BattleRuleset.BaseDefense(level) * StarPolicy.StarDefenseMilli(star) / 1000);
        return new ChannelBonus(power, defense);
    }

    /// <summary>Loyalty share, or null at the Bound/Insubordinate bands that pay +0 per-mille.
    /// A non-positive loyalty is an ABSENT contract: it short-circuits before reading
    /// <see cref="ContractPolicy"/>, because 0 is always below <c>DeployFloor</c> (Insubordinate, +0)
    /// — same contribution, no tuning-hub read, so a host that never configured contracts still
    /// resolves star-only actors.</summary>
    public static ChannelBonus? Loyalty(int loyalty, int level)
    {
        if (loyalty <= 0) return null;
        var rank = ContractPolicy.RankFor(loyalty);
        var milli = ContractPolicy.RankBonusMilli(rank);
        if (milli <= 0) return null;
        // Floored at the rank step (Sworn 1 / Trusted 2 / Devoted 3) exactly like stars. Unchanged
        // from WebMatchService.LoyaltyChannelMods.
        var floor = (long)rank - 1;
        var power = Math.Max(floor, BattleRuleset.BaseAtk(level) * milli / 1000);
        var defense = Math.Max(floor, BattleRuleset.BaseDefense(level) * milli / 1000);
        return new ChannelBonus(power, defense);
    }
}

/// <summary>
/// Hub contributor for <see cref="StarLoyaltyBonus"/> — the registered producer the sheet and
/// (post-fuse) battle share, replacing the private <c>BattleChannelMod</c> fold.
///
/// <para>Per-context delegate, matching <see cref="AptitudeSubsystem"/> / <see cref="AtomDerivedSubsystem"/>:
/// the module owns resolving an actor's star/loyalty into channel values, not where those durable
/// values are stored. Order 100 — FlatSum is commutative, so it shares the tier with
/// progression/aptitude.</para>
///
/// <para>Deliberately contributes nothing when the actor has 0 stars and no paying loyalty band, so a
/// bare sheet resolve never forces <see cref="StarPolicy"/>/<see cref="ContractPolicy"/> configuration —
/// the pre-existing tests that never configured those hubs stay green.</para>
/// </summary>
public sealed class StarLoyaltySubsystem : IActorStatSubsystem
{
    readonly Func<StatContext, StarLoyaltyContribution> _contribution;

    public StarLoyaltySubsystem(Func<StatContext, StarLoyaltyContribution>? contribution = null) =>
        _contribution = contribution ?? (_ => StarLoyaltyContribution.None);

    public string SubsystemId => "rpg.star.loyalty";
    public int Order => 100;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var c = _contribution(ctx);
        var level = Math.Max(1, c.Level);

        if (StarLoyaltyBonus.Star(c.Star, level) is { } s)
        {
            var source = ContributionSourceIds.Grant($"star:{c.Star}");
            mods.Add(new DerivedModifier(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, s.Power, SourceId: source));
            mods.Add(new DerivedModifier(DerivedStatChannels.CombatDefenseOmni, DerivedModifierOp.Flat, s.Defense, SourceId: source));
        }

        if (StarLoyaltyBonus.Loyalty(c.Loyalty, level) is { } l)
        {
            var source = ContributionSourceIds.Grant($"loyalty:{c.Loyalty}");
            mods.Add(new DerivedModifier(DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat, l.Power, SourceId: source));
            mods.Add(new DerivedModifier(DerivedStatChannels.CombatDefenseOmni, DerivedModifierOp.Flat, l.Defense, SourceId: source));
        }
    }
}
