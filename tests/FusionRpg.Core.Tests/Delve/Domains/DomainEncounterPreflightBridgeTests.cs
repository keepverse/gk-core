using System.Globalization;
using System.Text.RegularExpressions;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Tests.Delve.Encounter;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>
/// D4.17 row 6's own production wiring (party-dungeon-todo.md, 2026-09-07) — proves
/// <see cref="DomainEncounterPreflight.Build"/> through the actual <see cref="DomainPreflight.Run"/>
/// entry point over the real six shipped domains, their real room palettes, their real
/// `encounterRef`s and the real (pre-filtered, classified) species corpus. Rows 1/2/3/4/9/10 are
/// stubbed to always pass, matching <see cref="DomainGraphPreflightBridgeTests"/>'s own isolation
/// style — this file's only job is row 6.
/// </summary>
public class DomainEncounterPreflightBridgeTests
{
    static readonly CreatureThreatTuning RealThreat = RealAnchorCorpusFixture.ThreatTuning;
    static readonly EncounterTuning RealEncounterTuning = EncounterTuningHub.Tuning;
    static readonly AptitudeTuning RealAptitudes =
        AptitudeTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "aptitudes.v2.json")));
    static readonly PowerTuning RealPower =
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "power-scale.v2.json")));
    static readonly CreatureShapeTuning RealShape =
        CreatureShapeTuningLoader.Parse(File.ReadAllText(Path.Combine(DungeonTestFiles.RepoRoot(), "data", "tuning", "creature-shape.v1.json")));

    /// <summary>The one real, required step: `SlotFilter.Candidates` refuses eagerly on ANY
    /// unclassified corpus entry, so the caller (this bridge's own wiring) must pre-filter, per its
    /// own doc comment.</summary>
    static IReadOnlyList<ConcreteAnchor> ClassifiedCorpus() =>
        EncounterCorpusBuilder.Build(DungeonTestFiles.SpeciesDir(), RealAptitudes, RealPower, RealShape, RealThreat)
            .Where(a => a.ThreatBand is not null).ToList();

    static DomainPreflightInputs InputsIsolatingRow6(Func<DomainRow, IReadOnlyList<DomainRefusal>> checkEncounters) => new(
        DangerBandOrdinals: new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 },
        KnownLayoutIds: DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).Select(d => d.LayoutTemplateId).ToHashSet(StringComparer.Ordinal),
        KnownSpeciesIds: DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).Select(d => d.BossSpeciesRef).ToHashSet(StringComparer.Ordinal),
        ThreatBandOrdinalFor: _ => 99,
        BossFloorRungOrdinal: 0,
        CellsLayoutCanPlace: _ => Array.Empty<(string, string)>(),
        CellsPaletteFills: _ => Array.Empty<(string, string)>(),
        CheckGraphs: _ => Array.Empty<DomainRefusal>(),
        CheckObjects: _ => Array.Empty<DomainRefusal>(),
        CheckEncounters: checkEncounters,
        CheckEvents: _ => Array.Empty<DomainRefusal>(),
        CheckQuests: _ => Array.Empty<DomainRefusal>(),
        KnownDropTableIds: Array.Empty<string>(),
        BoundLootKinds: Array.Empty<string>(),
        LootBindingFor: _ => new Dictionary<string, string>(StringComparer.Ordinal),
        OfferedRungCountFor: _ => 1);

    [Fact]
    public void Build_null_arguments_throw()
    {
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        var refs = RoomEncounterRefSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var byId = EncounterSeedFile.LoadAllById(DungeonTestFiles.EncountersDir(), RealThreat);
        var corpus = ClassifiedCorpus();
        Assert.Throws<ArgumentNullException>(() => DomainEncounterPreflight.Build(null!, refs, byId, corpus, RealEncounterTuning));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterPreflight.Build(palettes, null!, byId, corpus, RealEncounterTuning));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterPreflight.Build(palettes, refs, null!, corpus, RealEncounterTuning));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterPreflight.Build(palettes, refs, byId, null!, RealEncounterTuning));
        Assert.Throws<ArgumentNullException>(() => DomainEncounterPreflight.Build(palettes, refs, byId, corpus, null!));
    }

    /// <summary>
    /// The real end-to-end read, through the actual `DomainPreflight.Run` entry point — and a second
    /// real, structural content gap found by actually running it, not assumed. All six real domains
    /// refuse row 6 today on a boss-retinue-shaped slot (`posture=Bastion, reach=Melee`, top rungs).
    ///
    /// History that constrains this assertion: every one of those refusals once read "has 0
    /// candidate(s) ignoring element" — because only 184/841 real species anchors carried a
    /// `threatBand` at all. The `species-gear-chain` threat-band-fill (T3) closed exactly that
    /// systematic gap by scoring every missing rung, so the suite below pins the two halves
    /// separately: (a) row 6's own wiring still runs end-to-end (six refusals, same rule, same
    /// slot shape, window present); (b) the corpus-wide zero is gone — at least one domain's
    /// slot now counts a candidate ignoring element. Refusals that remain rest on the encounters'
    /// own narrower predicates (targetPreference, per-domain climate spread), which are live
    /// content facts per encounter, not a missing-corpus blocker — and the bridge's own
    /// negative/trivial controls below prove the bridge itself is correct either way.
    /// </summary>
    [Fact]
    public void Row6_against_all_six_real_domains_refuses_boss_retinue_slots_with_candidates_present()
    {
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var palettes = DomainSeedFile.LoadRoomPalettes(DungeonTestFiles.DomainsDir());
        var refs = RoomEncounterRefSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var byId = EncounterSeedFile.LoadAllById(DungeonTestFiles.EncountersDir(), RealThreat);
        var corpus = ClassifiedCorpus();

        var checkEncounters = DomainEncounterPreflight.Build(palettes, refs, byId, corpus, RealEncounterTuning);
        var refusals = DomainPreflight.Run(domains, InputsIsolatingRow6(checkEncounters));

        Assert.Equal(domains.Count, refusals.Count);
        var realDomainIds = domains.Select(d => d.DomainId).ToHashSet(StringComparer.Ordinal);
        var ignoringElementCounts = new List<int>();
        foreach (var r in refusals)
        {
            Assert.Equal("domain.encounter:slot", r.Rule);
            Assert.Contains(r.DomainId, realDomainIds);
            // F16: the message names the FULL tuple. `targetPreference` is the member that used to be
            // omitted while `SlotFilter.Candidates` filtered on it -- five domains refuse on the
            // `Frontline` slot and fire refuses on the `Swarm` one (printed below), so the assertion
            // pins the preference's presence and its closed vocabulary rather than one domain's value.
            Assert.StartsWith("slot[posture=Bastion, reach=Melee, targetPreference=", r.Detail);
            Assert.True(r.Detail.StartsWith("slot[posture=Bastion, reach=Melee, targetPreference=Frontline]", StringComparison.Ordinal)
                || r.Detail.StartsWith("slot[posture=Bastion, reach=Melee, targetPreference=Swarm]", StringComparison.Ordinal),
                $"unexpected demanded preference: {r.Detail}");
            var m = Regex.Match(r.Detail, @"has (\d+) candidate\(s\) ignoring element");
            Assert.True(m.Success, $"row-6 detail changed shape: {r.Detail}");
            ignoringElementCounts.Add(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }
        // The closed blocker, pinned as a population fact rather than a literal count: the
        // systematic zero is gone — at least one boss slot counts a real candidate.
        Assert.Contains(ignoringElementCounts, n => n >= 1);
    }

    /// <summary>The negative control: a domain naming an encounterRef that is NOT a real encounter id
    /// is refused `domain.encounter:ref-missing`, naming the bad id -- proves the bridge really reads
    /// the palette and really validates against the real encounter set, not a coincidence.</summary>
    [Fact]
    public void A_room_palette_naming_an_unreal_encounterRef_is_refused_ref_missing()
    {
        var domain = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).First(d => d.DomainId == "domain.fire-001");
        var palettes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = new[] { "room.bogus-001" } };
        var refs = new Dictionary<string, string?>(StringComparer.Ordinal) { ["room.bogus-001"] = "encounter.does-not-exist" };
        var byId = EncounterSeedFile.LoadAllById(DungeonTestFiles.EncountersDir(), RealThreat);
        var corpus = ClassifiedCorpus();

        var checkEncounters = DomainEncounterPreflight.Build(palettes, refs, byId, corpus, RealEncounterTuning);
        var refusals = DomainPreflight.Run(new[] { domain }, InputsIsolatingRow6(checkEncounters));

        Assert.Single(refusals);
        Assert.Equal("domain.encounter:ref-missing", refusals[0].Rule);
        Assert.Contains("encounter.does-not-exist", refusals[0].Detail);
    }

    /// <summary>A domain whose palette references only rooms with `encounterRef: "none"` never even
    /// calls into `EncounterPreflight.Run` (an empty encounter list is vacuously fine) -- proves the
    /// bridge does not manufacture a refusal where the real content simply has no combat.</summary>
    [Fact]
    public void A_domain_with_no_combat_rooms_at_all_passes_trivially()
    {
        var domain = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).First(d => d.DomainId == "domain.fire-001");
        var palettes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [domain.DomainId] = new[] { "room.rest-none-002" } };
        var refs = RoomEncounterRefSeedFile.LoadAll(DungeonTestFiles.RoomsDir());
        var byId = EncounterSeedFile.LoadAllById(DungeonTestFiles.EncountersDir(), RealThreat);
        var corpus = ClassifiedCorpus();

        var checkEncounters = DomainEncounterPreflight.Build(palettes, refs, byId, corpus, RealEncounterTuning);
        var refusals = DomainPreflight.Run(new[] { domain }, InputsIsolatingRow6(checkEncounters));

        Assert.Empty(refusals);
    }
}
