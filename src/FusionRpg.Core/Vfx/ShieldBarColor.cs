namespace FusionRpg.Core.Vfx;

/// <summary>
/// HP-weighted multi-stop colors for the in-game RPG shield bar.
/// Spatial across the fill (not temporal hybrid rainbow). Pure — no Unity.
/// </summary>
public static class ShieldBarColor
{
    /// <summary>Untyped / none shield fill — distinct from ElementFxPalette white omni.</summary>
    public static readonly (byte R, byte G, byte B) UntypedRgb = (140, 190, 255);

    public readonly struct Stop
    {
        public Stop(string elementId, long hp, float startU, float endU, byte r, byte g, byte b)
        {
            ElementId = elementId;
            Hp = hp;
            StartU = startU;
            EndU = endU;
            R = r;
            G = g;
            B = b;
        }

        public string ElementId { get; }
        public long Hp { get; }
        public float StartU { get; }
        public float EndU { get; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
    }

    /// <summary>Element id → bar RGB. Null/empty/none/omni/unknown → <see cref="UntypedRgb"/>.</summary>
    public static (byte R, byte G, byte B) Rgb(string? elementId)
    {
        var n = Norm(elementId);
        return n switch
        {
            "fire" or "ice" or "air" or "earth" or "light" or "dark" => ElementFxPalette.Rgb(n),
            _ => UntypedRgb
        };
    }

    /// <summary>
    /// Builds contiguous HP-weighted stops for stacks with hp &gt; 0.
    /// Returns false when there is nothing to draw (empty or zero total HP).
    /// </summary>
    public static bool TryBuildStops(
        IReadOnlyList<(string? ElementId, long Hp)> stacks,
        List<Stop> into)
    {
        into.Clear();
        if (stacks == null || stacks.Count == 0)
            return false;

        long total = 0;
        for (var i = 0; i < stacks.Count; i++)
        {
            if (stacks[i].Hp > 0)
                total += stacks[i].Hp;
        }

        if (total <= 0)
            return false;

        float u = 0f;
        var remaining = 0;
        for (var i = 0; i < stacks.Count; i++)
            if (stacks[i].Hp > 0) remaining++;

        for (var i = 0; i < stacks.Count; i++)
        {
            var hp = stacks[i].Hp;
            if (hp <= 0) continue;
            remaining--;
            var end = remaining == 0 ? 1f : u + (float)hp / total;
            var id = Norm(stacks[i].ElementId);
            if (id.Length == 0) id = "none";
            var rgb = Rgb(id);
            into.Add(new Stop(id, hp, u, end, rgb.R, rgb.G, rgb.B));
            u = end;
        }

        return into.Count > 0;
    }

    /// <summary>
    /// Sample RGB at fill fraction u∈[0,1]. Each stop is solid; boundaries prefer the
    /// left stop except u==1 which lands on the last stop.
    /// </summary>
    public static bool TryColorAt(IReadOnlyList<Stop> stops, float u, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (stops == null || stops.Count == 0)
            return false;
        if (u < 0f) u = 0f;
        if (u > 1f) u = 1f;

        for (var i = 0; i < stops.Count - 1; i++)
        {
            if (u < stops[i].EndU)
            {
                var s = stops[i];
                rgb = (s.R, s.G, s.B);
                return true;
            }
        }

        var last = stops[stops.Count - 1];
        rgb = (last.R, last.G, last.B);
        return true;
    }

    /// <summary>Convenience: build stops then sample. False when nothing to draw.</summary>
    public static bool TryColorAt(
        IReadOnlyList<(string? ElementId, long Hp)> stacks,
        float u,
        out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        var buf = new List<Stop>(3);
        if (!TryBuildStops(stacks, buf))
            return false;
        return TryColorAt(buf, u, out rgb);
    }

    static string Norm(string? id) => (id ?? "").Trim().ToLowerInvariant();
}
