namespace FusionRpg.Core.Stats;

public interface IComposeStrategy
{
    double ComposeChannel(double baseline, IEnumerable<StatModifier> channelMods);
}

/// <summary>
/// Flat → Increased(sum) → More(product) → Override(highest priority).
/// Override replaces the entire channel result (no leftover Flat).
/// </summary>
public sealed class PhasedComposeStrategy : IComposeStrategy
{
    public double ComposeChannel(double baseline, IEnumerable<StatModifier> channelMods)
    {
        var list = channelMods.ToList();
        var overrides = list.Where(m => m.Op == ModifierOp.Override)
            .OrderByDescending(m => m.Priority)
            .ThenBy(m => m.SourceId, StringComparer.Ordinal)
            .ToList();
        if (overrides.Count > 0)
            return overrides[0].Value;

        var flat = list.Where(m => m.Op == ModifierOp.Flat).Sum(m => m.Value);
        var increased = list.Where(m => m.Op == ModifierOp.Increased).Sum(m => m.Value);
        var more = list.Where(m => m.Op == ModifierOp.More).ToList();

        var afterFlat = baseline + flat;
        var afterInc = afterFlat * (1.0 + increased);
        var afterMore = afterInc;
        foreach (var m in more)
            afterMore *= 1.0 + m.Value;
        return afterMore;
    }
}

public sealed class StatComposer
{
    readonly IComposeStrategy _strategy;

    public StatComposer(IComposeStrategy? strategy = null) =>
        _strategy = strategy ?? new PhasedComposeStrategy();

