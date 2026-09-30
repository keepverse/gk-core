using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `enhance-reroll` (item module 15) at the DAL — the five head columns, `effect_instance_atom
/// .suppressed`, the `effect_instance_op` ledger with its correlation uniqueness, and the
/// `reroll_cost_mult` seed. Driven with the REAL shipped `gk-core/data/tuning/enhancement.v1.json`.
/// </summary>
public class InstanceOpTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public InstanceOpTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static EnhancementTuning Tuning() => EnhancementTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "enhancement.v1.json")));

    string NewInstance() => _store.SaveInstance(new InstanceRow
    {
        ContainerId = "item.blade",
        RollSeed = 12345,
        CatalogRevision = _store.GetCatalogRevision(),
        Origin = InstanceOrigin.Drop,
        Atoms = new[]
        {
            new InstanceAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1), """{"amount":45}"""),
            new InstanceAtomRow(2, AtomRow.DeriveId("atom.might", "", 1), """{"amount":12}"""),
        },
    });

    static MutationResult Enhanced(int seq, long value) => new(
        "success", 1, new[] { new AtomValueSet(seq, "amount", value) }, Array.Empty<int>(), Array.Empty<AtomAppend>());

    [Fact]
    public void A_fresh_instance_starts_at_plus_zero_with_no_transcript()
    {
        var id = NewInstance();
        var head = _store.GetInstanceMutationHead(id);

        Assert.NotNull(head);
        Assert.Equal(0, head!.EnhanceLevel);
        Assert.Equal(0, head.PityCounter);
        Assert.Equal(0, head.MutationSeq);
        Assert.Null(head.StateHash);
        // D2 §11.3's lean: origin_values_json is written LAZILY, at the FIRST mutation. An item
        // nobody ever crafts never pays for a second copy of its own numbers.
        Assert.Null(head.OriginValuesJson);
        Assert.Empty(_store.ReadMutationOps(id));
    }

    [Fact]
    public void An_op_appends_to_the_ledger_and_rewrites_the_head_in_one_transaction()
    {
        var id = NewInstance();
        var result = Enhanced(1, 46);

        var append = _store.AppendMutationOp(id, MutationOpKind.Enhance, "corr.1", 999, result,
            "hash-1", """{"1":{"amount":45}}""", "2026-09-05T00:00:00Z");

        Assert.True(append.Ok);
        Assert.False(append.Replayed);
        Assert.Equal(1, append.Seq);

        var head = _store.GetInstanceMutationHead(id)!;
        Assert.Equal(1, head.EnhanceLevel);
        Assert.Equal(1, head.MutationSeq);
        Assert.Equal("hash-1", head.StateHash);
        Assert.Equal("""{"1":{"amount":45}}""", head.OriginValuesJson);

        var ops = _store.ReadMutationOps(id);
        Assert.Single(ops);
        Assert.Equal(MutationOpKind.Enhance, ops[0].Kind);
        Assert.Equal(999, ops[0].OpSeed);
        Assert.Equal(46, ops[0].Result.Values.Single().Value);
    }

    [Fact]
    public void Origin_values_json_is_written_once_and_never_rewritten()
    {
        var id = NewInstance();
        _store.AppendMutationOp(id, MutationOpKind.Enhance, "corr.1", 1, Enhanced(1, 46), "h1", "ORIGIN", "u");
        _store.AppendMutationOp(id, MutationOpKind.Enhance, "corr.2", 2, Enhanced(1, 47), "h2", "SOMETHING-ELSE", "u");

        // D2 rung 1' is the ORIGIN — a later op must never move it, or replay starts from the wrong
        // place and every earlier op silently means something different.
        Assert.Equal("ORIGIN", _store.GetInstanceMutationHead(id)!.OriginValuesJson);
    }

    [Fact]
    public void A_replayed_correlation_returns_the_recorded_result()
    {
        // D2 clause 8, the shape RpgStore.Souls.cs already ships: a retry is idempotent, not a second
        // application.
        var id = NewInstance();
        var result = Enhanced(1, 46);

        var first = _store.AppendMutationOp(id, MutationOpKind.Enhance, "corr.retry", 7, result, "h", "o", "u");
        var second = _store.AppendMutationOp(id, MutationOpKind.Enhance, "corr.retry", 7, result, "h", "o", "u");

        Assert.True(second.Ok);
        Assert.True(second.Replayed);
        Assert.Equal(first.Seq, second.Seq);
        Assert.Single(_store.ReadMutationOps(id));
        Assert.Equal(1, _store.GetInstanceMutationHead(id)!.EnhanceLevel); // applied ONCE
    }

    [Fact]
    public void A_reused_correlation_with_different_parameters_is_refused()
    {
        var id = NewInstance();
        _store.AppendMutationOp(id, MutationOpKind.Enhance, "corr.x", 7, Enhanced(1, 46), "h", "o", "u");

        var clash = _store.AppendMutationOp(id, MutationOpKind.Enhance, "corr.x", 7, Enhanced(1, 999), "h", "o", "u");
        Assert.False(clash.Ok);
        Assert.Contains("different parameters", clash.Reason);
        Assert.Single(_store.ReadMutationOps(id));
    }

    [Fact]
    public void The_same_correlation_on_a_different_instance_is_a_different_op()
    {
        // UNIQUE is (instance_id, correlation_id) — a batch that touches two items with one
        // correlation (a transfer!) is legal and must stay legal.
        var donor = NewInstance();
        var recipient = NewInstance();

        Assert.True(_store.AppendMutationOp(donor, MutationOpKind.EnhanceTransferOut, "corr.move", 1,
            new MutationResult("applied", 0, Array.Empty<AtomValueSet>(), Array.Empty<int>(), Array.Empty<AtomAppend>()),
            "h", "o", "u").Ok);
        Assert.True(_store.AppendMutationOp(recipient, MutationOpKind.EnhanceTransferIn, "corr.move", 1,
            new MutationResult("applied", 7, Array.Empty<AtomValueSet>(), Array.Empty<int>(), Array.Empty<AtomAppend>()),
            "h", "o", "u").Ok);

        Assert.Equal(7, _store.GetInstanceMutationHead(recipient)!.EnhanceLevel);
    }

    [Fact]
    public void An_identity_change_suppresses_the_row_rather_than_deleting_it()
    {
        var id = NewInstance();
        var reforge = new MutationResult("applied", 0, Array.Empty<AtomValueSet>(), new[] { 2 }, Array.Empty<AtomAppend>());
        Assert.True(_store.AppendMutationOp(id, MutationOpKind.RerollAffix, "corr.reforge", 3, reforge, "h", "o", "u").Ok);

        var atoms = _store.GetInstance(id)!.Atoms;
        Assert.Equal(2, atoms.Count); // the row is still there, seq unchanged
        Assert.Contains(atoms, a => a.Seq == 2);
    }

    [Fact]
    public void The_transcript_replays_to_the_stored_head_for_every_mutated_instance()
    {
        // D2 clause 3 over a whole fixture database, not a spot check: build several instances,
        // mutate each a different number of times, then replay all of them.
        var origins = new Dictionary<string, InstanceHead>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 1; i <= 5; i++)
        {
            var id = NewInstance();
            var origin = new InstanceHead(0, new[]
            {
                new InstanceAtomHead(1, "atom.vitality", new Dictionary<string, long>(StringComparer.Ordinal) { ["amount"] = 45 }),
                new InstanceAtomHead(2, "atom.might", new Dictionary<string, long>(StringComparer.Ordinal) { ["amount"] = 12 }),
            });
            origins[id] = origin;

            var head = origin;
            for (var op = 1; op <= i; op++)
            {
                var value = 45 + op;
                var result = Enhanced(1, value);
                head = MutationReplay.Replay(head, new[]
                {
                    new MutationOp(id, op, MutationOpKind.Enhance, $"c{op}", op, result, "u"),
                }, validate: false);

                var hash = MutationCanonical.StateHash(head);
                Assert.True(_store.AppendMutationOp(id, MutationOpKind.Enhance, $"c{op}", op, result, hash, "{}", "u").Ok);
                hashes[id] = hash;
            }
        }

        foreach (var (id, origin) in origins)
        {
            var stored = _store.GetInstanceMutationHead(id)!;
            var replayed = MutationReplay.VerifyAgainst(origin, _store.ReadMutationOps(id), stored.StateHash!);
            Assert.Equal(stored.EnhanceLevel, replayed.EnhanceLevel);
            Assert.Equal(hashes[id], MutationCanonical.StateHash(replayed));
        }
    }

    [Fact]
    public void The_pity_counter_persists_across_reads()
    {
        var id = NewInstance();
        _store.SetInstancePityCounter(id, 17);
        Assert.Equal(17, _store.GetInstanceMutationHead(id)!.PityCounter);

        // A second store over the same directory — "persists across sessions", the rpg_summon_pity
        // shape reused.
        var reopened = _testStore.Reopen();
        Assert.Equal(17, reopened.GetInstanceMutationHead(id)!.PityCounter);
    }

    [Fact]
    public void An_op_that_would_take_the_level_below_zero_is_refused()
    {
        var id = NewInstance();
        var underflow = new MutationResult("failure-downgrade", -1, Array.Empty<AtomValueSet>(), Array.Empty<int>(), Array.Empty<AtomAppend>());
        var result = _store.AppendMutationOp(id, MutationOpKind.Enhance, "corr.under", 1, underflow, "h", "o", "u");

        Assert.False(result.Ok);
        Assert.Empty(_store.ReadMutationOps(id));
    }

    [Fact]
    public void Deleting_an_instance_cascades_its_ops()
    {
        var id = NewInstance();
        _store.AppendMutationOp(id, MutationOpKind.Enhance, "corr.1", 1, Enhanced(1, 46), "h", "o", "u");
        Assert.Single(_store.ReadMutationOps(id));

        _store.DeleteInstance(id);
        Assert.Empty(_store.ReadMutationOps(id));
    }

    [Fact]
    public void Reroll_cost_mult_seeds_every_rung_through_the_SC7_gate()
    {
        var t = Tuning();
        _store.SeedRerollCostMult(t);

        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
        {
            var rung = RarityLadder.RungIds[i];
            Assert.Equal(RerollPolicy.RungLegMilli(i, t), _store.GetRarityBudget(rung, "reroll_cost_mult"));
        }

        // ⭐ 2026-09-05: module 16 (`sockets`) decided socket_min/socket_max, so this row MOVED
        // rather than loosening — the key now writes, and the SC7 gate is asserted against a key with
        // no consumer at all. The gate has to survive the closed list happening to be fully decided.
        _store.SetRarityBudget("almanac", "socket_max", 4);
        Assert.Equal(4, _store.GetRarityBudget("almanac", "socket_max"));
        Assert.Throws<RarityBudgetKeyRejection>(() => _store.SetRarityBudget("almanac", "no_such_key", 3));
    }
}
