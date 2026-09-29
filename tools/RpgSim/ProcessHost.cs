using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// How to boot one real server process. Every value is the caller's: this type owns the transport, not
/// the world the process runs against.
/// </summary>
public sealed record ProcessHostOptions
{
    /// <summary>The real <c>FusionRpg.Server.exe</c> (or its apphost) to run.</summary>
    public required string ServerExePath { get; init; }

    /// <summary>
    /// The process's own data directory, passed as <c>FUSIONRPG_DATA</c>. Since **CS-F3** (owner ruling
    /// 2026-09-23) a FRESH directory boots: the species tree ships beside the exe and
    /// <c>Program.cs:683</c>'s <c>SpeciesImportRunner.RunSelfHealing</c> imports it on the first boot. So a
    /// caller that wants a *specific* roster still provisions it (<c>species-import</c> / a real install),
    /// and a caller that just wants a working server may hand in an empty directory — this type never opens
    /// the store itself either way (RS2.5 keeps <c>gk-core/tools/RpgSim</c> store-free).
    /// </summary>
    public required string DataDir { get; init; }

    /// <summary>Listen URL. Default: a free loopback port, picked at start.</summary>
    public string? Url { get; init; }

    /// <summary>Where the child's stdout/stderr are drained. Default: <c>&lt;DataDir&gt;/server.log</c>.</summary>
    public string? LogPath { get; init; }

    /// <summary>
    /// <c>FUSIONRPG_SIM</c>. <c>true</c> is the sim server a scenario runs against; <c>false</c> boots a
    /// player install, which the runner must refuse (owner ruling D1 (b)) — the refusal probe.
    /// </summary>
    public bool SimEnabled { get; init; } = true;

    /// <summary>
    /// The scenario's declared clock offset, in seconds, handed to the child as
    /// <c>FUSIONRPG_CLOCK_OFFSET</c> (rpg-simulator RS3 increment 5a, owner ruling on RS-F16). The
    /// server's composition root reads it once and configures the ONE clock seam
    /// (<c>FusionRpg.Core.Time.ServerClock</c>), so a declared <c>clock.mode: offset</c> is honest for the
    /// real process exactly as it is for the in-process host. Zero means the machine clock, and is written
    /// as an ABSENT variable rather than <c>0</c> — the child inherits this process's environment, so a
    /// stale offset from an earlier run has to be removed, not merely not-set (the same reason
    /// <see cref="SimEnabled"/> removes <c>FUSIONRPG_SIM</c>).
    /// </summary>
    public long ClockOffsetSeconds { get; init; }

    /// <summary>
    /// How long to wait for <c>GET /health</c> before the boot is a failure. **180 s, raised from 90 s on
    /// 2026-09-23** (lane `sim-t3-2`, RS-CF3): a real boot on a saturated machine exceeded 90 s while
    /// making progress — the process-host comparison's mid-run REBOOT (the corpus's `clock.set` step) hit
    /// `No connection could be made because the target machine actively refused it` after 1 m 30 s, in a
    /// run whose whole project took 14 m 8 s with other lanes' suites on the box. The budget is a bound on
    /// a boot, not a perf assertion: a boot that is genuinely stuck still fails, and it still reports the
    /// server's own log rather than a bare timeout.
    /// </summary>
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>Poll interval while waiting for readiness.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Delete <see cref="DataDir"/> when the host is disposed. A failed delete throws.</summary>
    public bool DeleteDataDirOnDispose { get; init; } = true;
}

