using FusionRpg.Core.Power;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// The FREE-BUILD test. With no class gate, nothing stops a player putting a point wherever it pays
/// most — so the distribution is only correct if the answer to "where does it pay most?" **depends on
/// who you are fighting**.
///
/// <para>Two failure modes, and they are opposites:</para>
/// <list type="bullet">
///   <item><b>Mandatory</b> — an aptitude that is the best point against every opponent. Every build
///     takes it, so it is a tax, not a choice.</item>
///   <item><b>Dead</b> — an aptitude that is the best point against none of them. Nobody takes it,
///     so it is not a choice either.</item>
/// </list>
///
/// <para>Both are measured the same way: the marginal win rate of one more point,
/// <c>dW/d(share_i)</c>, taken against every opponent. This is a finite difference on
/// <see cref="Analytic.Predict"/> — it needs no trials, so the whole 12 x N table costs milliseconds
/// and is exactly reproducible. A sampled version of this table would be buried in noise: the
/// per-point effect is a fraction of a percent, and 3,000 duels resolve to about 0.9pp.</para>
/// </summary>
public static class Marginal
{
    /// <summary>One aptitude point out of a 100-point allocation. Small enough to read as a
    /// derivative, large enough that the finite difference is not floating-point dust.</summary>
    public const double Delta = 1.0;

    public sealed record Row(string Aptitude, double CurrentPoints, double[] DeltaWinPerOpponent)
    {
        public double Best => DeltaWinPerOpponent.Max();
        public double Worst => DeltaWinPerOpponent.Min();
        /// <summary>How much the answer depends on the opponent. Near zero means this aptitude pays
        /// the same everywhere — which is what a tax looks like, and what a dead stat looks like.</summary>
        public double Spread => Best - Worst;
    }

    public static List<Row> For(Build subject, IReadOnlyList<Build> opponents, AptitudeModel model,
                                PowerLadder ladder, int theta, IReadOnlyList<string> roster)
    {
        var baseline = opponents
            .Select(o => Analytic.Predict(subject.At(theta, model, ladder), o.At(theta, model, ladder)).WinShareA)
            .ToArray();

        var rows = new List<Row>();
        foreach (var apt in roster)
        {
            // Nudge one aptitude by +Delta. Every other share falls because the total rises — that
            // fall IS the opportunity cost, and leaving it out would measure "is this good?" instead
            // of "is this the best place for the point?", which is the only question free build asks.
            var nudged = Clone(subject, roster);
            nudged.Points[apt] = nudged.Points.GetValueOrDefault(apt) + Delta;

            var deltas = new double[opponents.Count];
            for (var i = 0; i < opponents.Count; i++)
            {
                var w = Analytic.Predict(nudged.At(theta, model, ladder), opponents[i].At(theta, model, ladder)).WinShareA;
                deltas[i] = w - baseline[i];
            }
            rows.Add(new Row(apt, subject.Points.GetValueOrDefault(apt), deltas));
        }
        return rows;
    }

    /// <summary>A copy carrying every aptitude in the roster, absent ones at zero — because "what
    /// would my first point in Bulwark buy?" is a question a free build asks constantly, and it is
    /// unanswerable if the aptitude is not on the sheet.</summary>
    static Build Clone(Build b, IReadOnlyList<string> roster)
    {
        var pts = new Dictionary<string, double>(b.Points, StringComparer.Ordinal);
        foreach (var a in roster) pts.TryAdd(a, 0.0);
        return new Build
        {
            Name = b.Name, Element = b.Element, Points = pts,
            HpPerLadder = b.HpPerLadder, DamagePerLadder = b.DamagePerLadder, ShieldPerLadder = b.ShieldPerLadder
        };
    }

    /// <summary>The twelve, in posture order (class-system-ideal.md §4).</summary>
    public static readonly string[] Roster =
    [
        "Might", "Fortitude", "Vigor", "Onslaught",
        "Agility", "Composure", "Pierce", "Focus",
        "Bulwark", "Retribution", "Precision", "Ferocity"
    ];
}
