namespace FusionRpg.Core.Stats;

public sealed class StatModifier
{
    public string SourceKind { get; init; } = "";
    public string SourceId { get; init; } = "";
    public string PluginId { get; init; } = "";
    public string Channel { get; init; } = "";
    public ModifierOp Op { get; init; }
    public double Value { get; init; }
    public int Priority { get; init; }

    /// <summary>
    /// FA1 / session apply scope. Empty or <c>match</c> = all living; <c>plant:N</c>/<c>zombie:N</c>/<c>entity:HEX</c> scoped.
    /// Copied from Effect grant <c>ownerKey</c> on ModifyStat.
    /// </summary>
    public string ApplyOwnerKey { get; init; } = "";

    public string Key =>
        $"{SourceKind}|{SourceId}|{Channel}|{(int)Op}|{StatApplyScope.Normalize(ApplyOwnerKey)}";
}
