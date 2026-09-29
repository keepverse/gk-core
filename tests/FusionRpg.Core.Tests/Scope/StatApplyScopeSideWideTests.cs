using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Scope;

/// <summary>
/// The side-wide owner key (<c>plant:*</c> / <c>zombie:*</c>) — "every actor on one side, any type id",
/// the scope the grammar could not express between <c>match</c> (both sides) and <c>plant:12</c>
/// (one side AND one type).
///
/// <para><b>Why it exists, in one line:</b> the patron aura is plant-side only
/// (spec-patron-creature.md:27) but its grant could only ask for <c>match</c>, so a live probe read the
/// same <c>combat.power.fire</c> magnitude on the ZOMBIE side — the aura buffed the enemy
/// (creature-standalone PT7). The fix is a key, not a second mechanism: this gate stays the one place
/// that answers "does this owner key apply to this resolve context".</para>
///
/// <para>These are CONTRACT assertions — the grammar's closed vocabulary and its side arithmetic.
/// Nothing here counts a content population.</para>
/// </summary>
public class StatApplyScopeSideWideTests
{
    static StatContext Plant(int typeId = 7) =>
        new() { Side = StatSide.Plant, TypeId = typeId, EntityKey = "1A2B" };

    static StatContext Zombie(int typeId = 7) =>
        new() { Side = StatSide.Zombie, TypeId = typeId, EntityKey = "3C4D" };

    // ── spelling ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_side_wide_keys_are_canonical_under_normalize()
    {
        Assert.Equal("plant:*", EffectOwnerKey.PlantSide);
        Assert.Equal("zombie:*", EffectOwnerKey.ZombieSide);

        Assert.Equal("plant:*", StatApplyScope.Normalize(EffectOwnerKey.PlantSide));
        Assert.Equal("plant:*", StatApplyScope.Normalize("  PLANT:* "));
        Assert.Equal("zombie:*", StatApplyScope.Normalize("Zombie:*"));
    }

    // ── Matches: the side arithmetic ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_side_wide_plant_key_matches_every_plant_regardless_of_type_id()
    {
        // Type id is not merely ignored — it is not consulted at all, so an actor whose type the
        // grammar has never seen still matches. That is the whole point of the key.
        Assert.True(StatApplyScope.Matches(EffectOwnerKey.PlantSide, Plant(typeId: 0)));
        Assert.True(StatApplyScope.Matches(EffectOwnerKey.PlantSide, Plant(typeId: 3)));
        Assert.True(StatApplyScope.Matches(EffectOwnerKey.PlantSide, Plant(typeId: 999)));
        Assert.True(StatApplyScope.Matches(EffectOwnerKey.PlantSide, StatSide.Plant, 12, null));
        Assert.True(StatApplyScope.Matches("PLANT:*", StatSide.Plant, 12, "anything"));
    }

    [Fact]
    public void A_side_wide_key_never_matches_the_other_side()
    {
        Assert.False(StatApplyScope.Matches(EffectOwnerKey.PlantSide, Zombie(typeId: 0)));
        Assert.False(StatApplyScope.Matches(EffectOwnerKey.PlantSide, StatSide.Zombie, 12, null));

        Assert.True(StatApplyScope.Matches(EffectOwnerKey.ZombieSide, Zombie(typeId: 5)));
        Assert.False(StatApplyScope.Matches(EffectOwnerKey.ZombieSide, Plant(typeId: 5)));
    }

    [Fact]
    public void Match_still_matches_both_sides()
    {
        // The shipped behaviour every other match-scoped grant depends on. Not narrowed by the new key.
        Assert.True(StatApplyScope.Matches(EffectOwnerKeys.Match, Plant()));
        Assert.True(StatApplyScope.Matches(EffectOwnerKeys.Match, Zombie()));
        Assert.True(StatApplyScope.Matches("", Plant()));
        Assert.True(StatApplyScope.Matches(null, Zombie()));
    }

    [Fact]
    public void The_type_keyed_arms_are_not_widened_by_the_new_key()
    {
        Assert.True(StatApplyScope.Matches("plant:5", Plant(typeId: 5)));
        Assert.False(StatApplyScope.Matches("plant:5", Plant(typeId: 6)));
        Assert.False(StatApplyScope.Matches("plant:5", Zombie(typeId: 5)));
        Assert.True(StatApplyScope.Matches("zombie:5", Zombie(typeId: 5)));
        Assert.False(StatApplyScope.Matches("zombie:5", Plant(typeId: 5)));
    }

    // ── the four functions agree about the new key ───────────────────────────────────────────────

