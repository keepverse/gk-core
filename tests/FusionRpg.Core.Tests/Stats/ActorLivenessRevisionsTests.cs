using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>
/// lawn `LW1.6` (<c>lawn-playable/spec-actor-liveness-refresh.md</c>): the receive half's testable core
/// — one wire invalidation in, one bump or one refusal out, with the recompose LEFT LAZY.
///
/// <para><b>The bound is what these tests are for.</b> An invalidation must not recompose every actor:
/// a player-scoped kind moves the player's counter and the affected actors re-resolve once, on their own
/// next read, and an actor nothing reads costs nothing. That is asserted here by counting whose
/// effective revision moved — never by timing anything.</para>
/// </summary>
[Trait("VerificationId", "core.actor-liveness-revision")]
public class ActorLivenessRevisionsTests
{
    const long Player = 7;
    const long OtherPlayer = 8;
    const string Ptr = "1a2b3c40";
    const string Sibling = "1a2b3c41";

    static LivenessActorKey Key(long playerId, string entityKey) => new(playerId, entityKey);

    [Fact]
    public void A_player_scoped_kind_moves_that_players_actors_and_nothing_else()
    {
        var revisions = new ActorLivenessRevisions();
        var mine = Key(Player, Ptr);
        var theirs = Key(OtherPlayer, Ptr);

        var seenMine = revisions.Read(mine);
        var seenTheirs = revisions.Read(theirs);

        Assert.True(LivenessInvalidationRouter.TryApply(
            revisions, LivenessInvalidationWire.CommanderAllocation, Player, null, out var kind, out var refusal));
        Assert.Equal(LivenessInvalidationKind.CommanderAllocation, kind);
        Assert.Equal(LivenessRefusal.None, refusal);

        // Marked dirty, not recomposed: the revision moved and nothing else happened.
        Assert.True(revisions.IsStale(mine, seenMine));
        Assert.False(revisions.IsStale(theirs, seenTheirs));
        Assert.Equal(1, revisions.PlayerCount);
        Assert.Equal(0, revisions.ActorCount); // a player-scoped bump writes no per-actor row at all
    }

    [Fact]
    public void An_entity_scoped_kind_moves_exactly_that_entity()
    {
        var revisions = new ActorLivenessRevisions();
        var target = Key(Player, Ptr);
        var sibling = Key(Player, Sibling);

        var seenTarget = revisions.Read(target);
        var seenSibling = revisions.Read(sibling);

        Assert.True(LivenessInvalidationRouter.TryApply(
            revisions, LivenessInvalidationWire.UniqueAllocation, Player, Ptr, out _, out _));

        Assert.True(revisions.IsStale(target, seenTarget));
        Assert.False(revisions.IsStale(sibling, seenSibling));
        Assert.Equal(1, revisions.ActorCount);
    }

    [Fact]
    public void Every_kind_has_a_scope_and_the_entity_scoped_ones_are_the_two_that_are_not_match_frozen()
    {
        Assert.Equal(LivenessInvalidationScope.Player, LivenessInvalidationScopes.ScopeOf(LivenessInvalidationKind.Ladder));
        Assert.Equal(LivenessInvalidationScope.Player, LivenessInvalidationScopes.ScopeOf(LivenessInvalidationKind.CommanderAllocation));
        Assert.Equal(LivenessInvalidationScope.Player, LivenessInvalidationScopes.ScopeOf(LivenessInvalidationKind.Tree));
        Assert.Equal(LivenessInvalidationScope.Entity, LivenessInvalidationScopes.ScopeOf(LivenessInvalidationKind.UniqueAllocation));
        Assert.Equal(LivenessInvalidationScope.Entity, LivenessInvalidationScopes.ScopeOf(LivenessInvalidationKind.Equip));
    }

    [Fact]
    public void An_unknown_kind_is_refused_and_reported_never_silently_skipped()
    {
        var revisions = new ActorLivenessRevisions();

        // A kind this build does not know means the server is newer than the injector (rule 5). The
        // refusal is what lets the caller say so instead of quietly ignoring it.
        Assert.False(LivenessInvalidationRouter.TryApply(revisions, "someFutureKind", Player, Ptr, out _, out var refusal));
        Assert.Equal(LivenessRefusal.UnknownKind, refusal);
        Assert.False(LivenessInvalidationRouter.TryApply(revisions, null, Player, Ptr, out _, out refusal));
        Assert.Equal(LivenessRefusal.UnknownKind, refusal);

        // ...and a refused invalidation changed nothing.
        Assert.Equal(0, revisions.ActorCount);
        Assert.Equal(0, revisions.PlayerCount);
    }

