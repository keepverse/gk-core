namespace FusionRpg.Core.Stats.Derived;

public sealed record DerivedModifier(
    string ChannelId,
    DerivedModifierOp Op,
    double Value,
    int Priority = 0,
    string SourceId = "");

public enum DerivedModifierOp
{
    Flat,
    Increased,
    Replace,
    Flag
}
