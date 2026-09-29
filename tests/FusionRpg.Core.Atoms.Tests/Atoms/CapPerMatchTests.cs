using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// <c>capPerMatch</c> (E15). It has been in the FA9 allowlist with <b>no implementation anywhere</b>
/// since the opcode shipped — an economy atom could declare a cap and mint without limit.
///
/// <para>The cap is the one <b>post-proc</b> gate: it fires after the proc already succeeded, so the
/// ICD is stamped and the roll is drawn before the dispatch is suppressed. That is what keeps a
/// capped binding at the same RNG stream position as an uncapped one — without it, a replay of the
/// same seed diverges the moment a cap is hit.</para>
/// </summary>
public class CapPerMatchTests
{
    sealed class Clock
    {
        public long Ms;
        public long Now() => Ms;
    }

    sealed class CountingRandom : IAtomRandom
    {
        readonly IAtomRandom _inner;
        public CountingRandom(IAtomRandom inner) => _inner = inner;
        public int Draws { get; private set; }
        public int NextInclusive(int min, int max) { Draws++; return _inner.NextInclusive(min, max); }
        public int NextPerMille() { Draws++; return _inner.NextPerMille(); }
    }

    static RunnerEntry Economy(int cap, int chanceMilli = 1000, bool rolls = false) =>
        new("atom.sun-tap.t1",
            "resource.economy",
            AtomTriggers.OnDamageDealt,
            PredicateCompiler.Always,
            chanceMilli,
            0,
            "atom.sun-tap.t1",
            rolls
                ? new Dictionary<string, ValueBounds> { ["amount"] = new(10, 40, RollPolicy.OnApply) }
                : new Dictionary<string, ValueBounds> { ["amount"] = new(25, 25, RollPolicy.Fixed) },
            new RunnerLimits(cap, -1, -1, -1),
            new Dictionary<string, object?> { ["currency"] = "sun", ["op"] = "add" });

    sealed class Harness
    {
        public readonly Clock Clock = new();
        public readonly CountingRandom Proc;
        public readonly CountingRandom Apply;
        public readonly AtomRunner Runner;
        public int Dispatches;

        public Harness(RunnerEntry entry, ulong seed = 42, string matchKey = "m1")
        {
            Proc = new CountingRandom(new AtomRandom(seed, AtomStreams.Proc));
            Apply = new CountingRandom(new AtomRandom(seed, AtomStreams.Apply));
            var index = TriggerIndex.Build(new[] { new RunnerBinding("b1", 0, "player:1", entry) });
            Runner = new AtomRunner(null!, index, Proc, Apply, Clock.Now, matchKey,
                dispatch: _ => { Dispatches++; return true; });
        }

        public int Hit() => Runner.OnEvent(new RunnerEvent(
            TriggerIndex.Ordinal(AtomTriggers.OnDamageDealt), "0xA", "0xB",
            new EntityFacts(0, 1, 1000, -1, 0, 0, false, false, 0),
            new EntityFacts(1, 2, 1000, -1, 0, 1, false, false, 0)));

        public int Hits(int n)
        {
            var d = 0;
            for (var i = 0; i < n; i++) d += Hit();
            return d;
        }
    }

    [Fact]
    public void A_cap_of_five_dispatches_five_times_out_of_twenty()
    {
        var h = new Harness(Economy(cap: 5));

        Assert.Equal(5, h.Hits(20));
        Assert.Equal(5, h.Dispatches);
        Assert.Equal(5, h.Runner.State.DispatchesThisMatch(0));
    }

    [Fact]
    public void The_cap_skip_is_recorded_once_per_binding_per_match()
    {
        // A capped economy atom is still hit at board rate. One record per attempt would bury the
        // log under the single effect that has already stopped doing anything.
        var h = new Harness(Economy(cap: 2));

        h.Hits(50);

        var notice = Assert.Single(h.Runner.CapNotices);
        Assert.Equal("b1", notice.BindingId);
        Assert.Equal("atom.sun-tap.t1", notice.AtomId);
        Assert.Equal("m1", notice.MatchKey);
        Assert.Equal(2, notice.Cap);
    }

