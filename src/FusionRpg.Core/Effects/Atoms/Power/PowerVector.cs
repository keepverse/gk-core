using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms.Power;

/// <summary>
/// What an effect is worth, in five kinds of worth.
///
/// <para><b>A vector, never a scalar, is the stored truth.</b> Adding a crit-rate atom to a
/// crit-damage atom underprices both, and adding an offense atom to a defense atom compares things
/// that do not compare. Diablo 3 needed three separate aggregates for exactly this reason and its
/// sheet numbers are still wrong, because they omit multiplicative sources. Any scalar is a derived
/// read (E10), never truth.</para>
///
/// <para><b>Integer points throughout.</b> Prices are compared, summed, budgeted and hashed; a
/// double would make two runs of the same catalog disagree in the last bit and move a content hash
/// for nothing.</para>
/// </summary>
public readonly record struct PowerVector(
    int Offense, int Survivability, int Control, int Utility, int Economy)
{
    public static readonly PowerVector Zero = default;

    /// <summary>The five, in the order every report and every serialization uses.</summary>
    public static readonly string[] Categories =
        { "offense", "survivability", "control", "utility", "economy" };

    public int this[int index] => index switch
    {
        0 => Offense,
        1 => Survivability,
        2 => Control,
        3 => Utility,
        4 => Economy,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    public static PowerVector operator +(PowerVector a, PowerVector b) => new(
        a.Offense + b.Offense,
        a.Survivability + b.Survivability,
        a.Control + b.Control,
        a.Utility + b.Utility,
        a.Economy + b.Economy);

    public static PowerVector operator -(PowerVector a, PowerVector b) => new(
        a.Offense - b.Offense,
        a.Survivability - b.Survivability,
        a.Control - b.Control,
        a.Utility - b.Utility,
        a.Economy - b.Economy);

    /// <summary>Scale by an integer per-mille factor, rounding once, half away from zero.</summary>
    public PowerVector ScaleMilli(long milli) => new(
        PowerMath.MulMilli(Offense, milli),
        PowerMath.MulMilli(Survivability, milli),
        PowerMath.MulMilli(Control, milli),
        PowerMath.MulMilli(Utility, milli),
        PowerMath.MulMilli(Economy, milli));

    public bool IsZero => this == Zero;

    /// <summary>Sum of the five. A crude read, and never the display scalar — that is E10's.</summary>
    public int Total => Offense + Survivability + Control + Utility + Economy;

    public static PowerVector FromCategory(PowerCategory category, int points)
    {
        // A kind may declare several categories; the price is split evenly rather than counted once
        // per category, or a two-category kind would be worth twice a one-category kind for free.
        var flags = CategoryList(category);
        if (flags.Count == 0) return Zero;

        var each = PowerMath.DivRound(points, flags.Count);
        var v = Zero;
        foreach (var f in flags) v = v.With(f, each);
        return v;
    }

    public PowerVector With(PowerCategory single, int points) => single switch
    {
        PowerCategory.Offense => this with { Offense = Offense + points },
        PowerCategory.Survivability => this with { Survivability = Survivability + points },
        PowerCategory.Control => this with { Control = Control + points },
        PowerCategory.Utility => this with { Utility = Utility + points },
        PowerCategory.Economy => this with { Economy = Economy + points },
        _ => this,
    };

    public static List<PowerCategory> CategoryList(PowerCategory mask)
    {
        var list = new List<PowerCategory>(5);
        foreach (var f in new[]
                 {
                     PowerCategory.Offense, PowerCategory.Survivability,
                     PowerCategory.Control, PowerCategory.Utility, PowerCategory.Economy,
                 })
            if ((mask & f) == f) list.Add(f);
        return list;
    }

    /// <summary>Canonical JSON for <c>power_json</c> — keys in category order, integers only.</summary>
    public string ToJson() =>
        $"{{\"offense\":{Offense},\"survivability\":{Survivability},\"control\":{Control}," +
        $"\"utility\":{Utility},\"economy\":{Economy}}}";

    public static PowerVector FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Zero;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            var r = doc.RootElement;
            return new PowerVector(Read(r, "offense"), Read(r, "survivability"),
                Read(r, "control"), Read(r, "utility"), Read(r, "economy"));
        }
        catch (JsonException)
        {
            return Zero;
        }

        static int Read(JsonElement o, string name) =>
            o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out var v) ? v : 0;
    }
}

/// <summary>
/// Integer per-mille arithmetic, with <b>one</b> rounding point.
///
/// <para>The cost function multiplies four conditionality factors together. Rounding each to an
/// integer as it goes is how <c>chance/1000</c> became 0 for every proc below 1000‰ — defect D1: the
/// whole conditional half of the catalog priced at zero. Factors stay in per-mille and the result is
/// rounded once, at the end.</para>
/// </summary>
public static class PowerMath
{
    public const long One = 1000;

    /// <summary>Multiply a value by a per-mille factor. Rounds half away from zero.</summary>
    public static int MulMilli(long value, long milli) => (int)DivRound(value * milli, One);

    /// <summary>Combine per-mille factors without leaving per-mille.</summary>
    public static long CombineMilli(long a, long b) => DivRound(a * b, One);

    /// <summary>
    /// Integer division rounding half <b>away from zero</b> — the same rule everywhere, so a price
    /// does not depend on which order two equal factors were applied in.
    /// </summary>
    public static long DivRound(long numerator, long denominator)
    {
        if (denominator == 0) return 0;
        var sign = (numerator < 0) ^ (denominator < 0) ? -1 : 1;
        var n = Math.Abs(numerator);
        var d = Math.Abs(denominator);
        return sign * ((n + d / 2) / d);
    }

    public static int DivRound(int numerator, int denominator) =>
        (int)DivRound((long)numerator, denominator);
}
