using FusionRpg.Core.Dungeon.Registry;

namespace FusionRpg.Core.Delve.Objects;

/// <summary>`objects.breakMode`'s own closed four (Tunables) — a plain string on
/// <c>DungeonTuning.ObjectsTuning</c>, not a C# enum, so this is names for the same four literals,
/// never a private re-encoding.</summary>
public static class ObjectsBreakMode
{
    public const string None = "none";
    public const string Stamina = "stamina";
    public const string Structure = "structure";
    public const string Either = "either";
}

/// <summary>One resolved verb attempt — a refusal by name, or which owner it hands off to (§5's own
/// table: "always someone else's number"). Flat with a `Kind` tag rather than a real union (this
/// codebase's own established shape for a small closed outcome set, e.g. `PackSettlementWrite`).</summary>
public sealed record VerbOutcome(
    string Kind, bool Ok, string? Reason,
    string? LaneId, string? ConsumeStock, string? ActionId, string? SectorId, string? StructureHpBand, string? DeckSourceRef)
{
    public static VerbOutcome Refuse(string reason) => new("refuse", false, reason, null, null, null, null, null, null);
    public static VerbOutcome OpenGate(string laneId, string consumeStock) => new("open-gate", true, null, laneId, consumeStock, null, null, null, null);
    public static VerbOutcome PayThenOpen(string actionId, string laneId) => new("pay-then-open", true, null, laneId, null, actionId, null, null, null);
    public static VerbOutcome StructureFight(string sectorId, string structureHpBand) => new("structure-fight", true, null, null, null, null, sectorId, structureHpBand, null);
    public static VerbOutcome DeckDraw(string sourceRef) => new("deck-draw", true, null, null, null, null, null, null, sourceRef);
}

/// <summary>
/// D3.28 (spec-supplies-and-objects.md §5) — resolves one verb attempt against one `RoomObject`.
/// Every check is a named refusal before any hand-off; the hand-off itself computes no number ("always
/// someone else's"). Pure: <paramref name="requirementHolds"/>/<paramref name="alreadySpent"/> are
/// caller-supplied facts (the caller already evaluated `obj.Requirement` against its own party state —
/// this file does not know how to build a `FactReader`, matching this whole program's "read model
/// owned elsewhere" shape), and <paramref name="breakMode"/>/<paramref name="structureHpBand"/> are
/// the two tuning reads this table needs, not a whole `DungeonTuning` this function has no other use
/// for.
///
/// <para><b>Follows §5's own detailed verb table over the "Code style" section's own illustrative
/// switch</b> where the two disagree — the code-style sample only shows `loot`/`disarm`/`pray` gated
/// on `ObjectKind.Curio`, but §5's own table (read in full, not skimmed) gives `open` and `disarm`
/// TWO kinds each (`open`: obstacle via `LaneGate`, curio via an event-deck draw with the `key`
/// override tag; `disarm`: curio OR a trapped obstacle, both via an event-deck draw), and `pray` on
/// `building`, not `curio` — the sample's own opening line calls itself a style guide ("delegates in,
/// record out, first broken rule wins"), not an exhaustive switch, so the detailed table wins.
/// `destroy` still carries no `ObjectKind` guard (§5's own row lists both obstacle and structure); a
/// `none`-break room's own `RoomObjectBuilder.For` call (D3.27) never puts "destroy" in that object's
/// `Verbs` at all, so "verb not offered" already covers `breakMode == none` without this function
/// needing to know about it.</para>
///
/// <para><b>Honest gap, named:</b> §7's own text distinguishes a `wild` room's `altar` (pray → wild-
/// room's pull, a real R5 mechanism) from its `cage` (open → refused until wave 4) — but `wild(altar/
/// cage)` is ONE room kind in §4's own source table, and `RoomObject.SourceRef` for a building carries
/// only the room kind string, with nothing yet distinguishing which of the two a given `wild` room
/// actually is. `pray` on a `building` therefore resolves to the SAME `DeckDraw` hand-off as a shrine
/// here — correct for shrine, but not yet the altar-specific "wild-room's own pull" hand-off (`spec-
/// wild-room.md`, wave 4, "an interface here" per this module's own Objective). Building that
/// distinction needs `RoomObjectBuilder`'s own source shape extended first, with a real wild-room
/// caller to verify the split against — not guessed here.</para>
/// </summary>
public static class VerbResolver
{
    public static VerbOutcome Resolve(
        RoomObject obj, string verb, bool requirementHolds, bool alreadySpent, string breakMode, string structureHpBand)
    {
        if (obj is null) throw new ArgumentNullException(nameof(obj));
        if (string.IsNullOrWhiteSpace(verb)) throw new ArgumentException("verb required", nameof(verb));
        if (!InteractionVerbCatalog.IsKnown(verb))
            throw new InvalidOperationException($"verb '{verb}' is not in interaction-verbs.v1.json");

        if (!obj.Verbs.Contains(verb, StringComparer.Ordinal)) return VerbOutcome.Refuse("object.verb-not-offered");
        if (obj.OneShot && alreadySpent) return VerbOutcome.Refuse("object.spent");
        if (obj.Requirement is not null && !requirementHolds) return VerbOutcome.Refuse("object.requirement-unmet");

        return (verb, obj.Kind) switch
        {
            ("open", ObjectKind.Obstacle) => VerbOutcome.OpenGate(obj.SourceRef, $"key.{obj.SourceRef}"),
            ("open", ObjectKind.Curio) => VerbOutcome.DeckDraw(obj.SourceRef),
            ("destroy", ObjectKind.Obstacle or ObjectKind.Structure) when breakMode is ObjectsBreakMode.Stamina or ObjectsBreakMode.Either =>
                VerbOutcome.PayThenOpen("delve.break", obj.SourceRef),
            ("destroy", ObjectKind.Obstacle or ObjectKind.Structure) => VerbOutcome.StructureFight(obj.SectorId, structureHpBand),
            ("disarm", ObjectKind.Curio or ObjectKind.Obstacle) => VerbOutcome.DeckDraw(obj.SourceRef),
            ("loot", ObjectKind.Curio) => VerbOutcome.DeckDraw(obj.SourceRef),
            ("pray", ObjectKind.Building) => VerbOutcome.DeckDraw(obj.SourceRef), // shrine's own resolution; altar's own split is the honest gap above
            ("garrison", ObjectKind.Structure) => VerbOutcome.Refuse("object.garrison-unbuilt"), // §5: "refused in v1 (no structure rows)"
            _ => VerbOutcome.Refuse("object.verb-unresolved"),
        };
    }
}
