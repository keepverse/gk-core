using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Seed;

/// <summary>
/// What one call to <see cref="SeedImportRunner"/> did.
///
/// <para><c>ContentSource</c> is the field <c>RpgStore.ToHealth</c> (E46) reports — <c>Imported</c>
/// and <c>AlreadyCurrent</c> both mean the catalog tables genuinely hold content, so both read as
/// <c>"imported"</c>; <c>SeedTreeNotFound</c> and <c>Failed</c> both mean the caller is still on the
/// shipped code fallback, so both read as <c>"codeFallback"</c> (spec-player-content-boot.md §3.2:
/// the fallback must stop being invisible, not stop existing).</para>
/// </summary>
public enum SeedImportStatus
{
    /// <summary>This call imported the tree — <c>catalog_revision</c> moved off zero.</summary>
    Imported,

    /// <summary><c>catalog_revision</c> was already non-zero; nothing was read or written (test 4 —
    /// no re-import, no revision bump on relaunch).</summary>
    AlreadyCurrent,

    /// <summary><c>catalog_revision</c> was zero, but no <c>gk-data/packs/fusion/data/seed</c> tree with owned content was
    /// reachable from the search start directory. Not fatal — the fallback stays.</summary>
    SeedTreeNotFound,

    /// <summary><c>catalog_revision</c> was zero, a seed tree was found, but reading, validating, or
    /// importing it failed. Not fatal — the fallback stays. <c>Detail</c> says why.</summary>
    Failed,
}

/// <param name="Detail">Human-readable reason, present on every status except <see cref="SeedImportStatus.Imported"/>
/// and <see cref="SeedImportStatus.AlreadyCurrent"/> (both need no explanation).</param>
/// <param name="Outcome">The transaction's own report, when an import transaction actually ran.</param>
public sealed record SeedImportRunResult(SeedImportStatus Status, string? Detail, ImportOutcome? Outcome)
{
    /// <summary>True when the catalog tables hold real content either because this call just wrote
    /// them or because an earlier launch already did.</summary>
    public bool Ok => Status is SeedImportStatus.Imported or SeedImportStatus.AlreadyCurrent;

    /// <summary>The value <c>HealthDto.ContentSource</c> carries — see the class remark.</summary>
    public string ContentSource => Ok ? "imported" : "codeFallback";
}

/// <summary>
/// The reusable half of a seed import — locate the tree, sweep it, read it, collect it, and write it
/// through <see cref="RpgStore.ImportContent"/>. Extracted from <c>gk-forge/tools/AtomImporter/Program.cs</c>
/// (E46, player-content-boot) so there is exactly one implementation of "how a seed tree becomes
/// catalog rows": the CLI (a developer script, <c>gk-fusion/scripts/deploy-play.py:483</c>) and the server's
/// own self-healing startup import both call the members here.
///
/// <para><b>The CLI keeps its own reporting.</b> <c>Program.cs</c> still owns argument parsing,
/// <c>--check</c>/<c>--validate</c>, and its line-by-line console report — those are CLI concerns, not
/// import concerns — so it calls the finer-grained members (<see cref="Roots"/>, <see cref="Files"/>,
/// <see cref="Collect"/>) rather than <see cref="RunSelfHealing"/>, which is shaped for a caller that
/// wants one verdict and never wants an exception.</para>
///
/// <para><b><see cref="RunSelfHealing"/> never throws.</b> A player's server must still boot on a
/// broken or absent seed tree (spec-player-content-boot.md §3.2, §4) — every failure path inside it is
/// caught and folded into <see cref="SeedImportRunResult.Failed"/> rather than left to propagate,
/// which is what lets <c>FusionRpg.Server/Program.cs</c> call it with no <c>try</c>/<c>catch</c> of its
/// own and still be certain startup cannot die here.</para>
/// </summary>
public static class SeedImportRunner
{
    /// <summary>Find the directory ending in <paramref name="segments"/>, trying the detected
    /// workspace roots first and then walking up from <paramref name="startDir"/>.
    ///
    /// <para><b>Why the roots come first.</b> The walk alone was a complete answer only while all
    /// three roots were the legacy repository root. After the Keepverse split <c>gk-data/packs/fusion/data/seed</c> and
    /// <c>gk-data/packs/fusion/data/generated</c> live in a <c>gk-data</c> pack while <c>gk-core/data/tuning</c> lives in
    /// <c>gk-core</c>, so a program running from <c>gk-core/tests/.../bin</c> reaches
    /// <c>gk-core/data/tuning</c> by luck and <c>gk-data/packs/fusion/data/seed</c> not at all. That is why the split built cleanly
    /// and then failed every content-reading test at runtime with a <c>FileNotFoundException</c> on a
    /// path that demonstrably exists. <see cref="KeepverseRoots.Roots"/> is that missing half: it
    /// returns both candidate roots and lets the filesystem decide, so this method never has to know
    /// whether a given <c>data/...</c> path is content or core.
    ///
    /// <para><b>The walk is kept, not replaced.</b> It still answers for anything outside the two roots
    /// (<c>dist/FusionRpg.Server/data</c>, a caller-supplied directory, a checkout laid out some other
    /// way), and in a legacy checkout the roots collapse to the repository root, so this returns
    /// exactly what the old walk returned. A path matching nothing still returns <see langword="null"/>,
    /// and this never throws: callers use it to ask "is it there", and a probe must not be the thing
    /// that fails.
    /// </para></summary>
    public static string? FindUp(string startDir, params string[] segments)
    {
        foreach (var root in KeepverseRoots.Roots(startDir))
        {
            var candidate = Path.Combine(new[] { root }.Concat(segments).ToArray());
            if (Directory.Exists(candidate)) return candidate;
        }

        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(segments).ToArray());
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static IReadOnlyList<string> Roots(string seedRoot, bool explicitRoot) =>
        SeedScanner.AtomRoots(seedRoot, explicitRoot, Directory.Exists);

