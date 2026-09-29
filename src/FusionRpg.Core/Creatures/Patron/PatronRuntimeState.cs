namespace FusionRpg.Core.Creatures.Patron;

/// <summary>
/// Process-local patron designation cache — the injector fills it from the server's `patron.aura`
/// command (and the server/SIM fill it directly). Read by <see cref="Effects.Plugins.PatronSecondaryPlugin"/>
/// at match start to decide WHETHER a player has any patron designated at all — a cheap, Core-layer
/// signal that avoids a store round trip from a Secondary plugin (Core cannot see `RpgStore`).
///
/// <para><b>No longer carries a combat magnitude</b> (patron-absorption, `seed-to-concrete` T6.2,
/// 2026-09-06): the aura's power/defense values now reach combat as the `fx.patron_aura` grant's own
/// compiled atom action rows (`gk-data/packs/fusion/data/seed/atoms/patron-aura.json`, resolved fresh per push via
/// <c>AtomPushService.BuildExternalRefs</c> → <c>PatronEndpoints.Compute</c>), read by the same
/// <c>GrantedDerivedAtomReader</c> path every other match-scoped grant uses — never a bespoke
/// compose-time overlay. The old `MatchAura`/`BeginMatch`/`EndMatch` freeze existed only to hand that
/// overlay a snapshot; with the overlay deleted (`PatronAuraOverlay.cs`), freezing has nothing left to
/// serve, so it was removed rather than kept as an unread mirror of the grant.</para>
/// </summary>
public static class PatronRuntimeState
{
    static readonly object Gate = new();
    static PatronAura? _aura;
    static long _playerId;

    public static void Set(long playerId, PatronAura? aura)
    {
        lock (Gate)
        {
            _playerId = playerId;
            _aura = aura;
        }
    }

    public static bool TryGet(long playerId, out PatronAura aura)
    {
        lock (Gate)
        {
            // playerId 0 = caller has no player context (injector before hello) — match any.
            if (_aura != null && (playerId == 0 || _playerId == 0 || _playerId == playerId))
            {
                aura = _aura;
                return true;
            }
        }

        aura = null!;
        return false;
    }
}
