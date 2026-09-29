using FusionRpg.Contracts;
using FusionRpg.Core.Match;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Time;

namespace FusionRpg.Core;

/// <summary>
/// Server-side board simulation (no Unity). Optional <see cref="MatchOverlay"/> folds
/// capture kinds into MatchRuntime (W1-F) so Sim lists are not the overlay living SSOT.
/// </summary>
public sealed class SimEngine
{
    public MatchTracker Tracker { get; } = new();
    public List<SimEntity> Plants { get; } = new();
    public List<SimEntity> Zombies { get; } = new();
    public List<SimMower> Mowers { get; } = new();
    public string? LevelName { get; private set; }
    public string? MatchKey { get; private set; }
    public int Wave { get; private set; }
    public int MaxWave { get; private set; }
    public StatsConfig LastStats { get; private set; } = new();
    public StatSystem Stats { get; } = StatSystemBootstrap.CreateDefault();
    /// <summary>Sole Hot compose gate over <see cref="Stats"/> (ADR 2026-09-07).</summary>
    public FusionRpg.Core.Stats.Derived.ActorHub ActorHub { get; }

    /// <summary>Optional overlay fold target (W1-F). Null = sim-only lists.</summary>
    public MatchRuntime? MatchOverlay { get; private set; }

    int _plantSeq;
    int _zombieSeq;
    int _mowerSeq;

    // Shield probe (combat-unification, sim-adoption): sim mounts the SSOT shield runtime
    // behind the shared apply pipeline in direct-sink mode — sim has no funnel by design.
    // Neutral snapshots: sim entities carry no derived channels. No tick host in sim, so
    // shields persist until broken or the board resets (durations are not supported here).
    readonly Combat.Shield.ShieldRuntime _shields = new();
    readonly Combat.Shield.ShieldGate _shieldGate;
    readonly Combat.IHpDeltaSink _hpSink;

    public SimEngine()
    {
        ActorHub = FusionRpg.Core.Stats.Derived.ActorHubBootstrap.CreateDefault(Stats);
        _shieldGate = new Combat.Shield.ShieldGate(_shields, static (_, _) =>
            new Combat.CombatActorSnapshot(
                global::FusionRpg.Core.Stats.Derived.ActorDerivedSnapshot.StubNeutral(),
                global::FusionRpg.Core.Stats.Derived.ActorElementTypes.Neutral));
        _hpSink = new SimHpSink(this);
    }

    sealed class SimHpSink : Combat.IHpDeltaSink
    {
        readonly SimEngine _engine;
        public SimHpSink(SimEngine engine) => _engine = engine;

        public bool Apply(string ownerKey, long amount, string? pluginId, string? effectId,
            string? grantId, string channel, List<FusionRpg.Contracts.ElementPayloadComponentDto>? elements)
        {
            var ptr = ownerKey.StartsWith("entity:", StringComparison.Ordinal)
                ? ownerKey.Substring("entity:".Length)
                : ownerKey;
            var e = _engine.Plants.FirstOrDefault(p => p.Ptr == ptr)
                    ?? _engine.Zombies.FirstOrDefault(z => z.Ptr == ptr);
            if (e is null) return false;
            // Damage keeps the legacy semantics (floor 0, NO MaxHp clamp — overhealed sim
            // entities lose only what was dealt); only heals clamp up at MaxHp.
            var next = e.Hp + amount;
            e.Hp = (int)(amount < 0 ? Math.Max(0, next) : Math.Min(e.MaxHp, next));
            return true;
        }
    }

    public int NextPlantNumber => _plantSeq + 1;
    public int NextZombieNumber => _zombieSeq + 1;
    public int NextMowerNumber => _mowerSeq + 1;

    /// <summary>Attach a MatchRuntime; subsequent SimResult events are Applied to it.</summary>
    public MatchRuntime EnableMatchOverlay(MatchRuntime? runtime = null)
    {
        MatchOverlay = runtime ?? new MatchRuntime();
        return MatchOverlay;
    }

    public void DisableMatchOverlay() => MatchOverlay = null;

    public void Reset()
    {
        Tracker.Clear();
        Plants.Clear();
        Zombies.Clear();
        Mowers.Clear();
        _shields.Clear();
        LevelName = null;
        MatchKey = null;
        Wave = 0;
        MaxWave = 0;
        LastStats = new StatsConfig();
        _plantSeq = 0;
        _zombieSeq = 0;
        _mowerSeq = 0;
        Stats.ClearBaselines();
        MatchOverlay = null;
    }

