using FusionRpg.Core.Balance.Analytic;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Tools.CombatSim;

// class-system-todo.md P4.6: cross-checks FusionRpg.Core.Balance.Analytic.Predictor (the port) against
// gk-core/tools/CombatSim's Analytic.Predict (the reference it ports) on the archetypes CombatSim's own
// predict command resolves -- the exact bootstrap sequence Program.cs's PredictCmd uses (read in full
// this session), so this tool proves the port on the SAME data spec-deterministic-core.md's own
// `--actions basic` etc. commands and docs/research/class-system/_baseline-residual.json were measured
// against, not a hand-picked easier case.

TuningBootstrap.Load(Array.Empty<string>());
Analytic.AssertChannelsRegistered();

var ladder = new PowerLadder(
    PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(TuningBootstrap.RepoRoot, "data", "tuning", "power-scale.v2.json"))));
var tuning = AptitudeTuning.Load("aptitudes.v1");
var model = tuning.ToModel("aptitudes.v1");

var names = new[] { "force", "finesse", "bastion" };
var builds = names.Select(Build.Load).ToList();
const int theta = 100;
var archetypes = builds.Select(bd => bd.At(theta, model, ladder)).ToList();

var maxAbsDiff = 0.0;
var rows = new List<string>();
for (var i = 0; i < archetypes.Count; i++)
for (var j = 0; j < archetypes.Count; j++)
{
    if (i == j) continue;
    var arA = archetypes[i];
    var arB = archetypes[j];

    var reference = Analytic.Predict(arA, arB);

    var actorA = ToActor(arA);
    var actorB = ToActor(arB);
    var ported = Predictor.Predict(actorA, actorB);

    var diff = Math.Abs(reference.WinShareA - ported.WinShareA);
    maxAbsDiff = Math.Max(maxAbsDiff, diff);
    rows.Add($"  {arA.Name,-8}v {arB.Name,-8} reference {reference.WinShareA,20:G17}  ported {ported.WinShareA,20:G17}  diff {diff:E3}");
    if (Environment.GetEnvironmentVariable("PROVE_TRACE") == "1")
    {
        rows.Add($"    ref  rateA {reference.RateAgainstA:G10} rateB {reference.RateAgainstB:G10} varA {reference.VarAgainstA:G10} varB {reference.VarAgainstB:G10} recovA {reference.RecoveryA:G10} recovB {reference.RecoveryB:G10} roundsA {reference.RoundsA:G10} roundsB {reference.RoundsB:G10}");
        rows.Add($"    port rateA {ported.RateAgainstA:G10} rateB {ported.RateAgainstB:G10} varA {ported.VarAgainstA:G10} varB {ported.VarAgainstB:G10} recovA {ported.RecoveryA:G10} recovB {ported.RecoveryB:G10} roundsA {ported.RoundsA:G10} roundsB {ported.RoundsB:G10}");
    }
}

Console.WriteLine("ProvePredictor -- FusionRpg.Core.Balance.Analytic.Predictor vs tools/CombatSim Analytic.Predict");
Console.WriteLine($"  archetypes: {string.Join(", ", names)}   theta: {theta}   model: {model.Name}");
Console.WriteLine();
foreach (var row in rows) Console.WriteLine(row);
Console.WriteLine();
Console.WriteLine($"  MAX ABS DIFF in WinShareA across {rows.Count} arrows: {maxAbsDiff:E3}");

