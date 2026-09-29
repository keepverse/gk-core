using System.Text.Json;

namespace FusionRpg.Core.Diagnostics;

/// <summary>
/// The ceiling a lawn perf feature is measured against — the lawn program's own perf budget
/// (<c>data/tuning/lawn-perf-budget.v{n}.json</c> — the revision is the CONSTANT
/// <see cref="LawnPerfBudgetFiles.Current"/>).
///
/// <para><b>Why this is data.</b> The number decides whether a feature ships, which is this repo's own
/// test for a balance-surface number (<c>tunables-ssot.md</c>): a pass would want to change it. Before
/// this record the <=6% ceiling lived in a plan document, in a commit message and in code comments —
/// three places a gate cannot read.</para>
///
/// <para><b>Both gates are required (owner ruling 2026-09-16).</b> Share <b>and</b> fps, on the same
/// reference scenario. <see cref="MinFpsRatioOfOff"/> is <c>null</c> while unmeasured, and that is a
/// declaration, not an absence — <see cref="LawnPerfBudgetTuningLoader"/> refuses a document that omits
/// the key entirely. A gate that reads this file must treat <c>null</c> as "cannot pass yet"; see
/// <see cref="IsFpsGateMeasured"/>.</para>
/// </summary>
/// <param name="PipelineSharePercent">Share of wall clock the measured section may take at
/// <paramref name="ReferenceZombies"/> zombies.</param>
/// <param name="ReferenceZombies">The reference scenario's zombie count — what "the reference scenario"
/// means, so a measurement taken elsewhere is not comparable.</param>
/// <param name="MinFpsRatioOfOff">On-arm fps as a fraction of the off-arm's fps, or <c>null</c> while
/// unmeasured.</param>
/// <param name="Sections">Per-section shares, keyed by the measured perf section's own name
/// (`lawn.ai.decide` is this program's). A key PRESENT with a <c>null</c> value is *declared but
/// unmeasured* — the file's own contract — so a gate asks <see cref="TryGetSectionShare"/> and treats
/// null as "cannot pass yet"; a key ABSENT means no gate for that section at all. Trailing and optional
/// so every construction that predates it compiles.</param>
public sealed record LawnPerfCeiling(
    double PipelineSharePercent,
    int ReferenceZombies,
    double? MinFpsRatioOfOff,
    IReadOnlyDictionary<string, double?>? Sections = null)
{
    /// <summary>False while the fps half is unmeasured. A gate must not read <c>null</c> as a pass.</summary>
    public bool IsFpsGateMeasured => MinFpsRatioOfOff.HasValue;

    /// <summary>True when the document DECLARES a share for this section, measured or not.</summary>
    public bool TryGetSectionShare(string sectionId, out double? share)
    {
        share = null;
        if (Sections is null || !Sections.TryGetValue(sectionId, out var declared)) return false;
        share = declared;
        return true;
    }

    /// <summary>True only when the section's share is declared AND measured — the only state a gate may
    /// pass on.</summary>
    public bool IsSectionMeasured(string sectionId) =>
        TryGetSectionShare(sectionId, out var share) && share.HasValue;
}

/// <summary>The whole document. <see cref="SchemaVersion"/> is the document's own shape version;
/// <see cref="Version"/> is the published tuning revision (<c>gk-core/tools/tuning/publish.py</c>).</summary>
public sealed record LawnPerfBudgetTuning(int SchemaVersion, int Version, LawnPerfCeiling Ceiling);

public sealed class LawnPerfBudgetRejection : Exception
{
    public LawnPerfBudgetRejection(string message) : base(message) { }
}

/// <summary>Process-wide holder, matching <c>LawnAttritionTuningHub</c>'s plain-holder shape: the host
/// reads the file at startup (<c>gk-core/src/FusionRpg.Server/Program.cs</c>) and a gate reads the hub, so no
/// reader invents a fallback ceiling when the file is missing.</summary>
public static class LawnPerfBudgetTuningHub
{
    static LawnPerfBudgetTuning? _tuning;

    public static void Configure(LawnPerfBudgetTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static LawnPerfBudgetTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "LawnPerfBudgetTuningHub.Configure(...) has not run. Read data/tuning/" + LawnPerfBudgetFiles.Current + " " +
        "at startup — there is no built-in default to fall back to.");

    public static bool IsConfigured => _tuning != null;

    /// <summary>Tests only.</summary>
    public static void Reset() => _tuning = null;
}

