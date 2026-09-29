using System.Diagnostics;
using FusionRpg.Core.Balance.Guards;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;

// class-system-todo.md P8.6/P8.7 -- run -> emitted metrics -> aggregate -> fit -> publish, one command
// chain, no human step. Reuses the shipped telemetry shape (PerfProbe.RecordValue, the SAME call sites
// ActorHub.ResolveDerived already carries for progression.power/resource.regen.stamina -- P1.10/P8.2)
// and the SAME production resolver (AptitudeResolver via ActorHubBootstrap) and guard
// (TerminationGuard/Predictor) every other measurement this program made this session used. Runs on
// SIMULATED (closed-form) data only -- no server, no PerfProbe HTTP hop -- matching P8.6's own verify
// line ("the chain runs on simulated runs"); the live-server POST /api/perf leg is Phase 9's own job
// (P9.1, "collect real-run metrics"), not this one's.

string repoRoot = FindRepoRoot();
string tuningDir = Path.Combine(repoRoot, "data", "tuning");
string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));

var dryRun = args.Contains("--dry-run");
var plantReserved = args.Contains("--plant-reserved-coefficient"); // P8.7's own proof scenario.
var label = ArgOrDefault(args, "--label", "P8.6 automated residual-fit loop");
// --domain defaults to the real, live "aptitudes" domain; a test can point it at a disposable domain
// name so the FULL chain (through this tool's own publish step, not a hand-rolled equivalent) can be
// proven end to end without ever risking the real gk-core/data/tuning/ files -- the exact incident this
// pass's own research note records (docs/research/class-residual-2026-08-27.md, P8.6 section).
var domain = ArgOrDefault(args, "--domain", "aptitudes");
// solid-enforcement tuning-immutability (SE2.2): --tuning-dir isolates the ONE domain this tool
// fits/publishes into a disposable directory (a test's own temp copy of e.g. aptitudes.v1.json) --
// `tuningDir`/`Read()` above stay pointed at the REAL gk-core/data/tuning/ for the other, read-only support
// files (combat/shield/derived-stats/power-scale/status/stats) this tool bootstraps the resolver
// with, since a test isolating its own domain never needs to duplicate those too. Defaults to the
// real gk-core/data/tuning/, so omitting the flag is byte-identical to this tool's prior behaviour.
var tuningDirPath = ArgOrDefault(args, "--tuning-dir", Path.Combine(repoRoot, "data", "tuning"));
// Defaults to the live shipped file for the CONFIGURED domain (never a hardcoded version literal --
// AptitudeTuningHub.cs's own doc comment already warns this exact staleness can recur silently on the
// next version bump); --input <path> targets an arbitrary tuning file (e.g. v1, or a scratch copy) for
// proving the fit algorithm itself against a KNOWN-broken input, without touching gk-core/data/tuning/ or
// requiring the file to already be the one publish.py would bump from.
var inputPath = ArgOrDefault(args, "--input", Path.Combine(tuningDirPath, LatestTuningFileName(tuningDirPath, domain)));

AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(File.ReadAllText(inputPath)));
CombatPolicy.Configure(CombatTuningLoader.Parse(Read("combat.v1.json")));
ShieldPolicy.Configure(ShieldTuningLoader.Parse(Read("shield.v1.json")));
DerivedStatPolicy.Configure(DerivedStatTuningLoader.Parse(Read("derived-stats.v2.json")));
PowerTuningHub.Configure(PowerTuningLoader.Parse(Read("power-scale.v2.json")));
StatusPolicy.Configure(StatusTuningLoader.Parse(Read("status.v1.json")));
StatsTuningHub.Configure(StatsTuningLoader.Parse(Read("stats.v1.json")));

// class-system-todo.md P8.7: a coefficient whose family has no shipped reader AND no cited external
// target must never be fit. Matches _meta.measurable's own current prose (gk-core/data/tuning/aptitudes.v2.json)
// -- hardcoded here rather than parsed from that prose string, since the prose is free text meant for a
// human, not a machine-readable contract.
var reservedFamilyPrefixes = new[]
{
    "resource.max.stamina", "resource.efficiency.stamina",
    "resource.max.qi", "resource.regen.qi", "resource.efficiency.qi",
    "resource.max.hunger", "resource.regen.hunger", "resource.efficiency.hunger",
    "resource.max.spirit", "resource.regen.spirit",
    "skill.cooldown", "resource.efficiency", "move.range",
};
bool IsReserved(string channel) => reservedFamilyPrefixes.Any(p => channel.StartsWith(p, StringComparison.Ordinal));