// Not 1e-9: FusionRpg.Core.Balance.Analytic.PhaseModel.ShieldEffectiveHp deliberately takes
// shieldMaxHp as a `long` (spec-deterministic-core.md §5's "every magnitude arrives as long" rule,
// and it matches ShieldRuntime.Apply's own `maxHp = grant.BaseHp + capacity` exact-long computation).
// gk-core/tools/CombatSim's Analytic.ShieldEffectiveHp keeps that value as an unrounded double throughout --
// less faithful to the SHIPPED system than the port is, per spec-deterministic-core.md §2's own
// standard ("calls the shipped resolver's functions"). That one rounding step is the entire source of
// the residual few-1e-7 gap below (verified against Analytic.cs's own field-by-field trace: rateA,
// rateB, varA, varB, recovA and recovB all match to the last printed digit; only the shield-affected
// hpB/roundsB differ, and only by the fraction of an HP point the long-rounding introduces) -- a
// deliberate, more-correct divergence from the POC, not an unexplained one, so the bound here is loose
// enough to accept it while staying four orders of magnitude tighter than any residual band this
// program measures against (1.8%/2.4%, 4.1%/7.7%).
var referenceMatchPass = maxAbsDiff < 1e-4;
Console.WriteLine(referenceMatchPass ? "  PASS -- the port matches the reference within long-rounding tolerance." : "  FAIL -- the port diverges from the reference.");

// ---- Theta-invariance -----------------------------------------------------------------------------
// spec-deterministic-core.md §6 test 2 / §8 item 2: "Win_rate_is_exactly_theta_invariant... identical
// from Θ=10 to Θ=5,000." Predictor.Predict itself has no notion of Θ -- it only sees already-resolved
// channels -- so the property genuinely belongs to the full pipeline: resolve the SAME build at two
// very different Θ through the real AptitudeReadFunctions Contest/Magnitude split, then Predict on
// each, and the win share must come out identical.
Console.WriteLine();
Console.WriteLine("Theta-invariance -- same builds resolved at Theta=10 and Theta=5000");
var thetaMaxDiff = 0.0;
foreach (var lowHigh in new[] { (10, 5000), (100, 2000) })
{
    var (thetaLow, thetaHigh) = lowHigh;
    var lowArchetypes = builds.Select(bd => bd.At(thetaLow, model, ladder)).ToList();
    var highArchetypes = builds.Select(bd => bd.At(thetaHigh, model, ladder)).ToList();
    for (var i = 0; i < lowArchetypes.Count; i++)
    for (var j = 0; j < lowArchetypes.Count; j++)
    {
        if (i == j) continue;
        var lowWin = Predictor.Predict(ToActor(lowArchetypes[i]), ToActor(lowArchetypes[j])).WinShareA;
        var highWin = Predictor.Predict(ToActor(highArchetypes[i]), ToActor(highArchetypes[j])).WinShareA;
        var d = Math.Abs(lowWin - highWin);
        thetaMaxDiff = Math.Max(thetaMaxDiff, d);
        Console.WriteLine($"  Θ={thetaLow,-5} vs Θ={thetaHigh,-5}  {lowArchetypes[i].Name,-8}v {lowArchetypes[j].Name,-8} {lowWin:G17}  vs  {highWin:G17}  diff {d:E3}");
    }
}

// Long-rounded shieldMaxHp (the same source as the reference-match tolerance above) makes this "near
// enough" rather than bit-exact -- a shield's contribution is a small fraction of total HP, and
// rounding it to the nearest whole HP at two very different Θ leaves a tiny, explained residual.
var thetaInvariancePass = thetaMaxDiff < 1e-4;
Console.WriteLine($"  MAX ABS DIFF across Theta pairs: {thetaMaxDiff:E3}");
Console.WriteLine(thetaInvariancePass ? "  PASS -- Theta-invariant within long-rounding tolerance." : "  FAIL -- win share moved with Theta.");

// ---- Actions (no status), then actions + status ----------------------------------------------------
// class-system-todo.md P4.6 acceptance: "all four axes <= 7.7% max" against Analytic.Predict(a, b,
// actions) with Analytic.Status set -- the SAME `--actions basic --status` combination spec-
// deterministic-core.md §3's own command targets. Run BOTH scopes, not just the combined one: isolating
// "actions alone" first is what actually found and diagnosed the gap below, rather than leaving a
// single failing number unexplained.
var actionSet = ActionSet.Load("basic");
var economyOptions = actionSet.Actions
    .Select(x => new ActionSchedule.ActionOption(
        x.Id, x.Priority, x.DamageMultiplier,
        x.Cost?.ResourceId, x.Cost?.ShareOfOutputMilli ?? 0))
    .ToList();
