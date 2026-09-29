namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// The kernel source guard, extracted from its tests so the guard itself can be tested.
///
/// A guard that would stay green while the thing it guards is violated is worse than no guard —
/// it manufactures confidence. Keeping the logic here lets one test point it at the real kernel
/// and others point it at planted violations, so we know it can actually fail.
///
/// <b>It is a line-based heuristic, not a proof, and should be described that way.</b> A compiler
/// would be exact; this is a grep with judgement. Known blind spots, recorded so nobody mistakes a
/// green scan for a guarantee: a call split mid-expression (<c>items.</c> newline <c>Where(</c>)
/// never forms a contiguous token; block comments (<c>/* … */</c>) are not stripped, so a banned
/// word inside one reads as code; verbatim strings with doubled quotes can confuse the quote
/// tracker. Its value is catching the ordinary case cheaply on every run — the exotic ones are for
/// review to catch.
///
/// Two rule sets, because they protect different properties:
/// <list type="bullet">
/// <item><b>Purity</b> — determinism. No wall clock, no RNG, no dictionary enumeration, no
/// <c>using static</c> bypass. Applies to every kernel file, with no exceptions. Floating point is
/// NOT a purity rule (owner ruling 2026-09-15: the project-wide floating-point ban is removed —
/// floating point is allowed for any quantity; determinism of a double in a hashed golden is handled
/// by the platform stamp in ssot-power-scale.md §10.7, not by a source ban).</item>
/// <item><b>Tick path</b> — frame cost. No LINQ, no scene scans, no stat resolves. The kernel
/// runs inside the Unity frame, so these are allocation and latency, not style.</item>
/// </list>
/// </summary>
static class KernelPurityScan
{
    /// <summary>Determinism hazards. No file is exempt.</summary>
    public static readonly string[] BannedEverywhere =
    {
        // wall clock. Written with a trailing dot, like `.GetHashCode(` below: the danger is the
        // STATIC MEMBER ACCESS (`DateTime.UtcNow`, `DateTimeOffset.Now`), not the bare type name —
        // BattleEngine already uses `DateTimeOffset` as a deterministic VALUE (the synthetic round
        // clock, passed by the caller, never read from the wall clock), and the action program's
        // extracted step functions need the same parameter type to interoperate with it.
        "DateTime.", "DateTimeOffset.", "Stopwatch", "Environment.Tick", "TimeProvider",
        // randomness — including the ones that do not look like randomness. `.GetHashCode(` is a
        // CALL: string and object hash codes are seeded per process, so using one as a value makes
        // a run unreproducible. Written with the leading dot so it does not flag the perfectly
        // legitimate `override int GetHashCode()` declaration.
        "Random", "Guid.NewGuid", ".GetHashCode(",
        // dictionary enumeration
        ".Keys", ".Values",
        // `using static` is a true purity bypass, not just a tick-path one: `using static
        // System.Guid;` turns `NewGuid()` into a bare call the dotted `Guid.NewGuid` substring
        // check can no longer see. It stays banned everywhere, including inside a tick-path-exempt
        // directory, because it can hide a wall-clock or RNG call just as easily as it hides LINQ.
        "using static"
    };

    /// <summary>
    /// Frame-cost hazards on the tick path: LINQ allocates enumerators and delegates, a scene scan
    /// is the exact cost this repo already had to rescue once, and a stat resolve on a per-event
    /// path breaks the standing "per-hit cost is O(1)" invariant.
    /// </summary>
    public static readonly string[] BannedOnTickPath =
    {
        ".Select(", ".SelectMany(", ".Where(", ".OrderBy(", ".OrderByDescending(", ".GroupBy(",
        ".ToList(", ".ToArray(", ".ToDictionary(", ".Any(", ".All(", ".First(", ".FirstOrDefault(",
        ".Last(", ".Single(", ".SequenceEqual(", ".Concat(", ".Distinct(", ".Reverse(",
        ".Skip(", ".Take(", ".Aggregate(", ".Sum(", ".Min(", ".Max(", ".Count(",
        "FindObjectsOfType", "GetComponent", "StatSystem.Resolve",
        // A plain `using System.Linq;` is not itself a bypass — dotted calls still have their dot,
        // so the tokens above still match. It lives here rather than in BannedEverywhere because
        // its only cost IS the tick-path one: a directory exempted from LINQ's frame cost has no
        // reason to also ban declaring the intent to use it.
        "using System.Linq"
    };