    public SimResult CatalogTypes()
    {
        return Ok(Evt("catalog.types", new Dictionary<string, object>
        {
            ["plants"] = new object[]
            {
                new Dictionary<string, object> { ["type"] = 0, ["typeName"] = SimDefaults.PlantTypeName, ["displayName"] = "Peashooter" }
            },
            ["zombies"] = new object[]
            {
                new Dictionary<string, object> { ["type"] = 0, ["typeName"] = SimDefaults.ZombieTypeName, ["displayName"] = "Zombie" }
            }
        }));
    }

    public SimResult BoardStart(SimBoardStartRequest? req)
    {
        Tracker.Clear();
        Plants.Clear();
        Zombies.Clear();
        Mowers.Clear();
        _shields.Clear();
        Stats.ClearBaselines();
        LevelName = string.IsNullOrWhiteSpace(req?.LevelName) ? SimDefaults.LevelName : req!.LevelName!;
        MatchKey = string.IsNullOrWhiteSpace(req?.MatchKey) ? Guid.NewGuid().ToString() : req!.MatchKey!;
        Wave = 0;
        return WithKey(Ok(Evt("board.start", new Dictionary<string, object>
        {
            ["levelName"] = LevelName,
            ["levelType"] = req?.LevelType ?? "Advanture",
            ["boardLevel"] = req?.BoardLevel ?? 1,
            ["modifiers"] = new Dictionary<string, object>
            {
                ["zombieHealthMultiplier"] = req?.ZombieHealthMultiplier ?? 1f
            }
        })));
    }

    public SimResult BoardEnd(SimBoardEndRequest? req)
    {
        var summary = req?.Summary ?? new Dictionary<string, object>();
        var name = req?.LevelName ?? LevelName ?? SimDefaults.LevelName;
        var result = Ok(Evt("board.end", new Dictionary<string, object>
        {
            ["levelName"] = name,
            ["summary"] = summary
        }));
        FoldOverlay(result);
        Tracker.Clear();
        Plants.Clear();
        Zombies.Clear();
        Mowers.Clear();
        _shields.Clear();
        Stats.ClearBaselines();
        var key = MatchKey;
        MatchKey = null;
        result.MatchKey = key;
        return result;
    }

    public SimResult MatchResult(SimMatchResultRequest? req) =>
        WithKey(Ok(Evt("match.result", new Dictionary<string, object> { ["result"] = req?.Result ?? "victory" })));

    public SimResult BoardSnapshot(SimSnapshotRequest? req)
    {
        req ??= new SimSnapshotRequest();
        return WithKey(Ok(Evt("board.snapshot", new Dictionary<string, object>
        {
            ["sun"] = req.Sun ?? 50,
            ["wave"] = req.Wave ?? Wave,
            ["maxWave"] = req.MaxWave ?? MaxWave,
            ["mowerUsedCount"] = req.MowerUsedCount ?? 0,
            ["plantsPlanted"] = req.PlantsPlanted ?? 0,
            ["plantsDied"] = req.PlantsDied ?? 0,
            ["zombiesKilled"] = req.ZombiesKilled ?? 0,
            ["duration"] = req.Duration ?? 0d,
            ["gameResult"] = req.GameResult ?? "Victory",
            ["sunProduced"] = req.SunProduced ?? 100,
            ["sunConsumed"] = req.SunConsumed ?? 50,
            ["totalZombieDamage"] = req.TotalZombieDamage ?? 0f,
            ["plantsShoveled"] = req.PlantsShoveled ?? 0,
            ["zombiesMindControlled"] = req.ZombiesMindControlled ?? 0,
            ["moneyEarned"] = req.MoneyEarned ?? 0
        })));
    }

    public SimResult MatchWin() => WithKey(Ok(Evt("match.win", new Dictionary<string, object>())));

    public SimResult MatchLose() => WithKey(Ok(Evt("match.lose", new Dictionary<string, object>())));

    public SimResult WaveChange(SimWaveRequest? req)
    {
        Wave = req?.Wave ?? Wave + 1;
        MaxWave = req?.MaxWave ?? MaxWave;
        return WithKey(Ok(Evt("wave.change", new Dictionary<string, object>
        {
            ["wave"] = Wave,
            ["maxWave"] = MaxWave
        })));
    }

