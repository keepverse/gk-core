using FusionRpg.Contracts;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;

namespace FusionRpg.Core.Combat;

/// <summary>
/// Plan DamagePacket → resolve ptrs → Funnel mutations. Never a multi-ptr FA10.
/// </summary>
public static class CombatDamageDispatcher
{
    public static int DispatchInstant(
        DamagePacket packet,
        BoardSnapshot snapshot,
        EffectEventDto? ev,
        EffectFunnel? funnel,
        CombatPolicy? policy = null,
        ICombatRng? rng = null,
        ICombatMath? math = null,
        List<string>? skipped = null,
        Shield.ShieldGate? shieldGate = null,
        CombatActorResolve? actorResolve = null,
        DamageOrigin origin = DamageOrigin.DirectHit,
        Action<DamageApplyResult, DamageOrigin, IReadOnlyList<ElementPayloadComponent>, string?>? onDamageApplied = null)
    {
        using var _perf = FusionRpg.Core.Diagnostics.PerfProbe.Measure(FusionRpg.Core.Diagnostics.PerfSection.CombatDispatch);
        policy ??= CombatPolicy.Default;
        math ??= PassThroughCombatMath.Instance;
        snapshot ??= BoardSnapshot.Empty;
        var limit = policy.ResolveProcDepthLimit(packet.ProcDepthLimit);
        if (packet.ChainDepth >= limit)
        {
            skipped?.Add((packet.SourceGrantId ?? "") + ":proc-depth");
            return 0;
        }

        var ptrs = TargetResolver.Resolve(packet.Target, snapshot, ev, policy, rng);
        var n = 0;
        foreach (var raw in ptrs)
        {
            var ptr = CombatPtr.Normalize(raw);
            if (string.IsNullOrWhiteSpace(ptr)) continue;
            var amount = math.Finalize(packet.SignedAmount, ptr, packet, snapshot.FindPtr(ptr));
            if (funnel == null) continue;
            // Apply tail is the shared DamageApplyPipeline (combat-unification): shield gate
            // → funnel, byte-identical to the pre-extraction inline tail.
            var applied = DamageApplyPipeline.ApplyPacketToFunnel(
                packet, ptr, amount, ev?.HitCount ?? 1, shieldGate, funnel, ev, origin);

            // spec-gate-counters.md §2.2/§7 P1 -- the ElementMasteryCounter seam. Never a new required
            // dependency: `onDamageApplied` is null on every pre-existing caller (defaulted, like
            // `origin` itself), so this line changes nothing for a caller that doesn't wire it. The
            // components travel as `ElementPayloadComponent`, not the wire DTO, so the callback never
            // has to know about `FusionRpg.Contracts` -- the same "read for presence, parsed once"
            // shape `OverlayCombatCalculator.ParseComponents` already gives every other combat-math
            // caller.
            onDamageApplied?.Invoke(
                applied, origin, OverlayCombatCalculator.ParseComponents(packet.ElementPayload), packet.ActorPtr);

            switch (applied.Outcome)
            {
                case DamageApplyOutcome.Applied:
                    n++;
                    break;
                case DamageApplyOutcome.FullyAbsorbed:
                    skipped?.Add((packet.SourceGrantId ?? ptr) + ":absorbed");
                    break;
                default:
                    skipped?.Add((packet.SourceGrantId ?? ptr) + ":enqueue");
                    break;
            }

            // T5.4 (spec-reflection.md SS3): reads finalDamage pre-shield -- `amount`, computed
            // above, never `applied` -- and reuses ProcDepthLimit as the ONLY bound (SS2, no
            // second counter). A terminal bounce is left to the SAME top-of-function check on
            // the recursive call inside TryReflect, so it is dropped, never applied at a
            // clamped zero (SS2.1 rule 2).
            //
            // CombatPolicy.ReflectReadsPostShield flips that one reading: `applied.AppliedAmount`
            // is what actually reached HP, so a fully absorbed hit reflects nothing. Default false
            // keeps the shipped behaviour exactly -- the value is already on hand either way, so
            // this costs no extra work, only a choice.
            var reflectSource = policy.ReflectReadsPostShield ? applied.AppliedAmount : amount;
            if (actorResolve != null && rng != null && reflectSource < 0 && !string.IsNullOrWhiteSpace(packet.ActorPtr))
                TryReflect(packet, ptr, reflectSource, ev, funnel, policy, rng, math, skipped, shieldGate, actorResolve, snapshot);
        }

        return n;
    }

