using FusionRpg.Contracts;
using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Power;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// Sole Server Hot compose entry for UniqueActor sheet/derived — ActorHub only, FULL durable fan-in
/// (progression + aptitude + equip atoms + passive tree). Status (<c>l2b.derived</c>) stays
/// Injector-only (Hot session). "BattleStatComposer stays locked-separate" (ADR 2026-09-07) is
/// HISTORICAL — that ADR exception was overturned 2026-09-12 as a SOLID/DRY defect
/// (`decisions.md` "ActorHub sole Hot compose gate"), and battle-hub-fuse (T6, 2026-09-13) deleted
/// `BattleStatComposer` entirely: `BattleHubCompose` composes through this SAME `ActorHub`/
/// `DerivedComposer` every other surface uses. Battle is fused, not merely "no longer locked-separate."
/// Injector tree hydrate (T13, 2026-09-13): CLOSED via HTTP fan-in, not a local
/// PassiveTreeTuningHub configure — see <see cref="TreeBoundAtoms"/>'s own doc comment.
/// </summary>
public static class UniqueActorHubCompose
{
    public static (ActorHub Hub, StatContext Ctx) Build(RpgStore store, UniqueActorDto actor)
    {
        var level = (int)Math.Max(1, actor.Level);
        var baseline = new EntityBaseline
        {
            Hp = BattleRuleset.BaseHp(level),
            MaxHp = BattleRuleset.BaseHp(level),
            Atk = BattleRuleset.BaseAtk(level)
        };

        var factory = new StatContextFactory();
        var ctx = string.Equals(actor.Side, "zombie", StringComparison.OrdinalIgnoreCase)
            ? factory.ForZombie(actor.InstanceId, baseline, actor.TypeId, playerId: actor.PlayerId)
            : factory.ForPlant(actor.InstanceId, baseline, actor.TypeId, playerId: actor.PlayerId);

        var powerIndex = new Power.ServerPowerIndexProvider(store, PowerTuningHub.Tuning);

        var specimenId = actor.InstanceId;
        var aptitude = ResolveAptitudeAllocation(store, specimenId, actor.PlayerId);
        IReadOnlyList<BoundDerivedAtom> BoundAtoms(StatContext _)
        {
            var list = new List<BoundDerivedAtom>();
            list.AddRange(EquippedBoundAtoms.DerivedFromStore(store, specimenId));
            list.AddRange(TreeBoundAtoms.ForPlayer(store, powerIndex, actor.PlayerId));
            return list;
        }

        // species-progression `species-layer-delivery` step 6.2 (SP6.8) — the interim join above
        // (SP0.5 / SP3.6: a hand-rolled BoundAtoms fold that could not even carry a LadderMicro row,
        // only ever LayerValue.Fixed) is REMOVED. The sheet now registers the SAME
        // `rpg.species-layer` subsystem every other compose path uses, through
        // ActorHubBootstrap.CreateDefault's own opt-in seam — 1a + 1b of the specimen's OWNER empire
        // (never its side, never 2b), the SAME RpgStore.SpeciesLayersForSpecimen call SP6.7's
        // world-turn/web-squad wiring uses. Ledger-only, no preview fallback: a non-fuser's species
        // has no 1b row, and SpeciesLayersForSpecimen's own contract still emits 1a alone in that
        // case (unlike the previous interim join, gated on the ledger roll's own presence, which
        // composed NOTHING for a non-fuser — 1a's own presence was never conditioned on having
        // fused, so this is a widening, not a narrowing, and is exactly what module 6's own "1a
        // always accompanies a levelled species" rule expects).
        var speciesId = store.GetCreatureProfile(specimenId)?.SpeciesId;
        IReadOnlyList<Core.Creatures.Layers.ProjectedLayerRow>? SpeciesLayers(StatContext _)
        {
            if (string.IsNullOrWhiteSpace(speciesId)) return Array.Empty<Core.Creatures.Layers.ProjectedLayerRow>();
            var ownerEmpire = store.SpecimenOwnerEmpire(specimenId)?.Empire ?? store.HumanEmpireOf(actor.PlayerId);
            return store.SpeciesLayersForSpecimen(
                new FusionRpg.Core.Saves.EmpireRef(new FusionRpg.Core.Saves.SaveId(actor.PlayerId), ownerEmpire),
                speciesId);
        }

        // channelmods-hub: star/loyalty reach the sheet through the SAME producer battle uses
        // (StarLoyaltyBonus), so the two cannot drift. Read once — a specimen's star and its
        // contract loyalty are durable per-actor values, and Build runs per sheet/derived read.
        var profile = store.GetCreatureProfile(specimenId);
        var loyalty = store.GetContract(specimenId)?.Loyalty ?? 0;
        StarLoyaltyContribution StarLoyalty(StatContext _) =>
            new(profile?.Star ?? 0, loyalty, level);

        var hub = ActorHubBootstrap.CreateDefault(
            powerIndex: powerIndex,
            aptitudeTuning: AptitudeTuningHub.Tuning,
            aptitudeAllocation: _ => aptitude,
            boundDerivedAtoms: BoundAtoms,
            seedResourceBaseline: true,
            starLoyalty: StarLoyalty,
            speciesLayers: SpeciesLayers);

        return (hub, ctx);
    }