    [Fact]
    public void A_cap_of_zero_dispatches_nothing_and_is_not_read_as_absent()
    {
        // Zero is a real cap. If "absent" and "zero" shared an encoding, every uncapped atom would
        // become this one.
        var h = new Harness(Economy(cap: 0));

        Assert.Equal(0, h.Hits(10));
        Assert.Single(h.Runner.CapNotices);
    }

    [Fact]
    public void A_capped_binding_sits_at_the_same_stream_position_as_an_uncapped_one()
    {
        // THE reason the cap is last. If it short-circuited before the roll, a capped atom would
        // stop consuming the stream and every later roll in the match would shift.
        var capped = new Harness(Economy(cap: 3, chanceMilli: 750, rolls: true), seed: 99);
        var uncapped = new Harness(Economy(cap: -1, chanceMilli: 750, rolls: true), seed: 99);

        capped.Hits(40);
        uncapped.Hits(40);

        Assert.Equal(uncapped.Proc.Draws, capped.Proc.Draws);
        Assert.Equal(uncapped.Apply.Draws, capped.Apply.Draws);
        Assert.True(capped.Dispatches < uncapped.Dispatches);
    }

    [Fact]
    public void Reaching_the_cap_still_stamps_the_icd()
    {
        var entry = Economy(cap: 1) with { IcdMs = 500 };
        var h = new Harness(entry);

        Assert.Equal(1, h.Hit());          // dispatches, stamps ICD
        h.Clock.Ms += 500;
        Assert.Equal(0, h.Hit());          // proc succeeds, cap suppresses
        Assert.Equal(new[] { AtomRunner.SkipCap }, h.Runner.LastSkipped);

        // The suppressed proc consumed the clock: the next event is inside the fresh window.
        Assert.Equal(h.Clock.Ms + 500, h.Runner.State.IcdUntil(0));
    }

    [Fact]
    public void A_new_match_resets_the_counter_and_the_one_shot_notice()
    {
        var h = new Harness(Economy(cap: 3));

        Assert.Equal(3, h.Hits(10));
        Assert.Single(h.Runner.CapNotices);

        h.Runner.BeginMatch("m2");

        Assert.Empty(h.Runner.CapNotices);
        Assert.Equal(0, h.Runner.State.DispatchesThisMatch(0));
        Assert.Equal(3, h.Hits(10));
        Assert.Equal("m2", Assert.Single(h.Runner.CapNotices).MatchKey);
    }

    [Fact]
    public void An_absent_cap_never_suppresses()
    {
        var h = new Harness(Economy(cap: -1));

        Assert.Equal(200, h.Hits(200));
        Assert.Empty(h.Runner.CapNotices);
    }

    [Fact]
    public void A_dispatch_the_funnel_refuses_does_not_count_against_the_cap()
    {
        // The counter tracks what actually left, not what was attempted — otherwise a full mailbox
        // would silently spend a player's per-match budget.
        var index = TriggerIndex.Build(new[] { new RunnerBinding("b1", 0, "player:1", Economy(cap: 2)) });
        var clock = new Clock();
        var refused = 0;
        var runner = new AtomRunner(
            null!, index, new AtomRandom(1, AtomStreams.Proc), new AtomRandom(1, AtomStreams.Apply),
            clock.Now, "m1", dispatch: _ => { refused++; return false; });

        for (var i = 0; i < 10; i++)
            runner.OnEvent(new RunnerEvent(
                TriggerIndex.Ordinal(AtomTriggers.OnDamageDealt), "0xA", "0xB", default, default));

        Assert.Equal(10, refused);
        Assert.Equal(0, runner.State.DispatchesThisMatch(0));
        Assert.Empty(runner.CapNotices);
    }
}
