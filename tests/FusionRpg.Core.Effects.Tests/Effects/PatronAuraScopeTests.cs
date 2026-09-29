using FusionRpg.Contracts;
using FusionRpg.Core.Creatures.Patron;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

// `ActorHub` is also a namespace name under FusionRpg.Core.Tests (ActorHubTests' own), so the type
// needs an alias here rather than a bare identifier.
using ActorHubType = FusionRpg.Core.Stats.Derived.ActorHub;

// Same namespace as the folder's other files (NOT `…Tests.Effects`: that name is already a
// resolution path `TreeAtomSourceTests` and others depend on — see OverlayFilterInstakillTests' own
// note on the same trap).
namespace FusionRpg.Core.Tests;

/// <summary>
/// The patron aura's SCOPE — the defect creature-standalone PT7 found live: the aura is plant-side only
/// (spec-patron-creature.md:27) but its grant asked for <c>match</c>, so the identical
/// <c>combat.power.fire</c> magnitude came back on the ZOMBIE side too. The aura was buffing the enemy.
///
/// <para><b>These tests enter through the real pipeline, not the gate in isolation.</b> A patron-shaped
/// grant is put in the production grant store (<see cref="InMemoryEffectGrantStore"/>) with a def
/// compiled by <c>AtomCompiler</c> and converted by the same codec the injector receiver uses, then read
/// back through <see cref="GrantedDerivedAtomReader"/> — the exact chain a live lawn actor's derived
/// channel comes from (<c>GrantedDerivedAtoms.For</c> → <c>AtomDerivedSubsystem</c> → <c>ActorHub</c>).
/// A test against <c>StatApplyScope.Matches</c> alone would NOT have caught this defect: the aura never
/// goes through that gate, it goes through the grant-store lookup.</para>
/// </summary>
public class PatronAuraScopeTests
{
    const string EffectId = "fx.patron_aura";
    const string Channel = "combat.power.fire";
    const long PowerMilli = 47;

    static StatContext Plant(int typeId = 0) =>
        new() { Side = StatSide.Plant, TypeId = typeId, EntityKey = "P1" };

    static StatContext Zombie(int typeId = 0) =>
        new() { Side = StatSide.Zombie, TypeId = typeId, EntityKey = "Z1" };

    /// <summary>
    /// The REAL transport shape, built the way production builds it: the seed's own
    /// <c>stat.derived</c> atom (<c>gk-data/packs/fusion/data/seed/atoms/patron-aura.json</c>: channel/op/amount, icdKey
    /// <c>fx.patron_aura</c>) through <see cref="AtomCompiler"/>, then
    /// <see cref="AtomPushCodec.ToDef"/> — the same conversion the injector's push receiver performs.
    /// Nothing here hand-writes a <c>ModifyDerivedStat</c> row.
    /// </summary>
    static EffectDef AuraDef()
    {
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.patron-aura-power", "fire", 1),
            KindId = "stat.derived",
            FamilyId = "atom.patron-aura-power",
            Variant = "fire",
            Tier = 1,
            Name = "Patron aura power (fire)",
            IcdKey = EffectId,
            ParamsJson = "{\"channel\":\"" + Channel + "\",\"op\":\"flat\",\"amount\":" + PowerMilli + "}",
            WhenJson = "{}",
        };

