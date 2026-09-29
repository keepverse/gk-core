using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E5 acceptance (spec-container-schema.md). Same law as E4: a bad container is rejected <b>whole</b>,
/// with its id and reason, and does not enter the catalog.
///
/// <para>The rule doing the most work here is <c>pool_rolls ≤ distinct <b>drawable</b> groups</c>.
/// Counting groups whose every row is <c>weight = 0</c> passes validation and then silently
/// under-fills the instance — which is precisely the failure this program exists to remove.</para>
/// </summary>
public class ContainerValidatorTests
{
    // A tiny catalog stand-in: atom id -> (kindId, familyId, variant, tier).
    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);

    static ContainerValidatorTests()
    {
        void Add(string family, string variant, int tier, string kind = "stat.modify")
        {
            var id = AtomRow.DeriveId(family, variant, tier);
            Catalog[id] = new AtomRow
            {
                AtomId = id, KindId = kind, FamilyId = family, Variant = variant, Tier = tier,
                ParamsJson = kind == "stat.modify"
                    ? "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}"
                    : "{\"amount\":-120}",
            };
        }

        foreach (var t in new[] { 1, 2, 3 })
        {
            Add("atom.vitality", "", t);
            Add("atom.might", "", t);
            Add("atom.elemental-power", "fire", t, "resource.delta");
            Add("atom.elemental-power", "ice", t, "resource.delta");
        }
    }

    static AtomRow? Lookup(string id) => Catalog.TryGetValue(id, out var a) ? a : null;

    // T3.1 (affix-schema): the pool draws affix ids now. Every `P(id, ...)` fixture below names an
    // atom id directly, simulating `affix-library`'s (module 3, not yet built) 1:1 single-ref
    // generation — same id, one ref. T3.2's per-class drawable-group counting DOES read `Class` now,
    // so a fixed Prefix here means every fixture's `prefixRolls` exercises the prefix budget only —
    // `SuffixRolls` stays at its default 0 throughout this file, on purpose.
    static AffixRow? LookupAffix(string id) =>
        Catalog.TryGetValue(id, out var atom) ? new AffixRow(id, AffixClass.Prefix, new[] { new AffixRefRow(1, atom.AtomId) }) : null;

    static AtomRejection Check(ContainerRow c) => ContainerValidator.Validate(c, Lookup, LookupAffix);

    static ContainerRow Item(
        IEnumerable<ContainerAtomRow>? atoms = null,
        IEnumerable<ContainerPoolRow>? pool = null,
        int prefixRolls = 0,
        int? minTier = null,
        int? maxTier = null) => new()
    {
        ContainerId = "item.ember-band",
        Kind = ContainerKind.Item,
        PrefixRolls = prefixRolls,
        MinTier = minTier,
        MaxTier = maxTier,
        Atoms = (atoms ?? Array.Empty<ContainerAtomRow>()).ToList(),
        Pool = (pool ?? Array.Empty<ContainerPoolRow>()).ToList(),
    };

    static ContainerPoolRow P(string id, int weight, string? group = null) => new(id, weight, group);

    // ---- the fixed core -------------------------------------------------------------------------

    [Fact]
    public void A_plain_fixed_list_is_valid()
    {
        var c = Item(atoms: new[]
        {
            new ContainerAtomRow(1, "atom.vitality.t1"),
            new ContainerAtomRow(2, "atom.might.t2"),
        });

        Assert.True(Check(c).IsOk, Check(c).ToString());
    }

    [Fact]
    public void A_duplicate_seq_is_rejected_because_resolve_order_must_be_stable()
    {
        var c = Item(atoms: new[]
        {
            new ContainerAtomRow(1, "atom.vitality.t1"),
            new ContainerAtomRow(1, "atom.might.t1"),
        });

        Assert.Equal(AtomRejectionReason.DuplicateSeq, Check(c).Reason);
    }

    [Fact]
    public void An_unknown_atom_id_rejects_the_container_whole()
    {
        var c = Item(atoms: new[] { new ContainerAtomRow(1, "atom.nope.t1") });

        Assert.Equal(AtomRejectionReason.UnknownAtom, Check(c).Reason);
    }

    [Fact]
    public void The_same_atom_in_both_the_core_and_the_pool_is_rejected()
    {
        var c = Item(
            atoms: new[] { new ContainerAtomRow(1, "atom.vitality.t1") },
            pool: new[] { P("atom.vitality.t1", 10), P("atom.might.t1", 10) },
            prefixRolls: 1);

        Assert.Equal(AtomRejectionReason.DuplicateAtomInContainer, Check(c).Reason);
    }

    // ---- overrides are value specs ---------------------------------------------------------------

    [Fact]
    public void An_override_naming_a_param_the_kind_does_not_declare_is_rejected()
    {
        var c = Item(atoms: new[]
        {
            new ContainerAtomRow(1, "atom.vitality.t1", "{\"sparkle\":3}"),
        });

        Assert.Equal(AtomRejectionReason.UnknownParam, Check(c).Reason);
    }

    [Fact]
    public void An_override_changing_the_atoms_kind_is_rejected()
    {
        // An override tunes a value; it does not rewrite what the atom is.
        var c = Item(atoms: new[]
        {
            new ContainerAtomRow(1, "atom.vitality.t1", "{\"kind_id\":\"resource.delta\"}"),
        });

        Assert.Equal(AtomRejectionReason.OverrideChangesKind, Check(c).Reason);
    }

    [Fact]
    public void An_override_with_a_malformed_value_spec_is_rejected()
    {
        var c = Item(atoms: new[]
        {
            new ContainerAtomRow(1, "atom.vitality.t1", "{\"amount\":{\"min\":200,\"max\":100}}"),
        });

        Assert.Equal(AtomRejectionReason.BadValueSpec, Check(c).Reason);
    }

    [Fact]
    public void A_well_formed_override_is_accepted()
    {
        var c = Item(atoms: new[]
        {
            new ContainerAtomRow(1, "atom.vitality.t1",
                "{\"amount\":{\"min\":40,\"max\":60,\"roll\":\"onInstantiate\"}}"),
        });

        Assert.True(Check(c).IsOk, Check(c).ToString());
    }

    // ---- the pool ---------------------------------------------------------------------------------

    [Fact]
    public void A_negative_weight_is_rejected_not_clamped()
    {
        var c = Item(pool: new[] { P("atom.vitality.t1", -1) }, prefixRolls: 1);

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(c).Reason);
    }

    [Fact]
    public void A_zero_weight_row_is_kept_and_simply_never_drawn()
    {
        var c = Item(
            pool: new[] { P("atom.vitality.t1", 10), P("atom.might.t1", 0) },
            prefixRolls: 1);

        Assert.True(Check(c).IsOk, Check(c).ToString());
    }

    [Fact]
    public void A_pool_where_every_row_is_zero_weight_is_unsatisfiable()
    {
        var c = Item(pool: new[] { P("atom.vitality.t1", 0), P("atom.might.t1", 0) }, prefixRolls: 1);

        Assert.Equal(AtomRejectionReason.UnsatisfiablePool, Check(c).Reason);
    }

    [Fact]
    public void Pool_rolls_beyond_the_distinct_groups_is_rejected()
    {
        // Two groups, three draws: the one-per-group rule cannot be satisfied.
        var c = Item(
            pool: new[] { P("atom.vitality.t1", 10), P("atom.might.t1", 10) },
            prefixRolls: 3);

        Assert.Equal(AtomRejectionReason.PoolRollsExceedGroups, Check(c).Reason);
    }

    [Fact]
    public void Zero_weight_groups_do_not_count_toward_the_drawable_total()
    {
        // The spec's worked case: A(10), B(0), C(0) with pool_rolls = 3. Three groups exist and the
        // naive check passes -- then the draw yields one atom and the instance is silently short.
        var c = Item(
            pool: new[] { P("atom.vitality.t1", 10), P("atom.might.t1", 0), P("atom.elemental-power.fire.t1", 0) },
            prefixRolls: 3);

        Assert.Equal(AtomRejectionReason.PoolRollsExceedGroups, Check(c).Reason);
    }

    [Fact]
    public void Group_defaults_to_family_plus_variant_so_fire_and_ice_are_different_groups()
    {
        // Two variants of ONE family are two groups -- normal ARPG itemisation. Were the default
        // family_id alone, this container could only ever roll one of them.
        var c = Item(
            pool: new[] { P("atom.elemental-power.fire.t1", 10), P("atom.elemental-power.ice.t1", 10) },
            prefixRolls: 2);

        Assert.True(Check(c).IsOk, Check(c).ToString());
    }

    [Fact]
    public void Two_tiers_of_one_variant_share_a_group_so_two_draws_are_impossible()
    {
        var c = Item(
            pool: new[] { P("atom.elemental-power.fire.t1", 10), P("atom.elemental-power.fire.t2", 10) },
            prefixRolls: 2);

        Assert.Equal(AtomRejectionReason.PoolRollsExceedGroups, Check(c).Reason);
    }

    [Fact]
    public void An_explicit_group_overrides_the_default()
    {
        var c = Item(
            pool: new[]
            {
                P("atom.elemental-power.fire.t1", 10, "elements"),
                P("atom.elemental-power.ice.t1", 10, "elements"),
            },
            prefixRolls: 2);

        Assert.Equal(AtomRejectionReason.PoolRollsExceedGroups, Check(c).Reason);
    }

    [Fact]
    public void Pool_rolls_above_zero_needs_at_least_one_pool_row()
    {
        Assert.Equal(AtomRejectionReason.UnsatisfiablePool, Check(Item(prefixRolls: 2)).Reason);
    }

    [Fact]
    public void A_container_with_no_pool_rows_is_legal_when_pool_rolls_is_zero()
    {
        var c = Item(atoms: new[] { new ContainerAtomRow(1, "atom.vitality.t1") });

        Assert.True(Check(c).IsOk, Check(c).ToString());
    }

    // ---- the tier window --------------------------------------------------------------------------

    [Fact]
    public void A_pool_atom_outside_the_tier_window_is_rejected()
    {
        var c = Item(
            pool: new[] { P("atom.vitality.t3", 10) },
            prefixRolls: 1, minTier: 1, maxTier: 2);

        Assert.Equal(AtomRejectionReason.TierOutOfWindow, Check(c).Reason);
    }

    [Fact]
    public void A_pool_atom_inside_the_tier_window_is_accepted()
    {
        var c = Item(
            pool: new[] { P("atom.vitality.t2", 10) },
            prefixRolls: 1, minTier: 1, maxTier: 2);

        Assert.True(Check(c).IsOk, Check(c).ToString());
    }

    [Fact]
    public void The_tier_window_does_not_constrain_the_fixed_core()
    {
        // The window is what the POOL may offer. A trait's fixed core says what it is.
        var c = Item(
            atoms: new[] { new ContainerAtomRow(1, "atom.vitality.t3") },
            pool: new[] { P("atom.might.t1", 10) },
            prefixRolls: 1, minTier: 1, maxTier: 1);

        Assert.True(Check(c).IsOk, Check(c).ToString());
    }

    // ---- identity ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(ContainerKind.Item, "trait.wrong-prefix")]
    [InlineData(ContainerKind.Trait, "item.wrong-prefix")]
    [InlineData(ContainerKind.SpeciesPassive, "skill.wrong-prefix")]
    public void The_container_id_prefix_must_match_its_kind(ContainerKind kind, string id)
    {
        var c = Item(atoms: new[] { new ContainerAtomRow(1, "atom.vitality.t1") })
            with { Kind = kind, ContainerId = id };

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(c).Reason);
    }

    [Fact]
    public void Every_kind_has_a_prefix_and_a_valid_id_passes()
    {
        foreach (var kind in Enum.GetValues<ContainerKind>())
        {
            var prefix = ContainerRow.PrefixOf(kind);
            Assert.False(string.IsNullOrEmpty(prefix), kind.ToString());

            var c = Item(atoms: new[] { new ContainerAtomRow(1, "atom.vitality.t1") })
                with { Kind = kind, ContainerId = prefix + ".sample" };

            Assert.True(Check(c).IsOk, $"{kind}: {Check(c)}");
        }
    }

    [Fact]
    public void A_species_progression_container_passes_validation()
    {
        // species-progression SP3.2: SpeciesLayerProjector.ToContainer's own output shape — a
        // species-empire (2b) row is a `stat.derived` atom exactly like every other kind, so no new
        // validator rule is needed, only the new kind + prefix + id grammar (map C3).
        var c = Item(atoms: new[] { new ContainerAtomRow(1, "atom.vitality.t1") })
            with { Kind = ContainerKind.SpeciesProgression, ContainerId = "species-progression.peashooter" };

        Assert.True(Check(c).IsOk, Check(c).ToString());
    }

    [Theory]
    [InlineData(ContainerKind.SpeciesProgression, "species-passive.wrong-prefix")]
    [InlineData(ContainerKind.SpeciesPassive, "species-progression.wrong-prefix")]
    public void SpeciesPassive_and_SpeciesProgression_cannot_take_each_others_prefix(ContainerKind kind, string id)
    {
        // The two kinds are never interchangeable: `species-passive` stays frozen generator output,
        // `species-progression` is the projector's player/empire-mutable output. Neither id grammar
        // accepts the other's prefix, so a container can never silently carry 2b under the frozen kind.
        var c = Item(atoms: new[] { new ContainerAtomRow(1, "atom.vitality.t1") })
            with { Kind = kind, ContainerId = id };

        Assert.Equal(AtomRejectionReason.BadParamValue, Check(c).Reason);
    }

    [Fact]
    public void Group_keys_cannot_collide_across_family_and_variant_boundaries()
    {
        // family "atom.a" + variant "bc" vs family "atom.ab" + variant "c": concatenating without a
        // separator makes both "atom.abc", silently merging two families into one group.
        Catalog["atom.a.bc.t1"] = new AtomRow
        {
            AtomId = "atom.a.bc.t1", KindId = "stat.modify", FamilyId = "atom.a", Variant = "bc", Tier = 1,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        };
        Catalog["atom.ab.c.t1"] = new AtomRow
        {
            AtomId = "atom.ab.c.t1", KindId = "stat.modify", FamilyId = "atom.ab", Variant = "c", Tier = 1,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        };

        var c = Item(pool: new[] { P("atom.a.bc.t1", 10), P("atom.ab.c.t1", 10) }, prefixRolls: 2);

        Assert.True(Check(c).IsOk, "two distinct families must be two groups: " + Check(c));
    }
}
