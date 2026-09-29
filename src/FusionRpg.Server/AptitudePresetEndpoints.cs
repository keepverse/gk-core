using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Power;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Data;
using FusionRpg.Server.Gates;
using Microsoft.AspNetCore.SignalR;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

/// <summary>
/// aptitude-sheet AS-3.1 / AS-3.2 — <c>/api/aptitude-presets</c>: named library CRUD, active binding,
/// D13 materialize, favour GET (S1), transactional Activate (S3). Broadcasts via
/// <see cref="AptitudeEndpoints.BroadcastBestEffort"/>.
/// </summary>
public static class AptitudePresetEndpoints
{
    public static void MapAptitudePresets(this WebApplication app)
    {
        var g = app.MapGroup("/api/aptitude-presets");

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            var list = store.ListAptitudePresets(playerId).Select(p => ProjectPreset(store, p)).ToList();
            return Results.Ok(new { presets = list });
        });

        g.MapPost("/", (SaveAptitudePresetRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { reason = "presets.name.missing" });

            var kind = string.IsNullOrWhiteSpace(body.Kind) ? RpgStore.AptitudePresetKindPlayer : body.Kind!.Trim();
            var presetId = string.IsNullOrWhiteSpace(body.PresetId)
                ? Guid.NewGuid().ToString("N")
                : body.PresetId!.Trim();
            var entries = ParseEntries(presetId, body.Rows);
            if (entries is null)
                return Results.BadRequest(new { reason = "presets.rows.missing" });

            var row = new RpgAptitudePresetRow(
                presetId, pid, body.Name.Trim(), kind,
                ServerClock.UtcNow.ToString("o"), Revision: 0);
            var reason = store.SaveAptitudePreset(row, entries, isCreate: true);
            if (reason == "presets.softMax")
                return Results.Conflict(new { reason, softMax = AptitudePresetTuningHub.Tuning.SoftMaxPresets });
            if (!string.IsNullOrEmpty(reason))
                return Results.BadRequest(new { reason });

            _ = BroadcastLibrary(hub, pid);
            var saved = store.GetAptitudePreset(presetId)!;
            return Results.Ok(ProjectPreset(store, saved));
        });

        g.MapPut("/{presetId}", (string presetId, SaveAptitudePresetRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var existing = store.GetAptitudePreset(presetId);
            if (existing is null) return Results.NotFound();
            var pid = body.PlayerId ?? existing.PlayerId;
            if (pid != existing.PlayerId) return Results.BadRequest(new { reason = "presets.owner.mismatch" });
            if (string.IsNullOrWhiteSpace(body.Name))
                return Results.BadRequest(new { reason = "presets.name.missing" });

            var kind = string.IsNullOrWhiteSpace(body.Kind) ? existing.Kind : body.Kind!.Trim();
            var entries = ParseEntries(presetId, body.Rows);
            if (entries is null)
                return Results.BadRequest(new { reason = "presets.rows.missing" });

            var row = existing with { Name = body.Name.Trim(), Kind = kind };
            var reason = store.SaveAptitudePreset(row, entries, isCreate: false);
            if (!string.IsNullOrEmpty(reason))
                return Results.BadRequest(new { reason });

            _ = BroadcastLibrary(hub, pid);
            return Results.Ok(ProjectPreset(store, store.GetAptitudePreset(presetId)!));
        });

        g.MapDelete("/{presetId}", (string presetId, long? playerId, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var existing = store.GetAptitudePreset(presetId);
            if (existing is null) return Results.NotFound();
            var pid = playerId ?? existing.PlayerId;
            if (!store.DeleteAptitudePreset(pid, presetId))
                return Results.NotFound();
            _ = BroadcastLibrary(hub, pid);
            return Results.Ok(new { deleted = presetId });
        });

        g.MapGet("/active", (long playerId, string scope, string? scopeKey, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(scope))
                return Results.BadRequest(new { reason = "presets.scope.missing" });
            var active = store.GetAptitudePresetActive(playerId, scope, scopeKey ?? "");
            if (active is null) return Results.Ok(new { playerId, scope, scopeKey = scopeKey ?? "", presetId = (string?)null });
            return Results.Ok(new
            {
                playerId = active.PlayerId,
                scope = active.Scope,
                scopeKey = active.ScopeKey,
                presetId = active.PresetId
            });
        });

        g.MapPut("/active", (SetActiveRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Scope))
                return Results.BadRequest(new { reason = "presets.scope.missing" });
            if (string.IsNullOrWhiteSpace(body.PresetId))
                return Results.BadRequest(new { reason = "presets.id.missing" });

            var reason = store.SetAptitudePresetActive(pid, body.Scope!, body.ScopeKey ?? "", body.PresetId!);
            if (reason == "presets.notFound") return Results.NotFound();
            if (!string.IsNullOrEmpty(reason)) return Results.BadRequest(new { reason });

            _ = AptitudePresetActivation.BroadcastScoped(hub, pid, body.Scope!, body.ScopeKey);
            return Results.Ok(new
            {
                playerId = pid,
                scope = body.Scope,
                scopeKey = body.ScopeKey ?? "",
                presetId = body.PresetId
            });
        });

        g.MapPost("/materialize", (MaterializeRequest body, RpgStore store) =>
        {
            if (string.IsNullOrWhiteSpace(body.PresetId))
                return Results.BadRequest(new { reason = "presets.id.missing" });
            if (body.Budget < 0)
                return Results.BadRequest(new { reason = "presets.budget.negative" });

            var preset = store.GetAptitudePreset(body.PresetId!);
            if (preset is null) return Results.NotFound();
            if (body.PlayerId is long pid && pid != preset.PlayerId)
                return Results.BadRequest(new { reason = "presets.owner.mismatch" });

            var result = AptitudePresetMaterialize.Materialize(
                RpgStore.ToRowSpecs(store.GetAptitudePresetEntries(preset.PresetId)), body.Budget);
            if (!result.Ok)
                return Results.Conflict(new { reason = result.Reason });

            return Results.Ok(new
            {
                presetId = preset.PresetId,
                budget = body.Budget,
                shares = result.Shares,
                leftover = result.Leftover
            });
        });

        // EP1.20 (spec-auto-assign-control.md, C1/C5) — the closed rule ids this scope may offer.
        // Never a hardcoded FE list: Mode C (commander) has no species, so `species-favour` is
        // omitted here rather than left for the FE to hide, or for a click to refuse (C5 says hide,
        // not refuse). The three posture ids and `even` never refuse (AssignLadder.TryRung), so they
        // are always offered; `active-preset` can still refuse reactively on click (C4) if the scope
        // has no active preset -- that is the existing `/suggest` refusal path, unchanged here.
        g.MapGet("/rules", (string? scope) =>
        {
            if (string.IsNullOrWhiteSpace(scope))
                return Results.BadRequest(new { reason = "presets.scope.missing" });
            var trimmed = scope.Trim();
            var allocation = AptitudePresetActivation.ScopeToAllocation(trimmed);
            if (allocation is null)
                return Results.BadRequest(new { reason = "presets.scope.unknown" });

            // Keyed off the SAME scope mapping every other route here uses, never a second string
            // comparison that could drift from it: `commander` is Mode C, the only scope with no
            // species, so C5's hide-not-refuse rule follows the mapping rather than the spelling.
            var rules = AptitudeAutoAssignRules.All
                .Where(id => allocation != AllocationScope.Commander || id != AptitudeAutoAssignRules.SpeciesFavour)
                .ToList();
            return Results.Ok(new { scope = trimmed, rules });
        });

        // EP1.4 (spec-assign-ladder.md) — a draft only; never persists. With `rule` omitted, the
        // ladder walks tuning.Order; with `rule` given, only that rule runs (AssignLadder.TryOne).
        g.MapPost("/suggest", (SuggestRequest body, RpgStore store, IPowerIndexProvider powerIndex) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Scope))
                return Results.BadRequest(new { reason = "presets.scope.missing" });

            var scope = body.Scope!.Trim();
            var scopeKey = body.ScopeKey ?? "";
            if (AptitudePresetActivation.ScopeToAllocation(scope) is null)
                return Results.BadRequest(new { reason = "presets.scope.unknown" });

            var budgetResolve = AptitudePresetActivation.ResolveBudget(store, powerIndex, pid, scope, scopeKey);
            if (!budgetResolve.Ok)
                return Results.BadRequest(new { reason = budgetResolve.Reason });

            var ctx = BuildAssignContext(store, pid, scope, scopeKey);

            string ruleId;
            IReadOnlyList<AptitudePresetRowSpec> rows;
            IReadOnlyList<AssignSkip> skipped;
            if (string.IsNullOrWhiteSpace(body.Rule))
            {
                var suggestion = AssignLadder.Suggest(ctx, AptitudePresetTuningHub.Tuning.AssignLadder);
                ruleId = suggestion.RuleId;
                rows = suggestion.Rows;
                skipped = suggestion.Skipped;
            }
            else
            {
                var (ok, oneRows, reason) = AssignLadder.TryOne(body.Rule!.Trim(), ctx);
                if (!ok) return Results.BadRequest(new { reason });
                ruleId = body.Rule!.Trim();
                rows = oneRows;
                skipped = Array.Empty<AssignSkip>();
            }

            var materialized = AptitudePresetMaterialize.Materialize(rows, budgetResolve.Budget);
            if (!materialized.Ok)
                return Results.Conflict(new { reason = materialized.Reason });

            return Results.Ok(new
            {
                ruleId,
                rows = rows.Select(r => new { aptitudeId = r.AptitudeId, targetPermille = r.TargetPermille }),
                skipped = skipped.Select(s => new { ruleId = s.RuleId, reason = s.Reason }),
                draftShares = materialized.Shares,
                leftover = materialized.Leftover
            });
        });

        // EP1.16 (spec-default-build.md, W4) — the systemCopy producer: "a copy of the build the
        // game suggested". Writes ONE preset whose rows equal the CURRENT ladder suggestion (the
        // walk, never an explicit `rule` — that is what `/suggest` is for), named after the winning
        // rung unless `name` is given. Refuses past `softMaxPresets` exactly as a player preset does
        // (test 7): SaveAptitudePreset is the SAME gate, not a second one.
        g.MapPost("/suggested", (SuggestedPresetRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.Scope))
                return Results.BadRequest(new { reason = "presets.scope.missing" });

            var scope = body.Scope!.Trim();
            var scopeKey = body.ScopeKey ?? "";
            if (AptitudePresetActivation.ScopeToAllocation(scope) is null)
                return Results.BadRequest(new { reason = "presets.scope.unknown" });

            var ctx = BuildAssignContext(store, pid, scope, scopeKey);
            var suggestion = AssignLadder.Suggest(ctx, AptitudePresetTuningHub.Tuning.AssignLadder);

            var presetId = Guid.NewGuid().ToString("N");
            var name = string.IsNullOrWhiteSpace(body.Name)
                ? $"Suggested ({suggestion.RuleId})"
                : body.Name!.Trim();
            var row = new RpgAptitudePresetRow(
                presetId, pid, name, RpgStore.AptitudePresetKindSystemCopy,
                ServerClock.UtcNow.ToString("o"), Revision: 0);
            var entries = suggestion.Rows.Select(r => new RpgAptitudePresetEntryRow(
                presetId, r.AptitudeId, r.TargetPermille, r.MinAbs, r.MaxAbs, r.MinPermille, r.MaxPermille)).ToList();

            var reason = store.SaveAptitudePreset(row, entries, isCreate: true);
            if (reason == "presets.softMax")
                return Results.Conflict(new { reason, softMax = AptitudePresetTuningHub.Tuning.SoftMaxPresets });
            if (!string.IsNullOrEmpty(reason))
                return Results.BadRequest(new { reason });

            _ = BroadcastLibrary(hub, pid);
            var saved = store.GetAptitudePreset(presetId)!;
            return Results.Ok(ProjectPreset(store, saved));
        });

        // S1 — favour is target permille from SpeciesBuildPlanCatalog; empty {} when no plan (S7).
        g.MapGet("/favour/{speciesId}", (string speciesId) =>
        {
            if (string.IsNullOrWhiteSpace(speciesId))
                return Results.BadRequest(new { reason = "species.missing" });
            if (!SpeciesBuildPlanCatalog.IsConfigured)
                return Results.Ok(new { sharesPermille = new Dictionary<string, long>(StringComparer.Ordinal) });

            var shares = SpeciesBuildPlanCatalog.SharesFor(speciesId);
            // Empty catalog entry → {} (not an error). Planned species: values sum 1000.
            return Results.Ok(new { sharesPermille = shares });
        });

        g.MapPost("/activate", (ActivateRequest body, RpgStore store, IPowerIndexProvider powerIndex, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.PresetId))
                return Results.BadRequest(new { reason = "presets.id.missing" });
            if (string.IsNullOrWhiteSpace(body.Scope))
                return Results.BadRequest(new { reason = "presets.scope.missing" });

            var scope = body.Scope!.Trim();
            var scopeKey = body.ScopeKey ?? "";

            // respec-free-counter EP4.10 — the payment choice is the player's, and an unknown spelling is
            // a 400, never a default (`RespecPayments.TryParse`). Parsed at the edge because the spelling
            // is the request's shape; the service takes the enum. (This is the one place the lift moves a
            // refusal earlier: an unknown `payWith` on an otherwise-invalid activate used to be reported
            // after the budget/materialize refusals and is now reported first. Both are 400s naming the
            // same thing, and nothing is written on either path.)
            RespecPayment? payWith = null;
            if (!string.IsNullOrWhiteSpace(body.PayWith))
            {
                if (!RespecPayments.TryParse(body.PayWith, out var parsed))
                    return Results.BadRequest(new { reason = "respec.payment.unknown", payWith = body.PayWith });
                payWith = parsed;
            }

            // Constructed inline rather than injected: this route's test host registers only RpgStore
            // and IPowerIndexProvider, and a non-registered complex parameter would be inferred as a
            // second request body (the same reason ActionLoadoutService is constructed inline at the
            // /api/loadout route). Program.cs still registers the singleton for BP2.3's appliers.
            var activation = new AptitudePresetActivation(store, powerIndex, hub);
            var r = activation.Activate(pid, body.PresetId!, scope, scopeKey, body.CorrelationId, payWith);
            if (!r.Ok)
            {
                if (r.Reason is "presets.notFound") return Results.NotFound();
                // The store's own 409s: the soul price it would pay, and (for the species choice) the
                // free stock it could spend instead — the same two numbers the respec route sends.
                if (r.Reason is "souls.insufficient")
                    return Results.Conflict(new { reason = r.Reason, priceAmount = r.PriceAmount });
                if (r.Reason is "respec.payment.choice-required")
                    return Results.Conflict(new
                    {
                        reason = r.Reason,
                        soulPrice = r.PriceAmount,
                        freeRespecStock = r.FreeStock,
                    });
                if (r.Stage == ActivationStage.BudgetCheck)
                    return Results.Conflict(new { reason = r.Reason, spent = r.Spent, budget = r.CheckBudget });
                if (r.Stage == ActivationStage.Materialize)
                    return Results.Conflict(new { reason = r.Reason });
                if (r.Stage == ActivationStage.Allocation)
                    return Results.BadRequest(new { reason = r.Reason, detail = r.Detail });
                return Results.BadRequest(new { reason = r.Reason });
            }

            return Results.Ok(new
            {
                playerId = pid,
                presetId = body.PresetId,
                scope,
                scopeKey,
                budget = r.Budget,
                shares = r.Shares,
                leftover = r.Leftover,
                priced = r.Priced,
                priceAmount = r.PriceAmount,
                respecCount = r.RespecCount,
                soulBalance = r.SoulBalance,
                replay = r.Replay
            });
        });
    }

    static object ProjectPreset(RpgStore store, RpgAptitudePresetRow p)
    {
        var entries = store.GetAptitudePresetEntries(p.PresetId);
        return new
        {
            presetId = p.PresetId,
            playerId = p.PlayerId,
            name = p.Name,
            kind = p.Kind,
            createdUtc = p.CreatedUtc,
            revision = p.Revision,
            rows = entries.Select(e => new
            {
                aptitudeId = e.AptitudeId,
                targetPermille = e.TargetPermille,
                minAbs = e.MinAbs,
                maxAbs = e.MaxAbs,
                minPermille = e.MinPermille,
                maxPermille = e.MaxPermille
            }).ToList()
        };
    }

    static List<RpgAptitudePresetEntryRow>? ParseEntries(string presetId, List<PresetRowDto>? rows)
    {
        if (rows is null || rows.Count == 0) return null;
        return rows.Select(r => new RpgAptitudePresetEntryRow(
            presetId,
            r.AptitudeId ?? "",
            r.TargetPermille,
            r.MinAbs,
            r.MaxAbs,
            r.MinPermille,
            r.MaxPermille)).ToList();
    }

    /// <summary>EP1.4 — the ladder's own inputs, resolved for `scope`/`scopeKey`. `FavourAllowed` is
    /// structural, not a caller-supplied flag: Mode C (the commander pool) has no species at all
    /// (aptitude-sheet-ideal.md "Mode C — Commander"), so `species-favour` never applies there. The
    /// "primary" aptitude for `SpeciesPosture` is the plan row's own highest-share entry (a tie
    /// breaks on aptitude id, for determinism) — the same fact `CreatureBuildPlanGen`'s
    /// `aptitudePrimary` names at generation time, read back here rather than re-derived a second way.</summary>
    static AssignContext BuildAssignContext(RpgStore store, long playerId, string scope, string scopeKey)
    {
        IReadOnlyList<AptitudePresetRowSpec>? activeRows = null;
        var active = store.GetAptitudePresetActive(playerId, scope, scopeKey);
        if (active is not null)
        {
            var preset = store.GetAptitudePreset(active.PresetId);
            if (preset is not null)
                activeRows = RpgStore.ToRowSpecs(store.GetAptitudePresetEntries(preset.PresetId));
        }

        var favourAllowed = scope != "commander";
        IReadOnlyDictionary<string, long>? favour = null;
        Posture? posture = null;

        if (favourAllowed)
        {
            var speciesId = scope switch
            {
                "unique" => store.GetCreatureProfile(scopeKey)?.SpeciesId,
                "species" => scopeKey,
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(speciesId) && SpeciesBuildPlanCatalog.IsConfigured)
            {
                var shares = SpeciesBuildPlanCatalog.SharesFor(speciesId);
                if (shares.Count > 0)
                {
                    favour = shares;
                    var primaryId = shares
                        .OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                        .First().Key;
                    if (AptitudeCatalog.TryGet(primaryId, out var row))
                        posture = row.Posture;
                }
            }
        }

        return new AssignContext(activeRows, favour, posture, favourAllowed);
    }

    static Task BroadcastLibrary(IHubContext<RpgHub> hub, long playerId) =>
        AptitudeEndpoints.BroadcastBestEffort(
            hub, new AptitudeEndpoints.AptitudesUpdatedDto(playerId, "commander", null, null));

    public sealed class SaveAptitudePresetRequest
    {
        public long? PlayerId { get; set; }
        public string? PresetId { get; set; }
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public List<PresetRowDto>? Rows { get; set; }
    }

    public sealed class PresetRowDto
    {
        public string? AptitudeId { get; set; }
        public long TargetPermille { get; set; }
        public long? MinAbs { get; set; }
        public long? MaxAbs { get; set; }
        public long? MinPermille { get; set; }
        public long? MaxPermille { get; set; }
    }

    public sealed class SetActiveRequest
    {
        public long? PlayerId { get; set; }
        public string? Scope { get; set; }
        public string? ScopeKey { get; set; }
        public string? PresetId { get; set; }
    }

    public sealed class MaterializeRequest
    {
        public long? PlayerId { get; set; }
        public string? PresetId { get; set; }
        public long Budget { get; set; }
    }

    public sealed class ActivateRequest
    {
        public long? PlayerId { get; set; }
        public string? PresetId { get; set; }
        public string? Scope { get; set; }
        public string? ScopeKey { get; set; }
        public string? CorrelationId { get; set; }

        /// <summary>`respec-free-counter` EP4.10 — `"souls"` or `"freeRespec"` when the caller is
        /// activating a SPECIES-scope preset (the one respec that has a choice); absent otherwise.
        /// Parsed at the edge by <see cref="RespecPayments.TryParse"/>.</summary>
        public string? PayWith { get; set; }
    }

    public sealed class SuggestRequest
    {
        public long? PlayerId { get; set; }
        public string? Scope { get; set; }
        public string? ScopeKey { get; set; }
        /// <summary>Omitted: the ladder walks tuning order. Given: only that one rule runs
        /// (<see cref="AssignLadder.TryOne"/>) — what the explicit auto-assign button needs.</summary>
        public string? Rule { get; set; }
    }

    /// <summary>EP1.16 (W4) — `POST /suggested`'s own request: always the ladder's current WALK
    /// (never an explicit rule; that is `/suggest`'s own job), optionally named.</summary>
    public sealed class SuggestedPresetRequest
    {
        public long? PlayerId { get; set; }
        public string? Scope { get; set; }
        public string? ScopeKey { get; set; }
        public string? Name { get; set; }
    }
}
