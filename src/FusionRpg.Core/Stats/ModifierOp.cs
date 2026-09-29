namespace FusionRpg.Core.Stats;

public enum ModifierOp
{
    Flat = 0,
    Increased = 1,
    More = 2,
    Override = 3
}

/// <summary>
/// Which way is better on a channel (E16).
///
/// <para><b>An interval inverts the whole grammar.</b> <c>Increased</c> on <c>attackInterval</c>
/// makes the plant <i>slower</i> — so an author writing "+20% attack interval" meaning "shoots
/// faster" gets the opposite, the cost function prices a buff as a penalty, and the UI copy reads
/// backwards. The direction is declared once, here, and everything downstream reads it rather than
/// each place guessing.</para>
/// </summary>
public enum ChannelDirection
{
    HigherIsBetter = 0,
    LowerIsBetter = 1,
}

public static class StatChannels
{
    public const string Hp = "hp";
    public const string MaxHp = "maxHp";
    public const string Atk = "atk";
    public const string Defense = "defense";
    public const string Arm1 = "arm1";
    public const string Arm1Max = "arm1Max";
    public const string Arm2 = "arm2";
    public const string Arm2Max = "arm2Max";

    // E16: promoted from cheat-document keys to real composed channels. They were written directly
    // by the extras path from `P-ATK-INT` / `P-PROD-INT` / `Z-SPD-U`, bypassing the modifier bag —
    // so "shoots faster", the genre's single most wanted affix, was impossible to author. The
    // documented channel enum even listed them, which is how the gap survived unnoticed.
    public const string AttackInterval = "attackInterval";
    public const string ProduceInterval = "produceInterval";
    public const string ZombieSpeed = "zombieSpeed";

    // E38 (spec-entity-fields-12plus.md): "E16 run a second time" — twelve more Unity fields that
    // were written straight from cheat keys, past the modifier bag, promoted the same way. Primary
    // channels go 11 -> 23.
    public const string PlantShield = "plantShield";
    public const string AttackCountdown = "attackCountdown";
    public const string AttackSpeedAdder = "attackSpeedAdder";
    public const string ProduceCountdown = "produceCountdown";
    public const string PlantSpeed = "plantSpeed";
    public const string PlantMoveSpeed = "plantMoveSpeed";
    public const string PlantLevel = "plantLevel";
    public const string ShootingLevel = "shootingLevel";
    public const string ArmorFlat = "armorFlat";
    // Bearer frame (⛔ DECIDED 2026-09-03): LowerIsBetter because lower is better FOR THE BEARER —
    // everything that writes this channel writes the entity that has it (EntityStatWriter.cs writes
    // the same entity being resolved, never "someone else's" takeDmgMultiplier). This channel is
    // NOT the authoring surface for "enemies take more damage" — that debuff is a `status.apply`
    // payload, priced as a status (its own coefficient, trigger frequency, uptime), never a
    // stat.modify here. A RAISE on your own takeDmgMultiplier is a real penalty to you, and prices
    // negative under CostFunction's sign flip — see CostFunction's own EntityFieldsTwelvePlusTests.
    public const string TakeDmgMultiplier = "takeDmgMultiplier";
    public const string ZombieSpeedCurrent = "zombieSpeedCurrent";
    public const string ZombieOriginSpeed = "zombieOriginSpeed";

    /// <summary>The twenty-three primary channels, in declaration order (11 since E16, 23 since E38).</summary>
    public static readonly string[] All =
    {
        Hp, MaxHp, Atk, Defense, Arm1, Arm1Max, Arm2, Arm2Max,
        AttackInterval, ProduceInterval, ZombieSpeed,
        PlantShield, AttackCountdown, AttackSpeedAdder, ProduceCountdown, PlantSpeed, PlantMoveSpeed,
        PlantLevel, ShootingLevel, ArmorFlat, TakeDmgMultiplier, ZombieSpeedCurrent, ZombieOriginSpeed,
    };

    /// <summary>
    /// An interval must never reach zero. Depending on the call site that is a divide-by-zero or an
    /// infinite fire rate; neither is shippable, and both are reachable from ordinary content
    /// (`More` −100% on an interval) rather than from a hostile edit.
    /// </summary>
    public static double MinimumInterval => Derived.StatsTuningHub.Tuning.MinimumInterval;

    public static ChannelDirection DirectionOf(string? channel)
    {
        // E22: an imported effect_channel_policy row overrides the code default for an EXISTING
        // channel — E1's code-or-data rule applied to itself (a value change with a live consumer,
        // not a new channel). An empty table (nothing imported) falls through unchanged.
        if (channel is not null && ChannelPolicyTable.Current.TryGetDirection(channel, out var stored))
            return stored;

        return channel switch
        {
            AttackInterval or ProduceInterval => ChannelDirection.LowerIsBetter,
            // E38: two more structural countdowns (same reason as the intervals above — driven
            // toward zero is a divide-by-zero / infinite-fire-rate risk) plus takeDmgMultiplier
            // (the bearer frame — see that constant's own doc comment for the full reasoning).
            AttackCountdown or ProduceCountdown or TakeDmgMultiplier => ChannelDirection.LowerIsBetter,
            _ => ChannelDirection.HigherIsBetter,
        };
    }

    public static bool IsLowerBetter(string? channel) =>
        DirectionOf(channel) == ChannelDirection.LowerIsBetter;
}

public enum StatSide
{
    Plant = 0,
    Zombie = 1
}
