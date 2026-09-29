using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Actions.Unlock;

/// <summary>
/// combat-ai `lawn-cost-authority` A (CAI4.4, spec-lawn-cost-authority.md): the ONE rung derivation.
/// Extracted verbatim from `BattleRunState.EffectiveRungOf`'s private body (A23,
/// spec-cost-scaling-holder-rung.md §2) so the lawn's cost ledger and the battle's can share it instead
/// of the lawn carrying a `rungOf: (_, _) => 1` constant.
///
/// <para><b>Why it takes a held-row seam instead of an `IBattleView`.</b> The one thing in the original
/// body that belongs to the caller is `HeldActionOf(actorKey, actionId)` — "the held row the actor
/// actually swung, never a catalog lookup" — so it arrives as
/// <paramref name="heldActionOf"/>. Everything else is data the caller already has.</para>
///
/// <para><b><paramref name="floorWhenUnknown"/> is the whole point of the extraction.</b> Battle passes
/// 0, which is exactly today's `?? 0` tail and therefore byte-identical; the lawn passes 1, because
/// <c>CostLedger</c> throws on a rung with no table row and the lawn's default loadout can name an id
/// the battle catalog has never heard of. A floor of 0 there is not a smaller price, it is a throw
/// (`RungPolicy.Table.TryResolve`), which is what the row's planted-violation test pins.</para>
///
/// <para><b>Resolution order, unchanged and load-bearing.</b> (1) the actor's own
/// <see cref="UnlockState.Held"/> list, when both the state and the tuning are supplied, checked in
/// <c>Held</c> order and resolved through <see cref="UnlockLadder.EffectiveRung"/> with the swung row's
/// own <c>RungBand</c>; (2) otherwise the SWUNG row's authored <see cref="CompiledAction.Rung"/>; (3)
/// otherwise the catalog row's; (4) otherwise <paramref name="floorWhenUnknown"/>. Step 2 before step 3
/// is AE1.2's fix: a held row absent from the catalog — siege's <c>AdditionalHeldActions</c>, a lent
/// garrison action — resolves its real rung instead of the catalog's or 0.</para>
/// </summary>
public static class EffectiveRungResolver
{
    /// <summary>The rung this actor's use of this action prices at.</summary>
    /// <param name="actionCatalog">The battle's compiled catalog, or null for a catalog-less battle.</param>
    /// <param name="unlockStateFor">The actor's unlock state, or null when the caller supplies none —
    /// the exact byte-identical-to-today default, since a null state skips step 1 entirely.</param>
    /// <param name="unlockTuning">The published unlock tuning, or null for the same reason.</param>
    /// <param name="heldActionOf">The SWUNG row lookup: the caller's own "what does this actor actually
    /// hold" read, never a catalog lookup.</param>
    /// <param name="floorWhenUnknown">What an id resolved by none of the four steps prices at. 0 is
    /// battle's own tail; 1 is the lawn's, so an uncatalogued id is affordable instead of a throw.</param>
    public static int Resolve(
        string actorKey,
        string actionId,
        ActionCatalog? actionCatalog,
        Func<string, UnlockState>? unlockStateFor,
        UnlockTuning? unlockTuning,
        Func<string, string, CompiledAction?> heldActionOf,
        int floorWhenUnknown = 0)
    {
        if (actorKey is null) throw new ArgumentNullException(nameof(actorKey));
        if (actionId is null) throw new ArgumentNullException(nameof(actionId));
        if (heldActionOf is null) throw new ArgumentNullException(nameof(heldActionOf));

        var state = unlockStateFor?.Invoke(actorKey);
        if (state is not null && unlockTuning is not null)
        {
            foreach (var held in state.Held)
            {
                if (held.UnlockId == actionId)
                {
                    var band = heldActionOf(actorKey, actionId)?.RungBand;
                    return UnlockLadder.EffectiveRung(held.EarnCountAtAcceptance, unlockTuning, band).Value;
                }
            }
        }

        return heldActionOf(actorKey, actionId)?.Rung
               ?? actionCatalog?.Get(actionId)?.Rung
               ?? floorWhenUnknown;
    }
}
