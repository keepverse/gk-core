using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Uniques;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>
/// D4.28 (spec-unique-pipeline.md §5) — the `boss-unique` group's binding/eligibility, the
/// source-locked-exactly-once check, and `firstClearRef` validation. Uses the REAL shipped
/// `uniques.v1.json` tuning (`UniqueTests.Tuning()`, `rungFloorOrdinal: 80`) rather than a synthetic
/// one, since the eligibility floor is exactly what this task's own acceptance line names.
/// </summary>
public class DungeonLootTableGenTests
{
    static UniqueTuning Tuning() => Tests.Items.UniqueTests.Tuning();

    static DungeonLootTableGen.BossUniqueCandidate Candidate(
        string seedId = "unique.rot-bloom-30-002", int rungOrdinal = 90, bool enabled = true,
        UniqueAcquisition acquisition = UniqueAcquisition.Drop, bool climateMatches = true) =>
        new(seedId, "item." + seedId["unique.".Length..], rungOrdinal, enabled, acquisition, climateMatches);

    // ---- eligibility ----------------------------------------------------------------------------

    [Fact]
    public void A_rung_90_enabled_drop_unique_that_matches_climate_is_eligible()
    {
        Assert.True(DungeonLootTableGen.IsEligible(Candidate(), Tuning()));
    }

    [Fact]
    public void Below_the_shipped_rung_floor_is_ineligible()
    {
        Assert.False(DungeonLootTableGen.IsEligible(Candidate(rungOrdinal: 79), Tuning()));
    }

    [Fact]
    public void At_exactly_the_shipped_rung_floor_is_eligible()
    {
        Assert.Equal(80, Tuning().RungFloorOrdinal); // sanity: this IS today's real shipped floor
        Assert.True(DungeonLootTableGen.IsEligible(Candidate(rungOrdinal: 80), Tuning()));
    }

    [Fact]
    public void Disabled_is_ineligible_even_at_a_high_rung()
    {
        Assert.False(DungeonLootTableGen.IsEligible(Candidate(enabled: false), Tuning()));
    }

    [Fact]
    public void Deterministic_acquisition_is_ineligible_a_deterministic_unique_never_sits_in_a_table()
    {
        Assert.False(DungeonLootTableGen.IsEligible(Candidate(acquisition: UniqueAcquisition.Deterministic), Tuning()));
    }

    [Fact]
    public void Source_locked_acquisition_is_eligible_unlike_deterministic()
    {
        Assert.True(DungeonLootTableGen.IsEligible(Candidate(acquisition: UniqueAcquisition.SourceLocked), Tuning()));
    }

    [Fact]
    public void A_climate_mismatch_is_ineligible_a_filter_never_a_weight()
    {
        Assert.False(DungeonLootTableGen.IsEligible(Candidate(climateMatches: false), Tuning()));
    }

