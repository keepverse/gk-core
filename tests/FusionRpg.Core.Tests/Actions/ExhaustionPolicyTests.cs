using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// T16 (action-todo.md, spec-action-costs.md §7): exhaustion as a status. Reuses
/// <see cref="StatusRuntime"/> for storage/lifecycle and <see cref="StatusStatMod"/> as the debuff's
/// atom container; proves the four acceptance lines directly rather than through a proxy: one apply
/// per transition (counted across repeated calls at an unchanged state), a pure re-evaluate-on-read
/// check, and a self-regen-cycle rejected at construction (poise named explicitly, per spec).
///
/// <para><b>Honest gap, not fixed here:</b> <c>BattleEngine.ActorState.Derived</c>
/// (<c>BattleStatComposer.Compose(setup)</c>) is computed once at battle setup and never re-composed
/// from live <see cref="StatusRuntime"/> state during a battle — so a live exhaustion instance's
/// <see cref="StatusStatMod"/>s do not yet move an actor's derived combat channels in an actual
/// fight, the same "correct on paper, unreachable in battle" shape T14's grant-path finding named for
/// <c>resource.delta</c>/<c>shield.grant</c>. T16's own acceptance criteria are entirely mechanical
/// (apply count, re-evaluation, load-time validation) and are proved here against
/// <see cref="StatusRuntime"/> directly, which is real, live, mutated state — not against battle
/// combat outcomes, which this gap makes an honest thing not to claim.</para>
/// </summary>
public class ExhaustionPolicyTests
{
    static ExhaustionPolicy MakePolicy(StatusCatalog catalog, string resourceId, params StatusStatMod[] mods) =>
        new(catalog, new Dictionary<string, IReadOnlyList<StatusStatMod>> { [resourceId] = mods });

    static StatusRuntime MakeRuntime(StatusCatalog catalog) =>
        new(catalog, (_, _) => ActorDerivedSnapshot.Empty);

    [Fact]
    public void IsExhaustedIsAPureReadWithNoRuntimeInvolvedAtAll()
    {
        // The whole "re-evaluates on read" property, proven the strongest way available: no
        // StatusRuntime, no StatusCatalog, no instance -- just the resolved value in, the answer out.
        Assert.True(ExhaustionPolicy.IsExhausted(0));
        Assert.True(ExhaustionPolicy.IsExhausted(-5));
        Assert.False(ExhaustionPolicy.IsExhausted(1));
        Assert.False(ExhaustionPolicy.IsExhausted(long.MaxValue));
    }

    [Fact]
    public void CrossingTheLeaveThresholdFlipsTheAnswerWithNoWriteInBetween()
    {
        // Two calls, no runtime touched between them -- the "no write" half of the acceptance line,
        // made structural rather than argued: there is nothing here CAPABLE of writing.
        var wasExhausted = ExhaustionPolicy.IsExhausted(0);
        var nowRecovered = ExhaustionPolicy.IsExhausted(3);

        Assert.True(wasExhausted);
        Assert.False(nowRecovered);
    }

    [Fact]
    public void ConstructorRejectsASelfRegenCycle()
    {
        var catalog = new StatusCatalog();
        var selfRegenMod = new StatusStatMod(DerivedStatChannels.ResourceRegen("stamina"), "flat", -5);

        Assert.Throws<ArgumentException>(() => MakePolicy(catalog, "stamina", selfRegenMod));
    }

