namespace FusionRpg.Core.Stats.Derived;

/// <summary>
/// Sheet-channel metadata for <c>ActorSheetChannelDto</c> — registry/surface SSOT → wire.
/// Render-state vocabulary matches design §3 / derived-cook D2.
/// </summary>
public static class DerivedSheetChannelMeta
{
    public const string StateActive = "active";
    public const string StateDefault = "default";
    public const string StateCapped = "capped";
    public const string StateStub = "stub";
    public const string StateNoProducer = "no-producer";
    public const string StateUnregistered = "unregistered";

    static readonly HashSet<string> StubChannels = new(StringComparer.Ordinal)
    {
        DerivedStatChannels.ProgressionPower,
        DerivedStatChannels.ProgressionRealm
    };

    /// <summary>
    /// Six-state classification for a channel present on the sheet snapshot.
    /// Unregistered is for expand-join holes outside the snapshot (FE/cook parity).
    /// </summary>
    public static string ResolveRenderState(
        string channelId,
        DerivedStatDef? def,
        double value,
        IReadOnlyList<(string SourceId, double Value)> contributions)
    {
        if (StubChannels.Contains(channelId))
            return StateStub;

        if (def is null)
            return StateUnregistered;

        if (DerivedAuditCoverage.IsKnownNoProducer(channelId))
            return StateNoProducer;

        if (def.Cap is { } cap && value >= cap - 1e-9)
            return StateCapped;

        var touched = false;
        for (var i = 0; i < contributions.Count; i++)
        {
            if (Math.Abs(contributions[i].Value) > 1e-9)
            {
                touched = true;
                break;
            }
        }

        if (!touched && Math.Abs(value - def.DefaultValue) < 1e-9)
            return StateDefault;

        if (!touched)
            return StateDefault;

        return StateActive;
    }

    public static string ResolveUnitClass(DerivedStatDef? def, string? surfaceUnitClass)
    {
        if (def?.Unit is { } unit)
            return unit.ToString();
        if (!string.IsNullOrEmpty(surfaceUnitClass))
            return surfaceUnitClass;
        return "";
    }
}
