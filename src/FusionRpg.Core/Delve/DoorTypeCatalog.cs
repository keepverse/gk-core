using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Delve;

/// <summary>
/// Projects <c>dungeon-registries</c>' <see cref="DoorKindCatalog"/> into the
/// <see cref="LaneTypeDef"/> shape <see cref="WorldValidation"/> rule 1 needs under the delve
/// profile — the door registry's own flags (<c>gated</c>, <c>oneWay</c>, <c>hidden</c>) already
/// mirror <c>LaneTypeDef</c>'s (spec-dungeon-registries.md §"door-kinds.v1.json": "the same flags
/// LaneTypeDef carries"). <see cref="LaneTypeDef.CostMultiplierMilli"/> is 1000 on every row —
/// nothing marches in a delve, so the cost is inert but must be positive to pass the existing
/// validator (<c>LaneTypeCatalog.cs</c>: "a lane type must cost something to march").
/// <see cref="LaneTypeDef"/> has no field for "hidden" (a secret door reads through the room's own
/// <c>secretEligible</c>, a level up) — that bit of the registry row is not projected here because
/// nothing at this layer reads it.
/// </summary>
public sealed class DoorTypeCatalog
{
    const long InertCostMultiplierMilli = 1000;

    readonly IReadOnlyList<LaneTypeDef> _all;
    readonly Dictionary<string, LaneTypeDef> _byId;

    public DoorTypeCatalog(IReadOnlyList<DoorKindDef> doorKinds)
    {
        var rows = doorKinds.Select(k => new LaneTypeDef
        {
            LaneTypeId = k.DoorKindId,
            Name = k.DoorKindId,
            CostMultiplierMilli = (int)InertCostMultiplierMilli,
            CarriesSupply = false, // LegionSupply never runs on a delve world (spec §5) — inert either way
            CarriesPressure = false,
            OneWay = k.OneWay,
            Gated = k.Gated,
            Ley = false,
        }).ToList();

        _all = LaneTypeCatalog.Validate(rows); // the EXISTING validator, unmodified
        _byId = _all.ToDictionary(r => r.LaneTypeId, StringComparer.Ordinal);
    }

    public IReadOnlyList<LaneTypeDef> All => _all;

    public bool IsKnown(string? typeId) => typeId != null && _byId.ContainsKey(typeId);

    public LaneTypeDef Get(string typeId) =>
        _byId.TryGetValue(typeId, out var def) ? def : throw new ArgumentException($"Unknown door kind id '{typeId}'.");
}
