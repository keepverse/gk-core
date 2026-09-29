using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `LootMintAt.Mint` — the real, missing implementation behind `LootContentView.Mint` /
/// `DelveLoot.RollRoom`'s and `.RollQuestReward`'s own `mintAt` parameter (party-dungeon-todo.md
/// D4.12/D3.11/D3.3-D3.5, `[[loot-content-view-unwired]]`). Proves: all seven excluded
/// <see cref="DropEntryKind"/> members refuse by name (a defensive completeness property of this
/// function's own public contract — see the class doc comment on <see cref="LootMintAt"/> for why the
/// real, unmodified `LootPipeline.Resolve` cannot actually reach these branches today); the Equipment,
/// Unique and Relic arms each really call the real <see cref="Instantiator.TryInstantiate"/>,
/// deterministically, reading the grant's own `RollSeed`; a missing optional lookup refuses rather
/// than null-refs.
/// </summary>
public class LootMintAtTests
{
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    static AtomRow Atom(string familyId, string variant, int tier) => new()
    {
        AtomId = AtomRow.DeriveId(familyId, variant, tier),
        KindId = "stat.modify",
        FamilyId = familyId,
        Variant = variant,
        Tier = tier,
        Name = familyId,
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
    };

    static LootGrant Grant(DropEntryKind kind, int index = 0, string refId = "", ulong rollSeed = 12345) => new(
        index, kind, refId, Count: 1, AffixChannel: AffixChannels.Drop,
        BaseTypeId: kind == DropEntryKind.Equipment ? "item.humanoid-feet-a-001" : null,
        Frame: kind == DropEntryKind.Equipment ? "humanoid" : null,
        Role: kind == DropEntryKind.Equipment ? "footing" : null,
        MinTier: 1, MaxTier: 1, PrefixRolls: kind == DropEntryKind.Equipment ? 1 : 0, RollSeed: rollSeed);

    static LootMintLookups EmptyLookups() => new(
        LookupAtom: _ => null, LookupAffix: _ => null, Equipment: null, ContainerFor: null);

    // ---- the seven unsupported kinds refuse by name ---------------------------------------------------

