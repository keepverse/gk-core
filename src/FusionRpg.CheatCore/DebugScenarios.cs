namespace FusionRpg.CheatCore;

public sealed class DebugScenarioStep
{
    public string Name { get; init; } = "";
    public object Payload { get; init; } = new { };
}

/// <summary>Named debug effect-test scenarios (server expands → injector <c>debug.run-steps</c>).</summary>
public static class DebugScenarios
{
    // Structural (tunables-ssot.md T2), all four below — vanilla PVZ Fusion's own type ids
    // (confirm via /api/types live), not balance numbers this repo owns.
    public const int PeaTypeId = 0; // Peashooter
    // Structural (tunables-ssot.md T2) — see PeaTypeId above.
    public const int SunflowerTypeId = 1;
    // Structural (tunables-ssot.md T2) — see PeaTypeId above.
    public const int WallNutTypeId = 3;
    // Structural (tunables-ssot.md T2) — see PeaTypeId above.
    public const int BasicZombieTypeId = 0;

    /// <summary>Step names allowed in Expand output (unit-tested).</summary>
    public static readonly HashSet<string> AllowedStepNames = new(StringComparer.Ordinal)
    {
        "debug.reset-mods",
        "debug.session",
        "debug.wave-freeze",
        "debug.set-mods",
        "debug.reset-board",
        "debug.spawn-plant",
        "debug.spawn-zombie",
        "debug.spawn-bullet",
        "debug.apply-status",
        "debug.apply-status-float",
        "debug.clear-status",
        "debug.arm",
        "debug.disarm",
        "debug.kill",
        "debug.kill-plant",
        "debug.reapply",
        "debug.ensure-sun",
        "debug.select",
        "debug.spawn-cell",
        "debug.snapshot",
        "debug.economy",
        "debug.board-stats",
        "debug.board-config",
        "debug.board-action",
        "debug.spawn-grid",
        "debug.clear-grid",
        "debug.set-box",
        "debug.grid-query",
        "debug.ice-road",
        "debug.effect.grant",
        "debug.effect.withdraw",
        "debug.effect.clear",
        "debug.effect.list",
        "debug.effect.fire-synthetic",
        "debug.effect.enqueue-delta",
        "debug.effect.board-snapshot",
        "debug.effect.dots",
        "debug.effect.counters",
        "debug.status",
        "debug.status.apply",
        "debug.actor-derived",
        "debug.combat.pin-element",
        "debug.combat.silence-vanilla",
        "debug.combat.probe",
        "debug.combat.snapshot",
        "debug.shield.demo-all",
        "debug.shield.snapshot",
        "debug.fx.probe-shaders",
        "debug.fx.world-flash",
        "debug.fx.play",
        "debug.fx.list",
        "debug.fx.mute",
        "debug.fx.unmute",
        "debug.fx.state",
        "pvz.spawn.extra"
    };

    // Structural (tunables-ssot.md T2), all three below — vanilla PVZ Fusion's own type ids, same
    // class as PeaTypeId etc. above.
    public const int GraveGridTypeId = 7;
    // Structural (tunables-ssot.md T2) — see GraveGridTypeId above.
    public const int IceBlockGridTypeId = 8;
    // Structural (tunables-ssot.md T2) — see GraveGridTypeId above.
    public const int DriverZombieTypeId = 16;

