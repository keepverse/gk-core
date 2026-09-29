using FusionRpg.Core.Delve.Supplies;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Supplies;

/// <summary>D3.25 (spec-supplies-and-objects.md §1) — `SupplyInstantiation.Concrete`. Fixture pattern
/// mirrors `InstantiatorTests.cs`'s/`DelveLootTests.cs`'s own `Container`/`Catalog`/`Tuning` shape.</summary>
public class SupplyInstantiationTests
{
    const int ThetaRoom = 20;
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);

    static SupplyInstantiationTests()
    {
        var id = AtomRow.DeriveId("atom.ration-restore", "", 1);
        Catalog[id] = new AtomRow
        {
            AtomId = id, KindId = "resource.delta", FamilyId = "atom.ration-restore", Variant = "", Tier = 1,
            ParamsJson = "{\"channel\":\"hunger\",\"op\":\"flat\",\"amount\":300}",
        };
    }

    static AtomRow? Lookup(string atomId) => Catalog.TryGetValue(atomId, out var a) ? a : null;
    static AffixRow? LookupAffix(string _) => null;

    // D27/F3 CLOSED 2026-09-08 (X7 "container-kind-expansion" landed 2026-09-07,
    // ConsumableDef.cs:230 ConsumableContainerKindAvailable = true): ContainerKind.Consumable is now
    // real, prefix "consumable" (ContainerRow.PrefixOf). SupplyInstantiation.Concrete itself never
    // branched on Kind (confirmed by reading it -- a plain pass-through to Instantiator.TryInstantiate),
    // so this fixture update is pure test-quality: proving the real, now-available kind works, rather
    // than perpetually exercising the ContainerKind.Item stand-in this file used before the kind existed.
    static ContainerRow Container() => new()
    {
        ContainerId = "consumable.ration",
        Kind = ContainerKind.Consumable,
        PrefixRolls = 0,
        Atoms = new List<ContainerAtomRow> { new(1, AtomRow.DeriveId("atom.ration-restore", "", 1)) },
        Pool = new List<ContainerPoolRow>(),
    };

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SupplyInstantiation.Concrete(null!, Lookup, LookupAffix, 1, "s", ThetaRoom, Tuning, 0, out _));
        Assert.Throws<ArgumentNullException>(() =>
            SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, 1, "s", ThetaRoom, null!, 0, out _));
    }

    [Fact]
    public void An_empty_stream_name_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, 1, "", ThetaRoom, Tuning, 0, out _));
    }

    [Fact]
    public void Produces_a_real_instantiated_instance_not_a_flat_literal()
    {
        var r = SupplyInstantiation.Concrete(
            Container(), Lookup, LookupAffix, delveSeed: 12345, SupplyStreams.Drop(2, 0, 0), ThetaRoom, Tuning, 0, out var instance);
        Assert.True(r.IsOk, r.ToString());
        Assert.NotNull(instance);
        Assert.Equal("consumable.ration", instance!.ContainerId);
        Assert.NotEqual(0L, instance.RollSeed);
    }

    [Fact]
    public void Reads_theta_room_not_a_fixed_pin()
    {
        SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, 12345, SupplyStreams.Drop(0, 0, 0), ThetaRoom, Tuning, 0, out var atRoom);
        SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, 12345, SupplyStreams.Drop(0, 0, 0), ThetaRoom + 100, Tuning, 0, out var atHigher);
        Assert.Equal(ThetaRoom, atRoom!.ThetaContent);
        Assert.Equal(ThetaRoom + 100, atHigher!.ThetaContent);
        Assert.NotEqual(atRoom.ContentFingerprint(), atHigher.ContentFingerprint()); // a different Theta -> a different fingerprint (§1's own reproducibility claim)
    }

    // ---- reproducibility: TryInstantiate twice over the same (container, revision, seed, Theta) ----

    [Fact]
    public void The_same_seed_and_stream_reproduce_an_identical_instance()
    {
        SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, 999, SupplyStreams.Drop(1, 2, 3), ThetaRoom, Tuning, 0, out var a);
        SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, 999, SupplyStreams.Drop(1, 2, 3), ThetaRoom, Tuning, 0, out var b);
        Assert.Equal(a!.ContentFingerprint(), b!.ContentFingerprint());
    }

    [Fact]
    public void Drop_and_entry_streams_never_collide_even_at_the_same_ordinal()
    {
        // Sampled across many delve seeds, matching this program's own established stream-namespacing
        // proof style (SlotFillTests, EventDrawTests, DelveLootTests).
        var dropSeeds = new List<long>();
        var entrySeeds = new List<long>();
        for (ulong seed = 0; seed < 20; seed++)
        {
            SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, seed, SupplyStreams.Drop(0, 0, 0), ThetaRoom, Tuning, 0, out var d);
            SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, seed, SupplyStreams.Entry(0), ThetaRoom, Tuning, 0, out var e);
            dropSeeds.Add(d!.RollSeed);
            entrySeeds.Add(e!.RollSeed);
        }
        Assert.NotEqual(dropSeeds, entrySeeds);
    }

    [Fact]
    public void A_different_ordinal_off_the_same_room_draws_on_its_own_stream()
    {
        var atN0 = new List<long>();
        var atN1 = new List<long>();
        for (ulong seed = 0; seed < 20; seed++)
        {
            SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, seed, SupplyStreams.Drop(3, 4, 0), ThetaRoom, Tuning, 0, out var i0);
            SupplyInstantiation.Concrete(Container(), Lookup, LookupAffix, seed, SupplyStreams.Drop(3, 4, 1), ThetaRoom, Tuning, 0, out var i1);
            atN0.Add(i0!.RollSeed);
            atN1.Add(i1!.RollSeed);
        }
        Assert.NotEqual(atN0, atN1);
    }

    [Fact]
    public void An_invalid_container_refusal_passes_through_unchanged()
    {
        var bad = Container() with { Atoms = new List<ContainerAtomRow> { new(1, "atom.does-not-exist") } };
        var r = SupplyInstantiation.Concrete(bad, Lookup, LookupAffix, 1, SupplyStreams.Drop(0, 0, 0), ThetaRoom, Tuning, 0, out var instance);
        Assert.False(r.IsOk);
        Assert.Null(instance);
    }

    // ---- SupplyStreams: the one owner of both formats ----

    [Fact]
    public void SupplyStreams_formats_match_the_specs_own_literal_shapes()
    {
        Assert.Equal("dungeon:supply:2:0:5", SupplyStreams.Drop(2, 0, 5));
        Assert.Equal("dungeon:supply:entry:3", SupplyStreams.Entry(3));
    }
}
