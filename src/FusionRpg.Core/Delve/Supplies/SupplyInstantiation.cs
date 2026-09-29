using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Supplies;

/// <summary>The two reserved stream shapes §1/§9 name, filed on `delve-graph-roll`'s own reserved
/// list (spec-delve-graph-roll.md:130-132) — the one owner of both formats.</summary>
public static class SupplyStreams
{
    /// <summary>A supply entering a pack via a room drop or a merchant sale — `n` is the supply's own
    /// ordinal in the room's floor list.</summary>
    public static string Drop(int row, int col, int n) => $"dungeon:supply:{row}:{col}:{n}";

    /// <summary>A supply provisioned at entry (`Θ_entrance`), never a room.</summary>
    public static string Entry(int n) => $"dungeon:supply:entry:{n}";
}

/// <summary>
/// D3.25 (spec-supplies-and-objects.md §1) — a concrete supply is `Instantiator.TryInstantiate`
/// called ONCE, at `Θ_room`, when the supply enters a pack. Never a second roll implementation — this
/// file is a thin, named wrapper over the shared SDK, the same shape as
/// <see cref="FusionRpg.Core.Delve.Loot.DelveLoot.InstantiateBossFirstClearGrant"/> (D3.15).
/// </summary>
public static class SupplyInstantiation
{
    /// <summary><paramref name="streamName"/> is the caller's own <see cref="SupplyStreams.Drop"/> or
    /// <see cref="SupplyStreams.Entry"/> call — this method owns the roll, not the stream naming, so a
    /// supply's drop stream and its entry stream can never accidentally collide on a shared format.</summary>
    public static AtomRejection Concrete(
        ContainerRow container,
        Func<string, AtomRow?> lookupAtom,
        Func<string, AffixRow?> lookupAffix,
        ulong delveSeed,
        string streamName,
        int thetaRoom,
        PowerTuning tuning,
        long catalogRevision,
        out InstanceRow? instance,
        InstanceOrigin origin = InstanceOrigin.Drop)
    {
        if (container is null) throw new ArgumentNullException(nameof(container));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (string.IsNullOrWhiteSpace(streamName)) throw new ArgumentException("streamName required", nameof(streamName));

        var rollSeed = unchecked((long)SeededRng.DeriveStream(delveSeed, streamName).NextULong());
        return Instantiator.TryInstantiate(
            container, lookupAtom, lookupAffix, rollSeed, thetaRoom, tuning, out instance, origin, catalogRevision);
    }
}
