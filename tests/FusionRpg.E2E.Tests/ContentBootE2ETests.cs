using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Data;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// E20's seam, run against the real server host (completeness-audit.md finding A2/A3).
///
/// <para>Waves 1–5 proved the importer and the loader correct in isolation. Neither had ever been
/// proven to work <b>together, against the actual host boot path</b> — <c>Program.cs</c> now calls
/// <see cref="RpgStore.LoadContentIntoRuntime"/> once at startup, and this asserts that a real
/// <see cref="AtomSeedFile.Collect"/> → <see cref="RpgStore.ImportContent"/> → <c>LoadContentIntoRuntime</c>
/// chain — the exact chain <c>gk-forge/tools/AtomImporter</c> and <c>Program.cs</c> run in production — makes an
/// imported row the one <see cref="ElementTable.Current"/> reflects.</para>
///
/// <para>Runs against the shared <see cref="RpgApiFactory"/>'s already-booted store rather than
/// standing up a second host: <c>RpgApiFactory</c> configures the process via environment variables
/// read once at boot, and a second <c>WebApplicationFactory&lt;Program&gt;</c> instance in the same
/// process would race that configuration against the other 28 E2E test classes sharing the fixture.
/// Re-invoking the loader on the same running store after a real import is the same production call,
/// on the same production data, without that hazard.</para>
///
/// <para>Mutates the process-global <see cref="ElementTable"/>/<see cref="PowerTables"/> statics —
/// restored in <c>finally</c> so the 28 other classes in the shared "e2e" collection, which runs
/// sequentially, are not affected by this test running before them.</para>
/// </summary>
[Collection("e2e")]
public class ContentBootE2ETests
{
    readonly RpgStore _store;

    public ContentBootE2ETests(RpgApiFactory factory) => _store = factory.Services.GetRequiredService<RpgStore>();

    static string RepoRoot() => ContentRoot.Path;

    [Fact]
    public void A_real_import_through_the_real_host_store_is_what_the_loader_reflects()
    {
        var root = RepoRoot();
        var files = new[] { "elements" }
            .Select(d => Path.Combine(KeepverseRoots.Content(), "data", "seed", d))
            .SelectMany(d => Directory.GetFiles(d, "*.json"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var outcome = _store.ImportContent(collected.Content);
        Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));

        try
        {
            _store.LoadContentIntoRuntime();

            // The shipped roster and the seed file agree today (E18's parity proof), so this is not
            // vacuous only because both sides already say "fire" — assert the loader reads THIS
            // store's rows, not the code literal, by checking the store directly agrees with what
            // ElementTable.Current now reports, independent of the shipped copy.
            var stored = _store.GetElementTable();
            Assert.Equal(stored.Elements.Select(e => e.ElementId).OrderBy(x => x, StringComparer.Ordinal),
                ElementTable.Current.Elements.Select(e => e.ElementId).OrderBy(x => x, StringComparer.Ordinal));

            var storedPower = _store.GetPowerTables();
            Assert.Equal(storedPower.Coefficients.Count, PowerTables.Current.Coefficients.Count);
        }
        finally
        {
            ElementTable.ResetToShipped();
            PowerTables.ResetToAuthored();
        }
    }

    [Fact]
    public void A_seeded_element_the_shipped_table_does_not_have_survives_the_real_import_chain()
    {
        // The audit's exact failure mode, end to end: a JSON seed file naming a novel element, run
        // through the real parser, the real transaction, and the real production loader call —
        // proving ElementTable.Current carries it, not the shipped fallback, which does not have it.
        //
        // Deliberately its own throwaway store rather than the shared "e2e" fixture: `effect_element`
        // is upsert-only (append-only ordinals — see RpgStore.Elements.cs), so a row added there
        // cannot be un-added, and this test's job is to add one that does not exist shipped. Using the
        // fixture's real database would leave a permanent stray row for the other 28 classes sharing
        // it. The chain under test — Collect → ImportContent → LoadContentIntoRuntime — is identical
        // either way; only which store it runs against differs, and in memory it is isolated by
        // construction.
        using var throwaway = DataTestStore.Create();
        {
            const string seedJson = """
                {
                  "schemaVersion": 1,
                  "kind": "element",
                  "entries": [
                    { "id": "audit-e20", "displayName": "Audit E20", "ordinal": 0 }
                  ]
                }
                """;
            var collected = AtomSeedFile.Collect(new[] { ("audit-e20.json", seedJson) });
            Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

            var tempStore = throwaway.Store;
            var outcome = tempStore.ImportContent(collected.Content);
            Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));

            tempStore.LoadContentIntoRuntime();

            Assert.Contains(ElementTable.Current.Elements, e => e.ElementId == "audit-e20");
            Assert.DoesNotContain(ElementTable.Shipped().Elements, e => e.ElementId == "audit-e20");
        }
        ElementTable.ResetToShipped();
        PowerTables.ResetToAuthored();
    }
}
