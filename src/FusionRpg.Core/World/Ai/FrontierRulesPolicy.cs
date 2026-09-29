using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Loam;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// The one policy that plays (spec-ai-commander.md §The decision layer).
///
/// Ordered rules, first match wins, **at most one order per entity**. Rules rather than scoring,
/// deliberately: scoring wants an economy to argue with and there is not one until
/// <c>sector-development</c>. The tables underneath — threat, value, reach, supply — are what both a
/// rule list and a scorer need, and they are what this module was really building.
///
/// Every decision is made from <see cref="IWorldView"/>, so Zomboss plays blind on exactly the terms
/// you do. When he walks into something, it is because his last report was six turns old.
/// </summary>
public sealed class FrontierRulesPolicy : IFactionPolicy
{
    public const string Id = "frontier-rules";

    public static readonly FrontierRulesPolicy Instance = new();

    /// <summary>Wounds above this, in per-mille of a member's health, are worth standing still for.</summary>
    public static int RecoverAtMilli => WorldAiPolicy.Tuning.FrontierRules.RecoverAtMilli;

    /// <summary>How far a legion will travel to look at ground nobody has seen.</summary>
    public static int ExploreTurns => WorldAiPolicy.Tuning.FrontierRules.ExploreTurns;

    /// <summary>
    /// The reconnection-cost floor a `Sever` target must clear to be worth attacking
    /// (spec-loam-ai.md), harness-tuned via `SeveranceThresholdTests`. Comfortably below a genuine
    /// articulation point's cost (which runs into
    /// <see cref="FusionRpg.Core.World.Topology.AllPairsCost.Unreachable"/>-scale territory the
    /// moment cutting it splits the enemy's holdings) and comfortably above a redundant sector's
    /// near-zero one.
    /// </summary>
    public static long SeveranceThresholdCost => WorldAiPolicy.Tuning.FrontierRules.SeveranceThresholdCost;

    /// <summary>Momentum hysteresis margin, per-mille — see <see cref="FrontierRulesTuning"/>.</summary>
    public static int MomentumMarginMilli => WorldAiPolicy.Tuning.FrontierRules.MomentumMarginMilli;

    public string PolicyId => Id;

    public IReadOnlyList<PolicyOrder> Decide(IWorldView view, ulong seed)
    {
        var orders = new List<PolicyOrder>();

        var defensive = ThreatMap.For(view, ThreatReading.Defensive);
        var supplied = BelievedSupply.ConnectedSectors(view);

        foreach (var entity in view.OwnForces.OrderBy(e => e.EntityId, StringComparer.Ordinal))
        {
            // A force beaten in the field spends its next turn recovering; ordering it about would
            // only fill the turn report with drops.
            if (entity.Routed) continue;

            var reach = ReachMap.For(view, entity);
            var value = ValueMap.For(view, defensive, reach);

            var order = Defend(view, entity, defensive, reach)
                        ?? Abandon(view, entity)
                        ?? Finish(view, entity)
                        ?? Take(view, entity)
                        ?? Sever(view, entity, reach, supplied)
                        ?? Recover(view, entity, supplied)
                        ?? Explore(view, entity, reach)
                        ?? Expand(view, entity, value, reach, supplied)
                        ?? Hold(view, entity);

            orders.Add(order);
        }

        // A faction with nothing left to command still has to say something, or the log cannot tell
        // "chose to do nothing" from "was never asked".
        if (orders.Count == 0) orders.AddRange(StandFastPolicy.Instance.Decide(view, seed));

        return orders;
    }

    // ---- 1. Defend ---------------------------------------------------------------------------