    [Fact]
    public void A_missing_identity_is_refused_rather_than_scoped_to_nothing()
    {
        var revisions = new ActorLivenessRevisions();

        Assert.False(LivenessInvalidationRouter.TryApply(
            revisions, LivenessInvalidationWire.Tree, playerId: 0, entityKey: null, out _, out var refusal));
        Assert.Equal(LivenessRefusal.MissingPlayer, refusal);

        Assert.False(LivenessInvalidationRouter.TryApply(
            revisions, LivenessInvalidationWire.Equip, Player, entityKey: null, out _, out refusal));
        Assert.Equal(LivenessRefusal.MissingEntity, refusal);
        Assert.False(LivenessInvalidationRouter.TryApply(
            revisions, LivenessInvalidationWire.Equip, Player, entityKey: "   ", out _, out refusal));
        Assert.Equal(LivenessRefusal.MissingEntity, refusal);

        Assert.Equal(0, revisions.ActorCount);
        Assert.Equal(0, revisions.PlayerCount);
    }

    [Fact]
    public void A_duplicate_invalidation_is_free_and_the_revision_never_drifts_on_a_read()
    {
        var revisions = new ActorLivenessRevisions();
        var key = Key(Player, Ptr);

        LivenessInvalidationRouter.TryApply(revisions, LivenessInvalidationWire.Tree, Player, null, out _, out _);
        LivenessInvalidationRouter.TryApply(revisions, LivenessInvalidationWire.Tree, Player, null, out _, out _);

        // A duplicate costs the same as the first: the consumer sees "stale" and re-resolves once on
        // its next read. Reading is pure — asking twice cannot move the number it compares.
        var seen = revisions.Read(key);
        Assert.True(revisions.IsStale(key, seen - 1));
        Assert.False(revisions.IsStale(key, seen));
        Assert.Equal(seen, revisions.Read(key));
    }

    [Fact]
    public void The_effective_revision_is_the_sum_so_a_large_entity_counter_cannot_mask_a_player_bump()
    {
        var revisions = new ActorLivenessRevisions();
        var key = Key(Player, Ptr);

        for (var i = 0; i < 100; i++) revisions.Bump(key);
        var beforePlayerBump = revisions.Read(key);

        revisions.BumpPlayer(Player);

        // A max() would have returned `beforePlayerBump` here — and a player who levels mid-session
        // would keep reading a frozen Θ, which is the defect this module exists to close.
        Assert.Equal(beforePlayerBump + 1, revisions.Read(key));
    }

    [Fact]
    public void A_reused_pointer_starts_clean()
    {
        var revisions = new ActorLivenessRevisions();
        var key = Key(Player, Ptr);
        revisions.Bump(key);
        Assert.Equal(1, revisions.Read(key));

        // The injector's death edge knows only the pointer, so it drops every player's row for it: the
        // new actor at this address carries no revision of its own.
        Assert.Equal(1, revisions.ForgetActor(Ptr));
        Assert.Equal(0, revisions.ActorCount);
        Assert.False(revisions.IsStale(key, 0));
    }

    [Fact]
    public void Dropping_a_reused_pointer_does_not_drop_the_players_own_unread_counter()
    {
        var revisions = new ActorLivenessRevisions();
        var key = Key(Player, Ptr);
        revisions.Bump(key);
        revisions.BumpPlayer(Player);
        Assert.Equal(2, revisions.Read(key));

        revisions.ForgetActor(Ptr);

        // The entity's own contribution is gone; the player's is not an actor's to drop, so a
        // player-scoped change the injector has not yet acted on stays visible.
        Assert.Equal(1, revisions.Read(key));
        Assert.False(revisions.IsStale(key, 1));
        Assert.True(revisions.IsStale(key, 0));
        Assert.Equal(1, revisions.PlayerCount);

        // ...and the board barrier drops everything, so a new match is a fresh window.
        revisions.Clear();
        Assert.Equal(0, revisions.ActorCount);
        Assert.Equal(0, revisions.PlayerCount);
        Assert.False(revisions.IsStale(key, 0));
    }
}
