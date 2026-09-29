using System;
using System.Collections.Generic;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Unlock;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// combat-ai `lawn-cost-authority` A (CAI4.4, spec-lawn-cost-authority.md): the ONE rung derivation.
/// The extraction's fidelity is proven by the battle goldens not moving; these tests prove the
/// resolver's own CONTRACT — its four resolution steps, the `floorWhenUnknown` parameter, and the two
/// planted violations that make the floor load-bearing rather than decorative.
/// </summary>
public class EffectiveRungResolverTests
{
    static UnlockTuning Tuning(int rungCap = 10) =>
        new(P1Milli: 500, DeltaMilli: 100, FloorMilli: 100, HeldCap: 8, RungCap: rungCap, DiscardTaxCoeffMilli: 0);

    static CompiledAction Action(string id, int rung, RungBand? band = null) => new(
        id, ActionKind.Skill, rung, Array.Empty<ActionTag>(), true, 1, false, false, "item.test",
        ActionEnvelope.NoOp with { ActionId = id },
        new CompiledTargetSpec(true, Array.Empty<FusionRpg.Contracts.TargetSpec>()),
        0, int.MaxValue, null, false, PredicateCompiler.Always,
        Array.Empty<CompiledActionCost>(), Array.Empty<ActionScopeRow>(), band);

    /// <summary>The pre-CAI4.4 body, transcribed — the reference this extraction must match. Its own
    /// existence here is the point: the resolver has to agree with the code it replaced, and a
    /// hand-written expected value per case would not catch a reordered step.</summary>
    static int OriginalBody(
        string actorKey, string actionId, ActionCatalog? catalog,
        Func<string, UnlockState>? unlockStateFor, UnlockTuning? tuning,
        Func<string, string, CompiledAction?> heldActionOf)
    {
        var state = unlockStateFor?.Invoke(actorKey);
        if (state is not null && tuning is not null)
        {
            foreach (var held in state.Held)
            {
                if (held.UnlockId == actionId)
                {
                    var band = heldActionOf(actorKey, actionId)?.RungBand;
                    return UnlockLadder.EffectiveRung(held.EarnCountAtAcceptance, tuning, band).Value;
                }
            }
        }

        return heldActionOf(actorKey, actionId)?.Rung ?? catalog?.Get(actionId)?.Rung ?? 0;
    }

    static int Resolve(
        string actionId,
        CompiledAction? held = null,
        CompiledAction? catalogued = null,
        UnlockState? unlockState = null,
        UnlockTuning? tuning = null,
        int floorWhenUnknown = 0)
    {
        var catalog = catalogued is null ? ActionCatalog.Empty : ActionCatalog.Build(new[] { catalogued });
        return EffectiveRungResolver.Resolve(
            "actor", actionId, catalog,
            unlockStateFor: unlockState is null ? null : _ => unlockState,
            unlockTuning: tuning,
            heldActionOf: (_, id) => held is not null && held.ActionId == id ? held : null,
            floorWhenUnknown: floorWhenUnknown);
    }

    /// <summary>
    /// The spread the row names — held / not-held / catalog / no-catalog, with and without unlock state
    /// — compared against the transcribed original body, `floorWhenUnknown: 0` both sides.
    /// </summary>
    [Fact]
    public void Resolve_with_floor_zero_matches_the_original_body_across_the_spread()
    {
        var unlockState = UnlockState.FromPersisted(7, new[] { new HeldUnlock("act.unlocked", 7) });
        var tuning = Tuning(rungCap: 10);

        var cases = new (string Label, string ActionId, CompiledAction? Held, CompiledAction? Catalogued,
            UnlockState? State, UnlockTuning? Tuning)[]
        {
            ("held + catalog, no unlock state", "act.a", Action("act.a", rung: 6), Action("act.a", rung: 6), null, null),
            ("not held, catalog", "act.b", null, Action("act.b", rung: 4), null, null),
            ("held, no catalog", "act.c", Action("act.c", rung: 3), null, null, null),
            ("held + unlock state", "act.unlocked", Action("act.unlocked", rung: 2), null, unlockState, tuning),
            ("unlock state present, id not in it", "act.other", Action("act.other", rung: 9), null, unlockState, tuning),
            ("nothing anywhere", "act.absent", null, null, null, null),
            ("unlock state without tuning", "act.unlocked", Action("act.unlocked", rung: 2), null, unlockState, null),
        };

        foreach (var (label, actionId, held, catalogued, state, tune) in cases)
        {
            var expected = OriginalBody("actor", actionId,
                catalogued is null ? ActionCatalog.Empty : ActionCatalog.Build(new[] { catalogued }),
                state is null ? null : _ => state, tune,
                (_, id) => held is not null && held.ActionId == id ? held : null);

            var actual = Resolve(actionId, held, catalogued, state, tune, floorWhenUnknown: 0);

            Assert.True(expected == actual,
                $"{label}: original body said {expected}, EffectiveRungResolver said {actual}");
        }
    }

