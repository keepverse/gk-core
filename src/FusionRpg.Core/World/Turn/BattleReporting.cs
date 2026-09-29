using FusionRpg.Core.Items.Drops;

namespace FusionRpg.Core.World.Turn;

/// <summary>
/// One request in, one line of report and a new world out. Both places that start a fight — a
/// meeting during movement and a `clear` during sieges — go through here, so a battle always costs
/// the same and always shows up in the report the same way.
///
/// <para>⭐ `drop-tables` `siege-loot` (2026-09-07): the real, confirmed call site for base-defense's
/// first loot binding — found in review that the original candidate call sites
/// (`DistrictAssaultResolver`/`DistrictAssaultPhase`) were under another session's active edit, and
/// that this file already computes the exact win/loss signal needed
/// (<see cref="EngagementExit.CoreTaken"/>) with no concurrent-edit risk. <b>The last hop is NOT
/// wired</b>: `TurnEngine.Step` → `Assaults` → `DistrictAssaultPhase.Run` → here would need
/// `DistrictAssaultPhase.cs` to thread the new `powerTuning`/`turn` parameters through, and that file
/// is exactly the one under active edit. `powerTuning is null` (every existing caller today) is
/// byte-identical to this file's pre-2026-09-07 behavior — proven by a dedicated test.</para>
/// </summary>
public static class BattleReporting
{
    /// <param name="arrivedViaLane">
    /// Entity id to lane id, for any force that fully arrived at a sector earlier in this same
    /// <see cref="Movement.MovementPhase"/> call — only <see cref="Movement.MovementPhase"/> ever
    /// populates it. Lets <see cref="BattleApplication"/> fall a freshly-arrived attacker back down
    /// the lane it just used if this fight routs it, the same way a mid-crossing rout already falls
    /// back down the lane it was on.
    /// </param>
    /// <param name="turn">Only read for the siege-loot correlation id below — every existing caller
    /// omits it (default 0), which is never observed since that path is itself gated on
    /// <paramref name="powerTuning"/>.</param>
    /// <param name="powerTuning">Null (the default) skips siege-loot resolution entirely, matching
    /// `ClaimResolver.Run`'s identical convention for `sector-loot-wiring`.</param>
    public static WorldState Fight(
        WorldState world, BattleRequest request, IBattleResolver resolver,
        TurnReport report, string phase, ulong seed,
        IReadOnlyDictionary<string, string>? arrivedViaLane = null,
        int turn = 0, FusionRpg.Core.Power.PowerTuning? powerTuning = null)
    {
        var attacker = world.Entities.FirstOrDefault(e =>
            string.Equals(e.EntityId, request.AttackerEntityId, StringComparison.Ordinal));
        if (attacker is null) return world;

        var combatants = new List<WorldEntity> { attacker };
        if (request.DefenderEntityId is { } defenderId)
        {
            var defender = world.Entities.FirstOrDefault(e =>
                string.Equals(e.EntityId, defenderId, StringComparison.Ordinal));

            // The other side may already have died earlier in the same turn; a fight with nobody in
            // it is silently no fight rather than a phantom victory.
            if (defender is null) return world;
            combatants.Add(defender);
        }

        // commander-roster EP3.11 (`spec-legion-commander.md`): a Commander member whose specimen is
        // not in `Roster` this turn is not fielded, and the reason is named here because this is the
        // one place that has both the resolver's rule and the report. Reported BEFORE the battle line
        // (the command drops' own ordering: the reason is read before what it led to), and scoped to
        // the attacker's own faction — a legion's absence is its owner's business, not the map's.
        // The resolver applies the identical predicate through `IsAway`, so the report and the fielded
        // set can never disagree.
        if (resolver is DistrictAssaultResolver district)
            foreach (var member in attacker.Members)
                if (DistrictAssaultResolver.IsAway(member, district.MemberAway))
                    report.Add(phase, TurnReportKinds.Event, request.BattleId,
                        $"{DistrictAssaultResolver.CommanderAway}:{member.SpeciesId}",
                        sectorId: request.Kind == BattleKinds.Lane ? null : request.LocationId,
                        audience: attacker.OwnerFactionId);

        var outcome = resolver.Resolve(request, combatants, seed);
        var next = BattleApplication.Apply(world, outcome, arrivedViaLane);

        if (outcome.GuardCleared && request.SlotIndex is { } slotIndex)
            next = BattleApplication.ClearGuard(next, request.LocationId, slotIndex);

        // New, and empty for every existing kind — so every existing battle takes the identical path.
        if (outcome.SlotResults.Count > 0)
            next = BattleApplication.ApplySlotResults(next, request.LocationId, outcome.SlotResults);

        // `LocationId` is "sector id, or lane id for a crossing" (`BattleSeam.cs:34`) — a lane-kind
        // battle must not put a lane id in the sector slot, which is exactly the class of bug
        // world-stage W13 exists to fix elsewhere in `MovementPhase.cs`; this line does not
        // reintroduce it for the one battle kind that can carry a lane id here.
        //
        // base-defense `siege-engagement` (module 20): a district assault names its own exit instead
        // of the generic winner-only line — "one report line per engagement, exit named" — scoped to
        // District only so every other kind's report text is byte-for-byte unchanged. Report text is
        // never part of `StateHasher.Hash` (confirmed via `TurnEngine.Step`'s own return line), so this
        // is golden-safe regardless, but the District-only guard keeps the change minimal anyway.
        var detail = request.Kind == BattleKinds.District && outcome.Exit is { } exit
            ? $"district:{request.LocationId}:{exit}"
            : $"{request.Kind}:{request.LocationId}:{outcome.WinnerEntityId ?? "none"}";
        report.Add(phase, TurnReportKinds.Battle, request.BattleId, detail,
            sectorId: request.Kind == BattleKinds.Lane ? null : request.LocationId);

        // siege-loot: inert until a real caller supplies powerTuning (see class doc). A won district
        // assault (CoreTaken) resolves a real LootSourceRow -- never mints through LootPipeline/
        // Instantiator, the same DB-write boundary sector-loot-wiring's own ClaimResolver call respects.
        if (powerTuning is { } tuning
            && request.Kind == BattleKinds.District && outcome.Exit == EngagementExit.CoreTaken)
        {
            var sector = next.Sectors.FirstOrDefault(s =>
                string.Equals(s.SectorId, request.LocationId, StringComparison.Ordinal));
            if (sector is not null)
            {
                var lootRejection = SiegeLoot.TryResolve(
                    sector.SectorId, turn, sector.DangerBand, sector.TypeId, tuning, out var lootSource);
                if (lootRejection.IsOk && lootSource is not null)
                    report.Add(phase, TurnReportKinds.Event, request.BattleId,
                        "siege.loot:" + lootSource.TableId, sector.SectorId);
            }
        }

        return next;
    }
}
