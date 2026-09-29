using System.Text.Json;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Events;

/// <summary>Which members of the standing party an atom touches (spec §5's own "Targets" paragraph).
/// `Party` (every standing member) is the default; `Downed` members are never a target here — "a downed
/// member is touched only by a revive-class supply" (spec, verbatim).</summary>
public enum EventEffectTarget { Party, One }

/// <summary>
/// One `stat.derived` (delve-long buff) grant this dispatch decided, NOT applied — spec §5's own row 4
/// names the write owner as `RpgStore.Delve.CloseDelve` (a Data-layer method, via the already-shipped
/// `ReconcileUniqueEquipmentAtomBindingsUnlocked` shape), and Core never writes SQL
/// (`guard-dal.ps1`). This is the SAME "Core decides, Data writes" split D2.21/22's own
/// `ExtractionSettlement.Decide` → `RpgStore.Delve.SettleExtractionUnlocked` already established: this
/// record is what a FUTURE `CloseDelve`-adjacent caller reads to `ProduceAndBind` the outcome's own
/// container against <see cref="ContainerId"/>, sourced <see cref="Source"/> (`"delve:{delveId}"`,
/// withdrawn at `CloseDelve` by source — the exact reconciler shape D2.23 already built for equipment).
/// </summary>
public sealed record StatDerivedGrant(string MemberInstanceId, string ContainerId, string Source);

/// <summary>The full result of dispatching one instantiated outcome's atoms against a party (spec §5):
/// the party's own updated <see cref="DelveMemberState"/>s (rows 1-3 of the dispatch table, applied),
/// plus every `stat.derived` grant decided but not yet bound (row 4, a caller's job). Row 5
/// (`ui.present`) has no return value — it is applied directly through the caller-supplied
/// <see cref="Effects.IUiPresentSink"/> as the dispatch runs, the same "side-effect through an injected
/// interface" shape <see cref="DelveUiPresentSink"/>/<see cref="DelveResourceDelta"/> already establish
/// for exactly this program.</summary>
public sealed record EventOutcomeDispatchResult(
    IReadOnlyList<DelveMemberState> Members,
    IReadOnlyList<StatDerivedGrant> StatDerivedGrants);

