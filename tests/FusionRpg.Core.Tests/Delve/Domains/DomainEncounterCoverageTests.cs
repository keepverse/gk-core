using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Tests.Delve.Encounter;
using FusionRpg.Core.Tests.Dungeon;
using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>
/// D4.31 (party-dungeon-todo.md, 2026-09-07) — `DomainEncounterCoverage.Report`, run for real over the
/// six shipped domains. Reuses D4.17 row 6's own already-proven setup (classified corpus, real rooms/
/// encounters) since it is the identical underlying mechanism (`Encounter.Build` -&gt;
/// `SlotFilter.Candidates`) that already refuses on the classified corpus's own thinness — this file
/// proves the SAME real finding from a different acceptance angle, not a new one.
/// </summary>
public class DomainEncounterCoverageTests
{
    static readonly CreatureThreatTuning RealThreat = RealAnchorCorpusFixture.ThreatTuning;
    static readonly EncounterTuning RealEncounterTuning = EncounterTuningHub.Tuning;
    static readonly AptitudeTuning RealAptitudes =
        AptitudeTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "aptitudes.v2.json")));
    static readonly PowerTuning RealPower =
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "power-scale.v2.json")));
    static readonly CreatureShapeTuning RealShape =
        CreatureShapeTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "creature-shape.v1.json")));
    static readonly RaidModeTuning Solo = DungeonTuningHub.Tuning.RaidModes["solo"];
    static readonly DifficultyRungTuning Hard = DungeonTuningHub.Tuning.Rungs["hard"];

    /// <summary>The real dungeon tuning, for the one test that rolls a real graph.</summary>
    static DungeonTuning Tuning => DungeonTuningHub.Tuning;

    static IReadOnlyList<ConcreteAnchor> ClassifiedCorpus() =>
        EncounterCorpusBuilder.Build(DungeonTestFiles.SpeciesDir(), RealAptitudes, RealPower, RealShape, RealThreat)
            .Where(a => a.ThreatBand is not null).ToList();

    [Fact]
    public void Report_null_arguments_throw()
    {
        var domain = new DomainRow("d", "n", "f", "t", "fire", "shallow", "many", "l", "species.x", null, "Lair", null);
        var rooms = new Dictionary<string, RoomPaletteEntry>();
        var refs = new Dictionary<string, string?>();
        var byId = new Dictionary<string, EncounterAnchor>();
        var corpus = ClassifiedCorpus();
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(null!, Array.Empty<string>(), rooms, refs, byId, corpus, 500, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, null!, rooms, refs, byId, corpus, 500, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), null!, refs, byId, corpus, 500, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), rooms, null!, byId, corpus, 500, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), rooms, refs, null!, corpus, 500, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), rooms, refs, byId, null!, 500, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), rooms, refs, byId, corpus, 500, null!, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), rooms, refs, byId, corpus, 500, Solo, null!, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), rooms, refs, byId, corpus, 500, Solo, Hard, null!, RealThreat, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), rooms, refs, byId, corpus, 500, Solo, Hard, RealEncounterTuning, null!, RealAptitudes, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), rooms, refs, byId, corpus, 500, Solo, Hard, RealEncounterTuning, RealThreat, null!, RealPower, 32, 81));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.Report(domain, Array.Empty<string>(), rooms, refs, byId, corpus, 500, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, null!, 32, 81));
    }

    /// <summary>An empty palette samples nothing — zero distinct cells, budget fails honestly (never a
    /// false "meets budget" on no data), no refusals (nothing was even attempted).</summary>
    [Fact]
    public void An_empty_room_palette_reports_zero_cells_and_fails_budget_honestly()
    {
        var domain = new DomainRow("domain.x", "n", "f", "t", "fire", "shallow", "many", "l", "species.x", null, "Lair", null);
        var report = DomainEncounterCoverage.Report(domain, Array.Empty<string>(),
            new Dictionary<string, RoomPaletteEntry>(), new Dictionary<string, string?>(),
            new Dictionary<string, EncounterAnchor>(), ClassifiedCorpus(), 500, Solo, Hard,
            RealEncounterTuning, RealThreat, RealAptitudes, RealPower, sampleSeeds: 32, budgetTarget: 81);

        Assert.Equal("domain.x", report.DomainId);
        Assert.Equal(0, report.DistinctCells);
        Assert.False(report.MeetsBudget);
        Assert.Empty(report.RoomRefusals);
    }

    /// <summary>Non-`fight`/`elite`/`boss` rooms (e.g. `rest`) are never sampled at all, even when
    /// present in the palette with a real encounterRef — spec §8's own scope.</summary>
    [Fact]
    public void Non_combat_room_kinds_are_never_sampled()
    {
        var domain = new DomainRow("domain.x", "n", "f", "t", "fire", "shallow", "many", "l", "species.x", null, "Lair", null);
        var rooms = new Dictionary<string, RoomPaletteEntry>(StringComparer.Ordinal) { ["room.rest"] = new("room.rest", "rest", null) };
        var refs = new Dictionary<string, string?>(StringComparer.Ordinal) { ["room.rest"] = "encounter.real" };
        var byId = new Dictionary<string, EncounterAnchor>(StringComparer.Ordinal)
        {
            ["encounter.real"] = new EncounterAnchor(Formation.Pack,
                new[] { new EncounterSlot(Posture.Bastion, null, null, "lone") },
                new[] { 0 }, ElementSpreadMode.Mono, new ThreatWindow(1, 10), null, null),
        };

        var report = DomainEncounterCoverage.Report(domain, new[] { "room.rest" }, rooms, refs, byId,
            ClassifiedCorpus(), 500, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower,
            sampleSeeds: 32, budgetTarget: 81);

        Assert.Equal(0, report.DistinctCells);
        Assert.Empty(report.RoomRefusals);
    }

    /// <summary>
    /// The real end-to-end run over all six real domains — proving the SAME real finding D4.17 row 6
    /// already documented (all fight/elite/boss archetypes refuse on the classified corpus's own
    /// thinness), now from D4.31's own acceptance angle. Not asserted to pass — the classified corpus
    /// is real and thin; this reports the true verdict.
    /// </summary>
    [Fact]
    public void Real_domains_report_the_same_classified_corpus_refusal_row_6_already_found()
    {
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var refs = RoomEncounterRefSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var byId = EncounterSeedFile.LoadAllById(DungeonTestFiles.EncountersDir(), RealThreat);
        var corpus = ClassifiedCorpus();

        Assert.Equal(6, domains.Count);
        foreach (var domain in domains)
        {
            var report = DomainEncounterCoverage.Report(domain, palettes[domain.DomainId], rooms, refs, byId,
                corpus, 500, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower,
                sampleSeeds: 32, budgetTarget: 81);

            Assert.False(report.MeetsBudget, $"{domain.DomainId} unexpectedly met budget — the classified-corpus gap may have closed");
            Assert.NotEmpty(report.RoomRefusals);
        }
    }

    /// <summary>
    /// D4.31's own "sibling-collision reporting" gap (spec §8's own "StS rule": two same-kind
    /// siblings on one graph row resolving to the same cell are a finding here and a filed ask on
    /// `delve-graph-roll`). Hand-built graphs isolate the grouping logic; the real-content test at the
    /// end of this block proves the whole path against a real rolled domain.
    ///
    /// <para>The anchor below is deliberately minimal (one Bastion pack slot, mono spread) so every
    /// roll of it can only ever produce ONE cell — which is exactly the real-world shape the rule
    /// exists to catch: an anchor too constrained to give its siblings distinct shapes. Two siblings
    /// still draw on genuinely independent streams (`DelveStreams.Room(row, col)`), so a collision here
    /// is not an artefact of a shared seed.</para>
    /// </summary>
    static EncounterAnchor SingleShapeAnchor() => new(
        Formation.Pack, new[] { new EncounterSlot(Posture.Bastion, null, null, "lone") },
        new[] { 0 }, ElementSpreadMode.Mono, new ThreatWindow(1, 10), null, null);

    static DelveRoomFact Fact(int row, int col, string kind, string archetypeId) => new(
        row, col, $"r{row:00}c{col:00}", kind, archetypeId, BaseBand: 0, IsSecret: false,
        SightLanes: 0, ScoutSightLanes: 0, PartyRouteMask: 0, KeyForLaneId: null);

    static DelveGraph GraphOf(params DelveRoomFact[] facts) => new(
        Array.Empty<WorldSector>(), Array.Empty<WorldLane>(), facts, Array.Empty<DelveWalk>());

    static IReadOnlyList<string> SiblingsOf(DelveGraph graph) => DomainEncounterCoverage.SiblingCollisions(
        graph,
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["room.fight-a"] = "encounter.one-shape",
            ["room.fight-b"] = "encounter.one-shape",
            ["room.elite-a"] = "encounter.one-shape",
        },
        new Dictionary<string, EncounterAnchor>(StringComparer.Ordinal) { ["encounter.one-shape"] = SingleShapeAnchor() },
        ClassifiedCorpus(), delveSeed: 7UL, roomTheta: 500, climate: null, Solo, Hard,
        RealEncounterTuning, RealThreat, RealAptitudes, RealPower, bossSpeciesRef: "species.x");

    [Fact]
    public void SiblingCollisions_null_arguments_throw()
    {
        var graph = GraphOf(Fact(1, 1, "fight", "room.fight-a"));
        var refs = new Dictionary<string, string?>(StringComparer.Ordinal);
        var byId = new Dictionary<string, EncounterAnchor>(StringComparer.Ordinal);
        var corpus = ClassifiedCorpus();

        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(null!, refs, byId, corpus, 7, 500, null, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, "s"));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(graph, null!, byId, corpus, 7, 500, null, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, "s"));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(graph, refs, null!, corpus, 7, 500, null, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, "s"));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(graph, refs, byId, null!, 7, 500, null, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, "s"));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(graph, refs, byId, corpus, 7, 500, null, null!, Hard, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, "s"));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(graph, refs, byId, corpus, 7, 500, null, Solo, null!, RealEncounterTuning, RealThreat, RealAptitudes, RealPower, "s"));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(graph, refs, byId, corpus, 7, 500, null, Solo, Hard, null!, RealThreat, RealAptitudes, RealPower, "s"));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(graph, refs, byId, corpus, 7, 500, null, Solo, Hard, RealEncounterTuning, null!, RealAptitudes, RealPower, "s"));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(graph, refs, byId, corpus, 7, 500, null, Solo, Hard, RealEncounterTuning, RealThreat, null!, RealPower, "s"));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterCoverage.SiblingCollisions(graph, refs, byId, corpus, 7, 500, null, Solo, Hard, RealEncounterTuning, RealThreat, RealAptitudes, null!, "s"));
    }

    [Fact]
    public void An_empty_graph_reports_no_sibling_findings()
    {
        Assert.Empty(SiblingsOf(GraphOf()));
    }

    /// <summary>A single room on a row can never collide with itself — the group needs two members.</summary>
    [Fact]
    public void One_room_on_a_row_is_never_a_finding()
    {
        Assert.Empty(SiblingsOf(GraphOf(Fact(1, 1, "fight", "room.fight-a"))));
    }

    /// <summary>The rule itself: two same-kind siblings on ONE row that both resolve to the one shape
    /// their shared anchor can produce are a finding, named `"{row}:{kind}"`.</summary>
    [Fact]
    public void Two_same_kind_siblings_on_one_row_resolving_to_one_cell_are_a_finding()
    {
        var findings = SiblingsOf(GraphOf(
            Fact(1, 1, "fight", "room.fight-a"),
            Fact(1, 2, "fight", "room.fight-b")));

        Assert.Equal(new[] { "1:fight" }, findings);
    }

    /// <summary>Same kind, DIFFERENT rows — not siblings. `delve-graph-roll`'s own graph-row concept
    /// is the grouping, never "any two rooms of one kind".</summary>
    [Fact]
    public void Same_kind_rooms_on_different_rows_are_never_a_finding()
    {
        Assert.Empty(SiblingsOf(GraphOf(
            Fact(1, 1, "fight", "room.fight-a"),
            Fact(2, 1, "fight", "room.fight-b"))));
    }

    /// <summary>Same row, DIFFERENT kinds — not siblings either; the spec's own words are "same-kind
    /// siblings".</summary>
    [Fact]
    public void Different_kinds_on_one_row_are_never_a_finding()
    {
        Assert.Empty(SiblingsOf(GraphOf(
            Fact(1, 1, "fight", "room.fight-a"),
            Fact(1, 2, "elite", "room.elite-a"))));
    }

    /// <summary>
    /// The real-content proof: roll a real `domain.fire-001` graph through the same
    /// `DomainAnchorBuilder.From` + `DelveGraphRoll.Roll` path `DomainGraphPreflight.Build` uses, then
    /// run the rule over it. Asserts the findings are DETERMINISTIC (two runs, identical) and that every
    /// returned group id parses to a real `(row, kind)` pair present in the rolled graph — so the key
    /// can never name a group the graph does not have.
    /// </summary>
    [Fact]
    public void A_real_rolled_domain_graph_yields_only_deterministic_groups_that_exist_in_the_graph()
    {
        var domain = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).First(d => d.DomainId == "domain.fire-001");
        var palette = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir())[domain.DomainId];
        var rooms = RoomPaletteSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var refs = RoomEncounterRefSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var byId = EncounterSeedFile.LoadAllById(DungeonTestFiles.EncountersDir(), RealThreat);
        var corpus = ClassifiedCorpus();

        var anchor = DomainAnchorBuilder.From(domain, rooms, palette);
        var layout = RealLayoutCatalog().Resolve(domain.LayoutTemplateId);
        Assert.NotNull(layout);
        var graph = DelveGraphRoll.Roll(anchor, layout!, seed: 4242UL, raidMode: "solo", Tuning);

        IReadOnlyList<string> Run() => DomainEncounterCoverage.SiblingCollisions(
            graph, refs, byId, corpus, delveSeed: 4242UL, roomTheta: 500, climate: null, Solo, Hard,
            RealEncounterTuning, RealThreat, RealAptitudes, RealPower, domain.BossSpeciesRef);

        var first = Run();
        Assert.Equal(first, Run());

        var realGroups = graph.Facts.Select(f => $"{f.Row}:{f.Kind}").ToHashSet(StringComparer.Ordinal);
        Assert.All(first, g => Assert.Contains(g, realGroups));
    }


    static LayoutTemplateCatalog RealLayoutCatalog()
    {
        var rows = LayoutSeedFile.LoadAll(DungeonTestFiles.LayoutsDir());
        var bandDefs = new Dictionary<string, BandDef>
        {
            ["depthBand"] = new BandDef { BandName = "depthBand", Members = Tuning.DepthBandRows.Keys.ToList() },
            ["widthBand"] = new BandDef { BandName = "widthBand", Members = Tuning.WidthBandCols.Keys.ToList() },
            ["branchiness"] = new BandDef { BandName = "branchiness", Members = Tuning.BranchinessPathWalks.Keys.ToList() },
            ["density"] = new BandDef
            {
                BandName = "density",
                Members = Tuning.GateDensityPerRoomMilli.Keys
                    .Union(Tuning.SecretDensityPerRoomMilli.Keys).Union(Tuning.OneWayDensityPerRoomMilli.Keys).ToList(),
            },
        };
        var load = LayoutTemplateCatalog.Load(rows, bandDefs, Tuning.RaidModes.Keys.ToList());
        Assert.Empty(load.Rejections);
        return load.Catalog;
    }
}
