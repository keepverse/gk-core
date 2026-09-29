using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Tools.CombatSim;

/// <summary>One resolved attack, as the real pipeline produced it.</summary>
public readonly record struct TrialResult(
    long BaseDamage,
    bool Calculated,
    bool Hit,
    bool Crit,
    bool Parried,
    bool Blocked,
    double PHit,
    double PCrit,
    double CritMult,
    double WeightedDelta,
    long PreShieldDamage,
    long DefenderDamage,
    long ShieldAbsorbed,
    long AttackerSelfDamage,
    int ReflectBounces)
{
    public bool Missed => Calculated && !Hit;
    public bool CleanHit => Calculated && Hit && !Parried && !Blocked;
    public bool Reflected => ReflectBounces > 0;
}

/// <summary>One fight fought to the death (or to <see cref="Scenario.MaxRounds"/>).</summary>
public readonly record struct FightResult(
    int Rounds,
    bool AttackerDied,
    bool DefenderDied,
    long AttackerHpLeft,
    long DefenderHpLeft,
    long DamageDealt,
    long DamageReflected)
{
    /// <summary>Neither side could finish the other — a real outcome, not an error.</summary>
    public bool Stalemate => !AttackerDied && !DefenderDied;

    /// <summary>The tank killed the thing hitting it. The question this mode exists to answer.</summary>
    public bool DefenderWins => AttackerDied && !DefenderDied;
}

/// <summary>
/// Drives <see cref="CombatDamageDispatcher.DispatchInstant"/> — the same entry point the injector
/// and battle hosts use — over <see cref="FoundationHarness"/>. Nothing here reimplements combat
/// math; every number comes back out of src/.
/// </summary>
public static class Simulator
{
    const string AtkPtr = "atk";
    const string DefPtr = "def";

    public static List<TrialResult> Run(Scenario s, Action<int>? progress = null)
    {
        // Two independent streams, both seeded: `sampler` picks each trial's stats, `combatRng`
        // makes the in-combat rolls. Splitting them means changing the stat ranges cannot shift the
        // roll sequence, so two sweep steps stay comparable rather than differing by RNG drift.
        var sampler = new Random(s.Seed);
        var combatRng = new SeededCombatRng(s.Seed);
        var harness = new FoundationHarness(s.Seed).WithShieldGate();

        OverlayCombatBreakdown? captured = null;
        var math = OverlayCombatMath.Create(
            harness.Resolve, ElementHub.Default, combatRng, (bd, _, _) => captured = bd);

        var defenderTypes = s.DefenderElement is { } de && ElementRoster.TryParse(de, out var parsed)
            ? ActorElementTypes.Create(parsed)
            : ActorElementTypes.Neutral;

        var policy = CombatPolicy.Default;
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnDamageDealt, Tick = 0 };
        var results = new List<TrialResult>(s.Trials);
        var skipped = new List<string>();

