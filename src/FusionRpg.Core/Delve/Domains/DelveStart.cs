using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Delve.Pack;
using FusionRpg.Core.Delve.Roll;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>The request body `POST /api/delve/start` and the map door (R10) both send — "the SAME
/// body" (spec-domain-catalog.md §6, verbatim). <see cref="ParentWorldId"/> is null for a Sanctum
/// entry, set for a map-door entry — the one field that tells <see cref="DelveStartLive.ContentTermsJsonFor"/>
/// which world's terms to freeze.</summary>
public sealed record DelveStartRequest(
    string CorrelationId, long PlayerId, string DomainId, string? ParentWorldId,
    string RungIdOrTailLabel, bool Oath, string RaidMode,
    IReadOnlyList<string> MemberInstanceIds, IReadOnlyList<PackItem> CarryIn);

/// <summary>The recorded delve a correlation replay returns — just enough to detect
/// `correlation.mismatch` (the request's own fields must match, spec §6 step 1).</summary>
public sealed record DelveStartReplay(long DelveId, string WorldId, string DomainId, string RaidMode, string RungIdOrTailLabel, bool Oath);

/// <summary>Everything the one write transaction (spec §6 step 7) needs, composed by
/// <see cref="DelveStart.Run"/> without writing anything itself. A caller with an existing
/// <c>DelveStartReplay</c> never reaches this — <see cref="DelveStart.Run"/> returns the replayed row
/// wrapped as success with <see cref="Plan"/> null instead (see its own doc comment).</summary>
public sealed record DelveStartPlan(
    long PlayerId, string DomainId, string RaidMode, string RungIdOrTailLabel, bool Oath, string CorrelationId,
    IReadOnlyList<string> MemberInstanceIds, IReadOnlyList<PackItem> CarryIn,
    ulong Seed, DelveGraph Graph, long ProvisioningPriceSouls, string ContentTermsJson);

/// <summary>
/// Every fact <see cref="DelveStart.Run"/> needs beyond the request and <see cref="DomainCatalog"/> —
/// the same caller-supplied-delegate idiom <see cref="DomainPreflightInputs"/> (D4.17) and
/// <see cref="DomainOfferLive"/> (D4.19) already established for this module, scaled to a
/// six-refusal-group orchestrator. Every delegate here stands in for a REAL dependency this session's
/// own research confirmed by reading the file directly, not guessed:
/// <list type="bullet">
/// <item><see cref="RaidModesForLayout"/> — CLOSED 2026-09-07: `LayoutTemplateCatalog` now exists
/// (D4.30's real prerequisite chain) and `DelveEndpoints.cs`'s own `BuildDelveStartLive` wires it
/// for real; kept as a caller-supplied delegate here since Core still never reads the seed
/// directory itself.</item>
/// <item><see cref="MemberIsInAnotherActiveDelve"/> — `rpg_delve_pack_lock` is keyed by ITEM instance,
/// never actor instance; no existing read answers "is this actor in a delve" (a real, buildable gap
/// left to the Data-layer caller since it needs `rpg_delves.parties_json` scanned across every Active
/// row for this player, `RpgStore.Delve.cs`'s own job, not a pure Core concern).</item>
/// <item><see cref="ProvisioningPriceFor"/> — `DelvePrices.Provisioning` is real and callable directly,
/// but composing its own `thetaEntrance` input needs `RoomThetaComposer.Compose`'s own four real
/// parameters (`PowerTuning`/`DungeonTuning`/`DomainThetaInputs`/`ParentWorldTerms`) — already opaque
/// inside <see cref="ComposeRungs"/>'s own closure, not duplicated a second time here.</item>
/// <item><see cref="ContentTermsJsonFor"/> — no real production code has EVER constructed a real
/// `ParentWorldTerms` from live world state (confirmed: every real construction found is a hardcoded
/// test literal; `ServerPowerIndexProvider.cs`'s own `RealmsAdvanced: 0` shows even the power program's
/// own server-side reader hardcodes the one field it does not track) — a genuine, pre-existing,
/// cross-program gap this task does not resolve, only names.</item>
/// <item><see cref="RollAndPreflight"/> — `DelveGraphRoll.Roll` takes a `DomainAnchor`, a
/// `delve-graph-roll`-owned type distinct from this module's own `DomainRow`; bridging the two is
/// "each row's own adapter, correctly built only once a real domain exists to test the bridge
/// against" — the exact D4.17 reasoning, applied here to the identical situation.</item>
/// </list>
/// </summary>
public sealed record DelveStartLive(
    Func<string, DelveStartReplay?> ReplayFor,
    Func<string, Staleness> StalenessFor,
    Func<string, (string? State, long? DelveId)> DelveStateFor,
    Func<string, PlayerClears> ClearsFor,
    Func<DomainRow, PlayerClears, RungOfferSet> ComposeRungs,
    Func<string, IReadOnlyList<string>> RaidModesForLayout,
    Func<string, (int Parties, int SquadSlots)?> PartyShapeForRaidMode,
    Func<string, bool> MemberIsOwnedRosterBound,
    Func<string, bool> MemberIsRecovering,
    Func<string, bool> MemberIsOnExpedition,
    Func<string, bool> MemberIsInAnotherActiveDelve,
    int ProvisionCells,
    Func<string, long> StockOf,
    Func<DomainRow, long> ProvisioningPriceFor,
    Func<long, long> SoulBalanceFor,
    Func<DomainRow, string?, string> ContentTermsJsonFor,
    Func<ulong> SealSeed,
    Func<DomainRow, string, ulong, (DelveGraph? Graph, string? RefusalDetail)> RollAndPreflight);