    public SimResult SpawnPlant(StatsConfig stats, SimSpawnPlantRequest? req)
    {
        LastStats = stats;
        req ??= new SimSpawnPlantRequest();
        var hp = req.Hp ?? SimDefaults.PlantHp;
        var maxHp = req.MaxHp ?? hp;
        var atk = req.Attack ?? SimDefaults.PlantAttack;
        var ptr = string.IsNullOrWhiteSpace(req.Ptr) ? $"P{++_plantSeq}" : req.Ptr!;

        if (!Tracker.TryApply(ptr))
            return WithKey(new SimResult { Skipped = true });

        var entity = new SimEntity
        {
            Side = "plant",
            Ptr = ptr,
            Type = req.Type ?? 0,
            TypeName = string.IsNullOrWhiteSpace(req.TypeName) ? SimDefaults.PlantTypeName : req.TypeName!,
            HpBase = hp,
            Hp = hp,
            MaxHpBase = maxHp,
            MaxHp = maxHp,
            AttackBase = atk,
            Attack = atk,
            Col = req.Col ?? 0,
            Row = req.Row ?? 0
        };
        var events = new List<EventEnvelope>();
        var before = entity.Hp;
        var baseline = Stats.CaptureOrGet(ptr, () => new EntityBaseline
        {
            Hp = hp,
            MaxHp = maxHp,
            Atk = atk
        });
        var ctx = Stats.Contexts.ForPlant(ptr, baseline, entity.Type, MatchKey, cheatScale: stats, applyStats: stats.ApplyStats);
        var final = ActorHub.Resolve(ctx).AppliedCombat;
        if (stats.ApplyStats)
        {
            entity.MaxHp = final.MaxHp;
            entity.Hp = final.Hp;
            entity.Attack = final.Atk;
            events.Add(Evt("stat.applied", new Dictionary<string, object>
            {
                ["side"] = "plant",
                ["type"] = entity.Type,
                ["typeName"] = entity.TypeName,
                ["ptr"] = ptr,
                ["hpBefore"] = before,
                ["hpAfter"] = entity.Hp,
                ["attackBefore"] = atk,
                ["attackAfter"] = entity.Attack
            }));
        }
        Plants.Add(entity);
        events.Add(Evt("plant.spawn", PlantDump(entity, "start")));
        var result = new SimResult();
        result.Events.AddRange(events);
        return WithKey(result);
    }

    public SimResult SpawnZombie(StatsConfig stats, SimSpawnZombieRequest? req)
    {
        LastStats = stats;
        req ??= new SimSpawnZombieRequest();
        var hp = req.Hp ?? SimDefaults.ZombieHp;
        var maxHp = req.MaxHp ?? hp;
        var atk = req.Attack ?? SimDefaults.ZombieAttack;
        var armor = req.Armor ?? 0;
        var armorMax = req.ArmorMax ?? armor;
        var ptr = string.IsNullOrWhiteSpace(req.Ptr) ? $"Z{++_zombieSeq}" : req.Ptr!;

        if (!Tracker.TryApply(ptr))
            return WithKey(new SimResult { Skipped = true });

        var entity = new SimEntity
        {
            Side = "zombie",
            Ptr = ptr,
            Type = req.Type ?? 0,
            TypeName = string.IsNullOrWhiteSpace(req.TypeName) ? SimDefaults.ZombieTypeName : req.TypeName!,
            HpBase = hp,
            Hp = hp,
            MaxHpBase = maxHp,
            MaxHp = maxHp,
            AttackBase = atk,
            Attack = atk,
            ArmorBase = armor,
            Armor = armor,
            ArmorMax = armorMax
        };
        var events = new List<EventEnvelope>();
        var before = entity.Hp;
        var baseline = Stats.CaptureOrGet(ptr, () => new EntityBaseline
        {
            Hp = hp,
            MaxHp = maxHp,
            Atk = atk,
            Arm1 = armor,
            Arm1Max = armorMax
        });
        var ctx = Stats.Contexts.ForZombie(ptr, baseline, entity.Type, MatchKey, cheatScale: stats, applyStats: stats.ApplyStats);
        var final = ActorHub.Resolve(ctx).AppliedCombat;
        if (stats.ApplyStats)
        {
            entity.MaxHp = final.MaxHp;
            entity.Hp = final.Hp;
            entity.Attack = final.Atk;
            if (armorMax > 0) entity.ArmorMax = final.Arm1Max;
            if (armor > 0) entity.Armor = final.Arm1;
            events.Add(Evt("stat.applied", new Dictionary<string, object>
            {
                ["side"] = "zombie",
                ["type"] = entity.Type,
                ["typeName"] = entity.TypeName,
                ["ptr"] = ptr,
                ["hpBefore"] = before,
                ["hpAfter"] = entity.Hp,
                ["attackBefore"] = atk,
                ["attackAfter"] = entity.Attack
            }));
        }
        Zombies.Add(entity);
        events.Add(Evt("zombie.spawn", ZombieDump(entity,
            string.IsNullOrWhiteSpace(req.Source) ? "initHealth" : req.Source!,
            req.SourceKind, req.SourceId)));
        var result = new SimResult();
        result.Events.AddRange(events);
        return WithKey(result);
    }

