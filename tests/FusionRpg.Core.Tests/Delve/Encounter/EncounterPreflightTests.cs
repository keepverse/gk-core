using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>D2.7 (spec-encounter-generator.md §8 "Preflight") — model-free, per-domain candidate
/// counts, a rung histogram, and named refusals.</summary>
public class EncounterPreflightTests
{
    static readonly EncounterTuning Tuning = EncounterTuningHub.Tuning;

    static ConcreteAnchor A(string id, string threatBand, string aptitude, string reach, ElementTypeId element) => new()
    {
        SpeciesId = id, ThreatBand = threatBand,
        ThreatRung = RealAnchorCorpusFixture.ThreatTuning.Thresholds.First(t => t.Id == threatBand).Rung,
        AptitudePrimary = aptitude, Reach = Enum.Parse<EncounterReach>(reach, true),
        TargetPreference = TargetPreference.Frontline, ElementPrimary = element,
    };

    static readonly IReadOnlyList<ConcreteAnchor> Corpus = new[]
    {
        A("bastion-fire", "raider", "Bulwark", "short", ElementTypeId.Fire),
        A("force-fire", "raider", "Might", "melee", ElementTypeId.Fire),
        A("force-ice", "warden", "Onslaught", "melee", ElementTypeId.Ice),
    };

    static EncounterAnchor Anchor(EncounterSlot slot, ThreatWindow window) => new(
        Formation.Pack, new[] { slot }, new[] { 0 }, ElementSpreadMode.Mono, window, null);

    // ---- a healthy domain ----

    [Fact]
    public void A_domain_whose_every_slot_has_candidates_is_not_refused()
    {
        var domain = new EncounterDomain("domain-a", ElementTypeId.Fire,
            new[] { Anchor(new EncounterSlot(Posture.Force, EncounterReach.Melee, null, "few"), new ThreatWindow(1, 10)) });

        var result = EncounterPreflight.Run(Corpus, new[] { domain }, Tuning);

        Assert.Empty(result.RefusedDomains);
        Assert.Empty(result.RefusalReasons);
    }

    // ---- refusal: zero candidates regardless of element ----

    /// <summary>F16 (party-dungeon-todo.md, filed and fixed 2026-09-23): the refusal names the slot's FULL
    /// tuple, `targetPreference` included. It used to omit that member while `SlotFilter.Candidates`
    /// filtered on it — so a refusal read as "no Bastion+Melee anchors exist" when the real demand was
    /// Bastion+Melee+Swarm, the misdiagnosis D4.17 row 6 and F15 both had to re-derive by hand.</summary>
    [Fact]
    public void A_refusal_names_the_slots_target_preference_when_one_is_demanded()
    {
        var domain = new EncounterDomain("domain-p", ElementTypeId.Fire,
            new[] { Anchor(new EncounterSlot(Posture.Bastion, EncounterReach.Siege, TargetPreference.Swarm, "few"), new ThreatWindow(1, 10)) });

        var result = EncounterPreflight.Run(Corpus, new[] { domain }, Tuning);

        Assert.Contains("targetPreference=Swarm", result.RefusalReasons["domain-p"]);
        Assert.Contains("reach=Siege", result.RefusalReasons["domain-p"]);
        Assert.Contains("posture=Bastion", result.RefusalReasons["domain-p"]);
    }

    /// <summary>A slot demanding no particular preference keeps the shorter text — the addition is
    /// conditional, never a blank `targetPreference=` for a tuple that does not filter on one.</summary>
    [Fact]
    public void A_refusal_omits_the_target_preference_when_the_slot_demands_none()
    {
        var domain = new EncounterDomain("domain-q", ElementTypeId.Fire,
            new[] { Anchor(new EncounterSlot(Posture.Bastion, EncounterReach.Siege, null, "few"), new ThreatWindow(1, 10)) });

        var result = EncounterPreflight.Run(Corpus, new[] { domain }, Tuning);

        Assert.DoesNotContain("targetPreference", result.RefusalReasons["domain-q"]);
    }

