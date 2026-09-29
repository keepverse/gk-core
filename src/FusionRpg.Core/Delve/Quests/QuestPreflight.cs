using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;

namespace FusionRpg.Core.Delve.Quests;

/// <summary>Named preflight rules this file raises (spec §8's own list). Three of the spec's own
/// bulleted checks — "a template not in the registry," "a `targetRef` mismatching `targetKind`," "a
/// `countBand` on a count-less template that is not `none`," "a tree failing `TryCompile` [or] using
/// a refused leaf" — are ALREADY enforced by `QuestCatalog.Load` itself (D4.9), so are deliberately
/// not re-checked here; this file owns only the checks `Load` structurally cannot make (a raw-tree
/// leaf scan, a cross-field floor/ceil compare, and a pool-wide count).</summary>
public static class QuestPreflightRules
{
    public const string RoomKindIsBossForbidden = "quest.room-kind-is-boss-forbidden";
    public const string FloorAboveCeil = "quest.floor-above-ceil";
    public const string TooFewNonSinkAnchors = "quest.too-few-non-sink-anchors";

    /// <summary>Hardened 2026-09-07: a `rewardBand` window whose `floorRung`/`ceilRung` do not resolve
    /// against the supplied rarity ladder — found live via the real shipped `dungeon.v1.json` (its own
    /// `quests.rewardBand.*` values are difficulty-rung ids, not item-rarity ids). Replaces an
    /// uncaught <see cref="KeyNotFoundException"/> that previously escaped <see cref="CheckFloorNotAboveCeil"/>.</summary>
    public const string RewardBandRungUnresolvable = "quest.reward-band-rung-unresolvable";

    /// <summary>D4.13 (citation corrected 2026-09-07): spec §8's own satisfiability sweep — "every
    /// `(domain, layout, raidMode, rung)` over [seeds]: `Roll`, assert the §2 filter leaves ≥
    /// `offeredAtEntry`, else refuse the domain naming the layout and the first template that
    /// starved." <see cref="QuestPreflight.Run"/> is the first caller of this rule id.</summary>
    public const string SatisfiabilitySweepStarved = "quest.satisfiability-sweep-starved";
}

/// <summary>
/// D4.13 (spec-delve-quests.md §8) — the full preflight surface. `TreeUsesLeaf`/`CheckNoRoomKindIsBoss`/
/// `CheckFloorNotAboveCeil`/`CheckEnoughNonSinkAnchors` are the three row/pool-level checks built
/// 2026-09-06, each needing no `domain-catalog` type at all. <see cref="Run"/>, built 2026-09-07 once
/// `domain-catalog` (D4.15-D4.22) landed, is the spec's own full `Run(corpus, domains, layouts, tuning)`
/// signature: it composes those three checks with the ONE genuinely new piece, the satisfiability
/// sweep, into the single entry point `domain-catalog`'s importer and `dungeon audit` both call.
/// </summary>
public static class QuestPreflight
{
    /// <summary>A plain recursive walk over And/Or/Not/Leaf — no leaf-encoding knowledge of its own,
    /// so it never has to guess at `RoomKindCatalog`'s real ordinal scheme for "boss."</summary>
    public static bool TreeUsesLeaf(PredicateNode? node, Func<PredicateNode.Leaf, bool> matches)
    {
        if (matches is null) throw new ArgumentNullException(nameof(matches));
        return node switch
        {
            null => false,
            PredicateNode.Leaf leaf => matches(leaf),
            PredicateNode.Not not => TreeUsesLeaf(not.Child, matches),
            PredicateNode.And and => and.Children.Any(c => TreeUsesLeaf(c, matches)),
            PredicateNode.Or or => or.Children.Any(c => TreeUsesLeaf(c, matches)),
            _ => false,
        };
    }

