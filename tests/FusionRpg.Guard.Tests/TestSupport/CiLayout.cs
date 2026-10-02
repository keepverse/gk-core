using System.Text.RegularExpressions;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// How a <c>ci.yml</c> step's <c>working-directory</c> maps to a path in the checked-out tree.
///
/// WHY THIS EXISTS. Two CI-wiring guards - <c>CiPytestWiringTests</c> and
/// <c>GeneratorCheckCiParityTests</c> - each compare a path DECLARED somewhere else against a
/// <c>working-directory</c> read out of <c>ci.yml</c>: a pytest project's <c>root</c> from
/// <c>verification-boundaries.v1.json</c>, and a wrapper's <c>WORKING_DIRECTORY</c> from
/// <c>scripts/checks/gen-*.py</c>. Both were literal string comparisons, which was correct only
/// while one repository held all of the code and was checked out at the workspace root. The split
/// broke both halves of that at once, and the breakage was invisible for a specific reason: CI's
/// working-directory is resolved against <c>github.workspace</c>, not against the repository, so a
/// path that is right locally is wrong on the runner, and the guards were asserting the local
/// spelling.
///
/// Two facts make the old comparison unrecoverable as written:
///
/// 1. <c>actions/checkout</c> resolves a step's <c>working-directory</c> against the WORKSPACE.
///    <c>defaults.run.working-directory</c> does NOT rebase it - a step that declares one resolves
///    from the workspace root regardless of the default - so "the directory the step runs in" is
///    not the string on its own line. A step with no <c>working-directory</c> runs in the job's
///    base, which is a real directory and was previously uncountable.
/// 2. A declared path names the tree the way that tree is named NEXT TO the thing that owns it.
///    <c>tools/seedsmith</c> is gk-forge's path, and <c>web/fusion-rpg-web</c> is gk-web's; after
///    the split neither exists inside gk-core at all. This is the same rule
///    <c>scripts/checks/common.py::resolve_owned</c> already applies on the Python side (local root
///    first, then a sibling), and it is why the wrapper constants are still correct and must not be
///    "fixed" to a workspace-relative spelling - that change would break the real local run to
///    satisfy a string comparison.
///
/// So both sides are reduced to one coordinate system, the WORKSPACE-RELATIVE path, before being
/// compared. That keeps every existing assertion exactly as strong: a pytest project still has to be
/// wired at its own directory, and a wrapper's command still has to appear in a step running in that
/// wrapper's own directory. What changed is only how a declared path is turned into a directory a
/// runner would actually use - which is the part that had become wrong.
///
/// The owning search is LOCAL ROOT FIRST, then the fixed set of repositories the split produced, and
/// it is not a search upward until something is found: a path no repository owns returns null and
/// the caller reports it by name, so moving a tree somewhere undeclared cannot make a gate quietly
/// pass.
/// </summary>
static class CiLayout
{
    /// <summary>The job's <c>defaults.run.working-directory</c>, or <c>"."</c> when it declares
    /// none. This is the directory a step with no <c>working-directory</c> of its own runs in, and
    /// under a job that declares no default it is the workspace root.</summary>
    public static string JobBaseDirectory(string ciText)
    {
        // Match the defaults block only. A `working-directory:` inside a step belongs to that step,
        // and reading the last one in the file would silently rebase every step on one step's path.
        var defaults = Regex.Match(ciText, @"(?m)^(\s*)defaults:\s*$");
        if (!defaults.Success) return ".";
        var indent = defaults.Groups[1].Value.Length;
        foreach (var line in ciText[(defaults.Index + defaults.Length)..].Split('\n'))
        {
            var trimmed = line.TrimStart();
            var lineIndent = line.Length - trimmed.Length;
            // A line indented no further than `defaults:` has left the block.
            if (trimmed.Length > 0 && lineIndent <= indent) break;
            if (trimmed.StartsWith("working-directory:", StringComparison.Ordinal))
                return trimmed["working-directory:".Length..].Trim().Replace('\\', '/');
        }
        return ".";
    }

    /// <summary>The directory a step actually runs in: its own <c>working-directory</c> when it has
    /// one, otherwise the job base. A step's own value resolves against the workspace, so it is
    /// used verbatim; the base is where a step with no value of its own lands.</summary>
    public static string StepWorkingDirectory(string? declared, string jobBase)
        => string.IsNullOrWhiteSpace(declared) ? jobBase : declared.Replace('\\', '/');

    /// <summary>The workspace-relative directory a project or wrapper declaring
    /// <paramref name="root"/> is expected to run in. A project that declares the repository that
    /// OWNS it resolves against that repository's checkout directory; anything else resolves against
    /// the job base, which is where this repository is checked out.
    ///
    /// This is DECLARED rather than probed on purpose. The owning repository is checked out at a
    /// different place locally than it is on a runner - gk-workflow is the workspace root in a full
    /// workspace and a <c>gk-workflow/</c> subdirectory in CI - so a probe would make this guard pass
    /// only where the gate it protects is not the one being run. Asking the filesystem would also
    /// have it answer differently on a clone that has not checked out every sibling yet.</summary>
    public static string ExpectedWorkspacePath(string jobBase, string root, string? owningRepo)
        => owningRepo is null
            ? Normalize($"{jobBase}/{root}")
            : Normalize($"{owningRepo}/{root}");

    /// <summary>The workspace-relative directory that OWNS <paramref name="relative"/>, or null when
    /// no repository does. <c>"."</c> and <c>""</c> mean the repository itself, which is gk-core for
    /// a wrapper and for a project declared at the root.</summary>
    public static string? OwningWorkspaceRelative(string relative)
    {
        var workspace = KeepverseRoots.Workspace();
        relative = (relative ?? "").Replace('\\', '/').Trim();
        if (relative.Length == 0 || relative == ".") return RelativeTo(workspace, KeepverseRoots.Core());

        // An upward reference is already expressed relative to the workspace, which is the only root
        // a workspace-relative answer can be spoken in: `../gk-forge/tools/seedsmith` is gk-forge's
        // own path, one level up from gk-core, so it normalises to the workspace-relative spelling.
        if (relative.StartsWith("../", StringComparison.Ordinal))
            return Normalize(relative.Substring(3));

        foreach (var repo in Repositories())
        {
            var candidate = Path.Combine(workspace, repo, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate) || Directory.Exists(candidate))
                return Normalize($"{repo}/{relative}");
        }
        return null;
    }

    /// <summary>The same repository list <c>common.py</c>'s <c>owning_base</c> consults: the
    /// repository under test first, then the siblings the split produced. Fixed on purpose - a
    /// search upward until something matches would let an unrelated directory answer for a tree,
    /// which is the failure mode this helper exists to remove.</summary>
    static IEnumerable<string> Repositories()
    {
        var workspace = KeepverseRoots.Workspace();
        var core = RelativeTo(workspace, KeepverseRoots.Core());
        if (core != ".") yield return core;
        foreach (var sibling in new[] { "gk-forge", "gk-web", "gk-workflow", "gk-fusion", "gk-content", "gk-data" })
            yield return sibling;
    }

    static string RelativeTo(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Length == 0 ? "." : relative;
    }

    /// <summary>Folds <c>.</c> and <c>..</c> segments away, so <c>gk-core/..</c> and
    /// <c>.</c> compare equal instead of differing only in spelling.</summary>
    public static string Normalize(string path)
    {
        var segments = new List<string>();
        foreach (var segment in (path ?? "").Replace('\\', '/').Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); continue; }
            segments.Add(segment);
        }
        return segments.Count == 0 ? "." : string.Join('/', segments);
    }
}