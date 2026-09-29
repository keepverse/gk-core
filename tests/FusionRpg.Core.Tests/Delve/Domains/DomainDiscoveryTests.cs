using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Delve.Domains;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>D4.20 (spec-domain-catalog.md §5b) — `DomainDiscovery`: the expedition-tick pick (i) and
/// the climate/band-ordinal reveal rule (ii), both pure and deterministic, neither wired to a real
/// caller yet (the tick kind is an explicit ask on `expeditions`; the reveal rule is inert on the
/// six-domain first-ship corpus, all `shallow`) — named honestly in the todo's own evidence rather
/// than assumed wired.</summary>
public class DomainDiscoveryTests
{
    // ---- Pick (i) -----------------------------------------------------------------------------

    [Fact]
    public void Pick_returns_one_of_the_candidates()
    {
        var picked = DomainDiscovery.Pick(new[] { "domain.a", "domain.b", "domain.c" }, tickSeed: 1);
        Assert.Contains(picked, new[] { "domain.a", "domain.b", "domain.c" });
    }

    [Fact]
    public void Pick_is_deterministic_for_the_same_seed()
    {
        var candidates = new[] { "domain.a", "domain.b", "domain.c" };
        var first = DomainDiscovery.Pick(candidates, tickSeed: 777);
        var second = DomainDiscovery.Pick(candidates, tickSeed: 777);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Pick_does_not_depend_on_the_callers_own_input_order()
    {
        var sorted = new[] { "domain.a", "domain.b", "domain.c" };
        var shuffled = new[] { "domain.c", "domain.a", "domain.b" };

        Assert.Equal(DomainDiscovery.Pick(sorted, tickSeed: 42), DomainDiscovery.Pick(shuffled, tickSeed: 42));
    }

    [Fact]
    public void Pick_can_reach_every_candidate_equal_weight_over_enough_seeds()
    {
        var candidates = new[] { "domain.a", "domain.b", "domain.c" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var seed = 0; seed < 60 && seen.Count < candidates.Length; seed++)
            seen.Add(DomainDiscovery.Pick(candidates, seed));

        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void Pick_throws_the_frameworks_own_exception_on_an_empty_pool()
    {
        Assert.Throws<NoDrawableWeightedOptionException>(() => DomainDiscovery.Pick(Array.Empty<string>(), tickSeed: 1));
    }

    [Fact]
    public void Pick_null_argument_throws()
    {
        Assert.Throws<ArgumentNullException>(() => DomainDiscovery.Pick(null!, tickSeed: 1));
    }

    // ---- RevealedByClear (ii) -------------------------------------------------------------------

    static readonly IReadOnlyDictionary<string, int> BandOrdinals = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["shallow"] = 1, ["mid"] = 2, ["deep"] = 3, ["abyssal"] = 4,
    };

    static DomainRow Domain(string id, string climate, string dangerBand) =>
        new(id, "Test", "Flavor.", "theme.overgrown", climate, dangerBand, "many",
            "layout.standard", "species.warden", null, "Lair", null);

    static DomainCatalog Catalog(params DomainRow[] rows) => DomainCatalog.Load(rows, BandOrdinals).Catalog;

    [Fact]
    public void A_clear_reveals_the_next_band_in_the_same_climate()
    {
        var shallow = Domain("domain.fire-shallow", "fire", "shallow");
        var mid = Domain("domain.fire-mid", "fire", "mid");
        var catalog = Catalog(shallow, mid);

        var revealed = DomainDiscovery.RevealedByClear(shallow, new[] { shallow, mid }, catalog);

        Assert.Equal(new[] { "domain.fire-mid" }, revealed);
    }

    [Fact]
    public void A_clear_never_reveals_a_different_climates_next_band()
    {
        var fireShallow = Domain("domain.fire-shallow", "fire", "shallow");
        var waterMid = Domain("domain.water-mid", "water", "mid");
        var catalog = Catalog(fireShallow, waterMid);

        var revealed = DomainDiscovery.RevealedByClear(fireShallow, new[] { fireShallow, waterMid }, catalog);

        Assert.Empty(revealed);
    }

    [Fact]
    public void A_clear_never_reveals_its_own_band_or_two_bands_up()
    {
        var shallow = Domain("domain.fire-shallow", "fire", "shallow");
        var alsoShallow = Domain("domain.fire-shallow-2", "fire", "shallow");
        var deep = Domain("domain.fire-deep", "fire", "deep"); // two bands up, not one
        var catalog = Catalog(shallow, alsoShallow, deep);

        var revealed = DomainDiscovery.RevealedByClear(shallow, new[] { shallow, alsoShallow, deep }, catalog);

        Assert.Empty(revealed);
    }

    [Fact]
    public void Inert_on_a_shallow_only_corpus_nothing_at_mid_to_reveal()
    {
        // "inert on the first-ship corpus, which has no mid" (spec §5b, verbatim) -- pinned directly.
        var climates = new[] { "fire", "water", "earth", "air", "light", "dark" };
        var domains = climates.Select(c => Domain($"domain.{c}-shallow", c, "shallow")).ToArray();
        var catalog = Catalog(domains);

        var revealed = DomainDiscovery.RevealedByClear(domains[0], domains, catalog);

        Assert.Empty(revealed);
    }

    [Fact]
    public void Multiple_domains_at_the_next_band_come_back_ordinal_sorted()
    {
        var shallow = Domain("domain.fire-shallow", "fire", "shallow");
        var midZ = Domain("domain.fire-mid-z", "fire", "mid");
        var midA = Domain("domain.fire-mid-a", "fire", "mid");
        var catalog = Catalog(shallow, midZ, midA);

        var revealed = DomainDiscovery.RevealedByClear(shallow, new[] { shallow, midZ, midA }, catalog);

        Assert.Equal(new[] { "domain.fire-mid-a", "domain.fire-mid-z" }, revealed);
    }

    [Fact]
    public void RevealedByClear_null_arguments_throw()
    {
        var shallow = Domain("domain.fire-shallow", "fire", "shallow");
        var catalog = Catalog(shallow);
        Assert.Throws<ArgumentNullException>(() => DomainDiscovery.RevealedByClear(null!, new[] { shallow }, catalog));
        Assert.Throws<ArgumentNullException>(() => DomainDiscovery.RevealedByClear(shallow, null!, catalog));
        Assert.Throws<ArgumentNullException>(() => DomainDiscovery.RevealedByClear(shallow, new[] { shallow }, null!));
    }
}
