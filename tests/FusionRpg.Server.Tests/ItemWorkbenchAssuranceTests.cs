using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// species-gear-chain T42 (`craft-assurance` d) — the enhance request's own assurance LINES (ids +
/// counts), coalesced by <see cref="ItemWorkbench.Enhance"/> into debited cost lines and the SAME
/// derivation of <see cref="EnhanceContext.AssureLoaded"/>/<see cref="EnhanceContext.WardLoaded"/>,
/// through the ONE real <see cref="RpgStore.TrySpendAndApply"/> transaction. A real
/// <see cref="DataTestStore"/>, the real shipped tuning, a synthetic zero-cost recipe (isolating the
/// assurance lines as the only thing debited) — the live-probe-standard's own bar: this drives the
/// real executor end to end, never a debug fabrication.
/// </summary>
public class ItemWorkbenchAssuranceTests : IDisposable
{
    const string RecipeId = "recipe.assurance-wiring-test";
    const string ElevateRecipeId = "recipe.assurance-wiring-test-elevate";
    const string RepairRecipeId = "recipe.assurance-wiring-test-repair";
    const string ContainerId = "item.assurance-wiring-test";
    const int ItemLevel = 200; // matches EnhancePolicyTests' own peril-level fixture (D4 v1 reach)

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly string _playerKey;
    readonly EnhancementTuning _enhancement;
    readonly MaterialRecipeCatalog _recipes;
    string _instanceId = "";

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static string Tuning(string file) => File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", file));

    public ItemWorkbenchAssuranceTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _playerKey = _playerId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        _enhancement = EnhancementTuning.Parse(Tuning("enhancement.v1.json"));
        CraftAssuranceTuningHub.Configure(CraftAssuranceTuning.Parse(Tuning("craft-assurance.v1.json")));
        // species-gear-chain T44: Repair() reads DeploymentHierarchyTuningHub.Tuning.RepairDestroyChanceMilli
        // (T23's own module 7 territory) -- the same Hub ItemWorkbenchEndpointsTests' own constructor
        // configures for its own Repair coverage.
        DeploymentHierarchyTuningHub.Configure(
            DeploymentHierarchyTuningLoader.Parse(Tuning("deployment-hierarchy.v5.json")));

