using FusionRpg.Contracts;
using FusionRpg.Core.Onboarding;
using FusionRpg.Core.Progression;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>Server adapter for the durable first-session checkpoint ledger.</summary>
public static class OnboardingEndpoints
{
    public static void MapOnboarding(this WebApplication app)
    {
        var g = app.MapGroup("/api/onboarding");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            var state = ProjectState(store, playerId);
            return state is null ? Results.NotFound() : Results.Ok(state);
        });

        g.MapPost("/{playerId:long}/checkpoints/{checkpointId}/claim",
            (long playerId, string checkpointId, RpgStore store) =>
            {
                if (!store.PlayerExists(playerId)) return Results.NotFound();
                var result = store.ClaimOnboardingCheckpoint(playerId, checkpointId);
                var body = new OnboardingClaimDto
                {
                    Ok = result.Ok,
                    Reason = result.Reason,
                    Checkpoint = result.Row is null ? null : ProjectCheckpoint(result.Row)
                };
                return result.Ok ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status409Conflict);
            });

        g.MapPost("/{playerId:long}/stories/{storyId}/ack",
            (long playerId, string storyId, OnboardingStoryAckRequest request, RpgStore store) =>
            {
                if (!store.PlayerExists(playerId)) return Results.NotFound();
                var result = store.AcknowledgeOnboardingStory(playerId, storyId, request.Version, request.Outcome);
                var body = new OnboardingStoryAckDto
                {
                    Ok = result.Ok,
                    Reason = result.Reason,
                    Story = result.Row is null ? null : ProjectStory(result.Row)
                };
                if (result.Ok) return Results.Ok(body);
                var status = result.Reason == "onboarding.story-conflict"
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status400BadRequest;
                return Results.Json(body, statusCode: status);
            });

        // ---- rift-gate first-open-signal -------------------------------------------------------
        //
        // The durable once-per-player "FE has been opened" fact. Written FE-side on first load
        // (deliberately NOT keyed on the tombstone or the embed marker — it is keyed on the FE being
        // opened, so it is also correct for the launcher's "Open RPG UI" or a plain browser visit).
        // `actionable` is the server's own injector-connectivity read, so a consumer knows whether the
        // capture trigger can fire yet. This endpoint never blocks the FE and never touches the story.

        g.MapGet("/{playerId:long}/first-open", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(ProjectFirstOpen(store, playerId));
        });

        g.MapPost("/{playerId:long}/first-open", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            store.RecordFirstOpen(playerId);
            // Idempotent: recording twice is a no-op, so the response is the same either way.
            return Results.Ok(ProjectFirstOpen(store, playerId));
        });
    }

    static FirstOpenDto ProjectFirstOpen(RpgStore store, long playerId)
    {
        var row = store.GetFirstOpen(playerId);
        return new FirstOpenDto
        {
            PlayerId = playerId,
            Opened = row is not null,
            OpenedUtc = row?.OpenedUtc,
            Revision = row?.Revision ?? 0,
            // The honest condition for the capture trigger: the injector must be running and in-game.
            // An unactionable fact stays pending until one connects.
            Actionable = row is not null && store.IsFirstOpenActionable,
        };
    }

    internal static OnboardingStateDto? ProjectState(RpgStore store, long playerId)
    {
        if (!store.PlayerExists(playerId)) return null;
        var rows = store.ListOnboardingCheckpoints(playerId) ?? Array.Empty<RpgStore.OnboardingCheckpointRow>();
        var stories = store.ListOnboardingStories(playerId) ?? Array.Empty<RpgStore.OnboardingStoryRow>();
        var player = store.GetRpgActor(playerId, RpgActorKinds.Player, 0)
                     ?? store.GetRpgProgressionSummary(playerId)?.Player;
        return new OnboardingStateDto
        {
            PlayerId = playerId,
            PlayerLevel = player?.Level ?? 1,
            // The envelope revision must cover every durable member of the response. Story
            // acknowledgements are independent from reward checkpoints, so omitting them made a
            // fresh story row (and later story-only acknowledgements) invisible to revision-aware
            // consumers.
            Revision = rows.Select(x => x.Revision)
                .Concat(stories.Select(x => x.Revision))
                .DefaultIfEmpty(0)
                .Max(),
            Checkpoints = rows.Select(ProjectCheckpoint).ToList(),
            Stories = stories.Select(ProjectStory).ToList()
        };
    }

    static OnboardingCheckpointDto ProjectCheckpoint(RpgStore.OnboardingCheckpointRow row) => new()
    {
        CheckpointId = row.CheckpointId,
        State = row.State,
        EarnedRunId = row.EarnedRunId,
        RewardRef = row.RewardRef,
        PayloadJson = row.PayloadJson,
        EarnedUtc = row.EarnedUtc,
        ClaimedUtc = row.ClaimedUtc,
        Revision = row.Revision
    };

    static OnboardingStoryDto ProjectStory(RpgStore.OnboardingStoryRow row) => new()
    {
        StoryId = row.StoryId,
        Version = row.Version,
        State = row.State,
        Outcome = row.Outcome,
        Eligible = row.Eligible,
        AcknowledgedUtc = row.AcknowledgedUtc,
        Revision = row.Revision
    };
}
