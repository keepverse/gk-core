using FusionRpg.Contracts;
using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Diagnostics;

/// <summary>class-system-todo.md V5 — named point-in-time gauges (progression.power at resolve, and
/// the seam P7.2/P8.2's poise/stamina metrics will use once those runtimes exist). Serialized against
/// PerfProbeTests' own [Collection("PerfProbe")] group so ResetAll/SnapshotAndReset calls do not
/// interleave with that file's — see that file's own header comment for why PerfProbe's global state
/// forces this.</summary>
[Collection("PerfProbe")]
public class PerfProbeValueTests
{
    [Fact]
    public void RecordValue_roundTripsThroughSnapshot()
    {
        PerfProbe.ResetAll();
        PerfProbe.RecordValue("test.gauge.roundtrip", 47.5);

        var window = PerfProbe.SnapshotAndReset();
        var values = Assert.IsType<Dictionary<string, object>>(window["values"]);
        Assert.Equal(47.5, values["test.gauge.roundtrip"]);
    }

    [Fact]
    public void RecordValue_latestWriteWinsBeforeSnapshot()
    {
        PerfProbe.ResetAll();
        PerfProbe.RecordValue("test.gauge.latest", 1.0);
        PerfProbe.RecordValue("test.gauge.latest", 2.0);
        PerfProbe.RecordValue("test.gauge.latest", 3.0);

        var window = PerfProbe.SnapshotAndReset();
        var values = Assert.IsType<Dictionary<string, object>>(window["values"]);
        Assert.Equal(3.0, values["test.gauge.latest"]);
    }

    [Fact]
    public void Snapshot_clearsValues()
    {
        PerfProbe.ResetAll();
        PerfProbe.RecordValue("test.gauge.clears", 9.0);
        var first = PerfProbe.SnapshotAndReset();
        Assert.True(((Dictionary<string, object>)first["values"]).ContainsKey("test.gauge.clears"));

        var second = PerfProbe.SnapshotAndReset();
        Assert.False(((Dictionary<string, object>)second["values"]).ContainsKey("test.gauge.clears"));
    }

    [Fact]
    public void Disabled_probe_recordsNoValue()
    {
        PerfProbe.ResetAll();
        var was = PerfProbe.Enabled;
        try
        {
            PerfProbe.Enabled = false;
            PerfProbe.RecordValue("test.gauge.disabled", 5.0);
        }
        finally { PerfProbe.Enabled = was; }

        var window = PerfProbe.SnapshotAndReset();
        var values = Assert.IsType<Dictionary<string, object>>(window["values"]);
        Assert.False(values.ContainsKey("test.gauge.disabled"));
    }

    [Fact]
    public void EmptyName_isIgnored()
    {
        PerfProbe.ResetAll();
        PerfProbe.RecordValue("", 1.0);
        PerfProbe.RecordValue(null!, 2.0);
        var window = PerfProbe.SnapshotAndReset();
        var values = Assert.IsType<Dictionary<string, object>>(window["values"]);

        // Assert the two BAD names are absent — not that the whole bag is empty.
        //
        // `PerfProbe` is a process-global and xUnit runs test classes in PARALLEL, while
        // `ActorHub.ResolveDerived` records three gauges on EVERY resolve. So any class resolving stats
        // between this test's `ResetAll()` and `SnapshotAndReset()` lands a legitimate value in the
        // window, and `Assert.Empty(values)` fails through no fault of the code under test. It held
        // only by winning a race, and grew flakier as the suite gained resolve-heavy tests
        // (aura-skill Phase 5 added ~60, several thousand resolves) — observed failing once in a full
        // run, then passing 3/3 isolated and 3/3 full.
        //
        // This is the idiom the sibling test above already uses (`Assert.False(values.ContainsKey(...))`)
        // and it tests the actual claim: an empty or null gauge name is ignored.
        Assert.False(values.ContainsKey(""), "an empty gauge name must be ignored");
        Assert.DoesNotContain(values.Keys, k => string.IsNullOrWhiteSpace(k));
    }

    [Fact]
    public void SeededResolve_emitsProgressionPower()
    {
        // P1.10's own symptom, pinned as a regression name: an un-hydrated IPowerIndexProvider makes
        // every magnitude collapse to P(0) because Theta reads 0. This proves the metric that makes
        // that detectable from emitted numbers rather than only from a debugger.
        PerfProbe.ResetAll();
        var stats = StatSystemBootstrap.CreateDefault();
        var hub = ActorHubBootstrap.CreateDefault(stats, powerIndex: new FixedPowerIndexProvider(47));
        var ctx = stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        hub.ResolveDerived(ctx);

        var window = PerfProbe.SnapshotAndReset();
        var values = Assert.IsType<Dictionary<string, object>>(window["values"]);
        Assert.True(values.ContainsKey(DerivedStatChannels.ProgressionPower),
            "expected progression.power to be emitted by ActorHub.ResolveDerived");
        Assert.NotEqual(0.0, (double)values[DerivedStatChannels.ProgressionPower]);
    }

    [Fact]
    public void UnhydratedResolve_emitsZeroProgressionPower()
    {
        // The complementary case: the shipped default (StubPowerIndexProvider, Theta=0) must emit
        // exactly 0, not omit the key -- "Theta is zero" has to be a value the metric reports, not an
        // absence a consumer would misread as "not measured".
        PerfProbe.ResetAll();
        var hub = ActorHubBootstrap.CreateDefault();
        var ctx = hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        hub.ResolveDerived(ctx);

        var window = PerfProbe.SnapshotAndReset();
        var values = Assert.IsType<Dictionary<string, object>>(window["values"]);
        Assert.True(values.ContainsKey(DerivedStatChannels.ProgressionPower));
        Assert.Equal(0.0, (double)values[DerivedStatChannels.ProgressionPower]);
    }

