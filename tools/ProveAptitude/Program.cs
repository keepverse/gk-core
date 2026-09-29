using System.Globalization;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;

// class-system-todo.md P2.6, V3: drives a resolve on BOTH engines for the same allocation/Theta and
// emits { theta, perChannel: {overlay, battle}, deltas } -- fails (exit 1) on any non-zero delta.
// Follows prove-overlay-combat.ps1's -OutJson/exit-1-on-failure shape; unlike that script, both
// engines being compared are pure FusionRpg.Core types, so this is a console tool, not a live-game
// REST probe (V3's own note on why prove-aptitude.ps1 could not be written before this existed).

string repoRoot = FindRepoRoot();
string tuningDir = Path.Combine(repoRoot, "data", "tuning");

var aptitudeTuning = AptitudeTuningLoader.Parse(File.ReadAllText(Path.Combine(tuningDir, "aptitudes.v2.json")));
var powerTuning = PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(tuningDir, "power-scale.v2.json")));
DerivedStatPolicy.Configure(DerivedStatTuningLoader.Parse(File.ReadAllText(Path.Combine(tuningDir, "derived-stats.v2.json"))));

// The line 51 comment below used to be true -- Tuning was only read inside the ElementPrimary/
// ElementSecondary branches, both skipped by this tool's null defaults. Later feature waves broke
// that: battle-tempo (T14/B28) added an unconditional Tuning.SpeciesTempoReferenceIntervalMs read for
// turnSpeed, and battle-resources seeded all six resource pools via BattleRuleset.Base*, which in turn
// calls BattleRuleset.BaseHp -> PowerTuningHub.Tuning (the SAME power-scale.v2.json already parsed
// above into `powerTuning` for the overlay-side PowerLadder -- the battle side reads it through a
// separate static hub, never through that local instance). BattleHubCompose.Compose (battle-hub-fuse
// T6's replacement for the deleted BattleStatComposer.Compose) throws the same "Configure(...) has not
// run" on every call regardless of which setup fields are populated, so this mirrors Program.cs's own
// boot sequence exactly (same loaders, same tuning files) rather than inventing a narrower substitute.
FusionRpg.Core.Power.PowerTuningHub.Configure(powerTuning);
FusionRpg.Core.Battle.BattleTuningHub.Configure(
    FusionRpg.Core.Battle.BattleTuningLoader.Parse(File.ReadAllText(Path.Combine(tuningDir, "battle.v5.json"))));
FusionRpg.Core.Battle.BattleRuleset.ConfigureResources(
    FusionRpg.Core.Battle.BattleResourceTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "battle-resources.v1.json"))));
// battle-hub-fuse T6: AptitudeSubsystem (the battle side's new Hub twin) reads AptitudeTuningHub.Tuning
// directly rather than taking a tuning parameter -- the deleted ResolveForBattle took aptitudeTuning as
// a plain argument, so this global-hub configure step is new, not a duplicate of an existing one.
FusionRpg.Core.Stats.Aptitudes.AptitudeTuningHub.Configure(aptitudeTuning);
// solid-remediation T3.2: BattleHubCompose now routes through ActorHubBootstrap.CreateDefault, which
// registers RpgProgressionSubsystem unconditionally -- and that subsystem writes `progression.realm`
// from StatusPolicy.ProgressionPowerStubDefault, i.e. out of status.v{n}.json. So battle compose gained
// a status-tuning dependency it did not have, and this tool threw "Configure(...) has not run" on every
// call. Extended for exactly the reason the comment above records for T6's own new dependencies: mirror
// Program.cs's boot sequence with the same loader and the same file, never a narrower substitute.
FusionRpg.Core.Status.StatusPolicy.Configure(
    FusionRpg.Core.Status.StatusTuningLoader.Parse(
        File.ReadAllText(Path.Combine(tuningDir, "status.v1.json"))));

var ladder = new PowerLadder(powerTuning);
var registry = DerivedStatRegistry.CreateDefault();

int theta = ArgInt(args, "--theta", 1000);
string source = ArgStringOrDefault(args, "--source", "Might");
long points = ArgLong(args, "--points", 100);
string? outPath = ArgString(args, "--out", null);
// Default: unfiltered -- every channel the allocation touches, compared. class-system-todo.md P2.6 /
// Checkpoint 2 is scoped to "Might -> combat.power.omni" (the one vertical slice P2.4/P2.5 actually
// built and proved), so its own invocation passes --channels explicitly. Left unfiltered by default
// because the wider comparison is useful and HONEST.
var channelFilter = ArgString(args, "--channels", null)?
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .ToHashSet(StringComparer.Ordinal);

