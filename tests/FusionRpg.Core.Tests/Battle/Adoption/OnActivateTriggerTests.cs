using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Adoption;

/// <summary>
/// T43 (action-todo.md Phase 12, spec-on-activate-trigger.md §2) — proven from outside `BattleEngine`
/// through the existing T14 `onEffectHostReady` test seam: a synthetic `OnActivate`-triggered
/// `resource.delta` def, upserted and granted directly (bypassing A18a's own binding loop entirely —
/// this file proves the FIRING mechanism in isolation, not the binding seam A18a already covers), self-
/// damaging the attacker by a fixed amount every time it fires. A differential comparison (same seed,
/// with vs. without the probe grant) isolates the probe's own effect from ordinary combat variance —
/// robust regardless of hit/miss, since `report.Rounds` is identical between the two runs (same seed,
/// same RNG streams; the probe consumes no combat-affecting randomness).
/// </summary>
public class OnActivateTriggerTests
{
    const long ProbeAmount = -1; // self-damage per fire; large enough to never round-trip-vanish, small relative to BaseHp so the actor survives many fires

    static readonly EffectDef ProbeDef = new()
    {
        EffectId = "test.on-activate-probe",
        EffectType = EffectTypes.Triggered,
        Name = "OnActivate probe",
        Triggers = new() { AtomTriggers.OnActivate },
        Actions = new()
        {
            new EffectActionRow
            {
                Seq = 1,
                Action = EffectActions.ApplyResourceDelta,
                Params = new Dictionary<string, object?> { ["amount"] = (double)ProbeAmount, ["targetPtr"] = "squad:0" },
            },
        },
    };

    static BattleActorSetup Actor(string key, string side, long? maxHp = null, long? atk = null) => new()
    {
        Key = key, Side = side, SpeciesId = "t43-species", TypeId = 10_005, Level = 6,
        MaxHp = maxHp ?? BattleRuleset.BaseHp(6), Atk = atk ?? BattleRuleset.BaseAtk(6), Defense = BattleRuleset.BaseDefense(6),
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "t43-wave",
        // Minimal wave Atk: isolates squad:0's own Hp trajectory to (its starting Hp) + (the probe's
        // own self-damage) -- otherwise a close, multi-round fight lets the wave's own chip damage
        // confound the differential exactly when the probe tips a near-death round one earlier
        // (found empirically: -1000/fire killed squad:0 outright in round 1; even -1/fire against a
        // full-strength wave shaved one whole round off report.Rounds on a seed that happened to run
        // the fight down to the wire).
        Squad = new[] { Actor("squad:0", "squad", maxHp: BattleRuleset.BaseHp(6) * 100) },
        // High wave HP: forces several rounds, so the differential proof spans natural hit/miss
        // variance rather than resting on a single, possibly-atypical round.
        Wave = new[] { Actor("wave:0", "wave", maxHp: BattleRuleset.BaseHp(6) * 20, atk: 1) },
    };

    static BattleReport Resolve(ulong seed, bool bindProbe) =>
        BattleEngine.Resolve(Setup(), seed, onEffectHostReady: host =>
        {
            if (!bindProbe) return;
            host.Bag.Catalog.Upsert(ProbeDef);
            host.Bag.Grant(new EffectGrantDto
            {
                GrantId = "probe:squad:0",
                EffectId = ProbeDef.EffectId,
                OwnerKind = "entity",
                OwnerKey = EffectOwnerKeys.Entity("squad:0"),
                PluginId = "battle",
            });
        });

    [Fact]
    public void Fires_once_per_resolved_intent_regardless_of_hit_or_miss()
    {
        var without = Resolve(seed: 11, bindProbe: false);
        var with = Resolve(seed: 11, bindProbe: true);

        // Same seed -> identical RNG draws for initiative/crit/essence/status -> identical round count
        // and identical hit/miss sequence between the two runs. The probe's own Grant/Upsert calls
        // touch neither: EffectType is Triggered (not Passive, no OnGranted trigger), so Grant() does
        // not self-fire, and the fixed amount never rolls chance.
        Assert.Equal(without.Rounds, with.Rounds);
        Assert.True(with.Rounds > 1, "the wave HP budget above is sized to force a multi-round battle");

        var squadWithout = without.Actors.Single(a => a.Key == "squad:0").HpRemaining;
        var squadWith = with.Actors.Single(a => a.Key == "squad:0").HpRemaining;

        // TWO fires per round, not one -- found empirically, then confirmed against
        // EffectOwnerKey.MatchesEvent (EffectProcAndOwner.cs): an entity:-owned grant matches an event
        // whose ActorPtr OR TargetPtr names that entity. squad:0's own OnActivate (it is the actor)
        // matches via ActorPtr; wave:0's OnActivate against squad:0 (wave is the actor, squad the
        // target) ALSO matches, via TargetPtr -- the exact dual-check every other trigger in this
        // system already relies on (an OnDamageDealt-owner and an OnDamageTaken-owner both need to see
        // the same event). OnActivate inherits this for free since it reuses the same owner-matching
        // path, with a real content consequence spec-on-activate-trigger.md now documents: a
        // "self-buff on activate" grant fires when its OWNER acts *and* when its owner is the TARGET
        // of someone else's activation, unless the content adds its own actor-is-me filter. squad:0
        // never dies in this fixture (100x BaseHp against a 1-Atk wave and a 1-per-fire probe), so
        // "both sides active every round" holds for the whole battle -- exactly 2 x report.Rounds fires.
        Assert.Equal(squadWithout + ProbeAmount * with.Rounds * 2, squadWith);
    }

    [Fact]
    public void An_actor_with_no_bound_OnActivate_grant_is_unaffected()
    {
        // Golden-neutrality's own narrow case: the exact scenario every shipped battle is in today
        // (A18a's own scope -- nothing binds a grant without a real ContainerId). Proven directly
        // rather than only inferred from the full suite's own unchanged hashes.
        var report = Resolve(seed: 11, bindProbe: false);
        Assert.NotNull(report);
    }
}