var statusProfile = FusionRpg.Tools.CombatSim.StatusProfile.Default;
var portedStatus = new Predictor.StatusProfile(statusProfile.StatusId, statusProfile.MagnitudeShareOfBase, statusProfile.BaseDurationRounds, statusProfile.GrantChance);

double RunScope(string label, bool withStatus)
{
    Console.WriteLine();
    Console.WriteLine($"{label} -- --actions basic{(withStatus ? " --status" : "")}");
    Analytic.Status = withStatus ? statusProfile : null;
    var maxDiff = 0.0;
    for (var i = 0; i < archetypes.Count; i++)
    for (var j = 0; j < archetypes.Count; j++)
    {
        if (i == j) continue;
        var arA = archetypes[i];
        var arB = archetypes[j];

        var reference = Analytic.Predict(arA, arB, actionSet);
        var economy = new Predictor.ActionEconomy(economyOptions, ToPools(arA), ToPools(arB));
        var ported = Predictor.Predict(ToActor(arA), ToActor(arB), roundLimit: null, economy, withStatus ? portedStatus : null);

        var diff = Math.Abs(reference.WinShareA - ported.WinShareA);
        maxDiff = Math.Max(maxDiff, diff);
        Console.WriteLine($"  {arA.Name,-8}v {arB.Name,-8} reference {reference.WinShareA,20:G17}  ported {ported.WinShareA,20:G17}  diff {diff:E3}");
        if (Environment.GetEnvironmentVariable("PROVE_TRACE") == "1")
        {
            Console.WriteLine($"    ref  rateA {reference.RateAgainstA:G10} rateB {reference.RateAgainstB:G10} varA {reference.VarAgainstA:G10} varB {reference.VarAgainstB:G10} recovA {reference.RecoveryA:G10} recovB {reference.RecoveryB:G10} roundsA {reference.RoundsA:G10} roundsB {reference.RoundsB:G10}");
            Console.WriteLine($"    port rateA {ported.RateAgainstA:G10} rateB {ported.RateAgainstB:G10} varA {ported.VarAgainstA:G10} varB {ported.VarAgainstB:G10} recovA {ported.RecoveryA:G10} recovB {ported.RecoveryB:G10} roundsA {ported.RoundsA:G10} roundsB {ported.RoundsB:G10}");
        }
    }
    Console.WriteLine($"  MAX ABS DIFF in WinShareA across {archetypes.Count * (archetypes.Count - 1)} arrows: {maxDiff:E3}");
    return maxDiff;
}

var actionsOnlyMaxDiff = RunScope("Actions only (no status)", withStatus: false);
var actionsStatusMaxDiff = RunScope("Actions + status", withStatus: true);
Analytic.Status = null;

// Actions-only (no status) is held to the SAME long-rounding-only tolerance as the core path: it is
// PROVEN correct here (10-27 round walks, shields, reflection and recovery all live at once for the
// hardest matchup) with no separate allowance needed.
var actionsOnlyPass = actionsOnlyMaxDiff < 1e-4;

