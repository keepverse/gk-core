using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Narrative.Hosts;

/// <summary>
/// The ONE place a <see cref="ParentWorldTerms"/> is built from live state
/// (spec-host-content-theta.md §2, npc-story-events NR2.18). Before this file there was no
/// production producer at all — every construction in the repository was a test literal — so the
/// Delve and every narrative host would each have grown their own reading of the parent world.
/// The Delve's own wiring (<c>DelveParentTerms</c>, <c>delve-live-start</c>) chooses WHICH world to
/// read and delegates here; it never builds the terms itself
/// (spec-host-content-theta.md §Standards audit #1).
///
/// <para>Three of the four Θ_content inputs have no live source yet, and this function names the
/// owner of each gap rather than inventing a value:</para>
/// <list type="bullet">
/// <item><b>WorldTier</b> — owner: the world-map program. <c>WorldState</c> carries no tier field
/// (<c>gk-core/src/FusionRpg.Core/World/WorldState.cs</c>, the sector/era rows).</item>
/// <item><b>ZombossLevel</b> — owner: the world-map program. No field on <c>WorldState</c> either;
/// the level lives in the PvZ run, which the RPG never reads as current state (DESIGN-GATE §2.1).</item>
/// <item><b>RealmsAdvanced</b> — owner: the power/empire program. No column exists, and the power
/// program's own server-side reader hardcodes the same zero
/// (<c>gk-core/src/FusionRpg.Server/Power/ServerPowerIndexProvider.cs:16-18</c>).</item>
/// </list>
///
/// <para>Zero is absence, not corruption — the reading <see cref="Power.PowerIndexComposer"/>
/// already applies to a missing progression row (<c>ClampNonNegative</c>), which is why this
/// returns terms rather than throwing. When a term gains a real source, this function is the only
/// edit: the scan in <c>HostContentThetaTests</c> refuses a second construction site.</para>
/// </summary>
public static class ParentWorldTermsSource
{
    /// <summary>
    /// The parent world's terms for <paramref name="world"/>. <paramref name="world"/> is nullable
    /// and, today, unread: it is the input the three missing sources will come from, so the call
    /// shape does not have to change when they land.
    /// </summary>
    public static ParentWorldTerms For(WorldState? world)
    {
        _ = world;
        return new ParentWorldTerms(WorldTier: 0, ZombossLevel: 0, RealmsAdvanced: 0);
    }
}
