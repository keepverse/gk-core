using System.Text.RegularExpressions;
using FusionRpg.Core.PassiveTree;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Data;
using PassiveTreeAtoms = FusionRpg.Core.PassiveTree.Resolve.TreeAtomSource;

namespace FusionRpg.Server;

/// <summary>
/// Commander-scope shared-tree <c>stat.derived</c> atoms for Server Hub fan-in.
///
/// <para>lawn-tree-hydrate (T13, 2026-09-13): "Injector does not hydrate <see cref="PassiveTreeTuningHub"/>
/// -- lawn tree is a named gap" is CLOSED, not by configuring this hub in the Injector process (it
/// still isn't, and does not need to be -- the SQL-backed resolve below stays Server-only) but by a
/// thin HTTP round trip: <c>PassiveTreeEndpoints.MapGet("/bound-atoms/{playerId}")</c> calls this exact
/// method and serializes its result; <c>RpgClient.RefreshTreeBoundAtomsAsync</c> fetches it and caches
/// it in <c>FusionRpg.Injector.Stats.TreeBoundAtomsCache</c>, fanned into the Injector's own
/// <c>ActorHub</c> via its <c>boundDerivedAtoms</c> delegate alongside the live-grant reader
/// (<c>CheatState.cs</c>) -- the same "Injector has no store, Server does the SQL work once" shape
/// <c>RpgClient.RefreshCommanderAllocationAsync</c> already uses for commander aptitude.</para>
/// </summary>
public static class TreeBoundAtoms
{
    static readonly Regex AptitudeGatePattern =
        new(@"^aptitude\.(?<id>[A-Za-z]+)@Commander$", RegexOptions.Compiled);

    public static IReadOnlyList<BoundDerivedAtom> ForPlayer(
        RpgStore store, IPowerIndexProvider powerIndex, long playerId)
    {
        PassiveTreeTuning tuning;
        try { tuning = PassiveTreeTuningHub.Tuning; }
        catch (InvalidOperationException) { return Array.Empty<BoundDerivedAtom>(); }

        var scopeKey = AptitudeEndpoints.ScopeKey(playerId);
        var sharedTreeIds = store.ListTreeCatalogTrees()
            .Where(t => t.Category != TreeCategory.Species)
            .Select(t => t.TreeId)
            .ToList();
        if (sharedTreeIds.Count == 0) return Array.Empty<BoundDerivedAtom>();

        var trees = store.LoadTreeCatalog(sharedTreeIds);
        var classified = store.LoadAndClassifyTreeState(AllocationScope.Commander, scopeKey);
        var owned = TreeStateReconciler.LiveOnly(classified);
        var allocation = store.LoadAllocation(AllocationScope.Commander, scopeKey);

        var baseByTree = new Dictionary<string, long>(StringComparer.Ordinal);
        var stanceGroupByTree = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            if (!TryParseAptitudeGate(tree.Tree.GateQuantity, out var aptitudeId)) continue;
            baseByTree[tree.Tree.TreeId] = allocation.PointsAt(AllocationScope.Commander, aptitudeId);
            stanceGroupByTree[tree.Tree.TreeId] = AptitudeCatalog.Get(aptitudeId).Posture.ToString();
        }

        var selfSpent = TreeNodeSet.SelfSpent(owned);
        var hNodesMilli = Concentration.HerfindahlMilli(selfSpent.Values.Select(s => s.NodeCount).ToList());
        var hSoulsMilli = Concentration.HerfindahlMilli(selfSpent.Values.Select(s => s.SoulLevels).ToList());
        var hMilli = Concentration.BlendMilli(hNodesMilli, hSoulsMilli, tuning.Concentration.WMilli);
        var focusMilli = Concentration.FmaxAppliedMilli(hMilli, tuning.Concentration.FmaxMilli);

        var theta = (long)powerIndex.ActorIndex(new StatContext { PlayerId = playerId });
        var powerTuning = PowerTuningHub.Tuning;
        var result = new List<BoundDerivedAtom>();

        foreach (var tree in trees)
        {
            var gatePoints = CrossUnlock.Gate(tree.Tree.TreeId, baseByTree, stanceGroupByTree);
            var tierReached = TierGate.Reached(gatePoints, tree.Tree.Tiers, tuning.TierLadder.ReqScalePoints);
            var ownedInThisTree = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in tree.Nodes)
                if (owned.ContainsKey(node.NodeId))
                    ownedInThisTree.Add(node.NodeId);

            result.AddRange(PassiveTreeAtoms.BoundAtomsFor(
                tree, ownedInThisTree, tierReached, theta, powerTuning, focusMilli));
        }

        return result;
    }

    static bool TryParseAptitudeGate(string gateQuantity, out string aptitudeId)
    {
        var match = AptitudeGatePattern.Match(gateQuantity ?? "");
        if (match.Success && AptitudeCatalog.IsAptitudeId(match.Groups["id"].Value))
        {
            aptitudeId = match.Groups["id"].Value;
            return true;
        }
        aptitudeId = "";
        return false;
    }
}
