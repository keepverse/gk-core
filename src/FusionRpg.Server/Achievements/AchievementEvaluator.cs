using System.Threading.Channels;
using FusionRpg.Core.Achievements;
using FusionRpg.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FusionRpg.Server.Achievements;

// Cold achievement drain (spec-achievement-evaluator.md): evaluates durable fact
// evidence off every Hot path and appends exactly-once unlock rows. Enqueue from
// world-turn commit, ingest, or expedition settlement — never from combat.hit.
public sealed class AchievementEvaluator : BackgroundService
{
    readonly Channel<(AchievementDefinition Def, AchievementEvidence Evidence)> _channel =
        Channel.CreateUnbounded<(AchievementDefinition, AchievementEvidence)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    readonly RpgStore _store;
    readonly ILogger<AchievementEvaluator> _log;

    public AchievementEvaluator(RpgStore store, ILogger<AchievementEvaluator> log)
    {
        _store = store;
        _log = log;
    }

    public void Enqueue(AchievementDefinition def, AchievementEvidence evidence) =>
        _channel.Writer.TryWrite((def, evidence));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        while (!stoppingToken.IsCancellationRequested)
        {
            (AchievementDefinition Def, AchievementEvidence Evidence) item;
            try { item = await reader.ReadAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try
            {
                var receipt = _store.EvaluateAchievement(item.Def, item.Evidence);
                if (receipt is not null)
                    _log.LogInformation(
                        "Achievement {DefId} unlock {UnlockId} (replayed {Replayed})",
                        item.Def.DefId, receipt.Value.UnlockId, receipt.Value.Replayed);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Achievement evaluation failed for {DefId}; dropped, never retried blind",
                    item.Def.DefId);
            }
        }
    }
}
