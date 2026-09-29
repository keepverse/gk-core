// Hybrid viability measurement — passive-tree-ideal.md §3.3.

using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Core.Balance.Guards;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;

var theta = args.Length > 0 && long.TryParse(args[0], out var t) ? t : 100L;

// TerminationGuard.ToActor -> ActorHubBootstrap.CreateDefault touches every one of these hubs. Same
// full set, same live-highest-version rule, as gk-forge/tools/DominanceBaseline and gk-core/tools/ResidualFitLoop --
// never a hand-picked version literal (AptitudeTuningHub's own staleness warning).
var repoRoot = FindRepoRoot();
var tuningDir = Path.Combine(repoRoot, "data", "tuning");
string Read(string domain) => File.ReadAllText(Path.Combine(tuningDir, LatestTuningFileName(tuningDir, domain)));
string ReadPinned(string fileName) => File.ReadAllText(Path.Combine(tuningDir, fileName));
AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(Read("aptitudes")));
CombatPolicy.Configure(CombatTuningLoader.Parse(Read("combat")));
ShieldPolicy.Configure(ShieldTuningLoader.Parse(Read("shield")));
DerivedStatPolicy.Configure(DerivedStatTuningLoader.Parse(Read("derived-stats")));
// ⛔ power-scale is PINNED, not latest-wins. `power-scale.v3.json` is not a newer curve — it is a
// different artifact kind that happens to share the domain prefix: "Pins, not curves. Each row is a
// channel's composed value at the Theta=20 reference", owned by passive-tree-repair, and its own
// _meta says "v2.json frozen". Its channel keys are templates (`combat.power.{variant}`, `arm1Max`)
// with no `atk`/`defense`, so `BattleRuleset.ChannelLadderFor("atk")` throws against it.
PowerTuningHub.Configure(PowerTuningLoader.Parse(ReadPinned("power-scale.v2.json")));
StatusPolicy.Configure(StatusTuningLoader.Parse(Read("status")));
StatsTuningHub.Configure(StatsTuningLoader.Parse(Read("stats")));

static string LatestTuningFileName(string tuningDir, string domain)
{
    var pat = new Regex($@"^{Regex.Escape(domain)}\.v(\d+)\.json$");
    var best = Directory.EnumerateFiles(tuningDir)
        .Select(Path.GetFileName)
        .Select(n => (Name: n!, Match: pat.Match(n!)))
        .Where(x => x.Match.Success)
        .Select(x => (x.Name, Version: int.Parse(x.Match.Groups[1].Value)))
        .OrderByDescending(x => x.Version)
        .FirstOrDefault();
    if (best.Name is null) throw new InvalidOperationException($"no {domain}.v*.json found in {tuningDir}");
    return best.Name;
}

/// Marker: `Directory.Build.props`, the tracked MSBuild root file -- NOT a tool. The marker this
/// used to name, `scripts/guard-class-system.ps1`, was ported to `.py` and no longer exists, so
/// EVERY invocation threw `could not locate repo root`. A marker must be a file no tool migration
/// can delete; the two `FusionRpg.Core.ClassSystem.Tests` files beside this one already used
/// `Directory.Build.props`, and this adopts their convention.
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("could not locate repo root above " + AppContext.BaseDirectory);
}

// Same twelve, same order, as gk-forge/tools/DominanceBaseline and gk-core/tools/ResidualFitLoop — kept identical so a
// drift in one is a diff against the others.
string[] roster =
{
    "Might", "Fortitude", "Vigor", "Onslaught", "Agility", "Composure",
    "Pierce", "Focus", "Bulwark", "Retribution", "Precision", "Ferocity",
};

// BestResponse.DominanceMatrix's fixed corner-shape constant (100/roster.Length/2, per-mille).
const long Floor = 4167;
const long Total = 100_000;

