using System;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Seed;
using FusionRpg.TestSupport;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `species-progression` SP0.1 — the layer-1b ledger, born keyed `(save_id, empire_id)`. In-memory
/// (testing-standard.md); nothing here asserts a row count of a population.
/// </summary>
public class SpeciesModLedgerTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SpeciesModLedgerTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static EmpireRef HumanOf(RpgStore store, long saveId) => new(new SaveId(saveId), store.HumanEmpireOf(saveId));

    [Fact]
    public void Appending_the_same_correlation_twice_writes_one_row()
    {
        var owner = HumanOf(_store, 1);

        Assert.True(_store.AppendSpeciesMod(owner, "peashooter", SpeciesModMechanism.FusionPick,
            correlationId: "instance-abc", instanceId: "instance-abc", catalogRevision: 7));
        Assert.False(_store.AppendSpeciesMod(owner, "peashooter", SpeciesModMechanism.FusionPick,
            correlationId: "instance-abc", instanceId: "instance-abc", catalogRevision: 7));

        var rows = _store.ListSpeciesMods(owner);
        Assert.Single(rows);
        Assert.Equal("instance-abc", rows[0].CorrelationId);
        Assert.Equal(SpeciesModMechanism.FusionPick, rows[0].Mechanism);
        Assert.Equal(7, rows[0].CatalogRevision);
    }

    [Fact]
    public void An_empire_that_is_not_the_saves_is_refused()
    {
        var notAnEmpireOfSave1 = new EmpireRef(new SaveId(1), new EmpireId("penny"));

        Assert.Throws<SpeciesModEmpireNotInSave>(() => _store.AppendSpeciesMod(
            notAnEmpireOfSave1, "peashooter", SpeciesModMechanism.FusionPick,
            correlationId: "c1", instanceId: "i1", catalogRevision: 1));
    }

    [Fact]
    public void Save_B_never_lists_save_A_rows()
    {
        var saveOne = HumanOf(_store, 1);
        _store.AppendSpeciesMod(saveOne, "peashooter", SpeciesModMechanism.FusionPick,
            correlationId: "a1", instanceId: "a1", catalogRevision: 1);

        var second = new SaveId(_store.CreatePlayer("Second").Id);
        var saveTwo = new EmpireRef(second, _store.HumanEmpireOf(second.Value));
        _store.AppendSpeciesMod(saveTwo, "wallnut", SpeciesModMechanism.FusionPick,
            correlationId: "b1", instanceId: "b1", catalogRevision: 1);

        Assert.Equal("a1", Assert.Single(_store.ListSpeciesMods(saveOne)).CorrelationId);
        Assert.Equal("b1", Assert.Single(_store.ListSpeciesMods(saveTwo)).CorrelationId);
    }

    [Fact]
    public void After_a_real_boot_a_save_that_never_fused_has_no_layer_1b_rows()
    {
        // The C2 regression (SP0.4). The eager roster roll used to materialise a species instance for
        // EVERY species at boot, so a save that never fused already "owned" layer 1b and every later
        // fusion was refused as already-materialised. It is gone: a real boot, a second save, a second
        // boot — and neither save holds a ledger row or a species-origin roll.
        using var booted = DataTestStore.Create();
        var store = booted.Store;

        // The content root comes from the shared resolver, never a private walk from this file's own
        // directory. A private `..\..\..` is depth-sensitive and resolved one level ABOVE the repo
        // root, so `FindUp` found some *other* tree — in a lane worktree, the main checkout's — or, in
        // the main checkout itself (`D:\Works\source` holds no `gk-data/packs/fusion/data/seed`), none at all, and the boot
        // answered SeedTreeNotFound. That is a fixture defect, not a ledger one: the boot stayed loud
        // and correct (spec-player-content-boot.md §3.2/§4) for the directory it was handed. Resolver
        // contract: Directory.Build.props / tasks/keepverse-split-plan.md.
        var boot = SeedImportRunner.RunSelfHealing(store, ContentRoot.Path);
        // The boot must be REAL — a missing or unimportable seed tree would make the claim below
        // vacuous, and `Failed` is as vacuous as `SeedTreeNotFound`.
        Assert.True(boot.Ok, $"the boot must import real content — status {boot.Status}: {boot.Detail}");
        var second = store.CreatePlayer("Second");
        SeedImportRunner.RunSelfHealing(store, ContentRoot.Path);

        Assert.Contains(store.ListPlayers(), p => p.Id == second.Id);
        foreach (var save in store.ListPlayers())
        {
            var owner = new EmpireRef(new SaveId(save.Id), store.HumanEmpireOf(save.Id));
            Assert.Empty(store.ListSpeciesMods(owner));
        }
        Assert.Equal(0, store.CountEffectInstancesWithOriginForTest("drop"));
    }

    [Fact]
    public void An_empire_of_the_same_save_with_no_rows_lists_none()
    {
        var humanOfSave1 = HumanOf(_store, 1);
        _store.AppendSpeciesMod(humanOfSave1, "peashooter", SpeciesModMechanism.FusionPick,
            correlationId: "a1", instanceId: "a1", catalogRevision: 1);

        var zombossOfSave1 = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        Assert.Empty(_store.ListSpeciesMods(zombossOfSave1));
    }

    [Fact]
    public void The_mechanism_vocabulary_is_closed_at_the_one_the_spec_names()
    {
        // Pinned by design: a second mechanism is a reviewed change (a new way to modify a species for a
        // player), not an incidental addition.
        Assert.Single(Enum.GetValues<SpeciesModMechanism>());
        Assert.Equal("fusion-pick", SpeciesModMechanism.FusionPick.Token());
    }
}
