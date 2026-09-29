using System.Text;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Display;

/// <summary>G3 §4.4's closed vocabulary — where a rendered line came from.</summary>
public enum SourceKind
{
    Base, Implicit, AffixPrefix, AffixSuffix, Enhancement, SocketInsert, Resonance,
    Word, SetThreshold, GrantedAction, UniqueIdentity, UniqueVariance,
}

/// <summary>A roll-quality bar — only ever produced for `RollPolicy.OnInstantiate` (spec's own rule:
/// `Fixed` would lie about luck with a full bar; `OnApply` shows the band, not a bar).</summary>
public readonly record struct RollBar(int Segments)
{
    public const int MaxSegments = 5;
}

/// <summary>
/// One rendered magnitude. `Key`/`Args` are the ONLY human-readable leaf shape (Boundaries: "every
/// human-readable leaf is {key, args}") — never a glued string. `Args` carries the frozen numbers a
/// template's placeholders need; the renderer (module 20) formats them, this layer never emits markup.
/// </summary>
/// <param name="Unit">
/// <b>Nullable, and the null is load-bearing.</b> A magnitude line always carries its unit — that is
/// the whole point of the lane. A STRUCTURAL line (the header, a requirement clause, a set name, the
/// footer) carries no magnitude and therefore has no unit, and naming one anyway would be the exact
/// lie SC4 exists to prevent: <c>DominancePresentation.GroupByUnitClass</c> already puts an
/// unresolvable unit in <b>its own</b> group rather than folding it into <c>GameUnits</c>, for the
/// same reason. <c>ItemDisplayRenderer.Line</c> still requires a concrete unit — there is no way to
/// produce an unlabelled magnitude.
/// </param>
/// <param name="SourceKind">
/// Nullable for the same reason, and it is what keeps G3 §4.4's twelve-value vocabulary <b>closed</b>:
/// a header or footer line is not an atom line and declares no source kind, rather than forcing a
/// thirteenth member for "not from an atom".
/// </param>
public readonly record struct DisplayLine(
    string Key, IReadOnlyDictionary<string, string> Args, UnitClass? Unit, SourceKind? SourceKind, int GroupOrder,
    RollBar? RollBar = null, string? ContextRead = null, int? RollQualityPerMille = null);

/// <summary>One of the card's eleven ordered sections (G3 §4.1) — order and contents unchanged here,
/// this module only produces the lines inside each.</summary>
public readonly record struct DisplayBlock(string BlockKey, IReadOnlyList<DisplayLine> Lines);

/// <summary>The whole card — an ordered tree, never markup.</summary>
public readonly record struct DisplayModel(IReadOnlyList<DisplayBlock> Blocks)
{
    /// <summary>
    /// Every line in the model, flattened in block order then line order. The index into this
    /// sequence is what <see cref="CompareModel.DifferingLineIndexes"/> points at, so "line 7 differs"
    /// means one thing and not two.
    /// </summary>
    public IEnumerable<DisplayLine> Lines => Blocks.SelectMany(b => b.Lines);

    /// <summary>
    /// The byte-identity `spec-item-card.md:302` claims over one
    /// <c>(container_id, catalog_revision, roll_seed)</c>. Args are emitted in ORDINAL KEY ORDER
    /// rather than in insertion order: a dictionary's enumeration order is an implementation detail,
    /// and a determinism claim that depends on one is not a determinism claim.
    /// </summary>
    public string Fingerprint()
    {
        var sb = new StringBuilder();
        foreach (var block in Blocks)
        {
            sb.Append(block.BlockKey).Append('\n');
            foreach (var line in block.Lines)
            {
                sb.Append("  ").Append(line.Key)
                  .Append('|').Append(line.Unit?.ToString() ?? "-")
                  .Append('|').Append(line.SourceKind?.ToString() ?? "-")
                  .Append('|').Append(line.GroupOrder)
                  .Append('|').Append(line.RollBar?.Segments.ToString() ?? "-")
                  .Append('|').Append(line.RollQualityPerMille?.ToString() ?? "-")
                  .Append('|').Append(line.ContextRead ?? "-");
                foreach (var (k, v) in line.Args.OrderBy(a => a.Key, StringComparer.Ordinal))
                    sb.Append('|').Append(k).Append('=').Append(v);
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Two cards, diffed line by line — the same <see cref="DisplayLine"/>s the single-card render
/// produces (comparison diffs RENDERED lines, never a parallel computation), <b>plus</b> I13's own
/// comparison payload, which this model carries rather than recomputes.
///
/// <para>⛔ <b>Nothing here is a second answer.</b> <see cref="Deltas"/>/<see cref="Dominance"/>/
/// <see cref="MeanRollQualityMilli"/> come from <c>ArmouryCompare.Compare</c>; <see cref="Badge"/>,
/// <see cref="Trade"/> and <see cref="UnitGroups"/> come from <c>DominancePresentation</c>. This
/// record is the join, and there is deliberately no synthesized scalar anywhere in it — SC9, and
/// <see cref="FootnoteKey"/> is the permanent, non-dismissible copy that says why.</para>
/// </summary>
/// <param name="DifferingLineIndexes">
/// Indexes into the flattened line sequence (<see cref="DisplayModel.Lines"/>) where the two cards do
/// not render the same thing. Positions past the shorter card's end are differences too — a line the
/// candidate has and the incumbent does not is exactly what a player needs to see.
/// </param>
/// <param name="IncomparableReasonKey">
/// Non-null <b>only</b> for <see cref="DominanceVerdict.Incomparable"/>. §4.2: "an incomparable
/// verdict with no explanation reads as a bug", so the reason is part of the model rather than
/// something a component is trusted to remember to add.
/// </param>
public readonly record struct CompareModel(
    DisplayModel Left,
    DisplayModel Right,
    IReadOnlyList<int> DifferingLineIndexes,
    IReadOnlyList<ChannelDelta> Deltas,
    DominanceVerdict Dominance,
    Surfaces.VerdictBadge Badge,
    Surfaces.SidegradeTrade Trade,
    IReadOnlyList<Surfaces.UnitClassGroup> UnitGroups,
    int MeanRollQualityMilliLeft,
    int MeanRollQualityMilliRight,
    string FootnoteKey,
    string? IncomparableReasonKey);
