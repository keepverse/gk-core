using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// One side of a transfer. ⛔ D26 again: the ±8 window reads ITEM level, never the player's.
/// </summary>
/// <param name="RoleId">Module 3's stable role id — never a display name.</param>
/// <param name="Frame">`humanoid`, `plant`, or `hybrid`. A hybrid is refused until module 3 settles
/// hybrid role ids (I6 §9 #7 names the dependency by name).</param>
public readonly record struct TransferSide(string RoleId, string Frame, int ItemLevel, int EnhanceLevel);

/// <summary>
/// The decided transfer. <b>The granted delta is the RESULT, never the recipe</b> (D2 clause 4) — a
/// later <c>TransferRatioMilli</c> change rewrites no completed transfer, because what the log holds
/// is "the recipient gained +7", not "the recipient gained 70% of whatever the donor had".
/// </summary>
public readonly record struct TransferOutcome(bool Allowed, int GrantedLevels, int DonorLevelAfter, AtomRejection Refusal);

/// <summary>
/// I6 §7.4's release valve, adopted rather than redesigned (spec-enhance-reroll.md §6a).
///
/// <para><b>Why it is lossy, in I6's own words:</b> "a lossless transfer turns +X into a portable
/// currency, the item becomes a disposable carrier, and the decision disappears." And why it exists:
/// "without it, enhancement punishes finding better loot — you keep the worse item because it is the
/// one you paid for."</para>
///
/// <para>⭐ <b>§4b raises this from a nicety to the module's answer to its own worst number.</b> With
/// N ≈ 0.19 realms of crafting value at v1 depth, an investment locked to one item is one the player
/// abandons at the next content step; transfer is the only mechanism here that lets it follow them.
/// So 700‰ is a load-bearing tunable, not the "pure feel number" I6 §10 Q4 admits it was.</para>
///
/// <para><b>One transaction, two ops.</b> <c>enhance-transfer-out</c> on the donor and
/// <c>enhance-transfer-in</c> on the recipient share one <c>correlation_id</c> and commit together —
/// a half-applied transfer duplicates levels. This class decides; the store commits both rows or
/// neither.</para>
/// </summary>
public static class TransferPolicy
{
    static TransferPolicy() => MutationRules.EnsureRegistered();

    public const string HybridFrame = "hybrid";

    public static TransferOutcome Resolve(TransferSide donor, TransferSide recipient, EnhancementTuning t)
    {
        if (donor.EnhanceLevel < 0 || recipient.EnhanceLevel < 0)
            throw new ArgumentOutOfRangeException(nameof(donor), "enhancement levels cannot be negative");

        // ⚠ Gated on module 3. Transfer keys on role equality and I6 §9 #7 names the dependency:
        // hybrid role ids must be the same ids the pure frames use (OD3), "or transfer across a
        // hybrid is undefined". Refused by name rather than guessed at.
        if (IsHybrid(donor.Frame) || IsHybrid(recipient.Frame))
            return Refuse("enhance.transfer-hybrid-frame-undefined",
                "a transfer whose donor or recipient sits on a hybrid frame is undefined until module 3 " +
                "(slot-roles) settles hybrid role ids — I6 §9 #7", donor);

        if (!string.Equals(donor.RoleId, recipient.RoleId, StringComparison.Ordinal))
            return Refuse("enhance.transfer-role-mismatch",
                $"donor role '{donor.RoleId}' and recipient role '{recipient.RoleId}' differ — I6 §7.4 gates on the " +
                "stable role id, never a display name", donor);

        var gap = Math.Abs(donor.ItemLevel - recipient.ItemLevel);
        if (gap > t.TransferItemLevelWindow)
            return Refuse("enhance.transfer-level-window",
                $"item levels {donor.ItemLevel} and {recipient.ItemLevel} are {gap} apart, outside the " +
                $"±{t.TransferItemLevelWindow} window", donor);

        // floor(donor_level × ratio / 1000), then clamped to the RECIPIENT's own item-level cap. The
        // clamp is a property of the receiving item, not a progression ceiling — the same rule a
        // direct enhancement of that item would hit.
        var granted = checked((long)donor.EnhanceLevel * t.TransferRatioMilli) / 1000L;
        var recipientCap = EnhancePolicy.MaxLevelForItemLevel(recipient.ItemLevel, t);
        var headroom = Math.Max(0, recipientCap - recipient.EnhanceLevel);
        var applied = (int)Math.Min(granted, headroom);

        return new TransferOutcome(Allowed: true, GrantedLevels: applied, DonorLevelAfter: 0, Refusal: AtomRejection.Ok);
    }

    static bool IsHybrid(string frame) => string.Equals(frame, HybridFrame, StringComparison.OrdinalIgnoreCase);

    static TransferOutcome Refuse(string ruleId, string detail, TransferSide donor) =>
        new(Allowed: false, GrantedLevels: 0, DonorLevelAfter: donor.EnhanceLevel,
            Refusal: MutationRules.Violated(ruleId, detail));
}
