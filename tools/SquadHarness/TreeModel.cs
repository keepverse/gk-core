using System.Text.Json;
using FusionRpg.Core.PassiveTree;
using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §4 / §11 S2 (todo "F4: S2 -- concentration and cross-unlock"). The tree model
/// `req(t)`, `W(T)`, `H`, `F` per actor, with D25's ownership cost folded in -- calling the REAL
/// production resolvers rather than re-deriving any of them:
///
/// <list type="bullet">
/// <item><c>req(t)</c> -- <see cref="TierGate.Reached"/> (D26 ladder, D29 cap read from
/// <see cref="PassiveTreeTuning.TierLadder"/>).</item>
/// <item><c>H</c>/<c>F</c> -- <see cref="Concentration.HerfindahlMilli"/>,
/// <see cref="Concentration.BlendMilli"/>, <see cref="Concentration.FmaxAppliedMilli"/> (D4/D8/D39).</item>
/// <item>D25's ownership cost -- <see cref="TreeUnlockCost.Cumulative"/> against a wallet read via
/// <see cref="PointBudget.SkillPointsFor"/> (D34's per-scope skill-point rate table).</item>
/// <item>D28's largest-mate credit rule -- <see cref="CrossUnlock.Gate"/>, the one rule that ships in
/// <c>src/</c>. `none`/`quarter`/`full` have NO production implementation (D28 only shipped
/// `largest`) -- producing evidence for the other three is this module's own job (§11 S2), never a
/// re-derivation of a shipped resolver.</item>
/// </list>
///
/// <para><b>What this class does NOT model.</b> The soul track (D3) is S3's job (§11): every
/// <see cref="ActorTreeResult.HSoulsMilli"/> here reads exactly zero -- an honest "not modelled yet",
/// never a guess. Per-tree node granularity (<see cref="NodesPerTier"/>) and the authored tier count
/// (<see cref="AuthoredTierCount"/>) are D29's own structural shape (10 tiers, ~40 nodes/tree = 4/tier)
/// restated as this harness's own constant, because the harness builds trees in memory and never reads
/// the generated catalog (spec §13 "Never" list: no `RpgStore`, no `data/`) -- so it cannot read
/// `TreeRecord.NodesPerTier`'s real per-tier array and uses D29's own average instead, named as such.
/// </para>
///
/// <para><b>Cross-tree spending order under D25 is a harness-level default, stated as one.</b> No spec
/// settles which tree a multi-tree actor spends its skill-point wallet on first (doc 12 §7.5 only fixes
/// that the COUNT is global, never per tree). <see cref="AllocateOwnedNodes"/> resolves this the same
/// way this codebase resolves every other unstated ordering choice: a fixed, published, deterministic
/// rule (highest aptitude investment first, capped at that tree's own gate-opened node count, ties
/// broken by tree id ordinal) rather than a random or hand-picked one.</para>
/// </summary>
public static class TreeModel
{
    /// <summary>D29 -- ten tiers, structural (changing it changes the game's shape, not how it feels;
    /// AGENTS.md's magic-number test). Never a tunable.</summary>
    public const int AuthoredTierCount = 10;

    /// <summary>D29's own average: "10 tiers x 2 branches, ~40 nodes per tree" -> 4 nodes/tier. This
    /// harness never reads the generated catalog's real per-tier <c>NodesPerTier</c> array (§13 "Never"
    /// list), so this is the harness's own structural approximation of D29's shape -- named as an
    /// approximation, not a re-declaration of catalog data.</summary>
    public const long NodesPerTier = 4;

    /// <summary>gk-core/tools/HybridViability's own <c>--crossunlock</c> sweep comment: "ordering is
    /// b-invariant." Used only as <c>crossunlock</c>'s CLI default (concentration's own sweep is
    /// explicitly ABOUT <c>b</c>/Fmax and never defaults it -- Program.cs requires <c>--b</c> there,
    /// matching the spec's own example command).</summary>
    public const long DefaultCrossUnlockB = 5;

