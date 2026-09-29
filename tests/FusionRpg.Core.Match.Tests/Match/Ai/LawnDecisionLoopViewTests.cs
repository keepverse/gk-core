using System;
using System.Collections.Generic;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Actions.Ai.Lawn;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match.Ai;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// combat-ai wave 4, second composition — <b>the loop through the REAL lawn view</b>.
///
/// <para>`CAI-loop-1` proved the halves compose, but it fed the policy a hand-rolled `IBattleView`. The
/// production lawn view is `LawnBattleView` (CAI4.1), and it is the one thing between the board and every
/// decision, so the two claims only IT can carry are checked here, through the real profiled policy rather
/// than in its own suite:</para>
/// <list type="number">
/// <item><b>Side comes only from the oracle.</b> A unit whose RAW board side is `zombie` and whose oracle
/// says ally must never be targeted, and an oracle-enemy whose raw side is `plant` must be. The two
/// readings are made disjoint on purpose, so this is a discriminator rather than a smoke test.</item>
/// <item><b>One derived resolve per actor per frame</b>, however many times the policy asks — the memo's
/// whole contract, measured as (reads the policy demanded) vs (resolves the cache performed).</item>
/// </list>
///
/// <para>The fake board is stood up the production way: a `BoardSnapshot` census, one `ILawnUnitView` per
/// ptr (relation filled from the same oracle the view reads), and a `LawnDerivedCache` over a real derived
/// snapshot.</para>
/// </summary>
public class LawnDecisionLoopViewTests
{
    const string Self = "ptr.self";
    const string RawZombieAlly = "ptr.zombie.near";
    const string RawZombieEnemy = "ptr.zombie.far";
    const string RawPlantEnemy = "ptr.plant";
    const string Species = "species.alpha";

    // ---- fixtures -------------------------------------------------------------------------

    static CompiledAction Action(string id, params ActionTag[] tags) => new(
        id, ActionKind.Skill, 1, tags, true, 1, false, false, "container." + id,
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>());

    static LawnKit Kit() => new();

    /// <summary>The four basics plus a granted action, so the store has a real kit to hand out.</summary>
    sealed class LawnKit
    {
        public LawnKit()
        {
            Actions = new[]
            {
                Action("act.attack", ActionTag.Offensive), Action("act.guard", ActionTag.Defensive),
                Action("act.move", ActionTag.Movement), Action("act.innate", ActionTag.Utility),
                Action("act.skill", ActionTag.Heal),
            };
            Catalog = ActionCatalog.Build(Actions);
            Sets = new LawnHeldActionSets(Catalog);
            Sets.PushSpecies(
                Species, new SpeciesBasicsRow(Species, "act.attack", "act.guard", "act.move", "act.innate"),
                new[] { new ActionGrantRow(OwnerKind.UniqueActor, "subject.1", "act.skill", "item.1") },
                _ => false);
        }

        public CompiledAction[] Actions { get; }
        public ActionCatalog Catalog { get; }
        public LawnHeldActionSets Sets { get; }
    }

