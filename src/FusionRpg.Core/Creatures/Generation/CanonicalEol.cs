namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// Line-ending normalisation for the committed generated trees.
///
/// <para><b>The defect this closes (2026-09-19, found while auditing <c>--check</c>).</b>
/// <c>JsonSerializer</c> with <c>WriteIndented = true</c> indents with <c>Environment.NewLine</c>, so
/// the same content serialises to CRLF on Windows and LF elsewhere — while <c>.gitattributes</c>
/// (<c>* text=auto eol=lf</c>) checks every generated file out as LF. A tree written on Windows is
/// therefore byte-stale against its own fresh checkout: <c>CreatureSpeciesGen --check</c> reported all
/// 904 species stale, and <c>CreatureBuildPlanGen --check</c> its plan, in every clean worktree, while
/// the machine that produced them read clean because its working files were the CRLF output itself.</para>
///
/// <para>Both halves go through here: the canonical text is written as LF, and text read back from disk
/// is normalised before it is compared.</para>
/// </summary>
public static class CanonicalEol
{
    /// <summary>CRLF → LF. A JSON string value escapes its own control characters, so a raw CRLF in
    /// serialiser output is always an indentation break, never payload.</summary>
    public static string ToLf(this string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}