    public SimResult DamagePlant(StatsConfig stats, SimDamageRequest req)
    {
        LastStats = stats;
        var e = Plants.FirstOrDefault(p => p.Ptr == req.Ptr);
        if (e is null) return Fail("plant not found");
        var incoming = req.Damage ?? SimDefaults.HitDamage;
        var before = incoming;
        var after = incoming;
        if (stats.ApplyStats)
        {
            var baseline = new EntityBaseline { Hp = 1, MaxHp = 1, Atk = 1 };
            var ctx = Stats.Contexts.ForPlant(e.Ptr, baseline, cheatScale: stats, applyStats: true);
            var final = ActorHub.Resolve(ctx).AppliedCombat;
            after = StatMath.ScaleIncoming(incoming, final.DefensePercent, final.DefenseFlat);
        }

        var applied = Combat.DamageApplyPipeline.Apply(
            e.Ptr, -after, 1, Array.Empty<Combat.Element.ElementPayloadComponent>(),
            attackerSnapshot: null, ownerSnapshot: null, _shieldGate, _hpSink, pluginId: "sim");
        var result = new SimResult();
        if (stats.LogDamage)
        {
            var payload = new Dictionary<string, object>
            {
                ["ptr"] = e.Ptr,
                ["damage"] = after,
                ["before"] = before,
                ["after"] = after
            };
            if (applied.AbsorbedAmount > 0)
                payload["shieldAbsorbed"] = applied.AbsorbedAmount;
            result.Events.Add(Evt("plant.damage", payload));
        }

        return WithKey(result);
    }

    public SimResult DamageZombie(StatsConfig stats, SimDamageRequest req)
    {
        LastStats = stats;
        var e = Zombies.FirstOrDefault(z => z.Ptr == req.Ptr);
        if (e is null) return Fail("zombie not found");
        var incoming = req.Damage ?? SimDefaults.HitDamage;
        var before = incoming;
        var after = incoming;
        if (stats.ApplyStats)
        {
            var baseline = new EntityBaseline { Hp = 1, MaxHp = 1, Atk = 1 };
            var ctx = Stats.Contexts.ForZombie(e.Ptr, baseline, cheatScale: stats, applyStats: true);
            var final = ActorHub.Resolve(ctx).AppliedCombat;
            after = StatMath.ScaleIncoming(incoming, final.DefensePercent, final.DefenseFlat);
        }

        var applied = Combat.DamageApplyPipeline.Apply(
            e.Ptr, -after, 1, Array.Empty<Combat.Element.ElementPayloadComponent>(),
            attackerSnapshot: null, ownerSnapshot: null, _shieldGate, _hpSink, pluginId: "sim");
        var result = new SimResult();
        if (stats.LogDamage)
        {
            var payload = new Dictionary<string, object>
            {
                ["ptr"] = e.Ptr,
                ["damage"] = after,
                ["before"] = before,
                ["after"] = after
            };
            if (applied.AbsorbedAmount > 0)
                payload["shieldAbsorbed"] = applied.AbsorbedAmount;
            result.Events.Add(Evt("zombie.damage", payload));
        }

        return WithKey(result);
    }

