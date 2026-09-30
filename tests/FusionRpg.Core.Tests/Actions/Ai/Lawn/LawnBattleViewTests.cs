using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai.Lawn;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match.Ai;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Actions.Ai.Lawn;

/// <summary>
/// combat-ai `lawn-actor-view` (CAI4.1, spec-lawn-actor-view.md): the adapter over the frozen lawn
/// census. Every member has a stated answer, and the two that would otherwise be guessed — side and the
/// downed read — are asserted in both directions.
/// </summary>
public class LawnBattleViewTests
{
    sealed class FakeUnit : ILawnUnitView
    {
        public string Ptr { get; init; } = "";
        public RelationKind Relation { get; init; } = RelationKind.Enemy;
        public long HpCurrent { get; init; }
        public long HpMax { get; init; }
    }

    sealed class StubOracle : IOwnSideOracle
    {
        readonly Dictionary<string, RelationKind> _answers;
        public int Calls;
        public StubOracle(params (string Ptr, RelationKind Relation)[] answers)
        {
            _answers = new Dictionary<string, RelationKind>(StringComparer.Ordinal);
            foreach (var (ptr, relation) in answers) _answers[ptr] = relation;
        }
        public RelationKind? RelationOf(string ptr)
        {
            Calls++;
            return _answers.TryGetValue(ptr, out var relation) ? relation : null;
        }
    }

    static BoardEntitySnap Snap(string ptr, string rawSide, int row = 2, int col = 3,
        int typeId = 11, bool mindControlled = false) =>
        new() { Ptr = ptr, Side = rawSide, TypeId = typeId, Row = row, Col = col, MindControlled = mindControlled };

    static readonly CompiledAction Usable = new(
        "act.hit", ActionKind.Skill, 1, Array.Empty<ActionTag>(), true, 1, false, false, "container.hit",
        ActionEnvelope.NoOp with { ActionId = "act.hit" },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    static LawnBattleView View(
        IOwnSideOracle oracle, IEnumerable<BoardEntitySnap>? snaps = null,
        IEnumerable<FakeUnit>? units = null, int derivedResolves = 0,
        Func<string, IReadOnlyList<CompiledAction>>? heldActions = null,
        Action? onCensus = null)
    {
        var census = snaps?.ToArray() ?? Array.Empty<BoardEntitySnap>();
        var unitByPtr = (units ?? Array.Empty<FakeUnit>()).ToDictionary(u => u.Ptr, StringComparer.Ordinal);

        return new LawnBattleView(
            oracle,
            censusOf: () => { onCensus?.Invoke(); return new BoardSnapshot(census); },
            unitOf: ptr => unitByPtr.TryGetValue(ptr, out var u) ? u : null,
            heldActionsOf: heldActions ?? (_ => Array.Empty<CompiledAction>()),
            elementOf: _ => 0,
            statusMaskOf: _ => 0UL,
            derived: new LawnDerivedCache(_ => ActorDerivedSnapshot.StubNeutral(), _ => 0L));
    }

    /// <summary>Hypnosis, end to end: the raw board side says `zombie` and the ORACLE says this
    /// perspective owns it, so it reads as MY side. A ptr no oracle knows falls to the other side.</summary>
    [Fact]
    public void Side_comes_only_from_the_oracle_never_from_the_raw_board_side()
    {
        var oracle = new StubOracle(("hypno:1", RelationKind.Ally), ("plant:2", RelationKind.Self));
        var view = View(oracle, new[] { Snap("hypno:1", "zombie"), Snap("plant:2", "plant"), Snap("vanilla:3", "plant") });

        Assert.Equal(LawnBattleView.MySideCode, view.SideOf("hypno:1"));     // raw side would have said 1
        Assert.Equal(LawnBattleView.MySideCode, view.SideOf("plant:2"));
        Assert.Equal(LawnBattleView.OtherSideCode, view.SideOf("vanilla:3")); // unknown -> the other side
    }

    /// <summary>The mutation-killing half: if <c>SideOf</c> were changed to read <c>BoardEntitySnap.Side</c>,
    /// the test above goes red — so the raw-side field must not be READ anywhere in the file either.</summary>
    [Fact]
    public void No_member_reads_the_raw_board_side()
    {
        var text = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "FusionRpg.Core", "Actions", "Ai", "Lawn", "LawnBattleView.cs"));

