using System.Text.Json;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Tools.SquadHarness;

// spec-squad-harness.md "Project structure": "Program.cs stays a thin CLI over them" -- parse args,
// pick the mode, print, write. Every real measurement type (BuildFactory, SquadRoster, SquadMatch,
// Sweep, TransferReport, Resolution, DeterminismHash) lives in its own file and is fully reachable from
// a test project via ProjectReference, which is the whole reason this tool is NOT the single-top-level-
// Program.cs shape gk-core/tools/HybridViability and gk-core/tools/CombatSim use (todo.md's Phase F intro, quoting the
// spec by name).
//
// F2 ships four modes for real: `duel`, `squad` (+ `--opponent wave`, §2), `transfer` (the three-column
// report, §5) and `verify` (the determinism self-check). F3 adds `erosion` (§10.1, mechanism-wiring
// A10a). F4 adds `concentration` and `crossunlock` (§4/§11 S2 -- the tree model, D25's ownership cost,
// D28's four credit rules). F5 adds `soultrack` (§11 S3). F6 adds `budget` (§11 S4, D15's marginal
// win share per budget point, across the DUEL roster).

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: SquadHarness <duel|squad|transfer|erosion|concentration|crossunlock|soultrack|budget|verify> --seed <n> " +
                             "[--theta <n>] [--trials <n>] [--refine <n>] [--allocation-shape shipped|per-actor] " +
                             "[--opponent squad|wave] [--wave <id>] [--roster duel|squad|all] [--limit <n>] " +
                             "[--erosion-milli <n>] [--fmax-milli <list>] [--w-milli <list>] [--b <n>] " +
                             "[--rule none,largest,quarter,full] [--theta-per-soul-level-milli <list>] " +
                             "(soultrack: --theta <list> is its OWN required comma sweep, not the single global value) " +
                             "(budget: --b-list <list> is its OWN required comma sweep of at least two b values, " +
                             "e.g. --b-list 5,6; --fmax-milli/--w-milli take single values here, defaulting to the shipped tuning) " +
                             "[--parallel] [--out <path>]");
    return 2;
}

var mode = args[0];
var rest = args.Skip(1).ToList();

// spec "Commands": "--seed is required -- there is no default... a seed nobody chose behaves like one
// somebody did." Refused loudly and by name, never defaulted.
if (!TryGetOption(rest, "--seed", out var seedText))
{
    Console.Error.WriteLine("refused: --seed is required and has no default (a seed nobody chose is not reproducible)");
    return 2;
}
if (!ulong.TryParse(seedText, out var runSeed))
{
    Console.Error.WriteLine($"refused: --seed must be an unsigned 64-bit integer, got '{seedText}'");
    return 2;
}

var theta = GetLongOption(rest, "--theta", 100L);
var trials = GetLongOption(rest, "--trials", 100L);
var refineTrials = Modes.ParseRefineTrials(rest);
var parallel = rest.Contains("--parallel");
var outPath = TryGetOption(rest, "--out", out var outText) ? outText : null;
var shape = Modes.ParseAllocationShape(rest);

TuningBootstrap.Configure();
var spec = new RunSpec(theta, trials, runSeed);

