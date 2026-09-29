namespace FusionRpg.Core.Delve.Wild;

/// <summary>
/// D4.4 (spec-wild-room.md §8, "Remembers") — the shift a species' own most recent wild-talk
/// resolution earns on its next encounter. **This delve's own log only**: the spec is explicit and
/// verbatim — "a pure read over THIS delve's `decisions_json` talk rows… No table: the log is the
/// memory and ends with the delve." There is no cross-delve table, column, or store write anywhere
/// in this module; a fresh delve's decision log starts empty, and `For` returns "no opinion" (0)
/// whenever nothing about the species is in the rows it was handed.
///
/// <para>Deliberately takes a minimal, three-field local row (<see cref="WildTalkLogRow"/>) rather
/// than a full decision/payload type: `DelveDecision`/`PartyFacts`-shaped types do not exist
/// anywhere in this tree (confirmed while building D3.26 `SupplyUse`, still true here), so this file
/// models only the three fields it actually reads rather than guessing at the rest of that shape.
/// </para>
/// </summary>
public static class WildMemory
{
    public const string ResolutionJoins = "joins";
    public const string ResolutionFight = "fight";
    public const string ResolutionAttacks = "attacks";
    public const string ResolutionLeave = "leave";
    public const string ResolutionFlees = "flees";
    public const string ResolutionTakesLeaves = "takesLeaves";

    /// <summary>The three fields `WildMemory.For` reads off a decision log row: which surface it was
    /// logged under (§8 reads only `surface: wild` rows), which species it names, and how the talk
    /// resolved — one of the six <c>Resolution*</c> constants above (a terminal verb, `fight`/`leave`,
    /// or a drawn outcome, `joins`/`takesLeaves`/`flees`/`attacks`).</summary>
    public sealed record WildTalkLogRow(string Surface, string SpeciesId, string Resolution);

    const string WildSurface = "wild";

    /// <summary>
    /// The most recent `wild`-surface row naming <paramref name="speciesId"/> decides (spec,
    /// verbatim): `joins` → −1 (toward `eager`); `fight`/`attacks` (a betrayal, whether direct or
    /// drawn) → +1; `leave`/`flees`/`takesLeaves` → 0. No row for this species → 0, no opinion.
    /// Exactly one band's worth of shift, whatever the count — the exemption this file carries for
    /// that structural clamp.
    /// </summary>
    public static int For(IReadOnlyList<WildTalkLogRow> decisions, string speciesId)
    {
        if (decisions is null) throw new ArgumentNullException(nameof(decisions));
        if (string.IsNullOrWhiteSpace(speciesId)) throw new ArgumentException("speciesId required", nameof(speciesId));

        for (var i = decisions.Count - 1; i >= 0; i--)
        {
            var row = decisions[i];
            if (row.Surface != WildSurface || row.SpeciesId != speciesId) continue;

            return row.Resolution switch
            {
                ResolutionJoins => -1,
                ResolutionFight or ResolutionAttacks => 1,
                ResolutionLeave or ResolutionFlees or ResolutionTakesLeaves => 0,
                _ => throw new ArgumentException($"Unknown wild talk resolution '{row.Resolution}'.", nameof(decisions)),
            };
        }

        return 0; // nothing on record for this species -- no opinion, not a guess
    }
}
