namespace FusionRpg.Core.Delve.Attrition;

/// <summary>
/// `delve-attrition` D2.21 (spec-delve-attrition.md §8) — the delve's own wipe check.
///
/// <para><b>The settlement DECISION moved out on 2026-09-17</b> (`solid-remediation` T4.8, D10):
/// <see cref="Battle.Attrition.SettlementOutcome"/>, <see cref="Battle.Attrition.MemberSettlement"/>
/// and the per-member Retire/Recover/Roster rule are engine vocabulary now
/// (<see cref="Battle.Attrition.MemberSettlementRules.Decide"/>), because whether a downed member is
/// rostered, recovering or retired is a question every mode asks — leaving it here meant every other
/// mode either had no answer or would grow a second one.</para>
///
/// <para>These two stayed, deliberately: they read <see cref="DelveMemberState"/>, a delve party
/// shape. Promoting them would drag a mode's vocabulary into the engine, which is D10's own defect
/// pointing the other way. The delve's difficulty ladder is likewise untouched — `permadeathApplies`
/// was always resolved by the caller, which is exactly what made the decision portable.</para>
/// </summary>
public static class ExtractionSettlement
{
    /// <summary>A party stands while one member has <c>hp &gt; 0</c> and is not <see cref="DelveMemberState.Downed"/>
    /// (spec §8, verbatim). A member missing the <c>"hp"</c> key is a caller defect, not a "not
    /// standing" case — <see cref="DelveMemberState.Pools"/> is documented as carrying all six ids.</summary>
    public static bool PartyStands(IReadOnlyList<DelveMemberState> party)
    {
        if (party is null) throw new ArgumentNullException(nameof(party));

        foreach (var member in party)
        {
            if (!member.Pools.TryGetValue("hp", out var hp))
                throw new ArgumentException($"member '{member.InstanceId}' has no 'hp' pool.", nameof(party));
            if (hp > 0 && !member.Downed)
                return true;
        }

        return false;
    }

    /// <summary>The raid is wiped when every one of its parties has no standing member (spec §8:
    /// "When every party of the raid has no standing member: CloseDelve(Wiped)"). A raid with zero
    /// parties is a caller defect, not vacuously wiped or vacuously not — reject it rather than guess.</summary>
    public static bool IsWiped(IReadOnlyList<IReadOnlyList<DelveMemberState>> parties)
    {
        if (parties is null) throw new ArgumentNullException(nameof(parties));
        if (parties.Count == 0) throw new ArgumentException("a raid has at least one party.", nameof(parties));

        foreach (var party in parties)
            if (PartyStands(party))
                return false;

        return true;
    }
}
