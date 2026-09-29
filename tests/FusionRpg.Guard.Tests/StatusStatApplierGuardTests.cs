using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// E21 (completeness-audit.md finding A1): the injector half of the status-stat applier cannot be
/// unit-tested — <c>EffectRuntime.cs</c> needs the game's Unity/interop assemblies to build, and the
/// injector cannot host a test project (the same constraint E19's receiver tests worked around by
/// extracting a humble object). This guard reads the file as text, matching every other injector-
/// wiring guard in this project (<c>ChannelExtensionGuardTests</c>, <c>FunnelDeltaGuardTests</c>).
///
/// <para><b>What it protects against.</b> Before E21, <c>StatusRuntime.OnApplied</c>/<c>OnEnded</c>
/// only played VFX — <c>StatusStatPayload.ToModifiers</c>/<c>SourceIdOf</c> existed and were fully
/// tested and had zero callers. A future edit to <c>EffectRuntime.cs</c> that removes the
/// Upsert/WithdrawSource calls (say, while refactoring the VFX cue lookup) would silently reopen that
/// exact gap — the four <c>ModifyStat</c> statuses would go back to changing nothing, and nothing in
/// Core would notice, because Core's own tests use the real <c>StatSystem</c> directly and never touch
/// this wiring.</para>
/// </summary>
public class StatusStatApplierGuardTests
{
    [Fact]
    public void OnApplied_upserts_the_status_stat_modifiers()
    {
        var text = ReadInjector("Effects", "EffectRuntime.cs");

        Assert.Contains("StatusStatPayload.ToModifiers(inst)", text, StringComparison.Ordinal);
        Assert.Contains("CheatState.Stats.Upsert(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void OnEnded_withdraws_by_the_same_source_id_the_apply_half_used()
    {
        var text = ReadInjector("Effects", "EffectRuntime.cs");

        Assert.Contains("StatusStatPayload.SourceIdOf(inst)", text, StringComparison.Ordinal);
        Assert.Contains("CheatState.Stats.WithdrawSource(\"status\",", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_halves_trigger_a_reapply_so_the_change_is_actually_written()
    {
        // Upsert/WithdrawSource alone only change the session bag; nothing re-composes and writes the
        // entity's fields until something calls Resolve again. Without this, the fix would look wired
        // and still do nothing until an unrelated cheat toggle happened to trigger a resolve.
        var text = ReadInjector("Effects", "EffectRuntime.cs");

        var applyIdx = text.IndexOf("_status.OnApplied = inst =>", StringComparison.Ordinal);
        var endedIdx = text.IndexOf("_status.OnEnded = inst =>", StringComparison.Ordinal);
        Assert.True(applyIdx >= 0, "OnApplied handler not found");
        Assert.True(endedIdx >= 0, "OnEnded handler not found");

        var applyBlock = text.Substring(applyIdx, endedIdx - applyIdx);
        Assert.Contains("CheatActions.ReapplyLivingForOwner(\"entity:\" + inst.HostPtr)", applyBlock, StringComparison.Ordinal);

        var endedBlock = text[endedIdx..];
        Assert.Contains("CheatActions.ReapplyLivingForOwner(\"entity:\" + inst.HostPtr)", endedBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void The_gate_is_StatMods_not_every_status()
    {
        // Most statuses are pure CC/VFX with no StatMods — invalidating and rewriting every entity's
        // stats on every status tick would be needless churn this guard would otherwise miss.
        var text = ReadInjector("Effects", "EffectRuntime.cs");

        Assert.Contains("if (inst.StatMods.Count > 0)", text, StringComparison.Ordinal);
    }

    static string ReadInjector(params string[] relative)
    {
        var path = Path.Combine(new[] { FindRepoRoot(), "src", "FusionRpg.Injector" }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
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
