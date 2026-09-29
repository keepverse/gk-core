using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// aura-skill T5 (W1): the commander-scope allocation delegate <c>ActorHubBootstrap.CreateDefault</c>'s
/// <c>aptitudeAllocation</c> parameter needs, so the 486 aptitude-share edges resolve to something
/// other than zero on a live lawn. <c>CheatState.cs</c> builds the hub with no allocation today —
/// <c>AllocationStore</c>/<c>RpgStore.LoadAllocation</c> exist and are tested (point-economy) but have
/// zero production callers (class-system-todo.md's own named gap); this is the first one.
///
/// <para><b>Never reads on the hot path.</b> <see cref="Resolve"/> is what
/// <c>ActorHubBootstrap.CreateDefault</c> wires as <c>aptitudeAllocation</c> — <c>AptitudeSubsystem</c>
/// calls it once per stat resolve, many times a frame in production. It is a bare field read. The
/// actual read (an HTTP round trip to the Server in production) only happens inside
/// <see cref="Refresh"/>, called from the injector's own slow poll loop — the same cadence
/// <c>RefreshPvzStatsAsync</c> already polls at — never per stat resolve.</para>
/// </summary>
public sealed class CommanderAllocationSource
{
    readonly Func<AptitudeAllocation> _read;
    AptitudeAllocation _cached = AptitudeAllocation.Empty;

    public CommanderAllocationSource(Func<AptitudeAllocation> read) =>
        _read = read ?? throw new ArgumentNullException(nameof(read));

    /// <summary>Called on a poll tick, never on the hot path. There is no server-side revision number
    /// to gate on today (<c>AptitudeEndpoints.ProjectState</c> carries none) — the injector's own poll
    /// cadence IS the "one revision," so this unconditionally re-reads and replaces the cache each
    /// call, exactly once per call, never more.</summary>
    public void Refresh() => _cached = _read();

    /// <summary>The hot-path delegate for <c>aptitudeAllocation</c> — a bare field read, never a call
    /// into the reader. <paramref name="_"/> is unused: this source is scoped to the local injector's
    /// one active commander, not per-<see cref="StatContext.PlayerId"/> — the same single-player-local
    /// shape <c>CheatState.PvzStatsRevision</c> already uses.</summary>
    public AptitudeAllocation Resolve(StatContext _) => _cached;
}