    /// <summary>
    /// `species-progression` SP1.4 (`layer-source-selector`, three-path parity) — extracted so the
    /// sheet's own aptitude compose is directly comparable, by value, against the lawn's
    /// (`SpeciesAllocationSource.Resolve`, SP1.3) and the web squad's (`WebMatchService.BuildSquad`,
    /// SP1.4) without driving a full sheet render. Asks the SAME
    /// <see cref="ProgressionLayerSelector"/> both other paths ask: the specimen's OWN allocation
    /// always applies (a unique specimen is a dedicated progression source — never falls back to the
    /// empire-wide CreatureType allocation, unlike a general lawn spawn), and the commander term
    /// applies only when its OWNER empire (never an assumed human owner) carries one.
    /// </summary>
    internal static AptitudeAllocation ResolveAptitudeAllocation(RpgStore store, string specimenId, long playerId)
    {
        var ownerEmpire = store.SpecimenOwnerEmpire(specimenId)?.Empire ?? store.HumanEmpireOf(playerId);
        var layers = ProgressionLayerSelector.Select(
            CreatureProgressionSource.UniqueSpecimen(specimenId, occurrenceId: "sheet"),
            ownerEmpire, store.HumanEmpireOf(playerId));
        // ai-empire-species EP4.18 (R23): the commander term is the OWNER EMPIRE's pool, through the ONE
        // empire-keyed read at this store's configured host Theta (`CommanderPoolFor`). It used to be
        // `AptitudeEndpoints.ScopeKey(playerId)` -- the human player's key -- applied to EVERY owner,
        // because the selector made the term human-only; under R23 a Zomboss-owned specimen carries
        // Zomboss's own pool and never the human's.
        var commanderAllocation = layers.CarriesCommander
            ? store.CommanderPoolFor(
                new FusionRpg.Core.Saves.EmpireRef(
                    new FusionRpg.Core.Saves.SaveId(playerId), ownerEmpire)).Allocation
            : AptitudeAllocation.Empty;
        // EP1.14 (spec-default-build.md) -- the sheet Hub compose seam: a levelled specimen the
        // player never built now reads its species' default distribution instead of Empty.
        var uniqueAllocation = store.EffectiveUniqueAllocation(specimenId, AptitudeTuningHub.Tuning).Allocation;
        return commanderAllocation + uniqueAllocation;
    }

