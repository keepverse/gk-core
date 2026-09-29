using FusionRpg.Core.Narrative.Vocabulary;
using Xunit;

namespace FusionRpg.Core.Tests.Narrative.Vocabulary;

/// <summary>
/// npc-story-events NR1.5's real-corpus read, second attempt: the runtime reader and the authored corpus
/// disagree in five places, and every one of them is a place where the corpus follows narrative-seed's OWN
/// spec while the reader had been written from a fixture. This file pins the reconciliation adopted here —
/// the reader now reads what the seed side authored — with a document per case built in memory.
///
/// <para>Sources, each read this session: `spec-storylet-vocab.md` §2 (the keyed-object shape),
/// §3.1 (a host's `climates` may be empty — "`world.anomaly` fires nowhere today"; `roomKind` is the word
/// `none` on non-Delve rows), §3.4 (the condition column is `compilesTo`, whose `none` row reads
/// `nothing`).</para>
/// </summary>
[Trait("VerificationId", "core.narrative")]
[Trait("Guard", "narrative")]
public sealed class RegistryReconciliationTests
{
    static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "tests", "fixtures", "narrative", "_registry", name));

    static string Committed(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "narrative", "_registry", name));

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "dungeon"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    /// <summary>Rewrites a keyed-object vocabulary as the ARRAY of rows the adapter emits, each row carrying
    /// its own `id` — the two shapes the corpus and the fixtures use for the same rows.</summary>
    static string AsArray(string json, string key, bool includeId = true)
    {
        var doc = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var rows = doc[key]!.AsObject();
        var array = new System.Text.Json.Nodes.JsonArray();
        foreach (var (id, row) in rows)
        {
            var copy = (System.Text.Json.Nodes.JsonObject)row!.DeepClone();
            if (includeId) copy["id"] = id;
            array.Add(copy);
        }
        doc[key] = array;
        return doc.ToJsonString();
    }

    [Fact]
    public void An_array_shaped_vocabulary_reads_the_same_rows_as_a_keyed_one()
    {
        var keyed = HostKindCatalog.Parse(Fixture("host-kinds.v1.json"));
        var arrayed = HostKindCatalog.Parse(AsArray(Fixture("host-kinds.v1.json"), "hostKinds"));

        Assert.Equal(
            keyed.Select(h => h.Id).OrderBy(id => id, StringComparer.Ordinal),
            arrayed.Select(h => h.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.All(arrayed, row => Assert.False(string.IsNullOrWhiteSpace(row.Place)));
    }

    [Fact]
    public void An_array_row_with_no_id_is_refused()
    {
        var json = AsArray(Fixture("host-kinds.v1.json"), "hostKinds", includeId: false);

        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => HostKindCatalog.Parse(json));

        Assert.Contains("non-empty string 'id'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_seed_sides_sentinel_words_read_as_absence()
    {
        // `roomKind: "none"` on a non-Delve host (§3.1) and `compilesTo: "nothing"` (§3.4) are the corpus's
        // words for "no value"; a consumer must see absence, not the word.
        var none = HostKindCatalog.Parse(Committed("host-kinds.v1.json"));
        Assert.Contains(none, host => host.Place != "delve" && host.RoomKind is null);

        var conditions = ConditionCatalog.Parse(Committed("conditions.v1.json"));
        Assert.Contains(conditions, c => c.Id == "none" && c.Leaf is null);
        Assert.Contains(conditions, c => c.Id == "danger-band-is" && c.Leaf == "BandIs");
    }

    [Fact]
    public void Both_spellings_of_the_compiled_leaf_are_read()
    {
        // The corpus writes `compilesTo`; the fixtures predate it and write `leaf`. One value either way.
        var fromFixture = ConditionCatalog.Parse(Fixture("conditions.v1.json"));
        var fromCommitted = ConditionCatalog.Parse(Committed("conditions.v1.json"));

        var fixtureBand = fromFixture.Single(c => c.Id == "danger-band-is").Leaf;
        var committedBand = fromCommitted.Single(c => c.Id == "danger-band-is").Leaf;

        Assert.Equal("BandIs", fixtureBand);
        Assert.Equal(fixtureBand, committedBand);
    }

    [Fact]
    public void The_seed_sides_role_requirement_marker_reads_as_absence()
    {
        // `conditions.v1.json`'s `role-cast` row carries the seed side's own marker in `compilesTo`
        // ("role requirement: resolved by casting"), which their validator and test both skip
        // (storylet_vocab.py:309-310, test_narrative_storylet_vocab.py:193-194). Read as absence — and ONLY
        // that prefix, so any other text in the column still refuses.
        var conditions = ConditionCatalog.Parse(Committed("conditions.v1.json"));

        Assert.Null(conditions.Single(c => c.Id == "role-cast").Leaf);
        Assert.Equal("BandIs", conditions.Single(c => c.Id == "danger-band-is").Leaf);
    }

    [Fact]
    public void A_flat_role_tags_file_reads_like_a_wrapped_one()
    {
        // The seed side writes `roleKinds`/`requireFamilies` at the ROOT (spec §3.6) and its `roleKinds` is an
        // ARRAY of rows; the fixtures wrap the two blocks under `roleTags` with `roleKinds` as an object.
        var flat = RoleTagCatalog.Parse(Committed("role-tags.v1.json"));
        var wrapped = RoleTagCatalog.Parse(Fixture("role-tags.v1.json"));

        Assert.NotEmpty(flat.RoleKinds);
        Assert.Contains(flat.RoleKinds, kind => kind.Id == "none");
        Assert.Contains(wrapped.RoleKinds, kind => kind.Id == "none");
        Assert.NotEmpty(flat.Families);
    }

    [Fact]
    public void An_empty_climates_list_is_legal_for_a_host_that_fires_nowhere_yet()
    {
        // §3.1: "an empty `climates` (`world.anomaly`) means the host fires nowhere today and the planner
        // declares no cell for it" — legal, and the corpus relies on it.
        var rows = HostKindCatalog.Parse(AsArray(Fixture("host-kinds.v1.json"), "hostKinds"));

        HostKindCatalog.Validate(rows);
        Assert.All(rows, row => Assert.NotNull(row.ClimateSource));
    }
}
