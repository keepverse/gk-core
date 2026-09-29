using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Events;

/// <summary>Named content rules this module raises, under its own registered namespace — the same
/// "one code with a namespaced payload" shape `ConsumableRules` already establishes
/// (`AtomRejectionReason.ContentRuleViolated`), never a private reason enum.</summary>
public static partial class EventRules
{
    public const string Namespace = "event";

    public const string DuplicateId = "event.duplicate-id";
    public const string BadKind = "event.bad-kind";
    public const string BadRepeatScope = "event.bad-repeat-scope";
    public const string BadOutcomeCount = "event.bad-outcome-count";
    public const string BadOrdinal = "event.bad-ordinal";
    public const string NothingOnlyOnStory = "event.nothing-only-on-story";
    public const string BadDropBand = "event.bad-drop-band";
    public const string BadConsequence = "event.bad-consequence";
    public const string BadSupplyOverride = "event.bad-supply-override";
    public const string ChainRefRequiredForStory = "event.chain-ref-required-for-story";

    // D3.9 (spec-event-deck.md §9, "Refusals and preflight") -- domain-level rules, raised by
    // EventDeckPreflight rather than EventCatalog.Load itself, same namespace, same idiom.
    public const string MissingRequiredOutcomeMix = "event.missing-required-outcome-mix";
    public const string ChainRefCycle = "event.chain-ref-cycle";
    public const string ChainRefKindMismatch = "event.chain-ref-kind-mismatch";
    public const string RoomKindIsBossForbidden = "event.room-kind-is-boss-forbidden";
    public const string UnknownStatusId = "event.unknown-status-id";
    public const string OverrideTagUnsupplied = "event.override-tag-unsupplied";

    // D3.9 continued (spec-event-deck.md §9): the `nerve.*` conjunct of "no `nerve.*` id or non-event
    // atom kind in any container" -- see EventDeckPreflight.CheckNoNerveTargetInAnyContainer's own doc
    // comment for why only this conjunct is buildable today; the "non-event atom kind" conjunct is a
    // separate, still-open scope question, not this rule id.
    public const string NerveTargetInContainer = "event.nerve-target-in-container";

    static EventRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Forces the static constructor above to have run — same empty-body idiom
    /// `ConsumableRules.EnsureRegistered` already uses, since a static constructor only runs on
    /// first member access and `Fail` below is that first access for a caller who never touched
    /// this type before.</summary>
    public static void EnsureRegistered() { }

