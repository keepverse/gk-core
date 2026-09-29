using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Delve.Domains;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>D4.19 (spec-domain-catalog.md §4) — `DomainOffers.For`: hidden-when-stale, sealed/resume
/// from live delve state, rungs replaced by resume, the rung-vs-tail-step clear split, and "the six
/// first-ship domains render" (the todo's own literal Verify line, proven with six domains here since
/// no real seed content exists yet, D4.16).</summary>
public class DomainOffersTests
{
    static readonly IReadOnlyDictionary<string, int> DangerBandOrdinals = new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 };
    static readonly string[] KnownRungIds = { "very-easy", "easy", "medium", "hard", "very-hard", "nightmare", "hell", "abyss", "hopeless", "impossible" };

    static DomainRow Domain(string id, string entry = "many", string name = "Test Domain") =>
        new(id, name, "A test flavor.", "theme.overgrown", "fire", "shallow", entry,
            "layout.standard", "species.warden", null, "Lair", null);

    static DomainCatalog CatalogWith(params DomainRow[] rows) => DomainCatalog.Load(rows, DangerBandOrdinals).Catalog;

    static RungOfferSet PassingOffer(bool onceEntry = false) => new(
        Rungs: new[]
        {
            new RungOfferRow("very-easy", Offered: false, Band: null, BandName: null, IsPermadeath: false, Refusal: RungOfferRefusal.BandBelowFloor),
            new RungOfferRow("medium", Offered: true, Band: 2, BandName: "Shallow", IsPermadeath: false, Refusal: RungOfferRefusal.None),
            new RungOfferRow("very-hard", Offered: true, Band: 4, BandName: "Abyssal", IsPermadeath: true, Refusal: RungOfferRefusal.None),
            new RungOfferRow("impossible", Offered: false, Band: null, BandName: null, IsPermadeath: true, Refusal: RungOfferRefusal.NotUnlockedYet),
        },
        TailSteps: new[] { new TailOfferRow(1, Offered: true, Band: 4, BandName: "Abyssal", Label: "Abyss +1", Refusal: RungOfferRefusal.None) },
        IsOnceEntry: onceEntry, OnceSealOnWipe: false, OnceFailKeepsBossLoot: false);

    static DomainOfferLive Live(
        Func<string, Staleness>? stalenessFor = null,
        Func<DomainRow, PlayerClears, RungOfferSet>? composeRungs = null) => new(
        KnownRungIds: KnownRungIds,
        StalenessFor: stalenessFor ?? (_ => Staleness.Fresh),
        ComposeRungs: composeRungs ?? ((_, _) => PassingOffer()),
        RungLabelFor: id => id switch { "medium" => "Medium", "very-hard" => "Very Hard", _ => id },
        BossDisplayNameFor: _ => "Grand Warden",
        RaidModesForLayout: _ => new[] { "solo", "duo" },
        ProvisionableFor: _ => new[] { new ProvisionableOfferDto("container.satchel", "Satchel", 250, 4) });

    static DomainProgressFact Progress(string domainId, params DomainClearFact[] clears) => new(domainId, clears);

    [Fact]
    public void A_fresh_found_domain_renders_with_offered_rungs_only()
    {
        var catalog = CatalogWith(Domain("domain.a"));
        var offers = DomainOffers.For(new[] { Progress("domain.a") }, catalog, _ => (null, null), Live());

        var offer = Assert.Single(offers);
        Assert.Equal("domain.a", offer.DomainId);
        Assert.Equal(2, offer.Rungs.Count); // BandBelowFloor and NotUnlockedYet rows dropped
        Assert.Contains(offer.Rungs, r => r.RungId == "medium");
        Assert.Contains(offer.Rungs, r => r.RungId == "very-hard");
        Assert.Single(offer.TailSteps);
    }

    [Fact]
    public void A_stale_domain_is_hidden_absent_not_locked()
    {
        var catalog = CatalogWith(Domain("domain.a"));
        var offers = DomainOffers.For(new[] { Progress("domain.a") }, catalog, _ => (null, null), Live(stalenessFor: _ => Staleness.Stale));

        Assert.Empty(offers);
    }

    [Fact]
    public void A_domain_removed_from_the_catalog_since_discovery_is_skipped()
    {
        var catalog = CatalogWith(Domain("domain.a")); // "domain.b" is not in the catalog
        var offers = DomainOffers.For(new[] { Progress("domain.b") }, catalog, _ => (null, null), Live());

        Assert.Empty(offers);
    }

    [Fact]
    public void A_many_domain_is_never_sealed_even_with_an_archived_delve_row()
    {
        var catalog = CatalogWith(Domain("domain.a", entry: "many"));
        var offers = DomainOffers.For(new[] { Progress("domain.a") }, catalog, _ => ("Archived", 99L), Live());

        Assert.False(Assert.Single(offers).Sealed);
    }

    [Fact]
    public void A_once_domain_with_an_archived_row_is_sealed()
    {
        var catalog = CatalogWith(Domain("domain.a", entry: "once"));
        var offers = DomainOffers.For(new[] { Progress("domain.a") }, catalog, _ => ("Archived", 99L), Live());

        Assert.True(Assert.Single(offers).Sealed);
    }

    [Fact]
    public void EntryKey_is_standing_for_many_and_single_descent_for_once()
    {
        var catalog = CatalogWith(Domain("domain.many", entry: "many"), Domain("domain.once", entry: "once"));
        var offers = DomainOffers.For(
            new[] { Progress("domain.many"), Progress("domain.once") }, catalog, _ => (null, null), Live());

        Assert.Equal("standing", offers.Single(o => o.DomainId == "domain.many").EntryKey);
        Assert.Equal("single-descent", offers.Single(o => o.DomainId == "domain.once").EntryKey);
    }

    [Fact]
    public void An_active_delve_replaces_rungs_and_tail_steps_with_resume()
    {
        var catalog = CatalogWith(Domain("domain.a"));
        var offers = DomainOffers.For(new[] { Progress("domain.a") }, catalog, _ => ("Active", 42L), Live());

        var offer = Assert.Single(offers);
        Assert.NotNull(offer.Resume);
        Assert.Equal(42L, offer.Resume!.DelveId);
        Assert.Empty(offer.Rungs);
        Assert.Empty(offer.TailSteps);
    }

    [Fact]
    public void OathOffered_and_permadeath_are_derived_from_IsPermadeath_never_both_true()
    {
        var catalog = CatalogWith(Domain("domain.a"));
        var offers = DomainOffers.For(new[] { Progress("domain.a") }, catalog, _ => (null, null), Live());

        var offer = Assert.Single(offers);
        var belowGate = offer.Rungs.Single(r => r.RungId == "medium");
        var atGate = offer.Rungs.Single(r => r.RungId == "very-hard");
        Assert.True(belowGate.OathOffered);
        Assert.False(belowGate.Permadeath);
        Assert.False(atGate.OathOffered);
        Assert.True(atGate.Permadeath);
    }

    [Fact]
    public void RaidModes_boss_name_and_provisionable_pass_through_the_caller_supplied_delegates()
    {
        var catalog = CatalogWith(Domain("domain.a"));
        var offers = DomainOffers.For(new[] { Progress("domain.a") }, catalog, _ => (null, null), Live());

        var offer = Assert.Single(offers);
        Assert.Equal(new[] { "solo", "duo" }, offer.RaidModes);
        Assert.Equal("Grand Warden", offer.BossName);
        Assert.Single(offer.Provisionable);
        Assert.Equal("container.satchel", offer.Provisionable[0].ContainerId);
    }

    // ---- the rung-vs-tail-step clear split ---------------------------------------------------------

    [Fact]
    public void A_known_rung_id_clear_becomes_a_RungIds_entry_passed_to_ComposeRungs()
    {
        PlayerClears? captured = null;
        var live = Live(composeRungs: (_, clears) => { captured = clears; return PassingOffer(); });
        var catalog = CatalogWith(Domain("domain.a"));

        DomainOffers.For(new[] { Progress("domain.a", new DomainClearFact("medium", false, 1)) }, catalog, _ => (null, null), live);

        Assert.Contains("medium", captured!.RungIds);
        Assert.Empty(captured.TailSteps);
    }

    [Fact]
    public void A_bare_integer_clear_becomes_a_TailSteps_entry_passed_to_ComposeRungs()
    {
        PlayerClears? captured = null;
        var live = Live(composeRungs: (_, clears) => { captured = clears; return PassingOffer(); });
        var catalog = CatalogWith(Domain("domain.a"));

        DomainOffers.For(new[] { Progress("domain.a", new DomainClearFact("1", false, 1)) }, catalog, _ => (null, null), live);

        Assert.Contains(1, captured!.TailSteps);
        Assert.Empty(captured.RungIds);
    }

    [Fact]
    public void Cleared_lists_only_known_rung_ids_ordinal_sorted_never_tail_step_numbers()
    {
        var catalog = CatalogWith(Domain("domain.a"));
        var offers = DomainOffers.For(
            new[] { Progress("domain.a", new DomainClearFact("very-hard", false, 1), new DomainClearFact("medium", false, 2), new DomainClearFact("1", true, 3)) },
            catalog, _ => (null, null), Live());

        Assert.Equal(new[] { "medium", "very-hard" }, Assert.Single(offers).Cleared);
    }

    // ---- ordering and multi-domain rendering ------------------------------------------------------

    [Fact]
    public void Offers_come_back_in_domainId_ordinal_order()
    {
        var catalog = CatalogWith(Domain("domain.z"), Domain("domain.a"), Domain("domain.m"));
        var offers = DomainOffers.For(
            new[] { Progress("domain.z"), Progress("domain.a"), Progress("domain.m") }, catalog, _ => (null, null), Live());

        Assert.Equal(new[] { "domain.a", "domain.m", "domain.z" }, offers.Select(o => o.DomainId));
    }

    [Fact]
    public void The_six_first_ship_domains_all_render()
    {
        // "the six first-ship domains render" (todo's own Verify line) -- one `many` domain per
        // climate at `shallow` (spec §7), proven with a fixture shape since no real seed content
        // exists yet (D4.16's own honestly-deferred import write arm).
        var climates = new[] { "fire", "water", "earth", "air", "light", "dark" };
        var domains = climates.Select((c, i) => Domain($"domain.{c}-shallow-{i:000}")).ToArray();
        var catalog = CatalogWith(domains);
        var progress = domains.Select(d => Progress(d.DomainId)).ToArray();

        var offers = DomainOffers.For(progress, catalog, _ => (null, null), Live());

        Assert.Equal(6, offers.Count);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        var catalog = CatalogWith(Domain("domain.a"));
        Assert.Throws<ArgumentNullException>(() => DomainOffers.For(null!, catalog, _ => (null, null), Live()));
        Assert.Throws<ArgumentNullException>(() => DomainOffers.For(Array.Empty<DomainProgressFact>(), null!, _ => (null, null), Live()));
        Assert.Throws<ArgumentNullException>(() => DomainOffers.For(Array.Empty<DomainProgressFact>(), catalog, null!, Live()));
        Assert.Throws<ArgumentNullException>(() => DomainOffers.For(Array.Empty<DomainProgressFact>(), catalog, _ => (null, null), null!));
    }
}
