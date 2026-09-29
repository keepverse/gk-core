using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `core-scorer` (module 1, spec-core-scorer.md §1): the scorer's own closed term
/// vocabulary. Adding a term is a REVIEWED change (a new weight key, a new column in every
/// breakdown, a new line in every trace) — not a balance pass.
/// </summary>
public enum ScoreTerm { HitChance = 0, Objective, Kill, LowHp, CannotCounter, Round, Risk }

/// <summary>
/// combat-ai `core-scorer` §1: identical field list, order and types to the shipped `AiCandidate`
/// (moved from `Battle/Siege/SiegeAi.cs:71-74`, 2026-09-20), so the siege projection in that file's
/// now-shim `AiScoring` is a positional copy and the move is auditable by eye.
/// </summary>
public readonly record struct TargetCandidate(
    string ActorKey, int BaseTier, int Aggression,
    int HitChanceMilli, int ObjectiveClassMilli, bool IsKillingBlow,
    int TargetMissingHpMilli, bool TargetCanCounter, long IncomingThreatMilli);

/// <summary>combat-ai `core-scorer` §1: identical to the shipped `AiScoreBreakdown`
/// (moved from `Battle/Siege/SiegeAi.cs:83-84`, 2026-09-20).</summary>
public readonly record struct ScoreBreakdown(
    long HitChance, long Objective, long Kill, long LowHp, long CannotCounter, long Round, long Risk, long Total);

/// <summary>
/// combat-ai `core-scorer` §1: the scoring half of the shipped `Siege.AiTuning`
/// (`Battle/Siege/SiegeAi.cs:62-63`), with the two siege-geometry fields
/// (`ObjectiveReferenceDistanceCells`, `ThreatRadiusCells`) left behind — those stay siege-local.
/// The two DEAD fields (`StanceDefault`, `AutoResolveHandicapMilli`) are gone from the record as of
/// `stance-wiring` (module 11, CAI3.1), which published `siege.v3.json` without them.
/// `AggressionRange` and `MaxCandidatesScored` are STRUCTURAL: a per-decision work bound and a closed
/// vocabulary width. Neither is a progression ceiling.
/// </summary>
public sealed record ScoringWeights(
    int HitChance, int Objective, int Kill, int LowHp, int CannotCounter, int Round, int Risk, // overflow-bounded: scoring WEIGHT coefficients (~1-120 authored), never a Theta-scaled magnitude — the shipped Weight* fields these replace carried the same exemption by name
    int AggressionRange, int MaxCandidatesScored);

/// <summary>combat-ai `core-scorer` §4: Argmax (the default, RNG-free) or an opt-in seeded weighted
/// pick over the survivors of the tier/keep-percentage cut.</summary>
public enum SelectionMode { Argmax = 0, SeededWeighted = 1 }

/// <summary>
/// combat-ai `core-scorer` §4: the selection stage's own policy. `KeepPctMilli` 1000 keeps only ties
/// with the best (shifted) weight — the identity value, at which the whole pipeline reduces to the
/// shipped `ChooseTarget` exactly. `RngStreamName` is a battle-owned `SeededRng.DeriveStream` name
/// (e.g. `"ai.select"`), read only when `Mode == SeededWeighted`.
/// </summary>
public sealed record SelectionPolicy(SelectionMode Mode, int KeepPctMilli, string RngStreamName)
{
    /// <summary>Argmax, keep only the best — the identity policy every existing caller gets at
    /// migration.</summary>
    public static readonly SelectionPolicy Identity = new(SelectionMode.Argmax, 1000, "");
}

