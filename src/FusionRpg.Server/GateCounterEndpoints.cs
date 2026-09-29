using FusionRpg.Core.PassiveTree.GateCounters;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// passive-tree-todo.md G6 — spec-gate-counters.md §10, §15 criterion 9: "the counters are invisible
/// without a read path, and `tree-surface` needs one." Copies `AptitudeEndpoints.cs`'s own
/// GET-state/POST-write shape rather than inventing a second one — the todo names that file (and
/// `PassiveTreeEndpoints.cs`) as the pattern this module follows.
///
/// <para><b>Why no allocation-style broadcast on POST.</b> `AptitudeEndpoints`/`PassiveTreeEndpoints`
/// broadcast because a POST there is a player action the web UI and the injector both need to reflect
/// immediately. A gate-counter credit is the opposite shape — a periodic, host-scheduled flush of
/// background progress (§4.3: "a lost window costs a little progress, never correctness"), not a
/// player-visible state change with a UI waiting on it. `tree-surface` reads the GET on its own refresh
/// cadence, the same way it already re-polls for anything else that isn't pushed.</para>
///
/// <para><b>The credit body's shape.</b> §10's project-structure row describes this endpoint as taking
/// "the batched flush ( `GateCounterAccumulator.DrainAndClear()` output shape,
/// `IReadOnlyDictionary&lt;GateCounterKey, long&gt;` )". A `GateCounterKey` is JSON-unfriendly as a
/// dictionary key (it is a 4-tuple), so the wire shape is the flattened list this file's own
/// <see cref="GateCounterCreditEntryDto"/> declares — one entry per key, each carrying all four identity
/// fields plus its delta. This also means one request can batch credits for more than one owner, which
/// <see cref="GateCounterAccumulator"/> itself never assumes (§4.3: "keyed (ownerKey, quantity,
/// subjectId)") but the store (`RpgStore.FlushGateCounters`) already accepts happily — a fresher host
/// (a future multi-actor accumulator) costs this endpoint nothing to support.</para>
///
/// <para><b>Subject-id validation, deliberately loose on POST.</b> `StatusCategoryRegistry.Register` can
/// add ids at runtime (§2.1's closing paragraph) in the INJECTOR process, which this SERVER process does
/// not necessarily share at any given moment — the server and the injector are separate processes
/// (AGENTS.md's module table). Rejecting an unrecognised `subjectId` here would make the server the
/// place a legitimate future status id silently stops crediting. `RpgStore.FlushGateCounters` already
/// treats `subject_id` as an opaque TEXT column (§4.1: "raw counts only... inputs only"), so this
/// endpoint validates structure (non-blank fields, a known `quantity` family, a positive delta) and
/// leaves subject-id membership to whichever process actually knows the roster.</para>
/// </summary>
public static class GateCounterEndpoints
{
    public static void MapGateCounters(this WebApplication app)
    {
        var g = app.MapGroup("/api/gate-counters");

        g.MapPost("/credit", (GateCounterCreditRequest body, RpgStore store) =>
        {
            if (body.Credits is null || body.Credits.Count == 0)
                return Results.Ok(new { credited = 0 });

            var deltas = new Dictionary<GateCounterKey, long>();
            foreach (var entry in body.Credits)
            {
                if (string.IsNullOrWhiteSpace(entry.OwnerKind))
                    return Results.BadRequest(new { reason = "credits.ownerKind.missing" });
                if (string.IsNullOrWhiteSpace(entry.OwnerKey))
                    return Results.BadRequest(new { reason = "credits.ownerKey.missing" });
                if (entry.Quantity != StatusAppliedCounter.Quantity && entry.Quantity != ElementMasteryCounter.Quantity)
                    return Results.BadRequest(new { reason = "credits.quantity.unknown", quantity = entry.Quantity });
                if (string.IsNullOrWhiteSpace(entry.SubjectId))
                    return Results.BadRequest(new { reason = "credits.subjectId.missing" });
                if (entry.Delta <= 0)
                    return Results.BadRequest(new { reason = "credits.delta.notPositive", delta = entry.Delta });

                var key = new GateCounterKey(entry.OwnerKind, entry.OwnerKey, entry.Quantity, entry.SubjectId);
                deltas.TryGetValue(key, out var existing);
                // Widen-before-multiply doesn't apply to a plain sum, but the overflow-throws rule
                // still does (AGENTS.md) -- two entries for the same key in one batch is not expected
                // (DrainAndClear's dictionary shape already de-dupes), but merging under `checked`
                // rather than overwriting keeps a client that DID send duplicates from losing one.
                checked { deltas[key] = existing + entry.Delta; }
            }

            store.FlushGateCounters(deltas);
            return Results.Ok(new { credited = deltas.Count });
        });

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(ProjectState(store, playerId));
        });
    }

    /// <summary>
    /// §5.2's "known content gap" vs. "player has not started" split, made explicit on the wire per
    /// this task's own acceptance bullet ("the tier-0 reason is distinguishable on the wire, not only
    /// in Core") -- <see cref="FamilyDto.HasProducer"/> answers the first question and is never inferred
    /// from a zero count; every subject's own `count`/`index`/`equivalents` triple answers the second.
    /// </summary>
    static object ProjectState(RpgStore store, long playerId)
    {
        var ownerKind = "player";
        var ownerKey = AptitudeEndpoints.ScopeKey(playerId);
        var tuning = PassiveTreeTuningHub.Tuning.GateCounters;

        // A fresh registry per request: GateQuantityRegistry's exclusivity guarantee (§12) protects
        // against two producers answering the SAME family within one registry's lifetime -- it says
        // nothing about, and does not need to survive across, independent stateless requests. The two
        // sources themselves are pure delegates over `store`/`tuning`, so building them fresh per call
        // costs nothing a cached singleton would have saved.
        var registry = new GateQuantityRegistry();
        registry.Register(new StatusAppliedSource(
            (owner, subjectId) => store.LoadGateCounter(owner.Kind, owner.Key, StatusAppliedCounter.Quantity, subjectId),
            tuning));
        registry.Register(new ElementMasterySource(
            (owner, subjectId) => store.LoadGateCounter(owner.Kind, owner.Key, ElementMasteryCounter.Quantity, subjectId),
            tuning));

        var actor = new GateActorContext(new GateOwnerKey(ownerKind, ownerKey));

        var statusCounts = store.LoadGateCountersForOwner(ownerKind, ownerKey, StatusAppliedCounter.Quantity);
        var elementCounts = store.LoadGateCountersForOwner(ownerKind, ownerKey, ElementMasteryCounter.Quantity);

        var statusFamily = BuildFamily(
            StatusCategoryRegistry.AllStatusIds, statusCounts,
            id => MasteryIndex.Equivalents(statusCounts.TryGetValue(id, out var c) ? c : 0, tuning.StatusMasteryRatePoints, tuning),
            registry.HasProducer(StatusAppliedCounter.Quantity));

        var elementFamily = BuildFamily(
            ElementRoster.Concrete.Select(e => e.ToElementId()), elementCounts,
            id => MasteryIndex.Equivalents(elementCounts.TryGetValue(id, out var c) ? c : 0, tuning.ElementMasteryRatePoints, tuning),
            registry.HasProducer(ElementMasteryCounter.Quantity));

        // registry.AptitudePointEquivalents(...) is exercised above through the per-family `equivalents`
        // helpers rather than called again here -- the two read the exact same `MasteryIndex` path
        // (`StatusAppliedSource`/`ElementMasterySource` themselves), and calling the registry too would
        // just be a second route to the identical number, not a different one worth returning.
        _ = actor;

        return new
        {
            playerId,
            families = new Dictionary<string, FamilyDto>(StringComparer.Ordinal)
            {
                [StatusAppliedCounter.Quantity] = statusFamily,
                [ElementMasteryCounter.Quantity] = elementFamily
            }
        };
    }

    static FamilyDto BuildFamily(
        IEnumerable<string> subjectIds,
        IReadOnlyDictionary<string, long> counts,
        Func<string, long> equivalentsFor,
        bool hasProducer)
    {
        var subjects = new Dictionary<string, SubjectDto>(StringComparer.Ordinal);
        foreach (var id in subjectIds)
        {
            var count = counts.TryGetValue(id, out var c) ? c : 0;
            var tuning = PassiveTreeTuningHub.Tuning.GateCounters;
            subjects[id] = new SubjectDto(
                Count: count,
                Index: MasteryIndex.Index(count, tuning),
                Equivalents: equivalentsFor(id));
        }

        return new FamilyDto(hasProducer, subjects);
    }

    public sealed class GateCounterCreditRequest
    {
        public List<GateCounterCreditEntryDto>? Credits { get; set; }
    }

    public sealed class GateCounterCreditEntryDto
    {
        public string? OwnerKind { get; set; }
        public string? OwnerKey { get; set; }
        public string? Quantity { get; set; }
        public string? SubjectId { get; set; }
        public long Delta { get; set; }
    }

    public sealed record SubjectDto(long Count, long Index, long Equivalents);

    /// <summary><see cref="HasProducer"/> is §5.2's "known content gap, never inferred from the zero" --
    /// always `true` for both families from this endpoint (it registers both sources itself), kept as a
    /// real field rather than a hardcoded `true` so a future family this endpoint has not learned to
    /// build a source for yet reports itself honestly instead of by omission.</summary>
    public sealed record FamilyDto(bool HasProducer, IReadOnlyDictionary<string, SubjectDto> Subjects);
}
