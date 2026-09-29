namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `ai-tiers-personality` (module 3, CAI1.9, spec-ai-tiers-personality.md §3): shades a
/// profile with one actor's <see cref="AiPersonality"/> — "a personality shades a role and never
/// breaks it." Three of the four axes land here; the fourth (<see cref="PersonalityAxis.Aggression"/>)
/// is NOT a profile field — it is the actor's own contribution to a CANDIDATE's effective tier
/// (module 6's, `aggression-tier-map`, CAI1.13's cross-module note in the spec's own Open Questions),
/// so applying it is that module's job, not this one's. This type touches only the three axes that
/// land on the profile record itself.
///
/// <para><b>Every axis's own two-step contract</b>: the personality BOUND limits the draw (module 1's
/// `AiPersonalityFactory`); the APPLIED RESULT is separately clamped to that field's own natural
/// domain — never below 0 for a weight (the loader already refuses a negative authored weight,
/// `SiegeTuning.cs:346-349` is the shipped precedent this follows), and 0..1000 for a per-mille floor
/// (the bounded-ratio domain every other per-mille field in this schema already has). Both steps are
/// required: a wide bound authored on a profile that wants a tight range still gets clamped to that
/// tight, natural domain.</para>
///
/// <para><b>Identity at all-bounds-0.</b> Every offset is 0 (module 1's own personality draw), so
/// every clamp is a no-op and the applied profile equals the authored profile field for field —
/// `All_bounds_zero_is_byte_identical` asserts exactly this.</para>
/// </summary>
public static class AiPersonalityApply
{
    public static CombatAiProfile Apply(CombatAiProfile profile, AiPersonality personality)
    {
        var recklessness = personality.OffsetOf(PersonalityAxis.Recklessness);
        var focus = personality.OffsetOf(PersonalityAxis.Focus);
        var thrift = personality.OffsetOf(PersonalityAxis.Thrift);

        if (recklessness == 0 && focus == 0 && thrift == 0)
            return profile; // identity fast path — also what keeps the byte-identity test trivially exact

        var scoring = profile.Scoring;
        // Recklessness: "+ lowers risk aversion" -- i.e. lowers WeightRisk. Never below 0.
        var weightRisk = ClampNonNegative(checked(scoring.WeightRisk - recklessness));
        // Focus: "focuses the weak and the finishable" -- WeightLowHp and WeightKill together. Never below 0.
        var weightLowHp = ClampNonNegative(checked(scoring.WeightLowHp + focus));
        var weightKill = ClampNonNegative(checked(scoring.WeightKill + focus));

        var appliedScoring = scoring with { WeightRisk = weightRisk, WeightLowHp = weightLowHp, WeightKill = weightKill };

        // Thrift: "+ hoards, - spends" -- every reserve floor's FloorMilliOfMax, clamped to the bounded
        // per-mille ratio 0..1000 every other per-mille field in this schema already lives in.
        IReadOnlyList<AiReserveFloor> appliedReserves = profile.Reserves;
        if (thrift != 0 && profile.Reserves.Count > 0)
        {
            var shifted = new AiReserveFloor[profile.Reserves.Count];
            for (var i = 0; i < profile.Reserves.Count; i++)
            {
                var floor = profile.Reserves[i];
                var appliedMilli = Math.Clamp(checked(floor.FloorMilliOfMax + thrift), 0, 1000);
                shifted[i] = floor with { FloorMilliOfMax = appliedMilli };
            }
            appliedReserves = shifted;
        }

        return profile with { Scoring = appliedScoring, Reserves = appliedReserves };
    }

    static int ClampNonNegative(int value) => value < 0 ? 0 : value;
}
