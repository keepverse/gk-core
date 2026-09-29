using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Tools.RpgSim;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// A host plan: the exact value the server receives as <c>FUSIONRPG_DATA</c>, together with the store
/// options that address the SAME data. One value, so the two can never disagree.
/// </summary>
/// <param name="DataSource">A shared-memory URI, or a real directory path.</param>
/// <param name="StoreOptions">Options resolving to the same two databases <see cref="DataSource"/> names.</param>
internal readonly record struct E2EHostPlan(string DataSource, RpgStoreOptions StoreOptions);

/// <summary>
/// The shared `e2e` host: the REAL server (<see cref="WebApplicationFactory{TEntryPoint}"/> over
/// <c>Program</c>) on the <b>memory</b> storage plan.
///
/// <para><b>Why memory, and why it is the default here.</b> A routine suite run must open no file
/// (<c>docs/contributing/testing-standard.md</c> R1/R2 — the disk is used only when the disk is the
/// thing under test). This fixture used to point <c>FUSIONRPG_DATA</c> at
/// <c>%TEMP%/fusionrpg-e2e-{guid}</c> and hand the directory to the real bootstrap, which made every
/// boot of the server write <c>rpg-hot.sqlite</c>, <c>rpg-media.sqlite</c> and their WAL to the SSD,
/// under twelve concurrent lanes. Measured on this machine: <b>10 leaked directories, 5.15 GB</b>. The
/// writable plan is now the <i>exception</i> (<see cref="FileBackedRpgApiFactory"/>, whose consumers are
/// tagged <c>DiskSemantics</c>) and the default opens nothing.</para>
///
/// <para><b>The keeper is load-bearing.</b> A shared-memory database exists only while at least one
/// connection to it stays open, so this fixture keeps the seed store in a field for its whole lifetime —
/// the same pattern as <c>gk-core/tests/FusionRpg.Data.Tests/DataTestStore.cs</c>. Drop it and the server's own
/// store would find an empty, freshly-named database.</para>
/// </summary>
public class RpgApiFactory : WebApplicationFactory<Program>
{
    /// <summary>What the server receives as <c>FUSIONRPG_DATA</c> — a memory URI here, never a path.</summary>
    public string DataSource { get; }

    /// <summary>Options addressing the same two databases <see cref="DataSource"/> names.</summary>
    protected RpgStoreOptions PlanOptions { get; }

    /// <summary>
    /// `SimFlags.Enabled` (`FUSIONRPG_SIM=1`), read at REGISTRATION time by `Program.cs`. The shared
    /// collection fixture wants it ON; `SimDisabledApiFactory` overrides it to OFF so DM-F2's gate can
    /// be proven against the real bootstrap without giving this class a second seeding path.
    /// </summary>
    protected bool SimEnabled { get; }

    /// <summary>
    /// The seeded store, held open for this fixture's lifetime: it is the keeper that keeps the two
    /// shared-memory databases in existence while the host and every <see cref="OpenStore"/> handle use
    /// them. Disposed in <see cref="Dispose(bool)"/>.
    /// </summary>
    RpgStore? _rosterKeeper;

    /// <summary>
    /// The memory plan: one database name per fixture instance, so two hosts in one process (and the
    /// tests that read their data) can never collide. `-media` is the second-name convention
    /// <c>Program.cs</c> mirrors when it derives the names from the URI (see <c>MemoryDbName</c> there);
    /// a mismatch is loud, not silent — the roster read throws on an empty species catalog.
    /// </summary>
    static E2EHostPlan MemoryPlan()
    {
        var name = "e2e-" + Guid.NewGuid().ToString("N");
        return new E2EHostPlan(
            SqliteConnectionFactory.MemoryUri(name),
            new RpgStoreOptions { InMemory = true, HotName = name, MediaName = name + "-media" }.Resolve());
    }

    public RpgApiFactory() : this(MemoryPlan(), simEnabled: true) { }

    protected RpgApiFactory(bool simEnabled) : this(MemoryPlan(), simEnabled) { }

