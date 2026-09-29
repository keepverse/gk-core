using System.Diagnostics;
using FusionRpg.Core.Effects.Atoms;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E13's permanent guard. Not the candidate comparison — that lives in <c>gk-core/tests/FusionRpg.Bench</c>
/// and is run deliberately. This is what stops the hot path regressing without anyone noticing.
///
/// <para><b>It fails on a sustained regression, never on one slow sample.</b> A benchmark inside a
/// unit-test suite shares a machine with everything else in CI, so a single unlucky run means
/// nothing. Median of 9, a generous multiple of the budget, and the raw numbers always printed so a
/// human can judge a borderline result rather than trusting the verdict.</para>
/// </summary>
public class AtomBenchGuardTests
{
    /// <summary>spec-runtime-form-benchmark.md: ≤ 50 ns per atom evaluation.</summary>
    const double BudgetNs = 50.0;

    /// <summary>Fails at 1.5× budget. The gap absorbs a noisy agent, not a real regression.</summary>
    const double FailAt = BudgetNs * 1.5;

    const int Predicates = 200;
    const int Runs = 9;
    const int Passes = 40;

    readonly ITestOutputHelper _out;

    public AtomBenchGuardTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void The_compiled_form_stays_inside_its_ns_per_atom_budget()
    {
        var (compiled, owners) = BuildCorpus();

        for (var i = 0; i < 3; i++) Sweep(compiled, owners); // warm the JIT

        var samples = new double[Runs];
        for (var i = 0; i < Runs; i++) samples[i] = Sweep(compiled, owners);

        Array.Sort(samples);
        var median = samples[Runs / 2];

        // Always printed, pass or fail: a number nobody can see is a number nobody can act on.
        _out.WriteLine($"ns/atom over {Predicates} predicates, median of {Runs}: {median:F2}");
        _out.WriteLine($"  raw: {string.Join(", ", samples.Select(s => s.ToString("F2")))}");
        _out.WriteLine($"  budget {BudgetNs:F0}, fails above {FailAt:F0}");

        Assert.True(median <= FailAt,
            $"median {median:F2} ns/atom exceeds {FailAt:F0} (budget {BudgetNs:F0}). Raw: " +
            string.Join(", ", samples.Select(s => s.ToString("F2"))));
    }

    [Fact]
    public void The_compiled_form_allocates_nothing_per_evaluation()
    {
        var (compiled, owners) = BuildCorpus();

        var sink = 0;
        for (var i = 0; i < 1000; i++)
        {
            var warm = owners[i % owners.Length];
            if (compiled[i % compiled.Length].Evaluate(ref warm)) sink++;
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var i = 0; i < 100_000; i++)
        {
            var facts = owners[i % owners.Length];
            if (compiled[i % compiled.Length].Evaluate(ref facts)) sink++;
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        _out.WriteLine($"allocated over 10^5 evaluations: {allocated} bytes (sink {sink})");
        Assert.Equal(0, allocated);
    }

    /// <summary>
    /// The shipped form must still be the one the benchmark chose. If <c>TryCompile</c> ever returns
    /// the typed graph again, that is a decision someone should have to make on purpose.
    /// </summary>
    [Fact]
    public void TryCompile_emits_the_encoding_E13_selected()
    {
        PredicateCompiler.TryCompile(
            new PredicateNode.Leaf(LeafId.SideIs, Subject.Self, Text: "zombie"),
            _ => -1, out var compiled, _ => -1);

        Assert.IsType<FlatPredicate>(compiled);
    }

    static (ICompiledPredicate[] Compiled, FactReader[] Owners) BuildCorpus()
    {
        var rng = new Random(13_2026);
        var compiled = new ICompiledPredicate[Predicates];

        for (var i = 0; i < Predicates; i++)
        {
            var depth = i % 4 + 1;
            PredicateCompiler.TryCompile(Tree(rng, depth), Bit, out compiled[i], Bit);
        }

        var owners = new FactReader[256];
        for (var i = 0; i < owners.Length; i++) owners[i] = new FactReader(Facts(rng), Facts(rng));

        return (compiled, owners);

        static int Bit(string s) => s.Length % 4;
    }

    static double Sweep(ICompiledPredicate[] compiled, FactReader[] owners)
    {
        var sw = Stopwatch.StartNew();
        var sink = 0;
        var evals = 0;

        for (var pass = 0; pass < Passes; pass++)
            for (var i = 0; i < compiled.Length; i++)
            {
                // Strided so the walk crosses memory rather than sitting in one cache line.
                var facts = owners[(i * 53 + pass * 31) % owners.Length];
                if (compiled[(i * 37 + pass * 101) % compiled.Length].Evaluate(ref facts)) sink++;
                evals++;
            }

        sw.Stop();
        if (sink == int.MinValue) throw new InvalidOperationException(); // keep the loop alive
        return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / evals;
    }

    static PredicateNode Tree(Random rng, int remaining)
    {
        if (remaining <= 1)
            return new PredicateNode.Leaf(
                (LeafId)rng.Next(11),
                rng.Next(2) == 0 ? Subject.Self : Subject.Target,
                Value: rng.Next(500),
                Text: "seed",
                Values: new[] { rng.Next(300), rng.Next(300) });

        var kids = new PredicateNode[1 + rng.Next(2)];
        for (var i = 0; i < kids.Length; i++) kids[i] = Tree(rng, remaining - 1);

        return rng.Next(3) switch
        {
            0 => new PredicateNode.Or(kids),
            1 => new PredicateNode.Not(kids[0]),
            _ => new PredicateNode.And(kids),
        };
    }

    static EntityFacts Facts(Random r) => new(
        Side: r.Next(3), TypeId: r.Next(300), HpMilli: r.Next(1001),
        ElementId: r.Next(-1, 6), Row: r.Next(-1, 6), Col: r.Next(-1, 10),
        IsMindControlled: r.Next(2) == 0, IsKiller: r.Next(2) == 0,
        StatusMask: (ulong)r.Next(0, 16));
}