/// <summary>
/// combat-ai `core-scorer` (module 1, spec-core-scorer.md): **the** scorer for every place. Moved out
/// of `Battle/Siege/SiegeAi.cs`'s `AiScoring` on 2026-09-20 — that class is now a thin forwarding
/// shim over this one, kept only so the three existing siege suites
/// (`SiegeAiTests`/`SiegeAiIntentSourceTests`/`SiegeAiLiveWiringTests`) need no edit. This is the
/// ONLY additive score in the repo; a second copy anywhere is a SOLID (S) defect
/// (battle-engine-ssot.md §2/§5 Q3/Q4).
///
/// <para><b>No `IBattleView` read anywhere in this file</b> — structurally, not just by discipline:
/// nothing here takes one, so determinism (battle-engine-ssot.md §5 Q6) is provable from the type
/// signatures alone, exactly the property `SiegeAi.cs:6-11` claimed for the pre-move file.</para>
/// </summary>
public static class CandidateScorer
{
    /// <summary>
    /// combat-ai `core-scorer` §10 (moved from `AiScoring.EffectiveTier`, `SiegeAi.cs:88-103`,
    /// 2026-09-20): signed aggression applied INSIDE the tier computation — never as a score bonus,
    /// which is what makes a taunt absolute within its tier and irrelevant outside it. Higher
    /// aggression pulls a candidate into a numerically LOWER (better) effective tier; a negative
    /// aggression (stealth) pushes it into a higher (worse) one. Bounded to `aggressionRange`: the
    /// range IS the vocabulary, not a magnitude a balance pass widens.
    ///
    /// <para>combat-ai `aggression-tier-map` CAI1.13 (spec-aggression-tier-map.md §1): an aggression
    /// OUTSIDE the range now SATURATES onto the edge instead of throwing. The tier set is a CLOSED
    /// VOCABULARY of `2*aggressionRange+1` members — there is no tier −3 to fall off — and
    /// `ai.aggression` composes as a FlatSum channel with no `Cap` (Stacking a +2 taunt channel with a
    /// +1 personality offset is a legal actor, not a corrupt one). A THROW here was M13: a third taunt
    /// ended the battle. `Math.Clamp` because the result is an index into a closed set and the
    /// operation has a name; `saturatedBy` because docs/architecture/numeric-types.md's objection to a clamp is a clamp with NO
    /// SYMPTOM, and a signed overshoot is that symptom (it reaches the decision trace, §4).</para>
    ///
    /// <para>Renamed from the pre-CAI1.13 doc comment's "Bounded to" phrasing, which described the
    /// removed throw.</para>
    /// </summary>
    public static int EffectiveTier(int baseTier, int aggression, int aggressionRange) =>
        EffectiveTier(baseTier, aggression, aggressionRange, out _);

    /// <summary>
    /// CAI1.13 §1: the reporting overload. <paramref name="saturatedBy"/> is the SIGNED overshoot —
    /// `+n` when aggression exceeded the range, `−n` when it fell below, `0` when nothing was lost — so
    /// "my stealth is being ignored" and "my taunt is being ignored" stay distinguishable.
    /// </summary>
    public static int EffectiveTier(int baseTier, int aggression, int aggressionRange, out int saturatedBy)
    {
        // Unchanged, and deliberately still a throw: a non-positive range is MALFORMED CONFIGURATION,
        // not a runtime data value. Module 2's loader already refuses it at parse; this is the last
        // line of defence. The asymmetry with the saturation above is the point — there is no
        // "nearest vocabulary" for an empty one, so there is nothing to saturate ONTO.
        if (aggressionRange <= 0) throw new ArgumentOutOfRangeException(nameof(aggressionRange));

        var used = Math.Clamp(aggression, -aggressionRange, aggressionRange);
        saturatedBy = checked(aggression - used); // 0 when nothing was lost; signed, so direction shows
        return checked(baseTier - used);
    }

    /// <summary>
    /// CAI1.13 §4: how much of an actor's aggression its own tier projection discarded, for a caller
    /// that holds the total but not the tier (the trace line). One implementation, not a second clamp —
    /// this calls <see cref="EffectiveTier(int,int,int,out int)"/> with a structural `baseTier` of 0.
    /// </summary>
    public static int SaturatedByOf(int aggression, int aggressionRange)
    {
        EffectiveTier(baseTier: 0, aggression, aggressionRange, out var saturatedBy);
        return saturatedBy;
    }

    /// <summary>
    /// combat-ai `core-scorer` §2 (moved from `AiScoring.Score`, `SiegeAi.cs:110-123`, 2026-09-20):
    /// the additive score. Every term is `long`-widened before summing, `checked` throughout — an
    /// overflow throws rather than silently inverting a comparison, which is the hardest possible bug
    /// to attribute in an AI (it would reliably pick the WORST option and look correct while doing
    /// it). Accumulation order is fixed — hit-chance, objective, kill, low-HP, cannot-counter, round,
    /// then `-risk` — a contract, not a style choice: the sum is `checked`, so reordering could change
    /// *which* term overflows first.
    /// </summary>
    public static long Score(TargetCandidate c, int currentRound, ScoringWeights w)
    {
        if (currentRound < 0) throw new ArgumentOutOfRangeException(nameof(currentRound));

        long total = 0;
        total = checked(total + (long)w.HitChance * c.HitChanceMilli);
        total = checked(total + (long)w.Objective * c.ObjectiveClassMilli);
        total = checked(total + (long)w.Kill * (c.IsKillingBlow ? 1000 : 0));
        total = checked(total + (long)w.LowHp * c.TargetMissingHpMilli);
        total = checked(total + (long)w.CannotCounter * (c.TargetCanCounter ? 0 : 1000));
        total = checked(total + (long)w.Round * currentRound);
        total = checked(total - (long)w.Risk * c.IncomingThreatMilli);
        return total;
    }

