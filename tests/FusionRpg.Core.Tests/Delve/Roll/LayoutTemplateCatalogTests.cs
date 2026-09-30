using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Delve.Roll;

/// <summary>
/// D4.30's real prerequisite chain (2026-09-07) — `LayoutTemplateCatalog`/`LayoutSeedFile`, closing
/// D4.19/D4.21's own named "no `LayoutTemplateCatalog` exists" gap now that real content exists
/// (`gk-data/packs/fusion/data/seed/dungeon/layouts/*.json`, six entries, `layouts.py`).
/// </summary>
public class LayoutTemplateCatalogTests
{
    static readonly IReadOnlyDictionary<string, BandDef> Bands = new Dictionary<string, BandDef>(StringComparer.Ordinal)
    {
        ["depthBand"] = new BandDef { BandName = "depthBand", Members = new[] { "short", "medium", "long" } },
        ["widthBand"] = new BandDef { BandName = "widthBand", Members = new[] { "narrow", "mid", "wide" } },
        ["branchiness"] = new BandDef { BandName = "branchiness", Members = new[] { "linear", "forked", "webbed" } },
        ["density"] = new BandDef { BandName = "density", Members = new[] { "none", "sparse", "dense" } },
    };

    static readonly IReadOnlyList<string> RaidModes = new[] { "solo", "pair", "quad" };

    static LayoutTemplate Row(string id, string size = "short", string width = "narrow", string branch = "linear",
        string gate = "none", string secret = "none", string oneWay = "none", string[]? raidModes = null) =>
        new(id, size, width, branch, gate, secret, oneWay, raidModes ?? new[] { "solo" });

    // ---- Load: the happy path ----

    [Fact]
    public void A_well_formed_row_loads_with_zero_rejections()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a") }, Bands, RaidModes);

        Assert.Empty(result.Rejections);
        Assert.Equal(1, result.Catalog.Count);
        Assert.NotNull(result.Catalog.Resolve("layout.a"));
    }

    [Fact]
    public void The_real_six_shipped_layouts_round_trip_with_zero_rejections()
    {
        var rows = LayoutSeedFile.LoadAll(DungeonTestFiles.LayoutsDir());
        var result = LayoutTemplateCatalog.Load(rows, Bands, RaidModes);

        Assert.Empty(result.Rejections);
        Assert.Equal(6, result.Catalog.Count);
    }

    // ---- Load: every rejection rule, one red test each ----

    [Fact]
    public void A_duplicate_id_refuses_by_name()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a"), Row("layout.a") }, Bands, RaidModes);

        Assert.Single(result.Rejections);
        Assert.Contains(LayoutRules.DuplicateId, result.Rejections[0].ToString());
        // The FIRST occurrence is legitimately valid and stays -- only the second, duplicate
        // attempt is refused (DomainCatalog.Load's own established per-row-duplicate-check shape).
        Assert.Equal(1, result.Catalog.Count);
    }

    [Theory]
    [InlineData("sizeBand", "not-a-size")]
    public void A_bad_sizeBand_refuses_by_name(string _, string badValue)
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a", size: badValue) }, Bands, RaidModes);
        Assert.Single(result.Rejections);
        Assert.Contains(LayoutRules.BadBandMember, result.Rejections[0].ToString());
    }

    [Fact]
    public void A_bad_widthBand_refuses_by_name()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a", width: "not-a-width") }, Bands, RaidModes);
        Assert.Single(result.Rejections);
        Assert.Contains(LayoutRules.BadBandMember, result.Rejections[0].ToString());
    }

    [Fact]
    public void A_bad_branchiness_refuses_by_name()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a", branch: "not-a-shape") }, Bands, RaidModes);
        Assert.Single(result.Rejections);
        Assert.Contains(LayoutRules.BadBandMember, result.Rejections[0].ToString());
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("secret")]
    [InlineData("oneWay")]
    public void A_bad_density_field_refuses_by_name(string which)
    {
        var row = which switch
        {
            "gate" => Row("layout.a", gate: "not-a-density"),
            "secret" => Row("layout.a", secret: "not-a-density"),
            _ => Row("layout.a", oneWay: "not-a-density"),
        };
        var result = LayoutTemplateCatalog.Load(new[] { row }, Bands, RaidModes);
        Assert.Single(result.Rejections);
        Assert.Contains(LayoutRules.BadBandMember, result.Rejections[0].ToString());
    }

    [Fact]
    public void An_empty_raidModes_refuses_never_empty_per_spec()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a", raidModes: Array.Empty<string>()) }, Bands, RaidModes);
        Assert.Single(result.Rejections);
        Assert.Contains(LayoutRules.EmptyRaidModes, result.Rejections[0].ToString());
    }

    [Fact]
    public void A_null_raidModes_refuses_the_same_way_as_empty()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a") with { RaidModes = null } }, Bands, RaidModes);
        Assert.Single(result.Rejections);
        Assert.Contains(LayoutRules.EmptyRaidModes, result.Rejections[0].ToString());
    }

    [Fact]
    public void An_unknown_raidMode_refuses_by_name()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a", raidModes: new[] { "duo" }) }, Bands, RaidModes);
        Assert.Single(result.Rejections);
        Assert.Contains(LayoutRules.BadRaidMode, result.Rejections[0].ToString());
    }

    [Fact]
    public void A_duplicate_raidMode_refuses_by_name()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a", raidModes: new[] { "solo", "solo" }) }, Bands, RaidModes);
        Assert.Single(result.Rejections);
        Assert.Contains(LayoutRules.DuplicateRaidMode, result.Rejections[0].ToString());
    }

    [Fact]
    public void One_bad_row_never_blocks_a_healthy_sibling()
    {
        var result = LayoutTemplateCatalog.Load(
            new[] { Row("layout.bad", size: "not-a-size"), Row("layout.good") }, Bands, RaidModes);

        Assert.Single(result.Rejections);
        Assert.Equal(1, result.Catalog.Count);
        Assert.NotNull(result.Catalog.Resolve("layout.good"));
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => LayoutTemplateCatalog.Load(null!, Bands, RaidModes));
        Assert.Throws<ArgumentNullException>(() => LayoutTemplateCatalog.Load(Array.Empty<LayoutTemplate>(), null!, RaidModes));
        Assert.Throws<ArgumentNullException>(() => LayoutTemplateCatalog.Load(Array.Empty<LayoutTemplate>(), Bands, null!));
    }

    // ---- RaidModesFor: the verify line's own headline ----

    [Fact]
    public void RaidModesFor_returns_the_real_row_own_raidModes()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a", raidModes: new[] { "pair", "quad" }) }, Bands, RaidModes);
        Assert.Equal(new[] { "pair", "quad" }, result.Catalog.RaidModesFor("layout.a"));
    }

    [Fact]
    public void RaidModesFor_an_unknown_id_returns_empty_never_throws()
    {
        var result = LayoutTemplateCatalog.Load(new[] { Row("layout.a") }, Bands, RaidModes);
        Assert.Empty(result.Catalog.RaidModesFor("layout.does-not-exist"));
    }
}

