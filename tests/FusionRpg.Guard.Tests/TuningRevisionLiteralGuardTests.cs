using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// strain-splice-host SSH5.2/SSH7.1/SSH8.4 (circuit-topology §4): each shipped tuning revision is
/// named ONCE, in <c>SocketTuningFiles</c>, and every production reader names that constant. A
/// literal filename per reader is how one reader stays on <c>v1</c> after the others move — the exact
/// defect these revision swings exist to avoid.
///
/// <para><b>Reads source as text</b>, matching every other guard here — Guard.Tests carries no
/// project reference to Core or Data on purpose, so this cannot accidentally exercise what it
/// polices.</para>
///
/// <para><b>What is allowed:</b> the constant's own file; comments, documentation and other prose;
/// and, temporarily, the test files <see cref="UnconvertedTests"/> names, which the reader-conversion
/// rows remove as they land. That set is shrinking by design and is empty for this slice.</para>
/// </summary>
public class TuningRevisionLiteralGuardTests
{
    static readonly Regex SocketsLiteral = new(@"sockets\.v[0-9]+\.json", RegexOptions.Compiled);
    static readonly Regex StrainSpliceLiteral = new(@"strain-splice\.v[0-9]+\.json", RegexOptions.Compiled);
    static readonly Regex MaterialsLiteral = new(@"materials\.v[0-9]+\.json", RegexOptions.Compiled);

    /// <summary>The ONE file allowed to name the literals in code — the constants themselves.</summary>
    const string ConstantFile = "src/FusionRpg.Core/Items/Sockets/SocketTuningFiles.cs";

    /// <summary>
    /// Test files that still read a shipped tuning by literal and are converted by their owning
    /// reader rows. Each entry is a task's own TODO, not a licence: the guard fails once a converted
    /// file is left here too, and this slice leaves the set empty.
    /// </summary>
    static readonly HashSet<string> UnconvertedTests = new(StringComparer.Ordinal)
    {
        // SSH5.3–SSH5.5: CONVERTED (removed from this list).
        // SSH7.1 and SSH8.4: the current strain-splice/materials test readers are converted in this
        // slice. A test that means an OLD revision as history keeps its literal and says so, and
        // would be named here; none does today.
    };

    [Fact]
    public void No_reader_names_a_sockets_revision_literal() =>
        No_reader_names_a_revision_literal("sockets", SocketsLiteral, "SocketTuningFiles.Current");

    [Fact]
    public void every_reader_loads_the_current_strain_splice_revision() =>
        No_reader_names_a_revision_literal(
            "strain-splice", StrainSpliceLiteral, "SocketTuningFiles.StrainSplice");

    [Fact]
    public void every_reader_loads_the_current_materials_revision() =>
        No_reader_names_a_revision_literal("materials", MaterialsLiteral, "SocketTuningFiles.Materials");

    static void No_reader_names_a_revision_literal(
        string domain, Regex literal, string canonicalConstant)
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        var staleAllowlist = new List<string>();

        foreach (var top in new[] { "src", "tools", "tests" })
        {
            foreach (var file in EnumerateCsFiles(Path.Combine(root, top)))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel.Contains("/obj/", StringComparison.Ordinal) ||
                    rel.Contains("/bin/", StringComparison.Ordinal)) continue;
                if (rel == ConstantFile) continue;

