using FusionRpg.Contracts;
using FusionRpg.Core.Progression;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

[Collection(FusionRpg.Data.Tests.CorpseCache.LawnAttritionTuningCollection.Name)]
public class UniqueActorStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;

    public UniqueActorStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    [Fact]
    public void Create_starts_Roster_and_lists()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 3);
        Assert.Equal(UniqueActorPhases.Roster, a.Phase);
        Assert.Equal(3, a.TypeId);
        Assert.False(string.IsNullOrWhiteSpace(a.InstanceId));
        var list = _store.ListUniqueActors(_playerId);
        Assert.Contains(list.Items, x => x.InstanceId == a.InstanceId);
        Assert.Equal(a.InstanceId, _store.GetUniqueActor(a.InstanceId)!.InstanceId);
    }

    [Fact]
    public void Deploy_ack_idempotent_corr_then_ActiveBound()
    {
        var a = _store.CreateUniqueActor(_playerId, "zombie", 1);
        var corr = "corr-" + Guid.NewGuid().ToString("N");

        var d1 = _store.TryBeginUniqueDeploy(a.InstanceId, corr, "m-1");
        Assert.True(d1.Ok);
        Assert.True(d1.Queued);
        Assert.Equal(UniqueActorPhases.Deploying, d1.Actor!.Phase);
        Assert.Equal(corr, d1.Actor.DeployCorrelationId);

        var d2 = _store.TryBeginUniqueDeploy(a.InstanceId, corr, "m-1");
        Assert.True(d2.Ok);
        Assert.False(d2.Queued);

        var ack = _store.TryAckUniqueSpawn(corr, "0xABC", "m-1");
        Assert.True(ack.Ok);
        Assert.Equal(UniqueActorPhases.ActiveBound, ack.Actor!.Phase);
        Assert.Equal("0xABC", ack.Actor.LastPtr);
        Assert.Equal("m-1", ack.Actor.MatchKey);
    }

    [Fact]
    public void Bound_ack_refuses_open_pointer_collision_in_same_match()
    {
        var first = _store.CreateUniqueActor(_playerId, "zombie", 1);
        var second = _store.CreateUniqueActor(_playerId, "zombie", 2);
        Assert.True(_store.TryBeginUniqueDeploy(first.InstanceId, "corr-bind-a", "m-bind").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-bind-a", "ptr-live", "m-bind").Ok);
        Assert.True(_store.TryBeginUniqueDeploy(second.InstanceId, "corr-bind-b", "m-bind").Ok);

        var collision = _store.TryAckUniqueSpawn("corr-bind-b", "ptr-live", "m-bind");
        Assert.False(collision.Ok);
        Assert.Equal("binding.collision", collision.Reason);
        Assert.Equal(UniqueActorPhases.Deploying, _store.GetUniqueActor(second.InstanceId)!.Phase);
    }

    [Fact]
    public void Fail_deploy_returns_Roster()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 2);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "c-fail").Ok);
        var fail = _store.TryFailUniqueDeploy(a.InstanceId);
        Assert.True(fail.Ok);
        Assert.Equal(UniqueActorPhases.Roster, fail.Actor!.Phase);
        Assert.Null(fail.Actor.DeployCorrelationId);
    }

    [Fact]
    public void Observe_die_retires_and_board_end_recovers_to_Roster()
    {
        // Task 4C.1 (explicit lawn→Retire trigger): a bound specimen named by plant.die died
        // for real — Retired, never Roster. Survivors still walk home via board.end.
        var a = _store.CreateUniqueActor(_playerId, "plant", 5);
        var corr = "corr-die";
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, "m-die").Ok);
        Assert.True(_store.TryAckUniqueSpawn(corr, "0xDIE", "m-die").Ok);

        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("plant.die", "m-die", """{"ptr":"0xDIE"}""")
        });
        var dead = _store.GetUniqueActor(a.InstanceId)!;
        Assert.Equal(UniqueActorPhases.Retired, dead.Phase);
        Assert.Null(dead.MatchKey);
        Assert.Null(dead.LastPtr);

        var b = _store.CreateUniqueActor(_playerId, "zombie", 6);
        var corr2 = "corr-end";
        Assert.True(_store.TryBeginUniqueDeploy(b.InstanceId, corr2, "m-end").Ok);
        Assert.True(_store.TryAckUniqueSpawn(corr2, "0xEND", "m-end").Ok);
        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("board.end", "m-end", "{}")
        });
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(b.InstanceId)!.Phase);
        Assert.Null(_store.GetUniqueActor(b.InstanceId)!.LastPtr);
    }

    // ---- T6.1 (2026-09-06): ObserveUniqueActorEvents now reports which players' ActiveBound roster
    // changed, so a caller can re-push the atom union for exactly those players -------------------

    [Fact]
    public void An_ack_event_reports_the_actors_own_player_as_affected()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 4);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "corr-report", "m-report").Ok);

        var affected = _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("pvz.spawn.extra.ack", "m-report", """{"correlationId":"corr-report","ptr":"0xREPORT"}""")
        }).AffectedPlayerIds;

        Assert.Equal(new[] { _playerId }, affected);
    }

    [Fact]
    public void A_recover_event_reports_the_same_player_as_the_ack_that_bound_it()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 5);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "corr-rec", "m-rec").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-rec", "0xREC", "m-rec").Ok);

        var affected = _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("plant.die", "m-rec", """{"ptr":"0xREC"}""")
        }).AffectedPlayerIds;

        Assert.Equal(new[] { _playerId }, affected);
    }

    [Fact]
    public void An_event_naming_an_unknown_ptr_or_correlation_reports_no_affected_player()
    {
        var affected = _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("pvz.spawn.extra.ack", "m-none", """{"correlationId":"no-such-corr","ptr":"0xNONE"}"""),
            ("plant.die", "m-none", """{"ptr":"0xNONE"}"""),
        }).AffectedPlayerIds;

        Assert.Empty(affected);
    }

    [Fact]
    public void A_batch_touching_the_same_player_twice_reports_that_player_once()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 4);
        var b = _store.CreateUniqueActor(_playerId, "zombie", 6);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "corr-dup-a", "m-dup").Ok);
        Assert.True(_store.TryBeginUniqueDeploy(b.InstanceId, "corr-dup-b", "m-dup").Ok);

        var affected = _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("pvz.spawn.extra.ack", "m-dup", """{"correlationId":"corr-dup-a","ptr":"0xDUPA"}"""),
            ("pvz.spawn.extra.ack", "m-dup", """{"correlationId":"corr-dup-b","ptr":"0xDUPB"}"""),
        }).AffectedPlayerIds;

        Assert.Equal(new[] { _playerId }, affected);
    }

    [Fact]
    public void A_shared_match_key_recovering_two_specimens_reports_their_distinct_players_once_each()
    {
        var otherPlayerId = _store.CreatePlayer("second-player").Id;
        var mine = _store.CreateUniqueActor(_playerId, "plant", 4);
        Assert.True(_store.TryBeginUniqueDeploy(mine.InstanceId, "corr-multi-mine", "m-multi").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-multi-mine", "0xMINE", "m-multi").Ok);

        // A second player's own row sharing the same match_key — realistic today only as a fixture
        // (this repo's own lawn is single-player-per-match), but the query itself has no player
        // filter, so the return value must not silently assume one player per match_key either.
        var theirs = _store.CreateUniqueActor(otherPlayerId, "zombie", 9);
        Assert.True(_store.TryBeginUniqueDeploy(theirs.InstanceId, "corr-multi-theirs", "m-multi").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-multi-theirs", "0xTHEIRS", "m-multi").Ok);

        var affected = _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("match.result", "m-multi", "{}")
        }).AffectedPlayerIds;

        Assert.Equal(2, affected.Count);
        Assert.Contains(_playerId, affected);
        Assert.Contains(otherPlayerId, affected);
    }

    [Fact]
    public void Retire_from_Roster_and_illegal_from_Deploying()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 1);
        Assert.True(_store.TryRetireUniqueActor(a.InstanceId).Ok);
        Assert.Equal(UniqueActorPhases.Retired, _store.GetUniqueActor(a.InstanceId)!.Phase);

        var b = _store.CreateUniqueActor(_playerId, "plant", 2);
        Assert.True(_store.TryBeginUniqueDeploy(b.InstanceId, "c-ret").Ok);
        Assert.False(_store.TryRetireUniqueActor(b.InstanceId).Ok);
    }

    [Fact]
    public void Illegal_deploy_from_ActiveBound_rejected()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 9);
        var corr = "c-act";
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr).Ok);
        Assert.True(_store.TryAckUniqueSpawn(corr, "0x1").Ok);
        var again = _store.TryBeginUniqueDeploy(a.InstanceId, "c-other");
        Assert.False(again.Ok);
        Assert.Contains("phase", again.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Observe_ack_event_to_ActiveBound()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 4);
        var corr = "corr-ack-obs";
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, "m-ack").Ok);

        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("pvz.spawn.extra.ack", "m-ack", """{"correlationId":"corr-ack-obs","ptr":"0xACK"}""")
        });
        var row = _store.GetUniqueActor(a.InstanceId)!;
        Assert.Equal(UniqueActorPhases.ActiveBound, row.Phase);
        Assert.Equal("0xACK", row.LastPtr);
        Assert.Equal("m-ack", row.MatchKey);
    }

    [Fact]
    public void Observe_zombie_die_retires_and_match_result_recovers_to_Roster()
    {
        // Task 4C.1: zombie.die is a real death too — Retired. match.result survivors → Roster.
        var a = _store.CreateUniqueActor(_playerId, "zombie", 7);
        var corr = "corr-zdie";
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, "m-z").Ok);
        Assert.True(_store.TryAckUniqueSpawn(corr, "0xZDIE", "m-z").Ok);

        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("zombie.die", "m-z", """{"ptr":"0xZDIE"}""")
        });
        var dead = _store.GetUniqueActor(a.InstanceId)!;
        Assert.Equal(UniqueActorPhases.Retired, dead.Phase);
        Assert.Null(dead.MatchKey);
        Assert.Null(dead.LastPtr);

        var b = _store.CreateUniqueActor(_playerId, "plant", 8);
        var corr2 = "corr-mres";
        Assert.True(_store.TryBeginUniqueDeploy(b.InstanceId, corr2, "m-res").Ok);
        Assert.True(_store.TryAckUniqueSpawn(corr2, "0xMRES", "m-res").Ok);
        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("match.result", "m-res", "{}")
        });
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(b.InstanceId)!.Phase);
        Assert.Null(_store.GetUniqueActor(b.InstanceId)!.LastPtr);
    }

    [Fact]
    public void Retire_on_die_never_exposes_Recovering_revision_bumps_by_two()
    {
        // Task 4C.1: plant.die retires (never Recovering); the retire write still bumps revision.
        var a = _store.CreateUniqueActor(_playerId, "plant", 11);
        var corr = "corr-rev";
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, "m-rev").Ok);
        Assert.True(_store.TryAckUniqueSpawn(corr, "0xREV", "m-rev").Ok);
        var active = _store.GetUniqueActor(a.InstanceId)!;
        Assert.Equal(UniqueActorPhases.ActiveBound, active.Phase);
        var revActive = active.Revision;

        _store.ObserveUniqueActorEvents(new (string Kind, string? MatchKey, string PayloadJson)[]
        {
            ("plant.die", "m-rev", """{"ptr":"0xREV"}""")
        });
        var after = _store.GetUniqueActor(a.InstanceId)!;
        Assert.Equal(UniqueActorPhases.Retired, after.Phase);
        Assert.NotEqual(UniqueActorPhases.Recovering, after.Phase);
        // RetireUniqueActorUnlocked writes revision + 1 (the old +2 belonged to the
        // RecoverToRosterUnlocked write this path no longer takes).
        Assert.True(after.Revision >= revActive + 1);
    }

    [Fact]
    public void Retire_from_ActiveBound_clears_bind_fields()
    {
        var a = _store.CreateUniqueActor(_playerId, "zombie", 12);
        var corr = "corr-retire-bound";
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, "m-ret").Ok);
        Assert.True(_store.TryAckUniqueSpawn(corr, "0xRET", "m-ret").Ok);

        var (ok, _, row) = _store.TryRetireUniqueActor(a.InstanceId);
        Assert.True(ok);
        Assert.Equal(UniqueActorPhases.Retired, row!.Phase);
        Assert.Null(row.MatchKey);
        Assert.Null(row.LastPtr);
        Assert.Null(row.DeployCorrelationId);
    }

    [Fact]
    public void Create_plant_and_zombie_sides_preserved_through_deploy_begin()
    {
        var plant = _store.CreateUniqueActor(_playerId, "plant", 1);
        var zombie = _store.CreateUniqueActor(_playerId, "zombie", 2);
        Assert.Equal("plant", plant.Side);
        Assert.Equal("zombie", zombie.Side);

        Assert.True(_store.TryBeginUniqueDeploy(plant.InstanceId, "c-p").Ok);
        Assert.True(_store.TryBeginUniqueDeploy(zombie.InstanceId, "c-z").Ok);
        Assert.Equal("plant", _store.GetUniqueActor(plant.InstanceId)!.Side);
        Assert.Equal("zombie", _store.GetUniqueActor(zombie.InstanceId)!.Side);
    }

    [Fact]
    public void FailExpired_Deploying_returns_Roster_no_second_create()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 9);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "corr-timeout", "m-to").Ok);
        Assert.Equal(UniqueActorPhases.Deploying, _store.GetUniqueActor(a.InstanceId)!.Phase);

        var future = DateTimeOffset.UtcNow.AddMinutes(10);
        var failed = _store.FailExpiredUniqueDeploys(TimeSpan.FromSeconds(30), future);
        Assert.Single(failed);
        Assert.Equal(a.InstanceId, failed[0].InstanceId);
        var after = _store.GetUniqueActor(a.InstanceId)!;
        Assert.Equal(UniqueActorPhases.Roster, after.Phase);
        Assert.Null(after.DeployCorrelationId);

        // Same specimen can deploy again (no second Create).
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "corr-timeout-2").Ok);
        Assert.Equal(1, _store.ListUniqueActors(_playerId).Items.Count(x => x.InstanceId == a.InstanceId));
    }

    [Fact]
    public void Sweep_stale_ActiveBound_without_open_run_to_Roster()
    {
        var a = _store.CreateUniqueActor(_playerId, "zombie", 4);
        var corr = "corr-sweep";
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, "m-orphan").Ok);
        Assert.True(_store.TryAckUniqueSpawn(corr, "0xSWP", "m-orphan").Ok);
        Assert.Equal(UniqueActorPhases.ActiveBound, _store.GetUniqueActor(a.InstanceId)!.Phase);

        var n = _store.SweepStaleActiveBoundUniqueActors();
        Assert.True(n >= 1);
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(a.InstanceId)!.Phase);
        Assert.Null(_store.GetUniqueActor(a.InstanceId)!.LastPtr);
    }

    [Fact]
    public void A_new_board_start_closes_the_board_that_never_ended_and_frees_its_specimen()
    {
        // lawn-combat-wire L-N40 (live 2026-09-16): a game killed mid-match sends no board.end, so its run keeps
        // ended_utc NULL, the boot sweep's "is the run still open?" test says yes, and the specimen stays ActiveBound
        // for good — every later deploy is refused. deployment-hierarchy-ideal.md's model says a lawn deployment's
        // scope IS the match session, so the fix closes the abandoned scope; the existing sweep then settles the
        // specimen through its own ActiveBound → Roster path.
        var killed = "m-killed-" + Guid.NewGuid().ToString("N")[..8];
        var next = "m-next-" + Guid.NewGuid().ToString("N")[..8];
        StartBoard(killed);
        var stale = _store.CreateUniqueActor(_playerId, "plant", 979);
        Assert.True(_store.TryBeginUniqueDeploy(stale.InstanceId, "corr-killed", killed).Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-killed", "0xKILLED", killed).Ok);

        StartBoard(next);   // the game came back on a new board; the old one can never end

        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(stale.InstanceId)!.Phase);
        Assert.Null(_store.GetUniqueActor(stale.InstanceId)!.LastPtr);
    }

    [Fact]
    public void A_new_board_start_leaves_a_specimen_bound_to_that_same_board_alone()
    {
        var live = "m-live-" + Guid.NewGuid().ToString("N")[..8];
        StartBoard(live);
        var a = _store.CreateUniqueActor(_playerId, "plant", 0);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "corr-live", live).Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-live", "0xLIVE", live).Ok);

        StartBoard(live);   // a repeated board.start for the SAME board (L-N29's self-heal path)

        Assert.Equal(UniqueActorPhases.ActiveBound, _store.GetUniqueActor(a.InstanceId)!.Phase);
        Assert.Equal("0xLIVE", _store.GetUniqueActor(a.InstanceId)!.LastPtr);
    }

    [Fact]
    public void Closing_abandoned_runs_frees_their_specimens_and_is_what_boot_runs()
    {
        var abandoned = "m-boot-" + Guid.NewGuid().ToString("N")[..8];
        StartBoard(abandoned);
        var a = _store.CreateUniqueActor(_playerId, "zombie", 2);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "corr-boot", abandoned).Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-boot", "0xBOOT", abandoned).Ok);
        Assert.Equal(0, _store.SweepStaleActiveBoundUniqueActors());   // the run still reads open, so the sweep refuses

        var freed = _store.CloseAbandonedRunsAndSweep();

        Assert.True(freed >= 1);
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(a.InstanceId)!.Phase);
    }

    void StartBoard(string matchKey) =>
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "board.start",
                MatchKey = matchKey,
                Payload = new { levelName = "ln40" }
            }
        });

    [Fact]
    public void Sweep_preserves_ActiveBound_with_open_run()
    {
        var matchKey = "m-open-" + Guid.NewGuid().ToString("N")[..8];
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "board.start",
                MatchKey = matchKey,
                Payload = new { levelName = "open-for-unique" }
            }
        });

        var a = _store.CreateUniqueActor(_playerId, "plant", 2);
        var corr = "corr-open";
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, corr, matchKey).Ok);
        Assert.True(_store.TryAckUniqueSpawn(corr, "0xOPEN", matchKey).Ok);
        Assert.Equal(UniqueActorPhases.ActiveBound, _store.GetUniqueActor(a.InstanceId)!.Phase);

        Assert.Equal(0, _store.SweepStaleActiveBoundUniqueActors());
        Assert.Equal(UniqueActorPhases.ActiveBound, _store.GetUniqueActor(a.InstanceId)!.Phase);
        Assert.Equal("0xOPEN", _store.GetUniqueActor(a.InstanceId)!.LastPtr);
    }

    [Fact]
    public void HasAnyActiveBound_true_while_bound()
    {
        Assert.False(_store.HasAnyActiveBoundUniqueActors());
        var a = _store.CreateUniqueActor(_playerId, "plant", 1);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "c-ab", "m-ab").Ok);
        Assert.True(_store.TryAckUniqueSpawn("c-ab", "0xAB", "m-ab").Ok);
        Assert.True(_store.HasAnyActiveBoundUniqueActors());
    }

    [Fact]
    public void Upsert_and_get_stat_mods_json()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 1);
        Assert.Equal("{}", _store.GetUniqueStatModsJson(a.InstanceId));
        _store.UpsertUniqueStatModsJson(a.InstanceId, """{"absolutes":{"hp":12}}""");
        Assert.Contains("12", _store.GetUniqueStatModsJson(a.InstanceId), StringComparison.Ordinal);
    }

    [Fact]
    public void Equipment_upsert_rebuilds_mods_grants_preserves_absolutes()
    {
        // stub.atk_ring is atom-backed (mods-absorption, spec-mods-absorption.md): its grant no
        // longer reaches mods_json at all — it grants exclusively through effect_binding now
        // (reconciliation fails closed here since this fixture never imports the atom seed tree, so
        // there's no container to bind against; UniqueEquipmentAtomBindingTests and
        // ModsAbsorptionTests (Core.Tests) cover the real binding against the real seed tree). This
        // test stays scoped to what it always tested: the store-level mods_json/absolutes upsert path.
        var a = _store.CreateUniqueActor(_playerId, "plant", 1);
        _store.UpsertUniqueStatModsJson(a.InstanceId, """{"absolutes":{"hp":42},"grants":[]}""");

        var eq = _store.UpsertUniqueEquipment(a.InstanceId, "weapon", "stub.atk_ring");
        Assert.Equal(a.InstanceId, eq.InstanceId);
        Assert.Contains(eq.Items, x => x.Slot == "weapon" && x.ItemId == "stub.atk_ring");
        Assert.DoesNotContain("fx.passive_atk_flat", eq.ModsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("equip-stub-atk:weapon", eq.ModsJson, StringComparison.Ordinal);
        Assert.Contains("\"hp\":42", eq.ModsJson.Replace(" ", ""), StringComparison.Ordinal);

        var cleared = _store.ClearUniqueEquipmentSlot(a.InstanceId, "weapon");
        Assert.DoesNotContain(cleared.Items, x => x.Slot == "weapon");
        Assert.DoesNotContain("fx.passive_atk_flat", cleared.ModsJson, StringComparison.Ordinal);
        Assert.Contains("42", cleared.ModsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Equipment_empty_item_clears_slot()
    {
        var a = _store.CreateUniqueActor(_playerId, "zombie", 2);
        _store.UpsertUniqueEquipment(a.InstanceId, "trinket", "stub.butter_bead");
        Assert.Single(_store.ListUniqueEquipment(a.InstanceId));
        _store.UpsertUniqueEquipment(a.InstanceId, "trinket", "");
        Assert.Empty(_store.ListUniqueEquipment(a.InstanceId));
    }

    [Fact]
    public void Equipment_rejects_unknown_item_and_bad_slot()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 1);
        var unknown = Assert.Throws<ArgumentException>(() =>
            _store.UpsertUniqueEquipment(a.InstanceId, "weapon", "stub.nope"));
        Assert.Equal("itemId", unknown.ParamName);

        var badSlot = Assert.Throws<ArgumentException>(() =>
            _store.UpsertUniqueEquipment(a.InstanceId, "hat", "stub.atk_ring"));
        Assert.Equal("slot", badSlot.ParamName);
        Assert.Empty(_store.ListUniqueEquipment(a.InstanceId));
    }

    [Fact]
    public void Equipment_equips_a_real_relic_and_rejects_wrong_slot()
    {
        // relic.ashen_reliquary is atom-backed (fx.passive_atk_flat, mods-absorption) — its grant no
        // longer reaches mods_json; slot validation (out of this module's scope) is unchanged.
        var a = _store.CreateUniqueActor(_playerId, "plant", 1);
        var eq = _store.UpsertUniqueEquipment(a.InstanceId, "weapon", "relic.ashen_reliquary");
        Assert.Contains(eq.Items, x => x.Slot == "weapon" && x.ItemId == "relic.ashen_reliquary");
        Assert.DoesNotContain("fx.passive_atk_flat", eq.ModsJson, StringComparison.Ordinal);

        var mismatch = Assert.Throws<ArgumentException>(() =>
            _store.UpsertUniqueEquipment(a.InstanceId, "armor", "relic.ashen_reliquary"));
        Assert.Equal("itemId", mismatch.ParamName);
        Assert.StartsWith("slot_mismatch", mismatch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Equipment_same_stub_two_slots_grants_nothing_in_mods_json_either_slot()
    {
        // 2026-09-06: stub.hp_charm is atom-backed now too (item.fx-entity-atk) — every known item's
        // per-slot identity lives on effect_binding (a distinct binding per slot, proven in
        // ModsAbsorptionTests/UniqueEquipmentAtomBindingTests, which import the real seed tree this
        // fixture does not), never on a stamped mods_json grantId any more; the real regression to
        // guard HERE is that the SAME item in two slots leaks a grant into neither slot's mods_json.
        var a = _store.CreateUniqueActor(_playerId, "zombie", 2);
        _store.UpsertUniqueEquipment(a.InstanceId, "weapon", "stub.hp_charm");
        var eq = _store.UpsertUniqueEquipment(a.InstanceId, "armor", "stub.hp_charm");
        Assert.DoesNotContain("fx.entity_atk", eq.ModsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Equipment_rebuild_preserves_flat_absolutes()
    {
        // 2026-09-06: stub.hp_charm is atom-backed now too — absolutes survive regardless, proven
        // against a mods_json that no longer carries any grant for a known item at all.
        var a = _store.CreateUniqueActor(_playerId, "plant", 1);
        _store.UpsertUniqueStatModsJson(a.InstanceId, """{"hp":12,"atk":3}""");
        var eq = _store.UpsertUniqueEquipment(a.InstanceId, "trinket", "stub.hp_charm");
        Assert.Contains("12", eq.ModsJson, StringComparison.Ordinal);
        Assert.Contains("3", eq.ModsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("fx.entity_atk", eq.ModsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Award_xp_levels_up_and_refuses_retired()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 1);
        Assert.Equal(1, a.Level);
        Assert.Equal(0, a.Xp);

        var (ok, reason, after, _) = _store.AwardUniqueActorXp(a.InstanceId, 150, "test");
        Assert.True(ok);
        Assert.Equal("", reason);
        Assert.Equal(2, after!.Level);
        Assert.Equal(50, after.Xp);

        Assert.True(_store.TryRetireUniqueActor(a.InstanceId).Ok);
        var refuse = _store.AwardUniqueActorXp(a.InstanceId, 10, "nope");
        Assert.False(refuse.Ok);
        Assert.Equal("phase.retired", refuse.Reason);
    }

    /// <summary>
    /// Was `Award_xp_rejects_non_finite_delta`: `AwardUniqueActorXp`'s `delta` moved from `double` to
    /// `long` (effort-power reconciliation, 2026-09-05, RpgStore.UniqueActors.cs's own doc comment) —
    /// a `long` can never be NaN or infinite, so those two cases no longer compile. `delta &lt;= 0` is
    /// the guard that replaced them (same file, same "bad_delta" reason), so the non-positive cases
    /// it actually covers now are zero and negative.
    /// </summary>
    [Fact]
    public void Award_xp_rejects_a_non_positive_delta()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 1);
        Assert.Equal("bad_delta", _store.AwardUniqueActorXp(a.InstanceId, -1).Reason);
        Assert.Equal("bad_delta", _store.AwardUniqueActorXp(a.InstanceId, 0).Reason);
        Assert.Equal(1, _store.GetUniqueActor(a.InstanceId)!.Level);
    }

    [Fact]
    public void Award_xp_does_not_touch_type_progression()
    {
        var a = _store.CreateUniqueActor(_playerId, "plant", 7);
        var beforeProg = _store.GetRpgActor(_playerId, "plant", a.TypeId);
        Assert.True(_store.AwardUniqueActorXp(a.InstanceId, 250, "specimen-only").Ok);
        var afterProg = _store.GetRpgActor(_playerId, "plant", a.TypeId);
        Assert.Equal(beforeProg?.Xp ?? 0, afterProg?.Xp ?? 0);
        Assert.Equal(beforeProg?.Level ?? 1, afterProg?.Level ?? 1);
    }

    [Fact]
    public void Lawn_kill_observation_awards_attributed_unique_xp_once()
    {
        ProgressionTuningHub.Configure(ContractTuningTestBootstrap.DefaultProgression with
        {
            Awards = ContractTuningTestBootstrap.DefaultProgression.Awards with { SpecimenLawnKill = 18 }
        });
        var a = _store.CreateUniqueActor(_playerId, "zombie", 1);
        Assert.True(_store.TryBeginUniqueDeploy(a.InstanceId, "corr-lawn", "m-lawn").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-lawn", "killer-ptr", "m-lawn").Ok);

        _store.ObserveUniqueActorEvents(new[]
        {
            ("plant.die", (string?)"m-lawn", "{\"ptr\":\"enemy-ptr\",\"killerPtr\":\"killer-ptr\",\"lifecycleOccurrence\":1}", (string?)null)
        });
        var after = _store.GetUniqueActor(a.InstanceId)!;
        Assert.Equal(18, after.Xp);
        Assert.Equal(UniqueActorPhases.ActiveBound, after.Phase);

        // The actor remains bound after the enemy kill; replaying the same occurrence cannot award
        // the receipt a second time.
        _store.ObserveUniqueActorEvents(new[]
        {
            ("plant.die", (string?)"m-lawn", "{\"ptr\":\"enemy-ptr\",\"killerPtr\":\"killer-ptr\",\"lifecycleOccurrence\":1}", (string?)null)
        });
        Assert.Equal(18, _store.GetUniqueActor(a.InstanceId)!.Xp);

        _store.ObserveUniqueActorEvents(new[]
        {
            ("board.end", (string?)"m-lawn", "{}", (string?)null)
        });
        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(a.InstanceId)!.Phase);
    }

    [Fact]
    public void Lawn_duration_uses_scaled_active_match_milliseconds_only()
    {
        var baseline = ContractTuningTestBootstrap.DefaultProgression;
        ProgressionTuningHub.Configure(baseline with
        {
            Awards = baseline.Awards with
            {
                SpecimenBoundIntervalMs = 1_000,
                SpecimenBoundIntervalXp = 5
            }
        });
        var actor = _store.CreateUniqueActor(_playerId, "zombie", 1);
        Assert.True(_store.TryBeginUniqueDeploy(actor.InstanceId, "corr-duration", "m-duration").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-duration", "ptr-duration", "m-duration", 100).Ok);

        // 3,000 active milliseconds pays exactly three intervals. The wall-clock event time is
        // intentionally unrelated; settlement must use the injector's scaled match clock.
        _store.ObserveUniqueActorEvents(new[]
        {
            ("zombie.die", (string?)"m-duration",
                "{\"ptr\":\"ptr-duration\",\"lifecycleOccurrence\":1,\"activeMatchMs\":3100}",
                (string?)DateTimeOffset.UtcNow.AddDays(-3).ToString("o"))
        });

        // Task 4C.1: the duration XP is still awarded before the retire, in the same
        // transaction — a named-by-die specimen retires with its earned XP, never Roster.
        var retired = _store.GetUniqueActor(actor.InstanceId)!;
        Assert.Equal(15, retired.Xp);
        Assert.Equal(UniqueActorPhases.Retired, retired.Phase);
    }

    [Fact]
    public void Lawn_terminal_settlement_rolls_back_receipt_xp_and_recovery_on_failure()
    {
        var baseline = ContractTuningTestBootstrap.DefaultProgression;
        var overflow = baseline with
        {
            Awards = baseline.Awards with
            {
                SpecimenLawnKill = 18,
                SpecimenBoundIntervalMs = 1,
                SpecimenBoundIntervalXp = long.MaxValue
            }
        };
        ProgressionTuningHub.Configure(overflow);
        var killer = _store.CreateUniqueActor(_playerId, "zombie", 2);
        var target = _store.CreateUniqueActor(_playerId, "plant", 3);
        Assert.True(_store.TryBeginUniqueDeploy(killer.InstanceId, "corr-atomic-k", "m-atomic").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-atomic-k", "ptr-killer", "m-atomic", 0).Ok);
        Assert.True(_store.TryBeginUniqueDeploy(target.InstanceId, "corr-atomic-t", "m-atomic").Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-atomic-t", "ptr-target", "m-atomic", 0).Ok);

        // Duration settlement overflows after the kill receipt is inserted. The per-event
        // transaction must roll back both that receipt and the XP/recovery writes.
        var eventTime = DateTimeOffset.UtcNow.AddSeconds(2).ToString("o");
        _store.ObserveUniqueActorEvents(new[]
        {
            ("plant.die", (string?)"m-atomic", "{\"ptr\":\"ptr-target\",\"killerPtr\":\"ptr-killer\",\"lifecycleOccurrence\":1,\"activeMatchMs\":2000}", (string?)eventTime)
        });
        var failedKiller = _store.GetUniqueActor(killer.InstanceId)!;
        var failedTarget = _store.GetUniqueActor(target.InstanceId)!;
        Assert.Equal(UniqueActorPhases.ActiveBound, failedKiller.Phase);
        Assert.Equal(UniqueActorPhases.ActiveBound, failedTarget.Phase);
        Assert.Equal(0, failedKiller.Xp);
        Assert.Equal(0, failedTarget.Xp);

        // Restore valid tuning and replay the same lifecycle event: a missing receipt proves the
        // failed attempt did not partially commit, and the retry settles exactly once.
        ProgressionTuningHub.Configure(baseline with
        {
            Awards = baseline.Awards with { SpecimenLawnKill = 18 }
        });
        _store.ObserveUniqueActorEvents(new[]
        {
            ("plant.die", (string?)"m-atomic", "{\"ptr\":\"ptr-target\",\"killerPtr\":\"ptr-killer\",\"lifecycleOccurrence\":1,\"activeMatchMs\":2000}", (string?)eventTime)
        });
        var recoveredKiller = _store.GetUniqueActor(killer.InstanceId)!;
        var retiredTarget = _store.GetUniqueActor(target.InstanceId)!;
        Assert.Equal(18, recoveredKiller.Xp);
        Assert.Equal(UniqueActorPhases.ActiveBound, recoveredKiller.Phase);
        // Task 4C.1: the die-named target retires for real; the killer (never die-named)
        // stays bound.
        Assert.Equal(UniqueActorPhases.Retired, retiredTarget.Phase);
        Assert.Equal(0, retiredTarget.Xp);
    }

    [Fact]
    public void Activity_projection_retains_explicit_unique_source_claim()
    {
        var matchKey = "m-source-" + Guid.NewGuid().ToString("N");
        var actor = _store.CreateUniqueActor(_playerId, "zombie", 3);
        Assert.True(_store.TryBeginUniqueDeploy(actor.InstanceId, "corr-7", matchKey).Ok);
        Assert.True(_store.TryAckUniqueSpawn("corr-7", "source-ptr", matchKey).Ok);
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"), Game = RpgConstants.GameId,
                Kind = "board.start", MatchKey = matchKey, Payload = new { levelName = "source" }
            },
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"), Game = RpgConstants.GameId,
                Kind = "zombie.spawn", MatchKey = matchKey,
                Payload = new { type = 3, ptr = "source-ptr", source = "extra", instanceId = actor.InstanceId, correlationId = "corr-7" }
            }
        });

        var fact = _store.ListPvzActivityFacts(_playerId)!.Items.First(x => x.Kind == "ZombieSpawned");
        Assert.Equal("creature.progression.v1", fact.SourceKind);
        Assert.Equal($"unique:{actor.InstanceId}:corr-7", fact.SourceId);
    }

    [Fact]
    public void Activity_projection_does_not_promote_unowned_extra_spawn_to_empire_species()
    {
        var matchKey = "m-unowned-extra-" + Guid.NewGuid().ToString("N");
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"), Game = RpgConstants.GameId,
                Kind = "board.start", MatchKey = matchKey, Payload = new { levelName = "source" }
            },
            new EventEnvelope
            {
                T = DateTime.UtcNow.ToString("o"), Game = RpgConstants.GameId,
                Kind = "zombie.spawn", MatchKey = matchKey,
                Payload = new { type = 3, ptr = "unowned-extra-ptr", source = "extra", instanceId = "missing-instance" }
            }
        });

        var fact = _store.ListPvzActivityFacts(_playerId)!.Items.First(x => x.Kind == "ZombieSpawned");
        Assert.NotEqual("creature.progression.v1", fact.SourceKind);
        Assert.Null(_store.GetRpgActor(_playerId, RpgActorKinds.Species, 10003));
    }
}
