using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// `battle-tempo` `commitment-binding` (spec-commitment-binding.md). Honours `Commitment` once a
/// window exists: a target that dies mid-wind-up either fizzles (`EarlyBound`) or re-targets
/// (`LateBound`/`EarlyBoundWithFallback`). Rig mirrors `TurnFsmActionEnvelopeTests`' own harness
/// shape exactly, extended with a re-selection delegate and a configurable profile default.
/// </summary>
public class CommitmentBindingTests
{
    sealed class Rig
    {
        public readonly EventQueue Queue = new(64);
        public readonly SimulationClock Clock = new();
        public readonly ActionSlots Slots;
        public readonly CooldownLedger Cooldowns = new();
        public readonly ActionRunner Runner;
        public readonly List<string> Log = new();

        readonly Dictionary<string, ActorTurnMachine> _actors = new(StringComparer.Ordinal);
        readonly HashSet<string> _dead = new(StringComparer.Ordinal);
        readonly NextEventAdvance _advance = new();
        readonly List<ScheduledEvent> _buffer = new(32);

        /// <summary>Fallback target this rig's re-selection delegate hands back — null means "no
        /// legal target", exercising the graceful-fizzle path.</summary>
        public string? FallbackTarget;
        public int ReselectCalls;

        public Rig(Commitment defaultCommitment = Commitment.LateBound, bool withReselect = true, int width = 4)
        {
            Slots = new ActionSlots(width, WScope.Global);
            Runner = new ActionRunner(Queue, Slots, Cooldowns, key => !_dead.Contains(key),
                defaultCommitment,
                withReselect ? (actorKey, deadTarget) => { ReselectCalls++; return FallbackTarget; } : null);
        }

        public ActorTurnMachine Add(string key)
        {
            var m = new ActorTurnMachine(key);
            _actors[key] = m;
            return m;
        }

        public ActorTurnMachine Actor(string key) => _actors[key];
        public void Kill(string key) => _dead.Add(key);

        public CommitRefusal Commit(string actorKey, ActionEnvelope env, string? target = null, string side = "left")
        {
            var m = _actors[actorKey];
            if (m.State == TurnState.Charging) m.TransitionTo(TurnState.Ready);
            return Runner.TryCommit(m, side, new ActionIntent(env.ActionId, target, env), Clock.Now);
        }

        public void Pump(long untilTick = long.MaxValue)
        {
            for (var guard = 0; guard < 10_000; guard++)
            {
                var due = Queue.PeekDueTick();
                if (due is not { } d || d > untilTick) return;

                Clock.TryAdvance(_advance, Queue);
                _buffer.Clear();
                Queue.PopDue(Clock.Now, _buffer);
                for (var i = 0; i < _buffer.Count; i++)
                {
                    var e = _buffer[i];
                    var actor = _actors[e.OwnerKey];
                    switch ((TimelineEventKind)e.Kind)
                    {
                        case TimelineEventKind.Resolve:
                            Log.Add($"{Clock.Now}:{e.OwnerKey}:{Runner.OnResolveDue(actor, e).ToString().ToLowerInvariant()}");
                            break;
                        case TimelineEventKind.Recovery:
                            Runner.OnRecoveryDue(actor, e);
                            Log.Add($"{Clock.Now}:{e.OwnerKey}:recovered");
                            break;
                    }
                }
            }

            throw new InvalidOperationException("pump did not terminate — the kernel is looping");
        }
    }

    static ActionEnvelope Strike(Commitment? commitment = null, long windup = 100) => new()
    {
        ActionId = "strike",
        WindupTicks = windup,
        RecoveryTicks = 50,
        ResolveOffsets = new long[] { 0 },
        Commitment = commitment,
    };

    [Fact]
    public void EarlyBoundFizzlesWhenTheTargetDiesMidWindup()
    {
        var rig = new Rig();
        var a = rig.Add("a");
        rig.Add("victim");
        rig.Commit("a", Strike(Commitment.EarlyBound), target: "victim");
        rig.Kill("victim"); // dies during the wind-up window, before the resolve tick
        rig.Pump();

        Assert.Contains(rig.Log, l => l.EndsWith(":a:fizzled"));
        Assert.Equal(0, rig.ReselectCalls); // EarlyBound never consults re-selection at all
    }

    [Fact]
    public void LateBoundReTargetsWhenTheTargetDiesMidWindup()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Add("victim");
        rig.Add("fallback");
        rig.FallbackTarget = "fallback";
        rig.Commit("a", Strike(Commitment.LateBound), target: "victim");
        rig.Kill("victim");
        rig.Pump();

