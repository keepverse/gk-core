namespace FusionRpg.Core.Items;

/// <summary>
/// I11 §2.7's cycle rule, one line: the gate reads attributes composed from every source EXCEPT
/// containers of the four equippable kinds — <c>item</c>, <c>gem</c>, <c>set</c>, <c>charm</c>. Call
/// it the *unassisted* value. Without it, two items each granting what the other requires make
/// legality order-dependent and partial failure undefined.
///
/// <para><b>Named by string, not by <see cref="FusionRpg.Core.Effects.Atoms.ContainerKind"/>.</b>
/// Two of the four kinds this rule excludes (<c>gem</c>, <c>set</c>, <c>charm</c>) do not exist as
/// enum values yet — they are X7's, still pending (D27's container-kind mint). Naming them by string
/// means this filter is already correct for the day they land, rather than needing a second edit when
/// the enum grows.</para>
///
/// <para><b>Honest scope, stated once (item-ideal.md, `equip-assign`):</b> nothing equippable can move
/// an attribute input today — there is no attribute composer wired to this module at all. This filter
/// exists anyway, structurally, so the first attribute clause to arrive inherits a composer that
/// already excludes the four kinds, rather than one that never excluded anything and would need a
/// retrofit under the exact conditions D19/I11 warn about.</para>
/// </summary>
public static class UnassistedAttributes
{
    public static readonly IReadOnlyCollection<string> EquippableKinds = new[] { "item", "gem", "set", "charm" };

    /// <summary>Every source NOT drawn from an equippable container kind.</summary>
    public static IEnumerable<(string ContainerKind, T Value)> Filter<T>(
        IEnumerable<(string ContainerKind, T Value)> sources) =>
        sources.Where(s => !EquippableKinds.Contains(s.ContainerKind, StringComparer.Ordinal));
}
