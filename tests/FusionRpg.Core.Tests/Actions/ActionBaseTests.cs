using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// AE1.2 (spec-action-base.md §Which rung): `BattleRunState.EffectiveRungOf` is the ONE instance rung
/// resolver the cost ledger already consumes (ST2.5 wired the method group). This file proves the half
/// that belongs to this task — its non-held fallback reads the row the actor actually SWUNG, never a
/// catalog lookup and never rung 0 for a row the catalog cannot resolve (siege `AdditionalHeldActions`,
/// a lent garrison action) — and that the ledger consults the resolver it is handed rather than a rung
/// rule of its own. `action-base`'s derivation, math and hit-site swap are AE1.3/AE1.4.
/// </summary>
public class ActionBaseTests
{
    static CompiledAction Dummy(string actionId, int rung) => new(
        ActionId: actionId, Kind: ActionKind.Skill, Rung: rung, Tags: Array.Empty<ActionTag>(),
        Enabled: true, Revision: 0, Grantable: false, DefaultAttackEligible: false, ContainerId: "",
        Envelope: ActionEnvelope.NoOp with { ActionId = actionId },
        Targeting: TargetSpecCompiler.Compile(new ActionTargetSpec()),
        MinRange: 0, MaxRange: int.MaxValue, RangeChannel: null, RequiresLineOfSight: false,
        Condition: PredicateCompiler.Always, Costs: Array.Empty<CompiledActionCost>(),
        Scopes: Array.Empty<ActionScopeRow>());

    static BattleActorSetup Animate(string key, string side,
        IReadOnlyList<string>? equipped = null,
        IReadOnlyList<CompiledAction>? additional = null) => new()
    {
        Key = key, Side = side, SpeciesId = "ab-species", TypeId = 31_001, Level = 3,
        MaxHp = 1000, Atk = 100, EquippedActionIds = equipped, AdditionalHeldActions = additional,
    };

    static BattleActorSetup Structure(string key, string side, string? garrisonedBy = null,
        IReadOnlyList<CompiledAction>? additional = null) => new()
    {
        Key = key, Side = side, SpeciesId = "ab-structure", TypeId = 31_002, Level = 0,
        MaxHp = 1000, Atk = 0, Kind = CombatantKind.Structure, GarrisonedBy = garrisonedBy,
        AdditionalHeldActions = additional,
    };

