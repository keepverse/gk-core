using FusionRpg.Core.World.Growth;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Movement;

/// <summary>
/// `commander-roster`'s legion half (spec-legion-commander.md, map D5): `attach-commander` seats ONE
/// `Commander` member on a legion and `detach-commander` takes it away.
///
/// <para>Resolves in `Snapshot`, beside `bind-warden` and `raise`, for the same reason those do:
/// ownership is only decided once the rest of the turn has run, so a legion this faction lost the same
/// turn must not keep a commander, and a legion already marching cannot take one (the seat is a base
/// thing). Every legion fact is re-validated here rather than trusted from admission, exactly as
/// <see cref="WardenResolver"/> re-validates its sector.</para>
///
/// <para><b>One `Commander` member per legion, a structural limit</b> — a legion has one leader (the
/// leader shape the owner named). It is not a tunable and not a cap on progression: it is the same
/// "a legion has one commander" statement the deployment tree draws, so it is enforced with a named
/// drop (<c>legion.has-commander</c>) rather than a number. A commander member fights and carries no
/// cargo, so it is a `Fighter`-shaped member that simply is not a `Bearer`.</para>
///
/// <para>The member's facts split by who can know them: the specimen's `InstanceId`, species and level
/// are STORE facts and ride the command, stamped by the Data-side admission; its `Hp` is this world
/// layer's own recruit figure (<see cref="RecruitPolicy.RaiseMemberHp"/>), never a derived combat stat —
/// a member's hp in the world graph is what it fights with on the world stage.</para>
/// </summary>
public static class CommanderAttachResolver
{
    /// <summary>The drop reasons, each named (spec-legion-commander.md "Checks split by where the fact
    /// lives"). The role and base checks live Data-side; these four are decided from the world graph.</summary>
    public const string LegionGone = "legion.gone";

    /// <inheritdoc cref="LegionGone"/>
    public const string LegionNotYours = "legion.not-yours";

    /// <inheritdoc cref="LegionGone"/>
    public const string LegionMarching = "legion.marching";

    /// <inheritdoc cref="LegionGone"/>
    public const string LegionHasCommander = "legion.has-commander";

    public static WorldState Run(
        WorldState world, IReadOnlyList<WorldCommand> commands, TurnReport report, string phase)
    {
        var next = world;

        foreach (var command in commands.Where(c => c.Kind == WorldCommandKinds.AttachCommander))
        {
            if (!TryValidateLegion(next, command, report, phase, out var legion)) continue;

            if (legion!.Members.Any(m => m.Role == WorldEntityMemberRole.Commander))
            {
                Drop(report, phase, command, LegionHasCommander);
                continue;
            }

            var member = new WorldEntityMember
            {
                InstanceId = command.MemberInstanceId,
                SpeciesId = command.MemberSpeciesId ?? "",
                Level = command.MemberLevel ?? 1,
                Hp = RecruitPolicy.RaiseMemberHp,
                Wounds = 0,
                Role = WorldEntityMemberRole.Commander,
            };
            next = WithMembers(next, legion.EntityId, legion.Members.Append(member).ToList());
        }

        foreach (var command in commands.Where(c => c.Kind == WorldCommandKinds.DetachCommander))
        {
            if (!TryValidateLegion(next, command, report, phase, out var legion)) continue;
            // Removing a commander that is not there is a no-op, never a refusal: the specimen goes
            // home either way, and the legion needs no reason to lose a leader it does not have.
            next = WithMembers(
                next, legion!.EntityId,
                legion.Members.Where(m => m.Role != WorldEntityMemberRole.Commander).ToList());
        }

        return next;
    }

    /// <summary>The one legion re-validation both verbs share: it exists, it is this faction's, and it is
    /// stationed rather than on a lane.</summary>
    static bool TryValidateLegion(
        WorldState world, WorldCommand command, TurnReport report, string phase, out WorldEntity? legion)
    {
        legion = world.Entities.FirstOrDefault(e =>
            string.Equals(e.EntityId, command.EntityId, StringComparison.Ordinal)
            && e.Kind == WorldEntityKind.Legion);
        if (legion is null)
        {
            Drop(report, phase, command, LegionGone);
            return false;
        }

        if (!string.Equals(legion.OwnerFactionId, command.CommanderId, StringComparison.Ordinal))
        {
            Drop(report, phase, command, LegionNotYours);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(legion.OnLaneId))
        {
            Drop(report, phase, command, LegionMarching);
            return false;
        }

        return true;
    }

    static WorldState WithMembers(WorldState world, string entityId, IReadOnlyList<WorldEntityMember> members) =>
        world with
        {
            Entities = world.Entities
                .Select(e => string.Equals(e.EntityId, entityId, StringComparison.Ordinal)
                    ? e with { Members = members }
                    : e)
                .ToList(),
        };

    static void Drop(TurnReport report, string phase, WorldCommand command, string reason) =>
        report.Add(phase, TurnReportKinds.CommandDropped, command.CommandId, reason);
}
