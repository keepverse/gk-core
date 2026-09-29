using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Display;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.PassiveTree.Binding;

/// <summary>
/// Ties <see cref="AffixComposer"/> + <see cref="ChannelUnits"/> + <see cref="ChannelLegality"/> +
/// <see cref="ChannelAnchor"/> + <see cref="CoefficientBinder"/> into one whole-node bind, and one
/// whole-tree run (task D2). B4 explicitly deferred this: "the CLI wrapper and full-tree
/// orchestration are mechanical composition of what's already proven … left for a follow-up pass."
///
/// <para><b>Scoped honestly to the one case this spec has a complete, worked formula for</b>
/// (§3.3-§3.4): a <see cref="ChannelLegality.Verdict.LadderScaled"/> (`GameUnits`-family) channel
/// anchored through <see cref="ChannelAnchor"/>'s atk/defense families. Two things are composed but
/// deliberately NOT priced by this pass, matching B4's own stated boundary rather than inventing a
/// formula this spec never worked out with a number:</para>
/// <list type="bullet">
/// <item>a resolved atom with no channel/op (a mechanism-class atom, e.g. `status.apply`) — B4's own
/// finding: "resolved successfully but not priced by this formula";</item>
/// <item>a resolved, LEGAL (non-refused) atom whose <c>UnitClass</c> verdict is
/// <see cref="ChannelLegality.Verdict.FlatPermilleOnly"/> or <see cref="ChannelLegality.Verdict.ThetaLinear"/>,
/// or a <see cref="ChannelLegality.Verdict.LadderScaled"/> channel <see cref="ChannelAnchor"/> cannot
/// anchor (only `combat.power.*`/`combat.defense.*`/`atk`/`defense` are anchored today, §3.6) — never
/// refused (that would contradict §4.1, which says these ARE legal binder targets), never priced by
/// a guessed formula either. Composing without pricing is the honest middle: the node's other atoms
/// (if any) still bind, and no wrong number is ever stored.</item>
/// </list>
/// </summary>
public static class TreeBinderRun
{
    /// <summary>Binds ONE node. Never reads <c>input.ExclusionForm</c> — §7.3's rule that an
    /// excluded node, nullification included, binds identically to an unexcluded one. That is a
    /// property of this method's SOURCE (proved by
    /// <c>TreeBinderRunTests.BindNode_source_never_reads_ExclusionForm</c>), not merely its output on
    /// one fixture.</summary>
    public static BoundNode BindNode(
        BindInputNode input,
        IReadOnlyDictionary<string, AffixRow> affixesById,
        IReadOnlyDictionary<string, AtomRow> atomsById,
        PowerTuning powerTuning)
    {
        var resolved = AffixComposer.Resolve(input.AffixIds, affixesById, atomsById);

        var atoms = new List<NodeAtom>();
        var statusAtoms = new List<NodeStatusAtom>();
        foreach (var r in resolved)
        {
            // Mechanism-class `status.apply` rows ride the sibling list verbatim (MC-1): status id,
            // duration, level, and grant chance straight from the row — no channel invented, no op
            // invented, no coefficient priced. Priced and executed elsewhere (mechanism-wiring).
            if (string.Equals(r.KindId, "status.apply", StringComparison.Ordinal))
            {
                statusAtoms.Add(ParseStatusAtom(input.NodeId, r));
                continue;
            }

            if (string.IsNullOrEmpty(r.ChannelId) || string.IsNullOrEmpty(r.Op))
                continue; // mechanism-class atom (no channel/op) -- resolved, not priced (B4).

            var unitClass = ChannelUnits.For(r.ChannelId);
            if (unitClass is null)
                continue; // no registered UnitClass for this channel -- composed, not priced; this
                          // is a vocabulary gap for whoever owns the channel, not a bind-time refusal.

            if (ChannelLegality.VerdictFor(unitClass.Value) != ChannelLegality.Verdict.LadderScaled)
                continue; // legal per §4.1, but this pass has no worked bake-time coefficient formula
                          // for the other three verdicts yet (§3.6 scopes channelAnchorMilli to the
                          // atk/defense families) -- composed, not priced. A stated scope gap, never
                          // a silently wrong price.

            long anchor;
            try { anchor = ChannelAnchor.ForChannel(r.ChannelId, powerTuning); }
            catch (ChannelAnchor.UnknownChannelPin) { continue; } // same scope gap, different door

            var op = ParseOp(r.Op, r.KindId, input.NodeId, r.ChannelId);
            var kMicro = CoefficientBinder.Bind(input.TreeShareMilli, input.TreeBudgetMilli,
                input.BudgetShareMilli, anchor, input.Branches);

            var atom = new NodeAtom(r.KindId, AttachPoint.Stat, r.ChannelId, op, r.Trigger,
                r.WhenJson, kMicro, ScaleAxis.PTheta, unitClass.Value);

            ChannelLegality.CheckBind(atom); // the LowerIsBetter / axis-agreement check -- the same
                                              // rule every other caller of this class already goes
                                              // through, never re-implemented here.
            atoms.Add(atom);
        }

        return new BoundNode(input.NodeId, atoms, statusAtoms);
    }

