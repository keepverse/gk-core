using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.World.Loam;

/// <summary>
/// L9 / capability-map finding A9. A test-shaped tool, not a dashboard: given a hand-built fixture
/// and a turn count, it replays loam's own arithmetic turn over turn — production accrues per
/// sector (capped, mirroring what `loam-turn`'s Production phase will do for real), upkeep draws
/// pooled per component, and any shortfall it cannot pay is a harness-local number rather than a
/// negative stock, exactly as `spec-loam-model.md` rule 2 requires of the real field.
///
/// **This harness is deliberately not `loam-turn`.** Nothing here is wired into `TurnEngine`; it
/// exists so the calculators' claims can be checked against a hundred turns of accumulation before
/// anything is built on top of them, the same shape W25-W34 used for the AI evaluation tables.
/// </summary>
public class EconomyHarnessTests
{
    readonly ITestOutputHelper _out;
    public EconomyHarnessTests(ITestOutputHelper output) => _out = output;

    const string Rich = "rich";
    const string Poor = "poor";

    static WorldSlot Rootbed(int index) => new() { SlotIndex = index, SlotTypeId = SlotTypeCatalog.RootbedSlotTypeId };

    static WorldSector Sector(string id, string owner, long startingStock, int development = 0, int danger = 0, IReadOnlyList<WorldSlot>? slots = null) =>
        new()
        {
            SectorId = id, TypeId = "stable", OwnerFactionId = owner, LoamStock = startingStock,
            DevelopmentLevel = development, DangerBand = danger, Slots = slots ?? Array.Empty<WorldSlot>()
        };

    static WorldLane Lane(string id, string from, string to) => new()
    {
        LaneId = id, FromSectorId = from, ToSectorId = to, TypeId = LaneTypeCatalog.RiftLaneTypeId
    };

    /// <summary>
    /// Two components, one per faction, deliberately shaped so §12.4 ("most sectors run a
    /// deficit") and P1 ("no faucet runs unbounded") both have something real to demonstrate:
    /// `rich` produces far more than it spends and would show a perpetually positive net flow if
    /// nothing capped it; `poor` carries one modest source (just enough to escape the G-C
    /// exemption for factions with none at all) against four sectors of pure deficit, with a
    /// starting stock deep enough to survive roughly the first third of the run before its own
    /// shortfall begins.
    /// </summary>
    static WorldState Fixture() => new()
    {
        WorldId = "harness", TemplateId = "test", Seed = 1,
        Factions = new[]
        {
            new WorldFaction { FactionId = Rich, Kind = WorldFactionKind.Player, Name = "Rich" },
            new WorldFaction { FactionId = Poor, Kind = WorldFactionKind.Player, Name = "Poor" }
        },
        Sectors = new[]
        {
            Sector("r1", Rich, startingStock: 0, slots: new[] { Rootbed(0) }),
            Sector("r2", Rich, startingStock: 0, slots: new[] { Rootbed(0) }),
            Sector("p1", Poor, startingStock: 0, development: 2, danger: 1, slots: new[] { Rootbed(0) }),
            Sector("p2", Poor, startingStock: 500, development: 2, danger: 1),
            Sector("p3", Poor, startingStock: 500, development: 2, danger: 1),
            Sector("p4", Poor, startingStock: 500, development: 2, danger: 1),
            Sector("p5", Poor, startingStock: 500, development: 2, danger: 1)
        },
        Lanes = new[]
        {
            Lane("l-r", "r1", "r2"),
            Lane("l-p1", "p1", "p2"), Lane("l-p2", "p2", "p3"), Lane("l-p3", "p3", "p4"), Lane("l-p4", "p4", "p5")
        }
    };

    sealed record TurnRow(int Turn, IReadOnlyDictionary<string, long> NetFlowByFaction, double DeficitShare);

