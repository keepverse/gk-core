using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Expeditions;

public static class ExpeditionTickKinds
{
    public const string Battle = "battle";
    public const string BossBattle = "boss-battle";
    public const string Quiet = "quiet";
    public const string FoundSouls = "found-souls";
    public const string WildCreatureMet = "wild-creature-met";
    public const string Injury = "injury";
}

public sealed record ExpeditionTickOutcome(
    int TickIndex, string Kind, int BattleIndex, long Souls,
    string? WildSpeciesId, bool WildJoins, string? WildVariant, string? InjuredKey);

public sealed record ExpeditionBattlePlan(
    int BattleIndex, int TickIndex, bool Boss, BattleSetup Setup, ulong BattleSeed);

public sealed record MaterialDrop(string MaterialId, long Qty);

public sealed record WildJoinResult(string SpeciesId, string Variant, IReadOnlyList<string> TraitIds);

public sealed record ExpeditionRewards(
    long EventSouls,
    IReadOnlyList<MaterialDrop> Materials,
    IReadOnlyList<WildJoinResult> WildJoins,
    int SpecimenXpPerBattleWon);

public sealed record ExpeditionResolution(
    string TierId, int ElapsedTicks,
    IReadOnlyList<ExpeditionTickOutcome> Ticks,
    IReadOnlyList<ExpeditionBattlePlan> Battles,
    ExpeditionRewards Rewards);

/// <summary>
/// Pure chain+events resolver (spec-expeditions.md): (tier, squad, seed, elapsedTicks) →
/// tick outcomes + battle plans + rewards manifest. Every tick derives its own RNG stream from
/// the expedition seed, so recall pro-rating is exact by construction: elapsed ticks resolve
/// identically whether or not the tail ever runs. The battles themselves resolve later through
/// BattleEngine (via WebMatchService at collect) using the per-battle seeds minted here.
/// </summary>
public static class ExpeditionResolver
{
    // Event roll CEILINGS (‰, cumulative): quiet <400, found-souls <750, wild-creature-met <900,
    // injury ≥900 — i.e. band widths 400/350/150/100. Edit widths by moving every later ceiling.
    // Loaded (tunables-ssot.md T1) — see ExpeditionTuningHub. −25% Atk per injury, on the omni
    // power channel, is InjuryPowerDivisor's shape (divide by 4).
    static ExpeditionEventRollTuning R => ExpeditionTuningHub.Tuning.EventRoll;
    static int QuietCeilMilli => R.QuietCeilMilli;
    static int FoundSoulsCeilMilli => R.FoundSoulsCeilMilli;
    static int WildCeilMilli => R.WildCeilMilli;
    static int WildJoinMilli => R.WildJoinMilli;
    static int ShinyDie => R.ShinyDie;
    static int InjuryPowerDivisor => R.InjuryPowerDivisor;