    /// <summary>
    /// A Seat of mine is threatened by more than is already standing on it.
    ///
    /// Compared against the garrison rather than against zero. Threat *spreads*, so on a small map
    /// almost everywhere carries some — a rule that fired on any threat at all would fire every turn
    /// for every legion, and the empire would never expand again.
    /// </summary>
    static PolicyOrder? Defend(
        IWorldView view, WorldEntity entity,
        IReadOnlyDictionary<string, long> threat, IReadOnlyDictionary<string, int> reach)
    {
        string? worst = null;
        long margin = 0;

        foreach (var sectorId in view.SectorIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (view.Believed(sectorId) is not { } believed) continue;
            if (!string.Equals(believed.OwnerFactionId, view.FactionId, StringComparison.Ordinal)) continue;
            if (believed.Slots.All(s => s.SlotTypeId != SlotTypeCatalog.SeatSlotTypeId)) continue;
            if (!reach.ContainsKey(sectorId)) continue;

            var incoming = threat.TryGetValue(sectorId, out var t) ? t : 0;
            var garrison = Garrison(view, sectorId);

            // One comparison, not two. `margin` starts at zero, so "threat exceeds the garrison" and
            // "this is the worst shortfall so far" are the same test on the first sector — the
            // separate guard that used to sit above this was redundant, and a mutant that deleted it
            // survived every test, which is how we found out.
            if (incoming - garrison <= margin) continue;
            margin = incoming - garrison;
            worst = sectorId;
        }

        if (worst is null) return null;
        if (string.Equals(entity.AtSectorId, worst, StringComparison.Ordinal)) return null;   // already home

        var path = Route(view, entity, worst);
        return path is null
            ? null
            : Order(view, entity, WorldCommandKinds.Move, $"defend {worst}, threat {margin} over the garrison",
                lanePath: path);
    }

    /// <summary>What this faction believes it already has standing in one of its sectors.</summary>
    static long Garrison(IWorldView view, string sectorId)
    {
        if (view.Believed(sectorId) is not { } believed) return 0;

        long total = 0;
        foreach (var force in believed.Forces)
            if (string.Equals(force.OwnerFactionId, view.FactionId, StringComparison.Ordinal))
                total += force.Defensive;

        return total;
    }

    // ---- 2. Abandon (spec-loam-ai-survival.md) ------------------------------------------------

    /// <summary>
    /// Do not keep what you cannot sustain. If the component this entity stands in cannot pay its
    /// own upkeep and its pooled stock will not outlast <see cref="LoamPolicy.AbandonmentHorizonTurns"/>
    /// at the current burn rate, the entity evacuates the component's single weakest sector — which
    /// is where it must be standing for this to fire — rather than being left to defend or recover
    /// in ground that is going to fade regardless. Evacuating also removes that sector's own
    /// garrison upkeep from the burn rate, which is the one thing an AI order can actually do about
    /// a doomed component: stop feeding it.
    ///
    /// One rule, deliberately: this does not touch ownership, ClaimResolver, or `LoamPhases` at all
    /// — the fade already does that. All this does is stop wasting an order on ground already lost.
    /// </summary>
    static PolicyOrder? Abandon(IWorldView view, WorldEntity entity)
    {
        if (entity.AtSectorId is not { } here) return null;
        if (view.Believed(here) is not { } hereBelief) return null;
        if (!string.Equals(hereBelief.OwnerFactionId, view.FactionId, StringComparison.Ordinal)) return null;

        var owned = view.SectorIds
            .Where(id => view.Believed(id) is { } b && string.Equals(b.OwnerFactionId, view.FactionId, StringComparison.Ordinal))
            .ToList();
        var component = TerritoryComponents.For(owned, SupplyReach.LinksOf(view.Lanes))
            .FirstOrDefault(c => c.Contains(here));
        if (component is null) return null;

        // G-C, mirrored from LoamUpkeep's truth side: a faction with no loam source anywhere is
        // exempt from upkeep entirely, so it has nothing to abandon. Every pre-loam AI fixture has
        // no rootbed at all, and without this check Abandon fired on every one of them.
        var hasSourceAnywhere = owned.Any(id =>
            view.Believed(id) is { } b
            && Habitability.For(b.Slots.Select(sl => (sl.SlotTypeId, sl.StructureId, sl.ConstructionTurnsRemaining))));
        if (!hasSourceAnywhere) return null;

        var handicap = view.Factions.First(f => string.Equals(f.FactionId, view.FactionId, StringComparison.Ordinal)).UpkeepHandicapMilli;
        // world-map W48/W49: the season is calendar, never fogged — the belief side reads it off
        // the turn exactly like truth does (`LoamUpkeep.BreakdownFor`), never a second copy.
        var seasonMilli = World.WorldTuningHub.Tuning.Seasons.UpkeepMilli[TurnCalendar.SeasonOf(view.CurrentTurn)];

        long totalProduction = 0, totalUpkeep = 0, totalStock = 0;
        string? weakest = null;
        long weakestBalance = long.MaxValue;

        foreach (var id in component)
        {
            if (view.Believed(id) is not { } b) continue;

            var production = LoamProduction.For(b.OwnerFactionId, b.Slots.Select(sl => sl.SlotTypeId));
            var garrisonMembers = view.OwnForces
                .Where(e => string.Equals(e.AtSectorId, id, StringComparison.Ordinal))
                .Sum(e => e.Members.Count);
            var upkeep = LoamUpkeep.For(garrisonMembers, b.DevelopmentLevel, b.DangerBand, b.FractureIntensityMilli, handicap, seasonMilli);

            totalProduction += production;
            totalUpkeep += upkeep;
            totalStock += view.OwnLoamStock(id);

            var balance = LoamBalance.PerSector(production, upkeep);
            if (weakest is null || balance < weakestBalance
                || (balance == weakestBalance && string.CompareOrdinal(id, weakest) < 0))
            {
                weakestBalance = balance;
                weakest = id;
            }
        }

        var componentBalance = LoamBalance.PerSector(totalProduction, totalUpkeep);
        if (componentBalance >= 0) return null;   // solvent — nothing to release

        var shortfall = -componentBalance;
        var turnsOfRunway = totalStock / shortfall;
        if (turnsOfRunway >= LoamPolicy.AbandonmentHorizonTurns) return null;   // still enough runway

        if (!string.Equals(weakest, here, StringComparison.Ordinal)) return null;   // not this entity's ground to give up

        var safeTarget = owned.FirstOrDefault(id => !component.Contains(id))
                          ?? owned.FirstOrDefault(id => !string.Equals(id, here, StringComparison.Ordinal));
        if (safeTarget is null) return null;

        var path = Route(view, entity, safeTarget);
        return path is null
            ? null
            : Order(view, entity, WorldCommandKinds.Move,
                $"abandon {here}, {turnsOfRunway} turns of runway under the {LoamPolicy.AbandonmentHorizonTurns}-turn horizon",
                lanePath: path);
    }