    /// <summary>
    /// Sim shield grant (sim-adoption): the server-side probe surface — no game required.
    /// Durations are unsupported (sim has no upkeep tick); shields persist until broken or
    /// the board resets.
    /// </summary>
    public SimResult GrantShield(SimShieldGrantRequest req)
    {
        var e = Plants.FirstOrDefault(p => p.Ptr == req.Ptr)
                ?? Zombies.FirstOrDefault(z => z.Ptr == req.Ptr);
        if (e is null) return Fail("entity not found");
        if (req.Amount <= 0) return Fail("amount must be > 0");
        global::FusionRpg.Core.Stats.Derived.ElementTypeId? element = null;
        if (!string.IsNullOrWhiteSpace(req.Element))
        {
            if (!global::FusionRpg.Core.Stats.Derived.ElementRoster.TryParse(req.Element, out var parsed))
                return Fail("unknown element " + req.Element);
            element = parsed;
        }

        var applied = _shieldGate.ApplyGrant(e.Ptr, new Combat.Shield.ShieldGrant
        {
            SourceId = "sim.shield:" + (string.IsNullOrWhiteSpace(req.SourceId) ? "manual" : req.SourceId),
            Element = element,
            BaseHp = req.Amount,
            Priority = req.Priority ?? Combat.Shield.ShieldPolicy.PrioritySkill,
            RefillOnMerge = true
        });
        var totals = _shields.Totals("entity:" + e.Ptr);
        return WithKey(Ok(Evt("shield.granted", new Dictionary<string, object>
        {
            ["ptr"] = e.Ptr,
            ["outcome"] = applied.Outcome.ToString(),
            ["shieldId"] = applied.Instance?.ShieldId ?? "",
            ["hp"] = totals.Hp,
            ["maxHp"] = totals.MaxHp
        })));
    }

    /// <summary>Current shield totals for a sim entity — state/dump surface.</summary>
    public (long Hp, long MaxHp) ShieldTotals(string ptr) => _shields.Totals("entity:" + ptr);

    public SimResult DiePlant(SimDieRequest req)
    {
        var e = Plants.FirstOrDefault(p => p.Ptr == req.Ptr);
        if (e is null) return Fail("plant not found");
        Plants.Remove(e);
        Stats.ForgetEntity(e.Ptr);
        return WithKey(Ok(Evt("plant.die", new Dictionary<string, object>
        {
            ["type"] = e.Type,
            ["typeName"] = e.TypeName,
            ["ptr"] = e.Ptr,
            ["reason"] = req.Reason ?? 0,
            ["reasonName"] = req.ReasonName ?? "Default"
        })));
    }

    public SimResult DieZombie(SimDieRequest req)
    {
        var e = Zombies.FirstOrDefault(z => z.Ptr == req.Ptr);
        if (e is null) return Fail("zombie not found");
        if (!Tracker.TryNoteZombieDead(e.Ptr))
            return WithKey(new SimResult { Skipped = true });
        Zombies.Remove(e);
        Stats.ForgetEntity(e.Ptr);
        return WithKey(Ok(Evt("zombie.die", new Dictionary<string, object>
        {
            ["type"] = e.Type,
            ["typeName"] = e.TypeName,
            ["ptr"] = e.Ptr,
            ["reason"] = req.Reason ?? 0
        })));
    }

    public SimResult PlaceMower(SimMowerRequest? req)
    {
        req ??= new SimMowerRequest();
        var ptr = string.IsNullOrWhiteSpace(req.Ptr) ? $"M{++_mowerSeq}" : req.Ptr!;
        var mower = Mowers.FirstOrDefault(m => m.Ptr == ptr);
        if (mower is null)
        {
            mower = new SimMower
            {
                Ptr = ptr,
                Type = req.Type ?? 0,
                TypeName = req.TypeName ?? "LawnMower",
                Row = req.Row ?? 0
            };
            Mowers.Add(mower);
        }
        return WithKey(Ok(Evt("mower.place", new Dictionary<string, object>
        {
            ["ptr"] = mower.Ptr,
            ["type"] = mower.Type,
            ["typeName"] = mower.TypeName,
            ["row"] = mower.Row
        })));
    }

    public SimResult StartMower(SimMowerRequest? req)
    {
        var ptr = req?.Ptr;
        var mower = string.IsNullOrWhiteSpace(ptr)
            ? Mowers.LastOrDefault()
            : Mowers.FirstOrDefault(m => m.Ptr == ptr);
        if (mower is null) return Fail("mower not found");
        mower.Started = true;
        return WithKey(Ok(Evt("mower.start", new Dictionary<string, object>
        {
            ["ptr"] = mower.Ptr,
            ["type"] = mower.Type,
            ["typeName"] = mower.TypeName
        })));
    }

