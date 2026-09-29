using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E9's price tables and the <c>power_json</c> backfill (spec-power-vector.md).
///
/// <para>The load-bearing rule here is the one about the sweep: it writes proposals and never touches
/// what ships. A sweep that could edit the authored table would make "hand-authored now, fitted
/// later" a slogan, and — because coefficients are hashed — running it would move every replay
/// verdict downstream for a number nobody adopted.</para>
/// </summary>
public class PowerStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public PowerStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static AtomRow Vitality(int amount = 45, int tier = 1) => new()
    {
        AtomId = AtomRow.DeriveId("atom.vitality", "", tier),
        KindId = "stat.modify",
        FamilyId = "atom.vitality",
        Tier = tier,
        Name = "Vitality",
        ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{amount}}}""",
    };

    // ---- the tables -----------------------------------------------------------------------------

    [Fact]
    public void An_empty_database_reads_the_authored_defaults()
    {
        var tables = _store.GetPowerTables();

        Assert.NotEmpty(tables.Coefficients);
        Assert.Equal(60, tables.FrequencyOf(AtomTriggers.OnDamageDealt));
    }

    [Fact]
    public void The_authored_tables_round_trip()
    {
        Assert.True(_store.UpsertPowerTables(PowerTables.Authored()).Ok);

        var read = _store.GetPowerTables();

        Assert.Equal(
            PowerTables.Authored().Coefficients.OrderBy(c => c.KindId + c.Channel, StringComparer.Ordinal),
            read.Coefficients.OrderBy(c => c.KindId + c.Channel, StringComparer.Ordinal));
        Assert.Equal(60, read.FrequencyOf(AtomTriggers.OnDamageDealt));
    }

    [Fact]
    public void A_zero_reference_scale_is_refused()
    {
        // Normalisation divides by it. A zero scale prices every magnitude alike — which is the units
        // trap the column exists to close, arriving through the table meant to prevent it.
        var broken = new PowerTables(
            new[] { new PowerCoefficientRow("stat.modify", "hp", 1000, 0) },
            PowerTables.Authored().Frequencies);

        var verdict = _store.UpsertPowerTables(broken);

        Assert.False(verdict.Ok);
        Assert.Contains("units trap", verdict.Reason, StringComparison.Ordinal);
    }

    // ---- the sweep ------------------------------------------------------------------------------

    [Fact]
    public void A_proposal_does_not_change_what_ships()
    {
        _store.UpsertPowerTables(PowerTables.Authored());
        var shipped = _store.GetPowerTables().Find("stat.modify", "maxHp")!;

        _store.UpsertCoefficientProposals(new[]
        {
            (new PowerCoefficientRow("stat.modify", "maxHp", 4242, 99), "sweep run 3"),
        });

        Assert.Equal(shipped, _store.GetPowerTables().Find("stat.modify", "maxHp"));
    }

    [Fact]
    public void A_proposal_does_not_move_the_content_hash()
    {
        // The proposal table is deliberately outside the covered set. If a sweep moved the stamp,
        // running it would make every replay verdict report a mismatch for a number nobody adopted.
        _store.UpsertPowerTables(PowerTables.Authored());
        var before = _store.ComputeContentHash().Hash;

        _store.UpsertCoefficientProposals(new[]
        {
            (new PowerCoefficientRow("stat.modify", "maxHp", 4242, 99), "sweep run 3"),
        });

        Assert.Equal(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void Adopting_a_proposal_does_move_the_content_hash()
    {
        // The other half — the coefficients themselves are covered, so a number that actually ships
        // is attributable.
        _store.UpsertPowerTables(PowerTables.Authored());
        var before = _store.ComputeContentHash().Hash;

        var adopted = PowerTables.Authored().Coefficients
            .Select(c => c.KindId == "stat.modify" && c.Channel == "maxHp" ? c with { CoeffMilli = 1200 } : c)
            .ToList();
        _store.UpsertPowerTables(new PowerTables(adopted, PowerTables.Authored().Frequencies));

        Assert.NotEqual(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void A_trigger_frequency_edit_moves_the_content_hash()
    {
        // Why it is a table rather than a constant: as a constant it would move every golden with no
        // content-hash change, which is the one outcome E8 exists to prevent.
        _store.UpsertPowerTables(PowerTables.Authored());
        var before = _store.ComputeContentHash().Hash;

        var faster = PowerTables.Authored().Frequencies
            .Select(f => f.Trigger == AtomTriggers.OnDamageDealt ? f with { PerMinute = 120 } : f)
            .ToList();
        _store.UpsertPowerTables(new PowerTables(PowerTables.Authored().Coefficients, faster));

        Assert.NotEqual(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void The_proposals_come_back_with_their_notes()
    {
        _store.UpsertCoefficientProposals(new[]
        {
            (new PowerCoefficientRow("stat.modify", "atk", 900, 2), "fitted from the 2026-08 sweep"),
        });

        var (proposed, note) = Assert.Single(_store.ListCoefficientProposals());

        Assert.Equal(900, proposed.CoeffMilli);
        Assert.Equal("fitted from the 2026-08 sweep", note);
    }

    // ---- the backfill ---------------------------------------------------------------------------

    [Fact]
    public void The_backfill_prices_every_atom_it_can()
    {
        _store.UpsertAtom(Vitality());
        _store.UpsertAtom(Vitality(90, 2));

        var (priced, unpriced) = _store.BackfillAtomPower();

        Assert.Equal(2, priced);
        Assert.Empty(unpriced);
        Assert.True(PowerVector.FromJson(_store.GetAtom("atom.vitality.t1")!.PowerJson).Survivability > 0);
    }

    [Fact]
    public void A_bigger_atom_is_priced_higher()
    {
        _store.UpsertAtom(Vitality(45));
        _store.UpsertAtom(Vitality(90, 2));
        _store.BackfillAtomPower();

        var small = PowerVector.FromJson(_store.GetAtom("atom.vitality.t1")!.PowerJson);
        var big = PowerVector.FromJson(_store.GetAtom("atom.vitality.t2")!.PowerJson);

        Assert.True(big.Survivability > small.Survivability);
    }

    [Fact]
    public void An_atom_with_no_coefficient_is_left_unpriced_rather_than_written_as_zero()
    {
        // A budget would happily accept a whole family that costs nothing.
        _store.UpsertAtom(Vitality());
        var noCoefficients = new PowerTables(
            Array.Empty<PowerCoefficientRow>(), PowerTables.Authored().Frequencies);

        var (priced, unpriced) = _store.BackfillAtomPower(noCoefficients);

        Assert.Equal(0, priced);
        Assert.Equal(new[] { "atom.vitality.t1" }, unpriced);
        Assert.Null(_store.GetAtom("atom.vitality.t1")!.PowerJson);
    }

    [Fact]
    public void Running_the_backfill_twice_changes_nothing_the_second_time()
    {
        // power_json is a hashed column: an unconditional rewrite would move the content hash every
        // time the backfill ran, which is the same defect the rarity and roster writes had.
        _store.UpsertAtom(Vitality());
        _store.BackfillAtomPower();
        var hash = _store.ComputeContentHash().Hash;
        var revision = _store.GetAtom("atom.vitality.t1")!.Revision;

        _store.BackfillAtomPower();

        Assert.Equal(hash, _store.ComputeContentHash().Hash);
        Assert.Equal(revision, _store.GetAtom("atom.vitality.t1")!.Revision);
    }

    [Fact]
    public void The_backfill_moves_the_content_hash_the_first_time()
    {
        // Prices are content. The move must happen, and be attributable.
        _store.UpsertAtom(Vitality());
        var before = _store.ComputeContentHash().Hash;

        _store.BackfillAtomPower();

        Assert.NotEqual(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void UpsertPowerTables_bumps_the_catalog_revision()
    {
        // C4 (completeness-audit.md): this direct API had no production caller and no revision bump
        // — an E19 receiver would never re-negotiate after a policy edit made through it.
        var before = _store.GetCatalogRevision();

        Assert.True(_store.UpsertPowerTables(PowerTables.Authored()).Ok);

        Assert.True(_store.GetCatalogRevision() > before);
    }

    // ---- P0.3: power_predicate_frequency ----------------------------------------------------------

    [Fact]
    public void An_empty_database_has_no_predicate_frequency_rows()
    {
        Assert.Empty(_store.GetPowerTables().PredicateFrequencies);
    }

    [Fact]
    public void Predicate_frequency_rows_round_trip()
    {
        var withRows = new PowerTables(
            PowerTables.Authored().Coefficients, PowerTables.Authored().Frequencies,
            new[]
            {
                new PredicateFrequencyRow("HasStatus", "burn", 900, 800, 700, 600),
                new PredicateFrequencyRow("HpBelowMilli", "300", 500, 500, 500, 500),
            });

        Assert.True(_store.UpsertPowerTables(withRows).Ok);
        var read = _store.GetPowerTables();

        Assert.Equal(
            withRows.PredicateFrequencies.OrderBy(p => p.LeafId + p.ArgKey, StringComparer.Ordinal),
            read.PredicateFrequencies.OrderBy(p => p.LeafId + p.ArgKey, StringComparer.Ordinal));
        // The floored chain reads back correctly too, not just the raw row:
        // 900x800/1000=720; 720x700/1000=504; 504x600/1000=302.4 -> 302.
        Assert.Equal(302L, read.PredicateFrequencyOf(LeafId.HasStatus, "burn", floorMilli: 0));
    }

    [Fact]
    public void Rewriting_predicate_frequency_rows_replaces_rather_than_accumulates()
    {
        _store.UpsertPowerTables(new PowerTables(
            PowerTables.Authored().Coefficients, PowerTables.Authored().Frequencies,
            new[] { new PredicateFrequencyRow("HasStatus", "burn", 900, 900, 900, 900) }));

        _store.UpsertPowerTables(new PowerTables(
            PowerTables.Authored().Coefficients, PowerTables.Authored().Frequencies,
            new[] { new PredicateFrequencyRow("HasStatus", "freeze", 500, 500, 500, 500) }));

        var read = _store.GetPowerTables();
        Assert.Single(read.PredicateFrequencies);
        Assert.Equal("freeze", read.PredicateFrequencies[0].ArgKey);
    }

    [Theory]
    [InlineData(-1, 1000, 1000, 1000)]
    [InlineData(1000, 1001, 1000, 1000)]
    public void An_out_of_range_predicate_factor_is_refused(
        int reachability, int susceptibility, int coincidence, int uptime)
    {
        // Every factor is a per-mille probability (PS-8 bounded ratio) — [0, 1000], not open-ended.
        var broken = new PowerTables(
            PowerTables.Authored().Coefficients, PowerTables.Authored().Frequencies,
            new[] { new PredicateFrequencyRow("HasStatus", "burn", reachability, susceptibility, coincidence, uptime) });

        var verdict = _store.UpsertPowerTables(broken);

        Assert.False(verdict.Ok);
        Assert.Contains("per-mille probability", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_predicate_frequency_edit_moves_the_content_hash()
    {
        _store.UpsertPowerTables(PowerTables.Authored());
        var before = _store.ComputeContentHash().Hash;

        _store.UpsertPowerTables(new PowerTables(
            PowerTables.Authored().Coefficients, PowerTables.Authored().Frequencies,
            new[] { new PredicateFrequencyRow("HasStatus", "burn", 900, 900, 900, 900) }));

        Assert.NotEqual(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void The_content_hash_schema_version_is_nine_now_that_the_prefix_suffix_split_has_landed()
    {
        // Drift canary, same discipline as ChannelPolicyStoreTests' own — bumped 7 -> 8 by
        // effect-pipeline T3.1 (affix-schema, 2026-09-02), then 8 -> 9 by T3.2 (prefix/suffix split,
        // same date) splitting effect_container's and rarity's pool_rolls column in two.
        _store.UpsertPowerTables(PowerTables.Authored());

        Assert.Equal(9, _store.ComputeContentHash().SchemaVersion);
    }
}
