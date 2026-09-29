using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.PassiveTree.Binding;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// F8 (passive-tree-todo.md, opened 2026-09-07 by F7's own investigation): rebuilds tree power on the
/// real <see cref="Core.PassiveTree.Resolve.TreeAtomSource"/> shape -- independent per-channel
/// modifiers, never folded back through <see cref="AptitudeAllocation"/>.
///
/// <para><b>Why this exists, stated once rather than re-argued at every call site.</b> F7 found
/// <see cref="TreeModel.Resolve"/>'s fold-back (`effective += AptitudeAllocation.Single(...)`) routes
/// tree power through <see cref="AptitudeAllocation.Share"/> -- `Total(id)/GrandTotal()`, a ZERO-SUM
/// ratio across every aptitude the actor holds. Growing one tree's contribution therefore dilutes every
/// OTHER tree's own combat contribution too, an artifact the real pipeline
/// (<see cref="Core.PassiveTree.Resolve.TreeAtomSource.BoundAtomsFor"/>, which writes a flat/increased
/// modifier straight to one channel, independent of any other tree) has no analog for -- and the
/// coupling is asymmetric between concentrated and spread allocations, the exact axis
/// <see cref="TreeModel.ConcentrationSweep"/>/<see cref="TreeModel.CrossUnlockSweep"/> measure.</para>
///
/// <para><b>The fix, mirroring an already-proven pattern in this same project.</b>
/// <see cref="Erosion.MeasureMechanismPair"/> already demonstrates the right shape for injecting a
/// non-aptitude channel contribution into this harness's battle-sim: build a plain
/// <see cref="BattleActorSetup"/> from an <see cref="AptitudeAllocation"/>
/// (<see cref="SquadMatch.ToActorSetup"/>), then <c>Concat</c> extra <see cref="BattleChannelMod"/>s onto
/// it -- never touching <see cref="RosterEntry"/>, <see cref="Screening"/> or
/// <see cref="SquadMatch.ToBattleSetup"/>, so F1-F6's existing measurement pipeline and its 178 tests
/// are structurally unaffected by this class's addition.</para>
///
/// <para><b>The representative-channel approximation, stated rather than hidden.</b> This harness never
/// reads the generated catalog (spec-squad-harness.md §13's "Never" list), so it cannot read a real
/// node's own <c>budgetShareMilli</c>. Exactly like <see cref="TreeModel.NodesPerTier"/> is already a
/// named structural average of D29's shape, this class approximates every owned node as spending an
/// EQUAL share of `treeBudgetMilli` (D15: 1000‰ per tree) across the ~40 authored nodes, and converts
/// that into a coefficient on ONE representative channel (<see cref="RepresentativeChannel"/>) via the
/// real, unmodified <see cref="CoefficientBinder.Bind"/> and <see cref="ChannelAnchor.ForChannel"/> --
/// never a re-derivation of either.</para>
/// </summary>
public static class TreeChannelModel
{
    /// <summary>`combat.power.omni` -- a real, registered `combat.power.*` channel (anchors against the
    /// ATK pin, spec-tree-binder.md §3.3's own worked-example family), already used elsewhere in this
    /// program as the "one representative channel" convention (spec-tree-binder.md §3.4).</summary>
    public const string RepresentativeChannel = "combat.power.omni";

    /// <summary>D29's own branch count -- the same constant <see cref="CoefficientBinder.Bind"/>'s real
    /// callers (task B4) divide by.</summary>
    public const long Branches = 2;

    /// <summary>D29's own shape restated as this class's structural approximation of "how many nodes
    /// share a tree's `treeBudgetMilli`" -- <see cref="TreeModel.AuthoredTierCount"/> ×
    /// <see cref="TreeModel.NodesPerTier"/>, the same figure <see cref="TreeModel"/>'s own doc already
    /// names as D29's average (10 × 4 = 40).</summary>
    public const long ApproxNodesPerTree = TreeModel.AuthoredTierCount * TreeModel.NodesPerTier;

    /// <summary>An equal split of `treeBudgetMilli` (1000‰, D15) across <see cref="ApproxNodesPerTree"/>
    /// nodes -- 25‰ per node. A structural approximation, named as one: the real catalog's own
    /// `budgetShareMilli` values are NOT uniform (spec-tree-binder.md §3.1's own worked examples show
    /// 45/46), but this harness has no per-node distribution to read (§13 "Never" list), so it uses the
    /// same kind of average <see cref="TreeModel.NodesPerTier"/> already relies on.</summary>
    public const long ApproxBudgetShareMilliPerNode = 1000L / ApproxNodesPerTree;

