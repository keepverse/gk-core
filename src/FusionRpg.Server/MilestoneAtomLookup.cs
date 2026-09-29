using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Server;

/// <summary>
/// The production <c>milestoneAtomFor</c> lookup <see cref="ItemWorkbench"/> takes (species-gear-chain
/// T13): the milestone atom row for <c>(family, tier)</c> — its id plus the amount range a successful
/// enhance rolls — read off the atom rows the host already holds (<c>RpgStore.GetAtom</c>, the same rows
/// the delve mint reads), so no corpus is read twice.
///
/// <para>⚠ <b>The two absences the contract distinguishes are the whole point of this class, and they
/// are opposite on purpose.</b> A family the milestone corpus never named <b>throws</b>
/// <see cref="MilestoneUnknownFamily"/> — that is a content gap and it must be loud, because a silently
/// absent milestone is a player-visible nothing. A <b>known</b> family the generator never expanded
/// (aegis, hardy, keen, evasion, bloom — no reference base for their channels) returns <c>null</c>, and
/// the attempt succeeds with no append: failing an enhance for a generator limitation would punish the
/// player for an authoring gap.</para>
///
/// <para>The atom id is <c>{family}.t{tier}</c>, which is not a convention invented here — it is
/// <see cref="AtomRow.AtomId"/>'s own documented derivation
/// (<c>{family_id}[.{variant}].t{tier}</c>, and a milestone family carries no variant). The amount range
/// comes from the row's own <see cref="AtomRow.ParamsJson"/>, which is the authored
/// <c>params.amount</c> the generator emitted, so no magnitude is invented on this side.</para>
/// </summary>
public static class MilestoneAtomLookup
{
    /// <param name="lookupAtom">The host's atom reader — <c>RpgStore.GetAtom</c> in production.</param>
    /// <param name="knownFamilies">
    /// Every <c>runtimeFamily</c> the milestone corpus names — read by
    /// <c>MilestoneFamilyFile.Read</c> over <c>gk-data/packs/fusion/data/seed/items/enhancement-milestones/milestones.json</c>.
    /// This is what lets the lookup tell "never named" (loud) from "named but not expanded" (quiet).
    /// </param>
    public static Func<string, int, MilestoneAtom?> Load(
        Func<string, AtomRow?> lookupAtom, IReadOnlySet<string> knownFamilies)
    {
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));
        if (knownFamilies is null) throw new ArgumentNullException(nameof(knownFamilies));

        return (family, tier) =>
        {
            if (family is not { Length: > 0 })
                throw new MilestoneUnknownFamily("milestone family is empty — the track names no family");
            if (!knownFamilies.Contains(family))
                throw new MilestoneUnknownFamily(
                    $"milestone family '{family}' is not named by the milestone corpus " +
                    "(data/seed/items/enhancement-milestones/milestones.json) — a content gap, " +
                    "never a fabricated row");

            var atom = lookupAtom($"{family}.t{tier}");
            if (atom is null) return null;   // named but unexpanded: no append, never a refusal

            return AmountsOf(atom.ParamsJson) is { } range
                ? new MilestoneAtom(atom.AtomId, range.Min, range.Max)
                : null;                      // no authored amount range: nothing honest to roll
        };
    }

    /// <summary>The authored <c>params.amount.{min,max}</c>, or <c>null</c> when the row carries neither.
    /// <c>long</c>, because an amount is a magnitude and the ladder is quadratic.</summary>
    static (long Min, long Max)? AmountsOf(string? paramsJson)
    {
        if (string.IsNullOrWhiteSpace(paramsJson)) return null;

        using var doc = JsonDocument.Parse(paramsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("amount", out var amount) ||
            amount.ValueKind != JsonValueKind.Object ||
            !amount.TryGetProperty("min", out var minEl) || minEl.ValueKind != JsonValueKind.Number ||
            !amount.TryGetProperty("max", out var maxEl) || maxEl.ValueKind != JsonValueKind.Number)
            return null;

        var min = minEl.GetInt64();
        var max = maxEl.GetInt64();
        return max < min ? null : (min, max);
    }
}
