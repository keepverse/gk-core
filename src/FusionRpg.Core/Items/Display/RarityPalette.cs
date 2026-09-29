namespace FusionRpg.Core.Items.Display;

/// <summary>
/// The rarity ladder's colour tokens (spec-item-card.md "The light-theme palette — owed twice, owned
/// here") — dark palette imported from the shipped registry (`core.v1.json` → `rarity.ladder[].
/// colourToken`), light palette a starting design pass (concrete hexes are the owner's taste; what
/// this module ships is the token slot and the measured rule set both palettes must satisfy).
///
/// <para>The invariant is NOT "L* increases" — it is "contrast against the ground increases". On a
/// dark ground those coincide; on a light one they invert, so the light palette orders `L*`
/// DECREASING with ordinal while keeping every other rule identical.</para>
/// </summary>
public static class RarityPalette
{
    /// <summary>The ten shipped dark-theme hexes, in ordinal order (`core.v1.json`
    /// `rarity.ladder[].colourToken`, chaff..almanac).</summary>
    public static readonly IReadOnlyList<string> Dark = new[]
    {
        "#63645d", "#697a5c", "#509639", "#37a39c", "#63a4ed",
        "#c994ff", "#ff94d2", "#ffab7a", "#f9d464", "#f3eaa0",
    };

    /// <summary>
    /// A starting light-theme palette, constructed (not eyeballed) to satisfy every rule this module
    /// measures — `L*` DECREASING across the ladder (48.0 → 4.5, steps ≥2.5 adjacent / ≥7 distance-2),
    /// monotone under both colour-blindness transforms, and WCAG AA 4.5:1 against a white ground for
    /// every rung. The whole range sits below the ~L*49.9 threshold where a colour stops clearing
    /// 4.5:1 against white at all — the shipped dark palette's own top end (almanac, L* 91.9) would
    /// fail that outright, which is exactly the "top of the ladder becomes least legible" failure the
    /// spec names. A slight per-rung hue tint on an otherwise near-grey ramp keeps each transform
    /// close to the achromatic case (colour-blindness simulations move an achromatic colour least),
    /// which is what makes the monotonicity survive both transforms without hand-tuning each one.
    /// **A design pass, not a final art direction** (spec's own boundary: "the ten concrete hexes are
    /// a design pass and taste is the owner's") — what is locked is the rule set, proven by
    /// <see cref="Validate"/> against this starting palette exactly as it will be against the dark one.
    /// </summary>
    public static readonly IReadOnlyList<string> Light = new[]
    {
        "#726f68", "#646854", "#455d45", "#355050", "#3a3c50",
        "#3d2d47", "#3a2232", "#301c14", "#221a06", "#101008",
    };

    // Structural, not tunable (tunables-ssot T2): the LOCKED legibility rule set Validate enforces on any
    // palette (the class doc: "what is locked is the rule set"). Minimum CIE L* separation between
    // neighbouring rarity colours and between colours two rungs apart. A palette is taste and changes;
    // these are the rule the palette is checked against, and they belong to display, not balance.
    public const double MinAdjacentDeltaL = 2.5;
    public const double MinDistanceTwoDeltaL = 7.0;

    /// <summary>WCAG AA for normal text.</summary>
    public const double MinWcagContrastRatio = 4.5;

    // ---- sRGB -> CIE L* --------------------------------------------------------------------------

    static (double R, double G, double B) ParseHex(string hex)
    {
        var h = hex.TrimStart('#');
        var r = Convert.ToInt32(h[..2], 16) / 255.0;
        var g = Convert.ToInt32(h[2..4], 16) / 255.0;
        var b = Convert.ToInt32(h[4..6], 16) / 255.0;
        return (r, g, b);
    }

    static double SrgbToLinear(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

    /// <summary>Relative luminance Y (sRGB, D65) — the same value both CIE L* and WCAG contrast are
    /// built from, so the two measures never silently disagree about what "brighter" means.</summary>
    public static double RelativeLuminance(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        var (rl, gl, bl) = (SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b));
        return 0.2126729 * rl + 0.7151522 * gl + 0.0721750 * bl;
    }

    /// <summary>CIE 1976 L* (perceptual lightness), D65 white point Yn = 1.</summary>
    public static double LStar(string hex)
    {
        var y = RelativeLuminance(hex);
        return y > 0.008856 ? 116.0 * Math.Pow(y, 1.0 / 3.0) - 16.0 : 903.3 * y;
    }

