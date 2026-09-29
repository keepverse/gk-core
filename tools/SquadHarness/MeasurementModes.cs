using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// One measurement mode's determinism-checkable output. <see cref="Hash"/> is a SHA-256 over
/// <see cref="CanonicalJson"/>, the exact §9.1 idiom (provenance-free content, indented=false) applied
/// uniformly across every mode -- <c>duel</c>/<c>squad</c> hash their own <see cref="HarnessRun"/>
/// exactly as F1 already did (unchanged values), and <c>transfer</c> hashes its own
/// <see cref="TransferReport.TransferResult"/> the same way, so <c>Every_mode_repeats_byte_identically</c>
/// can compare all three uniformly without knowing each mode's own internal shape.
/// </summary>
public sealed record MeasurementResult(string Hash, string CanonicalJson);

/// <summary>
/// spec-squad-harness.md "Code style": "One measurement mode. Adding a mode is adding a row to
/// <see cref="MeasurementModes.All"/> and nothing else: the CLI, the artifact writer and the determinism
/// test all enumerate this table, so a mode that is not here cannot be run, and a mode that IS here is
/// covered by <c>DeterminismTests.Every_mode_repeats_byte_identically</c> without a second edit."
///
/// <para><c>DefaultArtifactPath</c> is <c>null</c> for <c>duel</c> -- Project structure names no
/// checked-in research file for the duel bridge run (it exists to prove the 91-build reproduction, not
/// to publish a standing artifact), matching F1's own behaviour of only writing when <c>--out</c> is
/// given.</para>
/// </summary>
public sealed record MeasurementMode(
    string Name,
    string Question,
    Func<RunSpec, MeasurementResult> Run,
    string? DefaultArtifactPath);

public static class MeasurementModes
{
    /// <summary>Production table: the full 91-build duel roster and full 23-squad roster (§1.2). Built
    /// fresh per access (not <c>static readonly</c>) so a caller configuring <see cref="TuningBootstrap"/>
    /// after this class first loads still gets a roster built under the right tuning.</summary>
    public static IReadOnlyList<MeasurementMode> All =>
        AllWithRosters(SquadRoster.Duels(), SquadRoster.Squads(AllocationShape.PerActor));

