using System;
using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Saves;

/// <summary>
/// `save-identity` SE4.12 review fix — a host can never reach `RpgStore.Init` unconfigured. The store
/// resolves the authored registry itself (the same `FindUp` lookup every other seed reader uses) when
/// no hub was configured, so the E2E factory's own store — which `Init`s before `Program.cs` runs — no
/// longer depends on a `Configure` call it might forget.
/// </summary>
[Trait("VerificationId", "data.save-empires")]
public class NewSaveEmpiresHostWiringTests
{
    static string SeedPath([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        var root = Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
        return Path.Combine(root, "data", "seed", "saves", "_registry", "new-save-empires.v1.json");
    }

    [Fact]
    public void A_host_that_never_configured_the_hub_still_boots_and_seeds_from_the_authored_file()
    {
        try
        {
            NewSaveEmpiresHub.ResetForTests();
            Assert.False(NewSaveEmpiresHub.IsConfigured);

            // No Configure anywhere on this path: the store resolves the authored file by walking up
            // from the running image, exactly as the E2E factory's own store now does.
            using var testStore = DataTestStore.Create();
            Assert.Equal(EmpireId.Dave, testStore.Store.HumanEmpireOf(1));
        }
        finally
        {
            NewSaveEmpiresHub.Configure(NewSaveEmpires.Parse(File.ReadAllText(SeedPath())));
        }
    }
}