    // ---- 3. Finish ---------------------------------------------------------------------------

    /// <summary>Standing where a guard still holds a slot, with nothing hostile in sight.</summary>
    static PolicyOrder? Finish(IWorldView view, WorldEntity entity)
    {
        if (entity.AtSectorId is not { } here) return null;
        if (view.Believed(here) is not { } believed) return null;
        if (believed.Forces.Any(f => ZoneOfControl.IsHostile(f.OwnerFactionId, view.FactionId)
                                     && ZoneOfControl.Projects(f.Kind))) return null;

        // Lowest index first: an arbitrary but *stable* order, so a replay clears the same slot.
        var guarded = believed.Slots
            .Where(s => s.GuardState == GuardState.Intact && s.GuardWaveId != null)
            .OrderBy(s => s.SlotIndex)
            .FirstOrDefault();

        return guarded is null
            ? null
            : Order(view, entity, WorldCommandKinds.Clear, $"clear slot {guarded.SlotIndex} of {here}",
                sectorId: here, slotIndex: guarded.SlotIndex);
    }

    // ---- 4. Take -----------------------------------------------------------------------------

    /// <summary>
    /// Standing on ground that is believed clear and believed unowned.
    ///
    /// Guarded ground is refused here as well as by Finish above, so the two rules could be swapped
    /// without changing an outcome — a mutant proved it. They are ordered this way because *finish
    /// what is in front of you* reads correctly, not because anything depends on it.
    /// </summary>
    static PolicyOrder? Take(IWorldView view, WorldEntity entity)
    {
        if (entity.AtSectorId is not { } here) return null;
        if (view.Believed(here) is not { } believed) return null;
        if (believed.Detail != SectorSight.Full) return null;          // a glimpse reports no slots
        if (believed.OwnerFactionId != null) return null;
        if (believed.Slots.Any(s => s.GuardState == GuardState.Intact && s.GuardWaveId != null)) return null;

        return Order(view, entity, WorldCommandKinds.Claim, $"claim {here}", sectorId: here);
    }

    // ---- 5. Sever (spec-loam-ai.md) -----------------------------------------------------------

