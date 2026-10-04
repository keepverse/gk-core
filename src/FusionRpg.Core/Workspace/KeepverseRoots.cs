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
            // The legacy probe requires data/tuning as well as data/seed, and that is the same fix the
            // three Python copies carry. A split repository satisfies the old one-marker probe BY
            // ITSELF: gk-forge owns its own FusionRpg.slnx and its own
            // data/seed/creatures/{_generated,_registry}, because the split left a repository's generator
            // inputs where the generator is - so a walk upward from anywhere inside gk-forge stopped at
            // gk-forge and this type reported Legacy=true for it. That is not a cosmetic misdetection:
            // ContentRoot() returns `root` unchanged when Legacy is true, so every content path resolved
            // into gk-forge's partial data/seed instead of the gk-data pack, and CoreRoot() resolved to
            // gk-forge instead of gk-core.
            //
            // Verified against the pre-split repository rather than assumed: at its HEAD it carries
            // FusionRpg.slnx, data/seed, data/tuning and data/generated side by side. Measured across
            // the split, of the repositories carrying the solution file - gk-forge, gk-core,
            // gk-fusion - data/tuning is in gk-core ALONE, so no split repository satisfies both markers.
            //
            // NEAREST WINS is unchanged and is not negotiable: a legacy clone nested inside the workspace
            // must resolve against itself, or content resolves into a pack the caller never asked for.
            // The fix is the probe's PRECISION, not the walk's order - an attempt to make the outermost
            // match win fixed two repositories, did nothing for gk-forge, and broke that contract.
            if (File.Exists(Path.Combine(root, "FusionRpg.slnx")) &&
                Directory.Exists(Path.Combine(root, "data", "seed")) &&
                Directory.Exists(Path.Combine(root, "data", "tuning")))
                return (true, root);
            if (Directory.Exists(Path.Combine(root, "gk-core")) &&
                Directory.Exists(Path.Combine(root, "gk-data")))
                return (false, root);
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>The engine repository itself, by the shape only gk-core has: <c>src/</c> +
    /// <c>data/tuning/</c> beside <c>FusionRpg.slnx</c>. Null when no such directory is above
    /// <paramref name="start"/>.
    ///
    /// <para><b>WHY THIS IS NOT A THIRD PROBE INSIDE <see cref="Detect"/>.</b> It was, and it broke
    /// the workspace: gk-core satisfies this shape at its OWN root, so a walk upward from inside gk-core
    /// stopped there and returned "standalone" before it ever reached the directory one level up that
    /// actually holds <c>gk-core/</c> beside <c>gk-data/</c>. Every content path then resolved against
    /// gk-core instead of the gk-data pack. Caught by running the workspace suite — the failure was
    /// <c>86 failed / 0 passed</c> in <c>DungeonTestFiles</c>, all of them throwing
    /// <see cref="Content"/>, which is a workspace that had stopped working.</para>
    ///
    /// <para><b>So it is a FALLBACK, reached only after <see cref="Detect"/> has raised</b> — which is
    /// exactly the shape the Python <c>core_root()</c> already had, and the two now agree because they
    /// are guarded by the same idea: a layout that is only <i>absent siblings</i> must never outrank a
    /// layout that was actually detected. NEAREST WINS is not negotiable, and this is what it is
    /// protecting.</para>
    ///
    /// <para><b>WHAT IT RECOGNISES IS NARROW AND DELIBERATE.</b> <c>src/</c> plus <c>data/tuning/</c> is
    /// what makes a directory the engine repository, and gk-core owns exactly those two. It is
    /// deliberately NOT <c>data/seed</c>: recognising a repository that claims to carry the derived
    /// corpus is the misdetection the <c>data/tuning</c> refinement in <see cref="Detect"/> fixed once,
    /// and reintroducing it in the other direction would undo that.</para>
    ///
    /// <para><b>THE MEASUREMENT THAT MADE IT NECESSARY.</b> At 638072b, in an isolated clone with every
    /// sibling absent, <c>dotnet test</c> reported 12,435 of 12,556 failures carrying
    /// <c>KeepverseRoots.cs:line 292</c> — the <see cref="Detected"/> throw — and the count said
    /// "everything", which is the one reading that carries no information. <see cref="Core"/> can answer
    /// in a clone and was failing with the accessors that genuinely cannot.</para>
    /// </summary>
    public static string? StandaloneCore(string? start = null)
    {
        var dir = new DirectoryInfo(StartAt(start));
        while (dir is not null)
        {
            var root = dir.FullName;
            if (File.Exists(Path.Combine(root, "FusionRpg.slnx")) &&
                Directory.Exists(Path.Combine(root, "src")) &&
                Directory.Exists(Path.Combine(root, "data", "tuning")))
                return root;
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
        try
        {
            var (legacy, root) = Detected(start);
            return legacy ? root : Path.Combine(root, "gk-core");
        }
        catch (DirectoryNotFoundException)
        {
            // No layout at all. A STANDALONE CLONE IS ONE, and it is this repository: it carries
            // FusionRpg.slnx, src/ and data/tuning/. Returning it here is what makes `dotnet build` and
            // the content-free tests work with every sibling absent.
            //
            // The fallback is reached ONLY after Detect() has failed, which is the property that keeps a
            // workspace working: see StandaloneCore()'s own comment for the regression that putting this
            // probe inside Detect() caused, and why "a layout that is only absent siblings must never
            // outrank a detected layout" is the rule rather than a preference.
            if (StandaloneCore(start) is { } standalone) return standalone;
            throw;
        }
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
        var sibling = legacy ? root : Path.Combine(root, "gk-fusion");
        // Refuse rather than name a repository that is not there: this type's contract says
        // "a root is never guessed", and Content() already does it for its pack. A standalone
        // gk-core clone has no siblings by definition, so without this a caller gets a
        // confident path into a directory that does not exist.
        if (!Directory.Exists(sibling))
            throw new DirectoryNotFoundException(
                $"gk-fusion is not present at {sibling} (workspace at {root}); "
                + "a sibling repository cannot be reached by walking upward, so set "
                + "KEEPVERSE_FUSION_ROOT to point at it, or place it beside gk-core.");
        return sibling;
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
        var sibling = legacy ? root : Path.Combine(root, "gk-forge");
        // Refuse rather than name a repository that is not there: this type's contract says
        // "a root is never guessed", and Content() already does it for its pack. A standalone
        // gk-core clone has no siblings by definition, so without this a caller gets a
        // confident path into a directory that does not exist.
        if (!Directory.Exists(sibling))
            throw new DirectoryNotFoundException(
                $"gk-forge is not present at {sibling} (workspace at {root}); "
                + "a sibling repository cannot be reached by walking upward, so set "
                + "KEEPVERSE_FORGE_ROOT to point at it, or place it beside gk-core.");
        return sibling;
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
        var sibling = legacy ? root : Path.Combine(root, "gk-web");
        // Refuse rather than name a repository that is not there: this type's contract says
        // "a root is never guessed", and Content() already does it for its pack. A standalone
        // gk-core clone has no siblings by definition, so without this a caller gets a
        // confident path into a directory that does not exist.
        if (!Directory.Exists(sibling))
            throw new DirectoryNotFoundException(
                $"gk-web is not present at {sibling} (workspace at {root}); "
                + "a sibling repository cannot be reached by walking upward, so set "
                + "KEEPVERSE_WEB_ROOT to point at it, or place it beside gk-core.");
        return sibling;
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
