using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// Spec §8's own three seen-sets, `RecentCells` (§2 rule 4) alongside them since every one of the four is
/// the identical "read model owned elsewhere" idiom — <see cref="EventFilters.ByRepeatScope"/>/
/// <see cref="EventFilters.ByRecentCells"/>'s own already-shipped parameters, bundled into one record so
/// <see cref="EventDeck.Resolve"/> takes one seen-state argument instead of four positional sets.
/// <b>The store-layer reads/writes that assemble and persist these four sets are D3.9's own separate,
/// still-unbuilt job</b> (`rpg_delve_event_seen`, `rpg_delve_rooms.event_id`, spec §8's own table) — this
/// record is the caller-supplied input <see cref="EventFilters"/> already committed to, restated as a
/// named type rather than four loose parameters, matching every other bundled read model this program
/// already ships (`QuestRewardBankingInputs`, `BossFirstClearGrantInputs`).
/// </summary>
public sealed record EventSeenSets(
    IReadOnlySet<string> PerDelveSeen,
    IReadOnlySet<string> PerDomainSeen,
    IReadOnlySet<string> OncePerPlayerSeen,
    IReadOnlySet<EventFilters.EventCell> RecentCells)
{
    public static EventSeenSets Empty { get; } = new(
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<EventFilters.EventCell>());
}

/// <summary>
/// Spec §1's own output shape, corrected against real dependency types (see <see cref="EventDeck"/>'s own
/// doc comment for the two named corrections: <c>Banner</c> moved to <see cref="EventAnswerResult"/>;
/// <c>NextPity</c> added). <c>EventId</c>/<c>Instance</c>/<c>Choices</c> are all null/empty together when
/// an `unknown` room's pity resolved to `cache`/`merchant`/`fight` instead of an ordinary event —
/// materialising THAT resolution is explicitly out of this module's own scope (spec §4; `dungeon-loot`'s
/// cache draw, `encounter-generator`'s fight) and is named in <see cref="Warnings"/> rather than silently
/// left for a caller to discover the hard way.
/// </summary>
public sealed record EventResolution(
    string? EventId,
    string Kind,
    IReadOnlyList<string> Choices,
    string? DrawnOutcomeOrdinal,
    InstanceRow? Instance,
    string Consequence,
    IReadOnlyList<string> Warnings,
    UnknownPityState? NextPity);

/// <summary>
/// The result of <see cref="EventDeck.Answer"/> — spec §1's own "the host applies it": `Members`/
/// `StatDerivedGrants`/`Banners` are <see cref="EventOutcomeDispatch.Dispatch"/>'s own real output
/// (never applied for `leave`, `Applied: false` and <paramref name="EventAnswerResult.Members"/> echoing
/// the input party back unchanged).
/// </summary>
public sealed record EventAnswerResult(
    string Choice,
    bool Applied,
    IReadOnlyList<DelveMemberState> Members,
    IReadOnlyList<StatDerivedGrant> StatDerivedGrants,
    IReadOnlyList<DelveBanner> Banners);

