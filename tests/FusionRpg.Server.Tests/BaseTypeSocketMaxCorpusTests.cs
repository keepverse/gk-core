using FusionRpg.Core.Items.Sockets;
using FusionRpg.Server;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// File-bound by subject: the thing under test is the corpus LOADER's nested-partition walk, so the
/// fixture is a real directory tree on disk, not a store. It uses no SQLite, so the substrate gate's
/// `temp-store` rule does not apply; the one thing it was doing wrong was swallowing a failed delete
/// (testing-standard R3) — a failed cleanup is a failure, never `catch { }`.
/// </summary>
[Trait("Category", "DiskSemantics")]
[Trait("VerificationId", "server.item-workbench")]
public sealed class BaseTypeSocketMaxCorpusTests
{
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    static SocketTuning Shipped() =>
        SocketTuning.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current)));

    [Fact]
    public void Load_readsNestedBaseTypePartitions()
    {
        var root = Path.Combine(Path.GetTempPath(), "fusionrpg-base-sockets-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "footing", "plant");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(nested, "a.json"), """
                { "kind": "base-type", "entries": [
                  { "id": "item.plant-runner-a-001", "role": "footing", "socketMax": 2 }
                ] }
                """);

            var lookup = BaseTypeSocketMaxCorpus.Load(root, Shipped());

            Assert.Equal(2, lookup("item.plant-runner-a-001"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// strain-splice-host SSH1.6 (host-gate §5) — the runtime lookup no longer trusts the file past
    /// the role's own ceiling. `footing`'s ceiling is read from the LOADED revision and the fixture
    /// claims one above it, so the test keeps asserting the contract when the revision moves: v1's
    /// `footing` reached 2 (the old pinned 3 was stale from the v2 flip until 2026-09-21). Refused BY
    /// DROPPING the row, not by a new rule id: the lookup returns exactly what it already returns for
    /// an id the corpus never carried at all (`null`), and the workbench's existing
    /// `socket.base-type-socket-max-unavailable` refusal covers it.
    /// </summary>
    [Fact]
    public void A_base_row_above_its_role_ceiling_is_refused_at_load()
    {
        var root = Path.Combine(Path.GetTempPath(), "fusionrpg-base-sockets-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "footing", "plant");
        Directory.CreateDirectory(nested);
        try
        {
            var tuning = Shipped();
            var corrupt = tuning.CeilingFor(FusionRpg.Core.Items.ItemRole.Footing) + 1;
            File.WriteAllText(Path.Combine(nested, "a.json"), $$"""
                { "kind": "base-type", "entries": [
                  { "id": "item.corrupt-footing-001", "role": "footing", "socketMax": {{corrupt}} }
                ] }
                """);

            var lookup = BaseTypeSocketMaxCorpus.Load(root, tuning);

            Assert.Null(lookup("item.corrupt-footing-001"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void A_row_with_no_parseable_role_is_also_refused_at_load()
    {
        var root = Path.Combine(Path.GetTempPath(), "fusionrpg-base-sockets-" + Guid.NewGuid().ToString("N"));
        var nested = Path.Combine(root, "footing", "plant");
        Directory.CreateDirectory(nested);
        try
        {
            File.WriteAllText(Path.Combine(nested, "a.json"), """
                { "kind": "base-type", "entries": [
                  { "id": "item.no-role-001", "role": "not-a-real-role", "socketMax": 1 }
                ] }
                """);

            var lookup = BaseTypeSocketMaxCorpus.Load(root, Shipped());

            Assert.Null(lookup("item.no-role-001"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
