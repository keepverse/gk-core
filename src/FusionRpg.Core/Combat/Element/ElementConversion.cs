using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Element;

/// <summary>
/// The executor for `element.convert` (passive-tree `element-conversion`, D56,
/// spec-element-conversion.md §2c). Redistributes weight WITHIN an already-elemental
/// <see cref="ElementPayload"/> — never fabricates a component on a `null` payload (§2b: a hit with no
/// payload has nothing for this kind to read a share from, and there is no "Physical" element to assign
/// the untouched remainder to), and never leaves a zero-weight component behind (§2c: a fully-converted
/// source is REMOVED, since <see cref="ElementPayload.Validate"/> rejects `Weight &lt;= 0`).
///
/// <para><b>Composition order.</b> Applied one atom at a time, in `nodeKey` ordinal order (the caller's
/// job — this class is a single-atom pure function, never a batch). Because each call reads the source
/// component's weight from the PAYLOAD IT IS GIVEN, not from any original/authored value, a second
/// conversion naming the same `fromElement` after the first has already zeroed or reduced it correctly
/// converts only what remains — no caller-side bookkeeping needed to enforce the "never exceeds 1000‰
/// total" rule (§2c, §3): the invariant falls out of always reading the current state.</para>
/// </summary>
public static class ElementConversion
{
    /// <summary>
    /// One conversion atom against one payload. `null` in, `null` out (a no-op, never an error — §2b,
    /// §3). If <paramref name="fromElement"/> is omitted, the single largest remaining component is the
    /// source (§2c); if that named/inferred source does not exist in <paramref name="payload"/> at all,
    /// this call is a no-op for it (never fabricated, never negative).
    /// </summary>
    public static ElementPayload? Apply(
        ElementPayload? payload, ElementTypeId? fromElement, ElementTypeId toElement, long shareMilli)
    {
        if (shareMilli is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(shareMilli), shareMilli,
                "shareMilli must be 1..1000 (spec-element-conversion.md §2b's own params table)");
        if (payload is null)
            return null; // no-op: a hit with no ElementPayload has no component to convert from (§2b)

        var components = payload.Components;
        var source = fromElement is { } named
            ? components.FirstOrDefault(c => c.Element == named)
            : components.OrderByDescending(c => c.Weight).FirstOrDefault();

        if (source is null)
            return payload; // named/inferred source not present -- no-op for this atom (§2c)

        // docs/architecture/numeric-types.md rule 4: divide once, last. shareMilli=1000 -> multiplier EXACTLY 1.0 (IEEE754
        // identity), so a full conversion leaves remainingSourceWeight at EXACTLY 0.0, never a
        // near-zero float residue that would slip past the "removed, not retained at zero" rule below.
        var shareMultiplier = shareMilli / 1000.0;
        var moved = source.Weight * shareMultiplier;
        var remainingSourceWeight = source.Weight - moved;

        var result = new List<ElementPayloadComponent>(components.Count + 1);
        var targetFound = false;
        foreach (var component in components)
        {
            if (component.Element == source.Element)
            {
                // §2c: a component driven to exactly zero is REMOVED, never kept at weight 0 --
                // ElementPayload.Validate rejects Weight <= 0.
                if (remainingSourceWeight > 0)
                    result.Add(component with { Weight = remainingSourceWeight });
                continue;
            }
            if (component.Element == toElement)
            {
                result.Add(component with { Weight = component.Weight + moved });
                targetFound = true;
                continue;
            }
            result.Add(component);
        }
        if (!targetFound)
            result.Add(new ElementPayloadComponent(toElement, moved));

        return ElementPayload.From(result);
    }
}
