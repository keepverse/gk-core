using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// item-ideal.md, `equip-runtime` (module 5) — ⭐ the payoff. After this module, one hand-made item
/// on one actor is observable in battle. Mirrors <see cref="TraitAtomSource"/>'s own migration tests
/// exactly, because it is the same producer shape.
/// </summary>
public class EquipRuntimeTests
{
    // battle-hub-fuse T6: equipment reaches battle as Hub inputs (bound atoms resolved through the
    // same FromResolver projection production uses), never a composer static. No shared state, so
    // no Dispose reset is needed.
    //
    // battle-ops-parity T7: resolved by SpecimenId, never Key -- production (EquippedBoundAtoms,
    // WebMatchService) always resolves a specimen's equip atoms by its SpecimenId (the durable
    // assignment scope), and every fixture below sets a distinct Key ("squad:0") while varying
    // SpecimenId, so keying off Key here would resolve every actor's atoms identically regardless of
    // which specimen it actually is -- found and fixed live in this session: the earlier `setup.Key`
    // read a constant across every fixture and silently produced zero atoms.
    static BattleActorSetup WithBoundAtoms(BattleActorSetup setup, EquipAtomSource source) => setup with
    {
        HubInputs = (setup.HubInputs ?? new BattleHubInputs())
            with { BoundAtoms = setup.SpecimenId is { } specimenId ? source.DerivedAtomsFor(specimenId) : Array.Empty<BoundDerivedAtom>() }
    };

    static BattleActorSetup Actor(string? specimenId, int level = 5) => new()
    {
        Key = "squad:0", Side = "squad", SpeciesId = "spec", TypeId = 1, Level = level,
        SpecimenId = specimenId,
        MaxHp = 100, Atk = 50, Defense = 20,
    };

    static AtomRow DerivedAtom(string channel, int amount) => new()
    {
        AtomId = AtomRow.DeriveId("atom.equip-test", "", 1), KindId = "stat.derived",
        FamilyId = "atom.equip-test", Variant = "", Tier = 1, Name = "Equip Test",
        ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"flat\",\"amount\":{amount}}}",
    };

    [Fact]
    public void An_equipped_item_changes_a_battle_number()
    {
        var source = EquipAtomSource.FromResolver(specimenId =>
            specimenId == "s42" ? new[] { DerivedAtom(DerivedStatChannels.CombatPowerFire, 30) } : Array.Empty<AtomRow>());

        var geared = BattleHubCompose.Compose(WithBoundAtoms(Actor(specimenId: "s42"), source));
        var bare = BattleHubCompose.Compose(Actor(specimenId: "s42", level: 5) with { SpecimenId = null });

        Assert.Equal(bare.Get(DerivedStatChannels.CombatPowerFire) + 30,
            geared.Get(DerivedStatChannels.CombatPowerFire));
    }

    [Fact]
    public void Unequipping_removes_the_contribution()
    {
        var source = EquipAtomSource.FromResolver(_ =>
            new[] { DerivedAtom(DerivedStatChannels.CombatPowerFire, 30) });
        var empty = EquipAtomSource.FromResolver(_ => Array.Empty<AtomRow>());

        var before = BattleHubCompose.Compose(WithBoundAtoms(Actor(specimenId: "s42"), source));
        var after = BattleHubCompose.Compose(WithBoundAtoms(Actor(specimenId: "s42"), empty));

        Assert.Equal(30, before.Get(DerivedStatChannels.CombatPowerFire) - after.Get(DerivedStatChannels.CombatPowerFire));
    }

    [Fact]
    public void Equipment_and_trait_mods_compose_without_double_counting()
    {
        var traits = TraitAtomSource.FromContainers(
            new[]
            {
                new ContainerRow
                {
                    ContainerId = "trait.test-trait", Kind = ContainerKind.Trait,
                    Atoms = new[] { new ContainerAtomRow(1, "atom.trait-test.t1") },
                },
            },
            atomId => atomId == "atom.trait-test.t1" ? DerivedAtomNamed("atom.trait-test", DerivedStatChannels.CombatPowerFire, 10) : null);
        var equip = EquipAtomSource.FromResolver(_ =>
            new[] { DerivedAtom(DerivedStatChannels.CombatPowerFire, 30) });

        var setup = Actor(specimenId: "s42") with { TraitIds = new[] { "test-trait" } };
        var composed = BattleHubCompose.Compose(WithBoundAtoms(setup, equip), traits);

        var bareline = BattleHubCompose.Compose(Actor(specimenId: null));

        // Both contributions landed, exactly once each -- 10 (trait) + 30 (equipment), not 40 twice
        // and not one silently overwriting the other. The trait source rides explicitly (no shared
        // static); equipment rides the setup's Hub inputs, exactly like production.
        Assert.Equal(bareline.Get(DerivedStatChannels.CombatPowerFire) + 40,
            composed.Get(DerivedStatChannels.CombatPowerFire));
    }