// Actions + status held to the SAME bound: the run that isolated this (actions-only above, matching
// cleanly for even the hardest matchup) is what found and fixed a real bug -- Predictor.Predict had
// dotAtoB/dotBtoA assigned to the wrong side's dealtMean (A's own DoT was landing in B's own-output
// variable and vice versa; class-system-todo.md P4.6 evidence has the full derivation). Fixed at the
// source, not worked around here.
//
// THIS AXIS WAS RED at 9.222E-004 from some point after class-system P4.6 recorded it passing at
// 9.146e-5, until 2026-09-17. It is green now, and the cause is worth keeping because the hunt was
// mostly a list of good hypotheses that measurement killed.
//
// Root cause, in the PORT and internal to it rather than a disagreement with this reference:
// Predictor.Predict carried each side's DoT into dealtMean* -- and so into the damage RATE -- while
// passing the raw swing*.Mean into ShieldEffectiveHp and RecoveryPerRound. The same fight counted a
// DoT as damage when deciding how fast HP fell, and pretended it did not exist when deciding how long
// the shield lasted and how much regen had to out-heal. A DoT tick is incoming damage on all of those
// paths. Fixed by passing dealtMean* to all four sites; actions+status went to 8.836E-007, exactly the
// actions-only figure.
//
// Falsified along the way, recorded so nobody re-walks them: (1) NOT this program's D7 change to the
// reference -- re-running against the pre-08409554 Analytic.cs reproduced 9.222E-004 exactly; (2) NOT
// two resistance models -- both call the same shipped ResistanceEvaluator; (3) NOT different actor
// inputs -- both collapse archetype ranges with the identical (Min+Max)/2.0 midpoint; (4) NOT the
// uptime/DoT/CC composition -- Uptime is 1-(1-p)^d in both; (5) NOT the action multiplier scaling the
// status magnitude -- rateA matched to every printed digit.
//
// What localized it: PROVE_TRACE=1 on FORCE v BASTION showed every term matching except reflectShareA
// (0.700588866432 -> 0.718918527467) and recovA -- both functions of incoming damage, both fed from
// shield/recovery inputs, and the status inflates BASTION's output 13060.93 -> 57491.58, which is why
// that one pair breached the gate while the others sat at 1e-7.
//
// Two traps found on the way, neither the cause, both live: RateAgainstA is damage TAKEN by A
// (Simulator.cs:434), not A's output -- this investigation misread it once; and the two result records
// declare their positional parameters in OPPOSITE order (Analytic.cs:68 vs Predictor.cs:31), so
// constructing either positionally swaps them silently.
//
// KEEP THE 1e-4 BOUND. Do not repoint this reference at the port to make a future diff smaller: the
// reference is the oracle, and sharing code would compare a thing to itself and turn a real gate into
// a vacuous green.
var actionsStatusPass = actionsStatusMaxDiff < 1e-4;
Console.WriteLine();
Console.WriteLine($"Actions-only PASS threshold (1e-4, same as core path): {(actionsOnlyPass ? "PASS" : "FAIL")} (max diff {actionsOnlyMaxDiff:E3})");
Console.WriteLine($"Actions+status PASS threshold (1e-4): {(actionsStatusPass ? "PASS" : "FAIL -- localized to the status COMPOSITION layer in tools/CombatSim Analytic.Predict (both engines' Expected() agree exactly; the reference moves FORCE's output when no status can land); see the comment above this print")} (max diff {actionsStatusMaxDiff:E3})");

var pass = referenceMatchPass && thetaInvariancePass && actionsOnlyPass && actionsStatusPass;
return pass ? 0 : 1;

static Dictionary<string, ActionSchedule.PoolState> ToPools(Archetype a)
{
    var pools = new ActorPools(a);
    var result = new Dictionary<string, ActionSchedule.PoolState>(StringComparer.Ordinal);
    foreach (var id in new[] { "stamina", "qi" })
        result[id] = new ActionSchedule.PoolState(pools.Value(id), pools.Max(id), pools.Regen(id));
    return result;
}

static Predictor.Actor ToActor(Archetype a)
{
    var values = a.Stats.ToDictionary(kv => kv.Key, kv => (kv.Value.Min + kv.Value.Max) / 2.0, StringComparer.Ordinal);
    var derived = ActorDerivedSnapshot.FromValues(values);
    var snapshot = new CombatActorSnapshot(derived, ActorElementTypes.Neutral);

    var hp = (a.Hp.Min + a.Hp.Max) / 2.0;
    var baseDamage = (a.BaseDamage.Min + a.BaseDamage.Max) / 2.0;
    // Mirrors Analytic.ShieldEffectiveHp's own combination exactly: grant baseline + the capacity
    // channel the runtime adds on top (ShieldRuntime.Apply: maxHp = grant.BaseHp + capacity).
    var shieldBase = (a.ShieldHp.Min + a.ShieldHp.Max) / 2.0;
    var shieldCapacity = values.GetValueOrDefault("combat.shield.capacity.omni");
    var shieldMaxHp = (long)Math.Round(shieldBase + shieldCapacity, MidpointRounding.AwayFromZero);

    return new Predictor.Actor(a.Name, snapshot, hp, baseDamage, shieldMaxHp);
}
