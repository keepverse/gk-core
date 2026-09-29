using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Plugins;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests;

public class EffectOfflineKitTests
{
    [Fact]
    public void ForOwner_filters_by_key_and_kind()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "a",
            EffectId = "fx.butter_on_hit",
            OwnerKind = "match",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "b",
            EffectId = "fx.freeze_on_hit",
            OwnerKind = "plant",
            OwnerKey = EffectOwnerKeys.PlantType(0),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });

        Assert.Single(h.Bag.ForOwner(null, EffectOwnerKeys.Match));
        Assert.Single(h.Bag.ForOwner("plant", EffectOwnerKeys.PlantType(0)));
        Assert.Empty(h.Bag.ForOwner("zombie", EffectOwnerKeys.PlantType(0)));
    }

    [Fact]
    public void ForOwner_matches_entity_key_with_0x_normalize()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "e",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Entity("0xAAA")
        });
        Assert.Single(h.Bag.ForOwner(null, EffectOwnerKeys.Entity("AAA")));
        Assert.Single(h.Bag.ForOwner(null, EffectOwnerKeys.Entity("aaa")));
    }

    [Fact]
    public void WithdrawForOwner_removes_entity_grants_only()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "aaa",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Entity("AAA")
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "bbb",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Entity("BBB")
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "m",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });

        var r0 = h.Snapshot().Revision;
        Assert.Equal(1, h.Bag.WithdrawForOwner(null, EffectOwnerKeys.Entity("AAA")));
        Assert.True(h.Snapshot().Revision > r0);
        Assert.DoesNotContain(h.Snapshot().Grants, g => g.GrantId == "aaa");
        Assert.Contains(h.Snapshot().Grants, g => g.GrantId == "bbb");
        Assert.Contains(h.Snapshot().Grants, g => g.GrantId == "m");
        Assert.Contains(h.Sink.Items, i =>
            i.Action == EffectActions.ModifyStat && JsonOverlay.GetBool(i.Params, "remove"));
    }

    [Fact]
    public void WithdrawForOwner_removes_all_grants_on_same_entity_leaves_plant_and_match()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "e1",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Entity("DEAD")
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "e2",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Entity("0xDEAD"),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "plant",
            EffectId = "fx.freeze_on_hit",
            OwnerKind = "plant",
            OwnerKey = EffectOwnerKeys.PlantType(0),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "m",
            EffectId = "fx.cold_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });

        Assert.Equal(2, h.Bag.WithdrawForOwner(null, EffectOwnerKeys.Entity("DEAD")));
        Assert.DoesNotContain(h.Snapshot().Grants, g => g.GrantId is "e1" or "e2");
        Assert.Contains(h.Snapshot().Grants, g => g.GrantId == "plant");
        Assert.Contains(h.Snapshot().Grants, g => g.GrantId == "m");
    }

    [Fact]
    public void WithdrawForOwner_respects_ownerKind_filter()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "keep",
            EffectId = "fx.passive_atk_flat",
            OwnerKind = "specimen",
            OwnerKey = EffectOwnerKeys.Entity("AAA")
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "drop",
            EffectId = "fx.butter_on_hit",
            OwnerKind = "entity",
            OwnerKey = EffectOwnerKeys.Entity("AAA"),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });

        Assert.Equal(1, h.Bag.WithdrawForOwner("entity", EffectOwnerKeys.Entity("AAA")));
        Assert.Contains(h.Snapshot().Grants, g => g.GrantId == "keep");
        Assert.DoesNotContain(h.Snapshot().Grants, g => g.GrantId == "drop");
    }

    [Fact]
    public void WithdrawForOwner_matches_entity_key_ignore_case_and_0x()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "a",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Entity("aaa")
        });
        Assert.Equal(1, h.Bag.WithdrawForOwner(null, EffectOwnerKeys.Entity("AAA")));
        Assert.Empty(h.Snapshot().Grants);

        h.Grant(new EffectGrantDto
        {
            GrantId = "b",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Entity("0xBBB")
        });
        Assert.Equal(1, h.Bag.WithdrawForOwner(null, EffectOwnerKeys.Entity("BBB")));
        Assert.Empty(h.Snapshot().Grants);
    }

    [Fact]
    public void WithdrawForOwner_null_or_empty_returns_zero()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "m",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Match
        });
        Assert.Equal(0, h.Bag.WithdrawForOwner(null, null));
        Assert.Equal(0, h.Bag.WithdrawForOwner(null, "  "));
        Assert.Single(h.Snapshot().Grants);
    }

    [Fact]
    public void MatchesEvent_entity_key_normalizes_0x_ptr()
    {
        var grant = EffectGrant.FromDto(new EffectGrantDto
        {
            GrantId = "g",
            EffectId = "fx.spawn_zombie_ondeath",
            OwnerKey = EffectOwnerKeys.Entity("AAA")
        });
        var ev = new EffectEventDto
        {
            Trigger = EffectTriggers.OnDeath,
            Side = "zombie",
            ActorPtr = "0xAAA",
            TargetPtr = "0xAAA",
            TypeId = 0,
            MatchKey = "m1",
            Tick = 1
        };
        Assert.True(EffectOwnerKey.MatchesEvent(grant, ev));
    }

    [Fact]
    public void MatchesEvent_instance_and_unknown_owner_never_match()
    {
        var instanceGrant = EffectGrant.FromDto(new EffectGrantDto
        {
            GrantId = "gi",
            EffectId = "fx.butter_on_hit",
            OwnerKey = "instance:guid-1"
        });
        var unknownGrant = EffectGrant.FromDto(new EffectGrantDto
        {
            GrantId = "gu",
            EffectId = "fx.butter_on_hit",
            OwnerKey = "foo:bar"
        });
        var dealt = new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            Side = "plant",
            ActorPtr = "0xA",
            TargetPtr = "0xB",
            TypeId = 0,
            MatchKey = "m1",
            Tick = 1
        };
        var death = new EffectEventDto
        {
            Trigger = EffectTriggers.OnDeath,
            Side = "zombie",
            ActorPtr = "guid-1",
            TargetPtr = "guid-1",
            TypeId = 0,
            MatchKey = "m1",
            Tick = 2
        };
        Assert.False(EffectOwnerKey.MatchesEvent(instanceGrant, dealt));
        Assert.False(EffectOwnerKey.MatchesEvent(instanceGrant, death));
        Assert.False(EffectOwnerKey.MatchesEvent(unknownGrant, dealt));
    }

    [Fact]
    public void Grant_rejects_instance_ownerKey()
    {
        var h = new FoundationHarness();
        var ex = Assert.Throws<InvalidOperationException>(() => h.Grant(new EffectGrantDto
        {
            GrantId = "bad",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = "instance:guid-1"
        }));
        Assert.Contains("instance:", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(h.Snapshot().Grants);

        var exCase = Assert.Throws<InvalidOperationException>(() => h.Grant(new EffectGrantDto
        {
            GrantId = "bad2",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = "INSTANCE:GUID"
        }));
        Assert.Contains("instance:", exCase.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchesEvent_player_owner_still_matches()
    {
        var grant = EffectGrant.FromDto(new EffectGrantDto
        {
            GrantId = "gp",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Player(1),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var dealt = new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            Side = "plant",
            ActorPtr = "0xA",
            TargetPtr = "0xB",
            TypeId = 0,
            MatchKey = "m1",
            Tick = 1
        };
        Assert.True(EffectOwnerKey.MatchesEvent(grant, dealt));
    }

    [Fact]
    public void OnEvent_ignores_store_bypassed_instance_grant()
    {
        var h = new FoundationHarness();
        // Bypass EffectBag.Grant ingress — residual store path must still fail-closed on events.
        h.Bag.Grants.Upsert(EffectGrant.FromDto(new EffectGrantDto
        {
            GrantId = "forced-instance",
            EffectId = "fx.butter_on_hit",
            OwnerKey = "instance:guid-1",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        }));
        Assert.Contains(h.Snapshot().Grants, g => g.GrantId == "forced-instance");
        Assert.Empty(h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            Side = "plant",
            ActorPtr = "0xA",
            TargetPtr = "0xB",
            TypeId = 0,
            MatchKey = "m1",
            Tick = 1
        }).Actions);
    }

    [Fact]
    public void FromDto_coalesces_null_or_whitespace_OwnerKey_to_match()
    {
        var g = EffectGrant.FromDto(new EffectGrantDto
        {
            GrantId = "n",
            EffectId = "fx.butter_on_hit",
            OwnerKey = null!,
            OwnerKind = null!
        });
        Assert.Equal(EffectOwnerKeys.Match, g.OwnerKey);
        Assert.Equal("match", g.OwnerKind);
    }

    [Fact]
    public void Grant_still_accepts_entity_plant_match()
    {
        var h = new FoundationHarness();
        h.Grant(new EffectGrantDto
        {
            GrantId = "e",
            EffectId = "fx.passive_atk_flat",
            OwnerKey = EffectOwnerKeys.Entity("AAA")
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "p",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.PlantType(0),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "m",
            EffectId = "fx.freeze_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        Assert.Equal(3, h.Snapshot().Grants.Count);
    }

    [Fact]
    public void SimEffectHost_Die_fires_entity_OnDeath_then_withdraws()
    {
        var host = new SimEffectHost();
        host.Grant(new EffectGrantDto
        {
            GrantId = "entity-death",
            EffectId = "fx.spawn_zombie_ondeath",
            OwnerKey = EffectOwnerKeys.Entity("DEAD"),
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        host.Grant(new EffectGrantDto
        {
            GrantId = "match-butter",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });

        var plan = host.Die(side: "zombie", ptr: "0xDEAD", typeId: 0);
        Assert.Single(plan.Actions);
        Assert.Equal(EffectActions.SpawnEntity, plan.Actions[0].Action);
        Assert.Equal("entity-death", plan.Actions[0].GrantId);
        Assert.Empty(host.Bag.ForOwner(null, EffectOwnerKeys.Entity("DEAD")));
        Assert.Single(host.Bag.ForOwner(null, EffectOwnerKeys.Match));

        var hit = host.HitDealt();
        Assert.Single(hit.Actions);
        Assert.Equal("match-butter", hit.Actions[0].GrantId);
    }

    [Fact]
    public void Snapshot_revision_bumps_on_grant_and_catalog()
    {
        var h = new FoundationHarness();
        var r0 = h.Snapshot().Revision;
        h.Grant(new EffectGrantDto
        {
            GrantId = "r1",
            EffectId = "fx.butter_on_hit",
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var snap = h.Snapshot();
        Assert.True(snap.Revision > r0);
        Assert.Contains(snap.Grants, g => g.GrantId == "r1");
        var r1 = snap.Revision;
        h.ClearAll();
        Assert.True(h.Snapshot().Revision > r1);
        Assert.Empty(h.Snapshot().Grants);
    }

    [Fact]
    public void MatchButterSecondaryPlugin_grants_butter_and_plans_on_hit()
    {
        var h = new FoundationHarness();
        var plugins = new EffectPluginHost(h.Bag);
        plugins.Register(new MatchButterSecondaryPlugin());
        plugins.NotifyMatchStart("m-plugin");

        Assert.True(h.Bag.HasGrantForEffect("fx.butter_on_hit"));
        var plan = h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            MatchKey = "m-plugin",
            Side = "plant",
            ActorPtr = "0xA",
            TargetPtr = "0xB",
            TypeId = 0,
            TargetTypeId = 0,
            Damage = 10,
            Tick = 1
        });
        Assert.Single(plan.Actions);
        Assert.Equal(EffectActions.ApplyStatus, plan.Actions[0].Action);
        Assert.Equal("butter", plan.Actions[0].Params["status"]?.ToString());
    }

    [Fact]
    public void SimEffectHost_BeginMatch_grants_via_plugin_then_hit_plans()
    {
        var host = new SimEffectHost();
        host.Plugins.Register(new MatchButterSecondaryPlugin());
        host.BeginMatch("m-sim");
        Assert.True(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
        var plan = host.HitDealt(typeId: 0, targetTypeId: 0);
        Assert.Single(plan.Actions);
        Assert.Equal(EffectActions.ApplyStatus, plan.Actions[0].Action);
    }

    [Fact]
    public void SimEffectHost_EndMatch_withdraws_plugin_grants()
    {
        var host = new SimEffectHost();
        host.Plugins.Register(new MatchButterSecondaryPlugin());
        host.BeginMatch("m-sim");
        Assert.True(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
        host.EndMatch();
        Assert.False(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
        var plan = host.HitDealt(typeId: 0, targetTypeId: 0);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void SimEffectHost_ClearAll_skips_plugin_end_hook()
    {
        var host = new SimEffectHost();
        host.Plugins.Register(new MatchButterSecondaryPlugin());
        host.BeginMatch("m-sim");
        Assert.True(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
        host.ClearAll();
        Assert.False(host.Bag.HasGrantForEffect("fx.butter_on_hit"));
    }

    [Fact]
    public void SimEffectHost_FireFromCapture_maps_combat_hit()
    {
        var host = new SimEffectHost();
        host.Grant(new EffectGrantDto
        {
            GrantId = "cap",
            EffectId = "fx.butter_on_hit",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 }
        });
        var plan = host.FireFromCapture("combat.hit", new Dictionary<string, object>
        {
            ["side"] = "zombie",
            ["attackerPtr"] = "0xA",
            ["targetPtr"] = "0xB",
            ["fromType"] = 0,
            ["targetType"] = 0,
            ["damage"] = 20
        });
        Assert.NotNull(plan);
        Assert.Single(plan!.Actions);
        Assert.Equal(EffectTriggers.OnDamageDealt, plan.Trigger);
    }

    /// <summary>
    /// task E4, spec-mechanism-wiring.md §4.3 steps 1-2. Before this fold, both hosts' `ActorDerivedLookup`
    /// returned the pinned snapshot untouched — nothing for a bound `stat.derived` atom to contribute
    /// to. <see cref="SimEffectHost.ContributeDerived"/>/<see cref="FoundationHarness.ContributeDerived"/>
    /// both delegate to <c>ActorDerivedLookup.AddContribution</c>, so this proves the SAME fold logic
    /// reaches both hosts rather than two independently-written copies that could drift.
    ///
    /// <para>§4.3 Verification: the fold worked with <c>AtomKindRegistry</c>'s Sim cell for
    /// `stat.derived` still at <see cref="RuntimeState.None"/> — asserted first, so this test failed
    /// loudly (not silently passed for the wrong reason) the day E5 flipped it without updating this
    /// assertion, which is exactly what happened: E5 (2026-09-06) moved the cell to
    /// <see cref="RuntimeState.Partial"/>, then sim-hub-parity (T15, 2026-09-13) moved it again to
    /// <see cref="RuntimeState.Full"/> once the fold became op-aware, and this guard is updated in the
    /// same change both times, not left stale. <c>AddContribution</c> never asks
    /// <see cref="BindGate"/> anything, which is exactly what makes the fold and the registry cell
    /// independent — this test still runs unmodified in substance (Flat sums the same way under either
    /// state), it just no longer needs Sim's cell to sit at `None` or `Partial` to prove that
    /// independence.</para>
    /// </summary>
    [Fact]
    public void Sim_folds_bound_derived_contributions_onto_the_pinned_snapshot()
    {
        Assert.Equal(RuntimeState.Full,
            AtomKindRegistry.Get("stat.derived")!.SupportIn(RuntimeId.Sim));

        var flat = new BoundDerivedAtom(
            DerivedStatChannels.CombatDefenseOmni, DerivedModifierOp.Flat, 25, SourceId: "test-aura");

        var sim = new SimEffectHost();
        sim.PinDerived("0xA", ActorDerivedSnapshot.StubNeutral());
        sim.ContributeDerived("0xA", flat);
        Assert.Equal(25, sim.ResolveDerived("0xA").Get(DerivedStatChannels.CombatDefenseOmni));

        // Two contributions to the same channel sum, matching "plain sum is what FlatSum composing
        // IS" (ActorDerivedSnapshot.OverlayAdd's own doc).
        sim.ContributeDerived("0xA", flat with { Amount = 5, SourceId = "test-aura-2" });
        Assert.Equal(30, sim.ResolveDerived("0xA").Get(DerivedStatChannels.CombatDefenseOmni));

        // A ptr with no contribution is untouched -- the fold must not leak across actors.
        sim.PinDerived("0xB", ActorDerivedSnapshot.StubNeutral());
        Assert.Equal(0, sim.ResolveDerived("0xB").Get(DerivedStatChannels.CombatDefenseOmni));

        var harness = new FoundationHarness();
        harness.PinDerived("0xC", ActorDerivedSnapshot.StubNeutral());
        harness.ContributeDerived("0xC", flat);
        Assert.Equal(25, harness.ResolveDerived("0xC").Get(DerivedStatChannels.CombatDefenseOmni));
    }

    /// <summary>
    /// task E4, spec-mechanism-wiring.md §4.3 step 3. Before this, "no host constructs a
    /// `BindContext(RuntimeId.Sim)` today" -- the only production `BindContext` was `RpgHub.cs`'s
    /// `RuntimeId.Lawn` one, so flipping the registry cell on its own would authorize nothing.
    /// <see cref="SimEffectHost.TryBindDerivedAtoms"/>/<see cref="FoundationHarness.TryBindDerivedAtoms"/>
    /// are that call site: both run the row through the REAL <see cref="BindGate"/>, and both are
    /// exercised here.
    ///
    /// <para><b>E5 (§11 A5):</b> once the Sim cell for `stat.derived` moved off
    /// <see cref="RuntimeState.None"/> — first to <see cref="RuntimeState.Partial"/> (E5,
    /// 2026-09-06; see <see cref="The_four_derived_ops_decide_Full_versus_Partial"/> for why it
    /// landed there and not `Full` at the time), later to <see cref="RuntimeState.Full"/>
    /// (sim-hub-parity, T15, 2026-09-13) — the same row is genuinely ACCEPTED here:
    /// <see cref="BindGate.Check"/> only rejects <see cref="RuntimeState.None"/> and a non-planner
    /// host on <see cref="RuntimeState.PlanOnly"/>; `Partial` and `Full` both bind. This test used to
    /// assert the opposite (`RuntimeUnsupported`) before E5 first flipped the cell — updated here, not
    /// left to rot, per this test's own original doc comment ("that is E5's cell to flip, not this
    /// test's to fake past").</para>
    /// </summary>
    [Fact]
    public void A_stat_derived_bind_in_Sim_is_accepted()
    {
        var row = new AtomRow
        {
            AtomId = "test.derived.t1",
            KindId = "stat.derived",
            FamilyId = "test.derived",
            Tier = 1,
            ParamsJson = "{\"channel\":\"combat.defense.omni\",\"op\":\"flat\",\"amount\":25}"
        };

        var sim = new SimEffectHost();
        var simRejection = sim.TryBindDerivedAtoms(new[] { row }, OwnerScope.Match);
        Assert.True(simRejection.IsOk);

        var harness = new FoundationHarness();
        var harnessRejection = harness.TryBindDerivedAtoms(new[] { row }, OwnerScope.Match);
        Assert.True(harnessRejection.IsOk);
    }

    /// <summary>
    /// E5 (spec-mechanism-wiring.md §4.3 step 4) proved this fold against the real
    /// <see cref="DerivedComposer"/> and found it honoured only Flat/Increased -- <see cref="ActorDerivedLookup.Resolve"/>
    /// folded bound `stat.derived` contributions onto the pinned base via a PLAIN SUM
    /// (<see cref="ActorDerivedSnapshot.OverlayAdd"/>) that read only <see cref="BoundDerivedAtom.Amount"/>,
    /// never <see cref="BoundDerivedAtom.Op"/>. sim-hub-parity (T15, 2026-09-13) fixed it: `Resolve` now
    /// calls <see cref="DerivedComposer.ComposeChannelWithBaseline"/> per channel -- the SAME op-aware
    /// fold every other runtime's composer uses, with the pinned snapshot value standing in for
    /// <see cref="DerivedStatDef.DefaultValue"/>. This test proves, against the REAL
    /// <see cref="DerivedComposer"/> every other runtime composes through, that all four
    /// <see cref="DerivedModifierOp"/> values now match.
    ///
    /// <para><b>Flat</b> (a <c>FlatSum</c> channel) and <b>Increased</b> (a <c>SumIncreased</c>
    /// channel) were already sums, so the fix changes nothing about them: HONOURED before and after.</para>
    ///
    /// <para><b>Replace</b> (a <c>FlatReplace</c> channel) and <b>Flag</b> (a <c>MaxPriorityFlag</c>
    /// channel) are NOT sums in the real composer -- <c>ComposeFlatReplace</c> returns the
    /// highest-priority `Replace` value OUTRIGHT, discarding the baseline and every other
    /// contribution; <c>ComposeMaxFlag</c> returns the MAX of the flag-ish contributions.
    /// <c>ComposeChannelWithBaseline</c> reuses those SAME private helpers, so both now match: HONOURED.
    /// The Replace scenario below deliberately never sets an explicit <c>Priority</c> on either side --
    /// <see cref="BoundDerivedAtom"/> carries no `Priority` field at all (a genuine, pre-existing
    /// structural gap this task does not close), so a Sim-sourced <see cref="DerivedModifier"/> always
    /// defaults to `Priority: 0`, exactly like every other real producer converting FROM a
    /// `BoundDerivedAtom` already does (<c>AtomDerivedSubsystem</c>'s own conversion never sets one
    /// either) -- a tied multi-Replace breaks by `SourceId`, identically on both sides.</para>
    ///
    /// <para>Four of four honoured is the empirical basis for <see cref="RuntimeState.Full"/>, not
    /// <see cref="RuntimeState.Partial"/> -- <c>AtomKindRegistry</c>'s own comment on the `stat.derived`
    /// Sim cell says so, citing this test.</para>
    /// </summary>
    [Fact]
    public void The_four_derived_ops_decide_Full_versus_Partial()
    {
        var composer = new DerivedComposer();
        var lookup = new ActorDerivedLookup();

        // ---- Flat: progression.bonus.maxHp is a FlatSum channel, default 0. ----
        // (Was progression.bonus.atk until 2026-09-18, when the owner retired that channel --
        // solid-enforcement `retire-atk` R4. This asserts the FlatSum op's Full/Partial fold, a
        // property of the compose KIND, so any surviving FlatSum channel proves it identically.)
        const string flatChannel = DerivedStatChannels.ProgressionBonusMaxHp;
        var flatReal = composer.Compose(new[]
        {
            new DerivedModifier(flatChannel, DerivedModifierOp.Flat, 10)
        }).Get(flatChannel);

        lookup.Pin("0xFlat", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(flatChannel, 0)
        }));
        lookup.AddContribution("0xFlat", new BoundDerivedAtom(flatChannel, DerivedModifierOp.Flat, 10, "t"));
        var flatFold = lookup.Resolve("0xFlat", attackerLess: false).Get(flatChannel);

        Assert.Equal(10, flatReal);
        Assert.Equal(flatReal, flatFold); // HONOURED

        // ---- Increased: status.power.omni is a SumIncreased channel, default 0, uncapped. ----
        const string incChannel = DerivedStatChannels.StatusPowerOmni;
        var incReal = composer.Compose(new[]
        {
            new DerivedModifier(incChannel, DerivedModifierOp.Increased, 10)
        }).Get(incChannel);

        lookup.Pin("0xInc", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(incChannel, 0)
        }));
        lookup.AddContribution("0xInc", new BoundDerivedAtom(incChannel, DerivedModifierOp.Increased, 10, "t"));
        var incFold = lookup.Resolve("0xInc", attackerLess: false).Get(incChannel);

        Assert.Equal(10, incReal);
        Assert.Equal(incReal, incFold); // HONOURED

        // ---- Replace: progression.power is a FlatReplace channel, default 1.0. Neither side sets an
        // explicit Priority: BoundDerivedAtom carries no such field (a real, pre-existing structural
        // gap this task does not close), so a Sim-sourced DerivedModifier always defaults to
        // Priority: 0, matching what every OTHER real producer converting from a BoundDerivedAtom
        // already gets. A tie therefore breaks by SourceId on both sides. ----
        const string replaceChannel = DerivedStatChannels.ProgressionPower;
        var replaceReal = composer.Compose(new[]
        {
            new DerivedModifier(replaceChannel, DerivedModifierOp.Replace, 5, SourceId: "t1"),
            new DerivedModifier(replaceChannel, DerivedModifierOp.Replace, 50, SourceId: "t2")
        }).Get(replaceChannel);
        Assert.Equal(5, replaceReal); // tied priority (both 0) breaks by SourceId ascending -- "t1"
                                       // wins outright; baseline (1.0) discarded either way

        lookup.Pin("0xReplace", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(replaceChannel, 1.0)
        }));
        lookup.AddContribution("0xReplace",
            new BoundDerivedAtom(replaceChannel, DerivedModifierOp.Replace, 5, "t1"));
        lookup.AddContribution("0xReplace",
            new BoundDerivedAtom(replaceChannel, DerivedModifierOp.Replace, 50, "t2"));
        var replaceFold = lookup.Resolve("0xReplace", attackerLess: false).Get(replaceChannel);

        Assert.Equal(replaceReal, replaceFold); // sim-hub-parity (T15): now HONOURED

        // ---- Flag: status.immune.poison is a MaxPriorityFlag channel, default 0. ----
        var flagChannel = DerivedStatChannels.StatusImmune("poison");
        var flagReal = composer.Compose(new[]
        {
            new DerivedModifier(flagChannel, DerivedModifierOp.Flag, 1),
            new DerivedModifier(flagChannel, DerivedModifierOp.Flag, 1)
        }).Get(flagChannel);
        Assert.Equal(1, flagReal); // max of the flags, never a sum

        lookup.Pin("0xFlag", ActorDerivedSnapshot.FromValues(new[]
        {
            new KeyValuePair<string, double>(flagChannel, 0)
        }));
        lookup.AddContribution("0xFlag", new BoundDerivedAtom(flagChannel, DerivedModifierOp.Flag, 1, "t1"));
        lookup.AddContribution("0xFlag", new BoundDerivedAtom(flagChannel, DerivedModifierOp.Flag, 1, "t2"));
        var flagFold = lookup.Resolve("0xFlag", attackerLess: false).Get(flagChannel);

        Assert.Equal(flagReal, flagFold); // sim-hub-parity (T15): now HONOURED
    }
}
