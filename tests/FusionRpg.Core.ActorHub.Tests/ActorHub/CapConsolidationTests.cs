using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.ActorHub;

/// <summary>
/// spec-cap-consolidation.md §5 — <c>status.resist.{dot|cc|contagion}</c> had two homes for the same
/// cap (a hardcoded 0.95 at compose, the tunable re-applied at apply), so raising the tunable past 0.95
/// was a silent no-op: compose always ran first. Now there is exactly one enforcement point, at compose.
/// </summary>
public class CapConsolidationTests
{
    [Fact]
    public void RaisingTheCapActuallyRaisesIt()
    {
        using var _ = DerivedStatPolicy.UseScoped(new DerivedStatTuning(SchemaVersion: 1, Version: 1, CategoryResistCap: 0.99, TurnDefaultSpeed: 100));

        var composer = new DerivedComposer();
        var snap = composer.Compose(new[]
        {
            // Stack well past both the old and the new cap so only the cap decides the outcome.
            new DerivedModifier(DerivedStatChannels.StatusResistDot, DerivedModifierOp.Increased, 5.0)
        });

        Assert.Equal(0.99, snap.Get(DerivedStatChannels.StatusResistDot));
    }

    [Fact]
    public void LoweringStillLowers()
    {
        using var _ = DerivedStatPolicy.UseScoped(new DerivedStatTuning(SchemaVersion: 1, Version: 1, CategoryResistCap: 0.50, TurnDefaultSpeed: 100));

        var composer = new DerivedComposer();
        var snap = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.StatusResistDot, DerivedModifierOp.Increased, 5.0)
        });

        Assert.Equal(0.50, snap.Get(DerivedStatChannels.StatusResistDot));
    }

    [Fact]
    public void GoldensByteIdenticalAt095()
    {
        // No scope override -- exercises the ordinary global configuration a real test run boots with
        // (ContractTuningTestBootstrap's DefaultDerivedStats, 0.95), proving the refactor is invisible
        // at the shipped value.
        var reg = DerivedStatRegistry.CreateDefault();
        reg.TryGet(DerivedStatChannels.StatusResistDot, out var dot);
        reg.TryGet(DerivedStatChannels.StatusResistCc, out var cc);
        reg.TryGet(DerivedStatChannels.StatusResistContagion, out var contagion);
        reg.TryGet(DerivedStatChannels.StatusResistOmni, out var omni);

        Assert.Equal(0.95, dot.Cap);
        Assert.Equal(0.95, cc.Cap);
        Assert.Equal(0.95, contagion.Cap);
        Assert.Null(omni.Cap); // omni stays the uncapped balance knob

        var composer = new DerivedComposer();
        var snap = composer.Compose(new[]
        {
            new DerivedModifier(DerivedStatChannels.StatusResistDot, DerivedModifierOp.Increased, 5.0)
        });
        Assert.Equal(0.95, snap.Get(DerivedStatChannels.StatusResistDot));

        // The sparse per-status-id path agrees with the fixed-category path -- both read the same
        // frozen field, not two independently-live sources that could drift apart.
        Assert.True(reg.TryResolveChannel("status.resist.wither", out var sparse));
        Assert.Equal(0.95, sparse.Cap);
    }

    [Fact]
    public void OneClampNotTwo()
    {
        // Architecture test: the resist-cap clamp exists at exactly one site (DerivedComposer.Cap,
        // compose time). ResistanceEvaluator must carry zero Math.Min calls against a resist term --
        // that is precisely the second clamp this module deletes.
        var evaluatorPath = FindCoreFile("Status", "ResistanceEvaluator.cs");
        var evaluatorText = File.ReadAllText(evaluatorPath);
        Assert.DoesNotContain("Math.Min", evaluatorText, StringComparison.Ordinal);

        var composerPath = FindCoreFile("Stats", "Derived", "DerivedComposer.cs");
        var composerText = File.ReadAllText(composerPath);
        var clampCount = CountOccurrences(composerText, "Math.Min(value, def.Cap.Value)");
        Assert.Equal(1, clampCount);
    }

    [Fact]
    public void MissingTunableRejects()
    {
        var ex = Assert.Throws<DerivedStatTuningRejection>(() =>
            DerivedStatTuningLoader.Parse("""{"schemaVersion":1,"version":1}"""));
        Assert.Contains("categoryResistCap", ex.Message, StringComparison.Ordinal);
    }

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    static string FindCoreFile(params string[] relativeUnderCore)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName, "src", "FusionRpg.Core" }.Concat(relativeUnderCore).ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("could not find " + string.Join("/", relativeUnderCore) + " under src/FusionRpg.Core");
    }
}