    /// <summary>
    /// combat-ai `core-scorer` §4 (moved from `AiScoring.ChooseTarget`, `SiegeAi.cs:125-146`,
    /// 2026-09-20, and widened with the selection stage): Dill's dual utility, with tier as the rank.
    /// Never a random move: an empty candidate list returns null (the caller falls back to
    /// objective-pathing or holds), and this function has no notion of "no preference" once
    /// candidates exist. Truncates to <see cref="ScoringWeights.MaxCandidatesScored"/> BEFORE scoring
    /// — the structural work bound, applied in caller-supplied (i.e. already-meaningful) order. The
    /// cap-before-per-candidate-work placement is `target-stage`'s (module 1's own `TargetStage`); this
    /// overload keeps the shipped truncate-then-score shape so existing siege behaviour is unchanged
    /// until that stage lands.
    ///
    /// <para>1. <b>Veto</b> is the caller's own filtering (phase A upstream) — nothing that reaches
    /// here is vetoed again. 2. <b>Rank</b>: keep only candidates at the best effective tier — the
    /// shipped `Min`/`Where` pair. 3. <b>Cut</b>: shift every surviving score so the minimum is 1 —
    /// `w_i = score_i - min + 1` (mandatory, not cosmetic: the risk term subtracts, so a raw score is
    /// routinely negative and a percentage cut on a negative number is meaningless) — then drop any
    /// `w_i` below `best * KeepPctMilli / 1000` (integer division last, once, on the shifted weights).
    /// 4. <b>Select</b>: `Argmax` — highest score, ties broken by `StringComparer.Ordinal` on
    /// `ActorKey`, byte-for-byte the shipped `OrderByDescending(...).ThenBy(...)` pair, reads no RNG.
    /// `SeededWeighted` — order the survivors by `ActorKey` ordinal FIRST (a draw over an unordered set
    /// is not deterministic), then draw one index by cumulative weight from
    /// `SeededRng.DeriveStream(runSeed, RngStreamName)`. Never `System.Random`, never a clock.</para>
    ///
    /// <para>At `selection == null` (== <see cref="SelectionPolicy.Identity"/>, `Argmax`/1000) the
    /// pipeline reduces to the shipped `ChooseTarget` exactly — the Cut step's threshold equals the
    /// shifted best, so only ties with the best score survive, and Argmax's own ordinal tie-break
    /// picks among them identically to the pre-move code.</para>
    /// </summary>
    public static TargetCandidate? ChooseTarget(
        IReadOnlyList<TargetCandidate> candidates, int currentRound, ScoringWeights w,
        SelectionPolicy? selection = null, ulong runSeed = 0) =>
        ChooseTargetInto(new SelectionScratch(), candidates, currentRound, w, selection, runSeed);