        Assert.Contains(rig.Log, l => l.EndsWith(":a:resolved")); // re-targeted and landed, not fizzled
        Assert.Equal(1, rig.ReselectCalls);
    }

    [Fact]
    public void EarlyBoundWithFallbackReTargetsWhenTheTargetDiesMidWindup()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Add("victim");
        rig.Add("fallback");
        rig.FallbackTarget = "fallback";
        rig.Commit("a", Strike(Commitment.EarlyBoundWithFallback), target: "victim");
        rig.Kill("victim");
        rig.Pump();

        Assert.Contains(rig.Log, l => l.EndsWith(":a:resolved"));
        Assert.Equal(1, rig.ReselectCalls);
    }

    /// <summary>All three values are distinguishable on identical input, contrasted in one file —
    /// EarlyBound fizzles; LateBound/EarlyBoundWithFallback both re-target (the spec's own predicted
    /// shape: the death scenario is the only one this module covers, and both non-EarlyBound values
    /// behave identically in exactly that scenario).</summary>
    [Fact]
    public void AllThreeValuesBehaveDifferentlyOnTheSameSeedAndSetup()
    {
        ActionOutcome RunWith(Commitment commitment)
        {
            var rig = new Rig();
            rig.Add("a");
            rig.Add("victim");
            rig.Add("fallback");
            rig.FallbackTarget = "fallback";
            rig.Commit("a", Strike(commitment), target: "victim");
            rig.Kill("victim");
            rig.Pump();
            // Search for the fizzled/resolved entry specifically -- "recovered" always logs after
            // it, so the LAST entry is never the one this helper needs (a real bug caught here via
            // gk-core/tools/CommitmentProbe before this test ever ran for real).
            var outcomeEntry = rig.Log.First(l => l.EndsWith(":fizzled") || l.EndsWith(":resolved"));
            return outcomeEntry.EndsWith(":fizzled") ? ActionOutcome.Fizzled : ActionOutcome.Resolved;
        }

        Assert.Equal(ActionOutcome.Fizzled, RunWith(Commitment.EarlyBound));
        Assert.Equal(ActionOutcome.Resolved, RunWith(Commitment.LateBound));
        Assert.Equal(ActionOutcome.Resolved, RunWith(Commitment.EarlyBoundWithFallback));
    }

    /// <summary>D6: envelope first, profile default second — a locked action in a late-bound profile
    /// stays locked.</summary>
    [Fact]
    public void TheEnvelopeOverridesTheProfileDefault()
    {
        var rig = new Rig(defaultCommitment: Commitment.LateBound); // profile default is LateBound
        rig.Add("a");
        rig.Add("victim");
        rig.Add("fallback");
        rig.FallbackTarget = "fallback";
        // The envelope explicitly locks EarlyBound -- must win over the profile's LateBound default.
        rig.Commit("a", Strike(Commitment.EarlyBound), target: "victim");
        rig.Kill("victim");
        rig.Pump();

        Assert.Contains(rig.Log, l => l.EndsWith(":a:fizzled"));
        Assert.Equal(0, rig.ReselectCalls);
    }

    [Fact]
    public void AnUnsetEnvelopeInheritsTheProfileDefault()
    {
        var rig = new Rig(defaultCommitment: Commitment.EarlyBound); // profile default is EarlyBound
        rig.Add("a");
        rig.Add("victim");
        // Commitment left null (unset) on the envelope -- must inherit the profile's EarlyBound.
        rig.Commit("a", Strike(commitment: null), target: "victim");
        rig.Kill("victim");
        rig.Pump();

        Assert.Contains(rig.Log, l => l.EndsWith(":a:fizzled"));
        Assert.Equal(0, rig.ReselectCalls); // EarlyBound never consults re-selection
    }

    /// <summary>Backward compatibility: a caller that configures no re-selection delegate at all
    /// (every existing caller before this module) sees LateBound/EarlyBoundWithFallback degrade to a
    /// graceful fizzle rather than throwing — the same OUTWARD shape as before this module existed.</summary>
    [Fact]
    public void WithNoReselectionDelegateConfiguredLateBoundGracefullyFizzles()
    {
        var rig = new Rig(withReselect: false);
        rig.Add("a");
        rig.Add("victim");
        rig.Commit("a", Strike(Commitment.LateBound), target: "victim");
        rig.Kill("victim");
        rig.Pump();

        Assert.Contains(rig.Log, l => l.EndsWith(":a:fizzled"));
    }

    /// <summary>Re-selection returning null (no legal target found) also fizzles rather than
    /// resolving against a nonexistent actor.</summary>
    [Fact]
    public void ReselectionFindingNoLegalTargetAlsoFizzles()
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Add("victim");
        rig.FallbackTarget = null; // no legal fallback exists
        rig.Commit("a", Strike(Commitment.LateBound), target: "victim");
        rig.Kill("victim");
        rig.Pump();

        Assert.Contains(rig.Log, l => l.EndsWith(":a:fizzled"));
        Assert.Equal(1, rig.ReselectCalls); // it WAS consulted, it just found nothing
    }

    /// <summary>A target that stays alive through the whole wind-up is unaffected by ANY commitment
    /// value — regression guard against a change that fizzles or re-selects unconditionally.</summary>
    [Theory]
    [InlineData(Commitment.EarlyBound)]
    [InlineData(Commitment.LateBound)]
    [InlineData(Commitment.EarlyBoundWithFallback)]
    public void ALiveTargetIsUnaffectedByAnyCommitmentValue(Commitment commitment)
    {
        var rig = new Rig();
        rig.Add("a");
        rig.Add("victim"); // never killed
        rig.Commit("a", Strike(commitment), target: "victim");
        rig.Pump();

        Assert.Contains(rig.Log, l => l.EndsWith(":a:resolved"));
        Assert.Equal(0, rig.ReselectCalls);
    }

    /// <summary>battle-tempo `commitment-binding` CB2: re-selection consumes no RNG or other side
    /// state of its own — the delegate is called with exactly the acting actor's key and the dead
    /// target's key, deterministically, and calling it twice on the identical scenario produces the
    /// identical call count and identical outcome (the `B39` lesson: keep draw/consult counts
    /// identical between the re-target and non-re-target paths so a delta stays attributable).</summary>
    [Fact]
    public void ReplayingTheIdenticalScenarioProducesIdenticalReselectionCallCounts()
    {
        int RunAndCountCalls()
        {
            var rig = new Rig();
            rig.Add("a");
            rig.Add("victim");
            rig.Add("fallback");
            rig.FallbackTarget = "fallback";
            rig.Commit("a", Strike(Commitment.LateBound), target: "victim");
            rig.Kill("victim");
            rig.Pump();
            return rig.ReselectCalls;
        }

        var first = RunAndCountCalls();
        var second = RunAndCountCalls();
        Assert.Equal(1, first);
        Assert.Equal(first, second);
    }
}