    [Fact]
    public void A_domain_with_a_slot_that_has_zero_candidates_at_all_is_refused_by_name()
    {
        var domain = new EncounterDomain("domain-b", ElementTypeId.Fire,
            new[] { Anchor(new EncounterSlot(Posture.Bastion, EncounterReach.Siege, null, "few"), new ThreatWindow(1, 10)) }); // no siege anchors anywhere

        var result = EncounterPreflight.Run(Corpus, new[] { domain }, Tuning);

        Assert.Equal(new[] { "domain-b" }, result.RefusedDomains);
        Assert.Contains("Siege", result.RefusalReasons["domain-b"]);
    }

    // ---- refusal: candidates exist ignoring element, but the domain's own climate excludes them all ----

    [Fact]
    public void A_domain_refuses_when_candidates_exist_only_off_the_domains_own_climate()
    {
        // force-ice only matches under Ice; this domain's climate is Fire, so "ignoring element"
        // finds it but "under climate" (Fire alone) does not.
        var domain = new EncounterDomain("domain-c", ElementTypeId.Fire,
            new[] { Anchor(new EncounterSlot(Posture.Force, EncounterReach.Melee, null, "few"), new ThreatWindow(5, 5)) }); // rung 5 = warden = force-ice only

        var result = EncounterPreflight.Run(Corpus, new[] { domain }, Tuning);

        Assert.Equal(new[] { "domain-c" }, result.RefusedDomains);
        var reason = result.RefusalReasons["domain-c"];
        Assert.Contains("1 candidate", reason);  // ignoring element: force-ice alone
        Assert.Contains("0 under climate Fire", reason);
    }

    [Fact]
    public void A_null_climate_domain_reads_as_all_six_elements_never_refusing_on_element_alone()
    {
        var domain = new EncounterDomain("domain-d", null,
            new[] { Anchor(new EncounterSlot(Posture.Force, EncounterReach.Melee, null, "few"), new ThreatWindow(5, 5)) });

        var result = EncounterPreflight.Run(Corpus, new[] { domain }, Tuning);

        Assert.Empty(result.RefusedDomains); // force-ice (warden, rung 5) passes under "climate = none -> all six"
    }

    // ---- refusal: a null threatBand anywhere in the corpus counts as zero, not a crash ----

    [Fact]
    public void A_null_threatBand_anywhere_in_the_corpus_is_treated_as_zero_candidates_not_an_uncaught_exception()
    {
        var poisoned = Corpus.Append(A("unclassified", "raider", "Might", "melee", ElementTypeId.Fire) with { ThreatBand = null, ThreatRung = null }).ToList();
        var domain = new EncounterDomain("domain-e", ElementTypeId.Fire,
            new[] { Anchor(new EncounterSlot(Posture.Force, EncounterReach.Melee, null, "few"), new ThreatWindow(1, 10)) });

        var result = EncounterPreflight.Run(poisoned, new[] { domain }, Tuning);

        Assert.Equal(new[] { "domain-e" }, result.RefusedDomains); // the eager null-threatBand refusal poisons every slot
    }

    // ---- multiple domains: independent outcomes ----

    [Fact]
    public void One_refused_domain_does_not_affect_a_healthy_sibling_domain()
    {
        var healthy = new EncounterDomain("healthy", ElementTypeId.Fire,
            new[] { Anchor(new EncounterSlot(Posture.Force, EncounterReach.Melee, null, "few"), new ThreatWindow(1, 10)) });
        var broken = new EncounterDomain("broken", ElementTypeId.Fire,
            new[] { Anchor(new EncounterSlot(Posture.Bastion, EncounterReach.Siege, null, "few"), new ThreatWindow(1, 10)) });

        var result = EncounterPreflight.Run(Corpus, new[] { healthy, broken }, Tuning);

        Assert.Equal(new[] { "broken" }, result.RefusedDomains);
        Assert.DoesNotContain("healthy", result.RefusalReasons.Keys);
    }

