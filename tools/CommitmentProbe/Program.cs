// battle-tempo commitment-binding (CB1/CB2), executed standalone (Core.Tests blocked). Mirrors
// gk-core/tests/FusionRpg.Core.Tests/Battle/Timeline/CommitmentBindingTests.cs case-for-case.

using FusionRpg.Core.Battle.Timeline;

var failures = 0;
void Check(string name, bool condition)
{
    if (condition) { Console.WriteLine($"PASS  {name}"); return; }
    Console.WriteLine($"FAIL  {name}");
    failures++;
}

ActionEnvelope Strike(Commitment? commitment = null, long windup = 100) => new()
{
    ActionId = "strike", WindupTicks = windup, RecoveryTicks = 50,
    ResolveOffsets = new long[] { 0 }, Commitment = commitment,
};

(List<string> Log, int ReselectCalls) RunScenario(
    Commitment envelopeCommitment, Commitment defaultCommitment = Commitment.LateBound,
    bool withReselect = true, string? fallbackTarget = "fallback", bool killVictim = true)
{
    var queue = new EventQueue(64);
    var clock = new SimulationClock();
    var slots = new ActionSlots(4, WScope.Global);
    var cooldowns = new CooldownLedger();
    var actors = new Dictionary<string, ActorTurnMachine>();
    var dead = new HashSet<string>();
    var advance = new NextEventAdvance();
    var buffer = new List<ScheduledEvent>(32);
    var log = new List<string>();
    var reselectCalls = 0;

    var runner = new ActionRunner(queue, slots, cooldowns, key => !dead.Contains(key),
        defaultCommitment,
        withReselect ? (actorKey, deadTarget) => { reselectCalls++; return fallbackTarget; } : null);

    ActorTurnMachine Add(string key) { var m = new ActorTurnMachine(key); actors[key] = m; return m; }
    Add("a");
    Add("victim");
    if (fallbackTarget != null) Add(fallbackTarget);

    var a = actors["a"];
    a.TransitionTo(TurnState.Ready);
    runner.TryCommit(a, "left", new ActionIntent("strike", "victim", Strike(envelopeCommitment)), clock.Now);
    if (killVictim) dead.Add("victim");

    for (var guard = 0; guard < 10_000; guard++)
    {
        var due = queue.PeekDueTick();
        if (due is null) break;
        clock.TryAdvance(advance, queue);
        buffer.Clear();
        queue.PopDue(clock.Now, buffer);
        foreach (var e in buffer)
        {
            var actor = actors[e.OwnerKey];
            if ((TimelineEventKind)e.Kind == TimelineEventKind.Resolve)
                log.Add($"{e.OwnerKey}:{runner.OnResolveDue(actor, e).ToString().ToLowerInvariant()}");
            else if ((TimelineEventKind)e.Kind == TimelineEventKind.Recovery)
            { runner.OnRecoveryDue(actor, e); log.Add($"{e.OwnerKey}:recovered"); }
        }
    }
    return (log, reselectCalls);
}

{
    var (log, calls) = RunScenario(Commitment.EarlyBound);
    Check("EarlyBoundFizzlesWhenTheTargetDiesMidWindup", log.Contains("a:fizzled") && calls == 0);
}
{
    var (log, calls) = RunScenario(Commitment.LateBound);
    Check("LateBoundReTargetsWhenTheTargetDiesMidWindup", log.Contains("a:resolved") && calls == 1);
}
{
    var (log, calls) = RunScenario(Commitment.EarlyBoundWithFallback);
    Check("EarlyBoundWithFallbackReTargetsWhenTheTargetDiesMidWindup", log.Contains("a:resolved") && calls == 1);
}
{
    // "Resolved" (not fizzled) then "recovered" always follow -- search for the fizzled/resolved
    // entry specifically rather than assuming it is the last log line (recovery comes after it).
    string Outcome(Commitment c) =>
        RunScenario(c).Log.First(l => l.EndsWith("fizzled") || l.EndsWith("resolved")).EndsWith("fizzled")
            ? "fizzled" : "resolved";
    Check("AllThreeValuesBehaveDifferentlyOnTheSameSeedAndSetup",
        Outcome(Commitment.EarlyBound) == "fizzled" &&
        Outcome(Commitment.LateBound) == "resolved" &&
        Outcome(Commitment.EarlyBoundWithFallback) == "resolved");
}
{
    // envelope locks EarlyBound over a LateBound profile default -- envelope must win.
    var (log, calls) = RunScenario(Commitment.EarlyBound, defaultCommitment: Commitment.LateBound);
    Check("TheEnvelopeOverridesTheProfileDefault", log.Contains("a:fizzled") && calls == 0);
}
{
    // Commitment left null -- inherits the profile default (EarlyBound here).
    var queue = new EventQueue(64);
    var clock = new SimulationClock();
    var slots = new ActionSlots(4, WScope.Global);
    var cooldowns = new CooldownLedger();
    var actors = new Dictionary<string, ActorTurnMachine>();
    var dead = new HashSet<string>();
    var advance = new NextEventAdvance();
    var buffer = new List<ScheduledEvent>(32);
    var log = new List<string>();
    var calls = 0;
    var runner = new ActionRunner(queue, slots, cooldowns, key => !dead.Contains(key),
        Commitment.EarlyBound, (ak, dt) => { calls++; return "fallback"; });
    ActorTurnMachine Add(string key) { var m = new ActorTurnMachine(key); actors[key] = m; return m; }
    Add("a"); Add("victim"); Add("fallback");
    actors["a"].TransitionTo(TurnState.Ready);
    runner.TryCommit(actors["a"], "left", new ActionIntent("strike", "victim", Strike(null)), clock.Now);
    dead.Add("victim");
    for (var guard = 0; guard < 10_000; guard++)
    {
        var due = queue.PeekDueTick();
        if (due is null) break;
        clock.TryAdvance(advance, queue);
        buffer.Clear();
        queue.PopDue(clock.Now, buffer);
        foreach (var e in buffer)
            if ((TimelineEventKind)e.Kind == TimelineEventKind.Resolve)
                log.Add($"{e.OwnerKey}:{runner.OnResolveDue(actors[e.OwnerKey], e).ToString().ToLowerInvariant()}");
    }
    Check("AnUnsetEnvelopeInheritsTheProfileDefault", log.Contains("a:fizzled") && calls == 0);
}
{
    var (log, _) = RunScenario(Commitment.LateBound, withReselect: false);
    Check("WithNoReselectionDelegateConfiguredLateBoundGracefullyFizzles", log.Contains("a:fizzled"));
}
{
    var (log, calls) = RunScenario(Commitment.LateBound, fallbackTarget: null);
    Check("ReselectionFindingNoLegalTargetAlsoFizzles", log.Contains("a:fizzled") && calls == 1);
}
foreach (var c in new[] { Commitment.EarlyBound, Commitment.LateBound, Commitment.EarlyBoundWithFallback })
{
    var (log, calls) = RunScenario(c, killVictim: false);
    Check($"ALiveTargetIsUnaffectedByAnyCommitmentValue({c})", log.Contains("a:resolved") && calls == 0);
}
{
    var (_, first) = RunScenario(Commitment.LateBound);
    var (_, second) = RunScenario(Commitment.LateBound);
    Check("ReplayingTheIdenticalScenarioProducesIdenticalReselectionCallCounts", first == 1 && first == second);
}

Console.WriteLine();
Console.WriteLine(failures == 0 ? "ALL PROBES PASSED" : $"{failures} PROBE(S) FAILED");
Environment.Exit(failures == 0 ? 0 : 1);