    /// <summary>
    /// The reachable enemy sector whose loss would hurt them the worst, if it clears
    /// <see cref="SeveranceThresholdCost"/>. Sits above <see cref="Take"/> — claiming free,
    /// undefended ground is strictly lower-risk than attacking an enemy-held junction, so it still
    /// wins when both are available — and above <see cref="Recover"/> and routine expansion: a cut
    /// this good is worth pre-empting mere self-maintenance for.
    /// </summary>
    static PolicyOrder? Sever(
        IWorldView view, WorldEntity entity, IReadOnlyDictionary<string, int> reach, IReadOnlySet<string> supplied)
    {
        string? best = null;
        long bestScore = SeveranceThresholdCost;

        foreach (var sectorId in view.SectorIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!reach.ContainsKey(sectorId)) continue;
            if (view.Believed(sectorId) is not { OwnerFactionId: { } owner } believed) continue;
            if (string.Equals(owner, view.FactionId, StringComparison.Ordinal)) continue;

            var score = SeveranceScore.For(view, owner, sectorId);
            if (score <= bestScore) continue;

            bestScore = score;
            best = sectorId;
        }

        if (best is null) return null;

        var path = Route(view, entity, best);
        if (path is null || !SurvivesTheRoute(view, entity, path, supplied)) return null;

