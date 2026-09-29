namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// The golden artifact's comparison (<c>gk-core/tools/RpgSim/readback-verdict.md</c> §5, owner ruling **C2 (a)**:
/// golden *and* hash). The golden is a stored verdict at
/// <c>gk-core/tests/fixtures/rpg-scenarios/golden/&lt;scenario-id&gt;.verdict.json</c>, and its role is deliberately
/// narrow:
///
/// <list type="bullet">
/// <item>it pins the <b>stored artifact</b>, so a reviewer can see what the last accepted run read;</item>
/// <item>its <c>digest</c> is what a later run is compared against;</item>
/// <item>it is <b>not</b> the outcome oracle — an outcome-dependent row set is asserted by <c>expect.*</c>,
/// which pins the rule and the attribution rather than a count, so a balance change does not fail the
/// golden for a reason that is not a regression.</item>
/// </list>
///
/// <para><b>What is compared, and what is deliberately not.</b> The digest, the seed, the scenario id, the
/// set of read-backs (each one's name, method and route) and the digest's exclusion FIELDS. Not the readings'
/// <i>values</i>: they carry per-run identities and stamps, and §5 says the golden is not the oracle. A run
/// whose reading set or exclusion list changed is a CONTRACT move and is reported by name; a run whose values
/// moved for a non-determinism reason is what <c>--double-run</c> and <c>ReadbackVerdict</c>'s falsifier are
/// for.</para>
/// </summary>
public static class GoldenVerdict
{
    /// <param name="Same">True when nothing distinguishable moved.</param>
    /// <param name="Moved">One line per moved field, naming what it moved from and to.</param>
    public sealed record Comparison(bool Same, IReadOnlyList<string> Moved);

    public static Comparison Compare(ScenarioVerdict golden, ScenarioVerdict run)
    {
        var moved = new List<string>();

        if (!string.Equals(golden.ScenarioId, run.ScenarioId, StringComparison.Ordinal))
            moved.Add($"scenarioId: '{golden.ScenarioId}' -> '{run.ScenarioId}'");
        if (golden.Seed != run.Seed)
            moved.Add($"seed: {golden.Seed} -> {run.Seed}");
        if (!string.Equals(golden.Digest, run.Digest, StringComparison.Ordinal))
            moved.Add($"digest: {golden.Digest ?? "(none)"} -> {run.Digest ?? "(none)"}");

        // The reading SET is the contract: which read-backs exist, how each was fetched, and from which
        // route. The values are not compared (see the class comment).
        var goldenReadings = golden.Readings.Select(Describe).ToList();
        var runReadings = run.Readings.Select(Describe).ToList();
        if (!goldenReadings.SequenceEqual(runReadings, StringComparer.Ordinal))
            moved.Add($"readings: [{string.Join("; ", goldenReadings)}] -> [{string.Join("; ", runReadings)}]");

        var goldenExclusions = golden.DigestExclusions.Select(e => e.Field).ToList();
        var runExclusions = run.DigestExclusions.Select(e => e.Field).ToList();
        if (!goldenExclusions.SequenceEqual(runExclusions, StringComparer.Ordinal))
            moved.Add($"digestExclusions: [{string.Join(", ", goldenExclusions)}] -> [{string.Join(", ", runExclusions)}]");

        return new Comparison(moved.Count == 0, moved);
    }

    static string Describe(ScenarioReading reading) => $"{reading.Name} {reading.Method} {reading.Source}";

    /// <summary>The report a caller prints. A move is NAMED, never summarised as a boolean: the whole point
    /// of §5's narrow comparison is that a reader can see which field moved without diffing two JSON files.</summary>
    public static string Report(Comparison comparison, string goldenPath) => comparison.Same
        ? $"golden: {goldenPath} — the declared digest, the reading set and the exclusion fields are unchanged"
        : $"golden: {goldenPath} MOVED at {comparison.Moved.Count} field(s):" + Environment.NewLine +
          string.Join(Environment.NewLine, comparison.Moved.Select(m => "  " + m));
}
