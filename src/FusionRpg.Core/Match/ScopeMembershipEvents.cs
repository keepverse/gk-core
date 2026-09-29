namespace FusionRpg.Core.Match;

/// <summary>
/// The three transitions a buff/debuff scope's own-side WHO-value needs to react to
/// (buff-debuff-scope-ideal.md §4.1). Nothing else — this is not a general event bus.
/// </summary>
public enum ScopeMembershipTransition
{
    /// <summary>`UniqueBindingPhase.PendingSpawn -> Bound` (`MatchUniqueBindingsFacet.TryBindOnSpawn`).</summary>
    Bound,

    /// <summary>`UniqueBindingPhase.* -> Cleared` (`MatchUniqueBindingsFacet`'s `ClearInstance`).</summary>
    Cleared,

    /// <summary>
    /// `zombie.hypno`, either direction — T6's own new dispatch case in `MatchRuntime.cs`. Not raised
    /// from this file; `MindControlledNow` is the only field meaningful for this transition.
    /// </summary>
    MindControlToggled,

    /// <summary>
    /// base-defense `siege-obstacles`: an actor entered a board cell. The program's ONE reviewed
    /// vocabulary change, spent on a real mechanic — a Mine on that cell triggers. `siege-cover`
    /// originally introduced this pair and released it (decision 35 replaced terrain cover with
    /// per-shot shooting math, needing no membership change); this module is the one real consumer.
    /// Existing consumers of this enum (e.g. <see cref="Battle.BattlefieldOwnSideReactor"/>) have no
    /// `case` for it and no `default`, so it falls through harmlessly — verified, not assumed.
    /// </summary>
    CellEntered,

    /// <summary>Left a board cell. Paired with <see cref="CellEntered"/> — emitted on move, death and
    /// withdrawal, so an entry can never be left dangling (the leak this pairing exists to prevent).</summary>
    CellExited,
}

/// <summary>
/// Deliberately a struct, no allocation on the hot path — matches `UniqueBinding.Clone()`'s own
/// allocation discipline and this program's zero-allocation bar for anything in a per-tick path.
/// </summary>
public readonly record struct ScopeMembershipEvent(
    string Ptr,
    ScopeMembershipTransition Transition,
    bool? MindControlledNow = null);
