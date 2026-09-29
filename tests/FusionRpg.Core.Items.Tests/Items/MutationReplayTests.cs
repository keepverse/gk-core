using System.Reflection;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Power;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// D2 §9's fifteen clauses, as tests. The transcript law is the property this design most
/// deliberately buys: a rebalance must be structurally unable to reach backwards into an item a
/// player already owns.
/// </summary>
public class MutationReplayTests
{
    static InstanceHead Origin() => new(0, new[]
    {
        new InstanceAtomHead(1, "atom.power", new Dictionary<string, long>(StringComparer.Ordinal) { ["amount"] = 100 }),
        new InstanceAtomHead(2, "atom.guard", new Dictionary<string, long>(StringComparer.Ordinal) { ["amount"] = 40 }),
    });

    static MutationOp Op(int seq, MutationOpKind kind, MutationResult result, string? correlation = null) =>
        new("inst.1", seq, kind, correlation ?? $"corr.{seq}", 12345, result, "2026-09-05T00:00:00Z");

    [Fact]
    public void Replay_of_origin_plus_ops_equals_the_head()
    {
        var ops = new[]
        {
            Op(1, MutationOpKind.Enhance, new MutationResult("success", 1,
                new[] { new AtomValueSet(1, "amount", 102) }, Array.Empty<int>(), Array.Empty<AtomAppend>())),
            Op(2, MutationOpKind.Enhance, new MutationResult("success", 1,
                new[] { new AtomValueSet(1, "amount", 104), new AtomValueSet(2, "amount", 41) },
                Array.Empty<int>(), Array.Empty<AtomAppend>())),
        };

        var head = MutationReplay.Replay(Origin(), ops);
        Assert.Equal(2, head.EnhanceLevel);
        Assert.Equal(104, head.Atoms.Single(a => a.Seq == 1).Values["amount"]);
        Assert.Equal(41, head.Atoms.Single(a => a.Seq == 2).Values["amount"]);
    }

    [Fact]
    public void A_rebalance_of_the_odds_table_changes_no_owned_item()
    {
        // Clause 4, the property this design exists for. The transcript holds materialised deltas, so
        // replaying it after ANY tuning change produces the identical head — proven by replaying the
        // same ops with the real tuning file loaded and then with a deliberately-wrecked one; neither
        // is reachable from Replay at all, which is the point.
        var ops = new[]
        {
            Op(1, MutationOpKind.Enhance, new MutationResult("success", 1,
                new[] { new AtomValueSet(1, "amount", 102) }, Array.Empty<int>(), Array.Empty<AtomAppend>())),
        };

        var before = MutationCanonical.StateHash(MutationReplay.Replay(Origin(), ops));
        _ = EnhancementTuning.Parse(EnhancePolicyTests.TuningJson()
            .Replace("\"scalarPerLevelMilli\": 20", "\"scalarPerLevelMilli\": 999"));
        var after = MutationCanonical.StateHash(MutationReplay.Replay(Origin(), ops));

        Assert.Equal(before, after);
    }

    [Fact]
    public void Replay_never_reads_the_rules_table()
    {
        // Enforced by the TYPE, then asserted: no method on MutationReplay accepts a tuning, a
        // catalog, a container or an RNG, so a re-simulating replay is not expressible here.
        var forbidden = new[] { "Tuning", "Catalog", "Container", "Rng", "Random", "Policy" };
        foreach (var method in typeof(MutationReplay).GetMethods(BindingFlags.Public | BindingFlags.Static))
            foreach (var parameter in method.GetParameters())
                foreach (var word in forbidden)
                    Assert.False(parameter.ParameterType.Name.Contains(word, StringComparison.Ordinal),
                        $"MutationReplay.{method.Name} takes a {parameter.ParameterType.Name} — replay must not be able to re-run a formula");
    }