    /// <summary>`kMicro` for one representative node on <see cref="RepresentativeChannel"/>, via the
    /// REAL, unmodified <see cref="CoefficientBinder.Bind"/> -- `treeShareMilli` (D53: 1000, closed
    /// 2026-09-06 -- trees are the whole power budget today) and `treeBudgetMilli` (D15: 1000) are both
    /// read from the shipped tuning, never re-guessed.</summary>
    public static long RepresentativeKMicroPerNode(PassiveTreeTuning treeTuning, PowerTuning powerTuning)
    {
        var anchorMilli = ChannelAnchor.ForChannel(RepresentativeChannel, powerTuning);
        return CoefficientBinder.Bind(
            treeTuning.TreeShareMilli, treeTuning.TreeBudgetMilli,
            ApproxBudgetShareMilliPerNode, anchorMilli, Branches);
    }

    /// <summary>
    /// One tree's OWN contribution to <see cref="RepresentativeChannel"/> -- a PURE function of
    /// <paramref name="tree"/>'s own <see cref="TreeModel.PerTreeState.OwnedNodeCount"/> plus actor-wide
    /// constants that are legitimately global in the real game too (<paramref name="fMultiplier"/>:
    /// "F multiplies every tree-derived contribution... and nothing else",
    /// <see cref="Core.PassiveTree.Resolve.TreeAtomSource"/>'s own §5.3 rule, unchanged by this class;
    /// <paramref name="theta"/>: every tree reads the same actor Θ). NOTHING here reads any OTHER tree's
    /// state -- the exact property F7 found the old fold-back did not have (proven by
    /// <c>TreeChannelModelTests.Tree_Bs_own_amount_never_changes_with_tree_As_investment</c>).
    /// Mirrors <see cref="Core.PassiveTree.Resolve.TreeAtomSource.ResolveAmount"/>'s own `PTheta` branch
    /// exactly (`kMicro · P(Θ) / 1e6 · fMultiplier`), scaled by the node count instead of iterating one
    /// atom at a time -- summing `N` identical per-mille coefficients before the one division is
    /// arithmetically identical to summing `N` already-divided amounts, and cheaper.
    /// </summary>
    public static long PerTreeChannelAmount(
        TreeModel.PerTreeState tree, long kMicroPerNode, PowerLadder ladder, long theta, double fMultiplier)
    {
        if (tree.OwnedNodeCount == 0) return 0;
        var totalKMicro = checked(kMicroPerNode * tree.OwnedNodeCount);
        var pTheta = ladder.Value(checked((int)theta));
        var amount = (double)totalKMicro * pTheta / 1_000_000.0 * fMultiplier;
        return checked((long)Math.Round(amount, MidpointRounding.AwayFromZero));
    }

    /// <summary>
    /// One actor's whole tree-power contribution, as independent <see cref="BattleChannelMod"/>s --
    /// never an <see cref="AptitudeAllocation"/> mutation. Reuses <see cref="TreeModel.Resolve"/>
    /// UNCHANGED for `H`/`F`/gate/ownership-cost (F7 found no defect in those -- the defect is only in
    /// how tree power was folded INTO combat afterward), then converts each tree's own
    /// <see cref="PerTreeChannelAmount"/> into one summed modifier on <see cref="RepresentativeChannel"/>
    /// (additive: <see cref="BattleChannelMod"/> carries no op, always additive by construction --
    /// the same property the deleted <c>AptitudeResolver.ResolveForBattle</c> relied on -- so summing
    /// before wrapping is equivalent to wrapping-then-summing and avoids one
    /// <see cref="BattleChannelMod"/> per tree).
    /// </summary>
    public static IReadOnlyList<BattleChannelMod> ChannelModsFor(
        AptitudeAllocation allocation, long theta, long fmaxMilli, long wMilli, long b,
        bool includeOwnershipCost, TreeModel.CreditRule rule,
        PassiveTreeTuning treeTuning, PowerTuning powerTuning)
    {
        var resolved = TreeModel.Resolve(allocation, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule);
        var kMicroPerNode = RepresentativeKMicroPerNode(treeTuning, powerTuning);
        var ladder = new PowerLadder(powerTuning);
        var fMultiplier = resolved.FMilli / 1000.0;

        long total = 0;
        foreach (var tree in resolved.Trees)
            checked { total += PerTreeChannelAmount(tree, kMicroPerNode, ladder, theta, fMultiplier); }

        return total == 0
            ? Array.Empty<BattleChannelMod>()
            : new[] { new BattleChannelMod(RepresentativeChannel, total) };
    }