        return Order(view, entity, WorldCommandKinds.Move, $"sever {best}, severance {bestScore}", lanePath: path);
    }

    // ---- 6. Recover --------------------------------------------------------------------------

    /// <summary>Badly hurt, in supply, and not already dug in.</summary>
    static PolicyOrder? Recover(IWorldView view, WorldEntity entity, IReadOnlySet<string> supplied)
    {
        if (entity.AtSectorId is not { } here || !supplied.Contains(here)) return null;

        // Already dug in and still mending: say so. The stance persists on its own, so re-filing it
        // would cost a turn for nothing — but reporting "nothing worth doing" through the five turns
        // a legion spends healing is the audit trail lying about the most legible thing it does all
        // game. Note this ignores `RecoverAtMilli`: that threshold governs whether it is worth
        // *stopping* to heal, not whether healing is what is happening. Both halves were found by
        // playing twenty turns and reading them.
        var wounded = WoundedMilli(entity);
        if (string.Equals(entity.Stance, MovementPolicy.Hold, StringComparison.Ordinal))
            return wounded > 0
                ? Order(view, entity, WorldCommandKinds.StandFast, "holding position, still recovering")
                : null;

        if (wounded < RecoverAtMilli) return null;

        return Order(view, entity, WorldCommandKinds.Stance, "dig in and recover", stance: MovementPolicy.Hold);
    }

    static int WoundedMilli(WorldEntity entity)
    {
        if (entity.Members.Count == 0) return 0;

        long hp = 0, wounds = 0;
        foreach (var member in entity.Members)
        {
            hp += Math.Max(1, member.Hp);
            wounds += member.Wounds;
        }

        return (int)(wounds * 1000 / Math.Max(1, hp));
    }

    // ---- 7. Explore --------------------------------------------------------------------------

    /// <summary>
    /// Ground nobody has seen, close enough to be worth the trip.
    ///
    /// Files the *stance* or the *move*, never both: committing a stance costs the turn it is
    /// committed, so a policy that filed both would watch the move dropped and re-file it forever.
    /// </summary>
    static PolicyOrder? Explore(IWorldView view, WorldEntity entity, IReadOnlyDictionary<string, int> reach)
    {
        var target = view.SectorIds
            .Where(id => view.Believed(id) is null
                         && reach.TryGetValue(id, out var turns) && turns <= ExploreTurns)
            .OrderBy(id => reach[id])
            .ThenBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (target is null) return null;

        if (!string.Equals(entity.Stance, MovementPolicy.Scout, StringComparison.Ordinal))
            return Order(view, entity, WorldCommandKinds.Stance, $"scout toward {target}",
                stance: MovementPolicy.Scout);

        var path = Route(view, entity, target);
        return path is null ? null : Order(view, entity, WorldCommandKinds.Move, $"explore {target}", lanePath: path);
    }

    // ---- 8. Expand ---------------------------------------------------------------------------

    /// <summary>The best-valued reachable ground I do not hold, if it is worth having at all.</summary>
    static PolicyOrder? Expand(
        IWorldView view, WorldEntity entity,
        IReadOnlyDictionary<string, SectorValue> value, IReadOnlyDictionary<string, int> reach,
        IReadOnlySet<string> supplied)
    {
        string? best = null;
        // Strictly positive: zero is not worth a march. Survivor found extending the mutant set for
        // L20 (loam-ai-survival) — no existing fixture drives every reachable, unowned candidate to
        // a non-positive score, so a mutant starting this at `long.MinValue` goes uncaught. Pre-dates
        // this module and is `Expand`'s own rule, not `Abandon`'s; recorded rather than fixed here.
        long bestScore = 0;

        foreach (var sectorId in view.SectorIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!reach.ContainsKey(sectorId)) continue;
            if (string.Equals(entity.AtSectorId, sectorId, StringComparison.Ordinal)) continue;
            if (view.Believed(sectorId) is { } believed
                && string.Equals(believed.OwnerFactionId, view.FactionId, StringComparison.Ordinal)) continue;

            var score = value.TryGetValue(sectorId, out var v) ? v.Total : 0;
            if (score <= bestScore) continue;

            bestScore = score;
            best = sectorId;
        }

        if (best is null) return null;

        // ---- Momentum, as hysteresis (spec-ai-commander.md §Momentum, amended 2026-08-31) --------
        //
        // The observed defect: Zomboss alternated `defend black-gate` / `expand to verdant-shelf`
        // from turn 8 onward, never arriving anywhere. It is a feedback loop between two rules --
        // Defend pulls the legion home, the garrison rises, threat no longer exceeds it, Expand sends
        // it out, the garrison drops -- so a bonus term has nothing to attach to. What breaks a loop
        // like that is hysteresis: once committed, the alternative must be materially better, not
        // merely better.
        //
        // Applied here rather than in Defend on purpose. Defend is the emergency at the top of the
        // ladder and must stay free to interrupt a march; making an emergency sticky would trade
        // dithering for a legion that ignores a real threat, which is the worse failure.
        //
        // `LastOrderedDestination` is self-knowledge derived from the command log, not new stored
        // state, and it can never reach a replayed hash because replay never re-runs a policy.
        var standing = view.LastOrderedDestination(entity.EntityId);
        if (standing is not null
            && !string.Equals(standing, best, StringComparison.Ordinal)
            && !string.Equals(standing, entity.AtSectorId, StringComparison.Ordinal))
        {
            var standingScore = value.TryGetValue(standing, out var sv) ? sv.Total : 0;
            if (standingScore > 0)
            {
                // Widen before multiplying and divide once, last -- a value total is a magnitude and
                // the margin is per-mille (docs/architecture/numeric-types.md "Numeric overflow"). checked so an absurd score
                // throws rather than wrapping into a silently negative threshold.
                long required;
                checked
                {
                    required = standingScore
                             + standingScore * MomentumMarginMilli / 1000;
                }

                if (bestScore <= required)
                {
                    // Stay the course. Re-route to the standing target rather than returning null:
                    // returning null would drop through to Hold and strand the legion mid-map, which
                    // is a different bug wearing the same fix.
                    var keep = Route(view, entity, standing);
                    if (keep is not null && SurvivesTheRoute(view, entity, keep, supplied))
                        return Order(view, entity, WorldCommandKinds.Move,
                            $"expand to {standing}, holding course ({bestScore} does not beat {required})",
                            lanePath: keep);
                }
            }
        }

        var path = Route(view, entity, best);
        if (path is null || !SurvivesTheRoute(view, entity, path, supplied)) return null;

        return Order(view, entity, WorldCommandKinds.Move,
            $"expand to {best}, {value[best].Explain()}", lanePath: path);
    }

    // ---- 9. Hold -----------------------------------------------------------------------------

    static PolicyOrder Hold(IWorldView view, WorldEntity entity) =>
        Order(view, entity, WorldCommandKinds.StandFast, "nothing worth doing");

    // ---- the march-loam gate (spec-loam-ai.md) ------------------------------------------------

    /// <summary>
    /// Would this legion's leash survive its own route? One pass over an already-known lane path,
    /// partitioned into contiguous out-of-supply runs, requiring `Capacity ≥ Burn × (length of the
    /// longest such run)` — the leash only has to survive the worst single stretch beyond supply,
    /// since any in-supply sector along the way tops the legion back toward full before the next
    /// stretch begins. Not a turn-by-turn simulator: this reads `supplied` once, computed for the
    /// route's starting `WorldState`, the same pre-march-filter shape `Route` itself already uses.
    ///
    /// A legion with no burn at all (an empty roster) has nothing to run out on, so it always
    /// survives — there is nothing here for the gate to refuse.
    /// </summary>
    internal static bool SurvivesTheRoute(
        IWorldView view, WorldEntity entity, IReadOnlyList<string> lanePath, IReadOnlySet<string> supplied)
    {
        var burn = LegionSupply.Burn(entity);
        if (burn <= 0) return true;

        if (entity.AtSectorId is not { } origin) return true;   // Route() itself never produces a path without one

        var capacity = LegionSupply.Capacity(entity);
        var longestRun = 0;
        var currentRun = 0;

        foreach (var sectorId in SectorsAlong(view, origin, lanePath))
        {
            if (supplied.Contains(sectorId))
            {
                currentRun = 0;
                continue;
            }

            currentRun++;
            if (currentRun > longestRun) longestRun = currentRun;
        }

        return capacity >= burn * longestRun;
    }

    /// <summary>The ordered sectors a lane path actually visits, origin included — believed lanes, walked once.</summary>
    static IReadOnlyList<string> SectorsAlong(IWorldView view, string origin, IReadOnlyList<string> lanePath)
    {
        var sectors = new List<string>(lanePath.Count + 1) { origin };
        var at = origin;

        foreach (var laneId in lanePath)
        {
            var lane = view.Lanes.First(l => string.Equals(l.LaneId, laneId, StringComparison.Ordinal));
            at = string.Equals(lane.FromSectorId, at, StringComparison.Ordinal) ? lane.ToSectorId : lane.FromSectorId;
            sectors.Add(at);
        }

        return sectors;
    }

    // ---- plumbing ------------------------------------------------------------------------------

    /// <summary>
    /// The cheapest believed route, as lane ids. Null when there is none — under fog that includes
    /// routes that only *look* open, which is the point.
    /// </summary>
    static IReadOnlyList<string>? Route(IWorldView view, WorldEntity entity, string target)
    {
        var origin = entity.AtSectorId ?? entity.OnLaneTowardSectorId;
        if (origin is null || string.Equals(origin, target, StringComparison.Ordinal)) return null;

        // Breadth-first over believed lanes: fewest lanes rather than cheapest, because a march is
        // spent per lane and the engine walks the path one lane at a time anyway.
        var cameBy = new Dictionary<string, (string From, string LaneId)>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { origin };
        var frontier = new Queue<string>();
        frontier.Enqueue(origin);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            if (string.Equals(current, target, StringComparison.Ordinal)) break;

            foreach (var lane in view.Lanes.OrderBy(l => l.LaneId, StringComparer.Ordinal))
            {
                if (lane.State != LaneState.Open) continue;

                var type = LaneTypeCatalog.Get(lane.TypeId);
                if (type.Gated && lane.GateKeyId != null) continue;

                string next;
                if (string.Equals(lane.FromSectorId, current, StringComparison.Ordinal)) next = lane.ToSectorId;
                else if (!type.OneWay && string.Equals(lane.ToSectorId, current, StringComparison.Ordinal)) next = lane.FromSectorId;
                else continue;

                if (!seen.Add(next)) continue;
                cameBy[next] = (current, lane.LaneId);
                frontier.Enqueue(next);
            }
        }

        if (!cameBy.ContainsKey(target)) return null;

        var path = new List<string>();
        for (var at = target; cameBy.TryGetValue(at, out var step); at = step.From)
            path.Add(step.LaneId);

        path.Reverse();
        return path;
    }

    static PolicyOrder Order(
        IWorldView view, WorldEntity entity, string kind, string reason,
        string? sectorId = null, int? slotIndex = null, string? stance = null,
        IReadOnlyList<string>? lanePath = null) =>
        new(
            new WorldCommand
            {
                CommanderId = view.FactionId,
                CommandId = $"ai-{view.CurrentTurn}-{entity.EntityId}",
                Kind = kind,
                EntityId = kind == WorldCommandKinds.StandFast ? null : entity.EntityId,
                SectorId = sectorId,
                SlotIndex = slotIndex,
                Stance = stance,
                LanePath = lanePath ?? Array.Empty<string>()
            },
            reason);
}
