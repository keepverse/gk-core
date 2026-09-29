using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// class-system-todo.md P2.4 — the registered seam spec-aptitude-resolve.md §2 identifies:
/// <see cref="IActorStatSubsystem"/>, through <c>ActorHub.Register</c>, exactly where
/// <see cref="RpgProgressionSubsystem"/> already sits. Not <c>ClassStatPlugin</c> — that is a
/// different pipeline entirely (see that class's own doc comment).
///
/// <para>The allocation source is a per-context delegate, same shape as
/// <see cref="RpgProgressionSubsystem"/>'s <c>level</c> delegate and for the identical reason: this
/// module owns resolving an allocation into channel values, not where the allocation itself is
/// stored. `point-economy`'s `AllocationStore` (Phase 6) is the real source-of-truth once it exists;
/// until then the default is <see cref="AptitudeAllocation.Empty"/> — nobody has spent a point, so
/// this subsystem is wired into production and provably inert (§9: "zero goldens move on an empty
/// allocation") ahead of there being anything to allocate.</para>
///
/// <para><c>ContributeDerived</c> is idempotent (§2 rule 1): it holds no state between calls and
/// <see cref="AptitudeResolver.Resolve"/> is pure, so two calls with the same inputs produce the same
/// modifiers — and <c>ActorHub.Register</c> replacing by <see cref="SubsystemId"/> means a double
/// registration never double-adds regardless.</para>
///
/// <para><b>`species-build` T0.1/T0.2 — memoized, self-correcting.</b> <see cref="AptitudeResolver.Resolve"/>
/// loops every tuning edge (526 shipped) and calls <c>Share()</c> per edge — a `GrandTotal()` over 12
/// aptitudes x 4 scopes each, ~25,000 dictionary lookups per call — and this runs on the status/hit
/// path as well as the apply path. The memo is keyed on <c>(Side, TypeId, Theta)</c> plus the
/// ALLOCATION REFERENCE `_allocation(ctx)` returns for this call, and re-resolves whenever that
/// reference differs from what produced the cached entry.
///
/// <para><b>Why by reference, not an external bump.</b> A first draft tracked a manually-cleared
/// generation stamp, bumped from the one production call site that changes the allocation
/// (`CheatState.RefreshCommanderAllocationCache`). `CommanderAllocationSourceTests` (Core-only, no
/// injector, calling <see cref="CommanderAllocationSource.Refresh"/> directly) caught the real defect
/// in that design immediately: any caller that constructs
/// <c>CommanderAllocationSource</c>/<see cref="AptitudeSubsystem"/> WITHOUT going through
/// `CheatState` — which is every Core test, and any future non-injector host — would never fire the
/// bump, and the memo would silently serve a stale allocation forever. `FusionRpg.Core` cannot
/// reference `FusionRpg.Injector` to fix this from the other direction either.</para>
///
/// <para>Keying on the allocation reference removes the whole class of bug: nothing external has to
/// remember to call anything. `_allocation(ctx)` is a cheap, documented "never reads on the hot
/// path... a bare field read" lookup (<see cref="CommanderAllocationSource.Resolve"/>'s own doc
/// comment) — calling it every time costs nothing next to the ~25,000-lookup resolve it guards.
/// <see cref="AptitudeAllocation"/> has no value-equality override, so this is ordinary reference
/// equality: the SAME cached instance (the common "nothing changed since last refresh" case, and the
/// shared <see cref="AptitudeAllocation.Empty"/> singleton for "nobody has allocated") hits the memo;
/// any different instance — even one with identical points — safely, harmlessly recomputes. Bounded
/// growth still holds: a changed allocation OVERWRITES that key's single slot, it never adds a new
/// one, so the memo never grows past one entry per distinct `(Side, TypeId, Theta)` regardless of how
/// many times the allocation is refreshed.</para>
///
/// <para><b>`StatSystem.Invalidate()` needs no bump.</b> That signal is StatSystem's own primary-compose
/// dirty flag (cheats, items, session mods) — it does not touch the aptitude allocation at all, so a
/// reapply it triggers legitimately wants the SAME aptitude modifiers as before.</para>
///
/// <para><b>Tuning reconfiguration needs no bump either, and it cannot happen to a live instance.</b>
/// <c>_tuning</c> is an immutable constructor field; the only way to change it is to construct a NEW
/// <see cref="AptitudeSubsystem"/> (which starts with an empty memo by construction). Production never
/// even does this today — `CheatState.ActorHub` is built once via `??=` and never rebuilt — so this
/// path is structurally unreachable, not merely untested.</para>
/// </summary>
public sealed class AptitudeSubsystem : IActorStatSubsystem
{
    readonly Func<StatContext, AptitudeAllocation> _allocation;
    readonly AptitudeTuning _tuning;
    readonly PowerLadder _ladder;
    readonly IPowerIndexProvider _powerIndex;
    readonly DerivedStatRegistry _registry;

    // The memo. One slot per (Side, TypeId, Theta) -- see the type's own doc comment for why the
    // allocation that produced an entry is checked by reference on every read instead of living in the
    // dictionary key. Never static: a static memo would leak an allocation from one scoped test host
    // into another, the exact AptitudeTuningHub race this repo already fixed once (PointBudget.cs's
    // own doc comment).
    readonly Dictionary<(StatSide Side, int TypeId, int Theta), (AptitudeAllocation Allocation, IReadOnlyList<DerivedModifier> Mods)> _memo = new();

    public AptitudeSubsystem(
        AptitudeTuning tuning,
        PowerLadder ladder,
        IPowerIndexProvider? powerIndex = null,
        Func<StatContext, AptitudeAllocation>? allocation = null,
        DerivedStatRegistry? registry = null)
    {
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        _ladder = ladder ?? throw new ArgumentNullException(nameof(ladder));
        _powerIndex = powerIndex ?? new StubPowerIndexProvider();
        _allocation = allocation ?? (_ => AptitudeAllocation.Empty);
        _registry = registry ?? DerivedStatRegistry.CreateDefault();
    }

    public string SubsystemId => "rpg.aptitude";
    public int Order => 100; // same tier as RpgProgressionSubsystem; FlatSum/SumIncreased are both
                              // commutative, so relative order between the two carries no meaning today

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var allocation = _allocation(ctx) ?? AptitudeAllocation.Empty;
        var theta = _powerIndex.ActorIndex(ctx);
        var key = (ctx.Side, ctx.TypeId, theta);

        if (!_memo.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Allocation, allocation))
        {
            var resolved = AptitudeResolver.Resolve(allocation, _tuning, _ladder, theta, _registry).ToList();
            entry = (allocation, resolved);
            _memo[key] = entry;
        }

        foreach (var m in entry.Mods)
            mods.Add(m);
    }

    /// <summary>Forces every memoized entry to recompute on its next read, regardless of whether the
    /// allocation reference changed. Not needed for correctness — the memo is self-correcting by
    /// reference on every call — kept as an explicit escape hatch for a caller that wants to guarantee
    /// a fresh resolve without depending on object identity (e.g. a test).</summary>
    public void InvalidateMemo() => _memo.Clear();
}
