using FusionRpg.Contracts;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `solid-remediation` SR-18, closed 2026-09-17 by owner ruling: souls from a lawn kill go to the
/// <b>owner of the unique actor</b> that made it, not to whoever owns the run.
///
/// <para><b>What was wrong.</b> <c>ApplySoulEarnFromActivityUnlocked</c> credited the run's player
/// unconditionally, and the register recorded the reason as the <c>ZombieKilled</c> fact carrying no
/// attribution. The fact does not — but the raw capture payload reaching that method does
/// (<c>killerPtr</c>), and the unique-actor path beside it was already using exactly that to award the
/// same kill's XP to the right specimen. Souls and XP were answering "who earned this?" differently.</para>
///
/// <para>These tests pin the credit MOVING, and the falsifier pins it NOT moving when the kill cannot
/// be attributed — without the second, the first would pass on a store that simply always credited the
/// specimen's owner and happened to agree.</para>
/// </summary>
public class LawnKillSoulCreditTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public LawnKillSoulCreditTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string Now() => DateTime.UtcNow.ToString("o");

    void Insert(string kind, string matchKey, object payload) =>
        _store.InsertEvent(new EventEnvelope
        {
            T = Now(), Kind = kind, MatchKey = matchKey, Payload = payload,
        });

    /// <summary>Starts a run and returns its match key.
    ///
    /// <para>The run is owned by the store's CURRENT player, not by anyone this test names:
    /// <c>InsertEvent</c> does not forward <c>EventEnvelope.PlayerId</c> — <c>explicitPlayerId</c> is a
    /// parameter of the internal insert and is only supplied by the web-ingest path. Discovered by this
    /// test failing on its own fixture, which is the right way round.</para></summary>
    string StartRun()
    {
        var matchKey = "m-souls-" + Guid.NewGuid().ToString("N");
        Insert("board.start", matchKey, new { levelName = "test", levelType = "adventure" });
        return matchKey;
    }

    /// <summary>Deploys a plant-side unique actor owned by <paramref name="ownerId"/> into the match and
    /// returns the ptr it is bound to — the ptr a kill will name as its killer.</summary>
    string DeployPlantKiller(long ownerId, string matchKey)
    {
        var actor = _store.CreateUniqueActor(ownerId, "plant", 3);
        var corr = "corr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryBeginUniqueDeploy(actor.InstanceId, corr, matchKey).Ok);
        var ptr = "ptr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryAckUniqueSpawn(corr, ptr, matchKey).Ok);
        return ptr;
    }

    /// <summary>The ruling: the specimen's owner is paid, and the run's owner is not.</summary>
    [Fact]
    public void A_kill_by_a_deployed_specimen_credits_that_specimens_owner_not_the_run_owner()
    {
        var runOwner = _store.GetCurrentPlayerId();
        var specimenOwner = _store.CreatePlayer("SpecimenOwner").Id;
        Assert.NotEqual(runOwner, specimenOwner);

        var matchKey = StartRun();
        var killerPtr = DeployPlantKiller(specimenOwner, matchKey);

        var runOwnerBefore = _store.GetSoulBalance(runOwner).Balance;
        var specimenOwnerBefore = _store.GetSoulBalance(specimenOwner).Balance;

        Insert("zombie.die", matchKey, new { ptr = "zombie-ptr-1", killerPtr, type = 1 });

        Assert.True(_store.GetSoulBalance(specimenOwner).Balance > specimenOwnerBefore,
            "the specimen's owner earned nothing — SR-18's credit did not move");
        Assert.Equal(runOwnerBefore, _store.GetSoulBalance(runOwner).Balance);
    }

    /// <summary>The falsifier. An ordinary vanilla plant is not a unique actor, so the kill cannot be
    /// attributed and the run's own player is paid exactly as before. Without this, the test above
    /// would pass on a store that always credited some other player.</summary>
    [Fact]
    public void An_unattributable_kill_still_credits_the_run_owner()
    {
        var runOwner = _store.GetCurrentPlayerId();
        var bystander = _store.CreatePlayer("Bystander").Id;
        Assert.NotEqual(runOwner, bystander);

        var matchKey = StartRun();
        DeployPlantKiller(bystander, matchKey); // bound, but NOT the killer named below

        var runOwnerBefore = _store.GetSoulBalance(runOwner).Balance;
        var bystanderBefore = _store.GetSoulBalance(bystander).Balance;

        Insert("zombie.die", matchKey, new { ptr = "zombie-ptr-2", killerPtr = "some-vanilla-plant", type = 1 });

        Assert.True(_store.GetSoulBalance(runOwner).Balance > runOwnerBefore,
            "an unattributable kill must still pay the run's player");
        Assert.Equal(bystanderBefore, _store.GetSoulBalance(bystander).Balance);
    }

    /// <summary>A kill naming no killer at all — the shape the capture emitted before `killerPtr`
    /// existed — is still paid to the run's player rather than dropped.</summary>
    [Fact]
    public void A_kill_with_no_killer_named_credits_the_run_owner()
    {
        var runOwner = _store.GetCurrentPlayerId();
        var matchKey = StartRun();
        var before = _store.GetSoulBalance(runOwner).Balance;

        Insert("zombie.die", matchKey, new { ptr = "zombie-ptr-3", type = 1 });

        Assert.True(_store.GetSoulBalance(runOwner).Balance > before);
    }

    /// <summary>save-identity SE4.25: the killer's own <c>EmpireRef</c> decides credit, not just its
    /// `player_id` — a Zomboss specimen of THIS save (same row, `empire_id='zomboss'`; no production
    /// path mints a plant-side AI specimen yet, SE4.28 wires the real injector ownership, so this
    /// stamps the one column that path will set) earns the save's human empire NO souls, and ingest
    /// never throws.</summary>
    [Fact]
    public void A_kill_by_a_non_human_empires_specimen_earns_no_souls_and_never_throws()
    {
        var runOwner = _store.GetCurrentPlayerId();
        var matchKey = StartRun();
        var killerPtr = DeployPlantKiller(runOwner, matchKey);
        using (var db = FusionRpg.Data.Sqlite.SqliteConnectionFactory.Open(_store.HotPath))
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "UPDATE rpg_unique_actors SET empire_id = 'zomboss' WHERE last_ptr = $ptr;";
            cmd.Parameters.AddWithValue("$ptr", killerPtr);
            cmd.ExecuteNonQuery();
        }

        var before = _store.GetSoulBalance(runOwner).Balance;

        var ex = Record.Exception(() =>
            Insert("zombie.die", matchKey, new { ptr = "zombie-ptr-se425", killerPtr, type = 1 }));

        Assert.Null(ex);
        Assert.Equal(before, _store.GetSoulBalance(runOwner).Balance);
    }
}
