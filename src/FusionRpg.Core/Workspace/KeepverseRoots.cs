using System.Runtime.CompilerServices;

namespace FusionRpg.Core.Workspace;

/// <summary>
/// The three roots a path resolves from: content, core, workspace.
///
/// <para><b>Why this exists in production and not only in tests.</b> Before the Keepverse split all
/// three were the legacy repository root, so "walk up looking for <c>gk-data/packs/fusion/data/seed</c>" was a complete
/// answer. After the split it is not: <c>gk-data/packs/fusion/data/seed</c> and <c>gk-data/packs/fusion/data/generated</c> live in a
/// <c>gk-data</c> pack and <c>gk-core/data/tuning</c> lives in <c>gk-core</c>, and a program running from
/// <c>gk-core/tests/.../bin</c> cannot reach either by walking up. The split gave MSBuild knowledge
/// of the roots — <c>GkDataRoot</c> and friends in <c>Directory.Build.props</c> — and left the running
/// program with none, so the build went green and every content-reading test failed at runtime with
/// <c>FileNotFoundException</c> on a path that demonstrably exists. This type is that missing half.
///
/// <para><b>The contract, unchanged from the Python resolver.</b> Same rules as
/// <c>gk-forge/tools/seedsmith/seedsmith/workspace_roots.py</c> and <c>gk-core/scripts/lib/keepverse_roots.py</c>: the
/// <c>KEEPVERSE_*_ROOT</c> environment override wins; otherwise walk up from <paramref name="start"/>
/// to the first legacy repository (<c>FusionRpg.slnx</c> next to <c>gk-data/packs/fusion/data/seed/</c>) or Keepverse
/// workspace (<c>gk-core/</c> next to <c>gk-data/</c>). Nothing found throws here; a root is never
/// guessed. Keeping the three implementations in step is the whole point — see
/// <see cref="Roots"/> for the one behavioural difference, and why it is deliberate.
///
/// <para><b>No Unity, no SQL, no IO beyond probing the filesystem</b>, so this stays in
/// <c>FusionRpg.Core</c> and every assembly above it can use it: <c>FusionRpg.Data</c>'s
/// <c>SeedImportRunner.FindUp</c>, the generator tools, and the server.
/// </para>
/// </summary>
public static class KeepverseRoots
{
    /// <summary>Structural: the pack the current corpus ships as (decision D7), not a balance number.</summary>
    public const string DefaultPack = "fusion";