    public EntityFinal Compose(EntityBaseline baseline, IModifierBagReader bag, bool applyStats)
    {
        var all = bag.All;
        if (!applyStats)
        {
            // Scales off: still honor Override mods (Tab B absolute). Ignore Flat/Increased/More.
            var overrides = all.Where(m => m.Op == ModifierOp.Override).ToList();
            if (overrides.Count == 0)
            {
                return new EntityFinal
                {
                    Hp = Math.Max(1L, baseline.Hp),
                    MaxHp = Math.Max(1L, baseline.MaxHp),
                    Atk = Math.Max(1L, baseline.Atk),
                    Arm1 = Math.Max(0L, baseline.Arm1),
                    Arm1Max = Math.Max(0L, baseline.Arm1Max),
                    Arm2 = Math.Max(0L, baseline.Arm2),
                    Arm2Max = Math.Max(0L, baseline.Arm2Max),
                    DefensePercent = 1f,
                    DefenseFlat = 0,
                    AttackInterval = baseline.AttackInterval,
                    ProduceInterval = baseline.ProduceInterval,
                    ZombieSpeed = baseline.ZombieSpeed,
                    // E38: no composition to do with scales off and no Override present — same
                    // pass-through as every other channel above.
                    PlantShield = baseline.PlantShield,
                    AttackCountdown = baseline.AttackCountdown,
                    AttackSpeedAdder = baseline.AttackSpeedAdder,
                    ProduceCountdown = baseline.ProduceCountdown,
                    PlantSpeed = baseline.PlantSpeed,
                    PlantMoveSpeed = baseline.PlantMoveSpeed,
                    PlantLevel = baseline.PlantLevel,
                    ShootingLevel = baseline.ShootingLevel,
                    ArmorFlat = baseline.ArmorFlat,
                    TakeDmgMultiplier = baseline.TakeDmgMultiplier,
                    ZombieSpeedCurrent = baseline.ZombieSpeedCurrent,
                    ZombieOriginSpeed = baseline.ZombieOriginSpeed,
                    Contributions = all
                };
            }

            all = overrides;
        }

        long ChanHp(string channel, long y0, long min = 1) =>
            Math.Max(min, (long)Math.Round(_strategy.ComposeChannel(y0, all.Where(m => m.Channel == channel))));

        long Chan(string channel, long y0, long min = 1) =>
            Math.Max(min, (long)Math.Round(_strategy.ComposeChannel(y0, all.Where(m => m.Channel == channel))));

        double Real(string channel, double y0, double min)
        {
            // A zero baseline means the entity has no such stat — a zombie has no produce interval.
            // Composing modifiers onto it would invent one out of nothing.
            if (y0 <= 0) return 0;
            return Math.Max(min, _strategy.ComposeChannel(y0, all.Where(m => m.Channel == channel)));
        }

        // Floored above zero, not at it. `More` −100% on an interval is ordinary content, and the
        // result is a divide-by-zero or an infinite fire rate depending on which call site reads it.
        double Interval(string channel, double y0) =>
            Real(channel, y0, StatChannels.MinimumInterval);

        // E38 (spec-entity-fields-12plus.md): deliberately NOT `Real`/`Interval` — those treat a
        // zero baseline as "this entity has no such stat", true for E16's three (each captured on
        // only one side of the entity model, so the wrong-side baseline is always a C# default
        // zero). All twelve of E38's channels are captured from a genuine live field on THEIR OWN
        // side (EntityApply.cs's plant/zombie baseline construction), so a zero baseline is an
        // ordinary value — "no shield right now", "no attack-speed adder applied" — and skipping
        // composition on it would make the channel uncomposable for the common case, not merely
        // inert for an absent one.
        double RealAlways(string channel, double y0, double min) =>
            Math.Max(min, _strategy.ComposeChannel(y0, all.Where(m => m.Channel == channel)));

        // attackCountdown/produceCountdown share the interval floor's structural reason (driven to
        // zero or below is the same divide-by-zero / infinite-fire-rate risk an interval has) but
        // not `Interval`'s absent-baseline skip — a firing plant's countdown legitimately reads 0
        // mid-cycle, and that must still compose.
        double IntervalAlways(string channel, double y0) =>
            RealAlways(channel, y0, StatChannels.MinimumInterval);

        ComposeDefense(all, out var defPct, out var defFlat);

        return new EntityFinal
        {
            Hp = ChanHp(StatChannels.Hp, baseline.Hp),
            MaxHp = ChanHp(StatChannels.MaxHp, baseline.MaxHp),
            Atk = Chan(StatChannels.Atk, baseline.Atk),
            Arm1 = Chan(StatChannels.Arm1, baseline.Arm1, min: 0),
            Arm1Max = Chan(StatChannels.Arm1Max, baseline.Arm1Max, min: 0),
            Arm2 = Chan(StatChannels.Arm2, baseline.Arm2, min: 0),
            Arm2Max = Chan(StatChannels.Arm2Max, baseline.Arm2Max, min: 0),
            DefensePercent = defPct,
            DefenseFlat = defFlat,
            AttackInterval = Interval(StatChannels.AttackInterval, baseline.AttackInterval),
            ProduceInterval = Interval(StatChannels.ProduceInterval, baseline.ProduceInterval),
            ZombieSpeed = Real(StatChannels.ZombieSpeed, baseline.ZombieSpeed, min: 0),

            // E38's twelve. Magnitudes go through the same long `Chan` every other magnitude channel
            // uses; ratios/timers go through `RealAlways`/`IntervalAlways` above (never `Real`/
            // `Interval` — see those helpers' own doc comments). Plant speed/move-speed mirror
            // `zombieSpeed`'s existing `Real` shape exactly: most plants never move, so a zero
            // baseline there genuinely does mean "this plant has no such stat" the same way E16's
            // three do.
            PlantShield = Chan(StatChannels.PlantShield, baseline.PlantShield, min: 0),
            AttackCountdown = IntervalAlways(StatChannels.AttackCountdown, baseline.AttackCountdown),
            // Unguarded by design (§2b, decided 2026-09-03): an adder is a signed delta, so it gets
            // no floor at all — not even zero. A negative value is ordinary content here, unlike the
            // countdowns above (which cannot be negative) or the speeds (which freeze at zero).
            AttackSpeedAdder = RealAlways(
                StatChannels.AttackSpeedAdder, baseline.AttackSpeedAdder, min: double.NegativeInfinity),
            ProduceCountdown = IntervalAlways(StatChannels.ProduceCountdown, baseline.ProduceCountdown),
            PlantSpeed = Real(StatChannels.PlantSpeed, baseline.PlantSpeed, min: 0),
            PlantMoveSpeed = Real(StatChannels.PlantMoveSpeed, baseline.PlantMoveSpeed, min: 0),
            PlantLevel = Chan(StatChannels.PlantLevel, baseline.PlantLevel, min: 0),
            ShootingLevel = Chan(StatChannels.ShootingLevel, baseline.ShootingLevel, min: 0),
            ArmorFlat = RealAlways(StatChannels.ArmorFlat, baseline.ArmorFlat, min: 0),
            TakeDmgMultiplier = RealAlways(StatChannels.TakeDmgMultiplier, baseline.TakeDmgMultiplier, min: 0),
            ZombieSpeedCurrent = Real(StatChannels.ZombieSpeedCurrent, baseline.ZombieSpeedCurrent, min: 0),
            ZombieOriginSpeed = Real(StatChannels.ZombieOriginSpeed, baseline.ZombieOriginSpeed, min: 0),

            Contributions = bag.All
        };
    }

    /// <summary>
    /// DEF for ScaleIncoming: percent from Increased/More/Override on baseline 1 via strategy;
    /// Flat summed separately. Override replaces the whole defense view (percent = value, flat = 0).
    /// </summary>
    void ComposeDefense(IReadOnlyList<StatModifier> all, out float defensePercent, out long defenseFlat)
    {
        var def = all.Where(m => m.Channel == StatChannels.Defense).ToList();
        var ov = def.Where(m => m.Op == ModifierOp.Override)
            .OrderByDescending(m => m.Priority)
            .ThenBy(m => m.SourceId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (ov != null)
        {
            defensePercent = (float)Math.Max(0.0001, ov.Value);
            defenseFlat = 0;
            return;
        }

        defenseFlat = (long)Math.Round(def.Where(m => m.Op == ModifierOp.Flat).Sum(m => m.Value));
        var pctMods = def.Where(m => m.Op is ModifierOp.Increased or ModifierOp.More);
        var pct = _strategy.ComposeChannel(1.0, pctMods);
        defensePercent = (float)Math.Max(0.0001, pct);
    }
}
