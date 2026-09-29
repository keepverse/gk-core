using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Core.World.Turn;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// `commander-roster` EP3.8's Data half: the two `attach-commander` checks whose facts live in the
/// store, plus the stamping Core cannot do for itself.
///
/// <para>Same shape as the Wonder-build validator beside its call site (`RpgStore.WonderBuild.cs`): run
/// inside the turn's transaction, moments before `TurnEngine.Step`, returning the commands that survive.
/// A dropped command never reaches Core, so the named reason is recorded here and the resolver
/// re-validates the legion facts it can see.</para>
///
/// <para><b>Stamped, not re-derived.</b> The member needs the specimen's species and level — store facts
/// Core cannot read (`guard-dal.ps1` holds that line) — so this validator writes them onto the command.
/// Its `Hp` is the world layer's own recruit figure, supplied by the resolver from Core policy; a derived
/// combat stat is never stamped onto a world member.</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>The Data-side drop reasons (spec-legion-commander.md's check table).</summary>
    public const string CommanderRoleMissing = "commander.role.missing";

    /// <inheritdoc cref="CommanderRoleMissing"/>
    public const string CommanderNotAtBase = "commander.not-at-base";

    /// <summary>The public form: the turn path calls the Unlocked form inside its own transaction;
    /// this one exists for a caller (or a test) that holds no connection. Returns the commands that
    /// survive admission; the dropped ones were already recorded by name.</summary>
    public IReadOnlyList<WorldCommand> ValidateCommanderAttachCommands(
        string worldId, IReadOnlyList<WorldCommand> commands)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var kept = ValidateCommanderAttachCommandsUnlocked(db, tx, worldId, commands);
            tx.Commit();
            return kept;
        }
    }

    internal List<WorldCommand> ValidateCommanderAttachCommandsUnlocked(
        SqliteConnection db, SqliteTransaction tx, string worldId, IReadOnlyList<WorldCommand> commands)
    {
        var result = new List<WorldCommand>(commands.Count);

        // Fast path: no attach orders — the overwhelmingly common turn — returns the list untouched,
        // zero DB reads (the same discipline ValidateWonderRelicCommandsUnlocked follows).
        if (!commands.Any(c => c.Kind == WorldCommandKinds.AttachCommander))
        {
            result.AddRange(commands);
            return result;
        }

        var playerId = ReadWorldPlayerUnlocked(db, tx, worldId);
        EmpireRef? empire = playerId is { } pid
            ? new EmpireRef(new SaveId(pid), HumanEmpireOf(pid))
            : null;

        foreach (var command in commands)
        {
            if (command.Kind != WorldCommandKinds.AttachCommander)
            {
                result.Add(command);
                continue;
            }

            var instanceId = command.MemberInstanceId;
            if (string.IsNullOrWhiteSpace(instanceId)) return DropAttach(result, command, CommanderRoleMissing);

            // The specimen must hold the role for THIS world's empire — the role binding is the fact
            // that makes it a commander at all (commander-roster's own table).
            if (empire is not { } owner || !HasCommanderRoleUnlocked(db, owner, instanceId!))
                return DropAttach(result, command, CommanderRoleMissing);

            var actor = ReadUniqueActorUnlocked(db, instanceId!, tx);
            if (actor is null || !string.Equals(actor.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal))
                return DropAttach(result, command, CommanderNotAtBase);

            var profile = ReadCreatureProfileUnlocked(db, instanceId!);
            if (profile is null)
                return DropAttach(result, command, CommanderNotAtBase);

            result.Add(command with
            {
                MemberSpeciesId = profile.SpeciesId,
                MemberLevel = checked((int)actor.Level),
            });
        }

        return result;
    }

    static List<WorldCommand> DropAttach(List<WorldCommand> result, WorldCommand command, string reason)
    {
        // Recorded, never a gate: the turn proceeds without the seat (spec-legion-commander.md: the
        // world layer's own "delayed or missing", never a blocked turn). The commander-surface picker
        // lists only specimens that would pass these checks, so the gap is small and named.
        Console.WriteLine(
            $"[commander] {command.Kind} dropped for {command.MemberInstanceId}: {reason}");
        return result;
    }
}
