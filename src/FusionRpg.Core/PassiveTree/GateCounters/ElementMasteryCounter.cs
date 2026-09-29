using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// One <see cref="DamageApplyPipeline.Apply"/> result plus the two facts the pipeline itself does not
/// carry as a bundle: which side of the DoT-pulse discriminator this hit is on (P1,
/// <see cref="DamageOrigin"/>), and who the attacker is (the pipeline's <c>ptr</c> parameter is the
/// TARGET; the attacker has to travel alongside the call separately). Built by the caller right after
/// <c>DamageApplyPipeline.Apply</c> returns -- this type is the seam, not a new event source.
/// </summary>
public readonly record struct ElementMasteryCreditInput(
    DamageApplyResult Result,
    DamageOrigin Origin,
    IReadOnlyList<ElementPayloadComponent> Components,
    string? AttackerPtr);

/// <summary>
/// spec-gate-counters.md §2.2 -- "one credit per element component carried by a direct damage event
/// this player's actor landed for non-zero damage." Four sub-decisions, all decided here since (unlike
/// <see cref="StatusAppliedCounter"/>'s three-of-four-already-guaranteed shape) nothing upstream of
/// this class enforces any of them:
///
/// <list type="bullet">
/// <item><b>(a) Outbound, never inbound.</b> No attacker (<see cref="ElementMasteryCreditInput.AttackerPtr"/>
/// null/blank -- an attacker-less source, e.g. an environmental hazard) earns nothing. Unlike
/// <c>status_applied</c>'s (d), §2.2 names no self-target exclusion for damage -- this class implements
/// exactly the four sub-decisions §2.2 states and no more.</item>
/// <item><b>(b) Landed, never attempted, and non-zero.</b> <c>DamageApplyPipeline</c> deliberately lets
/// a zero un-absorbed delta reach the sink for miss-telemetry parity (<c>DamageApplyPipeline.cs:44-47,
/// 99-104</c>) and returns <c>FullyAbsorbed</c> separately when a shield eats the whole hit
/// (<c>:36-38,95-97</c>) -- so the credit test is <c>Outcome == Applied</c> **and**
/// <c>AppliedAmount != 0</c>, both required, neither sufficient alone.</item>
/// <item><b>(c) Each component once, never weighted.</b> A fire 0.6 / ice 0.4 packet credits fire +1
/// AND ice +1 -- <see cref="ElementPayloadComponent.Weight"/> is read for presence only (a credit is a
/// whole count per element, so the <c>double</c> weight has nothing to scale) and is never multiplied
/// into the credit. Components are de-duplicated by <see cref="ElementPayloadComponent.Element"/> before
/// crediting so a payload that happened to carry the same element twice still credits it once --
/// belt-and-suspenders alongside the shipped payload shape, which is not expected to duplicate an
/// element within one packet.</item>
/// <item><b>(d) Direct hits, never DoT pulses.</b> <see cref="DamageOrigin.StatusPulse"/> earns
/// nothing -- crediting a pulse would let one applied status earn an elemental credit every tick for
/// its whole duration, double-paying for the one application <c>status_applied</c> already credited
/// (§2.2d). <b>The real pulse call site now passes <see cref="DamageOrigin.StatusPulse"/></b> --
/// `BattleRunState.cs`'s `PulseSink` (Battle) and `StatusEffectBridge.cs`'s `StatusFunnelPulseSink`
/// (injector) both pass it explicitly (G1's once-deferred item, closed once `BattleEngine.cs`/
/// `BattleRunState.cs` came free of the concurrent edit this task originally routed around, per R9).
/// Every hit through either production pulse path now reaches this counter correctly excluded; a hit
/// through any OTHER caller of `DamageApplyPipeline.Apply`/`ApplyPacketToFunnel` still defaults to
/// `DirectHit`, which is correct for every one of them (they are not pulses).</item>
/// </list>
///
/// No <c>StatusCategoryRegistry</c>-style roster validation exists here on purpose:
/// <see cref="ElementTypeId"/> is a closed six-value enum
/// (<c>ActorElementTypes.cs:3-11</c>), not an open string id like a status -- there is no "unknown
/// element id reaching this counter" failure mode to guard against; the type system already closes it.
/// </summary>
public sealed class ElementMasteryCounter
{
    public const string Quantity = "element_mastery";

    readonly GateCounterAccumulator _accumulator;
    readonly Func<string, GateOwnerKey?> _resolveOwner;

    /// <param name="accumulator">Where a credit lands -- §4.3, no direct store write here.</param>
    /// <param name="resolveOwner">Answers "which player owns this attacker ptr", or <c>null</c> for
    /// none -- the same *reads a roster, never a live side flag* contract
    /// <see cref="StatusAppliedCounter"/>'s own resolver documents, so a host wiring both counters (task
    /// G6) can share one resolver implementation. Called at most once per <see cref="Handle"/>.</param>
    public ElementMasteryCounter(GateCounterAccumulator accumulator, Func<string, GateOwnerKey?> resolveOwner)
    {
        _accumulator = accumulator ?? throw new ArgumentNullException(nameof(accumulator));
        _resolveOwner = resolveOwner ?? throw new ArgumentNullException(nameof(resolveOwner));
    }

    /// <summary>Wire directly after a <c>DamageApplyPipeline.Apply</c> call:
    /// <c>counter.Handle(new ElementMasteryCreditInput(result, origin, components, attackerPtr));</c></summary>
    public void Handle(ElementMasteryCreditInput input)
    {
        // (d) DoT pulses excluded -- see the class doc's fourth bullet.
        if (input.Origin != DamageOrigin.DirectHit)
            return;

        // (b) Landed AND non-zero -- neither test alone is sufficient (see the class doc's second bullet).
        if (input.Result.Outcome != DamageApplyOutcome.Applied)
            return;
        if (input.Result.AppliedAmount == 0)
            return;

        // (a) Outbound only -- an attacker-less hit has nobody to credit.
        if (string.IsNullOrWhiteSpace(input.AttackerPtr))
            return;

        if (input.Components.Count == 0)
            return; // nothing carried -- nothing to credit, and nothing to look up an owner for.

        var owner = _resolveOwner(input.AttackerPtr);
        if (owner is null)
            return; // no player owns this attacker -- never inferred, never a default.

        // (c) Each distinct element once, never weighted.
        foreach (var element in DistinctElements(input.Components))
            _accumulator.Credit(new GateCounterKey(owner.Value, Quantity, element.ToElementId()));
    }

    static IEnumerable<ElementTypeId> DistinctElements(IReadOnlyList<ElementPayloadComponent> components)
    {
        var seen = new HashSet<ElementTypeId>();
        foreach (var component in components)
            if (seen.Add(component.Element))
                yield return component.Element;
    }
}
