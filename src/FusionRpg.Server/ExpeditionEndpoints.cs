using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

/// <summary>
/// Expedition loop (spec-expeditions.md): dispatch seals squad + seed; collect lazily resolves
/// the timeline, runs each planned battle through WebMatchService (correlation `exp:{id}:{n}`),
/// then applies event Souls / specimen XP / wild-join mints / materials in ONE state-gated
/// transaction — collect is exactly-once even across crashes.
/// </summary>
public sealed class ExpeditionService
{
    readonly RpgStore _store;
    readonly WebMatchService _webMatch;
    readonly IHubContext<RpgHub> _hub;
    readonly FusionRpg.Core.Power.IPowerIndexProvider _powerIndex;
    int _faultAfterNextCollectCommit;
    CollectCommitBarrier? _collectCommitBarrier;

    public ExpeditionService(RpgStore store, WebMatchService webMatch, IHubContext<RpgHub> hub,
        FusionRpg.Core.Power.IPowerIndexProvider powerIndex)
    {
        _store = store;
        _webMatch = webMatch;
        _hub = hub;
        _powerIndex = powerIndex;
    }

    public async Task<(bool Ok, string Reason, ExpeditionRow? Row)> DispatchAsync(
        long playerId, string correlationId, string tierId, IReadOnlyList<string> squad)
    {
        var seed = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
        var (ok, reason, row) = _store.DispatchExpedition(playerId, correlationId, tierId, squad, seed);

        // Replay must match the stored request (house pattern, cf. summon pulls): a reused
        // correlation with a different tier/squad silently returning the old expedition would
        // let the client believe its new squad went out.
        if (ok && reason == "replay"
            && (row!.TierId != tierId || !row.SquadInstanceIds.SequenceEqual(squad, StringComparer.Ordinal)))
            return (false, "correlation.mismatch", null);

        if (ok && reason != "replay")
        {
            try
            {
                await _hub.Clients.Group(RpgConstants.WebGroup)
                    .SendAsync("CreaturesUpdated", new { playerId }).ConfigureAwait(false);
            }
            catch
            {
                // best-effort; the dispatch is durable and a replayed request recovers the row
            }
        }

        return (ok, reason, row);
    }

