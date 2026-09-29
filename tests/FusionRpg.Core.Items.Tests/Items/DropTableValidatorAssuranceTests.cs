using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Materials;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T45 (`craft-assurance` g — R10, boss-only sourcing). Two independent rules,
/// both refusing under the existing <c>drop.assurance-non-boss</c> code (no new one):
/// <list type="number">
/// <item>an <c>assurance.*</c> material entry's OWN <see cref="AffixChannels"/> must be
/// <see cref="AffixChannels.Boss"/>, wherever it is authored (<c>DropTableValidator.ValidateGroup</c>);</item>
/// <item>every <see cref="DropEntryKind.Table"/> entry on the path down to it must ALSO be
/// boss-channel — a correctly-boss-channel leaf entry reached through a <c>drop</c>-channel table
/// link is still refused, naming the referencing (parent) table
/// (<c>DropTableValidator.ValidateAssuranceBossOnly</c>).</item>
/// </list>
/// Fixtures are synthetic, matching <c>LootPipelineTests.cs</c>'s own direct-row-construction style —
/// the real shipped corpus has no <c>assurance.*</c> entries yet (T45 authors the first ones
/// separately, in <c>gk-data/packs/fusion/data/seed/loot/**</c>), so this behaviour cannot yet be proven against it.
/// </summary>
[Trait("VerificationId", "core.drop-tables")]
public class DropTableValidatorAssuranceTests
{
    static readonly string AssureId = MaterialCatalog.AssuranceId("assure");

    static DropVolumeTuning Tuning() => DropVolumeTests.Tuning();

    static DropTableRow Table(string id, params DropTableEntryRow[] entries) =>
        new(id, new[] { "web" }, null, null, true, 1,
            new[] { new DropTableGroupRow("g1", 0, 1, entries) });

    static LootSourceRow Source(string tableId) => new("web-wave", "s1", tableId, ContentLevel: 20);

    [Fact]
    public void An_off_channel_assurance_entry_is_refused_by_name()
    {
        var entry = new DropTableEntryRow(0, DropEntryKind.Material, AssureId, Weight: 100,
            AffixChannel: AffixChannels.Drop);
        var table = Table("drop.test.assurance-off-channel", entry);

        var result = DropTableValidator.Validate(new[] { Source(table.TableId) }, new[] { table }, Tuning());

        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.assurance-non-boss", result.Detail, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_boss_channel_assurance_entry_is_accepted()
    {
        var entry = new DropTableEntryRow(0, DropEntryKind.Material, AssureId, Weight: 100,
            AffixChannel: AffixChannels.Boss);
        var table = Table("drop.test.assurance-on-channel", entry);

        var result = DropTableValidator.Validate(new[] { Source(table.TableId) }, new[] { table }, Tuning());

        Assert.True(result.IsOk, result.Detail);
    }

    [Fact]
    public void A_boss_channel_leaf_entry_reached_through_a_drop_channel_table_link_is_still_refused_naming_the_parent()
    {
        var leafEntry = new DropTableEntryRow(0, DropEntryKind.Material, AssureId, Weight: 100,
            AffixChannel: AffixChannels.Boss);
        var leaf = Table("drop.test.assurance-leaf", leafEntry);

        var link = new DropTableEntryRow(0, DropEntryKind.Table, leaf.TableId, Weight: 100,
            AffixChannel: AffixChannels.Drop);
        var parent = Table("drop.test.assurance-parent", link);

        var result = DropTableValidator.Validate(
            new[] { Source(parent.TableId) }, new[] { parent, leaf }, Tuning());

        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.assurance-non-boss", result.Detail, System.StringComparison.Ordinal);
        Assert.Contains(parent.TableId, result.Detail, System.StringComparison.Ordinal);
    }

    [Fact]
    public void A_boss_channel_leaf_reached_through_a_boss_channel_table_link_is_accepted()
    {
        var leafEntry = new DropTableEntryRow(0, DropEntryKind.Material, AssureId, Weight: 100,
            AffixChannel: AffixChannels.Boss);
        var leaf = Table("drop.test.assurance-leaf-ok", leafEntry);

        var link = new DropTableEntryRow(0, DropEntryKind.Table, leaf.TableId, Weight: 100,
            AffixChannel: AffixChannels.Boss);
        var parent = Table("drop.test.assurance-parent-ok", link);

        var result = DropTableValidator.Validate(
            new[] { Source(parent.TableId) }, new[] { parent, leaf }, Tuning());

        Assert.True(result.IsOk, result.Detail);
    }

    [Fact]
    public void A_non_assurance_material_is_unaffected_by_either_rule()
    {
        var entry = new DropTableEntryRow(0, DropEntryKind.Material, "some.other.material", Weight: 100,
            AffixChannel: AffixChannels.Drop);
        var table = Table("drop.test.ordinary-material", entry);

        var result = DropTableValidator.Validate(new[] { Source(table.TableId) }, new[] { table }, Tuning());

        Assert.True(result.IsOk, result.Detail);
    }
}