    /// <summary>D28's four candidate credit rules (spec §6, §11 S2). Only <see cref="Largest"/> is
    /// D28's shipped decision -- the other three exist so this module can produce the evidence the
    /// decision was made without (doc 12: "measured, pending a D25 re-run").</summary>
    public enum CreditRule { None, Largest, Quarter, Full }

    /// <summary>One tree's resolved state under one configuration. <see cref="GateNodeCount"/> is what
    /// the gate alone would let the actor own (no D25); <see cref="OwnedNodeCount"/> is what the actor
    /// actually holds once the skill-point wallet is applied (equal to <see cref="GateNodeCount"/> when
    /// ownership cost is off) -- carrying both, rather than only the final number, is what makes D25's
    /// effect visible in the artifact instead of just its result.</summary>
    public sealed record PerTreeState(
        string TreeId, long AptitudePoints, int GateTier, long GateNodeCount,
        long OwnedNodeCount, int AffordTier, long TreePower);

    /// <summary>One actor's whole tree-model resolution. <see cref="OwnershipCostApplied"/> and
    /// <see cref="Rule"/> are carried on the result itself so a caller comparing two
    /// <see cref="ActorTreeResult"/>s can never accidentally treat a with/without-D25 pair (or a
    /// different credit rule) as the same cell -- acceptance bullet 3's "reported as a different cell,
    /// never silently substituted."</summary>
    public sealed record ActorTreeResult(
        IReadOnlyList<PerTreeState> Trees, long HNodesMilli, long HSoulsMilli, long HMilli, long FMilli,
        AptitudeAllocation EffectiveAllocation, bool OwnershipCostApplied, CreditRule Rule);

    // ---- D28: the gate quantity per tree, under one of the four candidate credit rules -------------

    /// <summary>`gate(i)` under <paramref name="rule"/>. <see cref="CreditRule.Largest"/> calls the real
    /// production <see cref="CrossUnlock.Gate"/> unchanged -- the one rule D28 actually shipped.
    /// <see cref="CreditRule.None"/>/<see cref="CreditRule.Quarter"/>/<see cref="CreditRule.Full"/> have
    /// no production home (D28 only decided `largest`), so this module computes them itself, exactly
    /// mirroring the credit shapes <c>gk-core/tools/HybridViability</c>'s own (closed-form, non-production)
    /// <c>--crossunlock</c> sweep already used for the same three names.</summary>
    public static IReadOnlyDictionary<string, long> GateQuantities(
        IReadOnlyDictionary<string, long> baseByTree,
        IReadOnlyDictionary<string, string?> stanceGroupByTree,
        CreditRule rule)
    {
        if (baseByTree is null) throw new ArgumentNullException(nameof(baseByTree));
        if (stanceGroupByTree is null) throw new ArgumentNullException(nameof(stanceGroupByTree));

        if (rule == CreditRule.Largest)
            return baseByTree.Keys.ToDictionary(id => id, id => CrossUnlock.Gate(id, baseByTree, stanceGroupByTree), StringComparer.Ordinal);

        return baseByTree.Keys.ToDictionary(id => id, id =>
        {
            if (rule == CreditRule.None) return baseByTree[id];
            if (!stanceGroupByTree.TryGetValue(id, out var myGroup) || myGroup is null)
                return baseByTree[id]; // no stance group -> no mates, same 0-credit default as CrossUnlock.Credit (§4.1)

            long mateSum = 0;
            foreach (var (otherId, otherBase) in baseByTree)
            {
                if (string.Equals(otherId, id, StringComparison.Ordinal)) continue;
                if (stanceGroupByTree.TryGetValue(otherId, out var otherGroup) && string.Equals(otherGroup, myGroup, StringComparison.Ordinal))
                    checked { mateSum += otherBase; }
            }

            checked
            {
                return rule switch
                {
                    CreditRule.Full => baseByTree[id] + mateSum,
                    CreditRule.Quarter => baseByTree[id] + mateSum * 250 / 1000, // 25% credit -- widened, divided once
                    _ => baseByTree[id],
                };
            }
        }, StringComparer.Ordinal);
    }