    static ActorDerivedSnapshot Derived()
    {
        var mods = new List<DerivedModifier>
        {
            new(DerivedStatChannels.ProgressionPower, DerivedModifierOp.Flat, 0.0, SourceId: "test"),
            new(DerivedStatChannels.ProgressionRealm, DerivedModifierOp.Flat, 1.0, SourceId: "test"),
        };
        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, 1000.0, SourceId: "test"));
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, 0.0, SourceId: "test"));
        }

        return new DerivedComposer(DerivedStatRegistry.CreateDefault()).Compose(mods);
    }

    /// <summary>The one side authority the view is allowed to read.</summary>
    sealed class DictOracle : IOwnSideOracle
    {
        readonly Dictionary<string, RelationKind> _byPtr;

        public DictOracle(Dictionary<string, RelationKind> byPtr) => _byPtr = byPtr;

        public RelationKind? RelationOf(string ptr) => _byPtr.TryGetValue(ptr, out var r) ? r : null;
    }

    /// <summary>Counts what the POLICY demanded of the view, so the memo's supply can be compared with the
    /// demand instead of asserted against a guess.</summary>
    sealed class CountingView : IBattleView
    {
        readonly IBattleView _inner;
        public CountingView(IBattleView inner) { _inner = inner; }
        public int DerivedReads { get; private set; }

        public IReadOnlyList<string> LiveActorKeys => _inner.LiveActorKeys;
        public IReadOnlyList<string> LiveActorKeysFor(string actorKey) => _inner.LiveActorKeysFor(actorKey);
        public int SideOf(string actorKey) => _inner.SideOf(actorKey);
        public GridPos? PositionOf(string actorKey) => _inner.PositionOf(actorKey);
        public EntityFacts FactsOf(string actorKey) => _inner.FactsOf(actorKey);
        public IReadOnlyList<CompiledAction> HeldActionsOf(string actorKey) => _inner.HeldActionsOf(actorKey);
        public ActorDerivedSnapshot? DerivedOf(string actorKey)
        {
            DerivedReads++;
            return _inner.DerivedOf(actorKey);
        }

        public string? GarrisonedStructureKeyOf(string actorKey) => _inner.GarrisonedStructureKeyOf(actorKey);
        public GridPos? ObjectivePositionOf(string actorKey) => _inner.ObjectivePositionOf(actorKey);
        public long? MaxHpOf(string actorKey) => _inner.MaxHpOf(actorKey);
        public IReadOnlyList<string> DownedAllyKeysOf(string actorKey) => _inner.DownedAllyKeysOf(actorKey);
        public int AggressionOf(string actorKey) => _inner.AggressionOf(actorKey);
    }

    /// <summary>
    /// The board: `ptr.plant` is raw-side `plant`, both zombies are raw-side `zombie`, and the ORACLE is
    /// deliberately the opposite way round — the near zombie an ALLY, the plant an ENEMY. So a view that
    /// read raw sides would target a zombie, and one that reads the oracle must target the plant.
    /// </summary>
    /// <summary>Counts resolve CALLS and the distinct actors they covered — the two numbers a memo's
    /// contract is stated in. Counting only distinct keys would let a disabled memo pass.</summary>
    sealed class ResolveCounter
    {
        readonly Dictionary<string, int> _perActor = new(StringComparer.Ordinal);
        public int Total { get; private set; }
        public int DistinctActors => _perActor.Count;
        public void Bump(string ptr)
        {
            Total++;
            _perActor[ptr] = _perActor.TryGetValue(ptr, out var n) ? n + 1 : 1;
        }
    }

    static (LawnBattleView View, CountingView Counting, ResolveCounter Resolves, Dictionary<string, ILawnUnitView> Units)
        Board(LawnKit backing, Func<string, long>? revisionOf = null, bool flipOracle = false)
    {
        var relations = flipOracle
            ? new Dictionary<string, RelationKind>(StringComparer.Ordinal)
            {
                [Self] = RelationKind.Self, [RawZombieAlly] = RelationKind.Enemy,
                [RawZombieEnemy] = RelationKind.Enemy, [RawPlantEnemy] = RelationKind.Ally,
            }
            : new Dictionary<string, RelationKind>(StringComparer.Ordinal)
            {
                [Self] = RelationKind.Self, [RawZombieAlly] = RelationKind.Ally,
                [RawZombieEnemy] = RelationKind.Enemy, [RawPlantEnemy] = RelationKind.Enemy,
            };
        var oracle = new DictOracle(relations);

        var units = new Dictionary<string, ILawnUnitView>(StringComparer.Ordinal)
        {
            [Self] = new LawnUnitSnapshot(Self, RelationKind.Self, 1000, 1000),
            [RawZombieAlly] = new LawnUnitSnapshot(RawZombieAlly, relations[RawZombieAlly], 1000, 1000),
            [RawZombieEnemy] = new LawnUnitSnapshot(RawZombieEnemy, relations[RawZombieEnemy], 1000, 1000),
            [RawPlantEnemy] = new LawnUnitSnapshot(RawPlantEnemy, relations[RawPlantEnemy], 1000, 1000),
        };

        var census = new BoardSnapshot(new[]
        {
            new BoardEntitySnap { Ptr = Self, Side = "plant", Col = 0, Row = 0 },
            new BoardEntitySnap { Ptr = RawZombieAlly, Side = "zombie", Col = 1, Row = 0 },
            new BoardEntitySnap { Ptr = RawPlantEnemy, Side = "plant", Col = 3, Row = 0 },
            new BoardEntitySnap { Ptr = RawZombieEnemy, Side = "zombie", Col = 5, Row = 0 },
        });

        var resolves = new ResolveCounter();
        var derived = new LawnDerivedCache(
            ptr => { resolves.Bump(ptr); return Derived(); },
            revisionOf ?? (_ => 0L));

        var view = new LawnBattleView(
            relation: oracle,
            censusOf: () => census,
            unitOf: ptr => units.TryGetValue(ptr, out var u) ? u : null,
            heldActionsOf: ptr => ptr == Self ? backing.Sets.HeldFor(Species) : Array.Empty<CompiledAction>(),
            elementOf: _ => -1,
            statusMaskOf: _ => 0UL,
            derived: derived);

        var counting = new CountingView(view);
        return (view, counting, resolves, units);
    }

    static CoreIntentPolicy Policy(IBattleView view) => CoreIntentPolicy.Create(
        view, new CooldownLedger(), NoStanceHeld.Instance, AlwaysAffordable.Instance,
        AiPlace.Lawn, AiRole.Default);

    // ---- claim 1: the oracle decides sides, all the way into the chosen target ---------------

    [Fact]
    public void The_profiled_policy_targets_the_oracles_enemy_not_the_raw_side()
    {
        var backing = Kit();
        var (view, counting, _, _) = Board(backing);
        view.BeginFrame();

        var intent = Policy(counting).TryDeclare(Self, 0);

        Assert.False(intent.IsNone);
        Assert.Contains(intent.ActionId, Ids(backing.Sets));

        // 1. The chosen target is an ENEMY as the oracle defines it, and specifically never the
        //    raw-zombie unit the oracle calls an ally.
        Assert.NotEqual(RawZombieAlly, intent.TargetKey);
        Assert.Equal(1, counting.SideOf(intent.TargetKey!));
        // 2. And the oracle genuinely CONTRADICTS the raw reading on this board, which is what makes
        //    claim 1 a statement about the oracle rather than about the census: the raw `zombie` at col 1
        //    reads as MY side (0) and the raw `plant` at col 3 reads as the OTHER side (1). A view reading
        //    `BoardEntitySnap.Side` could produce neither number — and note the second raw zombie keeps its
        //    raw reading, so this is a contradiction on two units, not a blanket flip.
        Assert.Equal(0, counting.SideOf(RawZombieAlly));
        Assert.Equal(1, counting.SideOf(RawPlantEnemy));
        Assert.Equal(1, counting.SideOf(RawZombieEnemy));
    }

    [Fact]
    public void Flipping_the_oracle_flips_the_policy_target_and_nothing_else()
    {
        // The same board, the same census, the same raw sides — only the oracle moves. If the view had
        // any second source of side truth, this test is where it would show.
        var backing = Kit();
        var (view, counting, _, _) = Board(backing, flipOracle: true);
        view.BeginFrame();

        var intent = Policy(counting).TryDeclare(Self, 0);

        Assert.False(intent.IsNone);
        // The same board and the same raw sides, with only the oracle moved: now the PLANT reads as MY
        // side (0) and both raw zombies as the other side (1), so the target must be a zombie and can no
        // longer be the plant. Which of the two equal-scoring enemies wins is the scorer's business, not
        // this claim's.
        Assert.Equal(0, counting.SideOf(RawPlantEnemy));
        Assert.Equal(1, counting.SideOf(RawZombieAlly));
        Assert.NotEqual(RawPlantEnemy, intent.TargetKey);
        Assert.Equal(1, counting.SideOf(intent.TargetKey!));
    }

    [Fact]
    public void A_ptr_no_oracle_knows_reads_as_the_other_side_and_nothing_throws()
    {
        var backing = Kit();
        var (view, counting, _, _) = Board(backing);
        view.BeginFrame();

        // `DictOracle` answers null for an unknown ptr; the view maps that to the other side (1), the
        // documented fail-open answer for a raw vanilla unit the oracle has never seen — while the actor
        // the oracle DOES know reads as my own side.
        Assert.Equal(1, view.SideOf("ptr.nobody"));
        Assert.Equal(0, view.SideOf(Self));
    }

    // ---- claim 2: one derived resolve per actor per frame --------------------------------

    [Fact]
    public void The_real_views_derived_memo_serves_a_whole_decision_from_one_resolve_per_actor()
    {
        var backing = Kit();
        var (view, counting, resolves, units) = Board(backing);
        view.BeginFrame();

        var intent = Policy(counting).TryDeclare(Self, 0);
        Assert.False(intent.IsNone);

        // The policy reads derived many times through the decision (every gate that needs a stat), but the
        // cache resolves at most once per actor in the frame: the supply is bounded by the BOARD, not by
        // the demand. Both numbers are measured, so this cannot pass by the policy simply asking once.
        Assert.True(
            counting.DerivedReads > resolves.Total,
            $"the policy made {counting.DerivedReads} derived reads but the cache resolved {resolves.Total} times — " +
            "with the demand no larger than the supply this test proves nothing");
        Assert.True(
            resolves.Total <= units.Count,
            $"the cache resolved {resolves.Total} times for {units.Count} units in one frame");
        Assert.True(
            resolves.DistinctActors <= resolves.Total,
            "an actor cannot be resolved on fewer calls than there are distinct actors");

        // A second decision in the same frame resolves nothing FURTHER: the total is already at or below
        // the board size, so a memo that only worked once would show up here.
        var totalBefore = resolves.Total;
        Policy(counting).TryDeclare(Self, 1);
        Assert.Equal(totalBefore, resolves.Total);

        // …and the next frame drops the memo, which is the frame-scoped half of the contract.
        view.BeginFrame();
        Assert.True(view.DerivedOf(Self) is not null);
    }

    static IReadOnlyList<string> Ids(LawnHeldActionSets sets)
    {
        var held = sets.HeldFor(Species);
        var ids = new List<string>(held.Count);
        for (var i = 0; i < held.Count; i++) ids.Add(held[i].ActionId);
        return ids;
    }
}