    /// <summary>The detected layout, or <see langword="null"/> when neither shape is above
    /// <paramref name="start"/>. <see langword="false"/> means a Keepverse workspace.</summary>
    public static (bool Legacy, string Root)? Detect(string? start = null)
    {
        var dir = new DirectoryInfo(StartAt(start));
        while (dir is not null)
        {
            var root = dir.FullName;
            if (File.Exists(Path.Combine(root, "FusionRpg.slnx")) &&
                Directory.Exists(Path.Combine(root, "data", "seed")))
                return (true, root);
            if (Directory.Exists(Path.Combine(root, "gk-core")) &&
                Directory.Exists(Path.Combine(root, "gk-data")))
                return (false, root);
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Root that repo-relative content paths (<c>gk-data/packs/fusion/data/seed</c>, <c>gk-data/packs/fusion/data/generated</c>,
    /// <c>data/packs</c>) resolve from. Throws when no layout is detectable, because a caller asking
    /// for a named root has already decided it needs one.</summary>
    public static string Content(string? start = null)
    {
        if (Env("KEEPVERSE_CONTENT_ROOT") is { } env) return env;
        var (legacy, root) = Detected(start);
        if (legacy) return root;
        var pack = Path.Combine(root, "gk-data", "packs", Env("KEEPVERSE_PACK") ?? DefaultPack);
        if (!Directory.Exists(pack))
            throw new DirectoryNotFoundException(
                $"Content pack '{pack}' does not exist. The workspace was detected at '{root}' but the " +
                $"pack the corpus ships as is missing, so no content root can be named. Set " +
                $"KEEPVERSE_PACK if the corpus ships under a different pack name.");
        return pack;
    }

    /// <summary>Root of the engine repository: <c>src/</c>, <c>tests/</c>, <c>gk-core/data/tuning/</c>.</summary>
    public static string Core(string? start = null)
    {
        if (Env("KEEPVERSE_CORE_ROOT") is { } env) return env;
        var (legacy, root) = Detected(start);
        return legacy ? root : Path.Combine(root, "gk-core");
    }

    /// <summary>Root holding <c>docs/</c> and <c>tasks/</c>.</summary>
    public static string Workspace(string? start = null) =>
        Env("KEEPVERSE_WORKSPACE_ROOT") ?? Detected(start).Root;

    /// <summary>
    /// The roots a relative path should be tried against, in order, deduplicated.
    ///
    /// <para><b>This is the one deliberate difference from the Python resolver, and it exists because
    /// these are answers to a different question.</b> <c>workspace_roots.py</c> is asked "where does
    /// <c>gk-data/packs/fusion/data/seed</c> live", and it is entitled to one answer. <see cref="Roots"/> is asked "which
    /// roots should I try before falling back to walking up", because the caller passes a path whose
    /// owning root is not always knowable from the call site — <c>SeedImportRunner.FindUp</c> is handed
    /// <c>gk-data/packs/fusion/data/seed</c> and <c>gk-core/data/tuning</c> by different callers and cannot tell them apart, and the
    /// two live in different repositories after the split.
    ///
    /// <para>So it returns both, and lets the filesystem decide by trying them. That is why no
    /// convention is hardcoded here: <c>gk-core/data/tuning</c> exists only under the core root and
    /// <c>gk-data/packs/fusion/data/seed</c> only under the content pack, so trying content then core gives the right answer
    /// for both without the resolver encoding which is which. In a legacy checkout all three roots are
    /// the same directory, so this collapses to one entry and behaves exactly as the old walk did.
    ///
    /// <para><b>Returns an empty list when no layout is detectable</b>, rather than throwing. Callers
    /// use this as a best-effort pre-pass in front of a walk that already returns <see langword="null"/>
    /// for "not found", and a probe must not be the thing that throws.
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> Roots([CallerMemberName] string? start = null)
    {
        var found = new List<string>(2);
        void Add(string? candidate)
        {
            if (string.IsNullOrEmpty(candidate)) return;
            var full = Path.GetFullPath(candidate);
            if (!found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
        }

        // An explicit override is a statement about where things are, so it is honoured first and the
        // detection below is only a fallback. Two overrides can disagree; content wins for content
        // paths because that is the pack the corpus ships as.
        var contentOverride = Env("KEEPVERSE_CONTENT_ROOT");
        var coreOverride = Env("KEEPVERSE_CORE_ROOT");
        Add(contentOverride);
        Add(coreOverride);
        if (found.Count > 0) return found;

        var layout = Detect(start);
        if (layout is not { } detected) return found;
        if (detected.Legacy)
        {
            Add(detected.Root);
            return found;
        }
        Add(Path.Combine(detected.Root, "gk-data", "packs", Env("KEEPVERSE_PACK") ?? DefaultPack));
        Add(Path.Combine(detected.Root, "gk-core"));
        return found;
    }

    private static (bool Legacy, string Root) Detected(string? start) =>
        Detect(start) ?? throw new DirectoryNotFoundException(
            $"No legacy repository (FusionRpg.slnx next to data/seed/) or Keepverse workspace " +
            $"(gk-core/ next to gk-data/) above '{StartAt(start)}'. A root is never guessed.");

    private static string StartAt(string? start) =>
        string.IsNullOrEmpty(start) ? AppContext.BaseDirectory : start;

    private static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}
