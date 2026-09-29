namespace FusionRpg.Core.Items;

/// <summary>
/// One `rarity_budget` key: which module owns reading it, and whether that module has a decided
/// shape for it. SC7: "a row no code consumes is a lie in a table" — <see cref="IsRegistered"/>
/// gates on a key having a **named, spec-assigned consumer**, the same bar item-ideal.md's own
/// `ContentRuleViolated` namespace registry uses (a namespace registers once a lane starts raising
/// rule ids under it, not once its full runtime ships). `promote_from`, `enhance_cap` and
/// `power_ceiling` are seeded now with that bar met, even though modules 11/15 are not yet built —
/// `ssot-rarity.md` §5 marks all five "seeded now" for exactly this reason. As of 2026-09-05 every
/// key in the list below has a decided shape — `salvage_yield` (module 14), `reroll_cost_mult`
/// (module 15) and `socket_min`/`socket_max` (module 16) each closed their own. `set_eligible` and
/// `charm_potency` fail the bar outright: no spec reads either, so they are absent from
/// <see cref="All"/> entirely, not merely unshipped.
/// </summary>
public readonly record struct RarityBudgetKeyDef(string Key, string ConsumerModule, bool HasDecidedShape);

public sealed class RarityBudgetKeyRejection : Exception
{
    public RarityBudgetKeyRejection(string message) : base(message) { }
}

/// <summary>
/// The closed key registry (item-ideal.md, `rarity-bands`, module 7). Never grown ad hoc — a new key
/// is a reviewed addition here, naming its consumer, exactly like every other closed vocabulary in
/// this program.
/// </summary>
public static class RarityBudgetKeys
{
    public static readonly IReadOnlyList<RarityBudgetKeyDef> All = new[]
    {
        new RarityBudgetKeyDef("promote_from", "enhance-reroll (15)", HasDecidedShape: true),
        new RarityBudgetKeyDef("pity_guarded", "drop-volume (11)", HasDecidedShape: true),
        new RarityBudgetKeyDef("drop_weight_default", "drop-volume (11)", HasDecidedShape: true),
        new RarityBudgetKeyDef("enhance_cap", "enhance-reroll (15)", HasDecidedShape: true),
        new RarityBudgetKeyDef("power_ceiling", "item-power-reads (9)", HasDecidedShape: true),

        // ⭐ UNBLOCKED 2026-09-04 by module 14 (salvage-craft), which decided the shape ssot-rarity.md
        // §5 recorded as "awaiting I9": one integer per rung, the SUBSTRATE quantity a salvage of that
        // rung returns before the affix bonus (`salvageCoefficient.{rung}.substrateBase` in
        // gk-core/data/tuning/materials.v1.json, seeded by RpgStore.SeedSalvageYield). It satisfies §9.8's one
        // constraint on this key — it must NOT reuse `shard.{CreatureRarity}` ids — by naming no shard id
        // at all: the shard leg is R1's rung−1 rule, which is derived, not a per-rung budget row.
        new RarityBudgetKeyDef("salvage_yield", "salvage-craft (14)", HasDecidedShape: true),

        // ⭐ UNBLOCKED 2026-09-05 by module 15 (enhance-reroll), which decided the shape ssot-rarity.md
        // §5 recorded as "awaiting I7": the per-rung integer is the reroll price's RUNG LEG,
        // 1000 + rerollCostRungSlopeMilli × rungIndex (gk-core/data/tuning/enhancement.v1.json, seeded by
        // RpgStore.SeedRerollCostMult and read by RerollPolicy.CostMultMilli). §9.7's one constraint —
        // it must scale with AFFIX COUNT, not rung alone — is met by the second leg, which is not a
        // per-rung row: the total multiplier is rungLeg × (affixBase + affixStep × affixCount) / 1000,
        // and EnhancementTuning.Parse REFUSES a tuning whose affix leg does not out-spread the rung
        // leg, because a rung-dominant price inverts §8.1's "low rungs are the best crafting bases".
        new RarityBudgetKeyDef("reroll_cost_mult", "enhance-reroll (15)", HasDecidedShape: true),

        // ⭐ UNBLOCKED 2026-09-05 by module 16 (sockets), which decided the shape ssot-rarity.md §5
        // recorded as "awaiting I4": TWO integers per rung — the inclusive window a drop's socket
        // count is rolled from, BEFORE the base type's own socketMax clamps it
        // (`rarityGrant.{rung}.socketMin`/`.socketMax` in the current `sockets` revision,
        // `SocketTuningFiles.Current`, seeded by
        // RpgStore.SeedSocketGrants and read by SocketGeometry.SocketsAtDrop). ssot-sockets.md §9.5's
        // one constraint — rarity must grant a RANGE, not a number, so OD4's overlap principle reaches
        // this axis — is enforced at LOAD: SocketTuning.Parse refuses a table whose adjacent windows
        // do not overlap, because a gap makes socket count a strict ladder and re-opens §8.1's "the
        // only stat that matters" failure at full strength.
        new RarityBudgetKeyDef("socket_min", "sockets (16)", HasDecidedShape: true),
        new RarityBudgetKeyDef("socket_max", "sockets (16)", HasDecidedShape: true),

        // ⭐ ADDED 2026-09-05 by module 17 (uniques). ssot-uniques.md §5.3 proposed exactly one new key
        // — "may a unique carry this rung", 0 at ordinals 10-20 and 1 at 30-100 — and named this
        // registry as its home, mirroring the `set_eligible` slot. The shape: ONE 0/1 integer per rung,
        // DERIVED from the ordinal against `uniques.v1.json`'s `rungFloorOrdinal` rather than authored
        // as a second per-rung table beside the seeded ladder (UniqueTuning.IsRungEligible), and seeded
        // by RpgStore.SeedUniqueEligible. Read by UniqueValidator/UniqueCorpusValidator, which raise
        // ContentRuleViolated{unique.rung-ineligible} on a 0. Ordinals 10 `chaff` and 20 `sprout` are
        // DEFINED as the absence of design ("husks, clippings"; "it works, that is all it does") and a
        // unique is the presence of it — §10.7 leaves the owner one number to move if a `sprout`-rung
        // joke unique is ever wanted, and moving it re-seeds the column with no code change.
        new RarityBudgetKeyDef("unique_eligible", "uniques (17)", HasDecidedShape: true),

        // set_eligible and charm_potency are deliberately ABSENT, not merely undecided: D15 makes the
        // former vacuous (a set has no rarity) and spec-set-charm-gen.md never reads the latter. Never
        // re-add either without a spec that actually consumes it.
    };

    /// <summary>
    /// True only when <paramref name="key"/> is in the closed list AND its named module has a
    /// decided shape for it. A key present but not yet decided is registered as a future obligation
    /// but still refuses a seed row today — the same "not decided ≠ safe default" rule this whole
    /// program applies everywhere else. Every key currently listed is decided; the mechanism stays
    /// because the next key added will not be.
    /// </summary>
    public static bool IsRegistered(string key) =>
        All.Any(k => string.Equals(k.Key, key, StringComparison.Ordinal) && k.HasDecidedShape);

    public static void Validate(string key)
    {
        if (!IsRegistered(key))
            throw new RarityBudgetKeyRejection(
                $"rarity_budget key '{key}' is not registered with a decided consumer shape — " +
                "SC7 refuses a row no code consumes rather than letting it sit inert");
    }
}
