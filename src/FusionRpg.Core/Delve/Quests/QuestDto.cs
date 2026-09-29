namespace FusionRpg.Core.Delve.Quests;

/// <summary>
/// D4.14 (spec-delve-quests.md §Interface, "`QuestDto` (template name, flavour, `have / need`, done —
/// no `Θ`, rung id or `PartyIndex`)") — the player-facing projection `delve-stage`'s tracker reads.
/// `Name`/`Flavor` arrive as plain caller-supplied strings rather than living on `QuestRow` itself —
/// D4.9 deliberately left the anchor's own free-text fields off that type ("left for whichever later
/// task actually reads them"); this is that task, and it takes them as parameters rather than
/// retrofitting the already-tested catalog row.
/// </summary>
public sealed record QuestDto(string Name, string Flavor, int Have, int Need, bool Done);

public static class QuestDtoProjection
{
    public static QuestDto Project(string name, string flavor, int have, int need, bool done) =>
        new(name, flavor, have, need, done);
}
