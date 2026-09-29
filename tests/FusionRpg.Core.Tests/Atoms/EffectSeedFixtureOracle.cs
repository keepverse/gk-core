using FusionRpg.Contracts;
using FusionRpg.Core.Effects;

namespace FusionRpg.Core.Effects;

/// <summary>
/// E23/E11 Step 4 (completeness-audit.md B2): the retired hand-written catalog, moved out of
/// production Core and into the test tree it now only serves.
///
/// <para><b>Why it still exists at all.</b> Production reads exactly one source now —
/// <c>EffectAtomCatalog.CreateAll()</c>, generated from <c>data/seed/atoms/fx-*.json</c> by
/// <c>gk-forge/tools/ElementEnumGen --effect-emit</c> and verified against both the DTO shape
/// (<c>MigrationParityTests</c>) and real scenario execution (<c>EffectCatalogExecutionParityTests</c>,
/// <c>EffectAtomCatalogGeneratedTests</c>). This class is the <b>frozen oracle</b> those tests compare
/// against — a captured snapshot of what the migration was proving equivalent to, not a second live
/// source anything in <c>src/</c> reads. The audit's actual concern was <b>drift</b>: two sources both
/// capable of changing and disagreeing. A frozen test fixture cannot drift; only the generated side can
/// now change, which is exactly what these tests still catch.</para>
///
/// <para>Also used as a general-purpose "known-good EffectDef catalog" fixture by several unrelated
/// suites (<c>EffectBagTests</c>, <c>EffectFunnelTests</c>, <c>AtomRunnerTests</c>, etc.) that predate
/// the atom migration and never depended on it being production code — moving it here changes nothing
/// about what they test.</para>
///
/// <para><b>Do not add new defs here.</b> A new effect belongs in <c>gk-data/packs/fusion/data/seed/atoms/</c> as a row;
/// this class is a closed, historical snapshot of the 16 (+1 unused, <c>PatronAuraMarker</c>) defs
/// that existed before the migration, kept byte-identical to what it always was.</para>
/// </summary>
public static class EffectSeedCatalog
{
    public static IReadOnlyList<EffectDef> CreateAll() => new List<EffectDef>
    {
        ButterOnHit(),
        FreezeOnHit(),
        ColdOnHit(),
        PoisonOnHit(),
        ClearButter(),
        PassiveAtkFlat(),
        SpawnZombieOnDeath(),
        SpawnPlantBullet(),
        BoardCherry(),
        SpawnAndClearGrid(),
        SetDirtBox(),
        EconomySun(),
        IcdButter(),
        OnSpawnButter(),
        OverlayDamage(),
        ShieldGrantEffect()
    };

    public static EffectDef ButterOnHit() => Triggered(
        "fx.butter_on_hit", "Butter on hit", EffectTriggers.OnDamageDealt,
        EffectActions.ApplyStatus, new Dictionary<string, object?> { ["status"] = "butter", ["duration"] = 4f });

    public static EffectDef FreezeOnHit() => Triggered(
        "fx.freeze_on_hit", "Freeze on hit", EffectTriggers.OnDamageDealt,
        EffectActions.ApplyStatus, new Dictionary<string, object?> { ["status"] = "freeze", ["duration"] = 3f });

    public static EffectDef ColdOnHit() => Triggered(
        "fx.cold_on_hit", "Cold on hit", EffectTriggers.OnDamageDealt,
        EffectActions.ApplyStatus, new Dictionary<string, object?> { ["status"] = "cold", ["duration"] = 5f });

    public static EffectDef PoisonOnHit() => Triggered(
        "fx.poison_on_hit", "Poison on hit", EffectTriggers.OnDamageDealt,
        EffectActions.ApplyStatus, new Dictionary<string, object?> { ["status"] = "poison", ["duration"] = 5f });

    public static EffectDef ClearButter() => Triggered(
        "fx.clear_butter", "Clear butter", EffectTriggers.OnDamageDealt,
        EffectActions.ClearStatus, new Dictionary<string, object?> { ["status"] = "butter" });

    /// <summary>Patron aura marker (spec-patron-creature.md): a passive with NO actions — the grant
    /// is the session-visible lifecycle anchor; magnitudes live in PatronRuntimeState and apply
    /// as a pure compose-time overlay, never through FA stat writes. Unused by CreateAll() and by
    /// every test — kept for byte-identical parity with the pre-migration source, not because
    /// anything calls it.</summary>
    public static EffectDef PatronAuraMarker() => new()
    {
        EffectId = "fx.patron_aura",
        EffectType = EffectTypes.Passive,
        Name = "Patron aura",
        Enabled = true,
        SourceTag = "seed",
        Triggers = new List<string>(),
        Actions = new List<EffectActionRow>()
    };

    public static EffectDef PassiveAtkFlat() => new()
    {
        EffectId = "fx.passive_atk_flat",
        EffectType = EffectTypes.Passive,
        Name = "Passive ATK +10",
        Enabled = true,
        SourceTag = "seed",
        Triggers = new List<string> { EffectTriggers.OnGranted, EffectTriggers.OnRemoved },
        Actions = new List<EffectActionRow>
        {
            new()
            {
                Seq = 1,
                Action = EffectActions.ModifyStat,
                Params = new Dictionary<string, object?>
                {
                    ["channel"] = "atk",
                    ["flat"] = 10.0
                }
            }
        }
    };

