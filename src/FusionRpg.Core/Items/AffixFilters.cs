using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items;

/// <summary>
/// Frame, side and runtime legality (item-ideal.md, `affix-legality` module 8) — three axes
/// `item_role_family`'s (role, frame) shape does not carry, checked at bind/import time instead.
/// Runtime support is always read from <see cref="AtomKindRegistry"/>, never quoted from a document
/// (§4.9's own lesson: the document said `stat.derived` was quarantined a full week after the
/// registry stopped agreeing).
/// </summary>
public static class AffixFilters
{
    /// <summary>A family's own `frames` list must contain the base type's frame.</summary>
    public static bool FrameAllows(IReadOnlyCollection<string> familyFrames, string baseTypeFrame) =>
        familyFrames.Contains(baseTypeFrame, StringComparer.Ordinal);

    /// <summary>
    /// `side` is the PvZ battle side (zombie/plant), a different axis from item frame
    /// (humanoid/plant body). `"both"` always passes.
    /// </summary>
    public static bool SideAllows(string familySide, string actorBattleSide) =>
        familySide == "both" || string.Equals(familySide, actorBattleSide, StringComparison.Ordinal);

    /// <summary>
    /// True when <paramref name="kindId"/> can execute in <paramref name="target"/> at all
    /// (anything but <see cref="RuntimeState.None"/>).
    ///
    /// <para>⚠ `stat.derived` was Lawn/Battle/Sim = Full/Full/<b>None</b> when the D6 quarantine
    /// lifted (2026-09-02), so Sim was refused here. The effect-atom program moved the Sim cell to
    /// <b>Partial</b> on 2026-09-06 (mechanism-wiring E5 — `ActorDerivedLookup`'s contribution fold
    /// gave it a real, if Flat/Increased-only, consumer), so this predicate now ADMITS a
    /// `stat.derived` affix for a Sim target. That is the intended behaviour of reading the registry
    /// live rather than quoting a document — recorded here only because the old comment named a
    /// matrix that no longer exists. Tracked by
    /// `IlvlTierLadderTests.A_stat_derived_affix_is_now_allowed_for_a_sim_target_via_the_partial_fold`.</para>
    /// </summary>
    public static bool RuntimeAllows(string kindId, RuntimeId target)
    {
        var kind = AtomKindRegistry.Get(kindId);
        return kind is not null && kind.SupportIn(target) != RuntimeState.None;
    }

    /// <summary>
    /// G8 + D14: `warding`/`resilience` are legal only at the commander `standard` scope, which is out
    /// of scope for v1 — so in practice they are legal nowhere and must be refused at import.
    /// </summary>
    public static readonly IReadOnlyCollection<string> MatchScopeOnlyFamilies = new[]
    {
        "atom.warding", "atom.resilience",
    };

    public static bool IsMatchScopeOnly(string familyId) => MatchScopeOnlyFamilies.Contains(familyId, StringComparer.Ordinal);
}