    public async Task<(bool Ok, string Reason, ExpeditionCollectResult? Result)> CollectAsync(
        long expeditionId, long playerId, bool recall)
    {
        var row = _store.TryGetExpedition(expeditionId);
        if (row is null || row.PlayerId != playerId) return (false, "expedition.notfound", null);
        if (row.State != ExpeditionStates.Dispatched)
        {
            var replay = _store.TryGetExpeditionCollectResult(expeditionId);
            return replay is null
                ? (false, "expedition." + row.State.ToLowerInvariant(), null)
                : (true, "replay", replay);
        }

        var tier = ExpeditionTierCatalog.Get(row.TierId);
        var now = ServerClock.UtcNow;
        var due = DateTimeOffset.Parse(row.DueUtc);
        var dispatched = DateTimeOffset.Parse(row.DispatchedUtc);
        if (!recall && now < due) return (false, "expedition.notdue", null);
        var squadIds = row.SquadInstanceIds; // computed property re-parses JSON — read once

        // Recall pro-rates at tick boundaries; a due collect resolves the full timeline. A retry
        // after a crashed attempt must never resolve FEWER ticks than battles already ingested
        // (backwards clock skew would strand committed tail battles outside the closed state —
        // 2026-08-21 review I6), so elapsed is floored at the furthest logged battle's tick.
        var elapsed = now >= due
            ? tier.TickCount
            : Math.Clamp((int)((now - dispatched).TotalMinutes / tier.TickMinutes), 0, tier.TickCount);
        elapsed = Math.Max(elapsed, LoggedBattleTickFloor(row, tier, playerId));

        // The stored squad snapshot rule: BuildSquad reads the LIVE roster, which only works
        // because the soft-lock freezes deployment while dispatched; specimen XP from this very
        // collect lands after resolution. Already-logged battles replay their sealed setups.
        var (squadOk, squadReason, squad, _) = _webMatch.BuildSquad(playerId, squadIds);
        if (!squadOk) return (false, squadReason, null);

        var resolution = ExpeditionResolver.Resolve(tier.TierId, squad!, row.Seed, elapsed);

        // Battles run first — each is correlation-idempotent, so a crashed collect replays them.
        var battleResults = new List<ExpeditionBattleResult>();
        // Accumulated in MILLI-XP as long, divided by 1000 exactly once at the award below —
        // docs/architecture/numeric-types.md's numeric rule ("widen before multiplying, divide by 1000 last"). Summing
        // `rate * xpMilli / 1000.0` per battle instead would accumulate float error into a value
        // that is then persisted (progression-shape-audit-2026-09-04.md §4.1).
        var specimenXpMilli = new Dictionary<string, long>(StringComparer.Ordinal);
        var victories = 0;
        // species-build-todo.md T4.6, spec-zomboss-adaptive.md's own ⛔ seam: the enemy side actually
        // carries a pattern only for the expedition's BOSS battle — the Zomboss's own real production
        // caller. Resolved BEFORE RunPlannedMatchAsync (never during resolution), so `(setup, seed)`
        // stays reproducible from that point on, matching every other planned-battle setup here.
        var theta = (long)_powerIndex.ActorIndex(new FusionRpg.Core.Stats.StatContext { PlayerId = playerId });
        foreach (var plan in resolution.Battles)
        {
            var correlation = BattleCorrelation(row.Id, plan.BattleIndex);
            // Selection has a REAL side effect (advances rpg_zomboss_state) -- it must run exactly
            // once per battle, never once per CollectAsync retry. A battle already logged under this
            // correlation is a replay: RunPlannedMatchAsync's own replay branch ignores the setup
            // BODY it is handed and returns the stored (already-enriched) one, so the plain plan.Setup
            // below is only ever a placeholder for that call, never what actually resolves.
            var setup = plan.Boss && _store.TryGetWebMatchLog(playerId, correlation) is null
                ? _webMatch.ApplyZombossPattern(playerId, plan.Setup, theta, plan.BattleSeed)
                : plan.Setup;
            var (ok, reason, outcome) = await _webMatch
                .RunPlannedMatchAsync(playerId, correlation,
                    BattleMatchKey(row.Id, plan.BattleIndex), setup, plan.BattleSeed)
                .ConfigureAwait(false);
            if (!ok) return (false, "battle." + reason, null);

            var report = outcome!.Report;
            battleResults.Add(new ExpeditionBattleResult(
                plan.BattleIndex, plan.Boss,
                report.Outcome.ToString().ToLowerInvariant(), outcome.RunId, outcome.MatchKey,
                outcome.TurnOrder));

            // Specimen XP per battle won: survivors earn the tier rate × their genius multiplier.
            if (report.Outcome == BattleOutcome.Victory)
            {
                victories++;
                foreach (var actor in report.Actors)
                {
                    if (actor.Side != "squad" || !actor.Survived) continue;
                    var slot = SlotIndex(actor.Key);
                    if (slot < 0 || slot >= squadIds.Count) continue;
                    var instanceId = squadIds[slot];
                    specimenXpMilli.TryGetValue(instanceId, out var haveMilli);
                    checked
                    {
                        specimenXpMilli[instanceId] = haveMilli +
                            (long)resolution.Rewards.SpecimenXpPerBattleWon * actor.XpMilli;
                    }
                }
            }
        }

        // The resolver's manifest is complete (greedy multiplier and wild-join traits included) —
        // this layer only maps it onto store writes.
        var wildMints = resolution.Rewards.WildJoins.Select(join =>
        {
            var species = CreatureSpeciesCatalog.Get(join.SpeciesId);
            return new CreatureMintSpec
            {
                SpeciesId = species.SpeciesId,
                Side = species.Side,
                GameTypeId = species.GameTypeId,
                Rarity = species.BaseRarity.ToId(),
                Variant = join.Variant,
                ElementPrimary = species.ElementPrimary.ToElementId(),
                ElementSecondary = species.ElementSecondary?.ToElementId(),
                TraitIds = join.TraitIds.ToList(),
                Origin = "expedition"
            };
        }).ToList();

        var state = recall && elapsed < tier.TickCount ? ExpeditionStates.Recalled : ExpeditionStates.Collected;
        // The single divide, half away from zero — the same rounding direction PowerLadder and
        // RpgXpAwardMap use, so every XP that reaches the store was rounded once and identically.
        var xpAwards = specimenXpMilli.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, Xp: (kv.Value + (kv.Value >= 0 ? 500L : -500L)) / 1000L))
            .ToList();
        var resultDraft = new ExpeditionCollectResult(
            state, elapsed, resolution.Ticks, battleResults,
            resolution.Rewards.EventSouls, resolution.Rewards.Materials,
            Array.Empty<CreatureSpecimenDto>(),
            xpAwards.Select(xp => new ExpeditionSpecimenXp(xp.Key, xp.Xp)).ToList());

        var barrier = Volatile.Read(ref _collectCommitBarrier);
        if (barrier is not null)
        {
            try
            {
                await barrier.ArriveAndWaitAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.CompareExchange(ref _collectCommitBarrier, null, barrier);
            }
        }

        var injectPostCommitFault = Interlocked.Exchange(ref _faultAfterNextCollectCommit, 0) == 1;
        var commit = _store.CommitExpeditionRewardsAndResult(
            row.Id, playerId, state,
            new RpgStore.ExpeditionRewardApply(
                resolution.Rewards.EventSouls,
                resolution.Rewards.Materials.Select(m => (m.MaterialId, m.Qty)).ToList(),
                xpAwards,
                wildMints),
            resultDraft);
        if (commit.Result is null) return (false, commit.Reason, null);
        if (injectPostCommitFault && commit.Applied)
            throw new InvalidOperationException("Injected expedition collect post-commit fault.");
        if (!commit.Applied) return (true, "replay", commit.Result);
        var result = commit.Result;

        // Loyalty for the trip as a whole, not per battle: an expedition credits a win when most
        // of its battles were victories. A recall before the first battle is neither. Like the
        // web-match credit, this sits outside the exactly-once rewards envelope on purpose — a
        // replay returns before reaching here, so the worst case is a lost ±15, never a double credit.
        if (battleResults.Count > 0)
            _store.ApplyContractResults(playerId, squadIds, victories * 2 >= battleResults.Count);

        // Rewards and reveal are committed: notifies are best-effort. A hub fault here must not
        // fail the call because the exact reveal is now durable for a retry.
        try
        {
            await _hub.Clients.Group(RpgConstants.WebGroup)
                .SendAsync("CreaturesUpdated", new { playerId }).ConfigureAwait(false);
            await _hub.Clients.Group(RpgConstants.WebGroup)
                .SendAsync("SoulsUpdated", new { playerId }).ConfigureAwait(false);
            // EP1.15 (spec-default-build.md, cache trigger T3): a specimen the expedition collect
            // leveled changed nothing about its allocation, yet its resolved default did.
            foreach (var instanceId in commit.LeveledSpecimenIds)
                await AptitudeEndpoints.BroadcastBestEffort(_hub,
                    new AptitudeEndpoints.AptitudesUpdatedDto(playerId, "unique", instanceId, null)).ConfigureAwait(false);
        }
        catch
        {
            // best-effort; queries refresh on the next poll regardless
        }

        return (true, "", result);
    }

    static string BattleCorrelation(long expeditionId, int battleIndex) => $"exp:{expeditionId}:{battleIndex}";
    static string BattleMatchKey(long expeditionId, int battleIndex) => $"exp-{expeditionId}-{battleIndex}";

    /// <summary>Lowest elapsed-tick count consistent with battles already logged by a prior
    /// crashed attempt — at most one log lookup per possible battle (≤5).</summary>
    int LoggedBattleTickFloor(ExpeditionRow row, ExpeditionTierDef tier, long playerId)
    {
        var floor = 0;
        foreach (var (battleIndex, tickIndex, _) in ExpeditionResolver.BattleSchedule(tier))
        {
            if (_store.TryGetWebMatchLog(playerId, BattleCorrelation(row.Id, battleIndex)) != null)
                floor = Math.Max(floor, tickIndex);
        }

        return floor;
    }

    static int SlotIndex(string actorKey)
    {
        var sep = actorKey.LastIndexOf(':');
        return sep >= 0 && int.TryParse(actorKey[(sep + 1)..], out var i) ? i : -1;
    }

    internal bool ArmCollectCommitBarrier(int participants)
    {
        if (participants < 2) return false;
        return Interlocked.CompareExchange(
            ref _collectCommitBarrier,
            new CollectCommitBarrier(participants),
            null) is null;
    }

    internal bool ArmPostCollectCommitFault() =>
        Interlocked.Exchange(ref _faultAfterNextCollectCommit, 1) == 0;

    sealed class CollectCommitBarrier
    {
        readonly int _participants;
        readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _arrived;

        public CollectCommitBarrier(int participants) => _participants = participants;

        public async Task ArriveAndWaitAsync()
        {
            if (Interlocked.Increment(ref _arrived) >= _participants)
                _released.TrySetResult();
            await _released.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
    }
}

