using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Attrition;
using FusionRpg.Core.Delve.Events;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Events;

/// <summary>D3.8 (spec-event-deck.md §6, "Facts for an event, built once by the host").</summary>
public class EventFactsTests
{
    static readonly Func<string, int> StatusBit = id => id switch
    {
        "chilled" => 1, "burning" => 2, "wet" => 3, _ => -1,
    };

    static DelveMemberState Member(string id, long hp, bool downed = false, params string[] statusIds) => new(
        InstanceId: id,
        Pools: new Dictionary<string, long> { ["hp"] = hp, ["stamina"] = 500, ["hunger"] = 500, ["spirit"] = 500, ["qi"] = 500, ["poise"] = 500 },
        Statuses: statusIds.Select(s => new BattleStatusSpec(s, 0, 0)).ToArray(),
        Shield: null, NerveStacks: 0, Downed: downed, DownedOnce: downed);

    // ---- BuildSelf: argument validation ----

    [Fact]
    public void BuildSelf_null_or_empty_arguments_throw()
    {
        var members = new[] { Member("a", 500) };
        var maxHp = new Dictionary<string, long> { ["a"] = 1000 };
        Assert.Throws<ArgumentNullException>(() => EventFacts.BuildSelf(null!, maxHp, StatusBit));
        Assert.Throws<ArgumentException>(() => EventFacts.BuildSelf(Array.Empty<DelveMemberState>(), maxHp, StatusBit));
        Assert.Throws<ArgumentNullException>(() => EventFacts.BuildSelf(members, null!, StatusBit));
        Assert.Throws<ArgumentNullException>(() => EventFacts.BuildSelf(members, maxHp, null!));
    }

    [Fact]
    public void BuildSelf_every_member_downed_throws()
    {
        var members = new[] { Member("a", 0, downed: true), Member("b", 0, downed: true) };
        var maxHp = new Dictionary<string, long> { ["a"] = 1000, ["b"] = 1000 };
        var ex = Assert.Throws<InvalidOperationException>(() => EventFacts.BuildSelf(members, maxHp, StatusBit));
        Assert.Contains("downed", ex.Message);
    }

    [Fact]
    public void BuildSelf_missing_max_hp_entry_throws()
    {
        var members = new[] { Member("a", 500) };
        var maxHp = new Dictionary<string, long>();
        Assert.Throws<ArgumentException>(() => EventFacts.BuildSelf(members, maxHp, StatusBit));
    }

    // ---- HpMilli: lowest STANDING member ----

    [Fact]
    public void HpMilli_is_the_lowest_standing_members_own_ratio()
    {
        var members = new[] { Member("a", 800), Member("b", 200) };
        var maxHp = new Dictionary<string, long> { ["a"] = 1000, ["b"] = 1000 };
        var facts = EventFacts.BuildSelf(members, maxHp, StatusBit);
        Assert.Equal(200, facts.HpMilli);
    }

    [Fact]
    public void HpMilli_excludes_a_downed_member_even_if_its_own_ratio_would_be_lower()
    {
        var members = new[] { Member("a", 800), Member("b", 10, downed: true) };
        var maxHp = new Dictionary<string, long> { ["a"] = 1000, ["b"] = 1000 };
        var facts = EventFacts.BuildSelf(members, maxHp, StatusBit);
        Assert.Equal(800, facts.HpMilli); // "b" downed at 10‰ never pulls the party's own reading down
    }

    // ---- DownedCount ----

    [Fact]
    public void DownedCount_counts_exactly_the_downed_members()
    {
        var members = new[] { Member("a", 800), Member("b", 0, downed: true), Member("c", 0, downed: true) };
        var maxHp = new Dictionary<string, long> { ["a"] = 1000, ["b"] = 1000, ["c"] = 1000 };
        var facts = EventFacts.BuildSelf(members, maxHp, StatusBit);
        Assert.Equal(2, facts.DownedCount);
    }

    // ---- StatusMask: union over EVERY member, standing or not ----

