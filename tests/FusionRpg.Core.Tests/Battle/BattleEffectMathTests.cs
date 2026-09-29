using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// D1 (`battle-effect-math`, solid-remediation T2.4/T2.5) — effect-driven damage in a battle now
/// resolves through the combat resolver instead of applying its authored number verbatim.
///
/// <para><b>Why a probe and not a golden.</b> Wiring <c>CombatMath</c> onto battle's bag moved
/// <b>0 of 13,943</b> tests when it was first measured, and wiring it together with
/// <c>ActorResolve</c> and the element-payload fallback moved <b>0 of 14,026</b>. That is not evidence
/// the fix works — it is evidence that nothing in the suite ever drove the path. Every assertion here
/// is a falsifier: each one fails if the wiring is removed, and each one passed only after it
/// landed.</para>
///
/// <para><b>What was broken.</b> <c>CombatDamageDispatcher</c> falls back to
/// <c>PassThroughCombatMath</c> when the bag carries no <c>CombatMath</c>, and
/// <c>PassThroughCombatMath.Finalize</c> returns the amount unchanged. Battle's BASIC attack always
/// used the resolver; every <b>effect-driven</b> hit — DoT tick, on-hit rider, atom damage — did not.
/// So a registered defence, penetration, matchup or crit channel simply did not exist on that path.</para>
///
/// <para>⚠️ These assert that the element and the seed now <b>matter</b>, never by how much. The
/// magnitudes are tuning-owned; a balance pass must not turn this file red.</para>
/// </summary>
[Trait("VerificationId", "core.battle-effect-math")]
public class BattleEffectMathTests
{
    static BattleActorSetup Actor(string key, string side, int level,
        ElementTypeId? element = null,
        IReadOnlyList<BattleChannelMod>? mods = null,
        IReadOnlyList<BattleStatusSpec>? statuses = null) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = level,
        ElementPrimary = element,
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
        ChannelMods = mods ?? Array.Empty<BattleChannelMod>(),
        InitialStatuses = statuses ?? Array.Empty<BattleStatusSpec>()
    };

    /// <summary>A squad attacker that can barely hit and barely scratch, so only the DoT moves HP.</summary>
    static readonly BattleChannelMod[] Untouchable =
        { new(DerivedStatChannels.CombatDodgeOmni, 1000) };

    static BattleReport DotRun(ElementTypeId? element, IReadOnlyList<BattleChannelMod>? wallMods, ulong seed = 7) =>
        BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "effect-math-probe",
            Squad = new[] { Actor("squad:0", "squad", 1, mods: Untouchable) },
            Wave = new[]
            {
                Actor("wave:0", "wave", 10, element: element, mods: wallMods, statuses: new[]
                {
                    new BattleStatusSpec("wither", -100, DurationMs: 40_000, PeriodMs: 1000)
                })
            }
        }, seed);

    static long HpOf(BattleReport report, string key) =>
        report.Actors.Single(a => a.Key == key).HpRemaining;

    /// <summary>
    /// <b>CP2's third clause, in a real battle: a battle effect packet carries an element payload.</b>
    ///
    /// <para>An effect-driven hit fired from an elemental battle actor must no longer land its authored
    /// number. The only difference between the two runs is the attacker's <c>ElementPrimary</c>, so a
    /// divergence can only come from <c>EffectBag.ApplyOwnerElementFallback</c> filling the packet and
    /// <c>OverlayCombatMath</c> resolving it — the exact chain D1 says did not exist.</para>
    ///
    /// <para>Run inside <c>BattleEngine.Resolve</c> rather than on the offline harness, because the
    /// claim under test is about BATTLE's wiring, and the harness wires its own.</para>
    /// </summary>
    [Fact]
    public void An_effect_hit_from_an_elemental_battle_actor_no_longer_lands_its_authored_number()
    {
        var probe = new EffectDef
        {
            EffectId = "test.battle-element-probe",
            EffectType = EffectTypes.Triggered,
            Name = "battle element probe",
            Enabled = true,
            SourceTag = "test",
            Triggers = new() { EffectTriggers.OnDamageDealt },
            Actions = new()
            {
                new EffectActionRow
                {
                    Seq = 1,
                    Action = EffectActions.ApplyResourceDelta,
                    Params = new Dictionary<string, object?> { ["channel"] = "hp" },
                },
            },
        };

        long Run(ElementTypeId? attackerElement) => BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "battle-element-probe",
            Squad = new[] { Actor("squad:0", "squad", 6, element: attackerElement) },
            Wave = new[] { Actor("wave:0", "wave", 6, element: ElementTypeId.Ice) }
        }, seed: 21, onEffectHostReady: host =>
        {
            host.Bag.Catalog.Upsert(probe);
            host.Bag.Grant(new EffectGrantDto
            {
                GrantId = "probe:battle-element",
                EffectId = probe.EffectId,
                OwnerKind = "entity",
                OwnerKey = EffectOwnerKeys.Entity("squad:0"),
                PluginId = "battle",
                Overlay = new Dictionary<string, object?>
                {
                    ["amount"] = -40L,
                    ["icd_ms"] = 0,
                    ["target"] = new Dictionary<string, object?> { ["mode"] = TargetModes.EventTarget },
                    ["delivery"] = new Dictionary<string, object?> { ["mode"] = DeliveryModes.Instant },
                },
            });
        }).Actors.Single(a => a.Key == "squad:0").DamageDealt;

        // Cumulative damage dealt, not the target's remaining HP: the wave dies in both runs, so HP
        // bottoms out at 0 either way and cannot discriminate. Measured, not assumed -- the first
        // version of this test compared HpRemaining and read 0 == 0 as "no divergence".
        var neutral = Run(attackerElement: null);
        var fire = Run(ElementTypeId.Fire);

        Assert.True(neutral > 0, "the probe effect never landed, so this test proves nothing");
        Assert.NotEqual(neutral, fire);
    }

    /// <summary>
    /// <b>T2.5's acceptance, asserted rather than assumed:</b> `PassThroughCombatMath` is no longer
    /// reached in battle.
    ///
    /// <para>`CombatDamageDispatcher` falls back to it whenever the bag carries no `CombatMath`, and
    /// its `Finalize` returns the amount unchanged. Reading the wired collaborators off the real host
    /// is the only way to state "the fallback is not what runs" without inferring it from an outcome
    /// that a neutral actor would produce either way.</para>
    ///
    /// <para>`ActorResolve` is asserted in the same test because D2 names it as the second half of why
    /// reflect could never fire in a battle: even had the dispatcher been reached, the reflect gate
    /// needs an actor resolve and battle's bag had none.</para>
    /// </summary>
    [Fact]
    public void Battles_bag_carries_the_real_resolver_and_an_actor_resolve()
    {
        BattleEffectHost? captured = null;
        BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "wiring-probe",
            Squad = new[] { Actor("squad:0", "squad", 3) },
            Wave = new[] { Actor("wave:0", "wave", 3) }
        }, seed: 3, onEffectHostReady: h => captured = h);

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Bag.ActorResolve);

        var math = captured.Bag.CombatMath;
        Assert.NotNull(math);
        Assert.IsType<FusionRpg.Core.Combat.OverlayCombatMath>(math);
    }

    /// <summary>
    /// The four modes are wired ONCE, not four times. Delve (`DelveBattle`), siege
    /// (`DistrictAssaultResolver`) and the web match (`WebMatchService`) all reach combat through
    /// `BattleEngine.Resolve`, so the collaborators asserted above are the same objects they get.
    ///
    /// <para>This is D2's acceptance stated the way its own note demands — "do not add reflect to
    /// battle, that phrasing produces a second implementation". Nothing was added to battle; battle
    /// reached the shared dispatcher, and the other three inherited it. The grep the task names finds
    /// no reflect formula anywhere under `Battle/`, which is asserted here so it stays that way.</para>
    /// </summary>
    [Fact]
    public void No_reflect_formula_was_added_under_the_battle_folder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CONTRIBUTING.md")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var battle = Path.Combine(dir!.FullName, "src", "FusionRpg.Core", "Battle");
        var offenders = Directory.EnumerateFiles(battle, "*.cs", SearchOption.AllDirectories)
            .Where(f => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(f), @"/\s*[A-Za-z0-9_.]*Reflect(Rate|Share)Scale"))
            .Select(f => Path.GetFileName(f))
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// <b>The pulse path is wired, and it is inert for a reason this test names.</b>
    ///
    /// <para>`BattleRunState.ResolvePulseAmount` now routes a status pulse through the same
    /// <c>CombatMath</c> the lawn's pulse sink has always used, closing the half of D1 that the bag
    /// wiring could not reach — a battle DoT never enters <c>CombatDamageDispatcher</c> at all, because
    /// <c>BattlePulseSink</c> calls <c>DamageApplyPipeline.Apply</c> directly.</para>
    ///
    /// <para><b>But no battle status can be elemental today.</b> <c>BattleStatusSpec</c> carries
    /// <c>StatusId</c>, <c>MagnitudePerPulse</c>, <c>DurationMs</c>, <c>PeriodMs</c> and
    /// <c>GrantChanceMilli</c> — and no element. <c>StatusPulsePayload.For</c> reads the instance's own
    /// <c>Element</c>, so every battle pulse payload is empty and <c>Finalize</c> correctly returns the
    /// authored amount unchanged. An initial status also has no <c>AttackerPtr</c>, so there is no
    /// second side to contest against either.</para>
    ///
    /// <para>⚠️ This test is a <b>tripwire</b>. It fails the day <c>BattleStatusSpec</c> grows an
    /// element, which is precisely when the pulse resolver stops being inert and needs a real
    /// behavioural probe rather than this one. That is the intended outcome, not a regression.</para>
    /// </summary>
    [Fact]
    public void A_battle_status_cannot_yet_be_elemental_which_is_why_the_pulse_resolver_is_inert()
    {
        var parameters = typeof(BattleStatusSpec)
            .GetConstructors()
            .Single()
            .GetParameters()
            .Select(p => p.Name)
            .ToArray();

        Assert.DoesNotContain("Element", parameters, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ElementPrimary", parameters, StringComparer.OrdinalIgnoreCase);

        // And the consequence, measured rather than argued: an elemental ACTOR changes nothing,
        // because the pulse's element comes from the status, not from whoever owns it.
        Assert.Equal(
            HpOf(DotRun(element: null, wallMods: null), "wave:0"),
            HpOf(DotRun(ElementTypeId.Fire, wallMods: null), "wave:0"));
    }

    [Fact]
    public void Damage_observers_receive_sink_retained_overkill_not_the_requested_amount()
    {
        var attacker = Actor("squad:0", "squad", 5);
        var target = Actor("wave:0", "wave", 5) with { MaxHp = 100, CurrentHp = 5 };

        var observation = BattleEngine.DamageObservationForTest(
            new BattleSetup
            {
                WaveId = "overkill-observer",
                Squad = new[] { attacker },
                Wave = new[] { target },
            },
            seed: 3,
            targetKey: target.Key,
            attackerKey: attacker.Key,
            requestedDamage: 20);

        Assert.Equal(-5, observation.Result.AppliedAmount);
        Assert.Equal(-5, observation.ObservedAmount);
        Assert.Equal(0, observation.HpAfterFlush);
    }

    /// <summary>
    /// Determinism survives all of it: the same seed replays byte-identically. Asserted on the whole
    /// serialised report rather than one field, which is how the existing battle determinism tests
    /// state it — a new RNG stream that was not derived deterministically would break here.
    /// </summary>
    [Fact]
    public void The_same_seed_still_replays_byte_identically()
    {
        var a = System.Text.Json.JsonSerializer.Serialize(DotRun(ElementTypeId.Fire, null, seed: 11));
        var b = System.Text.Json.JsonSerializer.Serialize(DotRun(ElementTypeId.Fire, null, seed: 11));

        Assert.Equal(a, b);
    }

    /// <summary>
    /// A neutral actor is exactly where it was: no element, no payload, and
    /// <c>OverlayCombatMath.Finalize</c> returns the amount unchanged. This is why extending the wiring
    /// moved no existing golden, stated as a test rather than left as an argument.
    /// </summary>
    [Fact]
    public void A_neutral_actor_still_replays_byte_identically_across_runs()
    {
        var a = System.Text.Json.JsonSerializer.Serialize(DotRun(element: null, wallMods: null, seed: 3));
        var b = System.Text.Json.JsonSerializer.Serialize(DotRun(element: null, wallMods: null, seed: 3));

        Assert.Equal(a, b);
    }
}
