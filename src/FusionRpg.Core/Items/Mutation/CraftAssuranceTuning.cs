using System.Text.Json;

namespace FusionRpg.Core.Items.Mutation;

public sealed class CraftAssuranceTuningRejection : Exception
{
    public CraftAssuranceTuningRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser over <c>gk-core/data/tuning/craft-assurance.v1.json</c> — no file I/O (tunables-ssot.md §7.2:
/// "Core never reads a file. Hosts load and inject"), the same shape <see cref="EnhancementTuning"/>
/// and <see cref="Materials.MaterialTuning"/> already use.
///
/// <para>Two ratios, both <c>[0, 1000]</c> per-mille and both REQUIRED — no key has a default, so a
/// missing one throws at load rather than resolving to a silently-invented bonus.
/// <see cref="AssureBonusMilli"/> is species-gear-chain T41's own consumer
/// (<see cref="EnhancePolicy.Resolve"/>'s <c>assureBonusMilli</c> parameter);
/// <see cref="RepairCoverageBonusMilli"/> is authored here now but has no reader yet — T44
/// (<c>repair coverage leg</c>) is its own consumer, the same "wire it now, real callers catch up
/// later" posture this codebase already uses elsewhere (<c>RecipeContext.TrophyStock</c>,
/// <c>ItemWorkbench</c>'s many optional delegate seams).</para>
///
/// <para>⛔ <b>No §10 row is owed.</b> Neither ratio is level-derived — both are flat per-charge
/// bonuses, the same species of number `spec-craft-assurance.md`'s own Numeric types section names.
/// <b>Protect's own effect is structural</b> (a <c>const</c>, commented at its own call site, not a
/// key in this file) — it is not a magnitude a balance pass would tune, it is "does this attempt's
/// craft wear apply at all," a boolean behaviour T43 wires.</para>
/// </summary>
public sealed class CraftAssuranceTuning
{
    CraftAssuranceTuning(int assureBonusMilli, int repairCoverageBonusMilli)
    {
        AssureBonusMilli = assureBonusMilli;
        RepairCoverageBonusMilli = repairCoverageBonusMilli;
    }

    /// <summary>Per-mille success-chance bonus ONE loaded `assurance.assure` charge grants, applied
    /// before the roll (<see cref="EnhancePolicy.Resolve"/>).</summary>
    public int AssureBonusMilli { get; }

    /// <summary>Per-mille repair-coverage bonus one loaded `assurance.repair` charge grants. No
    /// reader in this task — T44's own consumer.</summary>
    public int RepairCoverageBonusMilli { get; }

    public static CraftAssuranceTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CraftAssuranceTuningRejection("craft-assurance tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new CraftAssuranceTuningRejection($"craft-assurance tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new CraftAssuranceTuningRejection("craft-assurance tuning: root is not an object");

            return new CraftAssuranceTuning(
                Ratio(root, "assureBonusMilli"),
                Ratio(root, "repairCoverageBonusMilli"));
        }
    }

    /// <summary>A required per-mille ratio in <c>[0, 1000]</c> — a bounded ratio, never a magnitude
    /// (a probability/coverage bonus cannot exceed certainty), so this is exempt from the
    /// no-hard-ceiling rule by construction, not by omission.</summary>
    static int Ratio(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new CraftAssuranceTuningRejection($"craft-assurance tuning: missing or non-integer '{key}'");
        if (v < 0 || v > 1000)
            throw new CraftAssuranceTuningRejection(
                $"craft-assurance tuning: '{key}'={v} is outside [0, 1000] per-mille");
        return v;
    }
}

/// <summary>
/// species-gear-chain T41 — host-injected at boot (Core never reads a file), the same
/// re-assignable-not-throw-on-second-call shape <see cref="Materials.DeploymentHierarchyTuningHub"/>
/// already holds. Configured here so a LATER task (T42's real `assure` spend, T44's `repair`
/// coverage) can read <see cref="Tuning"/> directly without a further <c>Program.cs</c> change — the
/// same "wire it now, real callers catch up later" posture this codebase already uses for
/// <c>RecipeContext.TrophyStock</c> and <c>ItemWorkbench</c>'s own many optional delegate seams.
/// </summary>
public static class CraftAssuranceTuningHub
{
    static CraftAssuranceTuning? _tuning;

    public static void Configure(CraftAssuranceTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static CraftAssuranceTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "CraftAssuranceTuningHub.Configure(...) has not run. Read data/tuning/craft-assurance.v1.json " +
        "at host startup (the server's Program.cs does).");

    public static bool IsConfigured => _tuning != null;

    public static void Reset() => _tuning = null;
}
