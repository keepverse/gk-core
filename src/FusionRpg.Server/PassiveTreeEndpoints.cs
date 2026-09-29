using System.Linq;
using System.Text.RegularExpressions;
using FusionRpg.Contracts;
using FusionRpg.Core.PassiveTree;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;

namespace FusionRpg.Server;

/// <summary>
/// passive-tree-todo.md I2 — "the wire": GET returns the resolve report, POST takes one whole
/// allocation, never a per-node call (spec-tree-surface.md §10, §12). Copies `AptitudeEndpoints.cs`'s
/// own shape verbatim (GET-state / POST-allocate / broadcast-to-both-groups) rather than inventing a
/// second one — the todo names that file as the exact pattern to follow.
///
/// <para><b>Scope, stated rather than assumed.</b> This endpoint resolves the SHARED corpus only (the
/// 42 non-species trees, D51 2026-09-06: 24 statuses not 21, was 39) — a species bloodline is Level 0b's own pinned-to-the-creature read
/// (spec-tree-surface.md §3: "never enters a browse"), a different task (I5) with a different key
/// shape (per-creature, not per-player). Loading all 840 species trees (33,600 nodes) into one
/// player-level response would also be exactly the un-windowed volume defect §3's own callout warns
/// against for `CreaturesPage.tsx`. `Commander` scope only, matching `AptitudeEndpoints`'s own stated
/// scope decision for the same reason: the other three scopes need a specimen picker this surface
/// does not have yet.</para>
///
/// <para><b>Gate-quantity resolution, the one real ambiguity this task closes with a stated default.</b>
/// Of the 42 shared trees (D51, 2026-09-06: 24 statuses not 21, was 39), only the 12 Primary ones have
/// a `TreeRecord` in the catalog today — their `gateQuantity` reads `"aptitude.&lt;Id&gt;@Commander"`,
/// resolved against this player's own `AptitudeAllocation`. The other 30 (`element_mastery`,
/// `status_applied.&lt;id&gt;`) never reach this loop yet at all — `passive-tree-todo.md` task J1's
/// missing plan-emission factory functions mean they have no generated content and therefore no
/// catalog row, gate state notwithstanding.</para>
///
/// <para><b>Fixed 2026-09-07 (J1's own acceptance bullet) — kept as history, not rewritten.</b> This
/// paragraph originally said the 30 non-Primary trees "have no points-producing pipeline yet." That
/// stopped being true 2026-09-06 (task G6): `ElementMasterySource`/`StatusAppliedSource` are real,
/// live-probed producers today. <see cref="AptitudeGatePattern"/>/<c>TryParseAptitudeGate</c> were
/// never taught to recognize their gate-quantity shapes, though — only
/// <c>aptitude.&lt;Id&gt;@Commander</c> resolved `Wired`, which would have rendered every one of those
/// 30 trees as `GateState = Unproduced` — wrongly, since their real gate is already wired — the moment
/// J1 shipped their content. Fixed by <see cref="IsWiredGateQuantity"/> below: a SEPARATE function from
/// <c>TryParseAptitudeGate</c>, deliberately — the cross-unlock credit loop above (`baseByTree`/
/// `stanceGroupByTree`) calls `AptitudeCatalog.Get(aptitudeId)` on `TryParseAptitudeGate`'s own output,
/// which would throw for an element/status id; `GateState` only needs a yes/no shape check, never the
/// parsed id, so it gets its own function rather than widening one whose callers assume an aptitude.</para>
/// </summary>
public static class PassiveTreeEndpoints
{
    // "aptitude.Might@Commander" -- the only gate-quantity shape THIS ENDPOINT recognizes as wired
    // (12 Primary paths, Commander scope). NOT the only shape with a real producer any more --
    // element_mastery/status_applied have had one since G6 (2026-09-06) -- this pattern was simply
    // never extended to them, tracked as J1's own acceptance bullet (see the class doc comment above).
    // A well-formed "aptitude.X@CreatureType" also resolves Unproduced here, deliberately: the other
    // three scopes need a specimen picker this surface does not have yet.
    static readonly Regex AptitudeGatePattern = new(@"^aptitude\.(?<id>[A-Za-z]+)@Commander$", RegexOptions.Compiled);

