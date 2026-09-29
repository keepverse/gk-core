using System.Diagnostics;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Topology;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.World.Topology;

/// <summary>
/// Task L11 / capability-map finding A5. `spec-world-topology.md` asserts the reconnection sweep is
/// "fine at six sectors and fine at sixty". The six is proven daily; sixty had never been run, and
/// the `huge` / `giant` world tiers are sized on that unmeasured claim.
///
/// Named *Bench* so `coverage.py` excludes it. It measures, it does not assert a timing — a wall
/// clock in a pass/fail test is a flaky test on a busy machine.
/// </summary>
public class ReconnectionCostBench
{
    readonly ITestOutputHelper _out;
    public ReconnectionCostBench(ITestOutputHelper output) => _out = output;

    /// <summary>A ring with chords: connected, with real chokepoints, like an authored map.</summary>
    static (List<string> Sectors, List<WorldLane> Lanes) Ring(int n)
    {
        var sectors = Enumerable.Range(0, n).Select(i => $"s{i:D3}").ToList();
        var lanes = new List<WorldLane>();
        for (var i = 0; i < n; i++)
            lanes.Add(new WorldLane
            {
                LaneId = $"l{i:D3}", FromSectorId = sectors[i], ToSectorId = sectors[(i + 1) % n],
                TypeId = "corridor", Length = 1000, Width = 1000
            });
        for (var i = 0; i + n / 2 < n; i += 4)
            lanes.Add(new WorldLane
            {
                LaneId = $"c{i:D3}", FromSectorId = sectors[i], ToSectorId = sectors[i + n / 2],
                TypeId = "corridor", Length = 1000, Width = 1000
            });
        return (sectors, lanes);
    }

    [Fact]
    public void Measure_the_sweep_at_every_declared_world_size()
    {
        _out.WriteLine("nodes | lanes |   ms | per-turn verdict");
        foreach (var n in new[] { 8, 16, 32, 64, 128 })
        {
            var (sectors, lanes) = Ring(n);
            ReconnectionCost.For(sectors, lanes, _ => null);          // warm
            var sw = Stopwatch.StartNew();
            var result = ReconnectionCost.For(sectors, lanes, _ => null);
            sw.Stop();
            Assert.Equal(n, result.Count);
            _out.WriteLine($"{n,5} | {lanes.Count,5} | {sw.Elapsed.TotalMilliseconds,6:F1}");
        }
    }
}
