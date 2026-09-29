using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// The LAWN executor for the <c>stat.derived</c> atom kind — the piece whose absence is why no aura
/// could reach a lawn entity (decisions.md, "Derived-write lawn executor", 2026-08-30).
///
/// <para><b>Why this exists.</b> <c>stat.derived</c> is the kind whose entire purpose is "direct
/// derived-channel mods", and an aura is exactly one. D6 quarantined it to
/// <c>None/None/None</c> in 2026-08-22 because no runtime had an executor — a bind would be accepted
/// and then do nothing, forever. E12 (2026-08-23) gave <b>battle</b> one via
/// <see cref="Battle.TraitAtomSource"/>; the lawn never got the twin. Five features
/// (patron, stars, injuries, contracts, commander aptitudes) each grew a private derived-write path
/// instead, exactly as actor-hub-ssot.md §6.1 predicted in writing.</para>
///
/// <para><b>Why a subsystem and not a sink arm.</b> The lawn's derived compose already runs per
/// resolve in <see cref="ActorHub.ResolveDerived"/>: every registered
/// <see cref="IActorStatSubsystem"/> contributes, <see cref="DerivedComposer"/> folds. Adding an arm
/// to <c>InjectorEffectActionSink</c> would be a second delivery path for a value the composer
/// already owns. This mirrors <see cref="AptitudeSubsystem"/>'s shape exactly — an injected
/// per-context delegate, so the module owns *resolving mods into channel values* and never owns
/// *where the bindings are stored*.</para>
///
/// <para><b>Order 350</b> is not a new band: actor-hub-ssot.md §6's registry table already reserves
/// <c>foundation.effect | 350 | session bag | future timed derived</c> for precisely this. Using the
/// reserved slot keeps the documented registry honest rather than inventing a sixth ordering.</para>
///
/// <para><c>ContributeDerived</c> is idempotent and stateless between calls, matching this seam's
/// standing contract — and <c>ActorHub.Register</c> replaces by <see cref="SubsystemId"/>, so a double
/// registration can never double-add.</para>
/// </summary>
public sealed class AtomDerivedSubsystem : IActorStatSubsystem
{
    /// <summary>
    /// The bound <c>stat.derived</c> atoms that apply to this actor, already scoped by owner key.
    /// A delegate for the same reason <see cref="AptitudeSubsystem"/> uses one: this module resolves
    /// values, it does not own the binding store. Production passes the injector's per-`owner_key`
    /// cache; tests pass a list.
    /// </summary>
    readonly Func<StatContext, IReadOnlyList<BoundDerivedAtom>> _boundFor;

    public AtomDerivedSubsystem(Func<StatContext, IReadOnlyList<BoundDerivedAtom>>? boundFor = null) =>
        _boundFor = boundFor ?? (_ => Array.Empty<BoundDerivedAtom>());

    public string SubsystemId => "atom.derived";

    /// <summary>Reserved for `foundation.effect` by actor-hub-ssot.md §6 — not a new band.</summary>
    public int Order => 350;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var bound = _boundFor(ctx);
        if (bound is null || bound.Count == 0) return;

        foreach (var atom in bound)
        {
            if (string.IsNullOrWhiteSpace(atom.Channel)) continue;
            // GG-49 / actor-hub-ssot §8.1: empty SourceId is a defect — skip rather than mint
            // an unattributed contribution that InspectSplit cannot name.
            if (string.IsNullOrWhiteSpace(atom.SourceId)) continue;
            mods.Add(new DerivedModifier(atom.Channel, atom.Op, atom.Amount, SourceId: atom.SourceId));
        }
    }

    /// <summary>
    /// Maps the atom row's <c>op</c> string to a composer op. <b>There is no `More` on the derived
    /// side</b> (effect-atom/definitions.md §14's kind note), so an unknown or `more` op is a content
    /// error, not something to silently coerce to `Flat` — silent coercion is how a wrong number ships
    /// looking correct.
    /// </summary>
    public static bool TryParseOp(string? op, out DerivedModifierOp parsed)
    {
        switch (op)
        {
            case "flat": parsed = DerivedModifierOp.Flat; return true;
            case "increased": parsed = DerivedModifierOp.Increased; return true;
            case "replace": parsed = DerivedModifierOp.Replace; return true;
            case "flag": parsed = DerivedModifierOp.Flag; return true;
            default: parsed = default; return false;
        }
    }
}

/// <summary>
/// One bound <c>stat.derived</c> atom, already resolved to the three things the composer needs.
/// Deliberately not the raw <see cref="AtomRow"/>: parsing <c>params_json</c> belongs at bind time,
/// not on a per-resolve hot path (perf SSOT — uncached resolves are the measured cost, not scans
/// alone).
/// </summary>
public readonly record struct BoundDerivedAtom(
    string Channel, DerivedModifierOp Op, double Amount, string SourceId);
