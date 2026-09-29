using System.Reflection;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World.Turn;

/// <summary>
/// base-defense `siege-seam` 7.1 (spec-siege-seam.md §1): proves — with a test, not a claim in a
/// document — that <see cref="WorldCanonical"/> is independent of <see cref="BattleRequest"/> and
/// <see cref="BattleOutcome"/>. This is the whole basis for the module's stated "zero golden risk":
/// if it ever stopped being true, widening the seam (adding <c>BoardProjection</c>, budgets, a
/// withdrawal verb) would risk moving world goldens purely by existing, whether or not anything
/// downstream actually reads the new fields.
/// </summary>
public class WorldCanonicalSeamGuardTests
{
    [Fact]
    public void WorldCanonical_Write_takes_only_a_WorldState()
    {
        var method = typeof(WorldCanonical).GetMethod(nameof(WorldCanonical.Write), BindingFlags.Public | BindingFlags.Static);
        Assert.NotNull(method);

        var parameters = method!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(WorldState), parameters[0].ParameterType);
    }

    [Fact]
    public void WorldCanonical_source_never_mentions_the_battle_seam_types()
    {
        var path = FindSource("WorldCanonical.cs");
        var text = File.ReadAllText(path);

        // The seam types by name -- if widening BattleSeam.cs ever leaked one of these into the
        // canonical writer, this is the line that should go red before any golden does.
        foreach (var banned in new[] { "BattleRequest", "BattleOutcome", "BattleSideOutcome", "BoardProjection", "SideBudget" })
            Assert.DoesNotContain(banned, text, StringComparison.Ordinal);
    }

    [Fact]
    public void No_public_WorldState_member_is_typed_as_a_battle_seam_type()
    {
        // The other half of the same claim: WorldState itself carries nothing seam-shaped that a
        // future WorldCanonical.Write could accidentally start reading. Reflection over the actual
        // shipped record, not a read of its source text.
        var seamTypes = new HashSet<Type> { typeof(BattleRequest), typeof(BattleOutcome), typeof(BattleSideOutcome) };
        foreach (var prop in typeof(WorldState).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Assert.DoesNotContain(prop.PropertyType, seamTypes);
    }

    static string FindSource(string relativeFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FusionRpg.Core", "World", relativeFileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not find src/FusionRpg.Core/World/{relativeFileName} from any parent of {AppContext.BaseDirectory}");
    }
}
