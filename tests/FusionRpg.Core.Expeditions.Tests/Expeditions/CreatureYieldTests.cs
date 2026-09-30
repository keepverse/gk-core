using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using Xunit;
using Xunit.Abstractions;

namespace FusionRpg.Core.Tests.Expeditions;

/// <summary>
/// species-gear-chain T31 (`creature-drop-tables` E3a, spec-creature-drop-tables.md § Design 3) — the
/// shard a creature yields follows its own rung, not `isBoss`; the same rung-derived input feeds an
/// Equipment-kind entry's `thetaContent` (T30's own scope addition), one derivation, two consumers.
/// </summary>
[Collection(ExpeditionsSequentialCollection.Name)]
[Trait("VerificationId", "core.creature-yield")]
public class CreatureYieldTests
{
    readonly ITestOutputHelper _out;

    public CreatureYieldTests(ITestOutputHelper output) => _out = output;


    // ---- CreatureYieldTuningHub.ShardFor — the tuning surface itself ------------------------------

    [Fact]
    public void Every_rung_resolves_a_shard_id_from_the_27_id_vocabulary()
    {
        foreach (var rungId in RarityLadder.RungIds)
        {
            var shardId = CreatureYieldTuningHub.ShardFor(rungId);
            Assert.Equal($"shard.{rungId}", shardId); // v1's legal-first identity map
        }
    }

