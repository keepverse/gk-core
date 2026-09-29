using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Drops;

/// <summary>
/// One player's loot pity, <b>keyed on rung ids</b> (Correction 5).
///
/// <para>⛔ I12's <c>items_since_r4</c> / <c>items_since_r6</c> are a SEVEN-rung vocabulary. Module 7
/// re-derived the ladder to ten and seeds <c>pity_guarded = 1</c> at ordinals 70 (`heirloom`) and 90
/// (`sunwoven`), so carrying <c>r4</c>/<c>r6</c> forward would leave two columns whose names name
/// nothing. The string id is the join; a positional label is what survives a ladder-length change with
/// the wrong meaning.</para>
///
/// <para>Counts equipment items MINTED, not loot events — a 20-hour expedition is one event yielding
/// four items and a battle is one event yielding zero, so counting events would make expedition
/// players rich and battle players poor for no design reason.</para>
/// </summary>
public readonly record struct LootPityState(long ItemsSinceHeirloom, long ItemsSinceSunwoven)
{
    public static LootPityState Empty => new(0, 0);
}

/// <summary>What pity did to one rarity draw, so the log can say so rather than the player guessing.</summary>
public readonly record struct LootPityOutcome(string RarityId, int Ordinal, bool Forced, LootPityState Next);

/// <summary>
/// Step 7's rarity draw and its two pity guards.
///
/// <para><b>Two independent rolls, and the disambiguation is the load-bearing half (D38).</b> Whether
/// anything drops at all is <see cref="DropVolume.RollsAnythingOnKill"/>, a flat 5 %. WHICH rung is
/// this — the rarity catalog's own weights, a different table, untouched by the first roll. A 5 % kill
/// rate is not a 5 % chance at an <c>almanac</c>.</para>
///
/// <para><b>Pity needs no content-band scoping</b>, and that falls out of §4.1 rather than being
/// bolted on: item level comes from the CONTENT, so a player who banks a counter farming level-1
/// waves cashes it in on a level-1 heirloom worth nothing. The exploit does not exist because the
/// level axis already closed it.</para>
/// </summary>
public static class RarityDraw
{
    /// <summary>Correction 5's two guarded rungs, by id. <c>almanac</c> (ordinal 100) is deliberately
    /// unguarded — its deterministic source is the first-clear grant and promotion, not a counter.
    /// Read from the creature enum's own <see cref="CreatureRarityIds.ToId"/> rather than re-listed
    /// (combat-math-dedup Task 11, D15) — the one declaration of the ten rung ids.</summary>
    public static readonly string HeirloomId = CreatureRarity.Heirloom.ToId();
    public static readonly string SunwovenId = CreatureRarity.Sunwoven.ToId();

    public static int OrdinalOf(IReadOnlyList<RarityRung> ladder, string rarityId) =>
        ladder.FirstOrDefault(r => string.Equals(r.RarityId, rarityId, StringComparison.Ordinal))?.Ordinal
        ?? throw new KeyNotFoundException($"rarity rung '{rarityId}' is not on the ladder");

