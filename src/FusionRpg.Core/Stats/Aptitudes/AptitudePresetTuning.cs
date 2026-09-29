using System.Linq;
using System.Text.Json;

namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>aptitude-sheet AS-3.1 — soft caps for the build-preset library
/// (<c>data/tuning/aptitude-presets.v{n}.json</c>). Separate from <see cref="AptitudeTuning"/> so the
/// huge edges file is not republished for a library-size knob (E8).</summary>
public sealed record AptitudePresetTuning(
    int SchemaVersion,
    int Version,
    /// <summary>Soft max presets per player — refuse create with a named reason past this (E8).
    /// Not a progression ceiling on PointBudget.</summary>
    long SoftMaxPresets,
    /// <summary>Optional default for a new row's abs max in the editor; materialize ignores unset abs.</summary>
    long DefaultRowAbsMax,
    /// <summary>`assignLadder.order` (v2, spec-assign-ladder.md) — the walk <see cref="AssignLadder.Suggest"/>
    /// runs. The terminal rung ("even") is structural, not tunable: it is what makes the walk total,
    /// so the loader below enforces it rather than trusting the balance surface to keep it.</summary>
    AssignLadderTuning AssignLadder);

public sealed class AptitudePresetTuningRejection : Exception
{
    public AptitudePresetTuningRejection(string message) : base(message) { }
}

public static class AptitudePresetTuningHub
{
    static AptitudePresetTuning? _tuning;

    public static void Configure(AptitudePresetTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    /// <summary>EP1.14 (spec-default-build.md) — lets a caller take the SAME best-effort branch
    /// <c>SpeciesBuildPlanCatalog.IsConfigured</c> already gives its own callers: a real server always
    /// configures this at boot, but a test fixture reached indirectly (e.g. through
    /// <c>EffectiveUniqueAllocation</c>'s ladder rung, itself reached from a squad build a fixture never
    /// meant to exercise aptitudes at all) may not. Checking this avoids a hard throw taking down an
    /// unrelated caller's whole resolve.</summary>
    public static bool IsConfigured => _tuning is not null;

    public static AptitudePresetTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "AptitudePresetTuningHub.Configure(...) has not run. Preset soft caps read " +
        "data/tuning/aptitude-presets.v{n}.json (tunables-ssot.md §7.2) — there is no built-in default.");
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class AptitudePresetTuningLoader
{
    public static AptitudePresetTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AptitudePresetTuningRejection("aptitude preset tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new AptitudePresetTuningRejection($"aptitude preset tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var softMax = Long(root, "softMaxPresets");
            if (softMax <= 0)
                throw new AptitudePresetTuningRejection("aptitude preset tuning: softMaxPresets must be > 0");
            return new AptitudePresetTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                SoftMaxPresets: softMax,
                DefaultRowAbsMax: Long(root, "defaultRowAbsMax"),
                AssignLadder: ParseAssignLadder(root));
        }
    }

    /// <summary>Load contract (spec-assign-ladder.md "Tunables"): `assignLadder.order` names only
    /// known rungs (<see cref="AssignLadder.KnownRungs"/>), no duplicates, and its last entry is
    /// `even` — the terminal rung is structural (what makes the walk total), never tunable, so this
    /// is enforced here rather than trusted to the balance surface.</summary>
    static AssignLadderTuning ParseAssignLadder(JsonElement root)
    {
        if (!root.TryGetProperty("assignLadder", out var ladderEl) || ladderEl.ValueKind != JsonValueKind.Object)
            throw new AptitudePresetTuningRejection("aptitude preset tuning: missing or non-object 'assignLadder'");
        if (!ladderEl.TryGetProperty("order", out var orderEl) || orderEl.ValueKind != JsonValueKind.Array)
            throw new AptitudePresetTuningRejection("aptitude preset tuning: missing or non-array 'assignLadder.order'");

        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in orderEl.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } id)
                throw new AptitudePresetTuningRejection("aptitude preset tuning: 'assignLadder.order' entries must be strings");
            if (!AssignLadder.KnownRungs.Contains(id, StringComparer.Ordinal))
                throw new AptitudePresetTuningRejection($"aptitude preset tuning: 'assignLadder.order' names an unknown rung '{id}'");
            if (!seen.Add(id))
                throw new AptitudePresetTuningRejection($"aptitude preset tuning: 'assignLadder.order' repeats rung '{id}'");
            order.Add(id);
        }
        if (order.Count == 0 || order[^1] != AptitudeAutoAssignRules.Even)
            throw new AptitudePresetTuningRejection(
                "aptitude preset tuning: 'assignLadder.order' must end on 'even' — the terminal rung is structural, not tunable");

        return new AssignLadderTuning(order);
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new AptitudePresetTuningRejection($"aptitude preset tuning: missing or non-int '{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new AptitudePresetTuningRejection($"aptitude preset tuning: missing or non-long '{key}'");
        return v;
    }
}
