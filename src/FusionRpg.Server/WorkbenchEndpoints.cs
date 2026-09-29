using System.Text.Json;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// The item program's <b>write</b> surface — the workbench. Deliberately its own file rather than a
/// <c>MapPost</c> inside <c>ItemSurfaceEndpoints.cs</c>: module 20 is read-only by construction, and
/// a write path through the presentation layer is the "second surface" that module exists to prevent.
/// The verbs here belong to modules 14, 15 and 16, and each route is a thin shell over
/// <see cref="ItemWorkbench"/> — no policy, no pricing and no persistence decisions live in this file.
///
/// <para><b><c>correlationId</c> is required, not optional.</b> Every verb is a spend, and a spend
/// without an idempotency key is a double-spend waiting for a network retry. It is the same key
/// <c>rpg_material_spend_log</c> and <c>effect_instance_op</c> are both unique on, so one retried
/// request returns the recorded outcome from both.</para>
/// </summary>
public static class WorkbenchEndpoints
{
    public sealed record SalvageRequest(long? PlayerId, string? InstanceId);

    public sealed record UpcycleRequest(long? PlayerId, string? RecipeId, string? CorrelationId);

    public sealed record ForgeGemRequest(
        long? PlayerId, string? RecipeId, string? InsertContainerId, string? CorrelationId);

    public sealed record ForgeRequest(long? PlayerId, string? RecipeId, string? CorrelationId);

    public sealed record RerollOneRequest(
        long? PlayerId, string? InstanceId, string? RecipeId, int? TargetSeq, string? CorrelationId);

    public sealed record RerollAllRequest(
        long? PlayerId, string? InstanceId, string? RecipeId, IReadOnlyList<int>? TargetSeqs, string? CorrelationId);

    /// <summary>
    /// species-gear-chain T42 — one requested assurance spend, ids and counts, never a flag.
    /// <see cref="MaterialId"/> must be one of the closed `assurance.*` ids; a malformed or
    /// unrecognized line refuses the whole attempt by name at <see cref="ItemWorkbench.Enhance"/>,
    /// never silently drops.
    /// </summary>
    public sealed record AssuranceLineRequest(string? MaterialId, int? Count);

    /// <param name="WardLoaded">
    /// ⛔ <b>Retired (species-gear-chain T39).</b> The free ward this once accepted is gone: the
    /// shipped web client sends the key on every enhance (usually <c>false</c>), so <c>false</c> and an
    /// absent key are accepted and ignored, and only <c>true</c> is refused by name
    /// (<c>enhance.ward-flag-retired</c>). Protection returns in T42 as a debited
    /// <c>assurance.protect</c> cost line — see <see cref="AssuranceLines"/> — never a request flag.
    /// </param>
    /// <param name="AssuranceLines">
    /// species-gear-chain T42 — the real replacement for the retired <paramref name="WardLoaded"/>
    /// flag: how many of which `assurance.*` id to spend on THIS attempt. Absent or empty is legal
    /// (an ordinary, unassured attempt); a malformed or verb-inappropriate line refuses by name.
    /// </param>
    public sealed record EnhanceRequest(
        long? PlayerId, string? InstanceId, string? RecipeId, string? CorrelationId, bool? WardLoaded,
        IReadOnlyList<AssuranceLineRequest>? AssuranceLines = null);

    /// <summary>species-gear-chain T23 — the workbench repair. No flag rides it: D1's destroy chance
    /// and D2's coverage are the attempt's own, read from tuning, never a request field.</summary>
    public sealed record RepairRequest(
        long? PlayerId, string? InstanceId, string? RecipeId, string? CorrelationId);

    /// <summary>species-gear-chain T26 — rarity promotion. Priced by the shipped `elevate` rows, so
    /// the request carries nothing but the item, the recipe and the idempotency key.</summary>
    public sealed record PromoteRequest(
        long? PlayerId, string? InstanceId, string? RecipeId, string? CorrelationId);

    /// <summary>species-gear-chain T37 — the upgrade consumes the input item and produces its authored
    /// successor, so the request carries the instance and the idempotency key and nothing else: the
    /// successor is the base type's own edge, and the price is the shipped `operations.upgrade` souls
    /// leg. No recipeId — this verb has no recipe corpus row.</summary>
    public sealed record UpgradeRequest(
        long? PlayerId, string? InstanceId, string? CorrelationId);

    /// <summary>species-gear-chain T37 — the upgrade's read-only preview: the same decision the verb
    /// makes, nothing spent and nothing consumed. No correlation id: there is nothing to replay.</summary>
    public sealed record UpgradePreviewRequest(long? PlayerId, string? InstanceId);