    public SimResult DieMower(SimMowerRequest? req)
    {
        var ptr = req?.Ptr;
        var mower = string.IsNullOrWhiteSpace(ptr)
            ? Mowers.LastOrDefault()
            : Mowers.FirstOrDefault(m => m.Ptr == ptr);
        if (mower is null) return Fail("mower not found");
        mower.Dead = true;
        return WithKey(Ok(Evt("mower.die", new Dictionary<string, object> { ["ptr"] = mower.Ptr })));
    }

    public SimResult Bullet() => WithKey(Ok(Evt("bullet.init", new Dictionary<string, object>
    {
        ["ptr"] = "B",
        ["damage"] = 20,
        ["plantType"] = 0,
        ["plantTypeName"] = SimDefaults.PlantTypeName
    })));

    public SimResult PlacePlant(SimPlacePlantRequest? req)
    {
        req ??= new SimPlacePlantRequest();
        var ptr = string.IsNullOrWhiteSpace(req.Ptr) ? $"P{NextPlantNumber}" : req.Ptr!;
        return WithKey(Ok(Evt("plant.place", new Dictionary<string, object>
        {
            ["ptr"] = ptr,
            ["type"] = req.Type ?? 0,
            ["typeName"] = req.TypeName ?? SimDefaults.PlantTypeName,
            ["col"] = req.Col ?? 0,
            ["row"] = req.Row ?? 0,
            ["isFreeSet"] = false
        })));
    }

    public SimResult PlaceZombie(SimPlaceZombieRequest? req)
    {
        req ??= new SimPlaceZombieRequest();
        var ptr = string.IsNullOrWhiteSpace(req.Ptr) ? $"Z{NextZombieNumber}" : req.Ptr!;
        return WithKey(Ok(Evt("zombie.place", new Dictionary<string, object>
        {
            ["ptr"] = ptr,
            ["type"] = req.Type ?? 0,
            ["typeName"] = req.TypeName ?? SimDefaults.ZombieTypeName,
            ["row"] = req.Row ?? 0,
            ["mindControlled"] = req.MindControlled
        })));
    }

    public SimResult Mix(SimMixRequest? req)
    {
        req ??= new SimMixRequest();
        return WithKey(Ok(Evt("plant.mix", new Dictionary<string, object>
        {
            ["usedType"] = req.UsedType ?? 0,
            ["usedTypeName"] = req.UsedTypeName ?? SimDefaults.PlantTypeName,
            ["plantPtr"] = req.PlantPtr ?? "P1",
            ["row"] = req.Row ?? 0
        })));
    }

    public SimResult Economy(SimEconomyRequest? req)
    {
        req ??= new SimEconomyRequest();
        return WithKey(Ok(Evt("board.economy", new Dictionary<string, object>
        {
            ["sun"] = req.Sun ?? 50,
            ["money"] = req.Money ?? 0,
            ["points"] = req.Points ?? 0,
            ["plantedCount"] = req.PlantedCount ?? 0
        })));
    }

    public SimResult SunSpend(SimSunSpendRequest? req)
    {
        req ??= new SimSunSpendRequest();
        return WithKey(Ok(Evt("sun.spend", new Dictionary<string, object> { ["count"] = req.Count })));
    }

    public SimResult SetLevelName(SimLevelNameRequest? req) =>
        WithKey(Ok(Evt("level.name", new Dictionary<string, object> { ["levelName"] = req?.LevelName ?? "Named" })));

    public SimResult WaveSpawn(SimWaveRequest? req) =>
        WithKey(Ok(Evt("wave.spawn", new Dictionary<string, object> { ["wave"] = req?.Wave ?? Wave })));

    public SimResult CardUse(SimCardUseRequest? req)
    {
        req ??= new SimCardUseRequest();
        return WithKey(Ok(Evt("card.use", new Dictionary<string, object>
        {
            ["plantType"] = req.PlantType ?? 0,
            ["typeName"] = req.TypeName ?? SimDefaults.PlantTypeName,
            ["cost"] = req.Cost ?? 100,
            ["thePlantLevel"] = req.PlantLevel ?? 1
        })));
    }

    public SimResult Pet(SimPetRequest? req)
    {
        req ??= new SimPetRequest();
        return WithKey(Ok(Evt("pet.spawn", new Dictionary<string, object>
        {
            ["ptr"] = req.Ptr ?? "PET1",
            ["type"] = req.PetType ?? 0,
            ["typeName"] = req.TypeName ?? "PetGargantuar"
        })));
    }

