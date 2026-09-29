using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>
/// D2.6 (spec-encounter-generator.md §6) — `EliteAffix` and the seventh `ContainerKind`. The real
/// `gk-data/packs/fusion/data/seed/effects/affixes/all.json` carries zero rows tagged for enemies today (no `tags` concept
/// exists on `AffixRow` at all yet) — the happy-path tests below use a small, hand-built pool, exactly
/// the "already-filtered pool as a plain parameter" shape `EliteAffix.cs`'s own class doc describes;
/// the degradation tests use the REAL empty state directly.
/// </summary>
public class EliteAffixTests
{
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 0, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static readonly Dictionary<string, AtomRow> Catalog = new(StringComparer.Ordinal);

    static EliteAffixTests()
    {
        void Add(string family, string variant, int tier, string paramsJson)
        {
            var id = AtomRow.DeriveId(family, variant, tier);
            Catalog[id] = new AtomRow { AtomId = id, KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier, ParamsJson = paramsJson };
        }
        Add("atom.enemy-power", "fire", 1, "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":10}");
        Add("atom.enemy-power", "ice", 1, "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":10}");
    }

    static AtomRow? LookupAtom(string id) => Catalog.TryGetValue(id, out var a) ? a : null;
    static AffixRow? LookupAffix(string id) =>
        Catalog.TryGetValue(id, out var atom) ? new AffixRow(id, AffixClass.Suffix, new[] { new AffixRefRow(1, atom.AtomId) }) : null;

    static readonly IReadOnlyList<ContainerPoolRow> RealPool = new List<ContainerPoolRow>();
    static readonly IReadOnlyList<ContainerPoolRow> FixturePool = new[]
    {
        new ContainerPoolRow("atom.enemy-power.fire.t1", 10),
        new ContainerPoolRow("atom.enemy-power.ice.t1", 10),
    };

    static BattleActorSetup Elite() => new() { Key = "wave:0", Side = "wave", SpeciesId = "x", Level = 100 };

    // ---- the real, named external dependency: an empty pool degrades, never fakes an affix ----

    [Fact]
    public void The_real_empty_pool_degrades_to_no_affix_with_a_named_warning()
    {
        var (actor, warning) = EliteAffix.Apply(Elite(), "delve-1-elite", "cultivated", 1, 3, 1, 0,
            RealPool, LookupAtom, LookupAffix, rollSeed: 1, thetaRoom: 100, Tuning);

        Assert.NotNull(warning);
        Assert.Contains("empty", warning);
        Assert.Null(actor.GrantedContainerIds); // untouched -- never a fake affix, never a flat stat bump
    }

    [Fact]
    public void An_empty_pool_never_calls_Instantiator_at_all()
    {
        // Proven indirectly: MinTier/MaxTier deliberately invalid (min > max, which ContainerValidator
        // refuses) would throw/refuse if TryInstantiate were reached -- the empty-pool short-circuit
        // must return before ever constructing the container.
        var (actor, warning) = EliteAffix.Apply(Elite(), "delve-1-elite", "cultivated", 5, 1 /* min > max */,
            1, 0, RealPool, LookupAtom, LookupAffix, 1, 100, Tuning);
        Assert.NotNull(warning);
        Assert.Contains("empty", warning);
    }

    // ---- the happy path: a real roll against a hand-built, already-filtered pool ----

    [Fact]
    public void A_successful_roll_grants_the_deterministic_container_id_and_no_warning()
    {
        var (actor, warning) = EliteAffix.Apply(Elite(), "delve-1-elite", "cultivated", 1, 1, 0, 1,
            FixturePool, LookupAtom, LookupAffix, rollSeed: 7, thetaRoom: 100, Tuning);

        Assert.Null(warning);
        Assert.Equal(new[] { "enemy.delve-1-elite" }, actor.GrantedContainerIds);
    }

    [Fact]
    public void The_granted_id_follows_the_seventh_kinds_own_prefix()
    {
        var (actor, _) = EliteAffix.Apply(Elite(), "delve-2-boss-p0", "cultivated", 1, 1, 0, 1,
            FixturePool, LookupAtom, LookupAffix, 7, 100, Tuning);
        Assert.Equal("enemy.delve-2-boss-p0", Assert.Single(actor.GrantedContainerIds!));
    }

    [Fact]
    public void Appends_to_any_already_granted_ids_rather_than_replacing_them()
    {
        var actorWithOne = Elite() with { GrantedContainerIds = new[] { "enemy.pre-existing" } };
        var (actor, _) = EliteAffix.Apply(actorWithOne, "delve-1-elite", "cultivated", 1, 1, 0, 1,
            FixturePool, LookupAtom, LookupAffix, 7, 100, Tuning);
        Assert.Equal(new[] { "enemy.pre-existing", "enemy.delve-1-elite" }, actor.GrantedContainerIds);
    }

