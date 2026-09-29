using FusionRpg.Contracts;
using FusionRpg.Core.Combat;

namespace FusionRpg.Core.Battle;

/// <summary>
/// aura-skill T21a: the real, production `IOwnSideOracle` for the MECHANICAL case — plant vs. zombie,
/// adjusted for mind control — which is everything this program's own two commanders (Dave/plants,
/// Zomboss/zombies) ever need. `AlwaysRelationOracle` (`DebugScopeRuntime.cs`) is a debug-only stub
/// that answers the same relation for every ptr; this is its real replacement.
///
/// <para><b>Deliberately NOT the specimen-ownership bridge.</b> `BattlefieldOwnSideReactor`'s own doc
/// comment names a SECOND, harder half of this problem: when a creature SPECIMEN exists, ownership needs
/// a Cold-plane `player_id` read that does not exist anywhere in Core today
/// (buff-debuff-scope-ideal.md §4.1/§2.3, confirmed by direct search — no such read path exists). That
/// half is `buff-debuff-scope-todo.md:249-250`'s own named, separate, unscoped gap — a larger,
/// standalone bridge for the creature program, not something this task silently half-builds. This oracle
/// answers correctly for every entity that IS mechanical (every plant and zombie on a lawn run,
/// mind-controlled or not) and is honest about not answering the specimen-ownership question at all.
/// </para>
/// </summary>
public sealed class MechanicalOwnSideOracle : IOwnSideOracle
{
    readonly string _mySide;
    readonly Func<string, BoardEntitySnap?> _resolve;

    /// <summary>
    /// <paramref name="mySide"/> is the reactor's own perspective — `"plant"` for Dave's own-side
    /// reactor, `"zombie"` for Zomboss's. <paramref name="resolve"/> is a board lookup
    /// (`BoardSnapshot.FindPtr`, in production) — injected rather than a hard dependency, so a test
    /// never needs a real board.
    /// </summary>
    public MechanicalOwnSideOracle(string mySide, Func<string, BoardEntitySnap?> resolve)
    {
        if (string.IsNullOrWhiteSpace(mySide))
            throw new ArgumentException("mySide must be a non-empty side id ('plant' or 'zombie')", nameof(mySide));
        _mySide = mySide;
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
    }

    /// <summary>Null exactly when the board genuinely has no entry for this ptr (already dead and
    /// pruned, or never tracked) — <see cref="IOwnSideOracle.RelationOf"/>'s own contract.</summary>
    public RelationKind? RelationOf(string ptr)
    {
        var entity = _resolve(ptr);
        if (entity is null) return null;

        // Mind control flips which side an entity fights FOR, not which side it visually belongs to
        // -- the reactor cares about the former.
        var effectiveSide = entity.MindControlled ? Opposite(entity.Side) : entity.Side;
        return string.Equals(effectiveSide, _mySide, StringComparison.OrdinalIgnoreCase)
            ? RelationKind.Ally
            : RelationKind.Enemy;
    }

    static string Opposite(string side) => side switch
    {
        "plant" => "zombie",
        "zombie" => "plant",
        _ => side, // an unrecognized side id passes through unchanged rather than guessing
    };
}
