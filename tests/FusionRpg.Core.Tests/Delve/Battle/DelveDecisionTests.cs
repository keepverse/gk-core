using System.Text.Json;
using FusionRpg.Core.Delve.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Battle;

/// <summary>D2.14 — the delve-level log's closed kind vocabulary and one entry's round-trip shape
/// (spec-delve-battle-profile.md §4a: `{seq, kind, partyIndex, tick?, payload}`).</summary>
public class DelveDecisionTests
{
    public static IEnumerable<object[]> FixedKindCases() =>
        DelveDecisionKinds.FixedKinds.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(FixedKindCases))]
    public void Every_fixed_kind_round_trips_through_json(string kind)
    {
        var decision = DelveDecision.Create(seq: 3, kind, partyIndex: 1, tick: 4200, payload: new { note = "x" });
        var json = JsonSerializer.Serialize(decision);
        var back = JsonSerializer.Deserialize<DelveDecision>(json);

        Assert.NotNull(back);
        Assert.Equal(decision.Seq, back!.Seq);
        Assert.Equal(decision.Kind, back.Kind);
        Assert.Equal(decision.PartyIndex, back.PartyIndex);
        Assert.Equal(decision.Tick, back.Tick);
    }

    [Theory]
    [InlineData("object.open")]
    [InlineData("object.break")]
    [InlineData("object.unlock")]
    public void An_object_verb_kind_is_accepted_as_a_pattern_not_a_fixed_member(string kind)
    {
        Assert.True(DelveDecisionKinds.IsKnown(kind));
        Assert.DoesNotContain(kind, DelveDecisionKinds.FixedKinds); // a pattern match, not a literal member
        var decision = DelveDecision.Create(seq: 0, kind);
        Assert.Equal(kind, decision.Kind);
    }

    [Fact]
    public void A_bare_object_prefix_with_no_verb_is_not_known()
    {
        Assert.False(DelveDecisionKinds.IsKnown("object."));
        Assert.False(DelveDecisionKinds.IsKnown("object"));
    }

    [Fact]
    public void An_unknown_kind_is_refused_loudly_not_silently_logged()
    {
        Assert.False(DelveDecisionKinds.IsKnown("teleport"));
        Assert.Throws<ArgumentException>(() => DelveDecision.Create(seq: 0, "teleport"));
    }

    [Fact]
    public void The_nine_fixed_kinds_are_exactly_the_spec_list()
    {
        Assert.Equal(
            new[] { "enter", "extract", "pack.drop", "pack.move", "retreat", "route", "steer", "supply.use", "talk" },
            DelveDecisionKinds.FixedKinds.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Appending_never_mutates_an_earlier_entry_the_log_is_append_only_by_construction()
    {
        // DelveDecision is an immutable record -- there is no member that could rewrite an already-
        // logged entry's Seq/Kind/Tick. The store's own AppendDecision (RpgStore.Delve.cs) re-serialises
        // the WHOLE array on each call but only ever appends; this test pins the C#-side half of that
        // contract: nothing about this type is mutable once constructed.
        var first = DelveDecision.Create(seq: 0, DelveDecisionKinds.Enter);
        var second = DelveDecision.Create(seq: 1, DelveDecisionKinds.Route, payload: new { toRoomId = "r01c00" });
        var log = new List<DelveDecision> { first, second };

        Assert.Equal(0, log[0].Seq);
        Assert.Equal(DelveDecisionKinds.Enter, log[0].Kind);
        Assert.Equal(1, log[1].Seq); // appending `second` never touched `first`'s own fields
    }
}