    [Theory]
    [InlineData(DropEntryKind.Material)]
    [InlineData(DropEntryKind.Currency)]
    [InlineData(DropEntryKind.Insert)]
    [InlineData(DropEntryKind.Charm)]
    [InlineData(DropEntryKind.Consumable)]
    [InlineData(DropEntryKind.Table)]
    [InlineData(DropEntryKind.Nothing)]
    public void Every_unsupported_kind_refuses_by_name_never_a_crash_or_a_silent_mishandle(DropEntryKind kind)
    {
        var grant = Grant(kind, index: 42);

        var rejection = LootMintAt.Mint(grant, thetaContent: 20, EmptyLookups(), Tuning, out var instance);

        Assert.False(rejection.IsOk);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, rejection.Reason);
        Assert.StartsWith("drop.mint-kind-unsupported:", rejection.Detail, StringComparison.Ordinal);
        Assert.Contains("grant 42", rejection.Detail);
        Assert.Contains(kind.ToString(), rejection.Detail);
        Assert.Null(instance);
    }

    [Fact]
    public void All_ten_DropEntryKind_members_are_accounted_for_by_the_seven_theory_cases_plus_the_three_built_arms()
    {
        // A closed-list guard: if an eleventh DropEntryKind is ever added, this fails loudly rather
        // than silently falling through the dispatcher's own `default` arm untested. Ten is the
        // reviewed growth (empire-development Task 1.3a adds Relic, spec-relic-item-kind.md §Design 2).
        var all = Enum.GetValues<DropEntryKind>();
        Assert.Equal(10, all.Length);
        var covered = new[]
        {
            DropEntryKind.Equipment, DropEntryKind.Unique, DropEntryKind.Relic, DropEntryKind.Material, DropEntryKind.Currency,
            DropEntryKind.Insert, DropEntryKind.Charm, DropEntryKind.Consumable, DropEntryKind.Table, DropEntryKind.Nothing,
        };
        Assert.Equal(all.OrderBy(k => (int)k), covered.OrderBy(k => (int)k));
    }

    // ---- Equipment ------------------------------------------------------------------------------------

    [Fact]
    public void Equipment_with_no_EquipmentContainerLookups_refuses_by_name()
    {
        var grant = Grant(DropEntryKind.Equipment);
        var rejection = LootMintAt.Mint(grant, 20, EmptyLookups(), Tuning, out var instance);

        Assert.False(rejection.IsOk);
        Assert.Contains("drop.mint-kind-unsupported", rejection.Detail);
        Assert.Contains("Equipment", rejection.Detail);
        Assert.Null(instance);
    }

    [Fact]
    public void Equipment_mints_through_the_real_Instantiator()
    {
        var cells = new[] { new RoleFamilyCell("footing", "humanoid", "atom.vigor", MaxTier: 3) };
        var atomsById = new[] { Atom("atom.vigor", "", 1), Atom("atom.vigor", "", 2), Atom("atom.vigor", "", 3) }
            .ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var lookups = new LootMintLookups(
            LookupAtom: id => atomsById.TryGetValue(id, out var a) ? a : null,
            LookupAffix: _ => null, // deliberately unreachable for a synthesized pool -- proves the dispatcher never needs it here
            Equipment: new EquipmentContainerLookups(cells,
                family => atomsById.Values.Where(a => a.FamilyId == family).ToList()),
            ContainerFor: null);

        var grant = Grant(DropEntryKind.Equipment, rollSeed: 999) with { MaxTier = 3 };
        var rejection = LootMintAt.Mint(grant, thetaContent: 20, lookups, Tuning, out var instance);

        Assert.True(rejection.IsOk, rejection.Detail);
        Assert.NotNull(instance);
        Assert.StartsWith("item.drop-humanoid-feet-a-001-", instance!.ContainerId, StringComparison.Ordinal);
        Assert.NotEmpty(instance.Atoms); // the container's one prefix roll actually drew something
    }

    [Fact]
    public void Equipment_reads_RollSeed_from_the_grant_not_a_fixed_value()
    {
        var cells = new[] { new RoleFamilyCell("footing", "humanoid", "atom.vigor", MaxTier: 3) };
        var atoms = new[] { Atom("atom.vigor", "", 1), Atom("atom.vigor", "", 2), Atom("atom.vigor", "", 3) };
        var atomsById = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        LootMintLookups LookupsFor() => new(
            LookupAtom: id => atomsById.TryGetValue(id, out var a) ? a : null,
            LookupAffix: _ => null,
            Equipment: new EquipmentContainerLookups(cells, family => atoms.Where(a => a.FamilyId == family).ToList()),
            ContainerFor: null);

        // Different rollSeeds, SAME index (so ContainerId/EphemeralContainerId and therefore the RNG
        // stream name are identical -- RollSeed must be the ONLY thing that can move the draw), both
        // drawing 1-of-3 candidates, must disagree at least once over a reasonable sample -- the same
        // "stream namespacing" proof this whole program uses everywhere. A first draft of this test
        // varied `index` alongside `rollSeed`, which also changes the container id (and therefore the
        // stream name) independent of RollSeed -- caught by this task's own mutation-test pass, which
        // found a hardcoded rollSeed of 0 still passed it; fixed by holding index fixed.
        var seenDifferent = false;
        for (ulong seed = 1; seed < 40 && !seenDifferent; seed++)
        {
            var a = Grant(DropEntryKind.Equipment, index: 1, rollSeed: seed) with { MaxTier = 3 };
            var b = Grant(DropEntryKind.Equipment, index: 1, rollSeed: seed + 1000) with { MaxTier = 3 };
            LootMintAt.Mint(a, 20, LookupsFor(), Tuning, out var ia);
            LootMintAt.Mint(b, 20, LookupsFor(), Tuning, out var ib);
            if (ia!.Atoms[0].AtomId != ib!.Atoms[0].AtomId) seenDifferent = true;
        }
        Assert.True(seenDifferent, "40 samples never disagreed -- RollSeed is very likely not reaching Instantiator");
    }

    [Fact]
    public void Equipment_determinism_same_grant_and_theta_reproduce_the_identical_fingerprint()
    {
        var cells = new[] { new RoleFamilyCell("footing", "humanoid", "atom.vigor", MaxTier: 3) };
        var atoms = new[] { Atom("atom.vigor", "", 1), Atom("atom.vigor", "", 2), Atom("atom.vigor", "", 3) };
        var atomsById = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        LootMintLookups LookupsFor() => new(
            LookupAtom: id => atomsById.TryGetValue(id, out var a) ? a : null,
            LookupAffix: _ => null,
            Equipment: new EquipmentContainerLookups(cells, family => atoms.Where(a => a.FamilyId == family).ToList()),
            ContainerFor: null);
        var grant = Grant(DropEntryKind.Equipment, rollSeed: 777) with { MaxTier = 3 };

        LootMintAt.Mint(grant, 20, LookupsFor(), Tuning, out var i1);
        LootMintAt.Mint(grant, 20, LookupsFor(), Tuning, out var i2);

        Assert.Equal(i1!.ContentFingerprint(), i2!.ContentFingerprint());
    }

    [Fact]
    public void Equipment_with_an_unsatisfiable_pool_refuses_via_the_real_ContainerValidator_not_a_private_copy()
    {
        // A role/frame with zero legal cells, but PrefixRolls > 0 -- ContainerValidator.Validate's own
        // "prefix_rolls/suffix_rolls is X/Y but the pool is empty" rule must fire, proving this
        // dispatcher leans on the shared validator rather than re-deriving the same check.
        var lookups = new LootMintLookups(
            LookupAtom: _ => null, LookupAffix: _ => null,
            Equipment: new EquipmentContainerLookups(Array.Empty<RoleFamilyCell>(), _ => Array.Empty<AtomRow>()),
            ContainerFor: null);

        var rejection = LootMintAt.Mint(Grant(DropEntryKind.Equipment), 20, lookups, Tuning, out var instance);

        Assert.False(rejection.IsOk);
        Assert.Equal(AtomRejectionReason.UnsatisfiablePool, rejection.Reason);
        Assert.Null(instance);
    }

    // ---- Unique -----------------------------------------------------------------------------------------

    [Fact]
    public void Unique_with_no_ContainerFor_refuses_by_name()
    {
        var grant = Grant(DropEntryKind.Unique, refId: "item.some-unique");
        var rejection = LootMintAt.Mint(grant, 20, EmptyLookups(), Tuning, out var instance);

        Assert.False(rejection.IsOk);
        Assert.Contains("drop.mint-kind-unsupported", rejection.Detail);
        Assert.Contains("Unique", rejection.Detail);
        Assert.Null(instance);
    }

    [Fact]
    public void Unique_with_an_unresolvable_ref_refuses_drop_unknown_unique()
    {
        var lookups = new LootMintLookups(
            LookupAtom: _ => null, LookupAffix: _ => null, Equipment: null, ContainerFor: _ => null);
        var grant = Grant(DropEntryKind.Unique, refId: "item.does-not-exist");

        var rejection = LootMintAt.Mint(grant, 20, lookups, Tuning, out var instance);

        Assert.False(rejection.IsOk);
        Assert.Contains("drop.unknown-unique", rejection.Detail);
        Assert.Contains("item.does-not-exist", rejection.Detail);
        Assert.Null(instance);
    }

    [Fact]
    public void Unique_mints_through_the_real_Instantiator_against_the_resolved_container()
    {
        var atom = Atom("atom.vitality", "", 1);
        var container = new ContainerRow
        {
            ContainerId = "item.a-real-unique",
            Kind = ContainerKind.Item,
            Rarity = "cultivated",
            Atoms = new[] { new ContainerAtomRow(0, atom.AtomId) },
        };
        var lookups = new LootMintLookups(
            LookupAtom: id => id == atom.AtomId ? atom : null,
            LookupAffix: _ => null,
            Equipment: null,
            ContainerFor: refId => refId == "item.a-real-unique" ? container : null);

        var grant = Grant(DropEntryKind.Unique, refId: "item.a-real-unique", rollSeed: 555);
        var rejection = LootMintAt.Mint(grant, thetaContent: 20, lookups, Tuning, out var instance);

        Assert.True(rejection.IsOk, rejection.Detail);
        Assert.NotNull(instance);
        Assert.Equal("item.a-real-unique", instance!.ContainerId);
        Assert.Single(instance.Atoms);
        Assert.Equal(atom.AtomId, instance.Atoms[0].AtomId);
    }

    // ---- Relic (empire-development Task 1.3a, spec-relic-item-kind.md §Design 3) --------------------

    [Fact]
    public void Relic_with_no_ContainerFor_refuses_by_name()
    {
        var grant = Grant(DropEntryKind.Relic, refId: "relic.some-cost-token");
        var rejection = LootMintAt.Mint(grant, 20, EmptyLookups(), Tuning, out var instance);

        Assert.False(rejection.IsOk);
        Assert.Contains("drop.mint-kind-unsupported", rejection.Detail);
        Assert.Contains("Relic", rejection.Detail);
        Assert.Null(instance);
    }

    [Fact]
    public void Relic_with_an_unresolvable_ref_refuses_drop_unknown_relic()
    {
        var lookups = new LootMintLookups(
            LookupAtom: _ => null, LookupAffix: _ => null, Equipment: null, ContainerFor: _ => null);
        var grant = Grant(DropEntryKind.Relic, refId: "relic.does-not-exist");

        var rejection = LootMintAt.Mint(grant, 20, lookups, Tuning, out var instance);

        Assert.False(rejection.IsOk);
        Assert.Contains("drop.unknown-relic", rejection.Detail);
        Assert.Contains("relic.does-not-exist", rejection.Detail);
        Assert.Null(instance);
    }

    [Fact]
    public void Relic_mints_through_the_real_Instantiator_with_no_atoms_fixed()
    {
        // The strongest relic shape: zero atoms (pure Wonder-build cost token, spec §Design 1) —
        // the arm must not require even one fixed atom to instantiate.
        var container = new ContainerRow
        {
            ContainerId = "relic.a-real-cost-token",
            Kind = ContainerKind.Relic,
        };
        var lookups = new LootMintLookups(
            LookupAtom: _ => null,
            LookupAffix: _ => null,
            Equipment: null,
            ContainerFor: refId => refId == "relic.a-real-cost-token" ? container : null);

        var grant = Grant(DropEntryKind.Relic, refId: "relic.a-real-cost-token", rollSeed: 555);
        var rejection = LootMintAt.Mint(grant, thetaContent: 20, lookups, Tuning, out var instance);

        Assert.True(rejection.IsOk, rejection.Detail);
        Assert.NotNull(instance);
        Assert.Equal("relic.a-real-cost-token", instance!.ContainerId);
        Assert.Empty(instance.Atoms);
    }

    // ---- guards -----------------------------------------------------------------------------------------

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => LootMintAt.Mint(null!, 20, EmptyLookups(), Tuning, out _));
        Assert.Throws<ArgumentNullException>(() => LootMintAt.Mint(Grant(DropEntryKind.Material), 20, null!, Tuning, out _));
        Assert.Throws<ArgumentNullException>(() => LootMintAt.Mint(Grant(DropEntryKind.Material), 20, EmptyLookups(), null!, out _));
    }
}
