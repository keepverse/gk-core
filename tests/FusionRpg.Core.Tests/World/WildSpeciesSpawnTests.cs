using System.Text.Json;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Loam;
using Xunit;
using Xunit.Abstractions;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// species-gear-chain T7 (spec-wild-species-spawn.md Testing strategy) against synthetic rosters
/// and inline tuning — no shipped species named, no population count asserted. The admission rule
/// itself lives in <c>CreatureAdmission</c> (T5); what is pinned here is the map's OWN reading of
/// it (CaptureOnly admitted — the rule that must differ from waves).
/// </summary>
public class WildSpeciesSpawnTests
{
    readonly ITestOutputHelper _out;

    public WildSpeciesSpawnTests(ITestOutputHelper output) => _out = output;

    static int _id;
    static CreatureSpeciesDef Species(
        string speciesId, CreatureRarity rarity,
        CreatureAcquisition acquisition = CreatureAcquisition.Summonable,
        ElementTypeId element = ElementTypeId.Earth) =>
        new()
        {
            SpeciesId = speciesId,
            BaseRarity = rarity,
            Acquisition = acquisition,
            CreatureTypeId = 20000 + (_id++),
            ElementPrimary = element,
        };

    static WorldSpawnTuning Tuning(
        string fallback = "fallback",
        long offClimateMilli = 200,
        IReadOnlyDictionary<string, WorldSpawnSectorTuning>? sectors = null) =>
        new(1, 1, offClimateMilli, fallback, sectors ?? new Dictionary<string, WorldSpawnSectorTuning>(StringComparer.Ordinal)
        {
            ["barren"] = new(CreatureRarity.Chaff, CreatureRarity.Fused,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["chaff"] = 100, ["sprout"] = 100, ["grafted"] = 100,
                    ["cultivated"] = 100, ["fused"] = 100,
                }, 0, Array.Empty<WorldSpawnMemberEntry>()),
        });

    static WorldSector Sector(string id = "s", string type = "barren", ElementTypeId? climate = null) =>
        new() { SectorId = id, TypeId = type, Climate = climate };

    static List<CreatureSpeciesDef> Roster() => new()
    {
        Species("aaa-event", CreatureRarity.Chaff, CreatureAcquisition.EventOnly),
        Species("both", CreatureRarity.Chaff, CreatureAcquisition.Summonable | CreatureAcquisition.CaptureOnly),
        Species("cap", CreatureRarity.Chaff, CreatureAcquisition.CaptureOnly),
        Species("ord", CreatureRarity.Chaff),
        Species("un", CreatureRarity.Chaff, CreatureAcquisition.EventOnly | CreatureAcquisition.Summonable),
    };

    // ── determinism ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_inputs_yield_the_same_warband_twice_and_across_a_shuffled_roster()
    {
        var roster = Roster();
        var sector = Sector();
        var tuning = Tuning();
        var first = WildSpawnRoller.Roll(7UL, "s", 3, sector, 3, tuning, roster).Select(p => p.SpeciesId).ToList();
        var second = WildSpawnRoller.Roll(7UL, "s", 3, sector, 3, tuning, roster).Select(p => p.SpeciesId).ToList();
        var shuffled = WildSpawnRoller.Roll(7UL, "s", 3, sector, 3, tuning, roster.AsEnumerable().Reverse().ToList())
            .Select(p => p.SpeciesId).ToList();
        Assert.Equal(first, second);
        Assert.Equal(first, shuffled);
    }

    // ── admission: the map's own reading ─────────────────────────────────────────────

    [Fact]
    public void Eventonly_is_refused_including_with_summonable_and_captureonly_is_admitted()
    {
        // The rule that must DIFFER from waves, asserted explicitly so a later session cannot
        // "unify" the two filters (spec Never).
        var tuning = Tuning();
        // Three admissibles (both/cap/ord) under a strict no-repeat cap: exactly three draws stay
        // in-pool; a fourth would fall the whole warband back to the named species by design.
        var picks = WildSpawnRoller.Roll(7UL, "s", 3, Sector(), 3, tuning, Roster())
            .Select(p => p.SpeciesId).ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("aaa-event", picks);
        Assert.DoesNotContain("un", picks);
        Assert.Contains("cap", picks);
        Assert.Contains("both", picks);
        Assert.Contains("ord", picks);
    }

    // ── climate weighting ────────────────────────────────────────────────────────────

    [Fact]
    public void Climate_biases_without_excluding()
    {
        var roster = new List<CreatureSpeciesDef>
        {
            Species("on", CreatureRarity.Chaff, CreatureAcquisition.Summonable, ElementTypeId.Fire),
            Species("off", CreatureRarity.Chaff, CreatureAcquisition.Summonable, ElementTypeId.Earth),
        };
        // offClimateMilli 0 would exclude; the shipped 200 must merely bias. (One draw here:
        // the on-climate species holds the single no-repeat seat; a second draw would find nothing
        // drawable and fall back by design.)
        var tuning = Tuning(offClimateMilli: 0);
        var biased = WildSpawnRoller.Roll(7UL, "s", 3, Sector(climate: ElementTypeId.Fire), 1, tuning, roster);
        Assert.All(biased, p => Assert.Equal("on", p.SpeciesId));

        var live = Tuning(offClimateMilli: 200);
        // Two draws, two seats: the cap forces alternation, so both climates appear across seeds —
        // the bias is proven by the milli-0 case above admitting only "on", never by counting heads.
        var mixed = WildSpawnRoller.Roll(7UL, "s", 3, Sector(climate: ElementTypeId.Fire), 2, live, roster)
            .Select(p => p.SpeciesId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("off", mixed);
    }

    // ── empty pool: named fallback, never a crash ────────────────────────────────────

    [Fact]
    public void An_unknown_sector_type_and_an_empty_pool_fall_back_to_the_named_species()
    {
        var tuning = Tuning(fallback: "fallback");
        var unknown = WildSpawnRoller.Roll(7UL, "nope", 3, Sector("nope", "void-type"), 2, tuning, Roster());
        Assert.All(unknown, p => Assert.Equal("fallback", p.SpeciesId));

        var eventOnly = new List<CreatureSpeciesDef>
            { Species("ghost", CreatureRarity.Chaff, CreatureAcquisition.EventOnly) };
        var empty = WildSpawnRoller.Roll(7UL, "s", 3, Sector(), 2, tuning, eventOnly);
        Assert.All(empty, p => Assert.Equal("fallback", p.SpeciesId));
    }

    // ── member HP: the species' own P(Θ), interim only when absent ─────────────────────────

    [Fact]
    public void Picks_carry_the_species_own_derived_hp()
    {
        // T18b: a member's HP is its species' `resource.max.hp` magnitude — differing per species,
        // never one flat value shared by all.
        var roster = new List<CreatureSpeciesDef>
        {
            WithHp("light", CreatureRarity.Chaff, 500),
            WithHp("heavy", CreatureRarity.Chaff, 5000),
        };
        var picks = WildSpawnRoller.Roll(7UL, "s", 3, Sector(), 2, Tuning(), roster)
            .ToDictionary(p => p.SpeciesId);
        Assert.Equal(500, picks["light"].Hp);
        Assert.Equal(5000, picks["heavy"].Hp);
    }

    [Fact]
    public void A_species_with_no_magnitude_falls_back_to_the_named_interim_never_a_crash()
    {
        // The interim survived T18b as THE fallback (criterion: retained only as that, with the
        // comment saying so) — a magnitudeless species spawns at it, loudly named, never zero.
        var roster = new List<CreatureSpeciesDef>
        {
            WithHp("bare", CreatureRarity.Chaff, null),
        };
        var pick = Assert.Single(WildSpawnRoller.Roll(7UL, "s", 3, Sector(), 1, Tuning(), roster));
        Assert.Equal("bare", pick.SpeciesId);
        Assert.Equal(WildSpawnRoller.InterimFlatMemberHp, pick.Hp);
        Assert.Equal(LoamPolicy.UnmadeMemberHp, WildSpawnRoller.InterimFlatMemberHp);
    }

    static CreatureSpeciesDef WithHp(string speciesId, CreatureRarity rarity, long? hp) =>
        new()
        {
            SpeciesId = speciesId,
            BaseRarity = rarity,
            Acquisition = CreatureAcquisition.Summonable,
            CreatureTypeId = 20000 + (_id++),
            ElementPrimary = ElementTypeId.Earth,
            Magnitudes = hp is { } value
                ? new Dictionary<string, long>(StringComparer.Ordinal) { ["resource.max.hp"] = value }
                : new Dictionary<string, long>(StringComparer.Ordinal),
        };

    // ── tuning contract ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_loader_refuses_unknown_rungs_inverted_windows_and_bad_millis()
    {
        Assert.Throws<WorldSpawnTuningRejection>(() => WorldSpawnTuningLoader.Parse(
            """{"schemaVersion":1,"version":1,"offClimateMilli":200,"fallbackSpeciesId":"f","sectors":{"barren":{"rarityFrom":"mythic","rarityTo":"fused","sameSpeciesMaxMilli":0,"weights":{"fused":100},"members":[]}}}"""));
        Assert.Throws<WorldSpawnTuningRejection>(() => WorldSpawnTuningLoader.Parse(
            """{"schemaVersion":1,"version":1,"offClimateMilli":200,"fallbackSpeciesId":"f","sectors":{"barren":{"rarityFrom":"fused","rarityTo":"chaff","sameSpeciesMaxMilli":0,"weights":{"chaff":100,"fused":100},"members":[]}}}"""));
        Assert.Throws<WorldSpawnTuningRejection>(() => WorldSpawnTuningLoader.Parse(
            """{"schemaVersion":1,"version":1,"offClimateMilli":2000,"fallbackSpeciesId":"f","sectors":{}}"""));
    }

    [Fact]
    public void The_shipped_file_parses_with_all_four_sector_rows()
    {
        var tuning = WorldSpawnTuningLoader.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "world-spawn.v1.json")));
        Assert.Equal(new[] { "barren", "homeworld", "rich", "stable" }, tuning.Sectors.Keys.OrderBy(k => k));
        Assert.Equal("NormalZombie", tuning.FallbackSpeciesId);
    }

    [Fact]
    public void The_fallback_and_every_member_entry_resolve_in_the_real_catalog()
    {
        // The SEED corpus, not the compiled test roster (which is a smaller working set): the
        // fallback must name a species the real import pipeline can actually expand.
        var catalog = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(KeepverseRoots.Content(), "data", "seed", "creatures", "species"),
                     "*.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var entries = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : Enumerable.Repeat(root, 1);
            foreach (var entry in entries)
                if (entry.TryGetProperty("speciesId", out var id) && id.ValueKind == JsonValueKind.String)
                    catalog.Add(id.GetString()!);
        }
        var tuning = WorldSpawnTuningLoader.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "world-spawn.v1.json")));
        Assert.Contains(tuning.FallbackSpeciesId, catalog);
        foreach (var sector in tuning.Sectors.Values)
        foreach (var member in sector.Members)
            Assert.Contains(member.SpeciesId, catalog);
    }

    // ── non-recruitable expressibility ───────────────────────────────────────────────

    [Fact]
    public void A_sector_table_can_express_a_non_recruitable_creature()
    {
        // SC8: none authored — this synthetic table proves the SHAPE carries the flag end to end.
        var sectors = new Dictionary<string, WorldSpawnSectorTuning>(StringComparer.Ordinal)
        {
            ["barren"] = new(CreatureRarity.Chaff, CreatureRarity.Chaff,
                new Dictionary<string, int>(StringComparer.Ordinal) { ["chaff"] = 100 }, 0,
                new[] { new WorldSpawnMemberEntry("void-beast", 100, Recruitable: false) }),
        };
        var tuning = Tuning(sectors: sectors);
        var picks = WildSpawnRoller.Roll(7UL, "s", 3, Sector(), 3, tuning, Roster());
        Assert.All(picks, p =>
        {
            Assert.Equal("void-beast", p.SpeciesId);
            Assert.False(p.Recruitable);
        });
    }

    // ── report, printed never asserted ───────────────────────────────────────────────

    [Fact]
    public void Report_distinct_species_reachable_across_a_simulated_world()
    {
        var tuning = WorldSpawnTuningLoader.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "world-spawn.v1.json")));
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        var fallbacks = 0;
        var climates = new ElementTypeId?[] { null, ElementTypeId.Earth, ElementTypeId.Fire };
        foreach (var sectorType in tuning.Sectors.Keys)
        foreach (var turn in Enumerable.Range(1, 25))
        {
            var sector = Sector("s", sectorType, climates[turn % climates.Length]);
            foreach (var pick in WildSpawnRoller.Roll(99UL, "s", turn, sector, 2, tuning))
            {
                distinct.Add(pick.SpeciesId);
                if (pick.SpeciesId == tuning.FallbackSpeciesId) fallbacks++;
            }
        }
        _out.WriteLine($"distinct species reachable: {distinct.Count}; fallback share: {fallbacks}");
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "data", "tuning", "world-spawn.v1.json")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
