using System.Text.Json;
using System.Text.Json.Serialization;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// class-system-todo.md V1 — machine-readable output for `predict` / `trinity` / `marginal`, so later
/// phases can diff a checked-in baseline instead of reading a console table. Extends the existing
/// `--csv`/`--out` flag: pass `--json` alongside `--out <path>` to write one of these shapes instead of
/// CSV. No shape here is invented ahead of what a later phase actually diffs against — each record
/// mirrors data the console output already prints, just structured instead of formatted.
/// </summary>
public static class JsonEmit
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static void Write(string path, object document)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(document, document.GetType(), Options));
    }

    // ── predict ──────────────────────────────────────────────────────────────────────────────────

    public sealed record PredictArrow(
        int Theta, string Attacker, string Defender,
        double PredictedWinShareA, double? SimulatedWinShareA, double? Residual,
        double RoundsA, double RoundsB, double NetAttritionA, double NetAttritionB,
        double? SimMedianRounds, bool NeverEnds);

    public sealed record ResidualSummary(double Mean, double Max, int Count);

    public sealed record PredictDocument(
        string Model, string ModelDescription, IReadOnlyList<int> Thetas, IReadOnlyList<string> Archetypes,
        bool Verified, int? Trials, IReadOnlyList<PredictArrow> Arrows,
        ResidualSummary? Residual, int UnendingCount);

    // ── trinity ──────────────────────────────────────────────────────────────────────────────────

    public sealed record TrinityStep(int Index, double WinAgainstPrev, double DistToPrev, IReadOnlyDictionary<string, double> Points);

    public sealed record TrinityChain(string StartedFrom, IReadOnlyList<TrinityStep> Steps, int CycleStart, int CycleLength, bool IsCycle, bool IsFixedPoint);

    public sealed record DominanceMatrixDocument(IReadOnlyList<string> Names, double[][] Wins, bool[][] Unending);

    /// <summary>What this run could and could not measure — the honest half of the acceptance
    /// criterion (class-system-map.md §4b: a red dominance row is an upper bound on severity, not a
    /// verdict, precisely because of what this block reports). `elementAxis` is read structurally, not
    /// asserted: `predict`/`trinity`/`marginal` do not vary element type today, so it is always
    /// "neutral" until P8.1 wires it live. `reservedFamilies` is derived from the model's own edges
    /// filtered by whether an action economy is active in this run — not a hand-copied list — so it
    /// tracks the tuning file's edges rather than a snapshot of them.</summary>
    public sealed record CoverageBlock(string ElementAxis, bool ActionsActive, IReadOnlyList<string> ReservedFamilies);

    public sealed record TrinityDocument(
        string Model, int Theta, IReadOnlyList<TrinityChain> Chains,
        DominanceMatrixDocument DominanceMatrix, IReadOnlyList<string> DominantCorners,
        CoverageBlock Coverage);

    // ── marginal ─────────────────────────────────────────────────────────────────────────────────

    public sealed record MarginalRow(
        string Aptitude, double CurrentPoints, IReadOnlyList<double> DeltaWinPerOpponent,
        double Best, double Worst, double Spread, bool Mandatory, bool Dead);

    public sealed record MarginalSubject(string Subject, IReadOnlyList<string> Opponents, IReadOnlyList<MarginalRow> Rows);

    public sealed record MarginalDocument(string Model, int Theta, IReadOnlyList<MarginalSubject> Subjects);

    /// <summary>Structural, not hand-copied: a family is reserved for THIS run when every edge that
    /// feeds it prices an action (resource cost, cooldown, effectiveness, move range) and no
    /// <see cref="ActionSet"/> is active to price it. hp/shield/combat families are never reserved —
    /// their damage math runs whether or not the action layer exists. Mirrors the reasoning already
    /// written in tuning/aptitudes.v1.json's own `_meta.measurable` prose, computed instead of quoted
    /// so it cannot go stale the way that prose can.</summary>
    public static IReadOnlyList<string> ReservedFamilies(AptitudeModel model, bool actionsActive)
    {
        if (actionsActive) return Array.Empty<string>();

        bool IsActionPriced(string channel) =>
            channel.StartsWith("resource.efficiency.", StringComparison.Ordinal) ||
            channel.StartsWith("skill.cooldown.", StringComparison.Ordinal) ||
            channel.StartsWith("skill.effectiveness.", StringComparison.Ordinal) ||
            channel == "move.range" ||
            (channel.StartsWith("resource.max.", StringComparison.Ordinal) &&
             !channel.EndsWith(".hp", StringComparison.Ordinal)) ||
            (channel.StartsWith("resource.regen.", StringComparison.Ordinal) &&
             !channel.EndsWith(".hp", StringComparison.Ordinal));

        return model.Edges
            .Select(e => e.Channel)
            .Where(IsActionPriced)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }
}