    /// <summary>
    /// RS3 increment 5a (owner ruling on RS-F16, candidate 1): an offset host — the scenario declared
    /// <c>clock.mode: offset</c> and this is the host that applies it. The offset reaches the host by the
    /// SAME route the real process uses (<c>FUSIONRPG_CLOCK_OFFSET</c>, read once by <c>Program.cs</c>'s
    /// composition root) and is also applied directly in the constructor, because
    /// <see cref="SeedSpeciesRoster"/> builds a store BEFORE <c>Program.cs</c> runs.
    ///
    /// <para><b>The caveat, stated rather than hidden:</b> the seam is process-global by design (one clock
    /// per process, spec §3), so an offset host and an ambient host cannot be alive at once. An
    /// offset-bearing test therefore builds its own factory inside the test, in the shared <c>e2e</c>
    /// collection so xunit serializes it against every other scenario test, and <see cref="Dispose(bool)"/>
    /// removes the variable and resets the seam — a stale offset left in the environment would be inherited
    /// by the NEXT boot. The one clock-sensitive comparison in the suite (the heartbeat freshness window)
    /// reads the RAW clock, not the seam, so a shifted seam cannot make a live injector look stale.</para>
    ///
    /// <para>The caller owns the lifetime and must dispose it. INTERNAL, not public: xunit allows a
    /// collection fixture type exactly one public constructor, and this is not the collection's.</para>
    /// </summary>
    internal RpgApiFactory(long clockOffsetSeconds) : this(MemoryPlan(), simEnabled: true, clockOffsetSeconds) { }

    private protected RpgApiFactory(E2EHostPlan plan, bool simEnabled, long clockOffsetSeconds = 0)
    {
        DataSource = plan.DataSource;
        PlanOptions = plan.StoreOptions;
        SimEnabled = simEnabled;
        ClockOffsetSeconds = clockOffsetSeconds;
        Environment.SetEnvironmentVariable("FUSIONRPG_SIM", SimEnabled ? "1" : null);
        Environment.SetEnvironmentVariable("FUSIONRPG_NO_BROWSER", "1");
        Environment.SetEnvironmentVariable("FUSIONRPG_DATA", DataSource);
        ApplyClockOffset(clockOffsetSeconds);
        SeedSpeciesRoster();
    }

    /// <summary>The declared clock offset in seconds (0 = the machine clock), for a test that wants to say
    /// what it proved.</summary>
    public long ClockOffsetSeconds { get; }

