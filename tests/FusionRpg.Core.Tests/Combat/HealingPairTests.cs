using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>spec-healing-pair.md — resource.restore.hp (Pool, unpaired, uncapped) and the `+heal.power`
/// term OverlayCombatMath.FinalizeHeal adds to the shipped heal pass-through.</summary>
public class HealingPairTests
{
    [Fact]
    public void NoGoldensMoveAtZero()
    {
        // heal.power = 0 (no snapshot authors it) -> byte-identical to the old bare pass-through.
        var math = OverlayCombatMath.Create(
            (_, _) => new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral));
        var packet = new DamagePacket { SignedAmount = 50, ActorPtr = "P1" };
        Assert.Equal(50, math.Finalize(50, "Z1", packet, null));
    }

    [Fact]
    public void HealPowerScalesHeal()
    {
        var healerSnapshot = new CombatActorSnapshot(
            ActorDerivedSnapshot.FromValues(new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.ResourceRestore("hp"), 20)
            }),
            ActorElementTypes.Neutral);
        var math = OverlayCombatMath.Create((ptr, attackerLess) => healerSnapshot);
        var packet = new DamagePacket { SignedAmount = 50, ActorPtr = "P1" };

        Assert.Equal(70, math.Finalize(50, "Z1", packet, null));
    }

    [Fact]
    public void HealNeverNegative()
    {
        // An overlay heal can never become damage, even with an absurdly negative heal.power.
        var healerSnapshot = new CombatActorSnapshot(
            ActorDerivedSnapshot.FromValues(new[]
            {
                new KeyValuePair<string, double>(DerivedStatChannels.ResourceRestore("hp"), -1000)
            }),
            ActorElementTypes.Neutral);
        var math = OverlayCombatMath.Create((ptr, attackerLess) => healerSnapshot);
        var packet = new DamagePacket { SignedAmount = 50, ActorPtr = "P1" };

        Assert.Equal(0, math.Finalize(50, "Z1", packet, null));
    }

    [Fact]
    public void AttackerLessHealContributesNothing()
    {
        // No packet.ActorPtr -> attacker-less resolve -> stub snapshot -> heal.power composes to 0,
        // not a guessed value.
        var resolveCalls = new List<(string? Ptr, bool AttackerLess)>();
        var math = OverlayCombatMath.Create((ptr, attackerLess) =>
        {
            resolveCalls.Add((ptr, attackerLess));
            return new CombatActorSnapshot(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral);
        });
        var packet = new DamagePacket { SignedAmount = 50 }; // no ActorPtr

        Assert.Equal(50, math.Finalize(50, "Z1", packet, null));
        Assert.Single(resolveCalls);
        Assert.True(resolveCalls[0].AttackerLess);
    }

    [Fact]
    public void HealIsPoolNotContest()
    {
        // The negative half of this claim -- that reclassifying heal.power as Contest actually fails
        // guard-stat-pairs.ps1, so this classification is load-bearing rather than incidental -- is
        // HealPowerReclassifiedAsContestFailsTheGuard in Guard.Tests/StatTaxonomyGuardTests.cs (it
        // needs to shell out to the real script; this project doesn't).
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryGet(DerivedStatChannels.ResourceRestore("hp"), out var def));
        Assert.Equal(StatClass.Pool, def.Class);
        Assert.Null(def.CounterpartOf);
        Assert.Null(def.Cap); // uncapped -- a magnitude, PS-8
    }

    [Fact]
    public void NoMatchupNoHitNoCrit()
    {
        // §2.1 made falsifiable: the heal branch of Finalize must never touch _elementHub, never roll
        // _rng, never touch crit math -- scanning the source is the honest proof, since the method
        // signature gives Finalize the ability to reach all three and the claim is that it doesn't.
        var text = ReadCoreFile("Combat", "OverlayCombatMath.cs");
        var healMethodStart = text.IndexOf("long FinalizeHeal(", StringComparison.Ordinal);
        Assert.True(healMethodStart >= 0, "FinalizeHeal method not found");
        var healMethodBody = text[healMethodStart..Math.Min(text.Length, healMethodStart + 900)];

        Assert.DoesNotContain("_elementHub", healMethodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_rng", healMethodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("_calculator", healMethodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Crit", healMethodBody, StringComparison.Ordinal);
        Assert.DoesNotContain("Matchup", healMethodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void LeechHealsAndDamagesBothHalvesFromOnePulse()
    {
        // Unit-level companion to the scenario fixture (status-leech-apply.json,
        // StatusScenarioTests) -- proves the mechanism directly against StatusRuntime rather than
        // through the full harness.
        var pulses = new List<(string Kind, string Target, double Amount)>();
        var sink = new RecordingPulseSink(pulses);
        var runtime = new StatusRuntime(
            StatusCatalogBootstrap.CreateDefault(),
            (ptr, attackerLess) => ActorDerivedSnapshot.StubNeutral());

        var now = DateTimeOffset.UtcNow;
        var outcome = runtime.Apply(
            new StatusApplyInput(
                StatusId: "leech",
                HostPtr: "Z1",
                AttackerPtr: "P1",
                GrantId: "g1",
                BaseMagnitude: -12,
                BaseDuration: 5000,
                PeriodMs: 1000,
                DurationMs: 5000),
            new FixedStatusRng(0.0),
            now);

        Assert.True(outcome.Applied);
        runtime.Tick(now.AddMilliseconds(1000), sink);

        Assert.Contains(pulses, p => p.Kind == "damage" && p.Target == "Z1" && p.Amount < 0);
        Assert.Contains(pulses, p => p.Kind == "heal" && p.Target == "P1" && p.Amount > 0);
    }

    [Fact]
    public void NonLeechStatusesDoNotHealTheAttacker()
    {
        var pulses = new List<(string Kind, string Target, double Amount)>();
        var sink = new RecordingPulseSink(pulses);
        var runtime = new StatusRuntime(
            StatusCatalogBootstrap.CreateDefault(),
            (ptr, attackerLess) => ActorDerivedSnapshot.StubNeutral());

        var now = DateTimeOffset.UtcNow;
        var outcome = runtime.Apply(
            new StatusApplyInput(
                StatusId: "wither",
                HostPtr: "Z1",
                AttackerPtr: "P1",
                GrantId: "g1",
                BaseMagnitude: -5,
                BaseDuration: 5000,
                PeriodMs: 1000,
                DurationMs: 5000),
            new FixedStatusRng(0.0),
            now);

        Assert.True(outcome.Applied);
        runtime.Tick(now.AddMilliseconds(1000), sink);

        Assert.DoesNotContain(pulses, p => p.Kind == "heal");
    }

    [Fact]
    public void HealStillOneMailbox()
    {
        // spec-healing-pair.md §6 -- still `+signedAmount -> Funnel -> FA10`, the SAME mailbox as
        // damage. CombatDamageDispatcher is the one caller of ICombatMath.Finalize; proving it applies
        // the result through exactly one path, with no sign-based branch, is the structural half of
        // this claim -- guard-funnel-delta.py's green run (FunnelDeltaGuardTests, Guard.Tests) is the
        // runtime-wide half, already covered there rather than duplicated here.
        var text = ReadCoreFile("Combat", "CombatDamageDispatcher.cs");
        var finalizeCallIndex = text.IndexOf("math.Finalize(", StringComparison.Ordinal);
        var funnelCallIndex = text.IndexOf("ApplyPacketToFunnel(", StringComparison.Ordinal);
        Assert.True(finalizeCallIndex >= 0, "math.Finalize call not found");
        Assert.True(funnelCallIndex > finalizeCallIndex, "ApplyPacketToFunnel must follow Finalize");

        var between = text[finalizeCallIndex..funnelCallIndex];
        Assert.DoesNotContain("if (amount", between, StringComparison.Ordinal);
        Assert.DoesNotContain("amount > 0", between, StringComparison.Ordinal);
        Assert.DoesNotContain("amount < 0", between, StringComparison.Ordinal);

        var occurrences = System.Text.RegularExpressions.Regex.Matches(
            text, System.Text.RegularExpressions.Regex.Escape("ApplyPacketToFunnel(")).Count;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public void HealIsNotNegativeDamage()
    {
        // Invariant: the heal path emits a SIGNED DELTA the Funnel adds, never an absolute HP value.
        // FinalizeHeal's return flows back through Finalize's single `long` return, same as damage --
        // proving it never reaches an absolute-set API is what makes "delta, not an absolute write"
        // true rather than assumed. SetHp(...) is a real, used API elsewhere (BattleEffects.cs,
        // ShieldRuntime.cs, SimEngine.cs) -- its absence here is meaningful, not just untried.
        var text = ReadCoreFile("Combat", "OverlayCombatMath.cs");
        Assert.DoesNotContain("SetHp(", text, StringComparison.Ordinal);
        Assert.DoesNotContain(".Hp =", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteAbsolute", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LifestealUnchanged()
    {
        // spec-healing-pair.md §3 -- lifesteal is deliberately untouched: a `resource.delta`
        // OnDamageDealt atom (atom-family-library.md; g-on-hit.json, ssot-affixes.md:1006), a
        // completely separate mechanism from OverlayCombatMath.FinalizeHeal / resource.restore.hp. This
        // module never touched item content, so the honest proof is the mechanism boundary itself:
        // the atom's kindId is still resource.delta (not rewired onto the overlay heal path), and no
        // runtime source this module touched even names "lifesteal".
        var itemFile = ReadItemDataFile("affix-families", "g-on-hit.json");
        var lifestealStart = itemFile.IndexOf("\"id\": \"atom.lifesteal\"", StringComparison.Ordinal);
        Assert.True(lifestealStart >= 0, "atom.lifesteal entry not found in g-on-hit.json");
        var lifestealBlock = itemFile[lifestealStart..Math.Min(itemFile.Length, lifestealStart + 300)];
        Assert.Contains("\"kindId\": \"resource.delta\"", lifestealBlock, StringComparison.Ordinal);

        foreach (var file in new[] { "OverlayCombatMath.cs", "OverlayCombatCalculator.cs" })
        {
            var src = ReadCoreFile("Combat", file);
            Assert.DoesNotContain("lifesteal", src, StringComparison.OrdinalIgnoreCase);
        }
    }

    sealed class RecordingPulseSink : IStatusPulseSink
    {
        readonly List<(string Kind, string Target, double Amount)> _pulses;
        public RecordingPulseSink(List<(string Kind, string Target, double Amount)> pulses) => _pulses = pulses;

        public void PulseHp(StatusInstance instance, double amount) =>
            _pulses.Add(("damage", instance.HostPtr, amount));

        public void PulseHealAttacker(StatusInstance instance, double baseHealAmount) =>
            _pulses.Add(("heal", instance.AttackerPtr ?? "", baseHealAmount));
    }

    static string ReadCoreFile(params string[] relativeUnderCore)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName, "src", "FusionRpg.Core" }.Concat(relativeUnderCore).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("could not find " + string.Join("/", relativeUnderCore));
    }

    static string ReadItemDataFile(params string[] relativeUnderData)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { KeepverseRoots.Content(), "data", "seed", "items" }.Concat(relativeUnderData).ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("could not find " + string.Join("/", relativeUnderData));
    }
}
