namespace FusionRpg.Core.PassiveTree.GateCounters;

/// <summary>
/// spec-gate-counters.md §12 -- the "aspect-scope collision rule". <c>element_mastery</c> has a named
/// future owner (the creature program's <c>aspect-scope</c> module) that D37 only deferred, not
/// cancelled, so this registry and that module CAN collide one day. The failure
/// this exists to prevent is silent double-counting, not duplication itself:
///
/// <list type="bullet">
/// <item><b>Exactly one producer answers a family.</b> <see cref="Register"/> is exclusive per
/// <see cref="IGateQuantitySource.Family"/> -- a second registration for a family that already has one
/// throws, naming both the existing and the incoming producer's type.</item>
/// <item><b>There is no combine path.</b> No sum, no max, no precedence chain -- two answers for one
/// family are unrepresentable by construction, which is a stronger guarantee than a warning nobody
/// reads.</item>
/// <item><b>The opposite of <c>ActorHub.Register</c>'s replace-by-id behaviour</b> -- replacement is
/// right for a subsystem that composes; it is wrong for a quantity that would otherwise be answered
/// twice.</item>
/// </list>
///
/// <para><b>Task split (recorded so it is not re-derived):</b> this class and
/// <see cref="IGateQuantitySource"/> are built here, in task G3, as the shared exclusivity contract
/// both counter-backed families need to plug into. The actual index-to-equivalents math
/// (<c>MasteryIndex</c>, the square-root transform, §9) and the real production
/// <c>StatusAppliedSource</c>/<c>ElementMasterySource</c> implementations that read it from the store
/// are task G4's "index transform and gate registry" -- this registry is the seam they attach to, not
/// a duplicate of their work.</para>
/// </summary>
public sealed class GateQuantityRegistry
{
    readonly Dictionary<string, IGateQuantitySource> _sources = new(StringComparer.Ordinal);

    /// <summary>Registers <paramref name="source"/> for its own <see cref="IGateQuantitySource.Family"/>.
    /// Throws <see cref="InvalidOperationException"/>, naming both the already-registered producer's
    /// type and the incoming one's, if that family already has a producer -- §12's "no combine
    /// path", made a compile-time-adjacent guarantee rather than a discipline.</summary>
    public void Register(IGateQuantitySource source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));
        if (string.IsNullOrWhiteSpace(source.Family))
            throw new ArgumentException("a gate quantity source must name a non-empty Family", nameof(source));

        if (_sources.TryGetValue(source.Family, out var existing))
        {
            if (ReferenceEquals(existing, source)) return; // re-registering the same instance is a no-op
            throw new InvalidOperationException(
                $"gate quantity family '{source.Family}' already has a producer " +
                $"({existing.GetType().FullName}); a second registration ({source.GetType().FullName}) " +
                "would double-count it -- spec-gate-counters.md §12: exactly one producer answers a " +
                "family, and there is no combine path. The handover is one line at the composition " +
                "root: delete the old registration, add the new one.");
        }

        _sources[source.Family] = source;
    }

    /// <summary>True once some producer has registered for <paramref name="family"/> -- the "this
    /// quantity has no producer" half of §5.2's tier-0 reason split (the other half, "no aptitude
    /// allocated yet", lives in `tree-resolve` and is not this type's concern).</summary>
    public bool HasProducer(string family) => _sources.ContainsKey(family);

    /// <summary>Aptitude-point-equivalents for <paramref name="id"/>, delegated to whichever producer
    /// (if any) registered for <c>id.Family</c>. A family with no producer answers <c>0</c> -- a known
    /// content gap (§5.2), never an error and never inferred as "player has not started".</summary>
    public long AptitudePointEquivalents(GateQuantityId id, GateActorContext actor) =>
        _sources.TryGetValue(id.Family, out var source)
            ? source.AptitudePointEquivalents(id, actor)
            : 0;
}
