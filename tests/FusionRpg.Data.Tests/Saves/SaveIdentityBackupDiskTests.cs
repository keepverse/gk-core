using System;
using System.IO;
using System.Linq;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Sqlite.Migrations;
using Xunit;

namespace FusionRpg.Data.Tests.Saves;

/// <summary>
/// `save-identity` SE4.15 — the half whose SUBJECT is the disk: the never-reused `VACUUM INTO` backup.
/// `DiskSemantics`-tagged and leak-proof through <see cref="DataTestStore.CreateFileBacked"/>, which
/// deletes its directory and throws on failure.
/// </summary>
[Trait("Category", "DiskSemantics")]
[Trait("VerificationId", "data.save-identity")]
public class SaveIdentityBackupDiskTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public SaveIdentityBackupDiskTests()
    {
        _testStore = DataTestStore.CreateFileBacked();
        _store = _testStore.Store;
        // Init's own migration already wrote a .bak; this test's subject is the NEXT attempt, so the
        // baseline starts clean.
        foreach (var file in Directory.GetFiles(_testStore.DataDir!, "*" + SaveIdentity.BackupInfix + "*.bak"))
            File.Delete(file);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    bool Migrate() => SaveIdentityFixtures.MigrateAgain(_testStore);

    bool HasMarker()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        return SaveIdentity.HasMarker(db);
    }

    string[] Backups() =>
        Directory.Exists(_testStore.DataDir!)
            ? Directory.GetFiles(_testStore.DataDir!, "*" + SaveIdentity.BackupInfix + "*.bak")
            : Array.Empty<string>();

    [Fact]
    public void The_backup_exists_before_the_first_write_and_a_second_attempt_never_reuses_it()
    {
        // A failure AFTER the backup and BEFORE the marker: the file must already be there, the marker
        // must not, and a later attempt must write a NEW backup and leave the first byte-identical.
        Assert.Throws<InvalidOperationException>(() => SaveIdentityFixtures.MigrateAgain(_testStore,
            failureInjector: _ => throw new InvalidOperationException("injected (test)")));
        Assert.False(HasMarker());

        var first = Assert.Single(Backups());
        var firstBytes = File.ReadAllBytes(first);

        Assert.True(Migrate());
        Assert.True(HasMarker());

        var after = Backups();
        Assert.Equal(2, after.Length);
        Assert.Contains(first, after);
        Assert.Equal(firstBytes, File.ReadAllBytes(first));   // never reused, never overwritten
    }

    [Fact]
    public void A_failed_backup_writes_nothing_and_propagates()
    {
        // Step 1 fails: no marker, no backup, and the exception escapes `Migrate` — the thing that makes
        // `Init` refuse to start (SE4.20 asserts the boot refusal itself).
        var threw = Assert.Throws<InvalidOperationException>(() => SaveIdentityFixtures.MigrateAgain(_testStore,
            backupFailure: _ => throw new InvalidOperationException("backup failed (test)")));
        Assert.Contains("backup", threw.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(HasMarker());
        Assert.Empty(Backups());
    }
}