    public sealed record SocketAddRequest(
        long? PlayerId, string? InstanceId, string? RecipeId, string? CorrelationId);

    public sealed record SocketInsertRequest(
        long? PlayerId, string? InstanceId, string? RecipeId, string? InsertContainerId, int? SocketIndex,
        string? CorrelationId);

    public sealed record SocketImbueRequest(
        long? PlayerId, string? InstanceId, string? RecipeId, int? SocketIndex, string? Element,
        string? CorrelationId);

    /// <summary>
    /// One recipe as a picker offers it — item-content <c>item-naming</c> T4.
    /// </summary>
    /// <param name="Name">The corpus's own authored English name (<c>"Forge: Cloth Armor"</c>), or
    /// <c>""</c> for an entry that authors none. The client shows the recipe id ONLY in the row's
    /// secondary line, never in the name slot.</param>
    public sealed record WorkbenchRecipeDto(
        string RecipeId, string Name, string Operation, string Frame, string OutputKind, string? OutputRef);

    /// <summary>One insert the player actually holds, named — item-content <c>item-naming</c> T4.</summary>
    /// <param name="Name">The gem corpus's authored name (<c>"Ember Shard"</c>), or <c>""</c> for a
    /// container the corpus does not carry. `Element` is <c>""</c> for a genuinely element-free
    /// insert, which is a legitimate value and not an absent read.</param>
    public sealed record WorkbenchInsertDto(string ContainerId, string Name, string Element, long Qty);

    public static void MapWorkbench(this WebApplication app, ItemWorkbench bench)
    {
        if (bench is null) throw new ArgumentNullException(nameof(bench));

        // The bench's OWN gem corpus — the same delegate it prices `socket-insert` against. Read off
        // the bench rather than passed in again: a picker named by a different catalog than the one
        // that prices filling the socket is how two surfaces come to disagree about what a gem is
        // called. `null` serves the held list with empty names, which the client renders as
        // "unnamed" rather than as the container id.
        var lookupInsert = bench.LookupInsert;

        // ⭐ The ONE read in this file, and it is why it is here rather than in the read-only surfaces
        // file: the list a picker offers must be the list the executor below prices against, and
        // `ItemWorkbench.Recipes` is that exact corpus. A recipe read from anywhere else could offer a
        // row the very next POST would refuse with `material.recipe-unknown`.
        //
        // ⏸ Until item-content T4 (2026-09-06) NO route served these 30 rows at all, so the craft and
        // socket benches asked the player to TYPE `recipe.014`. `GET /api/recipes` is the PvZ fusion
        // table and a different thing entirely.
        //
        // P8.5: `frame` is the CONTEXT filter the craft bench actually needs — "what can I run on THIS
        // piece" is a frame question, and it narrows the SAME list rather than adding a second route or
        // making the client re-filter. ⚠ A frame-specific verb must not lose the frame-AGNOSTIC verbs:
        // measured off the shipped corpus, 35 temper + 9 elevate + 5 reroll-one + 2 reroll-all + 1 socket
        // + 1 repair + 1 forge-gem rows are authored `frame: "any"` (the same marker
        // `ComboPricing.cs:639` uses), so matching only `wanted` would empty a humanoid bench of every
        // temper it can actually run. `any` matches every context; a concrete frame matches only
        // itself. The verb filter beside it is unchanged. `outputRef` is deliberately NOT a filter: it
        // is the base type an OUTPUT-producing verb mints (forge), so it is null on every
        // mutation-shaped verb and filtering by it would silently empty a temper picker.
        app.MapGet("/api/items/workbench/recipes", (string? operation, string? frame) =>
        {
            var rows = bench.Recipes.Recipes.Values
                .Where(r => operation is not { Length: > 0 } wanted ||
                            string.Equals(CraftOperations.Id(r.Operation), wanted, StringComparison.Ordinal))
                .Where(r => frame is not { Length: > 0 } wantedFrame ||
                            string.Equals(r.Frame, wantedFrame, StringComparison.Ordinal) ||
                            string.Equals(r.Frame, "any", StringComparison.Ordinal))
                .OrderBy(r => CraftOperations.Id(r.Operation), StringComparer.Ordinal)
                .ThenBy(r => r.RecipeId, StringComparer.Ordinal)
                .Select(r => new WorkbenchRecipeDto(
                    r.RecipeId, r.Name, CraftOperations.Id(r.Operation), r.Frame, r.OutputKind, r.OutputRef))
                .ToList();
            return Results.Ok(rows);
        });

        // The inserts the player actually holds, named — so "set an insert" is a pick, not a typed
        // container id. The `gem.` prefix is the same filter the combinations route already applies to
        // stock (ItemSurfaceEndpoints), not a new rule invented here.
        app.MapGet("/api/items/workbench/inserts/{playerId}", (string playerId, RpgStore store) =>
        {
            var rows = store.ListStock(playerId)
                .Where(s => s.ContainerId.StartsWith("gem.", StringComparison.Ordinal) && s.Qty > 0)
                .OrderBy(s => s.ContainerId, StringComparer.Ordinal)
                .Select(s =>
                {
                    var found = lookupInsert?.Invoke(s.ContainerId);
                    return new WorkbenchInsertDto(
                        s.ContainerId, found?.Name ?? "", found?.Def.Element ?? "", s.Qty);
                })
                .ToList();
            return Results.Ok(rows);
        });

        app.MapPost("/api/items/workbench/salvage", (SalvageRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            return Render(bench.Salvage(body.PlayerId ?? store.GetCurrentPlayerId(), instanceId));
        });

        app.MapPost("/api/items/workbench/upcycle", (UpcycleRequest body, RpgStore store) =>
        {
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.Upcycle(body.PlayerId ?? store.GetCurrentPlayerId(), recipeId, correlationId));
        });

