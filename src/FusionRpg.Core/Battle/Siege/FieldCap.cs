namespace FusionRpg.Core.Battle.Siege;

/// <summary>Stable reject reason codes (`Match.CapPolicy`'s own precedent, `GateReasons`) — a
/// rejection a player can be told the reason for is the difference between "the game refused" and "the
/// game is broken."</summary>
public static class SiegeRejectReasons
{
    public const string FieldCapSide = "siege.cap.side";
    public const string FieldCapInvalidSide = "siege.cap.invalid-side";
    public const string CoreIsAnArena = "siege.core-is-an-arena";
}

/// <summary>base-defense `siege-objective` §2, decision 5: how many units one side may have standing
/// on the board at once.</summary>
public readonly struct SiegeGateResult
{
    public SiegeGateResult(bool ok, string reason = "")
    {
        Ok = ok;
        Reason = reason ?? "";
    }

    public bool Ok { get; }
    public string Reason { get; }

    public static SiegeGateResult Allowed() => new(true, "");
    public static SiegeGateResult Reject(string reason) => new(false, reason);
}

/// <summary>
/// The field cap: an authored integer per base tier, IDENTICAL for both sides, and NEVER derived from
/// the board's own empty-cell count. §5.9's degenerate strategy: if the cap were <c>f(emptyCells)</c>
/// and shared, the defender could shrink the attacker's cap by building — wall off thirty of forty
/// cells and the attacker deploys two units at a time into a board full of towers. That is not a hard
/// defense to beat, it is a defense that cannot be attacked, which is the same thing and worse.
///
/// <para><b>A structural per-runtime cap, not a progression ceiling</b> (AGENTS.md exempts per-frame
/// and runtime caps): it bounds how much can exist at one moment, never how strong anything becomes.
/// `MaxLivingPlants = 50` is the named precedent for this exact exemption shape.</para>
///
/// <para><b>Reuses `Match.CapPolicy`'s PATTERN, not its type</b> (spec's own explicit boundary): the
/// same living-count-gate shape, the same `-1` unlimited sentinel, the same stable-reason-code
/// discipline — but its own type here, so this module carries no dependency on `Match`'s PvZ-sided
/// vocabulary (`plant`/`zombie`/`bullet`).</para>
/// </summary>
public sealed record FieldCapConfig
{
    /// <summary>Per side. -1 = unlimited, matching `CapPolicy`'s own sentinel exactly.</summary>
    public int MaxLivingPerSide { get; init; } = -1;
}

public static class FieldCap
{
    /// <summary>Structures never count against the field cap — they are not deployed units, and
    /// counting them would recreate the derived-from-cells degeneracy through the back door (building
    /// a wall would shrink your own army). The caller passes only the ANIMATE living count on the
    /// side; a garrisoning unit still costs a slot (§5.13), the structure it occupies never does.</summary>
    public static SiegeGateResult TryAdmit(string side, int livingAnimateOnSide, FieldCapConfig config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(side)) return SiegeGateResult.Reject(SiegeRejectReasons.FieldCapInvalidSide);
        if (config.MaxLivingPerSide < 0) return SiegeGateResult.Allowed();
        return livingAnimateOnSide >= config.MaxLivingPerSide
            ? SiegeGateResult.Reject(SiegeRejectReasons.FieldCapSide)
            : SiegeGateResult.Allowed();
    }
}
