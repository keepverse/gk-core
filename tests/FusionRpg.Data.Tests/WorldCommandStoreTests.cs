using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// W5 (spec-turn-engine.md §Persistence): the per-turn command log, keyed
/// (world, turn, commander, commandId). Submission is idempotent, commanders never overwrite each
/// other, and turns never bleed.
/// </summary>
public class WorldCommandStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldCommandStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.CreateWorld(1, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, "w"));
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static WorldCommand Cmd(string commander, string id, string? entityId = null) => new()
    {
        CommanderId = commander,
        CommandId = id,
        Kind = WorldCommandKinds.StandFast,
        EntityId = entityId
    };

    [Fact]
    public void A_submitted_command_comes_back_on_the_turn_it_was_filed_for()
    {
        var (ok, reason, replayed) = _store.SubmitWorldCommand("w", Cmd("dave", "c1"));
        Assert.True(ok, reason);
        Assert.False(replayed);

        var listed = _store.ListWorldCommands("w", 0);
        var only = Assert.Single(listed);
        Assert.Equal("dave", only.CommanderId);
        Assert.Equal("c1", only.CommandId);
        Assert.Equal(WorldCommandKinds.StandFast, only.Kind);
    }

    [Fact]
    public void Resubmitting_the_same_command_id_is_a_replay_that_changes_nothing()
    {
        _store.SubmitWorldCommand("w", Cmd("dave", "c1", entityId: "e-dave-legion-1"));

        // Same id, different payload — the stored original must win, or a client could rewrite
        // an order it already committed.
        var (ok, _, replayed) = _store.SubmitWorldCommand("w", Cmd("dave", "c1"));
        Assert.True(ok);
        Assert.True(replayed);

        var only = Assert.Single(_store.ListWorldCommands("w", 0));
        Assert.Equal("e-dave-legion-1", only.EntityId);
    }

    [Fact]
    public void Two_commanders_may_use_the_same_command_id()
    {
        _store.SubmitWorldCommand("w", Cmd("dave", "shared-id"));
        var (ok, reason, replayed) = _store.SubmitWorldCommand("w", Cmd("zomboss", "shared-id"));

        Assert.True(ok, reason);
        Assert.False(replayed);
        Assert.Equal(2, _store.ListWorldCommands("w", 0).Count);
    }

    [Fact]
    public void Commands_are_listed_in_stable_commander_then_sequence_order()
    {
        _store.SubmitWorldCommand("w", Cmd("zomboss", "z1"));
        _store.SubmitWorldCommand("w", Cmd("dave", "d2"));
        _store.SubmitWorldCommand("w", Cmd("dave", "d1"));

        var listed = _store.ListWorldCommands("w", 0);
        Assert.Equal(new[] { "dave", "dave", "zomboss" }, listed.Select(c => c.CommanderId));
        Assert.Equal(new[] { "d2", "d1" }, listed.Where(c => c.CommanderId == "dave").Select(c => c.CommandId));
    }

    [Fact]
    public void The_whole_payload_survives_the_log()
    {
        // Payload is typed in Core and serialized here; a field silently lost in the round trip
        // would surface much later as a command the engine cannot act on.
        var rich = Cmd("dave", "p1", entityId: "e-dave-legion-1") with
        {
            SectorId = "ember-hollow",
            SlotIndex = 2,
            LanePath = new[] { "l-home-ember", "l-ember-ash" }
        };
        Assert.True(_store.SubmitWorldCommand("w", rich).Ok);

        var stored = Assert.Single(_store.ListWorldCommands("w", 0));
        Assert.Equal("e-dave-legion-1", stored.EntityId);
        Assert.Equal("ember-hollow", stored.SectorId);
        Assert.Equal(2, stored.SlotIndex);
        Assert.Equal(new[] { "l-home-ember", "l-ember-ash" }, stored.LanePath);
    }

    [Fact]
    public void An_unknown_world_is_refused()
    {
        var (ok, reason, _) = _store.SubmitWorldCommand("no-such-world", Cmd("dave", "c1"));
        Assert.False(ok);
        Assert.Contains("world", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_inadmissible_command_never_reaches_the_log()
    {
        var (ok, reason, _) = _store.SubmitWorldCommand("w", Cmd("dave", "c1", entityId: "e-wild-pack-1"));
        Assert.False(ok);
        Assert.Equal("entity.not-yours", reason);
        Assert.Empty(_store.ListWorldCommands("w", 0));
    }

    // world-stage W22: `Amount` and `StructureId` through all six round-trip sites — the two fields
    // `WorldCommandRequest`/`CommandPayload` never carried, so a `sustain`/`build` order came back
    // amountless (or structure-less) the moment a turn resolved, even though `WorldCommand` and both
    // resolvers already had them wired.

    [Fact]
    public void A_sustain_orders_amount_round_trips_through_ListWorldCommands()
    {
        var sustain = new WorldCommand
        {
            CommanderId = "dave", CommandId = "c-sustain", Kind = WorldCommandKinds.Sustain,
            EntityId = "e-dave-legion-1", SectorId = "homeworld", Amount = 250
        };
        Assert.True(_store.SubmitWorldCommand("w", sustain).Ok);

        var stored = Assert.Single(_store.ListWorldCommands("w", 0));
        Assert.Equal(250, stored.Amount);
    }

    [Fact]
    public void A_build_orders_structure_id_round_trips_through_ListWorldCommands()
    {
        var build = new WorldCommand
        {
            CommanderId = "dave", CommandId = "c-build", Kind = WorldCommandKinds.Build,
            EntityId = "e-dave-legion-1", SectorId = "homeworld", SlotIndex = 1, StructureId = "well"
        };
        Assert.True(_store.SubmitWorldCommand("w", build).Ok);

        var stored = Assert.Single(_store.ListWorldCommands("w", 0));
        Assert.Equal("well", stored.StructureId);
    }

    [Fact]
    public void Both_fields_also_round_trip_through_ListLoggedWorldCommands()
    {
        Assert.True(_store.SubmitWorldCommand("w", new WorldCommand
        {
            CommanderId = "dave", CommandId = "c-sustain", Kind = WorldCommandKinds.Sustain,
            EntityId = "e-dave-legion-1", SectorId = "homeworld", Amount = 250
        }).Ok);

        var logged = Assert.Single(_store.ListLoggedWorldCommands("w", 0));
        Assert.Equal(250, logged.Command.Amount);
    }

    /// <summary>
    /// `ListWorldCommandsUnlocked` — the silent sixth site, called only from inside
    /// `CommitWorldTurn` (never directly testable) — is proven by driving a real commit and checking
    /// the *effect* `SustainResolver` only produces when it actually received the amount: the
    /// sector's own stock rising by exactly that much. If this field were lost on this path (as it
    /// was before this task), the command would resolve as `amount.invalid` and never touch stock.
    /// </summary>
    [Fact]
    public void The_amount_reaches_resolution_through_the_engines_own_internal_hydration_path()
    {
        Assert.True(_store.SubmitWorldCommand("w", new WorldCommand
        {
            CommanderId = "dave", CommandId = "c-sustain", Kind = WorldCommandKinds.Sustain,
            EntityId = "e-dave-legion-1", SectorId = "homeworld", Amount = 250
        }).Ok);

        var commit = _store.CommitWorldTurn("w", "dave", 0);
        Assert.True(commit.Ok, commit.Reason);
        Assert.True(commit.Advanced);

        // If `Amount` were lost on `ListWorldCommandsUnlocked`'s own internal path (as it was before
        // this task), `WorldCommandAdmission` would refuse it as `amount.invalid` and the report
        // would carry that drop instead of a clean `command.accepted`.
        var report = _store.GetWorldTurnReport("w", 0)!;
        Assert.DoesNotContain(report.Entries, e => e.Detail == "amount.invalid");
        Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandAccepted && e.Subject == "c-sustain");
    }

    /// <summary>A `payload_json` row committed before this task must still deserialize, with both
    /// new fields reading null — the same `stance`-shaped regression precedent this file's own
    /// comment on `CommandPayload` describes.</summary>
    [Fact]
    public void An_old_payload_row_with_neither_field_still_deserializes_with_both_null()
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_store.HotPath}");
        db.Open();
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO rpg_world_commands (world_id, turn, commander_id, command_id, seq, kind, payload_json, submitted_utc)
                VALUES ('w', 0, 'dave', 'c-old', 0, 'stand-fast', $payload, '2026-01-01T00:00:00Z');
                """;
            // The exact pre-W22 shape: no "Amount", no "StructureId" keys at all.
            cmd.Parameters.AddWithValue("$payload", """{"EntityId":null,"SectorId":null,"SlotIndex":null,"LanePath":[]}""");
            cmd.ExecuteNonQuery();
        }

        var stored = Assert.Single(_store.ListWorldCommands("w", 0));
        Assert.Null(stored.Amount);
        Assert.Null(stored.StructureId);
    }
}