    public static EffectDef SpawnZombieOnDeath() => Triggered(
        "fx.spawn_zombie_ondeath", "Spawn zombie on death", EffectTriggers.OnDeath,
        EffectActions.SpawnEntity, new Dictionary<string, object?>
        {
            ["kind"] = "zombie",
            ["typeId"] = 0,
            ["hp"] = 100,
            ["maxHp"] = 100
        });

    public static EffectDef SpawnPlantBullet() => new()
    {
        EffectId = "fx.spawn_plant_bullet",
        EffectType = EffectTypes.Triggered,
        Name = "Spawn plant + bullet",
        Enabled = true,
        SourceTag = "seed",
        Triggers = new List<string> { EffectTriggers.OnDamageDealt },
        Actions = new List<EffectActionRow>
        {
            new()
            {
                Seq = 1,
                Action = EffectActions.SpawnEntity,
                Params = new Dictionary<string, object?> { ["kind"] = "plant", ["typeId"] = 0, ["row"] = 2, ["col"] = 3 }
            },
            new()
            {
                Seq = 2,
                Action = EffectActions.SpawnEntity,
                Params = new Dictionary<string, object?> { ["kind"] = "bullet", ["typeId"] = 0, ["row"] = 2, ["x"] = 400 }
            }
        }
    };

    public static EffectDef BoardCherry() => Triggered(
        "fx.board_cherry", "Board cherry bomb", EffectTriggers.OnDamageDealt,
        EffectActions.BoardAction, new Dictionary<string, object?> { ["op"] = "cherry", ["row"] = 2, ["col"] = 4 });

    public static EffectDef SpawnAndClearGrid() => new()
    {
        EffectId = "fx.grid_item_cycle",
        EffectType = EffectTypes.Triggered,
        Name = "Spawn + clear grid item",
        Enabled = true,
        SourceTag = "seed",
        Triggers = new List<string> { EffectTriggers.OnDamageDealt },
        Actions = new List<EffectActionRow>
        {
            new()
            {
                Seq = 1,
                Action = EffectActions.SpawnGridItem,
                Params = new Dictionary<string, object?> { ["gridItemType"] = 7, ["row"] = 2, ["col"] = 3 }
            },
            new()
            {
                Seq = 2,
                Action = EffectActions.ClearGridItem,
                Params = new Dictionary<string, object?> { ["selector"] = "last", ["gridItemType"] = 7 }
            }
        }
    };

    public static EffectDef SetDirtBox() => Triggered(
        "fx.set_dirt_box", "Set dirt box", EffectTriggers.OnDamageDealt,
        EffectActions.SetBoxType, new Dictionary<string, object?> { ["boxType"] = 1, ["row"] = 2, ["col"] = 3 });

    public static EffectDef EconomySun() => Triggered(
        "fx.economy_sun", "Add sun", EffectTriggers.OnDamageDealt,
        EffectActions.Economy, new Dictionary<string, object?> { ["currency"] = "sun", ["op"] = "add", ["amount"] = 25 });

    public static EffectDef IcdButter() => Triggered(
        "fx.icd_butter", "ICD butter", EffectTriggers.OnDamageDealt,
        EffectActions.ApplyStatus, new Dictionary<string, object?> { ["status"] = "butter", ["duration"] = 2f });

    public static EffectDef OnSpawnButter() => Triggered(
        "fx.spawn_butter", "Butter on spawn", EffectTriggers.OnSpawn,
        EffectActions.ApplyStatus, new Dictionary<string, object?> { ["status"] = "butter", ["duration"] = 3f });

    public static EffectDef OverlayDamage() => Triggered(
        "fx.overlay_damage", "Overlay HP delta", EffectTriggers.OnDamageDealt,
        EffectActions.ApplyResourceDelta, new Dictionary<string, object?> { ["channel"] = "hp" });

    public static EffectDef ShieldGrantEffect() => new()
    {
        EffectId = "fx.shield_grant",
        EffectType = EffectTypes.Triggered,
        Name = "Grant shield",
        Enabled = true,
        SourceTag = "seed",
        Triggers = new List<string> { EffectTriggers.OnDamageDealt, EffectTriggers.OnTimer, EffectTriggers.OnSpawn },
        Actions = new List<EffectActionRow>
        {
            new() { Seq = 1, Action = EffectActions.GrantShield, Params = new Dictionary<string, object?>() }
        }
    };

    static EffectDef Triggered(string id, string name, string trigger, string action, Dictionary<string, object?> p) => new()
    {
        EffectId = id,
        EffectType = EffectTypes.Triggered,
        Name = name,
        Enabled = true,
        SourceTag = "seed",
        Triggers = new List<string> { trigger },
        Actions = new List<EffectActionRow> { new() { Seq = 1, Action = action, Params = p } }
    };
}