    // ---- D25: the skill-point wallet -> total affordable node count --------------------------------

    /// <summary>The largest node count whose <see cref="TreeUnlockCost.Cumulative"/> price does not
    /// exceed <paramref name="walletBudget"/> -- an integer binary search against the REAL production
    /// cost function, never a re-derivation of doc 12 §2's closed-form square root (a monotone search
    /// over the shipped function is not a reimplementation of it). Doubling-then-bisecting, so the
    /// search is O(log N) even for the largest budgets PS-8's endless `Θ` produces.</summary>
    public static long AffordableNodeCount(long walletBudget, long first, long step)
    {
        if (walletBudget < 0) throw new ArgumentOutOfRangeException(nameof(walletBudget), walletBudget, "wallet budget must be >= 0");

        long lo = 0, hi = 1;
        while (TryCumulative(hi, first, step) is { } grown && grown <= walletBudget)
        {
            lo = hi;
            checked { hi *= 2; }
        }

        while (lo + 1 < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (TryCumulative(mid, first, step) is { } cum && cum <= walletBudget) lo = mid; else hi = mid;
        }
        return lo;

        static long? TryCumulative(long count, long first, long step)
        {
            // Structurally unreachable at any realistic catalog size (TreeUnlockCost's own doc: the
            // cumulative cost overflows long only past ~3.04e9 nodes) but the search must still not
            // crash on a synthetic huge budget a test throws at it -- past this point the count is
            // simply "definitely more than affordable," which is exactly what returning null encodes.
            try { return TreeUnlockCost.Cumulative(count, first, step); }
            catch (OverflowException) { return null; }
        }
    }

    /// <summary>Water-fills <paramref name="totalAffordable"/> nodes across trees: highest
    /// <paramref name="priorityByTree"/> first (ties broken by tree id, ordinal), each tree capped at
    /// its own <paramref name="capByTree"/> (the gate-opened node count -- D25's price is
    /// currency-blind, doc 12 §5, but the GATE still bounds what there is to buy). See this class's own
    /// doc for why this ordering, specifically, is the harness's resolved default.</summary>
    public static IReadOnlyDictionary<string, long> AllocateOwnedNodes(
        IReadOnlyDictionary<string, long> priorityByTree,
        IReadOnlyDictionary<string, long> capByTree,
        long totalAffordable)
    {
        if (capByTree is null) throw new ArgumentNullException(nameof(capByTree));
        if (totalAffordable < 0) throw new ArgumentOutOfRangeException(nameof(totalAffordable), totalAffordable, "must be >= 0");

        var remaining = totalAffordable;
        var owned = capByTree.Keys.ToDictionary(id => id, _ => 0L, StringComparer.Ordinal);
        var order = capByTree.Keys
            .OrderByDescending(id => priorityByTree.GetValueOrDefault(id))
            .ThenBy(id => id, StringComparer.Ordinal);

        foreach (var id in order)
        {
            if (remaining <= 0) break;
            var take = Math.Min(capByTree[id], remaining);
            owned[id] = take;
            checked { remaining -= take; }
        }
        return owned;
    }

    // ---- the whole per-actor pipeline ---------------------------------------------------------------

