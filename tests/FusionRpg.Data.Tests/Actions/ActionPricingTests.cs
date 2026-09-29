using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Data.Tests.Actions;

/// <summary>
/// ST4.1 (`spec-budget-calibration-report.md` contracts 1 and 3): `ListActionPricing` is the read the
/// calibration report is built on, and it must price through the SAME path A-G1's catalog check uses —
/// otherwise the report calibrates a number nobody enforces.
///
/// <para>Two properties, and both are about the boundary rather than a value: every containered action
/// is priced <b>including the ones the budget rejects</b> (that gap is what the report measures), and
/// the price agrees with the check's own verdict on real seeded content.</para>
/// </summary>
[Trait("VerificationId", "data.action-pricing")]
public class ActionPricingTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ActionPricingTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string RepoRoot([CallerFilePath] string here = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", ".."));

    static RungTable ShippedRungTable() =>
        RungTableLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "action-rungs.v2.json")));

    /// <summary>Returns the container id, so a test can re-price it independently.</summary>
    string SeedSkill(string actionId, int rung, long amount)
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

    /// <summary>An independent second pricing, computed here from the stored atoms — the "second
    /// pricing function" whose disagreement spec test 5 exists to catch.</summary>
    long RepriceIndependently(string containerId)
    {
        var container = _store.GetContainer(containerId);
        Assert.NotNull(container);
        var atoms = container!.Atoms
            .Select(a => _store.GetAtom(a.AtomId))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();
        return (long)ActorPowerCache.Compose(atoms).Total;
    }

    [Fact]
    public void Every_containered_action_is_priced_including_the_one_the_budget_rejects()
    {
        // Contract 3, spec test 4. The overspent rung-1 action is dropped by the catalog build, which
        // is exactly why the report has to keep it: the actions the check refuses are the reading.
        var overspent = SeedSkill("skill.st41-overspent", rung: 1, amount: 100_000);
        SeedSkill("skill.st41-thrifty", rung: 10, amount: 5);

        var table = ShippedRungTable();
        var catalog = _store.BuildActionCatalog(table);
        var priced = _store.ListActionPricing();

        Assert.Null(catalog.Get("skill.st41-overspent"));
        Assert.NotNull(catalog.Get("skill.st41-thrifty"));

        var overspentRow = Assert.Single(priced, p => p.ActionId == "skill.st41-overspent");
        Assert.Equal(1, overspentRow.AuthoredRung);
        Assert.Contains(priced, p => p.ActionId == "skill.st41-thrifty");
        Assert.NotNull(overspent);
    }

    [Fact]
    public void The_report_prices_exactly_as_an_independent_second_pricing_does()
    {
        // Spec test 5, the planted violation. If a second pricing function were swapped into
        // `ListActionPricing` (a different compose, a different atom set, a cached figure), this
        // disagrees and fails -- which is the whole point of routing both readers through one helper.
        var containerId = SeedSkill("skill.st41-reprice", rung: 4, amount: 250);

        var priced = Assert.Single(_store.ListActionPricing(),
                                   p => p.ActionId == "skill.st41-reprice");

        Assert.Equal(RepriceIndependently(containerId), priced.RealizedPowerMilli);
    }

    [Fact]
    public void The_report_and_the_catalog_check_agree_on_where_the_budget_line_is()
    {
        // The report's figure and the check's verdict must be the SAME number: inside the rung's
        // budget the catalog keeps the action, above it the catalog drops it, and the priced figure
        // says which side of `powerBudgetMilli` it is on. A second pricing path breaks this pair.
        var table = ShippedRungTable();
        var inside = SeedSkill("skill.st41-inside", rung: 10, amount: 5);
        var above = SeedSkill("skill.st41-above", rung: 1, amount: 100_000);

        var catalog = _store.BuildActionCatalog(table);
        var priced = _store.ListActionPricing().ToDictionary(p => p.ActionId, StringComparer.Ordinal);

        Assert.True(table.TryGet(10, out var rungTen));
        Assert.True(table.TryGet(1, out var rungOne));
        var budgetTen = rungTen.PowerBudgetMilli;
        var budgetOne = rungOne.PowerBudgetMilli;
        Assert.NotNull(budgetTen);
        Assert.NotNull(budgetOne);

        Assert.Equal(RepriceIndependently(inside), priced["skill.st41-inside"].RealizedPowerMilli);
        Assert.True(priced["skill.st41-inside"].RealizedPowerMilli <= budgetTen!.Value,
                    "the kept action must price at or under its rung's budget");
        Assert.NotNull(catalog.Get("skill.st41-inside"));

        Assert.Equal(RepriceIndependently(above), priced["skill.st41-above"].RealizedPowerMilli);
        Assert.True(priced["skill.st41-above"].RealizedPowerMilli > budgetOne!.Value,
                    "the rejected action must price above its rung's budget");
        Assert.Null(catalog.Get("skill.st41-above"));
    }

    [Fact]
    public void An_empty_store_prices_nothing()
    {
        Assert.Empty(_store.ListActionPricing());
    }

    [Fact]
    public void A_pooled_channel_atom_the_pricing_cannot_price_is_named_not_silently_zero()
    {
        // ST4.5e: the real corpus's three zero-priced actions. Their channel is a POOL reference and
        // `RpgStore` has no channel-pool catalog to resolve it, so the atom is genuinely unpriced and
        // the action's realized power is 0 -- which used to be the whole story the report told. The id
        // is what makes the 0 readable: "these atoms could not be priced", not "worth nothing".
        var atomId = AtomRow.DeriveId("atom.st45e-pooled", "", 1);
        var containerId = "skill.st45e-pooled-container";

        var atomResult = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.derived",
            FamilyId = "atom.st45e-pooled",
            Tier = 1,
            Name = "pooled",
            ParamsJson = """
                {"amount":{"max":43,"min":21,"roll":"onApply"},
                 "channel":{"allowRepeat":false,"count":1,"pool":"pool.element-dodge"},"op":"flat"}
                """,
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
            ActionId = "skill.st45e-pooled",
            Name = "skill.st45e-pooled",
            Kind = ActionKind.Skill,
            Rung = 7,
            ContainerId = containerId,
            Grantable = true,
            Tags = new[] { ActionTag.Offensive },
        });
        Assert.True(actionResult.IsOk, actionResult.ToString());

        var pricedAction = Assert.Single(_store.ListActionPricing(), p => p.ActionId == "skill.st45e-pooled");

        Assert.Equal(0, pricedAction.RealizedPowerMilli);
        Assert.NotNull(pricedAction.UnpricedAtomIds);
        Assert.Equal(new[] { atomId }, pricedAction.UnpricedAtomIds!);
    }
}