    /// <summary>The Erosion-style seam: a plain <see cref="SquadMatch.ToActorSetup"/> (aptitude only,
    /// exactly as every other actor in this harness is built), with tree power appended as an
    /// independent overlay -- never routed through the allocation itself.</summary>
    public static BattleActorSetup ToActorSetupWithTreeChannels(
        string key, string side, AptitudeAllocation allocation, int theta,
        long fmaxMilli, long wMilli, long b, bool includeOwnershipCost, TreeModel.CreditRule rule,
        PassiveTreeTuning treeTuning, PowerTuning powerTuning)
    {
        var baseSetup = SquadMatch.ToActorSetup(key, side, allocation, theta);
        var treeMods = ChannelModsFor(allocation, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule, treeTuning, powerTuning);
        return treeMods.Count == 0 ? baseSetup : baseSetup with { ChannelMods = baseSetup.ChannelMods.Concat(treeMods).ToList() };
    }

    // ---- trial resolution, mirroring SquadMatch.Measure exactly except for the actor-setup builder ---

    static void Tally(BattleOutcome outcome, ref long victories, ref long defeats, ref long stalemates)
    {
        switch (outcome)
        {
            case BattleOutcome.Victory: checked { victories++; } break;
            case BattleOutcome.Defeat: checked { defeats++; } break;
            default: checked { stalemates++; } break;
        }
    }

    static PairResult ToPairResult(string attackerId, string defenderId, long victories, long defeats, long stalemates)
    {
        var decided = checked(victories + defeats);
        var winShareMilli = decided == 0 ? 0L : checked(victories * 1000L) / decided;
        return new PairResult(attackerId, defenderId, victories, defeats, stalemates, winShareMilli);
    }

    /// <summary>One (attacker, defender) cell, resolved with BOTH sides carrying real tree-channel
    /// contributions -- never <see cref="Screening.RunWithRefine"/> (built for plain
    /// <see cref="RosterEntry"/>s with no per-actor channel-mod hook), so this stays a parallel path with
    /// zero blast radius on F1-F6's own measurement pipeline, matching <see cref="Erosion"/>'s own
    /// precedent exactly.</summary>
    public static PairResult MeasurePairWithTreeChannels(
        RosterEntry attacker, RosterEntry defender, RunSpec spec,
        long fmaxMilli, long wMilli, long b, bool includeOwnershipCost, TreeModel.CreditRule rule)
    {
        if (spec.Theta <= 0 || spec.Theta > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.Theta, "theta must be 1..int.MaxValue");
        var theta = (int)spec.Theta;
        var treeTuning = PassiveTreeTuningHub.Tuning;
        var powerTuning = PowerTuningHub.Tuning;

        long victories = 0, defeats = 0, stalemates = 0;
        for (var k = 0L; k < spec.Trials; k++)
        {
            var seed = Seeds.Mix(spec.RunSeed, attacker.Id, defender.Id, k);
            var squad = attacker.Actors
                .Select((a, i) => ToActorSetupWithTreeChannels($"squad:{i}", "squad", a, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule, treeTuning, powerTuning))
                .ToList();
            var wave = defender.Actors
                .Select((a, i) => ToActorSetupWithTreeChannels($"wave:{i}", "wave", a, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule, treeTuning, powerTuning))
                .ToList();
            var report = BattleEngine.Resolve(new BattleSetup { Squad = squad, Wave = wave }, seed);
            Tally(report.Outcome, ref victories, ref defeats, ref stalemates);
        }
        return ToPairResult(attacker.Id, defender.Id, victories, defeats, stalemates);
    }

    // ---- S3 re-run: SoulTrackModel's own fold-back inherits the identical defect ---------------------