    /// <summary>
    /// Runs the harness's own turn loop: production accrues per sector (capped at
    /// <see cref="LoamPolicy.LoamCapacity"/>, overflow lost — mirrors L12), each component draws its
    /// upkeep from its pooled, capped stock (proportionally, remainder in ordinal order — mirrors
    /// the SSOT's stated draw rule), and whatever a component cannot pay is a shortfall that never
    /// makes a stock negative.
    /// </summary>
    static IReadOnlyList<TurnRow> Run(WorldState world, int turns)
    {
        var stock = world.Sectors.ToDictionary(s => s.SectorId, s => s.LoamStock, StringComparer.Ordinal);
        var rows = new List<TurnRow>();

        for (var turn = 1; turn <= turns; turn++)
        {
            // Realized production — what actually lands in stock, not the nominal seep — because
            // once a sector sits at capacity, further seep is lost, and a net-flow figure built from
            // the nominal number would hide exactly the throttling effect this harness exists to
            // show (an uncapped `rich` sector would otherwise report the same positive flow forever).
            var realizedProduction = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var sector in world.Sectors)
            {
                var before = stock[sector.SectorId];
                // The cap throttles new accrual only — it never claws back stock a template
                // authored above it. Capping via `Math.Min(capacity, before + nominal)` instead
                // would silently *shrink* an already-over-capacity sector on the very first turn,
                // reporting a negative "realized production" for a sector that produced nothing.
                var room = Math.Max(0, LoamPolicy.LoamCapacity - before);
                var added = Math.Min(room, LoamProduction.For(sector));
                stock[sector.SectorId] = before + added;
                realizedProduction[sector.SectorId] = added;
            }

            var netFlowByFaction = new Dictionary<string, long>(StringComparer.Ordinal);
            var deficitSectors = 0;
            var totalSectors = world.Sectors.Count;

            foreach (var sector in world.Sectors)
                if (LoamBalance.PerSector(world, sector) < 0)
                    deficitSectors++;

            foreach (var faction in world.Factions)
            {
                long factionNet = 0;
                foreach (var component in TerritoryComponents.For(world, faction.FactionId))
                {
                    var production = component.Sum(id => realizedProduction[id]);
                    var upkeep = component.Sum(id => LoamUpkeep.For(world, world.Sectors.Single(s => s.SectorId == id)));
                    var available = component.Sum(id => stock[id]);
                    var drawn = Math.Min(available, upkeep);

                    // Draw proportionally from member stocks, ordinal id order for the remainder —
                    // the SSOT's stated rule. Exact per-sector distribution is not this harness's
                    // claim (that is `loam-turn`'s own test), only that the pool as a whole cannot
                    // be drawn past what it holds.
                    var remaining = drawn;
                    foreach (var id in component.OrderBy(x => x, StringComparer.Ordinal))
                    {
                        if (available == 0) break;
                        var share = drawn * stock[id] / available;
                        stock[id] -= share;
                        remaining -= share;
                    }
                    if (remaining > 0 && component.Count > 0)
                        stock[component.OrderBy(x => x, StringComparer.Ordinal).First()] -= remaining;

                    factionNet += production - drawn;
                }

                netFlowByFaction[faction.FactionId] = factionNet;
            }

            rows.Add(new TurnRow(turn, netFlowByFaction, (double)deficitSectors / totalSectors));
        }

        return rows;
    }

    [Fact]
    public void Net_flow_is_not_monotone_positive_over_a_hundred_turn_run()
    {
        var rows = Run(Fixture(), turns: 100);

        foreach (var faction in new[] { Rich, Poor })
        {
            var series = rows.Select(r => r.NetFlowByFaction[faction]).ToList();
            var monotonePositive = series.Zip(series.Skip(1), (a, b) => b >= a).All(x => x) && series.All(v => v > 0);

            Assert.False(monotonePositive,
                $"{faction}'s net flow was monotone positive for the whole run — P1 requires no faucet run unbounded.");
        }
    }

    [Fact]
    public void The_deficit_share_stays_above_a_floor()
    {
        var rows = Run(Fixture(), turns: 100);

        // ideal §12.4: most ground loses money. Four of the seven sectors here have no source at
        // all, so the floor is generous — if this were ever measured near zero, the central
        // economic claim would not be true in practice.
        const double floor = 0.5;
        Assert.All(rows, r => Assert.True(r.DeficitShare >= floor,
            $"turn {r.Turn}: deficit share {r.DeficitShare:P0} fell below the {floor:P0} floor"));
    }

    [Fact]
    public void The_hundred_turn_table_prints_for_a_human_to_read()
    {
        var rows = Run(Fixture(), turns: 100);

        _out.WriteLine("turn | rich net | poor net | deficit share");
        foreach (var r in new[] { rows[0], rows[9], rows[19], rows[20], rows[21], rows[49], rows[99] })
            _out.WriteLine($"{r.Turn,4} | {r.NetFlowByFaction[Rich],8} | {r.NetFlowByFaction[Poor],8} | {r.DeficitShare:P0}");

        // income-vs-upkeep growth: not exercised here — nothing yet changes territory size without
        // an AI or player conquering ground (that is loam-ai / the AI program, not this harness).
        // yield concentration: with two rootbeds of equal output, concentration is a flat 50% in
        // this fixture and not a claim worth asserting on; a richer map (two-hearths, L17) is where
        // this metric first has something uneven to say.
        // binding frequency: no action reads loam yet (L20's Abandon rule is the first), so there is
        // nothing to measure — printing a fabricated number here would be worse than the gap.
        Assert.NotEmpty(rows);
    }
}