public class LayoutSeedFileTests
{
    [Fact]
    public void LoadAll_reads_the_real_six_shipped_layouts()
    {
        var rows = LayoutSeedFile.LoadAll(DungeonTestFiles.LayoutsDir());

        Assert.Equal(6, rows.Count);
        Assert.All(rows, r => Assert.False(string.IsNullOrEmpty(r.LayoutId)));
        Assert.All(rows, r => Assert.NotNull(r.RaidModes));
        Assert.All(rows, r => Assert.NotEmpty(r.RaidModes!));
    }

    [Fact]
    public void LoadAll_never_reads_the_index_file_as_an_anchor()
    {
        var rows = LayoutSeedFile.LoadAll(DungeonTestFiles.LayoutsDir());
        Assert.DoesNotContain(rows, r => r.LayoutId == "_index");
    }

    [Fact]
    public void LoadAll_on_a_missing_directory_returns_empty_not_throws()
    {
        var rows = LayoutSeedFile.LoadAll(Path.Combine(KeepverseRoots.Content(), "data", "seed", "dungeon", "does-not-exist"));
        Assert.Empty(rows);
    }

    [Fact]
    public void LoadAll_null_argument_throws()
    {
        Assert.Throws<ArgumentNullException>(() => LayoutSeedFile.LoadAll(null!));
    }

    [Fact]
    public void A_reloaded_row_round_trips_every_field_exactly()
    {
        var rows = LayoutSeedFile.LoadAll(DungeonTestFiles.LayoutsDir());
        var row = rows.Single(r => r.LayoutId == "layout.long-wide-webbed-001");

        Assert.Equal("long", row.SizeBand);
        Assert.Equal("wide", row.WidthBand);
        Assert.Equal("webbed", row.Branchiness);
        Assert.Equal("dense", row.GateDensity);
        Assert.Equal("dense", row.SecretDensity);
        Assert.Equal("dense", row.OneWayDensity);
        Assert.Equal(new[] { "quad" }, row.RaidModes);
    }
}
