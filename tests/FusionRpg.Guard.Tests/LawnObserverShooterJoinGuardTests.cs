using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N35: the observer's vanilla bullet hit must name the shooter the drain cached at the bullet's spawn
/// (peeked, not consumed — <c>TryRecordDealtFromBullet</c> takes the entry right after), while its swing stays the bullet.
/// Otherwise no RPG record (actor = shooter) can join it. Source scan: the Injector has no CI-runnable unit tests.
/// </summary>
public class LawnObserverShooterJoinGuardTests
{
    [Fact]
    public void Bullet_hits_name_the_cached_shooter_and_keep_the_bullet_as_the_swing()
    {
        var root = RepoRoot();
        var bridge = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "Effects", "LawnCombatObserverBridge.cs"));
        Assert.Contains("EventDrainHost.TryPeekBulletShooter(bullet.Pointer, out var shooter)", bridge, StringComparison.Ordinal);
        Assert.Contains("? (shooter.ToString(\"X\"), ptr)", bridge, StringComparison.Ordinal);

        var host = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "Effects", "EventDrainHost.cs"));
        var peek = host.IndexOf("public static bool TryPeekBulletShooter(", StringComparison.Ordinal);
        var end = host.IndexOf("\n    }", peek, StringComparison.Ordinal);
        Assert.True(peek >= 0 && end > peek, "missing TryPeekBulletShooter");
        Assert.DoesNotContain(".Remove(", host.Substring(peek, end - peek), StringComparison.Ordinal);

        var hooks = File.ReadAllText(Path.Combine(root, "src", "FusionRpg.Injector", "GameHooks.cs"));
        var observe = hooks.IndexOf("Effects.LawnCombatObserverBridge.RecordVanillaHit(\"zombie\", __instance, damageFrom, theDamage);", StringComparison.Ordinal);
        var record = hooks.IndexOf("Effects.EventDrainHost.TryRecordDealtFromBullet(", observe, StringComparison.Ordinal);
        Assert.True(observe >= 0 && record > observe, "the observer must read the shooter before the drain consumes it");
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