    [Fact]
    public void StatusMask_unions_bits_across_every_member_including_downed_ones()
    {
        var members = new[]
        {
            Member("a", 800, downed: false, "chilled"),
            Member("b", 0, downed: true, "burning"),
        };
        var maxHp = new Dictionary<string, long> { ["a"] = 1000, ["b"] = 1000 };
        var facts = EventFacts.BuildSelf(members, maxHp, StatusBit);

        var reader = new FactReader(facts, facts);
        Assert.True(reader.HasStatusBit(FusionRpg.Core.Effects.Atoms.Subject.Self, StatusBit("chilled")));
        Assert.True(reader.HasStatusBit(FusionRpg.Core.Effects.Atoms.Subject.Self, StatusBit("burning")));
        Assert.False(reader.HasStatusBit(FusionRpg.Core.Effects.Atoms.Subject.Self, StatusBit("wet")));
    }

    [Fact]
    public void An_unknown_status_id_contributes_nothing_to_the_mask()
    {
        var members = new[] { Member("a", 800, downed: false, "unrecognized-status") };
        var maxHp = new Dictionary<string, long> { ["a"] = 1000 };
        var facts = EventFacts.BuildSelf(members, maxHp, StatusBit);
        Assert.Equal(0UL, facts.StatusMask);
    }

    // ---- Stock pass-through ----

    [Fact]
    public void Stock_quantities_pass_through_verbatim()
    {
        var members = new[] { Member("a", 800) };
        var maxHp = new Dictionary<string, long> { ["a"] = 1000 };
        var facts = EventFacts.BuildSelf(members, maxHp, StatusBit, stock0Qty: 3, stock1Qty: 0, stock2Qty: 7, stock3Qty: 1);
        Assert.Equal(3, facts.Stock0Qty);
        Assert.Equal(0, facts.Stock1Qty);
        Assert.Equal(7, facts.Stock2Qty);
        Assert.Equal(1, facts.Stock3Qty);
    }

    // ---- BuildTarget ----

    [Fact]
    public void BuildTarget_maps_every_field_verbatim()
    {
        var facts = EventFacts.BuildTarget(elementId: 2, row: 5, col: 6, band: 3, roomKind: 8);
        Assert.Equal(2, facts.ElementId);
        Assert.Equal(5, facts.Row);
        Assert.Equal(6, facts.Col);
        Assert.Equal(3, facts.Band);
        Assert.Equal(8, facts.RoomKind);
        Assert.Equal(1000, facts.HpMilli); // Target is the room, not an actor -- always "full", never read by an event leaf
    }

    // ---- End to end: EventFacts' own output is real, compilable predicate input (D3.7 + D3.8 together) ----

    [Fact]
    public void The_built_facts_evaluate_correctly_against_a_real_compiled_predicate_tree()
    {
        var members = new[]
        {
            Member("a", 300, downed: false, "chilled"), // 300‰, the lowest standing
            Member("b", 900, downed: false),
            Member("c", 0, downed: true),
        };
        var maxHp = new Dictionary<string, long> { ["a"] = 1000, ["b"] = 1000, ["c"] = 1000 };
        var self = EventFacts.BuildSelf(members, maxHp, StatusBit);
        var target = EventFacts.BuildTarget(elementId: 0, row: 1, col: 1, band: 2, roomKind: 5);

        // "party is below half hp AND carrying chilled AND exactly one member down" AND "room is band 2, kind 5"
        var tree = new PredicateNode.And(new PredicateNode[]
        {
            new PredicateNode.Leaf(LeafId.HpBelowMilli, FusionRpg.Core.Effects.Atoms.Subject.Self, Value: 500),
            new PredicateNode.Leaf(LeafId.HasStatus, FusionRpg.Core.Effects.Atoms.Subject.Self, Text: "chilled"),
            new PredicateNode.Leaf(LeafId.PartyDownedCount, FusionRpg.Core.Effects.Atoms.Subject.Self, Value: 1),
            new PredicateNode.Leaf(LeafId.BandIs, FusionRpg.Core.Effects.Atoms.Subject.Target, Value: 2),
            new PredicateNode.Leaf(LeafId.RoomKindIs, FusionRpg.Core.Effects.Atoms.Subject.Target, Value: 5),
        });

        var r = PredicateCompiler.TryCompile(tree, StatusBit, out var compiled);
        Assert.True(r.IsOk);

        var reader = new FactReader(self, target);
        Assert.True(compiled.Evaluate(ref reader));
    }
}