    /// <summary>
    /// The same three-mode table, parametrized by roster -- see <see cref="TransferReport"/>'s own doc
    /// on its roster-parametrized <c>Build</c> overload for why: <c>SquadMatch</c>'s per-trial actor-
    /// setup rebuild measures at roughly 20ms/call on this machine, so the full roster costs minutes
    /// even at a single trial. Tests pass a small, REAL subset (one build per class) here; production
    /// code uses <see cref="All"/>, which calls this with the full rosters.
    /// </summary>
    public static IReadOnlyList<MeasurementMode> AllWithRosters(
        IReadOnlyList<NamedBuild> duelBuilds, IReadOnlyList<SquadBuild> squadBuilds) => new[]
    {
        new MeasurementMode(
            "duel",
            "Reproduces tools/HybridViability's 91-build bridge roster, resolved by BattleEngine trials instead of the closed form.",
            spec => HashRun(Sweep.Run("duel", duelBuilds.Select(RosterEntry.From).ToList(), spec, parallel: false)),
            null),

        new MeasurementMode(
            "squad",
            "Does the 1v1 build-class ordering survive at the six-actor scope the game is played at? (D33)",
            spec => HashRun(Sweep.Run("squad", squadBuilds.Select(RosterEntry.From).ToList(), spec, parallel: false)),
            "docs/research/passive-tree/_squad-scope.json"),

        new MeasurementMode(
            "transfer",
            "Does the 1v1 build-class ordering (closed form) survive at 1v1 trial resolution, and at the six-actor scope? (D33, spec §5)",
            spec => HashTransfer(TransferReport.Build(spec, duelBuilds, squadBuilds, AllocationShape.PerActor, refineTrials: null, parallel: false)),
            "docs/research/passive-tree/_scope-transfer.json"),

        // F4 (§4/§6/§11 S2): a small, FIXED default grid -- {1000, 1200} fmaxMilli x {500} wMilli x
        // {ownership cost on/off}, always over the mono-corner vs mono-spread pair. The real CLI mode
        // (Program.cs's own "concentration" case) takes --fmax-milli/--w-milli/--b with NO default --
        // this table entry exists only so Every_mode_repeats_byte_identically covers the mode without
        // paying full-sweep cost, the same "small REAL subset, never a hand-built stand-in" rule
        // TinyClassifiedRoster's own doc states.
        new MeasurementMode(
            "concentration",
            "At squad scope, does any fmaxMilli in the sweep (always including 1000, D5) make corner stop being strictly worse than spread? (spec §6)",
            spec => HashConcentration(TreeModel.ConcentrationSweep(spec,
                Corner(squadBuilds), Spread(squadBuilds),
                fmaxMillis: new long[] { 1000, 1200 }, wMillis: new long[] { 500 },
                b: TreeModel.DefaultCrossUnlockB, refineTrials: null, parallel: false)),
            "docs/research/passive-tree/_concentration-sweep.json"),

        // F4: same fixed-default posture for crossunlock's own axis (the four credit rules).
        new MeasurementMode(
            "crossunlock",
            "At squad scope, does D28's largest-mate credit rule still reverse the corner/spread ordering against none/quarter/full? (spec §6, §11 S2)",
            spec => HashCrossUnlock(TreeModel.CrossUnlockSweep(spec,
                Corner(squadBuilds), Spread(squadBuilds),
                rules: new[] { TreeModel.CreditRule.None, TreeModel.CreditRule.Largest, TreeModel.CreditRule.Quarter, TreeModel.CreditRule.Full },
                fmaxMilli: 1200, wMilli: 500, b: TreeModel.DefaultCrossUnlockB, refineTrials: null, parallel: false)),
            "docs/research/passive-tree/_crossunlock-sweep.json"),

        // erosion/budget (F3/F6) are deliberately NOT rows here -- MeasurementModesTests's own doc
        // comment names this: they stay covered by their own dedicated determinism tests
        // (BudgetSweepTests.Run_repeats_byte_identically_in_two_invocations) instead of this shared
        // table, because both take a required sweep axis with no default (--erosion-milli, --b-list)
        // that a fixed-default table row would have to invent rather than read from a real caller.
    };

    /// <summary>The mono-family "corner" representative -- the same resolved id (<c>BuildFactory.Roster[0]</c>,
    /// "Might") <see cref="Erosion"/> already uses for its own fixed attacker, so a reader sees ONE
    /// consistent "the corner build" across the whole module rather than a different pick per mode.</summary>
    static SquadBuild Corner(IReadOnlyList<SquadBuild> squadBuilds) =>
        squadBuilds.Single(s => s.Id == $"mono-{BuildFactory.Roster[0].ToLowerInvariant()}");

    static SquadBuild Spread(IReadOnlyList<SquadBuild> squadBuilds) =>
        squadBuilds.Single(s => s.Id == "mono-spread");

    static MeasurementResult HashRun(HarnessRun run) =>
        new(DeterminismHash.Hash(run), DeterminismHash.CanonicalJson(run));

    static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };

    static MeasurementResult HashTransfer(TransferReport.TransferResult result)
    {
        // TransferResult carries no provenance field of its own (no `at`/`environmentStamp`) -- exactly
        // HarnessRun's own §9.1 property (DeterminismHash's doc) -- so the whole record is the hash
        // input, no blanking step required.
        var json = JsonSerializer.Serialize(result, CanonicalOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new MeasurementResult(hash, json);
    }

    // Same "no provenance field, whole record is the hash input" shape as HashTransfer -- neither
    // ConcentrationResult nor CrossUnlockResult carries an `at`/timestamp of its own (TreeModel's own
    // artifact writer adds that separately, on the way out, after hashing would matter).
    static MeasurementResult HashConcentration(TreeModel.ConcentrationResult result)
    {
        var json = JsonSerializer.Serialize(result, CanonicalOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new MeasurementResult(hash, json);
    }

    static MeasurementResult HashCrossUnlock(TreeModel.CrossUnlockResult result)
    {
        var json = JsonSerializer.Serialize(result, CanonicalOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new MeasurementResult(hash, json);
    }
}
