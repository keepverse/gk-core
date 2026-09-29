using FusionRpg.Core.Delve.Domains;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>D4.17 (spec-domain-catalog.md §2) — `DomainPreflight.Run`: one red test per row (the
/// todo's own literal Verify line), and "a failing preflight leaves the database untouched" proven
/// structurally (this file references no store type at all).</summary>
public class DomainPreflightTests
{
    static DomainRow Domain(string id = "domain.test-001", string dangerBand = "shallow", string layoutId = "layout.standard",
        string bossRef = "species.warden") =>
        new(id, "Test Domain", "A test flavor.", "theme.overgrown", "fire", dangerBand, "many",
            layoutId, bossRef, null, "Lair", null);

    static readonly IReadOnlyDictionary<string, int> DangerBandOrdinals = new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 };

    // A fully-passing fixture -- every test below overrides exactly ONE delegate/collection to force
    // exactly one row to refuse, proving each row independently.
    static DomainPreflightInputs Passing() => new(
        DangerBandOrdinals: DangerBandOrdinals,
        KnownLayoutIds: new[] { "layout.standard" },
        KnownSpeciesIds: new[] { "species.warden" },
        ThreatBandOrdinalFor: _ => 5,
        BossFloorRungOrdinal: 3,
        CellsLayoutCanPlace: _ => new[] { ("fight", "fire") },
        CellsPaletteFills: _ => new[] { ("fight", "fire") },
        CheckGraphs: _ => Array.Empty<DomainRefusal>(),
        CheckObjects: _ => Array.Empty<DomainRefusal>(),
        CheckEncounters: _ => Array.Empty<DomainRefusal>(),
        CheckEvents: _ => Array.Empty<DomainRefusal>(),
        CheckQuests: _ => Array.Empty<DomainRefusal>(),
        KnownDropTableIds: new[] { "table.forest-cache" },
        BoundLootKinds: new[] { "cache" },
        LootBindingFor: _ => new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = "table.forest-cache" },
        OfferedRungCountFor: _ => 3);

    [Fact]
    public void A_fully_passing_domain_reports_zero_refusals()
    {
        Assert.Empty(DomainPreflight.Run(new[] { Domain() }, Passing()));
    }

    // ---- one red test per row ----

    [Fact]
    public void Row1_schema_a_bad_dangerBand_refuses_domain_schema()
    {
        var refusals = DomainPreflight.Run(new[] { Domain(dangerBand: "not-a-real-band") }, Passing());
        Assert.Single(refusals);
        Assert.Equal("domain.schema", refusals[0].Rule);
    }

    [Fact]
    public void Row2a_an_unknown_layout_refuses_domain_ref_missing()
    {
        var refusals = DomainPreflight.Run(new[] { Domain(layoutId: "layout.not-known") }, Passing());
        Assert.Single(refusals);
        Assert.Equal("domain.ref-missing", refusals[0].Rule);
        Assert.Contains("layoutTemplateId", refusals[0].Detail);
    }

    [Fact]
    public void Row2b_an_unknown_boss_species_refuses_domain_ref_missing()
    {
        var refusals = DomainPreflight.Run(new[] { Domain(bossRef: "species.not-known") }, Passing());
        Assert.Single(refusals);
        Assert.Equal("domain.ref-missing", refusals[0].Rule);
        Assert.Contains("bossSpeciesRef", refusals[0].Detail);
    }

    [Fact]
    public void Row2c_a_boss_below_the_threat_floor_refuses_domain_boss_below_floor()
    {
        var inputs = Passing() with { ThreatBandOrdinalFor = _ => 1 }; // below BossFloorRungOrdinal (3)
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("domain.boss-below-floor", refusals[0].Rule);
    }

    [Fact]
    public void Row2c_a_boss_exactly_at_the_threat_floor_is_legal_not_below_it()
    {
        // Boundary case for "< BossFloorRungOrdinal": equal to the floor must NOT refuse. Without this
        // exact-boundary test, a mutated "<=" is indistinguishable from the real "<" -- every other
        // fixture value here sits strictly below or strictly above the floor, never on it.
        var inputs = Passing() with { ThreatBandOrdinalFor = _ => 3 }; // == BossFloorRungOrdinal (3)
        Assert.Empty(DomainPreflight.Run(new[] { Domain() }, inputs));
    }

    [Fact]
    public void Row3_an_empty_palette_cell_refuses_domain_palette_cell_empty()
    {
        var inputs = Passing() with { CellsPaletteFills = _ => Array.Empty<(string, string)>() }; // nothing fills the demanded cell
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("domain.palette-cell-empty", refusals[0].Rule);
    }

    [Fact]
    public void Row4_a_graph_refusal_propagates_through_unchanged()
    {
        var inputs = Passing() with { CheckGraphs = _ => new[] { new DomainRefusal("domain.test-001", "domain.graph:dead-end", "detail") } };
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("domain.graph:dead-end", refusals[0].Rule);
    }

    [Fact]
    public void Row5_an_object_refusal_propagates_through_unchanged()
    {
        var inputs = Passing() with { CheckObjects = _ => new[] { new DomainRefusal("domain.test-001", "domain.object:gated-door-unreachable", "detail") } };
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("domain.object:gated-door-unreachable", refusals[0].Rule);
    }

    [Fact]
    public void Row6_an_encounter_refusal_propagates_through_unchanged()
    {
        var inputs = Passing() with { CheckEncounters = _ => new[] { new DomainRefusal("domain.test-001", "domain.encounter:slot-3", "detail") } };
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("domain.encounter:slot-3", refusals[0].Rule);
    }

    [Fact]
    public void Row7_an_event_refusal_propagates_through_unchanged()
    {
        var inputs = Passing() with { CheckEvents = _ => new[] { new DomainRefusal("domain.test-001", "domain.event:bad-outcome-mix", "detail") } };
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("domain.event:bad-outcome-mix", refusals[0].Rule);
    }

    [Fact]
    public void Row8_a_quest_refusal_propagates_through_unchanged()
    {
        var inputs = Passing() with { CheckQuests = _ => new[] { new DomainRefusal("domain.test-001", "quest.too-few-non-sink-anchors", "detail") } };
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("quest.too-few-non-sink-anchors", refusals[0].Rule);
    }

    [Fact]
    public void Row9_a_missing_loot_table_refuses_domain_loot_table_missing()
    {
        var inputs = Passing() with { LootBindingFor = _ => new Dictionary<string, string>(StringComparer.Ordinal) };
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("domain.loot-table-missing", refusals[0].Rule);
        Assert.Equal("cache", refusals[0].Detail);
    }

    [Fact]
    public void Row9b_a_loot_binding_naming_an_unknown_table_id_also_refuses_domain_loot_table_missing()
    {
        // The binding HAS the kind, but the id it names is not in KnownDropTableIds -- distinct from
        // Row9's missing-key case, and the only fixture that exercises the Contains half of the OR.
        var inputs = Passing() with { LootBindingFor = _ => new Dictionary<string, string>(StringComparer.Ordinal) { ["cache"] = "table.unknown-table" } };
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("domain.loot-table-missing", refusals[0].Rule);
        Assert.Equal("cache", refusals[0].Detail);
    }

    [Fact]
    public void Row10_zero_offered_rungs_refuses_domain_no_rung_offered()
    {
        var inputs = Passing() with { OfferedRungCountFor = _ => 0 };
        var refusals = DomainPreflight.Run(new[] { Domain() }, inputs);
        Assert.Single(refusals);
        Assert.Equal("domain.no-rung-offered", refusals[0].Rule);
    }

    // ---- every domain is checked, not just the first that fails ----

    [Fact]
    public void Every_domain_is_checked_a_bad_first_domain_does_not_hide_a_bad_second_ones_row()
    {
        var bad1 = Domain("domain.a", dangerBand: "nope");
        var bad2 = Domain("domain.b", layoutId: "layout.unknown");
        var refusals = DomainPreflight.Run(new[] { bad1, bad2 }, Passing());
        Assert.Equal(2, refusals.Count);
        Assert.Contains(refusals, r => r.DomainId == "domain.a" && r.Rule == "domain.schema");
        Assert.Contains(refusals, r => r.DomainId == "domain.b" && r.Rule == "domain.ref-missing");
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => DomainPreflight.Run(null!, Passing()));
        Assert.Throws<ArgumentNullException>(() => DomainPreflight.Run(new[] { Domain() }, null!));
    }
}
