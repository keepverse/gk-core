using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// `species-progression` SP1.5 (`layer-source-selector`, map C1) — the STRUCTURAL shape of the C1
/// defect, guarded so it cannot come back by a different route than the one SP1.2 fixed.
///
/// <para><b>The shape, precisely.</b> C1's defect was one member that composed a unique specimen's
/// OWN allocation (`AllocationScope.UniqueCreature`) UNCONDITIONALLY merged with its empire's species
/// allocation (`EffectiveSpeciesAllocation*`) — "2a and 2b are mutually exclusive" (module 1's own
/// doc comment) violated by construction. The fix (SP1.2-SP1.5) is not "never mention both scopes in
/// one member" — `RpgStore.WorldTurnHubInputsForUnlocked` legitimately switches over
/// <c>ProgressionLayers.Owner</c> and reads whichever ONE of the two scopes that owner names — it is
/// "never read both WITHOUT first asking <see cref="FusionRpg.Core.Stats.Aptitudes.ProgressionLayerSelector"/>
/// which one applies." A member that mentions both substrings but never calls
/// <c>ProgressionLayerSelector.Select</c> has re-derived the rule instead of asking for it — the exact
/// copied-rule drift map C1 names — and is refused here even if today it happens to compose the two
/// correctly; the guard is about the SHAPE, not today's luck.</para>
/// </summary>
[Trait("guard", "progression-layer-selector")]
public class ProgressionLayerSelectorGuardTests
{
    const string UniqueCreatureMarker = "AllocationScope.UniqueCreature";
    const string SpeciesAllocationMarker = "EffectiveSpeciesAllocation";
    const string SelectorMarker = "ProgressionLayerSelector.Select";
    const string SelectorFileName = "ProgressionLayerSelector.cs";

    [Fact]
    public void No_production_member_outside_the_selector_reads_both_scopes_without_asking_it()
    {
        var root = Path.Combine(FindRepoRoot(), "src");
        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.GetFileName(file) == SelectorFileName)
                continue;

