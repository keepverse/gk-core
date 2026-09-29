using System;
using System.IO;
using FusionRpg.Core.Narrative;
using FusionRpg.Server.Narrative;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// identity-rename T2: the server boot reads the authored lead-names registry. Two things have to hold
/// for a real deployment, and this file pins both against the <b>published</b> layout (the directory
/// this test host's own binary sits in), not against the repository tree:
/// <list type="number">
/// <item>the <c>&lt;Content&gt;</c> rule in <c>FusionRpg.Server.csproj</c> actually put the file next
/// to the exe, and the boot read resolves a lead token to the registry's own display string;</item>
/// <item>a server started <b>without</b> the file fails at boot with a message naming the path, and
/// never falls back to a default name.</item>
/// </list>
/// <para><c>BootContentCopyRuleTests</c> guards the copy-rule contract generically (it walks every
/// <c>Path.Combine(AppContext.BaseDirectory, "data", ...)</c> literal in <c>Program.cs</c>); this file
/// pins that this particular read resolves and how it fails. <c>LeadNamesTests</c> in
/// <c>FusionRpg.Core.Tests</c> pins the other half of "never a silent default": a hub that was never
/// configured refuses to answer at all.</para>
/// </summary>
[Trait("VerificationId", "server.lead-names")]
public class LeadNamesBootTests
{
    /// <summary>The path <c>Program.cs</c> names, relative to the server's own directory.</summary>
    static string PublishedPath() => Path.Combine(
        AppContext.BaseDirectory, "data", "seed", "narrative", "_registry", "names.en.v1.json");

    static LeadNames RegistryAt(string path) => LeadNames.Parse(File.ReadAllText(path));

    [Fact]
    public void The_published_server_carries_the_registry_and_resolves_the_summoner_to_its_display()
    {
        var path = PublishedPath();
        Assert.True(File.Exists(path),
            "the copy rule in FusionRpg.Server.csproj did not put the lead names registry next to the "
            + "server, so a published server cannot boot: " + path);

        LeadNamesBoot.Configure(path);

        // Asserted against the REGISTRY's value, never a literal: a rename stays a one-row edit and
        // this test follows it.
        var registry = RegistryAt(path);
        Assert.True(LeadNamesHub.IsConfigured);
        foreach (var token in LeadTokens.All)
            Assert.Equal(registry.Display(token), LeadNamesHub.Current.Display(token));
    }

    [Fact]
    public void A_server_without_the_file_fails_at_boot_naming_the_path_and_keeps_the_configured_registry()
    {
        var published = PublishedPath();
        LeadNamesBoot.Configure(published);
        var before = LeadNamesHub.Current.Display(LeadTokens.Summoner);

        var missing = Path.Combine(
            Path.GetTempPath(), "fusionrpg-no-names-" + Guid.NewGuid().ToString("N"),
            "data", "seed", "narrative", "_registry", "names.en.v1.json");

        var ex = Assert.Throws<FileNotFoundException>(() => LeadNamesBoot.Configure(missing));
        Assert.Equal(missing, ex.FileName);
        Assert.Contains(missing, ex.Message, StringComparison.Ordinal);

        // A fallback-to-default implementation would have overwritten the configured registry here.
        Assert.Equal(before, LeadNamesHub.Current.Display(LeadTokens.Summoner));
    }
}
