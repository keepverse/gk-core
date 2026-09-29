using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>
/// D2.1 (spec-encounter-generator.md §2 step 2) — `SlotFilter.Candidates`: a slot is a filter tuple
/// over anchor ordinals; a null `threatBand` is refused loudly, never defaulted.
/// </summary>
public class SlotFilterTests
{
    static readonly IReadOnlySet<ElementTypeId> AllElements =
        Enum.GetValues<ElementTypeId>().ToHashSet();

    static ConcreteAnchor Fixture(
        string id = "test.fixture", string? threatBand = "raider", string aptitudePrimary = "Might",
        string reach = "short", string targetPreference = "frontline", ElementTypeId element = ElementTypeId.Fire) =>
        new()
        {
            SpeciesId = id,
            ThreatBand = threatBand,
            ThreatRung = threatBand is null ? null : RealAnchorCorpusFixture.ThreatTuning.Thresholds.First(t => t.Id == threatBand).Rung,
            AptitudePrimary = aptitudePrimary,
            Reach = Enum.Parse<EncounterReach>(reach, ignoreCase: true),
            TargetPreference = Enum.Parse<TargetPreference>(targetPreference, ignoreCase: true),
            ElementPrimary = element,
        };

    // ---- the null-threatBand refusal — the acceptance line's own headline ----

    [Fact]
    public void A_null_threatBand_anywhere_in_the_corpus_refuses_loudly_never_defaults()
    {
        var corpus = new[] { Fixture("a", threatBand: null), Fixture("b") };
        var slot = new EncounterSlot(Posture.Force, Reach: null, TargetPreference: null, CountBand: "few");
        var window = new ThreatWindow(1, 10);

        var ex = Assert.Throws<EncounterRefusal>(() => SlotFilter.Candidates(corpus, slot, window, AllElements));
        Assert.Contains("'a'", ex.Message);
        Assert.Contains("threatBand", ex.Message);
        Assert.Contains("rung-4", ex.Message); // names what it explicitly refuses to fall back to
    }

    [Fact]
    public void The_null_threatBand_refusal_fires_even_when_that_anchor_would_fail_every_other_filter_too()
    {
        // Deliberately eager: the pseudocode checks ThreatBand FIRST, before posture/reach/element —
        // an unclassified anchor poisons the whole call regardless of whether it would otherwise match.
        var corpus = new[] { Fixture("unclassified", threatBand: null, aptitudePrimary: "Ferocity", reach: "long", element: ElementTypeId.Dark) };
        var slot = new EncounterSlot(Posture.Force, EncounterReach.Melee, TargetPreference.Backline, "few");
        var window = new ThreatWindow(1, 3);
        var narrowSpread = new HashSet<ElementTypeId> { ElementTypeId.Fire };

        Assert.Throws<EncounterRefusal>(() => SlotFilter.Candidates(corpus, slot, window, narrowSpread));
    }

    // ---- unfillable slot ----

    [Fact]
    public void An_unfillable_slot_refuses_with_a_named_reason()
    {
        var corpus = new[] { Fixture(aptitudePrimary: "Might") }; // Force posture
        var slot = new EncounterSlot(Posture.Bastion, Reach: null, TargetPreference: null, CountBand: "few");
        var window = new ThreatWindow(1, 10);

        var ex = Assert.Throws<EncounterRefusal>(() => SlotFilter.Candidates(corpus, slot, window, AllElements));
        Assert.Contains("unfillable", ex.Message);
    }

    // ---- each filter axis, in isolation ----

    [Fact]
    public void Posture_filters_by_AptitudeCatalog_never_a_raw_string_match()
    {
        var corpus = new[] { Fixture("force-one", aptitudePrimary: "Onslaught"), Fixture("bastion-one", aptitudePrimary: "Bulwark") };
        var slot = new EncounterSlot(Posture.Bastion, Reach: null, TargetPreference: null, CountBand: "few");

        var result = SlotFilter.Candidates(corpus, slot, new ThreatWindow(1, 10), AllElements);

        Assert.Equal("bastion-one", Assert.Single(result).SpeciesId);
    }

