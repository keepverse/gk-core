using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.9 (spec-event-deck.md §9, "Refusals and preflight") — PARTIALLY BUILT: the rules
/// that are pure over an already-loaded <see cref="EventCatalog"/> alone, plus one that is pure over
/// the container store instead (see below). Spec's own full rule list names ten checks; five are
/// buildable here over the catalog, model-free, exactly matching the spec's own framing ("model-free,
/// run by the domain importer and the tests, refusing the domain with the row named") — four always run
/// inside <see cref="Run"/>, the fifth (<see cref="CheckSupplyOverrideCoverage"/>) opts in via an
/// optional parameter. Three more (archetype/pool coverage, recent-cells headroom, `>= 1
/// encounter-event per rest archetype`) are built as `domain-catalog`'s own `DomainEventPreflight`
/// bridge instead, since they need a domain's own room palette this catalog-wide class has no
/// parameter for.
///
/// <para>A tenth item — spec's own "no `nerve.*` id or non-event atom kind in any container" — is a
/// SINGLE spec bullet with two conjuncts that turned out to need two DIFFERENT scopes once actually
/// read against code (not assumed): see <see cref="CheckNoNerveTargetInAnyContainer"/>'s own doc
/// comment for the full reasoning. Both conjuncts are built as of 2026-09-22, each as a STANDALONE
/// method rather than wired into <see cref="Run"/> — neither is pure over an <see cref="EventCatalog"/>
/// alone, so folding either into `Run`'s catalog-shaped signature would both conflate two unrelated
/// corpora and change an already-shipped method's contract, the same reason the three domain-bridge
/// rules live in `domain-catalog`'s own `DomainEventPreflight` instead.</para>
/// </summary>
public static class EventDeckPreflight
{
    /// <summary>Spec §9, verbatim: "≥ 1 `good` and ≥ 1 `bad`-or-`mixed` per event." `nothing` neither
    /// satisfies nor disqualifies either side.</summary>
    public static IReadOnlyList<AtomRejection> CheckOutcomeMix(EventCatalog catalog)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        var fails = new List<AtomRejection>();
        foreach (var row in catalog.All)
        {
            var hasGood = row.Outcomes.Any(o => string.Equals(o.Ordinal, "good", StringComparison.Ordinal));
            var hasBadOrMixed = row.Outcomes.Any(o =>
                string.Equals(o.Ordinal, "bad", StringComparison.Ordinal) ||
                string.Equals(o.Ordinal, "mixed", StringComparison.Ordinal));

            if (!hasGood || !hasBadOrMixed)
                fails.Add(EventRules.Fail(EventRules.MissingRequiredOutcomeMix,
                    $"'{row.EventId}' needs >= 1 'good' and >= 1 'bad'-or-'mixed' outcome (has good={hasGood}, bad-or-mixed={hasBadOrMixed})"));
        }
        return fails;
    }

    /// <summary>
    /// Spec §9, verbatim: "`chainRef` acyclic, same kind." An unresolved `chainRef` (pointing at an id
    /// absent from the catalog) is a DIFFERENT rule — referential integrity, not named in this section
    /// of the spec — and is silently skipped here rather than invented.
    /// </summary>
    public static IReadOnlyList<AtomRejection> CheckChainRefs(EventCatalog catalog)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        var fails = new List<AtomRejection>();
        foreach (var row in catalog.All)
        {
            if (row.ChainRef is null) continue;

            var target = catalog.Resolve(row.ChainRef);
            if (target is not null && !string.Equals(target.Kind, row.Kind, StringComparison.Ordinal))
                fails.Add(EventRules.Fail(EventRules.ChainRefKindMismatch,
                    $"'{row.EventId}' (kind '{row.Kind}') chains to '{row.ChainRef}' (kind '{target.Kind}') -- chainRef must stay the same kind"));

            if (HasCycleFrom(catalog, row.EventId))
                fails.Add(EventRules.Fail(EventRules.ChainRefCycle, $"'{row.EventId}' is part of a chainRef cycle"));
        }
        return fails;
    }

    static bool HasCycleFrom(EventCatalog catalog, string startId)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = startId;
        while (current is not null)
        {
            if (!seen.Add(current)) return true;
            current = catalog.Resolve(current)?.ChainRef;
        }
        return false;
    }

    /// <summary>Spec §9, verbatim: "`RoomKindIs boss` refused (*"no event may gate the boss"*)" —
    /// scans every event's own compiled-once eligibility tree for a <see cref="LeafId.RoomKindIs"/>
    /// leaf whose <paramref name="bossRoomKindOrdinal"/> matches, wherever it sits under `And`/`Or`/
    /// `Not`. <paramref name="bossRoomKindOrdinal"/> is a plain, caller-resolved ordinal — this module
    /// never imports `RoomKindCatalog` (D3.7's own "no second owner of a domain vocabulary" posture).</summary>
    public static IReadOnlyList<AtomRejection> CheckNoRoomKindIsBoss(EventCatalog catalog, int bossRoomKindOrdinal)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        var fails = new List<AtomRejection>();
        foreach (var row in catalog.All)
            if (row.Eligibility is not null && ContainsRoomKindIsBoss(row.Eligibility, bossRoomKindOrdinal))
                fails.Add(EventRules.Fail(EventRules.RoomKindIsBossForbidden,
                    $"'{row.EventId}' gates eligibility on RoomKindIs(boss) -- no event may gate the boss"));
        return fails;
    }

    static bool ContainsRoomKindIsBoss(PredicateNode node, int bossOrdinal) => node switch
    {
        PredicateNode.And a => a.Children.Any(c => ContainsRoomKindIsBoss(c, bossOrdinal)),
        PredicateNode.Or o => o.Children.Any(c => ContainsRoomKindIsBoss(c, bossOrdinal)),
        PredicateNode.Not n => ContainsRoomKindIsBoss(n.Child, bossOrdinal),
        PredicateNode.Leaf l => l.Id == LeafId.RoomKindIs && l.Value == bossOrdinal,
        _ => false,
    };

    /// <summary>
    /// Spec §9, verbatim: "known status ids." `PredicateCompiler.ValidateLeaf` does NOT itself refuse
    /// an unknown one — an unresolvable `HasStatus` interns to bit -1 and evaluates permanently false
    /// (confirmed by reading `PredicateCompiler.cs`'s own `ValidateLeaf`), so this is a genuinely
    /// separate, event-deck-owned check over the SAME `statusBit` function the catalog itself compiled
    /// against, catching a typo that would otherwise ship as a silently-dead condition.
    /// </summary>
    public static IReadOnlyList<AtomRejection> CheckKnownStatusIds(EventCatalog catalog, Func<string, int> statusBit)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (statusBit is null) throw new ArgumentNullException(nameof(statusBit));

        var fails = new List<AtomRejection>();
        foreach (var row in catalog.All)
        {
            if (row.Eligibility is null) continue;
            foreach (var badId in UnknownStatusIds(row.Eligibility, statusBit).Distinct(StringComparer.Ordinal))
                fails.Add(EventRules.Fail(EventRules.UnknownStatusId,
                    $"'{row.EventId}' HasStatus references unknown status id '{badId}'"));
        }
        return fails;
    }

    static IEnumerable<string> UnknownStatusIds(PredicateNode node, Func<string, int> statusBit) => node switch
    {
        PredicateNode.And a => a.Children.SelectMany(c => UnknownStatusIds(c, statusBit)),
        PredicateNode.Or o => o.Children.SelectMany(c => UnknownStatusIds(c, statusBit)),
        PredicateNode.Not n => UnknownStatusIds(n.Child, statusBit),
        PredicateNode.Leaf { Id: LeafId.HasStatus, Text: { } text } when statusBit(text) < 0 => new[] { text },
        _ => Array.Empty<string>(),
    };

    /// <summary>D3.9 (spec §9, `OverrideTagUnsupplied`): "every `supplyOverride` tag is carried by
    /// &gt;= 1 supply." `tagsCarriedBySupplies` is the caller-supplied union
    /// (<see cref="SupplyOverrideTagSeedFile.LoadAllOverrideTags"/>) — this function reads no file
    /// itself, matching every other rule in this class. Catalog-wide, not domain-scoped: a supply is
    /// global inventory, not bound to one domain's own room palette.</summary>
    public static IReadOnlyList<AtomRejection> CheckSupplyOverrideCoverage(EventCatalog catalog, IReadOnlySet<string> tagsCarriedBySupplies)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (tagsCarriedBySupplies is null) throw new ArgumentNullException(nameof(tagsCarriedBySupplies));

        var fails = new List<AtomRejection>();
        foreach (var row in catalog.All)
        {
            if (row.SupplyOverride is null) continue;
            if (!tagsCarriedBySupplies.Contains(row.SupplyOverride))
                fails.Add(EventRules.Fail(EventRules.OverrideTagUnsupplied,
                    $"'{row.EventId}' supplyOverride '{row.SupplyOverride}' is not carried by any real supply"));
        }
        return fails;
    }

    /// <summary>Every buildable per-event rule, run together — never throws, one
    /// <see cref="AtomRejection"/> per violation found, matching <see cref="EventCatalog.Load"/>'s own
    /// "N bad rows, N rejections" shape. <paramref name="tagsCarriedBySupplies"/> defaults to `null`
    /// (skip the check, every existing call site keeps compiling unchanged) — pass
    /// <see cref="SupplyOverrideTagSeedFile.LoadAllOverrideTags"/>'s own result to include it.
    ///
    /// <para>Two of spec §9's items are deliberately NOT called from here, for two different reasons.
    /// Three (archetype/pool coverage, recent-cells headroom, `>= 1 encounter-event per rest
    /// archetype`) are built as `domain-catalog`'s own `DomainEventPreflight` bridge instead, since they
    /// need a domain's own room palette this catalog-wide function has no parameter for. The tenth item
    /// ("no `nerve.*` id or non-event atom kind in any container") has BOTH conjuncts built as standalone
    /// methods — <see cref="CheckNoNerveTargetInAnyContainer"/> (the container store) and
    /// <see cref="CheckEventOutcomeAtomKinds"/> (each event's own outcomes, resolved through the real atom
    /// catalog) — neither wired in here, for the same reason. See D3.9's own todo entry for the full,
    /// dated accounting.</para>
    /// </summary>
    public static IReadOnlyList<AtomRejection> Run(
        EventCatalog catalog, int bossRoomKindOrdinal, Func<string, int> statusBit,
        IReadOnlySet<string>? tagsCarriedBySupplies = null)
    {
        var fails = new List<AtomRejection>();
        fails.AddRange(CheckOutcomeMix(catalog));
        fails.AddRange(CheckChainRefs(catalog));
        fails.AddRange(CheckNoRoomKindIsBoss(catalog, bossRoomKindOrdinal));
        fails.AddRange(CheckKnownStatusIds(catalog, statusBit));
        if (tagsCarriedBySupplies is not null)
            fails.AddRange(CheckSupplyOverrideCoverage(catalog, tagsCarriedBySupplies));
        return fails;
    }

    /// <summary>Spec §5's own closed dispatch vocabulary — the five atom kinds an event outcome may
    /// carry. Reuses <see cref="EventOutcomeDispatch"/>'s own public constants (one owner of the
    /// vocabulary, never a second hand-typed list that could drift from the dispatcher's switch).</summary>
    public static readonly IReadOnlyList<string> EventAtomKinds = new[]
    {
        EventOutcomeDispatch.ResourceDeltaKind,
        EventOutcomeDispatch.StatusApplyKind,
        EventOutcomeDispatch.ShieldGrantKind,
        EventOutcomeDispatch.StatDerivedKind,
        EventOutcomeDispatch.UiPresentKind,
    };

    /// <summary>
    /// Spec §9, the SECOND conjunct of the tenth item, verbatim: "no ... non-event atom kind in any
    /// container" — scoped to the containers an event's own outcome ACTUALLY instantiates. That scope is
    /// the only sound one: a whole-corpus scan would refuse most of the item/trait/species-passive corpus
    /// on day one, because real item-affix content legitimately carries kinds outside this module's five
    /// (`gk-data/packs/fusion/data/seed/atoms/generated/family-expand.g-attack.json`'s 15 `stat.modify` atoms, drawn by a real
    /// item container's pool on every rung) — confirmed by reading the real content, not assumed. See
    /// <see cref="CheckNoNerveTargetInAnyContainer"/>'s own doc comment for the same scope reasoning
    /// applied to the sibling conjunct.
    ///
    /// <para><b>Built 2026-09-22 — the "no importer turns `outcomes[].effects[]` into a real,
    /// addressable `ContainerRow`" blocker this file's own earlier note recorded is now STALE.</b>
    /// <see cref="EventEffectContainerBuild.From"/> is exactly that importer and
    /// <see cref="EventDeck.Resolve"/> is its real caller: the container an event outcome instantiates is a
    /// pure function of the outcome's own <see cref="EventEffectRef"/> list plus the atom catalog, so this
    /// rule runs that SAME resolver offline (same <see cref="EventEffectContainerBuild.ContainerIdFor"/> id
    /// included) and can never drift from what play instantiates.</para>
    ///
    /// <para>An effect ref that does not resolve at all — an unknown `powerBand`, or a family with no atom
    /// at the resolved tier — is reported under <see cref="EventRules.EffectAtomUnresolved"/> rather than
    /// silently skipped: <see cref="EventDeck.Resolve"/> throws the identical <see cref="EventDeckRefusal"/>,
    /// so staying quiet would be the "blank room at play" failure spec §9 exists to prevent.</para>
    /// </summary>
    public static IReadOnlyList<AtomRejection> CheckEventOutcomeAtomKinds(
        EventCatalog catalog, Func<string, AtomRow?> lookupAtom)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));

        var fails = new List<AtomRejection>();
        foreach (var row in catalog.All)
        {
            foreach (var outcome in row.Outcomes)
            {
                if (outcome.Effects is null || outcome.Effects.Count == 0)
                {
                    // EventEffectContainerBuild.From refuses an empty list (its own contract: it builds a
                    // container FROM effects). At play EventDeck.Resolve reaches the same refusal, so this
                    // is a content-integrity rejection, reported here rather than left to throw there.
                    fails.Add(EventRules.Fail(EventRules.EffectAtomUnresolved,
                        $"'{row.EventId}' outcome '{outcome.Ordinal}' has no effects -- nothing to instantiate"));
                    continue;
                }

                var containerId = EventEffectContainerBuild.ContainerIdFor(row.EventId, outcome.Ordinal);
                ContainerRow container;
                try
                {
                    container = EventEffectContainerBuild.From(containerId, outcome.Effects, lookupAtom);
                }
                catch (EventDeckRefusal refusal)
                {
                    fails.Add(EventRules.Fail(EventRules.EffectAtomUnresolved,
                        $"'{row.EventId}' outcome '{outcome.Ordinal}' cannot be instantiated: {refusal.Message}"));
                    continue;
                }

                foreach (var atomRef in container.Atoms)
                {
                    var atom = lookupAtom(atomRef.AtomId);
                    if (atom is null) continue; // unreachable: From() already refused an unresolvable atom id
                    if (EventAtomKinds.Contains(atom.KindId, StringComparer.Ordinal)) continue;
                    fails.Add(EventRules.Fail(EventRules.NonEventAtomKind,
                        $"'{row.EventId}' outcome '{outcome.Ordinal}' resolves atom '{atom.AtomId}' of kind " +
                        $"'{atom.KindId}', which is not one of spec §5's five event atom kinds " +
                        $"({string.Join(", ", EventAtomKinds)})"));
                }
            }
        }
        return fails;
    }

    /// <summary>
    /// Spec §9, verbatim (one bullet, two conjuncts): "no `nerve.*` id or non-event atom kind in any
    /// container." <b>Only the first conjunct is built here</b> — the two are decided independently
    /// below, each by reading spec-container-schema.md / definitions.md / this module's own §5 dispatch
    /// table and real shipped content, not assumed.
    ///
    /// <para><b>The `nerve.*` conjunct — built, whole-corpus.</b> `nerve.*`
    /// (<c>NerveStatusIds.For</c>, `Delve/Attrition/NervePolicy.cs`) is an EXCLUSIVELY-DERIVED
    /// projection: <c>NervePolicy.Sync</c> is the only writer — "at most one `nerve.*` instance per
    /// creature, never a status field" applied by ordinary content (that class's own doc comment). A
    /// `status.apply` atom targeting it from ANY container anywhere — an item, a passive node, an event
    /// outcome, a creature unique — breaks the same sync-ownership invariant identically, so this conjunct
    /// is genuinely global, not event-deck-scoped, and there is no narrower mechanism to scope it to: an
    /// event outcome names only <c>(Family, PowerBand)</c> (<see cref="EventEffectRef"/>,
    /// `EventRow.cs:12`), never a container id — that record's own doc comment: "a def row loaded here
    /// has no concrete container behind it yet" — so there is no edge from an event to "the containers
    /// it uses" to walk instead of the whole store.</para>
    ///
    /// <para><b>The "non-event atom kind" conjunct — BUILT 2026-09-22, as
    /// <see cref="CheckEventOutcomeAtomKinds"/>.</b> Read literally over "any container", it would refuse
    /// a container for holding a kind outside this module's own five (spec §5: `resource.delta`,
    /// `status.apply`, `shield.grant`, `stat.derived`, `ui.present`). That scope is empirically wrong, not
    /// just theoretically risky:
    /// `gk-data/packs/fusion/data/seed/atoms/generated/family-expand.g-attack.json` — real, shipped item-affix content —
    /// carries 15 `stat.modify` atoms (a real, registered kind, `AtomKindRegistry.cs:494`, not one of
    /// the five), which a real item container's pool draws through `effect_affix_ref` on every rung
    /// (confirmed live via the same shape `RpgStore.ListActionContainers` in `FusionRpg.Data` already
    /// uses — Core cannot reference it directly, so this is prose, not a `cref`). A whole-corpus scan
    /// for "non-event kind" would refuse most of the item/trait/
    /// species-passive corpus. The sound scope — "containers an event's own outcome actually resolves
    /// to" — was recorded here as not existing yet; it does now:
    /// <see cref="EventEffectContainerBuild.From"/> is the outcome→`ContainerRow` resolver and
    /// <see cref="EventDeck.Resolve"/> is its real caller, so the conjunct is checked against exactly what
    /// play instantiates. See <see cref="CheckEventOutcomeAtomKinds"/>.</para>
    /// </summary>
    public static IReadOnlyList<AtomRejection> CheckNoNerveTargetInAnyContainer(
        IReadOnlyList<ContainerRow> containers, Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix)
    {
        if (containers is null) throw new ArgumentNullException(nameof(containers));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));
        if (lookupAffix is null) throw new ArgumentNullException(nameof(lookupAffix));

        var fails = new List<AtomRejection>();
        foreach (var container in containers)
        {
            foreach (var atomId in ResolvedAtomIds(container, lookupAffix))
            {
                var atom = lookupAtom(atomId);
                // A dangling ref is ContainerValidator's own rejection (E5: "every atom_id resolves,
                // else reject") -- a container that failed THAT check never reaches the store, so this
                // is unreachable on real content; skipped rather than invented, the same posture
                // CheckChainRefs already takes on an unresolved chainRef.
                if (atom is null) continue;
                if (!string.Equals(atom.KindId, "status.apply", StringComparison.Ordinal)) continue;

                if (TargetsNerve(atom.ParamsJson))
                    fails.Add(EventRules.Fail(EventRules.NerveTargetInContainer,
                        $"container '{container.ContainerId}' atom '{atom.AtomId}' (status.apply) targets a " +
                        "nerve.* id -- nerve is an exclusively-derived projection (NervePolicy.Sync), never a direct grant"));
            }
        }
        return fails;
    }

    /// <summary>Every atom id a container can resolve to: the fixed core directly, plus every CONCRETE
    /// ref (<see cref="AffixRefRow.AtomId"/> set) inside every pool row's own affix. A slot ref
    /// (<see cref="AffixRefRow.IsSlot"/>) is skipped: every shipped slot domain today is `element`
    /// (`RpgStore.Containers.cs`'s own `DomainMembers`), a variant-selection axis on a family like
    /// `atom.elemental-power` — never a mechanism that picks WHICH atom-kind-family a ref resolves to,
    /// so a `status.apply` atom cannot be reached through a slot. Named here rather than silently
    /// assumed.</summary>
    static IEnumerable<string> ResolvedAtomIds(ContainerRow container, Func<string, AffixRow?> lookupAffix)
    {
        foreach (var a in container.Atoms)
            yield return a.AtomId;

        foreach (var p in container.Pool)
        {
            var affix = lookupAffix(p.AffixId);
            if (affix is null) continue; // dangling pool ref -- ContainerValidator's own rejection, not this rule's
            foreach (var r in affix.Refs)
                if (r.AtomId is not null)
                    yield return r.AtomId;
        }
    }

    /// <summary>`status.apply`'s own <c>ParamSchema</c> (`AtomKindRegistry.cs:660`): the target status
    /// lives at `params.status`, a required string. Absence or a non-string is not this rule's concern
    /// — E1's own load-time schema validation already refuses that shape before it reaches
    /// storage.</summary>
    static bool TargetsNerve(string paramsJson)
    {
        using var doc = JsonDocument.Parse(paramsJson);
        return doc.RootElement.TryGetProperty("status", out var statusEl)
            && statusEl.ValueKind == JsonValueKind.String
            && (statusEl.GetString() ?? "").StartsWith("nerve.", StringComparison.Ordinal);
    }
}