string[] roster =
{
    "Might", "Fortitude", "Vigor", "Onslaught", "Agility", "Composure",
    "Pierce", "Focus", "Bulwark", "Retribution", "Precision", "Ferocity",
};
const long floor = 4167;
long Spike() => 100_000 - floor * (roster.Length - 1);
AptitudeAllocation Corner(string spikeId) =>
    roster.Aggregate(AptitudeAllocation.Empty, (acc, id) =>
        acc + AptitudeAllocation.Single(AllocationScope.Commander, id, id == spikeId ? Spike() : floor));

ActorDerivedSnapshot Resolve(string name, AptitudeAllocation allocation, int theta)
{
    var hub = ActorHubBootstrap.CreateDefault(
        powerIndex: new FixedPowerIndexProvider(theta),
        aptitudeTuning: AptitudeTuningHub.Tuning,
        aptitudeAllocation: _ => allocation);
    var ctx = hub.Stats.Contexts.ForPlant(name, new EntityBaseline());
    return hub.ResolveDerived(ctx); // production path: fires PerfProbe.RecordValue for progression.power/resource.regen.stamina (P1.10/P8.2).
}

// ── 1. RUN — simulated corners, production resolver, production PerfProbe call sites ──────────────
Console.WriteLine("== ResidualFitLoop: run ==");
PerfProbe.ResetAll();
const long theta = 100;
const double citedStrikeCostPerRound = 1544.0; // spec-residual-fit.md:56 -- the only cited external reference this loop fits against.

var perCornerStaminaRegen = new Dictionary<string, double>();
foreach (var id in roster)
{
    var d = Resolve(id, Corner(id), (int)theta);
    perCornerStaminaRegen[id] = d.Get(DerivedStatChannels.ResourceRegen("stamina"), 0);
}

var violations = new List<(string A, string B, double NetA, double NetB)>();
for (var i = 0; i < roster.Length; i++)
    for (var j = i + 1; j < roster.Length; j++)
    {
        try { TerminationGuard.Assert(new[] { Corner(roster[i]), Corner(roster[j]) }, theta); }
        catch (TerminationViolation ex) { violations.Add((roster[i], roster[j], ex.NetAttritionA, ex.NetAttritionB)); }
    }

// ── 2. EMITTED METRICS — the shipped telemetry shape, not a second pipeline ────────────────────────
var snapshot = PerfProbe.SnapshotAndReset();
Console.WriteLine($"  PerfProbe snapshot keys: {string.Join(", ", snapshot.Keys)}");

// ── 3. AGGREGATE ─────────────────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== aggregate ==");
Console.WriteLine($"  stamina.regen: {perCornerStaminaRegen.Count} corners, max={perCornerStaminaRegen.Values.Max():F1}, cited cost={citedStrikeCostPerRound}");
Console.WriteLine($"  termination violations: {violations.Count}");
foreach (var v in violations) Console.WriteLine($"    {v.A} vs {v.B} (netA={v.NetA:F1}, netB={v.NetB:F1})");

// ── 4. FIT ───────────────────────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("== fit ==");
var changes = new List<(string DottedKey, long OldKMilli, long NewKMilli)>();
var refused = new List<string>();

// Pattern A: proportional-target fit (P8.2's own methodology, generalised). A metric with a known
// external target (regen must stay under a cited cost) is scaled toward TargetRatio of that target --
// 0.90, matching Might/Bulwark's own already-shipped, already-accepted level, not a fresh guess.
const double targetRatio = 0.90;
foreach (var (id, regen) in perCornerStaminaRegen)
{
    if (regen <= citedStrikeCostPerRound) continue; // already binds, nothing to fit.
    var channel = "resource.regen.stamina";
    if (IsReserved(channel) && !AllowFitDespiteReserved(channel))
    {
        refused.Add($"{channel} (source={id}): family is reserved (no shipped reader for the action-cost side) -- refusing to fit, per P8.7");
        continue;
    }
    var oldKMilli = FindEdgeKMilli(AptitudeTuningHub.Tuning, channel, id);
    if (oldKMilli is null) continue;
    var newKMilli = (long)Math.Round(oldKMilli.Value * (citedStrikeCostPerRound * targetRatio / regen));
    changes.Add(($"edges[channel={channel},source={id}].kMilli", oldKMilli.Value, newKMilli));
}

