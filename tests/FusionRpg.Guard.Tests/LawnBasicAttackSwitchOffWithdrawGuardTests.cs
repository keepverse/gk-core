using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// lawn-combat-wire L-N8: with the kill switch off, <c>LawnBasicAttackCostCharger.ShouldApplyRider</c> passes every
/// <c>OnDamageDealt</c> record through, so grants bound while the switch was on kept applying riders for free. The binder
/// must withdraw them on the off edge, every frame, before its empty-queue early return. Source scan: the Injector has
/// no CI-runnable unit tests.
/// </summary>
public class LawnBasicAttackSwitchOffWithdrawGuardTests
{
    [Fact]
    public void Binder_tick_checks_the_off_edge_before_any_early_return_and_withdraws_basic_attack_grants()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "Effects", "LawnBasicAttackGrantBinder.cs"));

        var tick = text.IndexOf("public static void Tick()", StringComparison.Ordinal);
        var edge = text.IndexOf("if (SwitchEdge.TurnedOff(LawnBasicAttackFeature.Enabled))", tick, StringComparison.Ordinal);
        var withdraw = text.IndexOf("WithdrawAllBound();", edge, StringComparison.Ordinal);
        var firstReturn = text.IndexOf("return;", tick, StringComparison.Ordinal);

        Assert.True(tick >= 0 && edge > tick, "Tick must check the switch's off edge");
        Assert.True(withdraw > edge, "the off edge must withdraw the bound grants");
        Assert.True(edge < firstReturn, "the edge check must run before Tick's first early return");

        var body = text.Substring(text.IndexOf("static void WithdrawAllBound()", StringComparison.Ordinal));
        Assert.Contains("BasicAttackGrantBuilder.IsBasicAttackGrantId(grant.GrantId)", body, StringComparison.Ordinal);
        Assert.Contains("EffectRuntime.Withdraw(grant.GrantId)", body, StringComparison.Ordinal);
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