/// <summary>
/// The real-process slow lane (rpg-simulator RS2.5, module <c>process-host</c>): a real
/// <c>FusionRpg.Server.exe</c> as its own process, on its own <c>FUSIONRPG_DATA</c> and an ephemeral
/// loopback port, with real HTTP and SignalR transport. The runner is unchanged — it takes an
/// <see cref="HttpClient"/> and never asks which host is behind it, which is what makes "one scenario
/// file, two hosts" (owner ruling E2 (a)) a transport difference rather than a second harness.
///
/// <para><b>Two things this type deliberately does NOT do.</b> It does not seed a world — provisioning a data
/// directory is the caller's step and the tool keeps no store reference — though since CS-F3 the server
/// SELF-HEALS an empty one on its first boot (<c>Program.cs:683</c>), so a caller may hand in a fresh
/// directory and get the shipped roster. And it does not retry a boot — a server that cannot come up is
/// reported with its own log, never restarted and hoped for.</para>
///
/// <para><b>Teardown is part of the contract.</b> <see cref="DisposeAsync"/> stops the process tree and
/// removes the data directory on every exit path; a process that will not stop, or a directory that will
/// not delete, throws. A swallowed cleanup failure is how a slow lane leaks a server per run.</para>
/// </summary>
public sealed class ProcessHost : IAsyncDisposable
{
    readonly Process _process;
    readonly StreamWriter _log;
    readonly bool _deleteDataDir;

    ProcessHost(Process process, StreamWriter log, string baseUrl, string dataDir, string logPath, bool deleteDataDir)
    {
        _process = process;
        _log = log;
        _deleteDataDir = deleteDataDir;
        BaseUrl = baseUrl;
        DataDir = dataDir;
        LogPath = logPath;
    }

    /// <summary>The loopback URL the server is listening on, e.g. <c>http://127.0.0.1:53111</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>The process's own data directory (what the child received as <c>FUSIONRPG_DATA</c>).</summary>
    public string DataDir { get; }

    /// <summary>Where the child's stdout/stderr were drained.</summary>
    public string LogPath { get; }

    /// <summary>The child's pid, for a report a reader can check.</summary>
    public int ProcessId => _process.Id;