        for (var i = 0; i < s.Trials; i++)
        {
            captured = null;
            skipped.Clear();
            harness.ClearAll();
            harness.PinDerived(AtkPtr, Build(s.Attacker, sampler));
            harness.PinDerived(DefPtr, Build(s.Defender, sampler));
            harness.PinElementTypes(AtkPtr, ActorElementTypes.Neutral);
            harness.PinElementTypes(DefPtr, defenderTypes);

            var shieldHp = (long)Math.Round(s.ShieldHp.Sample(sampler));
            if (shieldHp > 0) harness.GrantShield(DefPtr, shieldHp);

            var baseDamage = (long)Math.Round(s.BaseDamage.Sample(sampler));
            var packet = new DamagePacket
            {
                PacketId = "sim-" + i,
                SourceGrantId = "sim",
                ActorPtr = AtkPtr,
                Target = new TargetSpec { Mode = TargetModes.Single, Ptr = DefPtr },
                SignedAmount = -baseDamage,
                ChainDepth = 0,
                ElementPayload = Payload(s, sampler)
            };

            CombatDamageDispatcher.DispatchInstant(
                packet, BoardSnapshot.Empty, ev, harness.Funnel, policy, combatRng, math, skipped,
                harness.Bag.ShieldGate,
                s.Reflection ? harness.Resolve : null);
            harness.Funnel.Flush();

            long defenderDelta = 0, attackerDelta = 0;
            var bounces = 0;
            foreach (var action in harness.Funnel.LastFlushedActions)
            {
                if (!action.Params.TryGetValue("targetPtr", out var tp)) continue;
                if (!action.Params.TryGetValue("amount", out var am) || am is null) continue;
                var amount = Convert.ToInt64(am);
                switch (tp as string)
                {
                    case DefPtr: defenderDelta += amount; break;
                    case AtkPtr:
                        attackerDelta += amount;
                        if (amount < 0) bounces++;
                        break;
                }
            }

            // captured is null when the packet carried no element payload: OverlayCombatMath.Finalize
            // early-returns for that case, so the calculator never ran and the raw amount stands.
            var preShield = captured?.FinalSignedDelta ?? -baseDamage;

            results.Add(new TrialResult(
                BaseDamage: baseDamage,
                Calculated: captured != null,
                Hit: captured?.Hit ?? true,
                Crit: captured?.Crit ?? false,
                Parried: captured?.Parried ?? false,
                Blocked: captured?.Blocked ?? false,
                PHit: captured?.PHitFinal ?? 1.0,
                PCrit: captured?.PCritFinal ?? 0.0,
                CritMult: captured?.CritMultiplierFinal ?? 1.0,
                WeightedDelta: captured?.WeightedDelta ?? 0.0,
                PreShieldDamage: -preShield,
                DefenderDamage: -defenderDelta,
                ShieldAbsorbed: Math.Max(0, -preShield - -defenderDelta),
                AttackerSelfDamage: -attackerDelta,
                ReflectBounces: bounces));

            if (progress != null && (i + 1) % 1000 == 0) progress(i + 1);
        }