    [Fact]
    public void A_side_wide_key_is_known_grammar_and_deliberately_not_match_wide()
    {
        Assert.True(StatApplyScope.IsKnownOwnerKey(EffectOwnerKey.PlantSide));
        Assert.True(StatApplyScope.IsKnownOwnerKey("  ZOMBIE:*"));
        Assert.True(StatApplyScope.IsSideWideOwnerKey(EffectOwnerKey.PlantSide));
        Assert.True(StatApplyScope.IsSideWideOwnerKey(EffectOwnerKey.ZombieSide));

        // Match-wide means "BOTH sides". A caller that reads IsMatchWide as "no side check needed"
        // must not be told a one-side key covers the other side — that is the PT7 defect.
        Assert.False(StatApplyScope.IsMatchWide(EffectOwnerKey.PlantSide));
        Assert.False(StatApplyScope.IsMatchWide(EffectOwnerKey.ZombieSide));

        // ...while the shipped match-wide answers are untouched.
        Assert.True(StatApplyScope.IsMatchWide("match"));
        Assert.True(StatApplyScope.IsMatchWide("player:1"));
        Assert.False(StatApplyScope.IsMatchWide("plant:5"));

        // The near-misses stay unknown: only the exact `*` widens the key slot.
        Assert.False(StatApplyScope.IsKnownOwnerKey("plant:5*"));
        Assert.False(StatApplyScope.IsKnownOwnerKey("plant:**"));
        Assert.False(StatApplyScope.IsKnownOwnerKey("side:plant"));
    }

    // ── the store lookup widening ────────────────────────────────────────────────────────────────

    [Fact]
    public void OwnerKeyCovers_answers_a_same_side_lookup_and_nothing_else()
    {
        // Every grant reader asks for a CONCRETE actor's key, so this is the widening that makes a
        // side-wide grant reachable at all.
        Assert.True(StatApplyScope.OwnerKeyCovers(EffectOwnerKey.PlantSide, "plant:0"));
        Assert.True(StatApplyScope.OwnerKeyCovers(EffectOwnerKey.PlantSide, "plant:999"));
        Assert.True(StatApplyScope.OwnerKeyCovers(EffectOwnerKey.ZombieSide, "zombie:9"));

        Assert.False(StatApplyScope.OwnerKeyCovers(EffectOwnerKey.PlantSide, "zombie:0"));
        Assert.False(StatApplyScope.OwnerKeyCovers(EffectOwnerKey.PlantSide, "match"));
        Assert.False(StatApplyScope.OwnerKeyCovers(EffectOwnerKey.PlantSide, "entity:1a2b"));
        Assert.False(StatApplyScope.OwnerKeyCovers("plant:5", "plant:6"));

        // Exact equality for the shipped keys — nothing else is widened.
        Assert.True(StatApplyScope.OwnerKeyCovers("match", "match"));
        Assert.True(StatApplyScope.OwnerKeyCovers("plant:5", "plant:5"));
        Assert.True(StatApplyScope.OwnerKeyCovers("entity:0xAB", "entity:ab"));
        Assert.False(StatApplyScope.OwnerKeyCovers("match", "plant:5"));
    }

    // ── the boundary the key deliberately does not cross ─────────────────────────────────────────

    /// <summary>
    /// The TRIGGER gate (<see cref="EffectOwnerKey.MatchesEvent"/>) answers a different question from
    /// the apply scope: which EVENT a triggered grant fires on. It keeps its type-keyed arms only, so a
    /// side-wide key never matches an event — deliberate, and stated in <see cref="StatApplyScope"/>'s
    /// own header: the only producer is a triggerless <c>stat.derived</c> grant whose presence is the
    /// effect, and a triggered side-wide grant would name no single owner entity to attribute a proc to.
    /// Pinned so the day one is authored it fails here loudly rather than going silently inert.
    /// </summary>
    [Fact]
    public void The_trigger_gate_refuses_a_side_wide_key_while_match_and_type_keys_still_fire()
    {
        var dealt = new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            Side = "plant",
            TypeId = 5,
        };

        Assert.True(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.Match), dealt));
        Assert.True(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.PlantType(5)), dealt));
        Assert.False(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKey.PlantSide), dealt));

        var taken = new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageTaken,
            Side = "zombie",
            TypeId = 9,
        };

        Assert.True(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKeys.ZombieType(9)), taken));
        Assert.False(EffectOwnerKey.MatchesEvent(Grant(EffectOwnerKey.ZombieSide), taken));
    }

    static EffectGrant Grant(string ownerKey) => new()
    {
        GrantId = "g-trigger-gate",
        EffectId = "fx.test",
        OwnerKey = ownerKey,
    };

    // ── through the real resolve gate ────────────────────────────────────────────────────────────

    [Fact]
    public void A_side_wide_modifier_resolves_on_plants_of_every_type_and_never_on_a_zombie()
    {
        var sys = new StatSystem();
        var y0 = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 };

        sys.Upsert(sys.Modifiers.Flat("t", "effect", "g-side-wide", StatChannels.Atk, 5,
            applyOwnerKey: EffectOwnerKey.PlantSide));

        Assert.Equal(15, sys.Resolve(sys.Contexts.ForPlant("A", y0, typeId: 0)).Atk);
        Assert.Equal(15, sys.Resolve(sys.Contexts.ForPlant("B", y0, typeId: 42)).Atk);
        Assert.Equal(10, sys.Resolve(sys.Contexts.ForZombie("Z", y0, typeId: 0)).Atk);
        Assert.Equal(10, sys.Resolve(sys.Contexts.ForZombie("Y", y0, typeId: 42)).Atk);
    }
}