var allocation = AptitudeAllocation.Single(AllocationScope.Commander, source, points);

// Overlay path: AptitudeSubsystem's own seam, minus the ActorHub/StatContext ceremony this tool
// doesn't need -- Resolve -> DerivedComposer.Compose, exactly what AptitudeSubsystem.ContributeDerived
// does per-call.
var overlayMods = AptitudeResolver.Resolve(allocation, aptitudeTuning, ladder, theta, registry);
var overlaySnapshot = new DerivedComposer(registry).Compose(overlayMods);

// Battle path (battle-hub-fuse T6): the allocation reaches battle as a Hub input
// (BattleHubInputs.Aptitude), resolved through AptitudeSubsystem -> the same AptitudeResolver.Resolve
// the overlay path calls above -- exactly what WebMatchService's squad builder feeds a real setup.
// ElementPrimary/Secondary and TraitIds stay at their record defaults (null / empty), so
// BattleAffinitySubsystem/BattleTraitSubsystem contribute nothing -- this tool still proves only the
// aptitude seam, not the whole battle-setup pipeline. BattleTuningHub.Configure/
// BattleRuleset.ConfigureResources ARE required (see the boot sequence above): Compose's turnSpeed and
// six-resource-pool baseline seeding read Tuning/ResourceTuning unconditionally on every call,
// independent of which setup fields are set.
var setup = new BattleActorSetup
{
    Key = "prove-aptitude", Side = "squad", Level = theta,
    HubInputs = new BattleHubInputs { Aptitude = allocation },
};
var battleSnapshot = BattleHubCompose.Compose(setup);

var channels = overlayMods.Select(m => m.ChannelId)
    .Distinct(StringComparer.Ordinal)
    .Where(c => channelFilter is null || channelFilter.Contains(c))
    .OrderBy(c => c, StringComparer.Ordinal)
    .ToList();

var perChannel = new Dictionary<string, PerChannel>(StringComparer.Ordinal);
var deltas = new Dictionary<string, double>(StringComparer.Ordinal);
var anyNonZero = false;
const double Epsilon = 1e-9;

foreach (var ch in channels)
{
    var overlayVal = overlaySnapshot.Get(ch, 0.0);
    var battleVal = battleSnapshot.Get(ch, 0.0);
    var delta = overlayVal - battleVal;
    perChannel[ch] = new PerChannel(overlayVal, battleVal);
    deltas[ch] = delta;
    if (Math.Abs(delta) > Epsilon) anyNonZero = true;
}

if (channels.Count == 0)
{
    Console.Error.WriteLine(channelFilter is null
        ? $"error: source '{source}' at {points} points funds no edge in the shipped tuning -- nothing to compare"
        : $"error: --channels filter matched none of the channels source '{source}' funds -- nothing to compare");
    return 1;
}

var result = new ProveAptitudeResult(theta, source, points, perChannel, deltas, !anyNonZero);
var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });

outPath ??= Path.Combine(repoRoot, "docs", "research", "class-system", "_prove-aptitude.json");
Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
File.WriteAllText(outPath, json);
Console.WriteLine(json);
Console.WriteLine();
Console.WriteLine(anyNonZero
    ? $"FAIL — {deltas.Count(kv => Math.Abs(kv.Value) > Epsilon)} channel(s) disagree between overlay and battle"
    : $"OK — {channels.Count} channel(s), all deltas zero. Wrote {outPath}");

return anyNonZero ? 1 : 0;

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
        if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("could not locate repo root above " + AppContext.BaseDirectory);
}

static string? ArgString(string[] a, string flag, string? fallback)
{
    var i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : fallback;
}

static string ArgStringOrDefault(string[] a, string flag, string fallback) =>
    ArgString(a, flag, fallback) ?? fallback;

static int ArgInt(string[] a, string flag, int fallback)
{
    var s = ArgString(a, flag, null);
    return s is null ? fallback : int.Parse(s, CultureInfo.InvariantCulture);
}

static long ArgLong(string[] a, string flag, long fallback)
{
    var s = ArgString(a, flag, null);
    return s is null ? fallback : long.Parse(s, CultureInfo.InvariantCulture);
}

readonly record struct PerChannel(double Overlay, double Battle);
sealed record ProveAptitudeResult(
    int Theta, string Source, long Points,
    Dictionary<string, PerChannel> PerChannel, Dictionary<string, double> Deltas, bool Pass);
