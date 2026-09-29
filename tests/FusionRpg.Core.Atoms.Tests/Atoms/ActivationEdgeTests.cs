using System.Reflection;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E33 acceptance (spec-activation-edge.md). The activation edge: the seam that turns "this actor
/// decided to act" into an <c>OnActivate</c> atom event, so a bound container's <c>OnActivate</c>
/// atoms fire outside Battle. Ships no producer of its own — <c>A9 movement-actions</c> is the first
/// caller — so this suite is what stands between the seam and D6's exact failure mode (a path with no
/// consumer, accepted and then doing nothing forever) until that module lands.
/// </summary>
public class ActivationEdgeTests
{
    // ---- contract parity ---------------------------------------------------------------------------

    [Fact]
    public void EffectTriggers_OnActivate_is_ordinally_equal_to_AtomTriggers_OnActivate()
    {
        // Guards the exact string EffectBag matches on (case-insensitive compare, but the constants
        // themselves must agree byte-for-byte or a rename on one side silently drifts from the other).
        Assert.Equal(AtomTriggers.OnActivate, EffectTriggers.OnActivate, StringComparer.Ordinal);
    }

    static string[] PublicConstStrings(Type t) =>
        t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

    [Fact]
    public void EffectTriggers_declares_OnActivate_among_its_public_constants()
    {
        // §2.1's own form: "every constant in EffectTriggers, and no others" — asserted against the
        // class's own declared fields, not a literal count, so E34 taking this to 13 needs no edit
        // here (the guardrail this test protects, not merely pins).
        Assert.Contains(EffectTriggers.OnActivate, PublicConstStrings(typeof(EffectTriggers)));
    }

    [Fact]
    public void EffectActions_declares_GrantShield_and_ModifyDerivedStat()
    {
        // §2.1a: the /effects/contract action list was missing both before this module — GrantShield
        // has a live executor, ModifyDerivedStat is declarative-by-design but still published
        // vocabulary. This pins the SOURCE class has them; DebugEndpoints reflects off this same class
        // (server-side, not exercised by Core.Tests, but the reflection means it cannot drift from
        // this list by construction).
        var actions = PublicConstStrings(typeof(EffectActions));
        Assert.Contains(EffectActions.GrantShield, actions);
        Assert.Contains(EffectActions.ModifyDerivedStat, actions);
    }

    // E37 (spec-projectile-control.md §2b.2, criterion 4): /effects/contract's `actions` array is
    // `PublicConstStrings(typeof(EffectActions))` (DebugEndpoints.cs), so declaring the constant here
    // IS the whole of the "grow the published list" obligation — this pins the source has it, the same
    // shape the GrantShield/ModifyDerivedStat test above pins for E33.
    [Fact]
    public void EffectActions_declares_BulletModify()
    {
        Assert.Contains(EffectActions.BulletModify, PublicConstStrings(typeof(EffectActions)));
        Assert.Equal("BulletModify", EffectActions.BulletModify);
    }

    // ---- the capture kind ---------------------------------------------------------------------------

    [Fact]
    public void TryMap_actor_activate_maps_OnActivate_with_actorPtr_set()
    {
        var ev = EffectEventAdapterCore.TryMap(
            "actor.activate",
            new Dictionary<string, object> { ["actorPtr"] = "0xACT", ["side"] = "plant", ["typeId"] = 7 },
            tick: 5,
            matchKey: "m1");

        Assert.NotNull(ev);
        Assert.Equal(EffectTriggers.OnActivate, ev!.Trigger);
        Assert.Equal("0xACT", ev.ActorPtr);
        Assert.Equal("plant", ev.Side);
        Assert.Equal(7, ev.TypeId);
        Assert.Equal("m1", ev.MatchKey);
        Assert.Equal(5, ev.Tick);
    }

    [Fact]
    public void TryMap_actor_activate_with_no_actorPtr_maps_to_null_never_a_board_wide_fanout()
    {
        // The inverse of G5's FindObjectsOfType<Zombie>() hole — a payload naming no actor must never
        // become an event that matches everything.
        var ev = EffectEventAdapterCore.TryMap(
            "actor.activate",
            new Dictionary<string, object> { ["side"] = "plant" },
            tick: 5);

        Assert.Null(ev);
    }

