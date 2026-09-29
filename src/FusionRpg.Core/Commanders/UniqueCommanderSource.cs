using FusionRpg.Core.Saves;

namespace FusionRpg.Core.Commanders;

/// <summary>The role rows a <see cref="UniqueCommanderSource"/> reads, supplied by the host — Core never
/// touches a store (`DataCommanderDirectory`'s own rule), and `rpg_commander_role` is a Data table.
///
/// <para>Three reads, no more: which empire an instance holds the role for, which instances hold it for
/// an empire, and what to call one. The <b>owner</b> read is what makes resolution possible without a
/// second identity: a creature belongs to exactly one (save, empire), so its binding answers both.</para>
/// </summary>
public interface ICommanderRoleReader
{
    /// <summary>The empire this instance holds the commander role for, or <c>null</c> when it holds none
    /// (or does not exist). One answer per instance: a creature has one owner.</summary>
    EmpireRef? RoleEmpireOf(string instanceId);

    /// <summary>The instance ids holding the role for <paramref name="empire"/>, ordinal.</summary>
    IReadOnlyList<string> RoleInstanceIds(EmpireRef empire);

    /// <summary>The creature's display label for the roster: its nickname when set, else its species'
    /// display name. <c>null</c> when the instance is unknown — the source then falls back to the stable
    /// id's own stem rather than inventing a name.</summary>
    string? RoleHolderDisplayName(string instanceId);
}

/// <summary>`commander-roster` EP3.2 — the second directory <b>source</b>: a creature that holds the role
/// is a commander, with no code change per creature (the owner's ruling *"a commander literally a unique
/// demon, it only carry more role"*, R-C2).
///
/// <para>It extends the directory and never edits it: the authored rows stay the defaults and the
/// fallbacks, and this source only answers for `commander:unique:{instanceId}` stable ids. Every answer
/// comes from data — the role binding, the creature's own name, and the authored default for the R4
/// scope key — so there is no `switch` over <see cref="CommanderRef"/> or <see cref="EmpireId"/>
/// anywhere (guarded by `scripts/guard-open-identity.ps1`).</para>
/// </summary>
public sealed class UniqueCommanderSource : ICommanderRoster
{
    /// <summary>The stable-id prefix for a role-holding creature. It keeps the `commander:` prefix the
    /// authored rows use, so it can never collide with a faction id or a battle key
    /// (<c>commander-identity</c>'s own rule).</summary>
    public const string UniquePrefix = "commander:unique:";

    readonly ICommanderDirectory _authored;
    readonly ICommanderRoleReader _roles;

    /// <param name="authored">The shipped directory: it still answers `EmpireOf`, `DisplayName` and
    /// `AllocationScopeKey` for the authored rows, and it is the authority for R4's scope key — a
    /// creature commander's `AllocationScopeKey` IS the empire's default commander's key, because both
    /// apply and a leading creature adds only its aura.</param>
    public UniqueCommanderSource(ICommanderDirectory authored, ICommanderRoleReader roles)
    {
        _authored = authored ?? throw new ArgumentNullException(nameof(authored));
        _roles = roles ?? throw new ArgumentNullException(nameof(roles));
    }

    /// <summary>Does this stable id name a role-holding creature? False for a blank id, for any id
    /// without the prefix, and for a creature that holds no role.</summary>
    public bool TryResolve(string? stableId, out CommanderRef commander)
    {
        commander = default;
        if (string.IsNullOrWhiteSpace(stableId)) return false;
        var trimmed = stableId.Trim();
        if (!trimmed.StartsWith(UniquePrefix, StringComparison.Ordinal)) return false;

        var instanceId = trimmed[UniquePrefix.Length..];
        if (instanceId.Length == 0) return false;
        if (_roles.RoleEmpireOf(instanceId) is null) return false;

        commander = new CommanderRef(trimmed);
        return true;
    }

    /// <summary>The empire the creature's binding names.</summary>
    public EmpireId EmpireOf(CommanderRef commander)
    {
        var empire = _roles.RoleEmpireOf(InstanceOf(commander));
        if (empire is null)
            throw new ArgumentException(
                $"'{commander.StableId}' is not a role-holding creature — resolve before asking its empire",
                nameof(commander));
        return empire.Value.Empire;
    }

    /// <summary>The creature's own name (nickname, else its species' display name). The caller's
    /// <paramref name="playerName"/> is irrelevant here — that rule belongs to the authored rows
    /// (`displayFromPlayer`); a creature commander is never the player. Falls back to the stable id's
    /// stem only when the creature's name genuinely cannot be read, so a roster row is never blank.</summary>
    public string DisplayName(CommanderRef commander, string? playerName)
    {
        var instanceId = InstanceOf(commander);
        return _roles.RoleHolderDisplayName(instanceId) ?? instanceId;
    }

    /// <summary>R4: both apply, so a creature commander's scope key stays the empire's DEFAULT
    /// commander's key. Swapping commanders never changes the army's aptitudes.</summary>
    public string AllocationScopeKey(CommanderRef commander, long playerId) =>
        _authored.AllocationScopeKey(_authored.DefaultFor(EmpireOf(commander)), playerId);

    /// <summary>Just the role-holding creatures of an empire, ordinal by stable id. The directory's own
    /// <c>ForEmpire</c> composes this after its authored rows, which is why it is its own method: the
    /// composition must not re-enter the directory that called it.</summary>
    public IReadOnlyList<CommanderRef> RoleHoldersForEmpire(EmpireRef empire) =>
        _roles.RoleInstanceIds(empire)
            .Select(instanceId => new CommanderRef(UniquePrefix + instanceId))
            .OrderBy(r => r.StableId, StringComparer.Ordinal)
            .ToList();

    /// <summary>The authored default(s) for the empire, then its role-holding creatures — so a caller
    /// holding only this source still sees a full roster, and the ordering does not depend on the order
    /// the rows came back in.</summary>
    public IReadOnlyList<CommanderRef> ForEmpire(EmpireRef empire)
    {
        var roster = new List<CommanderRef>(
            _authored is ICommanderRoster authoredRoster
                ? authoredRoster.ForEmpire(empire)
                : Array.Empty<CommanderRef>());
        foreach (var holder in RoleHoldersForEmpire(empire))
        {
            if (roster.All(r => !string.Equals(r.StableId, holder.StableId, StringComparison.Ordinal)))
                roster.Add(holder);
        }
        return roster.OrderBy(r => r.StableId, StringComparer.Ordinal).ToList();
    }

    static string InstanceOf(CommanderRef commander)
    {
        var stableId = commander.StableId ?? "";
        return stableId.StartsWith(UniquePrefix, StringComparison.Ordinal)
            ? stableId[UniquePrefix.Length..]
            : "";
    }
}