        app.MapPost("/api/items/workbench/forge-gem", (ForgeGemRequest body, RpgStore store) =>
        {
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.InsertContainerId is not { Length: > 0 } insertContainerId)
                return Results.BadRequest(new { error = "insertContainerId required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.ForgeGem(
                body.PlayerId ?? store.GetCurrentPlayerId(), recipeId, insertContainerId, correlationId));
        });

        app.MapPost("/api/items/workbench/forge", (ForgeRequest body, RpgStore store) =>
        {
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.Forge(
                body.PlayerId ?? store.GetCurrentPlayerId(), recipeId, correlationId));
        });

        app.MapPost("/api/items/workbench/reroll-one", (RerollOneRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.TargetSeq is not { } targetSeq)
                return Results.BadRequest(new { error = "targetSeq required — a reroll with no target is a paid no-op" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.RerollOne(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId, recipeId, targetSeq, correlationId));
        });

        app.MapPost("/api/items/workbench/reroll-all", (RerollAllRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.TargetSeqs is not { Count: > 0 } targetSeqs)
                return Results.BadRequest(new { error = "targetSeqs required — a reroll with no target is a paid no-op" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.RerollAll(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId, recipeId, targetSeqs, correlationId));
        });

        app.MapPost("/api/items/workbench/enhance", (EnhanceRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            // species-gear-chain T39 — the free ward is retired. A stale client that still sends
            // `wardLoaded: true` is refused by name (enhance.ward-flag-retired, the registered
            // enhance.* namespace) with no level change, no debit and no op row: protection only ever
            // returns as a debited assurance.protect line (T42), never a request flag. `false` and an
            // absent key are what the shipped FE sends on every enhance — accepted and ignored. The
            // flag is deliberately NOT forwarded to the executor: no request field may reach
            // EnhanceContext.WardLoaded.
            if (body.WardLoaded == true)
                return Render(FreeWardRetired(instanceId, recipeId));

            // species-gear-chain T42: shape-validate the request's assurance lines HERE (a malformed
            // id/count is a request-shape problem, a 400, the same posture every other required-field
            // check on this route already takes) — the DOMAIN checks (verb eligibility, which ids
            // Enhance reads, duplicate coalescing) live in ItemWorkbench.Enhance itself, the one place
            // that also derives WardLoaded/AssureLoaded from this exact list.
            IReadOnlyList<AssuranceLine>? assuranceLines = null;
            if (body.AssuranceLines is { Count: > 0 } requested)
            {
                var parsed = new List<AssuranceLine>(requested.Count);
                foreach (var line in requested)
                {
                    if (line.MaterialId is not { Length: > 0 } id || line.Count is not { } count)
                        return Results.BadRequest(new { error = "each assuranceLines entry needs materialId and count" });
                    parsed.Add(new AssuranceLine(id, count));
                }
                assuranceLines = parsed;
            }

            return Render(bench.Enhance(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId, recipeId, correlationId,
                assuranceLines));
        });

        app.MapPost("/api/items/workbench/repair", (RepairRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.Repair(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId, recipeId, correlationId));
        });

        app.MapPost("/api/items/workbench/promote", (PromoteRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.Promote(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId, recipeId, correlationId));
        });

        app.MapPost("/api/items/workbench/upgrade", (UpgradeRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.Upgrade(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId, correlationId));
        });

        app.MapPost("/api/items/workbench/upgrade-preview", (UpgradePreviewRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            return Results.Ok(bench.UpgradePreview(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId));
        });

        app.MapPost("/api/items/workbench/socket-add", (SocketAddRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.SocketAdd(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId, recipeId, correlationId));
        });

        app.MapPost("/api/items/workbench/socket-insert",
            (SocketInsertRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.InsertContainerId is not { Length: > 0 } insertContainerId)
                return Results.BadRequest(new { error = "insertContainerId required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.SocketInsert(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId, recipeId, insertContainerId,
                body.SocketIndex, correlationId));
        });

        app.MapPost("/api/items/workbench/socket-imbue",
            (SocketImbueRequest body, RpgStore store) =>
        {
            if (body.InstanceId is not { Length: > 0 } instanceId)
                return Results.BadRequest(new { error = "instanceId required" });
            if (body.RecipeId is not { Length: > 0 } recipeId)
                return Results.BadRequest(new { error = "recipeId required" });
            if (body.SocketIndex is not { } socketIndex)
                return Results.BadRequest(new { error = "socketIndex required" });
            if (body.Element is not { Length: > 0 } element)
                return Results.BadRequest(new { error = "element required" });
            if (body.CorrelationId is not { Length: > 0 } correlationId)
                return Results.BadRequest(new { error = "correlationId required — a spend without one is not retry-safe" });
            return Render(bench.SocketImbue(
                body.PlayerId ?? store.GetCurrentPlayerId(), instanceId, recipeId, socketIndex, element,
                correlationId));
        });
    }

    /// <summary>
    /// A refused operation is <b>409 with the named rule</b>, never a 200 carrying a sad face and never
    /// a bare 400: the request was well formed and the answer is "the content rules say no", which is
    /// the same distinction <c>LoadoutEndpoints</c> already draws. The body is identical either way, so
    /// a caller renders one shape.
    /// </summary>
    static IResult Render(WorkbenchOutcomeDto outcome) =>
        outcome.Ok ? Results.Ok(outcome) : Results.Json(outcome, statusCode: StatusCodes.Status409Conflict);

    /// <summary>
    /// species-gear-chain T39: <c>wardLoaded: true</c>'s named refusal, raised through
    /// <see cref="MutationRules"/> so it lands under the registered <c>enhance.*</c> namespace — the
    /// same code family the enhance policy's own refusals use, never a new code. Built here rather than
    /// in the executor because the flag must never reach <c>EnhanceContext</c>: the refusal is a
    /// response to the request's shape, and it writes nothing.
    /// </summary>
    static WorkbenchOutcomeDto FreeWardRetired(string instanceId, string recipeId) =>
        new(false, "enhance",
            MutationRules.Violated("enhance.ward-flag-retired",
                "protection is a debited assurance.protect cost line, not a request flag").ToString(),
            instanceId, recipeId, 0, false, "refused", 0, 0, 0,
            Array.Empty<WorkbenchCostDto>(), Array.Empty<WorkbenchCostDto>(), Array.Empty<WorkbenchSocketDto>());
}

