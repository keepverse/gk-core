using FusionRpg.Contracts;
using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Match.Ai;

/// <summary>
/// zomboss-deploy-ai T3.1 (spec-zomboss-deploy-ai.md Correction 2/§Assumptions 2) — one visible unit on
/// the lawn, from the observer's own point of view. <see cref="Relation"/> is resolved through
/// whichever <see cref="Battle.IOwnSideOracle"/> the view was built from (production: an ownership
/// oracle mirroring <see cref="Battle.SpecimenOwnershipOracle"/>'s own "which player deployed it, not
/// which mechanical side it's on" rule) — never the unit's raw on-board side directly, so a
/// hypnotized/side-swapped unique creature (`CreatureDeployMode.HypnoAlly`, T1.4) keeps resolving to its real
/// deploying player.
/// </summary>
public interface ILawnUnitView
{
    string Ptr { get; }
    RelationKind Relation { get; }
    long HpCurrent { get; }
    long HpMax { get; }
}

/// <summary>
/// zomboss-deploy-ai T3.1 — the ONLY thing the Zomboss scorer (T3.3) may read. Mirrors
/// <see cref="FusionRpg.Core.World.Intel.IWorldView"/>'s own enforcement shape (spec-ai-commander.md Correction 3): a
/// genuinely separate type with zero members of type <c>MatchRuntime</c>/<c>Board</c>/
/// <see cref="MatchSnapshot"/> and no implicit conversion from any of them, so a policy call site has
/// no member, cast, or conversion path back to full board access — a compile-time impossibility,
/// reinforced by <c>ZombossDeployAiGuardTests.Nothing_under_Match_Ai_may_read_the_board_itself</c>
/// scanning for those literal type names under this same folder (mirrors
/// <c>WorldDeterminismGuardTests.Nothing_under_World_Ai_may_read_the_world_itself</c>).
///
/// <para>Per Assumption 2, this is deliberately narrow: wave, visible units, HP — nothing else (no
/// element matchups, no status list) until a real policy build (T3.3) proves it needs more.</para>
/// </summary>
public interface ILawnBoardView
{
    int WaveNumber { get; }
    int MaxWave { get; }
    IReadOnlyList<ILawnUnitView> VisibleUnits { get; }
}

/// <summary>Plain, Unity-free snapshot pair implementing the two views above — the shape a real
/// injector-side builder (deferred: no live board-HP/wave feed exists yet, see the map's own
/// "Deliberately deferred" Zomboss-roster gap) or a test's synthetic fixture both construct.</summary>
public sealed record LawnUnitSnapshot(string Ptr, RelationKind Relation, long HpCurrent, long HpMax) : ILawnUnitView;

public sealed record LawnBoardSnapshot(
    int WaveNumber, int MaxWave, IReadOnlyList<ILawnUnitView> VisibleUnits) : ILawnBoardView
{
    public static readonly LawnBoardSnapshot Empty = new(0, 0, Array.Empty<ILawnUnitView>());
}

/// <summary>
/// Builds one <see cref="LawnUnitSnapshot"/> by resolving <paramref name="ptr"/>'s relation through
/// <paramref name="oracle"/> — the one, enforced seam <see cref="ILawnBoardView"/> consumers use instead
/// of ever reading a unit's raw on-board side. <see cref="IOwnSideOracle.RelationOf"/> returning
/// null (no owner registered — a raw vanilla PvZ unit, not a unique creature) resolves to
/// <see cref="RelationKind.Enemy"/>: from Zomboss's own reading of the board, an un-owned unit is a
/// vanilla plant/zombie the game itself spawned, which is exactly as threatening/irrelevant to the
/// scorer as one that resolved Enemy outright — never silently dropped, never mis-read as Self/Ally.
/// </summary>
public static class LawnUnitViewFactory
{
    public static LawnUnitSnapshot Build(string ptr, long hpCurrent, long hpMax, IOwnSideOracle oracle)
    {
        if (oracle is null) throw new ArgumentNullException(nameof(oracle));
        var relation = oracle.RelationOf(ptr) ?? RelationKind.Enemy;
        return new LawnUnitSnapshot(ptr, relation, hpCurrent, hpMax);
    }
}
