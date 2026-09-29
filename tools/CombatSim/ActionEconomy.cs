using System.Text.Json;
using System.Text.Json.Serialization;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Balance.Analytic;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// A POC slice of the action layer's COST model — enough to make the resource distribution
/// measurable. The action program (A1–A10) is specced and unbuilt; this is not an implementation of
/// it, and <c>actions/basic.json</c> lists exactly what it does and does not model.
///
/// <para><b>The one mechanic this adds, and it is the point:</b> an actor who cannot pay cannot
/// attack. That turns <c>resource.max.stamina</c> and <c>resource.regen.stamina</c> from decoration
/// into a real constraint, and makes sustain an OFFENSIVE stat — which is the half of the economy the
/// resource-free model could not see at all.</para>
///
/// <para><b>max is burst, regen is sustain.</b> A full pool buys a run of actions up front; regen sets
/// the rate you can hold forever. Short fights are decided by the pool, long ones by the rate. Both
/// come from the aptitude distribution, so they are a real allocation trade.</para>
/// </summary>
public sealed class ActionSet
{
    public int SchemaVersion { get; set; }
    public string Name { get; set; } = "unnamed";
    public List<ActionDef> Actions { get; set; } = new();

    public sealed class ActionDef
    {
        public string Id { get; set; } = "";
        public string Category { get; set; } = "attack";
        public CostDef? Cost { get; set; }
        public double DamageMultiplier { get; set; }
        public int Priority { get; set; }

        /// <summary>combat-ai `action-schedule-twin` CAI2.3 (spec-action-schedule-twin.md §2): the
        /// per-row reserve-floor override, mirroring `ActionSchedule.ActionOption`'s own field. `-1`
        /// defers to the policy's default; absent from a shipped `actions/*.json` reads as `-1`.</summary>
        public long ReserveFloorMilli { get; set; } = -1;

        /// <summary>The per-row AREA waste guard. `1` (the default) is the identity; above 1 the walk
        /// refuses loudly, because the twin is a duel model (see `ActionPolicy.Choose`).</summary>
        public int MinTargets { get; set; } = 1;
    }

    public sealed class CostDef
    {
        public string ResourceId { get; set; } = "";
        public long ShareOfOutputMilli { get; set; }
        public string When { get; set; } = "onCommit";
    }

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static ActionSet Load(string nameOrPath)
    {
        var path = AptitudeModel.Resolve(nameOrPath, "actions");
        var s = JsonSerializer.Deserialize<ActionSet>(File.ReadAllText(path), Options)
                ?? throw new InvalidOperationException($"{path}: empty action set");
        foreach (var a in s.Actions)
        {
            if (a.Cost is null) continue;
            if (!DerivedStatChannelsMirror.ResourceIds.Contains(a.Cost.ResourceId))
                throw new InvalidOperationException(
                    $"{path}: action '{a.Id}' costs unknown resource '{a.Cost.ResourceId}'. " +
                    $"Known: {string.Join(", ", DerivedStatChannelsMirror.ResourceIds)}");
            // spec-action-costs.md §3: perTick is a channelled cost that ends the action when it runs
            // dry through the interrupt path. Neither engine here has an interrupt path, so a perTick
            // cost would silently behave as onCommit — reject rather than pretend.
            if (!a.Cost.When.Equals("onCommit", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"{path}: action '{a.Id}' uses when='{a.Cost.When}'. Only 'onCommit' is modelled — " +
                    "perTick needs the interrupt path (action program A5), which neither engine has.");
        }
        if (s.Actions.All(a => a.Cost is not null))
            throw new InvalidOperationException($"{path}: needs a free fallback action (pass), or a dry actor has nothing to do");
        return s;
    }
}

/// <summary>The locked five (<c>DerivedStatChannels.ResourceIds</c>), mirrored so the tool can name
/// them in an error before Core is reachable. Kept in one place so the mirror is auditable.</summary>
static class DerivedStatChannelsMirror
{
    public static readonly string[] ResourceIds = FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds.ToArray();
}

/// <summary>
/// One actor's pools during one fight. Lazy regen exactly as
/// <c>spec-action-costs.md §2</c> specifies: <c>value(now) = clamp(stored + rate × (now − lastTick), 0, max)</c>,
/// never scheduled.
/// </summary>
public sealed class ActorPools
{
    readonly Dictionary<string, double> _value = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> _max = new(StringComparer.Ordinal);
    readonly Dictionary<string, double> _regen = new(StringComparer.Ordinal);
    readonly Dictionary<string, long> _carry = new(StringComparer.Ordinal);

    public ActorPools(Archetype a)
    {
        foreach (var id in DerivedStatChannelsMirror.ResourceIds)
        {
            var max = Stat(a, $"resource.max.{id}");
            _max[id] = max;
            _regen[id] = Stat(a, $"resource.regen.{id}");
            _value[id] = max;   // spec-action-costs.md §5: a run starts full
        }
    }

    static double Stat(Archetype a, string channel) =>
        a.Stats.TryGetValue(channel, out var r) ? (r.Min + r.Max) / 2.0 : 0.0;

