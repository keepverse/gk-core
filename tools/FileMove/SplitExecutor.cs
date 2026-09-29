using System.Text.Json;
using System.Text.RegularExpressions;

namespace FusionRpg.Tools.FileMove;

/// <summary>The on-disk record of one applied increment: enough to undo it without re-planning.
/// Reuses <see cref="SplitOp"/> itself rather than a parallel type — a journal entry is exactly a
/// planned op with its `Before`/`After` bytes filled in for every kind that needs them (a
/// <see cref="SplitOpKind.MoveFile"/> op leaves both null in the plan, since a move never reads the
/// source into a string; the journal fills `After` with the source's bytes at apply time, because
/// undoing it later needs to verify the destination still holds them).</summary>
public sealed record SplitJournal(string ProjectName, IReadOnlyList<SplitOp> Entries);

/// <summary>Why <see cref="SplitExecutor.Revert"/> stopped without finishing.</summary>
public sealed record SplitRevertResult(bool Ok, string? BlockedPath, string? Reason)
{
    public static readonly SplitRevertResult Success = new(true, null, null);
}

/// <summary>
/// `core-split-apply` A4 items 2–5 — applies a <see cref="SplitPlan"/> to real files and can undo it.
///
/// <para><b>Journal before touch (item 2).</b> <see cref="Apply"/> reads every "before" byte it will
/// need and writes the journal to disk before executing a single op, so a process death mid-apply
/// still leaves a recoverable journal on disk.</para>
///
/// <para><b>Reverse-order undo (item 3).</b> <see cref="Revert"/> walks the journal back to front,
/// and before undoing each entry checks the path still holds exactly what <see cref="Apply"/> wrote.
/// The first mismatch stops the whole revert — it never clobbers a change it did not make.</para>
///
/// <para><b>Partial failure is the same path (item 4).</b> An exception thrown mid-<see cref="Apply"/>
/// reverts only the ops already applied, using the same per-entry undo <see cref="Revert"/> uses.</para>
///
/// <para>Every disk operation is an injected delegate, defaulting to the real <see cref="File"/>/
/// <see cref="Directory"/> calls — the same shape <see cref="FileMover"/> uses — so a test can run
/// against a real temp tree (disk is genuinely the subject here, `testing-standard.md` R2) while still
/// injecting a fault partway through <see cref="Apply"/> (F9).</para>
/// </summary>
public sealed class SplitExecutor
{
    readonly string _repoRoot;
    readonly Action<string, byte[]> _writeAllBytes;
    readonly Func<string, byte[]> _readAllBytes;
    readonly Action<string> _createDirectory;
    readonly Action<string, string> _moveFile;
    readonly Action<string> _deleteFile;
    readonly Action<string> _deleteDirectoryRecursive;
    readonly Func<string, bool> _fileExists;
    readonly Func<string, bool> _directoryExists;

    public SplitExecutor(
        string repoRoot,
        Action<string, byte[]>? writeAllBytes = null,
        Func<string, byte[]>? readAllBytes = null,
        Action<string>? createDirectory = null,
        Action<string, string>? moveFile = null,
        Action<string>? deleteFile = null,
        Action<string>? deleteDirectoryRecursive = null,
        Func<string, bool>? fileExists = null,
        Func<string, bool>? directoryExists = null)
    {
        _repoRoot = repoRoot;
        _writeAllBytes = writeAllBytes ?? File.WriteAllBytes;
        _readAllBytes = readAllBytes ?? File.ReadAllBytes;
        _createDirectory = createDirectory ?? (p => Directory.CreateDirectory(p));
        _moveFile = moveFile ?? File.Move;
        _deleteFile = deleteFile ?? File.Delete;
        _deleteDirectoryRecursive = deleteDirectoryRecursive ?? (p => Directory.Delete(p, recursive: true));
        _fileExists = fileExists ?? File.Exists;
        _directoryExists = directoryExists ?? Directory.Exists;
    }