    /// <summary>
    /// §4's whole model for one actor: `p_i`, `T_i` (via the real <see cref="TierGate.Reached"/>, fed a
    /// gate quantity that already carries <paramref name="rule"/>'s credit), `W_i`, `H`, `F`, folded
    /// back into an effective <see cref="AptitudeAllocation"/> -- exactly `gk-core/tools/HybridViability --trees`'s
    /// own fold-back (`p_i' = p_i + F*W_i`), reusing the shipped <see cref="Concentration"/>/
    /// <see cref="TierGate"/>/<see cref="CrossUnlock"/> resolvers instead of the tool's own closed-form
    /// re-derivation.
    ///
    /// <para><b>D25, non-decorative.</b> When <paramref name="includeOwnershipCost"/> is true, BOTH
    /// `W_i` (via <see cref="PerTreeState.AffordTier"/>, bounded by what the wallet actually bought) AND
    /// `H` (computed over <see cref="PerTreeState.OwnedNodeCount"/>, not the gate's node count) move --
    /// exactly the two places doc 12 §5's worked example shows D25 changing the outcome. Turning it off
    /// substitutes the gate's own node count for both, which is a genuinely DIFFERENT model, never the
    /// same numbers under a different label (acceptance bullet 3).</para>
    ///
    /// <para><b>H_souls is honestly zero.</b> The soul track is S3's own addition (§11); reading zero
    /// here says "not modelled yet," not "measured at zero."</para>
    /// </summary>
    public static ActorTreeResult Resolve(
        AptitudeAllocation allocation, long theta, long fmaxMilli, long wMilli, long b,
        bool includeOwnershipCost, CreditRule rule)
    {
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));
        if (theta <= 0) throw new ArgumentOutOfRangeException(nameof(theta), theta, "theta must be positive");
        if (fmaxMilli < 1000)
            throw new ArgumentOutOfRangeException(nameof(fmaxMilli), fmaxMilli,
                "fmaxMilli must be >= 1000 -- D5: 1000 (no multiplier) is the legal floor, never refused");
        if (wMilli is < 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(wMilli), wMilli, "wMilli must be 0..1000 (D8's blend weight)");
        if (b < 0) throw new ArgumentOutOfRangeException(nameof(b), b, "b (aptitude-point-equivalents per tier unit) cannot be negative");

        var roster = BuildFactory.Roster;

        // p_i = share_i * (Theta-scaled aptitude budget) -- the real production rate table
        // (PointBudget.PointsFor / AptitudeTuning.PointEconomy), never the hardcoded literal
        // gk-core/tools/HybridViability's own --trees sweep uses.
        var pointsByTree = roster.ToDictionary(id => id, id => allocation.PointsAt(AllocationScope.Commander, id), StringComparer.Ordinal);
        var totalAllocated = pointsByTree.Values.Sum();
        var aptitudeBudget = PointBudget.PointsFor(AllocationScope.Commander, theta, AptitudeTuningHub.Tuning);
        var pByTree = roster.ToDictionary(id => id,
            id => totalAllocated == 0 ? 0L : checked(pointsByTree[id] * aptitudeBudget) / totalAllocated,
            StringComparer.Ordinal);

        // Posture is READ from the shipped catalog, never re-declared (SquadRoster's own rule).
        var stanceGroupByTree = roster.ToDictionary(id => id, id => (string?)AptitudeCatalog.Get(id).Posture.ToString(), StringComparer.Ordinal);
        var gateByTree = GateQuantities(pByTree, stanceGroupByTree, rule);

        var reqScale = PassiveTreeTuningHub.Tuning.TierLadder.ReqScalePoints;
        var gateTierByTree = roster.ToDictionary(id => id, id => TierGate.Reached(gateByTree[id], AuthoredTierCount, reqScale), StringComparer.Ordinal);
        var gateNodeCountByTree = roster.ToDictionary(id => id, id => checked(gateTierByTree[id] * NodesPerTier), StringComparer.Ordinal);

        IReadOnlyDictionary<string, long> ownedNodeCountByTree;
        if (includeOwnershipCost)
        {
            // D34's per-scope skill-point rate (the real production wallet), never Theta_player shared
            // across scopes -- PointBudget.SkillPointsFor's own doc names exactly this failure mode.
            var wallet = PointBudget.SkillPointsFor(AllocationScope.Commander, theta, AptitudeTuningHub.Tuning);
            var unlockCost = PassiveTreeTuningHub.Tuning.UnlockCost;
            var affordable = AffordableNodeCount(wallet, unlockCost.FirstPoints, unlockCost.StepPoints);
            ownedNodeCountByTree = AllocateOwnedNodes(pByTree, gateNodeCountByTree, affordable);
        }
        else
        {
            ownedNodeCountByTree = gateNodeCountByTree;
        }

        var perTree = new List<PerTreeState>(roster.Count);
        foreach (var id in roster)
        {
            var owned = ownedNodeCountByTree[id];
            var affordTier = (int)Math.Min(gateTierByTree[id], owned / NodesPerTier);
            var w = checked(b * affordTier * (affordTier + 1) / 2); // T(T+1) always even -- exact division
            perTree.Add(new PerTreeState(id, pByTree[id], gateTierByTree[id], gateNodeCountByTree[id], owned, affordTier, w));
        }

        var hNodesMilli = Concentration.HerfindahlMilli(perTree.Select(t => t.OwnedNodeCount).ToList());
        const long hSoulsMilli = 0L; // no soul track modelled until S3 (spec §11 S3) -- honest zero, not an assumption
        var hMilli = Concentration.BlendMilli(hNodesMilli, hSoulsMilli, wMilli);
        var fMilli = Concentration.FmaxAppliedMilli(hMilli, fmaxMilli);

        var effective = AptitudeAllocation.Empty;
        foreach (var t in perTree)
        {
            var effectivePoints = checked(t.AptitudePoints + checked(fMilli * t.TreePower) / 1000);
            effective += AptitudeAllocation.Single(AllocationScope.Commander, t.TreeId, effectivePoints);
        }

        return new ActorTreeResult(perTree, hNodesMilli, hSoulsMilli, hMilli, fMilli, effective, includeOwnershipCost, rule);
    }

    /// <summary>Folds <see cref="Resolve"/> over every actor of a <see cref="SquadBuild"/>, producing a
    /// tree-adjusted <see cref="RosterEntry"/> the existing <see cref="Screening"/>/<see cref="Sweep"/>
    /// measurement machinery consumes unchanged -- this is the whole point of folding tree power back
    /// into an <see cref="AptitudeAllocation"/> rather than a private side-channel (§4).</summary>
    public static RosterEntry ApplyTreeModel(
        SquadBuild build, long theta, long fmaxMilli, long wMilli, long b, bool includeOwnershipCost, CreditRule rule) =>
        new(build.Id, build.Actors.Select(a => Resolve(a, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule).EffectiveAllocation).ToList());

    /// <summary>Duel-scale sibling of the <see cref="SquadBuild"/> overload above -- folds tree power into
    /// ONE actor's allocation (a <see cref="NamedBuild"/> from <see cref="SquadRoster.Duels"/>) rather
    /// than six. Added for <see cref="BudgetSweep"/> (§11 S4, todo "F6"), which measures at duel scope
    /// deliberately: doc 11 §6b's own "twelve corners" finding is itself a duel-scale
    /// (<c>gk-core/tools/HybridViability</c>) result.</summary>
    public static RosterEntry ApplyTreeModel(
        NamedBuild build, long theta, long fmaxMilli, long wMilli, long b, bool includeOwnershipCost, CreditRule rule) =>
        new(build.Id, new[] { Resolve(build.Allocation, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule).EffectiveAllocation });

    // ---- S2 mode 1: concentration -------------------------------------------------------------------

    /// <summary>One (fmaxMilli, wMilli, ownershipCost) cell of the §6 sweep -- corner vs spread's win
    /// share, its half-width, and the representative `H`/`F` that produced it (from one actor of the
    /// corner squad -- every actor in a mono squad resolves identically by construction, <c>SquadRoster</c>'s
    /// own "six copies" shape, so any one is representative).</summary>
    public sealed record ConcentrationCell(
        long FmaxMilli, long WMilli, bool OwnershipCostApplied,
        long CornerWinShareMilli, long HalfWidthMilli, long HMilli, long FMilli);

    public sealed record ConcentrationResult(
        string CornerId, string SpreadId, long Theta, long B, long Trials, long? RefineTrials,
        IReadOnlyList<ConcentrationCell> Cells, HarnessCoverage Coverage);

    /// <summary>
    /// §6's own sweep: does any <paramref name="fmaxMillis"/> value make the corner class stop being
    /// strictly worse than spread, at squad scope, with D25's ownership cost reported both ways?
    /// Refuses (never silently adds) when 1000 is missing from the sweep -- D5's own acceptance bullet:
    /// "1000 must be inside the sweep because D5 is provisional."
    /// </summary>
    public static ConcentrationResult ConcentrationSweep(
        RunSpec spec, SquadBuild corner, SquadBuild spread,
        IReadOnlyList<long> fmaxMillis, IReadOnlyList<long> wMillis, long b, long? refineTrials, bool parallel)
    {
        if (fmaxMillis is null || fmaxMillis.Count == 0) throw new ArgumentException("fmaxMilli sweep must be non-empty", nameof(fmaxMillis));
        if (wMillis is null || wMillis.Count == 0) throw new ArgumentException("wMilli sweep must be non-empty", nameof(wMillis));
        if (!fmaxMillis.Contains(1000L))
            throw new ArgumentException(
                "refused: fmaxMilli sweep must include 1000 (D5 is provisional -- the 'no multiplier' " +
                "case is a legitimate data point the sweep must not skip past, spec-squad-harness.md §6)",
                nameof(fmaxMillis));

        var cells = new List<ConcentrationCell>();
        foreach (var fmaxMilli in fmaxMillis)
        foreach (var wMilli in wMillis)
        foreach (var ownershipCost in new[] { false, true })
        {
            var cornerEntry = ApplyTreeModel(corner, spec.Theta, fmaxMilli, wMilli, b, ownershipCost, CreditRule.Largest);
            var spreadEntry = ApplyTreeModel(spread, spec.Theta, fmaxMilli, wMilli, b, ownershipCost, CreditRule.Largest);
            var screening = Screening.RunWithRefine("concentration", new[] { cornerEntry, spreadEntry }, spec, refineTrials, parallel);
            var pair = screening.Run.Pairs.Single(p => p.AttackerId == cornerEntry.Id && p.DefenderId == spreadEntry.Id);
            var halfWidth = Resolution.HalfWidthMilli(checked(pair.Victories + pair.Defeats));

            var representative = Resolve(corner.Actors[0], spec.Theta, fmaxMilli, wMilli, b, ownershipCost, CreditRule.Largest);
            cells.Add(new ConcentrationCell(fmaxMilli, wMilli, ownershipCost, pair.WinShareMilli, halfWidth, representative.HMilli, representative.FMilli));
        }

        return new ConcentrationResult(corner.Id, spread.Id, spec.Theta, b, spec.Trials, refineTrials, cells, Coverage.Standard());
    }

    // ---- S2 mode 2: crossunlock ----------------------------------------------------------------------

    public sealed record CrossUnlockCell(
        CreditRule Rule, bool OwnershipCostApplied,
        long CornerWinShareMilli, long HalfWidthMilli, long HMilli, long FMilli);

    public sealed record CrossUnlockResult(
        string CornerId, string SpreadId, long Theta, long B, long FmaxMilli, long WMilli, long Trials, long? RefineTrials,
        IReadOnlyList<CrossUnlockCell> Cells, HarnessCoverage Coverage);

    /// <summary>
    /// §6's cross-unlock re-run: does D28's `largest` rule still reverse the corner/spread ordering
    /// against `none`/`quarter`/`full`, at squad scope, D25 both ways? Fmax/w are fixed backdrops here
    /// (defaulted, at the CLI layer, to the SHIPPED tuning -- <c>concentration</c> is the mode that
    /// sweeps them; this mode's own axis is the credit rule).
    /// </summary>
    public static CrossUnlockResult CrossUnlockSweep(
        RunSpec spec, SquadBuild corner, SquadBuild spread,
        IReadOnlyList<CreditRule> rules, long fmaxMilli, long wMilli, long b, long? refineTrials, bool parallel)
    {
        if (rules is null || rules.Count == 0) throw new ArgumentException("rule sweep must be non-empty", nameof(rules));

        var cells = new List<CrossUnlockCell>();
        foreach (var rule in rules)
        foreach (var ownershipCost in new[] { false, true })
        {
            var cornerEntry = ApplyTreeModel(corner, spec.Theta, fmaxMilli, wMilli, b, ownershipCost, rule);
            var spreadEntry = ApplyTreeModel(spread, spec.Theta, fmaxMilli, wMilli, b, ownershipCost, rule);
            var screening = Screening.RunWithRefine("crossunlock", new[] { cornerEntry, spreadEntry }, spec, refineTrials, parallel);
            var pair = screening.Run.Pairs.Single(p => p.AttackerId == cornerEntry.Id && p.DefenderId == spreadEntry.Id);
            var halfWidth = Resolution.HalfWidthMilli(checked(pair.Victories + pair.Defeats));

            var representative = Resolve(corner.Actors[0], spec.Theta, fmaxMilli, wMilli, b, ownershipCost, rule);
            cells.Add(new CrossUnlockCell(rule, ownershipCost, pair.WinShareMilli, halfWidth, representative.HMilli, representative.FMilli));
        }

        return new CrossUnlockResult(corner.Id, spread.Id, spec.Theta, b, fmaxMilli, wMilli, spec.Trials, refineTrials, cells, Coverage.Standard());
    }

    // ---- artifact writers: propose only, never touch gk-core/data/tuning (§13 "Never" list) -----------------

    static readonly JsonSerializerOptions ArtifactOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ConcentrationArtifactPath(string repoRoot) =>
        Path.Combine(repoRoot, "docs", "research", "passive-tree", "_concentration-sweep.json");

    public static string CrossUnlockArtifactPath(string repoRoot) =>
        Path.Combine(repoRoot, "docs", "research", "passive-tree", "_crossunlock-sweep.json");

    /// <summary>Every cell is a number AND a half-width (F2's own acceptance rule, reused here) --
    /// <see cref="Artifacts"/>'s <c>ValueWithHalfWidth</c> shape, restated so this file has no
    /// cross-dependency on the F2 file for a two-field record.</summary>
    public sealed record ValueWithHalfWidth(long WinShareMilli, long HalfWidthMilli);

    public static string WriteConcentrationArtifact(ConcentrationResult result, string? outPath = null)
    {
        var payload = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            note = "docs/research measurement, not a golden, and never a data/tuning write (spec-squad-harness.md " +
                   "§13 'Never' list). Proposes concentration.fmaxMilli evidence only; tools/tuning/publish.py is " +
                   "the only writer of the real tunable (tunables-ssot.md T4).",
            cornerId = result.CornerId,
            spreadId = result.SpreadId,
            theta = result.Theta,
            b = result.B,
            trials = result.Trials,
            refineTrials = result.RefineTrials,
            cells = result.Cells.Select(c => new
            {
                fmaxMilli = c.FmaxMilli,
                wMilli = c.WMilli,
                ownershipCostApplied = c.OwnershipCostApplied,
                cornerVsSpread = new ValueWithHalfWidth(c.CornerWinShareMilli, c.HalfWidthMilli),
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

    public static string WriteCrossUnlockArtifact(CrossUnlockResult result, string? outPath = null)
    {
        var payload = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            note = "docs/research measurement, not a golden, and never a data/tuning write (spec-squad-harness.md " +
                   "§13 'Never' list). Re-runs D28's four candidate credit rules as evidence; the largest-mate " +
                   "rule REPORTS here, it does not decide (§6).",
            cornerId = result.CornerId,
            spreadId = result.SpreadId,
            theta = result.Theta,
            b = result.B,
            fmaxMilli = result.FmaxMilli,
            wMilli = result.WMilli,
            trials = result.Trials,
            refineTrials = result.RefineTrials,
            cells = result.Cells.Select(c => new
            {
                rule = c.Rule.ToString(),
                ownershipCostApplied = c.OwnershipCostApplied,
                cornerVsSpread = new ValueWithHalfWidth(c.CornerWinShareMilli, c.HalfWidthMilli),
                hMilli = c.HMilli,
                fMilli = c.FMilli,
            }),
            coverage = result.Coverage,
        };

        var json = JsonSerializer.Serialize(payload, ArtifactOptions);
        var path = outPath ?? CrossUnlockArtifactPath(TuningBootstrap.FindRepoRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
        return json;
    }
}
