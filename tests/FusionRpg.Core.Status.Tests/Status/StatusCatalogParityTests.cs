using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Status;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// status-rail Wave 0 / B1 — JSON surface catalog id set equals Bootstrap golden (24), and
/// injected <see cref="StatusCatalogFactory"/> carries real payloadKinds (bond empty by decision).
/// </summary>
public class StatusCatalogParityTests
{
    [Fact]
    public void Json_entry_ids_equal_Bootstrap_ids()
    {
        var surface = StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json"));
        var bootstrap = StatusCatalogBootstrap.CreateDefault().All()
            .Select(d => d.StatusId)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var json = surface.Entries.Select(e => e.Id)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // The count (population-pin SE3.3, 2026-09-19) is fully implied by the element-wise equality
        // below -- redundant, not a second fact, so it is never pinned separately.
        Assert.Equal(bootstrap, json);
    }

    [Fact]
    public void Injected_catalog_matches_Bootstrap_kinds_and_payloads()
    {
        var surface = StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json"));
        var injected = StatusCatalogFactory.FromSurface(surface);
        var bootstrap = StatusCatalogBootstrap.CreateDefault();

        foreach (var id in bootstrap.All().Select(d => d.StatusId))
        {
            var a = injected.GetRequired(id);
            var b = bootstrap.GetRequired(id);
            Assert.Equal(b.Kind, a.Kind);
            Assert.Equal(b.Stacking, a.Stacking);
            Assert.Equal(b.Family, a.Family);
            Assert.Equal(b.PulseHealsAttacker, a.PulseHealsAttacker);
            Assert.Equal(
                b.Categories.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                a.Categories.OrderBy(c => c, StringComparer.Ordinal).ToArray());
            Assert.Equal(
                b.PayloadKinds.OrderBy(k => k.ToString(), StringComparer.Ordinal).ToArray(),
                a.PayloadKinds.OrderBy(k => k.ToString(), StringComparer.Ordinal).ToArray());
            Assert.True(StatusCategoryRegistry.TryGetCategory(id, out var l2b));
            Assert.Equal(l2b, a.Categories[0]);
        }
    }

    [Fact]
    public void ModifyStat_UnityCc_OverTime_rows_have_nonempty_payloadKinds_except_bond()
    {
        var surface = StatusSurfaceCatalogLoader.Parse(ReadTuning("status-catalog.v1.json"));
        foreach (var e in surface.Entries)
        {
            if (e.Id == "bond")
            {
                Assert.Empty(e.PayloadKinds);
                continue;
            }

            if (e.Kind is StatusKind.UnityCc or StatusKind.OverTime or StatusKind.Contagion
                || e.PayloadKinds.Contains(StatusPayloadKind.ModifyStat)
                || e.Kind is StatusKind.Buff or StatusKind.Debuff or StatusKind.Meter
                    or StatusKind.CrowdControl)
            {
                Assert.NotEmpty(e.PayloadKinds);
            }
        }
    }

    static string ReadTuning(string fileName)
    {
        var root = FindRepoRoot();
        return File.ReadAllText(Path.Combine(root, "data", "tuning", fileName));
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
