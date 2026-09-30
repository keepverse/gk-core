using System.Linq;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Actions.Unlock;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// A-U1 (spec-rung-semantics.md): "does a rung mean the same thing to the author, the holder and the
/// guard? Today it does not, and five specs are written against the assumption that it does." Tests
/// mirror the spec's own §5 numbering.
/// </summary>
public class RungSemanticsTests
{
    static string RepoRoot([CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }

    static RungTable ShippedRungTable() =>
        RungTableLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "action-rungs.v1.json")));

    static UnlockTuning ShippedUnlockTuning() =>
        UnlockTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "action-unlock.v1.json")));

    static ActionRow Row(string id, int rung, string containerId = "") => new()
    {
        ActionId = id,
        Name = id,
        Kind = ActionKind.Skill,
        ContainerId = containerId,
        Rung = rung,
    };

    /// <summary>Test 1: <see cref="StructureBudgetGuard.Check"/> resolves the AUTHORED rung
    /// (<see cref="ActionRow.Rung"/>), never a holder-derived one — pinned rather than accidental, per
    /// §3.1's "the guard is correct; the specs' inference is wrong."</summary>
    [Fact]
    public void StructureBudgetGuard_resolves_the_authored_row_Rung_not_any_holder_derived_value()
    {
        var table = ShippedRungTable();

        // Rung 5 authors "condition" into its budget; rung 1 does not. Same content-shaped row
        // otherwise (a ConditionsJson), differing ONLY in the authored Rung — proving the guard's
        // verdict tracks that column and nothing a holder's earn history could ever touch (a
        // holder-side value never appears anywhere in this call).
        var lowRung = Row("action.test.low", rung: 1) with { ConditionsJson = "{}" };
        var highRung = Row("action.test.high", rung: 5) with { ConditionsJson = "{}" };

        var lowResult = StructureBudgetGuard.Check(lowRung, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(), table);
        var highResult = StructureBudgetGuard.Check(highRung, Array.Empty<ActionCostRow>(), Array.Empty<ActionScopeRow>(), table);

        Assert.False(lowResult.IsOk);  // rung 1's budget is empty — condition is unspendable there
        Assert.True(highResult.IsOk);  // rung 5 budgets for condition
    }

    /// <summary>Test 2: <c>effectiveRung</c> and <c>Rung</c> are distinct in the TYPE system, not two
    /// uses of one <c>int</c> — the names cannot silently re-merge.</summary>
    [Fact]
    public void EffectiveRung_is_a_distinct_type_from_the_authored_int_Rung()
    {
        Assert.NotEqual(typeof(int), typeof(EffectiveRung));

        var tuning = ShippedUnlockTuning();
        var effective = UnlockLadder.EffectiveRung(earnCount: 3, tuning);

        Assert.IsType<EffectiveRung>(effective);
        Assert.Equal(3, effective.Value);

        // ActionRow.Rung stays a plain int — an authored row never carries an EffectiveRung.
        var authored = Row("action.test.type", rung: 3).Rung;
        Assert.IsType<int>(authored);
    }

    /// <summary>Test 3 (corrected 2026-09-03 — there is no holder-side clamp to remove; the floor
    /// was the AUTHORED `rungBand`, so this asserts against the authored window itself): rung 1's
    /// `structureBudget` is empty, matching a first-ever signature unlock arriving at rung 1 rather
    /// than being forced to rung 5's `costMulti: 3627`.</summary>
    [Fact]
    public void The_authored_rung_one_row_carries_no_structure_budget_and_no_floor_tax()
    {
        var table = ShippedRungTable();

        Assert.True(table.TryGet(1, out var rung1));
        Assert.Empty(rung1.StructureBudget);
        Assert.Equal(1000, rung1.CostMulti); // no floor tax — 1.0x, not rung 5's 3627 (3.627x)

        Assert.True(table.TryGet(5, out var rung5));
        Assert.Equal(3627, rung5.CostMulti); // recorded as the moot cost the dropped floor would have paid
    }

    /// <summary>Test 4: the rejected shape (a signature `rungBand` floor above 1) stays rejected —
    /// mechanically, by absence: nothing committed under <c>data/</c> authors a `rungBand` window
    /// whose floor exceeds 1. Scoped to DATA, never markdown prose — the docs legitimately quote the
    /// old `[5,10]` shape inside their own "⛔ CORRECTED" history, and a text scan over prose cannot
    /// tell a live authored value from a historical citation. A-S1 (the module that would emit a real
    /// `rungBand`) is unbuilt, so this is the honest form of "planted violation" available today:
    /// prove the shape does not exist in any committed CONTENT, not that a validator refuses it (no
    /// such validator has a caller yet — nothing generates a `rungBand` in production).</summary>
    [Fact]
    public void No_committed_data_file_authors_a_rungBand_window_with_a_floor_above_one()
    {
        var root = RepoRoot();
        var dataDir = Path.Combine(root, "data");
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(dataDir, "*.json", SearchOption.AllDirectories))
        {
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), @"rungBand[^\[]*\[\s*(\d+)\s*,"))
            {
                var floor = int.Parse(m.Groups[1].Value);
                if (floor > 1) offenders.Add($"{Path.GetRelativePath(root, file)}: floor {floor}");
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>Test 4b: `minRung` never existed in `src/` or `data/` as a SEPARATE, unrelated concept
    /// (e.g. an action-rung clamp) — the only `MinRung` meaning in the tree is the aura ladder's own
    /// consumption floor (`AuraTuning.MinRung`). A drift test pins that fact, so a future clamp cannot
    /// arrive unnamed. It does NOT pin how many files legitimately reference that one constant — a
    /// second, third, ... real caller of `AuraTuning.MinRung` is expected content growth, not drift
    /// (backlog-clear AU2 added `AuraMagnitude.cs` as the second caller); sorted, order-independent
    /// comparison, so file-enumeration order is never a false signal either.</summary>
    [Fact]
    public void MinRung_has_zero_hits_outside_the_unrelated_aura_ladder_constant()
    {
        var root = RepoRoot();
        var hits = new List<string>();
        foreach (var dir in new[] { "src", "data" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full)) continue;
            foreach (var file in Directory.GetFiles(full, "*.*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)) continue;
                if (!file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;

                var text = File.ReadAllText(file);
                if (text.Contains("minRung", StringComparison.Ordinal) || text.Contains("MinRung", StringComparison.Ordinal))
                    hits.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        var expected = new[]
        {
            "src/FusionRpg.Core/Aura/AuraTuning.cs",
            "src/FusionRpg.Core/Aura/AuraMagnitude.cs",
        };
        Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), hits.OrderBy(x => x, StringComparer.Ordinal));
    }

    /// <summary>Test 5: `heldCap` and `rungCap` are separate keys — raising one leaves the other
    /// untouched.</summary>
    [Fact]
    public void HeldCap_and_rungCap_are_independent_dials()
    {
        var widened = ShippedUnlockTuning() with { RungCap = 15 };
        Assert.Equal(10, widened.HeldCap);
        Assert.Equal(15, widened.RungCap);

        // A holder who has earned past 10 now reaches effective rung 15, but TryAccept's own
        // capacity check still refuses a 11th held unlock — the two ceilings never share a read.
        Assert.Equal(15, UnlockLadder.EffectiveRung(earnCount: 999, widened).Value);

        var state = UnlockState.Empty();
        for (var i = 0; i < widened.HeldCap; i++)
            state.TryAccept($"skill.filler.{i}", widened, new AlwaysHitRng());
        // Self-referential (population-pin SE3.3, 2026-09-19): fills to widened's OWN configured cap,
        // not a coincidentally-matching second literal.
        Assert.Equal(widened.HeldCap, state.Held.Count);

        var refused = state.TryAccept("skill.overflow", widened, new AlwaysHitRng());
        Assert.False(refused.Accepted);
        Assert.Equal(UnlockRefusalReason.AtCapacity, refused.Reason);
    }

    /// <summary>Test 6: splitting the cap changes NO shipped behaviour — both dials still read the
    /// same starting value.</summary>
    [Fact]
    public void The_shipped_tuning_still_carries_equal_heldCap_and_rungCap_values()
    {
        var shipped = ShippedUnlockTuning();
        Assert.Equal(10, shipped.HeldCap);
        Assert.Equal(10, shipped.RungCap);
        Assert.Equal(shipped.HeldCap, shipped.RungCap); // behaviour-neutral by construction
    }

    /// <summary>Test 7 (and test 8's mechanical proof folded in — no repo-wide "every power scale is
    /// registered" scanner exists to plant a violation against, so this pins the positive fact
    /// directly, the same pattern <c>DistributionReconcileVerdictTests</c> already uses against this
    /// same file): the rung ladder's row exists in <c>ssot-power-scale.md</c> §10/§11.</summary>
    [Fact]
    public void The_rung_ladder_has_its_row_in_the_power_scale_register()
    {
        // docs/ is gk-workflow's, which is the workspace root and an ancestor of gk-core. See
    // RungPowerBudgetTests for the same read.
    var text = File.ReadAllText(Path.Combine(KeepverseRoots.Workspace(), "docs", "architecture", "power", "ssot-power-scale.md"));

        Assert.Contains("UnlockLadder.EffectiveRung", text, StringComparison.Ordinal);
        Assert.Contains("Action unlock ladder", text, StringComparison.Ordinal);
        Assert.Contains("heldCap", text, StringComparison.Ordinal);
        Assert.Contains("rungCap", text, StringComparison.Ordinal);
    }

    sealed class AlwaysHitRng : FusionRpg.Core.Effects.Atoms.IAtomRandom
    {
        public int NextInclusive(int min, int max) => min;
        public int NextPerMille() => 0;
    }
}