    [Fact]
    public void The_first_bad_slot_wins_a_domain_is_refused_at_most_once()
    {
        var twoBaddSlots = new EncounterAnchor(Formation.Pack,
            new[]
            {
                new EncounterSlot(Posture.Bastion, EncounterReach.Siege, null, "few"),
                new EncounterSlot(Posture.Bastion, EncounterReach.Long, null, "few"), // ALSO zero (no long-reach bastion in this fixture)
            },
            new[] { 0, 1 }, ElementSpreadMode.Mono, new ThreatWindow(1, 10), null);
        var domain = new EncounterDomain("domain-f", ElementTypeId.Fire, new[] { twoBaddSlots });

        var result = EncounterPreflight.Run(Corpus, new[] { domain }, Tuning);

        Assert.Single(result.RefusedDomains);
        Assert.Contains("Siege", result.RefusalReasons["domain-f"]); // the FIRST slot's own reason, not the second's
    }

    // ---- rung histogram ----

    [Fact]
    public void The_rung_histogram_counts_the_fixture_corpus_correctly_across_all_ten_rungs()
    {
        var result = EncounterPreflight.Run(Corpus, Array.Empty<EncounterDomain>(), Tuning);

        Assert.Equal(10, result.RungHistogram.Count);
        Assert.Equal(Enumerable.Range(1, 10), result.RungHistogram.Select(r => r.Rung)); // ordered, rung 1..10
        Assert.Equal(2, result.RungHistogram.Single(r => r.Rung == 4).Count); // raider: bastion-fire, force-fire
        Assert.Equal(1, result.RungHistogram.Single(r => r.Rung == 5).Count); // warden: force-ice
        Assert.Equal(0, result.RungHistogram.Single(r => r.Rung == 1).Count); // nuisance: none in this fixture
    }

    [Fact]
    public void The_rung_histogram_excludes_null_threatBand_rows_without_crashing()
    {
        var withUnbanded = Corpus.Append(A("x", "raider", "Might", "melee", ElementTypeId.Fire) with { ThreatBand = null, ThreatRung = null }).ToList();
        var result = EncounterPreflight.Run(withUnbanded, Array.Empty<EncounterDomain>(), Tuning);
        Assert.Equal(2, result.RungHistogram.Single(r => r.Rung == 4).Count); // the null-rung row contributes nothing
    }

    // ---- against the real corpus: the spec's own claim about rungs 2-6 ----

    [Fact]
    public void Against_the_real_corpus_a_domain_whose_window_sits_entirely_on_rungs_2_to_6_is_refused()
    {
        // §8, verbatim: "against today's corpus it refuses every domain whose windows sit on rungs
        // 2-6" -- the middle rungs are real but nearly empty (pest 3 x marauder 1 x raider 10 x
        // warden 10 x scourge 1, per this session's own 2026-09-06 corpus scan), so a slot demanding
        // a posture/reach/element combination absent from that thin slice genuinely has nothing.
        var banded = RealAnchorCorpusFixture.All.Where(a => a.ThreatBand is not null).ToList();
        var narrowSlot = new EncounterSlot(Posture.Bastion, EncounterReach.Siege, null, "few"); // 0 siege anchors exist at all, any rung
        var domain = new EncounterDomain("real-domain", ElementTypeId.Fire,
            new[] { Anchor(narrowSlot, new ThreatWindow(2, 6)) });

        var result = EncounterPreflight.Run(banded, new[] { domain }, Tuning);

        Assert.Equal(new[] { "real-domain" }, result.RefusedDomains);
    }

    // ---- null-argument validation ----

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => EncounterPreflight.Run(null!, Array.Empty<EncounterDomain>(), Tuning));
        Assert.Throws<ArgumentNullException>(() => EncounterPreflight.Run(Corpus, null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => EncounterPreflight.Run(Corpus, Array.Empty<EncounterDomain>(), null!));
    }
}
