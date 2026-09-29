using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// The only randomness the atom layer may touch. Named so a caller cannot reach for an ambient
/// RNG by accident.
/// </summary>
public interface IAtomRandom
{
    /// <summary>Uniform in <c>[min, max]</c>, both ends inclusive.</summary>
    int NextInclusive(int min, int max);

    /// <summary>A per-mille roll in <c>[0, 1000)</c>, for chance gates.</summary>
    int NextPerMille();
}

/// <summary>
/// The atom layer's named streams. `System.Random` never backs a replayable path — its seeded
/// sequence is not guaranteed stable across .NET versions, so a runtime upgrade would silently move
/// every replay with no content change to make it visible. These derive from the same owned
/// xoshiro256** the battle engine uses, and per-system streams mean an extra roll in one system
/// never shifts another.
/// </summary>
public static class AtomStreams
{
    /// <summary>Moment 4 — `OnApply` value rolls. Joins battle's initiative / crit / essence / status / proc.</summary>
    public const string Apply = "atom.apply";

    /// <summary>Chance gates on the runner (E15), kept off the value stream so a gate never shifts a magnitude.</summary>
    public const string Proc = "atom.proc";

    /// <summary>Weighted pool draws at instantiate (E6).</summary>
    public const string Pool = "atom.pool";
}

/// <summary>
/// A deterministic atom stream. Two constructors for the two roll moments: a named per-system stream
/// off the run seed for <see cref="RollPolicy.OnApply"/>, and a per-instance seed for
/// <see cref="RollPolicy.OnInstantiate"/> so re-reading an item reproduces it exactly.
/// </summary>
public sealed class AtomRandom : IAtomRandom
{
    readonly SeededRng _rng;

    /// <summary>A named stream derived from the run seed — use <see cref="AtomStreams"/> constants.</summary>
    public AtomRandom(ulong runSeed, string streamName)
        => _rng = SeededRng.DeriveStream(runSeed, streamName);

    /// <summary>An instance's frozen roll seed. Same seed, same rolls, forever.</summary>
    public AtomRandom(ulong instanceRollSeed)
        => _rng = new SeededRng(instanceRollSeed);

    public int NextInclusive(int min, int max)
    {
        if (min > max) throw new ArgumentException($"min {min} > max {max}");
        if (min == max) return min;

        // +1 because both ends are inclusive; widened to long so int.MinValue..int.MaxValue
        // does not overflow the span.
        var span = (long)max - min + 1;
        return (int)(min + (long)NextBelow((ulong)span));
    }

    public int NextPerMille() => _rng.NextPerMille();

    /// <summary>Unbiased draw in <c>[0, bound)</c> by rejection sampling, for spans wider than uint.</summary>
    ulong NextBelow(ulong bound)
    {
        var threshold = (0UL - bound) % bound; // 2^64 mod bound
        while (true)
        {
            var r = _rng.NextULong();
            if (r >= threshold) return r % bound;
        }
    }
}
