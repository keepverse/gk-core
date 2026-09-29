using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.TestSupport;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// E12: one trait migrates from <c>TraitBattleCatalog</c> to a bound container (spec-trait-migration.md).
///
/// <para><b>The predicted delta is zero, and this measures it.</b> The spec calls that prediction the
/// sign-off document: if a golden moves by anything other than serialisation shape, the migration is
/// wrong and stops. So the same squad is composed down both roads and the snapshots compared — not
/// "the number looks right", but "the two paths agree everywhere".</para>
///
/// <para><b>One trait, not seven.</b> The map said the seven funnel-routed traits become containers.
/// <c>FunnelRouted</c> classifies which traits the contracts module layers obedience onto, not which
/// the atom vocabulary can express. Checked against the 12 kinds, <c>critical-hunter</c> is the only
/// survivor: <c>stat.derived</c> mods merge at compose time, a path battle already runs.</para>
/// </summary>
public class TraitMigrationParityTests
{
    const string Trait = "critical-hunter";

    static BattleActorSetup Actor(params string[] traits) => new()
    {
        Key = "squad:0",
        Side = "squad",
        SpeciesId = "creature.test",
        Level = 5,
        MaxHp = 500,
        Atk = 40,
        Defense = 20,
        ElementPrimary = ElementTypeId.Fire,
        TraitIds = traits,
    };

    /// <summary>The migrated trait, as rows — read from the shipped seed files, not hand-built.</summary>
    static TraitAtomSource MigratedFromSeed()
    {
        var root = RepoRoot();
        var files = new[] { "atoms", "containers" }
            .Select(d => Path.Combine(root, "data", "seed", d))
            .SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var byId = collected.Content.Atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        return TraitAtomSource.FromContainers(
            collected.Content.Containers, id => byId.TryGetValue(id, out var a) ? a : null);
    }

    // ---- the predicted delta, measured -----------------------------------------------------------

    /// <summary>
    /// The pre-migration catalog value, written out here rather than read from
    /// <see cref="TraitBattleCatalog"/>.
    ///
    /// <para>The oracle has to be a <b>captured</b> baseline. Reading the live catalog worked only
    /// until the entry was retired — at which point the comparison was the migrated value against
    /// nothing, and it agreed about nothing. It failed loudly, which is the good version of that
    /// mistake, but a comparison that depends on the thing being replaced is the wrong shape.</para>
    /// </summary>
    static readonly BattleChannelMod PreMigrationMod =
        new(DerivedStatChannels.CombatCritRateOmni, 150);

    /// <summary>The trait source as it behaved before E12 — the captured baseline, as a source.</summary>
    static TraitAtomSource PreMigration()
    {
        var container = new ContainerRow
        {
            ContainerId = "trait." + Trait,
            Kind = ContainerKind.Trait,
            Atoms = new[] { new ContainerAtomRow(0, "atom.baseline.t1") },
        };
        var atom = new AtomRow
        {
            AtomId = "atom.baseline.t1",
            KindId = "stat.derived",
            FamilyId = "atom.baseline",
            Tier = 1,
            ParamsJson = "{\"channel\":\"" + PreMigrationMod.ChannelId
                         + "\",\"op\":\"flat\",\"amount\":" + PreMigrationMod.Amount + "}",
        };
        return TraitAtomSource.FromContainers(new[] { container }, _ => atom);
    }

    [Fact]
    public void The_migrated_trait_composes_identically_to_the_catalog_it_replaced()
    {
        // THE sign-off document: both roads, same squad, compared channel by channel. Measured
        // across the whole suite the delta is zero — no battle golden moves and no resolver output
        // changes. The prediction the spec asked for is now a measurement.
        var actor = Actor(Trait);

        var viaOldCatalog = BattleHubCompose.Compose(actor, PreMigration());
        var viaAtoms = BattleHubCompose.Compose(actor, MigratedFromSeed());

        AssertSameSnapshot(viaOldCatalog, viaAtoms);
    }

    [Fact]
    public void What_core_ships_matches_what_the_seed_files_author()
    {
        // Core holds the migrated mods so it needs no runtime content loader; the files are the
        // authored source. Two copies of one number is how they drift, so this pins them together.
        Assert.Equal(
            TraitAtomSource.Shipped().ModsFor(Trait),
            MigratedFromSeed().ModsFor(Trait));
    }

    [Fact]
    public void The_catalog_entry_is_retired_but_the_trait_still_carries_its_magnitude()
    {
        // The migration is only real if the old home is empty. If both still supplied it, the trait
        // would be double-counted the moment anything read them together.
        Assert.Empty(TraitBattleCatalog.Get(Trait).ChannelMods);
        Assert.Equal(new[] { PreMigrationMod }, BattleHubCompose.Traits.ModsFor(Trait));
    }

    [Fact]
    public void The_migrated_trait_is_actually_migrated()
    {
        // Without this, the parity above could be passing because the atom source found nothing and
        // silently fell back to the catalog — agreeing with itself.
        Assert.True(MigratedFromSeed().IsMigrated(Trait));
    }

    [Fact]
    public void The_number_is_still_a_hundred_and_fifty_crit_rate_points()
    {
        // Written out independently rather than read from either source, so the comparison has an
        // outside witness. +150 over the −250 parity baseline → σ(−1.0) ≈ 26.9% crit vs 7.6% base.
        var mods = MigratedFromSeed().ModsFor(Trait);

        var mod = Assert.Single(mods);
        Assert.Equal(DerivedStatChannels.CombatCritRateOmni, mod.ChannelId);
        Assert.Equal(150, mod.Amount);
    }

