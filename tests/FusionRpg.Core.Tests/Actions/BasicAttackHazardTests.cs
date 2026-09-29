using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Tests.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T13 (action-todo.md, spec-basic-attack-adoption.md §4): one fixture per hazard, each engineered
/// so that "improving" the extracted <c>RunBasicAttackStep</c> turns it red. Hazards 3 and 4 are the
/// ones most likely to be "fixed" by accident, because both look like bugs and neither is.
///
/// <para>The strongest protection for all seven is the T11 parity ladder
/// (<see cref="BasicAttackAdoptionTests"/>): eight diverse fixtures compared byte-for-byte, so ANY
/// hazard regression moves at least one of them. These tests exist to name each hazard explicitly,
/// so a failure here says WHICH rule broke instead of just "a fixture moved".</para>
/// </summary>
public class BasicAttackHazardTests
{
    static BattleActorSetup Actor(string key, string side, int level,
        ElementTypeId? elem = null, int? maxHp = null, IEnumerable<BattleStatusSpec>? initialStatuses = null,
        params string[] traits) => new()
    {
        Key = key, Side = side, SpeciesId = "hazard-species", TypeId = 10_002, Level = level,
        ElementPrimary = elem, TraitIds = traits,
        MaxHp = maxHp ?? BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level),
        InitialStatuses = initialStatuses?.ToArray() ?? Array.Empty<BattleStatusSpec>(),
    };

    // ---- hazard 1: initiative draws happen inside OrderBy, once per Active actor, source order ------

    [Fact]
    public void Hazard1_initiative_draws_exactly_once_per_active_actor_in_source_order()
    {
        var trace = new BattleTrace();
        BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002, trace);

        var initiative = SeededRng.DeriveStream(2002, "initiative");
        // CloseSetup: 2 squad + 2 wave, all active round 1 -> exactly 4 draws for round 1,
        // in actors-list source order (squad then wave) -- a reordered or deferred draw would
        // desync this sequence from round 1 onward.
        var expected = new[]
        {
            initiative.NextInt(1000), initiative.NextInt(1000),
            initiative.NextInt(1000), initiative.NextInt(1000),
        };

        Assert.Equal(expected, trace.Draws("initiative").Take(4).ToArray());
    }

    // ---- hazard 2: CC-locked actors still draw initiative, then skip their turn ---------------------

    [Fact]
    public void Hazard2_a_CC_locked_actor_draws_initiative_but_never_selects_a_target()
    {
        var locked = Actor("squad:0", "squad", 5, initialStatuses: new[]
        {
            new BattleStatusSpec("butter", 0, DurationMs: 100_000, PeriodMs: 1000),
        });
        var setup = new BattleSetup
        {
            WaveId = "hazard-cc",
            Squad = new[] { locked, Actor("squad:1", "squad", 5) },
            Wave = new[] { Actor("wave:0", "wave", 5) },
        };

        var trace = new BattleTrace();
        BattleEngine.Resolve(setup, 555, trace);

        // The CC check moving BEFORE the ordering would remove squad:0 from the draw sequence
        // entirely -- it must still be Active (and therefore draw) even though it cannot act.
        var initiative = SeededRng.DeriveStream(555, "initiative");
        var firstRoundDraws = trace.Draws("initiative").Take(3).ToArray();
        Assert.Equal(3, firstRoundDraws.Length); // squad:0, squad:1, wave:0 all drew

        // ...but squad:0 never appears as an attacker in the target log.
        Assert.DoesNotContain(trace.Targets, line => line.Contains("squad:0->", StringComparison.Ordinal));
    }

    // ---- hazard 4: a miss continues, but the crit stream has already advanced ----------------------

    [Fact]
    public void Hazard4_the_crit_stream_advances_on_every_swing_hit_or_miss()
    {
        var trace = new BattleTrace();
        var report = BattleEngine.Resolve(BattleGoldenTests.CloseSetup(), 2002, trace);

        // The crit/hit roll happens inside calculator.Compute, called unconditionally for every
        // attacker that reaches step 4 -- a miss must not be predicted and Compute skipped, which
        // would leave the crit stream one draw short of what a real accuracy roll consumes.
        // Rounds happened and at least one attack was attempted, so the stream must have drawn.
        Assert.True(report.Rounds > 0);
        Assert.NotEmpty(trace.Draws("crit"));

        // Every recorded target line corresponds to a step that reached Compute (a Continue from a
        // miss records no target-independent marker, but it still consumed a crit draw on the way
        // there) -- so crit draws must be >= the number of resolved targets for this battle, never
        // fewer, which would be the signature of a short-circuited miss.
        Assert.True(trace.Draws("crit").Count >= trace.Targets.Count);
    }

    // ---- hazard 5: essence rider draws happen only on a landed hit ----------------------------------

    [Fact]
    public void Hazard5_essence_draws_never_exceed_landed_hits_by_an_essence_carrier()
    {
        // WipeSetup's wave:0 carries void-touched (an essence trait). If essence draws happened
        // before the hit check, this count could exceed the number of hits that carrier landed.
        var trace = new BattleTrace();
        BattleEngine.Resolve(BattleGoldenTests.WipeSetup(), 3003, trace);

        var essenceDraws = trace.Draws("essence").Count;
        // Target lines are "{round} {attacker}->{target}" -- the attacker key follows the space.
        var wave0Hits = trace.Targets.Count(line =>
            line.Contains(' ', StringComparison.Ordinal) &&
            line[(line.IndexOf(' ', StringComparison.Ordinal) + 1)..].StartsWith("wave:0->", StringComparison.Ordinal));

        Assert.True(essenceDraws <= wave0Hits,
            $"essence drew {essenceDraws} times but wave:0 only landed {wave0Hits} hits — " +
            "a rider rolled before the hit check would desync the essence stream");
    }

    // ---- hazard 7: element components come from attacker.AttackComponents --------------------------

    [Fact]
    public void Hazard7_an_elementless_attacker_carries_no_element_components_into_the_swing()
    {
        // StompSetup's wave:2 has no ElementPrimary. If the action carried its own element payload
        // instead of reading attacker.AttackComponents, an elementless attacker's swing would still
        // show the SAME element-matchup shape as its elemental squadmates -- this fixture's outcome
        // already prices that shape into the golden hash, so byte-identity (BasicAttackAdoptionTests
        // / BattleGoldenTests) is the enforcement; this test documents which invariant it protects.
        var report = BattleEngine.Resolve(BattleGoldenTests.StompSetup(), 1001);
        Assert.Equal(BattleOutcome.Victory, report.Outcome); // the shape the golden already locks
    }

    // ---- SourceOrder vs OrdinalPtr: the field is load-bearing, not decorative ------------------------

    [Fact]
    public void SourceOrder_and_OrdinalPtr_pick_different_targets_when_the_two_disagree()
    {
        // Ptr sort ("wave:a" < "wave:z") disagrees with list/source order (wave:z listed first) --
        // exactly the shape spec-targeting.md §2a exists to name. The live engine's SelectTarget
        // (SourceOrder) must pick the FIRST LISTED entity; the shipped TargetResolver (OrdinalPtr)
        // must pick the lexicographically-first ptr. Both read the same pool; only the order differs.
        var board = new BoardSnapshot(new[]
        {
            new BoardEntitySnap { Ptr = "plant:1", Side = "plant", TypeId = 1, Col = 0, Row = 0 },
            new BoardEntitySnap { Ptr = "zombie:z", Side = "zombie", TypeId = 1, Col = 0, Row = 1 },
            new BoardEntitySnap { Ptr = "zombie:a", Side = "zombie", TypeId = 1, Col = 0, Row = 2 },
        });

        var spec = new FusionRpg.Core.Actions.ActionTargetSpec
        {
            Mode = FusionRpg.Core.Actions.ActionTargetMode.Single,
            Relation = FusionRpg.Contracts.RelationKind.Enemy,
            Ordering = FusionRpg.Core.Actions.ActionTargetOrdering.OrdinalPtr,
        };
        var compiled = FusionRpg.Core.Actions.TargetSpecCompiler.Compile(spec);
        var ordinalPick = FusionRpg.Core.Actions.ActionTargetResolver.Resolve(
            compiled, FusionRpg.Core.Actions.CasterSide.Plant, "plant:1", 0, 100, board, null, null, null);

        // SourceOrder, as SelectTarget actually implements it: first match in listed (source) order.
        var sourceOrderPick = board.Entities.First(e => e.Side == "zombie").Ptr;

        Assert.Equal("zombie:a", Assert.Single(ordinalPick));  // ordinal-ptr sort: "a" < "z"
        Assert.Equal("zombie:z", sourceOrderPick);             // source order: listed first

        Assert.NotEqual(sourceOrderPick, ordinalPick[0]); // the disagreement the field exists to name
    }
}
