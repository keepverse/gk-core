using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Bench;

/// <summary>
/// E13's candidate comparison. Two encodings, one corpus, cold and hot.
///
/// <para><b>Decision rule</b> (spec-runtime-form-benchmark.md): lowest <b>cold-cache</b> median wins.
/// A form that only wins hot is not the winner — a real board walks cold memory. If a candidate wins
/// cold by under 10% but loses hot by over 20%, the result is escalated rather than guessed.</para>
/// </summary>
public static class AtomFormBench
{
    const int Runs = 9;            // median of 9, per the spec's method
    const int OwnerCount = 512;    // enough distinct fact structs to defeat a single hot cache line
    const int HotIterations = 200;

    public sealed record Result(string Name, double ColdNs, double HotNs, long AllocBytes);

    public static IReadOnlyList<Result> Run()
    {
        var trees = Corpus.Trees();

        var typed = trees.Select(t =>
        {
            PredicateCompiler.TryCompile(t, Corpus.StatusBit, out var c, Corpus.ElementId);
            return c;
        }).ToArray();

        var flat = trees.Select(t => FlatPredicate.Build(t, Corpus.StatusBit, Corpus.ElementId)).ToArray();
        var owners = Corpus.Owners(OwnerCount);

        // Warm both before recording anything.
        for (var i = 0; i < 5; i++)
        {
            Cold(typed, owners); Hot(typed, owners);
            Cold(flat, owners); Hot(flat, owners);
        }

        // INTERLEAVED. The first cut measured one candidate to completion and then the other, so any
        // thermal or scheduler drift over the run landed entirely on whichever went first — and the
        // winner flipped between runs. Alternating within each round cancels drift shared by both.
        var typedCold = new double[Runs];
        var flatCold = new double[Runs];
        var typedHot = new double[Runs];
        var flatHot = new double[Runs];

        for (var round = 0; round < Runs; round++)
        {
            typedCold[round] = Cold(typed, owners);
            flatCold[round] = Cold(flat, owners);
            flatHot[round] = Hot(flat, owners);
            typedHot[round] = Hot(typed, owners);
        }

        return new[]
        {
            new Result("typed object graph", Median(typedCold), Median(typedHot),
                AllocationOf(typed, owners)),
            new Result("flattened, non-recursive", Median(flatCold), Median(flatHot),
                AllocationOf(flat, owners)),
        };
    }

    /// <summary>
    /// Cold-ish: every predicate paired with a different owner, striding the arrays so neither the
    /// tree nor the fact struct is the one just touched.
    /// </summary>
    static double Cold(ICompiledPredicate[] compiled, FactReader[] owners)
    {
        var sw = Stopwatch.StartNew();
        var sink = 0;
        var evals = 0;

        for (var pass = 0; pass < HotIterations; pass++)
        {
            for (var i = 0; i < compiled.Length; i++)
            {
                // Large co-prime strides: consecutive evaluations land far apart in both arrays.
                var t = (i * 37 + pass * 101) % compiled.Length;
                var o = (i * 53 + pass * 31) % owners.Length;

                var facts = owners[o];
                if (compiled[t].Evaluate(ref facts)) sink++;
                evals++;
            }
        }

        sw.Stop();
        Consume(sink);
        return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / evals;
    }

    /// <summary>Hot: the same tree and the same owner repeatedly — the friendliest possible case.</summary>
    static double Hot(ICompiledPredicate[] compiled, FactReader[] owners)
    {
        var sw = Stopwatch.StartNew();
        var sink = 0;
        var evals = 0;

        for (var pass = 0; pass < HotIterations; pass++)
        {
            for (var i = 0; i < compiled.Length; i++)
            {
                var facts = owners[i % owners.Length];
                if (compiled[i].Evaluate(ref facts)) sink++;
                evals++;
            }
        }

        sw.Stop();
        Consume(sink);
        return sw.Elapsed.TotalMilliseconds * 1_000_000.0 / evals;
    }

    /// <summary>Bytes allocated by evaluation alone over 10^5 iterations, after a warmup.</summary>
    static long AllocationOf(ICompiledPredicate[] compiled, FactReader[] owners)
    {
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

        var alloc = GC.GetAllocatedBytesForCurrentThread() - before;
        Consume(sink);
        return alloc;
    }

    static double Median(double[] samples)
    {
        var copy = (double[])samples.Clone();
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    /// <summary>Spread across runs — if this rivals the gap between candidates, the result is noise.</summary>
    public static double Spread(double[] samples)
    {
        var copy = (double[])samples.Clone();
        Array.Sort(copy);
        return (copy[^1] - copy[0]) / copy[copy.Length / 2] * 100.0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Consume(int value)
    {
        // Keep the result observable so the JIT cannot delete the loop that produced it.
        if (value == int.MinValue) Console.WriteLine(value);
    }

    public static string Environment() =>
        $"{RuntimeInformation.OSDescription} · {RuntimeInformation.ProcessArchitecture} · " +
        $".NET {System.Environment.Version} · {System.Environment.ProcessorCount} logical cores";
}
