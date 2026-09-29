using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E6 roll moment 2. The claim under test: the same
/// <c>(container, catalog_revision, roll_seed, theta_content)</c> reproduces an instance
/// byte-identically — over the atom set and frozen values, <b>excluding</b> the generated
/// `instance_id` and `created_utc`.
/// </summary>
public class InstantiatorTests
{
    // T3.4 (content-scale): PinTheta (20) is the pin — contentScale(20) == 1.000 exactly — so every
    // pre-T3.4 test in this file keeps asserting its original, pre-scaling values unchanged by
    // routing through Make()'s default. Tests that care about scaling pass a different theta directly.
    const int PinTheta = 20;
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);

    static InstantiatorTests()
    {
        void Add(string family, string variant, int tier, string paramsJson)
        {
            var id = AtomRow.DeriveId(family, variant, tier);
            Catalog[id] = new AtomRow
            {
                AtomId = id, KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier,
                ParamsJson = paramsJson,
            };
        }

        // A fixed magnitude, an OnInstantiate range, and an OnApply range — one of each roll moment.
        Add("atom.vitality", "", 1, "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}");
        Add("atom.might", "", 1,
            "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onInstantiate\"}}");
        Add("atom.surge", "", 1,
            "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{\"min\":100,\"max\":200,\"roll\":\"onApply\"}}");

        foreach (var v in new[] { "fire", "ice", "air" })
            for (var t = 1; t <= 2; t++)
                Add("atom.elemental-power", v, t, "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":5}");
    }

    static AtomRow? Lookup(string id) => Catalog.TryGetValue(id, out var a) ? a : null;

    // T3.1 (affix-schema): the pool draws affix ids now. Every pool fixture in this file names an
    // atom id directly, simulating `affix-library`'s (module 3, not yet built) 1:1 single-ref
    // generation — same id, one ref, class derived from the wrapped atom's kind.
    static AffixRow? LookupAffix(string id) =>
        Catalog.TryGetValue(id, out var atom) ? new AffixRow(id, AffixClass.Prefix, new[] { new AffixRefRow(1, atom.AtomId) }) : null;

    static InstanceRow Make(ContainerRow c, long seed, int thetaContent = PinTheta)
    {
        var r = Instantiator.TryInstantiate(c, Lookup, LookupAffix, seed, thetaContent, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return inst!;
    }

    static ContainerRow Container(
        IEnumerable<ContainerAtomRow>? atoms = null,
        IEnumerable<ContainerPoolRow>? pool = null,
        int prefixRolls = 0) => new()
    {
        ContainerId = "item.ember-band",
        Kind = ContainerKind.Item,
        PrefixRolls = prefixRolls,
        Atoms = (atoms ?? Array.Empty<ContainerAtomRow>()).ToList(),
        Pool = (pool ?? Array.Empty<ContainerPoolRow>()).ToList(),
    };

    static int AmountOf(InstanceAtomRow row)
    {
        using var doc = JsonDocument.Parse(row.ValuesJson);
        return doc.RootElement.GetProperty("amount").GetInt32();
    }

    // ---- reproducibility ---------------------------------------------------------------------------

    [Fact]
    public void The_same_container_and_seed_reproduce_an_identical_instance()
    {
        var c = Container(
            atoms: new[] { new ContainerAtomRow(1, "atom.might.t1") },
            pool: new[]
            {
                new ContainerPoolRow("atom.elemental-power.fire.t1", 10),
                new ContainerPoolRow("atom.elemental-power.ice.t1", 10),
            },
            prefixRolls: 1);

        Assert.Equal(Make(c, 4242).ContentFingerprint(), Make(c, 4242).ContentFingerprint());
    }

    [Fact]
    public void Different_seeds_produce_different_instances()
    {
        var c = Container(
            pool: new[]
            {
                new ContainerPoolRow("atom.elemental-power.fire.t1", 10),
                new ContainerPoolRow("atom.elemental-power.ice.t1", 10),
                new ContainerPoolRow("atom.elemental-power.air.t1", 10),
            },
            prefixRolls: 1);

        var seen = new HashSet<string>();
        for (long seed = 0; seed < 60; seed++) seen.Add(Make(c, seed).Atoms[0].AtomId);

        Assert.True(seen.Count > 1, "every seed drew the same atom");
    }

    [Fact]
    public void Power_is_null_at_instantiate_because_E9_lands_later()
    {
        var inst = Make(Container(atoms: new[] { new ContainerAtomRow(1, "atom.vitality.t1") }), 1);

        Assert.All(inst.Atoms, a => Assert.Null(a.PowerJson));
    }

    // ---- the three roll moments ---------------------------------------------------------------------

    [Fact]
    public void A_fixed_value_is_copied_verbatim()
    {
        var inst = Make(Container(atoms: new[] { new ContainerAtomRow(1, "atom.vitality.t1") }), 7);

        Assert.Equal(45, AmountOf(inst.Atoms[0]));
    }

    [Fact]
    public void An_OnInstantiate_value_is_frozen_inside_its_range()
    {
        for (long seed = 0; seed < 40; seed++)
        {
            var inst = Make(Container(atoms: new[] { new ContainerAtomRow(1, "atom.might.t1") }), seed);
            Assert.InRange(AmountOf(inst.Atoms[0]), 10, 20);
        }
    }

    [Fact]
    public void An_OnApply_value_is_left_unresolved_because_it_belongs_to_the_hit()
    {
        var inst = Make(Container(atoms: new[] { new ContainerAtomRow(1, "atom.surge.t1") }), 3);

        using var doc = JsonDocument.Parse(inst.Atoms[0].ValuesJson);
        var amount = doc.RootElement.GetProperty("amount");

        // Still an object with its bounds, not a number.
        Assert.Equal(JsonValueKind.Object, amount.ValueKind);
        Assert.Equal(100, amount.GetProperty("min").GetInt32());
        Assert.Equal(200, amount.GetProperty("max").GetInt32());
    }

    [Fact]
    public void An_override_is_what_gets_frozen()
    {
        var inst = Make(Container(atoms: new[]
        {
            new ContainerAtomRow(1, "atom.might.t1",
                "{\"amount\":{\"min\":90,\"max\":99,\"roll\":\"onInstantiate\"}}"),
        }), 11);

        Assert.InRange(AmountOf(inst.Atoms[0]), 90, 99);
    }

    // ---- the draw ------------------------------------------------------------------------------------

    [Fact]
    public void A_zero_weight_row_is_never_drawn()
    {
        var c = Container(
            pool: new[]
            {
                new ContainerPoolRow("atom.elemental-power.fire.t1", 10),
                new ContainerPoolRow("atom.elemental-power.ice.t1", 0),
            },
            prefixRolls: 1);

        for (long seed = 0; seed < 200; seed++)
            Assert.Equal("atom.elemental-power.fire.t1", Make(c, seed).Atoms[0].AtomId);
    }

    [Fact]
    public void At_most_one_atom_per_group_is_drawn()
    {
        // Two tiers of ONE variant share a group, so a two-roll container can only take one of them
        // plus something from another group.
        var c = Container(
            pool: new[]
            {
                new ContainerPoolRow("atom.elemental-power.fire.t1", 10),
                new ContainerPoolRow("atom.elemental-power.fire.t2", 10),
                new ContainerPoolRow("atom.elemental-power.ice.t1", 10),
            },
            prefixRolls: 2);

        for (long seed = 0; seed < 100; seed++)
        {
            var drawn = Make(c, seed).Atoms.Select(a => a.AtomId).ToList();
            var fire = drawn.Count(id => id.Contains(".fire."));
            Assert.True(fire <= 1, $"seed {seed} drew {fire} fire atoms: {string.Join(", ", drawn)}");
        }
    }

    [Fact]
    public void The_draw_respects_weights_with_exact_counts_for_a_fixed_seed_sequence()
    {
        // Exact counts, not a tolerance: a tolerance on a seeded test is an invitation to widen it.
        var c = Container(
            pool: new[]
            {
                new ContainerPoolRow("atom.elemental-power.fire.t1", 90),
                new ContainerPoolRow("atom.elemental-power.ice.t1", 10),
            },
            prefixRolls: 1);

        var fire = 0;
        for (long seed = 0; seed < 1000; seed++)
            if (Make(c, seed).Atoms[0].AtomId.Contains(".fire.")) fire++;

        // Pinned to this implementation and seed sequence; a change to either must be deliberate.
        // 90% weight over 1000 seeds. Pinned to this RNG and draw order: a change to either must
        // be a deliberate edit here, not a silently shifted distribution.
        //
        // 908 -> 903 (T3.2, prefix/suffix split): Draw()'s RNG stream is now named per-budget
        // (`atom.pool.prefix.<id>` instead of `atom.pool.<id>`), so the prefix draw's own sequence
        // shifted even though this fixture's single `prefixRolls: 1` budget and 90/10 weights are
        // otherwise unchanged — a deliberate, expected re-pin, not a distribution regression.
        Assert.Equal(903, fire);
    }

    [Fact]
    public void The_fixed_core_comes_first_and_drawn_atoms_continue_the_numbering()
    {
        var c = Container(
            atoms: new[]
            {
                new ContainerAtomRow(1, "atom.vitality.t1"),
                new ContainerAtomRow(2, "atom.might.t1"),
            },
            pool: new[] { new ContainerPoolRow("atom.elemental-power.fire.t1", 10) },
            prefixRolls: 1);

        var inst = Make(c, 5);

        Assert.Equal(new[] { 1, 2, 3 }, inst.Atoms.Select(a => a.Seq));
        Assert.Equal("atom.vitality.t1", inst.Atoms[0].AtomId);
        Assert.Equal("atom.elemental-power.fire.t1", inst.Atoms[2].AtomId);
    }

    [Fact]
    public void An_invalid_container_never_instantiates()
    {
        var c = Container(atoms: new[] { new ContainerAtomRow(1, "atom.nope.t1") });

        var r = Instantiator.TryInstantiate(c, Lookup, LookupAffix, 1, PinTheta, Tuning, out var inst);

        Assert.Equal(AtomRejectionReason.UnknownAtom, r.Reason);
        Assert.Null(inst);
    }

    [Fact]
    public void Two_containers_rolled_from_one_seed_do_not_share_a_sequence()
    {
        var a = Container(atoms: new[] { new ContainerAtomRow(1, "atom.might.t1") });
        var b = a with { ContainerId = "item.other-band" };

        // Same seed, different container: the stream is named per container, so the frozen values
        // are independent rather than accidentally identical.
        Assert.NotEqual(Make(a, 99).ContentFingerprint(), Make(b, 99).ContentFingerprint());
    }
}
