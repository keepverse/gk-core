using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// D2 clause 12 — a head that does not match its own transcript is a <b>defect</b>, never a warning
/// and never a silently-repaired row. It throws.
/// </summary>
public sealed class ReplayDivergence : Exception
{
    public ReplayDivergence(string message) : base(message) { }
}

/// <summary>
/// D2 §9's replay law, implemented: <c>replay(origin_values_json, ops[1..n]) == head</c>, byte-exact,
/// <b>with no catalog involved</b>.
///
/// <para>⭐ <b>Clause 4 is enforced by the TYPE, not by a comment.</b> Every method here takes an
/// origin head and a list of ops and nothing else — there is no parameter through which a rules
/// table, a tuning file, an odds curve or a catalog could reach this code, so a replay physically
/// cannot re-run a formula or re-roll a die. That is what makes a rebalance structurally unable to
/// reach backwards into an item a player already owns: a re-simulating replay would silently
/// un-succeed an attempt they paid for.</para>
/// </summary>
public static class MutationReplay
{
    static MutationReplay() => MutationRules.EnsureRegistered();

    /// <summary>
    /// Apply the recorded deltas in order. Ops must be dense and gapless — check with
    /// <see cref="ValidateSequence"/> first, or pass <paramref name="validate"/>.
    /// </summary>
    public static InstanceHead Replay(InstanceHead origin, IReadOnlyList<MutationOp> ops, bool validate = true)
    {
        if (origin is null) throw new ArgumentNullException(nameof(origin));
        if (ops is null) throw new ArgumentNullException(nameof(ops));

        if (validate)
        {
            var refusal = ValidateSequence(ops);
            if (!refusal.IsOk) throw new ReplayDivergence(refusal.Detail);
        }

        var level = origin.EnhanceLevel;
        var atoms = origin.Atoms.ToDictionary(
            a => a.Seq,
            a => new InstanceAtomHead(a.Seq, a.AtomId, new Dictionary<string, long>(a.Values, StringComparer.Ordinal), a.Suppressed));

        foreach (var op in ops)
        {
            var result = op.Result;
            level = checked(level + result.EnhanceLevelDelta);
            if (level < 0)
                throw new ReplayDivergence(
                    $"instance '{op.InstanceId}' op {op.Seq} takes the enhancement level below zero — the transcript is wrong, not the head");

            foreach (var append in result.Appended)
            {
                if (atoms.ContainsKey(append.Seq))
                    throw new ReplayDivergence(
                        $"instance '{op.InstanceId}' op {op.Seq} appends seq {append.Seq}, which already exists — " +
                        "seq is never reused and never renumbered (D2 clause 9)");
                atoms[append.Seq] = new InstanceAtomHead(append.Seq, append.AtomId,
                    new Dictionary<string, long>(append.Values, StringComparer.Ordinal));
            }

            foreach (var seq in result.Suppressed)
            {
                if (!atoms.TryGetValue(seq, out var atom))
                    throw new ReplayDivergence($"instance '{op.InstanceId}' op {op.Seq} suppresses seq {seq}, which does not exist");
                // Suppress-then-append: the row stays, it is never deleted and seq is never
                // renumbered (D2 clause 9).
                atoms[seq] = atom with { Suppressed = true };
            }

            foreach (var set in result.Values)
            {
                if (!atoms.TryGetValue(set.Seq, out var atom))
                    throw new ReplayDivergence($"instance '{op.InstanceId}' op {op.Seq} writes seq {set.Seq}, which does not exist");
                var values = new Dictionary<string, long>(atom.Values, StringComparer.Ordinal) { [set.Key] = set.Value };
                atoms[set.Seq] = atom with { Values = values };
            }
        }

        return new InstanceHead(level, atoms.Values.OrderBy(a => a.Seq).ToList());
    }

    /// <summary>
    /// D2 clause 7 — <c>op_seq</c> is dense, gapless and in order. An out-of-order arrival is
    /// <c>ContentRuleViolated{mutation.op-sequence-gap}</c>, never a re-sorted list.
    /// </summary>
    public static AtomRejection ValidateSequence(IReadOnlyList<MutationOp> ops)
    {
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Seq != i + 1)
                return MutationRules.Violated("mutation.op-sequence-gap",
                    $"op at position {i} carries seq {ops[i].Seq}, expected {i + 1} — the ledger is dense and gapless");
            if (ops[i].Seq > MutationLimits.MutationSeqCap)
                return MutationRules.Violated("mutation.op-sequence-cap",
                    $"op seq {ops[i].Seq} is past the structural cap of {MutationLimits.MutationSeqCap}");
        }

        var duplicates = ops.GroupBy(o => o.CorrelationId, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicates is not null)
            return MutationRules.Violated("mutation.correlation-duplicated",
                $"correlation id '{duplicates.Key}' appears {duplicates.Count()} times on one instance — " +
                "UNIQUE(instance_id, correlation_id) is what makes a retry idempotent");

        return AtomRejection.Ok;
    }

    /// <summary>
    /// D2 clause 3's check: replay the transcript and compare its canonical state hash against the
    /// stored one. A mismatch throws <see cref="ReplayDivergence"/> loudly.
    /// </summary>
    public static InstanceHead VerifyAgainst(InstanceHead origin, IReadOnlyList<MutationOp> ops, string expectedStateHash)
    {
        var head = Replay(origin, ops);
        var actual = MutationCanonical.StateHash(head);
        if (!string.Equals(actual, expectedStateHash, StringComparison.Ordinal))
            throw new ReplayDivergence(
                $"replay of {ops.Count} op(s) hashes to {actual}, the stored head says {expectedStateHash} — " +
                "this is a defect, not a warning: one of the two has been written outside the op ledger");
        return head;
    }
}
