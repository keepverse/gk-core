using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Delve;

/// <summary>
/// Projects <c>dungeon-registries</c>' <see cref="RoomKindCatalog"/> into the
/// <see cref="SectorTypeDef"/> shape <see cref="WorldValidation"/> rules 1 and 6 need under the
/// delve profile — one registry, two views (decisions.md:133), never a second owner of room-kind
/// RULES. <see cref="RoomKindCatalog"/> keeps the rules (climate neutrality, secret eligibility,
/// boss row, adjacency bans, joined weights); this projects only the fields
/// <see cref="SectorTypeDef"/> itself needs. Not served on <c>/api/world/catalog</c>
/// (spec-delve-scope.md §3) — the frozen map FE never sees a room kind.
/// </summary>
public sealed class RoomTypeCatalog
{
    readonly IReadOnlyList<SectorTypeDef> _all;
    readonly Dictionary<string, SectorTypeDef> _byId;

    public RoomTypeCatalog(IReadOnlyList<RoomKindDef> roomKinds)
    {
        var rows = roomKinds.Select(k => new SectorTypeDef
        {
            TypeId = k.RoomKindId,
            Name = k.RoomKindId, // no display name on a room kind registry row — the archetype anchor's own `name` is what a player sees
            BaseDangerBand = 0,  // depth is the sector's own DangerBand, composed by difficulty-ladder — never a catalog constant (spec §3)
            CanHostSeat = false, // a room never hosts a Seat/base
            Flags = k.BossRowAllowed ? SectorTypeFlags.Boss : SectorTypeFlags.None,
            AllowedSlotTypes = Array.Empty<string>(), // rooms carry zero slots in v1 (spec §3)
        }).ToList();

        _all = SectorTypeCatalog.Validate(rows); // the EXISTING validator, unmodified — same shape, same rules
        _byId = _all.ToDictionary(r => r.TypeId, StringComparer.Ordinal);
    }

    public IReadOnlyList<SectorTypeDef> All => _all;

    public bool IsKnown(string? typeId) => typeId != null && _byId.ContainsKey(typeId);

    public SectorTypeDef Get(string typeId) =>
        _byId.TryGetValue(typeId, out var def) ? def : throw new ArgumentException($"Unknown room kind id '{typeId}'.");
}