    /// <summary>
    /// combat-ai `decision-perf` CAI1.14 (site 4, spec-decision-perf.md): the same pipeline as
    /// <see cref="ChooseTarget"/>, writing every intermediate into a caller-supplied
    /// <see cref="SelectionScratch"/> so a warm scratch allocates ZERO bytes per decision. Candidates
    /// are read by INDEX — the pre-CAI1.14 body materialised `Take`/`inTier`/`survivors`/`cut` on every
    /// call. Arithmetic, `checked` placement and the ordinal tie-break are unchanged, term for term;
    /// `poolCount = min(Count, MaxCandidatesScored)` is the same first-n set `Take` produced.
    /// </summary>
    public static TargetCandidate? ChooseTargetInto(
        SelectionScratch scratch,
        IReadOnlyList<TargetCandidate> candidates, int currentRound, ScoringWeights w,
        SelectionPolicy? selection = null, ulong runSeed = 0)
    {
        if (scratch is null) throw new ArgumentNullException(nameof(scratch));
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (candidates.Count == 0) return null;
        selection ??= SelectionPolicy.Identity;

        scratch.Begin();
        try
        {
            var poolCount = candidates.Count > w.MaxCandidatesScored ? w.MaxCandidatesScored : candidates.Count;

            var bestTier = int.MaxValue;
            for (var i = 0; i < poolCount; i++)
            {
                var tier = EffectiveTier(candidates[i].BaseTier, candidates[i].Aggression, w.AggressionRange);
                if (tier < bestTier) bestTier = tier;
            }

            // Rank: only candidates at the best effective tier, kept in pool order.
            for (var i = 0; i < poolCount; i++)
            {
                var c = candidates[i];
                if (EffectiveTier(c.BaseTier, c.Aggression, w.AggressionRange) != bestTier) continue;
                scratch.InTier.Add(i);
                scratch.InTierScore.Add(Score(c, currentRound, w));
            }

            // Cut: shift so the minimum surviving score is 1, then keep only weights at or above
            // best * KeepPctMilli / 1000 (integer division runs last, once).
            var minScore = long.MaxValue;
            for (var i = 0; i < scratch.InTier.Count; i++)
                if (scratch.InTierScore[i] < minScore) minScore = scratch.InTierScore[i];
            scratch.MinScore = minScore;

            var bestWeight = long.MinValue;
            for (var i = 0; i < scratch.InTier.Count; i++)
            {
                var weight = scratch.WeightAt(i);
                if (weight > bestWeight) bestWeight = weight;
            }

            var threshold = checked(bestWeight * selection.KeepPctMilli) / 1000;
            for (var i = 0; i < scratch.InTier.Count; i++)
                if (scratch.WeightAt(i) >= threshold) scratch.Cut.Add(i);

            return selection.Mode == SelectionMode.SeededWeighted
                ? WeightedPick(candidates, scratch, runSeed, selection.RngStreamName)
                : Argmax(candidates, scratch);
        }
        finally { scratch.End(); }
    }

    static TargetCandidate? Argmax(IReadOnlyList<TargetCandidate> candidates, SelectionScratch scratch)
    {
        var bestSlot = -1;
        for (var slot = 0; slot < scratch.Cut.Count; slot++)
        {
            if (bestSlot < 0) { bestSlot = slot; continue; }

            var score = scratch.InTierScore[scratch.Cut[slot]];
            var bestScore = scratch.InTierScore[scratch.Cut[bestSlot]];
            if (score > bestScore
                || (score == bestScore && string.CompareOrdinal(
                        candidates[scratch.InTier[scratch.Cut[slot]]].ActorKey,
                        candidates[scratch.InTier[scratch.Cut[bestSlot]]].ActorKey) < 0))
                bestSlot = slot;
        }
        return bestSlot < 0 ? null : candidates[scratch.InTier[scratch.Cut[bestSlot]]];
    }

    /// <summary>Seeded weighted pick over the CUT set, ordered by `ActorKey` ordinal first (a draw over
    /// an unordered set is not deterministic). CAI1.14: the ordering is an in-place insertion sort over
    /// the scratch's own index list — no `List.Sort` delegate, no second list — and `ActorKey` is
    /// unique, so the result matches the unstable `List.Sort` it replaced.</summary>
    static TargetCandidate? WeightedPick(
        IReadOnlyList<TargetCandidate> candidates, SelectionScratch scratch, ulong runSeed, string rngStreamName)
    {
        var cut = scratch.Cut;
        if (cut.Count == 0) return null;

        for (var i = 1; i < cut.Count; i++)
        {
            var moved = cut[i];
            var j = i - 1;
            while (j >= 0 && string.CompareOrdinal(
                       candidates[scratch.InTier[cut[j]]].ActorKey,
                       candidates[scratch.InTier[moved]].ActorKey) > 0)
            {
                cut[j + 1] = cut[j];
                j--;
            }
            cut[j + 1] = moved;
        }

        var totalWeight = 0L;
        for (var i = 0; i < cut.Count; i++)
            totalWeight = checked(totalWeight + scratch.WeightAt(cut[i]));

        var rng = SeededRng.DeriveStream(runSeed, rngStreamName);
        var roll = rng.NextInt(checked((int)totalWeight));

        var cumulative = 0L;
        for (var i = 0; i < cut.Count; i++)
        {
            cumulative = checked(cumulative + scratch.WeightAt(cut[i]));
            if (roll < cumulative) return candidates[scratch.InTier[cut[i]]];
        }
        return candidates[scratch.InTier[cut[^1]]]; // unreachable: roll < totalWeight == final cumulative
    }

