using System.Globalization;

namespace FusionRpg.Tools.CombatSim;

public readonly record struct Percentiles(
    double Min, double P5, double P25, double Median, double P75, double P95, double Max, double Mean)
{
    public static Percentiles Of(IReadOnlyList<long> values)
    {
        if (values.Count == 0) return default;
        var sorted = values.OrderBy(v => v).ToArray();
        return new Percentiles(
            sorted[0], At(sorted, 0.05), At(sorted, 0.25), At(sorted, 0.50),
            At(sorted, 0.75), At(sorted, 0.95), sorted[^1], sorted.Average());
    }

    static double At(long[] sorted, double q)
    {
        // Nearest-rank. Deliberately not interpolated: damage is integral, and an interpolated p95
        // reports a number the pipeline can never actually produce.
        var idx = (int)Math.Ceiling(q * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}

public sealed class Summary
{
    public required string Label { get; init; }
    public required int Trials { get; init; }

    public double MissRate { get; init; }
    public double ParryRate { get; init; }
    public double BlockRate { get; init; }
    public double CleanHitRate { get; init; }
    public double CritRateOfCleanHits { get; init; }

    public double MeanBaseDamage { get; init; }
    public Percentiles DefenderDamage { get; init; }
    public double MeanDamageRatio { get; init; }
    public double MeanShieldAbsorbed { get; init; }
    public int ZeroDamageTrials { get; init; }

    public double ReflectRate { get; init; }
    public double MeanBouncedWhenReflected { get; init; }
    public double SelfDamageShareOfDealt { get; init; }
    public int MaxBouncesInOneTrial { get; init; }

    public static Summary From(string label, IReadOnlyList<TrialResult> t)
    {
        var n = t.Count;
        if (n == 0) throw new InvalidOperationException("no trials");
        var reflected = t.Where(x => x.Reflected).ToList();
        var totalDealt = t.Sum(x => x.DefenderDamage);
        var totalSelf = t.Sum(x => x.AttackerSelfDamage);
        var cleanHits = t.Count(x => x.CleanHit);

        return new Summary
        {
            Label = label,
            Trials = n,
            MissRate = t.Count(x => x.Missed) / (double)n,
            ParryRate = t.Count(x => x.Parried) / (double)n,
            BlockRate = t.Count(x => x.Blocked) / (double)n,
            CleanHitRate = cleanHits / (double)n,
            CritRateOfCleanHits = cleanHits == 0 ? 0 : t.Count(x => x.Crit) / (double)cleanHits,
            MeanBaseDamage = t.Average(x => (double)x.BaseDamage),
            DefenderDamage = Percentiles.Of(t.Select(x => x.DefenderDamage).ToList()),
            MeanDamageRatio = t.Average(x => x.BaseDamage == 0 ? 0 : x.DefenderDamage / (double)x.BaseDamage),
            MeanShieldAbsorbed = t.Average(x => (double)x.ShieldAbsorbed),
            ZeroDamageTrials = t.Count(x => x.DefenderDamage == 0),
            ReflectRate = reflected.Count / (double)n,
            MeanBouncedWhenReflected = reflected.Count == 0 ? 0 : reflected.Average(x => (double)x.AttackerSelfDamage),
            SelfDamageShareOfDealt = totalDealt == 0 ? 0 : totalSelf / (double)totalDealt,
            MaxBouncesInOneTrial = t.Max(x => x.ReflectBounces)
        };
    }

    /// <summary>Flat metric map — the CSV/JSON surface, so a sweep row and the console agree.</summary>
    public IReadOnlyList<KeyValuePair<string, double>> Metrics() => new[]
    {
        new KeyValuePair<string, double>("trials", Trials),
        new("missRate", MissRate),
        new("parryRate", ParryRate),
        new("blockRate", BlockRate),
        new("cleanHitRate", CleanHitRate),
        new("critRateOfCleanHits", CritRateOfCleanHits),
        new("meanBaseDamage", MeanBaseDamage),
        new("dmgMin", DefenderDamage.Min),
        new("dmgP5", DefenderDamage.P5),
        new("dmgP25", DefenderDamage.P25),
        new("dmgMedian", DefenderDamage.Median),
        new("dmgP75", DefenderDamage.P75),
        new("dmgP95", DefenderDamage.P95),
        new("dmgMax", DefenderDamage.Max),
        new("dmgMean", DefenderDamage.Mean),
        new("meanDamageRatio", MeanDamageRatio),
        new("meanShieldAbsorbed", MeanShieldAbsorbed),
        new("zeroDamageTrials", ZeroDamageTrials),
        new("reflectRate", ReflectRate),
        new("meanBouncedWhenReflected", MeanBouncedWhenReflected),
        new("selfDamageShareOfDealt", SelfDamageShareOfDealt),
        new("maxBouncesInOneTrial", MaxBouncesInOneTrial)
    };

    public string ToConsole()
    {
        var w = new StringWriter(CultureInfo.InvariantCulture);
        w.WriteLine($"  trials              {Trials:N0}   base damage (mean) {MeanBaseDamage:N1}");
        w.WriteLine();
        w.WriteLine("  OUTCOME                        DAMAGE TO DEFENDER");
        w.WriteLine($"    miss        {Pct(MissRate),8}         min      {DefenderDamage.Min,12:N0}");
        w.WriteLine($"    parried     {Pct(ParryRate),8}         p5       {DefenderDamage.P5,12:N0}");
        w.WriteLine($"    blocked     {Pct(BlockRate),8}         p25      {DefenderDamage.P25,12:N0}");
        w.WriteLine($"    clean hit   {Pct(CleanHitRate),8}         median   {DefenderDamage.Median,12:N0}");
        w.WriteLine($"    (crit|clean){Pct(CritRateOfCleanHits),8}         p75      {DefenderDamage.P75,12:N0}");
        w.WriteLine($"                                   p95      {DefenderDamage.P95,12:N0}");
        w.WriteLine($"    zero-damage {ZeroDamageTrials,8}         max      {DefenderDamage.Max,12:N0}");
        w.WriteLine($"                                   mean     {DefenderDamage.Mean,12:N1}");
        w.WriteLine();
        w.WriteLine($"  damage / base (mean)        {MeanDamageRatio,8:F3}×");
        if (MeanShieldAbsorbed > 0)
            w.WriteLine($"  shield absorbed (mean)      {MeanShieldAbsorbed,8:N1}");
        w.WriteLine();
        w.WriteLine("  REFLECTION");
        w.WriteLine($"    triggered            {Pct(ReflectRate),8}");
        w.WriteLine($"    bounced when it did  {MeanBouncedWhenReflected,8:N1}");
        w.WriteLine($"    attacker self-dmg    {Pct(SelfDamageShareOfDealt),8}  of all damage it dealt");
        w.WriteLine($"    max bounces / trial  {MaxBouncesInOneTrial,8}");
        return w.ToString();
    }

    static string Pct(double v) => (v * 100).ToString("F2", CultureInfo.InvariantCulture) + "%";
}

/// <summary>Outcome of many fights — who wins, how fast, and how much of the kill was reflection.</summary>
public sealed class FightSummary
{
    public required string Label { get; init; }
    public required int Fights { get; init; }
    public double AttackerWinRate { get; init; }
    public double DefenderWinRate { get; init; }
    public double MutualKillRate { get; init; }
    public double StalemateRate { get; init; }
    public Percentiles Rounds { get; init; }
    public double MeanDamageDealt { get; init; }
    public double MeanDamageReflected { get; init; }
    public double ReflectedShareOfDealt { get; init; }
    public double MeanAttackerHpLeftPct { get; init; }

    public static FightSummary From(string label, IReadOnlyList<FightResult> f, double attackerStartHp)
    {
        var n = f.Count;
        var dealt = f.Sum(x => (double)x.DamageDealt);
        return new FightSummary
        {
            Label = label,
            Fights = n,
            // Four categories, and they MUST partition: an earlier three-way split silently reported
            // 0%/0%/0% for a pure thorns build, because the dominant outcome there is that BOTH die
            // on the same swing — reflection returning 100% of damage kills its owner's killer in
            // lockstep. A missing category reads as "nothing happened", which is the one failure
            // mode a measurement tool must not have.
            AttackerWinRate = f.Count(x => x.DefenderDied && !x.AttackerDied) / (double)n,
            DefenderWinRate = f.Count(x => x.DefenderWins) / (double)n,
            MutualKillRate = f.Count(x => x.AttackerDied && x.DefenderDied) / (double)n,
            StalemateRate = f.Count(x => x.Stalemate) / (double)n,
            Rounds = Percentiles.Of(f.Select(x => (long)x.Rounds).ToList()),
            MeanDamageDealt = f.Average(x => (double)x.DamageDealt),
            MeanDamageReflected = f.Average(x => (double)x.DamageReflected),
            ReflectedShareOfDealt = dealt == 0 ? 0 : f.Sum(x => (double)x.DamageReflected) / dealt,
            MeanAttackerHpLeftPct = attackerStartHp <= 0
                ? 0 : f.Average(x => x.AttackerHpLeft / attackerStartHp)
        };
    }

    public IReadOnlyList<KeyValuePair<string, double>> Metrics() => new[]
    {
        new KeyValuePair<string, double>("fights", Fights),
        new("attackerWinRate", AttackerWinRate),
        new("defenderWinRate", DefenderWinRate),
        new("mutualKillRate", MutualKillRate),
        new("stalemateRate", StalemateRate),
        new("roundsMedian", Rounds.Median),
        new("roundsP95", Rounds.P95),
        new("roundsMax", Rounds.Max),
        new("meanDamageDealt", MeanDamageDealt),
        new("meanDamageReflected", MeanDamageReflected),
        new("reflectedShareOfDealt", ReflectedShareOfDealt),
        new("meanAttackerHpLeftPct", MeanAttackerHpLeftPct)
    };
}