// P8.7's own proof scenario: plant a reserved-family "violation" and confirm the loop refuses it
// rather than silently fitting noise. resource.efficiency.qi has no shipped reader (cited in
// _meta.measurable) -- treat ANY value here as "a coefficient someone tried to fit" and refuse it,
// exactly the shape a real future caller could trigger by mistake.
if (plantReserved)
{
    const string plantedChannel = "resource.efficiency.qi";
    if (IsReserved(plantedChannel))
        refused.Add($"{plantedChannel} (source=Focus, PLANTED): family is reserved -- refusing to fit, per P8.7");
    else
        throw new InvalidOperationException($"--plant-reserved-coefficient expected {plantedChannel} to be reserved, but IsReserved() said no -- the planted proof itself is broken, not proving anything");
}

// Pattern B (termination-violation guarded search) is NOT attempted by this pass: class-system-ideal.md
// 5d.4b's own warning ("fixing either alone moves the other") means a fully-automated search needs a
// dominance re-check after every candidate step across the WHOLE roster, not just the pair that
// violated -- P8.3's own real, hand-run history (a targeted cut tried first and REJECTED after
// measuring it made Might absolutely dominant) is the exact shape an automated search must reproduce
// to be trustworthy, and building that guarded search is real, separate scope beyond what a first
// loop pass should claim done. Recorded honestly rather than attempted unsafely: zero violations exist
// on the current shipped config (P8.3, verified at 4 Theta points), so there is nothing THIS run needs
// pattern B to fix -- the gap is in the LOOP's own generality, not in today's tuning.
Console.WriteLine($"  {changes.Count} change(s) computed, {refused.Count} refused (reserved)");
foreach (var c in changes) Console.WriteLine($"    {c.DottedKey}: {c.OldKMilli} -> {c.NewKMilli}");
foreach (var r in refused) Console.WriteLine($"    REFUSED: {r}");