    /// <summary>
    /// Draw one rung. Order of operations, and each step is load-bearing:
    /// <list type="number">
    /// <item>the entry's <c>rarity_floor</c> removes every rung below it;</item>
    /// <item>the ilvl cap is NOT applied here — item level gates affix STRENGTH, never rarity
    /// (§4.1: "every affix family is reachable at ilvl 1 at tier 1"), and the tier ceiling is step
    /// 8's job;</item>
    /// <item>the entry's <c>rarity_weight_shift_json</c> adds signed integer deltas by ordinal;</item>
    /// <item>the <c>sunwoven</c> soft ramp multiplies the two top rungs' weights once per ramp step;</item>
    /// <item>a hard floor forces the guarded rung or better, and records <c>pity_forced</c>.</item>
    /// </list>
    /// </summary>
    public static AtomRejection Draw(
        IReadOnlyList<RarityRung> ladder,
        DropTableEntryRow entry,
        LootPityState pity,
        DropVolumeTuning tuning,
        IAtomRandom rng,
        out LootPityOutcome outcome)
    {
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));
        if (rng is null) throw new ArgumentNullException(nameof(rng));
        outcome = default;

        var floorOrdinal = entry.RarityFloor is { Length: > 0 } floorId
            ? OrdinalOf(ladder, floorId)
            : int.MinValue;

        // Pity hard stops, strongest first. Both are "this rung OR BETTER", never an exact rung.
        var forced = false;
        if (pity.ItemsSinceSunwoven >= tuning.Pity.SunwovenHardCeilingItems)
        {
            floorOrdinal = Math.Max(floorOrdinal, OrdinalOf(ladder, SunwovenId));
            forced = true;
        }
        else if (pity.ItemsSinceHeirloom >= tuning.Pity.HeirloomHardFloorItems)
        {
            floorOrdinal = Math.Max(floorOrdinal, OrdinalOf(ladder, HeirloomId));
            forced = true;
        }

        var rampMultiplier = SunwovenRampMultiplier(pity.ItemsSinceSunwoven, tuning);
        var sunwovenOrdinal = OrdinalOf(ladder, SunwovenId);

        var candidates = new List<(RarityRung Rung, long Weight)>();
        long total = 0;
        foreach (var rung in ladder.OrderBy(r => r.Ordinal))
        {
            if (rung.Ordinal < floorOrdinal) continue;

            long w = rung.DropWeightPer100k;
            if (entry.RarityWeightShift is { } shift && shift.TryGetValue(rung.Ordinal, out var delta))
                w = checked(w + delta);
            if (w < 0) w = 0;                                   // a shift may zero a rung; it may not invert it.
            if (rung.Ordinal >= sunwovenOrdinal)
                w = checked(w * rampMultiplier);

            if (w == 0) continue;
            candidates.Add((rung, w));
            total = checked(total + w);
        }

        if (candidates.Count == 0 || total <= 0)
            return AtomRejection.Fail(AtomRejectionReason.UnsatisfiablePool,
                $"no rarity rung survives the entry's floor '{entry.RarityFloor ?? "(none)"}' plus its weight shifts");

        if (total > int.MaxValue)
            throw new OverflowException(
                $"rarity weight total {total} exceeds the draw's integer range — overflow throws, it never wraps");

        var roll = rng.NextInclusive(0, (int)(total - 1));
        long cursor = 0;
        foreach (var (rung, w) in candidates)
        {
            cursor = checked(cursor + w);
            if (roll >= cursor) continue;

            outcome = new LootPityOutcome(rung.RarityId, rung.Ordinal, forced, Advance(pity, rung.Ordinal, ladder));
            return AtomRejection.Ok;
        }

        throw new InvalidOperationException("rarity draw walked past its own cumulative weight");
    }

    /// <summary>
    /// The soft ramp: <c>multiplier × 2</c> once per <c>rampStepItems</c> past <c>rampStartItems</c>.
    /// <c>long</c> throughout and <c>checked</c> — the doubling is bounded in practice by the hard
    /// ceiling, but a tuning edit that removed the ceiling must overflow loudly, never wrap.
    /// </summary>
    public static long SunwovenRampMultiplier(long itemsSinceSunwoven, DropVolumeTuning tuning)
    {
        var p = tuning.Pity;
        if (itemsSinceSunwoven < p.SunwovenRampStartItems) return 1;

        var steps = (itemsSinceSunwoven - p.SunwovenRampStartItems) / p.SunwovenRampStepItems;
        long multiplier = 1;
        for (long i = 0; i < steps; i++)
            multiplier = checked(multiplier * p.SunwovenRampWeightMultiplier);
        return multiplier;
    }

    /// <summary>Advance both counters, resetting the ones the hit satisfied. Reset on a hit of that
    /// rung OR ABOVE — the counter measures a drought, not an exact rung.</summary>
    public static LootPityState Advance(LootPityState pity, int hitOrdinal, IReadOnlyList<RarityRung> ladder)
    {
        var heirloom = OrdinalOf(ladder, HeirloomId);
        var sunwoven = OrdinalOf(ladder, SunwovenId);
        return new LootPityState(
            hitOrdinal >= heirloom ? 0 : checked(pity.ItemsSinceHeirloom + 1),
            hitOrdinal >= sunwoven ? 0 : checked(pity.ItemsSinceSunwoven + 1));
    }
}