// Spread `Total` over `spikeIds` evenly, flooring every other aptitude — the corner shape generalised
// from one spike to k. k=1 reproduces DominanceBaseline's Corner() exactly.
AptitudeAllocation Build(params string[] spikeIds)
{
    var spikeBudget = Total - Floor * (roster.Length - spikeIds.Length);
    var each = spikeBudget / spikeIds.Length;
    var remainder = spikeBudget - each * spikeIds.Length; // integer split: give the rest to the first
    return roster.Aggregate(AptitudeAllocation.Empty, (acc, id) =>
    {
        var idx = Array.IndexOf(spikeIds, id);
        var pts = idx < 0 ? Floor : each + (idx == 0 ? remainder : 0);
        return acc + AptitudeAllocation.Single(AllocationScope.Commander, id, pts);
    });
}

var labels = new List<string>();
var kinds = new List<string>();
var builds = new List<AptitudeAllocation>();

void Add(string label, string kind, AptitudeAllocation b)
{
    labels.Add(label); kinds.Add(kind); builds.Add(b);
}

foreach (var a in roster) Add(a, "corner", Build(a));
for (var i = 0; i < roster.Length; i++)
for (var j = i + 1; j < roster.Length; j++)
    Add($"{roster[i]}+{roster[j]}", "hybrid2", Build(roster[i], roster[j]));
// A 3-way sample: every consecutive triple in roster order, which mixes within and across postures.
for (var i = 0; i < roster.Length; i++)
    Add($"{roster[i]}+{roster[(i + 1) % roster.Length]}+{roster[(i + 2) % roster.Length]}", "hybrid3",
        Build(roster[i], roster[(i + 1) % roster.Length], roster[(i + 2) % roster.Length]));
Add("even12", "spread", roster.Aggregate(AptitudeAllocation.Empty,
    (acc, id) => acc + AptitudeAllocation.Single(AllocationScope.Commander, id, Total / roster.Length)));

Console.WriteLine($"theta={theta}  builds={builds.Count} " +
                  $"(corners={kinds.Count(k => k == "corner")}, hybrid2={kinds.Count(k => k == "hybrid2")}, " +
                  $"hybrid3={kinds.Count(k => k == "hybrid3")}, spread=1)");

var report = DominanceGuard.Measure(builds, theta);

// Measure names actors positionally ("corner{i}" — its own internal convention, regardless of what the
// build actually is), so index maps back to labels[i] by construction.
static int Index(string name) => int.Parse(name["corner".Length..]);

var n = builds.Count;
var sum = new double[n];
var count = new int[n];
var beatenBy = new int[n];      // how many opponents beat this build
foreach (var arrow in report.Matrix)
{
    var i = Index(arrow.AttackerName);
    var j = Index(arrow.DefenderName);
    sum[i] += arrow.WinShareAttacker;
    count[i]++;
    if (arrow.WinShareAttacker > DominanceGuard.MajorityWinShare) beatenBy[j]++;
}

var mean = new double[n];
for (var i = 0; i < n; i++) mean[i] = count[i] == 0 ? 0 : sum[i] / count[i];

Console.WriteLine();
Console.WriteLine("class      n     mean win share   best            worst");
foreach (var kind in new[] { "corner", "hybrid2", "hybrid3", "spread" })
{
    var idx = Enumerable.Range(0, n).Where(i => kinds[i] == kind).ToList();
    if (idx.Count == 0) continue;
    var best = idx.OrderByDescending(i => mean[i]).First();
    var worst = idx.OrderBy(i => mean[i]).First();
    Console.WriteLine($"{kind,-9} {idx.Count,3}   {idx.Average(i => mean[i]),12:P2}   " +
                      $"{labels[best],-14} {mean[best]:P1}   {labels[worst],-22} {mean[worst]:P1}");
}

// CLASS vs CLASS — the mean-vs-field figure above is biased by field composition (66 hybrid2 against
// only 12 corners), so a corner is mostly being scored against hybrids and vice versa. This matrix
// scores each class against each class directly, which is the comparison the design question needs.
var classes = new[] { "corner", "hybrid2", "hybrid3", "spread" };
var pair = new Dictionary<(string, string), (double Sum, int N)>();
foreach (var arrow in report.Matrix)
{
    var key = (kinds[Index(arrow.AttackerName)], kinds[Index(arrow.DefenderName)]);
    var cur = pair.TryGetValue(key, out var v) ? v : (0.0, 0);
    pair[key] = (cur.Item1 + arrow.WinShareAttacker, cur.Item2 + 1);
}

