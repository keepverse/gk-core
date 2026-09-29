using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.9 (spec-event-deck.md §9, "Refusals and preflight"): the four rules buildable today,
/// pure over an already-loaded `EventCatalog`.</summary>
public class EventDeckPreflightTests
{
    const int BossOrdinal = 10;
    static readonly Func<string, int> StatusBit = id => id switch { "chilled" => 1, "burning" => 2, _ => -1 };

    static EventCatalog CatalogOf(params EventRow[] rows)
    {
        var eventKinds = new[] { "curio", "encounter-event", "shrine", "trap", "bargain", "story" };
        var repeatScopes = new[] { "per-delve", "per-domain", "once-per-player" };
        var ordinals = new[] { "good", "mixed", "bad", "nothing" };
        var dropBands = new[] { "staple", "frequent", "occasional", "seldom", "exceptional" };
        var overrideTags = new[] { "herbs", "key", "holy", "bait", "watch" };
        var result = EventCatalog.Load(rows, eventKinds, repeatScopes, ordinals, dropBands, overrideTags, StatusBit);
        Assert.Empty(result.Rejections);
        return result.Catalog;
    }

    static EventOutcomeRow Outcome(string ordinal) => new(ordinal, "staple", "none", Array.Empty<EventEffectRef>());

    static EventRow Row(string id, string kind = "curio", PredicateNode? eligibility = null,
        string? chainRef = null, string? supplyOverride = null, params string[] outcomeOrdinals) => new(
        EventId: id, Kind: kind, Theme: null, ClimateAffinity: null, RepeatScope: "per-delve",
        Eligibility: eligibility,
        Outcomes: outcomeOrdinals.Length > 0
            ? outcomeOrdinals.Select(Outcome).ToArray()
            : new[] { Outcome("good"), Outcome("bad") },
        SupplyOverride: supplyOverride, ChainRef: chainRef);

    static string DetailOf(AtomRejection r) => r.Detail;

    // ---- CheckOutcomeMix ----

    [Fact]
    public void CheckOutcomeMix_null_catalog_throws()
    {
        Assert.Throws<ArgumentNullException>(() => EventDeckPreflight.CheckOutcomeMix(null!));
    }

    [Theory]
    [InlineData("good", "bad")]
    [InlineData("good", "mixed")]
    [InlineData("good", "bad", "mixed")]
    public void A_good_plus_bad_or_mixed_event_passes(params string[] ordinals)
    {
        var catalog = CatalogOf(Row("e1", outcomeOrdinals: ordinals));
        Assert.Empty(EventDeckPreflight.CheckOutcomeMix(catalog));
    }

    [Fact]
    public void A_nothing_outcome_on_a_story_event_does_not_interfere_with_the_good_bad_check()
    {
        // "nothing" is legal only on kind:story (EventCatalog.Load's own D3.1 rule), and story
        // separately requires its own chainRef -- both satisfied here so this fixture actually loads.
        var catalog = CatalogOf(Row("e1", "story", chainRef: "e1", outcomeOrdinals: new[] { "good", "bad", "nothing" }));
        Assert.Empty(EventDeckPreflight.CheckOutcomeMix(catalog));
    }