        foreach (var offender in new[] { "snap.Side", "snap?.Side", "entity.Side", "e.Side" })
            Assert.DoesNotContain(offender, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Garrisoned_and_Objective_are_always_null_and_Position_is_never_null_for_a_live_actor()
    {
        var view = View(new StubOracle(), new[] { Snap("plant:2", "plant", row: 4, col: 7) });

        Assert.Null(view.GarrisonedStructureKeyOf("plant:2"));
        Assert.Null(view.ObjectivePositionOf("plant:2"));

        var pos = view.PositionOf("plant:2");
        Assert.NotNull(pos);
        Assert.Equal(4, pos!.Value.Row);
        Assert.Equal(7, pos.Value.Col);
    }

    /// <summary>A bounded ratio, not a progression cap: 0 when no ratio is defined, 1000 at or above max,
    /// and the floor of the true ratio in between.</summary>
    [Fact]
    public void HpMilli_is_zero_at_zero_max_and_a_thousand_above_max()
    {
        var view = View(new StubOracle(), new[] { Snap("a", "plant"), Snap("b", "plant"), Snap("c", "plant") },
            new[]
            {
                new FakeUnit { Ptr = "a", HpCurrent = 50, HpMax = 0 },     // no ratio defined
                new FakeUnit { Ptr = "b", HpCurrent = 250, HpMax = 100 },  // above max
                new FakeUnit { Ptr = "c", HpCurrent = 250, HpMax = 1000 }, // exactly a quarter
            });

        Assert.Equal(0, view.FactsOf("a").HpMilli);
        Assert.Equal(1000, view.FactsOf("b").HpMilli);
        Assert.Equal(250, view.FactsOf("c").HpMilli);
    }

    /// <summary>The census delegate is invoked only when a decision edge actually asks for something:
    /// constructing the view and turning the frame over must not build the snapshot.</summary>
    [Fact]
    public void The_census_delegate_is_never_invoked_until_a_member_asks()
    {
        var censusCalls = 0;
        var view = View(new StubOracle(), new[] { Snap("plant:2", "plant") }, onCensus: () => censusCalls++);

        Assert.Equal(0, censusCalls);   // constructed
        view.BeginFrame();
        Assert.Equal(0, censusCalls);   // a frame with no decision edge

        _ = view.LiveActorKeys;
        Assert.Equal(1, censusCalls);   // built once...
        _ = view.PositionOf("plant:2");
        _ = view.FactsOf("plant:2");
        Assert.Equal(1, censusCalls);   // ...and reused for the rest of the frame
    }

    /// <summary>N `DerivedOf` reads plus N gate-style reads cost ONE Hub resolve, because the view and
    /// the scorer share the same memo.</summary>
    [Fact]
    public void Derived_and_Aggression_reads_share_one_memoised_resolve()
    {
        var resolves = 0;
        var units = new[] { new FakeUnit { Ptr = "plant:2", HpCurrent = 10, HpMax = 10 } };
        var view = new LawnBattleView(
            new StubOracle(("plant:2", RelationKind.Self)),
            censusOf: () => new BoardSnapshot(new[] { Snap("plant:2", "plant") }),
            unitOf: ptr => units.FirstOrDefault(u => u.Ptr == ptr),
            heldActionsOf: _ => Array.Empty<CompiledAction>(),
            elementOf: _ => 0,
            statusMaskOf: _ => 0UL,
            derived: new LawnDerivedCache(_ => { resolves++; return ActorDerivedSnapshot.StubNeutral(); }, _ => 0L));

        for (var i = 0; i < 8; i++) _ = view.DerivedOf("plant:2");
        for (var i = 0; i < 8; i++) _ = view.AggressionOf("plant:2");

        Assert.Equal(1, resolves);
    }

    /// <summary>Every member is answered, including the two a lawn does not have — and the downed read is
    /// answered BY THIS CLASS rather than inherited from the interface's default-empty member, which is
    /// what the row's "never defaulted" line means.</summary>
    [Fact]
    public void All_members_are_answered_and_the_downed_read_is_not_inherited()
    {
        var view = View(new StubOracle(("plant:2", RelationKind.Self)),
            new[] { Snap("plant:2", "plant") },
            new[] { new FakeUnit { Ptr = "plant:2", HpCurrent = 5, HpMax = 10 } });

        Assert.Equal(new[] { "plant:2" }, view.LiveActorKeys);
        Assert.Equal(new[] { "plant:2" }, view.LiveActorKeysFor("plant:2"));
        Assert.Equal(LawnBattleView.MySideCode, view.SideOf("plant:2"));
        Assert.NotNull(view.PositionOf("plant:2"));
        Assert.Equal(500, view.FactsOf("plant:2").HpMilli);
        Assert.Empty(view.HeldActionsOf("plant:2"));
        Assert.NotNull(view.DerivedOf("plant:2"));       // no fog: never null for a live actor
        Assert.Null(view.GarrisonedStructureKeyOf("plant:2"));
        Assert.Null(view.ObjectivePositionOf("plant:2"));
        Assert.Equal(10, view.MaxHpOf("plant:2"));
        Assert.Equal(0, view.AggressionOf("plant:2"));
        Assert.Empty(view.DownedAllyKeysOf("plant:2"));

        // Answered HERE, not inherited: the declaring type of the implementation is this view.
        var method = typeof(LawnBattleView).GetMethod(nameof(IBattleView.DownedAllyKeysOf))!;
        Assert.Equal(typeof(LawnBattleView), method.DeclaringType);
    }

    /// <summary>The row's last acceptance line: the shipped stub, UNMODIFIED, drives this view and
    /// produces a real intent — i.e. the adapter is a usable <see cref="IBattleView"/>, not just a
    /// collection of getters.</summary>
    [Fact]
    public void StubIntentSource_unmodified_runs_against_this_view()
    {
        var view = View(new StubOracle(("plant:2", RelationKind.Self), ("zombie:9", RelationKind.Enemy)),
            new[] { Snap("plant:2", "plant"), Snap("zombie:9", "zombie") },
            new[]
            {
                new FakeUnit { Ptr = "plant:2", HpCurrent = 10, HpMax = 10 },
                new FakeUnit { Ptr = "zombie:9", HpCurrent = 10, HpMax = 10 },
            },
            heldActions: ptr => ptr == "plant:2" ? new[] { Usable } : Array.Empty<CompiledAction>());

        var source = new StubIntentSource(
            view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance);

        var intent = source.TryDeclare("plant:2", nowTick: 0);

        Assert.False(intent.IsNone);
        Assert.Equal("act.hit", intent.ActionId);
        Assert.Equal("zombie:9", intent.TargetKey);
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }
}