    public SimResult Grid(SimGridRequest? req)
    {
        req ??= new SimGridRequest();
        return WithKey(Ok(Evt("grid.place", new Dictionary<string, object>
        {
            ["ptr"] = req.Ptr ?? "G1",
            ["type"] = req.Type ?? 0,
            ["typeName"] = req.TypeName ?? "Grave",
            ["col"] = req.Col ?? 0,
            ["row"] = req.Row ?? 0
        })));
    }

    public SimResult Hypno(SimDieRequest? req) =>
        WithKey(Ok(Evt("zombie.hypno", new Dictionary<string, object>
        {
            ["ptr"] = req?.Ptr ?? "Z1",
            ["controlLevel"] = 1,
            ["isMindControlled"] = true
        })));

    public SimResult Crash(SimDieRequest? req) =>
        WithKey(Ok(Evt("plant.crash", new Dictionary<string, object> { ["ptr"] = req?.Ptr ?? "P1", ["level"] = 1 })));

    public SimResult CatalogRecipes() =>
        Ok(Evt("catalog.recipes", new Dictionary<string, object>
        {
            ["entries"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["parentA"] = 0, ["parentAName"] = "Peashooter",
                    ["parentB"] = 1, ["parentBName"] = "Sunflower",
                    ["result"] = 2, ["resultName"] = "GatlingPea"
                }
            }
        }));

    public SimResult CatalogZombies() =>
        WithKey(Ok(Evt("catalog.zombies", new Dictionary<string, object>
        {
            ["levelType"] = "Advanture",
            ["levelNumber"] = 1,
            ["types"] = new object[] { new Dictionary<string, object> { ["type"] = 0, ["typeName"] = SimDefaults.ZombieTypeName } }
        })));

    public SimResult Recapture(SimEntityStatsRequest? req)
    {
        req ??= new SimEntityStatsRequest();
        var side = req.Side ?? "zombie";
        var e = side == "plant"
            ? Plants.FirstOrDefault(p => p.Ptr == req.Ptr)
            : Zombies.FirstOrDefault(z => z.Ptr == req.Ptr);
        if (e is null) return Fail("entity not found");
        if (req.Hp is { } hp) e.Hp = hp;
        var dump = side == "plant" ? PlantDump(e, req.Source ?? "setHealthInTravel") : ZombieDump(e, req.Source ?? "setHealthInTravel");
        dump["side"] = side;
        return WithKey(Ok(Evt("entity.stats", dump)));
    }

    public SimResult Bare(string kind, Dictionary<string, object>? payload = null) =>
        WithKey(Ok(Evt(kind, payload ?? new Dictionary<string, object>())));

    public object Snapshot() => new
    {
        levelName = LevelName,
        matchKey = MatchKey,
        wave = Wave,
        maxWave = MaxWave,
        nextPlant = NextPlantNumber,
        nextZombie = NextZombieNumber,
        nextMower = NextMowerNumber,
        plants = Plants,
        zombies = Zombies,
        mowers = Mowers,
        // Shield probe surface (sim-adoption): live totals per shielded entity.
        shields = Plants.Concat(Zombies)
            .Select(e => new { ptr = e.Ptr, shield = ShieldTotals(e.Ptr) })
            .Where(x => x.shield.MaxHp > 0)
            .Select(x => new { x.ptr, hp = x.shield.Hp, maxHp = x.shield.MaxHp })
            .ToArray(),
        applied = Tracker.Applied.ToArray(),
        lastStats = LastStats
    };

    Dictionary<string, object> PlantDump(SimEntity e, string source) => new()
    {
        ["type"] = e.Type,
        ["typeName"] = e.TypeName,
        ["displayName"] = e.TypeName,
        ["ptr"] = e.Ptr,
        ["col"] = e.Col,
        ["row"] = e.Row,
        ["thePlantHealth"] = e.Hp,
        ["thePlantMaxHealth"] = e.MaxHp,
        ["theShieldHealth"] = 0,
        ["rpgShieldHp"] = ShieldTotals(e.Ptr).Hp,      // sim-adoption: live sim shield totals
        ["rpgShieldMax"] = ShieldTotals(e.Ptr).MaxHp,
        ["attackDamage"] = e.Attack,
        ["theLevel"] = 1,
        ["shootingLevel"] = 0,
        ["limDamage"] = 0,
        ["thePlantAttackInterval"] = 1.5f,
        ["attackSpeedAdder"] = 0f,
        ["hpBase"] = e.HpBase,
        ["maxHpBase"] = e.MaxHpBase,
        ["hp"] = e.Hp,
        ["maxHp"] = e.MaxHp,
        ["attackBase"] = e.AttackBase,
        ["attack"] = e.Attack,
        ["source"] = source
    };

    Dictionary<string, object> ZombieDump(SimEntity e, string source, string? sourceKind = null, string? sourceId = null)
    {
        var dump = new Dictionary<string, object>
        {
        ["type"] = e.Type,
        ["typeName"] = e.TypeName,
        ["displayName"] = e.TypeName,
        ["ptr"] = e.Ptr,
        ["theHealth"] = e.Hp,
        ["theMaxHealth"] = e.MaxHp,
        ["theFirstArmorHealth"] = e.Armor,
        ["theFirstArmorMaxHealth"] = e.ArmorMax,
        ["theSecondArmorHealth"] = 0,
        ["theSecondArmorMaxHealth"] = 0,
        ["rpgShieldHp"] = ShieldTotals(e.Ptr).Hp,      // sim-adoption: live sim shield totals
        ["rpgShieldMax"] = ShieldTotals(e.Ptr).MaxHp,
        ["theAttackDamage"] = e.Attack,
        ["level"] = 1,
        ["theArmor"] = 0f,
        ["takeDmgMultiplier"] = 1f,
        ["damageMultiplier"] = 1f,
        ["theSpeed"] = 1f,
        ["theOriginSpeed"] = 1f,
        ["uniqueSpeed"] = 1f,
        ["currentAllHealth"] = e.Hp + e.Armor,
        ["totalAllHealth"] = e.MaxHp + e.ArmorMax,
        ["isMindControlled"] = false,
        ["hpBase"] = e.HpBase,
        ["maxHpBase"] = e.MaxHpBase,
        ["hp"] = e.Hp,
        ["maxHp"] = e.MaxHp,
        ["attackBase"] = e.AttackBase,
        ["attack"] = e.Attack,
        ["armorBase"] = e.ArmorBase,
        ["armor"] = e.Armor,
        ["armorMaxBase"] = e.ArmorMax,
        ["armorMax"] = e.ArmorMax,
        ["source"] = source
        };
        if (!string.IsNullOrWhiteSpace(sourceKind)) dump["sourceKind"] = sourceKind!;
        if (!string.IsNullOrWhiteSpace(sourceId)) dump["sourceId"] = sourceId!;
        return dump;
    }

    SimResult WithKey(SimResult r)
    {
        r.MatchKey = MatchKey;
        FoldOverlay(r);
        return r;
    }

    void FoldOverlay(SimResult r)
    {
        if (MatchOverlay == null || r.Events.Count == 0) return;
        foreach (var env in r.Events)
        {
            if (env == null || string.IsNullOrWhiteSpace(env.Kind)) continue;
            // Ensure envelope MatchKey is set for board.start payload merge.
            if (string.IsNullOrWhiteSpace(env.MatchKey) && !string.IsNullOrWhiteSpace(MatchKey))
                env.MatchKey = MatchKey;

            // Sim BoardStart resets lists; LIVE MatchRuntime ignores mid-match start.
            // Auto-end first so overlay stays aligned with sim reset (W1-F).
            if (string.Equals(env.Kind, "board.start", StringComparison.OrdinalIgnoreCase) &&
                MatchOverlay.Phase is not MatchPhase.Idle)
            {
                MatchOverlay.Apply("board.end");
            }

            MatchOverlay.Apply(env.Kind, MatchValidator.PayloadDict(env));
        }
    }

    static SimResult Ok(EventEnvelope env)
    {
        var r = new SimResult();
        r.Events.Add(env);
        return r;
    }

    static SimResult Fail(string error) => new() { Error = error };

    /// <summary>Game profile stamped on emitted events — override to `webrpg-1` for web-mode e2e.</summary>
    public string GameProfileId { get; set; } = RpgConstants.GameId;

    EventEnvelope Evt(string kind, object payload) => new()
    {
        T = ServerClock.UtcNowDateTime.ToString("o"),
        Game = GameProfileId,
        Kind = kind,
        MatchKey = MatchKey,
        Payload = payload
    };
}
