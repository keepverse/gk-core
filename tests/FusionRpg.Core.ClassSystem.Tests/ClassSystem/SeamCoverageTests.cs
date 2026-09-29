using System.Reflection;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Plugins;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

/// <summary>class-system-todo.md P1.9 — every registered <see cref="IStatModifierPlugin"/> and
/// <see cref="IActorStatSubsystem"/> either contributes or declares itself inert
/// (<see cref="IDeclaredInertContributor"/>). distribution-reconcile's own finding was that "nothing
/// in CI can tell wired from reserved" — this is that check. Structural (IL body size), not
/// behavioural: invoking `Contribute` with one context and checking bag emptiness would false-negative
/// on a plugin that is genuinely wired but has nothing to contribute for that particular context.</summary>
public class SeamCoverageTests
{
    // An entirely empty `{ }` method body compiles to ~2 IL bytes (nop; ret, or just ret). Any method
    // doing real work — loading a field, branching, calling another method — is measurably larger.
    // Verified empirically against this repo's own 4 known-empty stubs and its known-real plugins
    // below, with headroom on both sides.
    const int InertIlByteCeiling = 8;

    [Fact]
    public void EveryRegisteredPluginContributesOrDeclaresInert()
    {
        var sys = StatSystemBootstrap.CreateDefault();
        var failures = new List<string>();
        foreach (var plugin in sys.Plugins.Ordered())
        {
            var method = plugin.GetType().GetMethod(nameof(IStatModifierPlugin.Contribute))!;
            var ilBytes = method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
            var isTriviallyEmpty = ilBytes <= InertIlByteCeiling;
            var declaresInert = plugin is IDeclaredInertContributor;
            if (isTriviallyEmpty && !declaresInert)
                failures.Add($"{plugin.PluginId}: Contribute is trivially empty ({ilBytes} IL bytes) but does not implement IDeclaredInertContributor");
        }
        Assert.True(failures.Count == 0, "silently-empty plugin(s): " + string.Join(", ", failures));
    }

    [Fact]
    public void EveryRegisteredSubsystemContributesOrDeclaresInert()
    {
        var hub = ActorHubBootstrap.CreateDefault();
        var failures = new List<string>();
        foreach (var subsystem in hub.Subsystems)
        {
            var method = subsystem.GetType().GetMethod(nameof(IActorStatSubsystem.ContributeDerived))!;
            var ilBytes = method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
            var isTriviallyEmpty = ilBytes <= InertIlByteCeiling;
            var declaresInert = subsystem is IDeclaredInertContributor;
            if (isTriviallyEmpty && !declaresInert)
                failures.Add($"{subsystem.SubsystemId}: ContributeDerived is trivially empty ({ilBytes} IL bytes) but does not implement IDeclaredInertContributor");
        }
        Assert.True(failures.Count == 0, "silently-empty subsystem(s): " + string.Join(", ", failures));
    }

    [Fact]
    public void KnownStubsAreAllDeclaredInert()
    {
        // The four shipped empty plugins must each carry a non-empty reason -- a marker with no
        // content would pass the structural check while still being uninformative.
        var stubs = new IStatModifierPlugin[]
        {
            new ClassStatPlugin(), new AchievementStatPlugin(), new ItemStatPlugin(), new BuffStatPlugin()
        };
        foreach (var stub in stubs)
        {
            var inert = Assert.IsAssignableFrom<IDeclaredInertContributor>(stub);
            Assert.False(string.IsNullOrWhiteSpace(inert.InertReason), $"{stub.PluginId}: empty InertReason");
        }
    }

    [Fact]
    public void PlantedSilentlyEmptyContributor_isCaught()
    {
        // The planted-violation proof this guard's own todo entry asks for: a fresh plugin with a
        // truly empty Contribute and NO IDeclaredInertContributor marker must be flagged, exactly as
        // ClassStatPlugin would have been before P1.8/P1.9 landed.
        var sys = new StatSystem();
        sys.Plugins.Register(new SilentlyEmptyPlantedPlugin());

        var failures = new List<string>();
        foreach (var plugin in sys.Plugins.Ordered())
        {
            var method = plugin.GetType().GetMethod(nameof(IStatModifierPlugin.Contribute))!;
            var ilBytes = method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
            if (ilBytes <= InertIlByteCeiling && plugin is not IDeclaredInertContributor)
                failures.Add(plugin.PluginId);
        }
        Assert.Contains("test.planted-empty", failures);
    }

    sealed class SilentlyEmptyPlantedPlugin : IStatModifierPlugin
    {
        public string PluginId => "test.planted-empty";
        public int Order => 999;
        public void Contribute(StatContext ctx, IModifierBagEditor bag) { }
    }
}
