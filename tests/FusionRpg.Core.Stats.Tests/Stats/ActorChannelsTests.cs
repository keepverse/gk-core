using FusionRpg.Core.Progression;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Stats;

/// <summary>spec-actor-channels.md (T4.4) — the six actor resources, move.range, and the two
/// progression rates. Several tests here prove the documented contract/formula shape rather than an
/// end-to-end consumer — each says so explicitly where that applies.</summary>
public class ActorChannelsTests
{
    static readonly string[] ExhaustibleIds = { "stamina", "hunger", "spirit", "qi", "poise" };

    [Fact]
    public void ResourceChannelsNotInCombatRoster()
    {
        // spec §2 property 1 -- resources are not element-typed and must never join
        // AllCombatChannelIds: that set is asserted at a generated total and expands over elements; a
        // resource channel there would break the assertion AND get swept into element expansion.
        var combatIds = new HashSet<string>(DerivedStatChannels.AllCombatChannelIds, StringComparer.Ordinal);

        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            Assert.DoesNotContain(DerivedStatChannels.ResourceMax(id), combatIds);
            Assert.DoesNotContain(DerivedStatChannels.ResourceRegen(id), combatIds);
            Assert.DoesNotContain(DerivedStatChannels.ResourceEfficiency(id), combatIds);
        }
        Assert.DoesNotContain(DerivedStatChannels.MoveRange, combatIds);
        Assert.DoesNotContain(DerivedStatChannels.ProgressionXpRate, combatIds);
        Assert.DoesNotContain(DerivedStatChannels.ProgressionBreakthroughSuccess, combatIds);
    }

    [Fact]
    public void FiveExhaustionDebuffsStack()
    {
        // spec §2.1 -- the exhaustion debuffs (stamina, hunger, spirit, qi, poise -- hp's depletion
        // is death, never an exhaustion debuff per gk-data/packs/fusion/data/seed/resources/roster.json) stacking on one
        // actor at once. Property 4: same five
        // compose kinds, same per-channel caps, no new ordering rule -- this actually runs the
        // combination for the first time. Two debuffs land on qi's efficiency specifically so the cap
        // is proven to still hold even while the other four pools are simultaneously debuffed.
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);

        var mods = new List<DerivedModifier>();
        foreach (var id in ExhaustibleIds)
        {
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceMax(id), DerivedModifierOp.Flat, -20, SourceId: "exhaustion." + id));
            mods.Add(new DerivedModifier(DerivedStatChannels.ResourceRegen(id), DerivedModifierOp.Flat, -5, SourceId: "exhaustion." + id));
        }
        // qi gets a second, independent efficiency debuff on top -- two sources stacking past the cap.
        mods.Add(new DerivedModifier(DerivedStatChannels.ResourceEfficiency("qi"), DerivedModifierOp.Increased, 0.7, SourceId: "exhaustion.qi.a"));
        mods.Add(new DerivedModifier(DerivedStatChannels.ResourceEfficiency("qi"), DerivedModifierOp.Increased, 0.7, SourceId: "exhaustion.qi.b"));

        var snapshot = composer.Compose(mods);

        foreach (var id in ExhaustibleIds)
        {
            Assert.Equal(-20, snapshot.Get(DerivedStatChannels.ResourceMax(id)));
            Assert.Equal(-5, snapshot.Get(DerivedStatChannels.ResourceRegen(id)));
        }
        // 0.7 + 0.7 = 1.4, clamped to DerivedStatPolicy.ResourceEfficiencyCap (1.0) -- the cap holds
        // under a two-source stack, itself nested inside the five-pool stack above.
        Assert.Equal(DerivedStatPolicy.ResourceEfficiencyCap, snapshot.Get(DerivedStatChannels.ResourceEfficiency("qi")));

        // hp is never an exhaustion debuff target (roster.json: "Depletion is death... never an
        // exhaustion debuff") -- unaffected by the other four stacking simultaneously.
        Assert.Equal(0, snapshot.Get(DerivedStatChannels.ResourceMax("hp")));
        Assert.Equal(0, snapshot.Get(DerivedStatChannels.ResourceRegen("hp")));
    }

    [Theory]
    [InlineData(100.0, 5.0, 1000)]
    [InlineData(50.0, -2.0, 5000)]
    [InlineData(0.0, 10.0, 250)]
    [InlineData(1000.0, 0.0, 10_000)]
    public void LazyValueMatchesTicked(double baseValue, double ratePerSecond, long elapsedMs)
    {
        // spec §2 property 3 -- value + rate x elapsed must equal a hypothetically ticked pool at N
        // sample points, proving the compute-on-read optimisation (avoiding 800 recurring scheduled
        // events for 200 actors x 4 regenerating pools against a 0.15ms kernel slice) is not a
        // behaviour change, just a different way to reach the same number. No ResourceRuntime class
        // exists yet (nothing ticks a resource today) -- this is a pure proof of the formula itself.
        var lazyValue = baseValue + ratePerSecond * (elapsedMs / 1000.0);

        var ticked = baseValue;
        const long stepMs = 50;
        var steps = elapsedMs / stepMs;
        for (var i = 0; i < steps; i++)
            ticked += ratePerSecond * (stepMs / 1000.0);

        Assert.Equal(ticked, lazyValue, 6);
    }

    [Fact]
    public void EfficiencyCannotExceedOne()
    {
        // spec §2.2 -- cost reduction cannot exceed 100%; a negative cost is a faucet. Registered
        // SumIncreased + Cap: ComposeChannel's FlatSum case never applies Cap at all, so this also
        // guards against the channel silently regressing back to an unenforced FlatSum registration.
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);

        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            Assert.True(registry.TryGet(DerivedStatChannels.ResourceEfficiency(id), out var def));
            Assert.Equal(DerivedComposeKind.SumIncreased, def.Compose);
            Assert.Equal(DerivedStatPolicy.ResourceEfficiencyCap, def.Cap);

            var mods = new[] { new DerivedModifier(DerivedStatChannels.ResourceEfficiency(id), DerivedModifierOp.Increased, 5.0, SourceId: "test") };
            var snapshot = composer.Compose(mods);
            Assert.Equal(DerivedStatPolicy.ResourceEfficiencyCap, snapshot.Get(DerivedStatChannels.ResourceEfficiency(id)));
        }
    }

    [Fact]
    public void MaxAndRegenUncapped()
    {
        // spec §2.2 -- max/regen are magnitudes and stay uncapped; they scale on P(Th) like any other.
        var registry = DerivedStatRegistry.CreateDefault();
        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            Assert.True(registry.TryGet(DerivedStatChannels.ResourceMax(id), out var maxDef));
            Assert.Null(maxDef.Cap);
            Assert.True(registry.TryGet(DerivedStatChannels.ResourceRegen(id), out var regenDef));
            Assert.Null(regenDef.Cap);
        }

        // Scales past any literal, no silent clamp -- the catalog-level half of "overflow throws,
        // never clamps" (the long-arithmetic half is the future runtime consumer's job, not built here).
        var composer = new DerivedComposer(registry);
        var mods = new[] { new DerivedModifier(DerivedStatChannels.ResourceMax("hp"), DerivedModifierOp.Flat, 999_999_999.0, SourceId: "test") };
        Assert.Equal(999_999_999.0, composer.Compose(mods).Get(DerivedStatChannels.ResourceMax("hp")));
    }

    [Fact]
    public void MoveRangePassesWithNoBoard()
    {
        // action-map.md:573 -- "With no board, every range check passes", which is what keeps the
        // basic attack byte-identical while the grid itself stays deferred. No range-check consumer
        // exists yet (the grid is deferred) -- what's provable now is that move.range's OWN resolution
        // is entirely board/grid-independent: DerivedComposer never receives or touches a board
        // object, so a channel composed through it structurally cannot fail against a missing one.
        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryGet(DerivedStatChannels.MoveRange, out var def));
        Assert.Equal(StatClass.Pool, def.Class);
        Assert.Equal(DerivedComposeKind.FlatSum, def.Compose);
        Assert.Null(def.Cap);

        var composer = new DerivedComposer(registry);
        var mods = new[] { new DerivedModifier(DerivedStatChannels.MoveRange, DerivedModifierOp.Flat, 3.0, SourceId: "item.reach") };
        Assert.Equal(3.0, composer.Compose(mods).Get(DerivedStatChannels.MoveRange));
    }

    [Fact]
    public void XpRateLayersOnAward()
    {
        // spec §4.1 -- progression.xpRate is a per-actor MULTIPLIER layered on top of
        // Award.PowerScale, never a replacement, and reads no level. No consumer is wired yet (this
        // module registers the channel only): this proves the documented contract formula --
        // finalXp = award.Delta * award.PowerScale * (1 + xpRate) -- is a no-op at the shipped
        // default (0), and that Award.Delta/PowerScale are untouched by this module.
        var award = new RpgXpAwardMap.Award("plant", 3, Delta: 100, Reason: "kill", PowerScale: 1.0);
        double ApplyXpRate(RpgXpAwardMap.Award a, double xpRate) => a.Delta * a.PowerScale * (1.0 + xpRate);

        Assert.Equal(award.Delta * award.PowerScale, ApplyXpRate(award, xpRate: 0.0), 6);
        Assert.Equal(award.Delta * award.PowerScale * 1.5, ApplyXpRate(award, xpRate: 0.5), 6);

        var registry = DerivedStatRegistry.CreateDefault();
        Assert.True(registry.TryGet(DerivedStatChannels.ProgressionXpRate, out var def));
        Assert.Equal(0.0, def.DefaultValue); // matches the "0 = no-op multiplier" contract above
        Assert.Null(def.Cap); // a rate, not a bounded ratio -- uncapped
        Assert.Null(def.Class); // non-combat (H.0's "Non-combat" row), same as progression.power/realm
    }

    [Fact]
    public void BreakthroughGrantsTheta()
    {
        // spec §4.2 -- breakthroughSuccess is a PROBABILITY only; what a success grants is Th
        // (progression.power), never a multiplier, and it must never be read as licence to un-pin
        // progression.realm from its permanent 1.0 (ADR P1). No roll/grant consumer is wired yet --
        // this proves the two channels stay structurally independent: breakthroughSuccess capped at
        // 1.0, realm's own definition completely untouched by this module.
        var registry = DerivedStatRegistry.CreateDefault();

        Assert.True(registry.TryGet(DerivedStatChannels.ProgressionBreakthroughSuccess, out var successDef));
        Assert.Equal(DerivedStatPolicy.BreakthroughSuccessCap, successDef.Cap);
        Assert.Equal(StatClass.Pool, successDef.Class);
        Assert.Null(successDef.CounterpartOf);

        Assert.True(registry.TryGet(DerivedStatChannels.ProgressionRealm, out var realmDef));
        Assert.Equal(DerivedComposeKind.FlatReplace, realmDef.Compose);
        Assert.Equal(1.0, realmDef.DefaultValue);
        Assert.Null(realmDef.Cap);
        Assert.Null(realmDef.Class);

        Assert.True(registry.TryGet(DerivedStatChannels.ProgressionPower, out var powerDef));
        Assert.NotEqual(powerDef.ChannelId, successDef.ChannelId);
    }

    [Fact]
    public void NoGoldensMove()
    {
        // spec §6 -- all 18 at defaults. Nothing reads any of them yet, so the full existing
        // scenario/golden suite passing unchanged (dotnet test tests\FusionRpg.Core.Tests, run
        // separately) is the systemic proof; this is the direct, targeted one: composing with zero
        // modifiers reproduces every documented default exactly.
        var registry = DerivedStatRegistry.CreateDefault();
        var composer = new DerivedComposer(registry);
        var snapshot = composer.Compose();

        foreach (var id in DerivedStatChannels.ResourceIds)
        {
            Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.ResourceMax(id)));
            Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.ResourceRegen(id)));
            Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.ResourceEfficiency(id)));
        }
        Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.MoveRange));
        Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.ProgressionXpRate));
        Assert.Equal(0.0, snapshot.Get(DerivedStatChannels.ProgressionBreakthroughSuccess));
    }
}