public static class LawnPerfBudgetTuningLoader
{
    public static LawnPerfBudgetTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new LawnPerfBudgetRejection("lawn-perf-budget tuning is empty");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        int Int(JsonElement node, string name)
        {
            if (!node.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number
                || !el.TryGetInt32(out var v))
                throw new LawnPerfBudgetRejection($"lawn-perf-budget tuning is missing integer '{name}'");
            return v;
        }

        double Number(JsonElement node, string name)
        {
            if (!node.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number
                || !el.TryGetDouble(out var v))
                throw new LawnPerfBudgetRejection($"lawn-perf-budget tuning is missing number '{name}'");
            return v;
        }

        if (!root.TryGetProperty("ceiling", out var ceilingEl) || ceilingEl.ValueKind != JsonValueKind.Object)
            throw new LawnPerfBudgetRejection("lawn-perf-budget tuning is missing the 'ceiling' object");

        // The fps half is a required GATE, not an optional field: an absent key would let a document
        // silently drop a gate the owner ruled is required. `null` is the measured/unmeasured
        // declaration and is accepted on purpose.
        if (!ceilingEl.TryGetProperty("minFpsRatioOfOff", out var fpsEl))
            throw new LawnPerfBudgetRejection(
                "lawn-perf-budget tuning is missing 'ceiling.minFpsRatioOfOff'; the fps half is a required "
                + "gate (owner ruling 2026-09-16), so use null to declare it unmeasured rather than omitting it");

        double? minFpsRatio = null;
        if (fpsEl.ValueKind != JsonValueKind.Null)
        {
            if (fpsEl.ValueKind != JsonValueKind.Number || !fpsEl.TryGetDouble(out var ratio))
                throw new LawnPerfBudgetRejection(
                    "'ceiling.minFpsRatioOfOff' must be a number or null (null = unmeasured)");
            minFpsRatio = ratio;
        }

        // `ceiling.sections` is optional (a document that predates it has none), but a value that IS
        // present must be a number or null: a malformed share is a typo in the number that decides
        // whether a feature ships, so it is refused rather than defaulted.
        Dictionary<string, double?>? sections = null;
        if (ceilingEl.TryGetProperty("sections", out var sectionsEl))
        {
            if (sectionsEl.ValueKind != JsonValueKind.Object)
                throw new LawnPerfBudgetRejection("'ceiling.sections' must be an object of section name -> share|null");
            sections = new Dictionary<string, double?>(StringComparer.Ordinal);
            foreach (var prop in sectionsEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Null) { sections[prop.Name] = null; continue; }
                if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetDouble(out var share))
                    throw new LawnPerfBudgetRejection(
                        $"'ceiling.sections.{prop.Name}' must be a number or null (null = declared but unmeasured)");
                if (share <= 0 || share > 100)
                    throw new LawnPerfBudgetRejection(
                        $"'ceiling.sections.{prop.Name}' is {share}; a share of wall is (0, 100]");
                sections[prop.Name] = share;
            }
        }

        var ceiling = new LawnPerfCeiling(
            PipelineSharePercent: Number(ceilingEl, "pipelineSharePercent"),
            ReferenceZombies: Int(ceilingEl, "referenceZombies"),
            MinFpsRatioOfOff: minFpsRatio,
            Sections: sections);

        // Direction, never a ceiling: a share of wall outside (0, 100] cannot describe a section's cost,
        // and a zero-or-negative fps floor is not a gate at all. A ratio above 1 (on faster than off) is
        // left legal -- a stricter gate is a tuning choice, not a defect.
        if (ceiling.PipelineSharePercent <= 0 || ceiling.PipelineSharePercent > 100)
            throw new LawnPerfBudgetRejection(
                $"'ceiling.pipelineSharePercent' is {ceiling.PipelineSharePercent}; a share of wall is (0, 100]");
        if (ceiling.ReferenceZombies <= 0)
            throw new LawnPerfBudgetRejection(
                $"'ceiling.referenceZombies' is {ceiling.ReferenceZombies}; the reference scenario needs zombies");
        if (ceiling.MinFpsRatioOfOff is { } floor && floor <= 0)
            throw new LawnPerfBudgetRejection(
                $"'ceiling.minFpsRatioOfOff' is {floor}; a minimum fps ratio must be positive — a floor of "
                + "zero or less would pass anything");

        return new LawnPerfBudgetTuning(
            SchemaVersion: Int(root, "schemaVersion"),
            Version: Int(root, "version"),
            Ceiling: ceiling);
    }
}
