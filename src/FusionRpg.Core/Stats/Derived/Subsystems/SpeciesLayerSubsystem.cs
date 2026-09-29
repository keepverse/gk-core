using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// species-progression module 6 (`species-layer-delivery`) step 6.2 — the ONE registered
/// <see cref="IActorStatSubsystem"/> that delivers 1a (species-passive core), 1b (a fusion/ledger
/// instance) and 2b (an empire's species allocation) rows to every compose path, replacing the five
/// private per-feature derived-write paths `actor-hub-ssot.md` §6.1 already predicted in writing.
///
/// <para><b>Not a re-bless.</b> Step 6.1 (this program's own T7/H1 obligation) already resolved each
/// <see cref="Aptitudes.AllocationScope"/> alone; this step ADDS new contributions from a source
/// (`rpg.species-layer`) nothing wired before — no existing actor's composed value moves, because
/// nothing today feeds 1a/1b, and 2b's own aptitude-allocation path is untouched until step 6.3 (spec
/// "What each step may move").</para>
///
/// <para><b>Rows, not raw allocations.</b> <paramref name="_rowsFor"/> hands back
/// <see cref="ProjectedLayerRow"/>s — module 3's (`species-layer-projector`) one projection mechanism,
/// already Θ-free (a <see cref="LayerValue.Fixed"/> value or a <see cref="LayerValue.LadderMicro"/>
/// coefficient) — so this subsystem's only job is supplying `P(Θ)` at resolve time via
/// <see cref="SpeciesLayerProjector.Resolve"/> and forwarding the result. It does not decide WHICH
/// rows an actor gets (`layer-source-selector`'s job, upstream of this delegate) or WHERE they are
/// stored (the injector's cache / the server's per-request assembly).</para>
///
/// <para><b>Memoized by rows reference, not by actor identity</b> (spec "The subsystem"): every actor
/// of the same `(empire, speciesId)` shares the exact same <see cref="ProjectedLayerRow"/> list
/// reference (the cache/assembly hands out one list per distinct pairing), so keying the memo on THAT
/// reference — never on <c>ctx</c> — naturally collapses every such actor into one cached resolve
/// instead of growing one slot per actor. A changed rows list (a fresh 2b re-projection, a save
/// switch) is simply a different reference and safely, harmlessly recomputes; the old slot is never
/// read again and this dictionary never grows past the number of distinct row lists in circulation.</para>
///
/// <para><b>Θ is read per resolve, never cached.</b> Rows are Θ-free by construction (module 3); only
/// the memo's <c>Theta</c> field is compared against the LIVE read on every call, so a Θ change alone
/// (with the same rows reference) invalidates and recomputes — proven by
/// <c>Theta_is_never_cached_in_species_layers</c>, which would catch a stale value surviving a Θ bump.</para>
///
/// <para><b>Order 100</b> is the same progression band <see cref="AptitudeSubsystem"/> already uses
/// (`actor-hub-ssot.md` §6) — not a new band. FlatSum/SumIncreased are both commutative, so relative
/// order between the two registered-at-100 subsystems carries no meaning, matching that subsystem's
/// own comment.</para>
/// </summary>
public sealed class SpeciesLayerSubsystem : IActorStatSubsystem
{
    readonly Func<StatContext, IReadOnlyList<ProjectedLayerRow>> _rowsFor;
    readonly IPowerIndexProvider _powerIndex;
    readonly PowerLadder _ladder;

    // One slot per ROWS REFERENCE (never per actor) -- see the type's own doc comment. Not static: a
    // static memo would leak one scoped test host's rows into another, the exact AptitudeTuningHub
    // race this repo already fixed once.
    readonly Dictionary<IReadOnlyList<ProjectedLayerRow>, (int Theta, IReadOnlyList<DerivedModifier> Mods)> _memo =
        new(ReferenceEqualityComparer.Instance);

    public SpeciesLayerSubsystem(
        PowerLadder ladder,
        IPowerIndexProvider? powerIndex = null,
        Func<StatContext, IReadOnlyList<ProjectedLayerRow>>? rowsFor = null)
    {
        _ladder = ladder ?? throw new ArgumentNullException(nameof(ladder));
        _powerIndex = powerIndex ?? new StubPowerIndexProvider();
        _rowsFor = rowsFor ?? (_ => Array.Empty<ProjectedLayerRow>());
    }

    public string SubsystemId => "rpg.species-layer";
    public int Order => 100; // same band as rpg.aptitude — not a new one

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var rows = _rowsFor(ctx) ?? Array.Empty<ProjectedLayerRow>();
        if (rows.Count == 0) return;

        var theta = _powerIndex.ActorIndex(ctx);
        if (!_memo.TryGetValue(rows, out var hit) || hit.Theta != theta)
        {
            var resolved = SpeciesLayerProjector.Resolve(rows, _ladder.Value(theta));
            _memo[rows] = hit = (theta, resolved);
        }

        foreach (var m in hit.Mods)
        {
            // GG-49 / actor-hub-ssot §8.1: an empty SourceId is a defect -- skip rather than mint an
            // unattributed contribution InspectSplit cannot name, the same defensive check
            // AtomDerivedSubsystem applies to its own externally-sourced rows.
            if (string.IsNullOrWhiteSpace(m.SourceId)) continue;
            mods.Add(m);
        }
    }

    /// <summary>Forces every memoized entry to recompute on its next read. Not needed for
    /// correctness (the memo self-corrects by rows reference and by Θ on every call) — an explicit
    /// escape hatch for a caller (a test) that wants to guarantee a fresh resolve.</summary>
    public void InvalidateMemo() => _memo.Clear();
}
