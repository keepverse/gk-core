using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// commander-identity SE4.3: Server tests that build their own <c>WebApplication</c> (not
/// <c>Program.cs</c>) still need the process-wide commander directory, which production configures at
/// boot. One module initializer reads the real authored registry, same convention as this assembly's
/// other test bootstraps.
/// </summary>
internal static class CommanderDirectoryTestBootstrap
{
    [ModuleInitializer]
    public static void Init()
    {
        FusionRpg.Core.Commanders.CommanderDirectoryHub.Configure(
            FusionRpg.Core.Commanders.DataCommanderDirectory.Parse(
                File.ReadAllText(Path.Combine(
                    KeepverseRoots.Content(), "data", "seed", "commanders", "_registry", "default-commanders.v1.json"))));
        // identity-rename T13: the same registry production configures before store.Init().
        FusionRpg.Core.Narrative.LeadNamesHub.Configure(FusionRpg.Core.Narrative.LeadNames.Parse(
                File.ReadAllText(Path.Combine(
                    KeepverseRoots.Content(), "data", "seed", "narrative", "_registry", "names.en.v1.json"))));
        // save-identity SE4.12: `RpgStore.Init` seeds a save's empires from the authored registry, so
        // every test that constructs a store needs it configured first.
        FusionRpg.Core.Saves.NewSaveEmpiresHub.Configure(
            FusionRpg.Core.Saves.NewSaveEmpires.Parse(
                File.ReadAllText(Path.Combine(
                    KeepverseRoots.Content(), "data", "seed", "saves", "_registry", "new-save-empires.v1.json"))));
    }

    static string FindRepoRoot()
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
