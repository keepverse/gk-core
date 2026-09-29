using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.PassiveTree.Resolve;

/// <summary>
/// The third source of the shape `TraitAtomSource`/`EquipAtomSource` already ship (spec-tree-resolve.md
/// §2.1-2.2, task B6). Fans owned, gate-open nodes' `stat.derived` atoms into
/// <see cref="BoundDerivedAtom"/>s — the SAME shape `AtomDerivedSubsystem` already reads via its
/// `boundFor` delegate. No new subsystem, no new order band, no eviction of the existing three: this
/// is a PRODUCER composed into the existing fan-in, never a fourth registration.
///
/// <para>Source id convention: `tree.{treeId}.{nodeId}` — one row per node, so attribution reaches
/// the player through the existing `DerivedContributionBag` path with no second component.</para>
/// </summary>
public static class TreeAtomSource
{
    /// <summary>Produces one `BoundDerivedAtom` per live, `stat.derived`-kind atom on every node the
    /// actor owns AND whose tier is at or below the reached gate. `tree-resolve` decides only
    /// whether a node's atoms are LIVE right now (gate open, node enabled) — it does not dispatch,
    /// trigger, or execute anything (§2.3); mechanism-class atoms are `mechanism-wiring`'s to
    /// execute and are skipped here by construction (their kind is never `stat.derived`).
    ///
    /// <para>The kind's PRIMARY sibling (<c>stat.modify</c>) is skipped too, and
    /// <see cref="TryReadOp"/>'s doc is where the three measured bars are named: the shared fan-in's
    /// one reader throws on a primary channel id, <c>More</c> has no derived-side representation, and
    /// the kind is trigger-capable where a once-resolved <see cref="BoundDerivedAtom"/> is not. This
    /// method is a PRODUCER for the derived fan-in; it is not where a primary contribution is
    /// delivered, and widening it cannot make one (P11.1r).</para>
    ///
    /// <para><b>D7 — `fMilli` applies D4/D5/D8's concentration multiplier here, and ONLY here.</b>
    /// This is the seam spec-tree-resolve.md §5.3 names: "`F` multiplies every tree-derived
    /// contribution — magnitude and contest alike" (every <see cref="ScaleAxis"/> branch below), "and
    /// nothing else" — a caller passes `fMilli` computed once, upstream, from
    /// <see cref="Concentration.FmaxAppliedMilli"/> over THIS actor's whole allocation; nothing not
    /// produced by this function ever sees it, so traits, equipment and base stats are structurally
    /// unaffected. `fMilli` is bounded below by `1000` (`F &gt;= 1.000`, §5.1's proof) — the caller is
    /// expected to pass `Concentration.FmaxAppliedMilli`'s own output, never a raw, unvalidated
    /// `H`.</para>
    ///
    /// <para>The multiplier is computed ONCE, upstream of the per-atom loop
    /// (`fMilli / 1000.0`), and applied as the LAST step inside <see cref="ResolveAmount"/> (docs/architecture/numeric-types.md
    /// rule 4: divide once, last) — so `fMilli = 1000` yields a multiplier of EXACTLY `1.0`, and
    /// multiplying any finite `double` by exactly `1.0` is an IEEE 754 identity operation: `Fmax =
    /// 1000‰` therefore removes `F` byte-identically (§5.4, test 9) without deleting the multiply
    /// itself — the code path still runs, it just multiplies by a no-op.</para></summary>
    public static IReadOnlyList<BoundDerivedAtom> BoundAtomsFor(
        LoadedTree tree, IReadOnlySet<string> ownedNodeIds, int tierReached, long thetaNode, PowerTuning powerTuning,
        long fMilli)
    {
        if (fMilli < 1000)
            throw new ArgumentOutOfRangeException(nameof(fMilli), fMilli,
                "F is bounded below by 1.000 (1000 per-mille) -- see spec-tree-resolve.md §5.1's proof");

        var result = new List<BoundDerivedAtom>();
        var ladder = new PowerLadder(powerTuning);
        var fMultiplier = fMilli / 1000.0; // computed ONCE, upstream of the loop -- see the class doc

        foreach (var node in tree.Nodes)
        {
            if (!node.Enabled) continue; // R2: a retired node is disabled, never live again
            if (!ownedNodeIds.Contains(node.NodeId)) continue;
            if (node.Tier > tierReached) continue; // the gate this actor has not opened yet (D11/D12:
                                                    // a closed gate invalidates, never repairs)
            if (ExclusionResolver.Resolve(tree, node, ownedNodeIds) is not null) continue; // D14/D40:
                                                    // an excluded node contributes zero -- reroute,
                                                    // precedence and nullification alike. The report
                                                    // builder (TreeResolveReport.Build) calls the SAME
                                                    // resolver so contribution and report can never drift.

            foreach (var atom in node.Atoms)
            {
                if (!TryReadOp(atom, out var composerOp))
                    continue; // the same predicate the report uses: only a kind/op the shared
                              // derived fan-in can actually read is live here. `TryReadOp` says which,
                              // and it is a read-capacity question, not a preference -- see its doc.
                var amount = ResolveAmount(atom, ladder, thetaNode, fMultiplier);
                var sourceId = ContributionSourceIds.Tree(tree.Tree.TreeId, node.NodeId);
                result.Add(new BoundDerivedAtom(atom.ChannelId, composerOp, amount, sourceId));
            }
        }
        return result;
    }

