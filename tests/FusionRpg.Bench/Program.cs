using FusionRpg.Bench;

Console.WriteLine("Atom runtime-form benchmark (E13)");
Console.WriteLine(AtomFormBench.Environment());
Console.WriteLine($"corpus: {Corpus.Size} predicates, depths 1-4, mixed leaves");
Console.WriteLine();

var results = AtomFormBench.Run();

Console.WriteLine($"{"form",-28} {"cold ns/atom",14} {"hot ns/atom",13} {"alloc B",10}");
foreach (var r in results)
    Console.WriteLine($"{r.Name,-28} {r.ColdNs,14:F2} {r.HotNs,13:F2} {r.AllocBytes,10}");

Console.WriteLine();
var winner = results.OrderBy(r => r.ColdNs).First();
var runnerUp = results.OrderBy(r => r.ColdNs).Skip(1).First();
var coldGap = (runnerUp.ColdNs - winner.ColdNs) / runnerUp.ColdNs * 100.0;
var hotGap = (winner.HotNs - runnerUp.HotNs) / runnerUp.HotNs * 100.0;

Console.WriteLine($"cold winner: {winner.Name} (by {coldGap:F1}%)");
if (coldGap < 10.0 && hotGap > 20.0)
    Console.WriteLine("ESCALATE: wins cold by <10% and loses hot by >20% - re-run at higher counts.");

Console.WriteLine();
Console.WriteLine("=====================================================================");
Console.WriteLine("World-graph turn-commit write benchmark (base-defense 3.1, world-graph-diff)");
Console.WriteLine(AtomFormBench.Environment());
Console.WriteLine();

var wg = WorldGraphWriteBench.Run();
Console.WriteLine($"synthetic world: {wg.TotalRows} rows (18 sectors x 20 slots, decision 19's scale)");
Console.WriteLine();
Console.WriteLine($"{"phase",-70} {"median ms",10}");
foreach (var p in wg.Phases)
    Console.WriteLine($"{p.Name,-70} {p.MedianMs,10:F2}");
