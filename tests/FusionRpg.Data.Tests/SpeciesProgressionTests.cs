using System;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Activity;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>`species-build` T1.1/T1.2/T1.3 (module 3, `species-xp`) — the storage half. Species
/// levelling reuses `rpg_actor_progression`/`rpg_xp_ledger` via <c>kind='species'</c>
/// (spec-species-xp.md §1 Option A, confirmed against this exact file before committing: `type_id`
/// carries <c>CreatureSpeciesDef.CreatureTypeId</c>, already unique per species). The pure curve/tuning
/// surface is covered in `FusionRpg.Core.Tests.Progression.SpeciesProgressionTests`; this file proves
/// the lawn projection (T1.2), the run-completion term (T1.3), and the `!pvzGame` conditional's two
/// directions. T1.4 (the expedition/game-closed source) lives in its own file next to
/// `RpgStore.Expeditions.cs`.
///
/// <para>Real roster ids used below (compiled default, `CreatureSpeciesCatalog.ConfigureFromCompiledDefault`,
/// confirmed against `CreatureSpeciesCatalog.Generated.cs`): plant GameTypeId 7 = 'fumeshroom'
/// (CreatureTypeId 60007); zombie GameTypeId 3 = 'polevaulterzombie' (CreatureTypeId 10003) — the SAME
/// GameTypeId as plant 'wallnut' but on a different <c>Side</c>, so the two keys never collide.</para>
/// </summary>
public class SpeciesProgressionTests : IDisposable
{
    const int FumeshroomGameTypeId = 7;
    const int FumeshroomCreatureTypeId = 60007;
    const int PolevaulterGameTypeId = 3;
    const int PolevaulterCreatureTypeId = 10003;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SpeciesProgressionTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>SE4.21: facts name a run that resolves to their own save — a real run, not a
    /// placeholder id. A fact whose run resolves nowhere awards nothing, so these tests start one.</summary>
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
        return _store.ListRuns(playerId).Single().Id;
    }

    [Fact]
    public void PlantPlaced_levels_the_species_row_matching_LawnElementIndexs_own_answer()
    {
        var player = _store.CreatePlayer("SpeciesLawn");
        var runId = StartRun(player.Id);
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.PlantPlaced,
            RunId = runId,
            SourceKind = "creature.progression.v1", SourceId = "general:fumeshroom",
            PayloadJson = """{"type":7}""",
            DedupeKey = "place-1"
        });

        var species = _store.GetRpgActor(player.Id, RpgActorKinds.Species, FumeshroomCreatureTypeId);
        Assert.NotNull(species);
        // A single placement is well below the L1->L2 threshold (60 xp, 4 xp/placement) -- proves
        // the row exists and accrued XP without needing a level-up in this assertion.
        Assert.Equal(1, species!.Level);
        Assert.Equal(4, species.Xp);

        // The species resolved must match LawnElementIndex's own answer for (Side, GameTypeId).
        var index = new LawnElementIndex(CreatureSpeciesCatalog.All);
        Assert.True(index.TryGet("plant", FumeshroomGameTypeId, out var expected));
        Assert.Equal(FumeshroomCreatureTypeId, expected.CreatureTypeId);

        // The existing plant-TYPE row (kind='plant') stays untouched alongside it.
        var plantType = _store.GetRpgActor(player.Id, RpgActorKinds.Plant, FumeshroomGameTypeId);
        Assert.NotNull(plantType);
    }

    [Fact]
    public void PlantPlaced_idempotent_the_same_fact_ingested_twice_levels_once()
    {
        var player = _store.CreatePlayer("SpeciesIdempotent");
        var runId = StartRun(player.Id);
        var req = new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.PlantPlaced,
            RunId = runId,
            SourceKind = "creature.progression.v1", SourceId = "general:fumeshroom",
            PayloadJson = """{"type":7}""",
            DedupeKey = "same-fact"
        };
        _store.AppendPvzActivityFact(player.Id, req);
        _store.AppendPvzActivityFact(player.Id, req);

        var species = _store.GetRpgActor(player.Id, RpgActorKinds.Species, FumeshroomCreatureTypeId);
        Assert.NotNull(species);
        Assert.Equal(4, species!.Xp); // one placement's worth, not two
    }

    [Fact]
    public void ZombieSpawned_levels_Zomboss_empires_species_row_not_the_humans()
    {
        // ai-empire-species EP4.14 (R1): a zombie species is Zomboss's. Before R1 this fact credited
        // the human player's row (`GetRpgActor(playerId, Species, ...)`); under R1 it credits
        // (SaveId, EmpireId.Zomboss) and the human keeps no zombie row at all.
        var player = _store.CreatePlayer("SpeciesZombieLawn");
        var runId = StartRun(player.Id);
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.ZombieSpawned,
            RunId = runId,
            SourceKind = "creature.progression.v1", SourceId = "general:polevaulterzombie",
            PayloadJson = """{"type":3}""",
            DedupeKey = "spawn-1"
        });

        var zomboss = EmpireSpecies(player.Id, EmpireId.Zomboss.Value, PolevaulterCreatureTypeId);
        Assert.Equal(4, zomboss.Xp); // one placement's worth
        // The human's old zombie row is not created by this fact (and, for a pre-R1 save, never
        // rewritten) — the reader's own level-1 default is the whole answer.
        Assert.Null(_store.GetRpgActor(player.Id, RpgActorKinds.Species, PolevaulterCreatureTypeId));
        Assert.Equal(1, _store.SpeciesLevelOf(
            new SaveId(player.Id), EmpireId.Zomboss, PolevaulterCreatureTypeId));
    }

    /// <summary>One empire's species row of this save, through the store's own wide empire read — the
    /// public <c>GetRpgActor</c> resolves only the human empire.</summary>
    RpgActorProgressionDto EmpireSpecies(long saveId, string empireId, int creatureTypeId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        return _store.ReadEmpireActorUnlocked(
            db, new EmpireRef(new SaveId(saveId), new EmpireId(empireId)),
            RpgActorKinds.Species, creatureTypeId)!;
    }

    [Fact]
    public void RunCompletion_fires_exactly_once_per_run_however_many_times_placed()
    {
        var player = _store.CreatePlayer("SpeciesRunOnce");
        var runId = StartRun(player.Id);
        for (var i = 0; i < 5; i++)
        {
            _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
            {
                Kind = PvzActivityKinds.PlantPlaced,
                RunId = runId,
                SourceKind = "creature.progression.v1", SourceId = "general:fumeshroom",
                PayloadJson = """{"type":7}""",
                DedupeKey = $"place-{i}"
            });
        }
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchEnded,
            RunId = runId,
            PayloadJson = """{"result":"victory"}""",
            DedupeKey = "match-end-1"
        });

        var species = _store.GetRpgActor(player.Id, RpgActorKinds.Species, FumeshroomCreatureTypeId);
        Assert.NotNull(species);
        // 5 placements x 4 + ONE run-completion x 100 = 120 TOTAL EARNED, never 5x100=500 (would
        // mean it fired per placement) and never <120 (would mean the run term never fired at all).
        // Raw `.Xp` alone under-reports this once a level-up consumes XP (100 alone crosses the L1
        // threshold of 60) -- TotalEarned reconstructs lifetime XP via the curve's own cumulative sum.
        Assert.Equal(20 + 100, TotalEarned(species!));
    }

    [Fact]
    public void RunCompletion_replayed_MatchEnded_never_double_pays()
    {
        var player = _store.CreatePlayer("SpeciesRunReplay");
        var runId = StartRun(player.Id);
        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.PlantPlaced, RunId = runId, SourceKind = "creature.progression.v1", SourceId = "general:fumeshroom",
            PayloadJson = """{"type":7}""", DedupeKey = "place-1"
        });
        var matchEnd = new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchEnded, RunId = runId, PayloadJson = """{"result":"victory"}""",
            DedupeKey = "match-end-1"
        };
        _store.AppendPvzActivityFact(player.Id, matchEnd);
        _store.AppendPvzActivityFact(player.Id, matchEnd); // replay of the identical fact

        var species = _store.GetRpgActor(player.Id, RpgActorKinds.Species, FumeshroomCreatureTypeId);
        Assert.Equal(4 + 100, TotalEarned(species!));
    }

    [Fact]
    public void RunCompletion_outEarns_a_plausible_heavy_match_of_placements_at_the_shipped_ratio()
    {
        // Mirrors Core.Tests' RunAward_outEarnsAPlausibleHeavyMatchOfPlacements, but through the real
        // Data-layer path end to end -- 20 placements of the SAME species, then one match end.
        var player = _store.CreatePlayer("SpeciesRunDominance");
        var runId = StartRun(player.Id);
        for (var i = 0; i < 20; i++)
        {
            _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
            {
            Kind = PvzActivityKinds.PlantPlaced, RunId = runId, SourceKind = "creature.progression.v1", SourceId = "general:fumeshroom",
            PayloadJson = """{"type":7}""",
                DedupeKey = $"heavy-{i}"
            });
        }
        var placementsOnlyXp = TotalEarned(_store.GetRpgActor(player.Id, RpgActorKinds.Species, FumeshroomCreatureTypeId)!);

        _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.MatchEnded, RunId = runId, PayloadJson = """{"result":"victory"}""",
            DedupeKey = "heavy-match-end"
        });
        var totalXp = TotalEarned(_store.GetRpgActor(player.Id, RpgActorKinds.Species, FumeshroomCreatureTypeId)!);
        var runAward = totalXp - placementsOnlyXp;

        Assert.True(runAward > placementsOnlyXp,
            $"the single run-completion award ({runAward}) must out-earn 20 placements ({placementsOnlyXp})");
    }

    [Fact]
    public void WebMode_run_does_not_level_the_PvZ_type_but_DOES_level_the_species()
    {
        // species-build T1.2's ⛔ callout, both directions: `!pvzGame` still protects the PvZ almanac
        // TYPE row, but must not widen to also skip the species row (a species is not a PvZ almanac
        // type). Driven through InsertEvent so `e.Game` actually reaches IsPvzGame(...) as false.
        var player = _store.CreatePlayer("SpeciesWebMode");
        _store.SetCurrentPlayer(player.Id);
        const string matchKey = "web-run-1";
        // A run only exists once a board.start event creates it; plant.place must share its
        // matchKey to resolve the SAME runId (InsertOneUnlocked's own FindRunId(matchKey) path) --
        // otherwise Project(...) is never reached at all (ProjectGlobal only handles catalog kinds).
        _store.InsertEvent(new EventEnvelope
        {
            Game = RpgConstants.GameIdWebRpg, Kind = "board.start", MatchKey = matchKey,
            Payload = new { }
        });
        _store.InsertEvent(new EventEnvelope
        {
            Game = RpgConstants.GameIdWebRpg, Kind = "plant.place", MatchKey = matchKey,
            Payload = new
            {
                type = FumeshroomGameTypeId,
                sourceKind = CreatureProgressionSource.ContractKind,
                sourceId = "general:fumeshroom"
            }
        });

        var plantType = _store.GetRpgActor(player.Id, RpgActorKinds.Plant, FumeshroomGameTypeId);
        Assert.Null(plantType); // the existing rule holds: web-mode never levels a PvZ almanac type

        var species = _store.GetRpgActor(player.Id, RpgActorKinds.Species, FumeshroomCreatureTypeId);
        Assert.NotNull(species); // the new rule: a species row is not a PvZ almanac type
        Assert.Equal(4, species!.Xp);
    }

    [Fact]
    public void Capture_with_malformed_or_contradictory_source_claim_fails_closed()
    {
        var player = _store.CreatePlayer("SpeciesInvalidClaim");
        _store.SetCurrentPlayer(player.Id);
        const string matchKey = "invalid-source-claim";
        _store.InsertEvent(new EventEnvelope
        {
            Game = RpgConstants.GameIdWebRpg, Kind = "board.start", MatchKey = matchKey,
            Payload = new { }
        });
        _store.InsertEvent(new EventEnvelope
        {
            Game = RpgConstants.GameIdWebRpg, Kind = "plant.place", MatchKey = matchKey,
            Payload = new
            {
                type = FumeshroomGameTypeId,
                sourceKind = CreatureProgressionSource.ContractKind,
                sourceId = "unique:not-a-general-claim"
            }
        });

        var fact = _store.ListPvzActivityFacts(player.Id)!.Items.Single(x => x.Kind == PvzActivityKinds.PlantPlaced);
        Assert.Equal("untrusted", fact.SourceKind);
        Assert.Equal("invalid-claim", fact.SourceId);
        Assert.Null(_store.GetRpgActor(player.Id, RpgActorKinds.Species, FumeshroomCreatureTypeId));
    }

    [Fact]
    public void Capture_with_only_an_opaque_injector_source_does_not_infer_empire_species()
    {
        var player = _store.CreatePlayer("SpeciesOpaqueSource");
        _store.SetCurrentPlayer(player.Id);
        const string matchKey = "opaque-source";
        _store.InsertEvent(new EventEnvelope
        {
            Game = RpgConstants.GameIdWebRpg, Kind = "board.start", MatchKey = matchKey,
            Payload = new { }
        });
        _store.InsertEvent(new EventEnvelope
        {
            Game = RpgConstants.GameIdWebRpg, Kind = "plant.place", MatchKey = matchKey,
            Payload = new { type = FumeshroomGameTypeId, source = "start" }
        });

        var fact = _store.ListPvzActivityFacts(player.Id)!.Items.Single(x => x.Kind == PvzActivityKinds.PlantPlaced);
        Assert.Equal("capture", fact.SourceKind);
        Assert.Equal("plant.place", fact.SourceId);
        Assert.Null(_store.GetRpgActor(player.Id, RpgActorKinds.Species, FumeshroomCreatureTypeId));
    }

    [Fact]
    public void Collision_loser_is_skipped_by_the_lawn_without_throwing_winner_still_levels()
    {
        // spec-species-xp.md's own LawnElementIndex doc comment: (Side, GameTypeId) is not guaranteed
        // unique, and the collision resolves deterministically (lowest SpeciesId wins) rather than
        // crashing. T1.2's own acceptance: the loser must not be permanently unlevellable -- it just
        // isn't reachable through THIS source, which this test proves is a silent skip, not a throw.
        var winner = MakeSpecies("aaa-winner", creatureTypeId: 70001, side: "plant", gameTypeId: 999);
        var loser = MakeSpecies("zzz-loser", creatureTypeId: 70002, side: "plant", gameTypeId: 999);

        using var _ = CreatureSpeciesCatalog.UseScoped(new[] { winner, loser });

        var player = _store.CreatePlayer("SpeciesCollision");
        var runId = StartRun(player.Id);
        var ex = Record.Exception(() => _store.AppendPvzActivityFact(player.Id, new PvzActivityAppendRequest
        {
            Kind = PvzActivityKinds.PlantPlaced, RunId = runId, SourceKind = "creature.progression.v1", SourceId = "general:aaa-winner",
            PayloadJson = """{"type":999}""",
            DedupeKey = "collide-1"
        }));
        Assert.Null(ex);

        Assert.NotNull(_store.GetRpgActor(player.Id, RpgActorKinds.Species, winner.CreatureTypeId));
        Assert.Null(_store.GetRpgActor(player.Id, RpgActorKinds.Species, loser.CreatureTypeId));
    }

    /// <summary>Reconstructs lifetime XP earned from a snapshot's (Level, Xp) pair via
    /// <see cref="RpgXpCurve.TotalToReach"/>'s own cumulative sum — raw `.Xp` alone under-reports
    /// total earnings once a level-up has consumed some of it, which every run-completion assertion
    /// in this file needs to account for (the 100-xp run award alone crosses the 60-xp L1 threshold).</summary>
    static long TotalEarned(RpgActorProgressionDto dto) =>
        RpgXpCurve.TotalToReach(RpgActorKinds.Species, dto.Level) + dto.Xp;

    static CreatureSpeciesDef MakeSpecies(string speciesId, int creatureTypeId, string side, int gameTypeId) => new()
    {
        SpeciesId = speciesId,
        Name = speciesId,
        Side = side,
        GameTypeId = gameTypeId,
        CreatureTypeId = creatureTypeId,
        ElementPrimary = FusionRpg.Core.Stats.Derived.ElementTypeId.Fire,
        ElementSecondary = null,
        BaseRarity = CreatureRarity.Chaff,
        DeployMode = CreatureDeployMode.PlantAvatar,
        Acquisition = CreatureAcquisition.Summonable,
        Variants = new[] { "normal" },
        TraitPool = Array.Empty<string>()
    };
}
