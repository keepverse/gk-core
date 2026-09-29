using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// Regression for 29cbb7f3: an <c>entity:{ptr}</c> grant matches <c>OnDamageDealt</c> only when the
/// grant owner is the attacker. Before the fix the victim's own grant also matched, doubling every
/// lawn basic-attack delta. Other triggers keep the actor-or-target match.
/// </summary>
public class EntityOwnerKeyDirectionTests
{
    const string Attacker = "1A2B";
    const string Victim = "3C4D";

    static EffectGrant Grant(string ownerKey) => EffectGrant.FromDto(new EffectGrantDto
    {
        GrantId = "g",
        EffectId = "fx.probe",
        OwnerKey = ownerKey
    });

    static EffectEventDto Ev(string trigger) => new()
    {
        Trigger = trigger,
        ActorPtr = Attacker,
        TargetPtr = Victim,
        Tick = 1
    };

    [Fact]
    public void OnDamageDealt_matches_the_attackers_own_entity_grant()
    {
        Assert.True(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.Entity(Attacker)), Ev(EffectTriggers.OnDamageDealt)));
    }

    [Fact]
    public void OnDamageDealt_does_not_match_the_victims_entity_grant()
    {
        Assert.False(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.Entity(Victim)), Ev(EffectTriggers.OnDamageDealt)));
    }

    [Fact]
    public void OnDamageDealt_actor_match_ignores_ptr_spelling_case()
    {
        Assert.True(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.Entity(Attacker.ToLowerInvariant())), Ev(EffectTriggers.OnDamageDealt)));
    }

    [Fact]
    public void OnDeath_still_matches_either_actor_or_target_entity_grant()
    {
        Assert.True(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.Entity(Attacker)), Ev(EffectTriggers.OnDeath)));
        Assert.True(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.Entity(Victim)), Ev(EffectTriggers.OnDeath)));
    }

    [Fact]
    public void Unrelated_entity_grant_matches_neither_direction()
    {
        Assert.False(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.Entity("FFFF")), Ev(EffectTriggers.OnDamageDealt)));
        Assert.False(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.Entity("FFFF")), Ev(EffectTriggers.OnDeath)));
    }
}
