using System;
using System.IO;
using FusionRpg.Core.Narrative;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// identity-rename T2, end to end: the <b>real</b> server boot (<c>Program.cs</c> through
/// <see cref="RpgApiFactory"/>) configures the process-wide lead-names hub from the shipped registry.
///
/// <para>This is the proof <c>LeadNamesBootTests</c> cannot give: that test calls the boot read itself,
/// so it would still pass if <c>Program.cs</c> never called it. Nothing in this process configures the
/// hub except the host's own boot — no module initializer, no fixture — so a configured hub here is
/// reached only through the real pipeline.</para>
/// </summary>
[Trait("VerificationId", "server.lead-names")]
public class LeadNamesBootE2ETests : IClassFixture<RpgApiFactory>
{
    readonly RpgApiFactory _factory;

    public LeadNamesBootE2ETests(RpgApiFactory factory) => _factory = factory;

    [Fact]
    public void The_real_host_boot_configures_the_lead_names_registry_from_the_shipped_file()
    {
        // Booting the host runs Program.cs up to and including its one LeadNamesBoot.Configure call.
        using var client = _factory.CreateClient();

        Assert.True(LeadNamesHub.IsConfigured,
            "the server boot did not configure the lead names hub from its shipped registry");

        var registry = LeadNames.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "data", "seed", "narrative", "_registry", "names.en.v1.json")));

        // Against the registry's own value, never a literal: a lead rename is one row edit and this
        // assertion follows it.
        foreach (var token in LeadTokens.All)
            Assert.Equal(registry.Display(token), LeadNamesHub.Current.Display(token));
    }
}