    /// <summary>Spec §8, verbatim: "[a tree] naming `RoomKindIs boss`" refuses — "a boss gate by
    /// another door" (§3). <paramref name="isRoomKindBossLeaf"/> names which leaf means "boss" —
    /// a plain caller-supplied predicate, since `RoomKindIs`'s own compiled `Value` is a raw ordinal
    /// (`PredicateCompiler.cs`) this file has no business re-deriving.</summary>
    public static void CheckNoRoomKindIsBoss(string domainId, IReadOnlyList<QuestRow> pool, Func<PredicateNode.Leaf, bool> isRoomKindBossLeaf)
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));
        foreach (var q in pool)
            if (TreeUsesLeaf(q.Predicate, isRoomKindBossLeaf))
                throw new QuestRefusal(domainId, q.QuestId, QuestPreflightRules.RoomKindIsBossForbidden,
                    "a quest predicate must never gate on RoomKindIs boss -- that is a door gate by another name");
    }

    /// <summary>Spec §8, verbatim: "`floorRung > ceilRung`" refuses. Rows whose `RewardBand` is
    /// itself unknown are skipped — `QuestCatalog.Load` already refused those, this is not a second
    /// check for the same defect.
    ///
    /// <para><b>Hardened 2026-09-07</b> (found while wiring the real <see cref="Run"/> sweep against
    /// real content for the first time — this function had zero real callers before that, only
    /// hand-consistent test fixtures): the real shipped `gk-core/data/tuning/dungeon.v1.json`'s own
    /// `quests.rewardBand.*.floorRung`/`.ceilRung` values are DIFFICULTY-rung ids (`"very-easy"`,
    /// `"easy"`, ...) — reproduced via a live test run, not assumed — while spec §12's own Tunables
    /// table cites `item-rarity.v1.json:7-18` as the ladder these ids climb; the real item-rarity
    /// ladder's own ids (`chaff`, `sprout`, ...) do not include `"very-easy"` at all. Calling
    /// <see cref="RarityDraw.OrdinalOf"/> with a mismatched id previously let a raw
    /// <see cref="KeyNotFoundException"/> escape uncaught — worse than "no flag, no fallback" (spec
    /// §8's own rule for this whole file): an uncaught exception names neither domain, quest nor rule,
    /// and would crash a `domain-catalog` importer or `dungeon audit` run outright rather than refusing
    /// one domain cleanly. An unresolvable rung id on an otherwise well-formed window IS, semantically,
    /// exactly what this check exists to catch — so it is now caught and reported as a real
    /// <see cref="QuestRefusal"/> naming the bad id, never a crash. The CONTENT fix itself — which two
    /// real item-rarity rungs "modest"/"fair"/"rich" should actually span — is a balance/content
    /// decision this function does not make on its own.</para>
    /// </summary>
    public static void CheckFloorNotAboveCeil(
        string domainId, IReadOnlyList<QuestRow> pool, IReadOnlyDictionary<string, RewardWindow> rewardBandsByMember, IReadOnlyList<RarityRung> ladder)
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));
        if (rewardBandsByMember is null) throw new ArgumentNullException(nameof(rewardBandsByMember));
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));

        foreach (var q in pool)
        {
            if (!rewardBandsByMember.TryGetValue(q.RewardBand, out var window)) continue;
            int floorOrdinal, ceilOrdinal;
            try
            {
                floorOrdinal = RarityDraw.OrdinalOf(ladder, window.FloorRung);
                ceilOrdinal = RarityDraw.OrdinalOf(ladder, window.CeilRung);
            }
            catch (KeyNotFoundException ex)
            {
                throw new QuestRefusal(domainId, q.QuestId, QuestPreflightRules.RewardBandRungUnresolvable,
                    $"rewardBand '{q.RewardBand}' (floorRung '{window.FloorRung}', ceilRung '{window.CeilRung}') does not resolve against the supplied rarity ladder -- {ex.Message}");
            }
            if (floorOrdinal > ceilOrdinal)
                throw new QuestRefusal(domainId, q.QuestId, QuestPreflightRules.FloorAboveCeil,
                    $"rewardBand '{q.RewardBand}' has floorRung '{window.FloorRung}' (ordinal {floorOrdinal}) above ceilRung '{window.CeilRung}' (ordinal {ceilOrdinal})");
        }
    }

    /// <summary>Spec §8, verbatim: "fewer than `offeredAtEntry` non-sink anchors in a pool" refuses —
    /// D14's own floor requirement (§5): non-sink quests must be able to fill the offer on their own
    /// below `hard`, before any sink-avoidance quest can ever unlock.</summary>
    public static void CheckEnoughNonSinkAnchors(
        string domainId, IReadOnlyList<QuestRow> pool, IReadOnlyList<ObjectiveTemplateDef> objectiveTemplates, int offeredAtEntry)
    {
        if (pool is null) throw new ArgumentNullException(nameof(pool));
        if (objectiveTemplates is null) throw new ArgumentNullException(nameof(objectiveTemplates));

        var sinkAvoidanceByTemplate = objectiveTemplates.ToDictionary(t => t.ObjectiveTemplateId, t => t.SinkAvoidance, StringComparer.Ordinal);
        var nonSinkCount = pool.Count(q => !(sinkAvoidanceByTemplate.TryGetValue(q.TemplateId, out var s) && s));
        if (nonSinkCount < offeredAtEntry)
            throw new QuestRefusal(domainId, "(pool)", QuestPreflightRules.TooFewNonSinkAnchors,
                $"only {nonSinkCount} non-sink-avoidance anchor(s), need at least {offeredAtEntry}");
    }

    /// <summary>
    /// D4.13's own real remaining gap, closed 2026-09-07 (citation corrected the same day): every
    /// read-only, cross-domain data source <see cref="Run"/>'s sweep needs, resolved ONCE by the
    /// caller — matching <see cref="DomainPreflightInputs"/>'s own established "bundle of delegates
    /// plus known data, never a live read" shape (`Delve/Domains/DomainPreflight.cs`).
    ///
    /// <para><see cref="QuestPoolByDomainId"/> holds already-RESOLVED <see cref="QuestRow"/>s (a
    /// domain's own `questPool` ids already looked up against a real <see cref="QuestCatalog"/>), not
    /// raw ids — resolving ids to rows is the caller's own job
    /// (<see cref="Domains.DomainSeedFile.LoadQuestPools"/> + <c>QuestCatalog.Resolve</c>), matching how
    /// <see cref="DomainRow"/> itself never carries a resolved pool (D4.15's own deliberate "pools are
    /// storage-only" scope).</para>
    ///
    /// <para><see cref="ArchetypeEventPoolHasKind"/> is the FIRST of the two delegates
    /// <see cref="QuestOffer.Satisfiable"/> declares but nothing implemented anywhere in the tree —
    /// build it via <see cref="QuestArchetypeEventBridge.Build"/> (real, 2026-09-07): a rolled room's
    /// <see cref="Roll.DelveRoomFact.ArchetypeId"/> IS a real room id (<c>DelveGraphRoll</c> draws it
    /// straight off <c>domain.RoomPalette</c>'s own <c>RoomId</c>), the SAME key
    /// <see cref="Domains.DomainEventPreflight"/> already reads a room's own <c>eventPool</c> by; the
    /// `gather-curio-kind` template's own `targetRef` is an EVENT KIND (spec §1's own table: "`curio-kind`
    /// (an event `kind`)"), the same `.Kind` field a resolved <c>EventRow</c> already carries.</para>
    ///
    /// <para><see cref="Tables"/>/<see cref="BaseTypesFor"/> back the SECOND missing delegate,
    /// `lootBindingOffersRole` — built per-domain inside <see cref="Run"/> via
    /// <see cref="QuestLootBindingBridge.Build"/> (real, 2026-09-07): resolves whether ANY of a domain's
    /// own bound drop tables (spec §2 step 2: "the role in some `lootBinding` TABLE's base-type set")
    /// carries an enabled `Equipment`-kind entry naming that role AND backed by ≥ 1 real base type via
    /// `BaseTypesFor` (<see cref="Items.Drops.LootContentView.BaseTypesFor"/>, <c>LootPipeline.cs:76</c>)
    /// — an authored-but-content-less `(frame, role)` pair must not count as satisfiable.</para>
    /// </summary>
    public sealed record QuestPreflightCorpus(
        IReadOnlyDictionary<string, RoomPaletteEntry> RoomsById,
        IReadOnlyDictionary<string, IReadOnlyList<string>> RoomPaletteByDomainId,
        IReadOnlyDictionary<string, IReadOnlyList<QuestRow>> QuestPoolByDomainId,
        Func<string, string, bool> ArchetypeEventPoolHasKind,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> LootBindingByDomainId,
        IReadOnlyDictionary<string, DropTableRow> Tables,
        Func<string, string, IReadOnlyList<string>> BaseTypesFor,
        IReadOnlyList<RarityRung> Ladder,
        int BossRoomKindOrdinal);

    /// <summary>
    /// D4.13's own full orchestration function, spec §8, cited verbatim: `QuestPreflight.Run(corpus,
    /// domains, layouts, tuning)`, "model-free, run by the `domain-catalog` importer and `dungeon
    /// audit`." Composes the three already-shipped row/pool checks above with the ONE genuinely new
    /// piece — the satisfiability sweep — into the single entry point the spec's own Interface table
    /// names.
    ///
    /// <para><b>Shape decision, made explicitly rather than guessed:</b> takes the FULL domain list
    /// directly (never a per-domain <c>Func&lt;DomainRow,...&gt;</c> delegate-builder), matching the
    /// spec citation's own plural "domains" and mirroring <see cref="Encounter.EncounterPreflight.Run"/>'s
    /// already-shipped `Run(corpus, domains, tuning)` shape (which ALSO takes every domain at once) —
    /// not <see cref="DomainPreflight.Run"/>'s own row-adapter convention, which is a DIFFERENT module's
    /// convention for composing five OTHER modules' sibling functions together, not a rule this
    /// function's own module must follow. The row-8 wiring gap into `DomainPreflightInputs.CheckQuests`
    /// is closed SEPARATELY by <see cref="Domains.DomainQuestPreflight"/>, a THIN adapter matching
    /// <see cref="Domains.DomainGraphPreflight"/>'s own established "catch the underlying throw, convert
    /// to a `DomainRefusal`" bridge shape — the same two-layer split every other row already has (a real
    /// module-owned function, plus a `domain-catalog`-owned bridge over it).</para>
    ///
    /// <para><b>Throws, never collects</b> — <see cref="QuestRefusal"/>'s own doc comment states the
    /// spec's rule directly ("no flag, no fallback, no empty offer"), and this function is simply
    /// SEQUENCING calls to the three already-shipped checks above, every one of which already throws;
    /// collecting would mean wrapping each one in its own try/catch for no benefit this module has ever
    /// asked for. This deliberately does NOT mirror <see cref="Events.EventDeckPreflight.Run"/>'s own
    /// collect-every-<c>AtomRejection</c> shape — that is a DIFFERENT module, over a DIFFERENT refusal
    /// type (<see cref="AtomRejection"/>, which `Load`-shaped functions in general use for "validate N
    /// rows, report every failure"); `QuestRefusal` is THIS module's own throw-first type, used
    /// identically by every sibling check above. First refusal wins, matching
    /// <see cref="Domains.DomainGraphPreflight.Build"/>'s own identical reasoning for its row-4 sweep
    /// ("the sweep stops at the first sample that throws rather than collecting every one... avoiding an
    /// O(raidModes × sampleSeeds) refusal flood for one bad domain") — extended here across the OUTER
    /// domain loop too, since a `dungeon audit` run that already found one real defect gains nothing
    /// from paying for the rest of the sweep.</para>
    ///
    /// <para><b>Sample width reuses <see cref="DungeonTuning.PreflightSampleSeeds"/> (shipped value 32),
    /// never the spec prose's own literal "256."</b> The spec's own header states "every number is a
    /// starting shape, never a balance decision," and §12's own Tunables table lists NO
    /// `quests.*`-scoped sample-seed key at all (only `offeredAtEntry`, `countBand.*Milli`,
    /// `rewardBand.*`, and the new `autopilotCompletionBand.*`) — meaning "256" was never meant to
    /// become its own second tunable. `preflight.sampleSeeds` already exists, is already documented
    /// domain-generally ("validator depth, never a balance lever," not scoped to graph-roll alone), and
    /// is already reused for this EXACT kind of import-time structural sweep by
    /// <see cref="Domains.DomainGraphPreflight.Build"/> (row 4, the closest sibling to this sweep).
    /// Introducing a second, hardcoded `256` here — disconnected from the one tunable this repo already
    /// uses for "how deep an import-time preflight validator samples" — is exactly the kind of private
    /// constant `docs/architecture/tunables-ssot.md` and this program's own "one power ladder, no
    /// private curve" discipline exist to prevent. The 256-seed PROPERTY TEST the spec's own Testing
    /// strategy names separately is free to keep its own literal 256 — a test asserting a property holds
    /// over a wide, arbitrary sample is a different concern from a production validator's sample depth,
    /// and `audit-magic-numbers.py` does not scan test files.</para>
    ///
    /// <para><b>Reuses <see cref="QuestOffer.Draw"/>, not a second copy of the D14 filter.</b> Spec §8
    /// says "assert the §2 filter leaves ≥ `offeredAtEntry`" — §2's own title is "Offering at entry and
    /// satisfiability" and names TWO filters inside it (step 2's satisfiability filter, step 3's D14
    /// filter) before step 4's draw; "the §2 filter," read as the whole of what §2 does before an offer
    /// is fixed, is genuinely rung-dependent (D14 excludes sink-avoidance anchors below `hard` — the
    /// reason this sweep varies over `rung` at all, since neither `Satisfiable` nor `Need`/`countBandMilli`
    /// read rung). Calling the real, already-tested <see cref="QuestOffer.Draw"/> (which internally
    /// re-applies both filters in the real sequential order) proves the REAL runtime entry-offer
    /// behaviour, not a structural substep, and avoids a second, drifting copy of D14's own sequential
    /// "non-sink draws first, sink unlocks once a risk quest lands" logic — the same "don't re-check a
    /// defect a sibling function already owns" discipline this file's own doc comment already applies to
    /// `QuestCatalog.Load`. This does NOT duplicate <see cref="CheckEnoughNonSinkAnchors"/>: that check
    /// is a STATIC, graph-independent floor on the POOL alone (are there structurally enough non-sink
    /// templates at all); this sweep is a DYNAMIC, per-rolled-graph, per-rung proof (are enough of them
    /// ALSO satisfiable and eligible on a real graph) — the same "static necessary condition, dynamic
    /// sufficient proof" relationship <see cref="Domains.DomainGraphPreflight"/>'s own sweep has to
    /// <see cref="Roll.DelveGraphRoll"/>'s own structural validators.</para>
    ///
    /// <para><b>A `DelveGraphRollRejection` at one (raidMode, seed) sample is silently skipped, never
    /// this function's own refusal</b> — an illegal graph is row 4's job
    /// (<see cref="Domains.DomainGraphPreflight"/>), not a quest defect; reporting it here would
    /// mis-attribute a `domain.graph:*`-shaped finding as a `quest.*` one. If EVERY sample for a domain
    /// is graph-illegal, this sweep correctly finds nothing further to add — the domain is already
    /// refused elsewhere.</para>
    /// </summary>
    public static void Run(QuestPreflightCorpus corpus, IReadOnlyList<DomainRow> domains, LayoutTemplateCatalog layouts, DungeonTuning tuning)
    {
        if (corpus is null) throw new ArgumentNullException(nameof(corpus));
        if (domains is null) throw new ArgumentNullException(nameof(domains));
        if (layouts is null) throw new ArgumentNullException(nameof(layouts));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var objectiveTemplates = ObjectiveTemplateCatalog.All;
        var rewardBandsByMember = tuning.QuestsRewardBand;
        var rungs = DifficultyRungCatalog.All;
        var hardOrdinal = DifficultyRungCatalog.Get("hard").Ordinal;

        bool IsRoomKindBossLeaf(PredicateNode.Leaf l) => l.Id == LeafId.RoomKindIs && l.Value == corpus.BossRoomKindOrdinal;
        // The real shipped countBand vocabulary is `lone·few·several·many` (D4.9's own named drift
        // from the spec's stale "few/some/most/all" prose) — "several"/"many" as the "most/all"
        // equivalent is `QuestOfferTests.cs`'s own already-established reading, reused verbatim rather
        // than re-guessed here.
        bool IsHighCountBand(string band) => band is "several" or "many";

        foreach (var domain in domains.OrderBy(d => d.DomainId, StringComparer.Ordinal))
        {
            if (!corpus.QuestPoolByDomainId.TryGetValue(domain.DomainId, out var pool)) continue; // no pool data supplied for this domain -- a caller-side gap, not this function's job to invent

            // The three already-shipped, already-tested row/pool checks -- an empty pool is a REAL
            // pool (never skipped ahead of these), so CheckEnoughNonSinkAnchors still refuses it by
            // name (0 non-sink anchors < offeredAtEntry) exactly like any other under-stocked pool.
            CheckNoRoomKindIsBoss(domain.DomainId, pool, IsRoomKindBossLeaf);
            CheckFloorNotAboveCeil(domain.DomainId, pool, rewardBandsByMember, corpus.Ladder);
            CheckEnoughNonSinkAnchors(domain.DomainId, pool, objectiveTemplates, tuning.QuestsOfferedAtEntry);

            if (!corpus.RoomPaletteByDomainId.TryGetValue(domain.DomainId, out var roomPalette)) continue; // row 3's own job
            var layout = layouts.Resolve(domain.LayoutTemplateId);
            if (layout is null) continue; // row 2's own job (unknown layoutTemplateId)

            DomainAnchor anchor;
            try { anchor = DomainAnchorBuilder.From(domain, corpus.RoomsById, roomPalette); }
            catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException) { continue; } // row 1/3's own job

            var lootBindingOffersRole = corpus.LootBindingByDomainId.TryGetValue(domain.DomainId, out var binding)
                ? QuestLootBindingBridge.Build(binding, corpus.Tables, corpus.BaseTypesFor)
                : new Func<string, bool>(_ => false); // no lootBinding at all for this domain -- correctly offers nothing, never a silent pass

            foreach (var raidMode in layout.RaidModes ?? Array.Empty<string>())
            {
                for (var i = 0; i < tuning.PreflightSampleSeeds; i++)
                {
                    var streamName = $"quest-preflight:{domain.DomainId}:{raidMode}:{i}"; // this sweep's OWN stream name -- never row 4's `domain-preflight:...`, so the two concerns' draws never correlate
                    var seed = SeededRng.DeriveStream(0, streamName).NextULong();

                    Roll.DelveGraph graph;
                    try { graph = DelveGraphRoll.Roll(anchor, layout, seed, raidMode, tuning); }
                    catch (DelveGraphRollRejection) { continue; } // row 4's own job -- an illegal graph, not a quest defect

                    var satisfiable = QuestOffer.Satisfiable(pool, graph.Facts, corpus.ArchetypeEventPoolHasKind, lootBindingOffersRole, tuning.QuestsCountBandMilli);

                    foreach (var rung in rungs)
                    {
                        var offered = QuestOffer.Draw(satisfiable, objectiveTemplates, unchecked((long)seed), tuning.QuestsOfferedAtEntry, rung.Ordinal, hardOrdinal, IsHighCountBand);
                        if (offered.Count >= tuning.QuestsOfferedAtEntry) continue;

                        var starved = pool.FirstOrDefault(q => !satisfiable.Contains(q));
                        var detail = starved is not null
                            ? $"layout '{layout.LayoutId}' raidMode '{raidMode}' rung '{rung.RungId}' seed#{i}: only {offered.Count}/{tuning.QuestsOfferedAtEntry} offered -- '{starved.QuestId}' (template '{starved.TemplateId}') is the first pool anchor unsatisfiable on this rolled graph"
                            : $"layout '{layout.LayoutId}' raidMode '{raidMode}' rung '{rung.RungId}' seed#{i}: only {offered.Count}/{tuning.QuestsOfferedAtEntry} offered -- every pool anchor is structurally satisfiable, but too few are eligible at this rung (D14 sink-avoidance gate)";
                        throw new QuestRefusal(domain.DomainId, starved?.QuestId ?? "(pool)", QuestPreflightRules.SatisfiabilitySweepStarved, detail);
                    }
                }
            }
        }
    }
}