    /// <summary>Carries one resolved <c>status.apply</c> row into a <see cref="NodeStatusAtom"/> —
    /// verbatim, never priced. Structural validation only (non-empty status, positive duration,
    /// sane level, chance in range); the status vocabulary itself is validated where statuses
    /// execute (FA2), not here. Anything malformed is a named <see cref="BindRefusal"/>, never a
    /// silent drop and never a guessed default.</summary>
    static NodeStatusAtom ParseStatusAtom(string nodeId, ResolvedAtom r)
    {
        string statusId, durationRaw;
        long level = 1;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.ParamsJson);
            var root = doc.RootElement;
            statusId = root.TryGetProperty("status", out var sEl) ? sEl.GetString() ?? "" : "";
            durationRaw = root.TryGetProperty("duration", out var dEl) ? dEl.GetRawText() : "";
            if (root.TryGetProperty("level", out var lEl) && lEl.TryGetInt64(out var lv)) level = lv;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new BindRefusal($"node '{nodeId}': status atom '{r.AtomId}' has unparseable params JSON: {ex.Message}");
        }

        if (string.IsNullOrEmpty(statusId))
            throw new BindRefusal($"node '{nodeId}': status atom '{r.AtomId}' names no status (params.status is required)");
        // Rows carry SECONDS float (FA2; hand rows carry 2-5); the carried shape stores whole ms.
        if (!double.TryParse(durationRaw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var durationS) || durationS <= 0)
            throw new BindRefusal($"node '{nodeId}': status atom '{r.AtomId}' has no positive duration seconds (got '{durationRaw}')");
        var durationMs = (long)Math.Round(durationS * 1000.0);

        long chancePermille = 1000; // the downstream default (AtomCompiler) — read, never invented
        if (!string.IsNullOrEmpty(r.WhenJson))
        {
            try
            {
                using var whenDoc = System.Text.Json.JsonDocument.Parse(r.WhenJson);
                if (whenDoc.RootElement.TryGetProperty("chance", out var chEl) && chEl.TryGetInt64(out var ch))
                    chancePermille = ch;
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new BindRefusal($"node '{nodeId}': status atom '{r.AtomId}' has unparseable whenJson: {ex.Message}");
            }
        }
        if (chancePermille < 0 || chancePermille > 1000)
            throw new BindRefusal($"node '{nodeId}': status atom '{r.AtomId}' chance {chancePermille}‰ is outside 0..1000");

        return new NodeStatusAtom(statusId, durationMs, checked((int)level), chancePermille, r.Trigger, r.WhenJson);
    }

    /// <summary>Parses the wire op string into the typed enum, refusing anything the vocabulary does
    /// not know. `more` IS known (task P4.2 added it: `stat.modify` supports it — `AtomKindRegistry
    /// .cs:517`), so an unknown op here means a genuinely bad string, not a derived-side `More`.
    /// A derived-side `More` is a different, still-loud refusal: it names §6 M3 and is raised by
    /// <see cref="ChannelLegality.CheckBind"/> at the bottom of the loop, the same check the catalog
    /// loader applies at load — so the rule lives in one place rather than a third copy here.</summary>
    static NodeAtomOp ParseOp(string op, string kindId, string nodeId, string channelId) =>
        Enum.TryParse<NodeAtomOp>(op, ignoreCase: true, out var parsed)
            ? parsed
            : throw new BindRefusal($"node '{nodeId}': channel '{channelId}' has unknown op '{op}' " +
                "on kind '" + kindId + "' (Flat|Increased|More|Replace|Flag)");

    /// <summary>
    /// Binds a whole tree's worth of nodes, batching every refusal into one
    /// <see cref="BinderRunReport"/> rather than throwing on the first (R5's own load-path discipline,
    /// extended here from the catalog loader to this module). §7.2 item 2: a refusal names the
    /// budget it did not spend. §7.2 item 3: the run verdict is `Fail`, never a silent partial
    /// success -- unless the slot is <see cref="BindInputNode.DeliberateHole"/>-flagged, in which case
    /// the unspent budget is still named but the run may still `Pass` (§7.2's "consequence for
    /// tree-plan", read here rather than invented; see <see cref="BinderRunReport.From"/>).
    /// </summary>
    public static BinderRunReport BindTree(
        IReadOnlyList<BindInputNode> nodes,
        IReadOnlyDictionary<string, AffixRow> affixesById,
        IReadOnlyDictionary<string, AtomRow> atomsById,
        PowerTuning powerTuning)
    {
        var bound = new List<BoundNode>();
        var refused = new List<RefusedSlot>();
        foreach (var node in nodes)
        {
            try
            {
                bound.Add(BindNode(node, affixesById, atomsById, powerTuning));
            }
            catch (BindRefusal ex)
            {
                refused.Add(new RefusedSlot(node.NodeId, ex.Message, node.BudgetShareMilli, node.DeliberateHole));
            }
        }
        return BinderRunReport.From(bound, refused);
    }
}