/// <summary>
/// D4.21 (spec-domain-catalog.md §6) — the one production path into `CreateDelve`. Six ordered
/// refusal groups, every one checked BEFORE any write (this function performs none itself, by
/// construction — no store type appears anywhere in this file); only when every group passes does it
/// return a <see cref="DelveStartPlan"/> for the Data-layer caller's own single transaction (spec's
/// own step 7) to consume. A correlation replay short-circuits everything and returns immediately —
/// "a replay returns the recorded delve" (spec §6 step 1) is a property of THIS function returning
/// early with `Plan: null` and `Refusal: null` (the caller reads <see cref="Replay"/> instead), not a
/// third return state bolted on afterward.
/// </summary>
public static class DelveStart
{
    public const string RuleCorrelationMissing = "correlation.missing";
    public const string RuleCorrelationMismatch = "correlation.mismatch";
    public const string RuleDomainStale = "domain.stale";
    public const string RuleDomainNotFound = "domain.not-found";
    public const string RuleDomainSealed = "domain.sealed";
    public const string RuleDelveInProgress = "delve.in-progress";
    public const string RuleRungNotOffered = "rung.not-offered";
    public const string RuleOathImplied = "oath.implied";
    public const string RuleRaidModeNotOffered = "raid.mode-not-offered";
    public const string RulePartyShape = "raid.party-shape";
    public const string RuleMemberUnavailable = "member.unavailable";
    public const string RulePackInvalid = "pack.invalid";
    public const string RuleSoulsInsufficient = "delve.souls-insufficient";
    public const string RuleGraphOrObjects = "delve.graph-or-objects";

    // Structural, not a balance tunable -- a protocol bound on an opaque idempotency key, matching
    // ExpeditionEndpoints.cs:281-284's own identical "correlationId present, <= 64 chars" convention
    // for the same purpose. A balance pass has no reason to ever touch this.
    const int MaxCorrelationIdLength = 64;

    public static (DomainRefusal? Refusal, DelveStartReplay? Replay, DelveStartPlan? Plan) Run(
        DelveStartRequest request, DomainCatalog catalog, DelveStartLive live)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (live is null) throw new ArgumentNullException(nameof(live));

        // ---- group 1: correlation, replay, domain state ----------------------------------------
        if (string.IsNullOrEmpty(request.CorrelationId) || request.CorrelationId.Length > MaxCorrelationIdLength)
            return (Refuse(request, RuleCorrelationMissing, "correlationId must be present and <= 64 chars"), null, null);

        var replay = live.ReplayFor(request.CorrelationId);
        if (replay is not null)
        {
            if (!string.Equals(replay.DomainId, request.DomainId, StringComparison.Ordinal) ||
                !string.Equals(replay.RaidMode, request.RaidMode, StringComparison.Ordinal) ||
                !string.Equals(replay.RungIdOrTailLabel, request.RungIdOrTailLabel, StringComparison.Ordinal) ||
                replay.Oath != request.Oath)
                return (Refuse(request, RuleCorrelationMismatch, "a replayed correlationId's body differs from the one recorded"), null, null);

            return (null, replay, null); // "a replay returns the recorded delve" -- nothing more to check
        }

        var domain = catalog.Resolve(request.DomainId);
        if (domain is null)
            return (Refuse(request, RuleDomainNotFound, $"domainId '{request.DomainId}' is not a known domain"), null, null);
        if (live.StalenessFor(request.DomainId) == Staleness.Stale)
            return (Refuse(request, RuleDomainStale, $"domainId '{request.DomainId}' is stale"), null, null);