    [Fact]
    public void An_event_with_only_good_outcomes_fails()
    {
        var catalog = CatalogOf(Row("e1", outcomeOrdinals: new[] { "good", "good" }));
        var fails = EventDeckPreflight.CheckOutcomeMix(catalog);
        var f = Assert.Single(fails);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, f.Reason);
        Assert.Contains(EventRules.MissingRequiredOutcomeMix, DetailOf(f));
        Assert.Contains("e1", DetailOf(f));
    }

    [Fact]
    public void An_event_with_no_good_outcome_fails()
    {
        var catalog = CatalogOf(Row("e1", outcomeOrdinals: new[] { "bad", "mixed" }));
        Assert.Single(EventDeckPreflight.CheckOutcomeMix(catalog));
    }

    [Fact]
    public void Multiple_bad_events_each_produce_their_own_named_rejection()
    {
        var catalog = CatalogOf(
            Row("bad1", outcomeOrdinals: new[] { "good", "good" }),
            Row("bad2", outcomeOrdinals: new[] { "bad", "mixed" }),
            Row("ok1", outcomeOrdinals: new[] { "good", "bad" }));

        var fails = EventDeckPreflight.CheckOutcomeMix(catalog);
        Assert.Equal(2, fails.Count);
        Assert.Contains(fails, f => DetailOf(f).Contains("bad1"));
        Assert.Contains(fails, f => DetailOf(f).Contains("bad2"));
    }

    // ---- CheckChainRefs ----

    [Fact]
    public void CheckChainRefs_null_catalog_throws()
    {
        Assert.Throws<ArgumentNullException>(() => EventDeckPreflight.CheckChainRefs(null!));
    }

    [Fact]
    public void No_chainRef_at_all_passes()
    {
        var catalog = CatalogOf(Row("e1"));
        Assert.Empty(EventDeckPreflight.CheckChainRefs(catalog));
    }

    [Fact]
    public void A_same_kind_non_cyclic_chain_passes()
    {
        // "curio" throughout, not "story": D3.1's own separate rule requires every story-kind event to
        // carry a chainRef, so a story chain can never terminate on a plain story row with none -- an
        // unrelated concern this test isn't exercising. "curio" isolates CheckChainRefs' own kind-match
        // + cycle-detection mechanic from that already-tested rule.
        var catalog = CatalogOf(Row("a", "curio", chainRef: "b"), Row("b", "curio"));
        Assert.Empty(EventDeckPreflight.CheckChainRefs(catalog));
    }

    [Fact]
    public void A_chain_to_a_different_kind_fails_with_kind_mismatch()
    {
        var catalog = CatalogOf(Row("a", "story", chainRef: "b"), Row("b", "curio"));
        var fails = EventDeckPreflight.CheckChainRefs(catalog);
        Assert.Contains(fails, f => DetailOf(f).Contains(EventRules.ChainRefKindMismatch));
    }

    [Fact]
    public void An_unresolved_chainRef_is_silently_skipped_not_a_rejection()
    {
        var catalog = CatalogOf(Row("a", "story", chainRef: "does-not-exist"));
        Assert.Empty(EventDeckPreflight.CheckChainRefs(catalog));
    }

    [Fact]
    public void A_two_cycle_flags_both_participating_events()
    {
        var catalog = CatalogOf(Row("a", "story", chainRef: "b"), Row("b", "story", chainRef: "a"));
        var fails = EventDeckPreflight.CheckChainRefs(catalog);
        var cycleFails = fails.Where(f => DetailOf(f).Contains(EventRules.ChainRefCycle)).ToList();
        Assert.Equal(2, cycleFails.Count);
        Assert.Contains(cycleFails, f => DetailOf(f).Contains("'a'"));
        Assert.Contains(cycleFails, f => DetailOf(f).Contains("'b'"));
    }

    [Fact]
    public void A_three_link_chain_with_no_cycle_passes()
    {
        var catalog = CatalogOf(
            Row("a", "curio", chainRef: "b"), Row("b", "curio", chainRef: "c"), Row("c", "curio"));
        Assert.Empty(EventDeckPreflight.CheckChainRefs(catalog));
    }

    // ---- CheckNoRoomKindIsBoss ----

    [Fact]
    public void CheckNoRoomKindIsBoss_null_catalog_throws()
    {
        Assert.Throws<ArgumentNullException>(() => EventDeckPreflight.CheckNoRoomKindIsBoss(null!, BossOrdinal));
    }

    [Fact]
    public void No_eligibility_tree_at_all_passes()
    {
        var catalog = CatalogOf(Row("e1"));
        Assert.Empty(EventDeckPreflight.CheckNoRoomKindIsBoss(catalog, BossOrdinal));
    }

    [Fact]
    public void A_top_level_RoomKindIs_boss_leaf_fails()
    {
        var eligibility = new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: BossOrdinal);
        var catalog = CatalogOf(Row("e1", eligibility: eligibility));
        var fails = EventDeckPreflight.CheckNoRoomKindIsBoss(catalog, BossOrdinal);
        var f = Assert.Single(fails);
        Assert.Contains(EventRules.RoomKindIsBossForbidden, DetailOf(f));
    }

    [Fact]
    public void A_RoomKindIs_leaf_for_a_different_kind_passes()
    {
        var eligibility = new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: 3);
        var catalog = CatalogOf(Row("e1", eligibility: eligibility));
        Assert.Empty(EventDeckPreflight.CheckNoRoomKindIsBoss(catalog, BossOrdinal));
    }

    [Fact]
    public void A_RoomKindIs_boss_leaf_buried_under_And_Or_Not_is_still_caught()
    {
        var buried = new PredicateNode.And(new PredicateNode[]
        {
            new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Self, Value: 500),
            new PredicateNode.Or(new PredicateNode[]
            {
                new PredicateNode.Leaf(LeafId.BandIs, Subject.Target, Value: 1),
                new PredicateNode.Not(new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: BossOrdinal)),
            }),
        });
        var catalog = CatalogOf(Row("e1", eligibility: buried));
        Assert.Single(EventDeckPreflight.CheckNoRoomKindIsBoss(catalog, BossOrdinal));
    }

    // ---- CheckKnownStatusIds ----

    [Fact]
    public void CheckKnownStatusIds_null_arguments_throw()
    {
        var catalog = CatalogOf(Row("e1"));
        Assert.Throws<ArgumentNullException>(() => EventDeckPreflight.CheckKnownStatusIds(null!, StatusBit));
        Assert.Throws<ArgumentNullException>(() => EventDeckPreflight.CheckKnownStatusIds(catalog, null!));
    }

    [Fact]
    public void A_known_status_id_passes()
    {
        var eligibility = new PredicateNode.Leaf(LeafId.HasStatus, Subject.Self, Text: "chilled");
        var catalog = CatalogOf(Row("e1", eligibility: eligibility));
        Assert.Empty(EventDeckPreflight.CheckKnownStatusIds(catalog, StatusBit));
    }

    [Fact]
    public void An_unknown_status_id_fails_naming_the_id()
    {
        var eligibility = new PredicateNode.Leaf(LeafId.HasStatus, Subject.Self, Text: "typo-status");
        var catalog = CatalogOf(Row("e1", eligibility: eligibility));
        var fails = EventDeckPreflight.CheckKnownStatusIds(catalog, StatusBit);
        var f = Assert.Single(fails);
        Assert.Contains("typo-status", DetailOf(f));
        Assert.Contains(EventRules.UnknownStatusId, DetailOf(f));
    }

    [Fact]
    public void An_unknown_status_id_buried_under_And_Or_Not_is_still_caught()
    {
        var buried = new PredicateNode.Not(new PredicateNode.And(new PredicateNode[]
        {
            new PredicateNode.Leaf(LeafId.HpBelowMilli, Subject.Self, Value: 500),
            new PredicateNode.Leaf(LeafId.HasStatus, Subject.Self, Text: "typo-status"),
        }));
        var catalog = CatalogOf(Row("e1", eligibility: buried));
        Assert.Single(EventDeckPreflight.CheckKnownStatusIds(catalog, StatusBit));
    }

    [Fact]
    public void The_same_unknown_status_id_appearing_twice_reports_once()
    {
        var tree = new PredicateNode.Or(new PredicateNode[]
        {
            new PredicateNode.Leaf(LeafId.HasStatus, Subject.Self, Text: "typo-status"),
            new PredicateNode.Leaf(LeafId.HasStatus, Subject.Target, Text: "typo-status"),
        });
        var catalog = CatalogOf(Row("e1", eligibility: tree));
        Assert.Single(EventDeckPreflight.CheckKnownStatusIds(catalog, StatusBit));
    }

    // ---- CheckSupplyOverrideCoverage ----

    [Fact]
    public void CheckSupplyOverrideCoverage_null_arguments_throw()
    {
        var catalog = CatalogOf(Row("e1"));
        Assert.Throws<ArgumentNullException>(() => EventDeckPreflight.CheckSupplyOverrideCoverage(null!, new HashSet<string>()));
        Assert.Throws<ArgumentNullException>(() => EventDeckPreflight.CheckSupplyOverrideCoverage(catalog, null!));
    }

    [Fact]
    public void CheckSupplyOverrideCoverage_a_tag_no_supply_carries_refuses_naming_it()
    {
        var catalog = CatalogOf(Row("e1", supplyOverride: "herbs"));
        var fails = EventDeckPreflight.CheckSupplyOverrideCoverage(catalog, new HashSet<string>(StringComparer.Ordinal));
        Assert.Single(fails);
        Assert.Contains("e1", DetailOf(fails[0]));
        Assert.Contains("herbs", DetailOf(fails[0]));
        Assert.Contains(EventRules.OverrideTagUnsupplied, DetailOf(fails[0]));
    }

    [Fact]
    public void CheckSupplyOverrideCoverage_a_tag_a_real_supply_carries_passes()
    {
        var catalog = CatalogOf(Row("e1", supplyOverride: "herbs"));
        var fails = EventDeckPreflight.CheckSupplyOverrideCoverage(catalog, new HashSet<string>(StringComparer.Ordinal) { "herbs" });
        Assert.Empty(fails);
    }

    [Fact]
    public void CheckSupplyOverrideCoverage_an_event_with_no_supplyOverride_is_never_checked()
    {
        var catalog = CatalogOf(Row("e1")); // supplyOverride: null (the "none" sentinel)
        var fails = EventDeckPreflight.CheckSupplyOverrideCoverage(catalog, new HashSet<string>(StringComparer.Ordinal));
        Assert.Empty(fails);
    }

    // ---- Run: everything together ----

    [Fact]
    public void Run_combines_every_rule_and_reports_every_violation()
    {
        var bossGate = new PredicateNode.Leaf(LeafId.RoomKindIs, Subject.Target, Value: BossOrdinal);
        var catalog = CatalogOf(
            Row("onlyGood", outcomeOrdinals: new[] { "good", "good" }),
            Row("gatesBoss", eligibility: bossGate),
            Row("clean", outcomeOrdinals: new[] { "good", "bad" }));

        var fails = EventDeckPreflight.Run(catalog, BossOrdinal, StatusBit);
        Assert.Contains(fails, f => DetailOf(f).Contains("onlyGood") && DetailOf(f).Contains(EventRules.MissingRequiredOutcomeMix));
        Assert.Contains(fails, f => DetailOf(f).Contains("gatesBoss") && DetailOf(f).Contains(EventRules.RoomKindIsBossForbidden));
        Assert.DoesNotContain(fails, f => DetailOf(f).Contains("clean"));
    }

    /// <summary>The optional 5th-rule wiring: absent (default) never runs the check at all — not just
    /// "runs it and it happens to pass" — proven by a fixture that WOULD fail it if it ran; supplied,
    /// the same fixture DOES refuse.</summary>
    [Fact]
    public void Run_only_checks_supply_override_coverage_when_the_caller_opts_in()
    {
        var catalog = CatalogOf(Row("needsHerbs", supplyOverride: "herbs", outcomeOrdinals: new[] { "good", "bad" }));

        var withoutOptIn = EventDeckPreflight.Run(catalog, BossOrdinal, StatusBit);
        Assert.DoesNotContain(withoutOptIn, f => DetailOf(f).Contains(EventRules.OverrideTagUnsupplied));

        var withOptIn = EventDeckPreflight.Run(catalog, BossOrdinal, StatusBit, new HashSet<string>(StringComparer.Ordinal));
        Assert.Contains(withOptIn, f => DetailOf(f).Contains(EventRules.OverrideTagUnsupplied));
    }

    // ---- CheckEventOutcomeAtomKinds ----
    // The "non-event atom kind" conjunct of D3.9's tenth spec-listed rule, built 2026-09-22. Scoped to
    // the containers an event's own outcome ACTUALLY resolves to (EventEffectContainerBuild.From -- the
    // same resolver EventDeck.Resolve calls), never the whole container store.

    /// <summary>An event outcome whose effect refs resolve to atoms of one of spec §5's five kinds.
    /// `AtomRow.DeriveId(family, "", tier)` is the same derivation EventEffectContainerBuild.From uses,
    /// so these fixtures must key their atoms on that exact id.</summary>
    static EventRow AtomKindRow(string id, string family, string powerBand, params string[] ordinals) => new(
        EventId: id, Kind: "curio", Theme: null, ClimateAffinity: null, RepeatScope: "per-delve",
        Eligibility: null,
        Outcomes: ordinals.Select(o => new EventOutcomeRow(o, "staple", "none", new[] { new EventEffectRef(family, powerBand) })).ToArray(),
        SupplyOverride: null, ChainRef: null);

    [Fact]
    public void CheckEventOutcomeAtomKinds_null_arguments_throw()
    {
        var catalog = CatalogOf(Row("e1"));
        Assert.Throws<ArgumentNullException>(() => EventDeckPreflight.CheckEventOutcomeAtomKinds(null!, LookupOf()));
        Assert.Throws<ArgumentNullException>(() => EventDeckPreflight.CheckEventOutcomeAtomKinds(catalog, null!));
    }

    [Fact]
    public void An_event_whose_outcome_resolves_to_a_dispatch_kind_passes()
    {
        // "trivial" -> tier 1 via UniqueBudget.TierOfPowerBand, the same band vocabulary every real
        // anchor uses.
        var atomId = AtomRow.DeriveId("atom.spirit-drain", "", 1);
        var drain = Atom(atomId, "resource.delta", "{\"channel\":\"spirit\",\"amount\":-40}");
        var catalog = CatalogOf(AtomKindRow("e1", "atom.spirit-drain", "trivial", "good", "bad"));

        Assert.Empty(EventDeckPreflight.CheckEventOutcomeAtomKinds(catalog, LookupOf(drain)));
    }

    [Fact]
    public void An_event_whose_outcome_resolves_to_a_non_event_kind_fails()
    {
        // `stat.modify` is a REAL, registered atom kind (AtomKindRegistry) and legitimately appears in
        // the item/trait/species-passive corpus -- it is simply not one of spec §5's five event kinds.
        var atomId = AtomRow.DeriveId("atom.vitality", "", 3); // "medium" -> tier 3 (UniqueBudget.TierOfPowerBand)
        var vitality = Atom(atomId, "stat.modify", "{\"channel\":\"hp\",\"amount\":120}");
        var catalog = CatalogOf(AtomKindRow("e1", "atom.vitality", "medium", "good", "bad"));

        var fails = EventDeckPreflight.CheckEventOutcomeAtomKinds(catalog, LookupOf(vitality));

        Assert.Equal(2, fails.Count); // one per outcome -- both name the same event, different ordinals
        foreach (var f in fails)
        {
            Assert.Contains(EventRules.NonEventAtomKind, DetailOf(f));
            Assert.Contains("e1", DetailOf(f));
            Assert.Contains(atomId, DetailOf(f));
            Assert.Contains("stat.modify", DetailOf(f));
        }
        Assert.Contains(fails, f => DetailOf(f).Contains("'good'"));
        Assert.Contains(fails, f => DetailOf(f).Contains("'bad'"));
    }

    [Fact]
    public void An_unknown_power_band_is_refused_as_unresolved_not_silently_skipped()
    {
        var atomId = AtomRow.DeriveId("atom.vitality", "", 3);
        var vitality = Atom(atomId, "stat.modify");
        var catalog = CatalogOf(AtomKindRow("e1", "atom.vitality", "not-a-band", "good", "bad"));

        var fails = EventDeckPreflight.CheckEventOutcomeAtomKinds(catalog, LookupOf(vitality));

        Assert.Equal(2, fails.Count);
        Assert.All(fails, f => Assert.Contains(EventRules.EffectAtomUnresolved, DetailOf(f)));
    }

    [Fact]
    public void A_family_with_no_atom_at_the_resolved_tier_is_refused_as_unresolved()
    {
        // "high" resolves to tier 4; the catalog only holds tier 1 for this family.
        var atomId = AtomRow.DeriveId("atom.mending", "", 1);
        var mending = Atom(atomId, "resource.delta");
        var catalog = CatalogOf(AtomKindRow("e1", "atom.mending", "high", "good", "bad"));

        var fails = EventDeckPreflight.CheckEventOutcomeAtomKinds(catalog, LookupOf(mending));

        Assert.Equal(2, fails.Count);
        Assert.All(fails, f => Assert.Contains(EventRules.EffectAtomUnresolved, DetailOf(f)));
    }

    [Fact]
    public void An_outcome_with_no_effects_is_refused_as_unresolved_never_thrown()
    {
        var catalog = CatalogOf(Row("e1", outcomeOrdinals: new[] { "good", "bad" })); // Outcome() carries no effects

        var fails = EventDeckPreflight.CheckEventOutcomeAtomKinds(catalog, LookupOf());

        Assert.Equal(2, fails.Count);
        Assert.All(fails, f => Assert.Contains(EventRules.EffectAtomUnresolved, DetailOf(f)));
    }

    /// <summary>The wiring posture, proven both ways with the SAME fixture: the conjunct is a STANDALONE
    /// method (<see cref="EventDeckPreflight.Run"/>'s own contract is unchanged — no new parameter, the
    /// same reason the three domain-bridge rules live in `DomainEventPreflight`), so `Run` never reports
    /// it while the standalone call does.</summary>
    [Fact]
    public void Run_never_reports_the_non_event_atom_kind_conjunct_the_standalone_check_does()
    {
        var atomId = AtomRow.DeriveId("atom.vitality", "", 3);
        var vitality = Atom(atomId, "stat.modify");
        var catalog = CatalogOf(AtomKindRow("e1", "atom.vitality", "medium", "good", "bad"));

        var fromRun = EventDeckPreflight.Run(catalog, BossOrdinal, StatusBit);
        Assert.DoesNotContain(fromRun, f => DetailOf(f).Contains(EventRules.NonEventAtomKind));

        var fromStandalone = EventDeckPreflight.CheckEventOutcomeAtomKinds(catalog, LookupOf(vitality));
        Assert.Contains(fromStandalone, f => DetailOf(f).Contains(EventRules.NonEventAtomKind));
    }

    // ---- CheckNoNerveTargetInAnyContainer ----
    // The nerve.* conjunct of D3.9's tenth spec-listed rule. Catalog-free by design (it scans the
    // container store, not events) -- these fixtures never touch EventCatalog/CatalogOf at all.

    static AtomRow Atom(string id, string kindId, string paramsJson = "{}") => new()
    {
        AtomId = id, KindId = kindId, FamilyId = id, Name = id, ParamsJson = paramsJson,
    };

    static ContainerRow Container(string id,
        IReadOnlyList<ContainerAtomRow>? atoms = null, IReadOnlyList<ContainerPoolRow>? pool = null) => new()
    {
        ContainerId = id, Kind = ContainerKind.Item,
        Atoms = atoms ?? Array.Empty<ContainerAtomRow>(), Pool = pool ?? Array.Empty<ContainerPoolRow>(),
    };

    static Func<string, AtomRow?> LookupOf(params AtomRow[] atoms)
    {
        var byId = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        return id => byId.TryGetValue(id, out var a) ? a : null;
    }

    static Func<string, AffixRow?> AffixLookupOf(params AffixRow[] affixes)
    {
        var byId = affixes.ToDictionary(a => a.AffixId, StringComparer.Ordinal);
        return id => byId.TryGetValue(id, out var a) ? a : null;
    }

    [Fact]
    public void CheckNoNerveTargetInAnyContainer_null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            EventDeckPreflight.CheckNoNerveTargetInAnyContainer(null!, LookupOf(), AffixLookupOf()));
        Assert.Throws<ArgumentNullException>(() =>
            EventDeckPreflight.CheckNoNerveTargetInAnyContainer(Array.Empty<ContainerRow>(), null!, AffixLookupOf()));
        Assert.Throws<ArgumentNullException>(() =>
            EventDeckPreflight.CheckNoNerveTargetInAnyContainer(Array.Empty<ContainerRow>(), LookupOf(), null!));
    }

    [Fact]
    public void An_empty_container_list_passes()
    {
        Assert.Empty(EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            Array.Empty<ContainerRow>(), LookupOf(), AffixLookupOf()));
    }

    [Fact]
    public void A_fixed_atom_status_apply_targeting_nerve_fails()
    {
        var nerveAtom = Atom("atom.fx-nerve-hit.t1", "status.apply", "{\"status\":\"nerve.unsettled\"}");
        var container = Container("item.bad-ring", atoms: new[] { new ContainerAtomRow(0, nerveAtom.AtomId) });

        var fails = EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            new[] { container }, LookupOf(nerveAtom), AffixLookupOf());

        var f = Assert.Single(fails);
        Assert.Contains("item.bad-ring", DetailOf(f));
        Assert.Contains(nerveAtom.AtomId, DetailOf(f));
        Assert.Contains(EventRules.NerveTargetInContainer, DetailOf(f));
    }

    [Fact]
    public void A_pool_affix_status_apply_targeting_nerve_fails()
    {
        // The pool path is a second, independent hop this rule must ALSO resolve: container -> pool
        // row -> affix -> ref -> atom -- never just the fixed core.
        var nerveAtom = Atom("atom.fx-nerve-jolt.t1", "status.apply", "{\"status\":\"nerve.shaken\"}");
        var affix = new AffixRow("affix.nerve-jolt", AffixClass.Suffix, new[] { new AffixRefRow(0, nerveAtom.AtomId) });
        var container = Container("item.cursed-band", pool: new[] { new ContainerPoolRow(affix.AffixId, 10) });

        var fails = EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            new[] { container }, LookupOf(nerveAtom), AffixLookupOf(affix));

        var f = Assert.Single(fails);
        Assert.Contains("item.cursed-band", DetailOf(f));
        Assert.Contains(nerveAtom.AtomId, DetailOf(f));
    }

    [Fact]
    public void A_status_apply_targeting_an_ordinary_status_passes()
    {
        var butterAtom = Atom("atom.fx-butter.t1", "status.apply", "{\"status\":\"butter\"}");
        var container = Container("item.fine-ring", atoms: new[] { new ContainerAtomRow(0, butterAtom.AtomId) });

        Assert.Empty(EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            new[] { container }, LookupOf(butterAtom), AffixLookupOf()));
    }

    [Fact]
    public void A_non_status_apply_atom_is_never_inspected_even_when_its_own_id_says_nerve()
    {
        // Proves the check resolves to the real AtomRow's own KindId/ParamsJson, never a substring
        // match on an id -- the exact real collision this program found while scoping this rule:
        // gk-data/packs/fusion/data/seed/passive-tree/nodes/nerve.unsettled.json is a real, unrelated, legitimate tree whose
        // own affixIds are ordinary stat.modify atoms, none of them status.apply, none targeting nerve.*.
        var lookalike = Atom("atom.nerve-brace.t1", "stat.modify", "{\"channel\":\"maxHp\",\"amount\":10}");
        var container = Container("item.nerve-guard", atoms: new[] { new ContainerAtomRow(0, lookalike.AtomId) });

        Assert.Empty(EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            new[] { container }, LookupOf(lookalike), AffixLookupOf()));
    }

    [Fact]
    public void A_container_id_containing_nerve_with_ordinary_content_passes()
    {
        // The same proof at the CONTAINER id layer, mirroring the real nerve.unsettled.json collision
        // (an id containing "nerve" whose real content has nothing to do with the status).
        var ordinary = Atom("atom.evd-brace.t1", "stat.modify", "{\"channel\":\"dodge\",\"amount\":5}");
        var container = Container("item.nerve.unsettled-charm", atoms: new[] { new ContainerAtomRow(0, ordinary.AtomId) });

        Assert.Empty(EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            new[] { container }, LookupOf(ordinary), AffixLookupOf()));
    }

    [Fact]
    public void Multiple_bad_containers_each_produce_their_own_named_rejection()
    {
        var nerve1 = Atom("atom.n1.t1", "status.apply", "{\"status\":\"nerve.unsettled\"}");
        var nerve2 = Atom("atom.n2.t1", "status.apply", "{\"status\":\"nerve.afflicted\"}");
        var ok = Atom("atom.ok.t1", "status.apply", "{\"status\":\"butter\"}");

        var containers = new[]
        {
            Container("item.bad1", atoms: new[] { new ContainerAtomRow(0, nerve1.AtomId) }),
            Container("item.bad2", atoms: new[] { new ContainerAtomRow(0, nerve2.AtomId) }),
            Container("item.ok1", atoms: new[] { new ContainerAtomRow(0, ok.AtomId) }),
        };

        var fails = EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            containers, LookupOf(nerve1, nerve2, ok), AffixLookupOf());

        Assert.Equal(2, fails.Count);
        Assert.Contains(fails, f => DetailOf(f).Contains("item.bad1"));
        Assert.Contains(fails, f => DetailOf(f).Contains("item.bad2"));
        Assert.DoesNotContain(fails, f => DetailOf(f).Contains("item.ok1"));
    }

    [Fact]
    public void A_dangling_fixed_atom_ref_is_skipped_not_thrown()
    {
        var container = Container("item.broken", atoms: new[] { new ContainerAtomRow(0, "atom.does-not-exist.t1") });
        Assert.Empty(EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            new[] { container }, LookupOf(), AffixLookupOf()));
    }

    [Fact]
    public void A_dangling_pool_affix_ref_is_skipped_not_thrown()
    {
        var container = Container("item.broken-pool", pool: new[] { new ContainerPoolRow("affix.does-not-exist", 5) });
        Assert.Empty(EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            new[] { container }, LookupOf(), AffixLookupOf()));
    }

    [Fact]
    public void A_slot_ref_with_no_concrete_atom_id_is_skipped()
    {
        // A slot ref carries no AtomId at all (element-domain variant selection, never a kind choice) --
        // ResolvedAtomIds must not throw or misresolve it.
        var affix = new AffixRow("affix.elemental", AffixClass.Prefix, new[]
        {
            new AffixRefRow(0, AtomId: null, SlotName: "E1", SlotDomain: "element",
                SlotPick: 1, SlotAtomPattern: "atom.elemental-power.$E1"),
        });
        var container = Container("item.elemental-ring", pool: new[] { new ContainerPoolRow(affix.AffixId, 10) });

        Assert.Empty(EventDeckPreflight.CheckNoNerveTargetInAnyContainer(
            new[] { container }, LookupOf(), AffixLookupOf(affix)));
    }
}
