using System;
using System.Collections.Generic;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// `empire-level` EP4.3 (module 13, `empire-progression` Wave D, ruling R19; spec:
/// `docs/architecture/empire-progression/spec-empire-level.md` "What feeds it"). An empire's level is
/// fed ONLY by its own species reaching a NEW highest level, once per `(species, level)` ever, through
/// the one `TryApplyXpUnlocked` every XP write already goes through.
///
/// <para>Every threshold assertion reads <see cref="RpgXpCurve.XpToNext"/> for the LOADED tuning and
/// every award assertion reads <see cref="RpgXpAwards"/>, never a literal — so a balance pass over
/// either curve moves the numbers here without turning this file red for the wrong reason.</para>
///
/// <para>Real roster ids (compiled default roster, the pair `SpeciesProgressionTests` documents): plant
/// GameTypeId 7 = 'fumeshroom' (CreatureTypeId 60007); zombie GameTypeId 3 = 'polevaulterzombie'
/// (CreatureTypeId 10003).</para>
/// </summary>
public class EmpireLevelTests : IDisposable
{
    const int FumeshroomGameTypeId = 7;
    const int FumeshroomCreatureTypeId = 60007;
    const int PolevaulterGameTypeId = 3;
    const int PolevaulterCreatureTypeId = 10003;