switch (mode)
{
    case "duel":
    {
        var roster = Modes.BuildDuelRoster();
        var screening = Screening.RunWithRefine("duel", roster, spec, refineTrials, parallel);
        Emit(screening.Run, outPath);
        return 0;
    }
    case "squad":
    {
        var opponent = Modes.ParseOpponent(rest);
        var roster = Modes.BuildSquadRoster(shape);
        if (opponent == "wave")
        {
            // spec §2: squad-vs-wave is reported separately, never mixed into the two named artifacts.
            if (!TryGetOption(rest, "--wave", out var waveId))
            {
                Console.Error.WriteLine("refused: --opponent wave requires --wave <id> (e.g. rift-tyrant)");
                return 2;
            }
            var waveReport = WaveOpponent.Run(roster, waveId, runSeed, trials);
            var wavePayload = new
            {
                allocationShape = shape.ToString(),
                waveId = waveReport.WaveId,
                waveTheta = waveReport.WaveTheta,
                note = Coverage.WaveOpponentNoteText,
                results = waveReport.Results,
            };
            var waveJson = JsonSerializer.Serialize(wavePayload,
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Console.WriteLine(waveJson);
            if (outPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                File.WriteAllText(outPath, waveJson);
            }
            return 0;
        }

        // Primary verdict (D46): mirror squads. Two-stage screened; every cell carries a half-width
        // (F2 acceptance) via Artifacts.WriteSquadScope, which also owns the default artifact path.
        var screening = Screening.RunWithRefine("squad", roster, spec, refineTrials, parallel);
        var squadJson = Artifacts.WriteSquadScope("squad", screening, spec, shape, outPath);
        Console.WriteLine(squadJson);
        return 0;
    }
    case "transfer":
    {
        var result = TransferReport.Build(spec, shape, refineTrials, parallel);
        foreach (var warning in result.ClosedFormDriftWarnings)
            Console.Error.WriteLine($"warning: {warning}");
        var transferJson = Artifacts.WriteTransfer(result, outPath);
        Console.WriteLine(transferJson);
        return 0;
    }
    case "erosion":
    {
        // spec-squad-harness.md §10.1 / spec-mechanism-wiring.md §11.1 -- mechanism-wiring's A10a.
        // --erosion-milli has no default (Erosion.Amount's own doc: "measured before it is specced").
        long erosionMilli;
        try
        {
            erosionMilli = ErosionMode.ParseErosionMilli(rest);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var result = Erosion.Build(spec, erosionMilli, refineTrials);
        var erosionJson = Erosion.WriteArtifact(result, outPath);
        Console.WriteLine(erosionJson);
        if (result.Duel.Verdict == "FAIL" || result.Duel.Verdict == "UNRESOLVED" ||
            result.Squad.Verdict == "FAIL" || result.Squad.Verdict == "UNRESOLVED")
            Console.Error.WriteLine(
                $"checkpoint F holds: duel={result.Duel.Verdict}, squad={result.Squad.Verdict} -- " +
                "UNRESOLVED holds the checkpoint exactly as FAIL does (spec-mechanism-wiring.md §11.1).");
        return 0;
    }
    case "concentration":
    {
        // spec §4/§6/§11 S2: the Fmax x w x ownership-cost sweep. --fmax-milli/--w-milli/--b are all
        // required, no default -- matching the spec's own example command and Erosion's own
        // --erosion-milli precedent: an unstated sweep range or dial is a design choice nobody made.
        IReadOnlyList<long> fmaxMillis, wMillis;
        long concentrationB;
        try
        {
            fmaxMillis = Modes.ParseLongList(rest, "--fmax-milli") ?? throw new ArgumentException(
                "refused: --fmax-milli is required and has no default (D5 is provisional -- an unstated " +
                "sweep range would be a design choice nobody made)");
            wMillis = Modes.ParseLongList(rest, "--w-milli") ?? throw new ArgumentException(
                "refused: --w-milli is required and has no default (D8's blend weight -- same reasoning as --fmax-milli)");
            concentrationB = Modes.ParseLong(rest, "--b") ?? throw new ArgumentException(
                "refused: --b is required and has no default (spec §6: 'b is not a balance dial... a content-density choice')");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var concSquads = SquadRoster.Squads(shape);
        var concCorner = concSquads.Single(s => s.Id == $"mono-{BuildFactory.Roster[0].ToLowerInvariant()}");
        var concSpread = concSquads.Single(s => s.Id == "mono-spread");

        TreeModel.ConcentrationResult concResult;
        try
        {
            concResult = TreeModel.ConcentrationSweep(spec, concCorner, concSpread, fmaxMillis, wMillis, concentrationB, refineTrials, parallel);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        var concJson = TreeModel.WriteConcentrationArtifact(concResult, outPath);
        Console.WriteLine(concJson);
        return 0;
    }
    case "crossunlock":
    {
        // spec §6/§11 S2: D28's four credit rules re-run at squad scope. --rule is required (no
        // default). --fmax-milli/--w-milli/--b default to the SHIPPED concentration tuning and doc
        // 05's own "ordering is b-invariant" figure -- this mode's own axis is the credit rule, not Fmax.
        var rules = Modes.ParseCreditRules(rest);
        if (rules is null)
        {
            Console.Error.WriteLine("refused: --rule is required and has no default (e.g. --rule none,largest,quarter,full)");
            return 2;
        }
        var crossFmaxMilli = Modes.ParseLong(rest, "--fmax-milli") ?? PassiveTreeTuningHub.Tuning.Concentration.FmaxMilli;
        var crossWMilli = Modes.ParseLong(rest, "--w-milli") ?? PassiveTreeTuningHub.Tuning.Concentration.WMilli;
        var crossB = Modes.ParseLong(rest, "--b") ?? TreeModel.DefaultCrossUnlockB;

        var crossSquads = SquadRoster.Squads(shape);
        var crossCorner = crossSquads.Single(s => s.Id == $"mono-{BuildFactory.Roster[0].ToLowerInvariant()}");
        var crossSpread = crossSquads.Single(s => s.Id == "mono-spread");

        var crossResult = TreeModel.CrossUnlockSweep(spec, crossCorner, crossSpread, rules, crossFmaxMilli, crossWMilli, crossB, refineTrials, parallel);
        var crossJson = TreeModel.WriteCrossUnlockArtifact(crossResult, outPath);
        Console.WriteLine(crossJson);
        return 0;
    }
    case "soultrack":
    {
        // spec §11 S3 / todo "F5: S3 -- the soul track in the model". --theta/--w-milli/
        // --theta-per-soul-level-milli are all required comma lists, no default -- matching --fmax-milli/
        // --w-milli/--rule's own "an unstated sweep range is a design choice nobody made" convention.
        // The global single-value --theta (used by every other mode) is intentionally NOT read here:
        // this mode owns its own theta SWEEP.
        IReadOnlyList<long> soulThetas, soulWMillis, soulThetaPerSoulLevelMillis;
        try
        {
            soulThetas = Modes.ParseLongList(rest, "--theta") ?? throw new ArgumentException(
                "refused: --theta is required and has no default for soultrack (a comma list, e.g. " +
                "100,150,200,300,400,600 -- doc 16's own range; an unstated sweep would be a design choice nobody made)");
            soulWMillis = Modes.ParseLongList(rest, "--w-milli") ?? throw new ArgumentException(
                "refused: --w-milli is required and has no default (D8's blend weight -- same reasoning as concentration's)");
            soulThetaPerSoulLevelMillis = Modes.ParseLongList(rest, "--theta-per-soul-level-milli") ?? throw new ArgumentException(
                "refused: --theta-per-soul-level-milli is required and has no default (D3's Ws -- unmeasured, spec §6)");
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }

        var soulFmaxMilli = Modes.ParseLong(rest, "--fmax-milli") ?? PassiveTreeTuningHub.Tuning.Concentration.FmaxMilli;
        var soulB = Modes.ParseLong(rest, "--b") ?? TreeModel.DefaultCrossUnlockB;

        var soulSquads = SquadRoster.Squads(shape);
        var soulCorner = soulSquads.Single(s => s.Id == $"mono-{BuildFactory.Roster[0].ToLowerInvariant()}");
        var soulSpread = soulSquads.Single(s => s.Id == "mono-spread");

        var soulResult = SoulTrackSweep.Run(spec, soulCorner, soulSpread, soulThetas, soulWMillis,
            soulThetaPerSoulLevelMillis, soulFmaxMilli, soulB, refineTrials, parallel);
        foreach (var msg in soulResult.CannotSeparateAtTheta300)
            Console.Error.WriteLine($"warning: {msg}");
        var soulJson = SoulTrackSweep.WriteArtifact(soulResult, outPath);
        Console.WriteLine(soulJson);
        return 0;
    }
    case "budget":
    {
        // spec §11 S4 / todo "F6: S4 -- the budget mode, and D42's two dials". --b-list is required, no
        // default (an unstated sweep would be a design choice nobody made, matching --fmax-milli's own
        // convention) -- and this mode reads it as its OWN comma sweep, not the single global --b other
        // modes take. --fmax-milli/--w-milli default to the shipped tuning here (this mode's own axis is
        // the budget-scale 'b' sweep, not Fmax/w).
        var bValues = Modes.ParseLongList(rest, "--b-list");
        if (bValues is null)
        {
            Console.Error.WriteLine("refused: --b-list is required and has no default (e.g. --b-list 5,6 -- a finite difference needs two points)");
            return 2;
        }
        var budgetFmaxMilli = Modes.ParseLong(rest, "--fmax-milli") ?? PassiveTreeTuningHub.Tuning.Concentration.FmaxMilli;
        var budgetWMilli = Modes.ParseLong(rest, "--w-milli") ?? PassiveTreeTuningHub.Tuning.Concentration.WMilli;

        var budgetDuelRoster = SquadRoster.Duels();

        BudgetSweep.Result budgetResult;
        try
        {
            budgetResult = BudgetSweep.Run(spec, budgetDuelRoster, bValues, budgetFmaxMilli, budgetWMilli, refineTrials, parallel);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        var budgetJson = BudgetSweep.WriteArtifact(budgetResult, outPath);
        Console.WriteLine(budgetJson);
        return 0;
    }
    case "verify":
    {
        var scope = Modes.ParseRosterScope(rest);
        var limit = TryGetOption(rest, "--limit", out var limitText) && int.TryParse(limitText, out var limitValue) ? limitValue : (int?)null;
        var runs = Modes.Verify(spec, shape, scope, parallel, limit);
        // F2: `--modes` opts into ALSO enumerating MeasurementModes.All and repeating each one
        // in-process (spec "Verification": "verify enumerates the mode table and covers every mode").
        // Opt-in, not default: the roster-level check above already accepts --roster/--limit for cost
        // control, and the mode table runs the FULL duel/squad/transfer rosters with no limit knob of
        // its own, so folding it into every `verify` call would silently balloon the cost of every
        // existing caller (including this module's own --limit-scoped self-checks).
        var includeModes = rest.Contains("--modes");
        var payload = new
        {
            allocationShape = shape.ToString(),
            runs = runs.Select(r => new { hash = DeterminismHash.Hash(r), r.RosterKind, actorCount = r.ActorIds.Count, pairCount = r.Pairs.Count }),
            modes = includeModes
                ? MeasurementModes.All.Select(m =>
                {
                    var first = m.Run(spec);
                    var second = m.Run(spec);
                    return new { m.Name, hash = first.Hash, repeats = first.Hash == second.Hash };
                })
                : null,
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (outPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, json);
        }
        return 0;
    }
    default:
        Console.Error.WriteLine($"unknown mode '{mode}' (this build ships duel, squad, transfer, erosion, concentration, crossunlock, soultrack, budget, verify)");
        return 2;
}

static bool TryGetOption(IReadOnlyList<string> args, string name, out string value)
{
    for (var i = 0; i < args.Count - 1; i++)
    {
        if (args[i] != name) continue;
        value = args[i + 1];
        return true;
    }
    value = "";
    return false;
}

static long GetLongOption(IReadOnlyList<string> args, string name, long fallback) =>
    TryGetOption(args, name, out var text) && long.TryParse(text, out var value) ? value : fallback;

static void Emit(HarnessRun run, string? outPath, AllocationShape? shape = null)
{
    var payload = new
    {
        hash = DeterminismHash.Hash(run),
        allocationShape = shape?.ToString(),
        run.RosterKind,
        run.ActorIds,
        pairs = run.Pairs,
    };
    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (outPath is null) return;
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllText(outPath, json);
}
