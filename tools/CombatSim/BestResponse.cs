using FusionRpg.Core.Power;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// THE STRUCTURAL TEST — does the posture trinity survive free build?
/// (class-system-ideal.md §8.8, the biggest open item on that page.)
///
/// <para>The RPS cycle was only ever measured between <b>three named allocations</b>. That is the
/// right question under a class system, where a player picks one of three. It says nothing under free
/// build, where a player moves through all twelve dimensions toward whatever pays — a set can be
/// perfectly cyclic and still sit next door to a build that beats all three.</para>
///
/// <para><b>The test is a fixed-point question.</b> Take a build; find the allocation that best beats
/// it; then find the allocation that best beats <i>that</i>; iterate.</para>
///
/// <list type="bullet">
///   <item><b>It cycles</b> — the sequence returns to a neighbourhood it has already visited. There is
///     no dominant build; the trinity is real structure, whatever names are on it.</item>
///   <item><b>It converges</b> — the sequence settles on one allocation that is its own best response.
///     That is a Nash equilibrium in a one-population game, i.e. <b>one correct build</b>, and the
///     trinity is a story told about three arbitrary samples of a space with one attractor.</item>
/// </list>
///
/// <para>Run on the CLOSED FORM, which is what makes it affordable: a best response is a hill-climb
/// over hundreds of evaluations, and a chain of them is hundreds of thousands. At simulator prices
/// this test is a research project; at closed-form prices it is seconds.</para>
/// </summary>
public static class BestResponse
{
    public sealed record Step(int Index, Dictionary<string, double> Points, double WinAgainstPrev, double DistToPrev);

    /// <summary>Total-variation distance between two allocations, both normalised to 100. Zero means
    /// identical; 100 means disjoint.</summary>
    public static double Distance(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b)
    {
        var keys = a.Keys.Union(b.Keys, StringComparer.Ordinal);
        return keys.Sum(k => Math.Abs(a.GetValueOrDefault(k) - b.GetValueOrDefault(k))) / 2.0;
    }

    /// <summary>
    /// Hill-climb the allocation space for the build that beats <paramref name="target"/> hardest.
    /// Same perturb-and-project machinery the balance search uses, so the space explored is the same
    /// legal space — a best response that cheated the floor would not be a build anyone could make.
    /// </summary>
    public static Build Against(Build target, Build seed, AptitudeModel model, PowerLadder ladder,
                                int theta, ActionSet? actions, Random rng, int restarts, int iters)
    {
        var defender = target.At(theta, model, ladder);
        Build? best = null;
        var bestWin = double.NegativeInfinity;

        for (var r = 0; r < restarts; r++)
        {
            // EVERY restart begins from a fresh RANDOM allocation, never from the target. Seeding the
            // search at the build it is trying to beat is a trap: a mirror match scores exactly 50%,
            // so a climb that fails to improve reports "the target is its own best response" — which
            // reads as convergence but is only a seeding artifact. The balance search already learned
            // this ("each restart begins from a fresh RANDOM allocation, not the seed file").
            var cur = Clone(seed);
            Search.Perturb(new[] { cur }, rng, r == 0 ? 1.2 : 2.2);
            var curWin = Analytic.Predict(cur.At(theta, model, ladder), defender, actions).WinShareA;
            var temp = 0.6;

            for (var i = 0; i < iters; i++)
            {
                var trial = Clone(cur);
                Search.Perturb(new[] { trial }, rng, temp);
                var win = Analytic.Predict(trial.At(theta, model, ladder), defender, actions).WinShareA;
                if (win > curWin) { cur = trial; curWin = win; }
                temp = Math.Max(0.08, temp * 0.94);
            }
            if (curWin > bestWin) { bestWin = curWin; best = cur; }
        }
        return best!;
    }

