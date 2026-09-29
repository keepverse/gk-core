using System.Collections.Concurrent;
using FusionRpg.Data.Abstractions;
using FusionRpg.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FusionRpg.E2E.Tests;

public class CompactionWorkerTests
{
    [Fact]
    public async Task Enqueue_closed_run_invokes_compactor_once()
    {
        var fake = new FakeHotCompactor();
        var worker = new CompactionWorker(fake, NullLogger<CompactionWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);

        try
        {
            worker.EnqueueClosedRun(42);
            Assert.True(fake.WaitForCalls(1, TimeSpan.FromSeconds(3)));
            Assert.Equal(new long?[] { 42 }, fake.Calls.ToArray());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    sealed class FakeHotCompactor : IHotCompactor
    {
        readonly ConcurrentQueue<long?> _calls = new();
        readonly ManualResetEventSlim _signal = new(false);

        public IReadOnlyList<long?> Calls => _calls.ToArray();

        public void CompactAfterRunClosed(long? closedRunId)
        {
            _calls.Enqueue(closedRunId);
            _signal.Set();
        }

        public bool WaitForCalls(int min, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_calls.Count >= min) return true;
                _signal.Wait(50);
            }
            return _calls.Count >= min;
        }
    }
}
