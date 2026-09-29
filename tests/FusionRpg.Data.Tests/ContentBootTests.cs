using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E20: <see cref="RpgStore.LoadContentIntoRuntime"/> is what the completeness audit's finding A2
/// found missing — a host that never calls this leaves <c>ElementTable.Current</c> and
/// <c>PowerTables.Current</c> on the shipped code copy forever, so an imported roster or coefficient
/// row changes the content hash and nothing else.
///
/// <para><b>Mutates process-global statics on purpose</b> — that is the method's entire job, so this
/// class runs in its own xunit collection to guarantee no other test observes the swap mid-flight, and
/// every test restores the shipped defaults in a <c>finally</c> so a failure here cannot leak into an
/// unrelated test later in the same run.</para>
/// </summary>
[Collection("EffectAtomRuntimeGlobals")]
public class ContentBootTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ContentBootTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        ElementTable.ResetToShipped();
        PowerTables.ResetToAuthored();
        _testStore.Dispose();
    }

    [Fact]
    public void An_empty_store_loads_the_shipped_defaults_and_changes_nothing()
    {
        // A host that has imported nothing must behave exactly as it did before this method existed
        // — otherwise E20 would be a behaviour change wearing a wiring fix's clothes.
        var beforeElements = ElementTable.Current.Elements.Count;
        var beforePower = PowerTables.Current.Coefficients.Count;

        _store.LoadContentIntoRuntime();

        Assert.Equal(beforeElements, ElementTable.Current.Elements.Count);
        Assert.Equal(beforePower, PowerTables.Current.Coefficients.Count);
        Assert.Equal(ElementTable.Shipped().Elements.Count, ElementTable.Current.Elements.Count);
    }

    [Fact]
    public void An_imported_roster_row_is_what_ElementTable_Current_reflects()
    {
        // The audit's exact failure mode: prove the loader, not just the store method it calls.
        var shipped = ElementTable.Shipped();
        var withVoid = shipped.Elements
            .Append(new ElementRow("void", "Void", shipped.Elements.Count, true))
            .ToArray();
        var table = new ElementTable(withVoid, shipped.CombatRows, shipped.ShieldRows);
        var (ok, reason) = _store.UpsertElementTable(table);
        Assert.True(ok, reason);

        _store.LoadContentIntoRuntime();

        Assert.Contains(ElementTable.Current.Elements, e => e.ElementId == "void");
        Assert.DoesNotContain(ElementTable.Shipped().Elements, e => e.ElementId == "void");
    }

    [Fact]
    public void An_imported_coefficient_row_is_what_PowerTables_Current_reflects()
    {
        var authored = PowerTables.Authored();
        var edited = new PowerCoefficientRow("stat.modify", "atk", 12345, 10);
        var replaced = authored.Coefficients
            .Where(c => !(c.KindId == edited.KindId && c.Channel == edited.Channel))
            .Append(edited)
            .ToArray();
        var (ok, reason) = _store.UpsertPowerTables(new PowerTables(replaced, authored.Frequencies));
        Assert.True(ok, reason);

        _store.LoadContentIntoRuntime();

        var found = PowerTables.Current.Find("stat.modify", "atk");
        Assert.NotNull(found);
        Assert.Equal(12345, found!.CoeffMilli);
    }
}

[CollectionDefinition("EffectAtomRuntimeGlobals", DisableParallelization = true)]
public class EffectAtomRuntimeGlobalsCollection { }
