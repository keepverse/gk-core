namespace FusionRpg.Core.PassiveTree.State;

public readonly record struct TreeRespecPrice(long Amount);

/// <summary>
/// D18's tree respec price (spec-tree-state.md §5, §5.1, task C10) — the exact structural sibling of
/// `RespecPolicy.PriceOf` (`gk-core/src/FusionRpg.Core/Stats/Aptitudes/RespecPolicy.cs:36-48`): same linear-
/// escalation-on-a-count shape, same `checked`/`long` discipline, divided by 1000 last, exactly once.
/// Adopts the shape with the tree's OWN persisted counter and OWN tunable amount — never the species
/// respec counter (C10's stated default for the open scoping question spec-tree-state.md §5.1 leaves
/// to the owner).
///
/// <para><b>Always available, always priced, never refused</b> — there is no "cannot respec" return
/// here on purpose, exactly like the policy this mirrors. Insufficient balance is a caller-level
/// refusal (the ONE named reason `RpgStore`'s respec transaction may decline for), never a refusal
/// this policy itself issues.</para>
/// </summary>
public static class TreeRespecPolicy
{
    /// <summary>`price(count) = basePrice + basePrice × count × escalationPermille / 1000`.</summary>
    public static TreeRespecPrice PriceOf(PassiveTreeTuning tuning, long count)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "respec count cannot be negative");

        checked
        {
            var basePrice = tuning.Respec.BasePrice;
            var amount = basePrice + basePrice * count * tuning.Respec.EscalationPermille / 1000;
            return new TreeRespecPrice(amount);
        }
    }
}
