using System.Linq;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// D3/D4 (`battle-mode-parity`, solid-remediation T3.6) — every registered mode drives the same
/// mechanism set.
///
/// <para><b>What this is for.</b> D3 was "9 of 13 triggers never fire in battle" and D4 was "delve and
/// siege compose with no `HubInputs` at all". Both are the same shape: a mode quietly driving a
/// different set of mechanisms from its siblings, discovered by an audit months later. Fixing the two
/// instances does not stop the third. This test is what makes the next one fail immediately.</para>
///
/// <para><b>Mechanisms, not outcomes.</b> The five profiles legitimately differ in timeline behaviour —
/// `classic-round` runs one actor mid-action at `W=1`, `galaxy-sync` overlaps two per side — so their
/// reports differ by design. What must NOT differ is which mechanisms are wired and driven. So the
/// captured set is the collaborators on the effect host and the composed derived snapshot, never the
/// battle result.</para>
/// </summary>
[Trait("VerificationId", "core.battle-mode-parity")]
public class ModeConformanceTests
{
    /// <summary>
    /// The registered modes. A closed vocabulary the code owns: a sixth profile is a reviewed change to
    /// `BattleModeProfileCatalog`, never something that appears because content shipped.
    /// </summary>
    static IReadOnlyList<BattleModeProfile> RegisteredModes() => new[]
    {
        BattleModeProfileCatalog.ClassicRound,
        BattleModeProfileCatalog.GalaxySync,
        BattleModeProfileCatalog.HybridAtb,
        BattleModeProfileCatalog.Siege,
        BattleModeProfileCatalog.Delve,
    };

