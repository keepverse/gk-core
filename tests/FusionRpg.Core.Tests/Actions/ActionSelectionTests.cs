using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T33/T34 (action-todo.md, spec-action-selection.md): the <c>IBattleView</c> seam and the stub AI.
/// </summary>
public class ActionSelectionTests
{
    sealed class FakeBattleView : IBattleView
    {
        public readonly List<string> Actors = new();
        public readonly Dictionary<string, int> Sides = new(StringComparer.Ordinal);
        public readonly Dictionary<string, GridPos?> Positions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, EntityFacts> Facts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<CompiledAction>> Held = new(StringComparer.Ordinal);

        public void Add(string key, int side, GridPos? pos, params CompiledAction[] held)
        {
            Actors.Add(key);
            Sides[key] = side;
            Positions[key] = pos;
            Facts[key] = new EntityFacts(side, 0, 1000, -1, pos?.Row ?? -1, pos?.Col ?? -1, false, false, 0);
            Held[key] = new List<CompiledAction>(held);
        }

        public IReadOnlyList<string> LiveActorKeys => Actors;
        public int SideOf(string actorKey) => Sides[actorKey];
        public GridPos? PositionOf(string actorKey) => Positions[actorKey];
        public EntityFacts FactsOf(string actorKey) => Facts[actorKey];
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) =>
            Held.TryGetValue(actorKey, out var list) ? list : Array.Empty<CompiledAction>();
        public FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot? DerivedOf(string actorKey) => null;
        public string? GarrisonedStructureKeyOf(string actorKey) => null;
        public GridPos? ObjectivePositionOf(string actorKey) => null;
        public long? MaxHpOf(string actorKey) => null;
        public int AggressionOf(string actorKey) => 0;
    }

    static CompiledAction Action(
        string id, ActionTag[]? tags = null, int minRange = 0, int maxRange = int.MaxValue,
        ICompiledPredicate? condition = null) => new(
        id, ActionKind.Skill, 1, tags ?? Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
        ActionEnvelope.NoOp with { ActionId = id }, new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        minRange, maxRange, null, false, condition ?? PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    static StubIntentSource Stub(FakeBattleView view) =>
        new(view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance);

    // ---- the decision, in order ----------------------------------------------------------------

    [Fact]
    public void NoHeldActionsPassesRatherThanHanging()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null);
        view.Add("wave:1", side: 1, pos: null, Action("act.attack"));

        var intent = Stub(view).TryDeclare("wave:0", nowTick: 0);

        Assert.True(intent.IsNone);
    }

    /// <summary>
    /// The sharpest test in the module (spec's own testing-strategy table): with no live production
    /// caller for <c>IIntentSource</c> yet (confirmed by search — <c>SeatOutcome</c>/<c>SeatResult</c>
    /// are declared but nothing in the kernel drives them, the same "declared-but-unwired seam" shape
    /// as several other gaps this program has already logged), there is no REAL battle loop to run
    /// this against. What IS provable, and what this proves: <c>TryDeclare</c> itself — the contract
    /// any future kernel wiring would call every round for every actor — never blocks and never
    /// throws, and returns <see cref="ActionIntent.None"/> every single time when nothing is legal,
    /// across many simulated rounds and every actor on a board. A real kernel loop consuming
    /// <c>SeatOutcome.Passed</c> for a `None` answer (rather than hanging on it) is that gap's own
    /// fix, not this module's — but THIS module cannot be the reason a future loop would hang.
    /// </summary>
    [Fact]
    public void TryDeclareNeverHangsAcrossManySimulatedRoundsWhenNobodyCanEverDeclare()
    {
        var view = new FakeBattleView();
        // A real board, everyone adjacent (distance 1), but every held action demands range exactly
        // 5 -- permanently unusable -- and nobody holds a movement action. The worst case.
        for (var i = 0; i < 50; i++)
            view.Add($"plant:{i}", side: 0, pos: new GridPos(0, i), Action($"act.unusable{i}", minRange: 5, maxRange: 5));
        for (var i = 0; i < 50; i++)
            view.Add($"zombie:{i}", side: 1, pos: new GridPos(1, i), Action($"act.unusable{i}", minRange: 5, maxRange: 5));

        var stub = Stub(view);
        for (var round = 0; round < 1000; round++)
        {
            for (var i = 0; i < view.Actors.Count; i++)
            {
                var intent = stub.TryDeclare(view.Actors[i], nowTick: round);
                Assert.True(intent.IsNone); // never a stuck clock: always resolves to Passed's own trigger
            }
        }
        // Reaching this line at all IS the proof -- a hang would have timed out the test run instead.
    }

    [Fact]
    public void NoLiveEnemyPassesRatherThanHanging()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, Action("act.attack"));
        view.Add("wave:1", side: 0, pos: null, Action("act.attack")); // same side -- not an enemy

        var intent = Stub(view).TryDeclare("wave:0", nowTick: 0);

        Assert.True(intent.IsNone);
    }

    [Fact]
    public void NothingUsableAndNoMovementActionPassesRatherThanHanging()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: new GridPos(0, 0), Action("act.attack", minRange: 5, maxRange: 5));
        view.Add("wave:1", side: 1, pos: new GridPos(0, 1)); // adjacent -- out of the attack's [5,5] band, no movement held

        var intent = Stub(view).TryDeclare("wave:0", nowTick: 0);

        Assert.True(intent.IsNone);
    }

    [Fact]
    public void AUsableActionProducesAnIntentAgainstTheChosenTarget()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, Action("act.attack"));
        view.Add("wave:1", side: 1, pos: null);

        var intent = Stub(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("act.attack", intent.ActionId);
        Assert.Equal("wave:1", intent.TargetKey);
    }

    [Fact]
    public void OutOfRangeButHoldingAMovementActionProducesAMoveNotAPass()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: new GridPos(0, 0),
            Action("act.attack", minRange: 5, maxRange: 5),
            Action("act.move", tags: new[] { ActionTag.Movement }));
        view.Add("wave:1", side: 1, pos: new GridPos(0, 1)); // too close for the attack

        var intent = Stub(view).TryDeclare("wave:0", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("act.move", intent.ActionId);
        Assert.Equal("wave:1", intent.TargetKey);
    }

    [Fact]
    public void APreferenceKeyChoosesTheHigherRankedActionAheadOfALowerRankedOne()
    {
        // HeldActionsOf is contractually already preference-ordered (StubIntentSource's own doc
        // comment: sorted once wherever the actor's action set is frozen, never per decision, to
        // keep TryDeclare allocation-free) -- so the fixture supplies them in that order directly,
        // and ActionTagPreference.Compare is what a real IBattleView would sort by when freezing it.
        var smite = Action("act.smite", tags: new[] { ActionTag.Offensive });
        var rest = Action("act.rest", tags: new[] { ActionTag.Utility });
        Assert.True(ActionTagPreference.Compare(smite, rest) < 0); // offensive outranks utility, proven directly

        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, smite, rest); // pre-sorted: smite first
        view.Add("wave:1", side: 1, pos: null);

        var intent = Stub(view).TryDeclare("wave:0", nowTick: 0);

        Assert.Equal("act.smite", intent.ActionId);
    }

    [Fact]
    public void ConstructTagRanksBelowEveryOtherTagIncludingUtility()
    {
        // base-defense siege-construction (2026-09-06): placing a structure mid-fight is the least
        // urgent default for the stub AI to prefer -- a real construction order comes from a
        // deliberate caller, never from this fallback preference.
        var build = Action("act.build", tags: new[] { ActionTag.Construct });
        var rest = Action("act.rest", tags: new[] { ActionTag.Utility });
        Assert.True(ActionTagPreference.Compare(rest, build) < 0);
    }

    [Fact]
    public void ConstructTagRoundTripsThroughNameAndTryParse()
    {
        Assert.Equal("construct", ActionTags.Name(ActionTag.Construct));
        Assert.True(ActionTags.TryParse("construct", out var tag));
        Assert.Equal(ActionTag.Construct, tag);
    }

    // ---- who: nearest, ties, and the no-board fallback ---------------------------------------

    [Fact]
    public void NearestByChebyshevDistanceIsChosenOverAFartherEnemy()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: new GridPos(0, 0), Action("act.attack"));
        view.Add("wave:1", side: 1, pos: new GridPos(0, 5)); // distance 5
        view.Add("wave:2", side: 1, pos: new GridPos(0, 1)); // distance 1 -- nearer

        var intent = Stub(view).TryDeclare("wave:0", nowTick: 0);

        Assert.Equal("wave:2", intent.TargetKey);
    }

    [Fact]
    public void EqualDistanceBreaksTiesOnOrdinalPtrCaseInsensitive()
    {
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: new GridPos(0, 0), Action("act.attack"));
        view.Add("wave:B", side: 1, pos: new GridPos(0, 1)); // same distance as wave:A
        view.Add("wave:A", side: 1, pos: new GridPos(1, 0)); // "A" < "B" ordinally

        var intent = Stub(view).TryDeclare("wave:0", nowTick: 0);

        Assert.Equal("wave:A", intent.TargetKey);
    }

    [Fact]
    public void WithNoBoardTheFirstLiveEnemyInListOrderIsChosenSourceOrder()
    {
        // spec §6: with no board, "nearest" is undefined and falls back to SourceOrder -- the exact
        // default BattleEngine.SelectTarget already uses, which is what keeps this module golden-
        // neutral until a real board exists. No production wiring exists yet to compare against
        // SelectTarget end-to-end (IIntentSource has zero production callers today) -- this proves
        // the STUB's own no-board rule matches the documented convention directly.
        var view = new FakeBattleView();
        view.Add("wave:0", side: 0, pos: null, Action("act.attack"));
        view.Add("wave:Z", side: 1, pos: null); // listed first
        view.Add("wave:A", side: 1, pos: null); // ordinally earlier, but listed second -- must NOT win

        var intent = Stub(view).TryDeclare("wave:0", nowTick: 0);

        Assert.Equal("wave:Z", intent.TargetKey);
    }

    [Fact]
    public void TiesAreIdenticalAcrossTwoRunsAndAcrossAShuffledActorsList()
    {
        var view1 = new FakeBattleView();
        view1.Add("wave:0", side: 0, pos: new GridPos(0, 0), Action("act.attack"));
        view1.Add("wave:C", side: 1, pos: new GridPos(0, 2));
        view1.Add("wave:A", side: 1, pos: new GridPos(2, 0));
        view1.Add("wave:B", side: 1, pos: new GridPos(0, 2)); // ties wave:C's distance

        var firstRun = Stub(view1).TryDeclare("wave:0", nowTick: 0);
        var secondRun = Stub(view1).TryDeclare("wave:0", nowTick: 0);
        Assert.Equal(firstRun, secondRun);

        var shuffled = new FakeBattleView();
        shuffled.Add("wave:0", side: 0, pos: new GridPos(0, 0), Action("act.attack"));
        shuffled.Add("wave:B", side: 1, pos: new GridPos(0, 2)); // insertion order reversed vs view1
        shuffled.Add("wave:A", side: 1, pos: new GridPos(2, 0));
        shuffled.Add("wave:C", side: 1, pos: new GridPos(0, 2));

        var shuffledRun = Stub(shuffled).TryDeclare("wave:0", nowTick: 0);
        Assert.Equal(firstRun, shuffledRun); // insertion-order dependence would break this
    }

    // ---- FactReader.Reads scales with targets, not actions x targets --------------------------

    [Fact]
    public void FactReaderReadsIsIndependentOfHowManyOtherEnemiesExistOnTheBoard()
    {
        // A leaf-touching condition so gate 5 genuinely reads facts when it runs.
        var readingCondition = new CountingPredicate();
        CompiledAction Attack(string id) => Action(id, tags: new[] { ActionTag.Offensive }, condition: readingCondition);

        var smallBoard = new FakeBattleView();
        smallBoard.Add("wave:0", side: 0, pos: new GridPos(0, 0), Attack("act.attack"));
        smallBoard.Add("wave:1", side: 1, pos: new GridPos(0, 1));

        readingCondition.Calls = 0;
        Stub(smallBoard).TryDeclare("wave:0", nowTick: 0);
        var callsWithOneEnemy = readingCondition.Calls;

        var largeBoard = new FakeBattleView();
        largeBoard.Add("wave:0", side: 0, pos: new GridPos(0, 0), Attack("act.attack"));
        for (var i = 0; i < 50; i++)
            largeBoard.Add($"wave:e{i}", side: 1, pos: new GridPos(0, 2 + i)); // 50 farther enemies

        readingCondition.Calls = 0;
        Stub(largeBoard).TryDeclare("wave:0", nowTick: 0);
        var callsWithFiftyEnemies = readingCondition.Calls;

        Assert.Equal(callsWithOneEnemy, callsWithFiftyEnemies); // exactly one target is ever evaluated
        Assert.Equal(1, callsWithOneEnemy); // sanity: gate 5 really did run, once
    }

    sealed class CountingPredicate : ICompiledPredicate
    {
        public int Calls;
        public bool Evaluate(ref FactReader facts) { Calls++; facts.Side(Subject.Target); return true; }
    }

    // ---- zero allocation -----------------------------------------------------------------------

    [Fact]
    public void TryDeclareAllocatesZeroBytesAcrossTwoHundredActors()
    {
        var view = new FakeBattleView();
        for (var i = 0; i < 100; i++)
            view.Add($"plant:{i}", side: 0, pos: new GridPos(0, i), Action($"act.attack{i}"));
        for (var i = 0; i < 100; i++)
            view.Add($"zombie:{i}", side: 1, pos: new GridPos(1, i), Action($"act.attack{i}"));

        var stub = Stub(view);
        var keys = view.Actors;

        void RunOneRound()
        {
            for (var i = 0; i < keys.Count; i++)
                stub.TryDeclare(keys[i], nowTick: 0);
        }

        RunOneRound(); // warm

        var before = GC.GetAllocatedBytesForCurrentThread();
        RunOneRound();
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
    }

    // ---- the seam itself: architecture test -----------------------------------------------------

    [Fact]
    public void StubIntentSourceNeverReferencesBattleStateDirectly()
    {
        var source = File.ReadAllText(StubIntentSourcePath());
        // Anything named after live engine internals would be a direct read, bypassing IBattleView --
        // the exact erosion spec §4 warns "the seam erodes on the first convenient shortcut."
        string[] forbidden = { "BattleEngine", "ActorState", "StatusRuntime" };
        foreach (var token in forbidden)
            Assert.DoesNotContain(token, source, StringComparison.Ordinal);
    }

    static string StubIntentSourcePath([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        return Path.Combine(repo, "src", "FusionRpg.Core", "Actions", "StubIntentSource.cs");
    }
}
