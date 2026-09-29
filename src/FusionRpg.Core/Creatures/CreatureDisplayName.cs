namespace FusionRpg.Core.Creatures;

/// <summary>How a creature is named wherever one is shown: its nickname when set, else its species'
/// display name, else nothing.
///
/// <para><b>One declaring site.</b> `UniqueActorHubCompose.ProjectSheet` (the actor sheet) and
/// `commander-roster`'s `ICommanderRoleReader` (the roster) both call this, so a creature cannot be
/// named one way in a sheet and another in a commander list. The species half reads
/// <see cref="CreatureSpeciesCatalog"/>, which the host configures from the committed species tree —
/// an unconfigured catalog yields no name rather than a guessed one.</para>
/// </summary>
public static class CreatureDisplayName
{
    public static string? For(string? nickname, string? speciesId)
    {
        if (!string.IsNullOrWhiteSpace(nickname)) return nickname;
        if (string.IsNullOrWhiteSpace(speciesId)) return null;
        return CreatureSpeciesCatalog.IsConfigured && CreatureSpeciesCatalog.IsKnown(speciesId)
            ? CreatureSpeciesCatalog.Get(speciesId).Name
            : null;
    }
}
