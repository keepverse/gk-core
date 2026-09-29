using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>T6/T7/T8 (action-todo.md, spec-targeting.md). The typed authoring contract, compiled to
/// the shipped resolver without modifying it.</summary>
public class ActionTargetingTests
{
    static BoardEntitySnap Entity(string ptr, string side, int col, int row, int typeId = 1) => new()
    {
        Ptr = ptr, Side = side, TypeId = typeId, Col = col, Row = row,
    };

    // ---- ActionTargetSpecJson — unknown keys rejected, round trip -------------------------------------

    [Fact]
    public void An_unknown_top_level_key_is_rejected()
    {
        var result = ActionTargetSpecJson.TryRead("""{"mode":"single","telekinesis":true}""", out _);
        Assert.False(result.IsOk);
        Assert.Contains("telekinesis", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_filter_key_is_rejected()
    {
        var result = ActionTargetSpecJson.TryRead(
            """{"mode":"single","filters":{"psychic":true}}""", out _);
        Assert.False(result.IsOk);
        Assert.Contains("psychic", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_absent_spec_reads_as_the_default()
    {
        var result = ActionTargetSpecJson.TryRead(null, out var spec);
        Assert.True(result.IsOk, result.ToString());
        Assert.Equal(ActionTargetMode.Single, spec.Mode);
        Assert.Equal(ActionRelation.Enemy, spec.Relation);
    }

    [Fact]
    public void A_full_spec_round_trips_through_write_and_read()
    {
        var spec = new ActionTargetSpec
        {
            Mode = ActionTargetMode.Multi,
            Relation = ActionRelation.Ally,
            Count = 3,
            MaxTargets = 5,
            Ordering = ActionTargetOrdering.SourceOrder,
            Filters = new ActionTargetFilters
            {
                TypeIds = new[] { 10, 20 },
                ExcludeMindControlled = true,
                Row = 2,
                ColMin = 1,
                ColMax = 4,
            },
        };

        var json = ActionTargetSpecJson.Write(spec);
        var result = ActionTargetSpecJson.TryRead(json, out var read);
        Assert.True(result.IsOk, result.ToString());

        Assert.Equal(spec.Mode, read.Mode);
        Assert.Equal(spec.Relation, read.Relation);
        Assert.Equal(spec.Count, read.Count);
        Assert.Equal(spec.MaxTargets, read.MaxTargets);
        Assert.Equal(spec.Ordering, read.Ordering);
        Assert.Equal(spec.Filters.TypeIds, read.Filters.TypeIds);
        Assert.Equal(spec.Filters.ExcludeMindControlled, read.Filters.ExcludeMindControlled);
        Assert.Equal(spec.Filters.Row, read.Filters.Row);
        Assert.Equal(spec.Filters.ColMin, read.Filters.ColMin);
        Assert.Equal(spec.Filters.ColMax, read.Filters.ColMax);
    }

    // ---- TargetSpecCompiler — one row serves both factions ---------------------------------------------

    [Fact]
    public void One_authored_action_serves_both_factions()
    {
        var spec = new ActionTargetSpec { Mode = ActionTargetMode.Single, Relation = ActionRelation.Enemy };
        var compiled = TargetSpecCompiler.Compile(spec);

        var board = new BoardSnapshot(new[]
        {
            Entity("plant:1", "plant", 0, 0),
            Entity("zombie:1", "zombie", 0, 5),
        });

        var fromPlant = ActionTargetResolver.Resolve(
            compiled, CasterSide.Plant, "plant:1", 0, 100, board, null, null, null);
        var fromZombie = ActionTargetResolver.Resolve(
            compiled, CasterSide.Zombie, "zombie:1", 0, 100, board, null, null, null);

        Assert.Equal(new[] { "zombie:1" }, fromPlant);
        Assert.Equal(new[] { "plant:1" }, fromZombie);
    }

    [Fact]
    public void Self_never_enters_the_resolver_pool()
    {
        var spec = new ActionTargetSpec { Mode = ActionTargetMode.Self };
        var compiled = TargetSpecCompiler.Compile(spec);
        Assert.True(compiled.IsSelf);
        Assert.Empty(compiled.PerSide);

        var board = new BoardSnapshot(new[] { Entity("plant:1", "plant", 0, 0) });
        var result = ActionTargetResolver.Resolve(
            compiled, CasterSide.Plant, "plant:1", 0, 0, board, null, null, null);
        Assert.Equal(new[] { "plant:1" }, result);
    }

    // ---- range gate: no board passes, a board excludes ---------------------------------------------------

    [Fact]
    public void With_no_board_every_range_check_passes()
    {
        var spec = new ActionTargetSpec { Mode = ActionTargetMode.All, Relation = ActionRelation.Enemy };
        var compiled = TargetSpecCompiler.Compile(spec);

        // The caster itself is not in the snapshot -> "no board" per this module's proxy.
        var board = new BoardSnapshot(new[] { Entity("zombie:1", "zombie", 0, 999) });
        var result = ActionTargetResolver.Resolve(
            compiled, CasterSide.Plant, "plant:missing", minRange: 0, maxRange: 1, board, null, null, null);

        Assert.Equal(new[] { "zombie:1" }, result);
    }

    [Fact]
    public void With_a_board_the_range_gate_excludes_outside_the_window()
    {
        var spec = new ActionTargetSpec { Mode = ActionTargetMode.All, Relation = ActionRelation.Enemy };
        var compiled = TargetSpecCompiler.Compile(spec);

        var board = new BoardSnapshot(new[]
        {
            Entity("plant:1", "plant", 0, 0),
            Entity("zombie:near", "zombie", 0, 2),
            Entity("zombie:far", "zombie", 0, 9),
        });

        var result = ActionTargetResolver.Resolve(
            compiled, CasterSide.Plant, "plant:1", minRange: 0, maxRange: 3, board, null, null, null);

        Assert.Equal(new[] { "zombie:near" }, result);
    }

    // ---- the gate applies before the random pick (spec-targeting.md §6c) ---------------------------------

    [Fact]
    public void The_range_gate_runs_before_the_random_pick_not_after()
    {
        // Four candidates, all in range. Request all 4 via RolledTarget -> the resolver must return
        // exactly the four, regardless of shuffle order (Count >= pool size).
        var spec = new ActionTargetSpec
        {
            Mode = ActionTargetMode.RolledTarget, Relation = ActionRelation.Enemy, Count = 4,
        };
        var compiled = TargetSpecCompiler.Compile(spec);

        var board = new BoardSnapshot(new[]
        {
            Entity("plant:1", "plant", 0, 0),
            Entity("zombie:a", "zombie", 0, 1),
            Entity("zombie:b", "zombie", 0, 2),
            Entity("zombie:c", "zombie", 0, 3),
            Entity("zombie:d", "zombie", 0, 9), // moved out of range below
        });

        var rng = ActionTargetResolver.DeriveRng(runSeed: 12345);

        // d is out of range: requesting Count=4 over a pool that only has 3 IN-RANGE members must
        // return exactly those 3 -- not 4 with d smuggled in, and not fewer than 3 because d was
        // drawn first and then discarded (which is exactly what a post-filter gate would risk).
        var result = ActionTargetResolver.Resolve(
            compiled, CasterSide.Plant, "plant:1", minRange: 0, maxRange: 3, board, null, null, rng);

        Assert.Equal(3, result.Count);
        Assert.Contains("zombie:a", result);
        Assert.Contains("zombie:b", result);
        Assert.Contains("zombie:c", result);
        Assert.DoesNotContain("zombie:d", result);
    }

    [Fact]
    public void Ordering_is_stable_through_the_gate_never_a_reshuffle()
    {
        // All mode is ordinal-ptr sorted by the shipped resolver. Removing an out-of-range,
        // non-adjacent member must not disturb the relative order of the survivors.
        var spec = new ActionTargetSpec { Mode = ActionTargetMode.All, Relation = ActionRelation.Enemy };
        var compiled = TargetSpecCompiler.Compile(spec);

        var board = new BoardSnapshot(new[]
        {
            Entity("plant:1", "plant", 0, 0),
            Entity("zombie:a", "zombie", 0, 1),
            Entity("zombie:m", "zombie", 0, 9), // out of range, sorts between a and z
            Entity("zombie:z", "zombie", 0, 1),
        });

        var result = ActionTargetResolver.Resolve(
            compiled, CasterSide.Plant, "plant:1", minRange: 0, maxRange: 3, board, null, null, null);

        Assert.Equal(new[] { "zombie:a", "zombie:z" }, result);
    }

    // ---- D14 (combat-math-dedup Task 10): the area-shape vocabulary is declared once ----------------

    /// <summary>
    /// The enum member names ARE <see cref="AreaShapes"/>'s const values — the one declaration both
    /// the Core enum and the wire vocabulary are read from. Asserted as a SET (not a count) because
    /// this is a closed four-shape vocabulary a human edits: a fifth shape must move both sides
    /// together, and the derivation in <c>ActionAreaShapes</c>/<c>TargetSpecCompiler.ToWireShape</c>
    /// is what makes that automatic.
    /// </summary>
    [Fact]
    public void ActionAreaShape_names_are_the_AreaShapes_const_values()
    {
        var enumNames = Enum.GetNames<ActionAreaShape>().ToHashSet(StringComparer.Ordinal);
        var wireConsts = new[] { AreaShapes.Row, AreaShapes.Column, AreaShapes.Square, AreaShapes.Rectangle }
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(wireConsts, enumNames);
    }

    [Theory]
    [InlineData(ActionAreaShape.Row, "row")]
    [InlineData(ActionAreaShape.Column, "column")]
    [InlineData(ActionAreaShape.Square, "square")]
    [InlineData(ActionAreaShape.Rectangle, "rectangle")]
    public void ActionAreaShapes_Name_and_TryParse_round_trip_lowercased(ActionAreaShape shape, string wire)
    {
        Assert.Equal(wire, ActionAreaShapes.Name(shape));
        Assert.True(ActionAreaShapes.TryParse(wire, out var parsed));
        Assert.Equal(shape, parsed);
    }

    [Fact]
    public void ActionAreaShapes_TryParse_still_refuses_a_wrong_case_and_an_unknown_name()
    {
        // Behaviour preserved from the hand-written switch: lowercase-only, unknown is a refusal.
        Assert.False(ActionAreaShapes.TryParse("Row", out _));
        Assert.False(ActionAreaShapes.TryParse("diamond", out _));
        Assert.False(ActionAreaShapes.TryParse(null, out _));
        Assert.Equal("", ActionAreaShapes.Name((ActionAreaShape)99));
    }

    /// <summary>
    /// The negative guard D14 asks for: the four lowercase literals may not return to
    /// <c>ActionAreaShapes</c>. The set-equality test above cannot see a re-introduced literal whose
    /// value still matches the enum (the exact drift this program exists to stop), so this scans the
    /// source. It bites on the pre-dedup shape (the switch bodies it replaced).
    /// </summary>
    [Fact]
    public void ActionAreaShapes_declares_no_shape_literal_of_its_own()
    {
        var path = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Actions", "ActionTargetSpec.cs");
        Assert.True(File.Exists(path), "missing " + path);
        var text = File.ReadAllText(path);

        var start = text.IndexOf("public static class ActionAreaShapes", StringComparison.Ordinal);
        Assert.True(start >= 0, "ActionAreaShapes class not found");
        var end = text.IndexOf("public static class ActionAnchorSources", start, StringComparison.Ordinal);
        Assert.True(end > start, "ActionAreaShapes class body not delimited");
        var body = text[start..end];

        foreach (var wire in new[] { "row", "column", "square", "rectangle" })
            Assert.DoesNotContain("\"" + wire + "\"", body, StringComparison.Ordinal);

        // Non-vacuity: the class must still derive its name from the enum.
        Assert.Contains("Enum.GetName", body, StringComparison.Ordinal);
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repo root");
    }
}