/// <summary>
/// ⏸ <b>A stopgap over module 6's missing <c>item_base_type</c> table</b>, and it says so rather than
/// pretending to be the table. Module 6 shipped the 740-entry base-type corpus as seed JSON and the
/// Core readers, but no table and no loader, so <c>socketMax</c> — a base type's own declared value,
/// which <c>SocketGeometry</c> takes as a parameter and never looks up — has nowhere to come from at
/// runtime. This reads it straight off the shipped corpus at boot.
///
/// <para>The day module 6 lands the table, this class is deleted and the delegate reads the table.
/// It is deliberately a <c>Func</c> at the <see cref="ItemWorkbench"/> boundary so that swap costs one
/// line — the same seam <c>LootContentView.SocketMaxFor</c> already uses.</para>
/// </summary>
public static class BaseTypeSocketMaxCorpus
{
    /// <summary>
    /// Returns <c>null</c> for an id the corpus does not carry, which the workbench refuses by name.
    /// An absent directory yields a lookup that always returns <c>null</c> — the socket verbs then
    /// refuse rather than guessing, which is <c>LootPipeline.Sockets</c>'s own rule.
    ///
    /// <para>strain-splice-host SSH1.6 (host-gate §5) — <b>the one place the runtime reads this
    /// trusts the file no further than <see cref="SocketGeometry.ValidateEntry"/> already lets it</b>.
    /// The corpus validator (<c>gk-forge/tools/ItemSeedValidator/Checks/SocketMaxCheck.cs</c>) refuses a base
    /// row above its role ceiling at AUTHORING time; this runtime lookup previously trusted the file
    /// regardless, so a corrupt or hand-edited row could still be bored past its role's own ceiling.
    /// A row with no parseable role, or whose <c>socketMax</c> exceeds its role's ceiling, is dropped
    /// here — the same generic refusal <see cref="SocketRules.EntryExceedsRoleCeiling"/> already
    /// names, never a new rule id and never a role-specific branch. The lookup then returns
    /// <c>null</c> for that id exactly as it already does for an id the corpus never carried, and the
    /// workbench's own existing <c>socket.base-type-socket-max-unavailable</c> refusal covers it —
    /// no new code downstream.</para>
    /// </summary>
    public static Func<string, int?> Load(string baseTypesDir, SocketTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var byId = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!Directory.Exists(baseTypesDir)) return _ => null;