    [Fact]
    public void Op_seq_is_dense_and_an_out_of_order_arrival_is_a_sequence_gap()
    {
        var gapped = new[]
        {
            Op(1, MutationOpKind.Enhance, MutationResult.Nothing("failure")),
            Op(3, MutationOpKind.Enhance, MutationResult.Nothing("failure")),
        };

        var refusal = MutationReplay.ValidateSequence(gapped);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, refusal.Reason);
        Assert.Contains("mutation.op-sequence-gap", refusal.Detail);
        Assert.Throws<ReplayDivergence>(() => MutationReplay.Replay(Origin(), gapped));
    }

    [Fact]
    public void A_reused_correlation_on_one_instance_is_refused()
    {
        var duplicated = new[]
        {
            Op(1, MutationOpKind.Enhance, MutationResult.Nothing("failure"), "corr.same"),
            Op(2, MutationOpKind.Enhance, MutationResult.Nothing("failure"), "corr.same"),
        };

        Assert.Contains("mutation.correlation-duplicated", MutationReplay.ValidateSequence(duplicated).Detail);
    }

    [Fact]
    public void Seq_is_never_renumbered_and_an_identity_change_suppresses_then_appends()
    {
        // D2 clause 9. The suppressed row stays in the head with its original seq; the replacement
        // arrives at a NEW seq.
        var reforge = Op(1, MutationOpKind.RerollAffix, new MutationResult("applied", 0,
            Array.Empty<AtomValueSet>(),
            new[] { 2 },
            new[] { new AtomAppend(3, "atom.frost", new Dictionary<string, long>(StringComparer.Ordinal) { ["amount"] = 55 }) }));

        var head = MutationReplay.Replay(Origin(), new[] { reforge });
        Assert.Equal(new[] { 1, 2, 3 }, head.Atoms.Select(a => a.Seq).ToArray());
        Assert.True(head.Atoms.Single(a => a.Seq == 2).Suppressed);
        Assert.Equal("atom.guard", head.Atoms.Single(a => a.Seq == 2).AtomId); // never deleted
        Assert.Equal(55, head.Atoms.Single(a => a.Seq == 3).Values["amount"]);
    }

    [Fact]
    public void Appending_a_seq_that_already_exists_is_a_loud_divergence()
    {
        var bad = Op(1, MutationOpKind.RerollAffix, new MutationResult("applied", 0,
            Array.Empty<AtomValueSet>(), Array.Empty<int>(),
            new[] { new AtomAppend(1, "atom.dup", new Dictionary<string, long>(StringComparer.Ordinal)) }));

        Assert.Throws<ReplayDivergence>(() => MutationReplay.Replay(Origin(), new[] { bad }));
    }

    [Fact]
    public void An_on_apply_affix_is_enhanced_by_rewriting_min_max_inside_values_json()
    {
        // D2 clause 14 — there is no effect_instance_atom.overrides_json, and the head IS the SSOT.
        // Pinned against the shipped DDL, which is instance_id · seq · atom_id · values_json ·
        // power_json (+ identity_digest from module 1); an OnApply spec stays as authored inside
        // values_json, so enhancing one rewrites min/max in place.
        var origin = new InstanceHead(0, new[]
        {
            new InstanceAtomHead(1, "atom.strike", new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["min"] = 10, ["max"] = 20,
            }),
        });

        var op = Op(1, MutationOpKind.Enhance, new MutationResult("success", 1,
            new[] { new AtomValueSet(1, "min", 12), new AtomValueSet(1, "max", 24) },
            Array.Empty<int>(), Array.Empty<AtomAppend>()));

        var head = MutationReplay.Replay(origin, new[] { op });
        Assert.Equal(12, head.Atoms[0].Values["min"]);
        Assert.Equal(24, head.Atoms[0].Values["max"]);
        Assert.Equal(2, head.Atoms[0].Values.Count); // nothing else was invented
    }

    [Fact]
    public void A_head_log_mismatch_raises_replay_divergence_loudly()
    {
        var ops = new[]
        {
            Op(1, MutationOpKind.Enhance, new MutationResult("success", 1,
                new[] { new AtomValueSet(1, "amount", 102) }, Array.Empty<int>(), Array.Empty<AtomAppend>())),
        };

        var honest = MutationCanonical.StateHash(MutationReplay.Replay(Origin(), ops));
        Assert.Equal(honest, MutationCanonical.StateHash(MutationReplay.VerifyAgainst(Origin(), ops, honest)));

        var ex = Assert.Throws<ReplayDivergence>(() => MutationReplay.VerifyAgainst(Origin(), ops, new string('0', 64)));
        Assert.Contains("defect, not a warning", ex.Message);
    }

    [Fact]
    public void The_state_hash_is_order_sensitive_and_length_prefixed()
    {
        // XOR-folding is banned for exactly this reason: it cannot tell these two apart.
        var a = new InstanceHead(0, new[]
        {
            new InstanceAtomHead(1, "ab", new Dictionary<string, long>(StringComparer.Ordinal) { ["k"] = 1 }),
            new InstanceAtomHead(2, "c", new Dictionary<string, long>(StringComparer.Ordinal) { ["k"] = 1 }),
        });
        var b = new InstanceHead(0, new[]
        {
            new InstanceAtomHead(1, "a", new Dictionary<string, long>(StringComparer.Ordinal) { ["k"] = 1 }),
            new InstanceAtomHead(2, "bc", new Dictionary<string, long>(StringComparer.Ordinal) { ["k"] = 1 }),
        });

        Assert.NotEqual(MutationCanonical.StateHash(a), MutationCanonical.StateHash(b));
        Assert.Equal(64, MutationCanonical.StateHash(a).Length); // SHA256 hex, not a fold
    }

    [Fact]
    public void The_result_json_round_trips_byte_identically()
    {
        var result = new MutationResult("success", 1,
            new[] { new AtomValueSet(2, "b", 7), new AtomValueSet(1, "a", 5) },
            new[] { 3, 2 },
            new[] { new AtomAppend(4, "atom.x", new Dictionary<string, long>(StringComparer.Ordinal) { ["z"] = 1, ["a"] = 2 }) });

        var json = MutationCanonical.WriteResult(result);
        var again = MutationCanonical.WriteResult(MutationCanonical.ReadResult(json));
        Assert.Equal(json, again);
        // Canonical means SORTED: a differently-ordered but equal result serialises identically.
        var shuffled = new MutationResult("success", 1,
            new[] { new AtomValueSet(1, "a", 5), new AtomValueSet(2, "b", 7) },
            new[] { 2, 3 }, result.Appended);
        Assert.Equal(json, MutationCanonical.WriteResult(shuffled));
    }

    [Fact]
    public void Mutation_seq_is_capped_at_4096_and_the_comment_says_it_is_structural()
    {
        Assert.Equal(4096, MutationLimits.MutationSeqCap);

        var source = File.ReadAllText(Path.Combine(
            MaterialCorpusTests.RepoRoot(), "src", "FusionRpg.Core", "Items", "Mutation", "MutationOp.cs"));
        Assert.Contains("Structural, not a design ceiling", source, StringComparison.Ordinal);
        Assert.Contains("THROWS", source, StringComparison.Ordinal);
    }

    // ---- §10, the one module-9 read ------------------------------------------------------------------

    [Fact]
    public void The_mutation_preview_reads_module_9_R3_and_nothing_else()
    {
        var tuning = new ItemPowerTuning(300, null, ShowPowerOnCard: true, PowerDisplaySigFigs: 2, PowerDisplayBandPercent: 25);
        var preview = MutationPreview.Preview(new PowerVector(1000, 0, 0, 0, 0), new PowerVector(1300, 0, 0, 0, 0), tuning);

        Assert.True(preview.Shown);
        Assert.Contains("±25%", preview.Render());
        Assert.Contains("→", preview.Render());

        // No pricer, vector or cost function is DECLARED under Items/Mutation — the read is module
        // 9's, borrowed, not re-derived.
        var dir = Path.Combine(MaterialCorpusTests.RepoRoot(), "src", "FusionRpg.Core", "Items", "Mutation");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs"))
        foreach (var line in File.ReadLines(file))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal)) continue;
            Assert.False(trimmed.Contains("class PowerVector", StringComparison.Ordinal)
                         || trimmed.Contains("record PowerVector", StringComparison.Ordinal)
                         || trimmed.Contains("class PowerScalar", StringComparison.Ordinal),
                $"{Path.GetFileName(file)} declares its own pricer — §10 forbids a second one");
        }
    }

    [Fact]
    public void Show_power_on_card_false_suppresses_the_preview_figure_too()
    {
        // ⚠ One tunable, two surfaces — or G3 §10 Q7's reversal is only half a reversal.
        var off = new ItemPowerTuning(300, null, ShowPowerOnCard: false, PowerDisplaySigFigs: 2, PowerDisplayBandPercent: 25);
        var preview = MutationPreview.Preview(new PowerVector(1000, 0, 0, 0, 0), new PowerVector(1300, 0, 0, 0, 0), off);

        Assert.False(preview.Shown);
        Assert.Equal("", preview.Render());
    }
}
