using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `item-power-reads` (item module 9) at the store: the rarity-keyed
/// <c>ContentValidation.Budget</c>'s first production caller, and the <c>ceilingFor</c> it finally
/// passes.
///
/// <para><b>The red-first pair is the point of this file.</b> Before module 9's reader existed, the
/// only thing that could be said about <c>ContentValidation.cs:73</c> was that nothing reached it —
/// so a test that merely asserts a green report proves nothing. Every test below therefore asserts
/// <see cref="FusionRpg.Core.Effects.Atoms.Power.ContentReport.Evaluated"/>, which is the number that
/// separates "checked and clean" from "checked nothing and said clean".</para>
/// </summary>
[Trait("VerificationId", "data.item-power-budget")]
public class RarityPowerBudgetStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public RarityPowerBudgetStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>`gk-data/packs/fusion/data/seed/rarity/ladder.v1.json`, row for row.</summary>
    static readonly RarityRow[] Ladder =
    {
        new("chaff", 10, 0, 0, 1, 1),
        new("sprout", 20, 0, 1, 1, 1),
        new("grafted", 30, 0, 1, 1, 3),
        new("cultivated", 40, 1, 1, 1, 3),
        new("fused", 50, 1, 1, 2, 4),
        new("chimeric", 60, 1, 2, 2, 4),
        new("heirloom", 70, 1, 2, 3, 5),
        new("firstseed", 80, 2, 2, 3, 5),
        new("sunwoven", 90, 2, 2, 4, 5),
        new("almanac", 100, 3, 2, 4, 5),
    };

    /// <summary>`gk-data/packs/fusion/data/seed/atoms/fx-core.json`'s first entry, verbatim — the one atom the one
    /// rarity-bearing container in the shipped seed tree actually holds.</summary>
    static AtomRow PassiveAtkFlat => new()
    {
        AtomId = AtomRow.DeriveId("atom.fx-passive-atk-flat", "", 1),
        KindId = "stat.modify",
        FamilyId = "atom.fx-passive-atk-flat",
        Tier = 1,
        Name = "Passive ATK +10",
        ParamsJson = "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":10}",
    };

    /// <summary>`gk-data/packs/fusion/data/seed/containers/first-clear-grants.json` — as of today the ONLY container in the
    /// shipped seed tree that names a rarity, which makes it the whole live population of this
    /// check.</summary>
    static ContainerRow FirstClearAlmanacSeed => new()
    {
        ContainerId = "item.first-clear-almanac-seed",
        Kind = ContainerKind.Item,
        Rarity = "almanac",
        Atoms = new List<ContainerAtomRow> { new(0, PassiveAtkFlat.AtomId) },
    };

    static IReadOnlyDictionary<string, ItemRarityRungTuning> RealTuning()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector")))
                return ItemRarityTuning.Parse(File.ReadAllText(
                    Path.Combine(dir.FullName, "data", "tuning", "item-rarity.v1.json")));
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    void ImportRealContent()
    {
        foreach (var rung in Ladder) Assert.True(_store.UpsertRarity(rung).Ok);
        Assert.True(_store.UpsertAtom(PassiveAtkFlat).IsOk);
        var check = _store.UpsertContainer(FirstClearAlmanacSeed);
        Assert.True(check.IsOk, check.ToString());
    }

    // ---- the red-first pair ------------------------------------------------------------------------

    [Fact]
    public void With_no_seeded_power_ceiling_the_budget_check_reports_green_over_zero_containers()
    {
        // RED. This is exactly what every caller of the rarity-keyed overload saw before module 9's
        // reader existed: ContentValidation.cs:73's `is not { } ceiling` skips, Ok is true, and the
        // pass examined NOTHING. The report says so, which is the only reason the degrade was ever
        // discoverable at all.
        ImportRealContent();

        var report = _store.ValidateRarityPowerBudget();

        Assert.True(report.Ok);
        Assert.Equal(0, report.Evaluated);
        Assert.Equal(0, _store.GetRarityPowerCeilings().PricedRungs);
        Assert.Null(_store.GetRarityPowerCeilings().CeilingFor("almanac"));
    }

    [Fact]
    public void With_the_ceiling_seeded_the_same_container_is_actually_evaluated()
    {
        // GREEN. Same content, same call, one seeded column — and the skip at :73 stops firing.
        ImportRealContent();
        _store.SeedRarityLadder(RealTuning());

        var report = _store.ValidateRarityPowerBudget();

        Assert.True(report.Ok);
        Assert.Equal(1, report.Evaluated);
        Assert.NotNull(_store.GetRarityPowerCeilings().CeilingFor("almanac"));
    }

    // ---- the reader, against the real seeded rows ----------------------------------------------------

    [Fact]
    public void The_seeded_column_prices_every_rung_against_the_specs_published_table()
    {
        ImportRealContent();
        _store.SeedRarityLadder(RealTuning());

        var ceilings = _store.GetRarityPowerCeilings();

        Assert.Equal("almanac", ceilings.TopRungId);
        Assert.Equal(46_000L, ceilings.PinAe);
        Assert.Equal(RarityLadder.RungIds.Count, ceilings.PricedRungs);

        // pinAE x share / 1000, with the shares read straight out of rarity_budget.
        Assert.Equal(0L, ceilings.Of("chaff").CeilingPoints!.Value);
        Assert.Equal(1_012L, ceilings.Of("sprout").CeilingPoints!.Value);
        Assert.Equal(22_632L, ceilings.Of("heirloom").CeilingPoints!.Value);
        Assert.Equal(46_000L, ceilings.Of("almanac").CeilingPoints!.Value);

        // ...and the share the reader used is the row the store holds, not a second copy.
        foreach (var rungId in RarityLadder.RungIds)
            Assert.Equal(
                _store.GetRarityBudget(rungId, "power_ceiling"),
                ceilings.Of(rungId).LadderShareMilli);
    }

    [Fact]
    public void An_over_budget_container_is_a_finding_that_names_it_and_the_server_still_boots()
    {
        // The lint's whole contract: report the offender, never clamp it, never refuse the import.
        // A sprout-rung container (ceiling 1012) holding a 400 hp affix (40,000 points) is over by a
        // factor of forty and must say so by id.
        ImportRealContent();
        _store.SeedRarityLadder(RealTuning());

        var fat = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.overspent", "", 1),
            KindId = "stat.modify",
            FamilyId = "atom.overspent",
            Tier = 1,
            Name = "Overspent",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":400}",
        };
        Assert.True(_store.UpsertAtom(fat).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.overspent-sprout",
            Kind = ContainerKind.Item,
            Rarity = "sprout",
            Atoms = new List<ContainerAtomRow> { new(0, fat.AtomId) },
        }).IsOk);

        var report = _store.ValidateRarityPowerBudget();

        Assert.Equal(2, report.Evaluated);
        var failure = Assert.Single(report.Failures);
        Assert.Equal("item.overspent-sprout", failure.Subject);
        Assert.Contains("1012", failure.Detail, StringComparison.Ordinal);

        // Nothing was clamped: the container is still in the store exactly as authored.
        Assert.Single(_store.GetContainer("item.overspent-sprout")!.Atoms);
    }

    // ---- the enumeration ------------------------------------------------------------------------------

    [Fact]
    public void Only_containers_that_name_a_rarity_are_enumerated()
    {
        ImportRealContent();
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "skill.no-rarity",
            Kind = ContainerKind.Skill,
            Atoms = new List<ContainerAtomRow> { new(0, PassiveAtkFlat.AtomId) },
        }).IsOk);

        var ids = _store.ListContainerIdsWithRarity();

        Assert.Equal(new[] { "item.first-clear-almanac-seed" }, ids);
    }

    [Fact]
    public void An_empty_rarity_ladder_names_its_own_absence_rather_than_pricing_nothing_quietly()
    {
        // A store with no ladder cannot price pinAE at all. That must be a named reason, not a table
        // of ten silent nulls that reads the same as "everything is within budget".
        var ceilings = _store.GetRarityPowerCeilings();

        Assert.Equal(0, ceilings.PricedRungs);
        Assert.NotNull(ceilings.UnavailableReason);
        Assert.Contains("unpriced", ceilings.RenderPinAe(), StringComparison.Ordinal);
    }
}