                var flagged = new List<string>();
                var inBlockComment = false;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var code = StripComments(lines[i], ref inBlockComment);
                    var match = literal.Match(code);
                    if (!match.Success || !IsReaderSyntax(code, match)) continue;
                    flagged.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                }

                if (UnconvertedTests.Contains(rel))
                {
                    // A file the allowlist still names but no longer needs is itself a defect: the
                    // allowlist must shrink in the same commit as the conversion.
                    if (flagged.Count == 0) staleAllowlist.Add(rel);
                    continue;
                }

                offenders.AddRange(flagged);
            }
        }

        Assert.True(offenders.Count == 0,
            $"these readers name a {domain} revision literal instead of {canonicalConstant}:\n" +
            string.Join("\n", offenders));
        Assert.True(staleAllowlist.Count == 0,
            "these files no longer need the temporary literal allowlist; remove them:\n" +
            string.Join("\n", staleAllowlist));
    }

    [Fact]
    public void The_one_constant_is_what_the_server_and_the_validator_name()
    {
        var root = RepoRoot();
        foreach (var rel in new[]
                 {
                     "src/FusionRpg.Server/Program.cs",
                     "tools/ItemSeedValidator/Checks/SocketMaxCheck.cs",
                 })
        {
            var text = File.ReadAllText(Path.Combine(root, rel));
            Assert.Contains("SocketTuningFiles.Current", text, StringComparison.Ordinal);
            Assert.DoesNotContain("\"sockets.v", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_server_names_the_current_strain_splice_and_materials_readers()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src/FusionRpg.Server/Program.cs"));
        Assert.Contains("SocketTuningFiles.StrainSplice", text, StringComparison.Ordinal);
        Assert.Contains("SocketTuningFiles.Materials", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"strain-splice.v", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"materials.v", text, StringComparison.Ordinal);
    }

    static bool IsReaderSyntax(string code, Match match)
    {
        var before = code[..match.Index];
        if (before.EndsWith("/", StringComparison.Ordinal) ||
            before.Contains("Path.Combine", StringComparison.Ordinal) ||
            before.Contains("Path.Join", StringComparison.Ordinal) ||
            before.Contains("Tuning(", StringComparison.Ordinal) ||
            before.Contains("ReadAllText(", StringComparison.Ordinal) ||
            before.Contains("ReadAllBytes(", StringComparison.Ordinal) ||
            before.Contains("OpenRead(", StringComparison.Ordinal) ||
            before.Contains("ReadText(", StringComparison.Ordinal) ||
            before.Contains("FileInfo(", StringComparison.Ordinal) ||
            before.Contains("DirectoryInfo(", StringComparison.Ordinal) ||
            before.Contains("GetFullPath(", StringComparison.Ordinal) ||
            before.Contains("GetFiles(", StringComparison.Ordinal) ||
            before.Contains("Parse(", StringComparison.Ordinal))
            return true;

        // A filename assigned to a path-shaped variable is a reader even when the read happens on a
        // later line. Do not treat a bare exception/message string as a reader.
        return Regex.IsMatch(before, @"(?:=|=>|return)\s*$", RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Remove C# comments while preserving string contents and line numbers. A filename in a doc
    /// comment or a block comment is prose; a filename in an exception message is also not a path
    /// join, and <see cref="IsReaderSyntax"/> deliberately distinguishes that from a real reader.
    /// </summary>
    static string StripComments(string source, ref bool inBlockComment)
    {
        var chars = source.ToCharArray();
        var inString = false;
        var inVerbatimString = false;
        var inRawString = false;
        var quote = '\0';
        var n = chars.Length;

        for (var i = 0; i < n; i++)
        {
            var c = chars[i];

            if (inBlockComment)
            {
                if (c == '*' && i + 1 < n && chars[i + 1] == '/')
                {
                    chars[i] = ' ';
                    chars[i + 1] = ' ';
                    i++;
                    inBlockComment = false;
                }
                else if (c is not '\r' and not '\n')
                {
                    chars[i] = ' ';
                }
                continue;
            }

            if (inRawString)
            {
                if (c == '"' && i + 2 < n && chars[i + 1] == '"' && chars[i + 2] == '"')
                {
                    i += 2;
                    inRawString = false;
                }
                continue;
            }

            if (inVerbatimString)
            {
                if (c == '"')
                {
                    if (i + 1 < n && chars[i + 1] == '"')
                    {
                        chars[i + 1] = ' ';
                        i++;
                    }
                    else
                    {
                        inVerbatimString = false;
                    }
                }
                continue;
            }

            if (inString)
            {
                if (c == '\\' && i + 1 < n)
                {
                    chars[i + 1] = ' ';
                    i++;
                }
                else if (c == quote)
                {
                    inString = false;
                }
                else if (c is '\r' or '\n')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '/' && i + 1 < n && chars[i + 1] == '/')
            {
                for (; i < n && chars[i] is not '\r' and not '\n'; i++)
                    chars[i] = ' ';
                i--;
                continue;
            }

            if (c == '/' && i + 1 < n && chars[i + 1] == '*')
            {
                chars[i] = ' ';
                chars[i + 1] = ' ';
                i++;
                inBlockComment = true;
                continue;
            }

            if (c == '@' && i + 1 < n && chars[i + 1] == '"')
            {
                inVerbatimString = true;
                i++;
                continue;
            }

            if (c == '"' && i + 2 < n && chars[i + 1] == '"' && chars[i + 2] == '"')
            {
                inRawString = true;
                i += 2;
                continue;
            }

            if (c is '"' or '\'')
            {
                inString = true;
                quote = c;
            }
        }

        return new string(chars);
    }

    /// <summary>
    /// Every <c>*.cs</c> under <paramref name="rootDir"/>, skipping <c>bin</c>/<c>obj</c> and every
    /// dot-directory, and skipping any directory the process cannot open. A guard must not fail
    /// because an ignored temp directory is unreadable (a leftover <c>tools/seedsmith/.tmp-*</c> audit
    /// directory raised <see cref="UnauthorizedAccessException"/> here on 2026-09-21): an access
    /// denial is a filesystem fact, not a reader naming a revision literal. Dot-directories are
    /// machine-local or tool-managed by convention and never tracked source.
    /// </summary>
    static IEnumerable<string> EnumerateCsFiles(string rootDir)
    {
        var pending = new Stack<string>();
        pending.Push(rootDir);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.cs");
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files) yield return file;

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(dir);
            }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (name is "bin" or "obj") continue;
                if (name.StartsWith(".", StringComparison.Ordinal)) continue;
                pending.Push(sub);
            }
        }
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
