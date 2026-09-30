using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `charm-carry` (item module 22) at the DAL — ssot-charms.md §4.2's FIVE tables, the pouch writes and
/// the run-hold lifecycle. Driven with the real generated charm corpus (`gk-data/packs/fusion/data/seed/items/charms/**`)
/// and its 10 resonance breakpoints.
/// </summary>
[Trait("VerificationId", "data.charm-carry")]
public class CharmCarryStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public CharmCarryStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }

    static string CharmsDir() => Path.Combine(RepoRoot(), "data", "seed", "items", "charms");

    static IReadOnlyList<CharmDef> Corpus() =>
        Directory.EnumerateFiles(CharmsDir(), "*.json")
            // resonance.json is a separate breakpoint table; the generator ledger is metadata,
            // not a charm corpus file and has no `entries` array.
            .Where(p => !Path.GetFileName(p).Equals("resonance.json", StringComparison.Ordinal))
            .Where(p => !Path.GetFileName(p).EndsWith(".ledger.json", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .SelectMany(p => CharmCorpus.Parse(File.ReadAllText(p)))
            .ToList();

    static IReadOnlyList<CharmResonanceRow> Resonance() =>
        CharmResonance.DeriveTable(File.ReadAllText(Path.Combine(CharmsDir(), "resonance.json")));

    void ImportRealCorpus() => _store.ImportCharmCorpus(Corpus(), Resonance());

    // ---- the catalog -------------------------------------------------------------------------------

    [Fact]
    public void The_real_corpus_round_trips_defs_and_ten_resonance_rows()
    {
        ImportRealCorpus();

        var defs = _store.ListCharmDefs();
        Assert.Equal(Corpus().Count, defs.Count);
        Assert.NotEmpty(defs);
        Assert.All(defs.Where(d => d.Class == CharmClass.Signet), d => Assert.True(d.UniqueCarry));
        Assert.All(defs.Where(d => d.Class != CharmClass.Signet), d => Assert.False(d.UniqueCarry));

        var rows = _store.ListCharmResonance();
        // Formula, not a literal (population-pin SE3.4, 2026-09-20): one 2-count and one 3-count row
        // per axis, same shape ThresholdGrantCorpusTests/CharmCarryCorpusTests prove for this same
        // resonance data.
        Assert.Equal(CharmResonance.AxesOf(rows).Count * 2, rows.Count);
        Assert.Equal(5, rows.Select(r => r.Axis).Distinct().Count());
        Assert.All(rows, r => Assert.True(r.IsAuthoredUnpadded));
    }

    [Fact]
    public void Import_replaces_rather_than_accumulates()
    {
        ImportRealCorpus();
        ImportRealCorpus();
        Assert.Equal(Corpus().Count, _store.ListCharmDefs().Count);
        Assert.Equal(10, _store.ListCharmResonance().Count);
    }

    [Fact]
    public void The_resonance_table_never_becomes_attunable()
    {
        // ⭐ §4.2's device, at the DAL: the ten resonance containers are imported into charm_resonance
        // and into NOTHING else, so "a `charm.` container with no charm_def row is not attunable" is a
        // property of the schema rather than a rule someone has to remember.
        ImportRealCorpus();
        var defIds = _store.ListCharmDefs().Select(d => d.ContainerId).ToHashSet(StringComparer.Ordinal);

        foreach (var r in _store.ListCharmResonance())
        {
            Assert.DoesNotContain(r.ContainerId, defIds);
            Assert.DoesNotContain(r.AuthoredContainerId, defIds);
            var refusal = _store.Attune("p1", "inst-x", r.AuthoredContainerId);
            Assert.False(refusal.Ok);
            Assert.Equal(nameof(CharmCarryRefusalReason.CharmNotCarryable), refusal.Reason);
        }
    }

    // ---- capacity ----------------------------------------------------------------------------------

    [Fact]
    public void Capacity_is_null_until_progression_writes_one_and_is_never_defaulted_in_SQL()
    {
        Assert.Null(_store.GetCharmCapacity("p1"));
        _store.SetCharmCapacity("p1", 6);
        Assert.Equal(6, _store.GetCharmCapacity("p1"));
        _store.SetCharmCapacity("p1", 20);
        Assert.Equal(20, _store.GetCharmCapacity("p1"));
    }

    [Fact]
    public void Capacity_above_the_top_authored_rung_is_accepted_and_never_clamped()
    {
        // AGENTS.md. §3.3's "20 AP at cap" is the last AUTHORED rung; the store has no CHECK constraint
        // and no ceiling column, so a later progression design can keep going with a file save.
        _store.SetCharmCapacity("p1", 10_000);
        Assert.Equal(10_000, _store.GetCharmCapacity("p1"));
    }

    [Fact]
    public void A_negative_capacity_throws_rather_than_clamping_to_zero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => _store.SetCharmCapacity("p1", -1));
    }

    // ---- the pouch ---------------------------------------------------------------------------------

    [Fact]
    public void Attuning_writes_intent_and_creates_no_binding()
    {
        // §3.8: "attunement is durable intent, not a runtime fact". The pouch row exists; no
        // effect_binding does, and that is asserted against the real binding table.
        ImportRealCorpus();
        var charm = Corpus().First();

        Assert.True(_store.Attune("p1", "inst-1", charm.ContainerId).Ok);

        var pouch = _store.ListPouch("p1");
        var row = Assert.Single(pouch);
        Assert.Equal(charm.ContainerId, row.ContainerId);
        Assert.Equal(charm.Axis, row.Axis);
        Assert.Equal(charm.ApCost, row.ApCost);
        Assert.Equal(charm.UniqueCarry, row.UniqueCarry);

        Assert.Equal(0, CountRows("SELECT COUNT(*) FROM effect_binding;"));
    }

    [Fact]
    public void Attuning_a_container_with_no_def_row_refuses_CharmNotCarryable()
    {
        ImportRealCorpus();
        var refusal = _store.Attune("p1", "inst-1", "charm.not-authored-anywhere");
        Assert.False(refusal.Ok);
        Assert.Equal(nameof(CharmCarryRefusalReason.CharmNotCarryable), refusal.Reason);
        Assert.Empty(_store.ListPouch("p1"));
    }

    [Fact]
    public void The_pouch_round_trips_into_the_gates_own_input_shape()
    {
        ImportRealCorpus();
        var picks = Corpus().Where(c => c.Axis == "survivability").Take(2).ToList();
        for (var i = 0; i < picks.Count; i++)
            Assert.True(_store.Attune("p1", $"inst-{i}", picks[i].ContainerId).Ok);

        var tuning = CharmAttunementTuning.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "charm-attunement.v1.json")));

        var pouch = _store.ListPouch("p1");
        Assert.Empty(CharmPouchGate.Explain(pouch, tuning.StartingCapacityAp, tuning,
            attunableContainerIds: _store.ListCharmDefs().Select(d => d.ContainerId)
                .ToHashSet(StringComparer.Ordinal)));
    }

    // ---- the run hold ------------------------------------------------------------------------------

    IReadOnlyList<CharmHold> SnapshotOf(string playerId) => CharmRunBinder.Snapshot(_store.ListPouch(playerId));

    void AttuneThree()
    {
        ImportRealCorpus();
        var picks = Corpus().Where(c => c.Axis == "control").Take(3).ToList();
        for (var i = 0; i < picks.Count; i++)
            Assert.True(_store.Attune("p1", $"inst-{i}", picks[i].ContainerId).Ok);
    }

    [Fact]
    public void Sealing_a_run_writes_the_snapshot_in_its_sealed_order()
    {
        AttuneThree();
        var snapshot = SnapshotOf("p1");

        Assert.True(_store.OpenCharmRunHold("expedition", 1, "p1", snapshot).Ok);

        var held = _store.ListCharmRunHold("expedition", 1);
        Assert.Equal(3, held.Count);
        Assert.Equal(new[] { 0, 1, 2 }, held.Select(h => h.Seq));
        Assert.Equal(snapshot.Select(h => h.InstanceId), held.Select(h => h.InstanceId));
        Assert.All(held, h => Assert.True(h.Active));
    }

    [Fact]
    public void Sealing_the_same_run_twice_is_a_replay_and_writes_nothing()
    {
        AttuneThree();
        Assert.True(_store.OpenCharmRunHold("expedition", 1, "p1", SnapshotOf("p1")).Ok);

        var again = _store.OpenCharmRunHold("expedition", 1, "p1", SnapshotOf("p1"));
        Assert.True(again.Ok);
        Assert.Equal("replay", again.Reason);
        Assert.Equal(3, _store.ListCharmRunHold("expedition", 1).Count);
    }

    [Fact]
    public void The_partial_unique_index_is_what_enforces_exclusivity_and_a_second_run_rolls_back_whole()
    {
        // ⭐ Not a check-then-insert: the INSERT is attempted and the index refuses it. A read-then-write
        // check has a window between the two; the index does not. And the refusal is ALL OR NOTHING —
        // a run is never half sealed.
        AttuneThree();
        Assert.True(_store.OpenCharmRunHold("expedition", 1, "p1", SnapshotOf("p1")).Ok);

        var clash = _store.OpenCharmRunHold("expedition", 2, "p1", SnapshotOf("p1"));
        Assert.False(clash.Ok);
        Assert.Equal(nameof(CharmCarryRefusalReason.CharmInUse), clash.Reason);
        Assert.Empty(_store.ListCharmRunHold("expedition", 2));
    }

    [Fact]
    public void A_raw_insert_that_bypasses_every_C_sharp_check_still_cannot_double_hold_a_charm()
    {
        // The rule is the INDEX, proven by going around the store's own method entirely.
        AttuneThree();
        Assert.True(_store.OpenCharmRunHold("expedition", 1, "p1", SnapshotOf("p1")).Ok);

        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO charm_run_hold
              (run_kind, run_id, player_id, instance_id, container_id, axis, ap_cost, seq, active)
            VALUES ('battle', 99, 'p1', 'inst-0', 'charm.whatever', 'control', 1, 0, 1);
            """;
        var ex = Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
        Assert.Equal(19, ex.SqliteErrorCode);
    }

    [Fact]
    public void An_inactive_hold_frees_the_charm_for_the_next_run_and_stays_for_audit()
    {
        // §3.8: "bindings withdraw by source; charm_run_hold rows go inactive and STAY for audit".
        // Deleting them would take the replay input with them.
        AttuneThree();
        Assert.True(_store.OpenCharmRunHold("expedition", 1, "p1", SnapshotOf("p1")).Ok);
        Assert.Equal(3, _store.CloseCharmRunHold("expedition", 1));

        Assert.Equal(3, _store.ListCharmRunHold("expedition", 1).Count);
        Assert.All(_store.ListCharmRunHold("expedition", 1), h => Assert.False(h.Active));

        Assert.True(_store.OpenCharmRunHold("expedition", 2, "p1", SnapshotOf("p1")).Ok);
    }

    [Fact]
    public void Un_attuning_a_held_charm_refuses_CharmInUse_and_the_row_survives()
    {
        // Refuse, never silently hold (§3.8): "a charm is a per-run dial, so a silently held change is
        // a player believing they made a decision that did nothing."
        AttuneThree();
        Assert.True(_store.OpenCharmRunHold("expedition", 1, "p1", SnapshotOf("p1")).Ok);

        var refusal = _store.Unattune("p1", "inst-0");
        Assert.False(refusal.Ok);
        Assert.Equal(nameof(CharmCarryRefusalReason.CharmInUse), refusal.Reason);
        Assert.Contains("expedition#1", refusal.Detail);
        Assert.Equal(3, _store.ListPouch("p1").Count);

        _store.CloseCharmRunHold("expedition", 1);
        Assert.True(_store.Unattune("p1", "inst-0").Ok);
        Assert.Equal(2, _store.ListPouch("p1").Count);
    }

    [Fact]
    public void Attuning_a_charm_a_live_run_holds_into_a_second_pouch_refuses_CharmInUse()
    {
        // Cross-run exclusivity is the cost that scales with how wide you play (§3.2 item 2).
        AttuneThree();
        Assert.True(_store.OpenCharmRunHold("expedition", 1, "p1", SnapshotOf("p1")).Ok);

        var refusal = _store.Attune("p2", "inst-0", Corpus().First(c => c.Axis == "control").ContainerId);
        Assert.False(refusal.Ok);
        Assert.Equal(nameof(CharmCarryRefusalReason.CharmInUse), refusal.Reason);
    }

    [Fact]
    public void The_pouch_stays_editable_while_a_run_holds_only_some_of_it()
    {
        // §3.8's "why the pouch stays editable and only the held charms lock" — freezing the whole
        // pouch would be miserable once expeditions run 20 hours in parallel.
        AttuneThree();
        var snapshot = CharmRunBinder.Snapshot(_store.ListPouch("p1").Take(1).ToList());
        Assert.True(_store.OpenCharmRunHold("expedition", 1, "p1", snapshot).Ok);

        Assert.False(_store.Unattune("p1", "inst-0").Ok);   // held
        Assert.True(_store.Unattune("p1", "inst-2").Ok);    // free
    }

    [Fact]
    public void HeldByLiveRun_reports_the_run_the_pouch_UI_must_name()
    {
        AttuneThree();
        Assert.True(_store.OpenCharmRunHold("battle", 7, "p1", SnapshotOf("p1")).Ok);

        var held = _store.HeldByLiveRun(new[] { "inst-0", "inst-9" });
        Assert.Equal("battle#7", held["inst-0"]);
        Assert.DoesNotContain("inst-9", held.Keys);

        _store.CloseCharmRunHold("battle", 7);
        Assert.Empty(_store.HeldByLiveRun(new[] { "inst-0" }));
    }

    [Fact]
    public void The_five_tables_exist_and_none_of_them_added_a_column_to_an_atom_table()
    {
        // §4.2's own reason for side tables: repeating effect_container's slot/rarity precedent for a
        // fifth kind is how a shared table becomes a union of every kind's private fields.
        foreach (var table in new[]
                 { "charm_def", "charm_pouch", "charm_run_hold", "charm_resonance", "charm_attunement" })
            Assert.Equal(1, CountRows(
                $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}';"));

        Assert.Equal(1, CountRows(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'ix_charm_run_hold_active';"));

        foreach (var col in new[] { "axis", "ap_cost", "unique_carry", "frame_hint" })
            Assert.Equal(0, CountRows(
                $"SELECT COUNT(*) FROM pragma_table_info('effect_container') WHERE name = '{col}';"));
    }

    long CountRows(string sql)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }
}
