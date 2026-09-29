using FusionRpg.Core.Activity;
using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.Onboarding;

public static class OnboardingCheckpointIds
{
    public const string FirstWinDave = "first-win-dave";
    public const string Level3GeneralSpecies = "level-3-general-species";
    public const string Level4DaveEquipment = "level-4-dave-equipment";

    public static readonly IReadOnlyList<string> Ordered =
        new[] { FirstWinDave, Level3GeneralSpecies, Level4DaveEquipment };
}

/// <summary>Minimal source evidence needed to decide the level-3 onboarding checkpoint.</summary>
public readonly record struct OnboardingSourceEvidence(
    string FactKind,
    string? SourceKind,
    string? SourceId,
    int? TypeId = null);

/// <summary>Durable state and the settled result being evaluated for one player.</summary>
public sealed record OnboardingEligibilityInput(
    long PlayerLevel,
    bool IsPvzGame,
    string? MatchResult,
    bool Settled,
    bool FirstWinEarned,
    bool SpeciesEarned,
    bool EquipmentEarned,
    IReadOnlyList<OnboardingSourceEvidence> Sources);

public sealed record OnboardingEligibilityResult(
    IReadOnlyList<string> NewlyEligible,
    string? Reason);

/// <summary>
/// Pure ordering/evidence gate for the first-session sequence. It decides eligibility only; Data owns
/// reward writes and the checkpoint ledger.
/// </summary>
public static class OnboardingCheckpointEvaluator
{
    public static OnboardingEligibilityResult Evaluate(OnboardingEligibilityInput input)
    {
        if (!input.IsPvzGame)
            return Refuse("onboarding.not-pvz");

        var newly = new List<string>(capacity: 3);
        var victory = input.Settled
            && PvzActivityKinds.NormalizeMatchResult(input.MatchResult) == "victory";

        var firstWinSatisfied = input.FirstWinEarned;
        if (!input.FirstWinEarned && victory)
        {
            newly.Add(OnboardingCheckpointIds.FirstWinDave);
            firstWinSatisfied = true;
        }

        var generalEvidence = HasGeneralEvidence(input.Sources);
        var speciesSatisfied = input.SpeciesEarned;
        if (!input.SpeciesEarned && firstWinSatisfied && input.PlayerLevel >= 3 && generalEvidence)
        {
            newly.Add(OnboardingCheckpointIds.Level3GeneralSpecies);
            speciesSatisfied = true;
        }

        if (!input.EquipmentEarned && firstWinSatisfied && speciesSatisfied && input.PlayerLevel >= 4)
            newly.Add(OnboardingCheckpointIds.Level4DaveEquipment);

        if (newly.Count == 0 && input.PlayerLevel >= 3 && firstWinSatisfied && !generalEvidence)
            return Refuse("onboarding.general-source-required");
        if (newly.Count == 0 && !victory && !input.FirstWinEarned)
            return Refuse("onboarding.victory-required");

        return new OnboardingEligibilityResult(newly, null);
    }

    static bool HasGeneralEvidence(IReadOnlyList<OnboardingSourceEvidence> sources)
    {
        foreach (var evidence in sources)
        {
            if (evidence.FactKind is not (PvzActivityKinds.PlantPlaced or PvzActivityKinds.ZombieSpawned))
                continue;
            if (!string.Equals(evidence.SourceKind, CreatureProgressionSource.EmpireGeneralKind,
                    StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(evidence.SourceId))
                continue;

            try
            {
                if (CreatureProgressionSource.Parse(evidence.SourceKind!, evidence.SourceId!)
                    is CreatureProgressionSource.EmpireGeneralSource)
                    return true;
            }
            catch (FormatException) { }
            catch (ArgumentException) { }
        }

        return false;
    }

    static OnboardingEligibilityResult Refuse(string reason) =>
        new(Array.Empty<string>(), reason);
}
