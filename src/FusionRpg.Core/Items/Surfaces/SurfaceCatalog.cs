namespace FusionRpg.Core.Items.Surfaces;

/// <summary>
/// GG-17's four designed states, closed. There is no fifth, and there is deliberately no "normal":
/// the populated case is <see cref="Ready"/> and it is named so that a surface cannot be written with
/// three states and an implied fourth.
/// </summary>
public enum SurfaceState
{
    /// <summary>The unlock condition has not fired. GG-44: never invisible, never present-but-dead —
    /// it renders and it SAYS what unlocks it (<see cref="SurfaceCatalog.UnlockKeyFor"/>).</summary>
    Locked = 0,

    Loading,

    /// <summary>Unlocked, no error, and nothing to show. A designed state, not a blank — the shipped
    /// <c>storage</c> tab's honest <c>EmptyState</c> is the pattern.</summary>
    Empty,

    Error,

    Ready,
}

/// <summary>The six surfaces spec-item-surfaces.md's Objective table declares. Closed.</summary>
public enum ItemSurface
{
    Armoury = 0,
    EquipScreen,
    ItemCard,
    Comparison,
    SocketBench,
    Compendium,
}

/// <summary>
/// What one surface reports about itself right now — the shape the Relics panel's body renders and
/// the shape <c>ItemSurfaceEndpoints</c> serves.
/// </summary>
/// <param name="UnlockKey">The state key that unlocks it, from
/// <c>gk-core/data/tuning/item-surfaces.v1.json</c>'s <c>surfaceUnlocks</c>. Always present, including when
/// the surface is already unlocked — a UI that only learns the key at the moment it must render
/// "locked" is a UI that renders an empty box the first time.</param>
public readonly record struct SurfaceStatus(ItemSurface Surface, SurfaceState State, string UnlockKey);

/// <summary>
/// The six surfaces × four designed states, as a pure resolver rather than as six components each
/// remembering to handle the same four cases.
///
/// <para>⛔ <b>This module renders nothing and computes no magnitude</b> (spec-item-surfaces.md's own
/// first rule). What it owns is which STATE each surface is in, which is a question about the
/// player's own state and therefore answerable in Core, deterministically, with a test.</para>
///
/// <para><b>GG-44, mechanically.</b> A surface is <see cref="SurfaceState.Locked"/> until its unlock
/// key is in the player's satisfied set, and <see cref="UnlockKeyFor"/> is total over the six, so a
/// locked surface can always say what unlocks it. There is no arm that returns "locked" with no key
/// — that is the failure GG-44 names, and it is unreachable here by construction.</para>
/// </summary>
public static class SurfaceCatalog
{
    /// <summary>The tuning key for each surface. Ordinal-stable and used by
    /// <see cref="ItemSurfaceTuning"/>'s own completeness check, so a seventh surface cannot be added
    /// without also declaring what unlocks it.</summary>
    public static string Id(ItemSurface surface) => surface switch
    {
        ItemSurface.Armoury => "armoury",
        ItemSurface.EquipScreen => "equipScreen",
        ItemSurface.ItemCard => "itemCard",
        ItemSurface.Comparison => "comparison",
        ItemSurface.SocketBench => "socketBench",
        ItemSurface.Compendium => "compendium",
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, null),
    };

    /// <summary>Every surface id, in declaration order.</summary>
    public static IReadOnlyList<string> Ids { get; } =
        Enum.GetValues(typeof(ItemSurface)).Cast<ItemSurface>().Select(Id).ToList();

    public static IReadOnlyList<ItemSurface> All { get; } =
        Enum.GetValues(typeof(ItemSurface)).Cast<ItemSurface>().ToList();

    /// <summary>The state key that unlocks a surface. Total over the six by construction.</summary>
    public static string UnlockKeyFor(ItemSurface surface, ItemSurfaceTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        return tuning.SurfaceUnlocks[Id(surface)];
    }

    /// <summary>
    /// One surface's state. The precedence is deliberate and is the order a player can act on:
    /// <b>locked before loading before error before empty</b>. A locked surface must not spin — the
    /// player cannot make the spinner finish. An errored surface must not read as empty — "you own
    /// nothing" and "we could not read what you own" are different sentences and a player who is told
    /// the first one stops looking.
    /// </summary>
    public static SurfaceStatus Resolve(
        ItemSurface surface,
        ItemSurfaceTuning tuning,
        IReadOnlySet<string> satisfiedUnlockKeys,
        bool loading,
        bool errored,
        int rowCount)
    {
        if (satisfiedUnlockKeys is null) throw new ArgumentNullException(nameof(satisfiedUnlockKeys));

        var key = UnlockKeyFor(surface, tuning);

        if (!satisfiedUnlockKeys.Contains(key)) return new SurfaceStatus(surface, SurfaceState.Locked, key);
        if (loading) return new SurfaceStatus(surface, SurfaceState.Loading, key);
        if (errored) return new SurfaceStatus(surface, SurfaceState.Error, key);
        if (rowCount <= 0) return new SurfaceStatus(surface, SurfaceState.Empty, key);
        return new SurfaceStatus(surface, SurfaceState.Ready, key);
    }
}
