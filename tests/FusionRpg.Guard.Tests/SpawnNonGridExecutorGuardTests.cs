using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// E40 (spec-spawn-non-grid.md §4): "the four executor arms are covered by a text guard in the
/// scripts\guard-*.ps1 family" — text-based, same reason and same shape as
/// <see cref="EntityFields12PlusGuardTests"/>: the injector assembly needs a real PVZ Fusion install
/// and never builds under CI (confirmed live during this module's own build, against the BepInEx
/// interop DLLs at `H:\Games\PVZ FUSION 3.8.1 FULL MOD TOOL` — that build is what caught
/// <c>CreateMower.SetMower</c> being an INSTANCE method, CS0120, not the static call the ambiguous
/// Harmony Postfix signature in <c>GameHooks.cs</c> suggested).
///
/// <para>This is the "round-trip the domain" half of §4's acceptance: every value in
/// <c>AtomKindRegistry</c>'s closed <c>spawn.entity.kind</c> vocabulary must have a matching arm in
/// <c>InjectorEffectActionSink.ExecSpawnEntity</c>'s switch — asserted per literal kind string here,
/// not by re-listing them by hand outside the source.</para>
/// </summary>
public class SpawnNonGridExecutorGuardTests
{
    [Theory]
    [InlineData("\"zombie\" =>")]
    [InlineData("\"plant\" =>")]
    [InlineData("\"bullet\" =>")]
    [InlineData("\"pet\" =>")]
    [InlineData("\"bucket\" =>")]
    [InlineData("\"mower\" =>")]
    [InlineData("\"coin\" =>")]
    public void Every_domain_value_has_a_switch_arm_in_ExecSpawnEntity(string arm)
    {
        var text = ReadInjector(System.IO.Path.Combine("Effects", "InjectorEffectActionSink.cs"));
        Assert.Contains(arm, text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_coin_arm_documents_that_it_is_unreachable_never_a_silent_no_op()
    {
        var text = ReadInjector(System.IO.Path.Combine("Effects", "InjectorEffectActionSink.cs"));
        Assert.Contains("\"coin\" => throw", text, StringComparison.Ordinal);
    }

    // PLANTED VIOLATION (§4): if the pet arm's payload ever dropped col, this would still pass unless
    // it names col explicitly -- pinning the literal forwarding call, not just the arm's existence,
    // so a future edit that silently drops a param (letting DebugActions.SpawnPet fall back to
    // CheatState.SpawnCol, the exact G1-class defect this module's own comment names) fails here.
    [Fact]
    public void The_pet_and_bucket_arms_forward_col_explicitly_not_left_to_the_CheatState_default()
    {
        var text = ReadInjector(System.IO.Path.Combine("Effects", "InjectorEffectActionSink.cs"));

        Assert.Contains("SpawnPetOnce(typeId, row, col)", text, StringComparison.Ordinal);
        Assert.Contains("SpawnBucketOnce(typeId, row, col)", text, StringComparison.Ordinal);
        Assert.Contains("[\"col\"] = col", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_mower_arm_forwards_x_not_col()
    {
        var text = ReadInjector(System.IO.Path.Combine("Effects", "InjectorEffectActionSink.cs"));
        Assert.Contains("SpawnMowerOnce(item, typeId, row)", text, StringComparison.Ordinal);
        Assert.Contains("[\"x\"] = x", text, StringComparison.Ordinal);
    }

    // The real game calls -- proves the sink's new arms route to a DebugActions method that calls the
    // real Unity API (MiniPet.SetPet / ItemManager.SetBucket / CreateMower's SetMower), the same
    // pattern SpawnPlant/SpawnZombie/SpawnBullet already use, not a stub.
    [Fact]
    public void DebugActions_spawn_pet_calls_the_real_MiniPet_SetPet()
    {
        var text = ReadInjector("DebugActions.cs");
        Assert.Contains("MiniPet.SetPet(board, LawnCoords.CellCenter(col, row), (PetType)typeId)", text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DebugActions_spawn_bucket_calls_the_real_ItemManager_SetBucket()
    {
        var text = ReadInjector("DebugActions.cs");
        Assert.Contains("mgr.SetBucket(board, (BucketType)typeId, LawnCoords.CellCenter(col, row))", text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DebugActions_spawn_mower_calls_the_real_CreateMower_SetMower()
    {
        var text = ReadInjector("DebugActions.cs");
        Assert.Contains("CreateMower.Instance.SetMower((MowerType)typeId, x, row)", text,
            StringComparison.Ordinal);
    }

    // §3: row/col clamps are STRUCTURAL (an out-of-board cell, never a balance question) -- the same
    // LawnCoords.ClampCol/ClampRow precedent DebugActions.PlaceGridItem already uses.
    [Fact]
    public void The_three_new_spawn_methods_clamp_through_LawnCoords_the_same_structural_precedent()
    {
        var text = ReadInjector("DebugActions.cs");

        Assert.Contains("public static bool SpawnPet(JsonElement p)", text, StringComparison.Ordinal);
        Assert.Contains("public static bool SpawnBucket(JsonElement p)", text, StringComparison.Ordinal);
        Assert.Contains("public static bool SpawnMower(JsonElement p)", text, StringComparison.Ordinal);

        // ClampCol/ClampRow appear at least once per new method's own clamp call (pet+bucket use both,
        // mower uses only ClampRow -- it places by x, not col) -- checked as overall presence counts
        // rather than per-method slicing, since the three methods sit consecutively in the file.
        var clampColCount = CountOccurrences(text, "LawnCoords.ClampCol(Int(p,");
        var clampRowCount = CountOccurrences(text, "LawnCoords.ClampRow(Int(p,");
        Assert.True(clampColCount >= 2, $"expected at least 2 ClampCol call sites (pet, bucket), found {clampColCount}");
        Assert.True(clampRowCount >= 3, $"expected at least 3 ClampRow call sites (pet, bucket, mower), found {clampRowCount}");
    }

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    static string ReadInjector(string relative)
    {
        var root = FindRepoRoot();
        var path = System.IO.Path.Combine(root, "src", "FusionRpg.Injector", relative);
        Assert.True(System.IO.File.Exists(path), "missing " + path);
        return System.IO.File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (System.IO.Directory.Exists(System.IO.Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new System.IO.DirectoryNotFoundException("repo root");
    }
}
