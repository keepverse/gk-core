using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E15 acceptance (spec-atom-runner.md). The load-bearing idea is <b>when a gate consumes</b>:
/// a pre-proc gate that fails consumes nothing, and the cap is post-proc, so a capped atom sits at
/// the same RNG stream position as an uncapped one and a replay holds.
/// </summary>
public class AtomRunnerTests
{
    // ---- fixtures ---------------------------------------------------------------------------------

    /// <summary>Counts draws, so "no roll was consumed" is proven rather than assumed.</summary>
    sealed class CountingRandom : IAtomRandom
    {
        readonly IAtomRandom _inner;
        public CountingRandom(IAtomRandom inner) => _inner = inner;
        public int Draws { get; private set; }

        public int NextInclusive(int min, int max) { Draws++; return _inner.NextInclusive(min, max); }
        public int NextPerMille() { Draws++; return _inner.NextPerMille(); }
    }

    sealed class Clock
    {
        public long Ms;
        public long Now() => Ms;
    }

    static RunnerEntry Entry(
        string atomId = "atom.strike.t1",
        string trigger = AtomTriggers.OnDamageDealt,
        int chanceMilli = 1000,
        int icdMs = 0,
        RunnerLimits? limits = null,
        ICompiledPredicate? predicate = null,
        (int Min, int Max)? amount = null) =>
        new(atomId,
            "resource.delta",
            trigger,
            predicate ?? PredicateCompiler.Always,
            chanceMilli,
            icdMs,
            atomId,
            amount is { } a
                ? new Dictionary<string, ValueBounds> { ["amount"] = new(a.Min, a.Max, RollPolicy.OnApply) }
                : new Dictionary<string, ValueBounds>(),
            limits ?? RunnerLimits.None,
            new Dictionary<string, object?> { ["channel"] = "hp", ["mode"] = "add" });

    static RunnerBinding Bind(RunnerEntry entry, string bindingId = "b1", int priority = 0) =>
        new(bindingId, priority, "player:1", entry);

    sealed class Harness
    {
        public readonly Clock Clock = new();
        public readonly EffectFunnel Funnel;
        public readonly CountingRandom Proc;
        public readonly CountingRandom Apply;
        public readonly AtomRunner Runner;

