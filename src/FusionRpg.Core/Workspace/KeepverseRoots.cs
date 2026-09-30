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

    /// <summary>
    /// Root of the AUTHORED content tree — <c>content/</c>, the display strings and other text the
    /// owner writes by hand.
    ///
    /// <para><b>This is a fourth root, and it was missing until the first test asked for it.</b> The
    /// other three are gk-core (code and tuning), the gk-data pack (the derived corpus) and
    /// gk-workflow (the process). <c>content/</c> is neither: it is a separate repository, gk-content,
    /// which holds exactly ONE file in this migration — <c>gk-content/content/display/en.json</c>. A tree with
    /// one file is why it went unnoticed, and the symptom was 178 test failures naming a path under
    /// the workspace root, because <see cref="Content"/> returned the gk-data pack and
    /// <c>content/</c> is not inside the pack.
    ///
    /// <para>Worth stating plainly because it is a contract error rather than only a missing method:
    /// <c>workspace_roots.py</c> lists <c>content</c> among the paths that
    /// <c>content_root()</c> resolves, which was true before the split and is false after it. The
    /// Python resolver is not wrong in practice — seedsmith reads <c>gk-data/packs/fusion/data/seed</c>, not
    /// <c>content/</c> — but its docstring names a path the split moved out from under it, and that
    /// is the same sentence a future reader would trust.
    /// </para></summary>
    public static string AuthoredContent(string? start = null)
    {
        if (Env("KEEPVERSE_AUTHORED_CONTENT_ROOT") is { } env) return env;
        var (legacy, root) = Detected(start);
        return legacy ? root : Path.Combine(root, "gk-content");
    }

    /// <summary>Root holding <c>docs/</c> and <c>tasks/</c>. In a workspace that is gk-workflow, so
    /// this is how a repository reads a document it does not itself own -
    /// <c>docs/architecture/power/ssot-power-scale.md</c>, for instance.</summary>
    public static string Workspace(string? start = null) =>
        Env("KEEPVERSE_WORKSPACE_ROOT") ?? Detected(start).Root;

    /// <summary>
    /// Root of gk-fusion: <c>src/FusionRpg.Injector/</c>, <c>src/FusionRpg.Launcher/</c> and the
    /// loader hosts.
    ///
    /// <para><b>This accessor exists because six gk-core tests could not be fixed any other
    /// way.</b> They scan the Injector's source to hold an invariant over it - that no handler
    /// writes a stat directly, that the HUD pool identity is not restated, that a status clear
    /// goes through one writer - and after the split each one looked for
    /// <c>&lt;gk-core&gt;/src/FusionRpg.Injector/GameHooks.cs</c>, a path gk-core does not
    /// contain. The two available answers were both wrong. Moving the tests to gk-fusion would put
    /// a test in a repository whose subject it does not own, and keeping a hand-rolled sibling
    /// lookup in each test would make gk-core depend on a sibling by a private convention that
    /// nothing documents. A named accessor is the third answer: the dependency is real, so it is
    /// stated once, here, where the resolver already lives and where an env override can redirect
    /// it.
    ///
    /// <para>Note the asymmetry with <see cref="Workspace"/>, which is an ANCESTOR of gk-core and
    /// therefore reachable by construction, while gk-fusion is a SIBLING and needs a name of its
    /// own. That difference is why one of the two was a one-line fix and the other needed a method
    /// written.</para>
    /// </summary>
    public static string Fusion(string? start = null)
    {
        if (Env("KEEPVERSE_FUSION_ROOT") is { } env) return env;
        var (legacy, root) = Detected(start);
        return legacy ? root : Path.Combine(root, "gk-fusion");
    }

    /// <summary>
    /// Root of gk-forge: the generator and audit tools (<c>tools/CreatureSpeciesGen</c>,
    /// <c>tools/ItemSeedValidator</c>, <c>tools/DominanceBaseline</c>, and the Python tree under
    /// <c>tools/seedsmith</c>).
    ///
    /// <para><b>Why this is a sibling and needs a name.</b> Same argument as <see cref="Fusion"/>:
    /// a generator belongs to the repository that owns its output, so the tools moved to gk-forge,
    /// while gk-core's tests legitimately RUN some of them and COMPILE against one
    /// (<c>CorpusDumpTests</c>). A tool that runs is reachable as a process and needs no root; the
    /// one that is compiled against needs its source tree, and there is no ancestor of gk-core that
    /// contains it.
    ///
    /// <para><b>It is NOT an excuse to compile against gk-forge.</b> That is still forbidden by
    /// this repository's AGENTS.md, and naming the root makes the one honest exception visible
    /// rather than hiding it behind a hop count: <c>CorpusDumpTests</c> is the only caller that
    /// needs types, and it is recorded as an open topology item rather than treated as settled.
    /// </para>
    /// </summary>
    public static string Forge(string? start = null)
    {
        if (Env("KEEPVERSE_FORGE_ROOT") is { } env) return env;
        var (legacy, root) = Detected(start);
        return legacy ? root : Path.Combine(root, "gk-forge");
    }

    /// <summary>
    /// Root of gk-web: the browser control room. The npm package sits one level down, at
    /// <c>web/fusion-rpg-web/</c>, so a caller wanting sources wants
    /// <c>Path.Combine(Web(), "web", "fusion-rpg-web")</c> — which is exactly the shape the guard
    /// tests were already writing by hand.
    ///
    /// <para>Named for the same reason as <see cref="Fusion"/> and <see cref="Forge"/>: after the
    /// split a path like <c>web/fusion-rpg-web/src/stages/world/fixtures/…</c> resolves against no
    /// ancestor of gk-core, and the two roots it could plausibly belong to - gk-core and gk-web -
    /// are siblings. Guessing between siblings is how a guard ends up asserting against a file that
    /// never existed.</para>
    /// </summary>
    public static string Web(string? start = null)
    {
        if (Env("KEEPVERSE_WEB_ROOT") is { } env) return env;
        var (legacy, root) = Detected(start);
        return legacy ? root : Path.Combine(root, "gk-web");
    }

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
        var authoredOverride = Env("KEEPVERSE_AUTHORED_CONTENT_ROOT");
        Add(authoredOverride);
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
        // Authored content first: it is a distinct repository, and a caller asking for "content"
        // must not be handed the derived pack that happens to share the word.
        Add(Path.Combine(detected.Root, "gk-content"));
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
