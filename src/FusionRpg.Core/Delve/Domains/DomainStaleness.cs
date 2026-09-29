namespace FusionRpg.Core.Delve.Domains;

/// <summary>Never a third state — spec §3's own comparison is binary ("unequal ⇒ Stale").</summary>
public enum Staleness { Fresh, Stale }

/// <summary>
/// Every fact `validated_json` records at import time (spec-domain-catalog.md §3, verbatim:
/// "{registryVersions, dungeonTuningHash, encounterTuningHash, catalogRevision, dropTableRevision,
/// sampleSeeds}") — the inputs of every §2 preflight row, nothing else. The SAME shape is recomputed
/// from the live hubs at read time; <see cref="DomainStaleness.Of"/> compares the two verbatim.
/// </summary>
public sealed record DomainValidatedFacts(
    IReadOnlyDictionary<string, long> RegistryVersions,
    string DungeonTuningHash,
    string EncounterTuningHash,
    long CatalogRevision,
    long DropTableRevision,
    int SampleSeeds);

/// <summary>
/// D4.18 (spec-domain-catalog.md §3) — "staleness is a comparison, never a clock: `DomainStaleness.Of
/// (row, live)` compares `validated_json` with the registries, tuning hashes and revisions the hubs
/// hold; unequal ⇒ `Stale`." No parameter here is a timestamp and nothing in this file reads one —
/// two calls with the same two <see cref="DomainValidatedFacts"/> return the same <see cref="Staleness"/>
/// regardless of how much real time passed between them, by construction, not by convention.
/// </summary>
public static class DomainStaleness
{
    public static Staleness Of(DomainValidatedFacts recorded, DomainValidatedFacts live)
    {
        if (recorded is null) throw new ArgumentNullException(nameof(recorded));
        if (live is null) throw new ArgumentNullException(nameof(live));

        if (!string.Equals(recorded.DungeonTuningHash, live.DungeonTuningHash, StringComparison.Ordinal)) return Staleness.Stale;
        if (!string.Equals(recorded.EncounterTuningHash, live.EncounterTuningHash, StringComparison.Ordinal)) return Staleness.Stale;
        if (recorded.CatalogRevision != live.CatalogRevision) return Staleness.Stale;
        if (recorded.DropTableRevision != live.DropTableRevision) return Staleness.Stale;
        if (recorded.SampleSeeds != live.SampleSeeds) return Staleness.Stale;
        if (!RegistryVersionsMatch(recorded.RegistryVersions, live.RegistryVersions)) return Staleness.Stale;

        return Staleness.Fresh;
    }

    /// <summary>Content equality, not reference or ordering — a dictionary re-serialized through JSON
    /// makes no promise about key order, so two dictionaries with the same entries in a different
    /// order must compare equal.</summary>
    static bool RegistryVersionsMatch(IReadOnlyDictionary<string, long> recorded, IReadOnlyDictionary<string, long> live)
    {
        if (recorded.Count != live.Count) return false;
        foreach (var (key, version) in recorded)
        {
            if (!live.TryGetValue(key, out var liveVersion) || liveVersion != version) return false;
        }
        return true;
    }
}
