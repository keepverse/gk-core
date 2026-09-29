using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Fusion;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Creatures.Materialise;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Derived;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public static class FusionModes
{
    public const string StarMerge = "star-merge";
    public const string Promotion = "promotion";
    public const string Recipe = "recipe";
}

/// <summary>WAVE F2.4 (creature-standalone, 2026-09-07): one player-selected inheritance pick — an atom
/// a fusion output should carry verbatim from one of its two sacrifices' own materialised roll
/// (F2.2). <see cref="SourceInstanceId"/> must name one of the request's own
/// <see cref="FusionRequest.SacrificeInstanceIds"/>; <see cref="AtomId"/> must be an atom that
/// specimen's own roll actually contains — both checked by name in <c>RecipeUnlocked</c>.</summary>
public sealed record FusionPick(string SourceInstanceId, string AtomId);

public sealed record FusionRequest(
    string Mode,
    string? BaseInstanceId,
    IReadOnlyList<string> SacrificeInstanceIds,
    string? PickedTraitId,
    IReadOnlyList<FusionPick>? Picks = null);

public sealed record FusionOutcome(
    bool Replayed,
    string Mode,
    CreatureSpecimenDto? Base,
    CreatureSpecimenDto? Minted,
    string? RecipeId,
    bool NewlyDiscovered,
    long DiscoverySouls,
    SoulBalanceDto Balance);

public sealed record CreatureLineageRow(long Id, string InstanceId, string Event, string DetailJson, string T);

public sealed partial class RpgStore
{
    /// <summary>Test-only: throws after sacrifices are consumed to prove transaction atomicity.</summary>
    internal Action? FusionMidTestHook;

    /// <summary>
    /// The fusion transaction (spec-creature-fusion.md): replay-check → validate → spend Souls +
    /// materials → consume sacrifices → mutate base or mint output → lineage → log — ONE
    /// gate-serialized transaction, refusals write nothing (ExecuteSummon discipline).
    /// </summary>
    public (bool Ok, string Reason, FusionOutcome? Outcome) ExecuteFusion(
        long playerId, string correlationId, FusionRequest request, ulong seed)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return (false, "correlation.missing", null);
        var corr = correlationId.Trim();

        // Normalize ids ONCE — actor reads trim but the is-base/duplicate guards compare strings,
        // so an untrimmed " abc " base could pass its own trimmed id as a sacrifice and consume
        // itself (2026-08-21 review, Important 1). One canonical form kills the class.
        request = request with
        {
            BaseInstanceId = string.IsNullOrWhiteSpace(request.BaseInstanceId)
                ? null
                : request.BaseInstanceId.Trim(),
            SacrificeInstanceIds = request.SacrificeInstanceIds
                .Select(s => (s ?? "").Trim())
                .ToList()
        };

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown", null);
            using var tx = db.BeginTransaction();
            var now = ServerClock.UtcNowDateTime.ToString("o");

            // Replay: validate the stored request, rebuild the outcome from current rows,
            // spend nothing (the atomic log row is the gate — no check-then-act window).
            var stored = ReadFusionLogUnlocked(db, playerId, corr);
            if (stored != null)
            {
                var storedRequest = JsonSerializer.Deserialize<FusionRequest>(stored.Value.InputsJson);
                if (storedRequest is null || !SameRequest(storedRequest, request))
                    return (false, "correlation.mismatch", null);
                tx.Commit();
                return (true, "replay", RebuildOutcomeUnlocked(db, playerId, stored.Value));
            }

            var result = request.Mode switch
            {
                FusionModes.StarMerge => StarMergeUnlocked(db, playerId, corr, request, seed, now),
                FusionModes.Promotion => PromotionUnlocked(db, playerId, corr, request, seed, now),
                FusionModes.Recipe => RecipeUnlocked(db, playerId, corr, request, seed, now),
                _ => (false, "mode.unknown", null)
            };

            if (!result.Ok)
                return result; // tx disposes → rollback; refusals write nothing

