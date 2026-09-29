using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Delve.Battle;

/// <summary>
/// D2.14 — what one raid member carries out of a room (spec-delve-battle-profile.md §5's golden-
/// argument table: `BattleActorResult.CarryOut`, `DelveCarryOut? { Statuses, Shield, Retreated }`,
/// populated only when the setup carried a `PartyIndex` — a data condition, not a profile branch).
/// `Statuses`/`Shield` mirror `BattleActorSetup.InitialStatuses`/`InnateShield`'s own types exactly,
/// since the host maps `CarryOut.Statuses → InitialStatuses` and the remaining shield → `InnateShield`
/// verbatim into the next room's setup (§5) — no re-shaping at the boundary.
/// </summary>
public sealed record DelveCarryOut(IReadOnlyList<BattleStatusSpec> Statuses, BattleInnateShield? Shield, bool Retreated);

/// <summary>
/// The setup mapping (§5): overlays a previous room's carry onto a fresh next-room setup built by
/// `encounter-generator`/the delve host's own party-half builder. Pure — no store, no clock; the
/// caller supplies the previous room's `HpRemaining` directly (**the one hp seat**, never re-derived
/// from `CarryOut` — `delve-attrition` §2 asserts `pools["hp"] == HpRemaining` at carry-out, but this
/// mapping does not read `CarryInPools` itself, since <see cref="DelveCarryOut"/> carries only
/// `Statuses`/`Shield`/`Retreated`; the five non-hp pools are `delve-attrition`'s own seat per §5's
/// closing line, "Resource pools are `delve-attrition`'s seat, not this module's").
/// </summary>
public static class DelveCarryIn
{
    /// <summary>Applies a carry-out onto a fresh setup for the actor's next room. A retreated or
    /// dead actor carries nothing forward (null) — there is no next-room setup to overlay for an
    /// actor who is not entering the next room at all; that decision belongs to whichever caller
    /// tracks raid membership across rooms, not this pure overlay.</summary>
    public static BattleActorSetup Apply(BattleActorSetup nextRoomSetup, long previousHpRemaining, DelveCarryOut carryOut) =>
        nextRoomSetup with
        {
            CurrentHp = previousHpRemaining,
            InitialStatuses = carryOut.Statuses,
            InnateShield = carryOut.Shield,
        };
}
