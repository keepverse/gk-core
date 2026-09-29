using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// Where a specimen sits in the deployment tree right now — the ONE parent question every child
/// admission asks (`spec-legion-commander.md` "The parent rule (R-C1)", `deployment-hierarchy-ideal.md`
/// §"The tree").
///
/// <para><see cref="Home"/> is the roster; <see cref="Legion"/> is a stationed or marching legion the
/// specimen is a member of. <see cref="AdmitsChild"/> is the rule the child admissions read: a child may
/// start from home or from a <b>stationed</b> legion, never from one that is marching — *"to enter the
/// lawn run, the commander must on any base"*, and the same sentence is what a delve slot and an
/// expedition seat read.</para>
/// </summary>
public sealed record CommanderParent(bool IsHome, string? LegionEntityId = null, bool Stationed = false)
{
    public static CommanderParent Home { get; } = new(true);

    public static CommanderParent Legion(string entityId, bool stationed) => new(false, entityId, stationed);

    /// <summary>May a deployment child start from here? Home yes; a stationed legion yes; a marching
    /// legion no.</summary>
    public bool AdmitsChild => IsHome || Stationed;
}

public sealed partial class RpgStore
{
    /// <summary>Where this specimen sits: read from the world graph, never from a cached flag. One
    /// query, used by every child admission (lawn Bound, the commander seat, and — EP3.10 — the delve
    /// slot and the expedition seat).</summary>
    public CommanderParent ParentOf(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return CommanderParent.Home;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ParentOfUnlocked(db, instanceId, tx: null);
        }
    }

    /// <summary>The legion a specimen is a member of, or home. A specimen in more than one legion across
    /// worlds (a data defect this read only reports) answers with the ordinal-first one, so the answer is
    /// stable rather than order-of-insertion.</summary>
    internal static CommanderParent ParentOfUnlocked(
        SqliteConnection db, string instanceId, SqliteTransaction? tx = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return CommanderParent.Home;

        using var cmd = db.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT e.entity_id, e.at_sector_id, e.on_lane_id
            FROM rpg_world_entity_members m
            JOIN rpg_world_entities e ON e.world_id = m.world_id AND e.entity_id = m.entity_id
            WHERE m.instance_id = $i AND e.kind = $legion
            ORDER BY m.world_id, e.entity_id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$i", instanceId.Trim());
        cmd.Parameters.AddWithValue("$legion", FusionRpg.Core.World.WorldEntityKind.Legion.ToString());

        using var r = cmd.ExecuteReader();
        if (!r.Read()) return CommanderParent.Home;

        var entityId = r.GetString(0);
        var atSector = r.IsDBNull(1) ? null : r.GetString(1);
        var onLane = r.IsDBNull(2) ? null : r.GetString(2);
        // Stationed means "on ground, not on a lane": a legion mid-march is on neither, and one that is
        // somehow both (never written by the engine) is not stationed either.
        var stationed = !string.IsNullOrWhiteSpace(atSector) && string.IsNullOrWhiteSpace(onLane);
        return CommanderParent.Legion(entityId, stationed);
    }
}