        public Harness(ulong seed, params RunnerBinding[] bindings)
        {
            var bag = new EffectBag(
                new InMemoryEffectCatalog(), new InMemoryEffectGrantStore(),
                new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)),
                new RecordingEffectSink());
            Funnel = new EffectFunnel(bag);
            Proc = new CountingRandom(new AtomRandom(seed, AtomStreams.Proc));
            Apply = new CountingRandom(new AtomRandom(seed, AtomStreams.Apply));
            Runner = new AtomRunner(Funnel, TriggerIndex.Build(bindings), Proc, Apply, Clock.Now, "m1");
        }

        public int Hit() => Runner.OnEvent(Event());

        public static RunnerEvent Event(string trigger = AtomTriggers.OnDamageDealt) =>
            new(TriggerIndex.Ordinal(trigger), "0xA", "0xB",
                new EntityFacts(0, 1, 1000, -1, 0, 0, false, false, 0),
                new EntityFacts(1, 2, 1000, -1, 0, 1, false, false, 0));
    }

    // ---- the trigger index ------------------------------------------------------------------------

    [Fact]
    public void Only_bindings_listening_to_the_trigger_are_visited()
    {
        var h = new Harness(1,
            Bind(Entry("a.dealt", AtomTriggers.OnDamageDealt), "b1"),
            Bind(Entry("a.death", AtomTriggers.OnDeath), "b2"));

        Assert.Equal(1, h.Runner.OnEvent(Harness.Event(AtomTriggers.OnDamageDealt)));
        Assert.Equal(1, h.Runner.OnEvent(Harness.Event(AtomTriggers.OnDeath)));
        Assert.Equal(0, h.Runner.OnEvent(Harness.Event(AtomTriggers.OnSpawn)));
    }

    [Fact]
    public void Evaluation_order_is_priority_desc_then_binding_id()
    {
        var index = TriggerIndex.Build(new[]
        {
            Bind(Entry("a"), "b-zulu", priority: 1),
            Bind(Entry("b"), "b-alpha", priority: 5),
            Bind(Entry("c"), "b-mike", priority: 5),
        });

        var order = index.Bindings.Select(b => b.BindingId).ToArray();

        Assert.Equal(new[] { "b-alpha", "b-mike", "b-zulu" }, order);
    }

    [Fact]
    public void A_triggerless_entry_is_refused_loudly_rather_than_bucketed()
    {
        // E7 emits triggerless modifiers as compiled Passive grants. One arriving here means the
        // compiler and the classifier disagree — and a modifier that never applies is invisible.
        var ex = Assert.Throws<ArgumentException>(() =>
            TriggerIndex.Build(new[] { Bind(Entry(trigger: null!), "b1") }));

        Assert.Contains("b1", ex.Message);
    }

    [Fact]
    public void An_unknown_trigger_ordinal_fires_nothing()
    {
        var h = new Harness(1, Bind(Entry()));

        Assert.Equal(0, h.Runner.OnEvent(new RunnerEvent(-1, "0xA", "0xB", default, default)));
    }

    // ---- ICD --------------------------------------------------------------------------------------

    [Fact]
    public void Two_events_inside_the_icd_window_dispatch_once()
    {
        var h = new Harness(1, Bind(Entry(icdMs: 250)));

        Assert.Equal(1, h.Hit());
        h.Clock.Ms += 100;
        Assert.Equal(0, h.Hit());
        Assert.Equal(new[] { AtomRunner.SkipIcd }, h.Runner.LastSkipped);

        h.Clock.Ms += 150; // now 250 ms since the first
        Assert.Equal(1, h.Hit());
    }

    [Fact]
    public void An_explicit_zero_icd_dispatches_every_event()
    {
        var h = new Harness(1, Bind(Entry(icdMs: 0)));

        for (var i = 0; i < 5; i++) Assert.Equal(1, h.Hit());
        Assert.Empty(h.Runner.LastSkipped);
    }

    // ---- pre-proc gates consume nothing -----------------------------------------------------------

    [Fact]
    public void A_false_predicate_consumes_no_icd_and_draws_no_roll()
    {
        var never = Compile("{\"leaf\":\"sideIs\",\"subject\":\"self\",\"value\":\"zombie\"}");
        var h = new Harness(1, Bind(Entry(chanceMilli: 500, icdMs: 250, predicate: never)));

        Assert.Equal(0, h.Hit());

        Assert.Equal(new[] { AtomRunner.SkipPredicate }, h.Runner.LastSkipped);
        Assert.Equal(0, h.Proc.Draws);
        Assert.Equal(0, h.Runner.State.IcdUntil(0));
    }

    [Fact]
    public void A_failed_chance_gate_consumes_no_icd()
    {
        // 0‰ never passes, so the ICD must still be unstamped after any number of attempts.
        var h = new Harness(1, Bind(Entry(chanceMilli: 0, icdMs: 250)));

        Assert.Equal(0, h.Hit());
        Assert.Equal(0, h.Hit());

        Assert.Equal(new[] { AtomRunner.SkipChance }, h.Runner.LastSkipped);
        Assert.Equal(0, h.Runner.State.IcdUntil(0));
        Assert.Equal(2, h.Proc.Draws); // the roll IS drawn — it is the gate, not a consequence
    }

    [Fact]
    public void A_certainty_draws_nothing_at_all()
    {
        // Foundation short-circuits at chance >= 1.0 and never consults its RNG. If the runner drew
        // anyway, the two paths would drift apart on a shared stream.
        var h = new Harness(1, Bind(Entry(chanceMilli: 1000)));

        h.Hit();

        Assert.Equal(0, h.Proc.Draws);
    }

    // ---- charges and meters -----------------------------------------------------------------------

    [Fact]
    public void Charges_are_spent_on_proc_and_stop_the_binding_when_gone()
    {
        var h = new Harness(1, Bind(Entry(limits: new RunnerLimits(-1, 2, -1, -1))));

        Assert.Equal(1, h.Hit());
        Assert.Equal(1, h.Hit());
        Assert.Equal(0, h.Hit());

        Assert.Equal(new[] { AtomRunner.SkipCharges }, h.Runner.LastSkipped);
        Assert.Equal(0, h.Runner.State.ChargesLeft(0));
    }

    [Fact]
    public void An_every_hits_meter_fires_on_the_nth_event()
    {
        var h = new Harness(1, Bind(Entry(limits: new RunnerLimits(-1, -1, 3, -1))));

        Assert.Equal(0, h.Hit());
        Assert.Equal(0, h.Hit());
        Assert.Equal(1, h.Hit());  // third
        Assert.Equal(0, h.Hit());
        Assert.Equal(0, h.Hit());
        Assert.Equal(1, h.Hit());  // sixth
    }

    [Fact]
    public void A_meter_of_one_is_the_same_as_no_meter()
    {
        var h = new Harness(1, Bind(Entry(limits: new RunnerLimits(-1, -1, 1, -1))));

        for (var i = 0; i < 4; i++) Assert.Equal(1, h.Hit());
    }

    [Fact]
    public void A_meter_that_has_not_reached_n_draws_no_roll()
    {
        var h = new Harness(1, Bind(Entry(chanceMilli: 500, limits: new RunnerLimits(-1, -1, 4, -1))));

        h.Hit();

        Assert.Equal(new[] { AtomRunner.SkipMeter }, h.Runner.LastSkipped);
        Assert.Equal(0, h.Proc.Draws);
        Assert.Equal(1, h.Runner.State.MeterAt(0)); // it ticked, though — a frozen meter never reaches N
    }

    // ---- the chance gate against an independent draw sequence --------------------------------------

    [Fact]
    public void Chance_800_over_ten_thousand_events_matches_the_stream_exactly()
    {
        const int events = 10_000;
        const int chance = 800;
        const ulong seed = 20260822;

        // The oracle is the stream itself, read independently of every gate in the runner: how many
        // of the first 10,000 draws fall under the threshold. Not a tolerance, and not a number
        // copied out of a previous run.
        var reference = new AtomRandom(seed, AtomStreams.Proc);
        var expected = 0;
        for (var i = 0; i < events; i++)
            if (reference.NextPerMille() < chance) expected++;

        var h = new Harness(seed, Bind(Entry(chanceMilli: chance)));
        var dispatched = 0;
        for (var i = 0; i < events; i++) dispatched += h.Hit();

        Assert.Equal(expected, dispatched);
        Assert.Equal(events, h.Proc.Draws);
    }

    [Fact]
    public void The_same_seed_reproduces_the_same_dispatch_count()
    {
        static int Run()
        {
            var h = new Harness(7, Bind(Entry(chanceMilli: 350)));
            var n = 0;
            for (var i = 0; i < 500; i++) n += h.Hit();
            return n;
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void The_proc_stream_and_the_apply_stream_are_independent()
    {
        // A gate roll must never shift a magnitude roll. Two atoms differing only in chance must
        // produce the same value sequence for the procs they share.
        var certain = new Harness(9, Bind(Entry(chanceMilli: 1000, amount: (10, 20))));
        var gated = new Harness(9, Bind(Entry(chanceMilli: 999, amount: (10, 20))));

        certain.Hit();
        gated.Hit();

        Assert.Equal(0, certain.Proc.Draws);
        Assert.Equal(1, gated.Proc.Draws);
        Assert.Equal(certain.Apply.Draws, gated.Apply.Draws);
    }

    // ---- dispatch ---------------------------------------------------------------------------------

    [Fact]
    public void A_dispatch_reaches_the_funnel_and_nothing_else()
    {
        var h = new Harness(1, Bind(Entry(amount: (10, 20))));

        Assert.False(h.Funnel.HasPending);
        Assert.Equal(1, h.Hit());
        Assert.True(h.Funnel.HasPending);
    }

    [Fact]
    public void A_fixed_value_is_not_rolled()
    {
        var h = new Harness(1, Bind(Entry(amount: (15, 15))));

        h.Hit();

        Assert.Equal(0, h.Apply.Draws);
    }

    [Fact]
    public void An_on_apply_range_is_rolled_once_per_dispatch()
    {
        var h = new Harness(1, Bind(Entry(amount: (10, 20))));

        h.Hit();
        h.Hit();

        Assert.Equal(2, h.Apply.Draws);
    }

    // ---- re-entry ----------------------------------------------------------------------------------

    [Fact]
    public void The_runner_never_re_enters_its_own_dispatch()
    {
        // Foundation dispatches at depth 0 and drains what a death adds inside the same window. A
        // runner that re-entered would turn one proc into an unbounded chain.
        AtomRunner? runner = null;
        var enqueues = 0;
        var innerSkips = Array.Empty<string>();
        var innerResult = -1;

        var clock = new Clock();
        runner = new AtomRunner(
            null!, TriggerIndex.Build(new[] { Bind(Entry()) }),
            new AtomRandom(1, AtomStreams.Proc), new AtomRandom(1, AtomStreams.Apply), clock.Now, "m1",
            dispatch: _ =>
            {
                enqueues++;
                innerResult = runner!.OnEvent(Harness.Event());
                innerSkips = runner.LastSkipped.ToArray();
                return true;
            });

        runner.OnEvent(Harness.Event());

        Assert.Equal(1, enqueues);
        Assert.Equal(0, innerResult);
        Assert.Equal(new[] { AtomRunner.SkipReentry }, innerSkips);
    }

    // ---- allocation ---------------------------------------------------------------------------------

    [Fact]
    public void The_gate_ladder_allocates_nothing()
    {
        var h = new Harness(1, Bind(Entry(icdMs: 60_000)));
        h.Hit(); // warm: first dispatch grows the skip list and the funnel's own buffers

        // Measured AROUND the loop only. A probe inside it measures its own bookkeeping — the last
        // benchmark that did reported a Stopwatch as 40 bytes of hot-path allocation.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100_000; i++) h.Runner.OnEvent(Harness.Event());
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }


    // ---- the caller ---------------------------------------------------------------------------------
    //
    // A runner nothing drives is a runner that does not exist. These pin the seam: SimEffectHost is
    // the Core-hosted double the spec names, and E19 wires the injector to the same shape.

    [Fact]
    public void A_runner_dispatch_is_flushed_by_the_same_event_that_caused_it()
    {
        // EffectBag.OnEvent calls Funnel.Flush() INSIDE itself. A runner that enqueues after the bag
        // has already run leaves its grant sitting in the mailbox until the NEXT event — a silent
        // one-event lag on every proc. The runner has to enqueue first; the bag drains it.
        var host = new SimEffectHost(seed: 3, matchKey: "m1");
        var entry = Entry(amount: (10, 20));
        host.WithCatalog(EffectSeedCatalog.CreateAll().Append(DefFor(entry)));
        host.UseRunner(new[] { Bind(entry) });

        Assert.False(host.Funnel.HasPending);
        host.HitDealt(attackerSide: "plant");

        Assert.False(host.Funnel.HasPending);
        Assert.True(host.Bag.HasGrantForEffect(entry.AtomId));
    }

    [Fact]
    public void A_host_with_no_runner_behaves_exactly_as_before()
    {
        // Foundation's path must be untouched whether a runner exists or not.
        var host = new SimEffectHost(seed: 3, matchKey: "m1");

        host.HitDealt(attackerSide: "plant");

        Assert.Null(host.Runner);
        Assert.False(host.Funnel.HasPending);
    }

    /// <summary>
    /// A runner dispatch names the atom id as its effect id, so the bag needs a def under that id —
    /// <c>EffectBag.Grant</c> throws on an unknown one, and it throws inside a later flush, far from
    /// the enqueue that caused it. <b>E11 creates these defs when the catalog becomes rows</b>; until
    /// then a test that lets a dispatch reach the bag has to supply one.
    /// </summary>
    static EffectDef DefFor(RunnerEntry entry) => new()
    {
        EffectId = entry.AtomId,
        EffectType = EffectTypes.Triggered,
        Name = entry.AtomId,
        Triggers = new List<string> { entry.Trigger! },
        Actions = new List<EffectActionRow> { new() { Seq = 1, Action = EffectActions.ApplyResourceDelta } },
    };

    [Fact]
    public void Beginning_a_match_on_the_host_resets_the_runner_caps()
    {
        var host = new SimEffectHost(seed: 3, matchKey: "m1");
        var entry = Entry(limits: new RunnerLimits(2, -1, -1, -1));
        host.WithCatalog(EffectSeedCatalog.CreateAll().Append(DefFor(entry)));
        var runner = host.UseRunner(new[] { Bind(entry) });

        for (var i = 0; i < 5; i++) host.HitDealt(attackerSide: "plant");
        Assert.Equal(2, runner.State.DispatchesThisMatch(0));

        host.BeginMatch("m2");

        Assert.Equal(0, runner.State.DispatchesThisMatch(0));
        Assert.Equal("m2", runner.State.MatchKey);
    }

    [Fact]
    public void The_mapper_defaults_a_missing_fact_so_a_condition_fails_rather_than_fires()
    {
        // BoardEntitySnap carries no HP. With no provider the default is full health, so an
        // "below half" condition is false — an unevaluatable condition must never fire an effect.
        var ev = RunnerEventMapper.From(new EffectEventDto
        {
            Trigger = AtomTriggers.OnDamageDealt,
            ActorPtr = "0xA",
            TargetPtr = "0xB",
            Side = "plant",
        });

        Assert.Equal(RunnerEventMapper.FullHpMilli, ev.Self.HpMilli);
        Assert.Equal(-1, ev.Self.ElementId);
        Assert.Equal(0UL, ev.Self.StatusMask);
        Assert.Equal(0, ev.Self.Side);
        Assert.Equal(1, ev.Target.Side);   // the other entity is the opposing side
    }

    [Fact]
    public void An_atom_compiled_by_E7_runs_end_to_end()
    {
        // The whole compile/run split in one test: an atom the compiler refuses to express as a
        // grant becomes a runner entry, and that entry actually gates and dispatches.
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.sun-tap", "", 1),
            KindId = "resource.economy",
            FamilyId = "atom.sun-tap",
            Tier = 1,
            Name = "Sun Tap",
            WhenJson = "{\"trigger\":\"OnDamageDealt\"}",
            ParamsJson = "{\"currency\":\"sun\",\"op\":\"add\",\"amount\":25,\"capPerMatch\":3}",
        };

        var compiled = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1);
        var entry = Assert.Single(compiled.Runtime);
        Assert.Equal(3, entry.Limits.CapPerMatch);
        Assert.Equal("sun", entry.Params["currency"]);
        Assert.Equal("add", entry.Params["op"]);

        var h = new Harness(1, Bind(entry));

        Assert.Equal(3, h.Runner.OnEvent(Harness.Event()) + h.Runner.OnEvent(Harness.Event())
                        + h.Runner.OnEvent(Harness.Event()) + h.Runner.OnEvent(Harness.Event()));
        Assert.Single(h.Runner.CapNotices);
    }

    static ICompiledPredicate Compile(string leafJson)
    {
        Assert.True(AtomJson.TryReadPredicate(
            System.Text.Json.JsonDocument.Parse(leafJson).RootElement, out var tree).IsOk);
        Assert.True(PredicateCompiler.TryCompile(tree!, null, out var compiled).IsOk);
        return compiled;
    }
}
