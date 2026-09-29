using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// lawn LW1.4 (<c>lawn-playable/spec-actor-liveness-refresh.md</c>): the invalidation vocabulary is a
/// closed vocabulary the code owns, so its membership is pinned here — the one place
/// <c>validation-ssot.md</c> allows a literal — and the revision is a monotonic <c>long</c> counter
/// per <c>(playerId, entityKey)</c>.
/// </summary>
[Trait("VerificationId", "core.actor-liveness-revision")]
public class ActorLivenessRevisionTests
{
    /// <summary>
    /// The pin. Five kinds, in order, with dense ordinals: the kind travels as a small integer on the
    /// wire, so a renumbering would re-point every already-sent notice, and an ADDED kind is a
    /// reviewed change that must move this array in the same commit (that is what makes the
    /// vocabulary closed rather than aspirational).
    ///
    /// <para><c>Player</c> is asserted ABSENT on purpose. It is <c>SP6.6</c>'s own notice on
    /// <c>PUT /api/players/current</c> (hard edge E2): this module extends that transport instead of
    /// declaring a second <c>Player</c>-kind channel, so a member named <c>Player</c> here would be
    /// the duplicate the edge forbids.</para>
    /// </summary>
    [Fact]
    public void The_kind_vocabulary_is_closed_and_pinned()
    {
        var kinds = (LivenessInvalidationKind[])Enum.GetValues(typeof(LivenessInvalidationKind));

        Assert.Equal(
            new[] { "Ladder", "CommanderAllocation", "UniqueAllocation", "Equip", "Tree" },
            kinds.Select(k => k.ToString()).ToArray());
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, kinds.Select(k => (int)k).ToArray());
        Assert.DoesNotContain("Player", kinds.Select(k => k.ToString()));
    }

    [Fact]
    public void A_revision_is_monotonic_per_actor_key_and_the_first_notice_is_distinguishable()
    {
        // Zero means "never invalidated", so the value a consumer reads before any notice and after
        // the first are different — which is what lets "recompose on next read" be a comparison.
        var key = new LivenessActorKey(7, "0x1A2B");
        var before = ActorLivenessRevision.Initial(key);

        Assert.Equal(0, before.Value);
        Assert.Equal(key, before.Key);

        var after = before.Next();
        Assert.Equal(1, after.Value);
        Assert.Equal(key, after.Key);
        Assert.True(after.IsNewerThan(before));
        Assert.False(before.IsNewerThan(after));
        Assert.False(after.IsNewerThan(after));
    }

    [Fact]
    public void Two_actors_counters_are_independent()
    {
        var one = ActorLivenessRevision.Initial(new LivenessActorKey(7, "0x1A2B"));
        var other = ActorLivenessRevision.Initial(new LivenessActorKey(7, "0x1A2B"));

        // Same key, same stream, same values — the counter is a value, so equality is by value.
        Assert.Equal(one, other);

        var bumped = one.Next();
        Assert.NotEqual(other, bumped);

        // A different player or a different entity is a DIFFERENT counter: nothing compares across
        // keys, which is why consumers key their stores on (playerId, entityKey).
        Assert.NotEqual(new LivenessActorKey(8, "0x1A2B"), one.Key);
        Assert.NotEqual(new LivenessActorKey(7, "0x9999"), one.Key);
    }

    [Fact]
    public void Overflow_throws_rather_than_wrapping()
    {
        // A wrapped counter would make a stale snapshot compare as fresh — the exact bug class this
        // type closes — so the bump is checked rather than allowed to wrap.
        var maxed = new ActorLivenessRevision(new LivenessActorKey(7, "0x1A2B"), long.MaxValue);

        Assert.Throws<OverflowException>(() => maxed.Next());
    }

    /// <summary>
    /// The WIRE spelling of each kind, and the refusal of an unknown one (LW1.5's sender and LW1.6's
    /// receiver both go through this). The wire name is a contract that outlives the C# identifier:
    /// pinning it here means renaming an enum member is a deliberate wire change, not a silent one.
    /// </summary>
    [Fact]
    public void Every_kind_has_a_pinned_wire_name_and_an_unknown_one_is_refused()
    {
        foreach (var kind in (LivenessInvalidationKind[])Enum.GetValues(typeof(LivenessInvalidationKind)))
        {
            var wire = LivenessInvalidationWire.NameOf(kind);
            Assert.True(LivenessInvalidationWire.TryParse(wire, out var parsed));
            Assert.Equal(kind, parsed);
        }

        Assert.Equal("commanderAllocation", LivenessInvalidationWire.NameOf(LivenessInvalidationKind.CommanderAllocation));
        Assert.Equal("uniqueAllocation", LivenessInvalidationWire.NameOf(LivenessInvalidationKind.UniqueAllocation));
        Assert.Equal("equip", LivenessInvalidationWire.NameOf(LivenessInvalidationKind.Equip));
        Assert.Equal("tree", LivenessInvalidationWire.NameOf(LivenessInvalidationKind.Tree));
        Assert.Equal("ladder", LivenessInvalidationWire.NameOf(LivenessInvalidationKind.Ladder));

        // A kind this build does not know is a REFUSAL the caller must report (a newer server), never
        // a silent skip — the failure mode `spec-actor-liveness-refresh.md` rule 5 names.
        Assert.False(LivenessInvalidationWire.TryParse("player", out _));
        Assert.False(LivenessInvalidationWire.TryParse(null, out _));
        Assert.False(LivenessInvalidationWire.TryParse("", out _));
    }
}