    /// <summary>WCAG 2 contrast ratio between two colours, always >= 1.</summary>
    public static double WcagContrast(string hexA, string hexB)
    {
        var la = RelativeLuminance(hexA);
        var lb = RelativeLuminance(hexB);
        var (lighter, darker) = la >= lb ? (la, lb) : (lb, la);
        return (lighter + 0.05) / (darker + 0.05);
    }

    // ---- colour-blindness simulation (Machado, Oliveira & Fonseca 2009 -- 100% severity) ----------
    // Linear-RGB matrices, the standard replacement for a full LMS round-trip and the one most game
    // engines ship; the Viénot 1999 LMS derivation this table descends from is cited in ssot-rarity.md.

    public static string SimulateDeuteranope(string hex) => Simulate(hex,
        (0.367322, 0.860646, -0.227968),
        (0.280085, 0.672501, 0.047413),
        (-0.011820, 0.042940, 0.968881));

    public static string SimulateProtanope(string hex) => Simulate(hex,
        (0.152286, 1.052583, -0.204868),
        (0.114503, 0.786281, 0.099216),
        (-0.003882, -0.048116, 1.051998));

    static string Simulate(string hex, (double, double, double) rowR, (double, double, double) rowG, (double, double, double) rowB)
    {
        var (r, g, b) = ParseHex(hex);
        var (rl, gl, bl) = (SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b));

        double Dot((double a, double b, double c) row) => row.a * rl + row.b * gl + row.c * bl;
        var (nr, ng, nb) = (Clamp01(Dot(rowR)), Clamp01(Dot(rowG)), Clamp01(Dot(rowB)));

        static double LinearToSrgb(double c) => c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
        return $"#{ToByte(LinearToSrgb(nr)):x2}{ToByte(LinearToSrgb(ng)):x2}{ToByte(LinearToSrgb(nb)):x2}";
    }

    static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);
    static int ToByte(double c) => (int)Math.Round(Clamp01(c) * 255.0);

    // ---- the measured rules, over EITHER palette ---------------------------------------------------

    public enum LightnessDirection { Increasing, Decreasing }

    public sealed record PaletteValidationResult(bool Ok, IReadOnlyList<string> Failures);

    /// <summary>
    /// Every rule both palettes must satisfy (spec's own table): `L*` monotone in the given direction
    /// (contrast against the ground always increases with ordinal; the direction flips per theme
    /// because the ground does); adjacent ΔL* >= 2.5; distance-2 ΔL* >= 7; monotone (in the SAME
    /// direction) under both the deuteranope and the protanope transform; hue never carries the
    /// ordering (checked structurally: this function does not read hue at all, by design — the tests
    /// that would fail if it silently started to are the negative-control tests).
    /// </summary>
    public static PaletteValidationResult Validate(IReadOnlyList<string> palette, LightnessDirection direction)
    {
        var failures = new List<string>();
        var lStars = palette.Select(LStar).ToList();

        CheckMonotoneAndDeltas(lStars, "L*", direction, failures);
        CheckMonotoneAndDeltas(palette.Select(h => LStar(SimulateDeuteranope(h))).ToList(), "deuteranope L*", direction, failures, deltaChecks: false);
        CheckMonotoneAndDeltas(palette.Select(h => LStar(SimulateProtanope(h))).ToList(), "protanope L*", direction, failures, deltaChecks: false);

        return new PaletteValidationResult(failures.Count == 0, failures);
    }

    static void CheckMonotoneAndDeltas(IReadOnlyList<double> values, string label, LightnessDirection direction, List<string> failures, bool deltaChecks = true)
    {
        for (var i = 1; i < values.Count; i++)
        {
            var ok = direction == LightnessDirection.Increasing ? values[i] > values[i - 1] : values[i] < values[i - 1];
            if (!ok) failures.Add($"{label} not monotone {direction} at index {i} ({values[i - 1]:F1} -> {values[i]:F1})");

            if (deltaChecks && Math.Abs(values[i] - values[i - 1]) < MinAdjacentDeltaL)
                failures.Add($"{label} adjacent delta at index {i} is {Math.Abs(values[i] - values[i - 1]):F2}, below {MinAdjacentDeltaL}");
        }

        if (!deltaChecks) return;
        for (var i = 2; i < values.Count; i++)
            if (Math.Abs(values[i] - values[i - 2]) < MinDistanceTwoDeltaL)
                failures.Add($"{label} distance-2 delta at index {i} is {Math.Abs(values[i] - values[i - 2]):F2}, below {MinDistanceTwoDeltaL}");
    }

    /// <summary>WCAG AA 4.5:1 for the rung name text against its own theme's ground.</summary>
    public static bool RungNameMeetsWcagAa(string rungHex, string groundHex) =>
        WcagContrast(rungHex, groundHex) >= MinWcagContrastRatio;
}
