using FusionRpg.Core.Items.Consumables;

namespace FusionRpg.Core.Delve.Supplies;

public sealed class SupplyClassMapRejection : Exception
{
    public SupplyClassMapRejection(string message) : base(message) { }
}

/// <summary>
/// D3.25 (spec-supplies-and-objects.md §1) — the class → executor mapping of §11.5, "the ONLY mapping,"
/// read by the import validator. Never a private second copy of this table.
/// </summary>
public static class SupplyClassMap
{
    /// <summary>§1, verbatim per class: `restore` → `resource.delta`; `ward` → `shield.grant`;
    /// `revive` → `resource.delta` (hp, gated on Downed — the gate itself is <see cref="SupplyUse"/>'s
    /// job, not this table's); `utility` → `status.clear` **or no atoms at all** (a key or bait is an
    /// override tag with nothing to fire — the empty case is legal, checked by the caller having
    /// nothing to iterate, not by this list); `draught` → `stat.derived`; `board` has no legal executor
    /// here at all — refused outright by <see cref="Validate"/>, never reaching this table.</summary>
    public static IReadOnlyList<string> AllowedAtomKinds(ConsumableClass classId) => classId switch
    {
        ConsumableClass.Restore => new[] { "resource.delta" },
        ConsumableClass.Ward => new[] { "shield.grant" },
        ConsumableClass.Revive => new[] { "resource.delta" },
        ConsumableClass.Utility => new[] { "status.clear" },
        ConsumableClass.Draught => new[] { "stat.derived" },
        ConsumableClass.Board => Array.Empty<string>(),
        _ => throw new ArgumentOutOfRangeException(nameof(classId)),
    };

    /// <summary>
    /// Import-time validation (§8): a `board` class is refused outright (no lawn in a delve); an
    /// `OnActivate` atom kind of `status.clear` is refused REGARDLESS of class — §3's own "wiring gap,
    /// named": `status.clear` is on `AtomTriggers.Events` only today, not `OnActivate`, so no antidote
    /// can legally fire one until `effect-atom-map.md`'s own row lands. Every other class's atoms must
    /// each be one of <see cref="AllowedAtomKinds"/>. A `utility` row with zero atoms passes trivially
    /// (nothing to check) — the legal "key or bait" shape; one WITH an atom is always refused today,
    /// since its only allowed kind (`status.clear`) is the one kind blocked above.
    /// </summary>
    public static void Validate(ConsumableClass classId, IReadOnlyList<string> onActivateAtomKinds, string containerId)
    {
        if (onActivateAtomKinds is null) throw new ArgumentNullException(nameof(onActivateAtomKinds));
        if (string.IsNullOrWhiteSpace(containerId)) throw new ArgumentException("containerId required", nameof(containerId));

        if (classId == ConsumableClass.Board)
            throw new SupplyClassMapRejection($"supply.board-in-delve: '{containerId}' is class 'board', refused in any delve context (no lawn)");

        if (onActivateAtomKinds.Contains("status.clear", StringComparer.Ordinal))
            throw new SupplyClassMapRejection(
                $"consumable.trigger-not-allowed: '{containerId}' fires 'status.clear' on OnActivate, " +
                "not an allowed trigger until effect-atom-map.md's own row lands");

        var allowed = AllowedAtomKinds(classId);
        foreach (var kind in onActivateAtomKinds)
            if (!allowed.Contains(kind, StringComparer.Ordinal))
                throw new SupplyClassMapRejection(
                    $"supply.wrong-executor: '{containerId}' (class '{ConsumableClasses.Wire(classId)}') fires " +
                    $"'{kind}', expected one of [{string.Join(",", allowed)}]");
    }
}