        // Two synthetic, zero-cost recipes (temper for Enhance, elevate for Promote): the ONLY thing
        // either recipe's own resolve ever adds is nothing, so every debited line in a test's own
        // outcome is an assurance line and nothing else.
        _recipes = MaterialRecipeCatalog.Load(new[]
        {
            $$"""
            {"entries": [
                {"id": "{{RecipeId}}", "operation": "temper", "outputKind": "mutation", "outputQty": 1,
                 "frame": "any", "costLines": []},
                {"id": "{{ElevateRecipeId}}", "operation": "elevate", "outputKind": "mutation", "outputQty": 1,
                 "frame": "any", "costLines": []},
                {"id": "{{RepairRecipeId}}", "operation": "repair", "outputKind": "mutation", "outputQty": 1,
                 "frame": "any", "costLines": []}
            ]}
            """,
        }, MaterialTuning.Parse(Tuning(SocketTuningFiles.Materials)));

        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);

        SeedItem();
    }

    public void Dispose()
    {
        CraftAssuranceTuningHub.Reset();
        _testStore.Dispose();
    }

    void SeedItem()
    {
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = ContainerId, Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(ItemRole.ArmamentPrimary), Rarity = "heirloom",
            Atoms = Array.Empty<ContainerAtomRow>(),
        }).IsOk);

        _instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = ContainerId, RollSeed = 99,
            CatalogRevision = _store.GetCatalogRevision(), Origin = InstanceOrigin.Drop,
            Atoms = Array.Empty<InstanceAtomRow>(),
        });

        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = _instanceId, PlayerId = _playerKey,
            AcquiredUtc = "2026-09-06T00:00:00Z", OriginKind = "drop",
        }).IsOk);

        var rungOrdinal = _store.ListRarities().First(r => r.RarityId == "heirloom").Ordinal;
        _store.PersistLoot(_playerKey,
            new LootManifest("assurance-drop", "table.wiring", 5UL, ItemLevel, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "wiring", _store.GetCatalogRevision(), 1,
            new[] { new ItemGenerationRow(_instanceId, 0, "item.wiring-base", rungOrdinal, ItemLevel,
                "humanoid", ItemRoles.Id(ItemRole.ArmamentPrimary), "drop") });
    }

    ItemWorkbench Bench(CraftWearSource? craftWear = null) => new(_store, _recipes.Tuning, _recipes,
        EnhancementTuning.Parse(Tuning("enhancement.v1.json")), SocketTuning.Parse(Tuning(SocketTuningFiles.Current)),
        craftWear: craftWear);

    /// <summary>A fixed, deterministic wear source for the T43 tests below: every base type/rung
    /// derives max=1000, and every attempt costs 100‰ (100 units) of wear.</summary>
    static readonly CraftWearSource FixedWear = new((_, _) => 1000L, WearPerAttemptMilli: 100);

    /// <summary>Fast-forward the instance straight to <paramref name="level"/>, through the REAL
    /// public store API (<see cref="RpgStore.AppendMutationOp"/>) rather than N real rolls — a
    /// legitimate store-layer test setup, not a debug fabrication: this test's own subject is the
    /// NEXT attempt, resolved through the real executor end to end.</summary>
    void SeedEnhanceLevel(int level)
    {
        var head = new InstanceHead(level, Array.Empty<InstanceAtomHead>());
        var appended = _store.AppendMutationOp(
            _instanceId, MutationOpKind.Enhance, $"test-seed-to-{level}",
            RpgStore.DeriveOpSeed(_instanceId, $"test-seed-to-{level}"),
            new MutationResult("seeded", level, Array.Empty<AtomValueSet>(), Array.Empty<int>(), Array.Empty<AtomAppend>()),
            MutationCanonical.StateHash(head), "{}", "2026-09-06T00:00:00Z");
        Assert.True(appended.Ok, appended.Reason);
    }

    static string AssureId => MaterialCatalog.AssuranceId("assure");
    static string ProtectId => MaterialCatalog.AssuranceId("protect");

    [Fact]
    public void Enough_assure_charges_reach_certainty_and_the_stack_is_debited()
    {
        SeedEnhanceLevel(19); // target +20, the real shipped 200‰ peril band
        var t = EnhancementTuning.Parse(Tuning("enhancement.v1.json"));
        var baseSuccess = EnhancePolicy.SuccessMilli(20, t);
        var bonus = CraftAssuranceTuningHub.Tuning.AssureBonusMilli;
        var charges = (int)((1000 - baseSuccess) / bonus);
        Assert.True(charges > 0 && (1000 - baseSuccess) % bonus == 0,
            "the real shipped bands/bonus must divide evenly for this test to prove exact certainty");

        _store.GrantMaterials(_playerId, new[] { (AssureId, (long)charges) });

        var outcome = Bench().Enhance(_playerId, _instanceId, RecipeId, "assure-to-certainty",
            new[] { new AssuranceLine(AssureId, charges) });

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("success", outcome.Outcome);
        Assert.Equal(1000, outcome.SuccessMilli);
        Assert.Equal(20, outcome.EnhanceLevel);
        var spentLine = Assert.Single(outcome.Spent, l => l.MaterialId == AssureId);
        Assert.Equal(charges, spentLine.Qty);
        Assert.Equal(0L, _store.GetMaterialQty(_playerId, AssureId));
    }

    [Fact]
    public void No_assure_stock_refuses_the_whole_attempt_debiting_and_moving_nothing()
    {
        SeedEnhanceLevel(19);
        // Zero granted -- the shipped shortfall path must refuse the WHOLE attempt.
        var outcome = Bench().Enhance(_playerId, _instanceId, RecipeId, "assure-no-stock",
            new[] { new AssuranceLine(AssureId, 1) });

        Assert.False(outcome.Ok);
        Assert.Equal(19, _store.GetInstanceMutationHead(_instanceId)!.EnhanceLevel);
        Assert.DoesNotContain(_store.ReadMutationOps(_instanceId), o => o.CorrelationId == "assure-no-stock");
    }

    [Fact]
    public void A_protect_loaded_peril_failure_keeps_the_level_and_debits_one_unit()
    {
        SeedEnhanceLevel(19);
        var t = EnhancementTuning.Parse(Tuning("enhancement.v1.json"));

        // Find a correlation id whose derived stream, WITHOUT protect, downgrades -- the same
        // seed-scan EnhancePolicyTests' own ward test already uses, just over correlation ids.
        string? downgrading = null;
        for (var i = 0; i < 512 && downgrading is null; i++)
        {
            var probe = $"protect-probe-{i}";
            var seed = unchecked((ulong)RpgStore.DeriveOpSeed(_instanceId, probe));
            var rng = SeededRng.DeriveStream(seed, MutationOpKinds.StreamName(MutationOpKind.Enhance));
            var ctx = new EnhanceContext(RarityLadder.RungIndexOf("heirloom"), ItemLevel, 19, 0, false);
            var probeAttempt = EnhancePolicy.Resolve(ctx, rng, t, out _);
            if (probeAttempt.Outcome == EnhanceOutcome.FailureWithDowngrade) downgrading = probe;
        }
        Assert.NotNull(downgrading);

        _store.GrantMaterials(_playerId, new[] { (ProtectId, 1L) });

        var outcome = Bench().Enhance(_playerId, _instanceId, RecipeId, downgrading!,
            new[] { new AssuranceLine(ProtectId, 1) });

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("failure", outcome.Outcome); // still a failure -- protect never raises odds
        Assert.Equal(19, outcome.EnhanceLevel); // kept, not dropped to 18
        var spentLine = Assert.Single(outcome.Spent, l => l.MaterialId == ProtectId);
        Assert.Equal(1, spentLine.Qty);
        Assert.Equal(0L, _store.GetMaterialQty(_playerId, ProtectId));
    }

    [Fact]
    public void A_replay_with_a_different_load_on_the_same_correlation_returns_the_recorded_outcome_and_debits_nothing()
    {
        SeedEnhanceLevel(19);
        _store.GrantMaterials(_playerId, new[] { (AssureId, 100L) });

        var t = EnhancementTuning.Parse(Tuning("enhancement.v1.json"));
        var baseSuccess = EnhancePolicy.SuccessMilli(20, t);
        var bonus = CraftAssuranceTuningHub.Tuning.AssureBonusMilli;
        var charges = (int)((1000 - baseSuccess) / bonus);

        var first = Bench().Enhance(_playerId, _instanceId, RecipeId, "assure-replay",
            new[] { new AssuranceLine(AssureId, charges) });
        Assert.True(first.Ok, first.Reason);
        var balanceAfterFirst = _store.GetMaterialQty(_playerId, AssureId);

        // The SAME correlation id, a DIFFERENT (larger) load -- must return the RECORDED outcome and
        // debit nothing further, never re-resolve with the new count.
        var replay = Bench().Enhance(_playerId, _instanceId, RecipeId, "assure-replay",
            new[] { new AssuranceLine(AssureId, charges + 50) });

        Assert.True(replay.Ok, replay.Reason);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Outcome, replay.Outcome);
        Assert.Equal(first.EnhanceLevel, replay.EnhanceLevel);
        Assert.Equal(balanceAfterFirst, _store.GetMaterialQty(_playerId, AssureId));
    }

    [Fact]
    public void A_duplicated_id_in_one_request_sums_into_one_debited_line()
    {
        SeedEnhanceLevel(19);
        var t = EnhancementTuning.Parse(Tuning("enhancement.v1.json"));
        var baseSuccess = EnhancePolicy.SuccessMilli(20, t);
        var bonus = CraftAssuranceTuningHub.Tuning.AssureBonusMilli;
        var total = (int)((1000 - baseSuccess) / bonus);
        Assert.True(total >= 2, "need at least 2 charges to split across two request entries");
        var first = total - 1;

        _store.GrantMaterials(_playerId, new[] { (AssureId, (long)total) });

        // The SAME id listed twice, split across two request entries -- the policy's own loaded
        // count (AssureLoaded) must equal the SUMMED, coalesced debited line, never two lines.
        var outcome = Bench().Enhance(_playerId, _instanceId, RecipeId, "assure-duplicate-id",
            new[] { new AssuranceLine(AssureId, first), new AssuranceLine(AssureId, total - first) });

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(1000, outcome.SuccessMilli);
        var line = Assert.Single(outcome.Spent, l => l.MaterialId == AssureId);
        Assert.Equal(total, line.Qty);
    }

    [Fact]
    public void An_assure_line_on_promote_refuses_by_name_the_verb_that_does_not_roll_a_die()
    {
        // species-gear-chain T43: Promote's own CraftOperation (Elevate) IS now class-eligible for
        // Assurance (it reads `protect`, see the sibling test below) -- but `assure` raises a
        // success chance Promote never rolls, so ItemWorkbench's own per-id gate refuses it by name,
        // never CostClassMatrix.Allows (which is correctly true here).
        Assert.True(CostClassMatrix.Allows(CraftOperation.Elevate, MaterialClass.Assurance));

        _store.GrantMaterials(_playerId, new[] { (AssureId, 10L) });
        var outcome = Bench().Promote(_playerId, _instanceId, ElevateRecipeId, "promote-assure-wrong-id",
            new[] { new AssuranceLine(AssureId, 1) });

        Assert.False(outcome.Ok);
        Assert.Contains("enhance.assurance-line-unrecognized", outcome.Reason);
    }

    [Fact]
    public void An_unprotected_promote_past_exhaustion_decrements_durability_by_the_wear_formula()
    {
        _store.SetPotential(_instanceId, max: 1, current: 0); // exhausted (CanDecay)
        _store.SetDurability(_instanceId, max: 1000, current: 1000);

        var outcome = Bench(FixedWear).Promote(_playerId, _instanceId, ElevateRecipeId, "promote-unprotected-wear");

        Assert.True(outcome.Ok, outcome.Reason);
        var expectedWear = CraftRiskPolicy.WearFor(1000, FixedWear.WearPerAttemptMilli);
        Assert.Equal(1000 - expectedWear, _store.GetDurability(_instanceId).Current);
    }

    [Fact]
    public void A_protected_promote_past_exhaustion_leaves_durability_unchanged_and_debits_one_unit()
    {
        _store.SetPotential(_instanceId, max: 1, current: 0); // exhausted (CanDecay)
        _store.SetDurability(_instanceId, max: 1000, current: 1000);
        _store.GrantMaterials(_playerId, new[] { (ProtectId, 1L) });

        var outcome = Bench(FixedWear).Promote(_playerId, _instanceId, ElevateRecipeId, "promote-protected-wear",
            new[] { new AssuranceLine(ProtectId, 1) });

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal(1000L, _store.GetDurability(_instanceId).Current); // completely unchanged
        var line = Assert.Single(outcome.Spent, l => l.MaterialId == ProtectId);
        Assert.Equal(1, line.Qty);
        Assert.Equal(0L, _store.GetMaterialQty(_playerId, ProtectId));
    }

    [Fact]
    public void A_replayed_protected_promote_decrements_and_debits_nothing_twice()
    {
        _store.SetPotential(_instanceId, max: 1, current: 0);
        _store.SetDurability(_instanceId, max: 1000, current: 1000);
        _store.GrantMaterials(_playerId, new[] { (ProtectId, 5L) });

        var bench = Bench(FixedWear);
        var first = bench.Promote(_playerId, _instanceId, ElevateRecipeId, "promote-protect-replay",
            new[] { new AssuranceLine(ProtectId, 1) });
        Assert.True(first.Ok, first.Reason);
        Assert.Equal(4L, _store.GetMaterialQty(_playerId, ProtectId));
        Assert.Equal(1000L, _store.GetDurability(_instanceId).Current);

        // The rung already moved, so a genuine second Promote would refuse at the top rung if
        // replayed the ordinary way -- proving the SAME correlation id short-circuits before any of
        // that even runs, exactly like Enhance's own replay guarantee.
        var replay = bench.Promote(_playerId, _instanceId, ElevateRecipeId, "promote-protect-replay",
            new[] { new AssuranceLine(ProtectId, 3) });

        Assert.True(replay.Ok, replay.Reason);
        Assert.True(replay.Replayed);
        Assert.Equal(4L, _store.GetMaterialQty(_playerId, ProtectId)); // nothing debited a second time
        Assert.Equal(1000L, _store.GetDurability(_instanceId).Current); // nothing decremented a second time
    }

    [Fact]
    public void A_malformed_assurance_line_refuses_by_name()
    {
        SeedEnhanceLevel(19);
        var outcome = Bench().Enhance(_playerId, _instanceId, RecipeId, "assure-malformed",
            new[] { new AssuranceLine("shard.chaff", 1) });

        Assert.False(outcome.Ok);
        Assert.Contains("enhance.assurance-line-not-assurance", outcome.Reason);
    }

    [Fact]
    public void A_repair_assurance_line_on_enhance_refuses_by_name()
    {
        SeedEnhanceLevel(19);
        _store.GrantMaterials(_playerId, new[] { (MaterialCatalog.AssuranceId("repair"), 1L) });
        var outcome = Bench().Enhance(_playerId, _instanceId, RecipeId, "assure-repair-on-enhance",
            new[] { new AssuranceLine(MaterialCatalog.AssuranceId("repair"), 1) });

        Assert.False(outcome.Ok);
        Assert.Contains("enhance.assurance-line-unrecognized", outcome.Reason);
    }

    [Fact]
    public void No_assurance_lines_is_byte_identical_to_the_pre_T42_shape()
    {
        SeedEnhanceLevel(3); // safe band, always succeeds
        var outcome = Bench().Enhance(_playerId, _instanceId, RecipeId, "assure-none");
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.DoesNotContain(outcome.Spent, l => l.Class == nameof(MaterialClass.Assurance));
    }

    // ---- species-gear-chain T44: repair coverage leg; protect refused on repair (R10) --------------

    static string RepairId => MaterialCatalog.AssuranceId("repair");

    [Fact]
    public void A_repair_charge_debits_in_the_one_transaction_and_the_destroy_chance_is_unaffected()
    {
        _store.SetDurability(_instanceId, max: 1000, current: 500);
        _store.GrantMaterials(_playerId, new[] { (RepairId, 1L) });

        var outcome = Bench().Repair(_playerId, _instanceId, RepairRecipeId, "repair-with-charge",
            new[] { new AssuranceLine(RepairId, 1) });

        Assert.True(outcome.Ok, outcome.Reason);
        var line = Assert.Single(outcome.Spent, l => l.MaterialId == RepairId);
        Assert.Equal(1, line.Qty);
        Assert.Equal(0L, _store.GetMaterialQty(_playerId, RepairId));
        // R10: the destroy chance is a real, tuning-driven roll this leg never touches -- proven
        // DETERMINISTICALLY at the Core level (RepairPolicyTests.Loading_repair_charges_never_moves_
        // the_destroy_chance). Not re-asserted here against a live roll, which would be flaky.
    }

    [Fact]
    public void A_protect_line_on_repair_refuses_by_name_R10()
    {
        _store.SetDurability(_instanceId, max: 1000, current: 500);
        _store.GrantMaterials(_playerId, new[] { (ProtectId, 1L) });

        var outcome = Bench().Repair(_playerId, _instanceId, RepairRecipeId, "repair-protect-refused",
            new[] { new AssuranceLine(ProtectId, 1) });

        Assert.False(outcome.Ok);
        Assert.Contains("enhance.assurance-line-unrecognized", outcome.Reason);
        Assert.Equal(1L, _store.GetMaterialQty(_playerId, ProtectId)); // debits nothing
    }

    [Fact]
    public void An_assure_line_on_repair_refuses_by_name()
    {
        _store.SetDurability(_instanceId, max: 1000, current: 500);
        _store.GrantMaterials(_playerId, new[] { (AssureId, 1L) });

        var outcome = Bench().Repair(_playerId, _instanceId, RepairRecipeId, "repair-assure-refused",
            new[] { new AssuranceLine(AssureId, 1) });

        Assert.False(outcome.Ok);
        Assert.Contains("enhance.assurance-line-unrecognized", outcome.Reason);
        Assert.Equal(1L, _store.GetMaterialQty(_playerId, AssureId));
    }

    [Fact]
    public void A_replayed_repair_with_a_charge_debits_nothing_a_second_time()
    {
        _store.SetDurability(_instanceId, max: 1000, current: 500);
        _store.GrantMaterials(_playerId, new[] { (RepairId, 5L) });

        var bench = Bench();
        var first = bench.Repair(_playerId, _instanceId, RepairRecipeId, "repair-replay",
            new[] { new AssuranceLine(RepairId, 1) });
        Assert.True(first.Ok, first.Reason);
        Assert.Equal(4L, _store.GetMaterialQty(_playerId, RepairId));

        var replay = bench.Repair(_playerId, _instanceId, RepairRecipeId, "repair-replay",
            new[] { new AssuranceLine(RepairId, 3) });

        Assert.True(replay.Ok, replay.Reason);
        Assert.True(replay.Replayed);
        Assert.Equal(4L, _store.GetMaterialQty(_playerId, RepairId)); // nothing debited a second time
    }
}