public static class ExpeditionEndpoints
{
    public static void MapExpeditions(this WebApplication app)
    {
        var g = app.MapGroup("/api/expeditions");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            var rejected = RejectUnavailableSave(playerId, store);
            if (rejected is not null) return rejected;
            return Results.Ok(new
            {
                serverUtc = ServerClock.UtcNowDateTime.ToString("o"),
                tiers = ExpeditionTierCatalog.All,
                items = store.ListExpeditions(playerId).Select(Project)
            });
        });

        g.MapGet("/{playerId:long}/materials", (long playerId, RpgStore store) =>
        {
            var rejected = RejectUnavailableSave(playerId, store);
            if (rejected is not null) return rejected;
            return Results.Ok(new { items = store.ListCreatureMaterials(playerId) });
        });

        g.MapPost("/dispatch", async (DispatchRequest body, ExpeditionService svc, RpgStore store) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            var rejected = RejectUnavailableSave(pid, store);
            if (rejected is not null) return rejected;
            if (string.IsNullOrWhiteSpace(body.CorrelationId))
                return Results.BadRequest(new { reason = "correlation.missing" });
            if (body.CorrelationId.Trim().Length > 64)
                return Results.BadRequest(new { reason = "correlation.toolong" });

            var (ok, reason, row) = await svc.DispatchAsync(
                pid, body.CorrelationId!, body.TierId ?? "", body.Squad ?? new List<string>());
            if (!ok)
                return reason == "player.archived"
                    ? Results.Conflict(new { reason })
                    : Results.BadRequest(new { reason });
            return Results.Ok(new { replayed = reason == "replay", expedition = Project(row!) });
        });

        g.MapPost("/{id:long}/collect", (long id, CollectRequest? body, ExpeditionService svc, RpgStore store) =>
            CollectOrRecallAsync(id, body, svc, store, recall: false));

        g.MapPost("/{id:long}/recall", (long id, CollectRequest? body, ExpeditionService svc, RpgStore store) =>
            CollectOrRecallAsync(id, body, svc, store, recall: true));
    }

    static IResult? RejectUnavailableSave(long playerId, RpgStore store)
    {
        if (!store.PlayerExists(playerId))
            return Results.NotFound(new { reason = "player.unknown" });
        if (!store.IsLiveSave(playerId))
            return Results.Conflict(new { reason = "save.archived" });
        return null;
    }

    static async Task<IResult> CollectOrRecallAsync(
        long id, CollectRequest? body, ExpeditionService svc, RpgStore store, bool recall)
    {
        var pid = body?.PlayerId ?? store.GetCurrentPlayerId();
        var rejected = RejectUnavailableSave(pid, store);
        if (rejected is not null) return rejected;
        var (ok, reason, result) = await svc.CollectAsync(id, pid, recall);
        if (!ok)
        {
            return reason is "expedition.notfound" ? Results.NotFound()
                : reason is "expedition.notdue" or "expedition.collected" or "expedition.recalled" or "expedition.closed"
                    ? Results.Conflict(new { reason })
                    : Results.BadRequest(new { reason });
        }

        return Results.Ok(new
        {
            state = result!.State,
            elapsedTicks = result.ElapsedTicks,
            ticks = result.Ticks,
            battles = result.Battles,
            soulsAwarded = result.SoulsAwarded,
            materials = result.Materials,
            wildJoins = result.WildJoins,
            specimenXp = result.SpecimenXp.Select(x => new { instanceId = x.InstanceId, xp = x.Xp })
        });
    }

    /// <summary>Wire projection: the sealed seed never leaves the server before collect (the
    /// resolver is pure, so a leaked seed lets a client pre-read every outcome and re-roll via
    /// free recalls — 2026-08-21 review), and correlation/squad_json are server bookkeeping.
    /// Seeds are also ulong — raw serialization would corrupt above 2^53 in JS.</summary>
    static object Project(ExpeditionRow row) => new
    {
        id = row.Id,
        state = row.State,
        tierId = row.TierId,
        squadInstanceIds = row.SquadInstanceIds,
        dispatchedUtc = row.DispatchedUtc,
        dueUtc = row.DueUtc,
        collectedUtc = row.CollectedUtc
    };

    /// <summary>
    /// SIM-only fault seams for the durability proof. They do not fabricate expedition state: the
    /// collect route still resolves and commits the real operation. The barrier releases two real
    /// requests together immediately before their reward transactions; the fault throws only after
    /// the store transaction has committed, modelling a lost response.
    ///
    /// <para>The old <c>/api/test/expedition-due</c> store bypass remains retired. Tests move the
    /// clock seam or dispatch with a past <c>utcNow</c>; no route rewrites <c>due_utc</c>.</para>
    /// </summary>
    public static void MapExpeditionTest(this RouteGroupBuilder test)
    {
        test.MapPost("/expedition-collect-commit-barrier/{participants:int}", (int participants, ExpeditionService svc) =>
            svc.ArmCollectCommitBarrier(participants)
                ? Results.Ok(new { armed = true, participants })
                : Results.Conflict(new { reason = "expedition.test-barrier.armed" }));
        test.MapPost("/expedition-collect-fault-after-commit", (ExpeditionService svc) =>
            svc.ArmPostCollectCommitFault()
                ? Results.Ok(new { armed = true })
                : Results.Conflict(new { reason = "expedition.test-fault.armed" }));
    }

    public sealed class DispatchRequest
    {
        public long? PlayerId { get; set; }
        public string? CorrelationId { get; set; }
        public string? TierId { get; set; }
        public List<string>? Squad { get; set; }
    }

    public sealed class CollectRequest
    {
        public long? PlayerId { get; set; }
    }
}