/// <summary>
/// `event-deck` D3.3/D3.5 (spec-event-deck.md §5) — the five-way atom-kind dispatch table, the second
/// of D3.5's own three named blockers to close (the `EventEffectRef -> ContainerRow` resolver
/// (<see cref="EventEffectContainerBuild"/>) was the first; the forced-outcome/`supplyOverride` path is
/// now also closed — see <see cref="OutcomeResolver.TryForcedOutcome"/>'s own doc comment — leaving only
/// `EventDeck.Build`'s own end-to-end orchestrator, D3.3, as the real remaining integration gap).
///
/// <para><b>Takes an already-real <see cref="InstanceRow"/></b> (the output of
/// <c>Instantiator.TryInstantiate</c>) and parses each atom's own frozen `ValuesJson` — the exact
/// extraction step D3.6's own `DelveResourceDelta` doc comment named as "the caller's job (`EventDeck`,
/// D3.3)". Deliberately narrow: this is NOT the general "any instance, any kind" JSON-to-live-effect
/// bridge D3.6 declined to build (that one is cross-program and unscoped) — this reads exactly the five
/// field sets <see cref="AtomKindRegistry"/> declares for event-deck's own five dispatch-table kinds,
/// for event outcomes only.</para>
///
/// <para><b>Targeting.</b> `resource.delta`/`shield.grant` are the only two kinds whose
/// <see cref="AtomKindRegistry"/> schema declares a `target` (Object) param at all — `status.apply`'s
/// own real schema declares none (confirmed by direct read: `status`/`duration`/`level` only). No
/// existing reader of the `target` Object param exists anywhere in the codebase to mirror (confirmed by
/// search) — this module is the first, so it DEFINES the minimal reading spec §5 itself asks for,
/// stated as an assumption rather than silently guessed (matching <c>AmbushDraw</c>'s own precedent for
/// an identically-unstated subject): a JSON object `{"scope": "one"}` selects
/// <see cref="EventEffectTarget.One"/>; anything else (absent key, absent `target`, any other value)
/// resolves to <see cref="EventEffectTarget.Party"/>, spec's own stated default. Read opportunistically
/// off every kind's frozen values regardless of its declared schema, so `status.apply`'s own missing
/// `target` declaration costs nothing — it simply never carries the key, and defaults to `Party`.</para>
///
/// <para><b>The negative-spirit-delta nerve special case (spec §5, row 1) — resolved as buildable via
/// PLAIN ARITHMETIC, `NervePolicy.Sync` deliberately never called here, confirmed by direct precedent,
/// not guessed.</b> `NervePolicy.Sync(StatusRuntime, hostPtr, stage, now)` needs a live
/// `StatusRuntime`/`hostPtr` — battle-scoped concepts this out-of-room dispatch has none of.
/// `RestResolver.cs`'s own doc comment (D2.20, the closest shipped sibling: also party-state, also
/// between rooms) settles this directly: "Re-projecting stacks to a live `nerve.*` status (`NerveLadder`
/// / `NervePolicy`) is the next room's battle setup's job, not this one's — this record is party state,
/// not runtime state." This dispatch follows the identical rule: a negative `spirit` delta adds
/// `tuning.AttritionNerve.StackPerCurio` to <see cref="DelveMemberState.NerveStacks"/> as plain
/// arithmetic (matching `RestResolver.StacksAfterRelief`'s own shape) and defers the live projection to
/// whichever room's battle setup runs next — never a `nerve.*` id applied from here (the `status.apply`
/// arm below refuses one explicitly, matching spec's own "never a `nerve.*` id from here").</para>
///
/// <para><b>`shield.grant` replace-vs-stack — resolved by the shipped TYPE, not a design guess.</b>
/// <see cref="DelveMemberState.Shield"/> is a single nullable <see cref="BattleInnateShield"/> field,
/// never a list (D2.17's own already-shipped shape) — a grant necessarily REPLACES whatever was there;
/// there is no second field to stack into, and widening that shipped record is outside this task's own
/// scope.</para>
///
/// <para><b>`status.apply`'s own real schema carries no magnitude/period/chance fields</b> (confirmed by
/// direct read of its <see cref="AtomKindRegistry"/> entry: `status`/`duration`(seconds)/`level` only) —
/// <see cref="BattleStatusSpec.MagnitudePerPulse"/> rides at `0` ("0 for pure CC", the record's own
/// documented, legitimate value, not an invented placeholder) and `PeriodMs`/`GrantChanceMilli` ride at
/// the record's own defaults; only `StatusId` and `DurationMs` (seconds × 1000) come from the atom.</para>
/// </summary>
public static class EventOutcomeDispatch
{
    public const string ResourceDeltaKind = "resource.delta";
    public const string StatusApplyKind = "status.apply";
    public const string ShieldGrantKind = "shield.grant";
    public const string StatDerivedKind = "stat.derived";
    public const string UiPresentKind = "ui.present";