    public static ExpeditionResolution Resolve(
        string tierId, IReadOnlyList<BattleActorSetup> squad, ulong seed, int elapsedTicks)
    {
        var tier = ExpeditionTierCatalog.Get(tierId);
        if (squad.Count == 0) throw new ArgumentException("Squad is empty.");
        if (squad.Count > tier.SquadSlots) throw new ArgumentException("Squad exceeds tier slots.");
        var elapsed = Math.Clamp(elapsedTicks, 0, tier.TickCount);

        var battleTicks = BattleTickIndices(tier);
        var waveChain = WaveChain(tier);
        var (soulsMin, soulsMax) = SoulsRange(tier);

        var ticks = new List<ExpeditionTickOutcome>();
        var battles = new List<ExpeditionBattlePlan>();
        var materials = new SortedDictionary<string, long>(StringComparer.Ordinal);
        var wildJoins = new List<WildJoinResult>();
        var injuries = new Dictionary<string, int>(StringComparer.Ordinal);
        long eventSouls = 0;
        var battleIndex = 0;

        for (var t = 1; t <= elapsed; t++)
        {
            if (battleTicks.TryGetValue(t, out var isBoss))
            {
                var waveId = isBoss ? BossWaveId : waveChain[Math.Min(battleIndex, waveChain.Length - 1)];
                var setup = new BattleSetup
                {
                    WaveId = waveId,
                    Squad = ApplyInjuries(squad, injuries),
                    Wave = WaveCatalog.Get(waveId).Enemies
                };
                var battleSeed = SeededRng.DeriveStream(seed, "battle:" + battleIndex).NextULong();
                battles.Add(new ExpeditionBattlePlan(battleIndex, t, isBoss, setup, battleSeed));
                // E3a (species-gear-chain T31, spec-creature-drop-tables.md § Design 3): the shard
                // keys on the KILLED SPECIES' OWN RUNG, from the planned wave — not on isBoss. Shards
                // still drop at plan time, win or lose — the resolver never sees battle outcomes (they
                // resolve later at collect), and win-gating here would break the manifest's
                // determinism, so the rung is derived from the PLANNED enemies (known here), never
                // from actual kills (not known here). Outcome-dependent rewards stay the battle
                // reports' own job.
                var shardId = CreatureYieldTuningHub.ShardFor(PlannedRungFor(setup.Wave));
                materials.TryGetValue(shardId, out var have);
                materials[shardId] = have + 1;
                ticks.Add(new ExpeditionTickOutcome(
                    t, isBoss ? ExpeditionTickKinds.BossBattle : ExpeditionTickKinds.Battle,
                    battleIndex, 0, null, false, null, null));
                battleIndex++;
                continue;
            }

            var rng = SeededRng.DeriveStream(seed, "tick:" + t);
            var roll = rng.NextPerMille();
            if (roll < QuietCeilMilli)
            {
                ticks.Add(new ExpeditionTickOutcome(t, ExpeditionTickKinds.Quiet, -1, 0, null, false, null, null));
            }
            else if (roll < FoundSoulsCeilMilli)
            {
                var souls = soulsMin + rng.NextInt(soulsMax - soulsMin + 1);
                eventSouls += souls;
                ticks.Add(new ExpeditionTickOutcome(t, ExpeditionTickKinds.FoundSouls, -1, souls, null, false, null, null));
            }
            else if (roll < WildCeilMilli)
            {
                var species = RollWildSpecies(rng);
                var joins = rng.NextPerMille() < WildJoinMilli;
                var variant = rng.NextInt(ShinyDie) == 0 ? "shiny" : "normal";
                if (joins)
                {
                    // Traits roll here, on the tick's own stream — the whole rewards manifest is
                    // Core's (spec-expeditions §Resolver); splitting streams across layers invites
                    // an unseen name collision (2026-08-21 review I3).
                    wildJoins.Add(new WildJoinResult(species.SpeciesId, variant,
                        SummonRoller.RollTraits(species, species.BaseRarity, rng)));
                }
                else
                {
                    // The creature slips away but sheds a trace of its element.
                    var essence = "essence." + species.ElementPrimary.ToElementId();
                    materials.TryGetValue(essence, out var have);
                    materials[essence] = have + 1;
                }

                ticks.Add(new ExpeditionTickOutcome(
                    t, ExpeditionTickKinds.WildCreatureMet, -1, 0, species.SpeciesId, joins,
                    joins ? variant : null, null));
            }
            else
            {
                var victim = squad[rng.NextInt(squad.Count)].Key;
                injuries.TryGetValue(victim, out var count);
                injuries[victim] = count + 1;
                ticks.Add(new ExpeditionTickOutcome(t, ExpeditionTickKinds.Injury, -1, 0, null, false, null, victim));
            }
        }

        // Greedy squad members raise the found-souls take (their battle-loot half rides the
        // battle reports' SoulLootMilli). Part of the manifest, so goldens cover it.
        var greedyBonus = TraitBattleCatalog.Get("greedy").SoulLootBonusMilli;
        var greedyCount = squad.Count(s => s.TraitIds.Contains("greedy", StringComparer.Ordinal));
        eventSouls = eventSouls * (1000 + greedyBonus * greedyCount) / 1000;

        return new ExpeditionResolution(
            tier.TierId, elapsed, ticks, battles,
            new ExpeditionRewards(
                eventSouls,
                materials.Select(kv => new MaterialDrop(kv.Key, kv.Value)).ToList(),
                wildJoins,
                XpPerBattleWon(tier)));
    }

    const string BossWaveId = "rift-tyrant";