        var def = Assert.Single(AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1).Defs);
        Assert.Equal(EffectId, def.EffectId);
        return AtomPushCodec.ToDef(def);
    }

    static InMemoryEffectCatalog Catalog()
    {
        var catalog = new InMemoryEffectCatalog();
        catalog.Upsert(AuraDef());
        return catalog;
    }

    static InMemoryEffectGrantStore Store(string ownerKind, string ownerKey, string grantId = "patron:aura")
    {
        var store = new InMemoryEffectGrantStore();
        store.Upsert(new EffectGrant
        {
            GrantId = grantId,
            EffectId = EffectId,
            OwnerKind = ownerKind,
            OwnerKey = ownerKey,
            PluginId = "sec.patron.aura",
        });
        return store;
    }

    static ActorHubType Hub(InMemoryEffectGrantStore store, InMemoryEffectCatalog catalog)
    {
        var hub = new ActorHubType(StatSystemBootstrap.CreateDefault());
        hub.Register(new AtomDerivedSubsystem(ctx => GrantedDerivedAtomReader.Read(store, catalog, ctx)));
        return hub;
    }

    // ── the defect: plant-side only ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_patron_shaped_grant_reaches_plant_reads_of_any_type_id()
    {
        var store = Store(OwnerScope.Name(OwnerKind.Plant), EffectOwnerKey.PlantSide);
        var catalog = Catalog();

        foreach (var typeId in new[] { 0, 3, 999 })
        {
            var atom = Assert.Single(GrantedDerivedAtomReader.Read(store, catalog, Plant(typeId)));
            Assert.Equal(Channel, atom.Channel);
            Assert.Equal(DerivedModifierOp.Flat, atom.Op);
            Assert.Equal(PowerMilli, atom.Amount);
        }
    }

    /// <summary>
    /// ⭐ The case that would have caught PT7. Same grant, same def, a zombie read — nothing.
    /// </summary>
    [Fact]
    public void A_patron_shaped_grant_does_not_apply_on_the_zombie_side()
    {
        var store = Store(OwnerScope.Name(OwnerKind.Plant), EffectOwnerKey.PlantSide);
        var catalog = Catalog();

        Assert.Empty(GrantedDerivedAtomReader.Read(store, catalog, Zombie(0)));
        Assert.Empty(GrantedDerivedAtomReader.Read(store, catalog, Zombie(3)));

        // ...and the zombie is not merely missing the atom: the COMPOSED channel is zero, which is what
        // the live probe read as 47 on both sides.
        var hub = Hub(store, catalog);
        Assert.Equal(PowerMilli, hub.Resolve(Plant()).Derived.Get(Channel, 0), 6);
        Assert.Equal(0, hub.Resolve(Zombie()).Derived.Get(Channel, 0), 6);
    }

    [Fact]
    public void A_zombie_side_wide_grant_does_not_reach_a_plant()
    {
        var store = Store(OwnerScope.Name(OwnerKind.Zombie), EffectOwnerKey.ZombieSide);

        Assert.Empty(GrantedDerivedAtomReader.Read(store, Catalog(), Plant()));
        Assert.Single(GrantedDerivedAtomReader.Read(store, Catalog(), Zombie()));
    }

    // ── non-regression: the shipped scopes are untouched ─────────────────────────────────────────

    [Fact]
    public void A_match_scoped_grant_of_the_same_effect_still_reaches_both_sides()
    {
        var store = Store("match", EffectOwnerKeys.Match, grantId: "match:aura");
        var catalog = Catalog();

        Assert.Equal(PowerMilli, Assert.Single(GrantedDerivedAtomReader.Read(store, catalog, Plant())).Amount);
        Assert.Equal(PowerMilli, Assert.Single(GrantedDerivedAtomReader.Read(store, catalog, Zombie())).Amount);
    }

    [Fact]
    public void A_type_keyed_grant_is_not_widened_to_the_whole_side()
    {
        var store = Store(OwnerScope.Name(OwnerKind.Plant), EffectOwnerKeys.PlantType(5), grantId: "plant:5");

        Assert.Single(GrantedDerivedAtomReader.Read(store, Catalog(), Plant(5)));
        Assert.Empty(GrantedDerivedAtomReader.Read(store, Catalog(), Plant(6)));
        Assert.Empty(GrantedDerivedAtomReader.Read(store, Catalog(), Zombie(5)));
    }

    // ── the producer and the withdraw: the key has to survive a whole match ──────────────────────

    [Fact]
    public void The_patron_plugin_grants_the_side_wide_plant_key_at_match_start()
    {
        PatronRuntimeState.Set(7, new PatronAura("fire", null, PowerMilli, 23, 0, 0));
        try
        {
            var host = new SimEffectHost(catalog: new[] { AuraDef() });
            host.BeginMatch("m-patron-scope");

            var grant = Assert.Single(host.Snapshot().Grants, g => g.GrantId == "patron:aura");
            Assert.Equal(EffectOwnerKey.PlantSide, grant.OwnerKey);
            // The ownerKind must name the same side: the reader looks a plant up as ("plant", "plant:N")
            // and ForOwner filters on both fields, so a `plant:*` key under ownerKind `match` would be
            // found by neither query.
            Assert.Equal(OwnerScope.Name(OwnerKind.Plant), grant.OwnerKind);
        }
        finally
        {
            PatronRuntimeState.Set(0, null);
        }
    }

    /// <summary>
    /// board.end must still take the grant away. <c>OnRemoved</c> passes no owner key, and the withdraw
    /// used to default that to the literal <c>match</c> scope — which a <c>plant:*</c> grant is not in,
    /// so the aura would have outlived the match it was granted for.
    ///
    /// <para>Deliberately <c>NotifyRemoved</c> alone, NOT <c>SimEffectHost.EndMatch</c>: that also calls
    /// <c>ClearAll</c>, which empties the bag whatever the plugin did — the test would have passed on a
    /// withdrawal that never ran. (Measured, not assumed: it did pass against a reverted withdraw.)</para>
    /// </summary>
    [Fact]
    public void Board_end_withdraws_the_side_wide_grant()
    {
        PatronRuntimeState.Set(7, new PatronAura("fire", null, PowerMilli, 23, 0, 0));
        try
        {
            var host = new SimEffectHost(catalog: new[] { AuraDef() });
            host.BeginMatch("m-patron-withdraw");
            Assert.True(host.Bag.HasGrantForEffect(EffectId));

            host.Plugins.NotifyRemoved(host.MatchKey);
            Assert.False(host.Bag.HasGrantForEffect(EffectId));
        }
        finally
        {
            PatronRuntimeState.Set(0, null);
        }
    }
}
