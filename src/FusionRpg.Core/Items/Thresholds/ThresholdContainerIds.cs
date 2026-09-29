using System.Globalization;
using System.Text.RegularExpressions;

namespace FusionRpg.Core.Items.Thresholds;

/// <summary>
/// The container ids this module's three consumers grant, and the zero pad that makes them sort.
///
/// <para><b>The two-digit pad is load-bearing, not cosmetic.</b> The actor effect list orders by
/// <c>container_id</c> ORDINAL — <c>ListBindings</c> is literally
/// <c>ORDER BY b.priority DESC, i.container_id ASC</c> (`RpgStore.AtomInstances.cs`). Unpadded,
/// <c>set.x-10</c> sorts before <c>set.x-2</c> and a ten-piece set resolves its tiers out of order.
/// Padded, ordinal order equals numeric order and the lower tier resolves first for free.</para>
///
/// <para><b>⛔ Nothing here has a legal <see cref="FusionRpg.Core.Effects.Atoms.ContainerKind"/> yet.</b>
/// D27 rules four new kinds (<c>gem</c> / <c>set</c> / <c>charm</c> / <c>combo</c>); the shipped enum has
/// six values and none of them (`ContainerRow.cs`), and the id regex mirrors the enum
/// (`ContainerValidator.cs`). That is X7, owned by the effect-atom lane — an ask, not an edit from here
/// (the `definitions.md` §1 grammar row is the SSOT the regex mirrors, and it wins over any spec). This
/// type therefore FORMATS and PARSES the ids the evaluator resolves to; binding them as real container
/// rows is a wiring gap that X7 closes, not a defect in this module.</para>
/// </summary>
public static class ThresholdContainerIds
{
    /// <summary>A set id may not end in <c>-NN</c>: it would collide with one of its own tier ids.</summary>
    static readonly Regex SetIdRe = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    static readonly Regex EndsInTierRe = new("-[0-9]{2}$", RegexOptions.Compiled);
    static readonly Regex AxisRe = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary><c>set.{set_id}-{pieces:D2}</c> — ssot-sets.md §4.3.</summary>
    public static string SetTier(string setId, int pieces)
    {
        if (!SetIdRe.IsMatch(setId ?? ""))
            throw new ArgumentException($"set id '{setId}' is not kebab-case", nameof(setId));
        if (EndsInTierRe.IsMatch(setId!))
            throw new ArgumentException(
                $"set id '{setId}' ends in -NN and would collide with one of its own tier ids", nameof(setId));
        if (pieces is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(pieces),
                $"pieces {pieces} does not fit the two-digit pad; a set that large is a content bug, " +
                "and widening the pad silently would reorder every id already on file");

        return $"set.{setId}-{pieces.ToString("D2", CultureInfo.InvariantCulture)}";
    }

    /// <summary><c>set.frame-mix-{ordinal:D2}</c>, ascending in <c>minorityMilli</c> — D27 puts the
    /// frame-mix bonus in the <c>set</c> kind explicitly.</summary>
    public static string FrameMixTier(int ordinal)
    {
        if (ordinal is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(ordinal), $"frame-mix ordinal {ordinal} does not fit the two-digit pad");
        return $"set.frame-mix-{ordinal.ToString("D2", CultureInfo.InvariantCulture)}";
    }

    /// <summary><c>charm.res-{axis}-{count:D2}</c> — ssot-charms.md §4.2's resonance breakpoint table.</summary>
    public static string CharmResonance(string axis, int count)
    {
        if (!AxisRe.IsMatch(axis ?? ""))
            throw new ArgumentException($"charm axis '{axis}' is not kebab-case", nameof(axis));
        if (count is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(count), $"resonance count {count} does not fit the two-digit pad");
        return $"charm.res-{axis}-{count.ToString("D2", CultureInfo.InvariantCulture)}";
    }

    /// <summary>The <c>effect_binding.source</c> tag a set's tiers withdraw as a group under.</summary>
    public static string SetSource(string setId) => $"set:{setId}";

    /// <summary>The <c>effect_binding.source</c> tag one resonance axis withdraws as a group under.</summary>
    public static string CharmResonanceSource(string axis) => $"charm-resonance:{axis}";

    /// <summary>D3's bonus is one group with one source; there is only ever one frame mix per body.</summary>
    public const string FrameMixSource = "frame-mix";

    /// <summary>
    /// ssot-sets.md §4.4 / ssot-charms.md §4.1. Set and frame-mix tiers bind at <b>0</b>, identical to
    /// an item binding — raising it would let a set bonus pre-empt an item proc for no design reason.
    /// Charm bindings sit at <b>-100</b>, so an actor's own gear reads before the account layer.
    /// </summary>
    public const int SetPriority = 0;

    /// <inheritdoc cref="SetPriority"/>
    public const int CharmPriority = -100;
}