    [Fact]
    public void TryMap_actor_activate_does_not_map_actionId_telemetry_only()
    {
        // The atom layer has no action vocabulary and gains none here — actionId must not surface on
        // any mapped field (there is no field for it to surface on).
        var ev = EffectEventAdapterCore.TryMap(
            "actor.activate",
            new Dictionary<string, object> { ["actorPtr"] = "0xACT", ["side"] = "plant", ["actionId"] = "reposition" },
            tick: 1);

        Assert.NotNull(ev);
        // EffectEventDto simply has no ActionId property — this test's real assertion is that TryMap
        // does not throw or otherwise choke on an unmapped key, and the mapped event is otherwise
        // exactly what §2.2's table names.
        Assert.Equal("0xACT", ev!.ActorPtr);
    }

    // ---- the owner-key arm: plant (a wiring fix — nothing matched before) -----------------------------

    static EffectGrant Grant(string ownerKey) => EffectGrant.FromDto(new EffectGrantDto
    {
        GrantId = "g",
        EffectId = "fx.probe",
        OwnerKey = ownerKey
    });

    [Fact]
    public void Plant_owner_key_matches_OnActivate_on_its_own_side_and_type()
    {
        var grant = Grant("plant:7");
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnActivate, Side = "plant", TypeId = 7, Tick = 1 };

