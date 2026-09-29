using FusionRpg.Core.PassiveTree.Resolve;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §11 S3 (todo "F5: S3 -- the soul track in the model"). Teaches
/// <see cref="TreeModel"/>'s `H` blend the SOUL track (D3, D8) so `concentration.wMilli` becomes
/// genuinely measurable across BOTH tracks -- <see cref="TreeModel.Resolve"/> always reads
/// <c>HSoulsMilli</c> as an honest zero (its own doc: "not modelled yet"); this class is what fills
/// that in, calling the REAL production <see cref="SoulTrack.ThetaNode"/> (spec-tree-resolve.md §6.2)
/// rather than re-deriving D3's formula (AGENTS.md "no private f(level)").
///
/// <para><b>What "soul levels per tree" means here, stated as a harness default (no production
/// formula exists to derive it -- this is `tree-state`'s own wiring gap, exactly like §1.1's per-actor
/// allocation gap).</b> A soul level is spent by player action and stored per NODE
/// (<c>RpgStore.PassiveTree.cs</c> stores <c>soul_level</c>, <see cref="FusionRpg.Core.PassiveTree.State.ClassifiedTreeNode"/>)
/// -- there is no shipped function mapping Θ to a TOTAL soul-level count the way
/// <see cref="PointBudget.PointsFor"/> maps Θ to an aptitude-point budget. This class reuses that SAME
/// real production rate as the harness's own soul-level budget (one Θ, one rate, applied to a SECOND
/// track) rather than inventing a private curve. The per-tree SHAPE (spike vs. spread) is the SAME
/// share vector the point track already computed (<see cref="TreeModel.PerTreeState.AptitudePoints"/>),
/// because a squad-harness actor has exactly one investment shape, not two independent ones -- this is
/// the same "one build, one corner-shape helper" discipline §1's own doc names for the point track.</para>
///
/// <para><b>H_souls reads the Θ OFFSET, never the absolute Θ_node.</b> D3's own formula
/// (<see cref="SoulTrack.ThetaNode"/>) is `Θ_actor + Ws·soulLevel/1000` -- every tree shares the SAME
/// `Θ_actor` floor, so a Herfindahl index over the absolute `Θ_node` values would dilute concentration
/// by a flat cross-tree constant that carries no investment information at all.
/// <see cref="TreeModel"/>'s own `H_nodes` is a Herfindahl over an INVESTMENT AMOUNT
/// (<c>OwnedNodeCount</c>, never a tier-inclusive absolute power number); `H_souls` is built the same
/// way here, over the per-tree OFFSET (`ThetaNode(...) - Θ_actor`, exactly `Ws·soulLevel_i/1000`) so
/// the two tracks feed <see cref="Concentration.HerfindahlMilli"/> the same KIND of quantity.</para>
///
/// <para><b>Reuses <see cref="TreeModel.Resolve"/> for everything the point track already computed.</b>
/// `W_i` (<see cref="TreeModel.PerTreeState.TreePower"/>) does not depend on `F`, so it is safe to
/// borrow directly from a plain <see cref="TreeModel.Resolve"/> call and refold it under a NEW `F`
/// computed from the real `H_souls` -- never a second, parallel tier/gate computation that could drift
/// from F4's own.</para>
/// </summary>
public static class SoulTrackModel
{
    /// <summary>One tree's soul-track state for one actor. <see cref="ThetaNode"/> is the REAL
    /// <see cref="SoulTrack.ThetaNode"/> output for this tree's soul level -- carried on the result so
    /// a test can assert it against that production function directly, for the SAME inputs, rather than
    /// trusting the internal call was made correctly.</summary>
    public sealed record SoulTreeState(string TreeId, long SoulLevel, long ThetaNode, long ThetaOffset);

