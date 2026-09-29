namespace FusionRpg.Core.Creatures;

/// <summary>
/// Trait identity (spec-creature-core.md): stored ids validated here; the grant-template wiring
/// into live Effect grants belongs to the deploy-facing modules, not V1.
/// </summary>
public sealed record CreatureTraitDef(string TraitId, string Name, string GrantTemplateId, string Blurb);

public static class CreatureTraitCatalog
{
    public static readonly IReadOnlyList<CreatureTraitDef> All = new CreatureTraitDef[]
    {
        // Combat traits (vision §8)
        new("berserker", "Berserker", "trait.berserker", "Hits harder as its own health falls."),
        new("regenerator", "Regenerator", "trait.regenerator", "Slowly recovers health over time."),
        new("soul-eater", "Soul Eater", "trait.soul-eater", "Feeds on fallen enemies."),
        new("critical-hunter", "Critical Hunter", "trait.critical-hunter", "Strikes weak points more often."),
        new("guardian", "Guardian", "trait.guardian", "Shields nearby allies."),
        new("swift", "Swift", "trait.swift", "Acts faster than its kin."),
        new("immortal", "Immortal", "trait.immortal", "Refuses the first death."),
        // Personality traits (identity now; behavior hooks later)
        new("loyal", "Loyal", "trait.loyal", "Stands by its summoner."),
        new("greedy", "Greedy", "trait.greedy", "Chases the richest prize."),
        new("bloodthirsty", "Bloodthirsty", "trait.bloodthirsty", "Seeks the fight."),
        new("coward", "Coward", "trait.coward", "Prefers to survive."),
        new("genius", "Genius", "trait.genius", "Learns quickly."),
        // Rare celestial/infernal essences (vision's void/chaos land here — traits, never elements)
        new("void-touched", "Void-Touched", "trait.void-touched", "Carries a sliver of the void."),
        new("chaos-marked", "Chaos-Marked", "trait.chaos-marked", "Bears the mark of chaos.")
    };

    static readonly Dictionary<string, CreatureTraitDef> ById =
        All.ToDictionary(t => t.TraitId, StringComparer.Ordinal);

    public static bool IsKnown(string? traitId) => traitId != null && ById.ContainsKey(traitId);

    public static CreatureTraitDef Get(string traitId) =>
        ById.TryGetValue(traitId, out var def)
            ? def
            : throw new ArgumentException($"Unknown creature trait id '{traitId}'.");
}