    public static IReadOnlyList<string> Files(IReadOnlyList<string> roots) =>
        SeedScanner.Files(roots);

    /// <summary>
    /// Read every file into one <see cref="SeedContent"/>. A file that cannot be read throws
    /// <see cref="IOException"/> with the path (relative to <paramref name="seedRoot"/>, matching what
    /// the caller sees on disk) already folded into the message — the same granularity the CLI always
    /// reported per file, preserved here so moving this code changed nothing about what an author sees.
    /// </summary>
    public static SeedCollectResult Collect(string seedRoot, IReadOnlyList<string> files)
    {
        var read = new List<(string Path, string Json)>(files.Count);
        foreach (var file in files)
        {
            var rel = Relative(seedRoot, file);
            try
            {
                read.Add((rel, File.ReadAllText(file)));
            }
            catch (IOException ex)
            {
                throw new IOException($"{rel}: {ex.Message}", ex);
            }
        }
        return AtomSeedFile.Collect(read);
    }

    /// <summary>A file's path relative to the seed root's own parent — the shape every SeedError and
    /// every CLI message already reports content under.</summary>
    public static string Relative(string seedRoot, string filePath)
    {
        var parent = Directory.GetParent(seedRoot)?.Parent?.FullName ?? seedRoot;
        return Path.GetRelativePath(parent, filePath).Replace('\\', '/');
    }

    /// <summary>
    /// The player-content-boot (E46) entry point. Gated on <c>catalog_revision</c> so a normal
    /// relaunch is a true no-op (spec §4: importing on every launch would bump the revision and make
    /// every rolled instance unbindable), and never throws — every failure becomes
    /// <see cref="SeedImportStatus.Failed"/> or <see cref="SeedImportStatus.SeedTreeNotFound"/> instead
    /// of an exception, because a broken seed tree must never take the server down with it (§3.2, §4).
    /// </summary>
    /// <param name="store">Already constructed and <c>Init()</c>-ed — this never opens its own store.</param>
    /// <param name="searchStartDir">Where to start walking up for <c>gk-data/packs/fusion/data/seed</c> — the server passes
    /// its own <c>AppContext.BaseDirectory</c>, the same root <c>gk-core/data/tuning</c> already resolves
    /// against, so the import reads the seed tree relative to wherever this process actually runs.</param>
    public static SeedImportRunResult RunSelfHealing(RpgStore store, string searchStartDir)
    {
        if (store.GetCatalogRevision() != 0)
            return new SeedImportRunResult(SeedImportStatus.AlreadyCurrent, null, null);

        try
        {
            var seedRoot = FindUp(searchStartDir, "data", "seed");
            if (seedRoot is null)
                return new SeedImportRunResult(SeedImportStatus.SeedTreeNotFound,
                    $"no data/seed found walking up from {searchStartDir}", null);

            var roots = Roots(seedRoot, explicitRoot: false);
            var files = Files(roots);
            if (files.Count == 0)
                return new SeedImportRunResult(SeedImportStatus.SeedTreeNotFound,
                    $"data/seed exists at {seedRoot} but its owned folders hold no *.json", null);

            var collected = Collect(seedRoot, files);
            if (!collected.IsOk)
                return new SeedImportRunResult(SeedImportStatus.Failed, Describe(collected.Errors), null);

            var outcome = store.ImportContent(collected.Content);
            if (!outcome.IsOk)
                return new SeedImportRunResult(SeedImportStatus.Failed, Describe(outcome.Errors), outcome);

            return new SeedImportRunResult(SeedImportStatus.Imported, null, outcome);
        }
        catch (Exception ex)
        {
            // ImportContent is one transaction (RpgStore.Import.cs), so a throw from inside it has
            // already rolled back — this catch only turns that into a verdict instead of letting it
            // reach the caller, which for the server's own startup path must never happen.
            return new SeedImportRunResult(SeedImportStatus.Failed, ex.Message, null);
        }
    }

    static string Describe(IReadOnlyList<SeedError> errors) =>
        $"{errors.Count} error(s): " + string.Join("; ", errors.Take(5).Select(e => e.ToString()))
        + (errors.Count > 5 ? $" (+{errors.Count - 5} more)" : "");
}
