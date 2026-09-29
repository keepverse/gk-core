using FusionRpg.Core.Battle.Ai;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Stats.Aptitudes;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// species-build-todo.md T4.6 — spec-zomboss-adaptive.md, read in full this session. Persists exactly
/// what <see cref="ZombossPatternSelector"/> needs about the past (a pure function otherwise) plus the
/// per-encounter pattern log the delayed reveal reads. A partial <see cref="RpgStore"/> slice, same
/// one connection/lock/`EnsureHotSchema`/`Reset()` pipeline every other feature already joins.
/// </summary>
public sealed record ZombossSelection(string PatternId, int EncounterIndex);

public sealed partial class RpgStore
{
    void EnsureZombossAdaptiveSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_zomboss_state (
              player_id                       INTEGER PRIMARY KEY,
              active_pattern_id               TEXT    NOT NULL,
              last_level                      INTEGER NOT NULL,
              encounters_since_last_repattern INTEGER NOT NULL,
              win_streak                      INTEGER NOT NULL,
              encounter_index                 INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS rpg_zomboss_pattern_log (
              player_id       INTEGER NOT NULL,
              encounter_index INTEGER NOT NULL,
              pattern_id      TEXT    NOT NULL,
              PRIMARY KEY (player_id, encounter_index)
            );
            """);
    }

    /// <summary>
    /// One Zomboss encounter's worth of adaptation, resolved BEFORE the battle runs (spec's own
    /// determinism rule — the caller bakes the returned <see cref="ZombossSelection.PatternId"/> into
    /// <c>BattleSetup.ZombossPatternId</c>, never re-rolling during resolution). A brand-new player (no
    /// stored state) is seeded with <c>LastLevel = level - 1</c> so the very first call reads as a
    /// genuine level-up trigger through <see cref="ZombossPatternSelector"/>'s OWN already-tested
    /// unbiased weighted pool — real seed-driven variety from encounter one, not a hard-coded starting
    /// pattern reached through a code path the selector's own tests never exercise.
    /// </summary>
    public ZombossSelection SelectZombossPattern(long playerId, int level, ulong seed, ZombossAdaptiveTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            var state = ReadZombossStateUnlocked(db, playerId) ?? new ZombossStateRow(
                ZombossPatterns.All[0], LastLevel: level - 1, EncountersSinceLastRepattern: tuning.RepatternCooldownEncounters,
                WinStreak: 0, EncounterIndex: 0);

            // Dave's own commander allocation (the directory's own established "same scope enum, a
            // sibling key" convention) is what the Zomboss reads to find a posture worth countering —
            // never the Zomboss's own allocation, which would be circular.
            var humanCommander = CommanderDirectoryHub.Current.DefaultFor(EmpireId.Dave);
            var playerAllocation = LoadAllocationUnlocked(db, AllocationScope.Commander,
                CommanderDirectoryHub.Current.AllocationScopeKey(humanCommander, playerId));
            var history = new ZombossHistory(
                state.ActivePatternId, state.LastLevel, state.EncountersSinceLastRepattern, state.WinStreak,
                DominantPosture.Of(playerAllocation));

            var chosen = ZombossPatternSelector.SelectNext(history, level, seed, tuning);
            var repatterned = !string.Equals(chosen, state.ActivePatternId, StringComparison.Ordinal);
            var encounterIndex = state.EncounterIndex + 1;

            UpsertZombossStateUnlocked(db, playerId, chosen, level,
                repatterned ? 0 : state.EncountersSinceLastRepattern, state.WinStreak, encounterIndex);

            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT OR IGNORE INTO rpg_zomboss_pattern_log(player_id, encounter_index, pattern_id)
                    VALUES ($p, $idx, $pat);
                    """;
                cmd.Parameters.AddWithValue("$p", playerId);
                cmd.Parameters.AddWithValue("$idx", encounterIndex);
                cmd.Parameters.AddWithValue("$pat", chosen);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            return new ZombossSelection(chosen, encounterIndex);
        }
    }

    /// <summary>Advances the encounter/win-streak counters after a battle actually resolves — never
    /// called on a replay (the outcome was already recorded the first time this match resolved).
    /// A no-op if no state exists yet (defensive: nothing was ever selected for this player).</summary>
    public void RecordZombossEncounterOutcome(long playerId, bool playerWon)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var state = ReadZombossStateUnlocked(db, playerId);
            if (state is null) { tx.Commit(); return; }

            UpsertZombossStateUnlocked(db, playerId, state.ActivePatternId, state.LastLevel,
                state.EncountersSinceLastRepattern + 1, playerWon ? state.WinStreak + 1 : 0, state.EncounterIndex);
            tx.Commit();
        }
    }

    /// <summary>Decision 4's delayed reveal: the pattern used <paramref name="revealDelayEncounters"/>
    /// encounters before <paramref name="thisEncounterIndex"/> — null when not enough history exists
    /// yet (the early encounters of a fresh save), which the caller treats as "nothing to reveal yet,"
    /// never a silent zero/placeholder pattern id.</summary>
    public string? GetRevealedZombossPatternId(long playerId, int thisEncounterIndex, int revealDelayEncounters)
    {
        var targetIndex = thisEncounterIndex - revealDelayEncounters;
        if (targetIndex < 1) return null;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT pattern_id FROM rpg_zomboss_pattern_log WHERE player_id=$p AND encounter_index=$idx;";
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$idx", targetIndex);
            return cmd.ExecuteScalar() as string;
        }
    }

    sealed record ZombossStateRow(string ActivePatternId, int LastLevel, int EncountersSinceLastRepattern, int WinStreak, int EncounterIndex);

    ZombossStateRow? ReadZombossStateUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT active_pattern_id, last_level, encounters_since_last_repattern, win_streak, encounter_index
            FROM rpg_zomboss_state WHERE player_id=$p;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new ZombossStateRow(r.GetString(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
    }

    void UpsertZombossStateUnlocked(SqliteConnection db, long playerId, string activePatternId, int lastLevel,
        int encountersSinceLastRepattern, int winStreak, int encounterIndex)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_zomboss_state(player_id, active_pattern_id, last_level, encounters_since_last_repattern, win_streak, encounter_index)
            VALUES ($p, $pat, $lvl, $esr, $ws, $idx)
            ON CONFLICT(player_id) DO UPDATE SET
              active_pattern_id = $pat, last_level = $lvl, encounters_since_last_repattern = $esr,
              win_streak = $ws, encounter_index = $idx;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$pat", activePatternId);
        cmd.Parameters.AddWithValue("$lvl", lastLevel);
        cmd.Parameters.AddWithValue("$esr", encountersSinceLastRepattern);
        cmd.Parameters.AddWithValue("$ws", winStreak);
        cmd.Parameters.AddWithValue("$idx", encounterIndex);
        cmd.ExecuteNonQuery();
    }
}