    public static ActorSheetDto ProjectSheet(
        RpgStore store, UniqueActorDto actor, IActorLiveStateStore? liveState = null)
    {
        var (hub, ctx) = Build(store, actor);
        var primaryFinal = hub.Stats.Resolve(ctx);
        var (snapshot, contributions) = hub.ResolveDerivedWithContributions(ctx);
        var powerIndex = new Power.ServerPowerIndexProvider(store, PowerTuningHub.Tuning);
        var profile = store.GetCreatureProfile(actor.InstanceId);
        var speciesName = profile != null
            && CreatureSpeciesCatalog.IsConfigured
            && CreatureSpeciesCatalog.IsKnown(profile.SpeciesId)
            ? CreatureSpeciesCatalog.Get(profile.SpeciesId).Name
            : null;
        // One declaring site for "nickname, else the species name" — commander-roster EP3.2's roster
        // reader calls the same helper, so a creature cannot be named two ways.
        var displayName = FusionRpg.Core.Creatures.CreatureDisplayName.For(profile?.Nickname, profile?.SpeciesId)
            ?? speciesName;
        var roleLabel = string.Equals(actor.Side, "zombie", StringComparison.OrdinalIgnoreCase) ? "Zombie" : "Plant";
        var xpToNext = RpgXpCurve.XpToNext(RpgActorKinds.Specimen, actor.Level);
        var standing = ProjectStanding(store, actor, powerIndex, contributions);
        var resourcePools = ProjectResourcePools(store, actor, snapshot, ctx, powerIndex);
        var (liveStatuses, shieldLayers, shieldSummary) = ProjectHotLive(liveState, actor.InstanceId);

        IReadOnlyList<DerivedStatSurfaceEntry> surfaceEntries = Array.Empty<DerivedStatSurfaceEntry>();
        try { surfaceEntries = DerivedStatSurfaceCatalogHub.Catalog.Entries; }
        catch (InvalidOperationException) { /* catalog optional until host configures */ }

        var registry = hub.Composer.Registry;
        var derived = snapshot.Channels
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                registry.TryResolveChannel(kv.Key, out var def);
                var compose = def?.Compose.ToString() ?? "";
                var display = kv.Key;
                var reading = "";
                string? surfaceUnit = null;
                var surface = FindSurface(surfaceEntries, kv.Key);
                if (surface is not null)
                {
                    display = surface.DisplayName.Resolve("en");
                    reading = surface.Reading.Resolve("en");
                    surfaceUnit = surface.UnitClass.ToString();
                    if (string.IsNullOrEmpty(compose))
                        compose = surface.Compose.ToString();
                }

                var contribRows = contributions.ContributionsFor(kv.Key).ToList();
                var contribPairs = contribRows
                    .Select(c => (c.SourceId, c.Value))
                    .ToList();

                return new ActorSheetChannelDto
                {
                    ChannelId = kv.Key,
                    DisplayName = display,
                    Reading = reading,
                    ComposeKind = compose,
                    // D6: Value stays double (exempt); shield long elsewhere.
                    Value = kv.Value,
                    UnitClass = DerivedSheetChannelMeta.ResolveUnitClass(def, surfaceUnit),
                    DefaultValue = def?.DefaultValue ?? 0,
                    Cap = def?.Cap,
                    RenderState = DerivedSheetChannelMeta.ResolveRenderState(
                        kv.Key, def, kv.Value, contribPairs),
                    Contributions = contribRows
                        .Select(c => new ActorContributionDto
                        {
                            SourceId = c.SourceId,
                            Label = ContributionSourceIds.FictionLabel(c.SourceId),
                            Op = c.Op.ToString(),
                            Value = c.Value
                        })
                        .ToList()
                };
            })
            .ToList();

        var primary = primaryFinal.Contributions
            .Select(m =>
            {
                var sid = ContributionSourceIds.Primary(m.SourceKind, m.SourceId);
                return new ActorContributionDto
                {
                    SourceId = sid,
                    Label = ContributionSourceIds.FictionLabel(sid),
                    Op = m.Op.ToString(),
                    Value = m.Value
                };
            })
            .ToList();

        return new ActorSheetDto
        {
            InstanceId = actor.InstanceId,
            PlayerId = actor.PlayerId,
            Side = actor.Side,
            TypeId = actor.TypeId,
            DisplayName = displayName,
            SpeciesId = profile?.SpeciesId,
            SpeciesName = speciesName,
            Phase = actor.Phase,
            RoleLabel = roleLabel,
            Level = actor.Level,
            Xp = actor.Xp,
            XpToNext = xpToNext,
            ElementTyping = profile == null ? null : new ActorElementTypingDto
            {
                Primary = profile.ElementPrimary,
                Secondary = profile.ElementSecondary
            },
            Standing = standing,
            Derived = derived,
            Primary = primary,
            LiveStatuses = liveStatuses,
            ResourcePools = resourcePools,
            ShieldSummary = shieldSummary,
            ShieldLayers = shieldLayers
        };
    }

    /// <summary>
    /// Hot bag → liveStatuses + shieldLayers + shieldSummary (S1/S2). No bag entry = cold honesty.
    /// Summary elementId = front drain-order layer; stacks = layer count; null when no layers.
    /// </summary>
    static (
        IReadOnlyList<ActorStatusGlyphDto> LiveStatuses,
        IReadOnlyList<ActorShieldLayerDto> ShieldLayers,
        ActorShieldSummaryDto? ShieldSummary)
        ProjectHotLive(IActorLiveStateStore? liveState, string instanceId)
    {
        var bag = liveState?.Get(instanceId);
        if (bag is null)
        {
            return (
                Array.Empty<ActorStatusGlyphDto>(),
                Array.Empty<ActorShieldLayerDto>(),
                null);
        }

        var statuses = bag.LiveStatuses ?? Array.Empty<ActorStatusGlyphDto>();
        var layers = bag.ShieldLayers ?? Array.Empty<ActorShieldLayerDto>();
        if (layers.Count == 0)
            return (statuses, Array.Empty<ActorShieldLayerDto>(), null);

        long sumCurrent = 0, sumMax = 0;
        for (var i = 0; i < layers.Count; i++)
        {
            sumCurrent += layers[i].Current;
            sumMax += layers[i].Max;
        }

        // Q3 / S2: omit glance when empty layers or totals ≤ 0.
        if (sumCurrent <= 0)
            return (statuses, layers, null);

        var front = layers[0];
        return (
            statuses,
            layers,
            new ActorShieldSummaryDto
            {
                ElementId = front.ElementId,
                Current = sumCurrent,
                Max = sumMax,
                Stacks = layers.Count
            });
    }

    /// <summary>
    /// standing-compose (T9) — Standing = <see cref="PowerVector"/> from every Hub combat writer
    /// <see cref="CombatPowerMembership"/> includes, not equip+tree atoms alone (the former
    /// HF-standing gap: aptitude, star/loyalty, and any future non-atom Hub writer were completely
    /// unpriced). No second composer, no private aptitude re-fold: <paramref name="contributions"/>
    /// is the SAME <see cref="DerivedContributionBag"/> <see cref="ProjectSheet"/> already resolved
    /// off the SAME Hub this method's caller built — this reads it, never recomputes it.
    ///
    /// <para><b>Residual, not a naive Hub-snapshot price.</b> Pricing every included channel's Hub
    /// TOTAL would double-count equip/tree (already priced below as real <c>AtomRow</c>s / synthetics)
    /// and would still exclude nothing extra, so instead: keep equip/tree exactly as they were, and
    /// for every membership-included channel, synthesize ONE atom per contribution whose SourceId is
    /// NOT already carried by an equip (<c>equip:</c>) or tree (<c>tree.</c>) atom — this is exactly
    /// "the Hub total minus what equip/tree already contributed," computed per-contribution rather
    /// than as a subtraction, so it can never go negative or hide a sign error. `progression.*` (Θ)
    /// and resource pools are excluded by <see cref="CombatPowerMembership.Includes"/> itself before
    /// any SourceId is even inspected — Θ genuinely cannot reach Standing through this path.</para>
    /// </summary>
    static ActorStandingDto ProjectStanding(
        RpgStore store, UniqueActorDto actor, IPowerIndexProvider powerIndex,
        DerivedContributionBag contributions)
    {
        var bound = new List<BoundDerivedAtom>();
        bound.AddRange(EquippedBoundAtoms.DerivedFromStore(store, actor.InstanceId));
        bound.AddRange(TreeBoundAtoms.ForPlayer(store, powerIndex, actor.PlayerId));

        foreach (var channel in contributions.Channels)
        {
            if (!CombatPowerMembership.Includes(channel)) continue;
            foreach (var c in contributions.ContributionsFor(channel))
            {
                if (c.SourceId.StartsWith("equip:", StringComparison.Ordinal)) continue;
                if (c.SourceId.StartsWith("tree.", StringComparison.Ordinal)) continue;
                bound.Add(new BoundDerivedAtom(channel, c.Op, c.Value, c.SourceId));
            }
        }

        // Equip/tree atoms are NOT pre-filtered above -- an equipped item or tree node can target a
        // non-combat channel (e.g. resource.max.hp), and Standing must not price that, so every atom
        // (equip, tree, and the residual synthetics alike) goes through the same membership filter
        // here, once, per the spec's own locked algorithm.
        var atoms = CombatPowerMembership.Filter(bound, b => b.Channel)
            .Select(SyntheticStatDerived)
            .ToList();

        var vector = ActorPowerCache.Compose(atoms);
        return new ActorStandingDto
        {
            Offense = vector.Offense,
            Survivability = vector.Survivability,
            Control = vector.Control,
            Utility = vector.Utility,
            Economy = vector.Economy
        };
    }

    /// <summary>
    /// Cold UniqueActor pools: Max from Hub (<see cref="ResourceBaselineSubsystem"/> + gear/tree).
    /// Current from persisted subset or at-rest full. LiveStatuses/shield stay Hot-only.
    /// BattleRuleset floor is a last-resort guard only if Hub max is still 0 (should not happen
    /// when seedResourceBaseline is registered).
    /// </summary>
    static IReadOnlyList<ActorResourcePoolDto> ProjectResourcePools(
        RpgStore store,
        UniqueActorDto actor,
        ActorDerivedSnapshot snapshot,
        StatContext ctx,
        IPowerIndexProvider powerIndex)
    {
        var level = (int)Math.Max(1, actor.Level);
        var theta = Math.Max(1, powerIndex.ActorIndex(ctx));
        var baseHp = BattleRuleset.BaseHp(level);
        var persisted = store.GetUniqueActorPersistedPools(actor.InstanceId);
        var pools = new List<ActorResourcePoolDto>(DerivedStatChannels.ResourceIds.Count);

        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            var max = ResourceChannelReader.Max(snapshot, id);
            if (max <= 0)
                max = BattleRuleset.BaseResourceMax(theta, id, baseHp); // overflow guard — Hub seed is SSOT
            long current;
            if (persisted.TryGetValue(id, out var stored))
                current = Math.Clamp(stored, 0, Math.Max(0, max));
            else
                current = max; // at-rest refill for ids not in the cross-delve persist subset
            pools.Add(new ActorResourcePoolDto
            {
                ResourceId = id,
                Current = current,
                Max = max
            });
        }

        return pools;
    }

    static AtomRow SyntheticStatDerived(BoundDerivedAtom bound) =>
        new()
        {
            AtomId = $"sheet.standing.{bound.SourceId}",
            KindId = "stat.derived",
            FamilyId = "sheet.standing",
            Variant = bound.Channel.Replace('.', '-'),
            Tier = 1,
            Name = bound.SourceId,
            ParamsJson =
                $"{{\"channel\":\"{bound.Channel}\",\"op\":\"flat\",\"amount\":{bound.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}",
            Enabled = true
        };

    static DerivedStatSurfaceEntry? FindSurface(IReadOnlyList<DerivedStatSurfaceEntry> entries, string channelId)
    {
        DerivedStatSurfaceEntry? best = null;
        var bestLen = -1;
        foreach (var e in entries)
        {
            var matches = string.Equals(channelId, e.Family, StringComparison.Ordinal)
                || channelId.StartsWith(e.Family + ".", StringComparison.Ordinal);
            if (matches && e.Family.Length > bestLen)
            {
                best = e;
                bestLen = e.Family.Length;
            }
        }
        return best;
    }
}
