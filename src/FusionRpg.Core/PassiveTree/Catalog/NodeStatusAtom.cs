namespace FusionRpg.Core.PassiveTree.Catalog;

/// <summary>
/// One node's granted <c>status.apply</c> atom, carried exactly as the row says it — a status id,
/// a duration in ms, a level, a grant chance in per-mille, and the trigger that fires it. This is
/// deliberately NOT a <see cref="NodeAtom"/>: magnitudes live there (channel/op/kMicro/axis), and
/// forcing a channelless status row into that shape would fabricate a channel, an op, and a
/// coefficient it never authored. The magnitude resolve path (<c>TreeAtomSource</c>) filters on
/// <c>stat.derived</c> and never sees this list; execution belongs to mechanism-wiring, which reads
/// this list when its executor lands. Carried, stored, countable — never priced, never executed here.
/// </summary>
public sealed record NodeStatusAtom(
    string StatusId,
    long DurationMs,
    int Level,
    long ChancePermille,
    string? Trigger,
    string? WhenJson);