    /// <summary>
    /// The one tuning value this class overrides, and why it is safe: `awards.speciesLevelUp` has NO
    /// other reader in the tree — EP4.1 added it and the credit this file tests is its first consumer —
    /// so raising it from the shipped 1 to 25 changes no number any other class in this assembly
    /// observes. 25 also makes ONE credit cross two of the shipped `xpCurve.empire` steps at once (10,
    /// then 15), which is how the k-fan-out is reached. Every other value is the bootstrap's own working
    /// set, restored in <see cref="Dispose"/>. `SpeciesProgressionTuningHub` is deliberately NOT touched:
    /// deviating from the bootstrap there WOULD change what `RpgXpAwardMap` and `SpeciesProgressionTests`
    /// compute, and that contention has already bitten once this session.
    /// </summary>
    const long TestSpeciesLevelUpAward = 25;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public EmpireLevelTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        ProgressionTuningHub.Configure(ContractTuningTestBootstrap.DefaultProgression with
        {
            Awards = ContractTuningTestBootstrap.DefaultProgression.Awards with
            {
                SpeciesLevelUp = TestSpeciesLevelUpAward,
            },
        });
    }

    public void Dispose()
    {
        ProgressionTuningHub.Configure(ContractTuningTestBootstrap.DefaultProgression);
        _testStore.Dispose();
    }

    long StartRun(long playerId)
    {
        _store.SetCurrentPlayer(playerId);
        _store.InsertEvent(new EventEnvelope
        {
            T = DateTime.UtcNow.ToString("o"),
            Kind = "board.start",
            MatchKey = "m-" + Guid.NewGuid().ToString("N"),
            Payload = new { levelName = "test", levelType = "adventure" },
        });
        return _store.ListRuns(playerId).OrderByDescending(r => r.Id).First().Id;
    }

    (long PlayerId, long RunId) Seed(string name)
    {
        var player = _store.CreatePlayer(name);
        return (player.Id, StartRun(player.Id));
    }

    void Append(long playerId, long runId, string kind, string sourceKind, string sourceId,
        string payload, string dedupe) =>
        _store.AppendPvzActivityFact(playerId, new PvzActivityAppendRequest
        {
            Kind = kind, RunId = runId, SourceKind = sourceKind, SourceId = sourceId,
            PayloadJson = payload, DedupeKey = dedupe,
        });

    void PlacePlant(long playerId, long runId, string dedupe) =>
        Append(playerId, runId, PvzActivityKinds.PlantPlaced, "creature.progression.v1",
            "general:fumeshroom", $$"""{"type":{{FumeshroomGameTypeId}}}""", dedupe);

    /// <summary>`MatchEnded` is the run-completion term's own trigger — the second of the two species XP
    /// paths and the dominant one. Its award is emitted ONCE per `(run, species)`, so a caller wanting a
    /// second one needs a second RUN, not a second fact.</summary>
    void EndRun(long playerId, long runId, string dedupe) =>
        Append(playerId, runId, PvzActivityKinds.MatchEnded, "feature", "manual",
            """{"result":"victory"}""", dedupe);

    /// <summary>One whole lawn match: a fresh run, the species fielded in it, and the run resolved. That
    /// is one per-placement award plus one run-completion award.</summary>
    void PlayAMatch(long playerId, string tag)
    {
        var runId = StartRun(playerId);
        PlacePlant(playerId, runId, $"{tag}-place");
        EndRun(playerId, runId, $"{tag}-end");
    }

    RpgActorProgressionDto? Actor(long playerId, string kind, int typeId) =>
        _store.GetRpgActor(playerId, kind, typeId);

    RpgActorProgressionDto? Species(long playerId, int typeId) =>
        Actor(playerId, RpgActorKinds.Species, typeId);

    RpgActorProgressionDto? Empire(long playerId) => Actor(playerId, RpgActorKinds.Empire, 0);

    /// <summary>The empire's own XP ledger rows for the credit's reason, in COMMIT order (the page comes
    /// back newest-first, and the order the credits happened is part of what these tests assert).</summary>
    List<RpgXpLedgerEntryDto> EmpireLedger(long playerId) =>
        _store.ListRpgXpLedger(playerId, RpgActorKinds.Empire, 0, RpgXpReasons.EmpireSpeciesLevelUp)!
            .Items.OrderBy(r => r.Id).ToList();

    /// <summary>Placements needed to cross ONE species level from level 1, read from the loaded species
    /// curve and its configured placement award — never a literal count.</summary>
    int PlacementsForOneSpeciesLevel()
    {
        var award = SpeciesProgressionTuningHub.Tuning.PlacementAward;
        return (int)((RpgXpCurve.XpToNext(RpgActorKinds.Species, 1) + award - 1) / award);
    }

    [Fact]
    public void The_run_completion_path_credits_the_empire_for_each_new_species_level()
    {
        var (playerId, runId) = Seed("EmpireRunCompletion");
        PlacePlant(playerId, runId, "p-0");
        Assert.Null(Empire(playerId));   // one placement is short of the species threshold

        EndRun(playerId, runId, "end-0");

        var species = Species(playerId, FumeshroomCreatureTypeId);
        Assert.NotNull(species);
        Assert.True(species!.HighestLevel >= 2, "the run-completion award should cross a species level");

        var empire = Empire(playerId);
        Assert.NotNull(empire);
        var rows = EmpireLedger(playerId);
        // One row per species level crossed, each carrying the award, and the last row's own
        // `level_after` is the level the empire row now shows.
        Assert.Equal(species.HighestLevel - 1, rows.Count);
        Assert.All(rows, r => Assert.Equal(TestSpeciesLevelUpAward, r.Delta));
        Assert.Equal((species.HighestLevel - 1) * TestSpeciesLevelUpAward, rows.Sum(r => r.Delta));
        Assert.Equal(1, rows[0].LevelBefore);
        Assert.True(rows[0].LevelAfter > rows[0].LevelBefore,
            "the empire starts at level 1 with no XP, so its first credit always crosses at least one step");
        Assert.Equal(empire.Level, rows[^1].LevelAfter);
    }

    [Fact]
    public void The_per_placement_path_credits_the_empire_for_each_new_species_level()
    {
        var (playerId, runId) = Seed("EmpirePlacement");
        for (var i = 0; i < PlacementsForOneSpeciesLevel(); i++) PlacePlant(playerId, runId, $"p-{i}");

        var species = Species(playerId, FumeshroomCreatureTypeId);
        Assert.NotNull(species);
        Assert.True(species!.HighestLevel >= 2, "the placements should have crossed a species level");

        var rows = EmpireLedger(playerId);
        // One credit per species level. Where each credit LEAVES the empire is the loaded curve's
        // business (with this class's award a single credit can cross two steps), so the row is
        // asserted by its start and by where the empire ended up, never by an assumed step size.
        Assert.Equal(species.HighestLevel - 1, rows.Count);
        Assert.Equal(1, rows[0].LevelBefore);
        Assert.Equal(Empire(playerId)!.Level, rows[^1].LevelAfter);
        Assert.Equal(TestSpeciesLevelUpAward, rows[0].Delta);
    }

    /// <summary>
    /// spec test 4's claim — `k` crossings pay `k` times — proved on the side where it is reachable, and
    /// the reason the other side is not.
    ///
    /// <para><b>Why not "one award crossing `k` species levels".</b> The shipped species curve is
    /// `first = 60, step = 24` and its awards are 4 (a placement) and 100 (a resolved run): the two
    /// smallest consecutive thresholds sum to 60 + 84 = 144, above every award the fact vocabulary can
    /// produce, and they grow by 24 each step — so a single award can never cross two species levels by
    /// construction (that is the curve's own anti-grind shape, not an accident). Reaching it would mean
    /// reconfiguring `SpeciesProgressionTuningHub`, which would change what `RpgXpAwardMap` and
    /// `SpeciesProgressionTests` compute WHILE they run — the contention this file's own comment rejects.
    /// The same `k` fan-out is reachable one step along, on the empire's own row, and that is what this
    /// asserts: one credit that crosses k empire steps queues k events (and EP4.5 pays k grants).</para>
    /// </summary>
    [Fact]
    public void One_credit_that_crosses_several_empire_levels_queues_one_event_each()
    {
        var (playerId, runId) = Seed("EmpireKCrossing");
        PlacePlant(playerId, runId, "p-0");

        var result = _store.AppendPvzActivityFact(playerId, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchEnded, RunId = runId, SourceKind = "feature", SourceId = "manual",
            PayloadJson = """{"result":"victory"}""", DedupeKey = "end-0",
        });

        var empire = Empire(playerId)!;
        Assert.True(empire.Level >= 3, $"one {TestSpeciesLevelUpAward}-xp credit should cross two steps, landed on {empire.Level}");

        // The level crossings are queued ON the dirty the caller receives — never broadcast from inside
        // the transaction — which is the same envelope the host drains after commit.
        var queued = result.Progression.Where(d => d.Kind == RpgActorKinds.Empire).SelectMany(d => d.LevelUps ?? Array.Empty<RpgStore.EmpireLevelUpEvent>()).ToList();
        Assert.Equal(2, queued.Count);
        Assert.Equal(new[] { (1L, 2L), (2L, 3L) }, queued.Select(e => (e.LevelBefore, e.LevelAfter)).ToArray());
        Assert.All(queued, e => Assert.Equal(playerId, e.PlayerId));
        Assert.All(queued, e => Assert.Equal(RpgActorKinds.Empire, RpgActorKinds.Empire));   // the empire row's own token
        Assert.All(queued, e => Assert.True(e.EmpireId.Length > 0));
        // EP4.4: the grant the event carries is the HOST's wiring, read from the shipped
        // `freeRespecsPerEmpireLevel` (the bootstrap configures the same value this assembly's tests
        // mean to exercise) -- so this closes EP4.3's "empty grants until EP4.4" note.
        Assert.All(queued, e => Assert.Equal(
            new[] { EmpireLevelGrantKind.FreeEmpireRespec },
            e.Grants.Select(g => g.Kind).ToArray()));
        Assert.All(queued, e => Assert.All(e.Grants, g => Assert.Equal(1, g.Amount)));
        // The species award rows carry no crossings — only the empire kind queues them.
        Assert.All(result.Progression.Where(d => d.Kind != RpgActorKinds.Empire), d => Assert.Null(d.LevelUps));
    }

    /// <summary>
    /// spec test 2: a demote-and-reclimb credits nothing, EVEN AFTER the empire's own ledger rows are
    /// gone — which is what compaction (`TrimXpTailsCore`) leaves behind, and the reason the rule reads
    /// the species row's `highest_level` rather than the ledger's dedupe keys.
    ///
    /// <para>The demoted state is written directly because no shipped activity fact awards a species a
    /// NEGATIVE delta: a demote is a real state <c>RpgXpApply</c> produces, but nothing in the fact
    /// vocabulary produces it yet, so the test constructs the state the rule is about rather than
    /// pretending a fact could.</para>
    /// </summary>
    [Fact]
    public void A_demote_and_reclimb_credits_nothing_even_after_the_ledger_rows_are_trimmed()
    {
        var (playerId, runId) = Seed("EmpireDemote");
        PlacePlant(playerId, runId, "p-0");
        EndRun(playerId, runId, "end-0");

        var climbed = Empire(playerId)!;
        Assert.Single(EmpireLedger(playerId));
        var peak = Species(playerId, FumeshroomCreatureTypeId)!.HighestLevel;
        Assert.True(peak >= 2);

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            // The state a demote leaves: level drops, `highest_level` does NOT (RpgXpApply keeps it
            // monotonic on purpose), and the within-level xp resets.
            using var demote = db.CreateCommand();
            demote.CommandText = """
                UPDATE rpg_actor_progression SET level=$l, xp=0, demotion_count=demotion_count+1
                WHERE save_id=$p AND empire_id=$e AND kind='species' AND type_id=$tid;
                DELETE FROM rpg_xp_ledger WHERE save_id=$p AND empire_id=$e AND kind='empire';
                """;
            demote.Parameters.AddWithValue("$l", peak - 1);
            demote.Parameters.AddWithValue("$p", playerId);
            demote.Parameters.AddWithValue("$e", _store.HumanEmpireOf(playerId).Value);
            demote.Parameters.AddWithValue("$tid", FumeshroomCreatureTypeId);
            demote.ExecuteNonQuery();
        }

        Assert.Empty(EmpireLedger(playerId));   // the `sp:*` rows are gone, exactly as compaction leaves it

        // Re-climb to exactly the level the species already reached before. One match is enough at the
        // shipped curve (its run-completion award crosses the level the demote gave back) and going
        // further would raise `highest_level` legitimately, which is a different case.
        PlayAMatch(playerId, "reclimb-1");

        var species = Species(playerId, FumeshroomCreatureTypeId)!;
        Assert.Equal(peak, species.Level);          // reached the earlier peak again
        Assert.Equal(peak, species.HighestLevel);   // and never raised it -> nothing to credit
        Assert.Empty(EmpireLedger(playerId));
        Assert.Equal(climbed.Level, Empire(playerId)!.Level);
    }

    /// <summary>
    /// spec test 2b, ruling R1's side rule — re-pointed by `ai-empire-species` EP4.14. Before EP4.14 a
    /// zombie species row was owned by the HUMAN empire (today's old routing), levelled there, and must
    /// credit nothing on the human's EMPIRE because its own empire is Zomboss's. EP4.14 closed that
    /// circuit: the zombie species now credits Zomboss's own species row, so the human owns neither a
    /// zombie species row nor an empire row — and Zomboss's empire is where the credit lands.
    /// </summary>
    [Fact]
    public void A_human_owned_zombie_species_never_moves_the_humans_empire()
    {
        var (playerId, runId) = Seed("EmpireSideRule");
        Append(playerId, runId, PvzActivityKinds.ZombieSpawned, "creature.progression.v1",
            "general:polevaulterzombie", $$"""{"type":{{PolevaulterGameTypeId}}}""", "z-0");
        EndRun(playerId, runId, "end-0");

        // The human owns no zombie species row at all now, and no empire row either — not one that
        // merely failed to grow.
        Assert.Null(Species(playerId, PolevaulterCreatureTypeId));
        Assert.Null(Empire(playerId));

        // The zombie species levelled, on Zomboss's empire of this save, and HIS empire credits it.
        var save = new SaveId(playerId);
        Assert.True(_store.SpeciesLevelOf(save, EmpireId.Zomboss, PolevaulterCreatureTypeId) >= 2,
            "the zombie species row must have levelled, on Zomboss's own empire");
        var zombossEmpire = EmpireActor(playerId, EmpireId.Zomboss);
        Assert.NotNull(zombossEmpire);
        Assert.True(zombossEmpire!.Level > 1, "the level-up must credit Zomboss's own empire level");
    }

    /// <summary>One empire's actor row of this save (`kind='empire'`), through the store's own wide
    /// empire read — the public `GetRpgActor` resolves only the human empire.</summary>
    RpgActorProgressionDto? EmpireActor(long saveId, EmpireId empire)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        return _store.ReadEmpireActorUnlocked(
            db, new EmpireRef(new SaveId(saveId), empire), RpgActorKinds.Empire, 0);
    }

    /// <summary>
    /// spec test 2c: a rolled-back transaction credits nothing. The tuning here has NO `xpCurve.empire`,
    /// so the credit throws INSIDE the append's transaction — after the species row update and its ledger
    /// insert have already run — and the rollback has to discard real work. That the species row keeps the
    /// value it had BEFORE the failed append is the proof that the credit runs in the SAME transaction as
    /// the species level that paid for it, and that there is nothing to broadcast: `AppendPvzActivityFact`
    /// rolls back and throws, so it returns no dirties at all (the queue EP4.7 drains is the returned
    /// dirty, never a store field that could survive a rollback).
    /// </summary>
    [Fact]
    public void A_rolled_back_transaction_credits_nothing_and_queues_nothing()
    {
        var (playerId, runId) = Seed("EmpireRollback");
        PlacePlant(playerId, runId, "p-0");
        var speciesBefore = Species(playerId, FumeshroomCreatureTypeId)!;

        ProgressionTuningHub.Configure(new ProgressionTuning(
            SchemaVersion: 1, Version: 3,
            PlantCurve: new XpCurveParams(80, 32), ZombieCurve: new XpCurveParams(70, 28),
            PlayerCurve: new XpCurveParams(100, 45), SpecimenCurve: new XpCurveParams(100, 45),
            Awards: new XpAwardsTuning(Kill: 12, Defeat: -100, Mower: -30, PlantPlace: 8, ZombieSpawn: 9)
            {
                SpeciesLevelUp = TestSpeciesLevelUpAward,
            }));

        var rejection = Assert.Throws<ProgressionTuningRejection>(
            () => EndRun(playerId, runId, "end-0"));
        Assert.Contains("empire", rejection.Message, StringComparison.Ordinal);

        var speciesAfter = Species(playerId, FumeshroomCreatureTypeId)!;
        Assert.Equal(speciesBefore.Level, speciesAfter.Level);
        Assert.Equal(speciesBefore.Xp, speciesAfter.Xp);
        Assert.Equal(speciesBefore.HighestLevel, speciesAfter.HighestLevel);
        Assert.Null(Empire(playerId));
        // Nothing from the failed append survived, including the run-completion award's own ledger row.
        Assert.DoesNotContain(_store.ListRpgXpLedger(playerId, null, null, null)!.Items,
            r => r.Reason == RpgXpReasons.SpeciesRunComplete);
    }

    /// <summary>spec test 3: a replayed fact changes neither the species nor the empire row.</summary>
    [Fact]
    public void A_replayed_fact_changes_neither_the_species_nor_the_empire()
    {
        var (playerId, runId) = Seed("EmpireReplay");
        var placements = PlacementsForOneSpeciesLevel();
        for (var i = 0; i < placements; i++) PlacePlant(playerId, runId, $"p-{i}");
        var speciesXp = Species(playerId, FumeshroomCreatureTypeId)!;
        var empire = Empire(playerId)!;
        var rows = EmpireLedger(playerId).Count;

        PlacePlant(playerId, runId, $"p-{placements - 1}");   // the same fact again

        Assert.Equal(speciesXp.Xp, Species(playerId, FumeshroomCreatureTypeId)!.Xp);
        Assert.Equal(speciesXp.Level, Species(playerId, FumeshroomCreatureTypeId)!.Level);
        Assert.Equal(empire.Xp, Empire(playerId)!.Xp);
        Assert.Equal(empire.Level, Empire(playerId)!.Level);
        Assert.Equal(rows, EmpireLedger(playerId).Count);
    }

    /// <summary>
    /// spec test 5, the curve half: the empire levels exactly when `RpgXpCurve.XpToNext(Empire, L)` says,
    /// for L read from the LOADED tuning. Each match's whole empire gain is replayed through the
    /// thresholds that curve publishes, so a private `f(level)` anywhere would fail this without a
    /// literal in sight. The loop deliberately walks both branches — a match that crosses a step and one
    /// that only accrues toward it.
    /// </summary>
    [Fact]
    public void The_empire_levels_exactly_when_the_loaded_curve_says()
    {
        var (playerId, _) = Seed("EmpireCurve");
        var level = 1L;
        var within = 0L;

        for (var match = 0; match < 4; match++)
        {
            var speciesBefore = Species(playerId, FumeshroomCreatureTypeId)?.HighestLevel ?? 1;
            PlayAMatch(playerId, $"match-{match}");
            var speciesAfter = Species(playerId, FumeshroomCreatureTypeId)!.HighestLevel;

            var gained = (speciesAfter - speciesBefore) * RpgXpAwards.SpeciesLevelUp;
            Assert.True(gained > 0, "every match must cross at least one species level for this test to mean anything");

            // The one shared ladder, read from the loaded tuning: fold the gain through the thresholds.
            within += gained;
            while (within >= RpgXpCurve.XpToNext(RpgActorKinds.Empire, level))
            {
                within -= RpgXpCurve.XpToNext(RpgActorKinds.Empire, level);
                level++;
            }

            var empire = Empire(playerId);
            Assert.NotNull(empire);
            Assert.Equal(level, empire!.Level);
            Assert.Equal(within, empire.Xp);
        }

        Assert.True(level >= 2, "four matches should have crossed the empire's first step");
    }

    // ---- EP4.6: the store-start backfill ---------------------------------------------------------

    /// <summary>The state a save from BEFORE this feature is in: species levels, no `kind = 'empire'`
    /// row, and none of the credit's own `sp:*` ledger rows — which is exactly what the credit would
    /// have left had it never existed.</summary>
    void EraseEmpireRows(long playerId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            DELETE FROM rpg_actor_progression WHERE save_id=$p AND kind='empire';
            DELETE FROM rpg_xp_ledger WHERE save_id=$p AND kind='empire';
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.ExecuteNonQuery();
    }

    static (long Level, long Xp, long Highest) EmpireRow(RpgStore store, long playerId)
    {
        var row = store.GetRpgActor(playerId, RpgActorKinds.Empire, 0)!;
        return (row.Level, row.Xp, row.HighestLevel);
    }

    static (long Before, long After, long Delta)[] EmpireCreditRows(RpgStore store, long playerId) =>
        store.ListRpgXpLedger(playerId, RpgActorKinds.Empire, 0, RpgXpReasons.EmpireSpeciesLevelUp)!
            .Items.OrderBy(r => r.Id).Select(r => (r.LevelBefore, r.LevelAfter, r.Delta)).ToArray();

    /// <summary>
    /// spec test 9, first half: for a seeded store the backfill produces the same empire row and grants
    /// as live crediting. Proven by ERASING what live play produced and letting a fresh store start
    /// rebuild it — the strongest available comparison, because the live row is not re-derived from the
    /// same code path in the assertion itself.
    /// </summary>
    [Fact]
    public void The_backfill_produces_what_live_play_credited()
    {
        var (playerId, _) = Seed("EmpireBackfillEqualsLive");
        PlayAMatch(playerId, "match-0");
        PlayAMatch(playerId, "match-1");
        var live = EmpireRow(_store, playerId);
        var liveCredits = EmpireCreditRows(_store, playerId);
        Assert.True(live.Level > 1, $"live play should have levelled the empire, landed on {live.Level}");
        Assert.NotEmpty(liveCredits);

        EraseEmpireRows(playerId);
        Assert.Null(_store.GetRpgActor(playerId, RpgActorKinds.Empire, 0));

        var reopened = _testStore.Reopen();   // "the next store start"
        Assert.Equal(live, EmpireRow(reopened, playerId));
        Assert.Equal(liveCredits, EmpireCreditRows(reopened, playerId));
    }

    /// <summary>
    /// spec test 9, second half: a second start changes nothing — including after the empire's own ledger
    /// rows are gone, which is what compaction leaves. The pass keys off the empire ROW, so once that row
    /// exists the backfill never runs for that empire again.
    /// </summary>
    [Fact]
    public void A_second_start_changes_nothing_even_after_the_empires_ledger_rows_are_deleted()
    {
        var (playerId, _) = Seed("EmpireBackfillIdempotent");
        PlayAMatch(playerId, "match-0");
        var first = EmpireRow(_store, playerId);

        var second = _testStore.Reopen();
        Assert.Equal(first, EmpireRow(second, playerId));

        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        {
            using var trim = db.CreateCommand();
            trim.CommandText = "DELETE FROM rpg_xp_ledger WHERE save_id=$p AND kind='empire';";
            trim.Parameters.AddWithValue("$p", playerId);
            trim.ExecuteNonQuery();
        }

        var third = _testStore.Reopen();
        Assert.Equal(first, EmpireRow(third, playerId));
        Assert.Empty(EmpireCreditRows(third, playerId));   // nothing was re-paid
    }

    /// <summary>
    /// spec test 9, third half: a pass that throws leaves no empire row, and the next start completes it.
    /// The throw is real — a tuning with no `xpCurve.empire` makes the credit refuse — and because the
    /// backfill runs ONE TRANSACTION PER EMPIRE, the rollback leaves the empire row absent rather than
    /// half-built.
    /// </summary>
    [Fact]
    public void A_pass_that_throws_leaves_no_empire_row_and_the_next_start_completes_it()
    {
        var (playerId, _) = Seed("EmpireBackfillThrow");
        PlayAMatch(playerId, "match-0");
        EraseEmpireRows(playerId);

        try
        {
            ProgressionTuningHub.Configure(new ProgressionTuning(
                SchemaVersion: 1, Version: 3,
                PlantCurve: new XpCurveParams(80, 32), ZombieCurve: new XpCurveParams(70, 28),
                PlayerCurve: new XpCurveParams(100, 45), SpecimenCurve: new XpCurveParams(100, 45),
                Awards: new XpAwardsTuning(Kill: 12, Defeat: -100, Mower: -30, PlantPlace: 8, ZombieSpawn: 9)
                {
                    SpeciesLevelUp = TestSpeciesLevelUpAward,
                }));

            Assert.Throws<ProgressionTuningRejection>(() => _testStore.Reopen());
        }
        finally
        {
            ProgressionTuningHub.Configure(ContractTuningTestBootstrap.DefaultProgression with
            {
                Awards = ContractTuningTestBootstrap.DefaultProgression.Awards with
                {
                    SpeciesLevelUp = TestSpeciesLevelUpAward,
                },
            });
        }

        Assert.Null(_store.GetRpgActor(playerId, RpgActorKinds.Empire, 0));   // no half-built row

        var reopened = _testStore.Reopen();                                    // the next start completes it
        Assert.NotNull(reopened.GetRpgActor(playerId, RpgActorKinds.Empire, 0));
        Assert.NotEmpty(EmpireCreditRows(reopened, playerId));
    }
}
