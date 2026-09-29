using System.Threading.Channels;
using FusionRpg.Data.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FusionRpg.Server;

/// <summary>Drains closed-run compaction work; never mid-run (open promote refused by writer).</summary>
public sealed class CompactionWorker : BackgroundService
{
    readonly Channel<long> _channel = Channel.CreateUnbounded<long>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    readonly IHotCompactor _compactor;
    readonly ILogger<CompactionWorker> _log;

    public CompactionWorker(IHotCompactor compactor, ILogger<CompactionWorker> log)
    {
        _compactor = compactor;
        _log = log;
    }

    public void EnqueueClosedRun(long runId) => _channel.Writer.TryWrite(runId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reader = _channel.Reader;
        while (!stoppingToken.IsCancellationRequested)
        {
            long runId;
            try { runId = await reader.ReadAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }

            try { _compactor.CompactAfterRunClosed(runId); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Compaction failed for closed run {RunId}; will retry on next board.end", runId);
            }
        }
    }
}
