using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>
/// D3.3 (spec-event-deck.md §1-§9) — the real orchestrator: <see cref="EventDeck.Build"/>,
/// <see cref="EventDeck.Resolve"/>, <see cref="EventDeck.Answer"/>, <see cref="EventDeck.DrawAmbush"/>.
/// Every subcomponent this composes (filters, draw, unknown-pity, outcome resolver, container build,
/// atom-kind dispatch, choices) already has its own dedicated, mutation-tested suite
/// (`EventFiltersTests`/`EventDrawTests`/`UnknownPityTests`/`OutcomeResolverTests`/
/// `EventEffectContainerBuildTests`/`EventOutcomeDispatchTests`/`EventChoicesTests`) — this file proves
/// the WIRING: that `Resolve` calls them in the right order with the right arguments and assembles a
/// real, honest <see cref="EventResolution"/>, and that `Answer` gates the real dispatch on the player's
/// actual choice (never dispatching on `leave`, never diverging on `use` vs `interact` since the forced-
/// outcome path is unbuilt — see <see cref="EventDeck"/>'s own doc comment, correction (3)).
/// </summary>
public class EventDeckTests
{
    // ---- shared fixtures, mirroring OutcomeResolverTests / EventOutcomeDispatchTests / InstantiatorTests ----

    static readonly IReadOnlyList<string> DropBandOrder = new[] { "staple", "frequent", "occasional", "seldom", "exceptional" };
    static readonly IReadOnlyDictionary<string, int> DropBandWeights = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["staple"] = 1000, ["frequent"] = 300, ["occasional"] = 90, ["seldom"] = 25, ["exceptional"] = 7,
    };

    static readonly DungeonTuning DungeonTuning = DungeonTuningHub.Tuning;
    static readonly DifficultyRungTuning Hard = DungeonTuning.Rungs["hard"]; // EventSeverityTier == 2, real shipped
    static readonly int StackPerCurio = DungeonTuning.AttritionNerve.StackPerCurio;

    // InstantiatorTests' own PinTheta/PowerTuning fixture -- contentScale == 1.000 exactly at theta 20,
    // so a frozen amount is byte-predictable in these tests without re-deriving ContentScale's own formula.
    const int PinTheta = 20;
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);
    static RoomTheta Theta(int theta = PinTheta) => new(new ContentContext(0, 0, 0, 0), theta, 0);

    static readonly Dictionary<string, AtomRow> Atoms = new(StringComparer.Ordinal)
    {
        // channel/amount only -- resource.delta's real schema declares no "op" (AtomKindRegistry.cs:586-599).
        [AtomRow.DeriveId("spirit-drain", "", 1)] = new()
        {
            AtomId = AtomRow.DeriveId("spirit-drain", "", 1), KindId = "resource.delta",
            FamilyId = "spirit-drain", Tier = 1, ParamsJson = "{\"channel\":\"spirit\",\"amount\":-40}",
        },
    };
    static AtomRow? LookupAtom(string id) => Atoms.TryGetValue(id, out var a) ? a : null;
    static AffixRow? LookupAffix(string id) => null; // no pool draws in any fixture container -- never invoked

    static EventOutcomeRow Outcome(string ordinal, string dropBand, string consequence, params EventEffectRef[] effects) =>
        new(ordinal, dropBand, consequence, effects);

    static EventRow Row(
        string id, string kind, string repeatScope = "per-delve", string? supplyOverride = null,
        params EventOutcomeRow[] outcomes) =>
        new(id, kind, Theme: null, ClimateAffinity: null, repeatScope, Eligibility: null, outcomes, supplyOverride, ChainRef: null);

    static EventCatalog CatalogOf(params EventRow[] rows)
    {
        var eventKinds = new[] { "curio", "encounter-event", "shrine", "trap", "bargain", "story" };
        var repeatScopes = new[] { "per-delve", "per-domain", "once-per-player" };
        var ordinals = new[] { "good", "mixed", "bad", "nothing" };
        var overrideTags = new[] { "herbs" };
        var result = EventCatalog.Load(rows, eventKinds, repeatScopes, ordinals, DropBandOrder, overrideTags, _ => -1);
        Assert.Empty(result.Rejections);
        return result.Catalog;
    }

    static FactReader MidHpFacts() => new(
        self: new EntityFacts(0, 0, 500, -1, -1, -1, false, false, 0),
        target: new EntityFacts(0, 0, 500, -1, -1, -1, false, false, 0));

    static DelveMemberState Member(string id, long spirit = 1000) => new(
        id, new Dictionary<string, long> { ["hp"] = 1000, ["stamina"] = 1000, ["hunger"] = 1000, ["spirit"] = spirit, ["qi"] = 1000, ["poise"] = 1000 },
        Array.Empty<BattleStatusSpec>(), Shield: null, NerveStacks: 0, Downed: false, DownedOnce: false);

    static ActorDerivedSnapshot Snapshot() => ActorDerivedSnapshot.FromValues(
        DerivedStatChannels.ResourceIds.SelectMany(id => new[]
        {
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceMax(id), 1000),
            new KeyValuePair<string, double>(DerivedStatChannels.ResourceRegen(id), 0),
        }));

    static DelveRoomFact CurioRoom(string archetypeId = "room.curio.grove", int row = 1, int col = 2) =>
        new(row, col, $"r{row:00}c{col:00}", "curio", archetypeId, BaseBand: 0, IsSecret: false,
            SightLanes: 0, ScoutSightLanes: 0, PartyRouteMask: 0, KeyForLaneId: null);

    static DelveRoomFact UnknownRoom(string archetypeId = "room.unknown.void", int row = 3, int col = 4) =>
        new(row, col, $"r{row:00}c{col:00}", EventFilters.UnknownRoomKind, archetypeId, BaseBand: 0, IsSecret: false,
            SightLanes: 0, ScoutSightLanes: 0, PartyRouteMask: 0, KeyForLaneId: null);

    static readonly IReadOnlyDictionary<string, UnknownPityTuning> CertainCache = PityOf(1000, 0, 0, 0, 0, 0);
    static readonly IReadOnlyDictionary<string, UnknownPityTuning> ImpossibleAll = PityOf(0, 0, 0, 0, 0, 0);

    static IReadOnlyDictionary<string, UnknownPityTuning> PityOf(
        long cacheBase, long cacheStep, long merchantBase, long merchantStep, long fightBase, long fightStep) =>
        new Dictionary<string, UnknownPityTuning>(StringComparer.Ordinal)
        {
            [UnknownPity.CacheKind] = new(cacheBase, cacheStep),
            [UnknownPity.MerchantKind] = new(merchantBase, merchantStep),
            [UnknownPity.FightKind] = new(fightBase, fightStep),
        };

    // A single-event, single-archetype deck, real end to end: a curio whose sole outcome pair (good/bad,
    // both authored "staple") drains spirit through a real resource.delta atom, and whose SupplyOverride
    // makes "use" a presented (never forced -- see correction (3)) verb alongside "interact".
    static EventDeck CurioDeck(string archetypeId = "room.curio.grove")
    {
        var effect = new EventEffectRef("spirit-drain", "trivial"); // trivial -> tier 1 (UniqueBudget.TierOfPowerBand)
        var row = Row("ev.grove", "curio", supplyOverride: "herbs",
            outcomes: new[]
            {
                Outcome("good", "staple", "none", effect),
                Outcome("bad", "staple", "none", effect),
            });
        var catalog = CatalogOf(row);
        var pools = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [archetypeId] = new[] { "ev.grove" },
        };
        return EventDeck.Build(pools, catalog, LookupAtom, LookupAffix, Tuning);
    }

    // ---- EventDeck.Build ----

    [Fact]
    public void Build_null_arguments_throw()
    {
        var catalog = CatalogOf();
        var pools = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        Assert.Throws<ArgumentNullException>(() => EventDeck.Build(null!, catalog, LookupAtom, LookupAffix, Tuning));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Build(pools, null!, LookupAtom, LookupAffix, Tuning));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Build(pools, catalog, null!, LookupAffix, Tuning));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Build(pools, catalog, LookupAtom, null!, Tuning));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Build(pools, catalog, LookupAtom, LookupAffix, null!));
    }

    [Fact]
    public void Build_resolves_archetype_pool_ids_into_real_EventRows()
    {
        var deck = CurioDeck();
        var pool = deck.PoolFor("room.curio.grove");
        Assert.Single(pool);
        Assert.Equal("ev.grove", pool[0].EventId);
    }

    [Fact]
    public void Build_an_unresolvable_event_id_throws_naming_archetype_and_event()
    {
        var catalog = CatalogOf(); // empty catalog
        var pools = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["room.curio.grove"] = new[] { "ev.ghost" },
        };
        var ex = Assert.Throws<EventDeckRefusal>(() => EventDeck.Build(pools, catalog, LookupAtom, LookupAffix, Tuning));
        Assert.Contains("room.curio.grove", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ev.ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PoolFor_an_unmapped_archetype_returns_empty_never_throws()
    {
        var deck = CurioDeck();
        Assert.Empty(deck.PoolFor("room.does.not.exist"));
    }

    // ---- EventDeck.Resolve: argument validation ----

    [Fact]
    public void Resolve_null_arguments_throw()
    {
        var deck = CurioDeck();
        var room = CurioRoom();
        var facts = MidHpFacts();
        Assert.Throws<ArgumentNullException>(() => EventDeck.Resolve(
            null!, room, facts, EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Resolve(
            deck, null!, facts, EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Resolve(
            deck, room, facts, null!, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Resolve(
            deck, room, facts, EventSeenSets.Empty, 1UL, null!, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Resolve(
            deck, room, facts, EventSeenSets.Empty, 1UL, Hard, null!, null, 1000, 1000, 500, DropBandOrder, DropBandWeights));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Resolve(
            deck, room, facts, EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, null!, DropBandWeights));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Resolve(
            deck, room, facts, EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, null!));
    }

    // ---- EventDeck.Resolve: ordinary event room, real end-to-end through Instantiator ----

    [Fact]
    public void Resolve_an_ordinary_room_draws_an_event_an_outcome_and_a_real_instance()
    {
        var deck = CurioDeck();
        var resolution = EventDeck.Resolve(
            deck, CurioRoom(), MidHpFacts(), EventSeenSets.Empty, seed: 12345UL, Hard, Theta(), roomClimate: null,
            1000, 1000, 500, DropBandOrder, DropBandWeights);

        Assert.Equal("ev.grove", resolution.EventId);
        Assert.Equal("curio", resolution.Kind);
        Assert.Contains("good", new[] { "good", "bad" }, StringComparer.Ordinal); // sanity: both are legal draws
        Assert.Contains(resolution.DrawnOutcomeOrdinal, new[] { "good", "bad" });
        Assert.Equal("none", resolution.Consequence);
        Assert.Empty(resolution.Warnings);
        Assert.Null(resolution.NextPity); // not an unknown room -- pity never touched
        Assert.NotNull(resolution.Instance);
        Assert.Equal($"item.ev-grove-{resolution.DrawnOutcomeOrdinal}", resolution.Instance!.ContainerId);
        Assert.Single(resolution.Instance.Atoms);
        Assert.Contains("\"channel\":\"spirit\"", resolution.Instance.Atoms[0].ValuesJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_presents_use_and_interact_never_leave_on_a_non_story_kind()
    {
        var deck = CurioDeck();
        var resolution = EventDeck.Resolve(
            deck, CurioRoom(), MidHpFacts(), EventSeenSets.Empty, 12345UL, Hard, Theta(), null,
            1000, 1000, 500, DropBandOrder, DropBandWeights);

        Assert.Equal(new[] { EventChoices.Use, EventChoices.Interact }, resolution.Choices);
    }

    [Fact]
    public void Resolve_is_deterministic_for_the_same_seed_and_room()
    {
        var deck = CurioDeck();
        var a = EventDeck.Resolve(deck, CurioRoom(), MidHpFacts(), EventSeenSets.Empty, 999UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights);
        var b = EventDeck.Resolve(deck, CurioRoom(), MidHpFacts(), EventSeenSets.Empty, 999UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights);

        Assert.Equal(a.EventId, b.EventId);
        Assert.Equal(a.DrawnOutcomeOrdinal, b.DrawnOutcomeOrdinal);
        Assert.Equal(a.Instance!.Atoms[0].ValuesJson, b.Instance!.Atoms[0].ValuesJson);
    }

    [Fact]
    public void Resolve_use_never_diverges_from_the_ordinary_outcome_draw_since_forced_outcome_is_unbuilt()
    {
        // Direct proof of correction (3): the SAME OutcomeResolver.PickOutcome call this method makes
        // internally, replayed here independently, must equal what Resolve actually drew -- proving
        // "use" never forces a different outcome than the ordinary weighted :outcome draw.
        var deck = CurioDeck();
        var room = CurioRoom();
        var resolution = EventDeck.Resolve(deck, room, MidHpFacts(), EventSeenSets.Empty, 555UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights);

        var expectedOutcome = OutcomeResolver.PickOutcome(
            deck.PoolFor(room.ArchetypeId)[0].Outcomes, Hard.EventSeverityTier, DropBandOrder, DropBandWeights, room.Row, room.Col, 555UL);

        Assert.Equal(expectedOutcome.Ordinal, resolution.DrawnOutcomeOrdinal);
    }

    [Fact]
    public void Resolve_an_empty_pool_after_filters_throws_EventDeckRefusal_naming_the_room()
    {
        var deck = CurioDeck();
        var room = CurioRoom(archetypeId: "room.no.such.archetype"); // PoolFor -> empty
        var ex = Assert.Throws<EventDeckRefusal>(() => EventDeck.Resolve(
            deck, room, MidHpFacts(), EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights));
        Assert.Contains($"({room.Row},{room.Col})", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_a_per_delve_seen_id_is_filtered_out_and_refuses_when_it_was_the_only_option()
    {
        var deck = CurioDeck();
        var room = CurioRoom();
        var seen = new EventSeenSets(
            PerDelveSeen: new HashSet<string> { "ev.grove" },
            PerDomainSeen: new HashSet<string>(), OncePerPlayerSeen: new HashSet<string>(),
            RecentCells: new HashSet<EventFilters.EventCell>());

        Assert.Throws<EventDeckRefusal>(() => EventDeck.Resolve(
            deck, room, MidHpFacts(), seen, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights));
    }

    // ---- EventDeck.Resolve: the unknown room / UnknownPity boundary ----

    [Fact]
    public void Resolve_an_unknown_room_without_partyPity_or_tuning_throws()
    {
        var deck = CurioDeck(archetypeId: "room.unknown.void");
        var room = UnknownRoom();
        Assert.Throws<ArgumentException>(() => EventDeck.Resolve(
            deck, room, MidHpFacts(), EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights,
            partyPity: null, unknownPityTuning: CertainCache));
        Assert.Throws<ArgumentException>(() => EventDeck.Resolve(
            deck, room, MidHpFacts(), EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights,
            partyPity: UnknownPityState.Empty, unknownPityTuning: null));
    }

    [Fact]
    public void Resolve_an_unknown_room_that_pities_into_cache_returns_no_event_and_names_the_callers_own_job()
    {
        var deck = CurioDeck(archetypeId: "room.unknown.void");
        var room = UnknownRoom();

        var resolution = EventDeck.Resolve(
            deck, room, MidHpFacts(), EventSeenSets.Empty, seed: 1UL, Hard, Theta(), null, 1000, 1000, 500,
            DropBandOrder, DropBandWeights, partyPity: UnknownPityState.Empty, unknownPityTuning: CertainCache);

        Assert.Null(resolution.EventId);
        Assert.Equal("cache", resolution.Kind);
        Assert.Empty(resolution.Choices);
        Assert.Null(resolution.DrawnOutcomeOrdinal);
        Assert.Null(resolution.Instance);
        Assert.Equal("none", resolution.Consequence);
        Assert.NotEmpty(resolution.Warnings);
        Assert.Contains("caller's own job", resolution.Warnings[0], StringComparison.Ordinal);
        Assert.NotNull(resolution.NextPity);
        Assert.Equal(0, resolution.NextPity!.Value.MissesCache); // the hit kind resets
        Assert.Equal(1, resolution.NextPity.Value.MissesMerchant); // every other kind advances
        Assert.Equal(1, resolution.NextPity.Value.MissesFight);
    }

    [Fact]
    public void Resolve_an_unknown_room_that_falls_through_pity_draws_an_ordinary_event_and_advances_every_counter()
    {
        var deck = CurioDeck(archetypeId: "room.unknown.void");
        var room = UnknownRoom();

        var resolution = EventDeck.Resolve(
            deck, room, MidHpFacts(), EventSeenSets.Empty, seed: 1UL, Hard, Theta(), null, 1000, 1000, 500,
            DropBandOrder, DropBandWeights, partyPity: UnknownPityState.Empty, unknownPityTuning: ImpossibleAll);

        Assert.Equal("ev.grove", resolution.EventId); // drawn from the unknown archetype's own pool
        Assert.NotNull(resolution.Instance);
        Assert.NotNull(resolution.NextPity);
        Assert.Equal(1, resolution.NextPity!.Value.MissesCache);
        Assert.Equal(1, resolution.NextPity.Value.MissesMerchant);
        Assert.Equal(1, resolution.NextPity.Value.MissesFight);
    }

    // ---- EventDeck.Answer: argument validation ----

    [Fact]
    public void Answer_null_and_bad_arguments_throw()
    {
        var deck = CurioDeck();
        var resolution = EventDeck.Resolve(deck, CurioRoom(), MidHpFacts(), EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights);
        var party = new[] { Member("m1") };
        var sink = new DelveUiPresentSink(1000);

        Assert.Throws<ArgumentNullException>(() => EventDeck.Answer(null!, "interact", party, LookupAtom, _ => Snapshot(), sink, "d1", StackPerCurio, 0));
        Assert.Throws<ArgumentException>(() => EventDeck.Answer(resolution, "", party, LookupAtom, _ => Snapshot(), sink, "d1", StackPerCurio, 0));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Answer(resolution, "interact", null!, LookupAtom, _ => Snapshot(), sink, "d1", StackPerCurio, 0));
        Assert.Throws<ArgumentNullException>(() => EventDeck.Answer(resolution, "interact", party, LookupAtom, _ => Snapshot(), null!, "d1", StackPerCurio, 0));
        Assert.Throws<ArgumentException>(() => EventDeck.Answer(resolution, "wander-off", party, LookupAtom, _ => Snapshot(), sink, "d1", StackPerCurio, 0));
    }

    // ---- EventDeck.Answer: the choice gate -- the verify line's own headline ----

    [Fact]
    public void Answer_leave_dispatches_nothing_and_echoes_the_party_back_unchanged()
    {
        var deck = CurioDeck();
        var resolution = EventDeck.Resolve(deck, CurioRoom(), MidHpFacts(), EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights);
        var party = new[] { Member("m1") };
        var sink = new DelveUiPresentSink(1000);

        var result = EventDeck.Answer(resolution, EventChoices.Leave, party, LookupAtom, _ => Snapshot(), sink, "d1", StackPerCurio, 0);

        Assert.False(result.Applied);
        Assert.Same(party, result.Members); // literally the same list -- nothing recomputed
        Assert.Empty(result.StatDerivedGrants);
        Assert.Equal(1000, party[0].Pools["spirit"]); // unchanged
    }

    [Fact]
    public void Answer_interact_dispatches_the_already_drawn_instance_against_the_party()
    {
        var deck = CurioDeck();
        var resolution = EventDeck.Resolve(deck, CurioRoom(), MidHpFacts(), EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights);
        var party = new[] { Member("m1") };
        var sink = new DelveUiPresentSink(1000);

        var result = EventDeck.Answer(resolution, EventChoices.Interact, party, LookupAtom, _ => Snapshot(), sink, "d1", StackPerCurio, 0);

        Assert.True(result.Applied);
        Assert.Equal(960, result.Members[0].Pools["spirit"]); // 1000 - 40, frozen amount at PinTheta's scale-1.000
        Assert.Equal(StackPerCurio, result.Members[0].NerveStacks); // negative spirit delta -> nerve stack, plain arithmetic
    }

    [Fact]
    public void Answer_use_dispatches_the_identical_outcome_as_interact_since_forced_outcome_is_unbuilt()
    {
        var deck = CurioDeck();
        var resolution = EventDeck.Resolve(deck, CurioRoom(), MidHpFacts(), EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500, DropBandOrder, DropBandWeights);
        var sink = new DelveUiPresentSink(1000);

        var viaInteract = EventDeck.Answer(resolution, EventChoices.Interact, new[] { Member("m1") }, LookupAtom, _ => Snapshot(), sink, "d1", StackPerCurio, 0);
        var viaUse = EventDeck.Answer(resolution, EventChoices.Use, new[] { Member("m1") }, LookupAtom, _ => Snapshot(), sink, "d1", StackPerCurio, 0);

        Assert.Equal(viaInteract.Members[0].Pools["spirit"], viaUse.Members[0].Pools["spirit"]);
        Assert.Equal(viaInteract.Members[0].NerveStacks, viaUse.Members[0].NerveStacks);
    }

    [Fact]
    public void Answer_on_a_resolution_with_no_instance_refuses_naming_the_choice()
    {
        var deck = CurioDeck(archetypeId: "room.unknown.void");
        var room = UnknownRoom();
        var resolution = EventDeck.Resolve(
            deck, room, MidHpFacts(), EventSeenSets.Empty, 1UL, Hard, Theta(), null, 1000, 1000, 500,
            DropBandOrder, DropBandWeights, partyPity: UnknownPityState.Empty, unknownPityTuning: CertainCache);

        Assert.Null(resolution.Instance);
        var ex = Assert.Throws<EventDeckRefusal>(() => EventDeck.Answer(
            resolution, EventChoices.Interact, new[] { Member("m1") }, LookupAtom, _ => Snapshot(), new DelveUiPresentSink(1000), "d1", StackPerCurio, 0));
        Assert.Contains("interact", ex.Message, StringComparison.Ordinal);
    }

    // ---- EventDeck.DrawAmbush: thin forward, not a re-implementation ----

    [Fact]
    public void DrawAmbush_forwards_verbatim_to_AmbushDraw_Draw()
    {
        var row = Row("ev.ambush", "encounter-event", outcomes: new[] { Outcome("good", "staple", "none"), Outcome("bad", "staple", "none") });
        var catalog = CatalogOf(row);
        var pool = new[] { catalog.Resolve("ev.ambush")! };
        var facts = MidHpFacts();

        var direct = AmbushDraw.Draw(pool, 5, 6, null, catalog, facts, _ => -1, ambushMilli: 1000, 1000, 1000, 500, seed: 77UL);
        var wrapped = EventDeck.DrawAmbush(pool, 5, 6, null, catalog, facts, _ => -1, ambushMilli: 1000, 1000, 1000, 500, seed: 77UL);

        Assert.Equal(direct.Ambushed, wrapped.Ambushed);
        Assert.Equal(direct.Event?.EventId, wrapped.Event?.EventId);
        Assert.Equal(direct.EmptyPoolWarning, wrapped.EmptyPoolWarning);
    }
}
