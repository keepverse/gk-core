using FusionRpg.Core.Delve.Domains;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>D4.18 (spec-domain-catalog.md §3) — "staleness is a comparison, never a clock." One red
/// test per field `validated_json` records, a green identical-facts case, null-arg guards, and a
/// source-scan proving no clock ever entered the file (the `OfferPricing`/D4.3 idiom applied to a
/// "never reads a clock" claim instead of a "never calls X directly" one).</summary>
public class DomainStalenessTests
{
    static DomainValidatedFacts Facts(
        long catalogRevision = 5, long dropTableRevision = 3, string dungeonHash = "hash-a",
        string encounterHash = "hash-b", int sampleSeeds = 32,
        IReadOnlyDictionary<string, long>? registryVersions = null) =>
        new(
            RegistryVersions: registryVersions ?? new Dictionary<string, long>(StringComparer.Ordinal) { ["rooms"] = 1, ["events"] = 2 },
            DungeonTuningHash: dungeonHash, EncounterTuningHash: encounterHash,
            CatalogRevision: catalogRevision, DropTableRevision: dropTableRevision, SampleSeeds: sampleSeeds);

    [Fact]
    public void Identical_facts_on_both_sides_are_fresh()
    {
        Assert.Equal(Staleness.Fresh, DomainStaleness.Of(Facts(), Facts()));
    }

    [Fact]
    public void A_changed_dungeon_tuning_hash_is_stale()
    {
        Assert.Equal(Staleness.Stale, DomainStaleness.Of(Facts(dungeonHash: "hash-a"), Facts(dungeonHash: "hash-a-v2")));
    }

    [Fact]
    public void A_changed_encounter_tuning_hash_is_stale()
    {
        Assert.Equal(Staleness.Stale, DomainStaleness.Of(Facts(encounterHash: "hash-b"), Facts(encounterHash: "hash-b-v2")));
    }

    [Fact]
    public void A_changed_catalog_revision_is_stale()
    {
        Assert.Equal(Staleness.Stale, DomainStaleness.Of(Facts(catalogRevision: 5), Facts(catalogRevision: 6)));
    }

    [Fact]
    public void A_changed_drop_table_revision_is_stale()
    {
        Assert.Equal(Staleness.Stale, DomainStaleness.Of(Facts(dropTableRevision: 3), Facts(dropTableRevision: 4)));
    }

    [Fact]
    public void A_changed_sample_seeds_is_stale()
    {
        Assert.Equal(Staleness.Stale, DomainStaleness.Of(Facts(sampleSeeds: 32), Facts(sampleSeeds: 64)));
    }

    [Fact]
    public void A_changed_registry_version_value_is_stale()
    {
        var recorded = Facts(registryVersions: new Dictionary<string, long>(StringComparer.Ordinal) { ["rooms"] = 1 });
        var live = Facts(registryVersions: new Dictionary<string, long>(StringComparer.Ordinal) { ["rooms"] = 2 });
        Assert.Equal(Staleness.Stale, DomainStaleness.Of(recorded, live));
    }

    [Fact]
    public void A_removed_registry_key_is_stale()
    {
        var recorded = Facts(registryVersions: new Dictionary<string, long>(StringComparer.Ordinal) { ["rooms"] = 1, ["events"] = 2 });
        var live = Facts(registryVersions: new Dictionary<string, long>(StringComparer.Ordinal) { ["rooms"] = 1 });
        Assert.Equal(Staleness.Stale, DomainStaleness.Of(recorded, live));
    }

    [Fact]
    public void An_added_registry_key_is_stale()
    {
        var recorded = Facts(registryVersions: new Dictionary<string, long>(StringComparer.Ordinal) { ["rooms"] = 1 });
        var live = Facts(registryVersions: new Dictionary<string, long>(StringComparer.Ordinal) { ["rooms"] = 1, ["events"] = 2 });
        Assert.Equal(Staleness.Stale, DomainStaleness.Of(recorded, live));
    }

    [Fact]
    public void Registry_versions_compare_by_content_not_insertion_order()
    {
        var recorded = Facts(registryVersions: new Dictionary<string, long>(StringComparer.Ordinal) { ["rooms"] = 1, ["events"] = 2 });
        var live = Facts(registryVersions: new Dictionary<string, long>(StringComparer.Ordinal) { ["events"] = 2, ["rooms"] = 1 });
        Assert.Equal(Staleness.Fresh, DomainStaleness.Of(recorded, live));
    }

    [Fact]
    public void Null_arguments_throw()
    {
        Assert.Throws<ArgumentNullException>(() => DomainStaleness.Of(null!, Facts()));
        Assert.Throws<ArgumentNullException>(() => DomainStaleness.Of(Facts(), null!));
    }

    // ---- "never a clock" (spec §3, verbatim) ----------------------------------------------------

    [Fact]
    public void The_source_file_never_reads_a_clock()
    {
        // Structural proof, not a behavioral inference: staleness is "a comparison, never a clock"
        // by construction if nothing in the file can read one. Repeated identical calls proving the
        // same answer would still pass a version that secretly clamped by elapsed time to zero --
        // scanning the source is the only proof that admits no such implementation.
        var path = FindSourceFile("DomainStaleness.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("DateTime", source);
        Assert.DoesNotContain("Utc", source);
        Assert.DoesNotContain("Stopwatch", source);
        Assert.DoesNotContain("Environment.TickCount", source);
    }

    static string FindSourceFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FusionRpg.Core", "Delve", "Domains", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}