    /// <summary>The soul-track twin of <see cref="ChannelModsFor"/> -- <see cref="SoulTrackModel.Resolve"/>
    /// computes the SAME final two-line fold-back (`effectivePoints = p_i + F·W_i`,
    /// <c>SoulTrackModel.cs:96-97</c>) as <see cref="TreeModel.Resolve"/>, just under an `F` recomputed
    /// from BOTH tracks' `H`. This reuses <see cref="PerTreeChannelAmount"/> unchanged, swapping in
    /// <see cref="SoulTrackModel.ActorSoulResult.FMilli"/> (the soul-aware `F`) for the plain one --
    /// proving the channel fix transfers to F5's own extension, not just F4's.</summary>
    public static IReadOnlyList<BattleChannelMod> SoulChannelModsFor(
        AptitudeAllocation allocation, long theta, long fmaxMilli, long wMilli, long b,
        bool includeOwnershipCost, TreeModel.CreditRule rule, long thetaPerSoulLevelMilli,
        PassiveTreeTuning treeTuning, PowerTuning powerTuning)
    {
        var pointsOnly = TreeModel.Resolve(allocation, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule);
        var soulResolved = SoulTrackModel.Resolve(allocation, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule, thetaPerSoulLevelMilli);

        var kMicroPerNode = RepresentativeKMicroPerNode(treeTuning, powerTuning);
        var ladder = new PowerLadder(powerTuning);
        var fMultiplier = soulResolved.FMilli / 1000.0; // the soul-track-AWARE F, not TreeModel's plain one

        long total = 0;
        foreach (var tree in pointsOnly.Trees)
            checked { total += PerTreeChannelAmount(tree, kMicroPerNode, ladder, theta, fMultiplier); }

        return total == 0
            ? Array.Empty<BattleChannelMod>()
            : new[] { new BattleChannelMod(RepresentativeChannel, total) };
    }

    public static BattleActorSetup ToActorSetupWithSoulChannels(
        string key, string side, AptitudeAllocation allocation, int theta,
        long fmaxMilli, long wMilli, long b, bool includeOwnershipCost, TreeModel.CreditRule rule,
        long thetaPerSoulLevelMilli, PassiveTreeTuning treeTuning, PowerTuning powerTuning)
    {
        var baseSetup = SquadMatch.ToActorSetup(key, side, allocation, theta);
        var soulMods = SoulChannelModsFor(allocation, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule, thetaPerSoulLevelMilli, treeTuning, powerTuning);
        return soulMods.Count == 0 ? baseSetup : baseSetup with { ChannelMods = baseSetup.ChannelMods.Concat(soulMods).ToList() };
    }

    /// <summary>One (attacker, defender) cell under the soul-aware channel model -- the soul-track
    /// twin of <see cref="MeasurePairWithTreeChannels"/>.</summary>
    public static PairResult MeasureSoulPairWithTreeChannels(
        RosterEntry attacker, RosterEntry defender, RunSpec spec,
        long fmaxMilli, long wMilli, long b, bool includeOwnershipCost, TreeModel.CreditRule rule,
        long thetaPerSoulLevelMilli)
    {
        if (spec.Theta <= 0 || spec.Theta > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(spec), spec.Theta, "theta must be 1..int.MaxValue");
        var theta = (int)spec.Theta;
        var treeTuning = PassiveTreeTuningHub.Tuning;
        var powerTuning = PowerTuningHub.Tuning;

        long victories = 0, defeats = 0, stalemates = 0;
        for (var k = 0L; k < spec.Trials; k++)
        {
            var seed = Seeds.Mix(spec.RunSeed, attacker.Id, defender.Id, k);
            var squad = attacker.Actors
                .Select((a, i) => ToActorSetupWithSoulChannels($"squad:{i}", "squad", a, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule, thetaPerSoulLevelMilli, treeTuning, powerTuning))
                .ToList();
            var wave = defender.Actors
                .Select((a, i) => ToActorSetupWithSoulChannels($"wave:{i}", "wave", a, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule, thetaPerSoulLevelMilli, treeTuning, powerTuning))
                .ToList();
            var report = BattleEngine.Resolve(new BattleSetup { Squad = squad, Wave = wave }, seed);
            Tally(report.Outcome, ref victories, ref defeats, ref stalemates);
        }
        return ToPairResult(attacker.Id, defender.Id, victories, defeats, stalemates);
    }

    // ---- S2 re-run: same cell shape TreeModel.ConcentrationSweep/CrossUnlockSweep already report -----

