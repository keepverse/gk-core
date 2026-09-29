using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items.Power;

namespace FusionRpg.Data;

/// <summary>
/// Module 9 (`item-power-reads`) at the DAL boundary: the seeded halves of
/// <c>power_ceiling(rung) = pinAE × ladderShareMilli(rung) / 1000</c> live in SQL, the arithmetic
/// lives in <see cref="RarityPowerCeilings"/>, and this partial is the join.
///
/// <para>⭐ <b>This is the rarity-keyed budget check's first production caller.</b> Until it landed,
/// <c>ContentValidation.Budget</c>'s rarity overload had zero — the only call in the tree
/// (<c>RpgStore.BuildActionCatalog</c>) uses the rung-keyed sibling — so
/// <c>ContentValidation.cs:73</c>'s <c>if (ceilingFor(...) is not { } ceiling) continue;</c> was
/// unreachable rather than merely unsatisfied, and the check could only ever report green over zero
/// containers. Same shape as that sibling: enumerate what the store holds, price it through
/// <see cref="ActorPowerCache.Compose"/>, and return findings that NAME the container.</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>
    /// Module 9's <c>ceilingFor</c>, bound to this store's own seeded rows: the ladder from
    /// <see cref="ListRarities"/>, the ‰ column from <c>rarity_budget.power_ceiling</c>
    /// (<see cref="GetRarityBudget"/>), the coefficients from <see cref="GetPowerTables"/>.
    /// </summary>
    public RarityPowerCeilings GetRarityPowerCeilings()
    {
        var ladder = ListRarities();
        // Read the whole column once rather than per lookup: Build asks for every rung exactly once,
        // and a per-call query would open one connection per rung for a value that cannot change
        // inside the call.
        var shares = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rung in ladder)
            if (GetRarityBudget(rung.RarityId, RarityPowerCeilings.BudgetKey) is { } share)
                shares[rung.RarityId] = share;

        return RarityPowerCeilings.Build(
            ladder,
            id => shares.TryGetValue(id, out var v) ? v : null,
            GetPowerTables());
    }

    /// <summary>
    /// Container ids that actually name a rarity. <c>effect_container.rarity</c> is nullable and most
    /// containers (skills, traits, migrated fx) carry none, so the budget check would otherwise walk
    /// the whole table to skip nearly all of it — and this is also the honest denominator for
    /// "how much content is this check even about".
    /// </summary>
    public IReadOnlyList<string> ListContainerIdsWithRarity()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText =
                "SELECT container_id FROM effect_container " +
                "WHERE rarity IS NOT NULL AND rarity <> '' ORDER BY container_id;";
            using var r = cmd.ExecuteReader();

            var list = new List<string>();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>
    /// D11/R1's budget lint over the stored content: every rarity-bearing container priced against
    /// its own rung's seeded ceiling.
    ///
    /// <para><b>A finding, never a refusal and never a generation input</b> —
    /// <c>ContentValidation</c>'s own standing rule, repeated here because this is the call site that
    /// could most easily be turned into a gate by accident. The caller logs the report; nothing
    /// clamps, nothing drops, nothing declines to import.</para>
    ///
    /// <para><see cref="ContentReport.Evaluated"/> is the number that matters on a green run: it was
    /// structurally 0 before this method existed.</para>
    ///
    /// <para>⚠ <b>Cost scales with the rarity-bearing population, which is ONE container today</b>
    /// (`item.first-clear-almanac-seed`, the only one in the shipped seed tree that names a rarity).
    /// Its caller runs it once at boot. If generated item instances ever land in
    /// <c>effect_container</c> with a rarity in the thousands, this becomes a per-boot table walk and
    /// wants moving behind the same kind of flag <c>--validate</c> is for — recorded here rather than
    /// pre-solved, because a guard sized for a population nobody has measured is its own defect.</para>
    /// </summary>
    public ContentReport ValidateRarityPowerBudget()
    {
        var ceilings = GetRarityPowerCeilings();

        var containers = new List<ContainerRow>();
        foreach (var id in ListContainerIdsWithRarity())
            if (GetContainer(id) is { } container)
                containers.Add(container);

        // Resolved once per container up front, so `atomsOf` is a dictionary read rather than a query
        // inside the validator's own loop (the same reason BuildActionCatalog hoists its atom read).
        var atoms = new Dictionary<string, IReadOnlyList<AtomRow>>(StringComparer.Ordinal);
        foreach (var container in containers)
            atoms[container.ContainerId] = container.Atoms
                .Select(a => GetAtom(a.AtomId))
                .Where(a => a is not null)
                .Select(a => a!)
                .ToList();

        return ContentValidation.Budget(
            containers,
            id => atoms.TryGetValue(id, out var list) ? list : Array.Empty<AtomRow>(),
            ceilings.CeilingFor);
    }
}