    [Fact]
    public void PoiseExhaustionMustNotTouchPoisesOwnRegenChannel()
    {
        // Named explicitly in the spec -- pinned as its own test rather than folded into the generic
        // self-regen-cycle case above, so a future refactor that narrows the generic check by
        // accident still fails loudly for the one resource the spec calls out by name.
        var catalog = new StatusCatalog();
        var poiseSelfRegen = new StatusStatMod(DerivedStatChannels.ResourceRegen("poise"), "flat", -1);

        var ex = Assert.Throws<ArgumentException>(() => MakePolicy(catalog, "poise", poiseSelfRegen));
        Assert.Contains("poise", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConstructorRejectsHpAsAnExhaustibleResource()
    {
        var catalog = new StatusCatalog();
        Assert.Throws<ArgumentException>(() =>
            MakePolicy(catalog, "hp", new StatusStatMod("combat.defense.omni", "flat", -10)));
    }

    [Fact]
    public void ANonSelfRegenDebuffIsAcceptedAtConstruction()
    {
        var catalog = new StatusCatalog();
        // Touches a DIFFERENT resource's regen and an ordinary combat channel -- neither is the
        // self-regen cycle the rule bans.
        var policy = MakePolicy(catalog, "stamina",
            new StatusStatMod("combat.defense.omni", "flat", -25),
            new StatusStatMod(DerivedStatChannels.ResourceRegen("qi"), "flat", -1));

        Assert.NotNull(policy); // reaching here without throwing is the assertion
    }

    [Fact]
    public void OneStatusApplyNotOnePerTickEvenHeldAtTheThresholdWithRegenTrickling()
    {
        var catalog = new StatusCatalog();
        var policy = MakePolicy(catalog, "stamina", new StatusStatMod("combat.defense.omni", "flat", -25));
        var runtime = MakeRuntime(catalog);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var applyCount = 0;
        // A pool "held at the threshold with regen trickling" oscillates around zero every tick --
        // simulated here as a fixed run of ten calls that all still read as exhausted (<=0).
        for (var tick = 0; tick < 10; tick++)
        {
            if (policy.Sync(runtime, "wave:0", "stamina", resolvedValue: 0, now.AddSeconds(tick)))
                applyCount++;
        }

        Assert.Equal(1, applyCount); // the acceptance line, counted rather than inferred from end state
        Assert.Single(runtime.ForHost("wave:0")); // and the final state IS identical either way -- exactly one live instance
    }

    [Fact]
    public void RecoveringAboveZeroWithdrawsTheLiveInstance()
    {
        var catalog = new StatusCatalog();
        var policy = MakePolicy(catalog, "stamina", new StatusStatMod("combat.defense.omni", "flat", -25));
        var runtime = MakeRuntime(catalog);
        var now = DateTimeOffset.UnixEpoch;

        Assert.True(policy.Sync(runtime, "wave:0", "stamina", resolvedValue: 0, now));
        Assert.Single(runtime.ForHost("wave:0"));

        var withdrew = policy.Sync(runtime, "wave:0", "stamina", resolvedValue: 5, now.AddSeconds(1));

        Assert.False(withdrew); // a withdraw is not counted as an "apply"
        Assert.Empty(runtime.ForHost("wave:0"));
    }

    [Fact]
    public void ReApplyingAfterRecoveryProducesASecondRealApply()
    {
        var catalog = new StatusCatalog();
        var policy = MakePolicy(catalog, "stamina", new StatusStatMod("combat.defense.omni", "flat", -25));
        var runtime = MakeRuntime(catalog);
        var now = DateTimeOffset.UnixEpoch;

        Assert.True(policy.Sync(runtime, "wave:0", "stamina", 0, now));
        policy.Sync(runtime, "wave:0", "stamina", 5, now.AddSeconds(1)); // recovers, withdrawn
        Assert.True(policy.Sync(runtime, "wave:0", "stamina", 0, now.AddSeconds(2))); // exhausted again -- a fresh apply, not blocked by history
    }

    [Fact]
    public void DifferentResourcesOnTheSameActorApplyIndependently()
    {
        var catalog = new StatusCatalog();
        var policy = new ExhaustionPolicy(catalog, new Dictionary<string, IReadOnlyList<StatusStatMod>>
        {
            ["stamina"] = new[] { new StatusStatMod("combat.defense.omni", "flat", -25) },
            ["qi"] = new[] { new StatusStatMod("combat.power.omni", "flat", -25) },
        });
        var runtime = MakeRuntime(catalog);
        var now = DateTimeOffset.UnixEpoch;

        policy.Sync(runtime, "wave:0", "stamina", 0, now);
        policy.Sync(runtime, "wave:0", "qi", 0, now);

        Assert.Equal(2, runtime.ForHost("wave:0").Count);

        policy.Sync(runtime, "wave:0", "stamina", 10, now.AddSeconds(1)); // stamina recovers alone

        var remaining = runtime.ForHost("wave:0");
        Assert.Single(remaining);
        Assert.Equal(ExhaustionStatusIds.For("qi"), remaining[0].StatusId);
    }

    [Fact]
    public void SyncOnAResourceThisPolicyDoesNotManageIsANoOp()
    {
        var catalog = new StatusCatalog();
        var policy = MakePolicy(catalog, "stamina", new StatusStatMod("combat.defense.omni", "flat", -25));
        var runtime = MakeRuntime(catalog);

        var applied = policy.Sync(runtime, "wave:0", "hunger", resolvedValue: 0, DateTimeOffset.UnixEpoch);

        Assert.False(applied);
        Assert.Empty(runtime.ForHost("wave:0"));
    }

    [Fact]
    public void TheAppliedInstanceCarriesTheAuthoredStatModsVerbatimAsItsAtomContainer()
    {
        // "Never a hardcoded channel list" -- proven by round-tripping an ARBITRARY mod list through
        // Apply and reading it back off the live instance, rather than asserting against any
        // resource-specific literal this test file happens to have chosen.
        var catalog = new StatusCatalog();
        var mods = new StatusStatMod[]
        {
            new("combat.defense.omni", "flat", -25),
            new("combat.dodge.omni", "increased", -0.1),
        };
        var policy = MakePolicy(catalog, "stamina", mods);
        var runtime = MakeRuntime(catalog);

        policy.Sync(runtime, "wave:0", "stamina", 0, DateTimeOffset.UnixEpoch);

        var instance = Assert.Single(runtime.ForHost("wave:0"));
        Assert.Equal(mods, instance.StatMods);
    }
}
