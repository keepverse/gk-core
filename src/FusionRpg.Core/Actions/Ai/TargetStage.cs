using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `core-scorer` (module 1, CAI1.2, spec-core-scorer.md §3): the cap moves in front of the
/// work. Today's shipped shape is one loop that filters AND builds a full <see cref="TargetCandidate"/>
/// for every live enemy (`SiegeAiIntentSource.cs`, pre-2026-09-20), with the
/// <c>MaxCandidatesScored</c> truncation happening downstream, inside the scorer. This stage splits
/// that into three ordered phases:
///
/// <list type="number">
/// <item>Phase A — cheap filters, in the view's own listed order: <c>key != self</c>;
/// <c>sideOf(key) != mySide</c>; <paramref name="isReadable"/> (e.g. `DerivedOf(key) is not null` —
/// fogged/unknown candidates never occupy a cap slot).</item>
/// <item>Phase B — TRUNCATE to <c>maxCandidatesScored</c>. The structural work bound, applied HERE,
/// on the already-filtered, view-ordered set.</item>
/// <item>Phase C — per-candidate inputs (hit chance, objective distance, killing blow, threat), built
/// ONLY for the capped set via the caller-supplied <paramref name="tryBuildCandidate"/> delegate.</item>
/// </list>
///
/// <para><b>Why this changes no decision.</b> Because phase A preserves the view's own order, "build
/// all, keep the first N" and "keep the first N, build those" produce the SAME set in the SAME order
/// — a provable identity, not a hoped-for one (<c>TargetStageCapTests</c> proves it directly by
/// wiring this stage into the real siege consumer and re-running the existing, unedited
/// `SiegeAiIntentSourceTests`/`SiegeAiLiveWiringTests`). Phase C's expensive per-candidate math (an
/// O(live) threat scan for EACH candidate) now runs O(cap) times instead of O(live) times — the
/// allocation profile of that inner scan is `decision-perf`'s (module 7) job, not this stage's; the
/// cap placement is what makes that later fix bounded.</para>
///
/// <para><b>No `IBattleView` read anywhere in this file</b> — every read is a caller-supplied
/// delegate, matching <see cref="CandidateScorer"/>'s own structural-determinism property
/// (battle-engine-ssot.md §5 Q6).</para>
/// </summary>
public static class TargetStage
{
    /// <summary>Phase C's own seam: build the full scoring inputs for one candidate key, or refuse
    /// (e.g. a race between the cheap phase-A readability check and this heavier read — never expected
    /// in a single-threaded battle, but the contract stays honest either way: a `false` here simply
    /// drops the candidate, exactly as an unreadable one is dropped in phase A).</summary>
    public delegate bool TryBuildCandidate(string candidateKey, out TargetCandidate candidate);

    /// <summary>
    /// Phase A (filter, in view order) + phase B (cap) + phase C (build only the capped set).
    /// </summary>
    /// <param name="actorKey">The deciding actor — excluded from its own candidate set.</param>
    /// <param name="mySide">The deciding actor's own side (<see cref="IBattleView.SideOf"/>).</param>
    /// <param name="liveActorKeys">The view's own listed order (<see cref="IBattleView.LiveActorKeys"/>)
    /// — truncation is stable only because this order is the caller's own, already-meaningful one.</param>
    /// <param name="sideOf">Cheap: <see cref="IBattleView.SideOf"/>, or an equivalent.</param>
    /// <param name="isReadable">Cheap: whether this candidate's data can be read at all (e.g.
    /// `DerivedOf(key) is not null` — false under fog). NEVER the expensive per-candidate build.</param>
    /// <param name="maxCandidatesScored">The structural work bound — a per-decision cap, not a
    /// progression ceiling.</param>
    /// <param name="tryBuildCandidate">Phase C: the expensive per-candidate inputs, built only for the
    /// capped set.</param>
    public static IReadOnlyList<TargetCandidate> BuildCapped(
        string actorKey,
        int mySide,
        IReadOnlyList<string> liveActorKeys,
        Func<string, int> sideOf,
        Func<string, bool> isReadable,
        int maxCandidatesScored,
        TryBuildCandidate tryBuildCandidate)
    {
        var eligible = new List<string>(liveActorKeys.Count);
        var result = new List<TargetCandidate>();
        BuildCappedInto(actorKey, mySide, liveActorKeys, sideOf, isReadable, maxCandidatesScored,
            tryBuildCandidate, eligible, result);
        return result;
    }

    /// <summary>
    /// combat-ai `decision-perf` CAI1.14 (site 3, spec-decision-perf.md): the same three phases writing
    /// into CALLER-SUPPLIED lists, so a hot owner (one per battle) keeps the two buffers for its whole
    /// life — `Clear()` keeps capacity, so a warm pair allocates nothing per decision. Both lists are
    /// cleared on entry; <paramref name="result"/> is left holding exactly phase C's output, in view
    /// order.
    ///
    /// <para>Single-decision scratch: the lists hold THIS decision's keys and candidates, so a nested
    /// decision through the same pair would overwrite them. The owning caller guards that
    /// (<c>SiegeAiIntentSource</c> throws on re-entry); this method itself is not re-entrant.</para>
    /// </summary>
    public static void BuildCappedInto(
        string actorKey,
        int mySide,
        IReadOnlyList<string> liveActorKeys,
        Func<string, int> sideOf,
        Func<string, bool> isReadable,
        int maxCandidatesScored,
        TryBuildCandidate tryBuildCandidate,
        List<string> eligible,
        List<TargetCandidate> result)
    {
        eligible.Clear();
        result.Clear();

        // Phase A: cheap filters, in view order.
        for (var i = 0; i < liveActorKeys.Count; i++)
        {
            var key = liveActorKeys[i];
            if (string.Equals(key, actorKey, StringComparison.Ordinal)) continue;
            if (sideOf(key) == mySide) continue;
            if (!isReadable(key)) continue;
            eligible.Add(key);
        }

        // Phase B: truncate to the structural work bound, on the already view-ordered, filtered set.
        var cappedCount = eligible.Count > maxCandidatesScored ? maxCandidatesScored : eligible.Count;

        // Phase C: build the full candidate ONLY for the capped set.
        for (var i = 0; i < cappedCount; i++)
            if (tryBuildCandidate(eligible[i], out var candidate))
                result.Add(candidate);
    }
}
