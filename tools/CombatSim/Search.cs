using FusionRpg.Core.Power;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// Searches aptitude point allocations for a HEALTHY rock-paper-scissors cycle.
///
/// <para>"Healthy" is two conditions, not one. The cycle must <b>close</b> (each posture beats exactly
/// one and loses to exactly one), and the win shares must be <b>non-degenerate</b> — a 100/0 arrow is
/// a lookup table, not a matchup, because the result is knowable before the fight starts. The target
/// band is centred on <see cref="Target"/> with a penalty that grows as any arrow approaches certainty.</para>
/// </summary>
public static class Search
{
    public const double Target = 0.65;

    /// <summary>No aptitude may fall below this share of its build's 100 points. A cycle balanced by
    /// abandoning a posture's signature aptitude is balanced between builds that no longer represent
    /// their postures — the allocation must stay recognisably distributed, not spiked.</summary>
    public const double MinShare = 15.0;

    public sealed record Candidate(
        Dictionary<string, Dictionary<string, double>> Points,
        double[] Arrows, double Score, double Spread)
    {
        /// <summary>Lowest single-aptitude share across all builds — the distribution health check.</summary>
        public double MinAllocation => Points.Values.SelectMany(p => p.Values).Min();
    }

    /// <summary>The three arrows the cycle needs, as (winner, loser) build indices.</summary>
    static readonly (int W, int L)[] Cycle = { (0, 2), (2, 1), (1, 0) }; // FORCE>BASTION>FINESSE>FORCE

    public static Candidate Evaluate(IReadOnlyList<Build> builds, AptitudeModel model, PowerLadder ladder,
                                     int theta, int trials, int seed, int rounds)
        => Score(builds, model, ladder, theta,
                 (a, b) => Simulator.Duel(a, b, trials, seed, rounds).AWinShare);

    /// <summary>
    /// The same fitness, read from the CLOSED FORM instead of from trials. No RNG, no duels — so it
    /// is deterministic (the same allocation always scores the same, which a sampled fitness never
    /// is) and several orders of magnitude faster, which is what lets the search SOLVE for a balanced
    /// allocation rather than hunt for one. Valid only where the closed form is valid: single phase,
    /// so no depleting pools (Analytic's class remarks).
    /// </summary>
    public static Candidate EvaluateAnalytic(IReadOnlyList<Build> builds, AptitudeModel model,
                                             PowerLadder ladder, int theta, ActionSet? actions = null)
        => Score(builds, model, ladder, theta, (a, b) => Analytic.Predict(a, b, actions).WinShareA);

    static Candidate Score(IReadOnlyList<Build> builds, AptitudeModel model, PowerLadder ladder,
                           int theta, Func<Archetype, Archetype, double> winShare)
    {
        var arrows = new double[3];
        for (var i = 0; i < Cycle.Length; i++)
        {
            var (w, l) = Cycle[i];
            arrows[i] = winShare(builds[w].At(theta, model, ladder), builds[l].At(theta, model, ladder));
        }
        var pts = builds.ToDictionary(b => b.Name,
            b => new Dictionary<string, double>(b.Points, StringComparer.Ordinal), StringComparer.Ordinal);
        // Focus feeds no combat channel at all, so a duel can never value it. Holding it to the floor
        // would be scoring the search against a measurement it cannot make.
        var starved = pts.Values
            .SelectMany(p => p.Where(kv => kv.Key != "Focus"))
            .Where(kv => kv.Value < MinShare).Sum(kv => MinShare - kv.Value);
        return new Candidate(pts, arrows, Score(arrows) + starved * 0.03, Spread(arrows));
    }

    /// <summary>
    /// Lower is better. A broken arrow (≤0.5 — the wrong posture won) is disqualifying, scored far
    /// worse than any legal cycle so no amount of good behaviour elsewhere can buy it back.
    /// </summary>
    public static double Score(double[] arrows)
    {
        var penalty = 0.0;
        foreach (var s in arrows)
        {
            if (s <= 0.5) penalty += 10.0 + (0.5 - s);      // arrow reversed: the cycle is broken
            else penalty += Math.Abs(s - Target);
            if (s > 0.90) penalty += (s - 0.90) * 5.0;      // degenerate: a matchup you can read in advance
        }
        return penalty;
    }

    /// <summary>Max − min across the arrows. A cycle where one arrow is far harder than the others is
    /// still lopsided even when all three point the right way.</summary>
    public static double Spread(double[] arrows) => arrows.Max() - arrows.Min();

    /// <summary>
    /// Perturb, then project back into the LEGAL allocation space: every combat aptitude keeps at
    /// least <see cref="MinShare"/> of its build's 100 points. Constraining the space beats
    /// penalising afterwards — a penalty leaves the degenerate spikes reachable, and they are strong
    /// attractors, so the hill-climb keeps rediscovering them and paying the fine.
    /// </summary>
    public static void Perturb(IReadOnlyList<Build> builds, Random rng, double strength)
    {
        foreach (var b in builds)
        {
            var keys = b.Points.Keys.ToList();
            foreach (var k in keys)
                b.Points[k] = Math.Max(0.5, b.Points[k] * Math.Exp((rng.NextDouble() - 0.5) * 2.0 * strength));
            Project(b, keys);
        }
    }

    /// <summary>Normalise to 100 and enforce the per-aptitude floor, alternating until both hold.
    /// Focus is exempt — it feeds no combat channel, so a duel cannot value it.</summary>
    static void Project(Build b, List<string> keys)
    {
        for (var pass = 0; pass < 8; pass++)
        {
            var sum = b.Points.Values.Sum();
            foreach (var k in keys) b.Points[k] = b.Points[k] / sum * 100.0;
            var fixedUp = false;
            foreach (var k in keys)
            {
                var floor = k == "Focus" ? 3.0 : MinShare;
                if (b.Points[k] < floor) { b.Points[k] = floor; fixedUp = true; }
            }
            if (!fixedUp) return;
        }
    }
}
