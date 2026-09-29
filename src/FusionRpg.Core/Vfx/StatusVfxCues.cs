using FusionRpg.Contracts;
using FusionRpg.Core.Status;

namespace FusionRpg.Core.Vfx;

/// <summary>
/// Maps status applies to presentation cues — SPEC W5, vfx-ssot.md §4.1. Pure; the injector
/// subscribes <see cref="StatusRuntime.OnApplied"/> and forwards the cue to its sink.
/// </summary>
public static class StatusVfxCues
{
    public static string CueId(string statusId) =>
        "status." + (statusId ?? "").Trim().ToLowerInvariant() + ".apply";

    public static string ExpireCueId(string statusId) =>
        "status." + (statusId ?? "").Trim().ToLowerInvariant() + ".expire";

    public static VfxCueDto Cue(StatusInstance instance) => new()
    {
        CueId = CueId(instance.StatusId),
        TargetPtr = instance.HostPtr,
        DurationMs = DurationMsOf(instance)
    };

    /// <summary>End signal for sustained visuals — routed to the state tracker, not a recipe.</summary>
    public static VfxCueDto ExpireCue(StatusInstance instance) => new()
    {
        CueId = ExpireCueId(instance.StatusId),
        TargetPtr = instance.HostPtr
    };

    /// <summary>Parse "status.{id}.apply|expire" cue ids; false for anything else.</summary>
    public static bool TryParse(string? cueId, out string statusId, out bool isExpire)
    {
        statusId = "";
        isExpire = false;
        if (string.IsNullOrWhiteSpace(cueId)) return false;
        if (!cueId.StartsWith("status.", StringComparison.OrdinalIgnoreCase)) return false;
        if (cueId.EndsWith(".apply", StringComparison.OrdinalIgnoreCase))
            statusId = cueId.Substring(7, cueId.Length - 7 - 6);
        else if (cueId.EndsWith(".expire", StringComparison.OrdinalIgnoreCase))
        {
            statusId = cueId.Substring(7, cueId.Length - 7 - 7);
            isExpire = true;
        }
        else
        {
            return false;
        }

        return statusId.Length > 0;
    }

    static int DurationMsOf(StatusInstance instance)
    {
        if (instance.ExpiresAt == DateTimeOffset.MaxValue) return 0;
        var ms = (instance.ExpiresAt - instance.AppliedAt).TotalMilliseconds;
        return ms > 0 && ms < int.MaxValue ? (int)Math.Round(ms) : 0;
    }
}