    [Fact]
    public void SeededResolve_emitsStaminaRegen()
    {
        // class-system-todo.md V5/P8.2: the regen half of "stamina binds" (r = cost/regen, spec-
        // residual-fit.md 2.2) must be observable from emitted metrics, matching progression.power's
        // own precedent exactly -- ActorHub.ResolveDerived's second RecordValue call site.
        PerfProbe.ResetAll();
        var hub = ActorHubBootstrap.CreateDefault(powerIndex: new FixedPowerIndexProvider(100));
        var ctx = hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        hub.ResolveDerived(ctx);

        var window = PerfProbe.SnapshotAndReset();
        var values = Assert.IsType<Dictionary<string, object>>(window["values"]);
        Assert.True(values.ContainsKey(DerivedStatChannels.ResourceRegen("stamina")),
            "expected resource.regen.stamina to be emitted by ActorHub.ResolveDerived");
    }

    [Fact]
    public void SeededResolve_emitsPoiseRegen()
    {
        // class-system-todo.md V5/P7.1: the poise pool exists (Checkpoint 7; unified onto
        // PoiseLedger/ActorResourcePools by battle-tempo poise-unification, 2026-09-05), so the
        // mechanism this metric needs is live -- but no aptitude edge feeds resource.regen.poise in the real shipped
        // config yet (P7.2's own named, still-open gap), so this reads 0 on a real resolve, matching
        // UnhydratedResolve_emitsZeroProgressionPower's own "present, not omitted" contract: "not fed
        // yet" is a value this metric reports, not a silently missing key.
        PerfProbe.ResetAll();
        var hub = ActorHubBootstrap.CreateDefault(powerIndex: new FixedPowerIndexProvider(100));
        var ctx = hub.Stats.Contexts.ForPlant("P1", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });

        hub.ResolveDerived(ctx);

        var window = PerfProbe.SnapshotAndReset();
        var values = Assert.IsType<Dictionary<string, object>>(window["values"]);
        Assert.True(values.ContainsKey(DerivedStatChannels.ResourceRegen("poise")),
            "expected resource.regen.poise to be emitted by ActorHub.ResolveDerived");
        Assert.Equal(0.0, (double)values[DerivedStatChannels.ResourceRegen("poise")]);
    }

    [Fact]
    public void Predict_emitsPeerDamagePerRound_forBothSides()
    {
        // class-system-todo.md V5/P7.2: the other half of guard-economy's own r = poiseRegen /
        // peerDamage (spec-guard-economy.md 5d.3's own "regen sized against PEER DAMAGE" rule).
        // ActorHubBootstrap.CreateDefault() needs no AptitudeTuningHub configuration (confirmed by the
        // sibling UnhydratedResolve_emitsZeroProgressionPower test above, already relying on exactly
        // this), so this test avoids the AptitudeTuningHub race hazard TerminationGuardTests/
        // DominanceGuardTests' own [Collection("AptitudeTuningHub")] exists for -- this file stays in
        // [Collection("PerfProbe")] instead, and xUnit allows only one collection per class.
        PerfProbe.ResetAll();
        var hub = ActorHubBootstrap.CreateDefault(powerIndex: new FixedPowerIndexProvider(100));
        var ctxA = hub.Stats.Contexts.ForPlant("A", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var ctxB = hub.Stats.Contexts.ForPlant("B", new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 });
        var snapshotA = new CombatActorSnapshot(hub.ResolveDerived(ctxA), ActorElementTypes.Neutral);
        var snapshotB = new CombatActorSnapshot(hub.ResolveDerived(ctxB), ActorElementTypes.Neutral);
        var actorA = new Predictor.Actor("A", snapshotA, Hp: 100, BaseDamage: 50.0, ShieldMaxHp: 0);
        var actorB = new Predictor.Actor("B", snapshotB, Hp: 100, BaseDamage: 50.0, ShieldMaxHp: 0);

        Predictor.Predict(actorA, actorB);

        var window = PerfProbe.SnapshotAndReset();
        var values = Assert.IsType<Dictionary<string, object>>(window["values"]);
        Assert.True(values.ContainsKey("balance.peerDamagePerRoundAgainstA"),
            "expected balance.peerDamagePerRoundAgainstA to be emitted by Predictor.Predict");
        Assert.True(values.ContainsKey("balance.peerDamagePerRoundAgainstB"),
            "expected balance.peerDamagePerRoundAgainstB to be emitted by Predictor.Predict");
        // BaseDamage: 50.0 on both sides guarantees a real, positive, easy-to-reason-about exchange —
        // not zero, and not dependent on any aptitude-fed combat.power.omni edge existing.
        Assert.True((double)values["balance.peerDamagePerRoundAgainstA"] > 0);
        Assert.True((double)values["balance.peerDamagePerRoundAgainstB"] > 0);
    }

    sealed class FixedPowerIndexProvider : IPowerIndexProvider
    {
        readonly int _theta;
        public FixedPowerIndexProvider(int theta) => _theta = theta;
        public int ActorIndex(StatContext ctx) => _theta;
        public int ContentIndex(ContentContext ctx) => _theta;
        public PowerAxisReport Explain(StatContext ctx) => new(_theta, Array.Empty<PowerAxisContribution>());
        public int ActorIndexFor(FusionRpg.Core.Saves.SaveId save, FusionRpg.Core.Commanders.EmpireId empire) => _theta;
    }
}
