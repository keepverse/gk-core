using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// spec-aptitude-resolve.md — turns an allocation into derived-channel modifiers, through the two
/// PS-3 read functions <see cref="AptitudeReadFunctions"/> owns. Pure: no I/O, no statics, no cache
/// (§5 rule 3 — the second-cheapest option after having none at all is a per-composer cache idiom;
/// this needs neither, since it holds no state between calls).
///
/// <para>Every dependency arrives as a parameter — including <see cref="PowerLadder"/> — so this stays
/// callable with nothing configured globally: the caller (a subsystem, a test, `deterministic-core`)
/// owns wiring <c>PowerTuningHub.Tuning</c>/<c>AptitudeTuningHub.Tuning</c> into concrete objects.</para>
/// </summary>
public static class AptitudeResolver
{
    static readonly AllocationScope[] AllScopes = Enum.GetValues<AllocationScope>();

    /// <summary>
    /// species-progression step 6.1 (spec-species-layer-delivery.md, R2 + R16 + R21, THE one explained
    /// re-bless — see `tasks/evidence-fragments/SP6.1.md` for the full before/after table). Resolves
    /// EACH <see cref="AllocationScope"/> the allocation holds points in ALONE — share taken over that
    /// scope's own total (<see cref="AptitudeAllocation.ShareWithinScope"/>), never the old merged
    /// grand-total share — and concatenates every scope's own contributions. A scope with no points
    /// (<see cref="AptitudeAllocation.TotalForScope"/> is zero) contributes nothing — not a zero-valued
    /// modifier (§7 test 5) — so an empty allocation still resolves to an empty list.
    ///
    /// <para><b>R21:</b> each scope's read output (never its share, which stays a bounded [0,1] ratio)
    /// is scaled by that scope's own <see cref="AptitudeLayerWeights"/> entry — the tunable that keeps
    /// "commander smallest, unique largest" as data now that resolve-alone no longer weights scopes
    /// implicitly by point rate the way the old merged denominator did. An actor with exactly one
    /// non-empty scope is BYTE-IDENTICAL to the pre-6.1 merged resolve only when that scope's weight is
    /// 1000 (the unweighted identity); `uniqueCreature` ships at 1000, every other scope does not.</para>
    /// </summary>
    public static IReadOnlyList<DerivedModifier> Resolve(
        AptitudeAllocation allocation, AptitudeTuning tuning, PowerLadder ladder, int theta, DerivedStatRegistry registry)
    {
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        var mods = new List<DerivedModifier>();
        long? pTheta = null; // computed at most once, lazily -- only if a funded edge actually reads it

        foreach (var scope in AllScopes)
        {
            if (allocation.TotalForScope(scope) <= 0) continue; // an empty scope is not "present" (R2/R16)
            var weightMilli = tuning.Read.LayerWeights.Of(scope);

            foreach (var edge in tuning.Edges)
            {
                var share = allocation.ShareWithinScope(scope, edge.Source);
                if (share <= 0.0) continue;

                if (!registry.TryResolveChannel(edge.Channel, out var def))
                    throw new InvalidOperationException(
                        $"aptitude edge targets unregistered channel '{edge.Channel}' (source '{edge.Source}') — " +
                        "spec-aptitude-tuning.md's own reader-census test (AptitudeTuningTests.EveryEdgeChannel_" +
                        "isRegistered_inDerivedStatRegistry) should have caught this before it ever reached here.");

                var kMilli = EffectiveKMilli(tuning, edge);
                double value;
                if (edge.Mode == AptitudeReadMode.Contest)
                {
                    var raw = AptitudeReadFunctions.Contest(kMilli, share, tuning.Read.Contest.ShareExponentMilli, tuning.Read.Contest.SpanPointsMilli);
                    value = raw * weightMilli / 1000.0; // R21, contest: double, no new rounding rule
                }
                else
                {
                    var raw = AptitudeReadFunctions.Magnitude(kMilli, share, tuning.Read.Magnitude.ShareExponentMilli, pTheta ??= ladder.Value(theta));
                    value = ScaleMilli(raw, weightMilli); // R21, magnitude: widen, checked, round-half-away /1000 -- the SAME per-mille idiom Recovery/Mitigation already use, reused rather than re-typed
                }

                // The op a contribution composes with is a property of the TARGET CHANNEL's registered
                // DerivedComposeKind, not of the read mode — these are independent axes (how the value is
                // computed vs. how multiple sources combine). FlatSum/FlatReplace both take an additive
                // Flat contribution (DerivedComposer.ComposeFlatReplace sums Flat as the baseline before any
                // Replace wins); an aptitude point is additive by nature, never a hard override. No shipped
                // edge targets a MaxPriorityFlag channel today; Flat is still the least-surprising fallback
                // if one ever does, since Flag/Replace/Increased in that kind's "max of" set would let one
                // point silently overrule every other source.
                var op = def.Compose == DerivedComposeKind.SumIncreased ? DerivedModifierOp.Increased : DerivedModifierOp.Flat;

                mods.Add(new DerivedModifier(edge.Channel, op, value,
                    SourceId: ContributionSourceIds.Aptitude(scope, edge.Source)));
            }
        }

        return mods;
    }

