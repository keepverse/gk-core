using FusionRpg.Contracts;
using FusionRpg.Core.Combat.Element;

namespace FusionRpg.Core.Combat;

/// <summary>ICombatMath adapter — runs overlay pipeline when element payload is present.</summary>
public sealed class OverlayCombatMath : ICombatMath
{
    readonly CombatActorResolve _resolve;
    readonly OverlayCombatCalculator _calculator;
    readonly ICombatRng _rng;
    readonly Action<OverlayCombatBreakdown, DamagePacket, string>? _emitBreakdown;

    OverlayCombatMath(
        CombatActorResolve resolve,
        OverlayCombatCalculator calculator,
        ICombatRng rng,
        Action<OverlayCombatBreakdown, DamagePacket, string>? emitBreakdown)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _calculator = calculator ?? throw new ArgumentNullException(nameof(calculator));
        _rng = rng ?? throw new ArgumentNullException(nameof(rng));
        _emitBreakdown = emitBreakdown;
    }

    public static OverlayCombatMath Create(
        CombatActorResolve resolve,
        IElementHub? elementHub = null,
        ICombatRng? rng = null,
        Action<OverlayCombatBreakdown, DamagePacket, string>? emitBreakdown = null) =>
        new(
            resolve,
            new OverlayCombatCalculator(elementHub),
            rng ?? new SeededCombatRng(0),
            emitBreakdown);

    public long Finalize(long signedAmount, string ptr, DamagePacket packet, BoardEntitySnap? entity)
    {
        if (signedAmount > 0)
            return FinalizeHeal(signedAmount, packet);

        if (packet.ElementPayload == null || packet.ElementPayload.Count == 0)
            return signedAmount;

        var components = OverlayCombatCalculator.ParseComponents(packet.ElementPayload);
        if (components.Count == 0)
            return signedAmount;

        var baseDamage = Math.Abs(signedAmount);
        var attackerLess = string.IsNullOrWhiteSpace(packet.ActorPtr);
        var attacker = _resolve(packet.ActorPtr, attackerLess);
        var defender = _resolve(ptr, attackerLess: false);

        var request = new OverlayCombatRequest
        {
            BaseOverlayDamage = baseDamage,
            Components = components,
            Attacker = attacker,
            Defender = defender
        };

        var (delta, breakdown) = _calculator.Compute(request, _rng);
        _emitBreakdown?.Invoke(breakdown, packet, ptr);
        return delta;
    }

    /// <summary>
    /// spec-healing-pair.md §2.1: <c>effectiveHeal = baseOverlayHeal + heal.power(healer)</c>. No
    /// matchup, no roll, no opposed term — strictly less than combat-damage-ssot.md §4.3's "Funnel
    /// transport only, no matchup/hit/crit" ban, so the boundary stays untouched. <c>heal.power</c> is
    /// <c>Pool</c> class (the healer's own output capacity, like <c>combat.shield.capacity</c>), so
    /// there is no defender-side term to read at all — only the healer (<c>packet.ActorPtr</c>) is
    /// resolved. An attacker-less heal (no <see cref="DamagePacket.ActorPtr"/>) resolves the same stub
    /// snapshot the damage path uses, which composes <c>heal.power</c> to its default 0 — contributing
    /// nothing, not a guessed value.
    /// </summary>
    long FinalizeHeal(long signedAmount, DamagePacket packet)
    {
        var attackerLess = string.IsNullOrWhiteSpace(packet.ActorPtr);
        var healer = _resolve(packet.ActorPtr, attackerLess);
        var healPower = healer.Derived.Get(Stats.Derived.DerivedStatChannels.ResourceRestore("hp"));

        // HealNeverNegative: an overlay heal can never become damage. heal.power is registered
        // uncapped (a magnitude, PS-8) but nothing forbids an authored negative modifier reaching it
        // upstream — the floor at 0 is what actually enforces "never negative" at this boundary.
        var effectiveHeal = Math.Max(0.0, signedAmount + healPower);
        // combat-numerics (lawn-combat-wire T4): `checked` so a heal magnitude past `long`'s range
        // throws (docs/architecture/numeric-types.md: "overflow throws, never wraps") instead of silently narrowing.
        // `healPower` is `double` — a CombatDerivedReader/ActorDerivedSnapshot channel read. That is
        // allowed (floating point is permitted for any quantity since the 2026-09-15 owner ruling);
        // the range rule is the checked narrowing to `long` below.
        return checked((long)Math.Round(effectiveHeal, MidpointRounding.AwayFromZero));
    }
}
