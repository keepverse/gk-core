using FusionRpg.Core.Power;

namespace FusionRpg.Core.Creatures.Patron;

/// <summary>Computed patron aura — per-mille combat bonuses on the patron's element channels.
/// Primary carries full power + half defense; a secondary element gets half of each.
///
/// <para><b>`long`, not `int`</b> (aura-skill T22, docs/architecture/numeric-types.md's numeric-overflow rule): since the
/// P(Θ) term (2026-08-30) makes this magnitude scale with the power ladder instead of staying
/// clamped forever, an `int` per-mille value would stop being exact past Θ≈3213 — measured, not
/// guessed (docs/architecture/numeric-types.md's own overflow table).</para></summary>
public sealed record PatronAura(
    string ElementPrimary, string? ElementSecondary,
    long PowerMilli, long DefenseMilli,
    long SecondaryPowerMilli, long SecondaryDefenseMilli);

/// <summary>
/// Patron rules (spec-patron-creature.md, owner locks 2026-08-21). Numbers are spec-locked; tuning
/// is ask-first. The kill-earn shape here applies ONLY when a patron is set — the audited
/// SoulEarnPolicy.KillEarn path stays byte-identical otherwise, and the 50-SOUL cap holds in
/// both shapes (the bonus only reaches it sooner).
/// </summary>
public static class PatronPolicy
{
    static PatronTuning? _tuning;

    /// <summary>Host-only (Injector/Server startup, or a test's inline construction).</summary>
    public static void Configure(PatronTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static PatronTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "PatronPolicy.Configure(...) has not run. Every patron rule reads data/tuning/patron.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    public static long SwitchCostSouls => Tuning.SwitchCostSouls;
    public static int AuraClampMilli => Tuning.AuraClampMilli;
    public static int PerStarMilli => Tuning.PerStarMilli;

    public static int RarityBaseMilli(CreatureRarity rarity) =>
        Tuning.RarityBaseMilli.TryGetValue(rarity, out var v) ? v : Tuning.RarityBaseMilli[CreatureRarity.Almanac];

    /// <summary>
    /// aura-skill T22 (owner sign-off 2026-08-30): `flatPart + pThetaTermMilli`. The flat part is the
    /// ORIGINAL formula, byte-identical and still clamped at <see cref="AuraClampMilli"/> — a small
    /// early-game floor from the creature's own rarity/star/level, unaffected by this change. The NEW
    /// term reads Θ through the SAME shared <see cref="PowerLadder"/> every other magnitude in this
    /// codebase reads (`ssot-power-scale.md` §10 row 16, one power ladder, no private curve) —
    /// `pThetaKMilli/1000 · P(Θ)`, uncapped, which is what keeps patron relevant as a commander's own
    /// P(Θ)-scaled aura grows past the old flat ceiling. <paramref name="powerTuning"/> is an explicit
    /// parameter, matching <see cref="KillEarnWithPatron"/>'s own established shape in this same
    /// class, never a hidden read of a global hub.
    /// </summary>
    public static long AuraMilli(CreatureRarity rarity, int star, long level, int pTheta, PowerTuning powerTuning)
    {
        if (powerTuning is null) throw new ArgumentNullException(nameof(powerTuning));
        var flatPart = Math.Clamp(RarityBaseMilli(rarity) + (long)PerStarMilli * star + level, 0, AuraClampMilli);

        var ladder = new PowerLadder(powerTuning);
        var pThetaValue = ladder.Value(pTheta);
        checked
        {
            var pThetaTermMilli = Tuning.PThetaKMilli * pThetaValue / 1000;
            return flatPart + pThetaTermMilli;
        }
    }

    public static PatronAura Aura(
        CreatureRarity rarity, int star, long level, int pTheta, PowerTuning powerTuning,
        string elementPrimary, string? elementSecondary)
    {
        var power = AuraMilli(rarity, star, level, pTheta, powerTuning);
        var hasSecondary = !string.IsNullOrWhiteSpace(elementSecondary);
        return new PatronAura(
            elementPrimary,
            hasSecondary ? elementSecondary : null,
            power,
            power / 2,
            hasSecondary ? power / 2 : 0,
            hasSecondary ? power / 4 : 0);
    }

    /// <summary>
    /// Kill-earn delta for the (counted+1)-th earning kill with a patron set: +1 base, +1 on every
    /// 10th, expressed as a running-total difference (T3.6 kept this shape even after the 50-soul
    /// cap it used to be exact against was deleted — the technique still isolates one kill's marginal
    /// share cleanly, it just no longer has a ceiling to be exact at). Uncapped, and scaled by
    /// <see cref="ContentScale"/> like the unpatroned path (<see cref="SoulEarnPolicy.KillEarn"/>) —
    /// deliberately, not named in SSOT §11.7a's own formula list, because leaving the patron bonus
    /// flat while the base path scales would make owning a patron a strictly WORSE choice at any
    /// depth past the pin, the opposite of what a bonus is for.
    /// </summary>
    public static long KillEarnWithPatron(int countedKills, int thetaEnemy, PowerTuning tuning)
    {
        var scaleMilli = ContentScale.Milli(thetaEnemy, tuning);
        long SoulsAfter(int earningKills) =>
            ContentScale.Apply(earningKills + earningKills / 10, scaleMilli);
        return SoulsAfter(countedKills + 1) - SoulsAfter(countedKills);
    }
}
