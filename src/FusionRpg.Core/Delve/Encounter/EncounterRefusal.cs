namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// `encounter-generator` D2.1/D2.7 (spec-encounter-generator.md §8) — every refusal is thrown, naming
/// the slot and the filter that could not be satisfied — no flag, no fallback. Built as part of D2.1
/// (the first task that needed something to throw); moved to its own file here, D2.7's own, since
/// D2.7 is the task that names it as a first-class deliverable rather than a `SlotFilter.cs` detail.
///
/// <para><b>Carries the slot, not an encounter id.</b> §8's own text says a refusal names "the
/// encounter id, slot and filter" — but no type in this module carries an encounter id at all
/// (`EncounterAnchor` has none; `Encounter.Build`'s signature takes no id parameter either), so there
/// is nothing to attach here without inventing an id this module does not otherwise track. The reason
/// string already carries the species id and the concrete filter that failed (`SlotFilter.Candidates`'s
/// own messages), which is enough to locate the failure without a redundant field.</para>
/// </summary>
public sealed class EncounterRefusal : Exception
{
    public EncounterSlot Slot { get; }

    public EncounterRefusal(EncounterSlot slot, string reason) : base(Format(slot, reason))
    {
        Slot = slot ?? throw new ArgumentNullException(nameof(slot));
    }

    static string Format(EncounterSlot slot, string reason) =>
        $"slot[posture={slot?.Posture}, reach={Describe(slot?.Reach)}, " +
        $"targetPreference={Describe(slot?.TargetPreference)}]: {reason}";

    static string Describe<T>(T? value) where T : struct => value?.ToString() ?? "any";
}
