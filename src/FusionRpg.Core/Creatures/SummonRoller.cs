using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Creatures;

/// <summary>Per-player pity counters — persisted, cross-banner, visible in the UI. Field names
/// renamed to their guarded rungs (spec-rarity-migration.md §6, T4.1) — `PullsSinceHeirloom` guards
/// ordinal 70, `PullsSinceSunwoven` guards ordinal 90 (ssot-rarity.md §3.3). The underlying
/// `rpg_summon_pity` SQL columns keep their old names (`pulls_since_epic`/`pulls_since_legendary`)
/// — the counters' function is unchanged, only which rung they guarantee moved, so a schema
/// migration for a label-only rename is not warranted.</summary>
public sealed record PityState(int PullsSinceHeirloom, int PullsSinceSunwoven)
{
    public static readonly PityState Fresh = new(0, 0);
}

public sealed record SummonRollResult(
    string SpeciesId,
    CreatureRarity Rarity,
    string Variant,
    IReadOnlyList<string> TraitIds);

/// <summary>
/// Pure summon roller (spec-creature-summoning.md, pity v2; widened to the ten-rung ladder by
/// `seed-to-concrete` T4.1, owner Q15). Deterministic from (banner, focus, count, pity, rng) — the
/// store runs it inside the pull transaction with the recorded seed. Rates are a monotone staircase,
/// chaff (implicit remainder) down to almanac (2‰, never pity-boosted) — see
/// `gk-core/data/tuning/summoning.v1.json`'s own note for the exact table and why it is a starting value.
/// Heirloom (ordinal 70) hard-pities at pull 25; sunwoven (ordinal 90) soft-ramps from pull 41,
/// hard-pities at 55; a 10-pull sprout-or-better floor rolls in the last slot.
/// </summary>
public static class SummonRoller
{
    static SummoningTuning? _tuning;

