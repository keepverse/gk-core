using FusionRpg.Contracts;

namespace FusionRpg.Core.Vfx;

/// <summary>
/// Element → RGB (vfx-ssot.md §16.2) and hybrid "rainbow" color math (§16.3). Pure — no Unity.
/// Hybrid = per-particle color at emit + one lerp per floater draw; no gradients, no materials.
/// </summary>
public static class ElementFxPalette
{
    public static (byte R, byte G, byte B) Rgb(string? elementId) => Norm(elementId) switch
    {
        "fire" => (255, 90, 40),
        "ice" => (110, 210, 255),
        "air" => (190, 255, 170),
        "earth" => (210, 160, 70),
        "light" => (255, 232, 120),
        "dark" => (150, 90, 220),
        _ => (255, 255, 255) // omni / unknown / no payload — matches the existing white overlay
    };

    /// <summary>Concrete (non-omni, known) components with positive weight, order preserved.</summary>
    public static List<ElementPayloadComponentDto> Concrete(IEnumerable<ElementPayloadComponentDto>? elements)
    {
        var list = new List<ElementPayloadComponentDto>();
        if (elements == null) return list;
        foreach (var e in elements)
        {
            if (e == null || e.Weight <= 0) continue;
            // Roster membership by strict name — keeps this palette in lockstep with ElementRoster.
            if (FusionRpg.Core.Stats.Derived.ElementRoster.TryParse(e.Element, out _))
                list.Add(e);
        }

        return list;
    }

    /// <summary>Hybrid floater color at life t∈[0,1]: cycles across component colors, wrapping end → start.</summary>
    public static (byte R, byte G, byte B) HybridColorAt(IReadOnlyList<ElementPayloadComponentDto> concrete, float t)
    {
        if (concrete == null || concrete.Count == 0) return (255, 255, 255);
        if (concrete.Count == 1) return Rgb(concrete[0].Element);
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;
        var pos = t * concrete.Count;
        var i = (int)pos;
        if (i >= concrete.Count) i = concrete.Count - 1;
        var frac = pos - i;
        var a = Rgb(concrete[i].Element);
        var b = Rgb(concrete[(i + 1) % concrete.Count].Element);
        return Lerp(a, b, frac);
    }

    /// <summary>
    /// Per-particle color for hybrid bursts: stratified weight-proportional element pick plus a small
    /// deterministic brightness jitter. Deterministic by (index, count) — no RNG, no allocation.
    /// </summary>
    public static (byte R, byte G, byte B) ParticleColor(
        IReadOnlyList<ElementPayloadComponentDto> concrete, int index, int count)
    {
        if (concrete == null || concrete.Count == 0) return (255, 255, 255);
        if (concrete.Count == 1) return Rgb(concrete[0].Element);
        var total = 0.0;
        foreach (var c in concrete) total += c.Weight;
        if (total <= 0) return Rgb(concrete[0].Element);
        if (count < 1) count = 1;
        if (index < 0) index = 0;
        var u = (index % count + 0.5) / count;
        var acc = 0.0;
        foreach (var c in concrete)
        {
            acc += c.Weight / total;
            if (u <= acc)
                return Jitter(Rgb(c.Element), index);
        }

        return Jitter(Rgb(concrete[concrete.Count - 1].Element), index);
    }

    static (byte R, byte G, byte B) Jitter((byte R, byte G, byte B) c, int index)
    {
        var f = 0.85f + 0.3f * (index % 5) / 4f;
        return (ClampByte(c.R * f), ClampByte(c.G * f), ClampByte(c.B * f));
    }

    static (byte R, byte G, byte B) Lerp((byte R, byte G, byte B) a, (byte R, byte G, byte B) b, double t)
    {
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        return (
            ClampByte(a.R + (b.R - a.R) * t),
            ClampByte(a.G + (b.G - a.G) * t),
            ClampByte(a.B + (b.B - a.B) * t));
    }

    static byte ClampByte(double v)
    {
        if (v <= 0) return 0;
        if (v >= 255) return 255;
        return (byte)Math.Round(v);
    }

    static string Norm(string? id) => (id ?? "").Trim().ToLowerInvariant();
}