    /// <summary>
    /// combat-ai `core-scorer` §2 (moved from `AiScoring.ScoreBreakdownOf`, `SiegeAi.cs:155-164`,
    /// 2026-09-20): every individual weighted term <see cref="Score"/> sums, broken out for a trace.
    /// <see cref="ScoreBreakdown.Total"/> calls <see cref="Score"/> directly rather than re-summing the
    /// terms here — a second accumulation could overflow/round differently than the tested,
    /// already-shipped one.
    /// </summary>
    public static ScoreBreakdown ScoreBreakdownOf(TargetCandidate c, int currentRound, ScoringWeights w) =>
        new(
            HitChance: checked((long)w.HitChance * c.HitChanceMilli),
            Objective: checked((long)w.Objective * c.ObjectiveClassMilli),
            Kill: checked((long)w.Kill * (c.IsKillingBlow ? 1000 : 0)),
            LowHp: checked((long)w.LowHp * c.TargetMissingHpMilli),
            CannotCounter: checked((long)w.CannotCounter * (c.TargetCanCounter ? 0 : 1000)),
            Round: checked((long)w.Round * currentRound),
            Risk: checked((long)w.Risk * c.IncomingThreatMilli),
            Total: Score(c, currentRound, w));

    /// <summary>combat-ai `core-scorer` §2 (moved from `AiScoring.TopThree`, `SiegeAi.cs:169-176`,
    /// 2026-09-20): the top three scored candidates with their full per-term breakdown,
    /// ordinal-tie-broken. Pure; costs nothing until a caller wires it in.
    ///
    /// <para>combat-ai `decision-perf` CAI1.14 (site 4): delegates to <see cref="TopThreeInto"/>, so the
    /// only allocation left is the result list a caller asked for by type.</para></summary>
    public static IReadOnlyList<(string ActorKey, ScoreBreakdown Breakdown)> TopThree(
        IReadOnlyList<TargetCandidate> candidates, int currentRound, ScoringWeights w)
    {
        var buffer = new (string ActorKey, ScoreBreakdown Breakdown)[3];
        var count = TopThreeInto(candidates, currentRound, w, buffer);
        var result = new List<(string ActorKey, ScoreBreakdown Breakdown)>(count);
        for (var i = 0; i < count; i++) result.Add(buffer[i]);
        return result;
    }

    /// <summary>
    /// combat-ai `decision-perf` CAI1.14 (site 4, spec-decision-perf.md): the same three, written into
    /// a CALLER-SUPPLIED buffer so a hot caller (siege's trace line) reuses one array per battle
    /// instead of building a `Select`/`OrderBy`/`ThenBy`/`Take`/`ToList` chain per decision. Returns the
    /// number of slots written (`0..min(3, candidates.Count, destination.Length)`).
    ///
    /// <para><b>Identity, not an approximation.</b> The shipped chain is
    /// `OrderByDescending(Total).ThenBy(ActorKey, Ordinal)`, and `ActorKey` is unique, so the order is
    /// TOTAL — picking the best not-yet-picked candidate three times therefore reproduces it exactly,
    /// including every tie (`A_battle_with_a_trace_still_records_the_same_top_three`). No LINQ, and no
    /// per-candidate materialisation: each slot rescans, which is 3·n O(1) scoring calls, cheaper than
    /// the chain it replaces for the small candidate sets this runs on.</para>
    /// </summary>
    public static int TopThreeInto(
        IReadOnlyList<TargetCandidate> candidates, int currentRound, ScoringWeights w,
        (string ActorKey, ScoreBreakdown Breakdown)[] destination)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        var take = destination.Length < 3 ? destination.Length : 3;
        if (candidates.Count < take) take = candidates.Count;

        Span<int> picked = stackalloc int[3];
        picked[0] = -1; picked[1] = -1; picked[2] = -1;

        for (var slot = 0; slot < take; slot++)
        {
            var best = -1;
            for (var i = 0; i < candidates.Count; i++)
            {
                var alreadyPicked = false;
                for (var p = 0; p < slot; p++)
                    if (picked[p] == i) { alreadyPicked = true; break; }
                if (alreadyPicked) continue;

                if (best < 0 || Precedes(candidates, i, best, currentRound, w)) best = i;
            }

            if (best < 0) return slot;
            picked[slot] = best;
            destination[slot] = (candidates[best].ActorKey, ScoreBreakdownOf(candidates[best], currentRound, w));
        }

