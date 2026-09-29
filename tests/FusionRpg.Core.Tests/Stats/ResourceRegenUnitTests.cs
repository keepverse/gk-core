using System.Text.Json;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// lawn `LW2.1` (<c>lawn-tuning-profile/spec-regen-unit-trace.md</c>): pin the unit of
/// <c>resource.regen.{id}</c> as a CONTRACT, and pin the reconciliation the trace
/// (<c>docs/architecture/lawn-tuning-profile/regen-unit-trace.md</c>) rests on.
///
/// <para>The unit is <b>units per tick</b> (tick = 100 ms, ten per second). The POC these coefficients
/// were fitted in reads the same number as <b>units per round</b>
/// (<c>gk-core/tools/CombatSim/ActionEconomy.cs:123-127</c>, one <c>Tick()</c> per loop round), so at a basic
/// attack's cadence the runtime accrues the same coefficient 200× more often. The trace is the record;
/// this file is the thing that fails if the tick contract is changed silently, and the arithmetic that
/// fails if the round length the 200× rests on moves.</para>
///
/// <para><b>Coverage note:</b> <c>ResourceSubTickRegenTests</c> already asserts the per-mille
/// carry-correction in depth for <c>poise</c>. This file asserts the UNIT across the whole closed
/// six-resource vocabulary and the POC-vs-runtime reconciliation, which nothing else does — it does not
/// restate the carry's own edge cases.</para>
/// </summary>
[Trait("VerificationId", "core.resource-regen-unit")]
public class ResourceRegenUnitTests
{
    /// <summary>A snapshot carrying one pool's max and regen, as
    /// <c>ResourceSubTickRegenTests.PoiseSnapshot</c> builds them — same composer, no second path.
    /// <paramref name="regenPerTick"/> is in WHOLE UNITS per tick, which is the channel's meaning.</summary>
    static ActorDerivedSnapshot Snapshot(string resourceId, double regenPerTick)
    {
        var composer = new DerivedComposer(DerivedStatRegistry.CreateDefault());
        return composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.ResourceMax(resourceId), DerivedModifierOp.Flat, 1000, SourceId: "test"),
            new DerivedModifier(DerivedStatChannels.ResourceRegen(resourceId), DerivedModifierOp.Flat, regenPerTick, SourceId: "test"),
        });
    }

    [Fact]
    public void The_channel_is_units_per_tick_for_every_resource_in_the_closed_vocabulary()
    {
        // The vocabulary is code-owned and closed, so iterating it is the assertion: a resource added
        // tomorrow is covered here with no edit, and no test asserts how many there are.
        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            var derived = Snapshot(id, regenPerTick: 2.0);

            // Two whole units per tick read as 2000 per-mille of a unit per tick. If the channel ever
            // meant per-second or per-round, this is the line that fails.
            Assert.Equal(2000, ResourceChannelReader.RegenPerMilleTick(derived, id));
        }
    }

    [Fact]
    public void One_authored_unit_per_tick_accrues_ten_units_over_one_second_and_one_over_one_tick()
    {
        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            var perMille = ResourceChannelReader.RegenPerMilleTick(Snapshot(id, regenPerTick: 1.0), id);
            Assert.Equal(1000, perMille);

            // Ten ticks (one second at TicksPerSecond = 10) accrue exactly ten units...
            Assert.Equal(10, new ResourcePoolState(0, 0).Settle(10, perMille, max: 1_000_000).Stored);
            // ...and one tick accrues exactly one.
            Assert.Equal(1, new ResourcePoolState(0, 0).Settle(1, perMille, max: 1_000_000).Stored);
        }
    }

    [Fact]
    public void A_fractional_rate_accrues_exactly_over_many_ticks_never_truncated_per_tick()
    {
        // 0.333/tick is the rate class the per-mille unit exists to express. Truncating per tick would
        // accrue 0 units forever; carrying the remainder accrues 333 units over a thousand ticks.
        var perMille = ResourceChannelReader.RegenPerMilleTick(Snapshot("stamina", regenPerTick: 0.333), "stamina");
        Assert.Equal(333, perMille);

        var oneTick = new ResourcePoolState(0, 0).Settle(1, perMille, max: 1_000_000);
        Assert.Equal(0, oneTick.Stored);      // no whole unit yet...
        Assert.Equal(333, oneTick.Carry);     // ...but the fraction is banked, not lost

        var thousandTicks = new ResourcePoolState(0, 0).Settle(1000, perMille, max: 1_000_000);
        Assert.Equal(333, thousandTicks.Stored);
        Assert.Equal(0, thousandTicks.Carry);
    }

    /// <summary>
    /// The reconciliation, as arithmetic rather than prose. One basic-attack round is its wind-up plus
    /// recovery in ticks, read from the tuning file (never a literal), and the runtime accrues exactly
    /// that multiple more per round than the POC's per-round <c>Tick()</c> does for the same
    /// coefficient — which is the 200× the trace records.
    /// </summary>
    [Fact]
    public void One_basic_attack_round_is_the_multiple_the_trace_reports()
    {
        var basic = JsonDocument.Parse(File.ReadAllText(FindTuning("action-timing.v1.json")))
            .RootElement.GetProperty("basicAttack");
        var roundTicks = basic.GetProperty("windupTicks").GetInt32() + basic.GetProperty("recoveryTicks").GetInt32();

        // Pinned because the trace's own 200× rests on it: if either timing moves, the conversion the
        // follow-up republish uses moves with it and this must be looked at deliberately.
        Assert.Equal(200, roundTicks);

        const long oneUnitPerTick = 1000; // per-mille of a unit, one whole unit per tick
        var runtimeOverOneRound = new ResourcePoolState(0, 0).Settle(roundTicks, oneUnitPerTick, max: long.MaxValue).Stored;

        // gk-core/tools/CombatSim/ActionEconomy.cs:123-127: `_value[id] + _regen[id] * rounds`, called once per
        // round with rounds = 1 — so one round of the SAME coefficient banks one unit there and
        // `roundTicks` here.
        const double pocUnitsPerRound = 1.0;
        Assert.Equal(pocUnitsPerRound * roundTicks, runtimeOverOneRound);
    }

    static string FindTuning(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "tuning", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"no data/tuning/{fileName} above the test output");
    }
}