    /// <param name="instance">A real, already-`TryInstantiate`d outcome container.</param>
    /// <param name="party">Every party member, standing or not, in the room's own order — the order
    /// this method's own <see cref="EventOutcomeDispatchResult.Members"/> preserves.</param>
    /// <param name="lookupAtom">The real atom catalog — this module holds no copy (established
    /// "read model owned elsewhere" idiom).</param>
    /// <param name="derivedFor">One member's own <c>ActorDerivedSnapshot</c> (max/regen per resource) —
    /// per-member, never one snapshot shared across a whole party, since every creature's own pools differ.</param>
    /// <param name="uiSink">The host's real `ui.present` sink — `op:banner` calls
    /// <see cref="Effects.IUiPresentSink.ShowBanner"/> directly as the dispatch runs.</param>
    /// <param name="delveId">Stamped into every <see cref="StatDerivedGrant.Source"/> as
    /// `"delve:{delveId}"` — spec §5's own row 4, verbatim.</param>
    /// <param name="nerveStackPerCurio">`tuning.AttritionNerve.StackPerCurio` — a plain caller-supplied
    /// value, never re-derived here (this module has no dependency on `Dungeon.Tuning`, matching every
    /// other primitive this program's own pure functions already take instead of a whole tuning object).</param>
    public static EventOutcomeDispatchResult Dispatch(
        InstanceRow instance,
        IReadOnlyList<DelveMemberState> party,
        Func<string, AtomRow?> lookupAtom,
        Func<string, ActorDerivedSnapshot> derivedFor,
        IUiPresentSink uiSink,
        string delveId,
        int nerveStackPerCurio,
        long atTick)
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        if (party is null) throw new ArgumentNullException(nameof(party));
        if (party.Count == 0) throw new ArgumentException("a party must have at least one member", nameof(party));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));
        if (derivedFor is null) throw new ArgumentNullException(nameof(derivedFor));
        if (uiSink is null) throw new ArgumentNullException(nameof(uiSink));
        if (string.IsNullOrWhiteSpace(delveId)) throw new ArgumentException("delveId required", nameof(delveId));
        if (nerveStackPerCurio < 0)
            throw new ArgumentOutOfRangeException(nameof(nerveStackPerCurio), nerveStackPerCurio, "a stack grant is never negative");

        var order = party.Select(m => m.InstanceId).ToList();
        var byId = new Dictionary<string, DelveMemberState>(StringComparer.Ordinal);
        foreach (var m in party)
        {
            if (string.IsNullOrWhiteSpace(m.InstanceId))
                throw new ArgumentException("every party member needs a non-empty InstanceId", nameof(party));
            if (!byId.TryAdd(m.InstanceId, m))
                throw new ArgumentException($"duplicate InstanceId '{m.InstanceId}' in party", nameof(party));
        }

        var grantOrder = new List<string>();
        var grantSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var atomRow in instance.Atoms.OrderBy(a => a.Seq))
        {
            var atom = lookupAtom(atomRow.AtomId)
                ?? throw new EventDeckRefusal($"instance '{instance.InstanceId}' atom '{atomRow.AtomId}' is not in the atom catalog");
            var values = ParseValues(atomRow.ValuesJson);
            var target = ReadTarget(values);
            var targeted = TargetedMembers(order, byId, target);

            switch (atom.KindId)
            {
                case ResourceDeltaKind:
                    ApplyResourceDelta(atom, values, targeted, byId, derivedFor, nerveStackPerCurio, atTick);
                    break;

                case StatusApplyKind:
                    ApplyStatusApply(atom, values, targeted, byId);
                    break;

                case ShieldGrantKind:
                    ApplyShieldGrant(atom, values, targeted, byId);
                    break;

                case StatDerivedKind:
                    foreach (var id in targeted)
                        if (grantSeen.Add(id))
                            grantOrder.Add(id);
                    break;

                case UiPresentKind:
                    ApplyUiPresent(atom, values, uiSink);
                    break;

                default:
                    throw new EventDeckRefusal(
                        $"atom '{atom.AtomId}' kind '{atom.KindId}' is not one of the five event-deck atom kinds " +
                        "(spec-event-deck.md §5: resource.delta, status.apply, shield.grant, stat.derived, ui.present)");
            }
        }

        var members = order.Select(id => byId[id]).ToList();
        var grants = grantOrder
            .Select(id => new StatDerivedGrant(id, instance.ContainerId, $"delve:{delveId}"))
            .ToList();
        return new EventOutcomeDispatchResult(members, grants);
    }

    static void ApplyResourceDelta(
        AtomRow atom, Dictionary<string, JsonElement> values, IReadOnlyList<string> targeted,
        Dictionary<string, DelveMemberState> byId, Func<string, ActorDerivedSnapshot> derivedFor,
        int nerveStackPerCurio, long atTick)
    {
        var channel = ReadString(values, "channel")
            ?? throw new EventDeckRefusal($"atom '{atom.AtomId}' (resource.delta) has no 'channel'");
        var amount = ReadLong(values, "amount", 0);

        foreach (var id in targeted)
        {
            var member = byId[id];
            var newPools = DelveResourceDelta.Apply(
                member.Pools,
                new[] { new DelveResourceDelta.ResourceDelta(channel, amount) },
                derivedFor(id), atTick);

            // Spec §5, row 1, verbatim: "A negative spirit delta also adds
            // attrition.nerve.stackPerCurio ... never a nerve.* id from here" — plain arithmetic, see
            // this class's own doc comment for why NervePolicy.Sync is never called here.
            var nerveStacks = member.NerveStacks;
            if (string.Equals(channel, "spirit", StringComparison.Ordinal) && amount < 0)
                nerveStacks = checked(nerveStacks + nerveStackPerCurio);

            byId[id] = member with { Pools = newPools, NerveStacks = nerveStacks };
        }
    }

    static void ApplyStatusApply(
        AtomRow atom, Dictionary<string, JsonElement> values, IReadOnlyList<string> targeted,
        Dictionary<string, DelveMemberState> byId)
    {
        var statusId = ReadString(values, "status")
            ?? throw new EventDeckRefusal($"atom '{atom.AtomId}' (status.apply) has no 'status'");

        // Spec §5, row 2, verbatim: "a nerve.* id in an event container is an import refusal." The
        // whole-corpus form of this check is D3.9's own already-shipped
        // EventDeckPreflight.CheckNoNerveTargetInAnyContainer (import time); this is the SAME rule
        // enforced again here, defensively, at dispatch time — belt and suspenders, not a duplicate
        // authority, since a hand-built fixture instance in a test never goes through import preflight.
        if (statusId.StartsWith("nerve.", StringComparison.Ordinal))
            throw new EventDeckRefusal(
                $"atom '{atom.AtomId}' (status.apply) targets '{statusId}' -- nerve is an exclusively-derived " +
                "projection (NervePolicy.Sync), never a direct grant from an event container");

        var durationSeconds = ReadLong(values, "duration", 0);
        var spec = new BattleStatusSpec(statusId, MagnitudePerPulse: 0, DurationMs: checked((int)(durationSeconds * 1000)));

        foreach (var id in targeted)
        {
            var member = byId[id];
            var statuses = member.Statuses.Append(spec).ToList();
            byId[id] = member with { Statuses = statuses };
        }
    }

    static void ApplyShieldGrant(
        AtomRow atom, Dictionary<string, JsonElement> values, IReadOnlyList<string> targeted,
        Dictionary<string, DelveMemberState> byId)
    {
        var baseHp = ReadLong(values, "amount", 0);
        ElementTypeId? element = null;
        if (ReadString(values, "element") is { } elementText && ElementRoster.TryParse(elementText, out var parsedElement))
            element = parsedElement;
        var priority = (int)ReadLong(values, "priority", 10);

        // DelveMemberState.Shield is a single nullable field (D2.17) -- a grant REPLACES, it never
        // stacks; see this class's own doc comment for why that is a type fact, not a design guess.
        var shield = new BattleInnateShield(baseHp, element, priority);
        foreach (var id in targeted)
            byId[id] = byId[id] with { Shield = shield };
    }

    static void ApplyUiPresent(AtomRow atom, Dictionary<string, JsonElement> values, IUiPresentSink uiSink)
    {
        var op = ReadString(values, "op");
        if (!string.Equals(op, "banner", StringComparison.Ordinal))
            throw new EventDeckRefusal(
                $"atom '{atom.AtomId}' (ui.present) op '{op ?? "(none)"}' -- event-deck's own dispatch " +
                "table (spec §5) names only op:banner");

        var bannerId = ReadString(values, "bannerId")
            ?? throw new EventDeckRefusal($"atom '{atom.AtomId}' (ui.present op:banner) has no 'bannerId'");
        int? durationMs = values.TryGetValue("durationMs", out var d) && d.ValueKind == JsonValueKind.Number
            ? d.GetInt32() : null;

        uiSink.ShowBanner(bannerId, durationMs);
    }

    static List<string> TargetedMembers(
        List<string> order, Dictionary<string, DelveMemberState> byId, EventEffectTarget target)
    {
        var standing = order.Where(id => !byId[id].Downed).ToList();
        if (target == EventEffectTarget.One)
            return standing.Count > 0 ? new List<string> { standing[0] } : new List<string>();
        return standing;
    }

    static EventEffectTarget ReadTarget(Dictionary<string, JsonElement> values)
    {
        if (values.TryGetValue("target", out var el) && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("scope", out var scopeEl) && scopeEl.ValueKind == JsonValueKind.String
            && string.Equals(scopeEl.GetString(), "one", StringComparison.Ordinal))
            return EventEffectTarget.One;
        return EventEffectTarget.Party;
    }

    static string? ReadString(Dictionary<string, JsonElement> values, string key) =>
        values.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    static long ReadLong(Dictionary<string, JsonElement> values, string key, long fallback) =>
        values.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt64() : fallback;

    static Dictionary<string, JsonElement> ParseValues(string json)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return result;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in doc.RootElement.EnumerateObject()) result[p.Name] = p.Value.Clone();
        return result;
    }
}
