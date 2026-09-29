using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N6: a debug element pin (<c>InjectorElementOverride</c>) already wins over the species element when
/// the actor defends (<c>InjectorCombatBridge</c>, E27), but the basic-attack grant baked its <c>elementPayload</c> from the
/// species element only, so a pinned actor defended as the pin and attacked as its species. The binder must read the pin
/// first, and a pin set after spawn must rebind the grant. Source scan: the Injector has no CI-runnable unit tests.
/// </summary>
public class LawnBasicAttackPinnedElementGuardTests
{
    [Fact]
    public void Binder_builds_the_grant_from_a_pinned_element_before_the_species_element()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "Effects", "LawnBasicAttackGrantBinder.cs"));
        var bind = text.IndexOf("static bool Bind(string ptr)", StringComparison.Ordinal);
        Assert.True(bind >= 0, "Bind must exist");

        var body = text.Substring(bind);
        var pin = body.IndexOf("InjectorElementOverride.TryGet(ptr, out var pinned)", StringComparison.Ordinal);
        var build = body.IndexOf("BasicAttackGrantBuilder.Build(ptr, attack.Primary, attack.Secondary", StringComparison.Ordinal);
        Assert.True(pin >= 0, "Bind must read the element pin");
        Assert.True(build > pin, "the grant must be built from the pin-aware elements, after the pin is read");
    }

    [Fact]
    public void Runtime_pin_command_requeues_the_grant_bind_for_that_ptr()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "CheatCommandRunner.cs"));
        var handler = text.IndexOf("static void HandleCombatPinElement(JsonElement p)", StringComparison.Ordinal);
        Assert.True(handler >= 0, "the pin handler must exist");

        var body = text.Substring(handler, text.IndexOf("static void EmitStatus(", handler, StringComparison.Ordinal) - handler);
        var pin = body.IndexOf("InjectorElementOverride.PinParse(ptr, primary, secondary);", StringComparison.Ordinal);
        var requeue = body.IndexOf("LawnBasicAttackGrantBinder.QueueSpawn(", StringComparison.Ordinal);
        Assert.True(pin >= 0 && requeue > pin, "a pin must requeue the grant bind after it is stored");
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