            IEnumerable<(string SignatureLine, string Body)> members;
            try
            {
                members = EnumerateMembers(File.ReadAllText(file)).ToList();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"EnumerateMembers failed on {file}: {ex.Message}", ex);
            }

            foreach (var member in members)
            {
                if (HasUnexemptedPairing(member.Body))
                    violations.Add($"{file}: member starting '{member.SignatureLine.Trim()}'");
            }
        }

        Assert.True(violations.Count == 0,
            "a member outside ProgressionLayerSelector.cs reads both AllocationScope.UniqueCreature "
            + "and EffectiveSpeciesAllocation* without asking ProgressionLayerSelector.Select first "
            + "(map C1's own copied-rule shape):\n" + string.Join("\n", violations));
    }

    // ---- the probe: proves the detector itself fires and does not false-positive -----------------

    [Fact]
    public void The_probe_fires_on_a_reintroduced_unconditional_pairing()
    {
        // The exact map C1 shape, reconstructed as a minimal, self-contained member: both scopes read
        // unconditionally, no selector asked at all.
        const string reintroduced = """
            internal BattleHubInputs? Reintroduced(SqliteConnection db, WorldEntityMember member, long playerId)
            {
                var commander = LoadAllocationUnlocked(db, AllocationScope.Commander, "player:" + playerId);
                var species = EffectiveSpeciesAllocationUnlocked(db, playerId, member.SpeciesId, tuning, empire);
                var specimen = LoadAllocationUnlocked(db, AllocationScope.UniqueCreature, member.InstanceId);
                return new BattleHubInputs { Aptitude = commander + species + specimen };
            }
            """;

        Assert.True(HasUnexemptedPairing(reintroduced),
            "the probe must fire on the reconstructed map C1 shape -- if it does not, the detector "
            + "itself is broken and the real scan above is not actually checking anything");
    }

    [Fact]
    public void The_sanctioned_shape_through_the_selector_does_not_fire()
    {
        // The SAME two scopes, in the SAME member -- but decided by the selector's owner switch, the
        // shape SP1.2 actually ships. Must NOT be flagged.
        const string sanctioned = """
            internal BattleHubInputs? Sanctioned(SqliteConnection db, WorldEntityMember member, long playerId)
            {
                var layers = ProgressionLayerSelector.Select(source, empire, humanEmpire);
                var commander = layers.CarriesCommander
                    ? LoadAllocationUnlocked(db, AllocationScope.Commander, "player:" + playerId)
                    : AptitudeAllocation.Empty;
                var specimen = layers.Owner switch
                {
                    ProgressionOwner.Specimen s => LoadAllocationUnlocked(db, AllocationScope.UniqueCreature, s.InstanceId),
                    ProgressionOwner.Species g => EffectiveSpeciesAllocationUnlocked(db, playerId, g.SpeciesId, tuning, layers.Empire),
                    _ => AptitudeAllocation.Empty,
                };
                return new BattleHubInputs { Aptitude = commander + specimen };
            }
            """;

        Assert.False(HasUnexemptedPairing(sanctioned),
            "a member that asks ProgressionLayerSelector.Select before reading either scope must not be flagged");
    }

    [Fact]
    public void A_member_that_reads_only_one_scope_never_fires()
    {
        const string oneScopeOnly = """
            public AptitudeAllocation ResolveAptitudeAllocation(RpgStore store, string specimenId, long playerId)
            {
                var uniqueAllocation = store.LoadAllocation(AllocationScope.UniqueCreature, specimenId);
                return uniqueAllocation;
            }
            """;

        Assert.False(HasUnexemptedPairing(oneScopeOnly));
    }

    // ---- the detector ------------------------------------------------------------------------------

    /// <summary>True when this member's body contains BOTH markers and never asks the selector. Pure,
    /// no file I/O -- exercised directly by the three probe facts above, and by the real scan.</summary>
    static bool HasUnexemptedPairing(string memberBody) =>
        memberBody.Contains(UniqueCreatureMarker, StringComparison.Ordinal)
        && memberBody.Contains(SpeciesAllocationMarker, StringComparison.Ordinal)
        && !memberBody.Contains(SelectorMarker, StringComparison.Ordinal);

    // ---- member enumeration (comments/strings stripped, brace-matched bodies) --------------------

    static readonly Regex MemberSignature = new(
        @"^[ \t]*(?:(?:public|private|internal|protected|static|readonly|async|override|virtual|sealed|new|partial|extern|unsafe)[ \t]+)*" +
        @"[\w<>\[\],\.\?]+[ \t]+(?:this\.)?\w+[ \t]*\([^;{}]*\)[ \t]*(?:where[^\{]*)?\{",
        RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Every top-level, brace-bodied member in the file, as (signature line, full brace-matched
    /// body text). Comments and string/char literals are blanked first (same length, so indices still
    /// line up) so a doc comment mentioning either marker in prose never counts as a real reference —
    /// `ProgressionLayerSelector.cs`'s own doc comments do exactly this, which is why it is excluded by
    /// filename above rather than relying on this stripping alone.</summary>
    static IEnumerable<(string SignatureLine, string Body)> EnumerateMembers(string rawText)
    {
        var text = StripCommentsAndStrings(rawText);
        foreach (Match m in MemberSignature.Matches(text))
        {
            var openBrace = m.Index + m.Length - 1;
            var close = FindMatchingBrace(text, openBrace);
            if (close < 0) continue;
            var lineStart = text.LastIndexOf('\n', m.Index) + 1;
            var lineEnd = text.IndexOf('\n', m.Index);
            var signatureLine = text[lineStart..(lineEnd < lineStart ? text.Length : lineEnd)];
            yield return (signatureLine, text[openBrace..(close + 1)]);
        }
    }

    static int FindMatchingBrace(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    static string StripCommentsAndStrings(string text)
    {
        var chars = text.ToCharArray();
        var n = chars.Length;
        var i = 0;
        while (i < n)
        {
            var c = chars[i];
            if (c == '/' && i + 1 < n && chars[i + 1] == '/')
            {
                while (i < n && chars[i] != '\n') { chars[i] = ' '; i++; }
                continue;
            }
            if (c == '/' && i + 1 < n && chars[i + 1] == '*')
            {
                chars[i] = ' '; chars[i + 1] = ' '; i += 2;
                while (i + 1 < n && !(chars[i] == '*' && chars[i + 1] == '/'))
                {
                    if (chars[i] != '\n') chars[i] = ' ';
                    i++;
                }
                if (i + 1 < n) { chars[i] = ' '; chars[i + 1] = ' '; i += 2; }
                continue;
            }
            if (c is '"' or '\'')
            {
                var quote = c;
                chars[i] = ' '; i++;
                while (i < n)
                {
                    if (chars[i] == '\\' && i + 1 < n) { chars[i] = ' '; chars[i + 1] = ' '; i += 2; continue; }
                    var was = chars[i];
                    if (was != '\n') chars[i] = ' ';
                    i++;
                    if (was == quote) break;
                }
                continue;
            }
            i++;
        }
        return new string(chars);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
