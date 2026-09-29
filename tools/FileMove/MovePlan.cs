using System.Text.RegularExpressions;

namespace FusionRpg.Tools.FileMove;

/// <summary>One file the move will rewrite, and what it will become.</summary>
/// <param name="Path">Repo-relative path of the file being changed.</param>
/// <param name="Before">Its current text.</param>
/// <param name="After">Its text after the move.</param>
/// <param name="Reason">Why this file is in the plan — shown by the dry run.</param>
public sealed record PlannedEdit(string Path, string Before, string After, string Reason);

/// <summary>
/// The full change set of one move, computed before anything is written.
/// </summary>
/// <param name="SourcePath">Repo-relative source.</param>
/// <param name="DestinationPath">Repo-relative destination.</param>
/// <param name="OldNamespace">The namespace the file declares today.</param>
/// <param name="NewNamespace">The namespace its destination folder implies.</param>
/// <param name="Edits">Every file that changes, including the moved file itself.</param>
/// <param name="Refusal">Non-null when the move must not happen; nothing is written.</param>
public sealed record MovePlan(
    string SourcePath,
    string DestinationPath,
    string OldNamespace,
    string NewNamespace,
    IReadOnlyList<PlannedEdit> Edits,
    string? Refusal)
{
    public bool IsRefused => Refusal is not null;
}

/// <summary>
/// `solid-remediation` T5.5 — move a C# file and rewire what the move breaks.
///
/// <para><b>Why a tool for four files.</b> It is not for those four: every one of them already declares
/// the namespace it should, so moving them is `git mv` and nothing else. The module's own measurement
/// said so, and the owner kept the tool anyway scoped to earn its keep. So the capabilities that matter
/// are the ones the four files do NOT exercise — rewiring callers, updating a project file across an
/// assembly boundary, and refusing a move that would create a cycle. Those are built against a synthetic
/// fixture, because a move tool is dangerous exactly where it is untested.</para>
///
/// <para><b>Text, not Roslyn.</b> Deliberate: the tool edits `.csproj` files as well as sources, it must
/// run without restoring a solution, and a namespace/using rewrite is a lexical change. The cost is that
/// it is conservative — it rewrites only what it can identify unambiguously and reports the rest rather
/// than guessing.</para>
/// </summary>
public static class MovePlanner
{
    static readonly Regex NamespaceDecl = new(
        @"^\s*namespace\s+(?<ns>[A-Za-z_][A-Za-z0-9_.]*)\s*(;|\{)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>The namespace a folder implies, from the assembly's root namespace plus the folders
    /// under its project directory.</summary>
    public static string NamespaceForFolder(string rootNamespace, string projectDir, string fileDir)
    {
        var rel = Path.GetRelativePath(projectDir, fileDir).Replace('\\', '/').Trim('/');
        if (rel is "" or ".") return rootNamespace;

        var parts = rel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return rootNamespace + "." + string.Join('.', parts);
    }

    public static string? DeclaredNamespace(string source)
    {
        var m = NamespaceDecl.Match(source);
        return m.Success ? m.Groups["ns"].Value : null;
    }

    /// <summary>
    /// Rewrites the moved file's own namespace declaration. Only the FIRST declaration is touched: a
    /// file with several is ambiguous about which one the folder names, and guessing there is how a
    /// tool silently reparents a type.
    /// </summary>
    public static string RewriteNamespace(string source, string newNamespace)
    {
        var m = NamespaceDecl.Match(source);
        if (!m.Success) return source;

        var g = m.Groups["ns"];
        return source[..g.Index] + newNamespace + source[(g.Index + g.Length)..];
    }

    /// <summary>
    /// Rewires a caller's `using` directives for a namespace that moved.
    ///
    /// <para>Adds the new using only when the caller actually had the old one — a caller in the same
    /// namespace as the moved type needed no using before and needs none after, and adding one there
    /// would be noise the reviewer has to dismiss on every move.</para>
    /// </summary>
    public static string RewireUsings(string source, string oldNamespace, string newNamespace)
    {
        var pattern = new Regex($@"^(?<indent>\s*)using\s+{Regex.Escape(oldNamespace)}\s*;\s*$",
            RegexOptions.Multiline);

        if (!pattern.IsMatch(source)) return source;

        // Already importing the destination too: drop the old line rather than leaving a duplicate.
        var hasNew = new Regex($@"^\s*using\s+{Regex.Escape(newNamespace)}\s*;\s*$", RegexOptions.Multiline)
            .IsMatch(source);

        return hasNew
            ? pattern.Replace(source, string.Empty).Replace("\r\n\r\n\r\n", "\r\n\r\n")
            : pattern.Replace(source, m => $"{m.Groups["indent"].Value}using {newNamespace};");
    }
}
