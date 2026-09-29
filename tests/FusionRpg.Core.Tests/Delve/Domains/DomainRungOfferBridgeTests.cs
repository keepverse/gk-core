using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Narrative.Hosts;
using FusionRpg.Core.Power;
using FusionRpg.Core.Tests.Dungeon;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>
/// D4.17 row 10's own production wiring (party-dungeon-todo.md, 2026-09-23) — proves
/// <see cref="DomainRungOffer.Build"/> through the actual <see cref="DomainPreflight.Run"/>
/// entry point over the real six shipped domains. Rows 1/2/3/9 run for real (all six pass them
/// with the stubbed inputs below, so every domain genuinely reaches row 10); rows 4-8 are
/// stubbed to always pass, matching <see cref="DomainEncounterPreflightBridgeTests"/>'s own
/// isolation style — this file's only job is row 10.
/// </summary>
public class DomainRungOfferBridgeTests
{
    static readonly PowerTuning RealPower = PowerTuningHub.Tuning;
    static DungeonTuning RealDungeon => DungeonTuningHub.Tuning;

    /// <summary>The owned absence reading — the import-time caller has no player and no world,
    /// so it passes the ONE owned construction site's null-world terms, never an invented literal
    /// (`ParentWorldTermsSource.For`, narrative hosts NR2.18).</summary>
    static readonly ParentWorldTerms AbsenceWorld = ParentWorldTermsSource.For(null);

    static readonly ParentWorldTerms ProgressedWorld = new(WorldTier: 1, ZombossLevel: 0, RealmsAdvanced: 2);

