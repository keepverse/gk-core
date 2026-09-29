using FusionRpg.Core.Delve.Domains;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>D4.15 (spec-domain-catalog.md §1) — `DomainCatalog.Load`: a load golden (the six
/// first-ship domains, one per climate, per §7) and an unknown ordinal refuses by name (the todo's
/// own two Verify lines).</summary>
public class DomainCatalogTests
{
    // spec §7's own real dangerBand->rung-ordinal mapping citation: "shallow resolves to band 2"
    // (spec-delve-graph-roll.md's own tuning) -- a plain fixture here since that module's real
    // tuning object is a different module's own concern this task takes as a caller-supplied lookup.
    static readonly IReadOnlyDictionary<string, int> DangerBandOrdinals = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["shallow"] = 2,
        ["mid"] = 4,
        ["deep"] = 6,
        ["abyssal"] = 8,
    };

    static DomainRow Domain(string id, string climate, string dangerBand = "shallow", string entry = "many") =>
        new(id, $"Name for {id}", $"Flavor for {id}", "theme.overgrown", climate, dangerBand, entry,
            "layout.standard-loop", "species.warden", null, "Lair", null);

    // spec §7, verbatim: "one many domain per climate -- the six ElementTypeIds -- at dangerBand: shallow"
    static readonly IReadOnlyList<DomainRow> SixFirstShipDomains = new[]
    {
        Domain("domain.fire-shallow-001", "fire"),
        Domain("domain.ice-shallow-001", "ice"),
        Domain("domain.air-shallow-001", "air"),
        Domain("domain.earth-shallow-001", "earth"),
        Domain("domain.light-shallow-001", "light"),
        Domain("domain.dark-shallow-001", "dark"),
    };

    // ---- the load golden ----

    [Fact]
    public void The_six_first_ship_domains_round_trip_with_zero_rejections()
    {
        var result = DomainCatalog.Load(SixFirstShipDomains, DangerBandOrdinals);

        Assert.Empty(result.Rejections);
        Assert.Equal(6, result.Catalog.Count);
        foreach (var domain in SixFirstShipDomains)
        {
            Assert.Equal(domain, result.Catalog.Resolve(domain.DomainId));
            Assert.Equal(2, result.Catalog.DangerBandOrdinalFor(domain.DomainId)); // shallow -> 2, resolved at load
        }
    }

    [Fact]
    public void Resolve_of_an_unknown_id_returns_null_never_throws()
    {
        var result = DomainCatalog.Load(SixFirstShipDomains, DangerBandOrdinals);
        Assert.Null(result.Catalog.Resolve("domain.does-not-exist"));
    }

    [Theory]
    [InlineData("shallow", 2)]
    [InlineData("mid", 4)]
    [InlineData("deep", 6)]
    [InlineData("abyssal", 8)]
    public void DangerBandOrdinalFor_genuinely_reads_the_callers_own_mapping_not_a_hardcoded_one(string band, int expectedOrdinal)
    {
        var domain = Domain("domain.test-001", "fire", dangerBand: band);
        var result = DomainCatalog.Load(new[] { domain }, DangerBandOrdinals);
        Assert.Empty(result.Rejections);
        Assert.Equal(expectedOrdinal, result.Catalog.DangerBandOrdinalFor(domain.DomainId));
    }

    // ---- an unknown ordinal refuses by name (the literal Verify line) ----

    [Fact]
    public void An_unknown_dangerBand_refuses_by_name()
    {
        var domain = Domain("domain.test-001", "fire", dangerBand: "not-a-real-band");
        var result = DomainCatalog.Load(new[] { domain }, DangerBandOrdinals);

        Assert.Single(result.Rejections);
        Assert.Contains(DomainRules.BadDangerBand, result.Rejections[0].Detail);
        Assert.Equal(0, result.Catalog.Count);
    }

    // ---- other red fixtures ----

    [Fact]
    public void A_duplicate_domain_id_refuses_by_name()
    {
        var domain = Domain("domain.test-001", "fire");
        var result = DomainCatalog.Load(new[] { domain, domain with { } }, DangerBandOrdinals);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(DomainRules.DuplicateId));
        Assert.Equal(1, result.Catalog.Count);
    }

    [Fact]
    public void An_entry_value_that_is_not_once_or_many_refuses_by_name()
    {
        var domain = Domain("domain.test-001", "fire", entry: "sometimes");
        var result = DomainCatalog.Load(new[] { domain }, DangerBandOrdinals);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(DomainRules.BadEntry));
        Assert.Equal(0, result.Catalog.Count);
    }

    [Fact]
    public void An_unknown_climate_refuses_by_name()
    {
        var domain = Domain("domain.test-001", "not-a-real-climate");
        var result = DomainCatalog.Load(new[] { domain }, DangerBandOrdinals);
        Assert.Contains(result.Rejections, r => r.Detail.Contains(DomainRules.BadClimate));
        Assert.Equal(0, result.Catalog.Count);
    }

    [Fact]
    public void A_once_entry_domain_is_legal()
    {
        var domain = Domain("domain.test-001", "fire", entry: "once");
        var result = DomainCatalog.Load(new[] { domain }, DangerBandOrdinals);
        Assert.Empty(result.Rejections);
    }

    [Fact]
    public void Mixed_good_and_bad_rows_in_one_call_report_one_rejection_per_bad_row_and_keep_every_good_row()
    {
        var good1 = Domain("domain.a", "fire");
        var good2 = Domain("domain.b", "ice");
        var bad1 = Domain("domain.c", "fire", dangerBand: "nope");
        var bad2 = Domain("domain.d", "not-a-climate");
        var result = DomainCatalog.Load(new[] { good1, good2, bad1, bad2 }, DangerBandOrdinals);

        Assert.Equal(2, result.Rejections.Count);
        Assert.Equal(2, result.Catalog.Count);
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => DomainCatalog.Load(null!, DangerBandOrdinals));
        Assert.Throws<ArgumentNullException>(() => DomainCatalog.Load(SixFirstShipDomains, null!));
    }
}