    /// <summary>
    /// The single kind/op admission predicate for the tree-derived fan-in. Keeping it beside
    /// <see cref="BoundAtomsFor"/> prevents a second caller (notably <see cref="TreeResolveReport"/>)
    /// from calling a binder-emitted primary or mechanism atom "contributing" while this method
    /// silently drops it. The returned op is the exact operation consumed by
    /// <see cref="AtomDerivedSubsystem.TryParseOp"/>.
    ///
    /// <para><b>Why <c>stat.modify</c> is refused here, measured rather than asserted (P11.1r).</b>
    /// Dropping it is a READ-CAPACITY fact about the one existing reader, not a choice. Three
    /// independent bars, each verified against the code that would have to carry it:</para>
    /// <list type="number">
    /// <item><b>Channel vocabulary.</b> <c>stat.modify</c>'s <c>channel</c> param is declared against
    /// <c>AtomKindRegistry.PrimaryChannels</c> (<c>AtomKindRegistry.cs:501</c>) — the 23 primary
    /// channels, <c>StatChannels.All</c> — and the committed corpus names only those. The single
    /// reader behind this fan-in, <see cref="DerivedComposer.Compose"/>, calls
    /// <c>DerivedStatRegistry.ValidateChannel</c> on every modifier and <b>throws</b>
    /// <see cref="UnknownDerivedChannelException"/> on a non-derived id. Routing a tree
    /// <c>stat.modify</c> atom through here is therefore not a silent no-op: it is a throw on the
    /// lawn's per-hit resolve path, one actor per hit. <c>stat.derived</c>'s own <c>channel</c> param
    /// is declared against the derived vocabulary (<c>AtomKindRegistry.cs:531</c>), which is why only
    /// this kind reaches this fold.</item>
    /// <item><b>Ops.</b> <c>stat.modify</c>'s op set is <c>Flat|Increased|More</c>;
    /// <see cref="DerivedModifierOp"/> has no <c>More</c> (effect-atom/definitions.md §14's kind note),
    /// and <see cref="AtomDerivedSubsystem.TryParseOp"/> refuses it BY NAME rather than coercing it
    /// (coercion is how a wrong number ships looking correct). A third of the kind is
    /// unrepresentable, so even a lossless channel mapping would drop those atoms silently.</item>
    /// <item><b>Life cycle.</b> <c>stat.modify</c> is the one kind with <c>TriggerOptional: true</c>
    /// (<c>AtomKindRegistry.cs:496-523</c>) — it composes per fire through a sourced, revertible
    /// primary ledger, not as one resolved bound list. <see cref="BoundDerivedAtom"/> is a
    /// once-resolved amount and cannot express that.</item>
    /// </list>
    /// <para>So the primary route is a different carrier on the primary half of the stat system
    /// (<c>StatSystem</c>'s session bag / an <c>IStatModifierPlugin</c> on the lawn, the primary
    /// battle ledger in battle) — a design decision owned by <c>passive-tree-repair</c> P11.1r and its
    /// <c>mechanism-wiring</c> design pass, not something this producer can widen into. What this
    /// method owes instead is to be honest about the bar, and to refuse rather than coerce: a reader
    /// that cannot carry a <c>stat.modify</c> atom must SKIP it, never fold it into a derived channel
    /// under a name the sheet does not read.</para>
    /// </summary>
    internal static bool TryReadOp(NodeAtom atom, out DerivedModifierOp composerOp)
    {
        composerOp = default;
        if (!string.Equals(atom.KindId, "stat.derived", StringComparison.Ordinal))
            return false;

        return AtomDerivedSubsystem.TryParseOp(NodeAtomOpToWireString(atom.Op), out composerOp);
    }