    /// <summary>F8's own re-run of <see cref="TreeModel.ConcentrationSweep"/> against the corrected
    /// model -- same cell shape, same refusal rule (1000 must be in the sweep), different actor-setup
    /// builder. Small-trial smoke runs are sufficient (F4's own acceptance shape: mechanism correctness,
    /// not a production-scale bar).</summary>
    public static TreeModel.ConcentrationResult ConcentrationSweep(
        RunSpec spec, SquadBuild corner, SquadBuild spread,
        IReadOnlyList<long> fmaxMillis, IReadOnlyList<long> wMillis, long b)
    {
        if (fmaxMillis is null || fmaxMillis.Count == 0) throw new ArgumentException("fmaxMilli sweep must be non-empty", nameof(fmaxMillis));
        if (wMillis is null || wMillis.Count == 0) throw new ArgumentException("wMilli sweep must be non-empty", nameof(wMillis));
        if (!fmaxMillis.Contains(1000L))
            throw new ArgumentException(
                "refused: fmaxMilli sweep must include 1000 (D5 is provisional, spec-squad-harness.md §6)",
                nameof(fmaxMillis));

        var cornerEntry = RosterEntry.From(corner);
        var spreadEntry = RosterEntry.From(spread);
        var cells = new List<TreeModel.ConcentrationCell>();
        foreach (var fmaxMilli in fmaxMillis)
        foreach (var wMilli in wMillis)
        foreach (var ownershipCost in new[] { false, true })
        {
            var pair = MeasurePairWithTreeChannels(cornerEntry, spreadEntry, spec, fmaxMilli, wMilli, b, ownershipCost, TreeModel.CreditRule.Largest);
            var halfWidth = Resolution.HalfWidthMilli(checked(pair.Victories + pair.Defeats));
            var representative = TreeModel.Resolve(corner.Actors[0], spec.Theta, fmaxMilli, wMilli, b, ownershipCost, TreeModel.CreditRule.Largest);
            cells.Add(new TreeModel.ConcentrationCell(fmaxMilli, wMilli, ownershipCost, pair.WinShareMilli, halfWidth, representative.HMilli, representative.FMilli));
        }
        return new TreeModel.ConcentrationResult(corner.Id, spread.Id, spec.Theta, b, spec.Trials, null, cells, Coverage.Standard());
    }

    /// <summary>F8's own re-run of <see cref="TreeModel.CrossUnlockSweep"/> against the corrected
    /// model.</summary>
    public static TreeModel.CrossUnlockResult CrossUnlockSweep(
        RunSpec spec, SquadBuild corner, SquadBuild spread,
        IReadOnlyList<TreeModel.CreditRule> rules, long fmaxMilli, long wMilli, long b)
    {
        if (rules is null || rules.Count == 0) throw new ArgumentException("rule sweep must be non-empty", nameof(rules));

        var cornerEntry = RosterEntry.From(corner);
        var spreadEntry = RosterEntry.From(spread);
        var cells = new List<TreeModel.CrossUnlockCell>();
        foreach (var rule in rules)
        foreach (var ownershipCost in new[] { false, true })
        {
            var pair = MeasurePairWithTreeChannels(cornerEntry, spreadEntry, spec, fmaxMilli, wMilli, b, ownershipCost, rule);
            var halfWidth = Resolution.HalfWidthMilli(checked(pair.Victories + pair.Defeats));
            var representative = TreeModel.Resolve(corner.Actors[0], spec.Theta, fmaxMilli, wMilli, b, ownershipCost, rule);
            cells.Add(new TreeModel.CrossUnlockCell(rule, ownershipCost, pair.WinShareMilli, halfWidth, representative.HMilli, representative.FMilli));
        }
        return new TreeModel.CrossUnlockResult(corner.Id, spread.Id, spec.Theta, b, fmaxMilli, wMilli, spec.Trials, null, cells, Coverage.Standard());
    }

    // ---- artifact writer: propose only, never touch gk-core/data/tuning (§13 "Never" list) ------------------

    static readonly JsonSerializerOptions ArtifactOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ConcentrationArtifactPath(string repoRoot) =>
        Path.Combine(repoRoot, "docs", "research", "passive-tree", "_concentration-sweep-v2.json");

    public static string WriteConcentrationArtifact(TreeModel.ConcentrationResult result, string? outPath = null)
    {
        var payload = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            note = "F8 re-run (docs/research measurement, not a golden, never a data/tuning write) -- " +
                   "tree power modelled as independent per-channel modifiers on combat.power.omni, never " +
                   "folded through AptitudeAllocation.Share. Compare cornerVsSpread against the F4-era " +
                   "_concentration-sweep.json for the same cells to see the delta F7's finding predicted.",
            cornerId = result.CornerId,
            spreadId = result.SpreadId,
            theta = result.Theta,
            b = result.B,
            trials = result.Trials,
            cells = result.Cells.Select(c => new
            {
                fmaxMilli = c.FmaxMilli,
                wMilli = c.WMilli,
                ownershipCostApplied = c.OwnershipCostApplied,
                cornerVsSpread = new TreeModel.ValueWithHalfWidth(c.CornerWinShareMilli, c.HalfWidthMilli),
                hMilli = c.HMilli,
                fMilli = c.FMilli,
            }),
            coverage = result.Coverage,
        };

        var json = JsonSerializer.Serialize(payload, ArtifactOptions);
        var path = outPath ?? ConcentrationArtifactPath(TuningBootstrap.FindRepoRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
        return json;
    }
}
