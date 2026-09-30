using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.PassiveTree.Binding;

/// <summary>
/// W3 (battle-derived-wire T2, 2026-09-23): battle HAS a reflect path now, and this file flipped on the
/// commit that wired it — the filename is historical. It previously pinned D2's "reflect is lawn-only"
/// so the <see cref="FusionRpg.Core.PassiveTree.Binding.BinderRunReport"/> note would go stale loudly
/// the day someone wired a battle consumer; that day is this one.
///
/// <para>The new facts, verified against the real source: <c>TryReflect</c> is reached by
/// <c>DispatchInstant</c> (the effect path) AND by <c>BattleRunState.ApplyHp</c> (battle's direct HP
/// path), it has ONE body (no duplicate formula), and no file under <c>Battle/</c> calls
/// <c>DispatchInstant</c> directly.</para>
/// </summary>
public class ReflectHasNoBattlePathTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    [Fact]
    public void TryReflect_has_one_body_and_the_effect_path_still_calls_it()
    {
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Combat", "CombatDamageDispatcher.cs");
        var source = File.ReadAllText(path);

        // Exactly one call site INSIDE the dispatcher: TryReflect(...) in DispatchInstant's own body.
        // The declaration itself ("internal static void TryReflect(") is excluded by requiring an
        // open-paren call, not a "static ... TryReflect(" declaration prefix.
        var callSites = Regex.Matches(source, @"(?<!static\s+void\s)\bTryReflect\s*\(");
        Assert.Equal(1, callSites.Count);

        // And exactly one declaration — the battle caller must reuse this body, never copy it.
        var declarations = Regex.Matches(source, @"\bvoid\s+TryReflect\s*\(");
        Assert.Equal(1, declarations.Count);
    }

    [Fact]
    public void Battle_calls_TryReflect_and_never_DispatchInstant_directly()
    {
        var battleDir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Battle");
        Assert.True(Directory.Exists(battleDir), $"expected {battleDir} to exist");

        var tryReflectCallers = 0;
        foreach (var file in Directory.GetFiles(battleDir, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            Assert.False(source.Contains("DispatchInstant("), $"{file} calls DispatchInstant directly -- " +
                "battle must reach the reflect step through the single TryReflect body, not by re-entering the dispatcher");
            if (source.Contains("TryReflect")) tryReflectCallers++;
        }

        // One battle caller: BattleRunState.ApplyHp. A second would be a duplicated gate.
        Assert.Equal(1, tryReflectCallers);
    }

    [Fact]
    public void Every_direct_caller_of_DispatchInstant_is_the_effect_or_lawn_path()
    {
        var coreDir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core");
        var expectedCallers = new[] { "EffectBag.cs", "StatusEffectBridge.cs", "CombatDamageDispatcher.cs" };

        foreach (var file in Directory.GetFiles(coreDir, "*.cs", SearchOption.AllDirectories))
        {
            if (!File.ReadAllText(file).Contains("DispatchInstant(")) continue;
            var name = Path.GetFileName(file);
            Assert.Contains(name, expectedCallers);
        }
    }
}
