using FusionRpg.Data;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

[Trait("VerificationId", "data.onboarding")]
public sealed class OnboardingCheckpointStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public OnboardingCheckpointStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void Player_one_starts_with_an_empty_checkpoint_view()
    {
        var player = _store.GetCurrentPlayer();

        var rows = _store.ListOnboardingCheckpoints(player!.Id);

        Assert.NotNull(rows);
        Assert.Empty(rows!);

        var stories = _store.ListOnboardingStories(player.Id);
        var story = Assert.Single(stories!);
        Assert.Equal("rift-prologue", story.StoryId);
        Assert.Equal(1, story.Version);
        Assert.Equal("unseen", story.State);
        Assert.Null(story.Outcome);
        Assert.True(story.Eligible);
    }

    [Fact]
    public void Story_acknowledgement_survives_store_restart_without_creating_a_checkpoint()
    {
        var player = _store.GetCurrentPlayer()!;
        var ack = _store.AcknowledgeOnboardingStory(player.Id, "rift-prologue", 1, "completed");

        Assert.True(ack.Ok);
        Assert.Equal(2, ack.Row!.Revision);
        Assert.Empty(_store.ListOnboardingCheckpoints(player.Id)!);

        using var reopened = _testStore.Reopen();
        var story = Assert.Single(reopened.ListOnboardingStories(player.Id)!);
        Assert.Equal("acknowledged", story.State);
        Assert.Equal("completed", story.Outcome);
        Assert.Equal(2, story.Revision);
        Assert.False(story.Eligible);
        Assert.Empty(reopened.ListOnboardingCheckpoints(player.Id)!);
    }

    [Fact]
    public void Existing_settled_victory_backfills_story_as_skipped_and_first_victory_skips_unseen_story()
    {
        var player = _store.GetCurrentPlayer()!;
        var match = "story-skip-" + Guid.NewGuid().ToString("N");
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                Game = RpgConstants.GameId, Kind = "board.start", MatchKey = match,
                T = "2026-01-01T00:00:00Z", Payload = new { levelName = "lawn", levelType = "classic", boardLevel = 1 }
            },
            new EventEnvelope
            {
                Game = RpgConstants.GameId, Kind = "match.result", MatchKey = match,
                T = "2026-01-01T00:01:00Z", Payload = new { result = "victory" }
            }
        });

        var story = Assert.Single(_store.ListOnboardingStories(player.Id)!);
        Assert.Equal("acknowledged", story.State);
        Assert.Equal("skipped", story.Outcome);
        Assert.False(story.Eligible);
        Assert.Single(_store.ListOnboardingCheckpoints(player.Id)!);

        // A second delivery is replay-safe: it cannot change the story revision or add a checkpoint.
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                Game = RpgConstants.GameId, Kind = "match.result", MatchKey = match,
                T = "2026-01-01T00:01:00Z", Payload = new { result = "victory" }
            }
        });
        var replay = Assert.Single(_store.ListOnboardingStories(player.Id)!);
        Assert.Equal(story.Revision, replay.Revision);
        Assert.Single(_store.ListOnboardingCheckpoints(player.Id)!);
    }

    [Fact]
    public void Legacy_unseen_story_repairs_after_run_backfill_without_overwriting_an_explicit_outcome()
    {
        var player = _store.GetCurrentPlayer()!;
        var match = "story-legacy-repair-" + Guid.NewGuid().ToString("N");
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                Game = RpgConstants.GameId, Kind = "board.start", MatchKey = match,
                T = "2026-01-04T00:00:00Z", Payload = new { levelName = "lawn", levelType = "classic", boardLevel = 1 }
            },
            new EventEnvelope
            {
                Game = RpgConstants.GameId, Kind = "match.result", MatchKey = match,
                T = "2026-01-04T00:01:00Z", Payload = new { result = "victory" }
            }
        });

        // Simulate the historic ordering bug: story bootstrap ran before legacy run rows had
        // acquired their player id, leaving an unseen row beside an already-settled victory.
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var reset = db.CreateCommand())
        {
            reset.CommandText = """
                UPDATE rpg_onboarding_story
                SET state='unseen', outcome=NULL, acknowledged_utc=NULL, revision=1
                WHERE player_id=$player AND story_id='rift-prologue' AND version=1;
                """;
            reset.Parameters.AddWithValue("$player", player.Id);
            Assert.Equal(1, reset.ExecuteNonQuery());
        }

        var repaired = Assert.Single(_store.ListOnboardingStories(player.Id)!);
        Assert.Equal("acknowledged", repaired.State);
        Assert.Equal("skipped", repaired.Outcome);
        Assert.False(repaired.Eligible);

        // The migration repair is deliberately narrow: it must never replace a durable player
        // choice just because that player later has a settled PvZ victory.
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var completed = db.CreateCommand())
        {
            completed.CommandText = """
                UPDATE rpg_onboarding_story
                SET state='acknowledged', outcome='completed', acknowledged_utc='2026-01-04T00:02:00Z', revision=9
                WHERE player_id=$player AND story_id='rift-prologue' AND version=1;
                """;
            completed.Parameters.AddWithValue("$player", player.Id);
            Assert.Equal(1, completed.ExecuteNonQuery());
        }

        var preserved = Assert.Single(_store.ListOnboardingStories(player.Id)!);
        Assert.Equal("acknowledged", preserved.State);
        Assert.Equal("completed", preserved.Outcome);
        Assert.Equal(9, preserved.Revision);
    }

    [Fact]
    public void Active_pvz_run_hides_eligible_story_without_acknowledging_it()
    {
        var player = _store.GetCurrentPlayer()!;
        _store.InsertEvents(new[]
        {
            new EventEnvelope
            {
                Game = RpgConstants.GameId, Kind = "board.start", MatchKey = "story-active",
                T = "2026-01-02T00:00:00Z", Payload = new { levelName = "lawn", levelType = "classic", boardLevel = 1 }
            }
        });

        var story = Assert.Single(_store.ListOnboardingStories(player.Id)!);
        Assert.Equal("unseen", story.State);
        Assert.False(story.Eligible);
        Assert.Null(story.Outcome);
    }

    [Fact]
    public void Earn_is_idempotent_and_preserves_the_first_reward_payload()
    {
        var player = _store.GetCurrentPlayer()!;
        var first = _store.TryEarnOnboardingCheckpoint(player.Id, "first-win-dave", 12, "soul:12", "{\"souls\":20}", "2026-01-01T00:00:00Z");
        var replay = _store.TryEarnOnboardingCheckpoint(player.Id, "first-win-dave", 99, "soul:99", "{\"souls\":99}", "2026-01-02T00:00:00Z");

        Assert.True(first);
        Assert.False(replay);
        var row = Assert.Single(_store.ListOnboardingCheckpoints(player.Id)!);
        Assert.Equal(12, row.EarnedRunId);
        Assert.Equal("soul:12", row.RewardRef);
        Assert.Equal("{\"souls\":20}", row.PayloadJson);
        Assert.Equal("earned", row.State);
        Assert.Equal(1, row.Revision);
    }

    [Fact]
    public void Claim_acknowledges_only_an_earned_row_and_replays_without_writing_again()
    {
        var player = _store.GetCurrentPlayer()!;
        _store.TryEarnOnboardingCheckpoint(player.Id, "first-win-dave", 12, "soul:12", "{}", "2026-01-01T00:00:00Z");

        var claimed = _store.ClaimOnboardingCheckpoint(player.Id, "first-win-dave");
        var replay = _store.ClaimOnboardingCheckpoint(player.Id, "first-win-dave");
        var locked = _store.ClaimOnboardingCheckpoint(player.Id, "level-3-general-species");

        Assert.True(claimed.Ok);
        Assert.Equal("claimed", claimed.Row!.State);
        Assert.Equal(2, claimed.Row.Revision);
        Assert.False(replay.Ok);
        Assert.Equal("onboarding.checkpoint-already-claimed", replay.Reason);
        Assert.False(locked.Ok);
        Assert.Equal("onboarding.checkpoint-locked", locked.Reason);
    }

    [Fact]
    public void Settled_pvz_victory_earns_first_win_inside_capture_transaction_once()
    {
        const string matchKey = "onboarding-first-win";
        var events = new[]
        {
            new EventEnvelope
            {
                Game = RpgConstants.GameId,
                Kind = "board.start",
                MatchKey = matchKey,
                T = "2026-01-01T00:00:00Z",
                Payload = new { levelName = "lawn", levelType = "classic", boardLevel = 1 }
            },
            new EventEnvelope
            {
                Game = RpgConstants.GameId,
                Kind = "match.result",
                MatchKey = matchKey,
                T = "2026-01-01T00:01:00Z",
                Payload = new { result = "won" }
            }
        };

        _store.InsertEvents(events);
        var soulsAfterFirstSettlement = _store.GetSoulBalance(1).Balance;
        _store.InsertEvents(new[] { events[1] });
        Assert.Equal(soulsAfterFirstSettlement, _store.GetSoulBalance(1).Balance);
        Assert.True(soulsAfterFirstSettlement > 0);

        var player = _store.GetCurrentPlayer()!;
        var row = Assert.Single(_store.ListOnboardingCheckpoints(player.Id)!);
        Assert.Equal("first-win-dave", row.CheckpointId);
        Assert.Equal("earned", row.State);
        Assert.Equal(1, row.Revision);
        Assert.StartsWith("fact:", row.RewardRef);
    }

    [Fact]
    public void Level_three_general_species_checkpoint_uses_validated_source_and_is_idempotent()
    {
        const string matchKey = "onboarding-species";
        var species = CreatureSpeciesCatalog.All.First(x => x.Side == "plant");
        var events = new List<EventEnvelope>
        {
            new EventEnvelope
            {
                Game = RpgConstants.GameId, Kind = "board.start", MatchKey = matchKey,
                T = "2026-01-02T00:00:00Z", Payload = new { levelName = "lawn", levelType = "classic", boardLevel = 1 }
            }
        };
        // The configured player curve needs 245 XP to reach level 3; 21 kills provide 252.
        for (var i = 0; i < 21; i++)
        {
            events.Add(new EventEnvelope
            {
                Game = RpgConstants.GameId, Kind = "zombie.die", MatchKey = matchKey,
                T = $"2026-01-02T00:{i + 1:00}:00Z",
                Payload = new { ptr = $"kill-{i}" }
            });
        }
        events.Add(new EventEnvelope
        {
            Game = RpgConstants.GameId, Kind = "plant.place", MatchKey = matchKey,
            T = "2026-01-02T01:00:00Z",
            Payload = new
            {
                ptr = "general-plant", type = species.GameTypeId, typeName = species.Name,
                sourceKind = CreatureProgressionSource.ContractKind,
                sourceId = $"general:{species.SpeciesId}"
            }
        });
        events.Add(new EventEnvelope
        {
            Game = RpgConstants.GameId, Kind = "match.result", MatchKey = matchKey,
            T = "2026-01-02T01:01:00Z", Payload = new { result = "victory" }
        });

        _store.InsertEvents(events);
        _store.InsertEvents(new[] { events[^1] });

        var player = _store.GetCurrentPlayer()!;
        var state = _store.GetRpgActor(player.Id, RpgActorKinds.Player, 0);
        Assert.True(state!.Level >= 3);
        var rows = _store.ListOnboardingCheckpoints(player.Id)!;
        Assert.Equal(2, rows.Count);
        var speciesRow = rows.Single(x => x.CheckpointId == "level-3-general-species");
        Assert.Equal("earned", speciesRow.State);
        Assert.Contains($"\"speciesId\":\"{species.SpeciesId}\"", speciesRow.PayloadJson);
        Assert.Contains("\"allocationMode\":\"auto-primary-stats\"", speciesRow.PayloadJson);
        Assert.Equal(1, speciesRow.Revision);
    }

    [Fact]
    public void Level_four_mints_one_player_owned_dave_item_and_replays_without_duplicates()
    {
        var repo = Directory.GetParent(AppContext.BaseDirectory)!.Parent!.Parent!.Parent!.Parent!.Parent!.FullName;
        var atomPath = Path.Combine(repo, "data", "seed", "atoms", "fx-core.json");
        var containerPath = Path.Combine(repo, "data", "seed", "containers", "first-clear-grants.json");
        var rarityPath = Path.Combine(repo, "data", "seed", "rarity", "ladder.v1.json");
        var collected = AtomSeedFile.Collect(new[]
        {
            (atomPath, File.ReadAllText(atomPath)),
            (containerPath, File.ReadAllText(containerPath)),
            (rarityPath, File.ReadAllText(rarityPath)),
        });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        var imported = _store.ImportContent(collected.Content);
        Assert.True(imported.Committed, string.Join("; ", imported.Errors));

        var species = CreatureSpeciesCatalog.All.First(x => x.Side == "plant");
        var key = "onboarding-level-four";
        var events = new List<EventEnvelope>
        {
            new() { Game = RpgConstants.GameId, Kind = "board.start", MatchKey = key,
                T = "2026-01-03T00:00:00Z", Payload = new { levelName = "lawn", levelType = "classic", boardLevel = 1 } }
        };
        for (var i = 0; i < 42; i++)
            events.Add(new() { Game = RpgConstants.GameId, Kind = "zombie.die", MatchKey = key,
                T = $"2026-01-03T00:{i + 1:00}:00Z", Payload = new { ptr = $"kill-{i}" } });
        events.Add(new() { Game = RpgConstants.GameId, Kind = "plant.place", MatchKey = key,
            T = "2026-01-03T01:00:00Z", Payload = new { ptr = "general-plant", type = species.GameTypeId,
                typeName = species.Name, sourceKind = CreatureProgressionSource.ContractKind,
                sourceId = $"general:{species.SpeciesId}" } });
        events.Add(new() { Game = RpgConstants.GameId, Kind = "match.result", MatchKey = key,
            T = "2026-01-03T01:01:00Z", Payload = new { result = "victory" } });

        _store.InsertEvents(events);
        _store.InsertEvents(new[] { events[^1] });

        var player = _store.GetCurrentPlayer()!;
        var rows = _store.ListOnboardingCheckpoints(player.Id)!;
        var item = Assert.Single(_store.ListItemsByPlayer(player.Id.ToString()));
        var assignment = Assert.Single(_store.ListPlayerItemAssignments(player.Id.ToString()));
        var reward = rows.Single(x => x.CheckpointId == "level-4-dave-equipment");
        Assert.Equal("earned", reward.State);
        Assert.Equal(item.InstanceId, assignment.RefId);
        Assert.Equal(FusionRpg.Core.Items.ItemRole.Standard, assignment.Role);
        Assert.Contains(item.InstanceId, reward.PayloadJson);
        Assert.Equal(1, _store.ListItemsByPlayer(player.Id.ToString()).Count);
    }
}