    static AtomRow DerivedAtomNamed(string family, string channel, int amount) => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1), KindId = "stat.derived",
        FamilyId = family, Variant = "", Tier = 1, Name = family,
        ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"flat\",\"amount\":{amount}}}",
    };

    /// <summary>
    /// A `stat.derived` `amount` is <c>ParamKind.Value</c> — a plain number OR a ValueSpec object
    /// (a curve reference, or `patron-absorption`'s <c>externalRef</c>). The shipped corpus carries
    /// twelve of the latter (`gk-data/packs/fusion/data/seed/atoms/patron-aura.json`), and
    /// <c>JsonElement.TryGetInt64</c> <b>throws</b> on an object rather than returning false. Without
    /// a ValueKind guard the whole compose died with an unhandled
    /// <c>InvalidOperationException</c> — and so did the geared corner run, which sweeps every
    /// `stat.derived` row in the corpus. Skip the row, exactly as an unparseable `op` is skipped;
    /// this seam has no ValueSpec resolver (the compiler does), so resolving one here would be
    /// inventing a second, divergent evaluation of the same spec.
    /// </summary>
    static AtomRow ValueSpecAtom(string channel) => new()
    {
        AtomId = AtomRow.DeriveId("atom.equip-valuespec", "", 1), KindId = "stat.derived",
        FamilyId = "atom.equip-valuespec", Variant = "", Tier = 1, Name = "Equip ValueSpec",
        ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"flat\",\"amount\":{{\"externalRef\":\"patron.auraPowerMilli.fire\"}}}}",
    };

    [Fact]
    public void A_ValueSpec_amount_is_skipped_not_crashed_on_in_battle()
    {
        var equip = EquipAtomSource.FromResolver(_ => new[]
        {
            ValueSpecAtom(DerivedStatChannels.CombatPowerFire),
            DerivedAtom(DerivedStatChannels.CombatPowerFire, 30),
        });

        var composed = BattleHubCompose.Compose(WithBoundAtoms(Actor(specimenId: "s42"), equip));
        var bare = BattleHubCompose.Compose(Actor(specimenId: null) with { SpecimenId = null });

        // The unresolvable row is skipped and the walk CONTINUES -- the readable atom beside it still
        // lands. A thrown row would take the whole squad build with it.
        Assert.Equal(bare.Get(DerivedStatChannels.CombatPowerFire) + 30,
            composed.Get(DerivedStatChannels.CombatPowerFire));
    }

    [Fact]
    public void A_ValueSpec_amount_is_skipped_on_the_derived_side_too()
    {
        var equip = EquipAtomSource.FromResolver(_ => new[]
        {
            ValueSpecAtom(DerivedStatChannels.CombatPowerFire),
            DerivedAtom(DerivedStatChannels.CombatPowerFire, 30),
        });

        var atoms = equip.DerivedAtomsFor("s42");

        // Same one shared parse (EquippedDerived), so the two sides cannot diverge on which rows count.
        Assert.Single(atoms);
        Assert.Equal(30L, atoms[0].Amount);
    }

    [Fact]
    public void No_specimen_id_means_no_equipment_contribution()
    {
        // No Hub inputs, no contribution — the bound-atom projection only runs for setups that
        // carry it, exactly like the old SpecimenId-gated read.
        var withoutSpecimen = BattleHubCompose.Compose(Actor(specimenId: null));
        var baseline = BattleHubCompose.Compose(Actor(specimenId: null) with { SpecimenId = null });

        Assert.Equal(baseline.Get(DerivedStatChannels.CombatPowerFire), withoutSpecimen.Get(DerivedStatChannels.CombatPowerFire));
    }

    [Fact]
    public void Sim_runtime_opens_partially_and_the_spec_says_why()
    {
        // mechanism-wiring E5 (2026-09-06): SimEffectHost gained a real consumer -- ActorDerivedLookup's
        // contribution fold, reached via SimEffectHost/FoundationHarness.ContributeDerived. Opened
        // `Partial`, not `Full`, at first: the fold was a plain sum (ActorDerivedSnapshot.OverlayAdd)
        // that honoured Flat/Increased and not Replace/Flag (EffectOfflineKitTests.
        // The_four_derived_ops_decide_Full_versus_Partial). sim-hub-parity (T15, 2026-09-13) closed that
        // gap: the fold now routes through DerivedComposer.ComposeChannelWithBaseline, the SAME op-aware
        // fold every other runtime uses, so `gk-core/tools/CombatSim` (which drives FoundationHarness, not this
        // class) can now simulate ANY op an item authors, not just Flat/Increased. Method name kept
        // (renamed from "..._stays_None_..." once already; this is its second history note, not a
        // second rename) to avoid drifting two out-of-program doc references
        // (item-todo.md, spec-equip-runtime.md) that cite it by name.
        var kind = AtomKindRegistry.Get("stat.derived")!;
        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Sim));
        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Battle));
        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Lawn));
    }
}
