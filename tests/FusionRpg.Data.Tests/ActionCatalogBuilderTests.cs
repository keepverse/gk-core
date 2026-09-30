using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests;

/// <summary>aura-skill T19 (audit D3 part two): "the correctness half of the owner's 'both' answer —
/// equipped actions resolve properly rather than being silently dropped by T3's degrade path."
/// `RpgStore.BuildActionCatalog` is the FIRST production caller of `ActionCompiler.Compile` at bulk
/// scale — before this task, `BattleEngine.Resolve` was NEVER given a non-null `ActionCatalog`
/// anywhere in production, so any non-empty `EquippedActionIds` always hit T3's degrade path,
/// regardless of whether the equipped action was perfectly valid. `RungPolicy` is configured globally
/// for this assembly (`ContractTuningTestBootstrap`'s `[ModuleInitializer]`), matching production's
/// own `Program.cs` startup sequence.</summary>
[Trait("VerificationId", "data.action-catalog")]
public class ActionCatalogBuilderTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ActionCatalogBuilderTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    string SeedSkillAction(string actionId)
    {
        var containerId = "skill." + actionId.Replace('.', '-') + "-container";
        var atomId = AtomRow.DeriveId("atom." + actionId.Replace('.', '-'), "", 1);
        var atomResult = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom." + actionId.Replace('.', '-'),
            Variant = "",
            Tier = 1,
            Name = actionId,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        });
        Assert.True(atomResult.IsOk, atomResult.ToString());

        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());

        var actionResult = _store.UpsertAction(new ActionRow
        {
            ActionId = actionId,
            Name = actionId,
            Kind = ActionKind.Skill,
            Rung = 1,
            ContainerId = containerId,
            Grantable = true,
            Tags = new[] { ActionTag.Offensive },
        });
        Assert.True(actionResult.IsOk, actionResult.ToString());
        return actionId;
    }

    [Fact]
    public void An_empty_store_builds_an_empty_catalog_never_throws()
    {
        var catalog = _store.BuildActionCatalog(RungPolicy.Table);
        Assert.Equal(0, catalog.Count);
    }

    [Fact]
    public void A_real_authored_skill_compiles_and_resolves_from_the_built_catalog()
    {
        // The exact D3 fix, proven at the level this task actually builds: before T19, NOTHING ever
        // called ActionCompiler.Compile against a real ActionRow in production -- a valid skill would
        // compile in isolation (ActionCompilerTests.cs) but never reach a real ActionCatalog.
        var actionId = SeedSkillAction("skill.catalog-real-resolve");

        var catalog = _store.BuildActionCatalog(RungPolicy.Table);

        var compiled = catalog.Get(actionId);
        Assert.NotNull(compiled);
        Assert.Equal(actionId, compiled!.ActionId);
        Assert.Equal(ActionKind.Skill, compiled.Kind);
    }

    // ---- A-G1 (spec-tier-access-gate.md §3.2, §5 tests 3/4) -----------------------------------------
    // The real production caller: this method IS the one place every authored action passes through
    // on the way to a battle-usable catalog (WebMatchService's own three call sites). A test that
    // called ContentValidation.Budget directly would prove the CHECK works, not that the CALLER is
    // real -- these go through BuildActionCatalog itself, the way spec test 4 demands.

    static string RepoRoot([CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }

    static RungTable ShippedV2RungTable() =>
        RungTableLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "action-rungs.v2.json")));

    /// <summary>Same shape as <see cref="SeedSkillAction"/>, but with a caller-chosen magnitude so the
    /// container's priced power can be pushed over or kept under a rung's budget on purpose.</summary>
    string SeedSkillActionWithMagnitude(string actionId, int rung, long amount)
    {
        var containerId = "skill." + actionId.Replace('.', '-') + "-container";
        var atomId = AtomRow.DeriveId("atom." + actionId.Replace('.', '-'), "", 1);
        var atomResult = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom." + actionId.Replace('.', '-'),
            Variant = "",
            Tier = 1,
            Name = actionId,
            ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{amount}}}""",
        });
        Assert.True(atomResult.IsOk, atomResult.ToString());

        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());

        var actionResult = _store.UpsertAction(new ActionRow
        {
            ActionId = actionId,
            Name = actionId,
            Kind = ActionKind.Skill,
            Rung = rung,
            ContainerId = containerId,
            Grantable = true,
            Tags = new[] { ActionTag.Offensive },
        });
        Assert.True(actionResult.IsOk, actionResult.ToString());
        return containerId;
    }

    [Fact]
    public void A_container_that_hugely_overspends_its_rung_budget_is_excluded_and_reported_by_the_real_caller()
    {
        // Test 4, the planted violation: 100,000 on a single `maxHp` atom prices far above rung 1's
        // 1,000-milli budget (action-rungs.v2.json). If BuildActionCatalog's own budget stage were
        // absent or bypassed, this action would compile clean (it is otherwise perfectly valid) and
        // silently reach the battle catalog -- the exact failure mode the check exists to catch.
        var containerId = SeedSkillActionWithMagnitude("skill.g1-overspent", rung: 1, amount: 100_000);

        List<(string ActionId, ActionRejection Rejection)> rejected = new();
        var catalog = _store.BuildActionCatalog(ShippedV2RungTable(), (id, r) => rejected.Add((id, r)));

        Assert.Null(catalog.Get("skill.g1-overspent"));
        var report = Assert.Single(rejected, r => r.ActionId == "skill.g1-overspent");
        Assert.Equal(ActionRejectionReason.PowerBudgetExceeded, report.Rejection.Reason);
        Assert.Contains(containerId, report.Rejection.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_inside_its_rung_budget_still_reaches_the_real_catalog()
    {
        // The companion to the planted-violation test: proves the budget stage is not simply
        // rejecting everything. A modest amount at rung 10 (budget 37,221 milli) stays well under.
        SeedSkillActionWithMagnitude("skill.g1-thrifty", rung: 10, amount: 5);

        var catalog = _store.BuildActionCatalog(ShippedV2RungTable());

        Assert.NotNull(catalog.Get("skill.g1-thrifty"));
    }

    [Fact]
    public void A_v1_rung_table_with_no_powerBudgetMilli_column_never_rejects_on_budget()
    {
        // Backward compatibility: a rung table loaded from action-rungs.v1.json (no powerBudgetMilli
        // column) resolves every rung's budget to null, so the check is skipped rather than treating
        // the absent ceiling as zero -- production behaviour is unchanged until something actually
        // loads v2.
        var v1Table = RungTableLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "action-rungs.v1.json")));
        SeedSkillActionWithMagnitude("skill.g1-v1-unbudgeted", rung: 1, amount: 100_000);

        var catalog = _store.BuildActionCatalog(v1Table);

        Assert.NotNull(catalog.Get("skill.g1-v1-unbudgeted"));
    }

    [Fact]
    public void A_rejected_row_is_skipped_not_fatal_to_the_whole_catalog()
    {
        // "Skipped, not fatal" -- one bad row (here: a container that no longer exists, simulating an
        // authoring mistake) must not take down every OTHER action's ability to resolve.
        var good = SeedSkillAction("skill.catalog-good");
        var badResult = _store.UpsertAction(new ActionRow
        {
            ActionId = "skill.catalog-bad",
            Name = "skill.catalog-bad",
            Kind = ActionKind.Skill,
            Rung = 1,
            ContainerId = "skill.catalog-bad-container", // never created -- UnknownContainer at compile time
            Grantable = true,
            Tags = new[] { ActionTag.Offensive },
        });
        // UpsertAction itself may or may not reject an unknown container at write time depending on
        // its own validation scope -- either is fine for this test, which only needs the ROW to exist
        // (or not) and the GOOD row to still compile regardless.
        _ = badResult;

        List<(string ActionId, ActionRejection Rejection)> rejected = new();
        var catalog = _store.BuildActionCatalog(RungPolicy.Table, (id, r) => rejected.Add((id, r)));

        Assert.NotNull(catalog.Get(good));
    }
}
