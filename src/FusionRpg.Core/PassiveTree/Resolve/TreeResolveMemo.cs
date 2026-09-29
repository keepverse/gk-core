using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.PassiveTree.Resolve;

/// <summary>
/// Task D7 — memoizes <see cref="TreeAtomSource.BoundAtomsFor"/> the same way
/// <c>AptitudeSubsystem</c> already memoizes its own resolve (`AptitudeSubsystem.cs:32-52`,
/// spec-tree-resolve.md §11: "Memoize by reference, never by an external bump... Key on ... plus the
/// tree-state reference, and re-resolve whenever that reference differs").
///
/// <para><b>What "the tree-state reference" is here.</b> `tree-state`'s own per-actor state record
/// (spec-tree-state.md) has not shipped yet — there is no single object today bundling one actor's
/// full cross-tree allocation. The `IReadOnlySet&lt;string&gt; ownedNodeIds` handed to
/// <see cref="TreeAtomSource.BoundAtomsFor"/> IS the state reference available at this seam: it plays
/// the exact role <c>AptitudeAllocation</c> plays for <c>AptitudeSubsystem</c> — a host that has not
/// changed an actor's tree hands back the SAME set instance every resolve (the common case), and any
/// genuinely different instance — even one with identical members — safely, harmlessly recomputes
/// rather than silently serving stale state. When `tree-state`'s record ships, the key naturally widens
/// to that reference instead; nothing about this shape needs to change to accept it.</para>
///
/// <para><b>Reference equality, not value equality — deliberately.</b> The perf SSOT
/// (`runbook/perf-probe-plan.md`) already names the failure mode this guards against: an uncached tree
/// resolve on the hit path. A value-equality (deep) comparison over a node-id set would cost as much as
/// the resolve itself and defeat the point of caching; a bare reference check is the "cheap enough to
/// call every time" test <c>AptitudeSubsystem</c>'s own doc comment states explicitly.</para>
///
/// <para><b>Bounded growth.</b> A changed reference for the same `(TreeId, TierReached, ThetaNode,
/// FMilli)` key OVERWRITES that key's single slot — it never adds a new one — so the memo never grows
/// past one entry per distinct key regardless of how many times an actor's allocation changes. Not
/// static, for the same reason `AptitudeSubsystem`'s memo is not static: a static memo would leak one
/// scoped host's cached resolve into another's.</para>
/// </summary>
public sealed class TreeResolveMemo
{
    readonly Dictionary<(string TreeId, int TierReached, long ThetaNode, long FMilli),
        (IReadOnlySet<string> Owned, IReadOnlyList<BoundDerivedAtom> Result)> _memo = new();

    /// <summary>Same contract as <see cref="TreeAtomSource.BoundAtomsFor"/>, memoized. Returns the
    /// SAME <see cref="IReadOnlyList{T}"/> instance on a cache hit (never a fresh, value-equal copy) —
    /// that reference identity is exactly what proves no recomputation happened, and it is what test 20
    /// asserts on.</summary>
    public IReadOnlyList<BoundDerivedAtom> BoundAtomsFor(
        LoadedTree tree, IReadOnlySet<string> ownedNodeIds, int tierReached, long thetaNode, PowerTuning powerTuning,
        long fMilli)
    {
        if (tree is null) throw new ArgumentNullException(nameof(tree));
        if (ownedNodeIds is null) throw new ArgumentNullException(nameof(ownedNodeIds));

        var key = (tree.Tree.TreeId, tierReached, thetaNode, fMilli);

        if (_memo.TryGetValue(key, out var entry) && ReferenceEquals(entry.Owned, ownedNodeIds))
            return entry.Result;

        var resolved = TreeAtomSource.BoundAtomsFor(tree, ownedNodeIds, tierReached, thetaNode, powerTuning, fMilli);
        _memo[key] = (ownedNodeIds, resolved);
        return resolved;
    }

    /// <summary>Forces every memoized entry to recompute on its next read, regardless of whether the
    /// owned-node-set reference changed. Not needed for correctness — the memo is self-correcting by
    /// reference on every call — kept as an explicit escape hatch the same way
    /// <c>AptitudeSubsystem.InvalidateMemo</c> is (e.g. for a test that wants to guarantee a fresh
    /// resolve without depending on object identity).</summary>
    public void InvalidateMemo() => _memo.Clear();
}