    /// <summary>
    /// Files exempt from the TICK-PATH rules only — never from purity. Diagnostics may allocate
    /// because they are null in production and every record site is null-conditional; that
    /// exemption is exactly why a diagnostic must never become non-null by default.
    ///
    /// Deliberately a narrow named list rather than a pattern: an exemption that matches by
    /// wildcard grows silently, and the next file to allocate would inherit it for free.
    /// </summary>
    public static readonly string[] DiagnosticsExemptFromTickPath = { "BattleTrace.cs" };

    /// <summary>
    /// Directory prefixes (relative to the scanned root, trailing slash) whose files are exempt
    /// from the TICK-PATH rules only — never purity. The action layer's targeting and generation
    /// code (<c>TargetResolver</c> and its compiler) legitimately uses LINQ throughout; unlike the
    /// per-frame battle kernel this scan was built for, action-layer allocation is asserted
    /// directly by its own tests (e.g. <c>FactReader.Reads</c> counts, zero-alloc assertions)
    /// rather than by this blunt static ban.
    ///
    /// Still a narrow named list, one entry per directory that has actually earned the exemption —
    /// not a wildcard, and not "everything under src/".
    /// </summary>
    public static readonly string[] TickPathExemptDirectories = { "Actions/" };

    /// <summary>
    /// Returns "file:line → token" for every offending line. Empty means clean.
    ///
    /// Recursive on purpose. A top-level-only scan silently stops covering the kernel the moment
    /// anyone adds a subfolder — the guard would stay green while new code went unguarded, which
    /// is the failure mode that makes a guard worse than none.
    /// </summary>
    public static List<string> Scan(string dir)
    {
        // A directory exemption must fire whether the caller scans an ANCESTOR of the exempt
        // directory (so the relative path is prefixed, e.g. "Actions/Rungs/Foo.cs") or scans the
        // exempt directory ITSELF as the root (so the relative path has no prefix at all, e.g.
        // "Rungs/Foo.cs" when `dir` already IS ".../Actions"). Computed once per scan, not per file.
        var rootName = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/', '\\')) + "/";
        var rootItselfExempt = TickPathExemptDirectories.Contains(rootName, StringComparer.Ordinal);

        var offences = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            // Relative path, not bare filename: over a recursive scan, matching on the name alone
            // means `Nested/Anything/BattleTrace.cs` silently inherits the exemption — which is
            // the wildcard growth this list exists to prevent. It also disambiguates offences
            // between same-named files in different folders.
            var name = Path.GetRelativePath(dir, file).Replace('\\', '/');
            var tickPathExempt = rootItselfExempt
                || DiagnosticsExemptFromTickPath.Contains(name, StringComparer.Ordinal)
                || TickPathExemptDirectories.Any(p => name.StartsWith(p, StringComparison.Ordinal));
            var lines = File.ReadAllLines(file);
            var inBlockComment = false;

            for (var i = 0; i < lines.Length; i++)
            {
                var code = StripComment(lines[i], ref inBlockComment);
                if (code.Length == 0) continue;

                foreach (var token in BannedEverywhere)
                    if (code.Contains(token, StringComparison.Ordinal))
                        offences.Add($"{name}:{i + 1} → {token}");

                if (tickPathExempt) continue;
                foreach (var token in BannedOnTickPath)
                    if (ContainsRealCall(code, token))
                        offences.Add($"{name}:{i + 1} → {token} (tick path)");
            }
        }