        return take;
    }

    /// <summary>The shipped ordering, spelled once: higher total first, then `ActorKey` ordinal.</summary>
    static bool Precedes(
        IReadOnlyList<TargetCandidate> candidates, int a, int b, int currentRound, ScoringWeights w)
    {
        var totalA = ScoreBreakdownOf(candidates[a], currentRound, w).Total;
        var totalB = ScoreBreakdownOf(candidates[b], currentRound, w).Total;
        if (totalA != totalB) return totalA > totalB;
        return string.CompareOrdinal(candidates[a].ActorKey, candidates[b].ActorKey) < 0;
    }

    /// <summary>
    /// combat-ai `core-scorer` §2 (moved from `AiScoring.FormatTopThree`, `SiegeAi.cs:185-189`,
    /// 2026-09-20): formats the top-three-with-breakdown as one line for a trace. Generated text —
    /// never asserted by a guardrail beyond the exact migration-fidelity check that proves the move
    /// (validation-ssot.md).
    /// </summary>
    public static string FormatTopThree(IReadOnlyList<(string ActorKey, ScoreBreakdown Breakdown)> topThree) =>
        FormatTopThree(topThree, topThree.Count);

    /// <summary>CAI1.14: the same line over the first <paramref name="count"/> slots of a reusable
    /// caller buffer, so a hot caller does not have to hand over an exactly-sized list.</summary>
    public static string FormatTopThree(
        IReadOnlyList<(string ActorKey, ScoreBreakdown Breakdown)> topThree, int count)
    {
        if (topThree is null) throw new ArgumentNullException(nameof(topThree));
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < count; i++)
        {
            if (i > 0) builder.Append(' ');
            var t = topThree[i];
            builder.Append($"#{i + 1}={t.ActorKey}(hit={t.Breakdown.HitChance},obj={t.Breakdown.Objective}," +
                $"kill={t.Breakdown.Kill},lowhp={t.Breakdown.LowHp},cc={t.Breakdown.CannotCounter}," +
                $"rnd={t.Breakdown.Round},risk={t.Breakdown.Risk},total={t.Breakdown.Total})");
        }
        return builder.ToString();
    }
}

/// <summary>
/// combat-ai `decision-perf` CAI1.14 (site 4, spec-decision-perf.md): the reusable scratch
/// <see cref="CandidateScorer.ChooseTargetInto"/> writes into, so a deciding owner (one per battle, or
/// one per policy instance) pays for the rank/cut/select lists ONCE for its whole life instead of four
/// `List` allocations per decision. `Clear()` keeps capacity, so once warm the pipeline allocates
/// nothing.
///
/// <para><b>One owner, one decision at a time.</b> The lists hold this decision's candidate indices,
/// so a nested decision would overwrite the outer decision's state and silently change its answer —
/// corrupt, not merely slower. <see cref="Begin"/> throws on re-entry rather than sharing the buffers
/// (<c>Re_entering_a_selection_scratch_throws_instead_of_sharing_it</c>); production re-entry depth is
/// 0 (`overlay-control-loops.md` §6 rule 7), and <see cref="End"/> closes the window in a `finally`.</para>
/// </summary>
public sealed class SelectionScratch
{
    /// <summary>Candidate indices at the best effective tier, in pool order; <see cref="InTierScore"/>
    /// is parallel to it.</summary>
    internal readonly List<int> InTier = new();
    internal readonly List<long> InTierScore = new();

    /// <summary>Slots into <see cref="InTier"/> that survived the keep-percentage cut.</summary>
    internal readonly List<int> Cut = new();

    /// <summary>The minimum score across <see cref="InTier"/>, the shift origin of every weight.</summary>
    internal long MinScore;

    bool _busy;

    internal void Begin()
    {
        if (_busy)
            throw new InvalidOperationException(
                "SelectionScratch is single-decision scratch and is not re-entrant: a nested " +
                "ChooseTargetInto would overwrite the outer decision's candidate state. Give each " +
                "deciding owner its own scratch and do not call back into the same one.");
        _busy = true;
        InTier.Clear();
        InTierScore.Clear();
        Cut.Clear();
        MinScore = 0;
    }

    internal void End() => _busy = false;

    /// <summary>`score - min + 1`, the shifted weight the cut and the weighted pick both read — the
    /// same expression the pre-CAI1.14 body stored beside each survivor, recomputed from the retained
    /// score so no parallel weight list has to exist.</summary>
    internal long WeightAt(int slot) => checked(InTierScore[slot] - MinScore + 1);
}