    public static IReadOnlyList<DebugScenarioStep> Expand(string id, string scenarioId)
    {
        var steps = new List<DebugScenarioStep>();
        void Cmd(string name, object payload) =>
            steps.Add(new DebugScenarioStep { Name = name, Payload = payload });

        // P0: clear Tab A / absolutes / probe pollution before every scenario.
        Cmd("debug.reset-mods", new { });

        switch (id.ToLowerInvariant())
        {
            case "p1-baseline":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new
                {
                    probePlant = false,
                    probeBullet = false,
                    plant = new { attackPercent = 1.0, defensePercent = 1.0 },
                    zombie = new { defensePercent = 1.0 },
                    bullet = new { damageSet = -1, damagePercent = 1.0 },
                    logDamage = true
                });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 5000, maxHp = 5000 });
                break;

            case "p1-plant":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new
                {
                    probePlant = true,
                    probeBullet = false,
                    plant = new { attackPercent = 5.0 },
                    bullet = new { damageSet = -1, damagePercent = 1.0 },
                    logDamage = true
                });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2, attackPercent = 5.0 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                break;

            case "p1-bullet":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new
                {
                    probePlant = false,
                    probeBullet = true,
                    plant = new { attackPercent = 1.0 },
                    bullet = new { damageSet = 999, damagePercent = 1.0 },
                    logDamage = true
                });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                break;

            case "hit-capture":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 10000, maxHp = 10000 });
                break;

            case "hit-capture-plant":
                // Wall-nut + close BasicZ — prove plant-side combat.hit (melee damageFrom=Zombie or Bullet).
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = WallNutTypeId, col = 6, row = 2, hp = 5000, maxHp = 5000 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 7.8f, hp = 8000, maxHp = 8000 });
                break;

            case "status-butter":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 5000, maxHp = 5000 });
                Cmd("debug.apply-status", new { target = "all-zombies", status = "butter", duration = 8 });
                break;

            case "status-freeze":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 5000, maxHp = 5000 });
                Cmd("debug.apply-status", new { target = "all-zombies", status = "freeze", duration = 5, level = 1 });
                break;

            case "status-cold":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 5000, maxHp = 5000 });
                Cmd("debug.apply-status", new { target = "all-zombies", status = "cold", duration = 5, level = 1 });
                break;

            case "status-poison":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 5000, maxHp = 5000 });
                Cmd("debug.apply-status", new { target = "all-zombies", status = "poison", duration = 5 });
                break;

            case "status-float-butter":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 5000, maxHp = 5000 });
                Cmd("debug.apply-status-float", new { target = "all-zombies", status = "butter" });
                break;

            case "status-clear":
                Cmd("debug.clear-status", new { target = "all-zombies" });
                break;

            case "def-plant":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { plant = new { defensePercent = 5.0 }, logDamage = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = WallNutTypeId, col = 6, row = 2, hp = 5000, maxHp = 5000, defensePercent = 5.0 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 7.8f, hp = 8000, maxHp = 8000 });
                break;

            case "def-zombie":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { zombie = new { defensePercent = 5.0 }, logDamage = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000, defensePercent = 5.0 });
                break;

            case "def-alt-paths":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 15000, maxHp = 15000 });
                break;

            case "onkilled-extra":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 50, maxHp = 50 });
                Cmd("debug.arm", new { kind = "onkill-extra", typeId = BasicZombieTypeId, once = true });
                Cmd("debug.kill", new { target = "all-zombies" });
                break;

            case "onhit-extra":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                Cmd("debug.arm", new { kind = "onhit-extra", typeId = BasicZombieTypeId, maxTriggers = 1 });
                break;

            case "onhit-status":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                Cmd("debug.arm", new { kind = "onhit-status", status = "butter", maxTriggers = 1 });
                break;

            case "onkill-status":
                // Victim last so "selected" oneshot hits the 50 HP zombie, not the tank.
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 5000, maxHp = 5000 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 1, hp = 50, maxHp = 50 });
                Cmd("debug.arm", new { kind = "onkill-status", status = "butter" });
                Cmd("debug.kill", new { target = "selected" });
                break;

            case "kill-signal":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 100, maxHp = 100 });
                Cmd("debug.kill", new { target = "all-zombies" });
                break;

            case "kill-plant":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.kill-plant", new { target = "all-plants" });
                break;

            case "spawn-matrix":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 1, row = 2, atk = 77, hp = 300, maxHp = 300 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 888, maxHp = 888 });
                break;

            case "spawn-bullet-hit":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 5f, hp = 10000, maxHp = 10000 });
                Cmd("debug.spawn-bullet", new { bulletType = 0, row = 2, x = 3f, y = 0f, damage = 50, fromType = PeaTypeId });
                break;

            case "wave-freeze-check":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                break;

            case "hitland-butter":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                // Butter bullet type may vary by pack — operator can override via spawn-bullet.
                Cmd("debug.spawn-bullet", new { bulletType = 0, row = 2, x = 4f, y = 0f, damage = 1 });
                break;

            case "econ-sun-set":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.economy", new { which = "sun", value = 777, add = false });
                break;

            case "econ-sun-add":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.economy", new { which = "sun", value = 100, add = false });
                Cmd("debug.economy", new { which = "sun", value = 50, add = true });
                break;

            case "econ-money-set":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.economy", new { which = "money", value = 888, add = false });
                break;

            case "econ-money-add":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.economy", new { which = "money", value = 200, add = false });
                Cmd("debug.economy", new { which = "money", value = 25, add = true });
                break;

            case "econ-points-set":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.economy", new { which = "points", value = 42, add = false });
                break;

            case "zombie-speed-slow":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { zombie = new { uniqueSpeed = 0.3 } });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 8f, hp = 20000, maxHp = 20000 });
                Cmd("debug.reapply", new { });
                break;

            case "zombie-speed-fast":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { zombie = new { uniqueSpeed = 2.0 } });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 8f, hp = 20000, maxHp = 20000 });
                Cmd("debug.reapply", new { });
                break;

            case "onspawn-inspect":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2, atk = 55, hp = 400, maxHp = 400 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 1234, maxHp = 1234 });
                break;

            case "ondeath-inspect":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 80, maxHp = 80 });
                Cmd("debug.kill-plant", new { target = "all-plants" });
                Cmd("debug.kill", new { target = "all-zombies" });
                break;

            case "zombie-atk-bite":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { zombie = new { attackPercent = 5.0 }, logDamage = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = WallNutTypeId, col = 6, row = 2, hp = 8000, maxHp = 8000 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 7.8f, hp = 20000, maxHp = 20000, attackPercent = 5.0 });
                Cmd("debug.reapply", new { });
                break;

            case "plant-produce":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { plant = new { produceInterval = 1.0 } });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = SunflowerTypeId, col = 2, row = 2 });
                Cmd("debug.reapply", new { });
                break;

            case "board-config-speed":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { board = new { zombieSpeedMultiplier = 0.4 } });
                Cmd("debug.board-config", new { });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 8f, hp = 20000, maxHp = 20000 });
                break;

            case "spawn-mc":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 6f, hp = 5000, maxHp = 5000, mindControl = true });
                break;

            case "env-freeze":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 5.5f, hp = 20000, maxHp = 20000 });
                Cmd("debug.board-action", new { op = "freeze", col = 5, row = 2, timer = 3f });
                break;

            case "env-doom":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 5.5f, hp = 20000, maxHp = 20000 });
                Cmd("debug.board-action", new { op = "doom", col = 5, row = 2, damage = 1800 });
                break;

            case "env-fireline":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 6f, hp = 20000, maxHp = 20000 });
                Cmd("debug.board-action", new { op = "fireline", row = 2, damage = 1800 });
                break;

            case "env-cherry":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 5.5f, hp = 20000, maxHp = 20000 });
                Cmd("debug.board-action", new { op = "cherry", col = 5, row = 2, damage = 1800 });
                break;

            case "env-grave":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-grid", new { typeId = GraveGridTypeId, col = 4, row = 2, graveType = 0 });
                break;

            case "tile-grave":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-grid", new { typeId = GraveGridTypeId, col = 4, row = 2, graveType = 0 });
                Cmd("debug.grid-query", new { col = 4, row = 2 });
                break;

            case "tile-grave-clear":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-grid", new { typeId = GraveGridTypeId, col = 4, row = 2, graveType = 0 });
                Cmd("debug.clear-grid", new { typeId = GraveGridTypeId, col = 4, row = 2 });
                Cmd("debug.grid-query", new { col = 4, row = 2 });
                break;

            case "tile-iceblock":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-grid", new { typeId = IceBlockGridTypeId, col = 5, row = 2 });
                break;

            case "tile-box-water":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-box", new { col = 3, row = 2, boxType = "Water" });
                Cmd("debug.grid-query", new { col = 3, row = 2 });
                break;

            case "tile-box-grass":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-box", new { col = 3, row = 2, boxType = "Water" });
                Cmd("debug.set-box", new { col = 3, row = 2, boxType = "Grass" });
                Cmd("debug.grid-query", new { col = 3, row = 2 });
                break;

            case "tile-box-lava":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-box", new { col = 3, row = 2, boxType = "Lava" });
                Cmd("debug.grid-query", new { col = 3, row = 2 });
                break;

            case "tile-box-dirt":
                // Nuclear / doom-scorched grass → Dirt (+ optional crater pit).
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-box", new { col = 4, row = 2, boxType = "nuclear", withPit = true });
                Cmd("debug.grid-query", new { col = 4, row = 2 });
                break;

            case "tile-ice-road":
                // Sledge / DriverZombie ice trail on the lawn.
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.ice-road", new { row = 2, x = 8f, typeId = DriverZombieTypeId, keepDriver = true });
                break;

            case "onkill-grave":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 5.5f, hp = 50, maxHp = 50 });
                Cmd("debug.arm", new { kind = "onkill-grave", col = 4 });
                Cmd("debug.kill", new { target = "all-zombies" });
                break;

            case "onkill-clear-grave":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-grid", new { typeId = GraveGridTypeId, col = 3, row = 2, graveType = 0 });
                Cmd("debug.spawn-grid", new { typeId = GraveGridTypeId, col = 5, row = 2, graveType = 0 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 6f, hp = 50, maxHp = 50 });
                Cmd("debug.arm", new { kind = "onkill-clear-grave" });
                Cmd("debug.kill", new { target = "all-zombies" });
                break;

            // --- Foundation Effect LIVE L1–L14 (no debug on-hit arms; EffectBag owns apply) ---
            case "effect-butter-hit":
                EffectHitBoard(Cmd, scenarioId, "fx.butter_on_hit", icdMs: 0);
                break;
            case "effect-freeze-hit":
                EffectHitBoard(Cmd, scenarioId, "fx.freeze_on_hit", icdMs: 0);
                break;
            case "effect-cold-hit":
                EffectHitBoard(Cmd, scenarioId, "fx.cold_on_hit", icdMs: 0);
                break;
            case "effect-clear-butter":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                Cmd("debug.apply-status", new { target = "all-zombies", status = "butter", duration = 30 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-clear",
                    effectId = "fx.clear_butter",
                    ownerKey = "match",
                    overlay = new { icd_ms = 0 }
                });
                break;
            case "effect-passive-atk":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true, plant = new { attackPercent = 1.0 } });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-passive",
                    effectId = "fx.passive_atk_flat",
                    ownerKey = "match",
                    overlay = new { flat = 10.0 }
                });
                Cmd("debug.board-stats", new { });
                break;
            case "combat-area-row":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 7.5f, hp = 8000, maxHp = 8000 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 8.2f, hp = 8000, maxHp = 8000 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 8.9f, hp = 8000, maxHp = 8000 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "area-row",
                    effectId = "fx.overlay_damage",
                    ownerKey = "match",
                    overlay = new
                    {
                        amount = -50,
                        icd_ms = 0,
                        target = new
                        {
                            mode = "Area",
                            shape = "Row",
                            anchor = "EventTarget",
                            filters = new { side = "zombie" },
                            maxTargets = 8
                        },
                        delivery = new { mode = "Instant" }
                    }
                });
                Cmd("debug.board-stats", new { });
                Cmd("debug.select", new { side = "zombie" });
                Cmd("debug.effect.fire-synthetic", new { trigger = "OnDamageDealt", side = "plant" });
                break;
            case "combat-counter-target":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "counter-target",
                    effectId = "fx.overlay_damage",
                    ownerKey = "match",
                    overlay = new
                    {
                        icd_ms = 0,
                        delivery = new { mode = "Counter", everyHits = 5, resetOnBurst = true, counterScope = "Target" },
                        burst = new
                        {
                            amount = -500,
                            target = new { mode = "EventTarget" },
                            delivery = new { mode = "Instant" }
                        }
                    }
                });
                Cmd("debug.board-stats", new { });
                Cmd("debug.select", new { side = "zombie" });
                for (var i = 0; i < 5; i++)
                    Cmd("debug.effect.fire-synthetic", new { trigger = "OnDamageDealt", side = "plant" });
                break;
            case "combat-counter-actor":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "counter-actor",
                    effectId = "fx.overlay_damage",
                    ownerKey = "match",
                    overlay = new
                    {
                        icd_ms = 0,
                        delivery = new { mode = "Counter", everyHits = 5, resetOnBurst = true, counterScope = "Actor" },
                        burst = new
                        {
                            amount = -500,
                            target = new { mode = "EventTarget", filters = new { side = "zombie" } },
                            delivery = new { mode = "Instant" }
                        }
                    }
                });
                Cmd("debug.board-stats", new { });
                Cmd("debug.select", new { side = "zombie" });
                for (var i = 0; i < 5; i++)
                    Cmd("debug.effect.fire-synthetic", new { trigger = "OnDamageDealt", side = "plant" });
                break;
            case "combat-dot":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 8000, maxHp = 8000 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "dot-overtime",
                    effectId = "fx.overlay_damage",
                    ownerKey = "match",
                    overlay = new
                    {
                        amount = -20,
                        icd_ms = 0,
                        delivery = new
                        {
                            mode = "OverTime",
                            periodMs = 1000,
                            durationMs = 5000,
                            tickBudget = 1
                        }
                    }
                });
                Cmd("debug.board-stats", new { });
                Cmd("debug.select", new { side = "zombie" });
                Cmd("debug.effect.fire-synthetic", new { trigger = "OnDamageDealt", side = "plant" });
                break;
            case "combat-heal":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 500, maxHp = 8000 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "heal",
                    effectId = "fx.overlay_damage",
                    ownerKey = "match",
                    overlay = new
                    {
                        amount = 30,
                        icd_ms = 0,
                        target = new { mode = "EventTarget" },
                        delivery = new { mode = "Instant" }
                    }
                });
                Cmd("debug.board-stats", new { });
                Cmd("debug.select", new { side = "zombie" });
                Cmd("debug.effect.fire-synthetic", new { trigger = "OnDamageDealt", side = "plant" });
                break;
            case "combat-random":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 7.5f, hp = 8000, maxHp = 8000 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, x = 8.2f, hp = 8000, maxHp = 8000 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 3, x = 7.5f, hp = 8000, maxHp = 8000 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "random-multi",
                    effectId = "fx.overlay_damage",
                    ownerKey = "match",
                    overlay = new
                    {
                        amount = -40,
                        icd_ms = 0,
                        target = new
                        {
                            mode = "Random",
                            count = 2,
                            maxTargets = 8,
                            filters = new { side = "zombie", excludeMindControlled = true }
                        },
                        delivery = new { mode = "Instant" }
                    }
                });
                Cmd("debug.board-stats", new { });
                Cmd("debug.select", new { side = "zombie" });
                Cmd("debug.effect.fire-synthetic", new { trigger = "OnDamageDealt", side = "plant" });
                break;
            case "effect-spawn-ondeath":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 40, maxHp = 40 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-death",
                    effectId = "fx.spawn_zombie_ondeath",
                    ownerKey = "match",
                    overlay = new { icd_ms = 0 }
                });
                Cmd("debug.kill", new { target = "all-zombies" });
                break;
            case "effect-spawn-plant-bullet":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-spawn-pb",
                    effectId = "fx.spawn_plant_bullet",
                    ownerKey = "match",
                    overlay = new { icd_ms = 0 }
                });
                break;
            case "effect-board-cherry":
                EffectHitBoard(Cmd, scenarioId, "fx.board_cherry", icdMs: 0);
                break;
            case "effect-grid-cycle":
                EffectHitBoard(Cmd, scenarioId, "fx.grid_item_cycle", icdMs: 0);
                break;
            case "effect-set-dirt":
                EffectHitBoard(Cmd, scenarioId, "fx.set_dirt_box", icdMs: 0);
                break;
            case "effect-economy-sun":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true });
                Cmd("debug.economy", new { which = "sun", value = 100, add = false });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-sun",
                    effectId = "fx.economy_sun",
                    ownerKey = "match",
                    overlay = new { icd_ms = 0 }
                });
                break;
            case "effect-icd-butter":
                EffectHitBoard(Cmd, scenarioId, "fx.icd_butter", icdMs: null);
                break;
            case "effect-withdraw":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-withdraw",
                    effectId = "fx.butter_on_hit",
                    ownerKey = "match",
                    overlay = new { icd_ms = 0 }
                });
                Cmd("debug.effect.withdraw", new { grantId = "live-withdraw" });
                break;
            case "effect-spawn-filter":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.reset-board", new { });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-spawn-ft",
                    effectId = "fx.spawn_butter",
                    ownerKey = "plant:" + PeaTypeId,
                    overlay = new { icd_ms = 0 }
                });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 5000, maxHp = 5000 });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
                break;

            case "effect-entity-atk":
                // Two peas; FA1 flat ATK only on SelectedPtr after first spawn (entity:selected).
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true, plant = new { attackPercent = 1.0 } });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 1, row = 2 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-entity-atk",
                    effectId = "fx.passive_atk_flat",
                    ownerKey = "entity:selected",
                    overlay = new { flat = 50.0 }
                });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 3, row = 2 });
                Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
                Cmd("debug.board-stats", new { });
                break;

            case "effect-plant-type-atk":
                // Pea + Wall-nut; grant plant:0 → only pea ATK rises.
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true, plant = new { attackPercent = 1.0 } });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 1, row = 2 });
                Cmd("debug.spawn-plant", new { typeId = WallNutTypeId, col = 3, row = 2 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-plant-type-atk",
                    effectId = "fx.passive_atk_flat",
                    ownerKey = "plant:" + PeaTypeId,
                    overlay = new { flat = 40.0 }
                });
                Cmd("debug.board-stats", new { });
                break;

            case "effect-match-midspawn":
                // Grant match flat → spawn second pea → both elevated.
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true, plant = new { attackPercent = 1.0 } });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 1, row = 2 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-match-mid",
                    effectId = "fx.passive_atk_flat",
                    ownerKey = "match",
                    overlay = new { flat = 15.0 }
                });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 3, row = 2 });
                Cmd("debug.board-stats", new { });
                break;

            case "effect-entity-midspawn":
                // Spawn A → entity grant → spawn B → board-stats; withdraw → board-stats again.
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true, plant = new { attackPercent = 1.0 } });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 1, row = 2 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-entity-mid",
                    effectId = "fx.passive_atk_flat",
                    ownerKey = "entity:selected",
                    overlay = new { flat = 50.0 }
                });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 3, row = 2 });
                Cmd("debug.board-stats", new { tag = "after-grant" });
                Cmd("debug.effect.withdraw", new { grantId = "live-entity-mid" });
                Cmd("debug.board-stats", new { tag = "after-withdraw" });
                break;

            case "effect-spawn-then-grant":
                // Spawn A+B → select A by col → entity grant → only A elevated.
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.effect.clear", new { });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new { logDamage = true, plant = new { attackPercent = 1.0 } });
                Cmd("debug.reset-board", new { });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 1, row = 2 });
                Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 3, row = 2 });
                Cmd("debug.select", new { side = "plant", col = 1, row = 2 });
                Cmd("debug.effect.grant", new
                {
                    grantId = "live-spawn-then-grant",
                    effectId = "fx.passive_atk_flat",
                    ownerKey = "entity:selected",
                    overlay = new { flat = 50.0 }
                });
                Cmd("debug.board-stats", new { });
                break;

            case "status-l2-wither":
                StatusL2Board(Cmd, scenarioId, OverlayDot("wither", -20), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-snapshot":
                StatusL2Board(Cmd, scenarioId, OverlayDot("wither", -20), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                Cmd("debug.status", new { });
                break;
            case "status-l2-resist":
                StatusL2Board(Cmd, scenarioId, OverlayDot("wither", -20), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "iron-dot"));
                break;
            case "status-l2-leech":
                StatusL2Board(Cmd, scenarioId, OverlayDot("leech", -12), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-rally":
                StatusL2Board(Cmd, scenarioId, OverlayTimed("rally", 4000), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-expose":
                StatusL2Board(Cmd, scenarioId, OverlayTimed("expose", 4000), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-command":
                StatusL2Board(Cmd, scenarioId, OverlayTimed("command", 4000), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-shatter":
                StatusL2Board(Cmd, scenarioId, OverlayTimed("shatter", 4000), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-bond":
                StatusL2Board(Cmd, scenarioId, BondOverlay(), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                for (var i = 0; i < 4; i++)
                    Cmd("debug.effect.fire-synthetic", FireSyntheticPayload(2, 7.5f));
                Cmd("debug.status", new { });
                break;
            case "status-l2-blight-row":
                StatusL2Board(Cmd, scenarioId, ContagionOverlay("blight", AreaSpread("Row")), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 8.2f, profile: "glass"),
                    ZombieSlot(row: 3, x: 7.5f, profile: "glass"),
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-rot":
                StatusL2Board(Cmd, scenarioId, ContagionOverlay("rot", AreaSpread("Column")), plantProfile: "neutral",
                    ZombieSlot(row: 3, x: 7.5f, profile: "glass"),
                    ZombieSlot(row: 2, x: 8.2f, profile: "glass"),
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-spark":
                StatusL2Board(Cmd, scenarioId, ContagionOverlay("spark", AreaSpread("Square", size: 3)), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 8.2f, profile: "glass"),
                    ZombieSlot(row: 4, x: 9.5f, profile: "glass"),
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-pact-mark":
                StatusL2Board(Cmd, scenarioId, ContagionOverlay("pact_mark", new
                {
                    mode = "Random",
                    count = 2,
                    filters = new { side = "zombie" }
                }), plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 8.2f, profile: "glass"),
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-spore":
                StatusL2Board(Cmd, scenarioId, ContagionOverlay("spore", AreaSpread("Rectangle", width: 3, height: 2)),
                    plantProfile: "neutral",
                    ZombieSlot(row: 2, x: 8.2f, profile: "glass"),
                    ZombieSlot(row: 4, x: 7.5f, profile: "glass"),
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-butter":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("butter", 4000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-freeze":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("freeze", 3000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-cold":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("cold", 5000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-poison":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("poison", 5000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-hypno":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("hypno", 4000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-ember":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("ember", 4000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-jala":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("jala", 4000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-kelp":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("kelp", 4000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-charm-pulse":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("charm_pulse", 2000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-resist-cc":
                StatusL2Board(Cmd, scenarioId, UnityCcOverlay("butter", 4000), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "iron-cc"));
                break;
            case "status-l2-resist-contagion":
                StatusL2Board(Cmd, scenarioId, ContagionOverlay("blight", AreaSpread("Row")), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 8.2f, profile: "iron-contagion"),
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                break;
            case "status-l2-poison-immune":
                StatusL2Board(Cmd, scenarioId, PoisonImmuneOverlay(), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "immune-poison"));
                break;
            case "status-l2-actor-derived":
                StatusL2Board(Cmd, scenarioId, OverlayDot("wither", -20), plantProfile: "caster",
                    ZombieSlot(row: 2, x: 7.5f, profile: "glass"));
                Cmd("debug.actor-derived", new { });
                break;

            // Reusable mid-match lab: operator enters any day lawn, then POST scenario.
            case "lab-empty":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new
                {
                    probePlant = false,
                    probeBullet = false,
                    plant = new { attackPercent = 0.0, defensePercent = 1.0 },
                    zombie = new { defensePercent = 1.0 },
                    bullet = new { damageSet = -1, damagePercent = 1.0 },
                    board = new { zombieCountMultiplier = 0.0 },
                    logDamage = true
                });
                Cmd("debug.board-config", new { });
                Cmd("debug.reset-board", new { });
                Cmd("debug.ensure-sun", new { value = 9999f });
                Cmd("debug.combat.silence-vanilla", new { plant = true });
                Cmd("debug.board-stats", new { });
                break;

            case "lab-overlay":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new
                {
                    probePlant = false,
                    probeBullet = false,
                    plant = new { attackPercent = 0.0, defensePercent = 1.0 },
                    zombie = new { defensePercent = 1.0 },
                    bullet = new { damageSet = -1, damagePercent = 1.0 },
                    board = new { zombieCountMultiplier = 0.0 },
                    logDamage = true
                });
                Cmd("debug.board-config", new { });
                Cmd("debug.reset-board", new { });
                Cmd("debug.ensure-sun", new { value = 9999f });
                Cmd("debug.spawn-plant", new
                {
                    typeId = PeaTypeId,
                    col = 2,
                    row = 2,
                    atk = 0,
                    derivedProfile = "combat-fire-caster",
                    elementPrimary = "fire"
                });
                Cmd("debug.spawn-zombie", new
                {
                    typeId = BasicZombieTypeId,
                    row = 2,
                    x = 7.5f,
                    hp = 20000,
                    maxHp = 20000,
                    derivedProfile = "combat-ice-tank",
                    elementPrimary = "ice"
                });
                Cmd("debug.combat.silence-vanilla", new { plant = true });
                Cmd("debug.board-stats", new { });
                break;

            // Shield bar HUD lab: freeze + clear + pea + zombie, then 3-stack demo on every living unit.
            case "lab-shield-bar":
                Cmd("debug.session", new { op = "start", scenarioId });
                Cmd("debug.wave-freeze", new { enabled = true });
                Cmd("debug.set-mods", new
                {
                    probePlant = false,
                    probeBullet = false,
                    plant = new { attackPercent = 0.0, defensePercent = 1.0 },
                    zombie = new { defensePercent = 1.0 },
                    bullet = new { damageSet = -1, damagePercent = 1.0 },
                    board = new { zombieCountMultiplier = 0.0 },
                    logDamage = false
                });
                Cmd("debug.board-config", new { });
                Cmd("debug.reset-board", new { });
                Cmd("debug.ensure-sun", new { value = 9999f });
                Cmd("debug.spawn-plant", new
                {
                    typeId = PeaTypeId,
                    col = 2,
                    row = 2,
                    atk = 0,
                    hp = 500,
                    maxHp = 500
                });
                Cmd("debug.spawn-zombie", new
                {
                    typeId = BasicZombieTypeId,
                    row = 2,
                    x = 7.5f,
                    hp = 5000,
                    maxHp = 5000
                });
                Cmd("debug.combat.silence-vanilla", new { plant = true });
                // demo-all needs registry populated after spawn — runs as next step on main thread.
                Cmd("debug.shield.demo-all", new
                {
                    amount = 100,
                    elements = new[] { "fire", "ice", "earth" }
                });
                Cmd("debug.shield.snapshot", new { });
                Cmd("debug.board-stats", new { });
                break;

            default:
                throw new ArgumentException("unknown scenario: " + id);
        }

        return steps;
    }

    static (int row, float x, string profile, long hp) ZombieSlot(int row, float x, string profile, long hp = 8000) =>
        (row, x, profile, hp);

    static object OverlayDot(string statusId, int amount) => new
    {
        statusId,
        amount,
        icd_ms = 0,
        periodMs = 1000,
        durationMs = 5000,
        tickBudget = 1,
        target = new { mode = "EventTarget", filters = new { side = "zombie" } }
    };

    static object OverlayTimed(string statusId, int durationMs) => new
    {
        statusId,
        icd_ms = 0,
        durationMs,
        target = new { mode = "EventTarget", filters = new { side = "zombie" } }
    };

    static object BondOverlay() => new
    {
        statusId = "bond",
        icd_ms = 0,
        delivery = new { mode = "Counter", everyHits = 5, resetOnBurst = true, counterScope = "Target" },
        burst = new
        {
            amount = -500,
            target = new { mode = "EventTarget" },
            delivery = new { mode = "Instant" }
        },
        target = new { mode = "EventTarget", filters = new { side = "zombie" } }
    };

    static object PoisonImmuneOverlay() => new
    {
        statusId = "poison",
        icd_ms = 0,
        durationMs = 5000,
        immunityTags = new[] { "poison" }
    };

    static object AreaSpread(string shape, int? size = null, int? width = null, int? height = null) => new
    {
        mode = "Area",
        shape,
        size,
        width,
        height,
        anchor = "EventTarget",
        filters = new { side = "zombie" }
    };

    static object ContagionOverlay(string statusId, object spreadTarget) => new
    {
        statusId,
        amount = -12,
        icd_ms = 0,
        periodMs = 1000,
        durationMs = 5000,
        tickBudget = 5,
        spread = new
        {
            chance = 1.0,
            icd_ms = 0,
            maxHops = 2,
            statusId,
            target = spreadTarget
        }
    };

    static object UnityCcOverlay(string statusId, int durationMs) => new
    {
        statusId,
        icd_ms = 0,
        durationMs
    };

    static object FireSyntheticPayload(int targetRow, float targetX) => new
    {
        trigger = "OnDamageDealt",
        side = "plant",
        actorCol = 2,
        actorRow = 2,
        typeId = PeaTypeId,
        targetRow,
        targetX,
        targetTypeId = BasicZombieTypeId
    };

    static void StatusL2Board(
        Action<string, object> Cmd,
        string scenarioId,
        object overlay,
        string plantProfile,
        params (int row, float x, string profile, long hp)[] zombies)
    {
        const int plantCol = 2;
        const int plantRow = 2;
        Cmd("debug.session", new { op = "start", scenarioId });
        Cmd("debug.effect.clear", new { });
        Cmd("debug.wave-freeze", new { enabled = true });
        Cmd("debug.reset-board", new { });
        Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = plantCol, row = plantRow, derivedProfile = plantProfile });
        foreach (var z in zombies)
        {
            Cmd("debug.spawn-zombie", new
            {
                typeId = BasicZombieTypeId,
                row = z.row,
                x = z.x,
                hp = z.hp,
                maxHp = z.hp,
                derivedProfile = z.profile
            });
        }

        Cmd("debug.effect.grant", new
        {
            grantId = "live-status-l2",
            effectId = "fx.overlay_damage",
            ownerKey = "match",
            overlay
        });
        Cmd("debug.board-stats", new { });
        var seed = zombies.Length > 0 ? zombies[^1] : (row: 2, x: 7.5f, profile: "glass", hp: 8000);
        Cmd("debug.select", new { side = "zombie", row = seed.row, x = seed.x });
        Cmd("debug.effect.fire-synthetic", FireSyntheticPayload(seed.row, seed.x));
        Cmd("debug.status", new { });
    }

    static void EffectHitBoard(Action<string, object> Cmd, string scenarioId, string effectId, int? icdMs)
    {
        Cmd("debug.session", new { op = "start", scenarioId });
        Cmd("debug.effect.clear", new { });
        Cmd("debug.wave-freeze", new { enabled = true });
        Cmd("debug.set-mods", new { logDamage = true });
        Cmd("debug.reset-board", new { });
        Cmd("debug.spawn-plant", new { typeId = PeaTypeId, col = 2, row = 2 });
        Cmd("debug.spawn-zombie", new { typeId = BasicZombieTypeId, row = 2, hp = 20000, maxHp = 20000 });
        object overlay = icdMs.HasValue ? new { icd_ms = icdMs.Value } : new { };
        Cmd("debug.effect.grant", new
        {
            grantId = "live-" + effectId.Replace('.', '-'),
            effectId,
            ownerKey = "match",
            overlay
        });
    }

    public static IReadOnlyList<string> AllIds { get; } = new[]
    {
        "p1-baseline", "p1-plant", "p1-bullet", "hit-capture", "hit-capture-plant",
        "status-butter", "status-freeze", "status-cold", "status-poison", "status-float-butter", "status-clear",
        "def-plant", "def-zombie", "def-alt-paths",
        "onkilled-extra", "onhit-extra", "onhit-status", "onkill-status",
        "kill-signal", "kill-plant", "spawn-matrix", "spawn-bullet-hit", "wave-freeze-check", "hitland-butter",
        "econ-sun-set", "econ-sun-add", "econ-money-set", "econ-money-add", "econ-points-set",
        "zombie-speed-slow", "zombie-speed-fast",
        "onspawn-inspect", "ondeath-inspect", "zombie-atk-bite", "plant-produce", "board-config-speed", "spawn-mc",
        "env-freeze", "env-doom", "env-fireline", "env-cherry", "env-grave",
        "tile-grave", "tile-grave-clear", "tile-iceblock",
        "tile-box-water", "tile-box-grass", "tile-box-lava", "tile-box-dirt", "tile-ice-road",
        "onkill-grave", "onkill-clear-grave",
        "effect-butter-hit", "effect-freeze-hit", "effect-cold-hit", "effect-clear-butter",
        "effect-passive-atk", "effect-spawn-ondeath", "effect-spawn-plant-bullet",
        "effect-board-cherry", "effect-grid-cycle", "effect-set-dirt", "effect-economy-sun",
        "effect-icd-butter", "effect-withdraw", "effect-spawn-filter", "effect-entity-atk",
        "effect-plant-type-atk", "effect-match-midspawn", "effect-entity-midspawn", "effect-spawn-then-grant",
        "combat-area-row", "combat-counter-target", "combat-counter-actor", "combat-dot",
        "combat-heal", "combat-random",
        "status-l2-wither", "status-l2-snapshot", "status-l2-resist", "status-l2-leech",
        "status-l2-rally", "status-l2-expose", "status-l2-command", "status-l2-shatter", "status-l2-bond",
        "status-l2-blight-row", "status-l2-rot", "status-l2-spark", "status-l2-pact-mark", "status-l2-spore",
        "status-l2-butter", "status-l2-freeze", "status-l2-cold", "status-l2-poison",
        "status-l2-hypno", "status-l2-ember", "status-l2-jala", "status-l2-kelp", "status-l2-charm-pulse",
        "status-l2-resist-cc", "status-l2-resist-contagion", "status-l2-poison-immune", "status-l2-actor-derived",
        "lab-empty", "lab-overlay", "lab-shield-bar"
    };
}
