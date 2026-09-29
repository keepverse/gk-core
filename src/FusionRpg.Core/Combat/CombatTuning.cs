using System.Text.Json;

namespace FusionRpg.Core.Combat;

/// <summary>
/// How <c>combat.defense</c> enters the damage formula (combat-damage-ssot.md §6.3).
/// </summary>
public enum DefenseShape
{
    /// <summary>Shipped v1: <c>powerAdjusted = base + Σw·(power − defense·pierce)</c>. Defense
    /// subtracts an absolute amount, so a defender whose defense exceeds the attacker's offense
    /// floors the hit at zero — the classic subtractive cliff.</summary>
    Subtractive,

    /// <summary>Defense divides instead of subtracting: <c>offense × K/(K + defense·pierce)</c>
    /// with <c>K = DefenseDivisorK × offense</c>. The denominator scaling with the incoming hit is
    /// what Path of Exile's armour formula does and what WoW achieves by scaling its constant with
    /// attacker level — it makes the mitigated fraction invariant when both sides climb the ladder
    /// together, and removes the zero-damage cliff (damage approaches 0 asymptotically, never
    /// reaching it). Not a cap: nothing is clamped, the curve simply never crosses zero.</summary>
    Divisive
}

/// <summary>
/// How <c>amplification − reduction</c> becomes a multiplier on final damage.
/// </summary>
public enum AmpShape
{
    /// <summary>Shipped v1: <c>max(0, 1 + ampDelta/scale)</c>. Unbounded upward, but the floor at
    /// zero is REACHABLE — once <c>reduction</c> exceeds <c>amplification</c> by one whole
    /// <c>AmpScale</c>, the multiplier is exactly 0 and the target takes literally nothing from any
    /// attack at any power. That is total immunity from one uncapped stat.</summary>
    LinearClamped,

    /// <summary>Mirrored asymptote — the same shape <c>PierceFactor</c> already uses, reflected:
    /// <c>1 + d/s</c> when <c>d ≥ 0</c>, <c>1/(1 − d/s)</c> when <c>d &lt; 0</c>. Identical to
    /// LinearClamped at <c>d = 0</c> (both 1.0) and for the whole amplifying half, so attackers see
    /// no change at all; the reducing half approaches zero without ever reaching it, so stacking
    /// <c>reduction</c> always helps and never confers immunity. This is what makes block/parry's
    /// "immunity impossible by construction" true of the mitigation chain too.</summary>
    Reciprocal
}

/// <summary>Combat balance surface (tunables-ssot.md T1) — loaded, not hard-coded. See
/// <see cref="CombatPolicy.Configure"/> and <see cref="CombatTuningLoader"/>. LastCol/LastRow are
/// board geometry (Lawn.LawnCoordMath), not balance, and are not part of this file.</summary>
public sealed record CombatTuning(
    int SchemaVersion, int Version,
    int ProcDepthLimit, int DefaultMaxTargets,
    int AreaDefaultSquareSize, int AreaDefaultRectangleWidth, int AreaDefaultRectangleHeight,
    int DotDefaultPeriodMs, int DotDefaultDurationMs,
    double PierceScale, double AmpScale,
    long BlockCapPermille, long ParryCapPermille, long AvoidanceBandCapPermille,
    double ReflectRateScale, double ReflectShareScale,
    long ParryNeutralShareKPm, DefenseShape DefenseShape, double DefenseDivisorK,
    bool ReflectReadsPostShield, AmpShape AmpShape);

public sealed class CombatTuningRejection : Exception
{
    public CombatTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class CombatTuningLoader
{
    public static CombatTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CombatTuningRejection("combat tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CombatTuningRejection($"combat tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            return new CombatTuning(
                SchemaVersion: Int(root, "schemaVersion", "$"),
                Version: Int(root, "version", "$"),
                ProcDepthLimit: Int(root, "procDepthLimit", "$"),
                DefaultMaxTargets: Int(root, "defaultMaxTargets", "$"),
                AreaDefaultSquareSize: Int(root, "areaDefaultSquareSize", "$"),
                AreaDefaultRectangleWidth: Int(root, "areaDefaultRectangleWidth", "$"),
                AreaDefaultRectangleHeight: Int(root, "areaDefaultRectangleHeight", "$"),
                DotDefaultPeriodMs: Int(root, "dotDefaultPeriodMs", "$"),
                DotDefaultDurationMs: Int(root, "dotDefaultDurationMs", "$"),
                PierceScale: Dbl(root, "pierceScale", "$"),
                AmpScale: Dbl(root, "ampScale", "$"),
                BlockCapPermille: Long(root, "blockCapPermille", "$"),
                ParryCapPermille: Long(root, "parryCapPermille", "$"),
                AvoidanceBandCapPermille: Long(root, "avoidanceBandCapPermille", "$"),
                ReflectRateScale: Dbl(root, "reflectRateScale", "$"),
                ReflectShareScale: Dbl(root, "reflectShareScale", "$"),
                ParryNeutralShareKPm: Long(root, "parryNeutralShareKPm", "$"),
                DefenseShape: Shape(root, "defenseShape", "$"),
                DefenseDivisorK: Dbl(root, "defenseDivisorK", "$"),
                ReflectReadsPostShield: Bool(root, "reflectReadsPostShield", "$"),
                AmpShape: Amp(root, "ampShape", "$"));
        }
    }

    static AmpShape Amp(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new CombatTuningRejection($"combat tuning: missing or non-string '{path}.{key}'");
        return el.GetString() switch
        {
            "linearClamped" => AmpShape.LinearClamped,
            "reciprocal" => AmpShape.Reciprocal,
            var other => throw new CombatTuningRejection(
                $"combat tuning: '{path}.{key}' must be 'linearClamped' or 'reciprocal', got '{other}'")
        };
    }

    static DefenseShape Shape(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new CombatTuningRejection($"combat tuning: missing or non-string '{path}.{key}'");
        // Explicit list, no Enum.TryParse: a typo'd shape must reject loudly rather than fall back
        // to a default, exactly like every other missing tunable (tunables-ssot.md T5).
        return el.GetString() switch
        {
            "subtractive" => DefenseShape.Subtractive,
            "divisive" => DefenseShape.Divisive,
            var other => throw new CombatTuningRejection(
                $"combat tuning: '{path}.{key}' must be 'subtractive' or 'divisive', got '{other}'")
        };
    }

    static bool Bool(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) ||
            (el.ValueKind != JsonValueKind.True && el.ValueKind != JsonValueKind.False))
            throw new CombatTuningRejection($"combat tuning: missing or non-boolean '{path}.{key}'");
        return el.GetBoolean();
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new CombatTuningRejection($"combat tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new CombatTuningRejection($"combat tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static double Dbl(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new CombatTuningRejection($"combat tuning: missing or non-numeric '{path}.{key}'");
        return el.GetDouble();
    }
}
