using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// Reads and writes a <see cref="ValueSpec"/> in the exact canonical JSON shape
/// <see cref="AtomJson.TryReadValueSpec"/> already reads (definitions.md §2). No second scaling
/// mechanism, no second JSON grammar — this only adds the write direction the atom program never
/// needed, because atoms are authored in `gk-data/packs/fusion/data/seed/` files this program does not touch.
/// </summary>
public static class ActionValueSpecJson
{
    public static ActionRejection TryRead(string? json, out ValueSpec spec)
    {
        spec = default;
        if (string.IsNullOrWhiteSpace(json))
            return ActionRejection.Fail(ActionRejectionReason.BadValueSpec, "value spec is empty");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json!); }
        catch (JsonException ex)
        {
            return ActionRejection.Fail(ActionRejectionReason.BadValueSpec, $"not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var result = AtomJson.TryReadValueSpec(doc.RootElement, out spec);
            if (!result.IsOk)
                return ActionRejection.Fail(ActionRejectionReason.BadValueSpec, result.Detail);
            return ActionRejection.Ok;
        }
    }

    /// <summary>Canonical write: a bare number when fixed, else the full object — the same two shapes read.</summary>
    public static string Write(ValueSpec spec)
    {
        if (spec.IsFixed) return spec.Min.ToString();

        var roll = spec.Roll switch
        {
            RollPolicy.OnInstantiate => "onInstantiate",
            RollPolicy.OnApply => "onApply",
            _ => "fixed",
        };

        return spec.CurveId is { Length: > 0 }
            ? $"{{\"min\":{spec.Min},\"max\":{spec.Max},\"roll\":\"{roll}\",\"curve\":\"{spec.CurveId}\"}}"
            : $"{{\"min\":{spec.Min},\"max\":{spec.Max},\"roll\":\"{roll}\"}}";
    }
}
