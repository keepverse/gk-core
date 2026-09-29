using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// `world-seed` (T5.1, `spec-world-seed.md`): the ONE place
/// <c>hash(worldSeed, streamName, targetId)</c> is computed. Every per-player generator — this
/// program's own resolver (module 2), `creature-seed`'s `player-materialise` (module 16) — derives
/// through <see cref="DeriveRollSeed"/>, never reimplements it, or two runtimes could disagree on the
/// same seed. Reuses <see cref="SeededRng.DeriveStream"/> exactly as it already runs in production
/// (<c>FusionRoller.cs:27</c>) — not a new hash function.
/// </summary>
public static class WorldSeed
{
    /// <summary>
    /// Pure and deterministic: the same three inputs always produce the same output, forever — no
    /// clock, no ambient state, no other input anywhere in the chain. <paramref name="streamName"/>
    /// is "which layer" (a module's own named stream, matching the `system:purpose`/`affix.slot`-
    /// style convention already established elsewhere), <paramref name="targetId"/> is "what is
    /// rolled" (a container id, a species id, ...) — the two axes are independent, so two different
    /// layers rolling the same target never collide, and two different targets in one layer never
    /// collide either.
    /// </summary>
    public static long DeriveRollSeed(long worldSeed, string streamName, string targetId)
    {
        if (string.IsNullOrEmpty(streamName)) throw new ArgumentException("streamName is empty", nameof(streamName));
        if (string.IsNullOrEmpty(targetId)) throw new ArgumentException("targetId is empty", nameof(targetId));

        return unchecked((long)SeededRng.DeriveStream(unchecked((ulong)worldSeed), $"{streamName}|{targetId}").NextULong());
    }
}