    /// <summary>
    /// <c>tuning.Recovery.ScaleMilli</c> is the termination-invariant dial (class-system-ideal.md §5d)
    /// — one multiplier over every edge whose channel starts with one of <c>tuning.Recovery.Families</c>,
    /// because <c>r = recovery/peerDamage</c> is a global ratio a single edge's own coefficient cannot
    /// target. Found missing here 2026-08-27 (this program's own POC, <c>gk-core/tools/CombatSim</c>, had the
    /// identical gap and fixed it the same session) — every recovery-family edge was reading its raw,
    /// undamped coefficient, silently discarding the dial the shipped tuning file's own
    /// <c>recovery._scaleWhy</c> note says was solved against a measured r=1.33 (an unkillable pair).
    /// Both factors are per-mille; widen before multiplying, divide by their combined scale once.
    /// </summary>
    /// <summary>Per-mille scale applied with round-half-away-from-zero, which is this repo's house
    /// rule for per-mille arithmetic (`effect-atom/definitions.md` §2: "rounded half away from zero,
    /// exactly once"). It was TRUNCATING, and truncation is not a neutral choice at small
    /// coefficients: with `recovery.scaleMilli = 374`, any edge with `kMilli &lt;= 2` scaled to
    /// **exactly zero** — a silently dead edge that the float POC still honoured, which is what
    /// `ResolverMatchesSimulatorTests` was reporting as a 22% divergence on `resource.regen.poise`
    /// (found 2026-09-02, Phase 0 six-resource coverage). Measured improvement against the POC's
    /// float model: kMilli=5 46.5% -> 7.0%, kMilli=10 19.8% -> 7.0%, kMilli=21 10.9% -> 1.9%, and
    /// unchanged at 0.1-2% for every coefficient above ~30, so large edges are untouched.</summary>
    static long ScaleMilli(long kMilli, long scaleMilli)
    {
        var product = checked(kMilli * scaleMilli);
        return (product + (product >= 0 ? 500 : -500)) / 1000;
    }

    /// <summary>
    /// species-progression SP3.4 (spec-species-layer-projector.md rule 1): internal-visible, not
    /// copied — <c>SpeciesLayerProjector.ProjectEmpire</c> calls this SAME function for the "same
    /// coefficient rule" its own parity claim depends on. No second implementation.
    /// </summary>
    internal static long EffectiveKMilli(AptitudeTuning tuning, AptitudeEdge edge)
    {
        var isRecovery = tuning.Recovery.Families.Any(f => edge.Channel.StartsWith(f, StringComparison.Ordinal));
        if (isRecovery) return ScaleMilli(edge.KMilli, tuning.Recovery.ScaleMilli);

        // tuning.Mitigation.ScaleMilli — Recovery's own sibling dial (class-system-todo.md P8.3,
        // AptitudeMitigation's own doc comment): the SAME termination invariant also depends on
        // defense/dodge/parry/block/absorption/heal-power, channels Recovery's own Families list never
        // reached, so a build whose survival leans on those instead of hp-regen was invisible to the
        // one dial that existed before this task.
        var isMitigation = tuning.Mitigation.Families.Any(f => edge.Channel.StartsWith(f, StringComparison.Ordinal));
        return isMitigation ? ScaleMilli(edge.KMilli, tuning.Mitigation.ScaleMilli) : edge.KMilli;
    }
}