    /// <summary>
    /// E3a's single derivation, consumed by <see cref="CreatureYieldTuningHub.ShardFor"/> (species-gear-chain
    /// T31): the HIGHEST rung among the wave's planned enemies — a wave fields several enemies at once
    /// (`WaveCatalog.Enemies`), and the mint stays per-TICK, not per-enemy (spec-creature-drop-tables.md
    /// § "E3a is NOT one read", point 3), so one rung must represent the whole tick. The wave roster
    /// itself is a fixed, `waveId`-seeded compile (`WaveCatalog.StableSeed`), never re-rolled per
    /// expedition run, so this stays plan-time-deterministic by construction — no RNG is drawn here.
    /// `.Max()` over the enum, never a bare relational compare against a named member
    /// (`CreatureRarityLadderGuardTests` forbids that shape; ordinal `Max`/`OrderBy` is the sanctioned
    /// one, per that guard's own "safe shapes" list).
    /// </summary>
    internal static string PlannedRungFor(IReadOnlyList<BattleActorSetup> wave) =>
        wave.Select(e => CreatureSpeciesCatalog.Get(e.SpeciesId).BaseRarity).Max().ToId();

    /// <summary>The tier's fixed battle schedule (index, tick, boss) — pure tier math, exposed so
    /// callers can reason about logged battles without resolving a timeline.</summary>
    public static IReadOnlyList<(int BattleIndex, int TickIndex, bool Boss)> BattleSchedule(ExpeditionTierDef tier)
    {
        var schedule = new List<(int, int, bool)>();
        var i = 0;
        foreach (var (tick, boss) in BattleTickIndices(tier).OrderBy(kv => kv.Key))
            schedule.Add((i++, tick, boss));
        return schedule;
    }

    /// <summary>Battle ticks evenly spaced at b·T/(B+1); the boss wave sits on the final tick.</summary>
    static Dictionary<int, bool> BattleTickIndices(ExpeditionTierDef tier)
    {
        var map = new Dictionary<int, bool>();
        for (var b = 1; b <= tier.BattleCount; b++)
            map[Math.Max(1, b * tier.TickCount / (tier.BattleCount + 1))] = false;
        if (tier.HasBossWave)
            map[tier.TickCount] = true;
        return map;
    }

    static string[] WaveChain(ExpeditionTierDef tier) => tier.TierId switch
    {
        "scout-30m" => new[] { "rift-skirmish" },
        "forage-4h" => new[] { "rift-skirmish", "rift-warband" },
        "hunt-8h" => new[] { "rift-skirmish", "rift-warband", "rift-onslaught" },
        "warpath-20h" => new[] { "rift-warband", "rift-onslaught", "rift-onslaught", "rift-tyrant" },
        _ => throw new ArgumentException($"No wave chain for tier '{tier.TierId}'.")
    };

    static (int Min, int Max) SoulsRange(ExpeditionTierDef tier) => tier.TierId switch
    {
        "scout-30m" => (5, 15),
        "forage-4h" => (20, 50),
        "hunt-8h" => (40, 90),
        _ => (80, 180)
    };

    static int XpPerBattleWon(ExpeditionTierDef tier) => tier.TierId switch
    {
        "scout-30m" => 10,
        "forage-4h" => 20,
        "hunt-8h" => 30,
        _ => 40
    };

    /// <summary>Wild pool: summonable-adjacent species only — never capture-exclusive, never the
    /// top-band rung. Rarity bands 84/15/1 (‰-scaled) with fallback to the bottom rung on an empty
    /// band. Renamed to the ten-rung ladder (seed-to-concrete T4.1) via the SAME band each old
    /// value migrated to (ssot-rarity.md §4.3) — behaviour-preserving: today's 84-species catalog
    /// only populates these four rungs.</summary>
    static CreatureSpeciesDef RollWildSpecies(SeededRng rng)
    {
        var rarityRoll = rng.NextPerMille();
        var rarity = rarityRoll < 840 ? CreatureRarity.Chaff
            : rarityRoll < 990 ? CreatureRarity.Cultivated
            : CreatureRarity.Heirloom;
        var band = WildBand(rarity);
        if (band.Count == 0)
        {
            // Fallback: the LOWEST rung that still holds wild species AFTER the rank floor. A fixed
            // Chaff lookup is only correct while that floor sits at the bottom rung — a floor above
            // the Chaff band empties that band too (spec-species-rank.md §6's own raised-floor case),
            // and the old two-step then indexed an empty list.
            foreach (var rung in CreatureRarityLadder.All)
            {
                band = WildBand(rung);
                if (band.Count > 0) break;
            }
        }
        if (band.Count == 0)
            // A floor that empties EVERY band is a tuning defect, not a corpus state: refused by name
            // rather than indexing into nothing. It can only fire when a balance pass raises
            // `expeditionWildBand` above every ranked species the catalog holds.
            throw new InvalidOperationException(
                "no wild species clears expeditionWildBand's floor " +
                $"({CreatureRankFloors.FloorFor(CreatureRankFloors.ExpeditionWildBand)}) at any rung — " +
                "retune that floor in creature-rank.v1.json, or the catalog carries no ranked species");
        return band[rng.NextInt(band.Count)];
    }

