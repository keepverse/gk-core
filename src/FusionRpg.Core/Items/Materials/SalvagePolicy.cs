using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Items.Materials;

/// <summary>
/// Everything <see cref="SalvagePolicy.Yield"/> needs, all of it readable off a frozen instance plus
/// its container. ⛔ Like <see cref="RecipeContext"/>, there is <b>nowhere here to put a player
/// property</b> — D26 applies to the converter as hard as to the cost table.
/// </summary>
/// <param name="RungIndex">0..9 on <see cref="RarityLadder.RungIds"/>. Never `rarity.ordinal`.</param>
/// <param name="ItemLevel">The content's number, not the player's.</param>
/// <param name="Frame">`humanoid` or `plant` — which substrate line the item returns.</param>
/// <param name="AffixCount">I8's number: drawn atoms on the instance.</param>
/// <param name="ElementalAffixCounts">Per concrete element, how many drawn atoms carry it.</param>
/// <param name="EnhanceLevel">Module 15's number, 0 if never enhanced.</param>
public sealed record SalvageInput(
    int RungIndex,
    int ItemLevel,
    string Frame,
    int AffixCount,
    IReadOnlyDictionary<string, int> ElementalAffixCounts,
    int EnhanceLevel);

public sealed class SalvageRejection : Exception
{
    public SalvageRejection(string message) : base(message) { }
}

/// <summary>
/// I9 §5.1's yield function — pure, integer-only, no file I/O, no store. <b>Salvage is a converter,
/// not a faucet.</b>
///
/// <para><b>R1 — the rung−1 rule.</b> Salvage returns a shard of the rung <i>below</i> the item's
/// own. Rarity always flows downhill; you can never bootstrap a ceiling by feeding the grinder its
/// own output, and `chaff` returns no shard at all.</para>
///
/// <para><b>R2 — the strict-loss invariant.</b> For every class a recipe spends, salvaging that
/// recipe's output returns strictly less of that class. Asserted as a property test over the WHOLE
/// recipe table, not as a design intention.</para>
///
/// <para><b>The grade lock.</b> `grade` is a function of item level, and item level is a function of
/// the content that dropped the item — so a level-10 zone returns `crude` forever, at any volume.
/// ⚠ This is not metering the player under D26: it is the salvage output of a <i>low-level item</i>
/// being low-level, which is a property of the target.</para>
///
/// <para>⛔ Two classes have <b>no salvage faucet at all</b>, by construction and not by omission:
/// `catalyst.forge` and `catalyst.flux`. The player's rate of making and re-randomising is therefore
/// pinned to content completed and cannot be accelerated by inventory management (I9 §7.3).</para>
/// </summary>
public static class SalvagePolicy
{
    /// <summary>
    /// The returned lines, in the same fixed class order a spend uses (shard → substrate → essence →
    /// catalyst) so a yield and a cost are directly comparable line by line. Souls are never
    /// returned — salvage never mints currency — and there is deliberately no souls line at zero,
    /// because a zero line is a row that invites someone to make it non-zero.
    /// </summary>
    public static IReadOnlyList<MaterialCostLine> Yield(SalvageInput item, MaterialTuning tuning)
    {
        if (item.RungIndex < 0 || item.RungIndex >= RarityLadder.RungIds.Count)
            throw new SalvageRejection(
                $"RungIndex {item.RungIndex} is outside 0..{RarityLadder.RungIds.Count - 1} — this is the rung INDEX, never rarity.ordinal");
        if (item.AffixCount < 0 || item.EnhanceLevel < 0)
            throw new SalvageRejection("affix count and enhancement level cannot be negative");

        var rungId = RarityLadder.RungIds[item.RungIndex];
        var coefficient = tuning.Salvage[rungId];
        var grade = tuning.GradeForItemLevel(item.ItemLevel);
        var lines = new List<MaterialCostLine>();

        // shard.{rung − 1} × shardBack[rung] — NEVER the item's own rung (R1). chaff has no rung
        // below it and its shardBack is 0, which MaterialTuning refuses to let a balance pass change.
        if (item.RungIndex > 0 && coefficient.ShardBack > 0)
        {
            // Indexed off the ladder's own ordered list, never `(CreatureRarity)index` — a bare cast is
            // what silently changed meaning the day the enum widened from four values to ten, and
            // CreatureRarityLadderGuardTests forbids the form outside the helper.
            var below = CreatureRarityLadder.OneRungBelow(CreatureRarityLadder.All[item.RungIndex]);
            lines.Add(new MaterialCostLine(MaterialClass.Shard, MaterialCatalog.ShardId(below), coefficient.ShardBack));
        }

        // substrate.{frame}.{grade} × (substrateBase[rung] + affixes). `long` throughout; checked so
        // an absurd affix count throws rather than wrapping into a negative yield.
        var substrateQty = checked(coefficient.SubstrateBase + item.AffixCount);
        lines.Add(new MaterialCostLine(
            MaterialClass.Substrate, MaterialCatalog.SubstrateId(item.Frame, grade), substrateQty));

        // essence.{element} × min(essenceCap[rung], elemental) per DISTINCT element present. Ordinal
        // order so two salvages of the same item produce byte-identical line lists.
        foreach (var (elementId, count) in item.ElementalAffixCounts
                     .Where(kv => kv.Value > 0)
                     .OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var qty = Math.Min(coefficient.EssenceCap, count);
            if (qty > 0)
                lines.Add(new MaterialCostLine(MaterialClass.Essence, MaterialCatalog.EssenceId(elementId), qty));
        }

        // catalyst.temper × (enh / divisor), integer division. temper is the ONLY catalyst salvage
        // ever returns, and it returns strictly less than enhancement paid in.
        var temper = item.EnhanceLevel / tuning.SalvageEnhanceReturnDivisor;
        if (temper > 0)
            lines.Add(new MaterialCostLine(MaterialClass.Catalyst, MaterialCatalog.CatalystId("temper"), temper));

        return lines;
    }
}
