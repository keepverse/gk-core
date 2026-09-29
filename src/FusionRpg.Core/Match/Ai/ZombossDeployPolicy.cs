using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Match.Ai;

/// <summary>One decision from one <see cref="ZombossDeployPolicy.Decide"/> call — never a batch, matching
/// <see cref="LawnDeployEventResult"/>'s own "yes/no + which" shape for the analogous player-side
/// decision.</summary>
public sealed record ZombossDeployDecision(bool Deploys, string? SpeciesId, string Reason)
{
    public static ZombossDeployDecision Decline(string reason) => new(false, null, reason);
}

/// <summary>
/// zomboss-deploy-ai T3.3 (spec-zomboss-deploy-ai.md Assumption 2/3) — a deterministic scorer over
/// <see cref="ILawnBoardView"/> (T3.1) and the T3.2 roster, never `MatchRuntime`/`Board` directly (the
/// type boundary itself makes that impossible — see
/// `ILawnBoardViewTests.Nothing_under_Match_Ai_may_read_the_board_itself`). Every weight is an
/// explicit parameter from <see cref="ZombossScorerTuning"/>, never a bare literal (this repo's own
/// tunables-ssot.md rule) — mirrors `Delve.Events.AmbushDraw`'s own "every tunable an explicit
/// parameter, never a static-hub read internally" shape exactly.
///
/// <para>Randomness derives from `SeededRng.DeriveStream(matchSeed, "zomboss-deploy-ai:{caseId}")` —
/// the exact `(seed, label)` convention this repo already uses for every other seeded AI/trigger
/// decision (`LawnDeployEventEvaluator`, `spec-ai-commander.md`'s own `DeriveStream(worldSeed,
/// "ai:{factionId}:{turn}")`) — never `System.Random`, never wall-clock.</para>
///
/// <para><b>Two-stage decision, not one blended score</b>: first, board state gates WHETHER Zomboss
/// considers deploying at all (own-unit cap, minimum enemy presence) plus a per-mille roll — the exact
/// "roll a chance, gated by a board-state condition" shape `LawnDeployEventEvaluator` already
/// established for the player-side trigger, reused rather than inventing a second roll shape for the
/// zombie side. Second, WHICH candidate wins is ranked by `BaseRarity` (descending) — the strongest
/// available reinforcement — tie-broken by `SpeciesId` ordinal, matching `CreatureRecipeCatalog`'s own
/// established tie-break convention for an otherwise-ambiguous species choice. This is deliberately the
/// simplest scorer that satisfies Assumption 2 ("a deterministic scorer... not a learned model or a
/// general rules engine") — a genuinely board-state-sensitive ranking (which candidate wins changes
/// with board state, not just whether one is picked at all) is a real, reversible follow-up once a live
/// roster exists to tune against, not invented here against zero play data.</para>
/// </summary>
public static class ZombossDeployPolicy
{
    public static ZombossDeployDecision Decide(
        ILawnBoardView board,
        IReadOnlyList<string> candidateSpeciesIds,
        Func<string, CreatureRarity> rarityOf,
        ZombossScorerTuning tuning,
        ulong matchSeed,
        string caseId)
    {
        if (board is null) throw new ArgumentNullException(nameof(board));
        if (candidateSpeciesIds is null) throw new ArgumentNullException(nameof(candidateSpeciesIds));
        if (rarityOf is null) throw new ArgumentNullException(nameof(rarityOf));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (string.IsNullOrEmpty(caseId)) throw new ArgumentException("caseId must be non-empty", nameof(caseId));

        if (candidateSpeciesIds.Count == 0)
            return ZombossDeployDecision.Decline("no eligible creature in the candidate pool");

        var ownCount = 0;
        var enemyCount = 0;
        foreach (var unit in board.VisibleUnits)
        {
            if (unit.Relation == RelationKind.Ally) ownCount++;
            else if (unit.Relation == RelationKind.Enemy) enemyCount++;
        }

        if (ownCount >= tuning.MaxConcurrentOwnUnits)
            return ZombossDeployDecision.Decline(
                $"already at {ownCount} concurrent own units (cap {tuning.MaxConcurrentOwnUnits})");
        if (enemyCount < tuning.MinEnemyUnitsToConsiderDeploy)
            return ZombossDeployDecision.Decline(
                $"only {enemyCount} enemy units visible (needs >= {tuning.MinEnemyUnitsToConsiderDeploy})");

        var roll = SeededRng.DeriveStream(matchSeed, $"zomboss-deploy-ai:{caseId}").NextPerMille();
        if (roll >= tuning.FireChanceMilli)
            return ZombossDeployDecision.Decline($"roll {roll} missed (needs <{tuning.FireChanceMilli})");

        var chosen = candidateSpeciesIds
            .OrderByDescending(id => (int)rarityOf(id))
            .ThenBy(id => id, StringComparer.Ordinal)
            .First();

        return new ZombossDeployDecision(true, chosen, $"roll {roll} hit — picked highest-rarity candidate");
    }
}