        return results;
    }

    /// <summary>
    /// One fight to the death: the attacker swings until someone's HP runs out. Same real pipeline
    /// as <see cref="Run"/> — this only adds two HP pools and a loop, so reflected damage can
    /// actually finish the attacker instead of just being counted.
    /// </summary>
    public static List<FightResult> RunFights(Scenario s, Action<int>? progress = null)
    {
        var sampler = new Random(s.Seed);
        var combatRng = new SeededCombatRng(s.Seed);
        var harness = new FoundationHarness(s.Seed).WithShieldGate();
        var math = OverlayCombatMath.Create(harness.Resolve, ElementHub.Default, combatRng, null);

        var defenderTypes = s.DefenderElement is { } de && ElementRoster.TryParse(de, out var parsed)
            ? ActorElementTypes.Create(parsed)
            : ActorElementTypes.Neutral;

        var policy = CombatPolicy.Default;
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnDamageDealt, Tick = 0 };
        var fights = new List<FightResult>(s.Trials);
        var skipped = new List<string>();

        for (var f = 0; f < s.Trials; f++)
        {
            // Stats are rolled ONCE per fight, not per swing: a build is a build for the whole
            // engagement. Re-rolling each swing would average away exactly the extremes a tank
            // build is made of.
            harness.ClearAll();
            harness.PinDerived(AtkPtr, Build(s.Attacker, sampler));
            harness.PinDerived(DefPtr, Build(s.Defender, sampler));
            harness.PinElementTypes(AtkPtr, ActorElementTypes.Neutral);
            harness.PinElementTypes(DefPtr, defenderTypes);

            var shieldHp = (long)Math.Round(s.ShieldHp.Sample(sampler));
            if (shieldHp > 0) harness.GrantShield(DefPtr, shieldHp);

            var atkHp = (long)Math.Round(s.AttackerHp.Sample(sampler));
            var defHp = (long)Math.Round(s.DefenderHp.Sample(sampler));
            long dealt = 0, reflected = 0;
            var round = 0;

            while (round < s.MaxRounds && atkHp > 0 && defHp > 0)
            {
                round++;
                var baseDamage = (long)Math.Round(s.BaseDamage.Sample(sampler));
                skipped.Clear();
                CombatDamageDispatcher.DispatchInstant(
                    new DamagePacket
                    {
                        PacketId = $"fight-{f}-{round}",
                        SourceGrantId = "sim",
                        ActorPtr = AtkPtr,
                        Target = new TargetSpec { Mode = TargetModes.Single, Ptr = DefPtr },
                        SignedAmount = -baseDamage,
                        ChainDepth = 0,
                        ElementPayload = Payload(s, sampler)
                    },
                    BoardSnapshot.Empty, ev, harness.Funnel, policy, combatRng, math, skipped,
                    harness.Bag.ShieldGate,
                    s.Reflection ? harness.Resolve : null);
                harness.Funnel.Flush();

                foreach (var action in harness.Funnel.LastFlushedActions)
                {
                    if (!action.Params.TryGetValue("targetPtr", out var tp)) continue;
                    if (!action.Params.TryGetValue("amount", out var am) || am is null) continue;
                    var amount = Convert.ToInt64(am);
                    switch (tp as string)
                    {
                        case DefPtr: defHp += amount; dealt -= amount; break;
                        case AtkPtr: atkHp += amount; reflected -= amount; break;
                    }
                }
            }

            fights.Add(new FightResult(
                Rounds: round,
                AttackerDied: atkHp <= 0,
                DefenderDied: defHp <= 0,
                AttackerHpLeft: Math.Max(0, atkHp),
                DefenderHpLeft: Math.Max(0, defHp),
                DamageDealt: dealt,
                DamageReflected: reflected));

            if (progress != null && (f + 1) % 500 == 0) progress(f + 1);
        }

        return fights;
    }

    /// <summary>
    /// Mutual duels: both archetypes swing every round until one falls. Initiative alternates by
    /// trial so first-strike advantage averages out — without that, whichever side the caller
    /// happened to name first would win the close matchups and the matrix would read as a cycle
    /// when it was really just turn order.
    /// </summary>
    public static DuelSummary Duel(Archetype a, Archetype b, int trials, int seed, int maxRounds)
        => Duel(a, b, trials, seed, maxRounds, null);

    /// <summary>
    /// With <paramref name="actions"/> non-null, every swing is a real action that must be PAID FOR:
    /// the actor picks the best affordable one, pays at commit (a miss still pays — spec-action-costs
    /// §3), and pools regenerate lazily at the end of the round. An actor with nothing affordable
    /// passes and deals no damage that round.
    /// </summary>
    public static DuelSummary Duel(Archetype a, Archetype b, int trials, int seed, int maxRounds, ActionSet? actions)
        => Duel(a, b, trials, seed, maxRounds, actions, null);

    /// <summary>With <paramref name="status"/> non-null, a landed hit attempts a DoT through the
    /// shipped <see cref="FusionRpg.Core.Status.ResistanceEvaluator"/>; ticks land at the start of each
    /// following round.</summary>
    public static DuelSummary Duel(Archetype a, Archetype b, int trials, int seed, int maxRounds,
                                   ActionSet? actions, StatusProfile? status)
    {
        var sampler = new Random(seed);
        var combatRng = new SeededCombatRng(seed);
        var harness = new FoundationHarness(seed).WithShieldGate();
        // A status only lands on a hit, so the duel needs the breakdown it previously discarded.
        OverlayCombatBreakdown? captured = null;
        var math = OverlayCombatMath.Create(harness.Resolve, ElementHub.Default, combatRng,
            (bd, _, _) => captured = bd);
        var policy = CombatPolicy.Default;
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnDamageDealt, Tick = 0 };
        var skipped = new List<string>();

        int aWins = 0, bWins = 0, mutual = 0, stale = 0;
        double lostA = 0, lostB = 0; var rateSamples = 0;
        var roundCounts = new List<long>(trials);

        for (var t = 0; t < trials; t++)
        {
            harness.ClearAll();
            harness.PinDerived("a", Build(a.Stats, sampler));
            harness.PinDerived("b", Build(b.Stats, sampler));
            harness.PinElementTypes("a", Types(a.Element));
            harness.PinElementTypes("b", Types(b.Element));

            var aShield = (long)Math.Round(a.ShieldHp.Sample(sampler));
            var bShield = (long)Math.Round(b.ShieldHp.Sample(sampler));
            if (aShield > 0) harness.GrantShield("a", aShield);
            if (bShield > 0) harness.GrantShield("b", bShield);

            var hp = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["a"] = (long)Math.Round(a.Hp.Sample(sampler)),
                ["b"] = (long)Math.Round(b.Hp.Sample(sampler))
            };
            var hp0 = new Dictionary<string, long>(hp, StringComparer.Ordinal);
            // Regeneration, ticked. Without it a pool that refills reads as one that does not, and
            // the termination invariant (class-system-ideal.md §5d) cannot be tested at all.
            var regen = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["a"] = Stat(a, "resource.regen.hp"),
                ["b"] = Stat(b, "resource.regen.hp")
            };

            var pools = actions is null ? null : new Dictionary<string, ActorPools>(StringComparer.Ordinal)
            {
                ["a"] = new(a), ["b"] = new(b)
            };

            // Active DoTs: target ptr -> (damage per round, rounds left).
            var dots = new Dictionary<string, (double PerRound, double Left)>(StringComparer.Ordinal);
            // Rounds each actor is still disabled by cc. Refresh, not stack.
            var ccLeft = new Dictionary<string, double>(StringComparer.Ordinal) { ["a"] = 0, ["b"] = 0 };

            var aFirst = t % 2 == 0;
            var round = 0;
            while (round < maxRounds && hp["a"] > 0 && hp["b"] > 0)
            {
                round++;

                // DoT ticks first, so a status can finish a target the swing would have.
                if (status is not null)
                    foreach (var key in dots.Keys.ToList())
                    {
                        var (per, left) = dots[key];
                        if (left <= 0) { dots.Remove(key); continue; }
                        hp[key] -= (long)Math.Round(per);
                        dots[key] = (per, left - 1);
                    }

                foreach (var (atk, def, src) in aFirst
                             ? new[] { ("a", "b", a), ("b", "a", b) }
                             : new[] { ("b", "a", b), ("a", "b", a) })
                {
                    if (hp[atk] <= 0 || hp[def] <= 0) continue;
                    // A disabled actor does not act — the whole of what cc does.
                    if (ccLeft[atk] > 0) { ccLeft[atk] -= 1; continue; }
                    captured = null;

                    // The action economy. Without it every actor swings every round for free, which
                    // is the fight the resource-free model was measuring.
                    var swingBase = src.BaseDamage.Sample(sampler);
                    if (pools is not null)
                    {
                        var chosen = ActionPolicy.Choose(actions!, pools[atk], swingBase);
                        if (chosen.Cost is not null)
                        {
                            var cost = ActionPolicy.CostOf(chosen, swingBase);
                            // Committing is what costs, not landing — pay before the roll, always.
                            if (!pools[atk].TryPay(new[] { (chosen.Cost.ResourceId, cost) })) continue;
                        }
                        if (chosen.DamageMultiplier <= 0) continue;   // pass: paid nothing, did nothing
                        swingBase *= chosen.DamageMultiplier;
                    }

                    skipped.Clear();
                    CombatDamageDispatcher.DispatchInstant(
                        new DamagePacket
                        {
                            PacketId = $"duel-{t}-{round}-{atk}",
                            SourceGrantId = "sim",
                            ActorPtr = atk,
                            Target = new TargetSpec { Mode = TargetModes.Single, Ptr = def },
                            SignedAmount = -(long)Math.Round(swingBase),
                            ChainDepth = 0,
                            ElementPayload = OneElement(src.Element)
                        },
                        BoardSnapshot.Empty, ev, harness.Funnel, policy, combatRng, math, skipped,
                        harness.Bag.ShieldGate, harness.Resolve);
                    harness.Funnel.Flush();

                    foreach (var action in harness.Funnel.LastFlushedActions)
                    {
                        if (!action.Params.TryGetValue("targetPtr", out var tp)) continue;
                        if (!action.Params.TryGetValue("amount", out var am) || am is null) continue;
                        if (tp is string key && hp.ContainsKey(key)) hp[key] += Convert.ToInt64(am);
                    }

                    if (status is not null && captured?.Hit == true)
                    {
                        var target = def == "a" ? a : b;
                        var source = atk == "a" ? a : b;
                        var (applied, mag, dur) = StatusMath.Roll(source, target, status, swingBase, sampler);
                        if (applied && dur > 0)
                        {
                            // refresh, not stack — StatusRuntime owns the family mutex
                            if (status.IsCc) ccLeft[def] = dur;
                            else dots[def] = (mag, dur);
                        }
                    }
                }

                // Lazy accrual, once per round (spec-action-costs.md §2). Both actors regenerate
                // whether or not they acted — regen is per tick of simulated time, not per action.
                if (pools is not null) { pools["a"].Tick(); pools["b"].Tick(); }

                foreach (var key in new[] { "a", "b" })
                {
                    if (hp[key] <= 0) continue;                       // no regenerating out of death
                    var cap = hp0[key];
                    hp[key] = Math.Min(cap, hp[key] + (long)Math.Round(regen[key]));
                }
            }

            roundCounts.Add(round);
            if (round > 0)
            {
                lostA += (hp0["a"] - hp["a"]) / (double)round;
                lostB += (hp0["b"] - hp["b"]) / (double)round;
                rateSamples++;
            }
            var aDead = hp["a"] <= 0;
            var bDead = hp["b"] <= 0;
            if (aDead && bDead) mutual++;
            else if (bDead) aWins++;
            else if (aDead) bWins++;
            else stale++;
        }

        return new DuelSummary
        {
            A = a.Name, B = b.Name, Duels = trials,
            AWins = aWins / (double)trials,
            BWins = bWins / (double)trials,
            MutualKills = mutual / (double)trials,
            Stalemates = stale / (double)trials,
            MedianRounds = Percentiles.Of(roundCounts).Median,
            RateAgainstA = rateSamples == 0 ? 0 : lostA / rateSamples,
            RateAgainstB = rateSamples == 0 ? 0 : lostB / rateSamples
        };
    }

    static double Stat(Archetype a, string channel) =>
        a.Stats.TryGetValue(channel, out var r) ? (r.Min + r.Max) / 2.0 : 0.0;

    static ActorElementTypes Types(string? element) =>
        element is { } e && ElementRoster.TryParse(e, out var p) ? ActorElementTypes.Create(p) : ActorElementTypes.Neutral;

    static List<ElementPayloadComponentDto>? OneElement(string? element) =>
        element is { } e && ElementRoster.TryParse(e, out var p)
            ? new List<ElementPayloadComponentDto> { new() { Element = p.ToElementId(), Weight = 1.0 } }
            : null;

    static ActorDerivedSnapshot Build(Dictionary<string, StatRange> stats, Random rng)
    {
        if (stats.Count == 0) return ActorDerivedSnapshot.StubNeutral();
        var overlay = new KeyValuePair<string, double>[stats.Count];
        var i = 0;
        foreach (var (id, range) in stats)
            overlay[i++] = new KeyValuePair<string, double>(id, range.Sample(rng));
        return ActorDerivedSnapshot.StubNeutral().Overlay(overlay);
    }

    static List<ElementPayloadComponentDto>? Payload(Scenario s, Random rng)
    {
        switch (s.Elements)
        {
            case ElementMode.None:
                return null;
            case ElementMode.SingleRandom:
            {
                var e = ElementRoster.Concrete[rng.Next(ElementRoster.Concrete.Count)];
                return new List<ElementPayloadComponentDto>
                {
                    new() { Element = e.ToElementId(), Weight = 1.0 }
                };
            }
            case ElementMode.Fixed:
            {
                var list = s.FixedElements!;
                var weight = 1.0 / list.Count;
                return list.Select(x => new ElementPayloadComponentDto { Element = x, Weight = weight }).ToList();
            }
            default:
                return null;
        }
    }
}
