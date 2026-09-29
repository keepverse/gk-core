using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Data;
using FusionRpg.Server.Gates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

/// <summary>
/// Contracts (spec-creature-contracts.md): binding slots and loyalty. Every read settles first — the
/// tribute clock is lazy, so "look at your contracts" is also "bring the books up to date".
/// </summary>
public static class ContractEndpoints
{
    public static void MapContracts(this WebApplication app)
    {
        var g = app.MapGroup("/api/contracts");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            store.SettleContracts(playerId);
            return Results.Ok(ProjectState(store, playerId));
        });

        // [FromServices] is load-bearing, not decorative: minimal API's parameter-binding cache
        // reflects for ANY public method literally named `BindAsync` on a parameter's type
        // (Type.GetMethod("BindAsync") with no BindingFlags filter, so it matches an INSTANCE
        // method too) and throws at endpoint-build time if it isn't the special static
        // `BindAsync(HttpContext, ParameterInfo)` convention shape. ContractService.BindAsync is
        // exactly the gate-services spec's own name for this method (spec-gate-services.md), so
        // the collision is with the platform's naming convention, not a naming choice made here;
        // an explicit [FromServices] resolves the parameter through DI before that convention scan
        // runs. Every endpoint taking a ContractService parameter needs it, not just `/bind`,
        // because the scan runs once per registered endpoint's parameter TYPES at host startup.
        g.MapPost("/bind", async (ContractRequest body, RpgStore store, [FromServices] ContractService contracts) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();

            var outcome = await contracts.BindAsync(pid, body.InstanceId ?? "");
            if (!outcome.Ok) return Refusal(outcome.Reason);
            return Results.Ok(ProjectState(store, pid));
        });

        g.MapPost("/release", async (ContractRequest body, RpgStore store, [FromServices] ContractService contracts) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();

            var outcome = await contracts.ReleaseAsync(pid, body.InstanceId ?? "");
            if (!outcome.Ok) return Refusal(outcome.Reason);
            return Results.Ok(ProjectState(store, pid));
        });

        g.MapPost("/ritual", async (ContractRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            var corrError = ValidateCorrelation(body.CorrelationId);
            if (corrError != null) return corrError;

            var (ok, reason, _) = store.PerformRitual(pid, body.InstanceId ?? "", body.CorrelationId!);
            if (!ok) return Refusal(reason);
            await NotifyAsync(hub, pid);
            return Results.Ok(ProjectState(store, pid));
        });

        g.MapPost("/slots/buy", async (ContractRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            var corrError = ValidateCorrelation(body.CorrelationId);
            if (corrError != null) return corrError;

            var (ok, reason, _) = store.BuyContractSlot(pid, body.CorrelationId!);
            if (!ok) return Refusal(reason);
            await NotifyAsync(hub, pid);
            return Results.Ok(ProjectState(store, pid));
        });
    }

    /// <summary>SIM clock hook: settles as if <c>days</c> had passed, so tribute and decay are
    /// testable without waiting for real midnights. Travelling forward is safe — a later real
    /// settle sees a future stamp and computes zero elapsed days.</summary>
    public static void MapContractTest(this RouteGroupBuilder test)
    {
        test.MapPost("/contracts/settle", (ContractSettleTestRequest body, RpgStore store) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            var result = store.SettleContracts(pid, ServerClock.UtcNow.AddDays(Math.Max(0, body.Days)));
            return Results.Ok(new
            {
                daysSettled = result.DaysSettled,
                soulsPaid = result.SoulsPaid,
                creaturesDecayed = result.CreaturesDecayed,
                state = ProjectState(store, pid)
            });
        });
    }

    static IResult? ValidateCorrelation(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
            return Results.BadRequest(new { reason = "correlation.missing" });
        return correlationId.Trim().Length > 64
            ? Results.BadRequest(new { reason = "correlation.toolong" })
            : null;
    }

    /// <summary>A price the player cannot meet is a conflict, not malformed input.</summary>
    static IResult Refusal(string reason) => reason is "souls.insufficient"
        ? Results.Conflict(new { reason })
        : Results.BadRequest(new { reason });

    static async Task NotifyAsync(IHubContext<RpgHub> hub, long playerId)
    {
        try
        {
            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("ContractsUpdated", new { playerId });
            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("SoulsUpdated", new { playerId });
        }
        catch
        {
            // best-effort: the write is durable, the next read reconciles
        }
    }

    public static object ProjectState(RpgStore store, long playerId)
    {
        var state = store.GetContractState(playerId);
        var purchased = state?.PurchasedSlots ?? 0;
        var contracts = store.ListContracts(playerId);
        var rarities = store.ListCreatureRoster(playerId).Items
            .ToDictionary(s => s.Profile.InstanceId, s => s.Profile.Rarity, StringComparer.Ordinal);

        var rows = contracts.Select(c =>
        {
            CreatureRarityIds.TryParse(
                rarities.TryGetValue(c.InstanceId, out var r) ? r : "chaff", out var rarity);
            return new
            {
                instanceId = c.InstanceId,
                bound = c.Bound,
                loyalty = c.Loyalty,
                rank = c.Rank.ToString().ToLowerInvariant(),
                rankBonusMilli = ContractPolicy.RankBonusMilli(c.Rank),
                personality = c.Personality.ToId(),
                upkeepPerDay = ContractPolicy.UpkeepPerDay(rarity, c.Personality),
                deployable = c.Deployable
            };
        }).OrderBy(c => c.instanceId, StringComparer.Ordinal).ToList();

        return new
        {
            capacity = new
            {
                used = rows.Count(c => c.bound),
                total = ContractPolicy.Capacity(purchased),
                purchasedSlots = purchased,
                // Same pin the store charges at (SoulSinkPolicy.VanillaPvzTheta), so the quoted
                // price and the debited price can never disagree.
                nextSlotPrice = ContractPolicy.NextSlotPrice(
                    purchased, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta,
                    FusionRpg.Core.Power.PowerTuningHub.Tuning),
                canBuy = ContractPolicy.CanBuySlot(purchased),
                // T3.6 (spec-caps-reconcile.md §2.3): ContractPolicy.MaxSlots is deleted -- price is
                // now the only real ceiling (SSOT §11.1a). int.MaxValue keeps this wire field's shape
                // for existing consumers ("no number is ever reached") rather than dropping it, which
                // would ripple into the web frontend's own maxSlots fixtures/types outside this
                // backend spec's file list -- flagged in power-todo.md T3.6 as a follow-up, not fixed
                // here silently.
                maxSlots = int.MaxValue
            },
            dailyTribute = rows.Where(c => c.bound).Sum(c => (long)c.upkeepPerDay),
            deployFloor = ContractPolicy.DeployFloor,
            loyaltyMax = ContractPolicy.LoyaltyMax,
            contracts = rows
        };
    }

    public sealed class ContractRequest
    {
        public long? PlayerId { get; set; }
        public string? InstanceId { get; set; }
        public string? CorrelationId { get; set; }
    }

    public sealed class ContractSettleTestRequest
    {
        public long? PlayerId { get; set; }
        public int Days { get; set; }
    }
}
