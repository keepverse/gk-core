using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Match.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Match.Ai;

/// <summary>
/// zomboss-deploy-ai T3.1 (spec-zomboss-deploy-ai.md Correction 2) — <see cref="ILawnBoardView"/>'s own
/// two acceptance criteria: the type boundary is structurally enforced (no leak-back-to-full-board-access
/// path exists to compile against), and relation resolves through ownership, not raw on-board side.
/// </summary>
public class ILawnBoardViewTests
{
    sealed class FakeOracle : IOwnSideOracle
    {
        readonly Dictionary<string, RelationKind?> _byPtr;
        public FakeOracle(Dictionary<string, RelationKind?> byPtr) => _byPtr = byPtr;
        public RelationKind? RelationOf(string ptr) => _byPtr.TryGetValue(ptr, out var r) ? r : null;
    }

    [Fact]
    public void Build_resolves_relation_from_the_oracle_not_a_raw_side_field()
    {
        // The whole point: nothing in this call ever mentions "which mechanical side this ptr is on" —
        // only the oracle's own answer decides Relation. A hypnotized/side-swapped unique creature (its
        // mechanical side flips, its OwnershipOracle answer does not) is exactly this shape.
        var oracle = new FakeOracle(new() { ["ptr-hypno-swapped"] = RelationKind.Ally });

        var unit = LawnUnitViewFactory.Build("ptr-hypno-swapped", hpCurrent: 42, hpMax: 100, oracle);

        Assert.Equal(RelationKind.Ally, unit.Relation);
        Assert.Equal("ptr-hypno-swapped", unit.Ptr);
        Assert.Equal(42, unit.HpCurrent);
        Assert.Equal(100, unit.HpMax);
    }

    [Fact]
    public void Build_treats_an_unregistered_ptr_as_Enemy_never_Self_or_Ally()
    {
        var oracle = new FakeOracle(new());

        var unit = LawnUnitViewFactory.Build("vanilla-zombie-ptr", hpCurrent: 20000, hpMax: 20000, oracle);

        Assert.Equal(RelationKind.Enemy, unit.Relation);
    }

    [Fact]
    public void Build_throws_on_a_null_oracle_rather_than_silently_defaulting()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LawnUnitViewFactory.Build("ptr", 1, 1, null!));
    }

    [Fact]
    public void LawnBoardSnapshot_Empty_carries_no_units_and_zero_waves()
    {
        Assert.Equal(0, LawnBoardSnapshot.Empty.WaveNumber);
        Assert.Equal(0, LawnBoardSnapshot.Empty.MaxWave);
        Assert.Empty(LawnBoardSnapshot.Empty.VisibleUnits);
    }

    /// <summary>
    /// W26-equivalent for this module (spec-zomboss-deploy-ai.md §Boundaries: "no privileged reads").
    /// Mirrors `WorldDeterminismGuardTests.Nothing_under_World_Ai_may_read_the_world_itself` exactly —
    /// a scorer that consulted `MatchRuntime`/`Board`/`MatchSnapshot` directly would not give wrong
    /// answers, it would give suspiciously good ones. Making the leak-prone type names unmentionable
    /// under this folder is the only test that would actually catch it.
    /// </summary>
    [Fact]
    public void Nothing_under_Match_Ai_may_read_the_board_itself()
    {
        var violations = new List<string>();

        foreach (var file in AiSourceFiles())
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (ReadsTheBoardItself(lines[i]))
                    violations.Add($"{Path.GetFileName(file)}:{i + 1} -> {lines[i].Trim()}");
        }

        Assert.True(violations.Count == 0,
            "a zomboss-deploy-ai type must read ILawnBoardView, never the board itself:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void The_guard_would_actually_catch_a_violation()
    {
        Assert.True(ReadsTheBoardItself("    static MatchRuntime? Cheat;"));
        Assert.True(ReadsTheBoardItself("        var truth = runtime.Board; // MatchRuntime leaked in"));
        Assert.True(ReadsTheBoardItself("    void Score(MatchSnapshot snap) { }"));

        Assert.False(ReadsTheBoardItself("/// never touches MatchRuntime — see the spec"));
        Assert.False(ReadsTheBoardItself("// MatchSnapshot is deliberately unreachable from here"));
        Assert.False(ReadsTheBoardItself("        var view = new LawnBoardSnapshot(...);"));
    }

    static bool ReadsTheBoardItself(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)) return false;
        return line.Contains("MatchRuntime", StringComparison.Ordinal)
            || line.Contains("MatchSnapshot", StringComparison.Ordinal)
            || line.Contains(": Board", StringComparison.Ordinal)
            || line.Contains("(Board ", StringComparison.Ordinal)
            || line.Contains(" Board.", StringComparison.Ordinal);
    }

    static IEnumerable<string> AiSourceFiles()
    {
        var root = Path.Combine(FindRepoRoot(), "src", "FusionRpg.Core", "Match", "Ai");
        return Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            : Enumerable.Empty<string>();
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FusionRpg.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
