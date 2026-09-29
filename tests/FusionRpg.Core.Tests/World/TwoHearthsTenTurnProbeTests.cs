using System.Text;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Turn;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// Checkpoint 5 support evidence (spec-loam-maps.md, tasks/loam-todo.md): the owner's ten-turn
/// playtest judges a subjective question no test can answer — "does this feel like a decision, does
/// the fade feel tense, is a split economy frightening?" That verdict is the owner's alone.
///
/// What a test *can* do is run the exact scenario named in the gate brief — ten turns on
/// `two-hearths`, both factions untouched — and prove the mechanic behaves sanely over that window
/// rather than degenerately (nothing crashes, nothing goes negative where it shouldn't, the numbers
/// the owner is about to look at are the numbers this probe already saw). This is a falsifier, not
/// the gate itself: it can only catch a mechanical problem before the owner's time is spent on it,
/// never answer whether the mechanic is any *fun*.
/// </summary>
public class TwoHearthsTenTurnProbeTests
{
    const ulong Seed = 7;
    readonly ITestOutputHelper _output;

    public TwoHearthsTenTurnProbeTests(ITestOutputHelper output) => _output = output;

    static WorldSector Find(WorldState w, string id) => w.Sectors.Single(s => s.SectorId == id);

    sealed record ComponentReading(string ComponentId, long Production, long Upkeep, long Net, long Stock, string? ReleaseCandidate);

    static IReadOnlyList<ComponentReading> ReadingsFor(WorldState world, string factionId)
    {
        var readings = new List<ComponentReading>();
        foreach (var component in TerritoryComponents.For(world, factionId))
        {
            long production = 0, upkeep = 0, stock = 0;
            foreach (var id in component)
            {
                var sector = Find(world, id);
                production += LoamProduction.For(sector);
                upkeep += LoamUpkeep.For(world, sector);
                stock += sector.LoamStock;
            }

            readings.Add(new ComponentReading(component[0], production, upkeep, production - upkeep, stock,
                LoamForecast.WillRelease(world, component)));
        }

        return readings;
    }

    [Fact]
    public void Ten_baseline_turns_on_two_hearths_never_degenerate_for_either_faction()
    {
        var world = WorldTemplateCatalog.Build(WorldTemplateCatalog.TwoHeartsId, Seed, worldId: "gate-probe");
        var log = new StringBuilder();

        for (var turn = 0; turn <= 10; turn++)
        {
            log.AppendLine($"-- turn {turn} --");

            foreach (var factionId in new[] { "dave", "zomboss" })
            {
                var readings = ReadingsFor(world, factionId);
                foreach (var r in readings)
                {
                    log.AppendLine(
                        $"  {factionId} component[{r.ComponentId}]: production={r.Production} upkeep={r.Upkeep} " +
                        $"net={r.Net} stock={r.Stock} releaseCandidate={r.ReleaseCandidate ?? "-"}");

                    // A component's own numbers must never be internally impossible: stock can't go
                    // negative, and a release candidate can only be named when the component is
                    // actually short (the same invariant LoamForecast.WillRelease itself enforces —
                    // this re-checks it from the outside, over every turn actually reached).
                    Assert.True(r.Stock >= 0, $"turn {turn}, {factionId}, component {r.ComponentId}: stock went negative");
                    if (r.ReleaseCandidate is not null)
                        Assert.True(r.Net < 0, $"turn {turn}, {factionId}, component {r.ComponentId}: named a release candidate with a non-negative net");
                }
            }

            if (turn < 10)
                world = TurnEngine.Step(world, Array.Empty<WorldCommand>(), Seed).World;
        }

        _output.WriteLine(log.ToString());

        // The capital clusters both factions start on are each self-sufficient by design (two
        // rootbeds apiece) — ten turns of doing nothing must not, by itself, cost either commander
        // their own home. A run that fails this would mean the baseline economy is miscalibrated
        // (upkeep outrunning production even with nobody interfering), which is worth knowing before
        // the owner's own ten turns, not after.
        Assert.Equal("dave", Find(world, "d-home").OwnerFactionId);
        Assert.Equal("zomboss", Find(world, "z-home").OwnerFactionId);
        Assert.True(Find(world, "d-home").StabilityMilli > 0);
        Assert.True(Find(world, "z-home").StabilityMilli > 0);
    }
}
