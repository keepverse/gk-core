using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

// empire-development Task 3.1 (`wonder-build-flow` Data-side, the final integration;
// `docs/architecture/loam-relics-and-wonders/spec-wonder-build-flow.md` §Design 4, §Design 7).
//
// Two functions, both called from `CommitWorldTurn` inside the ONE transaction it already opens
// (`RpgStore.WorldTurns.cs`): `ValidateWonderRelicCommandsUnlocked` immediately before
// `TurnEngine.Step`, `SpendWonderRelicsUnlocked` immediately after `DiffWorldGraphUnlocked`.
//
// Reads/deletes `rpg_world_entity_cargo` / `rpg_world_sector_storage` rows DIRECTLY — never via
// `cargo-transfer`'s own verbs (`DepositUnlocked`/`WithdrawUnlocked` enforce presence + faction +
// capacity gates for a legion moving its own cargo; this module needs none of those — it needs a
// presence read and a delete, in a turn-commit transaction those verbs do not participate in).
// SQL lives here, only here (guard-dal).
public sealed partial class RpgStore
{
    /// <summary>
    /// The disposition a spent relic's <c>rpg_item</c> row carries
    /// (spec-wonder-build-flow.md §Design 7): a new value in the same free-form, documented
    /// convention <c>Disposition</c> already uses (<c>RpgStore.Items.cs</c> names
    /// <c>owned</c>/<c>salvaged</c>/<c>transferred</c>/<c>destroyed</c>) — not a closed C# enum.
    /// </summary>
    public const string RelicConsumedDisposition = "consumed";

    const string BuildStartedPrefix = "build.started:";