    // ---- BuildBossUniqueGroup ----------------------------------------------------------------------

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DungeonLootTableGen.BuildBossUniqueGroup(null!, Tuning(), 7, 1000, out _));
        Assert.Throws<ArgumentNullException>(() =>
            DungeonLootTableGen.BuildBossUniqueGroup(new[] { Candidate() }, null!, 7, 1000, out _));
    }

    [Fact]
    public void An_empty_candidate_set_refuses_group_starved_rather_than_shipping_a_dead_group()
    {
        var rejection = DungeonLootTableGen.BuildBossUniqueGroup(
            Array.Empty<DungeonLootTableGen.BossUniqueCandidate>(), Tuning(), 7, 1000, out var entries);

        Assert.False(rejection.IsOk);
        Assert.Contains("unique.group-starved", rejection.Detail, StringComparison.Ordinal);
        Assert.Null(entries);
    }

    [Fact]
    public void All_candidates_ineligible_also_refuses_group_starved()
    {
        var rejection = DungeonLootTableGen.BuildBossUniqueGroup(
            new[] { Candidate(rungOrdinal: 10) }, Tuning(), 7, 1000, out var entries);

        Assert.False(rejection.IsOk);
        Assert.Contains("unique.group-starved", rejection.Detail, StringComparison.Ordinal);
        Assert.Null(entries);
    }

    [Fact]
    public void One_eligible_unique_produces_one_unique_row_beside_one_nothing_row_at_the_real_shipped_weights()
    {
        var rejection = DungeonLootTableGen.BuildBossUniqueGroup(
            new[] { Candidate() }, Tuning(), exceptionalWeight: 7, stapleWeight: 1000, out var entries);

        Assert.True(rejection.IsOk, rejection.Detail);
        Assert.Equal(2, entries!.Count);
        var uniqueRow = Assert.Single(entries, e => e.Kind == DropEntryKind.Unique);
        Assert.Equal("item.rot-bloom-30-002", uniqueRow.RefId);
        Assert.Equal(7, uniqueRow.Weight);
        Assert.Equal(AffixChannels.Boss, uniqueRow.AffixChannel);
        var nothingRow = Assert.Single(entries, e => e.Kind == DropEntryKind.Nothing);
        Assert.Equal(1000, nothingRow.Weight);
    }

    [Fact]
    public void An_ineligible_candidate_beside_an_eligible_one_is_excluded_by_id_never_categorically()
    {
        var eligible = Candidate(seedId: "unique.rot-bloom-30-002");
        var ineligible = Candidate(seedId: "unique.thorned-chassis-30-004", climateMatches: false);

        var rejection = DungeonLootTableGen.BuildBossUniqueGroup(
            new[] { eligible, ineligible }, Tuning(), 7, 1000, out var entries);

        Assert.True(rejection.IsOk, rejection.Detail);
        var uniqueRows = entries!.Where(e => e.Kind == DropEntryKind.Unique).ToList();
        Assert.Single(uniqueRows);
        Assert.Equal("item.rot-bloom-30-002", uniqueRows[0].RefId);
    }

    [Fact]
    public void Multiple_eligible_uniques_each_get_their_own_row_bound_by_id()
    {
        var a = Candidate(seedId: "unique.rot-bloom-30-002");
        var b = Candidate(seedId: "unique.verdant-graft-90-005");

        var rejection = DungeonLootTableGen.BuildBossUniqueGroup(new[] { a, b }, Tuning(), 7, 1000, out var entries);

        Assert.True(rejection.IsOk, rejection.Detail);
        var refIds = entries!.Where(e => e.Kind == DropEntryKind.Unique).Select(e => e.RefId).ToList();
        Assert.Equal(new[] { "item.rot-bloom-30-002", "item.verdant-graft-90-005" }, refIds);
    }

    // ---- ValidateSourceLockedOnce — the verify line's own headline ----------------------------------

    [Fact]
    public void Null_references_throw()
    {
        Assert.Throws<ArgumentNullException>(() => DungeonLootTableGen.ValidateSourceLockedOnce(null!));
    }

    [Fact]
    public void A_unique_referenced_by_exactly_one_table_has_no_violation()
    {
        var violations = DungeonLootTableGen.ValidateSourceLockedOnce(new[]
        {
            new DungeonLootTableGen.SourceLockReference("unique.rot-bloom-30-002", "drop.boss-unique.forest"),
        });

        Assert.Empty(violations);
    }

    /// <summary>The verify line's own literal words: "a source-locked unique is unreachable from any
    /// other table" — proven here as the positive claim (a real double-reference IS caught).</summary>
    [Fact]
    public void A_unique_referenced_by_two_tables_is_a_violation_naming_both()
    {
        var violations = DungeonLootTableGen.ValidateSourceLockedOnce(new[]
        {
            new DungeonLootTableGen.SourceLockReference("unique.rot-bloom-30-002", "drop.boss-unique.forest"),
            new DungeonLootTableGen.SourceLockReference("unique.rot-bloom-30-002", "drop.boss-unique.swamp"),
        });

        var v = Assert.Single(violations);
        Assert.Equal("unique.rot-bloom-30-002", v.UniqueId);
        Assert.Equal(new[] { "drop.boss-unique.forest", "drop.boss-unique.swamp" }, v.TableIds);
    }

    [Fact]
    public void The_same_table_referencing_a_unique_twice_is_not_a_violation()
    {
        // Two rows in the SAME table (e.g. two grant slots) is not "more than one table" -- the rule
        // is about tables, not row count.
        var violations = DungeonLootTableGen.ValidateSourceLockedOnce(new[]
        {
            new DungeonLootTableGen.SourceLockReference("unique.rot-bloom-30-002", "drop.boss-unique.forest"),
            new DungeonLootTableGen.SourceLockReference("unique.rot-bloom-30-002", "drop.boss-unique.forest"),
        });

        Assert.Empty(violations);
    }

    [Fact]
    public void Different_uniques_in_different_tables_never_cross_contaminate()
    {
        var violations = DungeonLootTableGen.ValidateSourceLockedOnce(new[]
        {
            new DungeonLootTableGen.SourceLockReference("unique.a", "table.1"),
            new DungeonLootTableGen.SourceLockReference("unique.b", "table.2"),
        });

        Assert.Empty(violations);
    }

    // ---- ValidateFirstClearRef ----------------------------------------------------------------------

    static IReadOnlyDictionary<string, DungeonLootTableGen.KnownUnique> KnownUniques() =>
        new Dictionary<string, DungeonLootTableGen.KnownUnique>(StringComparer.Ordinal)
        {
            ["item.deterministic-boss-relic"] = new(90, UniqueAcquisition.Deterministic),
            ["item.drop-only-unique"] = new(90, UniqueAcquisition.Drop),
            ["item.low-rung-deterministic"] = new(10, UniqueAcquisition.Deterministic),
        };

    [Fact]
    public void Null_knownUniques_or_tuning_throw()
    {
        Assert.Throws<ArgumentNullException>(() => DungeonLootTableGen.ValidateFirstClearRef("x", null!, Tuning()));
        Assert.Throws<ArgumentNullException>(() => DungeonLootTableGen.ValidateFirstClearRef("x", KnownUniques(), null!));
    }

    [Fact]
    public void Null_first_clear_ref_is_legal_none()
    {
        Assert.True(DungeonLootTableGen.ValidateFirstClearRef(null, KnownUniques(), Tuning()).IsOk);
    }

    [Fact]
    public void A_rung_90_deterministic_unique_is_a_legal_first_clear_ref()
    {
        var r = DungeonLootTableGen.ValidateFirstClearRef("item.deterministic-boss-relic", KnownUniques(), Tuning());
        Assert.True(r.IsOk, r.Detail);
    }

    [Fact]
    public void An_unknown_container_id_refuses_naming_it()
    {
        var r = DungeonLootTableGen.ValidateFirstClearRef("item.does-not-exist", KnownUniques(), Tuning());
        Assert.False(r.IsOk);
        Assert.Contains("dungeon.first-clear-ref-unknown", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_deterministic_unique_refuses_even_if_high_rung()
    {
        var r = DungeonLootTableGen.ValidateFirstClearRef("item.drop-only-unique", KnownUniques(), Tuning());
        Assert.False(r.IsOk);
        Assert.Contains("dungeon.first-clear-ref-not-deterministic", r.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_deterministic_unique_below_the_rung_floor_refuses()
    {
        var r = DungeonLootTableGen.ValidateFirstClearRef("item.low-rung-deterministic", KnownUniques(), Tuning());
        Assert.False(r.IsOk);
        Assert.Contains("dungeon.first-clear-ref-below-rung-floor", r.Detail, StringComparison.Ordinal);
    }

    // ---- DomainAnchor.FirstClearRef stays source-compatible -----------------------------------------

    [Fact]
    public void DomainAnchor_still_constructs_with_the_original_four_positional_args()
    {
        // Every existing call site (UnknownPityTests.cs) uses exactly this 4-arg form -- confirms the
        // new field's default keeps them compiling and behaving unchanged (FirstClearRef defaults null).
        var anchor = new FusionRpg.Core.Delve.Roll.DomainAnchor(
            "domain.forest", FusionRpg.Core.Stats.Derived.ElementTypeId.Fire, "shallow",
            Array.Empty<FusionRpg.Core.Delve.Roll.RoomPaletteEntry>());
        Assert.Null(anchor.FirstClearRef);
    }
}