// ── 5. PUBLISH ───────────────────────────────────────────────────────────────────────────────────
Console.WriteLine();
if (changes.Count == 0)
{
    Console.WriteLine("== publish == nothing to publish (no coefficient exceeded its target)");
    return 0;
}
if (dryRun)
{
    Console.WriteLine("== publish == --dry-run, not invoking publish.py");
    return 0;
}
// SAFETY: publish.py's own `aptitudes` domain always targets gk-core/data/tuning/'s own highest-version file,
// regardless of what --input this run measured against -- a real incident this tool caused once during
// its own development: running with --input pointing at a scratch/historical file still published
// against the REAL, live aptitudes domain, silently bumping it to a version computed from the WRONG
// input (v1's own numbers, applied on top of the real v2). Caught and reverted immediately, not left
// for review to catch. Refuse rather than risk repeating it: publishing is only safe when --input IS
// the domain's own current file, so this run's own numbers are the domain's own numbers plus this fit,
// not some other file's.
var liveDomainPath = Path.Combine(tuningDirPath, LatestTuningFileName(tuningDirPath, domain));
if (!PathsRefEqual(inputPath, liveDomainPath))
{
    Console.WriteLine($"== publish == REFUSED: --input ({inputPath}) is not the '{domain}' domain's own current file ({liveDomainPath}) -- publishing now would apply this run's numbers on top of a DIFFERENT file than the one they were measured against. Use --dry-run to inspect a non-live input, or omit --input to fit and publish the real, live config.");
    return 1;
}
Console.WriteLine("== publish ==");
var publishArgs = new List<string> { Path.Combine(repoRoot, "tools", "tuning", "publish.py"), domain, "--label", label, "--tuning-dir", tuningDirPath };
publishArgs.AddRange(changes.Select(c => $"{c.DottedKey}={c.NewKMilli}"));
var psi = new ProcessStartInfo("python") { WorkingDirectory = repoRoot, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
foreach (var a in publishArgs) psi.ArgumentList.Add(a);
using var proc = Process.Start(psi)!;
Console.WriteLine(proc.StandardOutput.ReadToEnd());
Console.Write(proc.StandardError.ReadToEnd());
proc.WaitForExit();
if (proc.ExitCode != 0) return proc.ExitCode;

// class-system-todo.md Checkpoint 8: "Theta-invariance exact and termination green AFTER EVERY FIT" —
// not a one-off property of today's config, a standing loop guarantee. Re-load the version publish.py
// just wrote (never assume the fit that looked safe on the input also holds on the published output —
// they are the same numbers, but re-checking the artifact that will actually ship is what this line
// asks for, not re-trusting the computation that produced it) and re-sweep termination on it directly.
Console.WriteLine();
Console.WriteLine("== post-publish verification ==");
var publishedPath = Path.Combine(tuningDirPath, LatestTuningFileName(tuningDirPath, domain));
AptitudeTuningHub.Configure(AptitudeTuningLoader.Parse(File.ReadAllText(publishedPath)));
var postPublishViolations = new List<string>();
foreach (var thetaCheck in new long[] { 20, 100, 500, 2000 })
    for (var i = 0; i < roster.Length; i++)
        for (var j = i + 1; j < roster.Length; j++)
        {
            try { TerminationGuard.Assert(new[] { Corner(roster[i]), Corner(roster[j]) }, thetaCheck); }
            catch (TerminationViolation) { postPublishViolations.Add($"Theta={thetaCheck}: {roster[i]} vs {roster[j]}"); }
        }
if (postPublishViolations.Count > 0)
{
    Console.WriteLine($"  WARNING: {postPublishViolations.Count} termination violation(s) on the file this run JUST published:");
    foreach (var v in postPublishViolations) Console.WriteLine($"    {v}");
    Console.WriteLine("  Published anyway (publish.py already wrote the file, and reverting a file is the owner's own call per T4) — but this fit did not actually satisfy the termination invariant it targeted. Investigate before trusting this version.");
}
else
{
    Console.WriteLine($"  0 termination violations across all {roster.Length * (roster.Length - 1) / 2} pairs, at Theta=20/100/500/2000 — the published file holds the invariant, verified, not assumed.");
}
return proc.ExitCode;

// ── helpers ──────────────────────────────────────────────────────────────────────────────────────
static bool AllowFitDespiteReserved(string channel) => false; // no override exists; a reserved family is refused unconditionally.

static long? FindEdgeKMilli(AptitudeTuning tuning, string channel, string source) =>
    tuning.Edges.Where(e => e.Channel == channel && e.Source == source).Select(e => (long?)e.KMilli).FirstOrDefault();

static string ArgOrDefault(string[] a, string flag, string fallback)
{
    var idx = Array.IndexOf(a, flag);
    return idx >= 0 && idx + 1 < a.Length ? a[idx + 1] : fallback;
}

/// <summary>Mirrors gk-core/tools/tuning/publish.py's own latest_version(domain) exactly (highest
/// {domain}.v{n}.json in the tuning dir) so this tool can check BEFORE invoking publish.py whether
/// --input actually IS the file publish.py itself would treat as current -- for WHATEVER domain
/// --domain names, not just the real "aptitudes" one, so a test can safely exercise this exact check
/// against a disposable domain instead.</summary>
static string LatestTuningFileName(string tuningDir, string domain)
{
    var pat = new System.Text.RegularExpressions.Regex($@"^{System.Text.RegularExpressions.Regex.Escape(domain)}\.v(\d+)\.json$");
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

static bool PathsRefEqual(string a, string b) =>
    string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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

sealed class FixedPowerIndexProvider : IPowerIndexProvider
{
    readonly int _theta;
    public FixedPowerIndexProvider(int theta) => _theta = theta;
    public int ActorIndex(StatContext ctx) => _theta;
    public int ContentIndex(ContentContext ctx) => _theta;
    public PowerAxisReport Explain(StatContext ctx) => new(_theta, Array.Empty<PowerAxisContribution>());
    /// <summary>One constant Theta for everything, the empire included.</summary>
    public int ActorIndexFor(FusionRpg.Core.Saves.SaveId save, FusionRpg.Core.Commanders.EmpireId empire) => _theta;
}
