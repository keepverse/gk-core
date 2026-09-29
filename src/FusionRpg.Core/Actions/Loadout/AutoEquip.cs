using FusionRpg.Core.Actions.Rungs;

namespace FusionRpg.Core.Actions.Loadout;

/// <summary>One held, skill-kind action eligible for auto-equip, and the rung it currently sits at
/// (spec-loadout.md §1: rung is derived, never stored — the caller resolves it before calling in,
/// the same way T19's <c>UnlockLadder.Rung</c> is always recomputed rather than read from a column).
/// </summary>
public readonly record struct AutoEquipCandidate(string ActionId, int Rung);

/// <summary>
/// T22 (action-todo.md, spec-loadout.md §3): auto-equip. "Every actor with no loadout row auto-
/// equips" — a Zomboss pattern, a generated creature, or any AI-driven actor must never fight with
/// three basics just because nobody chose for it.
///
/// <para><b>Power scale is the shipped rung ladder, named as a stand-in</b> (spec §3: "the rung as a
/// proxy"). `E9`'s real `PowerVector`→`PowerScalar` pipeline prices CONTENT (items/atoms), not
/// actions, and wiring it here would be new, unauthorized scope; `RungTable.QPowerMilli` is already
/// a real, shipped power-shaped value per rung (T1–T5) and is exactly what the spec sanctions using
/// instead. <b>The score reaches nothing but the ranking</b> — <see cref="Select"/>'s return type is
/// a bare id list with no numeric field anywhere in it, which is the architecture guarantee made
/// structural rather than merely promised (PS-4: a selection is not a magnitude).</para>
/// </summary>
public static class AutoEquip
{
    /// <summary>
    /// <paramref name="candidates"/> must already be filtered to the actor's HELD, SKILL-kind
    /// actions (spec §3: "candidates = held actions, skill kind only") — this method does not
    /// re-check kind or holding, the same division of responsibility <see cref="LoadoutSet"/> keeps
    /// between validation and the state it validates against. Ranks by power descending, ties break
    /// on <c>action_id</c> ordinal, takes at most <see cref="LoadoutSet.EffectiveMaxSize"/>
    /// (D4.25 — <paramref name="loadoutSlotsChannel"/> is the actor's composed
    /// <see cref="FusionRpg.Core.Stats.Derived.DerivedStatChannels.LoadoutSlots"/> value, rounded to a
    /// whole count at the caller's Data-layer boundary — see <see cref="LoadoutSet.EffectiveMaxSize"/>
    /// for why this is `long`, not the channel's own composed `double`).</para>
    ///
    /// <para><b>W14 (battle-derived-wire T18): no production site passes a channel value yet</b> — the
    /// composed `loadout.slots` is reserved at 0 until the equip/atom layer that grants it ships.</para>
    /// </summary>
    public static IReadOnlyList<string> Select(
        IReadOnlyList<AutoEquipCandidate> candidates, RungTable rungTable, long loadoutSlotsChannel = 0)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (rungTable is null) throw new ArgumentNullException(nameof(rungTable));

        var scored = new (string ActionId, long PowerMilli)[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            if (!rungTable.TryResolve(c.Rung, out var multipliers))
                throw new ArgumentOutOfRangeException(nameof(candidates), c.Rung, $"no rung row for action '{c.ActionId}'");
            scored[i] = (c.ActionId, multipliers.QPowerMilli);
        }

        // Stable total order: power descending, then action_id ordinal — never insertion order, so
        // a shuffled input produces byte-identical output (spec testing strategy).
        Array.Sort(scored, (a, b) =>
        {
            var byPower = b.PowerMilli.CompareTo(a.PowerMilli);
            return byPower != 0 ? byPower : string.CompareOrdinal(a.ActionId, b.ActionId);
        });

        var take = Math.Min(LoadoutSet.EffectiveMaxSize(loadoutSlotsChannel), scored.Length);
        var result = new string[take];
        for (var i = 0; i < take; i++) result[i] = scored[i].ActionId;
        return result;
    }
}
