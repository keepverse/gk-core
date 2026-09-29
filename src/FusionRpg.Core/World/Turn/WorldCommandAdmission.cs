namespace FusionRpg.Core.World.Turn;

/// <summary>
/// Admission — the cheap gate at submit time (spec-turn-engine.md §Commands). It answers "is this
/// order well-formed, and is this commander entitled to give it?" and nothing else.
///
/// It deliberately does NOT judge whether the order will still be possible when the turn resolves.
/// That is legality-at-reveal, it belongs to the engine, and it drops the command into the turn
/// report with a reason rather than refusing the submission — one commander's stale order must
/// never abort a turn.
/// </summary>
public static class WorldCommandAdmission
{
    /// <summary>Command ids reach the store as a primary key, so they are bounded here.</summary>
    public const int MaxCommandIdLength = 64;

    /// <summary>
    /// The refusal every <c>bind-warden</c> order gets. The owner withdrew the per-sector warden on
    /// 2026-09-13 (warden-mortality-ideal.md), and warden-freeze-fix (RulesetVersion 13) retired
    /// the verb. The kind stays known: stored command logs still carry it and must still hydrate.
    /// </summary>
    public const string WardenRetired = "warden.retired";

    public static (bool Ok, string Reason) Admit(WorldState world, WorldCommand command)
    {
        if (!WorldCommandKinds.IsKnown(command.Kind))
            return (false, "kind.unknown");

        // Before any shape check, so no field of the order can make it admissible. This gate is the
        // one every path passes: a person's submit (RpgStore.SubmitWorldCommands), a policy's
        // commit, the engine's own re-admission at Reveal (which drops a bind filed before the
        // retirement), and the bind endpoint, which asks here before it charges any soul fee.
        if (command.Kind == WorldCommandKinds.BindWarden)
            return (false, WardenRetired);

        if (string.IsNullOrWhiteSpace(command.CommandId))
            return (false, "command.id-missing");
        if (command.CommandId.Length > MaxCommandIdLength)
            return (false, "command.id-too-long");

        var commander = world.Factions
            .FirstOrDefault(f => string.Equals(f.FactionId, command.CommanderId, StringComparison.Ordinal));
        if (commander is null)
            return (false, "commander.unknown");

        if (command.EntityId is { } entityId)
        {
            var entity = world.Entities
                .FirstOrDefault(e => string.Equals(e.EntityId, entityId, StringComparison.Ordinal));
            if (entity is null)
                return (false, "entity.unknown");
            if (!string.Equals(entity.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
                return (false, "entity.not-yours");
        }

        var namedSector = command.SectorId is { } sectorId
            ? world.Sectors.FirstOrDefault(s => string.Equals(s.SectorId, sectorId, StringComparison.Ordinal))
            : null;
        if (command.SectorId != null && namedSector is null)
            return (false, "sector.unknown");

        if (command.Kind == WorldCommandKinds.Stance)
        {
            if (command.EntityId is null) return (false, "entity.missing");
            if (!Movement.MovementPolicy.IsKnownStance(command.Stance)) return (false, "stance.unknown");
        }

        if (command.Kind == WorldCommandKinds.Claim)
        {
            if (command.EntityId is null) return (false, "entity.missing");
            if (namedSector is null) return (false, "sector.missing");
        }

        if (command.Kind == WorldCommandKinds.Cede)
        {
            // No entity — a faction cedes ground, not a legion.
            if (namedSector is null) return (false, "sector.missing");
            if (!string.Equals(namedSector.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
                return (false, "sector.not-yours");
        }

        if (command.Kind == WorldCommandKinds.Raise)
        {
            // No entity — raise founds a *new* legion, so there is nothing yet to anchor an
            // ownership check on at submission time. Every other legality check (whose ground, a
            // Seat, a hostile entity standing in it, enough RecruitStock) is resolution-time, in
            // RaiseResolver at Snapshot — "not yours at Snapshot", per this kind's own acceptance,
            // the identical discipline BuildResolver already applies for `build`.
            if (namedSector is null) return (false, "sector.missing");
        }

        if (command.Kind == WorldCommandKinds.Develop)
        {
            // No entity — `develop` starts a sector-wide project, not a legion order. Ownership is
            // deferred to `Snapshot`, the same discipline `raise` already applies (`DevelopResolver`
            // re-validates it there rather than trusting this admission gate).
            if (namedSector is null) return (false, "sector.missing");
            if (command.ProjectId is not { } projectId || !Growth.ProjectCatalog.IsKnown(projectId))
                return (false, "project.unknown");
        }

        if (command.Kind == WorldCommandKinds.Sustain)
        {
            if (command.EntityId is null) return (false, "entity.missing");
            if (command.Amount is not { } amount || amount <= 0) return (false, "amount.invalid");
        }

        if (command.Kind == WorldCommandKinds.Build)
        {
            if (command.EntityId is null) return (false, "entity.missing");
            if (namedSector is null) return (false, "sector.missing");
            if (command.SlotIndex is not { } buildSlotIndex
                || namedSector.Slots.All(sl => sl.SlotIndex != buildSlotIndex))
                return (false, "slot.unknown");
            if (command.StructureId is not { } structureId || !StructureCatalog.IsKnown(structureId))
                return (false, "structure.unknown");
            // loam-relics-and-wonders `wonder-build-flow` §Design 3: structural only — right count,
            // no duplicates. Never an ownership check; Core cannot perform one.
            if (StructureCatalog.Get(structureId).RelicCost is > 0 and var needed)
            {
                if (command.RelicInstanceIds.Count != needed) return (false, "relic.count-mismatch");
                if (command.RelicInstanceIds.Distinct(StringComparer.Ordinal).Count() != needed)
                    return (false, "relic.duplicate");
            }
        }

        if (command.Kind == WorldCommandKinds.Clear)
        {
            // `clear` names its target outright — entity, sector, slot. Whether the legion is
            // actually standing there, and whether the guard is still up, is legality at reveal:
            // both can change between filing the order and the turn resolving.
            if (command.EntityId is null) return (false, "entity.missing");
            if (namedSector is null) return (false, "sector.missing");
            if (command.SlotIndex is not { } slotIndex
                || namedSector.Slots.All(sl => sl.SlotIndex != slotIndex))
                return (false, "slot.unknown");
        }

        if (command.Kind == WorldCommandKinds.Assault)
        {
            // `assault` names its target outright — entity, sector. Whether the legion is actually
            // standing there, and whether the sector is still hostile, is legality at reveal: both
            // can change between filing the order and the turn resolving, the same discipline
            // `clear`'s own admission rule already states.
            if (command.EntityId is null) return (false, "entity.missing");
            if (namedSector is null) return (false, "sector.missing");
        }

        // empire-inventory-surfaces `cargo-commands` §Design 3: five arms, one per verb family.
        // The generic EntityId ownership check above already covers every kind's acting legion;
        // each arm adds only its own shape checks. Whether the order is still possible at commit
        // (presence, faction, capacity, reachability) is legality-at-reveal in the Data-side pass.
        if (command.Kind == WorldCommandKinds.LoadCargo)
        {
            if (command.EntityId is null) return (false, "entity.missing");
            if (command.CargoKind != "instance" && command.CargoKind != "stack")
                return (false, "cargo.kind-unknown");
            // Mirrors the verb's own throws as refusals, never exceptions
            // (RpgStore.LegionCargo.cs LoadCargoUnlocked).
            if (command.CargoKind == "instance" && string.IsNullOrWhiteSpace(command.InstanceId))
                return (false, "cargo.ref-missing");
            if (command.CargoKind == "stack"
                && (string.IsNullOrWhiteSpace(command.ContainerId)
                    || command.Qty is not { } loadQty || loadQty <= 0))
                return (false, "cargo.ref-missing");
        }

        if (command.Kind == WorldCommandKinds.UnloadCargo)
        {
            if (command.EntityId is null) return (false, "entity.missing");
            if (command.Seq is null) return (false, "cargo.seq-missing");
        }

        if (command.Kind == WorldCommandKinds.TransferCargo)
        {
            if (command.EntityId is null) return (false, "entity.missing");
            if (string.IsNullOrWhiteSpace(command.TargetEntityId)) return (false, "cargo.target-missing");
            // The destination is ownership-checked here, not only at resolve: both legions must
            // be the commander's at filing just as at resolution (spec §Design 3).
            var target = world.Entities.FirstOrDefault(e =>
                string.Equals(e.EntityId, command.TargetEntityId, StringComparison.Ordinal));
            if (target is null
                || !string.Equals(target.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
                return (false, "entity.not-yours");
            if (command.Seq is null) return (false, "cargo.seq-missing");
        }

        if (command.Kind == WorldCommandKinds.DepositCargo
            || command.Kind == WorldCommandKinds.WithdrawCargo)
        {
            if (command.EntityId is null) return (false, "entity.missing");
            // The `Claim`/`Clear` arm precedent: a null SectorId refuses `sector.missing` here;
            // an unknown id never reaches this arm (the shared `sector.unknown` check above).
            if (namedSector is null) return (false, "sector.missing");
            if (command.Seq is null) return (false, "cargo.seq-missing");
        }

        if (command.Kind == WorldCommandKinds.ClaimCache)
        {
            if (command.EntityId is null) return (false, "entity.missing");
            if (string.IsNullOrWhiteSpace(command.CacheId)) return (false, "cache.missing");
            // `correlationId` is `CommandId` by lock (spec §Design 4) — `command.id-missing`
            // above already guarantees it non-empty, so `correlation.missing` is unreachable on
            // this path: named, not re-checked.
        }

        foreach (var laneId in command.LanePath)
            if (world.Lanes.All(l => !string.Equals(l.LaneId, laneId, StringComparison.Ordinal)))
                return (false, "lane.unknown");

        return (true, "ok");
    }
}
