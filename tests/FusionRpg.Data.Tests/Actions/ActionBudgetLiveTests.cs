using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Actions;

/// <summary>
/// ST5.3 (`spec-rung-table-activation.md` contract 2, spec test 4): the budget check is LIVE against the
/// table production loads. Until ST5.2 the server read `action-rungs.v1.json`, which predates the
/// `powerBudgetMilli` column, so the check's ceiling was always null and no action could ever be
/// rejected — the column existing is what makes this test meaningful, and the row assertion below is
/// what fails if a publish ever ships a table without it.
///
/// <para><b>Nothing here pins a version, a pass count or a budget value.</b> The table is discovered as
/// the newest published file, which is "the table production loads" stated without hard-coding a
/// number, and the assertions are about the REJECTION and its naming — spec test 4's own words.</para>
/// </summary>
[Trait("VerificationId", "data.action-budget-live")]
public class ActionBudgetLiveTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ActionBudgetLiveTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string RepoRoot([CallerFilePath] string here = "") => KeepverseRoots.Core();

    /// <summary>The newest published rung table — discovered, never named, so a future publish does not
    /// make this test read a stale version.</summary>
    static RungTable CurrentRungTable()
    {
        var newest = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "data", "tuning"), "action-rungs.v*.json")
            .Select(path => (Path: path, Version: int.Parse(
                Path.GetFileName(path)["action-rungs.v".Length..^".json".Length], System.Globalization.CultureInfo.InvariantCulture)))
            .OrderByDescending(x => x.Version)
            .First();

        return RungTableLoader.Parse(File.ReadAllText(newest.Path));
    }

    /// <summary>Returns the container id, so the rejection can be checked for naming it.</summary>
    string Seed(string actionId, int rung, long amount)
    {
        var containerId = "skill." + actionId.Replace('.', '-') + "-container";
        var atomId = AtomRow.DeriveId("atom." + actionId.Replace('.', '-'), "", 1);

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom." + actionId.Replace('.', '-'),
            Tier = 1,
            Name = actionId,
            ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{amount}}}""",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        Assert.True(_store.UpsertAction(new ActionRow
        {
            ActionId = actionId,
            Name = actionId,
            Kind = ActionKind.Skill,
            Rung = rung,
            ContainerId = containerId,
            Grantable = true,
            Tags = new[] { ActionTag.Offensive },
        }).IsOk);

        return containerId;
    }

    [Fact]
    public void The_live_table_carries_a_budget_ceiling_on_every_row()
    {
        // The precondition the rest of this file rests on: without the column the check cannot reject
        // anything, which is exactly the state ST5.2 ended.
        var table = CurrentRungTable();

        Assert.All(table.Rows, row => Assert.True(
            row.PowerBudgetMilli is > 0,
            $"rung {row.Rung} carries no powerBudgetMilli, so the live check has no ceiling to enforce"));
    }

    [Fact]
    public void A_planted_over_budget_container_is_rejected_by_name_and_an_affordable_one_is_kept()
    {
        // Spec test 4, through the real path: a real store, the real import shape, and the real
        // BuildActionCatalog loading the table production loads. The plant is over its rung's budget by
        // orders of magnitude, so it cannot be a near-miss on any row of any published version.
        var table = CurrentRungTable();
        var overspent = Seed("action.st53-overspent", rung: 1, amount: 100_000);
        Seed("action.st53-thrifty", rung: 10, amount: 5);

        var rejections = new Dictionary<string, ActionRejection>(StringComparer.Ordinal);
        var catalog = _store.BuildActionCatalog(table, (id, rejection) => rejections[id] = rejection);

        Assert.Null(catalog.Get("action.st53-overspent"));
        Assert.NotNull(catalog.Get("action.st53-thrifty"));

        var rejection = Assert.Single(rejections);
        Assert.Equal("action.st53-overspent", rejection.Key);
        Assert.Equal(ActionRejectionReason.PowerBudgetExceeded, rejection.Value.Reason);

        // "naming its id": the container is the budget's subject, so its id is what the finding carries.
        Assert.Contains(overspent, rejection.Value.ToString(), StringComparison.Ordinal);
    }
}