    /// <summary>
    /// THE DOMINANCE MATRIX — the definitive form of the test, and the one a hill-climb cannot fail to
    /// find. Spike each aptitude to the maximum a legal allocation permits and play every spike against
    /// every other. A <b>dominant row</b> (beats all others) means one build wins the game outright.
    ///
    /// <para>144 closed-form evaluations, instant. This exists because best-response chasing found a
    /// fixed point at <c>Bulwark 55</c> and reported "nothing beats it" — while a direct check showed
    /// <c>Vigor 55</c> beating it 100%. A search that misses a 100% counter is not evidence of absence;
    /// an exhaustive sweep of the corners is.</para>
    ///
    /// <para>The corners are the right sample because the space rewards concentration: every chain that
    /// converged, converged ON a corner.</para>
    /// </summary>
    public static (string[] Names, double[,] Wins, bool[,] Unending) DominanceMatrix(
        Build template, IReadOnlyList<string> roster, AptitudeModel model, PowerLadder ladder,
        int theta, ActionSet? actions, double floor)
    {
        var spikes = roster.Select(apt =>
        {
            var b = Clone(template);
            b.Name = apt;
            // Max legal spike: the floor holds every OTHER aptitude at `floor`, so the spike takes
            // whatever is left. Same legal space the balance search projects into.
            b.Points = roster.ToDictionary(x => x, x => x == apt ? 100.0 - floor * (roster.Count - 1) : floor,
                                           StringComparer.Ordinal);
            return b;
        }).ToList();

        var n = spikes.Count;
        var wins = new double[n, n];
        var unending = new bool[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            if (i == j) { wins[i, j] = 0.5; continue; }
            var pr = Analytic.Predict(spikes[i].At(theta, model, ladder), spikes[j].At(theta, model, ladder), actions);
            wins[i, j] = pr.WinShareA;
            // TERMINATION INVARIANT (§5d): recovery >= damage on BOTH sides means nobody can die.
            unending[i, j] = pr.NetAttritionA <= 0 && pr.NetAttritionB <= 0;
        }
        return (spikes.Select(x => x.Name).ToArray(), wins, unending);
    }

    static Build Clone(Build b) => new()
    {
        Name = b.Name, Element = b.Element,
        Points = new Dictionary<string, double>(b.Points, StringComparer.Ordinal),
        HpPerLadder = b.HpPerLadder, DamagePerLadder = b.DamagePerLadder, ShieldPerLadder = b.ShieldPerLadder
    };

    /// <summary>
    /// Iterate best responses and report whether the chain cycles or converges.
    /// </summary>
    /// <param name="cycleTolerance">How close two allocations must be to count as "the same build".
    /// In total-variation units out of 100, so 8 means the two agree on 92% of their points.</param>
    public static (List<Step> Chain, int CycleStart, int CycleLength) Chase(
        Build start, AptitudeModel model, PowerLadder ladder, int theta, ActionSet? actions,
        Random rng, int depth, int restarts, int iters, double cycleTolerance)
    {
        var chain = new List<Step> { new(0, new(start.Points, StringComparer.Ordinal), 0, 0) };
        var cur = start;

        for (var d = 1; d <= depth; d++)
        {
            var next = Against(cur, cur, model, ladder, theta, actions, rng, restarts, iters);
            var win = Analytic.Predict(next.At(theta, model, ladder), cur.At(theta, model, ladder), actions).WinShareA;
            var dist = Distance(next.Points, cur.Points);
            chain.Add(new Step(d, new(next.Points, StringComparer.Ordinal), win, dist));

            // Revisit check against EVERY earlier step, not just the previous one — a 2-cycle and a
            // 3-cycle are both cycles, and only comparing to the last step would miss both.
            for (var k = 0; k < chain.Count - 1; k++)
                if (Distance(chain[k].Points, next.Points) <= cycleTolerance)
                    return (chain, k, chain.Count - 1 - k);

            cur = next;
        }
        return (chain, -1, 0);
    }
}
