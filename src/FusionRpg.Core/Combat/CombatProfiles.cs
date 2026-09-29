namespace FusionRpg.Core.Combat;

/// <summary>
/// Per-host resolution profile (combat-unification map, owner decision 6). The formulas are
/// the SSOT and never fork per host — profiles carry only the explicitly profile-scoped
/// policy knobs. MinChipShareKPm: a LANDED hit deals at least ceil(share × base), min 1;
/// 0 disables (overlay byte-identity — enabling there is ask-first).
/// </summary>
public sealed record CombatProfile(long MinChipShareKPm)
{
    /// <summary>Live overlay — chip floor off, behavior byte-identical to pre-unification.</summary>
    public static readonly CombatProfile Overlay = new(0);

    /// <summary>Battle + sim — 5% chip floor closes the deterministic 0-damage stalemate class.</summary>
    public static readonly CombatProfile BattleSim = new(50);
}
