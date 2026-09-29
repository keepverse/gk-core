using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

/// <summary>Creature reads + specimen edits (spec-creature-core.md). Summoning arrives with its own module.</summary>
public static class CreatureEndpoints
{
    public static void MapCreatures(this WebApplication app)
    {
        var g = app.MapGroup("/api/creatures");

        // Species + trait catalogs (code-authored/generated — same payload for every player).
        g.MapGet("/catalog", () => Results.Ok(new
        {
            species = CreatureSpeciesCatalog.All.Select(s => new
            {
                speciesId = s.SpeciesId,
                name = s.Name,
                side = s.Side,
                gameTypeId = s.GameTypeId,
                creatureTypeId = s.CreatureTypeId,
                elementPrimary = s.ElementPrimary.ToElementId(),
                elementSecondary = s.ElementSecondary?.ToElementId(),
                rarity = s.BaseRarity.ToId(),
                // spec-species-rank.md §5: rank id + display name ride the catalog row, copied off
                // the catalog entry itself (never re-derived, never defaulted — null when skipped).
                rank = s.Rank?.ToId(),
                rankDisplayName = s.Rank?.ToDisplayName(),
                deployMode = s.DeployMode == CreatureDeployMode.HypnoAlly ? "hypno-ally" : "plant-avatar",
                // lawn LW1.2: the ADMITTED answer, not the raw `Summonable` flag. A species the build
                // marks out of play (`speciesKind: "excluded"`) still carries Summonable in the corpus,
                // so reporting the raw flag advertised species no summon could ever deliver — the same
                // one-declaring-site rule `CreatureAdmission` states, applied to the surface a player
                // reads before spending souls.
                summonable = CreatureAdmission.ForWave(s),
                captureOnly = s.Acquisition == CreatureAcquisition.CaptureOnly,
                variants = s.Variants,
                traitPool = s.TraitPool
            }),
            traits = CreatureTraitCatalog.All.Select(t => new
            {
                traitId = t.TraitId,
                name = t.Name,
                blurb = t.Blurb
            })
        }));

        g.MapGet("/{playerId:long}", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(store.ListCreatureRoster(playerId));
        });