    /// <summary>The wild pool for one rarity band: summonable-adjacent species only — never
    /// capture-exclusive, never the top-band rung. spec-species-rank.md §6: the rank floor lands BESIDE
    /// those two rules, never replacing them, because rank can only narrow — `CaptureOnly` still refuses
    /// and `Sunwoven` is still excluded. At the shipped bottom rung it admits everything the two rules
    /// above already admit (the band is byte-identical to pre-rank, which this project's own hash golden
    /// proves), and a species with no rank maps to the bottom rung AT THIS GATE, so the ladder never sees
    /// a null.</summary>
    static List<CreatureSpeciesDef> WildBand(CreatureRarity rarity) =>
        CreatureSpeciesCatalog.All
            .Where(s => s.Acquisition != CreatureAcquisition.CaptureOnly
                        && s.BaseRarity == rarity
                        && s.BaseRarity != CreatureRarity.Sunwoven
                        && CreatureRankFloors.Passes(CreatureRankFloors.ExpeditionWildBand, s.Rank))
            .OrderBy(s => s.SpeciesId, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// channelmods-hub T2 — the Hub twin of <see cref="ApplyInjuries"/>: one victim's injury count
    /// as attributed <c>Flat</c> derived contributions on <c>combat.power.omni</c> (source
    /// <c>grant:injury:{actorKey}</c> through <see cref="ContributionSourceIds.Grant"/> — GG-49 has
    /// no injury family, and the plan's Ask-first default reuses families already in use).
    /// <see cref="Derived.Subsystems.ExpeditionInjurySubsystem"/> contributes these on the Hub path;
    /// battles keep consuming <see cref="ApplyInjuries"/> until battle-hub-fuse.
    /// Re-home only: the per-injury magnitude below is byte-for-byte <see cref="ApplyInjuries"/>'s.
    /// </summary>
    public static IReadOnlyList<DerivedModifier> InjuryDerivedModifiers(string actorKey, int count, long atk)
    {
        if (count <= 0) return Array.Empty<DerivedModifier>();
        var source = ContributionSourceIds.Grant($"injury:{actorKey}");
        return Enumerable.Range(0, count).Select(_ => new DerivedModifier(
            DerivedStatChannels.CombatPowerOmni, DerivedModifierOp.Flat,
            -Math.Max(1, atk / InjuryPowerDivisor), SourceId: source)).ToList();
    }

    // battle-hub-fuse T5: injuries reach battle as Hub inputs (resolved through the
    // ExpeditionInjurySubsystem twin), not pre-folded ChannelMods. The map is snapshotted — the
    // plan keeps accumulating later ticks into `injuries`, and this battle must keep its own view.
    // (The old BattleChannelMod append is retired with the composer in T6.)
    static IReadOnlyList<BattleActorSetup> ApplyInjuries(
        IReadOnlyList<BattleActorSetup> squad, Dictionary<string, int> injuries)
    {
        if (injuries.Count == 0) return squad;
        var snapshot = new Dictionary<string, int>(injuries, StringComparer.Ordinal);
        return squad.Select(s =>
        {
            if (!snapshot.TryGetValue(s.Key, out var count) || count == 0) return s;
            return s with
            {
                HubInputs = (s.HubInputs ?? new BattleHubInputs()) with { Injuries = snapshot }
            };
        }).ToList();
    }
}
