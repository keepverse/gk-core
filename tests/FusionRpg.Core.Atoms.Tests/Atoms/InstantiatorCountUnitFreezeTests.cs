using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// D4.26 (spec-unique-pipeline.md §4): `Instantiator.Freeze` must not scale a count-unit channel.
/// `ContentScale.Apply(1, 4235)` is 4 — hand-verified directly against the real `ContentScale.Apply`
/// during this task's own research pass, not assumed from the spec's prose — and a slot count (e.g.
/// `loadout.slots`) must stay exactly 1 regardless of Θ. Fixture idiom mirrors `InstantiatorTests.cs`'s
/// own `Catalog`/`Lookup`/`AmountOf` shape, extended here because this file's own is the first
/// `stat.derived` fixture in this test class (`InstantiatorTests.cs`'s own fixtures are all
/// `stat.modify`).
/// </summary>
public class InstantiatorCountUnitFreezeTests
{
    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);

    static InstantiatorCountUnitFreezeTests()
    {
        void Add(string family, string kindId, string paramsJson)
        {
            var id = AtomRow.DeriveId(family, "", 1);
            Catalog[id] = new AtomRow { AtomId = id, KindId = kindId, FamilyId = family, Tier = 1, ParamsJson = paramsJson };
        }

        // The real shipped shape (gk-data/packs/fusion/data/seed/atoms/extend-slot.json), authored the idiomatic bare-int
        // way (matching trait-critical-hunter.json's own precedent, not the verbose {min,max,roll} form).
        Add("atom.extend-slot", "stat.derived", "{\"channel\":\"loadout.slots\",\"op\":\"flat\",\"amount\":1}");
        // A non-count stat.derived sibling, same shape, different channel — proves the guard is keyed
        // on the CHANNEL's own UnitClass, never on the kind id alone.
        Add("atom.probe-magnitude", "stat.derived", "{\"channel\":\"combat.power.omni\",\"op\":\"flat\",\"amount\":1}");
    }

    static AtomRow? Lookup(string id) => Catalog.TryGetValue(id, out var a) ? a : null;
    static AffixRow? LookupAffix(string id) => null; // every fixture here draws zero pool rolls

    static PowerTuning TuningAt(long bMilli) => PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, bMilli, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static ContainerRow Container(string atomId) => new()
    {
        ContainerId = "item.probe",
        Kind = ContainerKind.Item,
        Atoms = new List<ContainerAtomRow> { new(1, atomId) },
        Pool = new List<ContainerPoolRow>(),
    };

    static int AmountOf(InstanceAtomRow row)
    {
        using var doc = JsonDocument.Parse(row.ValuesJson);
        return doc.RootElement.GetProperty("amount").GetInt32();
    }

    /// <summary>The todo's own literal Verify line.</summary>
    [Fact]
    public void A_count_unit_channel_stays_one_at_theta_100_where_a_magnitude_would_have_scaled()
    {
        var tuning = TuningAt(400);
        var scaleMilli = ContentScale.Milli(100, tuning);
        Assert.NotEqual(1000, scaleMilli); // sanity: this (Θ, B) genuinely scales something here

        var rejection = Instantiator.TryInstantiate(
            Container(AtomRow.DeriveId("atom.extend-slot", "", 1)), Lookup, LookupAffix,
            rollSeed: 1, thetaContent: 100, tuning, out var instance);

        Assert.True(rejection.IsOk, rejection.Detail);
        Assert.Equal(1, AmountOf(instance!.Atoms[0]));
    }

    [Fact]
    public void A_non_count_stat_derived_channel_still_scales_normally_at_the_same_theta()
    {
        var tuning = TuningAt(400);
        var scaleMilli = ContentScale.Milli(100, tuning);
        var expected = (int)ContentScale.Apply(1, scaleMilli);
        Assert.NotEqual(1, expected); // sanity: the comparison sibling actually moves at this Θ

        var rejection = Instantiator.TryInstantiate(
            Container(AtomRow.DeriveId("atom.probe-magnitude", "", 1)), Lookup, LookupAffix,
            rollSeed: 1, thetaContent: 100, tuning, out var instance);

        Assert.True(rejection.IsOk, rejection.Detail);
        Assert.Equal(expected, AmountOf(instance!.Atoms[0]));
    }

    /// <summary>The Verify line's other half: a malformed count atom (rolled/ranged rather than a
    /// single fixed value) refuses rather than guessing which unscaled value would have been right.</summary>
    [Fact]
    public void A_ranged_or_rolled_count_channel_refuses_rather_than_ship_a_guess()
    {
        const string badId = "atom.bad-count.t1";
        Catalog[badId] = new AtomRow
        {
            AtomId = badId, KindId = "stat.derived", FamilyId = "atom.bad-count", Tier = 1,
            ParamsJson = "{\"channel\":\"loadout.slots\",\"op\":\"flat\",\"amount\":{\"min\":1,\"max\":5,\"roll\":\"onInstantiate\"}}",
        };

        var rejection = Instantiator.TryInstantiate(
            Container(badId), Lookup, LookupAffix, rollSeed: 1, thetaContent: 20, TuningAt(0), out var instance);

        Assert.False(rejection.IsOk);
        Assert.Null(instance);
        Assert.Contains("atom.count-scaled", rejection.Detail);
    }
}
