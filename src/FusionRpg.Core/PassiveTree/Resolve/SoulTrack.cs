namespace FusionRpg.Core.PassiveTree.Resolve;

/// <summary>
/// D3's soul track, end to end (spec-tree-binder.md §5.1-§5.4; spec-tree-resolve.md §6.2;
/// spec-tree-catalog.md §2.3). A soul level OFFSETS `Θ`, it never scales `kMicro` — the catalog's
/// stored coefficient stays byte-identical at soul level 0 and 50; only the `Θ_node` fed into
/// `P(Θ)` moves. This is the entire second progression track, computed at the read site and never
/// persisted (there is no `theta_node` column anywhere — `RpgStore.PassiveTree.cs` stores only
/// `soul_level`, an input, exactly like every other stored quantity in this program).
/// </summary>
public static class SoulTrack
{
    /// <summary>`Θ_node = Θ_actor + (thetaPerSoulLevelMilli · soulLevel) / 1000` — the per-mille divide
    /// happens exactly ONCE, here, before `PowerLadder.Value` (P) is ever called. This is the LAST
    /// division before the ladder read (docs/architecture/numeric-types.md rule 4: "divide by 1000 last, exactly once") — legal
    /// here specifically because nothing downstream divides again; `PowerLadder.Value` takes the
    /// resulting whole `Θ_node` and never re-scales it.</summary>
    public static long ThetaNode(long thetaActor, long soulLevel, long thetaPerSoulLevelMilli)
    {
        if (soulLevel < 0)
            throw new ArgumentOutOfRangeException(nameof(soulLevel), soulLevel, "soul level must be >= 0");
        if (thetaPerSoulLevelMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(thetaPerSoulLevelMilli), thetaPerSoulLevelMilli,
                "thetaPerSoulLevelMilli must be >= 0");

        // Widen before multiplying (docs/architecture/numeric-types.md rule 3) -- soulLevel and thetaPerSoulLevelMilli are both
        // long already, so the product is computed at full width; checked so a runaway soul level
        // throws rather than silently wrapping into a smaller (or negative) Theta.
        checked
        {
            return thetaActor + thetaPerSoulLevelMilli * soulLevel / 1000;
        }
    }
}