    // J1's own acceptance bullet (passive-tree-todo.md): the other two real, live-probed gate-quantity
    // shapes (task G6) — element_mastery's own id set is the 6-member ElementRoster (no @Scope collision
    // risk since "@Aspect" is a literal suffix, not a captured group); status_applied's id can itself
    // contain a dot (e.g. "nerve.unsettled") and no @Scope suffix at all (D35's own rule, deliberately
    // outside AllocationScope) -- captured permissively as ".+" and validated for real against
    // StatusCategoryRegistry, the same "narrow regex + real registry check" shape AptitudeGatePattern
    // already uses above.
    static readonly Regex ElementMasteryGatePattern = new(@"^element_mastery\.(?<id>[a-z]+)@Aspect$", RegexOptions.Compiled);
    static readonly Regex StatusAppliedGatePattern = new(@"^status_applied\.(?<id>.+)$", RegexOptions.Compiled);

    public static void MapPassiveTree(this WebApplication app)
    {
        var g = app.MapGroup("/api/passive-tree");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store, IPowerIndexProvider powerIndex) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(ProjectState(store, powerIndex, playerId));
        });

        g.MapPost("/allocate", (AllocateTreeNodesRequest body, RpgStore store, IPowerIndexProvider powerIndex, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (body.Nodes is null) return Results.BadRequest(new { reason = "nodes.missing" });
            foreach (var soulLevel in body.Nodes.Values)
                if (soulLevel < 0)
                    return Results.BadRequest(new { reason = "nodes.negativeSoulLevel" });

            // One whole-allocation write, exactly `SaveTreeNodeState`'s own full delete-then-insert
            // contract (spec-tree-surface.md §4 rule 3) -- never a per-node endpoint call.
            store.SaveTreeNodeState(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(pid), body.Nodes);

            _ = BroadcastBestEffort(hub, pid);
            // lawn LW1.5 (spec-actor-liveness-refresh.md): the tree spend's own liveness invalidation,
            // riding the ONE shared `AptitudesUpdated` transport rather than opening a second channel.
            // Scope "commander" is the player-scoped value the transport already has — a tree spend
            // moves the PLAYER's own tree atoms, not one specimen's, so it is not "unique".
            _ = AptitudeEndpoints.BroadcastBestEffort(hub, new AptitudeEndpoints.AptitudesUpdatedDto(
                pid, "commander", null, null, Kind: LivenessInvalidationWire.Tree));
            return Results.Ok(ProjectState(store, powerIndex, pid));
        });

        // I8's follow-up (spec-tree-surface.md §7.2 part 5) -- "the draft preview reports what a
        // change would close." Runs the draft's own hypothetical node set plus a hypothetical
        // aptitude-points delta through the SAME resolution ProjectState already gives the committed
        // GET (CrossUnlock/TierGate/TreeResolveReport, unchanged), and returns the same
        // PassiveTreeStateDto shape -- NEVER persisted (no SaveTreeNodeState, no SaveAllocation call
        // anywhere on this path). A client diffs this response against its own already-fetched
        // committed GET to find what newly opens or closes; that diff is a plain comparison of two
        // already-resolved server outputs, not a re-derivation of any gating math.
        g.MapPost("/{playerId:long}/preview", (long playerId, PreviewTreeStateRequest body, RpgStore store, IPowerIndexProvider powerIndex) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (body.Nodes is null) return Results.BadRequest(new { reason = "nodes.missing" });
            foreach (var soulLevel in body.Nodes.Values)
                if (soulLevel < 0)
                    return Results.BadRequest(new { reason = "nodes.negativeSoulLevel" });

            var scopeKey = AptitudeEndpoints.ScopeKey(playerId);
            var committedAllocation = store.LoadAllocation(AllocationScope.Commander, scopeKey);

            // Validated up front, against the REAL committed allocation -- never against a partially
            // applied delta -- so a bad request never burns the catalog/resolve work below.
            var aptitudeDeltaById = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var (aptitudeId, delta) in body.AptitudeDelta ?? new Dictionary<string, long>())
            {
                if (!AptitudeCatalog.IsAptitudeId(aptitudeId))
                    return Results.BadRequest(new { reason = "aptitudeDelta.unknownId", aptitudeId });

                var current = committedAllocation.PointsAt(AllocationScope.Commander, aptitudeId);
                long hypothetical;
                checked { hypothetical = current + delta; }
                if (hypothetical < 0)
                    // Aptitude points cannot go negative -- a domain bound, not a progression cap
                    // (PS-8 governs ceilings, not "you asked to remove more than you have"). Refused
                    // rather than clamped: clamping would silently preview a DIFFERENT delta than the
                    // one requested.
                    return Results.BadRequest(new { reason = "aptitudeDelta.wouldGoNegative", aptitudeId, current, delta });

                aptitudeDeltaById[aptitudeId] = delta;
            }

            return Results.Ok(ProjectState(store, powerIndex, playerId, ownedOverride: body.Nodes, aptitudeDeltaById: aptitudeDeltaById));
        });

        // lawn-tree-hydrate (T13) -- the Injector has no SQL store, so it cannot call
        // TreeBoundAtoms.ForPlayer itself; this is the one HTTP round trip that lets it fan the SAME
        // commander-scope shared-tree atoms the Server sheet already resolves into its own Hot
        // ActorHub, mirroring RpgClient.RefreshCommanderAllocationAsync's own shape (GET at session
        // start + on a SignalR reload broadcast, never a per-frame poll).
        g.MapGet("/bound-atoms/{playerId:long}", (long playerId, RpgStore store, IPowerIndexProvider powerIndex) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            var atoms = FusionRpg.Server.TreeBoundAtoms.ForPlayer(store, powerIndex, playerId)
                .Select(a => new BoundDerivedAtomDto
                {
                    Channel = a.Channel,
                    Op = a.Op.ToString(),
                    Amount = a.Amount,
                    SourceId = a.SourceId
                })
                .ToList();
            return Results.Ok(atoms);
        });
    }

    static async Task BroadcastBestEffort(IHubContext<RpgHub> hub, long playerId)
    {
        // Both groups -- the exact mechanism AptitudeEndpoints.cs:76-89 already ships and the todo
        // names by line number: a WebGroup-only send left an injector-side cache stale until its next
        // reconnect (found live, 2026-08-30). A tree allocation reaches the same two listeners.
        try { await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("PassiveTreeUpdated", new { playerId }); }
        catch { /* best-effort; the allocation is durable and the next GET reflects it */ }
        try { await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("PassiveTreeUpdated", new { playerId }); }
        catch { /* best-effort; the injector re-syncs at its own next session start regardless */ }
    }

    /// <summary><paramref name="ownedOverride"/>/<paramref name="aptitudeDeltaById"/> are the I8
    /// preview's own hooks (spec-tree-surface.md §7.2 part 5) -- both `null` (the GET/allocate callers
    /// below) reproduces today's committed projection byte-for-byte; either one supplied runs the
    /// EXACT SAME resolution (CrossUnlock/TierGate/TreeResolveReport, untouched below) over a
    /// hypothetical input instead of the stored one, and nothing on this path ever persists.</summary>
    static PassiveTreeStateDto ProjectState(
        RpgStore store, IPowerIndexProvider powerIndex, long playerId,
        IReadOnlyDictionary<string, long>? ownedOverride = null,
        IReadOnlyDictionary<string, long>? aptitudeDeltaById = null)
    {
        var tuning = PassiveTreeTuningHub.Tuning;
        var scopeKey = AptitudeEndpoints.ScopeKey(playerId);

        // §3: species trees are Level 0b's own pinned read, never a member of this general list --
        // filter to the shared corpus before paying for the heavier node/atom queries.
        var sharedTreeIds = store.ListTreeCatalogTrees()
            .Where(t => t.Category != TreeCategory.Species)
            .Select(t => t.TreeId)
            .ToList();
        var trees = store.LoadTreeCatalog(sharedTreeIds);

        // R3: retired/unknown node ids contribute zero and cost nothing to hold -- resolve reads the
        // LIVE-only projection, never the raw owned dictionary (TreeStateReconciler's own contract).
        // I8 preview: a supplied draft is classified fresh (read-only, never persisted) instead of the
        // stored row set -- same classify call, same live/retired/unknown rule either way.
        var classified = ownedOverride is not null
            ? store.ClassifyTreeNodeState(ownedOverride)
            : store.LoadAndClassifyTreeState(AllocationScope.Commander, scopeKey);
        var owned = TreeStateReconciler.LiveOnly(classified);

        var allocation = store.LoadAllocation(AllocationScope.Commander, scopeKey);

        // baseByTree/stanceGroupByTree cover every aptitude-gated tree in the shared corpus -- built
        // once, fed to CrossUnlock for every tree's own lender lookup (D28: "its whole posture comes
        // along for free"). A tree with no producer yet is simply absent from both maps, which is
        // exactly CrossUnlock's own documented "no stance group -> credit 0" default.
        var baseByTree = new Dictionary<string, long>(StringComparer.Ordinal);
        var stanceGroupByTree = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            if (!TryParseAptitudeGate(tree.Tree.GateQuantity, out var aptitudeId)) continue;
            var basePoints = allocation.PointsAt(AllocationScope.Commander, aptitudeId);
            // I8 preview: the hypothetical delta is added here, on the SAME base every committed
            // caller already reads -- the endpoint above has already proven this can never go
            // negative, so no second check is needed on this path.
            if (aptitudeDeltaById is not null && aptitudeDeltaById.TryGetValue(aptitudeId, out var delta))
                checked { basePoints += delta; }
            baseByTree[tree.Tree.TreeId] = basePoints;
            stanceGroupByTree[tree.Tree.TreeId] = AptitudeCatalog.Get(aptitudeId).Posture.ToString();
        }

        // H/F: computed ONCE over the actor's WHOLE allocation (every tree the actor owns anything in,
        // shared or species alike -- Concentration.cs's own contract is "the final allocation," not
        // "the shared corpus"), then handed to every tree's report unchanged (TreeResolveReport's own
        // "plain, already-resolved value" rule). TreeNodeSet parses treeId from the node id itself, so
        // this needs no species catalog load at all.
        var selfSpent = TreeNodeSet.SelfSpent(owned);
        var hNodesMilli = Concentration.HerfindahlMilli(selfSpent.Values.Select(s => s.NodeCount).ToList());
        var hSoulsMilli = Concentration.HerfindahlMilli(selfSpent.Values.Select(s => s.SoulLevels).ToList());
        var hMilli = Concentration.BlendMilli(hNodesMilli, hSoulsMilli, tuning.Concentration.WMilli);
        var focusMilli = Concentration.FmaxAppliedMilli(hMilli, tuning.Concentration.FmaxMilli);

        var reports = new List<TreeResolveReportDto>(trees.Count);
        foreach (var tree in trees)
        {
            var treeId = tree.Tree.TreeId;
            var isWired = IsWiredGateQuantity(tree.Tree.GateQuantity);
            var gateState = isWired ? TreeGateState.Wired : TreeGateState.Unproduced;

            // gate(i) = base(i) + credit(i) -- "you have" on the tier row (§7.2), own contribution
            // plus at most one lender's, never a sum of lenders (CrossUnlock's own D28 rule).
            var gatePoints = CrossUnlock.Gate(treeId, baseByTree, stanceGroupByTree);
            var lenderTreeId = CrossUnlock.Lender(treeId, baseByTree, stanceGroupByTree);
            var tierReached = TierGate.Reached(gatePoints, tree.Tree.Tiers, tuning.TierLadder.ReqScalePoints);

            var ownedInThisTree = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in tree.Nodes)
                if (owned.ContainsKey(node.NodeId))
                    ownedInThisTree.Add(node.NodeId);

            var report = TreeResolveReport.Build(tree, gateState, tierReached, gatePoints,
                ownedInThisTree, lenderTreeId, hMilli, focusMilli);

            reports.Add(new TreeResolveReportDto
            {
                TreeId = report.TreeId,
                Category = tree.Tree.Category.ToString(),
                Name = tree.Tree.Name,
                Description = tree.Tree.Description,
                GateState = report.GateState == TreeGateState.Wired ? "wired" : "unproduced",
                TierReached = report.TierReached,
                Tiers = tree.Tree.Tiers,
                AptitudePoints = report.AptitudePoints,
                // I8: this tree's own base spend, before any stance-mate's credit -- straight off the
                // same `baseByTree` map `CrossUnlock.Gate`/`Lender` above already read, never a second
                // computation. Zero for a tree absent from the map (no aptitude-gate producer, §9.1).
                OwnAptitudePoints = baseByTree.GetValueOrDefault(treeId, 0),
                ContributingNodeIds = report.ContributingNodeIds.ToList(),
                InvalidNodeIds = report.InvalidNodeIds.ToList(),
                LenderTreeId = report.LenderTreeId,
                HerfindahlMilli = report.HerfindahlMilli,
                FocusMilli = report.FocusMilli,
                ExcludedNodes = report.ExcludedNodes.Select(e => new ExcludedNodeDto
                {
                    NodeId = e.NodeId,
                    Form = e.Form.ToString(),
                    WinnerNodeId = e.WinnerNodeId,
                    IsInert = e.IsInert
                }).ToList(),
                // I6: structural (branch, tier) identity for EVERY enabled node, not only owned ones --
                // TreeResolveReport itself only ever names owned nodes, which cannot describe an
                // available or locked cell nobody has bought yet. Retired nodes excluded, same as
                // TreeResolveReport.Build's own rule.
                Nodes = tree.Nodes
                    .Where(n => n.Enabled)
                    .OrderBy(n => n.Tier)
                    .ThenBy(n => n.Branch)
                    .ThenBy(n => n.NodeId, StringComparer.Ordinal)
                    .Select(n => new TreeNodeSummaryDto
                    {
                        NodeId = n.NodeId,
                        Branch = n.Branch.ToString(),
                        Tier = n.Tier,
                        NodeClass = n.NodeClass.ToString(),
                        Name = n.Name,
                        Flavor = n.Flavor
                    }).ToList()
            });
        }

        // Skill-points wallet (task I3, spec-tree-surface.md §4.1) -- the "buy a trait" currency,
        // distinct from the aptitude points above ("open a tier") and souls ("deepen a trait", read
        // separately from /api/souls). `PointBudget.SkillPointsFor` (D34) and `TreeUnlockCost` (D25/
        // D36) are both pre-existing, pre-tested pure functions this endpoint is simply the first
        // production caller of -- an inert path being wired, not new math. `owned.Count` is exactly
        // OwnershipRules' "count of valid owned nodes" (LiveOnly already excludes retired/invalid
        // rows), and it spans every Commander-scope tree, matching `NodesOnOtherTreesCount = true`.
        var theta = (long)powerIndex.ActorIndex(new StatContext { PlayerId = playerId });
        var skillBudget = PointBudget.SkillPointsFor(AllocationScope.Commander, theta, AptitudeTuningHub.Tuning);
        var skillSpent = TreeUnlockCost.Cumulative(owned.Count, tuning.UnlockCost.FirstPoints, tuning.UnlockCost.StepPoints);
        var skillAvailable = TreeUnlockCost.Available(skillBudget, owned.Count, tuning.UnlockCost.FirstPoints, tuning.UnlockCost.StepPoints);

        return new PassiveTreeStateDto
        {
            PlayerId = playerId,
            CatalogRevision = store.GetTreeCatalogRevision(),
            SoulLevelByNodeId = new Dictionary<string, long>(owned, StringComparer.Ordinal),
            Trees = reports,
            SkillPointsBudget = skillBudget,
            SkillPointsSpent = skillSpent,
            SkillPointsAvailable = skillAvailable,
            TierReqScalePoints = tuning.TierLadder.ReqScalePoints,
            // I8 (spec-tree-surface.md §5.2): the (first, step) pair itself, so a client-side Plan
            // preview can reproduce TreeUnlockCost.Cumulative exactly for a HYPOTHETICAL owned count
            // -- the two already-wired totals above are a single (count, cumulative) sample and cannot
            // be inverted back into (first, step) in general.
            UnlockCostFirstPoints = tuning.UnlockCost.FirstPoints,
            UnlockCostStepPoints = tuning.UnlockCost.StepPoints,
            // I9 (spec-tree-surface.md §6): the concentration dial itself, so a DRAFT preview can
            // mirror H/F exactly for a hypothetical allocation -- the committed reading above never
            // uses these, it is `hMilli`/`focusMilli` per tree, computed once, already.
            ConcentrationFmaxMilli = tuning.Concentration.FmaxMilli,
            ConcentrationWMilli = tuning.Concentration.WMilli
        };
    }

    static bool TryParseAptitudeGate(string gateQuantity, out string aptitudeId)
    {
        var match = AptitudeGatePattern.Match(gateQuantity);
        if (match.Success && AptitudeCatalog.IsAptitudeId(match.Groups["id"].Value))
        {
            aptitudeId = match.Groups["id"].Value;
            return true;
        }
        aptitudeId = "";
        return false;
    }

    /// <summary>J1's own acceptance bullet: does <paramref name="gateQuantity"/> match ANY of the three
    /// real, live-probed gate-quantity shapes (`aptitude.&lt;Id&gt;@Commander`,
    /// `element_mastery.&lt;id&gt;@Aspect`, `status_applied.&lt;id&gt;`) -- a pure shape+membership
    /// check for <c>GateState</c> alone. Deliberately NOT <c>TryParseAptitudeGate</c>: that function's
    /// only caller (the cross-unlock credit loop above) calls <c>AptitudeCatalog.Get</c> on its parsed
    /// id, which requires the id to actually BE an aptitude -- widening it to also accept an element or
    /// status id would hand that loop a value its own next line cannot handle.</summary>
    static bool IsWiredGateQuantity(string gateQuantity)
    {
        if (TryParseAptitudeGate(gateQuantity, out _)) return true;

        var elementMatch = ElementMasteryGatePattern.Match(gateQuantity);
        if (elementMatch.Success && ElementRoster.TryParse(elementMatch.Groups["id"].Value, out _))
            return true;

        var statusMatch = StatusAppliedGatePattern.Match(gateQuantity);
        if (statusMatch.Success && StatusCategoryRegistry.TryGetCategory(statusMatch.Groups["id"].Value, out _))
            return true;

        return false;
    }
}