    /// <summary>
    /// Whether a node has at least one atom this resolver can actually read. A node carrying only
    /// <c>stat.modify</c> or a carried mechanism/status atom is owned, but it is not a derived
    /// contribution until its owning route exists; the report must not imply otherwise. For
    /// <c>stat.modify</c> that route is <b>unbuilt</b> (the three bars named on
    /// <see cref="TryReadOp"/>) — this predicate must not be relaxed on the assumption that widening it
    /// would reach a channel the shared fan-in can read.
    /// </summary>
    internal static bool HasReadableAtom(NodeRecord node) =>
        node.Atoms.Any(atom => TryReadOp(atom, out _));

    /// <summary><paramref name="fMultiplier"/> (`fMilli / 1000.0`, computed once by the caller) is
    /// applied as the LAST step on every branch alike — "magnitude and contest alike" (§5.3) — never
    /// folded into an earlier term, so it can never change WHICH axis a channel reads, only scale the
    /// already-axis-correct result.</summary>
    static double ResolveAmount(NodeAtom atom, PowerLadder ladder, long thetaNode, double fMultiplier) => atom.ScaleAxis switch
    {
        // PS-3: magnitudes read P(Theta), contests read Theta, LINEARLY. Getting this branch wrong
        // is a SILENT failure -- the sheet number rises either way, only the multiplier's behavior
        // differs (spec-tree-resolve.md §5, PS-3).
        // No clamping: PowerLadder.Value itself throws PowerIndexOverflow past MaxIndex, and that
        // throw-never-wrap behavior is the correct one to let propagate (AGENTS.md rule 5).
        ScaleAxis.PTheta => (double)atom.KMicro * ladder.Value(checked((int)thetaNode)) / 1_000_000.0 * fMultiplier,
        ScaleAxis.Theta => (double)atom.KMicro * thetaNode / 1_000_000.0 * fMultiplier,
        ScaleAxis.FlatPermille => (double)atom.KMicro / 1_000_000.0 * fMultiplier,
        _ => throw new ArgumentOutOfRangeException(nameof(atom), atom.ScaleAxis, "unknown scaleAxis"),
    };

    static string NodeAtomOpToWireString(NodeAtomOp op) => op switch
    {
        NodeAtomOp.Flat => "flat",
        NodeAtomOp.Increased => "increased",
        // P4.2: `More` is a real op — and it belongs to the PRIMARY kind. `stat.modify`'s op set is
        // `Flat|Increased|More` (`AtomKindRegistry`'s kind note), while the derived side refuses it by
        // name at load, at bind (§6 M3) and again in `AtomDerivedSubsystem.TryParseOp`. This arm is
        // therefore currently UNREACHABLE: `TryReadOp` admits only `stat.derived`, where `More` is
        // already refused upstream. It is written out rather than left to the `_ => ""` default for
        // P4.2's reason — every enum member is named, so adding one without a mapping fails
        // `Shared_projection_maps_every_NodeAtomOp_including_More` instead of silently mapping to an
        // empty op string. A future widening of the kind filter would therefore hit the parser's
        // NAMED refusal for `More` and skip the atom, which is the same outcome as the channel bar
        // refusing the whole atom: refused and reported, never composed into something it is not.
        NodeAtomOp.More => "more",
        NodeAtomOp.Replace => "replace",
        NodeAtomOp.Flag => "flag",
        _ => "",
    };
}