    /// <summary>
    /// Boot the server and wait until it answers <c>GET /health</c>. A process that exits first, or a
    /// process that never becomes healthy, is a failure carrying the tail of its own log — the server's
    /// reason is the diagnostic, never a timeout alone.
    /// </summary>
    public static async Task<ProcessHost> StartAsync(ProcessHostOptions options, CancellationToken ct = default)
    {
        if (!File.Exists(options.ServerExePath))
            throw new InvalidOperationException($"server executable not found: {options.ServerExePath}");
        if (!Directory.Exists(options.DataDir))
            // A MISSING path is refused, an EMPTY one is fine: since CS-F3 the server self-heals the shipped
            // species tree on its first boot, so `mkdir` is the whole recipe for a fresh world. The existence
            // check stays because a typo'd path would otherwise silently start a new world somewhere else.
            throw new InvalidOperationException(
                $"data directory not found: {options.DataDir} — create it first (an EMPTY directory is fine: " +
                "the server self-heals the shipped species tree on its first boot, Program.cs:683); this tool " +
                "never creates or opens a store");

        var baseUrl = (options.Url ?? $"http://127.0.0.1:{FreeLoopbackPort()}").TrimEnd('/');
        // Absolute BEFORE the child starts, for both paths and the exe: a relative FileName or
        // FUSIONRPG_DATA resolves against the CHILD's working directory (the server's own output dir),
        // not this process's, so a caller's relative path would silently point at a different place —
        // measured (before CS-F3's self-heal landed, which is why the old failure looked like this): the
        // server created an empty nested directory and died on the empty species roster while the caller's
        // directory was deleted as if it had been used.
        var serverExe = Path.GetFullPath(options.ServerExePath);
        var dataDir = Path.GetFullPath(options.DataDir);
        var logPath = options.LogPath ?? Path.Combine(dataDir, "server.log");

        var psi = new ProcessStartInfo
        {
            FileName = serverExe,
            WorkingDirectory = Path.GetDirectoryName(serverExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // `Environment` starts as a copy of THIS process's environment, so a flag that must be OFF has to
        // be removed, not merely not-set: the in-process fixture sets FUSIONRPG_SIM=1 process-wide.
        psi.Environment["FUSIONRPG_URLS"] = baseUrl;
        psi.Environment["FUSIONRPG_DATA"] = dataDir;
        psi.Environment["FUSIONRPG_NO_BROWSER"] = "1";
        if (options.SimEnabled) psi.Environment["FUSIONRPG_SIM"] = "1";
        else psi.Environment.Remove("FUSIONRPG_SIM");
        // RS3 increment 5a: the declared clock offset travels to the child the same way every other boot
        // flag does. Removed, not zeroed, when there is none — see ClockOffsetSeconds' own comment.
        if (options.ClockOffsetSeconds != 0)
            psi.Environment["FUSIONRPG_CLOCK_OFFSET"] = options.ClockOffsetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        else
            psi.Environment.Remove("FUSIONRPG_CLOCK_OFFSET");

        var log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            AutoFlush = true
        };
        var process = new Process { StartInfo = psi };
        var logLock = new object();
        void Append(string? line)
        {
            if (line is null) return;
            lock (logLock) log.WriteLine(line);
        }

        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception e)
        {
            log.Dispose();
            throw new InvalidOperationException($"could not start {options.ServerExePath}: {e.Message}", e);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var host = new ProcessHost(process, log, baseUrl, dataDir, logPath,
            options.DeleteDataDirOnDispose);

        try
        {
            await WaitForHealthAsync(host, options, ct);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        return host;
    }

    static async Task WaitForHealthAsync(ProcessHost host, ProcessHostOptions options, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri(host.BaseUrl), Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + options.ReadyTimeout;
        string? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (host._process.HasExited)
                throw new InvalidOperationException(
                    $"the server exited with code {host._process.ExitCode} before answering GET /health " +
                    $"(sim={options.SimEnabled}, data={options.DataDir}). Log tail:{Environment.NewLine}{host.LogTail()}");

            try
            {
                using var res = await http.GetAsync("health", ct);
                if (res.IsSuccessStatusCode)
                {
                    // The declaration is read, not assumed: /health is where a caller learns whether this
                    // process is a sim server or a player install.
                    var root = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
                    host.SimEnabledReported =
                        root.TryGetProperty("simEnabled", out var sim) && sim.ValueKind == JsonValueKind.True;
                    return;
                }

                lastError = $"GET /health -> {(int)res.StatusCode}";
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                lastError = e.Message;
            }

            await Task.Delay(options.PollInterval, ct);
        }

        throw new InvalidOperationException(
            $"the server at {host.BaseUrl} did not answer GET /health within {options.ReadyTimeout} " +
            $"(last: {lastError}). Log tail:{Environment.NewLine}{host.LogTail()}");
    }

    /// <summary>What the process's own <c>/health</c> reported, once it answered.</summary>
    public bool SimEnabledReported { get; private set; }

    /// <summary>The last part of the child's log, for a failure message a reader can act on. Read with
    /// a sharing mode that permits the live writer: the failure path is exactly when the process still
    /// holds its own log open.</summary>
    public string LogTail(int maxChars = 4000)
    {
        try
        {
            if (!File.Exists(LogPath)) return "(no log)";
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return text.Length <= maxChars ? text : text[^maxChars..];
        }
        catch (IOException e)
        {
            return $"(log unreadable: {e.Message})";
        }
    }

    static int FreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Stop the process tree, then remove the data directory. Both are contract: a process that will not
    /// stop and a directory that will not delete are failures reported by name, never swallowed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already gone between the check and the kill — the wait below is the authority.
            }

            if (!_process.WaitForExit(15_000))
                throw new InvalidOperationException(
                    $"the server process {_process.Id} did not stop within 15s (url {BaseUrl})");

            _process.WaitForExit(); // flush the async output readers
        }

        _process.Dispose();
        await _log.DisposeAsync();

        if (!_deleteDataDir) return;
        if (!Directory.Exists(DataDir)) return;

        // SQLite's connection pool can hold the file handle for a moment past the last close, so a
        // bounded retry is legitimate; a delete that never succeeds still throws.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                Directory.Delete(DataDir, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(250);
            }
            catch (UnauthorizedAccessException) when (attempt < 5)
            {
                await Task.Delay(250);
            }
        }

        throw new InvalidOperationException($"could not remove the data directory {DataDir}");
    }
}