    /// <summary>
    /// One reflection attempt for a single ptr's finalized hit. Builds a new, reversed
    /// DamagePacket (reflector to attacker) and re-enters <see cref="DispatchInstant"/> at the
    /// top -- spec-reflection.md SS2.2: reflection is a later Funnel event, not a callback
    /// inside the mitigation math. No ElementPayload on the bounce: OverlayCombatMath.Finalize
    /// passes an ElementPayload-less packet through unchanged, so `bounced` (already final)
    /// is not re-mitigated.
    ///
    /// <para><b>W3 (battle-derived-wire T2): <c>internal</c>, not <c>private</c>.</b>
    /// <c>BattleRunState.ApplyHp</c> — battle's direct HP apply (basic attack, guardian share, DoT
    /// pulse) — enters one level BELOW <see cref="DispatchInstant"/>, so it never reached this step
    /// and all four <c>combat.reflect.*</c> families were inert in every battle (audit D2). Battle now
    /// calls THIS method, the same one the effect path uses; the body is unchanged and duplicated
    /// nowhere. <c>ProcDepthLimit</c> remains the only termination bound (combat-damage-ssot.md
    /// §6.7a) — the bounce still carries <c>ChainDepth + 1</c> into the shared check.</para>
    /// </summary>
    internal static void TryReflect(
        DamagePacket packet, string reflectorPtr, long amount, EffectEventDto? ev, EffectFunnel funnel,
        CombatPolicy policy, ICombatRng rng, ICombatMath math, List<string>? skipped,
        Shield.ShieldGate? shieldGate, CombatActorResolve actorResolve, BoardSnapshot snapshot)
    {
        var attackerPtr = CombatPtr.Normalize(packet.ActorPtr);
        if (string.IsNullOrWhiteSpace(attackerPtr) || attackerPtr == reflectorPtr) return;

        var reflector = actorResolve(reflectorPtr, attackerLess: false).Derived;
        var reflectedUpon = actorResolve(attackerPtr, attackerLess: false).Derived;

        // Linear from zero, not the spec's own sigmoid sketch -- CombatPolicy.ReflectRateScale's
        // doc comment: sigmoid(0)=0.5 would hand every actor a default reflect chance,
        // contradicting NoGoldensMoveAtZero. Same reasoning already applied to parry/block (T5.3).
        // The shape now has one owner, ElementalResolver.RateFromZero, instead of being written out
        // here and again in PhaseModel (D8).
        //
        // D14: omni + element, weighted across the INCOMING hit's payload -- what an actor reflects
        // depends on what it was hit with. Weights sum to 1.0 (ElementPayload.Validate), so the omni
        // half survives the weighting unchanged and only the element half varies by component; that is
        // the same identity OverlayCombatCalculator's ampDelta already relies on. An untyped hit has no
        // components and reads the omni halves alone, which is byte-identical to the pre-D14 behaviour.
        var (rateDelta, dmgDelta) = ReflectDeltas(packet.ElementPayload, reflector, reflectedUpon);

        var pReflect = ElementalResolver.RateFromZero(rateDelta, 0.0, policy.ReflectRateScale);
        if (!CombatProbability.RollSuccess(rng, pReflect)) return;

        var reflectShare = ElementalResolver.RateFromZero(dmgDelta, 0.0, policy.ReflectShareScale);
        var bounced = (long)Math.Round(Math.Abs(amount) * reflectShare, MidpointRounding.AwayFromZero);
        if (bounced <= 0) return;

        var bounce = new DamagePacket
        {
            PacketId = packet.PacketId + ":reflect",
            SourceGrantId = packet.SourceGrantId,
            EffectId = packet.EffectId,
            PluginId = packet.PluginId,
            ActorPtr = reflectorPtr,
            Target = new TargetSpec { Mode = TargetModes.Single, Ptr = attackerPtr },
            SignedAmount = -bounced,
            ChainDepth = packet.ChainDepth + 1,
            Channel = packet.Channel,
            Tick = packet.Tick,
            ProcDepthLimit = packet.ProcDepthLimit
        };
        DispatchInstant(bounce, snapshot, ev, funnel, policy, rng, math, skipped, shieldGate, actorResolve);
    }

    /// <summary>
    /// The reflect rate and damage deltas, as omni + element weighted across the incoming payload.
    ///
    /// <para>A plain loop rather than LINQ: this runs inside <c>DispatchInstant</c>, on the hit path,
    /// and the kernel purity rule bans an allocated enumerator plus a delegate per hit. Two
    /// accumulators in one pass rather than two passes, for the same reason.</para>
    ///
    /// <para>No components is the untyped case and reads the omni channels alone — not zero. Reading
    /// the element half of an attack that had no element would be reading a channel nothing wrote.</para>
    /// </summary>
    static (double RateDelta, double DamageDelta) ReflectDeltas(
        IReadOnlyList<Contracts.ElementPayloadComponentDto>? payload,
        Stats.Derived.ActorDerivedSnapshot reflector,
        Stats.Derived.ActorDerivedSnapshot reflectedUpon)
    {
        if (payload == null || payload.Count == 0)
            return (
                reflector.Get(Stats.Derived.DerivedStatChannels.CombatReflectRateOmni)
                    - reflectedUpon.Get(Stats.Derived.DerivedStatChannels.CombatReflectResistRateOmni),
                reflector.Get(Stats.Derived.DerivedStatChannels.CombatReflectDamageOmni)
                    - reflectedUpon.Get(Stats.Derived.DerivedStatChannels.CombatReflectResistDamageOmni));

        var rate = 0.0;
        var damage = 0.0;
        var components = OverlayCombatCalculator.ParseComponents(payload);
        if (components.Count == 0)
            return (
                reflector.Get(Stats.Derived.DerivedStatChannels.CombatReflectRateOmni)
                    - reflectedUpon.Get(Stats.Derived.DerivedStatChannels.CombatReflectResistRateOmni),
                reflector.Get(Stats.Derived.DerivedStatChannels.CombatReflectDamageOmni)
                    - reflectedUpon.Get(Stats.Derived.DerivedStatChannels.CombatReflectResistDamageOmni));

        for (var i = 0; i < components.Count; i++)
        {
            var c = components[i];
            rate += c.Weight * (CombatDerivedReader.ReflectRate(reflector, c.Element)
                                - CombatDerivedReader.ReflectResistRate(reflectedUpon, c.Element));
            damage += c.Weight * (CombatDerivedReader.ReflectDamage(reflector, c.Element)
                                  - CombatDerivedReader.ReflectResistDamage(reflectedUpon, c.Element));
        }

        return (rate, damage);
    }
}
