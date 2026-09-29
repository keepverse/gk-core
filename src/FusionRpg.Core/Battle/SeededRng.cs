namespace FusionRpg.Core.Battle;

/// <summary>
/// Owned deterministic PRNG — xoshiro256** seeded via splitmix64 (spec-match-source-core.md).
/// Never use System.Random for anything replayable: its seeded sequence is not guaranteed
/// stable across .NET versions. This implementation is spec-fixed; changing it bumps RngAlgoVersion.
/// Per-system streams derive from one run seed so an extra roll in one system never shifts another.
/// </summary>
public sealed class SeededRng
{
    public const int RngAlgoVersion = 1;

    ulong _s0, _s1, _s2, _s3;

    public SeededRng(ulong seed)
    {
        // splitmix64 expansion of the seed into the four xoshiro words (never all-zero).
        var sm = seed;
        _s0 = SplitMix64(ref sm);
        _s1 = SplitMix64(ref sm);
        _s2 = SplitMix64(ref sm);
        _s3 = SplitMix64(ref sm);
    }

    /// <summary>Derive an independent stream from a run seed and a stable stream name.</summary>
    public static SeededRng DeriveStream(ulong runSeed, string streamName) =>
        new(runSeed ^ Fnv1a64(streamName));

    public ulong NextULong()
    {
        var result = Rotl(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);
        return result;
    }

    /// <summary>Unbiased [0, maxExclusive) via rejection sampling.</summary>
    public uint NextUInt(uint maxExclusive)
    {
        if (maxExclusive == 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        var threshold = (uint)(-maxExclusive) % maxExclusive; // 2^32 mod max
        while (true)
        {
            var r = (uint)(NextULong() >> 32);
            if (r >= threshold)
                return r % maxExclusive;
        }
    }

    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(maxExclusive));
        return (int)NextUInt((uint)maxExclusive);
    }

    /// <summary>Integer per-mille roll — combat math stays integer-only; compare against ‰ thresholds.</summary>
    public int NextPerMille() => NextInt(1000);

    static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    static ulong SplitMix64(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        var z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    static ulong Fnv1a64(string text)
    {
        var h = 14695981039346656037UL;
        foreach (var ch in text)
        {
            h ^= ch;
            h *= 1099511628211UL;
        }

        return h;
    }
}