        g.MapGet("/{playerId:long}/codex", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            return Results.Ok(store.ListCreatureCodex(playerId));
        });

        // Summon state: banners, costs, today's focus, and the player's visible pity counters.
        g.MapGet("/{playerId:long}/summon-state", (long playerId, RpgStore store) =>
        {
            if (!store.PlayerExists(playerId)) return Results.NotFound();
            var pity = store.GetSummonPity(playerId);
            var focus = SummonBannerCatalog.FocusFor(DateOnly.FromDateTime(ServerClock.UtcNowDateTime));
            return Results.Ok(new
            {
                banners = SummonBannerCatalog.All.Select(b => new
                {
                    bannerId = b.BannerId,
                    costPerPull = b.CostPerPull,
                    costPerTen = b.CostPerTen,
                    focusElement = b.HasElementFocus ? focus.ToElementId() : null
                }),
                pity = new
                {
                    pullsSinceHeirloom = pity.PullsSinceHeirloom,
                    pullsSinceSunwoven = pity.PullsSinceSunwoven,
                    heirloomGuaranteeAt = SummonRoller.HeirloomHardPity,
                    sunwovenGuaranteeAt = SummonRoller.SunwovenHardPity
                },
                balance = store.GetSoulBalance(playerId)
            });
        });

        g.MapPost("/summon", async (SummonRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(body.CorrelationId))
                return Results.BadRequest(new { reason = "correlation.missing" });
            if (body.CorrelationId.Trim().Length > 64) // a UUID is 36; dedupe rows are permanent
                return Results.BadRequest(new { reason = "correlation.toolong" });

            // Server-authoritative: focus resolved from today's rotation, seed minted here and recorded.
            var focus = SummonBannerCatalog.FocusFor(DateOnly.FromDateTime(ServerClock.UtcNowDateTime)).ToElementId();
            var seed = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0);
            var (ok, reason, outcome) = store.ExecuteSummon(
                pid, body.BannerId ?? SummonBannerCatalog.StandardRift, body.Count, body.CorrelationId!, seed, focus);
            if (!ok)
            {
                return reason is "souls.insufficient"
                    ? Results.Conflict(new { reason, balance = outcome?.Balance })
                    : Results.BadRequest(new { reason });
            }

            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("SoulsUpdated", new { playerId = pid });
            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("CreaturesUpdated", new { playerId = pid });
            // creature-lawn-deploy T2.1: a new specimen changes the plant-side deploy roster — mirrors
            // CommanderEndpoints.cs's own WebGroup+InjectorGroup pair for CommandersUpdated, since
            // CreaturesUpdated previously reached only the web frontend and never the injector's own
            // session cache (RpgClient.RefreshLawnDeployRosterCacheAsync).
            await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("CreaturesUpdated", new { playerId = pid });
            return Results.Ok(new
            {
                replayed = outcome!.Replayed,
                specimens = outcome.Specimens,
                pity = new { pullsSinceHeirloom = outcome.Pity.PullsSinceHeirloom, pullsSinceSunwoven = outcome.Pity.PullsSinceSunwoven },
                balance = outcome.Balance,
                discoverySouls = outcome.DiscoverySouls
            });
        });

        // Debug-only mint: bypasses gacha RNG/cost entirely, for test coverage that needs a specific
        // species on a live roster (e.g. proving a dual-typed creature's live elementPayload) without
        // gambling the player's real souls on it. Reuses the same MintCreature atomic path every other
        // mint source (summon, fusion, capture, delve) already goes through — never a second mint
        // implementation, matching this file's own "one summon module" discipline.
        g.MapPost("/debug/grant", async (DebugGrantRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            var speciesId = (body.SpeciesId ?? "").Trim();
            if (!CreatureSpeciesCatalog.IsKnown(speciesId))
                return Results.BadRequest(new { reason = "species.unknown" });
            var species = CreatureSpeciesCatalog.Get(speciesId);
            var (specimen, newlyDiscovered) = store.MintCreature(pid, new CreatureMintSpec
            {
                SpeciesId = species.SpeciesId,
                Side = species.Side,
                GameTypeId = species.GameTypeId,
                Rarity = species.BaseRarity.ToId(),
                Variant = "normal",
                ElementPrimary = species.ElementPrimary.ToElementId(),
                ElementSecondary = species.ElementSecondary?.ToElementId(),
                TraitIds = new List<string>(),
                Origin = "debug"
            });
            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("CreaturesUpdated", new { playerId = pid });
            await hub.Clients.Group(RpgConstants.InjectorGroup).SendAsync("CreaturesUpdated", new { playerId = pid });
            return Results.Ok(new { specimen, newlyDiscovered });
        });

        // Debug-only: compile atoms for exactly this one UniqueActor owner and hand back the raw
        // grants, so F3.2's (combat-unification Phase 7) baked-in hybrid elementPayload can be
        // observed against the REAL live store/catalog for a REAL specimen, without needing that
        // specimen actually deployed onto the currently-running lawn board. Calls the same
        // AtomPushService.Build production path every real push already goes through — never a
        // second compile — just against a one-owner scope instead of a whole player's union.
        g.MapGet("/debug/atoms-preview/{instanceId}", (string instanceId, RpgStore store) =>
        {
            var actor = store.GetUniqueActor(instanceId);
            if (actor is null) return Results.NotFound();
            var owner = new OwnerScope(OwnerKind.UniqueActor, instanceId);
            var resolution = store.ResolveBindings(owner, new BindContext(RuntimeId.Lawn));
            var push = new AtomPushService(store).Build(owner, new BindContext(RuntimeId.Lawn), matchSeed: 0);
            return Results.Ok(new
            {
                instanceId,
                grantCount = push.Grants.Count,
                grants = push.Grants.Select(gr => new
                {
                    gr.GrantId,
                    gr.EffectId,
                    elementPayload = gr.Overlay != null && gr.Overlay.TryGetValue("elementPayload", out var ep) ? ep : null
                }),
                acceptedBindings = resolution.Bindings.Count,
                refused = resolution.Refused.Select(r => new { r.BindingId, reason = r.Reason.ToString(), r.Detail })
            });
        });

        // Debug-only: a BARE UniqueActor (no creature profile) at a given side+gameTypeId, deployed with
        // a synthetic ptr — exactly AtomPushServiceInstanceOwnerRewriteTests's own
        // CreateUniqueActor + TryBeginUniqueDeploy + TryAckUniqueSpawn sequence, now against the real
        // live store. No creature profile means TryBeginUniqueDeploy skips the creature-contracts gate
        // entirely (that gate only fires when ReadCreatureProfileUnlocked finds a row), so this never
        // touches the real player's contract-slot/loyalty state. OwnerElements (AtomPushService.cs)
        // only reads Side/TypeId via LawnElementIndex, never the creature profile, so this is enough to
        // prove the real hybrid-typing wiring without a real Unity-spawned entity.
        g.MapPost("/debug/spawn-unique-actor", (SpawnUniqueActorRequest body, RpgStore store) =>
        {
            var pid = body.PlayerId ?? store.GetCurrentPlayerId();
            if (!store.PlayerExists(pid)) return Results.NotFound();
            var actor = store.CreateUniqueActor(pid, body.Side ?? "plant", body.GameTypeId);

            var corr = "debug-" + Guid.NewGuid().ToString("N");
            var begin = store.TryBeginUniqueDeploy(actor.InstanceId, corr);
            if (!begin.Ok) return Results.BadRequest(new { reason = "begin." + begin.Reason });

            var ptr = "DEBUG" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
            var ack = store.TryAckUniqueSpawn(corr, ptr);
            if (!ack.Ok) return Results.BadRequest(new { reason = "ack." + ack.Reason });

            return Results.Ok(new { instanceId = actor.InstanceId, ptr, actor = ack.Actor });
        });

        // Debug-only: bind one fixed, idempotent damage-dealing test atom (kind "resource.delta",
        // action ApplyResourceDelta) directly to a UniqueActor, bypassing the whole item-roll/equip
        // pipeline. A freshly minted specimen carries zero bound atoms, so F3.1's hybrid default has
        // nothing to bake an elementPayload into until SOMETHING with ApplyResourceDelta is bound —
        // this is that seam, reusing the exact Upsert/Instantiate/Bind primitives
        // AtomPushServiceInstanceOwnerRewriteTests already proves at the unit level, now against the
        // real live store so /debug/atoms-preview has something real to show.
        g.MapPost("/debug/grant-test-atom/{instanceId}", (string instanceId, RpgStore store) =>
        {
            var actor = store.GetUniqueActor(instanceId);
            if (actor is null) return Results.NotFound();

            const string family = "atom.debug-hybrid-proof";
            const string containerId = "item.debug-hybrid-proof";
            var atomId = AtomRow.DeriveId(family, "", 1);
            store.UpsertAtom(new AtomRow
            {
                AtomId = atomId,
                KindId = "resource.delta",
                FamilyId = family,
                Variant = "",
                Tier = 1,
                Name = "Debug hybrid-typing proof",
                ParamsJson = "{\"channel\":\"hp\",\"amount\":-100}",
                WhenJson = "{\"trigger\":\"" + EffectTriggers.OnDamageDealt + "\"}",
            });
            store.UpsertContainer(new ContainerRow
            {
                ContainerId = containerId,
                Kind = ContainerKind.Item,
                Atoms = new[] { new ContainerAtomRow(1, atomId) },
            });

            var container = store.GetContainer(containerId)!;
            var atoms = store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
            var result = Instantiator.TryInstantiate(
                container, id => atoms.TryGetValue(id, out var a) ? a : null,
                store.GetAffix, 1, 20, PowerTuningHub.Tuning, out var inst);
            if (!result.IsOk) return Results.BadRequest(new { reason = result.ToString() });
            var rolledInstanceId = store.SaveInstance(inst!);

            var bind = store.Bind(new BindingRow
            {
                InstanceId = rolledInstanceId,
                OwnerKind = OwnerKind.UniqueActor,
                OwnerKey = instanceId,
                Slot = "armament-primary",
                Source = "debug",
            });
            if (!bind.IsOk) return Results.BadRequest(new { reason = bind.ToString() });

            return Results.Ok(new { instanceId, atomId, rolledInstanceId, bound = true });
        });

        g.MapPost("/specimen/{instanceId}/nickname", async (
            string instanceId, NicknameRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var nickname = body.Nickname?.Trim();
            if (nickname is { Length: > 32 })
                return Results.BadRequest(new { reason = "nickname.toolong" });
            var (ok, profile) = store.SetCreatureNickname(instanceId, nickname);
            if (!ok) return Results.NotFound();
            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("CreaturesUpdated", new { instanceId });
            return Results.Ok(profile);
        });

        g.MapPost("/specimen/{instanceId}/lock", async (
            string instanceId, LockRequest body, RpgStore store, IHubContext<RpgHub> hub) =>
        {
            var (ok, profile) = store.SetCreatureLocked(instanceId, body.Locked);
            if (!ok) return Results.NotFound();
            await hub.Clients.Group(RpgConstants.WebGroup).SendAsync("CreaturesUpdated", new { instanceId });
            return Results.Ok(profile);
        });
    }

    public sealed class SummonRequest
    {
        public long? PlayerId { get; set; }
        public string? BannerId { get; set; }
        public int Count { get; set; } = 1;
        public string? CorrelationId { get; set; }
    }

    public sealed class DebugGrantRequest
    {
        public long? PlayerId { get; set; }
        public string? SpeciesId { get; set; }
    }

    public sealed class SpawnUniqueActorRequest
    {
        public long? PlayerId { get; set; }
        public string? Side { get; set; }
        public int GameTypeId { get; set; }
    }

    public sealed class NicknameRequest
    {
        public string? Nickname { get; set; }
    }

    public sealed class LockRequest
    {
        public bool Locked { get; set; }
    }
}