    [Fact]
    public void An_unknown_rung_throws_naming_it()
    {
        var ex = Assert.Throws<ArgumentException>(() => CreatureYieldTuningHub.ShardFor("not-a-rung"));
        Assert.Contains("not-a-rung", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_rejects_a_missing_rung()
    {
        var json = """{"schemaVersion":1,"version":1,"rungs":{"chaff":"shard.chaff"}}""";
        var ex = Assert.Throws<CreatureYieldTuningRejection>(() => CreatureYieldTuningLoader.Parse(json));
        Assert.Contains("rungs.sprout", ex.Message, StringComparison.Ordinal);
    }

    static string IdentityRungsJson(Func<string, string>? valueOverride = null)
    {
        var pairs = RarityLadder.RungIds.Select(r =>
            "\"" + r + "\":\"" + (valueOverride?.Invoke(r) ?? ("shard." + r)) + "\"");
        return "{\"schemaVersion\":1,\"version\":1,\"rungs\":{" + string.Join(",", pairs) + "}}";
    }

    [Fact]
    public void Loader_rejects_an_unknown_rung_key()
    {
        var withExtra = IdentityRungsJson().TrimEnd('}', '}') + ",\"not-a-rung\":\"shard.chaff\"}}";
        var ex = Assert.Throws<CreatureYieldTuningRejection>(() => CreatureYieldTuningLoader.Parse(withExtra));
        Assert.Contains("not-a-rung", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_rejects_a_shard_id_outside_the_27_id_vocabulary()
    {
        var json = IdentityRungsJson(r => r == "chaff" ? "item.made-up" : "shard." + r);
        var ex = Assert.Throws<CreatureYieldTuningRejection>(() => CreatureYieldTuningLoader.Parse(json));
        Assert.Contains("item.made-up", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_accepts_the_real_shipped_file()
    {
        var json = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "creature-yield.v1.json"));
        var tuning = CreatureYieldTuningLoader.Parse(json);
        Assert.Equal(RarityLadder.RungCount, tuning.ShardByRung.Count);
        foreach (var rungId in RarityLadder.RungIds)
            Assert.Equal($"shard.{rungId}", tuning.ShardByRung[rungId]);
    }

    // ---- ExpeditionResolver.PlannedRungFor — E3a's single derivation -------------------------------

    static BattleActorSetup EnemyOf(string speciesId) => new()
    {
        Key = "wave:0", Side = "wave", SpeciesId = speciesId, TypeId = 1, Level = 1,
    };

    [Fact]
    public void PlannedRungFor_returns_the_highest_rung_among_the_waves_enemies()
    {
        // Two real, catalog-backed species at opposite ends of the ladder — found by rung, not
        // hardcoded to a name the corpus could rename.
        var byRung = CreatureSpeciesCatalog.All.GroupBy(s => s.BaseRarity).ToDictionary(g => g.Key, g => g.First());
        var low = byRung[CreatureRarityLadder.All.First(r => byRung.ContainsKey(r))];
        var high = byRung[CreatureRarityLadder.All.Last(r => byRung.ContainsKey(r))];
        Assert.NotEqual(low.BaseRarity, high.BaseRarity); // the fixture needs at least two distinct rungs present

        var wave = new[] { EnemyOf(low.SpeciesId), EnemyOf(high.SpeciesId) };
        Assert.Equal(high.BaseRarity.ToId(), ExpeditionResolver.PlannedRungFor(wave));

        // Order in the list must not matter — it is a MAX, not "the last enemy wins".
        var reversed = new[] { EnemyOf(high.SpeciesId), EnemyOf(low.SpeciesId) };
        Assert.Equal(high.BaseRarity.ToId(), ExpeditionResolver.PlannedRungFor(reversed));
    }

    [Fact]
    public void PlannedRungFor_over_a_single_enemy_is_that_enemys_own_rung()
    {
        var species = CreatureSpeciesCatalog.All.First();
        Assert.Equal(species.BaseRarity.ToId(), ExpeditionResolver.PlannedRungFor(new[] { EnemyOf(species.SpeciesId) }));
    }

    [Fact]
    public void Every_shipped_wave_resolves_a_shard_through_the_real_tuning_hub()
    {
        // The real Resolve() call path, not the helper standalone -- every tier's boss and non-boss
        // ticks alike must resolve a shard id without throwing (module-initializer-configured hub).
        foreach (var tier in ExpeditionTierCatalog.All)
        {
            var squad = Enumerable.Range(0, tier.SquadSlots).Select(i => new BattleActorSetup
            {
                Key = $"squad:{i}", Side = "squad", SpeciesId = "test-species", TypeId = 1, Level = 5,
                MaxHp = BattleRuleset.BaseHp(5), Atk = BattleRuleset.BaseAtk(5), Defense = BattleRuleset.BaseDefense(5),
            }).ToList();
            var resolution = ExpeditionResolver.Resolve(tier.TierId, squad, 55, tier.TickCount);
            // Every battle tick mints exactly one shard (this task's own change); a non-battle tick
            // may ALSO mint an unrelated essence (a wild creature that slipped away) — so the shard
            // count is a floor keyed on the battle count, not an assertion over every material entry.
            var shardEntries = resolution.Rewards.Materials.Where(m => m.MaterialId.StartsWith("shard.", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(shardEntries);
            Assert.All(shardEntries, m => Assert.Contains(m.MaterialId, RarityLadder.RungIds.Select(r => "shard." + r)));
            Assert.Equal(resolution.Battles.Count, shardEntries.Sum(m => m.Qty));
        }
    }

    // ---- Plan-time determinism is a MAX over enemies, not an RNG draw — no stream consumed ---------

    [Fact]
    public void PlannedRungFor_draws_no_rng_it_is_a_pure_fold_over_the_fixed_wave_roster()
    {
        // The wave roster itself is a fixed, waveId-seeded compile (WaveCatalog.StableSeed), never
        // re-rolled per expedition run -- so calling the resolver twice with DIFFERENT expedition
        // seeds must still draw the identical shard for the identical tier (the shard depends only on
        // the wave's compiled roster, never on the expedition's own seed).
        var a = ExpeditionResolver.Resolve("scout-30m", new List<BattleActorSetup>
        {
            new() { Key = "squad:0", Side = "squad", SpeciesId = "test-species", TypeId = 1, Level = 5 },
            new() { Key = "squad:1", Side = "squad", SpeciesId = "test-species", TypeId = 1, Level = 5 },
        }, seed: 1, elapsedTicks: 6);
        var b = ExpeditionResolver.Resolve("scout-30m", new List<BattleActorSetup>
        {
            new() { Key = "squad:0", Side = "squad", SpeciesId = "test-species", TypeId = 1, Level = 5 },
            new() { Key = "squad:1", Side = "squad", SpeciesId = "test-species", TypeId = 1, Level = 5 },
        }, seed: 999_999, elapsedTicks: 6);

        Assert.Equal(
            a.Rewards.Materials.Select(m => m.MaterialId).OrderBy(x => x, StringComparer.Ordinal),
            b.Rewards.Materials.Select(m => m.MaterialId).OrderBy(x => x, StringComparer.Ordinal));
    }

    // ---- 3a: an Equipment-kind creature drop mints with thetaContent derived from the rung ---------

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    static AtomRow Atom(string familyId, int tier) => new()
    {
        AtomId = AtomRow.DeriveId(familyId, "", tier),
        KindId = "stat.modify",
        FamilyId = familyId,
        Variant = "",
        Tier = tier,
        Name = familyId,
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
    };

    [Theory]
    [InlineData("chaff", 1)]
    [InlineData("cultivated", 4)]
    [InlineData("almanac", 10)]
    public void An_equipment_kind_creature_drop_mints_a_real_saved_instance_with_the_rungs_own_theta(
        string rungId, int expectedTheta)
    {
        // The rung's own 1-based ordinal position on the SHARED ten-rung ladder feeds thetaContent —
        // an INDEX on the existing ladder (RarityLadder.RungIndexOf), never a computed magnitude, so
        // this declares no private f(level) (ssot-power-scale.md §10's anti-duplication clause). The
        // same rung E3a's shard lookup reads is the one input here too — one derivation, two
        // consumers (spec-creature-drop-tables.md § Design 3 / Open question 2).
        var thetaContent = RarityLadder.RungIndexOf(rungId) + 1;
        Assert.Equal(expectedTheta, thetaContent);

        var cells = new[] { new RoleFamilyCell("armament-primary", "humanoid", "atom.vigor", MaxTier: 1) };
        var atomsById = new[] { Atom("atom.vigor", 1) }.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var lookups = new LootMintLookups(
            LookupAtom: id => atomsById.TryGetValue(id, out var a) ? a : null,
            LookupAffix: _ => null,
            Equipment: new EquipmentContainerLookups(cells, family => atomsById.Values.Where(a => a.FamilyId == family).ToList()),
            ContainerFor: null);

        // Matches the shape gk-data/packs/fusion/data/seed/loot/tables-creature.json's own rare-gate entry authors
        // (frame humanoid, role armament-primary) — the same table T30 shipped.
        var grant = new LootGrant(
            Index: 0, Kind: DropEntryKind.Equipment, RefId: "", Count: 1, AffixChannel: AffixChannels.Drop,
            BaseTypeId: "item.humanoid-feet-a-001", Frame: "humanoid", Role: "armament-primary",
            MinTier: 1, MaxTier: 1, PrefixRolls: 1, RollSeed: 4242);

        var rejection = LootMintAt.Mint(grant, thetaContent, lookups, Tuning, out var instance);

        Assert.True(rejection.IsOk, rejection.Detail);
        Assert.NotNull(instance);
        Assert.NotEmpty(instance!.Atoms);
    }

    // ---- per-species yield distribution — a reading, printed, never asserted -----------------------

    /// <summary>
    /// spec-creature-drop-tables.md Testing strategy: "per-species yield distribution — a reading,
    /// printed, never asserted (the per-family split of multi-family species included)". The dominance
    /// GUARD itself is `roster-metrics`' (creature-seed module 14) named obligation, not this module's
    /// (Success criterion 7) — this report only surfaces the two entry kinds' inputs so that later
    /// work has something to look at, over the real, shipped catalog. Both `_.Count()` calls below are
    /// readings of the shipped roster, never pinned as an expected number.
    /// </summary>
    [Fact]
    public void Report_per_species_yield_across_both_entry_kinds()
    {
        _out.WriteLine("species,rung,shard(material),thetaContent(equipment)");
        foreach (var species in CreatureSpeciesCatalog.All.OrderBy(s => s.SpeciesId, StringComparer.Ordinal))
        {
            var rungId = species.BaseRarity.ToId();
            var shardId = CreatureYieldTuningHub.ShardFor(rungId);
            var thetaContent = RarityLadder.RungIndexOf(rungId) + 1;
            _out.WriteLine($"{species.SpeciesId},{rungId},{shardId},{thetaContent}");
        }

        var byRung = CreatureSpeciesCatalog.All.GroupBy(s => s.BaseRarity.ToId())
            .ToDictionary(g => g.Key, g => g.Count());
        foreach (var rungId in RarityLadder.RungIds)
            _out.WriteLine($"rung {rungId}: {byRung.GetValueOrDefault(rungId, 0)} species, " +
                            $"shard={CreatureYieldTuningHub.ShardFor(rungId)}, theta={RarityLadder.RungIndexOf(rungId) + 1}");
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