    string Abs(string repoRelative) => Path.Combine(_repoRoot, repoRelative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Applies every op in <paramref name="plan"/>. Writes the journal first; on success, deletes it
    /// (a journal is kept only for an increment that was NOT kept — i.e. one that got reverted). On any
    /// exception, reverts the ops already applied and rethrows, leaving the journal on disk.
    /// </summary>
    /// <param name="onBeforeOp">Test-only fault injection hook, called with the op's index before it
    /// runs. The default no-op means production callers never see it.</param>
    /// <returns>The journal path while the increment is applied-but-not-yet-kept; the caller (the CLI's
    /// build+test gate) deletes it itself by calling <see cref="Keep"/>, or reverts by calling
    /// <see cref="Revert"/>.</returns>
    public string Apply(SplitPlan plan, Action<int, SplitOp>? onBeforeOp = null)
    {
        if (plan.IsRefused) throw new InvalidOperationException("refused plan: " + plan.Refusal);

        var journalDir = Path.Combine(Path.GetTempPath(), "filemove-split-" + Guid.NewGuid().ToString("N"));
        _createDirectory(journalDir);
        var journalPath = Path.Combine(journalDir, "journal.json");

        var entries = new List<SplitOp>(plan.Ops.Count);
        foreach (var op in plan.Ops)
        {
            switch (op.Kind)
            {
                case SplitOpKind.CreateDirectory:
                    entries.Add(op);
                    break;
                case SplitOpKind.CreateFile:
                    entries.Add(op); // Before is already null; After is already known.
                    break;
                case SplitOpKind.ModifyFile:
                    // Re-read fresh rather than trusting the plan's snapshot -- Plan and Apply can run
                    // at different times, and the journal must record what is ACTUALLY about to be lost.
                    entries.Add(op with { Before = _readAllBytes(Abs(op.Path)) });
                    break;
                case SplitOpKind.MoveFile:
                    // A move never reads the source into the plan (SplitOp's own doc comment); the
                    // journal is the one place that needs the bytes, to verify the destination later.
                    entries.Add(op with { After = _readAllBytes(Abs(op.From!)) });
                    break;
            }
        }

        var journal = new SplitJournal(plan.ProjectName, entries);
        _writeAllBytes(journalPath, JsonSerializer.SerializeToUtf8Bytes(journal));

        var applied = 0;
        try
        {
            for (; applied < entries.Count; applied++)
            {
                onBeforeOp?.Invoke(applied, entries[applied]);
                ExecuteOp(entries[applied]);
            }
        }
        catch
        {
            RevertEntries(entries, applied - 1); // undo everything actually applied, in reverse
            throw;
        }

        return journalPath;
    }

    /// <summary>Deletes the journal for an increment that is being kept, or for one whose revert
    /// COMPLETED — the tree then holds exactly what it held before `Apply`, so there is nothing left to
    /// recover and the directory is a temp leak (A4 item 2: "deleted only after a kept increment";
    /// TVB-F4 widened that to "kept or cleanly reverted", because the auto-revert after a failed
    /// build/test is the common path and `%TEMP%` held 47 `filemove-split-*` directories). A revert
    /// BLOCKED by a path mismatch keeps it: that is the exit-3 recovery path.</summary>
    public void Keep(string journalPath)
    {
        var dir = Path.GetDirectoryName(journalPath)!;
        if (_fileExists(journalPath)) _deleteFile(journalPath);
        if (_directoryExists(dir)) _deleteDirectoryRecursive(dir);
    }

    void ExecuteOp(SplitOp op)
    {
        var path = Abs(op.Path);
        switch (op.Kind)
        {
            case SplitOpKind.CreateDirectory:
                _createDirectory(path);
                break;
            case SplitOpKind.CreateFile:
            case SplitOpKind.ModifyFile:
                _createDirectory(Path.GetDirectoryName(path)!);
                _writeAllBytes(path, op.After!);
                break;
            case SplitOpKind.MoveFile:
                _createDirectory(Path.GetDirectoryName(path)!);
                _moveFile(Abs(op.From!), path);
                break;
        }
    }

    /// <summary>Reads a journal back from disk.</summary>
    public SplitJournal ReadJournal(string journalPath) =>
        JsonSerializer.Deserialize<SplitJournal>(_readAllBytes(journalPath))
        ?? throw new InvalidOperationException("empty or unreadable journal: " + journalPath);

    /// <summary>
    /// Undoes every entry in <paramref name="journalPath"/>, back to front. Before undoing each entry,
    /// checks the path still holds exactly what <see cref="Apply"/> wrote — the first mismatch stops
    /// the whole revert (F10): that path is left exactly as found, and every entry not yet reached stays
    /// applied too. On SUCCESS the journal is deleted (TVB-F4 — the tree is back to its pre-apply state
    /// and the directory is a temp leak); on a BLOCKED revert it stays on disk, which is the exit-3
    /// recovery path.
    /// </summary>
    public SplitRevertResult Revert(string journalPath)
    {
        var journal = ReadJournal(journalPath);
        var result = RevertEntries(journal.Entries, journal.Entries.Count - 1);
        if (result.Ok) Keep(journalPath);
        return result;
    }

    /// <summary>Undoes entries [0..fromIndexInclusive] in reverse order. Used both by a standalone
    /// <see cref="Revert"/> (fromIndexInclusive = last entry) and by <see cref="Apply"/>'s own
    /// mid-failure cleanup (fromIndexInclusive = the last op that actually ran).</summary>
    SplitRevertResult RevertEntries(IReadOnlyList<SplitOp> entries, int fromIndexInclusive)
    {
        for (var i = fromIndexInclusive; i >= 0; i--)
        {
            var entry = entries[i];
            var path = Abs(entry.Path);

            switch (entry.Kind)
            {
                case SplitOpKind.CreateDirectory:
                    if (_directoryExists(path))
                        _deleteDirectoryRecursive(path);
                    break;

                case SplitOpKind.CreateFile:
                {
                    if (!MatchesApplied(path, entry.After))
                        return Blocked(entry.Path, "was modified since it was created");
                    if (_fileExists(path)) _deleteFile(path);
                    break;
                }

                case SplitOpKind.ModifyFile:
                {
                    if (!MatchesApplied(path, entry.After))
                        return Blocked(entry.Path, "was modified since Apply wrote it");
                    _writeAllBytes(path, entry.Before!);
                    break;
                }

                case SplitOpKind.MoveFile:
                {
                    if (!MatchesApplied(path, entry.After))
                        return Blocked(entry.Path, "was modified since it was moved");
                    var fromPath = Abs(entry.From!);
                    _createDirectory(Path.GetDirectoryName(fromPath)!);
                    _moveFile(path, fromPath);
                    break;
                }
            }
        }

        return SplitRevertResult.Success;
    }

    bool MatchesApplied(string absolutePath, byte[]? expectedAfter)
    {
        if (!_fileExists(absolutePath)) return false;
        var current = _readAllBytes(absolutePath);
        return expectedAfter is not null && current.AsSpan().SequenceEqual(expectedAfter);
    }

    static SplitRevertResult Blocked(string path, string reason) => new(false, path, reason);

    /// <summary>
    /// How many tests a `dotnet test` console run reported, or <c>null</c> when its output carries no
    /// summary line at all.
    ///
    /// <para><b>Why the apply gate needs this and cannot trust the exit code.</b> `dotnet test` exits
    /// <b>0</b> when it discovers nothing — it prints "No test is available in … Make sure that test
    /// discoverer &amp; executors are registered" and reports success. The first real increment hit
    /// exactly that: the shared props had been built from self-closing `&lt;PackageReference&gt;`
    /// elements only, so `xunit.runner.visualstudio` stayed in the residual, the new project had no
    /// test adapter, and its "test" step passed while running zero tests. `core-split-apply` A4 keeps
    /// an increment only when its build+test gate proves the moved tests ran; an exit code of 0 over
    /// zero tests proves the opposite.</para>
    /// </summary>
    public static int? TestsReported(string dotnetTestOutput)
    {
        var match = Regex.Match(dotnetTestOutput, @"Total:\s*(?<total>\d+)");
        return match.Success && int.TryParse(match.Groups["total"].Value, out var total) ? total : null;
    }
}