    [Fact]
    public void A_trait_with_the_migrated_one_reads_higher_crit_than_one_without()
    {
        // Proves the migrated path does something, so parity is not two zeros agreeing.
        var withTrait = BattleHubCompose.Compose(Actor(Trait), MigratedFromSeed());
        var without = BattleHubCompose.Compose(Actor(), MigratedFromSeed());

        Assert.Equal(
            without.Get(DerivedStatChannels.CombatCritRateOmni) + 150,
            withTrait.Get(DerivedStatChannels.CombatCritRateOmni));
    }

    // ---- the thirteen that did not move ------------------------------------------------------------

    [Theory]
    [InlineData("regenerator")]
    [InlineData("soul-eater")]
    [InlineData("berserker")]
    [InlineData("guardian")]
    [InlineData("swift")]
    [InlineData("immortal")]
    public void An_unmigrated_trait_still_reads_the_catalog(string traitId)
    {
        var migrated = MigratedFromSeed();

        Assert.False(migrated.IsMigrated(traitId));
        Assert.Equal(
            TraitBattleCatalog.Get(traitId).ChannelMods,
            migrated.ModsFor(traitId));
    }

    [Fact]
    public void A_squad_of_unmigrated_traits_composes_identically_either_way()
    {
        var actor = Actor("regenerator", "soul-eater", "guardian", "swift");

        AssertSameSnapshot(
            BattleHubCompose.Compose(actor, TraitAtomSource.CatalogOnly),
            BattleHubCompose.Compose(actor, MigratedFromSeed()));
    }

    // ---- the kind that had to re-open --------------------------------------------------------------

    [Fact]
    public void Stat_derived_is_supported_in_battle_now_that_it_has_a_consumer()
    {
        // D6 quarantined it to None/None/None because nothing consumed it — a bind would have been
        // accepted and then done nothing forever. E12 ships the consumer, so battle re-opens.
        var kind = AtomKindRegistry.Get("stat.derived")!;

        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Battle));
    }

    [Fact]
    public void Stat_derived_opens_fully_in_sim_now_that_the_fold_is_op_aware()
    {
        // The rule is unchanged: a runtime opens only where a consumer exists. LAWN opened 2026-08-30
        // (decisions.md "Derived-write lawn executor") on the strength of its OWN consumer --
        // `AtomDerivedSubsystem` on the injector's ActorHub -- never on the strength of battle's. SIM
        // opened 2026-09-06 (mechanism-wiring E5) to Partial once ActorDerivedLookup's contribution fold
        // gave it a real consumer that honoured Flat/Increased and not Replace/Flag
        // (EffectOfflineKitTests.The_four_derived_ops_decide_Full_versus_Partial), then sim-hub-parity
        // (T15, 2026-09-13) closed the gap to Full via DerivedComposer.ComposeChannelWithBaseline.
        // Renamed from "..._stays_closed_where_it_still_has_no_consumer", then from
        // "..._opens_partially_in_sim_where_the_fold_is_incomplete", neither of which stayed true.
        var kind = AtomKindRegistry.Get("stat.derived")!;

        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Lawn));
        Assert.Equal(RuntimeState.Full, kind.SupportIn(RuntimeId.Sim));
    }

    [Fact]
    public void The_migrated_trait_binds_in_battle_rather_than_being_rejected()
    {
        // Its own bind was rejected RuntimeUnsupported until the cell flipped — the module could not
        // have shipped the container without shipping the consumer.
        var root = RepoRoot();
        var atoms = AtomSeedFile.Collect(
            Directory.GetFiles(Path.Combine(root, "data", "seed", "atoms"), "*.json")
                .OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => (f, File.ReadAllText(f))).ToArray());

        var trait = atoms.Content.Atoms.Single(a => a.AtomId == "atom.critical-hunter.t1");

        OwnerScope.Validate(OwnerKind.Match, "match", out var scope);
        var verdict = BindGate.Check(
            new[] { trait }, scope, new BindContext(RuntimeId.Battle));

        Assert.True(verdict.IsOk, verdict.ToString());
    }

    [Fact]
    public void A_trait_container_carrying_a_kind_battle_cannot_run_contributes_nothing()
    {
        // Only stat.derived is read. A trait container with a status.apply in it is content whose
        // consumer battle does not have, and accepting it would be the silent no-op all over again.
        var container = new ContainerRow
        {
            ContainerId = "trait.invented",
            Kind = ContainerKind.Trait,
            Atoms = new[] { new ContainerAtomRow(0, "atom.noise.t1") },
        };
        var noise = new AtomRow
        {
            AtomId = "atom.noise.t1", KindId = "status.apply", FamilyId = "atom.noise", Tier = 1,
            ParamsJson = """{"status":"butter"}""",
        };

        var source = TraitAtomSource.FromContainers(new[] { container }, _ => noise);

        Assert.False(source.IsMigrated("invented"));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    static void AssertSameSnapshot(ActorDerivedSnapshot a, ActorDerivedSnapshot b)
    {
        foreach (var channel in DerivedStatChannels.AllCombatChannelIds)
            Assert.Equal(a.Get(channel), b.Get(channel));
    }

    static string RepoRoot() => ContentRoot.Path;
}