    /// <summary>A held action with an authored rung and NO unlock state prices at that rung — and the
    /// planted violation is that the lawn's old `rungOf: (_, _) => 1` constant would not.</summary>
    [Fact]
    public void A_held_action_with_no_unlock_state_prices_at_its_authored_rung()
    {
        var resolved = Resolve("act.deep", held: Action("act.deep", rung: 7));

        Assert.Equal(7, resolved);
        Assert.NotEqual(1, resolved);   // r = 7, so the old constant is visibly wrong here
        // The falsifier itself, so the pair above is a real check rather than a comment:
        Func<string, string, int> oldLawnConstant = (_, _) => 1;
        Assert.Equal(1, oldLawnConstant("actor", "act.deep"));
    }

    /// <summary>The unlock step: `min(earnCount, rungCap)` with no band, `min(earnCount, rungCap,
    /// band.Ceiling)` with one.</summary>
    [Fact]
    public void An_unlock_state_prices_at_min_earn_rungcap_and_band_ceiling()
    {
        var state = UnlockState.FromPersisted(9, new[] { new HeldUnlock("act.unlocked", 9) });

        Assert.Equal(9, Resolve("act.unlocked", held: Action("act.unlocked", rung: 2),
            unlockState: state, tuning: Tuning(rungCap: 10)));
        Assert.Equal(3, Resolve("act.unlocked", held: Action("act.unlocked", rung: 2),
            unlockState: state, tuning: Tuning(rungCap: 3)));
        Assert.Equal(4, Resolve("act.unlocked", held: Action("act.unlocked", rung: 2, band: new RungBand(Floor: 1, Ceiling: 4)),
            unlockState: state, tuning: Tuning(rungCap: 10)));
    }

    /// <summary>The authoring path beats the catalog: a held row ABSENT from the catalog still resolves
    /// its own rung (AE1.2's fix, siege `AdditionalHeldActions` and lent garrison actions).</summary>
    [Fact]
    public void A_held_row_absent_from_the_catalog_resolves_its_own_rung_not_the_catalogs()
    {
        Assert.Equal(5, Resolve("act.both", held: Action("act.both", rung: 5), catalogued: Action("act.both", rung: 2)));
    }

    /// <summary>
    /// `floorWhenUnknown: 1` is the lawn's whole reason for this extraction: `CostLedger` resolves a
    /// rung through `RungPolicy.Table`, which has no row for 0 and THROWS. The second half is the
    /// planted violation — with the battle floor, the same payment throws.
    /// </summary>
    [Fact]
    public void An_unknown_id_with_floor_one_prices_at_one_and_pays_while_floor_zero_throws()
    {
        Assert.Equal(1, Resolve("act.nowhere", floorWhenUnknown: 1));

        var derived = Snapshot(("stamina", 1000, 0));
        var pools = ActorResourcePools.CreateFull(derived, atTick: 0);
        var costs = new Dictionary<string, IReadOnlyList<ActionCostRow>>
        {
            ["act.nowhere"] = new ActionCostRow[]
            {
                new("act.nowhere", "stamina", ValueSpec.Of(10), ActionCostTiming.OnCommit),
            },
        };

        CostLedger Ledger(int floorWhenUnknown) => new(
            costs, _ => pools, _ => derived,
            rungOf: (actor, id) => EffectiveRungResolver.Resolve(
                actor, id, ActionCatalog.Empty, unlockStateFor: null, unlockTuning: null,
                heldActionOf: (_, _) => null, floorWhenUnknown: floorWhenUnknown),
            nowTick: () => 0L);

        // The lawn's floor: affordable, priced at rung 1, and it pays.
        Assert.Equal(CostPayOutcome.Paid, Ledger(floorWhenUnknown: 1).TryPay("actor", "act.nowhere", ActionCostTiming.OnCommit, rng: null).Outcome);

        // PLANTED VIOLATION: battle's floor on an uncatalogued id is not a cheaper price, it is a throw.
        var battleFloor = Ledger(floorWhenUnknown: 0);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => battleFloor.TryPay("actor", "act.nowhere", ActionCostTiming.OnCommit, rng: null));
    }

    static ActorDerivedSnapshot Snapshot(params (string resourceId, double max, double regen)[] resources)
    {
        var composer = new DerivedComposer(DerivedStatRegistry.CreateDefault());
        var mods = new List<DerivedModifier>
        {
            new(DerivedStatChannels.ProgressionPower, DerivedModifierOp.Flat, 0.0, SourceId: "test"),
            new(DerivedStatChannels.ProgressionRealm, DerivedModifierOp.Flat, 1.0, SourceId: "test"),
        };
        foreach (var (id, max, regen) in resources)
        {
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, max, SourceId: "test"));
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, regen, SourceId: "test"));
        }
        return composer.Compose(mods);
    }
}