    [Fact]
    public void A_null_slot_reach_admits_every_reach()
    {
        var corpus = new[] { Fixture("melee", reach: "melee"), Fixture("long", reach: "long") };
        var slot = new EncounterSlot(Posture.Force, Reach: null, TargetPreference: null, CountBand: "few");

        var result = SlotFilter.Candidates(corpus, slot, new ThreatWindow(1, 10), AllElements);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void A_stated_slot_reach_admits_only_that_reach()
    {
        var corpus = new[] { Fixture("melee", reach: "melee"), Fixture("long", reach: "long") };
        var slot = new EncounterSlot(Posture.Force, EncounterReach.Melee, TargetPreference: null, CountBand: "few");

        var result = SlotFilter.Candidates(corpus, slot, new ThreatWindow(1, 10), AllElements);

        Assert.Equal("melee", Assert.Single(result).SpeciesId);
    }

    [Fact]
    public void TargetPreference_filters_the_same_none_means_any_way_as_reach()
    {
        var corpus = new[] { Fixture("swarm", targetPreference: "swarm"), Fixture("elite", targetPreference: "elite") };

        var any = SlotFilter.Candidates(corpus, new EncounterSlot(Posture.Force, null, null, "few"), new ThreatWindow(1, 10), AllElements);
        Assert.Equal(2, any.Count);

        var stated = SlotFilter.Candidates(corpus, new EncounterSlot(Posture.Force, null, TargetPreference.Elite, "few"), new ThreatWindow(1, 10), AllElements);
        Assert.Equal("elite", Assert.Single(stated).SpeciesId);
    }

    [Fact]
    public void The_threat_window_bounds_are_inclusive_and_exclusive_correctly()
    {
        // raider=rung4, warden=rung5, tyrant=rung7
        var corpus = new[] { Fixture("raider", threatBand: "raider"), Fixture("warden", threatBand: "warden"), Fixture("tyrant", threatBand: "tyrant") };
        var slot = new EncounterSlot(Posture.Force, null, null, "few");

        var window45 = SlotFilter.Candidates(corpus, slot, new ThreatWindow(4, 5), AllElements);
        Assert.Equal(new[] { "raider", "warden" }, window45.Select(a => a.SpeciesId));

        Assert.Throws<EncounterRefusal>(() => // rung 6 alone (scourge) admits none of these three -- unfillable
            SlotFilter.Candidates(corpus, slot, new ThreatWindow(6, 6), AllElements));
    }

    [Fact]
    public void Element_spread_admits_only_elements_in_the_set()
    {
        var corpus = new[] { Fixture("fire", element: ElementTypeId.Fire), Fixture("ice", element: ElementTypeId.Ice) };
        var slot = new EncounterSlot(Posture.Force, null, null, "few");
        var monoFireOnly = new HashSet<ElementTypeId> { ElementTypeId.Fire };

        var result = SlotFilter.Candidates(corpus, slot, new ThreatWindow(1, 10), monoFireOnly);

        Assert.Equal("fire", Assert.Single(result).SpeciesId);
    }

    // ---- null-argument validation ----

    [Fact]
    public void Null_arguments_throw()
    {
        var slot = new EncounterSlot(Posture.Force, null, null, "few");
        var window = new ThreatWindow(1, 10);
        Assert.Throws<ArgumentNullException>(() => SlotFilter.Candidates(null!, slot, window, AllElements));
        Assert.Throws<ArgumentNullException>(() => SlotFilter.Candidates(Array.Empty<ConcreteAnchor>(), null!, window, AllElements));
        Assert.Throws<ArgumentNullException>(() => SlotFilter.Candidates(Array.Empty<ConcreteAnchor>(), slot, null!, AllElements));
        Assert.Throws<ArgumentNullException>(() => SlotFilter.Candidates(Array.Empty<ConcreteAnchor>(), slot, window, null!));
    }

    // ---- PostureOf ----

    [Theory]
    [InlineData("Might", Posture.Force)]
    [InlineData("might", Posture.Force)] // case-insensitive
    [InlineData("Agility", Posture.Finesse)]
    [InlineData("Ferocity", Posture.Bastion)]
    public void PostureOf_reads_AptitudeCatalog(string aptitudePrimary, Posture expected) =>
        Assert.Equal(expected, SlotFilter.PostureOf(aptitudePrimary));

    [Fact]
    public void PostureOf_an_unknown_aptitude_throws() =>
        Assert.Throws<InvalidOperationException>(() => SlotFilter.PostureOf("unresolved"));

    // ---- EncounterRefusal shape ----

    [Fact]
    public void EncounterRefusal_names_the_slot_and_the_reason()
    {
        var slot = new EncounterSlot(Posture.Bastion, EncounterReach.Long, TargetPreference.Structure, "many");
        var ex = new EncounterRefusal(slot, "test reason");

        Assert.Same(slot, ex.Slot);
        Assert.Contains("Bastion", ex.Message);
        Assert.Contains("Long", ex.Message);
        Assert.Contains("Structure", ex.Message);
        Assert.Contains("test reason", ex.Message);
    }

    [Fact]
    public void EncounterRefusal_describes_a_null_reach_or_targetPreference_as_any()
    {
        var slot = new EncounterSlot(Posture.Force, null, null, "few");
        var ex = new EncounterRefusal(slot, "reason");
        Assert.Contains("any", ex.Message);
    }

    // ---- ConcreteAnchor.From ----

    [Fact]
    public void ConcreteAnchor_From_computes_CreatureTypeId_with_the_plant_offset()
    {
        var anchor = new AnchorRow(
            SpeciesId: "x", Rarity: "common", ThreatBand: "raider", AptitudePrimary: "Might",
            AptitudeSecondary: null, Pure: true, AttackTempo: "steady", Reach: "short",
            Variants: Array.Empty<string>(), Side: "plant", GameTypeId: 42, ElementPrimary: "fire",
            ElementSecondary: null, DeployMode: "PlantAvatar", Acquisition: Array.Empty<string>(),
            Traits: Array.Empty<string>(), TargetPreference: "frontline");
        var species = new ConcreteSpecies
        {
            SpeciesId = "x", ElementPrimary = ElementTypeId.Fire, GameTypeId = 42, Side = "plant",
            AttackIntervalMs = 1500, TraitPool = new[] { "sturdy" },
        };

        var joined = ConcreteAnchor.From(anchor, species, RealAnchorCorpusFixture.ThreatTuning);

        // T19: the anchor carries its side so the id applies the plant offset — the old
        // floor-plus-gameTypeId value (10042) collided with the zombie sharing gameTypeId 42.
        Assert.Equal("plant", joined.Side);
        Assert.Equal(
            FusionRpg.Core.Creatures.CreatureSpeciesCatalog.CreatureTypeIdFor("plant", 42),
            joined.CreatureTypeId);
        Assert.Equal(60_042, joined.CreatureTypeId);
        Assert.Equal(EncounterReach.Short, joined.Reach);
        Assert.Equal(TargetPreference.Frontline, joined.TargetPreference);
        Assert.Equal(4, joined.ThreatRung); // raider
        Assert.Equal(1500, joined.AttackIntervalMs);
        Assert.Equal(new[] { "sturdy" }, joined.TraitPool);
    }

    [Fact]
    public void ConcreteAnchor_From_a_mismatched_species_id_throws()
    {
        var anchor = new AnchorRow("a", "common", "raider", "Might", null, true, "steady", "short",
            Array.Empty<string>(), "plant", 1, "fire", null, "PlantAvatar", Array.Empty<string>(), Array.Empty<string>(), "frontline");
        var species = new ConcreteSpecies { SpeciesId = "b" };

        Assert.Throws<InvalidOperationException>(() => ConcreteAnchor.From(anchor, species, RealAnchorCorpusFixture.ThreatTuning));
    }

    [Fact]
    public void ConcreteAnchor_From_an_unrecognised_reach_throws()
    {
        var anchor = new AnchorRow("a", "common", "raider", "Might", null, true, "steady", "teleport",
            Array.Empty<string>(), "plant", 1, "fire", null, "PlantAvatar", Array.Empty<string>(), Array.Empty<string>(), "frontline");
        var species = new ConcreteSpecies { SpeciesId = "a" };

        var ex = Assert.Throws<InvalidOperationException>(() => ConcreteAnchor.From(anchor, species, RealAnchorCorpusFixture.ThreatTuning));
        Assert.Contains("teleport", ex.Message);
    }

    [Fact]
    public void ConcreteAnchor_From_an_unrecognised_targetPreference_throws()
    {
        var anchor = new AnchorRow("a", "common", "raider", "Might", null, true, "steady", "short",
            Array.Empty<string>(), "plant", 1, "fire", null, "PlantAvatar", Array.Empty<string>(), Array.Empty<string>(), "annihilate");
        var species = new ConcreteSpecies { SpeciesId = "a" };

        var ex = Assert.Throws<InvalidOperationException>(() => ConcreteAnchor.From(anchor, species, RealAnchorCorpusFixture.ThreatTuning));
        Assert.Contains("annihilate", ex.Message);
    }

    [Fact]
    public void ConcreteAnchor_From_a_null_threatBand_carries_a_null_rung()
    {
        var anchor = new AnchorRow("a", "common", null, "Might", null, true, "steady", "short",
            Array.Empty<string>(), "plant", 1, "fire", null, "PlantAvatar", Array.Empty<string>(), Array.Empty<string>(), "frontline");
        var species = new ConcreteSpecies { SpeciesId = "a" };

        var joined = ConcreteAnchor.From(anchor, species, RealAnchorCorpusFixture.ThreatTuning);

        Assert.Null(joined.ThreatBand);
        Assert.Null(joined.ThreatRung);
    }

    // ---- ThreatWindow ----

    [Theory]
    [InlineData(4, 4, 4, true)]
    [InlineData(4, 6, 3, false)]
    [InlineData(4, 6, 7, false)]
    [InlineData(4, 6, 5, true)]
    public void ThreatWindow_Contains_is_inclusive_on_both_ends(int floor, int ceil, int rung, bool expected) =>
        Assert.Equal(expected, new ThreatWindow(floor, ceil).Contains(rung));

    // ---- against the real corpus: "candidate counts per slot over the fully banded corpus" ----

    [Fact]
    public void The_real_corpus_is_fully_banded_no_silent_default_species()
    {
        // threat-band-fill T3: the fill wrote every missing rung (scored from the dump where a
        // signal existed), so no shipped anchor reads as the silent default anymore. Pin the
        // invariant as a ratio (every anchor banded), never the population count — the corpus
        // grows whenever content ships (validation-ssot.md §1).
        var all = RealAnchorCorpusFixture.All.ToList();
        Assert.True(all.Count > 0);
        var banded = all.Where(a => a.ThreatBand is not null).ToList();
        Assert.Equal(all.Count, banded.Count);
    }

    [Fact]
    public void Candidate_counts_over_the_real_banded_corpus_are_stable_and_nonzero_for_a_populated_slot()
    {
        var banded = RealAnchorCorpusFixture.All.Where(a => a.ThreatBand is not null).ToList();
        // bastion|short|backline is the single largest real combination (32 of 176) -- verified
        // 2026-09-06 directly against the committed corpus.
        var slot = new EncounterSlot(Posture.Bastion, EncounterReach.Short, TargetPreference.Backline, "few");
        var window = new ThreatWindow(1, 10);

        var result = SlotFilter.Candidates(banded, slot, window, AllElements);

        Assert.True(result.Count >= 20, $"expected at least 20 bastion|short|backline anchors, got {result.Count}");
        // every returned row genuinely satisfies the tuple -- not just "count looks right"
        Assert.All(result, a =>
        {
            Assert.Equal(Posture.Bastion, SlotFilter.PostureOf(a.AptitudePrimary));
            Assert.Equal(EncounterReach.Short, a.Reach);
            Assert.Equal(TargetPreference.Backline, a.TargetPreference);
        });
    }

    [Fact]
    public void An_empty_element_spread_over_the_real_corpus_is_unfillable()
    {
        var banded = RealAnchorCorpusFixture.All.Where(a => a.ThreatBand is not null).ToList();
        var slot = new EncounterSlot(Posture.Force, null, null, "few");

        Assert.Throws<EncounterRefusal>(() =>
            SlotFilter.Candidates(banded, slot, new ThreatWindow(1, 10), new HashSet<ElementTypeId>()));
    }

    [Fact]
    public void Every_real_reach_value_present_in_the_corpus_parses_without_throwing()
    {
        // ConcreteAnchor.From already ran for all 176+ rows inside RealAnchorCorpusFixture's own
        // static build -- if any real anchor's reach/targetPreference string failed to parse, the
        // fixture's own static initializer would have thrown before any test in this class ran.
        // This test names that guarantee explicitly rather than leaving it merely implied.
        Assert.NotEmpty(RealAnchorCorpusFixture.All);
        var reachesSeen = RealAnchorCorpusFixture.All.Select(a => a.Reach).Distinct().ToList();
        Assert.Contains(EncounterReach.Short, reachesSeen);
        Assert.Contains(EncounterReach.Long, reachesSeen);
        Assert.Contains(EncounterReach.Melee, reachesSeen);
    }
}