    /// <summary>
    /// Configure the seam the way the composition root does, from the same environment variable. Shared
    /// with the offset tests so there is ONE implementation of "how this host applies a declared clock";
    /// zero restores the machine clock and clears the variable.
    /// </summary>
    public static void ApplyClockOffset(long offsetSeconds)
    {
        if (offsetSeconds == 0)
        {
            Environment.SetEnvironmentVariable("FUSIONRPG_CLOCK_OFFSET", null);
            FusionRpg.Core.Time.ServerClock.Reset();
            return;
        }

        Environment.SetEnvironmentVariable(
            "FUSIONRPG_CLOCK_OFFSET", offsetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var timeProvider = TimeProvider.System;
        var offset = TimeSpan.FromSeconds(offsetSeconds);
        FusionRpg.Core.Time.ServerClock.Configure(() => timeProvider.GetUtcNow() + offset, offsetSeconds);
    }

    /// <summary>
    /// Run <paramref name="body"/> with the host's clock moved <paramref name="offsetSeconds"/> ahead, then
    /// give the process clock back. The SIM timer rewind is retired (RS3 increment 5b), so a test that wants
    /// an expedition due MOVES THE CLOCK — the same declaration a scenario's <c>clock.set</c> step makes. The
    /// offset is given back even when the body throws: a leaked offset would make every later test in this
    /// process read a clock nobody declared.
    /// </summary>
    public static async Task<T> WithClockAheadAsync<T>(long offsetSeconds, Func<Task<T>> body)
    {
        ApplyClockOffset(offsetSeconds);
        try { return await body(); }
        finally { ApplyClockOffset(0); }
    }

    /// <inheritdoc cref="WithClockAheadAsync{T}(long, Func{Task{T}})"/>
    public static async Task WithClockAheadAsync(long offsetSeconds, Func<Task> body)
    {
        ApplyClockOffset(offsetSeconds);
        try { await body(); }
        finally { ApplyClockOffset(0); }
    }

    /// <summary>
    /// The in-process <see cref="IClockControl"/> (RS3 increment 5b): the host applies a scenario's
    /// <c>clock.set</c> declaration to the seam in place, because there is no process to reboot. This is the
    /// counterpart of <c>ProcessHostClockControl</c>, which reboots the real server on the same data dir.
    /// </summary>
    public sealed class ClockControl : IClockControl
    {
        public Task SetOffsetSecondsAsync(long offsetSeconds, CancellationToken ct = default)
        {
            ApplyClockOffset(offsetSeconds);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A second, independent store handle onto the SAME data the host opened: the memory counterpart of
    /// the old `new RpgStore(factory.DataDir)` ("the same file Program.cs's own store opened"), for a
    /// test that needs its own handle rather than reaching into DI. The CALLER owns it and must dispose
    /// it — a memory store holds the keepers that keep those databases alive.
    /// </summary>
    public RpgStore OpenStore() => new(PlanOptions);

    /// <summary>
    /// creature-lawn-deploy T1.6 found this whole suite's own server could never start: `Program.cs:322`
    /// (`catalog-runtime`'s 2026-09-05 flip) now calls `CreatureSpeciesCatalog.Configure(store.
    /// BuildCreatureSpeciesSnapshot())`, which throws on an empty roster — and a fresh `DataDir` has NEVER
    /// had `species-import` run against it. Confirmed pre-existing and suite-wide, not specific to any
    /// one test file: `StorageE2ETests.cs` (untouched by this program) failed identically, 0/7, before
    /// this fix. Seeded here, once per collection fixture (a NEW `RpgStore` instance against the SAME
    /// store `Program.cs`'s own DI-registered store will open next), from the real
    /// committed corpus — the same source and reader `ConcreteSpeciesSeedReaderTests.cs`'s own
    /// `RealCommittedSpecies()` already uses, so E2E tests exercise a realistic roster, not a synthetic
    /// one-off fixture.
    /// </summary>
    void SeedSpeciesRoster()
    {
        var dir = Path.Combine(RepoRoot(), "data", "generated", "creatures");
        var files = Directory.EnumerateFiles(dir, "*.json")
            .Where(p => !Path.GetFileName(p).StartsWith('_'));
        var species = files.Select(ConcreteSpeciesSeedReader.ParseFile).ToList();
        if (species.Count == 0)
            throw new InvalidOperationException($"no real committed species found under {dir}");

        _rosterKeeper = new RpgStore(PlanOptions);
        _rosterKeeper.Init();
        var outcome = _rosterKeeper.ImportSpecies(species);
        if (!outcome.IsOk)
            throw new InvalidOperationException(
                "RpgApiFactory.SeedSpeciesRoster failed: " + string.Join("; ", outcome.Errors));
    }

    // Marker is gk-fusion/src/FusionRpg.Injector, NOT gk-data/packs/fusion/data/generated/creatures — the same choice
    // ConcreteSpeciesSeedReaderTests.cs's own RepoRoot() already made, and for the same reason found
    // here the hard way: an MSBuild content-copy rule creates an EMPTY gk-data/packs/fusion/data/generated/creatures under the
    // test's own bin output, so that marker alone stops the upward search one level too early.
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not find repo root (no src/FusionRpg.Injector above test bin)");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Re-stated at host-build time as well as in the constructor: the factory can build more than one
        // host, and the flags have to hold for each of them. `FUSIONRPG_CLOCK_OFFSET` rides along for the
        // same reason — Program.cs's composition root reads it when the host is built.
        Environment.SetEnvironmentVariable("FUSIONRPG_SIM", SimEnabled ? "1" : null);
        Environment.SetEnvironmentVariable("FUSIONRPG_NO_BROWSER", "1");
        Environment.SetEnvironmentVariable("FUSIONRPG_DATA", DataSource);
        ApplyClockOffset(ClockOffsetSeconds);
        builder.UseEnvironment("Development");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Releasing the keeper IS the memory plan's whole cleanup: there is no directory and no file
            // to delete, and so nothing that can leak — the databases vanish with their last connection.
            _rosterKeeper?.Dispose();
            _rosterKeeper = null;

            // An offset host owns the process clock while it lives, so it gives it back: the variable and
            // the seam both return to the machine clock, or the next boot in this process would inherit a
            // clock nobody declared.
            if (ClockOffsetSeconds != 0) ApplyClockOffset(0);
        }

        base.Dispose(disposing);
    }
}
