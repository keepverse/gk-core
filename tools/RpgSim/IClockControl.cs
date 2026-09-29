namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// How the runner asks the HOST to believe a different clock (rpg-simulator RS3 increment 5b, owner ruling
/// on RS-F16 candidate 2).
///
/// <para><b>Why an interface and not a route.</b> The clock is product surface and belongs to whoever boots
/// the host (<c>docs/architecture/rpg-simulator-spec-clock-seam.md</c> §2, owner ruling D3 (b)): a route that
/// sets the clock is deliberately not specified, because it would be a player-reachable way to make every
/// deadline in the game lie. So the runner never reaches the clock over HTTP — it hands the value to the
/// control the embedding host supplied, and refuses the run by name when there is none.</para>
///
/// <para><b>Two implementations exist, one per approved host.</b> In-process, the E2E host applies the offset
/// to the seam in place (<c>RpgApiFactory.ClockControl</c>). Real-process, the host cannot be told anything
/// at run time, so <see cref="ProcessHostClockControl"/> stops it and reboots it on the SAME data directory,
/// the same port and the new offset — the process's own composition root then reads
/// <c>FUSIONRPG_CLOCK_OFFSET</c> exactly as it did at the first boot.</para>
/// </summary>
public interface IClockControl
{
    /// <summary>
    /// Make the host believe the machine clock is <paramref name="offsetSeconds"/> ahead. Absolute, from the
    /// machine clock — never a delta, so the runner performs no arithmetic over a scenario's declarations.
    /// </summary>
    Task SetOffsetSecondsAsync(long offsetSeconds, CancellationToken ct = default);
}

/// <summary>
/// The real-process implementation: a reboot on the same data directory, the same port and a new offset.
///
/// <para><b>This type owns the host's whole lifecycle</b> — <see cref="StartAsync"/> starts the first process
/// itself, and <see cref="DisposeAsync"/> stops the last one and removes the directory. That is not
/// incidental: every host it starts is started with <c>DeleteDataDirOnDispose: false</c>, because the
/// directory is the world the run is midway through and a reboot must find it. A caller that started its own
/// host and handed it here would have that host's intermediate stop delete the world — so the constructor is
/// private and the only way in is <see cref="StartAsync"/>.</para>
///
/// <para><b>Same port, so the caller's <see cref="HttpClient"/> stays valid.</b> A reboot that came back on a
/// fresh ephemeral port would force every caller to rebuild its client mid-run; the control therefore passes
/// the previous host's own URL to the next boot. A port taken in the gap is a boot failure, reported, never
/// silently retried onto a port the caller's client is not pointing at.</para>
///
/// <para><b>A failed reboot is reported, and leaves nothing half-alive.</b> If the replacement process will
/// not start, the control holds no host (rather than a disposed one), <see cref="Host"/> says so by name, and
/// <see cref="DisposeAsync"/> still removes the directory — a boot failure must not leak the world it was
/// booting on.</para>
/// </summary>
public sealed class ProcessHostClockControl : IClockControl, IAsyncDisposable
{
    readonly ProcessHostOptions _options;
    ProcessHost? _host;

    ProcessHostClockControl(ProcessHostOptions options, ProcessHost host)
    {
        _options = options;
        _host = host;
        OffsetSeconds = options.ClockOffsetSeconds;
    }

    /// <summary>Boot the first process and take ownership of its lifecycle.</summary>
    public static async Task<ProcessHostClockControl> StartAsync(
        ProcessHostOptions options, CancellationToken ct = default)
    {
        var host = await ProcessHost.StartAsync(options with { DeleteDataDirOnDispose = false }, ct);
        return new ProcessHostClockControl(options, host);
    }

    /// <summary>The live host. After a reboot this is a NEW process on the same URL and data directory.</summary>
    public ProcessHost Host => _host
        ?? throw new InvalidOperationException(
            "the clock control has no live host: the last reboot failed to start (see the boot error)");

    /// <summary>The offset the live host was booted with. Seeded from the options, so the first host's own
    /// declared offset is not mistaken for "no movement yet".</summary>
    public long OffsetSeconds { get; private set; }

    public async Task SetOffsetSecondsAsync(long offsetSeconds, CancellationToken ct = default)
    {
        if (offsetSeconds == OffsetSeconds) return;

        var current = Host;
        var url = current.BaseUrl;

        // Stop WITHOUT deleting: DeleteDataDirOnDispose is false on every host this type started, so the
        // world survives. Clear the field first, so a failed reboot cannot leave a disposed host behind for
        // Host/DisposeAsync to trip over.
        await current.DisposeAsync();
        _host = null;

        OffsetSeconds = offsetSeconds;
        _host = await ProcessHost.StartAsync(_options with
        {
            Url = url,
            ClockOffsetSeconds = offsetSeconds,
            DeleteDataDirOnDispose = false
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
        _host = null;

        if (!_options.DeleteDataDirOnDispose) return;
        if (!Directory.Exists(_options.DataDir)) return;
        Directory.Delete(_options.DataDir, recursive: true);
    }
}
