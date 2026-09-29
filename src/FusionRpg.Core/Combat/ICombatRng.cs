namespace FusionRpg.Core.Combat;

public interface ICombatRng
{
    /// <summary>Fisher–Yates / pick helper. Returns index in <c>[0, exclusiveMax)</c>.</summary>
    int Next(int exclusiveMax);
}

public sealed class SeededCombatRng : ICombatRng
{
    readonly Random _rng;

    public SeededCombatRng(int seed) => _rng = new Random(seed);

    public int Next(int exclusiveMax)
    {
        if (exclusiveMax <= 0) return 0;
        return _rng.Next(exclusiveMax);
    }
}