    /// <summary>
    /// The Data-side reachability gate (spec §Design 4): runs inside `CommitWorldTurn` between
    /// loading commands and calling `TurnEngine.Step` — the latest possible moment before
    /// resolution, strictly fresher than anything Core could check against a `WorldState` the
    /// relic is not even part of. Core has no DB access, so this gate lives here, not there.
    ///
    /// For every `build` command whose `StructureId` resolves to a `RelicCost &gt; 0` row, every
    /// named relic id must satisfy ALL of: a real `rpg_item` row owned by this world's player,
    /// carrying a live disposition, unassigned, tracing to a Relic container — AND reachable
    /// right now (aboard the issuing legion's cargo OR in the target sector's storage) — AND not
    /// already spoken for by an earlier command in this same commit batch. A command that fails
    /// is rewritten with `RelicInstanceIds` cleared to empty.
    ///
    /// Pipeline note (verified against `TurnEngine.Reveal`, not assumed from the spec's prose):
    /// the emptied command drops at Reveal's own re-admission with `relic.count-mismatch`
    /// BEFORE reaching Snapshot — `BuildResolver`'s `relic.not-reachable` re-check (spec §Design
    /// 4/6) only fires for commands that reach the resolver directly (Core-side tests prove
    /// that path). Either way the guarantees are identical: the build never starts, no relic
    /// spends, and the refusal is named. `count-mismatch` here means "verified unreachable
    /// moments before resolution", not "malformed at submit" — submit-time admission already
    /// passed for every stored command.
    /// </summary>
    internal List<WorldCommand> ValidateWonderRelicCommandsUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, IReadOnlyList<WorldCommand> commands)
    {
        var result = new List<WorldCommand>(commands.Count);

        var anyWonder = false;
        foreach (var c in commands)
        {
            if (c.Kind == WorldCommandKinds.Build && c.RelicInstanceIds.Count > 0 && IsWonderBuild(c))
            {
                anyWonder = true;
                break;
            }
        }

        // Fast path: no Wonder-shaped build orders — the overwhelmingly common turn — returns the
        // list untouched, zero DB reads.
        if (!anyWonder)
        {
            result.AddRange(commands);
            return result;
        }

        var playerStr = ReadWorldPlayerUnlocked(db, tx, worldId)?.ToString();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in commands)
        {
            if (command.Kind != WorldCommandKinds.Build
                || command.RelicInstanceIds.Count == 0
                || !IsWonderBuild(command))
            {
                result.Add(command);
                continue;
            }

            // Check every id BEFORE claiming any: a command that fails must poison nothing — its
            // build drops, its relics stay spendable, and a later command naming one of them must
            // still be able to succeed. Only an accepted command's ids join `claimed`.
            var ok = playerStr is not null;
            if (ok)
            {
                foreach (var relicId in command.RelicInstanceIds)
                {
                    if (!IsRelicSpendableUnlocked(db, tx, worldId, command, relicId, playerStr!, claimed))
                    {
                        ok = false;
                        break;
                    }
                }
            }

            if (ok)
            {
                foreach (var relicId in command.RelicInstanceIds)
                    claimed.Add(relicId);
                result.Add(command);
            }
            else
            {
                result.Add(command with { RelicInstanceIds = Array.Empty<string>() });
            }
        }

        return result;
    }

    static bool IsWonderBuild(WorldCommand command) =>
        command.StructureId is { } id
        && StructureCatalog.IsKnown(id)
        && StructureCatalog.Get(id).RelicCost > 0;

    /// <summary>
    /// One relic id's whole pre-check: unclaimed-this-batch, owned-live-unassigned-relic, and
    /// reachable-right-now. Reads live state on the caller's transaction, never a cached flag.
    /// </summary>
    static bool IsRelicSpendableUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, WorldCommand command,
        string relicId, string playerStr, HashSet<string> claimed)
    {
        if (string.IsNullOrWhiteSpace(relicId)) return false;
        if (claimed.Contains(relicId)) return false;

        // Single-predicate rule (whole-program review 2026-09-16): the owned-live-unassigned
        // + reachable-right-now check lives in EXACTLY one statement —
        // ListReachableRelicsUnlocked — and this gate reads it. The gate previously carried a
        // verbatim second copy of both halves (forked-SQL divergence risk); the list/gate
        // agreement tests now prove one implementation, not two matching texts. Blank
        // EntityId/SectorId yields an empty list (same as the old NULL-matches-nothing UNION).
        return ListReachableRelicsUnlocked(
            db, tx, worldId, command.EntityId ?? "", command.SectorId ?? "", playerStr)
            .Contains(relicId, StringComparer.Ordinal);
    }

    /// <summary>
    /// The relic spend (spec §Design 7): runs inside `CommitWorldTurn` immediately after
    /// `DiffWorldGraphUnlocked`, same transaction. Fires ONLY for an accepted
    /// <c>"build.started:"</c> report line — a command dropped for any reason (cap, materials,
    /// wrong slot, unreachable relic) never appears there, so the spend never runs for a build
    /// that did not actually happen ("refuse-then-move" by construction). Ordinary,
    /// non-Wonder structures (RelicCost == 0) are skipped outright, so stray ids on a non-Wonder
    /// order can never spend.
    ///
    /// Move, never copy: the source overlay row is deleted AND the `rpg_item` row is marked
    /// consumed, atomically. The deletes are best-effort (0 or 1 rows): `DiffEntities` runs
    /// BEFORE this function in the same commit, and its cargo-fate hook may already have moved
    /// the issuing legion's cargo into a corpse-cache row if the legion died this same turn —
    /// the build still happened, the relic is still consumed, and the death path owns those
    /// cache rows, not this function. The disposition update is authoritative and must land
    /// exactly once: anything else means the item row vanished mid-commit, which nothing in a
    /// turn commit can legitimately do — loud over silent.
    /// </summary>
    internal void SpendWonderRelicsUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId,
        IReadOnlyList<WorldCommand> commands, TurnReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        foreach (var entry in report.Entries)
        {
            if (!string.Equals(entry.Kind, TurnReportKinds.Event, StringComparison.Ordinal)) continue;
            if (!entry.Detail.StartsWith(BuildStartedPrefix, StringComparison.Ordinal)) continue;
            var structureId = entry.Detail[BuildStartedPrefix.Length..];
            if (!StructureCatalog.IsKnown(structureId)) continue;
            if (StructureCatalog.Get(structureId).RelicCost <= 0) continue;
            // Mirrors the spec's own `commands.First(...)`: command ids are unique per
            // (commander, turn), and a cross-commander collision resolves to list order.
            var command = commands.FirstOrDefault(c =>
                string.Equals(c.CommandId, entry.Subject, StringComparison.Ordinal));
            if (command is null) continue;
            if (command.RelicInstanceIds.Count == 0) continue;
            if (command.EntityId is null || command.SectorId is null)
                throw new InvalidOperationException(
                    $"wonder-build spend: accepted build '{command.CommandId}' names relics but " +
                    "has no issuing legion or target sector — the build happened, the relics must spend.");
            foreach (var relicId in command.RelicInstanceIds)
                SpendOneRelicUnlocked(db, tx, worldId, command.EntityId, command.SectorId, relicId);
        }
    }

    static void SpendOneRelicUnlocked(
        SqliteConnection db, SqliteTransaction tx,
        string worldId, string entityId, string sectorId, string relicId)
    {
        ExecInCounted(db, tx, """
            DELETE FROM rpg_world_entity_cargo
            WHERE world_id = $w AND entity_id = $e AND instance_id = $id;
            """,
            ("$w", worldId), ("$e", entityId), ("$id", relicId));
        ExecInCounted(db, tx, """
            DELETE FROM rpg_world_sector_storage
            WHERE world_id = $w AND sector_id = $s AND instance_id = $id;
            """,
            ("$w", worldId), ("$s", sectorId), ("$id", relicId));
        var marked = ExecInCounted(db, tx, """
            UPDATE rpg_item SET disposition = $d, revision = revision + 1
            WHERE instance_id = $id;
            """,
            ("$d", RelicConsumedDisposition), ("$id", relicId));
        if (marked != 1)
            throw new InvalidOperationException(
                $"wonder-build spend: rpg_item row '{relicId}' refused the 'consumed' mark " +
                $"({marked} rows) — the turn's own pre-check proved it live moments ago in this " +
                "same transaction, so this is corruption, not a race.");
    }
}
