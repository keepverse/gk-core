using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Expeditions;
using FusionRpg.Core.Items;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Expeditions;

/// <summary>
/// D3: ExpeditionResolver — pure chain+events. Per-tick derived RNG streams make recall
/// pro-rating trivially exact: elapsed ticks resolve identically whether or not the tail runs.
/// </summary>
[Collection(ExpeditionsSequentialCollection.Name)]
public class ExpeditionResolverTests
{
    static List<BattleActorSetup> Squad(int n = 2, int level = 5) =>
        Enumerable.Range(0, n).Select(i => new BattleActorSetup
        {
            Key = $"squad:{i}",
            Side = "squad",
            SpeciesId = "test-species",
            TypeId = 10_001,
            Level = level,
            MaxHp = BattleRuleset.BaseHp(level),
            Atk = BattleRuleset.BaseAtk(level),
            Defense = BattleRuleset.BaseDefense(level)
        }).ToList();

    static string Hash(ExpeditionResolution resolution) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resolution))));

    [Fact]
    public void Same_inputs_resolve_identically()
    {
        var a = ExpeditionResolver.Resolve("hunt-8h", Squad(), 77, elapsedTicks: 8);
        var b = ExpeditionResolver.Resolve("hunt-8h", Squad(), 77, elapsedTicks: 8);
        Assert.Equal(JsonSerializer.Serialize(a), JsonSerializer.Serialize(b));
    }

    [Fact]
    public void Recall_pro_rates_exactly_at_tick_boundaries()
    {
        var full = ExpeditionResolver.Resolve("forage-4h", Squad(3), 123, elapsedTicks: 8);
        var partial = ExpeditionResolver.Resolve("forage-4h", Squad(3), 123, elapsedTicks: 3);

        Assert.Equal(3, partial.Ticks.Count);
        Assert.Equal(
            JsonSerializer.Serialize(full.Ticks.Take(3)),
            JsonSerializer.Serialize(partial.Ticks));
        Assert.True(partial.Battles.Count <= full.Battles.Count);
        Assert.All(partial.Battles, b => Assert.True(b.TickIndex <= 3));

        var zero = ExpeditionResolver.Resolve("forage-4h", Squad(3), 123, elapsedTicks: 0);
        Assert.Empty(zero.Ticks);
        Assert.Empty(zero.Battles);
        Assert.Equal(0, zero.Rewards.EventSouls);
    }

    [Fact]
    public void Battle_chain_matches_the_tier_and_boss_lands_last()
    {
        foreach (var tier in ExpeditionTierCatalog.All)
        {
            var r = ExpeditionResolver.Resolve(tier.TierId, Squad(tier.SquadSlots), 9, tier.TickCount);
            var expected = tier.BattleCount + (tier.HasBossWave ? 1 : 0);
            Assert.Equal(expected, r.Battles.Count);
            Assert.All(r.Battles, b => Assert.True(WaveCatalog.IsKnown(b.Setup.WaveId)));
            if (tier.HasBossWave)
            {
                var boss = r.Battles.Last();
                Assert.True(boss.Boss);
                Assert.Equal(tier.TickCount, boss.TickIndex);
            }

            // Distinct per-battle seeds — one battle's rolls never shift another's.
            Assert.Equal(r.Battles.Count, r.Battles.Select(b => b.BattleSeed).Distinct().Count());
        }
    }

    [Fact]
    public void Wild_joins_respect_the_pool_rules()
    {
        // Sweep many seeds: every wild candidate must be summop-poolable — never capture-only,
        // never legendary (spec-expeditions.md §Never).
        for (ulong seed = 0; seed < 40; seed++)
        {
            var r = ExpeditionResolver.Resolve("warpath-20h", Squad(5), seed, 10);
            foreach (var tick in r.Ticks.Where(t => t.Kind == ExpeditionTickKinds.WildCreatureMet))
            {
                var species = CreatureSpeciesCatalog.Get(tick.WildSpeciesId!);
                Assert.NotEqual(CreatureAcquisition.CaptureOnly, species.Acquisition);
                Assert.NotEqual(CreatureRarity.Sunwoven, species.BaseRarity);
            }

            foreach (var join in r.Rewards.WildJoins)
                Assert.True(CreatureSpeciesCatalog.IsKnown(join.SpeciesId));
        }
    }

    [Fact]
    public void Souls_and_materials_are_bounded_and_validated()
    {
        var r = ExpeditionResolver.Resolve("warpath-20h", Squad(5), 4242, 10);
        Assert.True(r.Rewards.EventSouls >= 0);
        Assert.All(r.Rewards.Materials, m =>
        {
            Assert.True(CreatureMaterialCatalog.IsKnown(m.MaterialId), m.MaterialId);
            Assert.True(m.Qty > 0);
        });
        Assert.True(r.Rewards.SpecimenXpPerBattleWon > 0);
    }

    [Fact]
    public void Injury_debuffs_the_squad_for_later_battles()
    {
        // Find a seed whose timeline has an injury tick before a later battle, then assert the
        // injured member carries a power debuff in that battle's setup.
        for (ulong seed = 0; seed < 200; seed++)
        {
            var r = ExpeditionResolver.Resolve("warpath-20h", Squad(5), seed, 10);
            var injury = r.Ticks.FirstOrDefault(t => t.Kind == ExpeditionTickKinds.Injury);
            if (injury == null) continue;
            var laterBattle = r.Battles.FirstOrDefault(b => b.TickIndex > injury.TickIndex);
            if (laterBattle == null) continue;

            var member = laterBattle.Setup.Squad.Single(s => s.Key == injury.InjuredKey);
            // battle-hub-fuse T5: injuries ride as Hub inputs, not ChannelMods appends — and the
            // debuff reaches the composed snapshot through the ExpeditionInjurySubsystem twin.
            var injuries = member.HubInputs?.Injuries;
            Assert.NotNull(injuries);
            Assert.True(injuries!.TryGetValue(injury.InjuredKey!, out var count) && count > 0,
                "injured member must carry a positive injury count in its Hub inputs");
            var divisor = ExpeditionTuningHub.Tuning.EventRoll.InjuryPowerDivisor;
            var expected = (double)(count * -Math.Max(1, member.Atk / divisor));
            Assert.Equal(expected,
                BattleHubCompose.Compose(member).Get(DerivedStatChannels.CombatPowerOmni, 0));
            return; // proven
        }

        Assert.Fail("no seed in 0..199 produced an injury before a later battle — timeline math is off");
    }

    [Fact]
    public void Unknown_tier_and_empty_squad_reject()
    {
        Assert.Throws<ArgumentException>(() => ExpeditionResolver.Resolve("no-tier", Squad(), 1, 1));
        Assert.Throws<ArgumentException>(() =>
            ExpeditionResolver.Resolve("scout-30m", new List<BattleActorSetup>(), 1, 1));
    }

    /// <summary>spec-rarity-migration.md §4, superseded by species-gear-chain T31 (E3a): the old
    /// `ShardCommon`/`ShardRare` string-literal consts this test used to pin are gone — replaced by
    /// <see cref="CreatureYieldTuningHub.ShardFor"/>, a rung-keyed lookup over
    /// <c>gk-core/data/tuning/creature-yield.v1.json</c>. The guard's own concern (a shard id silently pointing
    /// at a retired legacy id) is now enforced structurally at LOAD time
    /// (<see cref="CreatureYieldTuningLoader.Parse"/> throws unless every value is one of the 27
    /// issuable ids) rather than pinned per-constant here; this test re-asserts the SAME property
    /// against every rung the live hub actually serves, so a future edit to the tuning file cannot
    /// reintroduce a legacy id without this failing.</summary>
    [Fact]
    public void Expedition_shard_lookup_references_only_live_ids()
    {
        foreach (var rungId in RarityLadder.RungIds)
        {
            var value = CreatureYieldTuningHub.ShardFor(rungId);
            Assert.Contains(value, CreatureMaterialCatalog.All);
            Assert.DoesNotContain(value, LegacyCreatureRarityIds.ForwardMap.Keys.Select(id => "shard." + id));
        }
    }

    // Re-blessed 2026-08-21 at battle RulesetVersion 2 (combat-unification): named
    // serialization-shape churn — BattleActorSetup gained InnateShield, so every embedded
    // plan changed bytes even though expedition MATH did not (per-tick streams unchanged;
    // Same_inputs/Recall tests prove the resolver itself byte-stable across the re-bless).
    //
    // Re-blessed 2026-08-24 at RulesetVersion 3 (T4.2, power-dial): Squad()'s actors sit at
    // level 5, away from the Theta=20 pin, so BaseHp/BaseAtk/BaseDefense legitimately moved with
    // bMilli 0->400 — expected magnitude movement, the same triage as BattleGoldenTests.cs.
    // Same_inputs_resolve_identically and the recall pro-rating tests stayed green unchanged,
    // confirming the resolver's OWN per-tick RNG logic did not move, only the embedded magnitudes.
    //
    // Re-blessed 2026-08-30 (aura-skill T12, Gate B): named serialization-shape churn again, the
    // SAME class of change as the 2026-08-21 InnateShield re-bless — BattleSetup gained
    // ActiveAuras (default empty, no behavior change for every existing caller including this one),
    // so every embedded plan's serialized JSON gained an "activeAuras":[] key and every hash moved.
    // Verified NOT a determinism break before re-blessing, not assumed: every other test in this
    // file (Same_inputs_resolve_identically, recall pro-rating) stayed green unchanged, confirming
    // the resolver's own math and RNG streams did not move — only the embedded BattleSetup's shape.
    //
    // Re-blessed 2026-08-31 in ONE step covering two roster changes made together: the species cap
    // removal (24 -> 84 species) and RarityForRank's legendary tier becoming proportional
    // (7 legendary instead of 2 at 84 species). Both feed WildBand, so they are one re-bless.
    //
    // Why the roster touches this golden at all — a DIFFERENT class from every re-bless
    // above — those were serialization shape or magnitude churn with the roster fixed. This one is
    // a genuine content change. WildBand (ExpeditionResolver.cs:231) picks wild enemies from
    // CreatureSpeciesCatalog.All filtered by rarity and ordered by SpeciesId, then indexes with
    // rng.NextInt(band.Count). Regenerating the catalog uncapped took it from 24 to 84 species, so
    // both the band's contents and its size changed and a different enemy is legitimately rolled.
    // Verified it is selection, not a determinism break: Same_inputs_resolve_identically and the
    // recall pro-rating tests stayed green unchanged, and Squad() uses a fixed "test-species" that
    // never touches the catalog — so the squad side of the resolution did not move at all.
    //
    // NOTE for the next capture: this golden is coupled to roster SIZE, so it moves every time
    // species are added. That is now expected rather than alarming, but it makes the test a poor
    // regression signal for the resolver itself — decoupling the wild-enemy pick from the live
    // catalog (a fixture band) would be the fix if the churn becomes annoying.
    // Re-blessed 2026-09-01 (seed-to-concrete T4.1) — the manifest now composes
    // shard.chaff/shard.cultivated (ten-rung ladder ids) instead of shard.common/shard.rare, per
    // this test's own comment above: coupled to roster/reward-id churn, expected to move, not a
    // regression signal. Squad size/theta/species set are unchanged; verified by reading the
    // resolver's own diff before re-blessing, not by inspection alone.
    // Re-blessed 2026-09-07 (combat-unification Phase 7 F1, owner decision: hybrid.
    // secondaryWeightMilli 0 -> 300) — checked before re-blessing, not assumed: `Squad()` above uses
    // a fixed synthetic "test-species" with no catalog-backed ElementSecondary, so the player side of
    // every resolve is unaffected. The wild-enemy side (`WildBand`, real `CreatureSpeciesCatalog.All`)
    // is not — 21/841 real species carry a genuine secondary element (measured live 2026-09-07), and
    // any of the four rolls landing one now embeds a real two-component `elementPayload` on that
    // enemy's own `BattleSetup` where it carried one component before, which is exactly what moves
    // this hash. The resolver's own RNG stream and which enemy gets picked are unaffected — only the
    // embedded setup's own shape changed, the same class of move this golden's own history already
    // names as expected, not a regression signal.
    // Re-blessed 2026-09-12 (vocabulary rename: the domain word for a summoned specimen becomes
    // "creature", reserving the old word for a future sub-race) — the resolved `WildCreatureMet`
    // tick's own `Kind` string is serialized into this hash, and it moved from "wild-demon-met" to
    // "wild-creature-met". Proven the sole cause, not assumed: restoring the old wire string in
    // `ExpeditionTickKinds.WildCreatureMet` reproduced these exact three previous hashes, and `hunt`
    // is unchanged below because none of its rolls landed a wild-meet tick. The resolver's own math,
    // RNG stream, and which enemy is picked are all unaffected — only one serialized literal moved,
    // the same class of shape churn this golden's own history already names as expected.
    // Re-blessed 2026-09-13 (actor-hub fuse T5, solid-run: injuries ride as Hub inputs, not
    // ChannelMods appends) — proven the sole cause, not assumed: dumped the full hunt-8h/3003
    // AND warpath-20h/4004 resolutions on both branches and diffed. Ticks identical (same wild
    // picks, same RNG stream), rewards identical; the ONLY delta is the embedded BattleSetup
    // injury representation: `ChannelMods: [{combat.power.omni, -7}]` became
    // `HubInputs.Injuries` (hunt: squad:0/squad:1; warpath: squad:1). Scout/forage rolls never
    // landed an injury tick, so their hashes are untouched — exactly what a representation-only
    // move predicts. Sibling determinism tests (Same_inputs_resolve_identically, recall
    // pro-rating) stayed green unchanged.
    // Re-blessed 2026-09-17 (solid-remediation T0.3) — species-gear-chain T19, the CreatureTypeId
    // collision fix (937390c3b). Proven the sole cause, not assumed: the formula itself is unchanged
    // (`floor + (plant ? 50_000 : 0) + gameTypeId`); what T19 fixed is that the SlotFilter anchor join
    // now carries `Side`, so a PLANT species finally receives the +50,000 offset it was silently
    // missing — the collision its commit message measures as "102 shared values" between plant and
    // zombie gameTypeIds. This golden serialises the whole `ExpeditionResolution`, and every tier here
    // embeds an `ExpeditionBattlePlan.Setup` carrying the enemy's `TypeId`; scout's is
    // `cherrythreepeater` at 61330, which is floor + 50_000 + 1330, i.e. the offset now applied.
    //
    // That is why ALL FOUR moved, including scout and forage, which the previous two re-blessings left
    // untouched: those were injury/wire-string changes that only some rolls reached, while an enemy
    // type id is embedded in every tier that resolves a battle. Checked and excluded as causes: the
    // resolver itself (`git diff` over gk-core/src/FusionRpg.Core/Expeditions/ since the last re-bless is
    // empty), `expeditions.v1.json` (unchanged), `BattleSetup`'s shape (no new fields — only new
    // `BaseResourceMax/Regen` statics), and the harness's `DefaultPower` (unchanged, still atk 92 /
    // defense 22). A real defect fix moved a serialized id, so the golden was stale, not regressed.
    //
    // Re-blessed 2026-09-19 (species-gear-chain T31, `creature-drop-tables` E3a) — the cause IS the
    // change under test, proven the sole one: `ExpeditionResolver`'s per-tick shard mint no longer
    // ternaries on `isBoss` (`shard.chaff`/`shard.cultivated` only) — it now keys on
    // `PlannedRungFor(setup.Wave)`, the HIGHEST `BaseRarity` among the wave's real, catalog-backed
    // enemies, looked up through `CreatureYieldTuningHub.ShardFor` (`gk-core/data/tuning/creature-yield.v1.json`).
    // Every tier's `Rewards.Materials` therefore embeds a DIFFERENT (and typically richer) shard id set
    // than the two-value ternary ever produced, which is exactly what moves all four hashes — the whole
    // point of this task. Verified not a determinism break: `Same_inputs_resolve_identically` and the
    // recall pro-rating tests stay green unchanged (same seed still yields the same manifest), and the
    // new `CreatureYieldTests.PlannedRungFor_draws_no_rng_it_is_a_pure_fold_over_the_fixed_wave_roster`
    // proves the shard choice depends only on the wave's fixed, `waveId`-seeded roster — never on the
    // expedition's own seed — so this re-bless is content/mechanism churn this task itself makes, not a
    // regression signal from anything else.
    // Re-blessed 2026-09-20 (features/mega-merge merge into cmdc/lane-b) — hunt AND warpath, the two
    // LARGER-squad/longer tiers (Squad(4)/8 ticks, Squad(5)/10 ticks). scout (Squad(2)/6) and forage
    // (Squad(3)/8) are unchanged, confirming the cause is narrow, not systemic. Traced, not guessed:
    // `git diff` of the old lane-b branch against mega-merge shows ZERO changes to this file,
    // ExpeditionResolver.cs, creature-yield.v1.json, or the compiled default species catalog — the
    // only change this merge actually makes that reaches expedition resolution is the aptitudes
    // v9->v10 rebase (this same merge commit): mega-merge's own aptitudes.v9 (solid-enforcement
    // `retire-atk`) removes the two Might/Ferocity -> progression.bonus.atk edges, which old lane-b's
    // v9 never had folded in until this merge. That edge removal changes a Might/Ferocity-aptitude
    // actor's derived attack magnitude, which can flip a battle outcome inside the resolver's own
    // per-tick simulation and shift the shared `SeededRng` stream position for every tick after it --
    // exactly the kind of cascade a LARGER roster (more actors, more ticks) is more likely to hit,
    // without touching the shard-by-rung mechanism T31 already re-blessed.
    const string ScoutHash = "E341747659ABA50D4E5D6F5247D899138E211EFC2E0D2518E9AB99B8E674F877";
    const string ForageHash = "F924C6E2C7D8686C26A0A2E3143BDD46FCF90CAC7369659FD8C3AAFB9FE0F7FD";
    const string HuntHash = "060949307199AF0A9EBDFAB1701BD53BF1B73F525C78A7701E8A69426B917956";
    const string WarpathHash = "1B9FA8DA0413D7ACD5C701A9D9AEF9F36318FF264C4E09A3F382C5893BCF409E";

    [Fact]
    public void Tier_goldens_are_locked()
    {
        var actual =
            $"scout:{Hash(ExpeditionResolver.Resolve("scout-30m", Squad(2), 1001, 6))}\n" +
            $"forage:{Hash(ExpeditionResolver.Resolve("forage-4h", Squad(3), 2002, 8))}\n" +
            $"hunt:{Hash(ExpeditionResolver.Resolve("hunt-8h", Squad(4), 3003, 8))}\n" +
            $"warpath:{Hash(ExpeditionResolver.Resolve("warpath-20h", Squad(5), 4004, 10))}";
        var expected = $"scout:{ScoutHash}\nforage:{ForageHash}\nhunt:{HuntHash}\nwarpath:{WarpathHash}";
        Assert.Equal(expected, actual);
    }

    // ---- rank floor (spec-species-rank.md §6, creature-seed Task 10) -----------------------------------

    [Fact]
    public void The_shipped_bottom_floor_admits_exactly_what_the_two_rules_admit()
    {
        // The pass-through proof, read from the resolver's OWN public output: every wild creature it
        // meets clears the shipped floor, is never capture-exclusive and is never Sunwoven — so the
        // rank predicate narrowed nothing. (The tier goldens above are the byte-level half of the same
        // proof: they are coupled to the band's own contents, so a rank predicate that changed
        // membership would move all four hashes.)
        var met = new HashSet<string>(StringComparer.Ordinal);
        for (ulong seed = 1; seed <= 40; seed++)
            foreach (var tick in ExpeditionResolver.Resolve("hunt-8h", Squad(), seed, elapsedTicks: 8).Ticks)
                if (tick.WildSpeciesId is { } id) met.Add(id);

        Assert.NotEmpty(met);
        foreach (var id in met)
        {
            var species = CreatureSpeciesCatalog.Get(id)!;
            Assert.True(CreatureRankFloors.Passes(CreatureRankFloors.ExpeditionWildBand, species.Rank));
            Assert.NotEqual(CreatureAcquisition.CaptureOnly, species.Acquisition & CreatureAcquisition.CaptureOnly);
            Assert.NotEqual(CreatureRarity.Sunwoven, species.BaseRarity);
        }
    }

    [Fact]
    public void A_floor_that_empties_every_wild_band_refuses_by_name_rather_than_indexing_nothing()
    {
        // A balance pass CAN raise this floor above every ranked species the catalog holds — and the
        // old two-step fallback (band -> Chaff band) then had nothing left to index. Refusing loudly
        // names the fix instead of an IndexOutOfRange the caller cannot act on.
        CreatureRankFloors.Configure(new CreatureRankTuning(
            1, CreatureRarityLadder.All.Select(r => r.ToId()).ToList(), Array.Empty<CreatureRankCell>(),
            CreatureRankFloors.DeclaredGates.ToDictionary(g => g, _ => CreatureRank.Almanac.ToId(),
                                                          StringComparer.Ordinal)));
        try
        {
            var refusal = Assert.Throws<InvalidOperationException>(() =>
            {
                for (ulong seed = 1; seed <= 40; seed++)
                    ExpeditionResolver.Resolve("hunt-8h", Squad(), seed, elapsedTicks: 8);
            });
            Assert.Contains("expeditionWildBand", refusal.Message);
        }
        finally
        {
            CreatureRankFloors.ResetToUnconfigured();
        }
    }
}