    public static AtomRejection Fail(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>The load result — a catalog plus every rejection, never a thrown exception (`ConsumableCatalogLoad`'s
/// own shape): N bad rows report N rejections in one pass, and the catalog holds every GOOD row regardless.</summary>
public readonly record struct EventCatalogLoad(EventCatalog Catalog, IReadOnlyList<AtomRejection> Rejections);

/// <summary>
/// `event-deck` D3.1 (spec-event-deck.md §2, §6). Rows arrive already parsed into
/// <see cref="EventRow"/> (the seed-import wiring — `SeedScanner`/`AtomSeedFile`/`RpgStore.ImportContent`,
/// following the `power-coefficient` precedent of one new <c>SeedEntryKind</c> — is a separate,
/// not-yet-built task; this catalog is provably correct and testable without it, the same posture
/// <see cref="Items.Consumables.ConsumableCatalog.Load"/> already ships in with zero production callers).
/// </summary>
public sealed class EventCatalog
{
    readonly IReadOnlyDictionary<string, EventRow> _byId;
    readonly IReadOnlyDictionary<string, ICompiledPredicate> _eligibility;

    EventCatalog(IReadOnlyDictionary<string, EventRow> byId, IReadOnlyDictionary<string, ICompiledPredicate> eligibility)
    {
        _byId = byId;
        _eligibility = eligibility;
    }

    public int Count => _byId.Count;

    public IReadOnlyList<EventRow> All => _byId.Values.OrderBy(e => e.EventId, StringComparer.Ordinal).ToList();

    public EventRow? Resolve(string eventId) => _byId.TryGetValue(eventId, out var row) ? row : null;

    /// <summary><see cref="PredicateCompiler.Always"/> for an event whose own tree was `null` (seed
    /// contract's `none` = always eligible) — never a lookup failure, since every row that reached the
    /// catalog compiled successfully by construction.</summary>
    public ICompiledPredicate EligibilityFor(string eventId) =>
        _eligibility.TryGetValue(eventId, out var compiled) ? compiled : PredicateCompiler.Always;

    /// <summary>The five real, shipped `dropBand` names (`gk-data/packs/fusion/data/seed/items/_registry/bands.v1.json`'s own
    /// `dropBand.enum`) — taken as a parameter, never hardcoded here, so a registry change cannot
    /// silently drift from what this loader accepts.</summary>
    public static EventCatalogLoad Load(
        IReadOnlyList<EventRow> rows,
        IReadOnlyList<string> eventKinds,
        IReadOnlyList<string> repeatScopes,
        IReadOnlyList<string> outcomeOrdinals,
        IReadOnlyList<string> dropBands,
        IReadOnlyList<string> overrideTags,
        Func<string, int> statusBit,
        Func<string, int>? elementId = null,
        Func<string, int>? stockBit = null)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        if (eventKinds is null) throw new ArgumentNullException(nameof(eventKinds));
        if (repeatScopes is null) throw new ArgumentNullException(nameof(repeatScopes));
        if (outcomeOrdinals is null) throw new ArgumentNullException(nameof(outcomeOrdinals));
        if (dropBands is null) throw new ArgumentNullException(nameof(dropBands));
        if (overrideTags is null) throw new ArgumentNullException(nameof(overrideTags));
        if (statusBit is null) throw new ArgumentNullException(nameof(statusBit));

        var fails = new List<AtomRejection>();
        var byId = new Dictionary<string, EventRow>(StringComparer.Ordinal);
        var eligibility = new Dictionary<string, ICompiledPredicate>(StringComparer.Ordinal);

        foreach (var row in rows.OrderBy(r => r.EventId, StringComparer.Ordinal))
        {
            if (byId.ContainsKey(row.EventId))
            {
                fails.Add(EventRules.Fail(EventRules.DuplicateId, $"'{row.EventId}' is defined twice"));
                continue;
            }

            if (!eventKinds.Contains(row.Kind, StringComparer.Ordinal))
            {
                fails.Add(EventRules.Fail(EventRules.BadKind, $"{row.EventId}: '{row.Kind}' is not a known event kind"));
                continue;
            }

            if (!repeatScopes.Contains(row.RepeatScope, StringComparer.Ordinal))
            {
                fails.Add(EventRules.Fail(EventRules.BadRepeatScope, $"{row.EventId}: '{row.RepeatScope}' is not a known repeat scope"));
                continue;
            }

            if (row.Outcomes is null || row.Outcomes.Count < 2 || row.Outcomes.Count > 4)
            {
                fails.Add(EventRules.Fail(EventRules.BadOutcomeCount,
                    $"{row.EventId}: {row.Outcomes?.Count ?? 0} outcomes, must be 2-4"));
                continue;
            }

            var isStory = string.Equals(row.Kind, "story", StringComparison.Ordinal);
            var outcomeFailed = false;
            foreach (var outcome in row.Outcomes)
            {
                if (!outcomeOrdinals.Contains(outcome.Ordinal, StringComparer.Ordinal))
                {
                    fails.Add(EventRules.Fail(EventRules.BadOrdinal, $"{row.EventId}: '{outcome.Ordinal}' is not a known outcome ordinal"));
                    outcomeFailed = true;
                    break;
                }

                if (string.Equals(outcome.Ordinal, "nothing", StringComparison.Ordinal) && !isStory)
                {
                    fails.Add(EventRules.Fail(EventRules.NothingOnlyOnStory,
                        $"{row.EventId}: outcome ordinal 'nothing' is legal only on kind 'story'"));
                    outcomeFailed = true;
                    break;
                }

                if (!dropBands.Contains(outcome.DropBand, StringComparer.Ordinal))
                {
                    fails.Add(EventRules.Fail(EventRules.BadDropBand, $"{row.EventId}: '{outcome.DropBand}' is not a known drop band"));
                    outcomeFailed = true;
                    break;
                }

                if (!Consequences.Contains(outcome.Consequence, StringComparer.Ordinal))
                {
                    fails.Add(EventRules.Fail(EventRules.BadConsequence, $"{row.EventId}: '{outcome.Consequence}' is not a known consequence"));
                    outcomeFailed = true;
                    break;
                }
            }
            if (outcomeFailed) continue;

            if (row.SupplyOverride is not null && !overrideTags.Contains(row.SupplyOverride, StringComparer.Ordinal))
            {
                fails.Add(EventRules.Fail(EventRules.BadSupplyOverride, $"{row.EventId}: '{row.SupplyOverride}' is not a known override tag"));
                continue;
            }

            if (isStory && string.IsNullOrWhiteSpace(row.ChainRef))
            {
                fails.Add(EventRules.Fail(EventRules.ChainRefRequiredForStory, $"{row.EventId}: kind 'story' requires a chainRef"));
                continue;
            }

            var compileRejection = PredicateCompiler.TryCompile(row.Eligibility, statusBit, out var compiled, elementId, stockBit);
            if (!compileRejection.IsOk)
            {
                fails.Add(AtomRejection.Fail(compileRejection.Reason, $"{row.EventId}: {compileRejection.Detail}"));
                continue;
            }

            byId[row.EventId] = row;
            eligibility[row.EventId] = compiled;
        }

        return new EventCatalogLoad(new EventCatalog(byId, eligibility), fails);
    }

    /// <summary>Event-deck's OWN vocabulary (spec-event-deck.md §5) — added by this module, not read
    /// from any registry, so it is the one closed set legitimately declared here rather than taken as
    /// a parameter.</summary>
    static readonly string[] Consequences = { "none", "loot", "encounter", "scout" };
}