Console.WriteLine();
Console.WriteLine("attacker vs defender  " + string.Join("  ", classes.Select(c => c.PadLeft(8))));
foreach (var a in classes)
{
    var cells = classes.Select(d =>
        pair.TryGetValue((a, d), out var v) && v.N > 0 ? $"{v.Sum / v.N,8:P1}" : "       -");
    Console.WriteLine($"{a,-20}  {string.Join("  ", cells)}");
}

Console.WriteLine();
Console.WriteLine($"dominant (beats every other build): " +
                  (report.IsDominant ? string.Join(", ", report.DominantBuildNames.Select(x => labels[Index(x)])) : "none"));

// The decision-relevant number: how far ahead is the average corner of the average hybrid, BEFORE any
// focus multiplier. Fmax rides on top of this, so this is the headroom it has to work inside.
double MeanOf(string kind) => Enumerable.Range(0, n).Where(i => kinds[i] == kind).Average(i => mean[i]);
var c = MeanOf("corner");
Console.WriteLine();
Console.WriteLine($"pre-F concentration gap (mean win share):");
Console.WriteLine($"  corner  vs hybrid2 : {c - MeanOf("hybrid2"):+0.00%;-0.00%;0.00%}");
Console.WriteLine($"  corner  vs hybrid3 : {c - MeanOf("hybrid3"):+0.00%;-0.00%;0.00%}");
Console.WriteLine($"  corner  vs spread  : {c - MeanOf("spread"):+0.00%;-0.00%;0.00%}");

