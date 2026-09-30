using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Workspace;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T37 — the upgrade's cost contract, as a CLOSED VOCABULARY pin rather than a
/// reading. The verb spends souls only and consumes the input item itself, so the class matrix must
/// admit exactly one material class on `upgrade` and refuse every other; a later edit that quietly
/// allows a substrate, shard, catalyst, trophy or assurance line here would turn a souls purchase into a
/// material sink. The pre-cogs of each assertion are the shipped `materials.v6.json` rows, read fresh.
/// </summary>
public class ItemUpgradeCostContractTests
{
    static MaterialTuning Tuning()
    {
        // See EquipProjectionSocketsTests.Sockets(): the CONTRIBUTING.md marker walk overshoots now
        // that the file is gk-workflow's, and data/tuning is gk-core's own.
        return MaterialTuning.Parse(File.ReadAllText(
            Path.Combine(KeepverseRoots.Core(), "data", "tuning", SocketTuningFiles.Materials)));
    }

    [Fact]
    public void The_upgrade_admits_souls_and_refuses_every_other_material_class()
    {
        Assert.True(CostClassMatrix.Allows(CraftOperation.Upgrade, MaterialClass.Souls));

        // Every class the enum owns, enumerated — a contract over a closed vocabulary, never a count.
        foreach (var cls in Enum.GetValues<MaterialClass>())
        {
            if (cls == MaterialClass.Souls) continue;
            Assert.False(CostClassMatrix.Allows(CraftOperation.Upgrade, cls),
                $"`upgrade` must not admit a {cls} line — it spends souls and consumes the input item");
        }
    }

    [Fact]
    public void The_upgrade_burns_no_catalyst_which_is_what_keeps_it_out_of_the_catalyst_arm()
    {
        Assert.Null(CostClassMatrix.CatalystFor(CraftOperation.Upgrade));
        Assert.False(CostClassMatrix.Allows(CraftOperation.Upgrade, MaterialClass.Catalyst));
    }

    [Fact]
    public void The_shipped_upgrade_row_prices_on_the_rung_alone()
    {
        var tuning = Tuning();
        Assert.True(tuning.Operations.TryGetValue(CraftOperation.Upgrade, out var row), "operations.upgrade is missing");
        Assert.NotNull(row!.Souls);

        var souls = row.Souls!.Value;
        // The `rung` variable, and nothing else: two calls that differ only in grade/enhance agree.
        var atRung2 = souls.BaseQty(grade: 1, rungIndex: 1, enhanceLevel: 0);
        var atRung2OtherInputs = souls.BaseQty(grade: 5, rungIndex: 1, enhanceLevel: 9);
        Assert.Equal(atRung2, atRung2OtherInputs);

        // And it climbs with the rung, one step at a time (coefficient × (rungIndex + 1)).
        Assert.True(souls.BaseQty(grade: 1, rungIndex: 2, enhanceLevel: 0) > atRung2);
    }

    [Fact]
    public void The_band_multiplier_is_not_part_of_the_upgrade_price_and_the_helpers_says_so()
    {
        var tuning = Tuning();
        var leg = tuning.Operations[CraftOperation.Upgrade].Souls!.Value;
        var baseQty = leg.BaseQty(grade: 1, rungIndex: 3, enhanceLevel: 0);

        // ⚠ The verb builds `MaterialCostLine.Souls(leg.BaseQty(...))` with NO band multiplier, because an
        // upgrade has no recipe and therefore no authored `soulsCostBand`. Pinned here so the difference is
        // deliberate and visible: the helper that WOULD scale it does something else.
        var band = tuning.BandMultipliersPerMille.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
        var banded = MaterialTuning.ApplyBand(baseQty, tuning.BandMultiplier(band));
        Assert.NotEqual(baseQty, banded);

        // Every other priced verb goes through the recipe path that applies the band; this one's price is
        // the coefficient itself (spec-item-upgrade-tree.md § Tunables).
        Assert.Equal(leg.Coefficient * 4, baseQty);
    }
}
