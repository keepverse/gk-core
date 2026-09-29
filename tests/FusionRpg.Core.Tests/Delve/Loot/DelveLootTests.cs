using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Loot;

/// <summary>D3.15 (spec-dungeon-loot.md §3's own "wiring gaps" table, row 1) —
/// `InstantiateBossFirstClearGrant`. Fixture pattern mirrors `InstantiatorTests.cs`'s own
/// `Container`/`Catalog`/`Tuning` shape exactly.</summary>
public class DelveLootTests
{
    const int Pin = 20;
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);

    static DelveLootTests()
    {
        var id = AtomRow.DeriveId("atom.vitality", "", 1);
        Catalog[id] = new AtomRow
        {
            AtomId = id, KindId = "stat.modify", FamilyId = "atom.vitality", Variant = "", Tier = 1,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        };
    }

    static AtomRow? Lookup(string atomId) => Catalog.TryGetValue(atomId, out var a) ? a : null;
    static AffixRow? LookupAffix(string _) => null;

    static ContainerRow Container() => new()
    {
        ContainerId = "item.boss-relic",
        Kind = ContainerKind.Item,
        PrefixRolls = 0,
        Atoms = new List<ContainerAtomRow> { new(1, AtomRow.DeriveId("atom.vitality", "", 1)) },
        Pool = new List<ContainerPoolRow>(),
    };

    // ---- argument validation ----

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            DelveLoot.InstantiateBossFirstClearGrant(null!, Lookup, LookupAffix, 1, 0, Pin, Tuning, 0, out _));
        Assert.Throws<ArgumentNullException>(() =>
            DelveLoot.InstantiateBossFirstClearGrant(Container(), Lookup, LookupAffix, 1, 0, Pin, null!, 0, out _));
    }

    // ---- "never flat": a real, rolled instance, not a bare literal ----

    [Fact]
    public void Produces_a_real_instantiated_instance_not_a_flat_literal()
    {
        var r = DelveLoot.InstantiateBossFirstClearGrant(
            Container(), Lookup, LookupAffix, lootSeed: 12345, grantIndex: 0, thetaBoss: Pin, Tuning, catalogRevision: 1, out var instance);
        Assert.True(r.IsOk, r.ToString());
        Assert.NotNull(instance);
        Assert.Equal("item.boss-relic", instance!.ContainerId);
        Assert.NotEqual(0L, instance.RollSeed); // "never flat" -- RollSeed 0 was the exact gap this replaces
        Assert.Single(instance.Atoms);
    }

    [Fact]
    public void Reads_theta_boss_not_a_fixed_pin()
    {
        var atPin = DelveLoot.InstantiateBossFirstClearGrant(
            Container(), Lookup, LookupAffix, 12345, 0, Pin, Tuning, 0, out var instAtPin);
        var atHigher = DelveLoot.InstantiateBossFirstClearGrant(
            Container(), Lookup, LookupAffix, 12345, 0, Pin + 100, Tuning, 0, out var instAtHigher);
        Assert.True(atPin.IsOk);
        Assert.True(atHigher.IsOk);
        Assert.Equal(Pin, instAtPin!.ThetaContent);
        Assert.Equal(Pin + 100, instAtHigher!.ThetaContent);
    }

    [Fact]
    public void Origin_defaults_to_Drop_the_specs_own_stated_v1_permission()
    {
        var r = DelveLoot.InstantiateBossFirstClearGrant(
            Container(), Lookup, LookupAffix, 12345, 0, Pin, Tuning, 0, out var instance);
        Assert.True(r.IsOk);
        Assert.Equal(InstanceOrigin.Drop, instance!.Origin);
    }

    // ---- determinism and stream namespacing (the grant's own index) ----

    [Fact]
    public void The_same_seed_and_grant_index_reproduce_an_identical_instance()
    {
        DelveLoot.InstantiateBossFirstClearGrant(Container(), Lookup, LookupAffix, 999, 2, Pin, Tuning, 0, out var a);
        DelveLoot.InstantiateBossFirstClearGrant(Container(), Lookup, LookupAffix, 999, 2, Pin, Tuning, 0, out var b);
        Assert.Equal(a!.ContentFingerprint(), b!.ContentFingerprint());
    }

    [Fact]
    public void A_different_grant_index_off_the_same_loot_seed_draws_on_its_own_stream()
    {
        // LootStreams.RollSeed(index) namespaces the draw per grant index -- proven the same way
        // SlotFillTests/EventDrawTests prove stream namespacing elsewhere in this program: sampled
        // across many loot seeds, the two indices must disagree at least once.
        var rollSeedsAt0 = new List<long>();
        var rollSeedsAt1 = new List<long>();
        for (ulong seed = 0; seed < 20; seed++)
        {
            DelveLoot.InstantiateBossFirstClearGrant(Container(), Lookup, LookupAffix, seed, 0, Pin, Tuning, 0, out var i0);
            DelveLoot.InstantiateBossFirstClearGrant(Container(), Lookup, LookupAffix, seed, 1, Pin, Tuning, 0, out var i1);
            rollSeedsAt0.Add(i0!.RollSeed);
            rollSeedsAt1.Add(i1!.RollSeed);
        }
        Assert.NotEqual(rollSeedsAt0, rollSeedsAt1);
    }

    // ---- refusals pass through ----

    [Fact]
    public void An_invalid_container_refusal_passes_through_unchanged()
    {
        var badContainer = Container() with
        {
            Atoms = new List<ContainerAtomRow> { new(1, "atom.does-not-exist") },
        };
        var r = DelveLoot.InstantiateBossFirstClearGrant(
            badContainer, Lookup, LookupAffix, 1, 0, Pin, Tuning, 0, out var instance);
        Assert.False(r.IsOk);
        Assert.Null(instance);
    }
}
