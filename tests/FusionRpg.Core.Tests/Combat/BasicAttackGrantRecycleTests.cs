using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// lawn-combat-wire T10 / L-N17: grants withdraw before a pointer is reused. The injector's leave-board
/// cleanup (<c>GameHooks.ForgetEntity</c> → <c>EffectRuntime.WithdrawEntity</c>) is
/// <see cref="EffectBag.WithdrawForOwner"/> on <c>entity:{ptr}</c>; these tests recycle an address through
/// that exact Core call.
/// </summary>
public class BasicAttackGrantRecycleTests
{
    const string P = "1A2B";

    static EffectBag NewBag()
    {
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectAtomCatalog.CreateAll());
        return new EffectBag(catalog, new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)), new RecordingEffectSink());
    }

    static string OverlayJson(EffectGrant g) => JsonSerializer.Serialize(g.Overlay);
    static string OverlayJson(EffectGrantDto d) => JsonSerializer.Serialize(d.Overlay);

    static IEnumerable<EffectGrant> MatchingDealt(EffectBag bag, string ptr) =>
        bag.ForOwner(null, EffectOwnerKeys.Entity(ptr)).Where(g => EffectOwnerKey.MatchesEvent(g, DealtBy(ptr)));

    static EffectEventDto DealtBy(string ptr) =>
        new() { Trigger = EffectTriggers.OnDamageDealt, ActorPtr = ptr, TargetPtr = "FFFF", Side = "plant", Tick = 1 };

    [Fact]
    public void Forgetting_a_ptr_withdraws_its_grant()
    {
        var bag = NewBag();
        bag.Grant(BasicAttackGrantBuilder.Build(P, ElementTypeId.Fire));

        Assert.Equal(1, bag.WithdrawForOwner(null, EffectOwnerKeys.Entity(P)));
        Assert.Empty(bag.ForOwner(null, EffectOwnerKeys.Entity(P)));
        Assert.Empty(MatchingDealt(bag, P));
    }

    [Fact]
    public void A_new_entity_at_a_recycled_ptr_carries_only_its_own_element()
    {
        var bag = NewBag();
        bag.Grant(BasicAttackGrantBuilder.Build(P, ElementTypeId.Fire));
        bag.WithdrawForOwner(null, EffectOwnerKeys.Entity(P));

        var fresh = BasicAttackGrantBuilder.Build(P, ElementTypeId.Ice);
        bag.Grant(fresh);

        var owned = Assert.Single(bag.ForOwner(null, EffectOwnerKeys.Entity(P)));
        Assert.Equal(OverlayJson(fresh), OverlayJson(owned));
        Assert.NotEqual(OverlayJson(BasicAttackGrantBuilder.Build(P, ElementTypeId.Fire)), OverlayJson(owned));
        Assert.Single(MatchingDealt(bag, P));
    }

    [Fact]
    public void Withdraw_catches_grants_bound_under_a_different_ptr_spelling()
    {
        var bag = NewBag();
        bag.Grant(BasicAttackGrantBuilder.Build(P.ToLowerInvariant(), ElementTypeId.Fire));
        bag.Grant(BasicAttackGrantBuilder.Build(P, ElementTypeId.Fire));

        bag.WithdrawForOwner(null, EffectOwnerKeys.Entity(P));

        Assert.Empty(bag.ForOwner(null, EffectOwnerKeys.Entity(P)));
        Assert.Empty(MatchingDealt(bag, P));
    }

    [Fact]
    public void Two_spellings_of_one_ptr_bind_one_grant_not_two()
    {
        // Two grants for one entity would both match its OnDamageDealt and double the rider delta —
        // the same failure class 29cbb7f3 fixed for attacker/victim matching.
        var bag = NewBag();
        bag.Grant(BasicAttackGrantBuilder.Build("0x" + P.ToLowerInvariant(), ElementTypeId.Fire));
        bag.Grant(BasicAttackGrantBuilder.Build(P, ElementTypeId.Fire));

        Assert.Single(bag.ForOwner(null, EffectOwnerKeys.Entity(P)));
        Assert.Single(MatchingDealt(bag, P));
    }

    [Fact]
    public void Rebinding_the_same_ptr_without_forgetting_is_an_upsert_not_a_duplicate()
    {
        var bag = NewBag();
        bag.Grant(BasicAttackGrantBuilder.Build(P, ElementTypeId.Fire));
        bag.Grant(BasicAttackGrantBuilder.Build(P, ElementTypeId.Fire));

        Assert.Single(bag.ForOwner(null, EffectOwnerKeys.Entity(P)));
    }

    /// <summary>lawn-combat-wire L-N36 (live 2026-09-15): the proc policy's default 250 ms cooldown on OnDamageDealt let only
    /// the first victim of a multi-victim swing ride. Two hits by one attacker at the same instant must both fire.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Two_hits_by_one_attacker_at_the_same_instant_both_fire(bool elemental)
    {
        var bag = NewBag();
        bag.Grant(BasicAttackGrantBuilder.Build(P, elemental ? ElementTypeId.Fire : null));

        var first = bag.OnEvent(new EffectEventDto { Trigger = EffectTriggers.OnDamageDealt, ActorPtr = P, TargetPtr = "AAAA", Side = "zombie", Damage = -20, Tick = 1 });
        var second = bag.OnEvent(new EffectEventDto { Trigger = EffectTriggers.OnDamageDealt, ActorPtr = P, TargetPtr = "BBBB", Side = "zombie", Damage = -20, Tick = 1 });

        Assert.DoesNotContain(first.Skipped, s => s.EndsWith(":icd", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Skipped, s => s.EndsWith(":icd", StringComparison.Ordinal));
        Assert.Equal(first.Skipped, second.Skipped); // the second hit is treated exactly like the first
    }
}