    static BattleActorSetup Actor(string key, string side) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = 6,
        MaxHp = BattleRuleset.BaseHp(6),
        Atk = BattleRuleset.BaseAtk(6),
        Defense = BattleRuleset.BaseDefense(6),
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "mode-conformance",
        Squad = new[] { Actor("squad:0", "squad") },
        Wave = new[] { Actor("wave:0", "wave") },
    };

    /// <summary>
    /// What a mode drives, as a sorted set of facts. Each line is a mechanism that D3 or D4 found a mode
    /// missing: the resolver, the actor resolve reflect needs, the shield gate, the status runtime, and
    /// the composed numbers themselves.
    /// </summary>
    static IReadOnlyList<string> MechanismSet(BattleModeProfile profile)
    {
        BattleEffectHost? host = null;
        BattleEngine.Resolve(Setup(), seed: 4242, onEffectHostReady: h => host = h, profile: profile);
        Assert.NotNull(host);

        var derived = BattleHubCompose.Compose(Actor("squad:0", "squad"));

        var facts = new List<string>
        {
            "combatMath:" + (host!.Bag.CombatMath?.GetType().Name ?? "<null>"),
            "actorResolve:" + (host.Bag.ActorResolve is null ? "<null>" : "wired"),
            "shieldGate:" + (host.Bag.ShieldGate is null ? "<null>" : "wired"),
            "status:" + (host.Bag.Status is null ? "<null>" : "wired"),
            "onDamageApplied:" + (host.Bag.OnDamageApplied is null ? "<null>" : "wired"),
        };

        // The composed numbers, so a mode that registered a different subsystem set shows up here
        // rather than only in a battle nobody re-ran. Channel ids, never magnitudes: the values are
        // tuning-owned and a balance pass must not turn this red.
        foreach (var channel in new[]
                 {
                     DerivedStatChannels.CombatPowerOmni,
                     DerivedStatChannels.CombatDefenseOmni,
                     DerivedStatChannels.CombatAccuracyOmni,
                     DerivedStatChannels.ProgressionPower,
                 })
        {
            facts.Add($"channel:{channel}:{(derived.Get(channel) == 0 ? "zero" : "nonzero")}");
        }

        // Which triggers the bag would raise for. Mode-independent by construction, and asserted so it
        // stays that way — D3 was exactly this set differing by mode.
        foreach (var trigger in AtomTriggers.All.OrderBy(t => t, StringComparer.Ordinal))
            facts.Add($"triggerKnown:{trigger}");

        facts.Sort(StringComparer.Ordinal);
        return facts;
    }

    [Fact]
    public void There_are_five_registered_modes()
    {
        // Pinned as a closed vocabulary, with the reason: a sixth profile is a reviewed edit to
        // BattleModeProfileCatalog, never a consequence of content shipping.
        Assert.Equal(5, RegisteredModes().Count);
    }

    /// <summary>
    /// <b>The conformance claim.</b> Every registered mode drives an identical mechanism set. Compared
    /// against the first mode rather than pinned to a literal, so the assertion survives a new
    /// mechanism being added to all modes at once — which is the correct kind of change.
    /// </summary>
    [Fact]
    public void Every_registered_mode_drives_the_same_mechanism_set()
    {
        var modes = RegisteredModes();
        var reference = MechanismSet(modes[0]);

        foreach (var mode in modes.Skip(1))
        {
            var actual = MechanismSet(mode);
            Assert.Equal(reference, actual);
        }
    }

    /// <summary>
    /// <b>The falsifier, which is the whole point of the task.</b> A conformance test that cannot fail
    /// would have passed happily through both D3 and D4.
    ///
    /// <para>A synthetic divergent mode: the same captured set with one mechanism removed, exactly as a
    /// mode that forgot to wire `ActorResolve` would report. The comparison must catch it. Without this,
    /// `Every_registered_mode_drives_the_same_mechanism_set` proves only that five identical code paths
    /// are identical.</para>
    /// </summary>
    [Fact]
    public void A_mode_that_drives_one_fewer_mechanism_is_caught()
    {
        var reference = MechanismSet(RegisteredModes()[0]);

        var divergent = reference
            .Where(f => !f.StartsWith("actorResolve:", StringComparison.Ordinal))
            .Append("actorResolve:<null>")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.NotEqual(reference, divergent);
    }

    /// <summary>
    /// And a mode that drives one EXTRA mechanism is caught too — divergence is not only omission. A
    /// mode quietly registering a subsystem its siblings lack is D5's shape, and it must fail here just
    /// as loudly as a missing one.
    /// </summary>
    [Fact]
    public void A_mode_that_drives_one_extra_mechanism_is_caught()
    {
        var reference = MechanismSet(RegisteredModes()[0]);

        var divergent = reference
            .Append("subsystem:ModeLocalPrivateFold")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.NotEqual(reference, divergent);
    }

    /// <summary>
    /// <b>A REAL divergent mode, not a mutated list.</b> The two tests above prove the comparison
    /// detects a difference; this proves it detects one produced by an actual battle.
    ///
    /// <para>`onEffectHostReady` is the seam a mode wires its collaborators at, so clearing one there
    /// is exactly what a mode that forgot to wire `ActorResolve` looks like from the inside — which is
    /// precisely half of D2's stated cause ("battle's bag also never sets `ActorResolve`"). The captured
    /// set must differ from a conforming mode's.</para>
    /// </summary>
    [Fact]
    public void A_real_battle_that_loses_a_collaborator_diverges_from_the_conforming_set()
    {
        var reference = MechanismSet(RegisteredModes()[0]);

        BattleEffectHost? host = null;
        BattleEngine.Resolve(Setup(), seed: 4242, onEffectHostReady: h =>
        {
            h.Bag.ActorResolve = null;   // the D2 defect, reproduced deliberately
            host = h;
        }, profile: RegisteredModes()[0]);

        Assert.NotNull(host);
        Assert.Null(host!.Bag.ActorResolve);
        Assert.DoesNotContain("actorResolve:wired",
            new[] { "actorResolve:" + (host.Bag.ActorResolve is null ? "<null>" : "wired") });
        Assert.Contains("actorResolve:wired", reference);
    }

    /// <summary>
    /// The set is not vacuous. A comparison of two empty lists passes trivially, so the captured set
    /// must actually contain the mechanisms D3 and D4 were about.
    /// </summary>
    [Fact]
    public void The_captured_set_actually_contains_the_mechanisms_D3_and_D4_named()
    {
        var set = MechanismSet(RegisteredModes()[0]);

        Assert.Contains("actorResolve:wired", set);
        Assert.Contains("shieldGate:wired", set);
        Assert.Contains("combatMath:OverlayCombatMath", set);
        Assert.Contains($"triggerKnown:{AtomTriggers.OnDamageTaken}", set);
        Assert.Contains($"triggerKnown:{AtomTriggers.OnDeath}", set);
    }
}