    public double Max(string id) => _max.GetValueOrDefault(id);
    public double Regen(string id) => _regen.GetValueOrDefault(id);
    public double Value(string id) => _value.GetValueOrDefault(id);

    /// <summary>One round of lazy accrual, through <see cref="ActionSchedule.Advance"/> — the shipped
    /// <c>ResourcePoolState.Settle</c> arithmetic, per-mille <c>long</c> with the sub-unit carry held
    /// across rounds — rather than a local double clamp that truncated every remainder.</summary>
    public void Tick(double rounds = 1.0)
    {
        var ticks = checked((long)Math.Round(rounds));
        if (ticks < 0) throw new ArgumentOutOfRangeException(nameof(rounds), rounds, "must be non-negative");
        for (var i = 0; i < ticks; i++)
            foreach (var id in DerivedStatChannelsMirror.ResourceIds)
            {
                var advanced = ActionSchedule.Advance(
                    new ActionSchedule.PoolState(_value[id], _max[id], _regen[id], _carry.GetValueOrDefault(id)));
                _value[id] = advanced.Value;
                _carry[id] = advanced.CarryMilli;
            }
    }

    /// <summary>
    /// spec-action-costs.md §3 — validate every cost, then consume every cost, roll back all of them
    /// if any fails. One cost per action here, but the shape is kept because the moment a second cost
    /// exists an aggregate check would pass when two errors cancel.
    /// </summary>
    public bool TryPay(IReadOnlyList<(string Id, double Amount)> costs)
    {
        foreach (var (id, amount) in costs)
            if (_value.GetValueOrDefault(id) < amount) return false;
        foreach (var (id, amount) in costs) _value[id] -= amount;
        return true;
    }
}

/// <summary>Costs and picks actions. Shared by the simulator and the closed form so a disagreement
/// between them can never be two different action policies.</summary>
public static class ActionPolicy
{
    /// <summary>Nominal output — what the cost is priced against. NOT damage dealt: committing is
    /// what costs (spec-action-costs.md §3), so a miss pays in full.</summary>
    public static double NominalOutput(ActionSet.ActionDef a, double baseDamage) => baseDamage * a.DamageMultiplier;

    public static double CostOf(ActionSet.ActionDef a, double baseDamage) =>
        a.Cost is null ? 0 : NominalOutput(a, baseDamage) * (a.Cost.ShareOfOutputMilli / 1000.0);

    /// <summary>
    /// Highest-priority action the actor can actually pay for — the same walk
    /// `ActionSchedule.Choose` performs, with the same two new behaviour knobs (combat-ai
    /// `action-schedule-twin` CAI2.3, spec-action-schedule-twin.md §2). Deliberately the simplest policy
    /// that exercises the economy — <c>action-selection</c> (A7) is a whole module and inventing a
    /// clever policy here would make the measurement about the policy instead of the distribution.
    ///
    /// <para><b>Identical semantics, one expression.</b> <paramref name="reserveFloorMilli"/> is a floor
    /// on what REMAINS after paying (0 = today's behaviour, so every existing call is unchanged);
    /// <paramref name="skipCosted"/> is the overkill waste guard, off by default. There is no tier
    /// parameter here on purpose: this tool IS the performance tier by construction (a fixed selector
    /// and a first-usable pass over the gates), and the twin's own note records that the two tiers
    /// provably collapse in a duel — so a second tier vocabulary here would be a fork, not a mirror.</para>
    /// </summary>
    public static ActionSet.ActionDef Choose(
        ActionSet set, ActorPools pools, double baseDamage,
        long reserveFloorMilli = 0, bool skipCosted = false)
    {
        // The affordability decision is ActionSchedule.Choose — one walk for the twin and the POC.
        // Only the CombatSim-shaped mapping (JSON ActionDef -> ActionOption, ActorPools -> PoolState)
        // is local to this file now.
        var options = set.Actions
            .Select(a => new ActionSchedule.ActionOption(
                a.Id, a.Priority, a.DamageMultiplier,
                a.Cost?.ResourceId,
                a.Cost?.ShareOfOutputMilli ?? 0,
                a.ReserveFloorMilli,
                a.MinTargets))
            .OrderBy(o => o.Priority)
            .ToArray();

        // The twin's own area-waste refusal, kept here because the round loop calls Choose directly
        // and so does not run Walk's constructor validation.
        foreach (var o in options)
            if (o.MinTargets > 1)
                throw new ArgumentException(
                    $"option '{o.Id}' is gated on minTargets={o.MinTargets}; the action-economy model is a " +
                    "DUEL model and cannot express an area waste guard.",
                    nameof(set));

        var poolStates = DerivedStatChannelsMirror.ResourceIds.ToDictionary(
            id => id,
            id => new ActionSchedule.PoolState(pools.Value(id), pools.Max(id), pools.Regen(id)),
            StringComparer.Ordinal);

        var chosen = ActionSchedule.Choose(
            options, poolStates, baseDamage,
            new ActionSchedule.SchedulePolicy("combatsim", AiTier.Performance, reserveFloorMilli, skipCosted),
            round: 0,
            fightEndsThisRound: skipCosted ? _ => true : null);

        return set.Actions.First(a => a.Id == chosen.Id);
    }
}
