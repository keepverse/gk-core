using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// `encounter-generator` D2.5 (spec-encounter-generator.md §5 "Shield pool") — the ONE `P(Θ_room)`
/// read this module makes, registered in `docs/architecture/power/inventory.json` against row 20
/// (`PowerLadder`) rather than a new row: this is a reviewed READ of the one ladder, never a second
/// curve (PS-2 — read once, here).
///
/// <para><b>Solo carries no shield at all.</b> `parties == 1` returns null — decision 3's own
/// "capacity per extra party" framing has nothing to add at N=1, and `raid.modes.solo` carries no
/// `bossShieldPerPartyMilli` key at all (absent by schema, not by omission — `DungeonTuning.cs`'s own
/// `RaidModeTuning.BossShieldPerPartyMilli` is `long?` for exactly this reason).</para>
/// </summary>
public static class BossShieldPool
{
    /// <summary>
    /// `pool = bossShieldPerPartyMilli · P(Θ_room) · (N−1) / 1000` — `long` throughout, widened before
    /// each multiply (both operands are already `long` or promote to one), divided by 1000 exactly
    /// once, last. Applied by the engine as an innate grant at t0, drained ahead of the sink
    /// (`ShieldGate.AbsorbFinalized`) — this method only computes the capacity, never touches the
    /// engine.
    /// </summary>
    public static BattleInnateShield? Resolve(int roomTheta, int parties, long? bossShieldPerPartyMilli, PowerLadder ladder)
    {
        if (parties < 1) throw new ArgumentOutOfRangeException(nameof(parties), "a raid always has at least 1 party.");
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));

        if (parties == 1) return null; // "Solo: none." -- §5, verbatim

        if (bossShieldPerPartyMilli is not { } shareMilli)
            throw new InvalidOperationException(
                $"{parties} parties, but bossShieldPerPartyMilli is absent — only solo (1 party) is allowed to omit it.");

        var pTheta = ladder.Value(roomTheta); // PS-2: the one read
        long pool;
        checked { pool = shareMilli * pTheta * (parties - 1) / 1000; }

        return new BattleInnateShield(BaseHp: pool, Priority: ShieldPolicy.PriorityInnate);
    }
}