    public static void Configure(SummoningTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static RollerTuning Tuning => (_tuning ?? throw new InvalidOperationException(
        "SummonRoller.Configure(...) has not run. Pity/rarity math reads " +
        "data/tuning/summoning.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.")).Roller;

    public static int HeirloomHardPity => Tuning.HeirloomHardPity;
    public static int SunwovenSoftStart => Tuning.SunwovenSoftStart;   // pull number where the ramp begins
    public static int SunwovenHardPity => Tuning.SunwovenHardPity;
    public static int SunwovenBasePerMille => Tuning.SunwovenBasePerMille;
    public static int SunwovenRampPerMille => Tuning.SunwovenRampPerMille;
    public static int AlmanacPerMille => Tuning.AlmanacPerMille;
    public static int HeirloomPerMille => Tuning.HeirloomPerMille;
    public static int ShinyOneIn => Tuning.ShinyOneIn;

    /// <summary>The rung a 10-pull's guaranteed floor targets — Cultivated, Rare's own migration
    /// target (ssot-rarity.md §4.3's forward map), preserving the OLD floor's actual GUARANTEE
    /// LEVEL rather than its ordinal position. This also happens to be the right choice for
    /// today's catalog: only four of the ten rungs are populated (the four legacy-mapped ones),
    /// so a floor targeting one of the six currently-empty rungs (e.g. Sprout) would silently
    /// degrade to Chaff via `BandWithFallback`'s own empty-band search — a real bug this design
    /// avoids, found by `Ten_pull_guarantees_sprout_or_better` failing against the real catalog.</summary>
    static readonly CreatureRarity FloorTarget = CreatureRarity.Cultivated;

    public static (IReadOnlyList<SummonRollResult> Results, PityState Pity) Roll(
        SummonBannerDef banner, ElementTypeId? focusElement, int count, PityState pity, SeededRng rng)
    {
        if (count is not (1 or 10)) throw new ArgumentOutOfRangeException(nameof(count));
        var results = new List<SummonRollResult>(count);
        var sawFloorOrBetter = false;
        for (var i = 0; i < count; i++)
        {
            // 10-pull floor: the guarantee slot rolls last.
            var floorRare = count == 10 && i == count - 1 && !sawFloorOrBetter;
            var rolled = RollRarity(pity, floorRare, rng);
            var result = RollSpecies(rolled, banner, focusElement, rng);
            // Pity and the floor track the DELIVERED band — if band fallback ever downgrades a
            // roll (empty summonable band), the player must not lose a pity hit they never got.
            pity = Advance(pity, result.Rarity);
            if (CreatureRarityLadder.AtLeast(result.Rarity, FloorTarget)) sawFloorOrBetter = true;
            results.Add(result);
        }

        return (results, pity);
    }

    static CreatureRarity RollRarity(PityState pity, bool floorRare, SeededRng rng)
    {
        var natural = RollRarityNatural(pity, rng);
        // The floor is a clean post-process, not woven into the cascade: whatever the natural
        // roll (and pity) produced, bump it up to FloorTarget if it landed below that — never
        // silently swallowed by one specific branch of the cascade (the earlier shape only forced
        // the floor from the bottommost branch, which broke the moment the catalog didn't
        // populate that exact rung — see FloorTarget's own doc comment).
        if (floorRare && !CreatureRarityLadder.AtLeast(natural, FloorTarget))
            return FloorTarget;
        return natural;
    }

    static CreatureRarity RollRarityNatural(PityState pity, SeededRng rng)
    {
        // Guard 2 (ordinal 90, Sunwoven) — soft ramp then hard pity. Almanac (ordinal 100, the
        // true top) sits ABOVE this guard and is never boosted by it (Q15: guards are at 70/90).
        var sunwovenPull = pity.PullsSinceSunwoven + 1;
        if (sunwovenPull >= SunwovenHardPity)
            return CreatureRarity.Sunwoven;

        var sunwovenPerMille = SunwovenBasePerMille +
            Math.Max(0, sunwovenPull - (SunwovenSoftStart - 1)) * SunwovenRampPerMille;
        var roll = rng.NextPerMille();
        if (roll < AlmanacPerMille)
            return CreatureRarity.Almanac;
        if (roll < AlmanacPerMille + sunwovenPerMille)
            return CreatureRarity.Sunwoven;

        // Guard 1 (ordinal 70, Heirloom) — independent hard pity, no ramp (matches the old
        // EpicHardPity shape: a flat pull-count floor, not a growing chance).
        if (pity.PullsSinceHeirloom + 1 >= HeirloomHardPity)
            return CreatureRarity.Heirloom;

        var cum = AlmanacPerMille + sunwovenPerMille;
        if (roll < cum + HeirloomPerMille) return CreatureRarity.Heirloom;
        cum += HeirloomPerMille;
        if (roll < cum + Tuning.FirstseedPerMille) return CreatureRarity.Firstseed;
        cum += Tuning.FirstseedPerMille;
        if (roll < cum + Tuning.ChimericPerMille) return CreatureRarity.Chimeric;
        cum += Tuning.ChimericPerMille;
        if (roll < cum + Tuning.FusedPerMille) return CreatureRarity.Fused;
        cum += Tuning.FusedPerMille;
        if (roll < cum + Tuning.CultivatedPerMille) return CreatureRarity.Cultivated;
        cum += Tuning.CultivatedPerMille;
        if (roll < cum + Tuning.GraftedPerMille) return CreatureRarity.Grafted;
        cum += Tuning.GraftedPerMille;
        if (roll < cum + Tuning.SproutPerMille) return CreatureRarity.Sprout;
        return CreatureRarity.Chaff;
    }

    static PityState Advance(PityState pity, CreatureRarity rolled) => new(
        PullsSinceHeirloom: CreatureRarityLadder.AtLeast(rolled, CreatureRarity.Heirloom) ? 0 : pity.PullsSinceHeirloom + 1,
        PullsSinceSunwoven: CreatureRarityLadder.AtLeast(rolled, CreatureRarity.Sunwoven) ? 0 : pity.PullsSinceSunwoven + 1);

    static SummonRollResult RollSpecies(
        CreatureRarity rarity, SummonBannerDef banner, ElementTypeId? focusElement, SeededRng rng)
    {
        var band = BandWithFallback(rarity);
        var picked = PickWeighted(band.Pool, banner, focusElement, rng);
        var variant = picked.Variants.Contains("shiny") && rng.NextInt(ShinyOneIn) == 0 ? "shiny" : "normal";
        return new SummonRollResult(picked.SpeciesId, band.Rarity, variant, RollTraits(picked, band.Rarity, rng));
    }

    static (CreatureRarity Rarity, List<CreatureSpeciesDef> Pool) BandWithFallback(CreatureRarity rarity)
    {
        var r = rarity;
        while (true)
        {
            var pool = CreatureSpeciesCatalog.All
                // ⛔ `CreatureAdmission.ForWave`, never the raw `Summonable` flag. The flag alone admits
                // a species the build has MARKED OUT OF PLAY (`speciesKind: "excluded"`), which is
                // exactly what live-probe Task 22 measured: 3 of 5 real pulls rolled
                // `typeId 261/264/265` (EnumValue261/264/265, all `speciesKind: excluded`), the engine
                // refused to plant them (`不存在该类型的植物`), and the player lost the souls. Admission's
                // own doc calls itself "the one declaring site" and "a fourth context is a new member
                // here, never a re-derivation in a fourth file" — the summon pool WAS that fourth
                // re-derivation, and it is the only place a player could be handed an out-of-play
                // species.
                .Where(s => s.BaseRarity == r && CreatureAdmission.ForWave(s))
                .ToList();
            if (pool.Count > 0) return (r, pool);
            if (CreatureRarityLadder.IsBottomRung(r))
                throw new InvalidOperationException("No summonable species in the catalog.");
            r = CreatureRarityLadder.OneRungBelow(r);
        }
    }

    /// <summary>
    /// The pool one rarity band draws from, with the same empty-band fallback the roller uses. Public so
    /// the join that must close — <b>nothing the roller can serve is a species the build refuses</b> — is
    /// asserted over the whole roster rather than sampled through <see cref="Roll"/>.
    /// </summary>
    public static IReadOnlyList<CreatureSpeciesDef> PoolFor(CreatureRarity rarity) => BandWithFallback(rarity).Pool;

    static CreatureSpeciesDef PickWeighted(
        List<CreatureSpeciesDef> pool, SummonBannerDef banner, ElementTypeId? focusElement, SeededRng rng)
    {
        if (!banner.HasElementFocus || focusElement is null)
            return pool[rng.NextInt(pool.Count)];

        // Focus banner: focus-element species get FocusWeightMultiplier× weight within the band.
        var mult = (int)Math.Round(banner.FocusWeightMultiplier);
        var total = 0;
        foreach (var s in pool)
            total += s.ElementPrimary == focusElement ? mult : 1;
        var pick = rng.NextInt(total);
        foreach (var s in pool)
        {
            pick -= s.ElementPrimary == focusElement ? mult : 1;
            if (pick < 0) return s;
        }

        return pool[^1];
    }

    /// <summary>Rarity-scaled trait roll from the species pool — shared by summons and wild joins.</summary>
    public static IReadOnlyList<string> RollTraits(CreatureSpeciesDef species, CreatureRarity rarity, SeededRng rng)
    {
        // ONE slot table for every acquisition path — fusion's SlotsFor is the authority.
        var want = Fusion.FusionRoller.SlotsFor(rarity);
        var pool = species.TraitPool.ToList();
        var picked = new List<string>(want);
        while (picked.Count < want && pool.Count > 0)
        {
            var i = rng.NextInt(pool.Count);
            picked.Add(pool[i]);
            pool.RemoveAt(i);
        }

        return picked;
    }
}
