namespace FusionRpg.Core.Delve.Quests;

/// <summary>
/// D4.13 (spec-delve-quests.md §8, "Refusals and preflight") — "every refusal is a thrown
/// `QuestRefusal` naming domain, quest and rule — no flag, no fallback, no empty offer" (spec,
/// verbatim). A single exception type (matching `DungeonRegistryRejection`/`EncounterRefusal`'s own
/// "one type per module" convention) rather than a private reason enum, since every caller already
/// wants the domain/quest/rule triple in the message, not a code to switch on.
/// </summary>
public sealed class QuestRefusal : Exception
{
    public string DomainId { get; }
    public string QuestId { get; }
    public string Rule { get; }

    public QuestRefusal(string domainId, string questId, string rule, string detail)
        : base($"{domainId}/{questId}: {rule} -- {detail}")
    {
        DomainId = domainId;
        QuestId = questId;
        Rule = rule;
    }
}