/// <summary>
/// `event-deck` D3.3 (spec-event-deck.md §1-§9) — the real `Build`/`Resolve`/`Answer` orchestrator, the
/// last piece of this module. Composes D3.1 (<see cref="EventCatalog"/>), D3.2
/// (<see cref="EventFilters"/>), D3.3's own already-shipped <see cref="EventDraw"/>, D3.4
/// (<see cref="UnknownPity"/>), D3.5 (<see cref="OutcomeResolver"/>, <see cref="EventEffectContainerBuild"/>,
/// <see cref="EventOutcomeDispatch"/>) and D3.8 (<see cref="EventChoices"/>) end to end, plus the one
/// real call site into <c>Instantiator.TryInstantiate</c> spec §5's pseudocode names but no shipped
/// caller had reached yet.
///
/// <para><b>Corrections against the spec's own literal citations, named rather than silently
/// followed:</b> (1) spec §1's pseudocode reads <c>tuning.DropBandWeight(...)</c> off a single
/// <c>DungeonTuning</c> object — no such member exists on the real, shipped type (`dropBand` is the
/// ITEM registry's own vocabulary, D3.1's own citation: "this module has no business owning" it,
/// already the reason <see cref="OutcomeResolver.WeightFor"/> takes a plain caller-supplied table). This
/// module's own <see cref="Resolve"/> takes <c>dropBandOrder</c>/<c>dropBandWeightTable</c> as explicit
/// parameters instead, matching <c>OutcomeResolverTests</c>' own established "local fixture, this module
/// never reads that registry" posture. (2) <c>EventResolution</c>'s own "never a write" framing (§1) is
/// read literally as a scope line, not just a style note: <see cref="Resolve"/> computes and freezes the
/// outcome (draw + <c>Instantiator.TryInstantiate</c>) but never calls
/// <see cref="EventOutcomeDispatch.Dispatch"/> — that is <see cref="Answer"/>'s own job, gated on the
/// player's actual choice, so a `leave` on a `story` event genuinely touches no party state at all. This
/// means <c>EventResolution</c>'s own <c>Banner</c> field (spec's interface table, `:421`) cannot be
/// populated by <see cref="Resolve"/> either — a banner is a dispatch side effect
/// (<c>EventOutcomeDispatch.ApplyUiPresent</c>'s own <see cref="IUiPresentSink.ShowBanner"/> call), which
/// has not run yet at <see cref="Resolve"/> time — so it moves to <see cref="EventAnswerResult"/> instead,
/// named here as a correction rather than dropped silently. (3) The forced-outcome/`supplyOverride` path
/// (§5's own "Forced outcome" paragraph) is STILL NOT WIRED HERE, but for a narrower reason than
/// before: the resolver-level logic now exists and is tested
/// (<see cref="OutcomeResolver.TryForcedOutcome"/>/<see cref="OutcomeResolver.Resolve"/>, 2026-09-08 —
/// no seed-contract field was ever needed, see that method's own doc comment for the citations). What
/// remains is a live `holdsOverrideStock` boolean sourced from the party's own PACK
/// (`spec-event-deck.md:161`: "`HoldsStock` facts are loaded from the pack") — cross-cutting
/// `event-deck`+`loot-pack`+`supplies-and-objects`, none of which has built the connecting glue
/// (`EventFacts.BuildSelf`'s own doc comment: "0 is the honest 'no pack yet' default" even after
/// `loot-pack` itself shipped) — a wiring gap between three already-real modules, not a missing
/// capability in any one of them. <see cref="Resolve"/> still always draws the ordinary weighted
/// `:outcome` here, and `use:{tag}` still applies that SAME drawn outcome exactly like `interact`
/// does, until a real pack-backed fact is threaded through this call.</para>
/// </summary>
public sealed class EventDeck
{
    readonly IReadOnlyDictionary<string, IReadOnlyList<EventRow>> _poolByArchetypeId;

    public EventCatalog Catalog { get; }
    public Func<string, AtomRow?> LookupAtom { get; }
    public Func<string, AffixRow?> LookupAffix { get; }
    public PowerTuning PowerTuning { get; }

    EventDeck(
        IReadOnlyDictionary<string, IReadOnlyList<EventRow>> poolByArchetypeId,
        EventCatalog catalog, Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix,
        PowerTuning powerTuning)
    {
        _poolByArchetypeId = poolByArchetypeId;
        Catalog = catalog;
        LookupAtom = lookupAtom;
        LookupAffix = lookupAffix;
        PowerTuning = powerTuning;
    }