        var (state, existingDelveId) = live.DelveStateFor(request.DomainId);
        var isOnce = string.Equals(domain.Entry, "once", StringComparison.Ordinal);
        if (isOnce && string.Equals(state, "Archived", StringComparison.Ordinal))
            return (Refuse(request, RuleDomainSealed, $"domainId '{request.DomainId}' is sealed (once-entry, already archived)"), null, null);
        if (string.Equals(state, "Active", StringComparison.Ordinal))
            return (Refuse(request, RuleDelveInProgress, $"domainId '{request.DomainId}' already has an in-progress delve"), null, null);

        // ---- group 2: rung/tail, oath, raid mode, party shape -----------------------------------
        var clears = live.ClearsFor(request.DomainId);
        var offer = live.ComposeRungs(domain, clears);
        var rungRow = offer.Rungs.FirstOrDefault(r => string.Equals(r.RungId, request.RungIdOrTailLabel, StringComparison.Ordinal));
        var tailRow = int.TryParse(request.RungIdOrTailLabel, out var tailN)
            ? offer.TailSteps.FirstOrDefault(t => t.N == tailN)
            : null;

        bool offered; bool isPermadeath;
        if (rungRow is not null) { offered = rungRow.Offered; isPermadeath = rungRow.IsPermadeath; }
        else if (tailRow is not null) { offered = tailRow.Offered; isPermadeath = true; } // past rung 10, always the mandatory tier
        else { offered = false; isPermadeath = false; }

        if (!offered)
            return (Refuse(request, RuleRungNotOffered, $"'{request.RungIdOrTailLabel}' is not currently offered for domainId '{request.DomainId}'"), null, null);
        if (request.Oath && isPermadeath)
            return (Refuse(request, RuleOathImplied, $"'{request.RungIdOrTailLabel}' is already mandatory permadeath -- oath cannot be requested on top of it"), null, null);

        if (!live.RaidModesForLayout(domain.LayoutTemplateId).Contains(request.RaidMode, StringComparer.Ordinal))
            return (Refuse(request, RuleRaidModeNotOffered, $"raidMode '{request.RaidMode}' is not offered by layout '{domain.LayoutTemplateId}'"), null, null);

        var shape = live.PartyShapeForRaidMode(request.RaidMode);
        if (shape is null || request.MemberInstanceIds.Count == 0 || request.MemberInstanceIds.Count > shape.Value.Parties * shape.Value.SquadSlots)
            return (Refuse(request, RulePartyShape, $"raidMode '{request.RaidMode}' does not accept {request.MemberInstanceIds.Count} member(s)"), null, null);

        // ---- group 3: every member available ----------------------------------------------------
        foreach (var memberId in request.MemberInstanceIds)
        {
            if (!live.MemberIsOwnedRosterBound(memberId) || live.MemberIsRecovering(memberId) ||
                live.MemberIsOnExpedition(memberId) || live.MemberIsInAnotherActiveDelve(memberId))
                return (Refuse(request, $"{RuleMemberUnavailable}:{memberId}", $"member '{memberId}' is not available"), null, null);
        }

        // ---- group 4: pack provisioning and price, from the bank --------------------------------
        var (packOk, packReason) = PackProvisioning.Validate(request.CarryIn, live.ProvisionCells, live.StockOf);
        if (!packOk)
            return (Refuse(request, RulePackInvalid, packReason), null, null);

        var price = live.ProvisioningPriceFor(domain);
        if (live.SoulBalanceFor(request.PlayerId) < price)
            return (Refuse(request, RuleSoulsInsufficient, $"provisioning costs {price} souls, balance is short"), null, null);
        // "nothing debited yet" (spec §6 step 4, verbatim) -- the debit is group 7's own write.

        // ---- group 5: parent terms frozen (no refusal -- a pure compose) ------------------------
        var contentTermsJson = live.ContentTermsJsonFor(domain, request.ParentWorldId);

        // ---- group 6: seed sealed, graph rolled, objects preflighted ----------------------------
        var seed = live.SealSeed();
        var (graph, refusalDetail) = live.RollAndPreflight(domain, request.RaidMode, seed);
        if (graph is null)
            return (Refuse(request, RuleGraphOrObjects, refusalDetail ?? "graph roll or object preflight refused"), null, null);

        // ---- group 7 is the caller's own single transaction; this function writes nothing -------
        return (null, null, new DelveStartPlan(
            request.PlayerId, request.DomainId, request.RaidMode, request.RungIdOrTailLabel, request.Oath,
            request.CorrelationId, request.MemberInstanceIds, request.CarryIn,
            seed, graph, price, contentTermsJson));
    }

    static DomainRefusal Refuse(DelveStartRequest request, string rule, string detail) => new(request.DomainId, rule, detail);
}
