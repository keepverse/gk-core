using FusionRpg.Data.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// The <b>file-plan</b> host, for the E2E cases where the disk IS the thing under test
/// (<c>docs/contributing/testing-standard.md</c> R2): <c>StorageE2ETests</c> drives the archive
/// catalog/purge endpoints, which are file-only by construction (<c>RpgStore.RequireFileArchive</c>),
/// and <c>TypeIconE2ETests</c> asserts the file plan's own <c>rpg-hot.sqlite</c> / <c>rpg-media.sqlite</c>
/// exist and that no <c>data/icons</c> mirror is written beside them.
///
/// <para>Everything else in this project uses the memory-plan <see cref="RpgApiFactory"/> — which opens
/// no file at all — because a write nobody needs is the damage this pair exists to end (measured: the
/// old always-file fixture left 10 directories / 5.15 GB behind). A consumer of THIS host must therefore
/// carry <c>[Trait("Category", "DiskSemantics")]</c> so the default profile skips it; both consumers do.
/// The trait on the factory class itself is the same declaration for the guard's file-level contract (a
/// `tests/**` file that builds a file-backed store must carry the tag); xUnit only reads it from test
/// classes, so here it is documentation, not filtering.</para>
///
/// <para><b>The directory is deleted in <see cref="Dispose(bool)"/>, and a failed delete throws</b>
/// (R3) — a file-bound suite owns its own files, and leaving them behind is the leak the memory plan
/// removed elsewhere.</para>
/// </summary>
[Trait("Category", "DiskSemantics")]
public sealed class FileBackedRpgApiFactory : RpgApiFactory
{
    /// <summary>The real directory the host's store lives in — the file plan's own subject.</summary>
    public string DataDir { get; }

    public FileBackedRpgApiFactory() : this(FilePlan()) { }

    FileBackedRpgApiFactory(E2EHostPlan plan) : base(plan, simEnabled: true) => DataDir = plan.DataSource;

    /// <summary>
    /// A unique real directory for the file plan, under the test output root rather than
    /// <c>Path.GetTempPath()</c>: the same choice <c>DataTestStore.CreateFileBacked()</c> already makes,
    /// so the file-bound cases add no litter to <c>%TEMP%</c>, which is where the 10 leaked
    /// <c>fusionrpg-e2e-*</c> directories (5.15 GB) came from.
    /// </summary>
    static E2EHostPlan FilePlan()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "e2e-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new E2EHostPlan(dir, RpgStoreOptions.For(dir).Resolve());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing); // releases the roster keeper (and closes the file store)

        if (!disposing) return;

        // Pooling holds the file handle past the connection close, so clear pools before deleting. No
        // catch: a cleanup failure must fail the test, never be swallowed (testing-standard R3).
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDir))
            Directory.Delete(DataDir, recursive: true);
    }
}

/// <summary>
/// The file-bound half of the E2E suite. Separate from <c>e2e</c> because it is a different host (a real
/// directory) and because both collections disable parallelization: the server reads
/// <c>FUSIONRPG_DATA</c> from the process environment, so two hosts must never be booting at once.
/// </summary>
[CollectionDefinition("e2e-file", DisableParallelization = true)]
public class FileBackedE2ECollection : ICollectionFixture<FileBackedRpgApiFactory> { }