    static ActorDerivedSnapshot Snapshot(double theta, params (string resourceId, double max, double regen)[] resources)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        var mods = new List<DerivedModifier>
        {
            new(DerivedStatChannels.ProgressionPower, DerivedModifierOp.Flat, theta, SourceId: "test"),
        };
        foreach (var (id, max, regen) in resources)
        {
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, max, SourceId: "test"));
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, regen, SourceId: "test"));
        }
        return composer.Compose(mods);
    }

    /// <summary>Spec test: a held row absent from the catalog (siege `AdditionalHeldActions`) resolves
    /// the rung it was compiled at — the pre-AE1.2 fallback returned 0 here, pricing a rung-7 skill as
    /// rung 0. Planted violation: delete the `HeldActionOf` clause from the fallback and this reads 0.</summary>
    [Fact]
    public void The_fallback_reads_the_swung_rows_rung_for_a_row_the_catalog_cannot_resolve()
    {
        var setup = new BattleSetup
        {
            WaveId = "ab-wave",
            Squad = new[] { Animate("squad:0", "squad", additional: new[] { Dummy("siege.strike", rung: 7) }) },
            Wave = new[] { Animate("wave:0", "wave") },
        };

        // No catalog at all: the only rung source is the held row itself.
        Assert.Equal(7, BattleEngine.EffectiveRungForTest(setup, seed: 7, actorKey: "squad:0", actionId: "siege.strike"));
    }

    /// <summary>Spec test: a garrisoned structure lends its actions to its occupant, and a lent row the
    /// catalog cannot resolve reads its own rung — the same fallback through <c>HeldActionsOf</c>' union.</summary>
    [Fact]
    public void The_fallback_reads_a_lent_garrison_rows_rung()
    {
        var catalog = ActionCatalog.Build(new[] { Dummy("skill.occupant", rung: 2) });
        var setup = new BattleSetup
        {
            WaveId = "ab-wave",
            Squad = new[] { Animate("squad:0", "squad") },
            Wave = new[]
            {
                Animate("wave:occupant", "wave", equipped: new[] { "skill.occupant" }),
                Structure("wave:tower", "wave", garrisonedBy: "wave:occupant",
                    additional: new[] { Dummy("tower.bolt", rung: 5) }),
            },
        };

        Assert.Equal(5, BattleEngine.EffectiveRungForTest(
            setup, seed: 5, actorKey: "wave:occupant", actionId: "tower.bolt", actionCatalog: catalog));
        // The occupant's own catalog action keeps its catalog rung, unchanged.
        Assert.Equal(2, BattleEngine.EffectiveRungForTest(
            setup, seed: 5, actorKey: "wave:occupant", actionId: "skill.occupant", actionCatalog: catalog));
    }

    /// <summary>Spec test: for every catalog action the fallback is byte-identical to
    /// `actionCatalog.Get(id).Rung` — both for an actor that holds it (the held row IS the catalog's
    /// compiled row) and for one that does not (the catalog lookup remains the last resort).</summary>
    [Fact]
    public void The_fallback_is_byte_identical_to_the_catalog_rung_for_every_catalog_action()
    {
        var actions = new[] { Dummy("skill.a", rung: 1), Dummy("skill.b", rung: 4), Dummy("skill.c", rung: 9) };
        var catalog = ActionCatalog.Build(actions);
        var setup = new BattleSetup
        {
            WaveId = "ab-wave",
            Squad = new[]
            {
                Animate("squad:0", "squad", equipped: new[] { "skill.a", "skill.b" }),
                Animate("squad:1", "squad"),
            },
            Wave = new[] { Animate("wave:0", "wave") },
        };

        foreach (var action in actions)
        {
            var expected = catalog.Get(action.ActionId)!.Rung;
            Assert.Equal(expected, BattleEngine.EffectiveRungForTest(setup, 1, "squad:0", action.ActionId, catalog));
            Assert.Equal(expected, BattleEngine.EffectiveRungForTest(setup, 1, "squad:1", action.ActionId, catalog));
        }
    }

    /// <summary>Spec test: a call-counting double proves the ledger consults the rung resolver it is
    /// handed — the ledger holds no rung rule of its own, and every price it charges is the resolver's
    /// output. The double returns rung 2 for a rung-1-authored row, so the price follows the double.</summary>
    [Fact]
    public void The_cost_ledger_calls_the_rung_resolver_it_is_handed()
    {
        const int Base = 10;
        Assert.True(RungPolicy.Table.TryResolve(2, out var rung2));
        var scaledOnce = CurveTable.ApplyMilli(Base, rung2.CostMulti);

        var derived = Snapshot(theta: 0, ("stamina", 1_000_000, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var start = pools.Resolve("stamina", 0, derived);
        var costs = new Dictionary<string, IReadOnlyList<ActionCostRow>>
        {
            ["skill.test"] = new[] { new ActionCostRow("skill.test", "stamina", ValueSpec.Of(Base), ActionCostTiming.OnCommit) },
        };

        var calls = 0;
        var ledger = new CostLedger(costs, _ => pools, _ => derived, (_, _) => { calls++; return 2; }, () => 0);

        Assert.True(ledger.Check("actor", "skill.test").IsUsable);
        Assert.Equal(1, calls); // one consult per decision, no cached rung inside the ledger

        Assert.Equal(CostPayOutcome.Paid, ledger.TryPay("actor", "skill.test", ActionCostTiming.OnCommit, rng: null).Outcome);
        Assert.Equal(2, calls);
        Assert.Equal(start - scaledOnce, pools.Resolve("stamina", 0, derived)); // priced at the double's rung, once
    }

    // ---- AE1.3: the pure derivation, the math, and P(Theta) ----

    /// <summary>Spec test: a skill's (and an innate's) base is the qPowerMilli of the LOADED table's row
    /// at the holder's effective rung — read from the table, never a literal, for every row.</summary>
    [Fact]
    public void A_skills_base_reads_the_rung_rows_qPower_for_every_row_of_the_loaded_table()
    {
        var table = RungPolicy.Table;
        Assert.NotEmpty(table.Rows);

        foreach (var row in table.Rows)
        {
            Assert.Equal(row.QPowerMilli, ActionBaseDerivation.BasePowerMilli(
                ActionKind.Skill, "skill.rung", row.Rung, table, ActionBaseTuningHub.Tuning));
            Assert.Equal(row.QPowerMilli, ActionBaseDerivation.BasePowerMilli(
                ActionKind.Innate, "innate.rung", row.Rung, table, ActionBaseTuningHub.Tuning));
        }
    }

    /// <summary>Spec test: the basic attack reads the tuning value (`action-base.v{n}.json`), never a row.</summary>
    [Fact]
    public void The_basic_attack_base_reads_the_tuning_value()
    {
        var tuning = ActionBaseTuningHub.Tuning;
        Assert.Equal(tuning.BasicAttackBasePowerMilli, ActionBaseDerivation.BasePowerMilli(
            ActionKind.Basic, "act.attack", effectiveRung: 0, RungPolicy.Table, tuning));
    }

    /// <summary>Spec test: a skill whose effective rung has no row throws, naming the action — never the
    /// basic-attack fallback.</summary>
    [Fact]
    public void A_skill_whose_rung_has_no_row_throws_naming_the_action()
    {
        var table = RungPolicy.Table;
        var ex = Assert.Throws<InvalidOperationException>(() => ActionBaseDerivation.BasePowerMilli(
            ActionKind.Skill, "skill.no-rung", int.MaxValue, table, ActionBaseTuningHub.Tuning));
        Assert.Contains("skill.no-rung", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Spec test: `BasePerHit` is `checked` `long`, divides by 1000 once and last — exact past
    /// `int` range, and a `long` overflow throws rather than wrapping.</summary>
    [Fact]
    public void BasePerHit_stays_exact_past_int_range_and_throws_checked_on_long_overflow()
    {
        const long baseMilli = 4_000_000_000; // > int.MaxValue
        const long pTheta = 3;
        var product = baseMilli * pTheta;     // 12e9 — past int range, comfortably inside long
        Assert.True(product > int.MaxValue);
        Assert.Equal(product / 1000, ActionBaseMath.BasePerHit(baseMilli, pTheta));

        Assert.Throws<OverflowException>(() => ActionBaseMath.BasePerHit(long.MaxValue, 1000));
    }

    /// <summary>Spec test: `PowerValue(int)` is the same cached ladder `BaseHp` reads — P(Θ), not a
    /// second curve.</summary>
    [Fact]
    public void PowerValue_is_the_same_ladder_as_BaseHp()
    {
        foreach (var theta in new[] { 1, 5, 20 })
            Assert.Equal(BattleRuleset.BaseHp(theta), BattleRuleset.PowerValue(theta));
    }

    /// <summary>Spec test: an unconfigured hub throws, naming the file — no built-in default. Restores
    /// the real configured tuning in `finally`, the same discipline `WonderCatalogTests` uses.</summary>
    [Fact]
    public void An_unconfigured_hub_throws_naming_the_file()
    {
        var configured = ActionBaseTuningHub.Tuning;
        ActionBaseTuningHub.Reset();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => _ = ActionBaseTuningHub.Tuning);
            Assert.Contains("action-base.v", ex.Message, StringComparison.Ordinal);
            Assert.False(ActionBaseTuningHub.IsConfigured);
        }
        finally
        {
            ActionBaseTuningHub.Configure(configured);
        }
    }

    // ---- AE1.4: the hit's base is the action's (H6) ----

    static long BaseHit(int rung, int theta) => ActionBaseMath.BasePerHit(
        ActionBaseDerivation.BasePowerMilli(ActionKind.Skill, "skill.hit", rung, RungPolicy.Table, ActionBaseTuningHub.Tuning),
        BattleRuleset.PowerValue(theta));

    static BattleSetup HitSetup(long atk, int? theta = null) => new()
    {
        WaveId = "ab-hit",
        Squad = new[]
        {
            new BattleActorSetup
            {
                Key = "squad:0", Side = "squad", SpeciesId = "ab-attacker", TypeId = 33_001,
                Level = 5, ThetaActor = theta, MaxHp = 200_000, Atk = atk, Defense = 0,
            },
        },
        Wave = new[]
        {
            new BattleActorSetup
            {
                Key = "wave:0", Side = "wave", SpeciesId = "ab-target", TypeId = 33_002,
                Level = 5, MaxHp = 200_000, Atk = 0, Defense = 0,
            },
        },
    };

    static long TotalDamage(BattleSetup setup) =>
        BattleEngine.Resolve(setup, seed: 991).Actors.Sum(a => a.DamageDealt);

    /// <summary>H6, the decisive falsifier: `Setup.Atk` no longer feeds damage. Two battles identical
    /// except `Atk` (1 vs a million) deal identical damage, because the base is the ACTION's and the
    /// magnitude is Θ (Hub `progression.power`). Reinstating the `LiveAtk` read makes these differ.</summary>
    [Fact]
    public void The_actors_atk_no_longer_feeds_damage()
    {
        var low = TotalDamage(HitSetup(atk: 1));
        var high = TotalDamage(HitSetup(atk: 1_000_000));

        Assert.True(low > 0, "the battle must actually land hits for this test to mean anything");
        Assert.Equal(low, high);
    }

    /// <summary>Spec test: the hit's magnitude follows the holder's Θ — identical actors except
    /// `ThetaActor` deal different damage, higher Θ dealing more.</summary>
    [Fact]
    public void The_hit_follows_the_holders_Theta()
    {
        var low = TotalDamage(HitSetup(atk: 100, theta: 5));
        var high = TotalDamage(HitSetup(atk: 100, theta: 20));

        Assert.True(high > low, $"Θ=20 must out-damage Θ=5 (got {high} vs {low})");
    }

    /// <summary>Spec test: two effective rungs on one attacker scale the hit by `qPower(9)/qPower(1)`,
    /// read from the loaded table (the truncating `/1000` makes the ratio approximate, so it is compared
    /// as a ratio rather than cross-multiplied).</summary>
    [Fact]
    public void Two_effective_rungs_scale_the_hit_by_qPower_read_from_the_table()
    {
        Assert.True(RungPolicy.Table.TryGet(1, out var rung1));
        Assert.True(RungPolicy.Table.TryGet(9, out var rung9));
        const int theta = 20;

        var hit1 = BaseHit(1, theta);
        var hit9 = BaseHit(9, theta);

        Assert.True(hit9 > hit1);
        // 2 decimals: the single truncating `/1000` inside BasePerHit leaves the ratio approximate.
        Assert.Equal((double)rung9.QPowerMilli / rung1.QPowerMilli, (double)hit9 / hit1, 2);
    }

    /// <summary>Spec test: two Θ scale the hit by `P(Θ₂)/P(Θ₁)` — Θ comes from the Hub channel, not from
    /// a setup field.</summary>
    [Fact]
    public void Two_thetas_scale_the_hit_by_P_of_theta()
    {
        var hitLow = BaseHit(3, 5);
        var hitHigh = BaseHit(3, 20);

        Assert.Equal(
            (double)BattleRuleset.PowerValue(20) / BattleRuleset.PowerValue(5),
            (double)hitHigh / hitLow,
            2);
    }
}