    /// <summary>An archetype (room anchor id) with no authored `eventPool` (most `fight`/`boss` kinds
    /// today) resolves to an empty pool, never a lookup failure — matching
    /// <see cref="RoomEventPoolSeedFile.LoadAll"/>'s own "an empty list is a real, legal value" framing.</summary>
    public IReadOnlyList<EventRow> PoolFor(string archetypeId) =>
        !string.IsNullOrWhiteSpace(archetypeId) && _poolByArchetypeId.TryGetValue(archetypeId, out var pool)
            ? pool
            : Array.Empty<EventRow>();

    /// <summary>
    /// Spec §2 "Deck build": resolves every archetype's own authored `eventPool` (a plain string-id list,
    /// <see cref="RoomEventPoolSeedFile.LoadAll"/>'s own shape, keyed by room id) into real
    /// <see cref="EventRow"/> objects through the already-loaded <paramref name="catalog"/>. Kind-fit
    /// validation (spec §2 rule 1: "checked at LOAD") is `domain-catalog`'s own `DomainEventPreflight`
    /// bridge's job (D3.9's own entry), not repeated here — an id this catalog cannot resolve is a
    /// preflight gap that reaches this constructor, and it refuses loudly rather than silently dropping
    /// the entry, since a silently-shrunk pool is exactly the "blank room" spec's own §9 forbids.
    /// </summary>
    public static EventDeck Build(
        IReadOnlyDictionary<string, IReadOnlyList<string>> eventPoolIdsByArchetypeId,
        EventCatalog catalog,
        Func<string, AtomRow?> lookupAtom,
        Func<string, AffixRow?> lookupAffix,
        PowerTuning powerTuning)
    {
        if (eventPoolIdsByArchetypeId is null) throw new ArgumentNullException(nameof(eventPoolIdsByArchetypeId));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));
        if (lookupAffix is null) throw new ArgumentNullException(nameof(lookupAffix));
        if (powerTuning is null) throw new ArgumentNullException(nameof(powerTuning));

        var byArchetype = new Dictionary<string, IReadOnlyList<EventRow>>(StringComparer.Ordinal);
        foreach (var (archetypeId, eventIds) in eventPoolIdsByArchetypeId)
        {
            var rows = new List<EventRow>(eventIds.Count);
            foreach (var eventId in eventIds)
            {
                var row = catalog.Resolve(eventId) ?? throw new EventDeckRefusal(
                    $"archetype '{archetypeId}' names event '{eventId}', which is not in the catalog -- " +
                    "a preflight gap (domain-catalog's own DomainEventPreflight should have caught this at import)");
                rows.Add(row);
            }
            byArchetype[archetypeId] = rows;
        }
        return new EventDeck(byArchetype, catalog, lookupAtom, lookupAffix, powerTuning);
    }

    /// <summary>
    /// Spec §1-§5, the real `Resolve` — composes the deck build's own pool (D3.2's filters), the pick
    /// (D3.3's own already-shipped <see cref="EventDraw"/>), the severity-shifted outcome draw (D3.5),
    /// the effect container resolver (D3.5) and the one real <c>Instantiator.TryInstantiate</c> call
    /// site spec's own pseudocode names but nothing had reached before this method. An `unknown` room
    /// (<paramref name="room"/>.Kind == "unknown") runs D3.4's own <see cref="UnknownPity"/> first;
    /// falling through to an event continues the ordinary path against the unknown archetype's own pool
    /// (§4, verbatim), while a `cache`/`merchant`/`fight` hit returns early (see
    /// <see cref="EventResolution"/>'s own doc comment) — this method never calls `dungeon-loot` or
    /// `encounter-generator` itself.
    /// </summary>
    /// <param name="deck">Built by <see cref="Build"/> — the domain's own per-archetype pools plus the
    /// atom/affix lookups and the power tuning `Instantiator.TryInstantiate` needs.</param>
    /// <param name="room">The room the graph already fixed (`delve-graph-roll`'s own
    /// <see cref="DelveRoomFact"/>) — `Row`/`Col` namespace every stream, `ArchetypeId` keys
    /// <see cref="PoolFor"/>, `Kind` is the room-archetype kind §2 rule 1 filters against.</param>
    /// <param name="facts">The party/room <see cref="FactReader"/> — D3.8's own <c>EventFacts.BuildSelf</c>/
    /// <c>BuildTarget</c> is the caller's job to have already built.</param>
    /// <param name="seen">§8's own four sets, bundled — see <see cref="EventSeenSets"/>.</param>
    /// <param name="seed">The delve's own sealed seed — every stream derives off this, room-namespaced.</param>
    /// <param name="rung">`difficulty.rungs[rungId]` — `EventSeverityTier` shifts the outcome draw (§5);
    /// `UnknownPityStepMultMilli*` feeds an `unknown` room's own pity chance (§4).</param>
    /// <param name="theta">`Θ_room` — every magnitude in the outcome's instance scales by this, once,
    /// through `Instantiator.TryInstantiate` (§5).</param>
    /// <param name="roomClimate">The room's own climate id, or `null` for a climate-neutral room — feeds
    /// <see cref="EventDraw.WeightMilliFor"/>'s own affinity weighting (§2), never a gate.</param>
    /// <param name="climateAffinityMatchMilli">`events.climateAffinity.matchMilli`.</param>
    /// <param name="climateAffinityNoneMilli">`events.climateAffinity.noneMilli`.</param>
    /// <param name="climateAffinityOffMilli">`events.climateAffinity.offMilli`.</param>
    /// <param name="dropBandOrder">The item registry's own `dropBand.enum`, caller-supplied (this module
    /// has no business owning it, D3.1's own citation) — `staple·frequent·occasional·seldom·exceptional`
    /// in production.</param>
    /// <param name="dropBandWeightTable">The item registry's own `dropBand.weightTable`, caller-supplied —
    /// `1000/300/90/25/7` in production.</param>
    /// <param name="catalogRevision">Fed straight through to `Instantiator.TryInstantiate` — `0` unless
    /// the caller tracks atom-catalog revisions.</param>
    /// <param name="partyPity">Required, and used, only when <paramref name="room"/>.Kind is
    /// <see cref="EventFilters.UnknownRoomKind"/> — this party's own unknown-node pity counters
    /// (`parties_json.pity{}`, R11: per party, never per delve).</param>
    /// <param name="unknownPityTuning">Required alongside <paramref name="partyPity"/> — `nodes.unknown.pity`.</param>
    /// <param name="domain">Optional — passed straight through to <see cref="UnknownPity.Resolve"/>'s own
    /// re-pick path; omitted, a pity hit resolves the KIND but not a concrete archetype (that method's own
    /// documented "not wired here yet" honest gap, unchanged by this caller).</param>
    public static EventResolution Resolve(
        EventDeck deck,
        DelveRoomFact room,
        FactReader facts,
        EventSeenSets seen,
        ulong seed,
        DifficultyRungTuning rung,
        RoomTheta theta,
        string? roomClimate,
        long climateAffinityMatchMilli, long climateAffinityNoneMilli, long climateAffinityOffMilli,
        IReadOnlyList<string> dropBandOrder,
        IReadOnlyDictionary<string, int> dropBandWeightTable,
        long catalogRevision = 0,
        UnknownPityState? partyPity = null,
        IReadOnlyDictionary<string, UnknownPityTuning>? unknownPityTuning = null,
        DomainAnchor? domain = null)
    {
        if (deck is null) throw new ArgumentNullException(nameof(deck));
        if (room is null) throw new ArgumentNullException(nameof(room));
        if (seen is null) throw new ArgumentNullException(nameof(seen));
        if (rung is null) throw new ArgumentNullException(nameof(rung));
        if (theta is null) throw new ArgumentNullException(nameof(theta));
        if (dropBandOrder is null) throw new ArgumentNullException(nameof(dropBandOrder));
        if (dropBandWeightTable is null) throw new ArgumentNullException(nameof(dropBandWeightTable));

        UnknownPityState? nextPity = null;

        if (string.Equals(room.Kind, EventFilters.UnknownRoomKind, StringComparison.Ordinal))
        {
            if (partyPity is null)
                throw new ArgumentException("an 'unknown' room needs partyPity", nameof(partyPity));
            if (unknownPityTuning is null)
                throw new ArgumentException("an 'unknown' room needs unknownPityTuning", nameof(unknownPityTuning));

            var pityResolution = UnknownPity.Resolve(room.Row, room.Col, partyPity.Value, unknownPityTuning, rung, seed, domain);
            nextPity = pityResolution.NextPity;

            if (!string.Equals(pityResolution.Kind, UnknownPity.EventKind, StringComparison.Ordinal))
            {
                // Spec §4: falls through to cache/merchant/fight, not an ordinary event. Materialising
                // it (dungeon-loot's cache draw, encounter-generator's fight) is the CALLER's own job —
                // named here rather than attempted, matching this module's own scope boundary.
                return new EventResolution(
                    EventId: null, Kind: pityResolution.Kind, Choices: Array.Empty<string>(),
                    DrawnOutcomeOrdinal: null, Instance: null, Consequence: "none",
                    Warnings: new[]
                    {
                        $"unknown room ({room.Row},{room.Col}) resolved to '{pityResolution.Kind}' " +
                        $"(archetype {(pityResolution.ArchetypeId ?? "(none)")}) -- materialising it is the " +
                        "caller's own job (dungeon-loot / encounter-generator), not event-deck's",
                    },
                    NextPity: nextPity);
            }
            // Falls through: an ordinary event draw against the unknown archetype's own pool (§2-3).
        }

        var pool = deck.PoolFor(room.ArchetypeId);
        var filtered = EventFilters.ApplyAll(
            pool, room.Kind, deck.Catalog, facts,
            seen.PerDelveSeen, seen.PerDomainSeen, seen.OncePerPlayerSeen, seen.RecentCells);

        var pickedEvent = EventDraw.PickEvent(
            filtered, room.Row, room.Col, roomClimate,
            climateAffinityMatchMilli, climateAffinityNoneMilli, climateAffinityOffMilli, seed);

        // Forced outcome / supplyOverride (§5): the resolver logic exists (OutcomeResolver.Resolve) but
        // is not called here yet -- no real holdsOverrideStock fact reaches this call (see this type's
        // own doc comment, correction (3)). Always the ordinary weighted :outcome draw for now, for
        // every choice including use:{tag}.
        var pickedOutcome = OutcomeResolver.PickOutcome(
            pickedEvent.Outcomes, rung.EventSeverityTier, dropBandOrder, dropBandWeightTable,
            room.Row, room.Col, seed);

        // One owner for the container id (`EventEffectContainerBuild.ContainerIdFor`, extracted
        // 2026-09-22): `EventDeckPreflight.CheckEventOutcomeAtomKinds` resolves the SAME id offline, so
        // the two can never drift apart. It is NOT the raw `eventId:ordinal` spec's pseudocode implies:
        // `ContainerValidator`'s own id grammar (`ContainerValidator.cs:27-29`) admits no second dot and
        // no colon, while a real shipped event id already carries dots of its own
        // (`event.bargain-creature.allpeater-001`) -- so every '.' folds to '-', or every real call fails.
        var containerId = EventEffectContainerBuild.ContainerIdFor(pickedEvent.EventId, pickedOutcome.Ordinal);
        var container = EventEffectContainerBuild.From(containerId, pickedOutcome.Effects, deck.LookupAtom);

        var effectsStreamName = DelveStreams.Event(room.Row, room.Col) + ":effects";
        var effectsRollSeed = unchecked((long)FusionRpg.Core.Battle.SeededRng.DeriveStream(seed, effectsStreamName).NextULong());

        var rejection = Instantiator.TryInstantiate(
            container, deck.LookupAtom, deck.LookupAffix, effectsRollSeed, theta.Theta, deck.PowerTuning,
            out var instance, InstanceOrigin.Drop, catalogRevision);
        if (!rejection.IsOk || instance is null)
            throw new EventDeckRefusal(
                $"room ({room.Row},{room.Col}) event '{pickedEvent.EventId}' outcome '{pickedOutcome.Ordinal}': {rejection}");

        var choices = EventChoices.Presented(pickedEvent);

        return new EventResolution(
            pickedEvent.EventId, pickedEvent.Kind, choices, pickedOutcome.Ordinal, instance,
            pickedOutcome.Consequence, Warnings: Array.Empty<string>(), NextPity: nextPity);
    }

    /// <summary>
    /// Spec §1/§6, the real `Answer` — the player's choice (or autopilot's own, per
    /// <see cref="EventChoices.Autopilot"/>) arrives as a second pure step. `leave` (only ever presented
    /// on `kind: story`) applies nothing — the room's already-drawn outcome is discarded, never dispatched,
    /// matching §5's own "no event gates the boss / a dominant choice is priced" framing that a walk-away
    /// must be a genuinely free, effect-free choice. `interact` and `use:{tag}` both dispatch the SAME
    /// already-drawn <paramref name="resolution"/>.Instance (see this type's own doc comment, correction
    /// (3) — the forced-outcome path is built but not wired into <see cref="Resolve"/> yet, so `use`
    /// never diverges from `interact` here); spending
    /// the tagged supply itself is `supplies-and-objects`' own `SupplyUse.Use` call, a DIFFERENT
    /// transaction this method does not attempt (spec §5: "a `talk` row and the spend a `supply.use` row
    /// — two entries, one transaction" — this method produces the talk half only).
    /// </summary>
    /// <param name="resolution">Whatever <see cref="Resolve"/> already computed for this room — dispatched
    /// at most once, since an event resolves once per room (spec §3).</param>
    /// <param name="choice">One of <see cref="EventChoices.Use"/>/<see cref="EventChoices.Interact"/>/
    /// <see cref="EventChoices.Leave"/> — not re-validated against <see cref="EventChoices.Presented"/>/
    /// <see cref="EventChoices.IsEligible"/> here; that gate is the caller's own job (the same "read
    /// model owned elsewhere" split every other method in this module already takes), since this method
    /// has no supply-stock fact to check eligibility against.</param>
    /// <param name="party">Every party member, standing or not — <see cref="EventOutcomeDispatch.Dispatch"/>'s
    /// own contract, unchanged for `leave`.</param>
    public static EventAnswerResult Answer(
        EventResolution resolution,
        string choice,
        IReadOnlyList<DelveMemberState> party,
        Func<string, AtomRow?> lookupAtom,
        Func<string, ActorDerivedSnapshot> derivedFor,
        DelveUiPresentSink uiSink,
        string delveId,
        int nerveStackPerCurio,
        long atTick)
    {
        if (resolution is null) throw new ArgumentNullException(nameof(resolution));
        if (string.IsNullOrWhiteSpace(choice)) throw new ArgumentException("choice required", nameof(choice));
        if (party is null) throw new ArgumentNullException(nameof(party));
        if (uiSink is null) throw new ArgumentNullException(nameof(uiSink));

        if (string.Equals(choice, EventChoices.Leave, StringComparison.Ordinal))
            return new EventAnswerResult(choice, Applied: false, party, Array.Empty<StatDerivedGrant>(), uiSink.Banners);

        if (!string.Equals(choice, EventChoices.Interact, StringComparison.Ordinal)
            && !string.Equals(choice, EventChoices.Use, StringComparison.Ordinal))
            throw new ArgumentException($"'{choice}' is not one of the fixed verbs (use/interact/leave)", nameof(choice));

        if (resolution.Instance is null)
            throw new EventDeckRefusal(
                $"Answer('{choice}') has nothing to apply -- this resolution's own Kind ('{resolution.Kind}') " +
                "carries no Instance (an unknown room resolved to cache/merchant/fight, not an ordinary event); " +
                "the caller must never offer use/interact on it");

        var dispatch = EventOutcomeDispatch.Dispatch(
            resolution.Instance, party, lookupAtom, derivedFor, uiSink, delveId, nerveStackPerCurio, atTick);

        return new EventAnswerResult(choice, Applied: true, dispatch.Members, dispatch.StatDerivedGrants, uiSink.Banners);
    }

    /// <summary>Thin wrapper, D3.9's own already-named gap ("`EventDeck.DrawAmbush`'s own thin wrapper is
    /// not built, since `EventDeck.cs` itself is D3.3's own already-named gap") — closed here now that
    /// this file exists, forwarding verbatim to the real, already-shipped <see cref="AmbushDraw.Draw"/>.
    /// `RestResolver.Resolve`'s own call site into this remains unbuilt (a `delve-attrition` wiring task,
    /// not this one's).</summary>
    public static AmbushOutcome DrawAmbush(
        IReadOnlyList<EventRow> restArchetypePool, int row, int col, string? roomClimate,
        EventCatalog catalog, FactReader facts, Func<string, int> statusBit, long ambushMilli,
        long climateAffinityMatchMilli, long climateAffinityNoneMilli, long climateAffinityOffMilli,
        ulong seed)
        => AmbushDraw.Draw(
            restArchetypePool, row, col, roomClimate, catalog, facts, statusBit, ambushMilli,
            climateAffinityMatchMilli, climateAffinityNoneMilli, climateAffinityOffMilli, seed);

    /// <summary>Spec §5's own `encounter` consequence value — this module's closed vocabulary, matching
    /// <see cref="EventCatalog.Load"/>'s own `Consequences` set.</summary>
    public const string EncounterConsequence = "encounter";

    /// <summary>
    /// Spec §5, verbatim: "`encounter` → a `fight` archetype re-picked on
    /// `dungeon:event:{r}:{c}:encounter`". Delegates to <see cref="PaletteCellPick.Pick"/> — the ONE
    /// owner of the palette-cell filter-then-pick idiom this module shares with §4's own unknown-pity
    /// re-pick — on the ROOM's own climate when it has one, else the domain's (a climate-neutral room
    /// has no cell of its own; see <see cref="PaletteCellPick"/>'s own doc comment for why the climate is
    /// a parameter rather than a read off the domain).
    ///
    /// <para><b>The caller is whoever materialises the consequence</b>, exactly the split `consequence:
    /// loot` already has with `dungeon-loot`: this module owns the STREAM NAME (§3's table reserves
    /// `dungeon:event:{r}:{c}:encounter` for event-deck) and therefore the pick; `Encounter.Build` then
    /// `DelveBattle.Run` are the caller's. Returns `null` when no <paramref name="domain"/> was supplied
    /// — there is then no palette to re-pick from, and a fabricated archetype id would be worse than an
    /// honest absence (the identical posture <see cref="UnknownPity"/>'s own `ArchetypeId` takes for
    /// §4's re-pick). An empty cell throws `DelveGraphRollRejection`, "the roller's own refusal".</para>
    /// </summary>
    /// <param name="roomClimate">The room's own climate id as the caller already supplies it to
    /// <see cref="Resolve"/> — `null` for a climate-neutral room, in which case the domain's own climate
    /// keys the cell (a climate-neutral room has none of its own).</param>
    public static string? PickEncounterArchetype(DomainAnchor? domain, string? roomClimate, int row, int col, ulong seed)
    {
        if (domain is null) return null;

        var climate = roomClimate is not null && ElementRoster.TryParse(roomClimate, out var parsed)
            ? parsed
            : domain.Climate;
        return PaletteCellPick.Pick(
            domain, "fight", climate, row, col, seed, DelveStreams.Event(row, col) + ":encounter");
    }
}
