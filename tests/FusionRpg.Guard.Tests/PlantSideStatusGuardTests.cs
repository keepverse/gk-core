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

        // Re-pinned 2026-10-01, and the cause is the MIGRATION rather than a battle change.
        //
        // The previous pin, 946E578D0092A77EB8DD59FDAF8C48FD3113B6042E0E4FB627E921EAB2B43013, was
        // MEASURED correct: it is the SHA-256 of the source repository's copy of this file at its HEAD,
        // byte for byte, LF endings included. So the pin was not stale and nothing had drifted silently.
        //
        // What changed the bytes is kvsplit's documented path-literal transform. This file is gk-core's,
        // and one comment line names its own coverage pattern; the transform rewrote it from
        // `src/FusionRpg.Core/Battle/**` to `gk-core/src/FusionRpg.Core/Battle/**`, because a
        // repository-relative path is ambiguous once nine repositories exist. That is the
        // `path-literal-moves` residue class, and it changes content by design.
        //
        // VERIFIED, not assumed: the diff between the source copy at the import SHA
        // (effc51d9b55f78aa7a5c47e14eef0e61b690e5eb) and this repository's copy is exactly ONE line, and it
        // is that comment. 511 lines on both sides; 29802 bytes against 29794, the 8-byte difference
        // being the inserted `gk-core/` prefix on that one line. Every other line is identical.
        //
        // Two things this explicitly is NOT, both measured before being ruled out:
        //   * Not a lossy import. lossy-check reports zero losses, and a one-line comment rewrite is the
        //     transform working, not data going missing.
        //   * Not a line-ending artifact. An intermediate measurement appeared to show the source
        //     storing CRLF, which would have made a raw byte pin fragile across checkouts. That was MY
        //     probe: Out-File rewrites LF to CRLF as it writes. Read straight from `git cat-file`, the
        //     source blob is 29794 bytes with zero CRLF, exactly like this one.
        //
        // So the guard is re-baselined to the bytes that are actually here, which is the deliberate
        // re-pin this pin exists to force. It is NOT weakened: the next edit to this file still fails
        // this test, and the comment now says what would make a future change illegitimate — anything
        // beyond a path literal.
        // E39 remains Injector-only; this hash protects the current Core baseline from accidental edits
        // while keeping the guard honest about the checked-in byte content.
        const string baselineHash = "38EAAB1B086867CB60991026B437034AC196961D18CD01E9439F409D52711A3D";
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
