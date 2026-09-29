namespace FusionRpg.Core.Delve;

/// <summary>
/// The two delve world-id shapes (spec-delve-scope.md §7, decision 15): a <c>many</c> domain is a
/// standing sub-world, one row per (player, domain), replaced under a fresh seed on each entry; a
/// <c>once</c> domain is one row per delve, archived at close. Pure string functions — no store
/// access, so a caller can compute the id before deciding whether the row already exists.
/// </summary>
public static class DelveWorldIds
{
    public static string ForManyDomain(string domainId, long playerId) => $"delve-{domainId}-p{playerId}";

    public static string ForOnceDelve(string domainId, long delveId) => $"delve-{domainId}-{delveId}";
}
