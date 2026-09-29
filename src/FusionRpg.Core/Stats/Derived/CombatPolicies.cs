namespace FusionRpg.Core.Stats.Derived;

public static class ElementMatchupPolicy
{
    public static double MatchupShareK => StatsTuningHub.Tuning.MatchupShareK;
}

public static class CombatProbabilityPolicy
{
    public static double AccuracyScale => StatsTuningHub.Tuning.AccuracyScale;
    public static double CritRateScale => StatsTuningHub.Tuning.CritRateScale;
    public static double CritDamageScale => StatsTuningHub.Tuning.CritDamageScale;
    public static double Steepness => StatsTuningHub.Tuning.Steepness;
}