    /// <summary>One actor's whole soul-track-aware resolution. <see cref="HNodesMilli"/> and
    /// <see cref="HSoulsMilli"/> are both carried (never just the blended <see cref="HMilli"/>) so a
    /// caller -- or a test -- can see which track is doing the work, exactly the transparency
    /// <see cref="TreeModel.ActorTreeResult"/> already gives the points track alone.</summary>
    public sealed record ActorSoulResult(
        IReadOnlyList<SoulTreeState> SoulTrees, long HNodesMilli, long HSoulsMilli, long HMilli, long FMilli,
        AptitudeAllocation EffectiveAllocation);

    /// <summary>
    /// §4's model, extended with D3's second ladder: resolves the points track via
    /// <see cref="TreeModel.Resolve"/> exactly as F4 does, then replaces the honest-zero `H_souls` with
    /// a REAL one built from <see cref="SoulTrack.ThetaNode"/>, reblends `H`, recomputes `F`, and refolds
    /// `p_i' = p_i + F·W_i` under the corrected `F` -- the same fold-back formula, never a private one.
    /// </summary>
    public static ActorSoulResult Resolve(
        AptitudeAllocation allocation, long theta, long fmaxMilli, long wMilli, long b,
        bool includeOwnershipCost, TreeModel.CreditRule rule, long thetaPerSoulLevelMilli)
    {
        // TreeModel.Resolve itself validates theta/fmaxMilli/wMilli/b -- no duplicate guard here beyond
        // thetaPerSoulLevelMilli, which SoulTrack.ThetaNode already validates per call.
        var pointsOnly = TreeModel.Resolve(allocation, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule);

        var totalAptitudePoints = pointsOnly.Trees.Sum(t => t.AptitudePoints);
        // The SAME real production rate the point track uses for its own budget (PointBudget.PointsFor,
        // AptitudeTuningHub.Tuning) -- this class's own doc: "one Theta, one rate, two tracks," never a
        // fresh f(level) for "how many souls does a Theta-level actor have."
        var soulBudget = PointBudget.PointsFor(AllocationScope.Commander, theta, AptitudeTuningHub.Tuning);

        var soulTrees = new List<SoulTreeState>(pointsOnly.Trees.Count);
        foreach (var t in pointsOnly.Trees)
        {
            var soulLevel = totalAptitudePoints == 0
                ? 0L
                : checked(t.AptitudePoints * soulBudget) / totalAptitudePoints; // widen, divide once
            var thetaNode = SoulTrack.ThetaNode(theta, soulLevel, thetaPerSoulLevelMilli); // REAL D3 call
            var offset = checked(thetaNode - theta);
            soulTrees.Add(new SoulTreeState(t.TreeId, soulLevel, thetaNode, offset));
        }

        var hSoulsMilli = Concentration.HerfindahlMilli(soulTrees.Select(t => t.ThetaOffset).ToList());
        var hMilli = Concentration.BlendMilli(pointsOnly.HNodesMilli, hSoulsMilli, wMilli);
        var fMilli = Concentration.FmaxAppliedMilli(hMilli, fmaxMilli);

        var effective = AptitudeAllocation.Empty;
        foreach (var t in pointsOnly.Trees)
        {
            var effectivePoints = checked(t.AptitudePoints + checked(fMilli * t.TreePower) / 1000);
            effective += AptitudeAllocation.Single(AllocationScope.Commander, t.TreeId, effectivePoints);
        }

        return new ActorSoulResult(soulTrees, pointsOnly.HNodesMilli, hSoulsMilli, hMilli, fMilli, effective);
    }

    /// <summary>Folds <see cref="Resolve"/> over every actor of a <see cref="SquadBuild"/>, mirroring
    /// <see cref="TreeModel.ApplyTreeModel"/> exactly (same fold shape, soul-track-aware model).</summary>
    public static RosterEntry ApplyTreeModel(
        SquadBuild build, long theta, long fmaxMilli, long wMilli, long b, bool includeOwnershipCost,
        TreeModel.CreditRule rule, long thetaPerSoulLevelMilli) =>
        new(build.Id, build.Actors.Select(a =>
            Resolve(a, theta, fmaxMilli, wMilli, b, includeOwnershipCost, rule, thetaPerSoulLevelMilli).EffectiveAllocation).ToList());
}