            tx.Commit();
            return result;
        }
    }

    (bool Ok, string Reason, FusionOutcome? Outcome) StarMergeUnlocked(
        SqliteConnection db, long playerId, string corr, FusionRequest request, ulong seed, string now)
    {
        var (ok, reason, baseActor, baseProfile, baseRarity) =
            ReadFusionBaseUnlocked(db, playerId, request.BaseInstanceId);
        if (!ok) return (false, reason, null);

        var targetStar = baseProfile!.Star + 1;
        if (targetStar > StarPolicy.StarCap(baseRarity))
            return (false, "star.maxed", null);
        var validation = ValidateSacrificesUnlocked(
            db, playerId, request, StarPolicy.SacrificesForStar(targetStar), requireRarity: baseRarity);
        if (validation != "") return (false, validation, null);

        var cost = FusionCostTable.StarMerge(baseRarity);
        var spendReason = SpendFusionCostsUnlocked(db, playerId, corr, cost, baseProfile.ElementPrimary, now);
        if (spendReason != "") return (false, spendReason, null);

        ConsumeSacrificesUnlocked(db, request.SacrificeInstanceIds, baseActor!.InstanceId, now);
        FusionMidTestHook?.Invoke();

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE rpg_creature_profiles SET star = $s, revision = revision + 1
                WHERE instance_id = $id;
                """;
            cmd.Parameters.AddWithValue("$s", targetStar);
            cmd.Parameters.AddWithValue("$id", baseActor.InstanceId);
            cmd.ExecuteNonQuery();
        }

        AppendLineageUnlocked(db, baseActor.InstanceId, "star-merge",
            JsonSerializer.Serialize(new { star = targetStar, sacrifices = request.SacrificeInstanceIds }), now);
        // Merges consume no randomness, but the log records the seed the caller actually sent —
        // an auditor diffing the log against request traces must see the truth.
        AppendFusionLogUnlocked(db, playerId, corr, FusionModes.StarMerge, request,
            JsonSerializer.Serialize(new { baseInstanceId = baseActor.InstanceId, star = targetStar }), seed, now);

        return (true, "", new FusionOutcome(
            false, FusionModes.StarMerge,
            ReadSpecimenUnlocked(db, baseActor.InstanceId), null, null, false, 0,
            ReadSoulBalanceUnlocked(db, playerId)));
    }

    (bool Ok, string Reason, FusionOutcome? Outcome) PromotionUnlocked(
        SqliteConnection db, long playerId, string corr, FusionRequest request, ulong seed, string now)
    {
        var (ok, reason, baseActor, baseProfile, baseRarity) =
            ReadFusionBaseUnlocked(db, playerId, request.BaseInstanceId);
        if (!ok) return (false, reason, null);
        if (request.SacrificeInstanceIds.Count != 0) return (false, "sacrifices.count", null);
        if (!StarPolicy.CanPromote(baseRarity, baseProfile!.Star, baseProfile.Promoted))
            return (false, "promotion.not-ready", null);
        // spec-species-rank.md §6: the rank floor lands BESIDE CanPromote, never replacing it —
        // rarity/star/promoted still decide first, and this can only ever narrow further. At the
        // shipped bottom rung it is a pass-through (zero golden moves); a species with no rank (the
        // pipeline skipped it, or the catalog does not know the id) maps to the bottom rung AT THIS
        // GATE, so the ladder never sees a null. Never recomputed on promotion: rank is
        // species-identity, promotion is specimen-state — the rank row is not touched below.
        if (!CreatureRankFloors.Passes(
                CreatureRankFloors.FusionPromotion, CreatureSpeciesCatalog.Get(baseProfile.SpeciesId)?.Rank))
            return (false, "promotion.rank-floor", null);

        var newRarity = CreatureRarityLadder.OneRungAbove(baseRarity);
        var cost = FusionCostTable.Promotion(newRarity);
        var spendReason = SpendFusionCostsUnlocked(db, playerId, corr, cost, baseProfile.ElementPrimary, now);
        if (spendReason != "") return (false, spendReason, null);
        FusionMidTestHook?.Invoke();

        var species = CreatureSpeciesCatalog.Get(baseProfile.SpeciesId);
        var traits = FusionRoller.RollPromotionTraits(
            species, baseProfile.TraitIds, FusionRoller.SlotsFor(newRarity), seed);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE rpg_creature_profiles SET
                  rarity = $r, star = 0, promoted = 1, traits_json = $traits, revision = revision + 1
                WHERE instance_id = $id;
                """;
            cmd.Parameters.AddWithValue("$r", newRarity.ToId());
            cmd.Parameters.AddWithValue("$traits", JsonSerializer.Serialize(traits));
            cmd.Parameters.AddWithValue("$id", baseActor!.InstanceId);
            cmd.ExecuteNonQuery();
        }

        AppendLineageUnlocked(db, baseActor.InstanceId, "promotion",
            JsonSerializer.Serialize(new { from = baseRarity.ToId(), to = newRarity.ToId() }), now);
        AppendFusionLogUnlocked(db, playerId, corr, FusionModes.Promotion, request,
            JsonSerializer.Serialize(new { baseInstanceId = baseActor.InstanceId, rarity = newRarity.ToId() }),
            seed, now);

        return (true, "", new FusionOutcome(
            false, FusionModes.Promotion,
            ReadSpecimenUnlocked(db, baseActor.InstanceId), null, null, false, 0,
            ReadSoulBalanceUnlocked(db, playerId)));
    }

    (bool Ok, string Reason, FusionOutcome? Outcome) RecipeUnlocked(
        SqliteConnection db, long playerId, string corr, FusionRequest request, ulong seed, string now)
    {
        // Recipe mode has no base — refusing a stray base id beats silently ignoring a creature the
        // caller thought was participating (2026-08-21 review S4).
        if (request.BaseInstanceId != null) return (false, "base.unexpected", null);
        var validation = ValidateSacrificesUnlocked(db, playerId, request, requiredCount: 2, requireRarity: null);
        if (validation != "") return (false, validation, null);

        var profileA = ReadCreatureProfileUnlocked(db, request.SacrificeInstanceIds[0])!;
        var profileB = ReadCreatureProfileUnlocked(db, request.SacrificeInstanceIds[1])!;
        var recipe = CreatureRecipeCatalog.TryMatch(profileA.SpeciesId, profileB.SpeciesId);
        if (recipe is null) return (false, "recipe.unknown", null);

        var combinedTraits = profileA.TraitIds.Concat(profileB.TraitIds)
            .Distinct(StringComparer.Ordinal).ToList();
        if (string.IsNullOrWhiteSpace(request.PickedTraitId)) return (false, "trait.missing", null);
        if (!combinedTraits.Contains(request.PickedTraitId, StringComparer.Ordinal))
            return (false, "trait.not-on-inputs", null);

        var output = CreatureSpeciesCatalog.Get(recipe.OutputSpeciesId);

        // WAVE F2.4 (creature-standalone, 2026-09-07): player-selected inheritance picks — validated
        // and priced before any spend, matching this method's own established refusal order.
        var picks = request.Picks ?? Array.Empty<FusionPick>();
        IReadOnlyList<ForcedPoolPick> forcedPicks = Array.Empty<ForcedPoolPick>();
        long picksSouls = 0;
        IReadOnlyList<InstanceAtomRow>? sourceAtomsA = null;
        IReadOnlyList<InstanceAtomRow>? sourceAtomsB = null;
        ContainerRow? outputSpeciesContainer = null;
        // G3: layer 1b belongs to the paying EMPIRE of the save, never to the person. R17: this path's
        // player id IS the save.
        var fusionOwner = new Core.Saves.EmpireRef(
            new Core.Saves.SaveId(playerId), HumanEmpireOfOrNull(db, playerId) ?? throw new SaveEmpiresNotSeeded(playerId));

        if (picks.Count > 0)
        {
            // The first-fusion rule, restated on the LEDGER (SP0.3, spec-species-mod-ledger.md behaviour 3):
            // a `fusion-pick` row for (save, paying empire, output species) means that empire already holds
            // a 1b roll for the species, so injecting picks would silently re-roll it out from under every
            // other specimen of that species it owns. The refusal string does not change.
            foreach (var mod in ListSpeciesModsUnlocked(db, fusionOwner))
            {
                if (string.Equals(mod.SpeciesId, output.SpeciesId, StringComparison.Ordinal))
                    return (false, "picks.already-materialised", null);
            }

            var slotCap = FusionRoller.SlotsFor(output.BaseRarity);
            if (picks.Count > slotCap) return (false, "picks.exceeds-slots", null);

            outputSpeciesContainer = GetContainer("species-passive." + output.SpeciesId);
            if (outputSpeciesContainer is null) return (false, "picks.no-target-container", null);

            var resolvedAtoms = new List<InstanceAtomRow>();
            foreach (var pick in picks)
            {
                IReadOnlyList<InstanceAtomRow> sourceAtoms;
                CreatureProfileDto sourceProfile;
                if (pick.SourceInstanceId == request.SacrificeInstanceIds[0])
                {
                    sourceAtoms = sourceAtomsA ??= PickSourceAtomsUnlocked(db, fusionOwner, profileA.SpeciesId);
                    sourceProfile = profileA;
                }
                else if (pick.SourceInstanceId == request.SacrificeInstanceIds[1])
                {
                    sourceAtoms = sourceAtomsB ??= PickSourceAtomsUnlocked(db, fusionOwner, profileB.SpeciesId);
                    sourceProfile = profileB;
                }
                else
                {
                    return (false, "picks.source-not-a-sacrifice", null);
                }

                // The same function the preview endpoint calls, so the atoms offered are exactly the atoms
                // accepted. A species with no container has no source and still reaches this code.
                if (sourceAtoms.Count == 0) return (false, "picks.source-not-materialised", null);

                var atom = sourceAtoms.FirstOrDefault(a => a.AtomId == pick.AtomId);
                if (atom is null) return (false, "picks.atom-not-rolled", null);
                resolvedAtoms.Add(atom);

                if (!CreatureRarityIds.TryParse(sourceProfile.Rarity, out var sourceRarity))
                    return (false, "picks.source-rarity-unknown", null);
                // InheritCostByRarity only covers OutputEligibilityFloor-and-above (Cultivated..
                // Almanac, the same rung set RecipeCost itself covers) — a below-floor source has no
                // entry, and FusionCostTable.InheritPick throws rather than guessing a price. Guard
                // here so a below-floor pick is a named refusal, never an unhandled exception.
                if (!CreatureRarityLadder.AtLeast(sourceRarity, CreatureRecipeCatalog.OutputEligibilityFloor))
                    return (false, "picks.source-below-inherit-floor", null);
                // spec-species-rank.md §6: the rank floor lands BESIDE the rarity floor above, and
                // reads the SOURCE species' own rank — a null (skipped) rank maps to bottom here.
                // The endpoint's preview consults the same policy for the same gate, so it never
                // offers a pick this line would refuse.
                if (!CreatureRankFloors.Passes(CreatureRankFloors.FusionRecipeEligibility,
                        CreatureSpeciesCatalog.Get(sourceProfile.SpeciesId)?.Rank))
                    return (false, "picks.source-below-rank-floor", null);
                picksSouls += FusionCostTable.InheritPick(sourceRarity);
            }

            forcedPicks = resolvedAtoms
                .Select(a => new ForcedPoolPick(a.AtomId, new[] { a }))
                .ToList();
        }

        var cost = FusionCostTable.Recipe(output.BaseRarity);
        if (picksSouls > 0) cost = cost with { Souls = cost.Souls + picksSouls };
        var spendReason = SpendFusionCostsUnlocked(
            db, playerId, corr, cost, output.ElementPrimary.ToElementId(), now);
        if (spendReason != "") return (false, spendReason, null);

        ConsumeSacrificesUnlocked(db, request.SacrificeInstanceIds, "recipe:" + recipe.RecipeId, now);
        FusionMidTestHook?.Invoke();

        var roll = FusionRoller.Roll(output, output.BaseRarity, request.PickedTraitId!, combinedTraits, seed);
        var minted = MintCreatureUnlocked(db, playerId, new CreatureMintSpec
        {
            SpeciesId = output.SpeciesId,
            Side = output.Side,
            GameTypeId = output.GameTypeId,
            Rarity = output.BaseRarity.ToId(),
            Variant = roll.Variant,
            ElementPrimary = output.ElementPrimary.ToElementId(),
            ElementSecondary = output.ElementSecondary?.ToElementId(),
            TraitIds = roll.TraitIds.ToList(),
            Origin = "fusion"
        }, now, out var speciesNewlyDiscovered);

        // WAVE F2.4 / SP0.3: honor the picks by writing the output species' 1b payload RIGHT NOW —
        // effect_instance + effect_instance_atom, then ONE rpg_player_species_mod ledger row (below) —
        // forcing them into the roll. The ledger row is what the guard above proved absent, so this is
        // the only moment it is legal. Written inline rather than through the retired
        // MaterialisePlayerSpecies, which opened its own lock/connection and would re-decide
        // "already owned" against a DIFFERENT snapshot than the one proven inside this transaction.
        if (picks.Count > 0)
        {
            var player = GetPlayerUnlocked(db, playerId)!;
            var thetaContent = FusionRpg.Core.Power.PowerTuningHub.Tuning.Curve.PinIndex;
            var rollSeed = WorldSeed.DeriveRollSeed(player.WorldSeed, "species", output.SpeciesId);
            var catalogRevision = GetCatalogRevision();

            var compose = InstanceProducer.Compose(
                outputSpeciesContainer!, GetAtom, GetAffix, DomainMembers, rollSeed, thetaContent,
                FusionRpg.Core.Power.PowerTuningHub.Tuning, out var speciesInstance,
                variant: null, InstanceOrigin.Drop, catalogRevision, forcedPicks: forcedPicks);
            if (!compose.IsOk)
                return (false, "picks.compose-failed." + compose.Reason, null);

            var speciesInstanceId = Guid.NewGuid().ToString("N");
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    INSERT INTO effect_instance
                      (instance_id, container_id, roll_seed, catalog_revision, created_utc, origin,
                       theta_content, content_scale_milli)
                    VALUES ($id, $c, $seed, $rev, $utc, $origin, $theta, $scale);
                    """;
                cmd.Parameters.AddWithValue("$id", speciesInstanceId);
                cmd.Parameters.AddWithValue("$c", speciesInstance!.ContainerId);
                cmd.Parameters.AddWithValue("$seed", speciesInstance.RollSeed);
                cmd.Parameters.AddWithValue("$rev", speciesInstance.CatalogRevision);
                cmd.Parameters.AddWithValue("$utc", now);
                cmd.Parameters.AddWithValue("$origin", speciesInstance.Origin.ToString().ToLowerInvariant());
                cmd.Parameters.AddWithValue("$theta", speciesInstance.ThetaContent);
                cmd.Parameters.AddWithValue("$scale", speciesInstance.ContentScaleMilli);
                cmd.ExecuteNonQuery();
            }

            foreach (var a in speciesInstance.Atoms)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText =
                    "INSERT INTO effect_instance_atom (instance_id, seq, atom_id, values_json, power_json) " +
                    "VALUES ($id, $seq, $atom, $vals, $power);";
                cmd.Parameters.AddWithValue("$id", speciesInstanceId);
                cmd.Parameters.AddWithValue("$seq", a.Seq);
                cmd.Parameters.AddWithValue("$atom", a.AtomId);
                cmd.Parameters.AddWithValue("$vals", a.ValuesJson);
                cmd.Parameters.AddWithValue("$power", (object?)a.PowerJson ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }

            // SP0.3: ONE ledger row, in the same transaction as the instance — this is layer 1b's carrier
            // now (spec-species-mod-ledger.md behaviour 3). `correlation_id` is the fused output's own
            // instance id, so replaying the fusion writes no second row; `instance_id` is the
            // effect_instance that holds the atom payload. The `player_species` write it replaces is left
            // in place and unread, and SP0.6 retires the eager/debug writers.
            AppendSpeciesModUnlocked(
                db, fusionOwner, output.SpeciesId, SpeciesModMechanism.FusionPick,
                correlationId: minted.Actor.InstanceId, instanceId: speciesInstanceId,
                catalogRevision: speciesInstance.CatalogRevision);
        }

        // Recipe discovery: first-ever success pays the output rarity's discovery bonus once.
        // The ledger append's return is the last word — a hand-edited discovery table must not
        // announce Souls that deduped away (2026-08-21 review).
        bool newlyDiscovered;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR IGNORE INTO rpg_fusion_discovery(player_id, recipe_id, t)
                VALUES($p, $r, $t);
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$r", recipe.RecipeId);
            cmd.Parameters.AddWithValue("$t", now);
            newlyDiscovered = cmd.ExecuteNonQuery() > 0;
        }

        long discoverySouls = 0;
        if (newlyDiscovered)
        {
            discoverySouls = SoulEarnPolicy.DiscoveryDelta(output.BaseRarity);
            if (!AppendSoulLedgerUnlocked(db, playerId, 0, discoverySouls,
                    SoulEarnPolicy.Reasons.Discovery, null, null, "recipe:" + recipe.RecipeId, now))
            {
                newlyDiscovered = false;
                discoverySouls = 0;
            }
        }

        // One discovery policy across every acquisition path: a species first obtained via
        // fusion pays the same species bonus a summon would (dedupe `species:{id}` makes it
        // once-ever regardless of which path got there first — 2026-08-21 review S5).
        var speciesReward = SoulEarnPolicy.DiscoveryDelta(output.BaseRarity);
        if (speciesNewlyDiscovered && speciesReward > 0
            && AppendSoulLedgerUnlocked(db, playerId, 0, speciesReward,
                SoulEarnPolicy.Reasons.Discovery, "species", output.SpeciesId,
                "species:" + output.SpeciesId, now))
        {
            discoverySouls += speciesReward;
        }

        // Update lineage: the newborn knows its ingredients; consumed-by rows point at it.
        AppendLineageUnlocked(db, minted.Actor.InstanceId, "recipe-birth",
            JsonSerializer.Serialize(new
            {
                recipeId = recipe.RecipeId,
                inputs = request.SacrificeInstanceIds
            }), now);
        AppendFusionLogUnlocked(db, playerId, corr, FusionModes.Recipe, request,
            JsonSerializer.Serialize(new
            {
                mintedInstanceId = minted.Actor.InstanceId,
                recipeId = recipe.RecipeId,
                // Stored so a replay reproduces the discovery reveal — a client that lost the
                // original response must still see its banner (2026-08-21 review, Important 2).
                newlyDiscovered,
                discoverySouls
            }), seed, now);

        return (true, "", new FusionOutcome(
            false, FusionModes.Recipe, null, minted, recipe.RecipeId,
            newlyDiscovered, discoverySouls, ReadSoulBalanceUnlocked(db, playerId)));
    }

    public List<string> ListFusionDiscoveries(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT recipe_id FROM rpg_fusion_discovery WHERE player_id = $p ORDER BY recipe_id;
                """;
            cmd.Parameters.AddWithValue("$p", playerId);
            using var r = cmd.ExecuteReader();
            var ids = new List<string>();
            while (r.Read())
                ids.Add(r.GetString(0));
            return ids;
        }
    }

    // ---- shared validation + spend helpers ----

    (bool Ok, string Reason, UniqueActorDto? Actor, CreatureProfileDto? Profile, CreatureRarity Rarity)
        ReadFusionBaseUnlocked(SqliteConnection db, long playerId, string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return (false, "base.missing", null, null, default);
        var actor = ReadUniqueActorUnlocked(db, instanceId.Trim());
        var profile = actor == null ? null : ReadCreatureProfileUnlocked(db, actor.InstanceId);
        // save-identity SE4.24: the one ownership predicate — a Zomboss specimen of this save must
        // never be a valid fusion base for the human.
        if (actor is null
            || !OwnsSpecimenUnlocked(db, new EmpireRef(new SaveId(playerId), HumanEmpireOf(playerId)), actor.InstanceId)
            || profile is null)
            return (false, "base.missing", null, null, default);
        if (!string.Equals(actor.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal))
            return (false, "base.phase", null, null, default);
        if (HasActiveExpeditionMembershipUnlocked(db, actor.InstanceId))
            return (false, "base.on-expedition", null, null, default);
        if (!CreatureRarityIds.TryParse(profile.Rarity, out var rarity))
            return (false, "base.rarity", null, null, default);
        return (true, "", actor, profile, rarity);
    }

    /// <summary>"" = valid; otherwise the refusal reason. Locked specimens are player-protected
    /// from consumption — the lock's teeth (owner lock 1/7).</summary>
    string ValidateSacrificesUnlocked(
        SqliteConnection db, long playerId, FusionRequest request, int requiredCount, CreatureRarity? requireRarity)
    {
        var ids = request.SacrificeInstanceIds;
        if (ids.Count != requiredCount) return "sacrifices.count";
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Count) return "sacrifice.duplicate";
        // save-identity SE4.24: the one ownership predicate, hoisted once for the whole sacrifice loop.
        var owner = new EmpireRef(new SaveId(playerId), HumanEmpireOf(playerId));
        foreach (var id in ids)
        {
            if (string.Equals(id, request.BaseInstanceId, StringComparison.Ordinal))
                return "sacrifice.is-base";
            var actor = ReadUniqueActorUnlocked(db, id);
            var profile = actor == null ? null : ReadCreatureProfileUnlocked(db, id);
            if (actor is null || !OwnsSpecimenUnlocked(db, owner, actor.InstanceId) || profile is null)
                return "sacrifice.invalid";
            if (!string.Equals(actor.Phase, UniqueActorPhases.Roster, StringComparison.Ordinal))
                return "sacrifice.phase";
            if (profile.Locked) return "sacrifice.locked";
            if (HasActiveExpeditionMembershipUnlocked(db, id)) return "sacrifice.on-expedition";
            // The designation has teeth like the lock: the active patron is unconsumable
            // (spec-patron-creature.md) — it may still LEAD a merge, only consumption refuses.
            if (IsPatronUnlocked(db, playerId, id)) return "sacrifice.is-patron";
            if (requireRarity is { } band)
            {
                if (!CreatureRarityIds.TryParse(profile.Rarity, out var rarity) || rarity != band)
                    return "sacrifice.rarity";
            }
        }

        return "";
    }

    /// <summary>"" = paid; otherwise the refusal reason. Souls + shards + result-element essences.</summary>
    string SpendFusionCostsUnlocked(
        SqliteConnection db, long playerId, string corr, FusionCost cost, string resultElementId, string now)
    {
        var balance = ReadSoulBalanceUnlocked(db, playerId);
        if (balance.Balance < cost.Souls) return "souls.insufficient";

        var drops = new List<(string MaterialId, long Qty)>
        {
            ("shard." + cost.ShardRarity.ToId(), cost.ShardCount),
            ("essence." + resultElementId, cost.EssenceCount)
        };
        if (!TrySpendCreatureMaterialsUnlocked(db, playerId, drops, now))
            return "materials.insufficient";

        if (!AppendSoulLedgerUnlocked(db, playerId, 0, -cost.Souls, SoulEarnPolicy.Reasons.Fusion,
                "spend", corr, corr, now))
            throw new InvalidOperationException("fusion spend dedupe collision — correlation reused outside the fusion log");
        return "";
    }

    bool TrySpendCreatureMaterialsUnlocked(
        SqliteConnection db, long playerId, IReadOnlyList<(string MaterialId, long Qty)> needs, string now)
    {
        foreach (var (materialId, qty) in needs)
        {
            if (!CreatureMaterialCatalog.IsKnown(materialId))
                throw new ArgumentException($"Unknown creature material id '{materialId}'.");
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE rpg_creature_materials SET qty = qty - $q, updated_utc = $t
                WHERE player_id = $p AND material_id = $m AND qty >= $q;
                """;
            cmd.Parameters.AddWithValue("$q", qty);
            cmd.Parameters.AddWithValue("$t", now); // one timestamp per fusion, tx-consistent
            cmd.Parameters.AddWithValue("$p", playerId);
            cmd.Parameters.AddWithValue("$m", materialId);
            if (cmd.ExecuteNonQuery() == 0)
                return false; // insufficient — the enclosing tx rolls earlier decrements back
        }

        return true;
    }

    void ConsumeSacrificesUnlocked(
        SqliteConnection db, IReadOnlyList<string> sacrificeIds, string consumerInstanceId, string now)
    {
        foreach (var id in sacrificeIds)
        {
            using (var cmd = db.CreateCommand())
            {
                cmd.CommandText = """
                    UPDATE rpg_unique_actors SET phase = $phase, revision = revision + 1, updated_utc = $now
                    WHERE instance_id = $id;
                    """;
                cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Retired);
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }

            AppendLineageUnlocked(db, id, "consumed-by",
                JsonSerializer.Serialize(new { consumer = consumerInstanceId }), now);
            ReleaseContractOnRetireUnlocked(db, id, now);
        }
    }

    void AppendLineageUnlocked(SqliteConnection db, string instanceId, string @event, string detailJson, string now)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_creature_lineage(instance_id, event, detail_json, t)
            VALUES($i, $e, $d, $t);
            """;
        cmd.Parameters.AddWithValue("$i", instanceId);
        cmd.Parameters.AddWithValue("$e", @event);
        cmd.Parameters.AddWithValue("$d", detailJson);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    void AppendFusionLogUnlocked(
        SqliteConnection db, long playerId, string corr, string mode, FusionRequest request,
        string outputJson, ulong seed, string now)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_fusion_log(player_id, correlation_id, mode, inputs_json, output_json, seed, t)
            VALUES($p, $c, $m, $in, $out, $seed, $t);
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$c", corr);
        cmd.Parameters.AddWithValue("$m", mode);
        cmd.Parameters.AddWithValue("$in", JsonSerializer.Serialize(request));
        cmd.Parameters.AddWithValue("$out", outputJson);
        cmd.Parameters.AddWithValue("$seed", seed.ToString());
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }

    // ---- reads ----

    public List<CreatureLineageRow> ListCreatureLineage(string instanceId, int limit = 100)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT id, instance_id, event, detail_json, t FROM rpg_creature_lineage
                WHERE instance_id = $i ORDER BY id ASC LIMIT $l;
                """;
            cmd.Parameters.AddWithValue("$i", instanceId ?? "");
            cmd.Parameters.AddWithValue("$l", limit);
            using var r = cmd.ExecuteReader();
            var rows = new List<CreatureLineageRow>();
            while (r.Read())
                rows.Add(new CreatureLineageRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
            return rows;
        }
    }

    public (string Mode, string InputsJson, string OutputJson, ulong Seed)? TryGetFusionLog(
        long playerId, string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadFusionLogUnlocked(db, playerId, correlationId.Trim());
        }
    }

    (string Mode, string InputsJson, string OutputJson, ulong Seed)? ReadFusionLogUnlocked(
        SqliteConnection db, long playerId, string corr)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT mode, inputs_json, output_json, seed FROM rpg_fusion_log
            WHERE player_id = $p AND correlation_id = $c;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$c", corr);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return (r.GetString(0), r.GetString(1), r.GetString(2), ParseSeed(r.GetString(3)));
    }

    CreatureSpecimenDto? ReadSpecimenUnlocked(SqliteConnection db, string instanceId)
    {
        var actor = ReadUniqueActorUnlocked(db, instanceId);
        var profile = actor == null ? null : ReadCreatureProfileUnlocked(db, instanceId);
        return actor == null || profile == null ? null : new CreatureSpecimenDto { Actor = actor, Profile = profile };
    }

    /// <summary>
    /// Replay semantics: the log's job is idempotency, not a snapshot. Specimens and the balance
    /// are re-read LIVE (a later merge, rename, or spend is reflected — returning the stale
    /// mid-flight state would contradict every other surface). Only the discovery result is
    /// event-like and comes back from the stored output, so the reveal survives a lost response.
    /// </summary>
    FusionOutcome RebuildOutcomeUnlocked(
        SqliteConnection db, long playerId, (string Mode, string InputsJson, string OutputJson, ulong Seed) log)
    {
        using var doc = JsonDocument.Parse(log.OutputJson);
        var root = doc.RootElement;
        CreatureSpecimenDto? baseSpecimen = null;
        CreatureSpecimenDto? minted = null;
        string? recipeId = null;
        if (root.TryGetProperty("baseInstanceId", out var b) && b.GetString() is { } baseId)
            baseSpecimen = ReadSpecimenUnlocked(db, baseId);
        if (root.TryGetProperty("mintedInstanceId", out var m) && m.GetString() is { } mintedId)
            minted = ReadSpecimenUnlocked(db, mintedId);
        if (root.TryGetProperty("recipeId", out var rec))
            recipeId = rec.GetString();
        var newlyDiscovered = root.TryGetProperty("newlyDiscovered", out var nd) && nd.GetBoolean();
        var discoverySouls = root.TryGetProperty("discoverySouls", out var ds) ? ds.GetInt64() : 0;
        return new FusionOutcome(true, log.Mode, baseSpecimen, minted, recipeId,
            newlyDiscovered, discoverySouls, ReadSoulBalanceUnlocked(db, playerId));
    }

    static bool SameRequest(FusionRequest stored, FusionRequest request) =>
        stored.Mode == request.Mode
        && stored.BaseInstanceId == request.BaseInstanceId
        && stored.PickedTraitId == request.PickedTraitId
        && stored.SacrificeInstanceIds.SequenceEqual(request.SacrificeInstanceIds, StringComparer.Ordinal)
        && (stored.Picks ?? Array.Empty<FusionPick>())
            .SequenceEqual(request.Picks ?? Array.Empty<FusionPick>());
}
