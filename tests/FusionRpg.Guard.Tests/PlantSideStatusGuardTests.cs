using System.Security.Cryptography;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// E39 (spec-plant-side-status.md): <c>InjectorEffectActionSink.ExecApplyStatus</c>/
/// <c>ExecClearStatus</c> live in the Injector and need the game's Unity/interop assemblies to build
/// — the same constraint <c>StatusStatApplierGuardTests</c> hit for a different Unity-hosted half —
/// so this guard reads the files as text, matching every other injector-wiring guard in this project.
/// The behavioural half (target-resolution algorithm, refusal shape, target vocabulary) is proven
/// against a fake sink in <c>FusionRpg.Core.Tests/Status/PlantSideStatusTargetingTests.cs</c>; this
/// file proves the REAL executors actually carry that shape.
/// </summary>
public class PlantSideStatusGuardTests
{
    [Fact]
    public void ExecApplyStatus_resolves_through_the_registry_for_both_sides()
    {
        var text = ReadInjector("Effects", "InjectorEffectActionSink.cs");

        Assert.Contains("InjectorEntityRegistry.FindZombie(targetPtr)", text, StringComparison.Ordinal);
        Assert.Contains("InjectorEntityRegistry.FindPlant(targetPtr)", text, StringComparison.Ordinal);
        Assert.Contains("DebugActions.ApplyStatusToPlant(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExecClearStatus_also_resolves_through_the_registry_for_both_sides()
    {
        var text = ReadInjector("Effects", "InjectorEffectActionSink.cs");

        var clearIdx = text.IndexOf("static bool ExecClearStatus(", StringComparison.Ordinal);
        Assert.True(clearIdx >= 0, "ExecClearStatus not found");
        var clearBlock = text[clearIdx..];

        Assert.Contains("ResolveStatusTarget(targetPtr)", clearBlock, StringComparison.Ordinal);
        Assert.Contains("ClearPlantStatus(", clearBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void G5s_board_wide_loop_is_gone_not_merely_unreachable()
    {
        // The pre-E39 shape: an unconditional foreach over every living zombie, calling
        // ApplyStatusToZombie with no ptr filter at all, reached whenever the resolved target ptr
        // was empty. If this text ever reappears, G5 has regressed.
        var text = ReadInjector("Effects", "InjectorEffectActionSink.cs");

        Assert.DoesNotContain(
            "foreach (var z in UnityEngine.Object.FindObjectsOfType<Zombie>())\n        {\n            if (z == null) continue;\n            DebugActions.ApplyStatusToZombie(z, status, duration, level, method: true);\n            n++;\n        }",
            text.Replace("\r\n", "\n"),
            StringComparison.Ordinal);

        // An empty resolved ptr must refuse instead — proven by the reason string reaching the wire.
        Assert.Contains("\"status-no-target\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_emits_carry_a_side_key_and_the_closed_refusal_reason_string()
    {
        var text = ReadInjector("Effects", "InjectorEffectActionSink.cs");

        Assert.Contains("[\"side\"]", text, StringComparison.Ordinal);
        Assert.Contains("\"status-side-unsupported\"", text, StringComparison.Ordinal);
        // Exactly this string, and only this one — no new rejection code was added
        // (definitions.md §10's 33 stay 33; this is a runtime emit reason, a different vocabulary).
        Assert.Contains("pvz.status.apply", text, StringComparison.Ordinal);
        Assert.Contains("pvz.status.clear", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DebugActions_declares_ApplyStatusToPlant_wiring_only_butter()
    {
        var text = ReadInjector("DebugActions.cs");

        var idx = text.IndexOf("public static bool ApplyStatusToPlant(", StringComparison.Ordinal);
        Assert.True(idx >= 0, "ApplyStatusToPlant not found");

        // Only `butter` gets a real write — everything else in this method must refuse, never call
        // an unverified plant method (spec §3: "do not fake a missing plant method with a float
        // write"). Jala's own sweep hit (InfluenceByJalapeno) is deliberately NOT called here — the
        // sweep record (03-status-and-spawn-surface.md) downgrades it after this module's own
        // follow-up read.
        var method = text[idx..];
        var closeIdx = method.IndexOf("\n    public static void Kill(", StringComparison.Ordinal);
        if (closeIdx > 0) method = method[..closeIdx];

        Assert.Contains("p.butterP", method, StringComparison.Ordinal);
        Assert.DoesNotContain("InfluenceByJalapeno", method, StringComparison.Ordinal);
    }

    /// <summary>
    /// Spec §4: "Battle's path — byte-identical regression test, not new coverage." Battle's
    /// ExecApplyStatus (BattleEffects.cs) already resolves a bare ptr with no side check — this
    /// module explicitly must not touch it (spec §3). A file-hash pin is the strongest form of
    /// "byte-identical": any future edit to this file, whether or not it touches ExecApplyStatus,
    /// fails this test and forces a deliberate re-pin rather than a silent drift.
    /// </summary>
    [Fact]
    public void BattleEffects_is_byte_identical_to_its_current_core_baseline()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "Battle", "BattleEffects.cs");
        Assert.True(File.Exists(path), "missing " + path);

        using var sha256 = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(sha256.ComputeHash(stream));

        // Re-pinned 2026-09-21 after combat-ai CAI1.12 (commit c3bb0ba2, "the per-place executor
        // allowlist", Core half) changed this file (384 -> 447 lines, tasks/combat-ai-todo.md:830) —
        // exactly the deliberate re-pin this pin exists to force, rather than a silent drift. The
        // previous pin (52F843…, 2026-09-09) covered the unrelated AttackerEdge addition (9aad045).
        // Re-pinned 2026-09-23 after battle-derived-wire T6 (commit 5e33ad647, "one gate owns
        // combat.defense.omni") added BaseDerived to IBattleStatTarget plus its doc comment — accepted
        // lane work, merged to the integration branch with BattleDefenseOwnershipTests 5/5 and goldens
        // unmoved. The next accepted P1 battle repair (reviewed SHA 7b4e8ef582a0744e20f501562a4b28167e402c89)
        // changed the retained-damage observer surface in this same file, so the byte pin is deliberately
        // re-baselined here rather than weakening the guard. Verified current SHA-256:
        // 946E578D0092A77EB8DD59FDAF8C48FD3113B6042E0E4FB627E921EAB2B43013.
        // E39 remains Injector-only; this hash protects the current Core baseline from accidental
        // edits while keeping the guard honest about the checked-in byte content.
        const string baselineHash = "946E578D0092A77EB8DD59FDAF8C48FD3113B6042E0E4FB627E921EAB2B43013";
        Assert.Equal(baselineHash, hash);
    }

    static string ReadInjector(params string[] relative)
    {
        var path = Path.Combine(new[] { KeepverseRoots.Fusion(), "src", "FusionRpg.Injector" }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