var outPath = args.Length > 1 ? args[1] : Path.Combine(repoRoot, "docs", "research", "class-system", "_hybrid-viability.json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
File.WriteAllText(outPath, JsonSerializer.Serialize(new
{
    at = DateTimeOffset.Now.ToString("o"),
    theta,
    note = "PRE-F baseline: the closed form reads allocation only; tree power and the focus multiplier " +
           "F are not modelled. Sized for passive-tree-ideal.md 3.3.",
    dominant = report.IsDominant ? report.DominantBuildNames.Select(x => labels[Index(x)]).ToArray() : Array.Empty<string>(),
    builds = Enumerable.Range(0, n).Select(i => new
    {
        label = labels[i], kind = kinds[i], meanWinShare = Math.Round(mean[i], 5), beatenBy = beatenBy[i]
    }).OrderByDescending(x => x.meanWinShare).ToArray()
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nwrote {outPath}");

// ---------------------------------------------------------------------------------------------
// TREE MODEL SWEEP — passive-tree-ideal.md D20/D3.3.
if (args.Contains("--trees"))
{
    var pointsPerTheta = 3L;   // grant.aptitudePointsPerTheta, data/tuning/aptitudes.v5.json
    var budget = theta * pointsPerTheta;

    static int TierFor(double points)
    {
        var t = 0;
        while (10 + 2.5 * (t + 1) * t <= points) t++;
        return t;
    }

    Console.WriteLine();
    Console.WriteLine($"=== TREE MODEL SWEEP (theta={theta}, budget={budget} aptitude points) ===");
    Console.WriteLine("b = aptitude-point-equivalents per tier unit; F = 1 + (Fmax-1)*H");
    Console.WriteLine();
    Console.WriteLine($"{"b",4} {"Fmax",5}   {"corner",8} {"hybrid2",8} {"hybrid3",8} {"spread",8}   verdict");

    foreach (var b in new[] { 0.0, 2.0, 5.0, 10.0, 20.0 })
    foreach (var fmax in new[] { 1.0, 1.25, 1.5 })
    {
        if (b == 0.0 && fmax != 1.0) continue;   // no trees => F is inert; report once

        var effective = new List<AptitudeAllocation>(builds.Count);
        foreach (var build in builds)
        {
            var pts = roster.ToDictionary(id => id,
                id => (double)build.PointsAt(AllocationScope.Commander, id));
            var total = pts.Values.Sum();
            var h = total <= 0 ? 0 : pts.Values.Sum(v => (v / total) * (v / total));
            var f = 1 + (fmax - 1) * h;
            var acc = AptitudeAllocation.Empty;
            foreach (var id in roster)
            {
                var p = pts[id] / total * budget;
                var tier = TierFor(p);
                var w = b * tier * (tier + 1) / 2.0;
                // back to the tool's own 100_000-total unit so shares stay comparable
                var eff = (p + f * w) / budget * Total;
                acc += AptitudeAllocation.Single(AllocationScope.Commander, id, (long)Math.Round(eff));
            }
            effective.Add(acc);
        }

        var rep = DominanceGuard.Measure(effective, theta);
        var m = new double[builds.Count];
        var sums = new double[builds.Count];
        var cnts = new int[builds.Count];
        foreach (var arrow in rep.Matrix)
        {
            var i = Index(arrow.AttackerName);
            sums[i] += arrow.WinShareAttacker; cnts[i]++;
        }
        for (var i = 0; i < builds.Count; i++) m[i] = cnts[i] == 0 ? 0 : sums[i] / cnts[i];
        double Mean(string k) => Enumerable.Range(0, builds.Count).Where(i => kinds[i] == k).Average(i => m[i]);

        var cor = Mean("corner"); var h2 = Mean("hybrid2"); var h3 = Mean("hybrid3"); var sp = Mean("spread");
        var verdict = cor > h2 && h2 > h3 && h3 > sp ? "focus>hybrid>spread  <== design intent"
            : cor > sp ? "focus beats spread"
            : "spread still wins";
        Console.WriteLine($"{b,4:0} {fmax,5:0.00}   {cor,8:P1} {h2,8:P1} {h3,8:P1} {sp,8:P1}   {verdict}");
    }
}

// === CROSS-UNLOCK SWEEP (owner, 2026-09-05: "model it and re-sweep before deciding") ===
if (args.Contains("--crossunlock"))
{
    var pointsPerTheta = 3L;
    var budget = theta * pointsPerTheta;
    const double B = 5.0;   // aptitude-point-equivalents per tier unit; ordering is b-invariant

    // Posture is READ from the shipped catalog, never re-declared here (Aptitude.cs:11,38-51).
    var postureOf = roster.ToDictionary(id => id, id => AptitudeCatalog.Get(id).Posture);

    // D20 as written: req(t) = 10 + 2.5*t*(t-1).  D26 reconciled: req(t) = 5*t*(t+1)/2.
    static int TierD20(double p) { var t = 0; while (10 + 2.5 * (t + 1) * t <= p) t++; return t; }
    static int TierD26(double p) { var t = 0; while (5.0 * (t + 1) * (t + 2) / 2.0 <= p) t++; return t; }
    // D29 caps the AUTHORED depth at 10 tiers. The ladder itself keeps going (PS-8), but there are
    // no nodes above tier 10 to buy, so power stops accruing there. The uncapped readings above are
    // kept so the sweep can show what the cap is worth.
    const int D29MaxTier = 10;
    static int TierD26Capped(double p) => Math.Min(TierD26(p), D29MaxTier);
    static int TierD20Capped(double p) => Math.Min(TierD20(p), D29MaxTier);

    // Build set aimed at the exact claim: pure vs concentrated-inside-one-posture vs spread.
    var xLabels = new List<string>(); var xKinds = new List<string>(); var xBuilds = new List<AptitudeAllocation>();
    void XAdd(string l, string k, AptitudeAllocation b) { xLabels.Add(l); xKinds.Add(k); xBuilds.Add(b); }

    foreach (var a in roster) XAdd(a, "corner", Build(a));
    foreach (var grp in roster.GroupBy(id => postureOf[id]))
    {
        var m = grp.ToArray();
        for (var i = 0; i < m.Length; i++)
        for (var j = i + 1; j < m.Length; j++)
            XAdd($"{m[i]}+{m[j]}", "inPosture2", Build(m[i], m[j]));
        XAdd($"{grp.Key}x4", "inPosture4", Build(m));            // the exploit build
    }
    // Cross-posture pairs, same count as inPosture2 (18) so the two means are comparable.
    var cross = (from a in roster from b in roster
                 where postureOf[a] != postureOf[b] && string.CompareOrdinal(a, b) < 0
                 select (a, b)).Take(18);
    foreach (var (a, b) in cross) XAdd($"{a}/{b}", "crossPosture2", Build(a, b));
    XAdd("even12", "spread", roster.Aggregate(AptitudeAllocation.Empty,
        (acc, id) => acc + AptitudeAllocation.Single(AllocationScope.Commander, id, Total / roster.Length)));

    Console.WriteLine();
    Console.WriteLine($"=== CROSS-UNLOCK SWEEP (theta={theta}, budget={budget} pts, b={B}, Fmax=1.20) ===");
    Console.WriteLine($"builds={xBuilds.Count}  corners=12 inPosture2=18 inPosture4=3 crossPosture2=18 spread=1");
    Console.WriteLine();
    Console.WriteLine($"{"ladder",6} {"credit rule",-12} {"corner",8} {"inPos2",8} {"inPos4",8} {"xPos2",8} {"spread",8}  {"treePwr in4/pure",16}");

    foreach (var (ladderName, tierFor) in new (string, Func<double, int>)[]
        { ("D20", TierD20), ("D26", TierD26), ("D26@10", TierD26Capped), ("D20@10", TierD20Capped) })
    foreach (var rule in new[] { "none", "largest", "quarter", "full" })
    {
        var effective = new List<AptitudeAllocation>(xBuilds.Count);
        var treePower = new double[xBuilds.Count];

        for (var bi = 0; bi < xBuilds.Count; bi++)
        {
            var pts = roster.ToDictionary(id => id, id => (double)xBuilds[bi].PointsAt(AllocationScope.Commander, id));
            var total = pts.Values.Sum();
            var h = total <= 0 ? 0 : pts.Values.Sum(v => (v / total) * (v / total));
            var f = 1 + (1.20 - 1) * h;                                  // D5 as revised
            var acc = AptitudeAllocation.Empty;
            foreach (var id in roster)
            {
                var p = pts[id] / total * budget;
                var mates = roster.Where(o => o != id && postureOf[o] == postureOf[id])
                                  .Select(o => pts[o] / total * budget).ToArray();
                var credit = rule switch
                {
                    "largest" => mates.Length == 0 ? 0 : mates.Max(),
                    "quarter" => 0.25 * mates.Sum(),
                    "full"    => mates.Sum(),
                    _         => 0.0,
                };
                var tier = tierFor(p + credit);                          // the GATE reads the credit
                var w = B * tier * (tier + 1) / 2.0;                     // power still linear per tier
                treePower[bi] += w;
                acc += AptitudeAllocation.Single(AllocationScope.Commander, id,
                                                 (long)Math.Round((p + f * w) / budget * Total));
            }
            effective.Add(acc);
        }

        var rep = DominanceGuard.Measure(effective, theta);
        var sums = new double[xBuilds.Count]; var cnts = new int[xBuilds.Count];
        foreach (var arrow in rep.Matrix) { var i = Index(arrow.AttackerName); sums[i] += arrow.WinShareAttacker; cnts[i]++; }
        var xMean = Enumerable.Range(0, xBuilds.Count).Select(i => cnts[i] == 0 ? 0 : sums[i] / cnts[i]).ToArray();
        double M(string k) => Enumerable.Range(0, xBuilds.Count).Where(i => xKinds[i] == k).Average(i => xMean[i]);
        double TP(string k) => Enumerable.Range(0, xBuilds.Count).Where(i => xKinds[i] == k).Average(i => treePower[i]);

        var ratio = TP("inPosture4") / TP("corner");
        Console.WriteLine($"{ladderName,6} {rule,-12} {M("corner"),8:P1} {M("inPosture2"),8:P1} {M("inPosture4"),8:P1} " +
                          $"{M("crossPosture2"),8:P1} {M("spread"),8:P1}  {ratio,15:0.00}x");
    }
}
