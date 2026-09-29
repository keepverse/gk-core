using FusionRpg.Core.Actions.Seeding;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>
/// D4.20 (spec-domain-catalog.md §5b) — the two DETERMINISTIC discovery rules. (i) is the expedition
/// `FoundDomain` tick's own pick, kept here as a pure function even though nothing calls it yet: the
/// tick kind itself (`ExpeditionTickKinds`, `ExpeditionResolver.cs:7-14`) does not exist today, and
/// adding one is an explicit ask on `expeditions` per this module's own spec text, not a decision to
/// make unilaterally from inside `domain-catalog`. (ii) is climate/band-ordinal reveal, real and
/// testable today even though it is inert on the first-ship corpus (six `shallow` domains, one per
/// climate, no `mid` band exists yet to reveal into).
/// </summary>
public static class DomainDiscovery
{
    /// <summary>Equal-weight over every unfound `shallow` domain, ordinal order first so the same
    /// SET picks the same domain regardless of the caller's own read order — the `QuestOffer.Draw`
    /// idiom (D4.10) applied to a single pick instead of a per-slot sequence. The bare inline `1`
    /// (not a named constant) matches `QuestOffer.cs:129`'s own identical "equal weights" idiom
    /// verbatim — a NAMED constant containing "weight" trips the magic-number audit's own balance-
    /// vocabulary heuristic (M2) even though no balance pass could ever tune this value: every
    /// candidate shares it, so the selection odds are identical whatever positive number it is.</summary>
    public static string Pick(IReadOnlyList<string> unfoundShallow, long tickSeed)
    {
        if (unfoundShallow is null) throw new ArgumentNullException(nameof(unfoundShallow));

        var options = unfoundShallow
            .OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => new WeightedOption<string>(id, 1)) // equal weights, QuestOffer.cs:129's own idiom
            .ToList();
        return WeightedChoice.Pick(options, tickSeed, "domain.discovery");
    }

    /// <summary>(ii), verbatim: "a clear at any rung of a domain at band ordinal b in climate c
    /// reveals the domains at b + 1 in c." No RNG — deterministic on the catalog's own already-loaded
    /// band ordinals (D4.15). Every already-found domain among the result is a harmless no-op at the
    /// caller's own <c>RecordFoundUnlocked</c> (first-wins, D4.18) — this function does not need to
    /// know which domains are already found.</summary>
    public static IReadOnlyList<string> RevealedByClear(
        DomainRow clearedDomain, IReadOnlyList<DomainRow> allDomains, DomainCatalog catalog)
    {
        if (clearedDomain is null) throw new ArgumentNullException(nameof(clearedDomain));
        if (allDomains is null) throw new ArgumentNullException(nameof(allDomains));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        var clearedOrdinal = catalog.DangerBandOrdinalFor(clearedDomain.DomainId);
        return allDomains
            .Where(d => string.Equals(d.Climate, clearedDomain.Climate, StringComparison.Ordinal))
            .Where(d => catalog.DangerBandOrdinalFor(d.DomainId) == clearedOrdinal + 1)
            .Select(d => d.DomainId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }
}