    [Fact]
    public void A_roll_that_finds_no_drawable_group_within_the_tier_window_degrades_instead_of_throwing()
    {
        // MinTier/MaxTier window (5..9) excludes every real t1 pool row -- ContainerValidator refuses
        // rather than Instantiator throwing, and EliteAffix turns that refusal into a warning.
        var (actor, warning) = EliteAffix.Apply(Elite(), "delve-1-elite", "cultivated", 5, 9, 0, 1,
            FixturePool, LookupAtom, LookupAffix, 7, 100, Tuning);

        Assert.NotNull(warning);
        Assert.Contains("refused", warning);
        Assert.Null(actor.GrantedContainerIds);
    }

    [Fact]
    public void Every_other_field_on_the_actor_is_untouched()
    {
        var elite = Elite();
        var (actor, _) = EliteAffix.Apply(elite, "delve-1-elite", "cultivated", 1, 1, 0, 1,
            FixturePool, LookupAtom, LookupAffix, 7, 100, Tuning);
        Assert.Equal(elite.Key, actor.Key);
        Assert.Equal(elite.SpeciesId, actor.SpeciesId);
        Assert.Equal(elite.Level, actor.Level);
    }

    // ---- argument validation ----

    [Fact]
    public void Null_and_empty_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            EliteAffix.Apply(null!, "x", null, null, null, 0, 0, FixturePool, LookupAtom, LookupAffix, 1, 100, Tuning));
        Assert.Throws<ArgumentException>(() =>
            EliteAffix.Apply(Elite(), "", null, null, null, 0, 0, FixturePool, LookupAtom, LookupAffix, 1, 100, Tuning));
        Assert.Throws<ArgumentNullException>(() =>
            EliteAffix.Apply(Elite(), "x", null, null, null, 0, 0, null!, LookupAtom, LookupAffix, 1, 100, Tuning));
        Assert.Throws<ArgumentNullException>(() =>
            EliteAffix.Apply(Elite(), "x", null, null, null, 0, 0, FixturePool, null!, LookupAffix, 1, 100, Tuning));
        Assert.Throws<ArgumentNullException>(() =>
            EliteAffix.Apply(Elite(), "x", null, null, null, 0, 0, FixturePool, LookupAtom, null!, 1, 100, Tuning));
        Assert.Throws<ArgumentNullException>(() =>
            EliteAffix.Apply(Elite(), "x", null, null, null, 0, 0, FixturePool, LookupAtom, LookupAffix, 1, 100, null!));
    }

    // ---- ContainerKind.Enemy: the verify line's own "ContainerValidator red/green for the new prefix" ----

    static ContainerRow EnemyContainer(string id) => new()
    {
        ContainerId = id, Kind = ContainerKind.Enemy,
        Atoms = new[] { new ContainerAtomRow(1, "atom.enemy-power.fire.t1") },
    };

    [Fact]
    public void An_enemy_prefixed_id_validates_green()
    {
        var result = ContainerValidator.Validate(EnemyContainer("enemy.delve-1-elite"), LookupAtom, LookupAffix);
        Assert.True(result.IsOk, result.ToString());
    }

    [Fact]
    public void EnemyDot_under_a_different_kind_is_refused()
    {
        var wrongKind = EnemyContainer("enemy.delve-1-elite") with { Kind = ContainerKind.Trait };
        var result = ContainerValidator.Validate(wrongKind, LookupAtom, LookupAffix);
        Assert.False(result.IsOk);
    }

    [Fact]
    public void The_six_old_prefixes_still_validate_exactly_as_before()
    {
        var oldKindPrefixes = new[]
        {
            (ContainerKind.Item, "item"), (ContainerKind.Trait, "trait"), (ContainerKind.Skill, "skill"),
            (ContainerKind.SpeciesPassive, "species-passive"), (ContainerKind.Patron, "patron"), (ContainerKind.WorldBuff, "world-buff"),
        };
        foreach (var (kind, prefix) in oldKindPrefixes)
        {
            var c = EnemyContainer($"{prefix}.sample") with { Kind = kind };
            var result = ContainerValidator.Validate(c, LookupAtom, LookupAffix);
            Assert.True(result.IsOk, $"{kind}: {result}");
        }
    }

    [Fact]
    public void PrefixOf_Enemy_is_the_literal_enemy_string()
    {
        Assert.Equal("enemy", ContainerRow.PrefixOf(ContainerKind.Enemy));
    }

    [Fact]
    public void There_are_now_exactly_fifteen_container_kinds()
    {
        // 7 -> 11 (2026-09-07): container-kind-expansion (X7) added Gem/Charm/Combo/Consumable --
        // item's own four, unrelated to this module's own Enemy addition.
        // 11 -> 13 (achievement-title T3): EmpireTitle/ActorTitle are achievement-title's own two.
        // 13 -> 14 (empire-development Task 1.3a, merged 2026-09-16): Relic
        // (spec-relic-item-kind.md §Design 2). Two programs grew this enum on separate branches and
        // each asserted its own total; the merged count is both. None of the four is this module's.
        // 14 -> 15 (species-progression SP3.2): ContainerKind.SpeciesProgression
        // (spec-species-layer-projector.md). Also none of this module's own.
        Assert.Equal(15, Enum.GetValues<ContainerKind>().Length);
    }
}