    static DomainPreflightInputs InputsIsolatingRow10(Func<DomainRow, int> offeredRungCountFor) => new(
        DangerBandOrdinals: new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 },
        KnownLayoutIds: DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).Select(d => d.LayoutTemplateId).ToHashSet(StringComparer.Ordinal),
        KnownSpeciesIds: DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir()).Select(d => d.BossSpeciesRef).ToHashSet(StringComparer.Ordinal),
        ThreatBandOrdinalFor: _ => 99,
        BossFloorRungOrdinal: 0,
        CellsLayoutCanPlace: _ => Array.Empty<(string, string)>(),
        CellsPaletteFills: _ => Array.Empty<(string, string)>(),
        CheckGraphs: _ => Array.Empty<DomainRefusal>(),
        CheckObjects: _ => Array.Empty<DomainRefusal>(),
        CheckEncounters: _ => Array.Empty<DomainRefusal>(),
        CheckEvents: _ => Array.Empty<DomainRefusal>(),
        CheckQuests: _ => Array.Empty<DomainRefusal>(),
        KnownDropTableIds: Array.Empty<string>(),
        BoundLootKinds: Array.Empty<string>(),
        LootBindingFor: _ => new Dictionary<string, string>(StringComparer.Ordinal),
        OfferedRungCountFor: offeredRungCountFor);

    static IReadOnlyDictionary<string, int> ShallowOrdinals() =>
        new Dictionary<string, int>(StringComparer.Ordinal) { ["shallow"] = 2 };

    [Fact]
    public void Build_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => DomainRungOffer.Build(null!, RealDungeon, ShallowOrdinals(), AbsenceWorld));
        Assert.Throws<ArgumentNullException>(() => DomainRungOffer.Build(RealPower, null!, ShallowOrdinals(), AbsenceWorld));
        Assert.Throws<ArgumentNullException>(() => DomainRungOffer.Build(RealPower, RealDungeon, null!, AbsenceWorld));
        Assert.Throws<ArgumentNullException>(() => DomainRungOffer.Build(RealPower, RealDungeon, ShallowOrdinals(), null!));
    }

    [Fact]
    public void Delegate_null_domain_throws()
    {
        var offeredFor = DomainRungOffer.Build(RealPower, RealDungeon, ShallowOrdinals(), AbsenceWorld);
        Assert.Throws<ArgumentNullException>(() => offeredFor(null!));
    }

    /// <summary>
    /// The real end-to-end read, through the actual `DomainPreflight.Run` entry point: all six
    /// real shipped domains (`shallow`/`many`, no permadeath override) reach row 10 and every one
    /// offers ≥ 1 rung at import-time clears — zero refusals of any rule. The per-domain count
    /// assertion beside it is the vacuity guard: it proves the bridge ran for every domain rather
    /// than the chain skipping row 10.
    /// </summary>
    [Fact]
    public void Row10_against_all_six_real_domains_offers_at_least_one_rung()
    {
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        Assert.True(domains.Count >= 6, $"expected the six shipped domains, found {domains.Count}");
        // The ordinals map below is complete by construction only while every shipped domain
        // stays on `shallow` — a new band fails loudly here, forcing an explicit update, never a
        // silent guess at its ordinal.
        Assert.All(domains, d => Assert.Equal("shallow", d.DangerBand));

        var offeredFor = DomainRungOffer.Build(RealPower, RealDungeon, ShallowOrdinals(), AbsenceWorld);
        foreach (var d in domains)
            Assert.True(offeredFor(d) >= 1, $"domain '{d.DomainId}' offers no rung at import-time clears");

        var refusals = DomainPreflight.Run(domains, InputsIsolatingRow10(offeredFor));
        Assert.Empty(refusals);
    }

    /// <summary>
    /// Row 10's own 2026-09-07 blocker, closed by measurement: the offered-vs-refused verdict reads
    /// neither `WorldTier`, `ZombossLevel` nor `RealmsAdvanced` (band floor + oath unlocks only —
    /// world terms enter `ContentContext` for Θ, never the offer decision), so the absence reading
    /// and a progressed player's terms give the identical count on every real domain. A
    /// wrong-world worry cannot move this verdict either way.
    /// </summary>
    [Fact]
    public void Offered_count_is_independent_of_parent_world_terms()
    {
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var absenceFor = DomainRungOffer.Build(RealPower, RealDungeon, ShallowOrdinals(), AbsenceWorld);
        var progressedFor = DomainRungOffer.Build(RealPower, RealDungeon, ShallowOrdinals(), ProgressedWorld);

        foreach (var d in domains)
            Assert.Equal(absenceFor(d), progressedFor(d));
    }

    /// <summary>
    /// The negative control: a domain below every rung's floor offers nothing and refuses
    /// `domain.no-rung-offered` naming its own danger band through the real entry point — proving
    /// the bridge refuses for real, not only passes. (An always-1 stub fails here; an always-0
    /// stub fails the all-domains test above.)
    /// </summary>
    [Fact]
    public void A_domain_below_every_rung_floor_refuses_naming_no_rung_offered()
    {
        var domain = new DomainRow(
            DomainId: "domain.test-below-floor", Name: "Test", Flavor: "Test", Theme: "test",
            Climate: "fire", DangerBand: "abyssal", Entry: "many",
            LayoutTemplateId: "layout.test", BossSpeciesRef: "boss.test",
            RetinueFamily: "fire", EntranceHint: "Test", PermadeathFromRung: null);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal) { ["abyssal"] = -1000 };
        var offeredFor = DomainRungOffer.Build(RealPower, RealDungeon, ordinals, AbsenceWorld);

        Assert.Equal(0, offeredFor(domain));

        var inputs = InputsIsolatingRow10(offeredFor) with
        {
            DangerBandOrdinals = ordinals,
            KnownLayoutIds = new HashSet<string>(StringComparer.Ordinal) { "layout.test" },
            KnownSpeciesIds = new HashSet<string>(StringComparer.Ordinal) { "boss.test" },
        };
        var refusals = DomainPreflight.Run(new[] { domain }, inputs);

        var refusal = Assert.Single(refusals);
        Assert.Equal("domain.no-rung-offered", refusal.Rule);
        Assert.Contains("abyssal", refusal.Detail);
    }

    /// <summary>
    /// An unknown `dangerBand` throws loudly from the ordinals lookup rather than mapping
    /// silently — unreachable in a real chain (row 1, `DomainCatalog.Load` over the same mapping,
    /// refuses `domain.bad-danger-band` first), the same loud-refusal posture
    /// `DomainAnchorBuilder` already uses for an unreal room id.
    /// </summary>
    [Fact]
    public void Unknown_danger_band_throws_rather_than_mapping_silently()
    {
        var domains = DomainSeedFile.LoadAll(DungeonTestFiles.DomainsDir());
        var offeredFor = DomainRungOffer.Build(RealPower, RealDungeon, ShallowOrdinals(), AbsenceWorld);
        var unknown = domains[0] with { DangerBand = "mid" };
        Assert.Throws<KeyNotFoundException>(() => offeredFor(unknown));
    }
}
