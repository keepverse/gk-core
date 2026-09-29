using System.Globalization;
using System.Text;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World.Intel;

namespace FusionRpg.Core.World;

/// <summary>
/// One canonical text form of a world. Everything determinism-related leans on this: template
/// determinism tests, the turn engine's state hash, and replay comparison.
///
/// Two rules keep it trustworthy as a hash input. Numbers format with
/// <see cref="CultureInfo.InvariantCulture"/> — a machine whose culture writes a non-ASCII negative
/// sign must not produce a different world hash. And collections are written in their **stored**
/// order, never re-sorted here, so a mis-ordered model shows up as a diff instead of being quietly
/// normalised away.
/// </summary>
public static class WorldCanonical
{
    public static string Write(WorldState w)
    {
        var sb = new StringBuilder();

        // The world's *id* is deliberately absent: it is identity, not state. Two worlds built from
        // the same template and seed and played the same way must hash identically, or a golden
        // could never be compared across saves, machines, or test fixtures.
        Row(sb, "world", w.TemplateId, w.Seed, w.CurrentTurn);

        foreach (var f in w.Factions)
            Row(sb, "faction", f.FactionId, f.Kind, f.Name, f.PolicyId, f.UpkeepHandicapMilli);

        foreach (var s in w.Sectors)
        {
            // `s.WardenBindingId` has always been in this row, but every world before world-stage
            // W28 held it null — no production path ever set it. It is the first field here whose
            // hash effect isn't "a number changed": once `bind-warden` (W28) is ordered, this cell
            // carries a real id and the world's hash differs with no numeric magnitude having moved.
            // world-map W44: RecruitStock/ProjectId/ProjectTurnsRemaining land together in one
            // batched re-bless (decisions.md's L25 precedent) — every world before this task held
            // all three at their zero/null default, so this cell's hash effect is the field batch
            // itself, not a number that moved.
            Row(sb, "sector", s.SectorId, s.TypeId, s.Climate, s.DangerBand, s.Phase, s.OwnerFactionId,
                s.StabilityMilli, s.PressureMilli, s.DepletionMilli, s.DevelopmentLevel,
                s.AuthoredIntel, s.LastSeenTurn, s.LayoutX, s.LayoutY,
                s.LoamStock, s.FractureIntensityMilli, s.WardenBindingId, s.NeglectedTurns,
                s.RecruitStock, s.ProjectId, s.ProjectTurnsRemaining);

            foreach (var sl in s.Slots)
                Row(sb, "slot", s.SectorId, sl.SlotIndex, sl.SlotTypeId, sl.Element, sl.State,
                    sl.OwnerFactionId, sl.GuardWaveId, sl.GuardState, sl.StructureId,
                    sl.ConstructionTurnsRemaining);
        }

        foreach (var l in w.Lanes)
            Row(sb, "lane", l.LaneId, l.FromSectorId, l.ToSectorId, l.TypeId, l.Length, l.Width,
                l.HazardMilli, l.WardLevel, l.GateKeyId, l.State);

        foreach (var e in w.Entities)
        {
            Row(sb, "entity", e.EntityId, e.Kind, e.OwnerFactionId, e.AtSectorId, e.OnLaneId,
                e.OnLaneTowardSectorId, e.LaneProgressMilli, e.Stance, e.MovementRemaining,
                e.Routed ? 1 : 0, e.CarriedLoam);

            for (var i = 0; i < e.Members.Count; i++)
            {
                var m = e.Members[i];
                Row(sb, "member", e.EntityId, i, m.InstanceId, m.SpeciesId, m.Level, m.Hp, m.Wounds, m.Role);
            }
        }

        // Belief is state, so it is hashed like state. Written last so a world with no intel yet
        // — every wave-1 save — produces exactly the bytes it always did.
        foreach (var faction in w.Intel)
        foreach (var snapshot in faction.Sectors)
        {
            Row(sb, "intel", faction.FactionId, snapshot.SectorId, snapshot.LastSeenTurn, snapshot.Detail,
                snapshot.OwnerFactionId, snapshot.Phase, snapshot.Climate, snapshot.DangerBand,
                snapshot.DevelopmentLevel);

            foreach (var slot in snapshot.Slots)
                Row(sb, "intel-slot", faction.FactionId, snapshot.SectorId, slot.SlotIndex,
                    slot.SlotTypeId, slot.Element, slot.GuardWaveId, slot.State, slot.GuardState,
                    slot.StructureId, slot.ConstructionTurnsRemaining);

            foreach (var force in snapshot.Forces)
                Row(sb, "intel-force", faction.FactionId, snapshot.SectorId, force.EntityId,
                    force.OwnerFactionId, force.Kind, force.Exact ? 1 : 0, force.Strength, force.BandIndex);
        }

        // buff-debuff-scope T12: same non-breaking shape as Intel above — a world with no active
        // scope modifier (every save/golden that predates this field) emits nothing here and
        // produces exactly the bytes it always did. Appending this to the existing "faction" row
        // instead would have moved every prior hash for a value that did not actually change
        // (found live: it moved WorldWaveOneAcceptanceTests' own golden even at the neutral default).
        foreach (var f in w.Factions)
        {
            if (f.ScopeModifierMilli != 1000)
                Row(sb, "faction-scope", f.FactionId, f.ScopeModifierMilli);
        }

        // base-defense `structure-state`: the SAME conditional-row shape as `faction-scope` above,
        // applied twice more. A separate row emitted only when the value is off its default, NOT a
        // column appended to the existing `slot` row — appending would move every prior hash for a
        // value that did not change, the exact failure `faction-scope`'s own comment records finding
        // live. `s.Slots` is already documented as ordered by SlotIndex, contiguous from zero, so no
        // explicit sort is needed here either — the same invariant the existing `slot` loop above
        // already relies on without re-asserting it.
        foreach (var s in w.Sectors)
        {
            foreach (var sl in s.Slots)
            {
                if (sl.StructureHp is { } hp)
                    Row(sb, "slot-hp", s.SectorId, sl.SlotIndex, hp);
                if (sl.SlotDepletionMilli != 0)
                    Row(sb, "slot-depletion", s.SectorId, sl.SlotIndex, sl.SlotDepletionMilli);
            }
        }

        // base-defense `siege-construction`: same conditional-row shape again — RubbleStock/
        // IronworkStock are new fields on `WorldSector`, and every world before this task holds both
        // at zero, so a separate off-default row (not a column appended to the existing "sector" row
        // above) keeps every prior hash byte-identical.
        foreach (var s in w.Sectors)
        {
            if (s.RubbleStock != 0)
                Row(sb, "sector-rubble", s.SectorId, s.RubbleStock);
            if (s.IronworkStock != 0)
                Row(sb, "sector-ironwork", s.SectorId, s.IronworkStock);
        }

        return sb.ToString();
    }

    static void Row(StringBuilder sb, params object?[] cells)
    {
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append('\t');
            sb.Append(Cell(cells[i]));
        }

        sb.Append('\n');
    }

    /// <summary>
    /// Invariant formatting, null as a placeholder, and no cell may carry the separators — a
    /// display name with a tab in it would otherwise silently shift every field after it.
    /// </summary>
    static string Cell(object? value) => value switch
    {
        null => "-",
        string s => Escape(s),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        ulong u => u.ToString(CultureInfo.InvariantCulture),
        ElementTypeId e => e.ToString(),
        Enum e => e.ToString(),
        _ => Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? "-")
    };

    static string Escape(string s) =>
        s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
}