        Assert.True(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    [Fact]
    public void Plant_owner_key_refuses_OnActivate_from_the_zombie_side()
    {
        var grant = Grant("plant:7");
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnActivate, Side = "zombie", TypeId = 7, Tick = 1 };

        Assert.False(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    [Fact]
    public void PLANTED_VIOLATION_reverting_the_plant_OnActivate_clause_would_fail_this_test()
    {
        // This test IS the falsifier §4 asks for: it is the exact assertion that fails if
        // EffectProcAndOwner's plant branch's OnActivate clause is ever reverted back to the bare
        // `return false` — the regression that leaves every type-keyed plant container inert to
        // activation atoms. Recorded here as documentation of what the planted-violation check
        // above (Plant_owner_key_matches_OnActivate_on_its_own_side_and_type) already proves by
        // construction: remove that clause, and that test goes red.
        var grant = Grant("plant:7");
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnActivate, Side = "plant", TypeId = 7, Tick = 1 };
        Assert.True(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    // ---- the owner-key arm: zombie (a narrowing behaviour change on Battle's live path) ---------------

    [Fact]
    public void Zombie_owner_key_matches_OnActivate_on_its_own_side_and_type()
    {
        var grant = Grant("zombie:7");
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnActivate, Side = "zombie", TypeId = 7, Tick = 1 };

        Assert.True(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    [Fact]
    public void Zombie_owner_key_refuses_OnActivate_with_a_missing_side()
    {
        // Today's unnarrowed branch returns TRUE here (only a PRESENT wrong side is refused) — this
        // is the test that pins the behaviour change §2.3 describes.
        var grant = Grant("zombie:7");
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnActivate, Side = null, TypeId = 7, Tick = 1 };

        Assert.False(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    [Fact]
    public void Zombie_owner_key_refuses_a_match_that_came_from_TargetTypeId()
    {
        // The actor's own type, never the target's. Today's unnarrowed branch returns TRUE here.
        var grant = Grant("zombie:7");
        var ev = new EffectEventDto
        {
            Trigger = EffectTriggers.OnActivate, Side = "zombie", TypeId = null, TargetTypeId = 7, Tick = 1
        };

        Assert.False(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    [Fact]
    public void Zombie_owner_key_refuses_Battles_own_OnActivate_shape_before_and_after()
    {
        // Pins that this module moves no Battle behaviour anyone can observe today: Battle's emit
        // (BasicAttack.cs:87-94) carries no Side and no TypeId, so this must stay false regardless of
        // the narrowing — the falsifier that this module's own change is a no-op for shipped content.
        var grant = Grant("zombie:7");
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnActivate, Side = null, TypeId = null, Tick = 1 };

        Assert.False(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    [Fact]
    public void PLANTED_VIOLATION_reverting_the_zombie_OnActivate_clause_would_fail_two_tests()
    {
        // The falsifier for the zombie half specifically: if EffectProcAndOwner's zombie branch's
        // explicit OnActivate clause is ever reverted (falling back through to the unnarrowed
        // TypeId-or-TargetTypeId return), BOTH of these go red — the missing-side one and the
        // TargetTypeId one — because the old fall-through matches both. The plant-branch violation
        // alone never touches this half, which is how §2.3's asymmetry survived the spec's first
        // draft; this test documents that the zombie half needs its OWN falsifier.
        var grant = Grant("zombie:7");

        var missingSide = new EffectEventDto { Trigger = EffectTriggers.OnActivate, Side = null, TypeId = 7, Tick = 1 };
        Assert.False(EffectOwnerKey.MatchesEvent(grant, missingSide));

        var fromTarget = new EffectEventDto
        {
            Trigger = EffectTriggers.OnActivate, Side = "zombie", TypeId = null, TargetTypeId = 7, Tick = 1
        };
        Assert.False(EffectOwnerKey.MatchesEvent(grant, fromTarget));
    }

    [Fact]
    public void PLANTED_VIOLATION_setting_OnActivate_to_a_different_case_would_fail_the_parity_test()
    {
        // The bag's own OrdinalIgnoreCase compare would hide a casing drift at runtime — this is why
        // the parity assertion (EffectTriggers_OnActivate_is_ordinally_equal_to_AtomTriggers_OnActivate)
        // is ordinal, not case-insensitive. Documented here as the third named falsifier §4 lists;
        // proven by that test itself failing if either constant's literal ever changes case.
        Assert.NotEqual("onactivate", AtomTriggers.OnActivate, StringComparer.Ordinal);
        Assert.Equal(AtomTriggers.OnActivate, EffectTriggers.OnActivate, StringComparer.Ordinal);
    }

    // ---- other owner keys need no change (spec's own explicit note) -----------------------------------

    [Fact]
    public void Match_scoped_owner_key_matches_OnActivate_same_as_every_other_trigger()
    {
        var grant = Grant(EffectOwnerKeys.Match);
        var ev = new EffectEventDto { Trigger = EffectTriggers.OnActivate, Side = "plant", TypeId = 7, Tick = 1 };

        Assert.True(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    [Fact]
    public void Entity_owner_key_matches_OnActivate_by_actor_pointer()
    {
        var grant = EffectGrant.FromDto(new EffectGrantDto
        {
            GrantId = "g",
            EffectId = "fx.probe",
            OwnerKey = EffectOwnerKeys.Entity("ACT")
        });
        var ev = new EffectEventDto
        {
            Trigger = EffectTriggers.OnActivate, Side = "plant", ActorPtr = "0xACT", TypeId = 7, Tick = 1
        };

        Assert.True(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    // ---- the fast gate --------------------------------------------------------------------------------
    //
    // EffectRuntime.HasOnActivateGrant() (Injector-side, not reachable from Core.Tests) is a thin
    // wrapper: `Bag.HasGrantWithTrigger(EffectTriggers.OnActivate)`. These prove that underlying
    // mechanism works correctly for this specific trigger — the only half of the gate Core.Tests can
    // reach; the wrapper itself is a one-line delegation reviewed by inspection, matching how every
    // other Injector-only change in this module is verified (build + code review, owner-run live
    // check for the rest).

    [Fact]
    public void HasGrantWithTrigger_OnActivate_is_false_with_no_such_grant()
    {
        var host = new SimEffectHost(seed: 1, matchKey: "m1");

        Assert.False(host.Bag.HasGrantWithTrigger(EffectTriggers.OnActivate));
    }

    [Fact]
    public void HasGrantWithTrigger_OnActivate_is_true_once_an_OnActivate_grant_exists()
    {
        var def = new EffectDef
        {
            EffectId = "fx.activate-probe",
            EffectType = EffectTypes.Triggered,
            Name = "Activate probe",
            Triggers = new List<string> { EffectTriggers.OnActivate },
            Actions = new List<EffectActionRow>
            {
                new() { Seq = 1, Action = EffectActions.ApplyResourceDelta,
                    Params = new Dictionary<string, object?> { ["channel"] = "hp", ["amount"] = -1 } },
            },
        };
        var host = new SimEffectHost(seed: 1, matchKey: "m1", catalog: new[] { def });

        host.Grant(new EffectGrantDto { GrantId = "g1", EffectId = "fx.activate-probe", OwnerKey = EffectOwnerKeys.Match });

        Assert.True(host.Bag.HasGrantWithTrigger(EffectTriggers.OnActivate));
    }
}
