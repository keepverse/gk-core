namespace FusionRpg.Contracts;

public enum DamageFxTag
{
    Neutral = 0,
    Heal = 1,
    Weak = 2,
    Resist = 3,
    Null = 4,
    Absorb = 5,
    Reflect = 6,
    Dodge = 7,
    Crit = 8,
    Penetrate = 9,
    Block = 10
}

/// <summary>GUI-only present. Not an FA opcode; never writes HP.</summary>
public sealed class DamageFxDto
{
    public string TargetPtr { get; set; } = "";
    public long Amount { get; set; }
    public DamageFxTag Tag { get; set; } = DamageFxTag.Neutral;
    public string Fx { get; set; } = "float";
    public int MergedCount { get; set; } = 1;

    /// <summary>Optional element payload for element-colored presents (vfx-ssot.md §16).</summary>
    public List<ElementPayloadComponentDto>? Elements { get; set; }
}
