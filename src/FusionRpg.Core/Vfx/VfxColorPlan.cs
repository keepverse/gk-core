using FusionRpg.Contracts;
using FusionRpg.Core.Effects;

namespace FusionRpg.Core.Vfx;

/// <summary>
/// Color/label precedence for one cue — vfx-ssot.md §16.4. Pure and computed once per cue:
/// semantic tags keep the tag palette; element payload colors plain and crit damage;
/// crit distinctness moves to font size (crit-orange collides with fire).
/// </summary>
public sealed class VfxColorPlan
{
    public (byte R, byte G, byte B) Rgb { get; init; } = (255, 255, 255);
    public bool Hybrid { get; init; }
    /// <summary>True when an element payload decided the color (single or hybrid) — gates RequireElement specs.</summary>
    public bool ElementColored { get; init; }
    /// <summary>Normalized single concrete element, absent for neutral and hybrid payloads.</summary>
    public string? ConcreteElement { get; init; }
    public IReadOnlyList<ElementPayloadComponentDto> HybridComponents { get; init; } =
        Array.Empty<ElementPayloadComponentDto>();
    public float FontScale { get; init; } = 1f;
    public string Label { get; init; } = "";
    public bool Crit { get; init; }
    public bool SemanticTag { get; init; }
    public long Amount { get; init; }

    /// <summary>Floater font scale at life t: crit pop × amount tier; semantic labels stay flat.</summary>
    public float FontScaleAt(float t)
    {
        if (SemanticTag) return 1f;
        var pop = Crit ? VfxRules.PopScale(t) : 1f;
        return pop * VfxRules.AmountScale(Amount);
    }

    /// <summary>Burst tint: near-white floater color renders as the legacy orange hit flash.</summary>
    public (byte R, byte G, byte B) BurstRgb =>
        Rgb.R > 217 && Rgb.G > 217 && Rgb.B > 217 ? VfxSeedCatalog.ProbeOrange : Rgb;

    /// <summary>Floater color at life t — cycles hue for hybrid, constant otherwise.</summary>
    public (byte R, byte G, byte B) ColorAt(float t) =>
        Hybrid ? ElementFxPalette.HybridColorAt(HybridComponents, t) : Rgb;

    public bool IsConcreteElement(string element) =>
        ElementColored && !Hybrid && string.Equals(ConcreteElement, element, StringComparison.OrdinalIgnoreCase);

    static readonly List<ElementPayloadComponentDto> EmptyComponents = new();

    public static VfxColorPlan For(
        DamageFxTag? tag,
        IReadOnlyList<ElementPayloadComponentDto>? elements,
        bool elementFxOn,
        long amount)
    {
        var t = tag ?? DamageFxTag.Neutral;
        var label = DamageFxPalette.Label(t, amount);
        var crit = t == DamageFxTag.Crit;
        var semantic = t is DamageFxTag.Dodge or DamageFxTag.Block or DamageFxTag.Null
            or DamageFxTag.Absorb or DamageFxTag.Reflect or DamageFxTag.Heal
            or DamageFxTag.Weak or DamageFxTag.Resist;
        var concrete = elementFxOn && !semantic && elements is { Count: > 0 }
            ? ElementFxPalette.Concrete(elements)
            : EmptyComponents;

        if (concrete.Count == 0)
        {
            return new VfxColorPlan
            {
                Rgb = DamageFxPalette.Rgb(t),
                Label = label,
                Crit = crit,
                SemanticTag = semantic,
                Amount = amount
            };
        }

        var fontScale = crit ? VfxRules.CritFontScale : 1f;
        if (concrete.Count == 1)
        {
            return new VfxColorPlan
            {
                Rgb = ElementFxPalette.Rgb(concrete[0].Element),
                Label = label,
                FontScale = fontScale,
                Crit = crit,
                Amount = amount,
                ElementColored = true,
                ConcreteElement = concrete[0].Element.Trim().ToLowerInvariant()
            };
        }

        return new VfxColorPlan
        {
            Rgb = ElementFxPalette.HybridColorAt(concrete, 0f),
            Hybrid = true,
            HybridComponents = concrete,
            Label = label,
            FontScale = fontScale,
            Crit = crit,
            Amount = amount,
            ElementColored = true
        };
    }
}