        return offences;
    }

    /// <summary>
    /// Receivers whose members share a name with a LINQ operator but are cheap intrinsics.
    /// <c>Math.Max</c> is not <c>IEnumerable.Max</c>, and flagging it would train people to
    /// suppress the guard — a guard that cries wolf gets disabled, which is the same outcome as
    /// having no guard, arrived at more slowly.
    /// </summary>
    /// <summary>
    /// Receivers whose members share a name with a LINQ operator but are cheap, non-allocating
    /// intrinsics: <c>Math.Max</c>, <c>Array.Reverse</c>, <c>string.Concat</c>, <c>Span.Slice</c>.
    /// Flagging these trains people to suppress the guard, and a suppressed guard is no guard.
    /// </summary>
    static readonly string[] SafeReceivers =
        { "Math", "MathF", "Array", "string", "String", "Span", "ReadOnlySpan", "Buffer" };

    /// <summary>
    /// Matches <c>.Name</c> followed by optional whitespace, optional generic arguments, more
    /// optional whitespace, then <c>(</c> — because C# allows all of that between a method name
    /// and its argument list. A raw substring match on <c>".Where("</c> is defeated by a single
    /// space, which is not a theoretical concern: it is one keystroke.
    /// </summary>
    static readonly Dictionary<string, System.Text.RegularExpressions.Regex> CallPatterns = new(StringComparer.Ordinal);

    static System.Text.RegularExpressions.Regex PatternFor(string token)
    {
        if (CallPatterns.TryGetValue(token, out var cached)) return cached;

        // token looks like ".Where(" or "FindObjectsOfType" or "StatSystem.Resolve"
        var built = token.EndsWith("(", StringComparison.Ordinal)
            ? System.Text.RegularExpressions.Regex.Escape(token[..^1]) + @"\s*(<[^>()]*>)?\s*\("
            : System.Text.RegularExpressions.Regex.Escape(token);

        var regex = new System.Text.RegularExpressions.Regex(built,
            System.Text.RegularExpressions.RegexOptions.Compiled);
        CallPatterns[token] = regex;
        return regex;
    }

    static bool ContainsRealCall(string code, string token)
    {
        foreach (System.Text.RegularExpressions.Match m in PatternFor(token).Matches(code))
        {
            if (!PrecededBySafeReceiver(code, m.Index)) return true;
        }

        return false;
    }

    static bool PrecededBySafeReceiver(string code, int at)
    {
        foreach (var receiver in SafeReceivers)
        {
            if (at < receiver.Length) continue;
            if (!code.AsSpan(at - receiver.Length, receiver.Length).SequenceEqual(receiver)) continue;

            // Require a real boundary before the receiver, so a variable named `_fooMath` is not
            // mistaken for the `Math` intrinsic.
            var boundary = at - receiver.Length - 1;
            if (boundary < 0 || !(char.IsLetterOrDigit(code[boundary]) || code[boundary] == '_'))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Removes a line's comment before scanning. Prose must be free to NAME a banned construct —
    /// the doc comments explaining why wall-clock reads are forbidden would otherwise trip the
    /// guard, which would teach people to stop documenting.
    /// </summary>
    /// <summary>
    /// Returns the code portion of a line, with comments removed.
    ///
    /// Quote-aware because a naive "cut at the first //" is a bypass, not merely a false positive:
    /// <c>var s = "a//b"; var t = DateTime.UtcNow;</c> would truncate inside the literal and hide
    /// what follows. Block-comment aware (state carried across lines by
    /// <paramref name="inBlockComment"/>) because otherwise prose inside a <c>/* … */</c> reads as
    /// code — and a guard that flags its own documentation gets suppressed.
    /// </summary>
    static string StripComment(string line, ref bool inBlockComment)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        var inString = false;

        for (var i = 0; i < line.Length; i++)
        {
            if (inBlockComment)
            {
                if (i + 1 < line.Length && line[i] == '*' && line[i + 1] == '/') { inBlockComment = false; i++; }
                continue;
            }

            var c = line[i];
            if (!inString && c == '/' && i + 1 < line.Length)
            {
                if (line[i + 1] == '/') break;                       // line comment: done
                if (line[i + 1] == '*') { inBlockComment = true; i++; continue; }
            }

            if (c == '\\' && inString) { i++; continue; }            // escaped char inside a string
            if (c == '"') inString = !inString;
            sb.Append(c);
        }

        return sb.ToString();
    }
}
