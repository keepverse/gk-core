using System.Text.Json;
using FusionRpg.Core.Items.Mutation;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T38 — the edge table's RUNTIME reader: the emitted corpus field. (The authoring
/// registry has no C# reader by design: the Python generator consumes it and its closure gate validates
/// it, and the C# parser that briefly existed with no production caller was deleted 2026-09-21 — see
/// `ItemUpgradeEdges.cs`.)
/// </summary>
public class ItemUpgradeEdgesTests
{
    static ItemUpgradeEdgeTable Corpus(params string[] docs) => ItemUpgradeEdgeCorpusReader.Parse(docs);

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    /// <summary>
    /// species-gear-chain T37 — the SHIPPED corpus, through the runtime reader the server boots with
    /// (`Program.cs` `ItemUpgradeEdgeHub.Configure`): the authored armour edges must survive parsing,
    /// every row that carries `successorOf` must yield an edge (nothing silently skipped), and every
    /// edge must name a real base type IN THE SAME FRAME. This is the corpus half of the closure
    /// contract the generator's own gate enforces on the authoring side, read back through the reader
    /// production actually uses — never through the registry.
    /// </summary>
    [Fact]
    public void The_shipped_corpus_edges_parse_and_close_on_a_real_base_type_in_the_same_frame()
    {
        var dir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "base-types");
        var docs = Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(File.ReadAllText)
            .ToList();

        var table = Corpus(docs.ToArray());
        var frames = new Dictionary<string, string>(StringComparer.Ordinal);
        var authoredRows = 0;
        foreach (var json in docs)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) continue;
            foreach (var entry in entries.EnumerateArray())
            {
                if (!entry.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                frames[id.GetString()!] = entry.TryGetProperty("frame", out var frame) && frame.ValueKind == JsonValueKind.String
                    ? frame.GetString()! : "";
                if (entry.TryGetProperty("successorOf", out var successor) && successor.ValueKind == JsonValueKind.String)
                    authoredRows++;
            }
        }

        Assert.NotEmpty(table.Edges);  // the authored armour pass — a reading that the wire is live
        // Reconciliation, not a population pin: every authored row reached the table.
        Assert.Equal(authoredRows, table.Edges.Count);
        foreach (var (source, target) in table.Edges)
        {
            Assert.True(frames.ContainsKey(target), $"{source} -> {target} does not resolve in the shipped corpus");
            Assert.Equal(frames[source], frames[target]);
        }
    }

    [Fact]
    public void The_corpus_reader_takes_each_entries_authored_edge()
    {
        var table = Corpus("""
            {"kind":"base-type","entries":[
              {"id":"item.humanoid-footing-cloth-001","frame":"humanoid","successorOf":"item.humanoid-footing-leather-001"},
              {"id":"item.humanoid-footing-leather-001","frame":"humanoid"}
            ]}
            """);

        Assert.Equal("item.humanoid-footing-leather-001", table.Edges["item.humanoid-footing-cloth-001"]);
        // The unauthored row yields NO entry at all, which is what `upgrade.no-successor` reports.
        Assert.False(table.Edges.ContainsKey("item.humanoid-footing-leather-001"));
        Assert.Single(table.Edges);
    }

    [Fact]
    public void An_unauthored_corpus_is_an_empty_table_not_a_failure()
    {
        var table = Corpus("""{"kind":"base-type","entries":[{"id":"item.a","frame":"humanoid"}]}""");

        Assert.Empty(table.Edges);
    }

    [Fact]
    public void A_malformed_document_or_entry_is_skipped_rather_than_thrown()
    {
        // The corpus reader's own posture (BaseTypeSocketMaxCorpus): an unreadable file or a
        // non-string edge yields no edge, and the verb then refuses by name.
        var table = Corpus(
            "{ not json",
            """{"entries":"not an array"}""",
            """{"entries":[{"id":"item.b","successorOf":7},{"successorOf":"item.c"},{"id":"item.d","successorOf":"item.d"}]}""",
            """{"entries":[{"id":"item.e","successorOf":"item.f"}]}""");

        var only = Assert.Single(table.Edges);
        Assert.Equal("item.e", only.Key);
        Assert.Equal("item.f", only.Value);
    }

    [Fact]
    public void The_hub_is_the_one_lookup_and_refuses_a_dangling_id_by_returning_null()
    {
        ItemUpgradeEdgeHub.Configure(Corpus("""{"entries":[{"id":"item.a","successorOf":"item.b"}]}"""));

        Assert.Equal("item.b", ItemUpgradeEdgeHub.EdgeFor("item.a"));
        Assert.Null(ItemUpgradeEdgeHub.EdgeFor("item.never-authored"));
    }
}
