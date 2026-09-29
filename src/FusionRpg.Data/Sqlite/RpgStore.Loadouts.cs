using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Effects.Atoms;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// <c>rpg_actor_loadout</c> (spec-loadout.md §1, T21): which of an actor's held skills are equipped
/// this run. Reuses <c>OwnerScope</c>'s seven scopes (T1's `rpg_action_grant` precedent) rather than
/// inventing an eighth.
///
/// <para><b>"Held" and "mid-run" stay caller-injected, same as <see cref="LoadoutSet.Validate"/>
/// itself demands.</b> Neither has a real backing table yet: the unlock ladder (T19/T20) has no
/// persistence of its own (<c>RpgStore.ActionUnlocks.cs</c> from spec-unlock-ladder.md's Structure
/// section is not built), and an owner scope's run-phase cross-reads into `rpg_unique_actors` is a
/// separate wiring concern this table does not own. <c>kindOf</c> IS wired for real here, since
/// `rpg_action.kind` already exists and is exactly what it is for.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsureLoadoutSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS rpg_actor_loadout (
              owner_kind TEXT NOT NULL,
              owner_key TEXT NOT NULL DEFAULT '',
              ordinal INTEGER NOT NULL,
              action_id TEXT NOT NULL,
              PRIMARY KEY (owner_kind, owner_key, ordinal)
            );
            """);
    }

    /// <summary>
    /// Validates the WHOLE proposed set before writing anything — a rejection leaves every existing
    /// row untouched (spec §2: "Rejects, never truncates"). On success, replaces the owner's entire
    /// loadout inside one transaction so a concurrent reader never observes a half-written set.
    /// </summary>
    public LoadoutValidation SetLoadout(
        OwnerScope owner, IReadOnlyList<string> actionIds, Func<string, bool> isHeld, Func<bool> isMidRun)
    {
        ActionKind KindOf(string actionId) => GetAction(actionId)?.Kind ?? ActionKind.Skill;

        var validation = LoadoutSet.Validate(actionIds, isHeld, KindOf, isMidRun);
        if (!validation.Ok) return validation;

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            using (var del = db.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM rpg_actor_loadout WHERE owner_kind = $kind AND owner_key = $key;";
                del.Parameters.AddWithValue("$kind", OwnerScope.Name(owner.Kind));
                del.Parameters.AddWithValue("$key", owner.Key ?? "");
                del.ExecuteNonQuery();
            }

            for (var i = 0; i < actionIds.Count; i++)
            {
                using var ins = db.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO rpg_actor_loadout (owner_kind, owner_key, ordinal, action_id)
                    VALUES ($kind, $key, $ord, $action);
                    """;
                ins.Parameters.AddWithValue("$kind", OwnerScope.Name(owner.Kind));
                ins.Parameters.AddWithValue("$key", owner.Key ?? "");
                ins.Parameters.AddWithValue("$ord", i);
                ins.Parameters.AddWithValue("$action", actionIds[i]);
                ins.ExecuteNonQuery();
            }

            tx.Commit();
        }

        return validation;
    }

    /// <summary>Ordinal order. <c>null</c> (never an empty list) when no row exists at all — "no
    /// loadout row means auto-equip" (T22), the same shape as T18's "no run row means a run of one."
    /// </summary>
    public IReadOnlyList<string>? GetLoadout(OwnerScope owner)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT action_id FROM rpg_actor_loadout
                WHERE owner_kind = $kind AND owner_key = $key
                ORDER BY ordinal ASC;
                """;
            cmd.Parameters.AddWithValue("$kind", OwnerScope.Name(owner.Kind));
            cmd.Parameters.AddWithValue("$key", owner.Key ?? "");
            using var r = cmd.ExecuteReader();

            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list.Count == 0 ? null : list;
        }
    }

    /// <summary>
    /// T22 (spec-loadout.md §3): the actor's real loadout if one was ever set, otherwise auto-equip
    /// from <paramref name="heldSkillCandidates"/> — "every actor with no loadout row auto-equips,"
    /// so a Zomboss pattern or a generated creature never fights with three basics just because nobody
    /// chose for it. Never persists the auto-equip result: it is recomputed from whatever is
    /// currently held every time this is called, so a later real unlock or discard is reflected
    /// immediately with no stale cached loadout to invalidate.
    /// </summary>
    public IReadOnlyList<string> GetLoadoutOrAutoEquip(OwnerScope owner, IReadOnlyList<AutoEquipCandidate> heldSkillCandidates)
    {
        var existing = GetLoadout(owner);
        if (existing != null) return existing;

        return AutoEquip.Select(heldSkillCandidates, RungPolicy.Table);
    }
}
