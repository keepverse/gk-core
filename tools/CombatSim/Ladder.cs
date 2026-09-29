using System.Globalization;
using FusionRpg.Core.Power;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// The scale-invariance test. Runs the same posture matrix at several Θ with the SAME point shares.
/// If the read modes are right, win shares hold across the ladder; if a family is on the wrong scale,
/// its contribution drifts against everything else and the cycle bends or collapses as Θ climbs.
/// That drift IS the answer to the unitClass question — it cannot be argued, only measured.
/// </summary>
public static class LadderTest
{
    public static void Run(AptitudeModel model, IReadOnlyList<Build> builds, IReadOnlyList<int> thetas,
                           int trials, int seed, int rounds, PowerLadder ladder, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine($"  MODEL  {model.Name}");
        if (!string.IsNullOrWhiteSpace(model.Description)) w.WriteLine($"         {model.Description}");
        w.WriteLine($"  {trials:N0} duels per cell · builds {string.Join(", ", builds.Select(b => b.Name))}");
        w.WriteLine();

        var header = "  " + "Θ".PadRight(8) + "P(Θ)".PadLeft(12);
        var pairs = new List<(int I, int J)>();
        for (var i = 0; i < builds.Count; i++)
            for (var j = i + 1; j < builds.Count; j++) pairs.Add((i, j));
        foreach (var (i, j) in pairs)
            header += $"{builds[i].Name + " v " + builds[j].Name,20}";
        w.WriteLine(header);
        w.WriteLine("  " + new string('-', header.Length));

        var first = new Dictionary<(int, int), double>();
        var drift = 0.0;
        foreach (var theta in thetas)
        {
            var row = "  " + theta.ToString(CultureInfo.InvariantCulture).PadRight(8)
                      + ladder.Value(theta).ToString("N0").PadLeft(12);
            foreach (var (i, j) in pairs)
            {
                var a = builds[i].At(theta, model, ladder);
                var b = builds[j].At(theta, model, ladder);
                var share = Simulator.Duel(a, b, trials, seed, rounds).AWinShare;
                if (!first.ContainsKey((i, j))) first[(i, j)] = share;
                else drift = Math.Max(drift, Math.Abs(share - first[(i, j)]));
                row += $"{share,20:P1}";
            }
            w.WriteLine(row);
        }

        w.WriteLine();
        w.WriteLine($"  MAX DRIFT from the Θ={thetas[0]} row:  {drift:P1}");
        w.WriteLine(drift <= 0.10
            ? "  → scale-invariant. These read modes hold across the ladder."
            : "  → NOT invariant. At least one family is on the wrong scale — its contribution");
        if (drift > 0.10)
            w.WriteLine("     grows or shrinks against the rest as Θ climbs, which is exactly what a");
        if (drift > 0.10)
            w.WriteLine("     wrong unitClass looks like from the outside.");
    }
}