        foreach (var file in Directory
                     .EnumerateFiles(baseTypesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    if (!entry.TryGetProperty("socketMax", out var max) || max.ValueKind != JsonValueKind.Number)
                        continue;
                    if (!entry.TryGetProperty("role", out var roleEl) || roleEl.ValueKind != JsonValueKind.String)
                        continue;
                    if (!ItemRoles.TryParse(roleEl.GetString(), out var role)) continue;

                    var socketMax = max.GetInt32();
                    if (!SocketGeometry.ValidateEntry(role, socketMax, tuning).IsOk) continue;

                    byId[id.GetString()!] = socketMax;
                }
            }
        }

        return baseTypeId => byId.TryGetValue(baseTypeId, out var value) ? value : null;
    }

    /// <summary>An explicit lookup for tests and for a host with no corpus on disk.</summary>
    public static Func<string, int?> From(IReadOnlyDictionary<string, int> byId) =>
        baseTypeId => byId.TryGetValue(baseTypeId, out var value) ? value : null;

    /// <summary>
    /// species-gear-chain T37 — the base-type registry's own <c>implicit.family</c> per id, read off
    /// the SAME seed JSON <c>socketMax</c> and <c>class</c> come from. It is the one fact the upgrade's
    /// card needs and no store table carries (<c>GetBaseType</c> returns frame and role only), because
    /// "the implicit change is shown before the input is consumed" is meaningless without it. Returns
    /// <c>null</c> for an id the corpus does not carry: the caller then shows no implicit rather than
    /// inventing one.
    /// </summary>
    public static Func<string, string?> LoadImplicitFamilyById(string baseTypesDir)
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(baseTypesDir)) return _ => null;

        foreach (var file in Directory
                     .EnumerateFiles(baseTypesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    if (!entry.TryGetProperty("implicit", out var implicitEl) ||
                        implicitEl.ValueKind != JsonValueKind.Object) continue;
                    if (!implicitEl.TryGetProperty("family", out var family) ||
                        family.ValueKind != JsonValueKind.String) continue;
                    byId[id.GetString()!] = family.GetString()!;
                }
            }
        }

        return baseTypeId => byId.TryGetValue(baseTypeId, out var value) ? value : null;
    }

    /// <summary>
    /// species-gear-chain T24 — the base-type registry's own <c>class</c> per id, read off the SAME
    /// seed JSON <c>socketMax</c> comes from. It is the one input <c>DurabilityTable.DeriveMax</c>
    /// needs beyond the rung and the tuning, so craft wear can derive an item's <c>durability_max</c>
    /// without a new table (module 6's own deferred one). Returns <c>null</c> for an id the corpus does
    /// not carry: an unknown base type then derives no max, and `CraftWearFor` leaves the pair alone
    /// rather than inventing a number.
    /// </summary>
    public static Func<string, string?> LoadClassById(string baseTypesDir)
    {
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(baseTypesDir)) return _ => null;

        foreach (var file in Directory
                     .EnumerateFiles(baseTypesDir, "*.json", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(file)); }
            catch (JsonException) { continue; }

            using (doc)
            {
                if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                    entries.ValueKind != JsonValueKind.Array) continue;

                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                    if (!entry.TryGetProperty("class", out var cls) || cls.ValueKind != JsonValueKind.String)
                        continue;
                    byId[id.GetString()!] = cls.GetString()!;
                }
            }
        }

        return baseTypeId => byId.TryGetValue(baseTypeId, out var value) ? value : null;
    }
}
