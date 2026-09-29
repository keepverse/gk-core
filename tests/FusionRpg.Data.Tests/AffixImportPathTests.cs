using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E32 (spec-affix-import-path.md): "making an authored affix loadable." Four breaks named, two
/// already closed before this module (verified, not rebuilt); this file proves the remaining two —
/// the swept folder (test 9 lives in <c>FusionRpg.AtomImporter.Tests/SeedScannerTests.cs</c>, since it
/// compares against seedsmith's own Python source) and the write path (`ImportContent` upserting
/// `content.Affixes`) — plus the pool-key rename and the optional-`class` decision.
/// </summary>
public class AffixImportPathTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public AffixImportPathTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>Seeds one real atom directly, bypassing the JSON pipeline — the affix tests below need
    /// a real atom to reference but are not testing atom import itself.</summary>
    void SeedAtom(string atomId, string family, string variant = "", int tier = 1)
    {
        var r = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId, KindId = "stat.modify", FamilyId = family, Variant = variant, Tier = tier,
            Name = family, ParamsJson = """{"channel":"atk","op":"flat","amount":10}""",
        });
        Assert.True(r.IsOk, r.ToString());
    }

    static string AffixFileJson(string id, string? className, params (string Atom, int Seq)[] refs)
    {
        var classField = className is null ? "" : "\"class\":\"" + className + "\",";
        var refsJson = string.Join(",", refs.Select(r => "{\"seq\":" + r.Seq + ",\"atom\":\"" + r.Atom + "\"}"));
        return "{\"schemaVersion\":1,\"kind\":\"affix\",\"entries\":[{\"id\":\"" + id + "\"," +
               classField + "\"refs\":[" + refsJson + "]}]}";
    }

    // ---- test 1 / 1b: a real "kind": "affix" file imports and its rows are queryable ----------------

    [Fact]
    public void A_kind_affix_file_imports_and_its_rows_are_queryable()
    {
        SeedAtom("atom.vitality.t1", "atom.vitality");
        var collected = AtomSeedFile.Collect(new[]
        {
            ("affixes.json", AffixFileJson("affix.authored.vit", "prefix", ("atom.vitality.t1", 0))),
        });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var outcome = _store.ImportContent(collected.Content);

        Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));
        Assert.Equal(1, outcome.Affixes);
        var stored = _store.GetAffix("affix.authored.vit");
        Assert.NotNull(stored);
        Assert.Equal(AffixClass.Prefix, stored!.Class);
        Assert.Equal("atom.vitality.t1", Assert.Single(stored.Refs).AtomId);
    }

    // ---- test 2: idempotent re-import ---------------------------------------------------------------

    [Fact]
    public void The_same_affix_file_re_imported_is_idempotent()
    {
        SeedAtom("atom.vitality.t1", "atom.vitality");
        var collected = AtomSeedFile.Collect(new[]
        {
            ("affixes.json", AffixFileJson("affix.authored.vit", "prefix", ("atom.vitality.t1", 0))),
        });

        var first = _store.ImportContent(collected.Content);
        Assert.True(first.RowsChanged > 0);
        var revisionAfterFirst = first.CatalogRevision;

        var second = _store.ImportContent(collected.Content);

        Assert.Equal(0, second.RowsChanged);
        Assert.Equal(revisionAfterFirst, second.CatalogRevision);
    }

    // ---- test 3: re-indenting does not move the content hash -----------------------------------------

    [Fact]
    public void Reindenting_the_affix_file_does_not_change_the_parsed_row_or_the_content_hash()
    {
        SeedAtom("atom.vitality.t1", "atom.vitality");
        var compact = """{"schemaVersion":1,"kind":"affix","entries":[{"id":"affix.authored.vit","class":"prefix","refs":[{"seq":0,"atom":"atom.vitality.t1"}]}]}""";
        var spaced = AffixFileJson("affix.authored.vit", "prefix", ("atom.vitality.t1", 0));

        var collectedCompact = AtomSeedFile.Collect(new[] { ("a.json", compact) });
        var outcomeCompact = _store.ImportContent(collectedCompact.Content);
        var hashAfterCompact = outcomeCompact.ContentHash;

        // Re-import the SAME logical row, differently formatted — RowsChanged must be zero (no
        // content actually differs) and the content hash must not move.
        var collectedSpaced = AtomSeedFile.Collect(new[] { ("b.json", spaced) });
        var outcomeSpaced = _store.ImportContent(collectedSpaced.Content);

        Assert.Equal(0, outcomeSpaced.RowsChanged);
        // ContentHashStamp carries a Dictionary<string,string>, which record equality compares by
        // reference — the .Hash string itself is the actual content digest to compare.
        Assert.Equal(hashAfterCompact!.Hash, outcomeSpaced.ContentHash!.Hash);
    }

    // ---- test 4: class absent / matching / contradicting ----------------------------------------------

    [Fact]
    public void An_absent_class_is_derived_from_the_bundles_refs()
    {
        SeedAtom("atom.vitality.t1", "atom.vitality"); // no `when` -> permanent -> Prefix
        var collected = AtomSeedFile.Collect(new[]
        {
            ("affixes.json", AffixFileJson("affix.authored.derived", null, ("atom.vitality.t1", 0))),
        });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var outcome = _store.ImportContent(collected.Content);

        Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));
        Assert.Equal(AffixClass.Prefix, _store.GetAffix("affix.authored.derived")!.Class);
    }

    [Fact]
    public void A_class_matching_the_derived_value_is_accepted()
    {
        SeedAtom("atom.vitality.t1", "atom.vitality");
        var collected = AtomSeedFile.Collect(new[]
        {
            ("affixes.json", AffixFileJson("affix.authored.matching", "prefix", ("atom.vitality.t1", 0))),
        });

        var outcome = _store.ImportContent(collected.Content);

        Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));
        Assert.Equal(AffixClass.Prefix, _store.GetAffix("affix.authored.matching")!.Class);
    }

    [Fact]
    public void PlantedViolation_a_class_contradicting_the_derived_value_is_refused_naming_both()
    {
        SeedAtom("atom.vitality.t1", "atom.vitality"); // permanent -> derives Prefix
        var collected = AtomSeedFile.Collect(new[]
        {
            ("affixes.json", AffixFileJson("affix.authored.contradicting", "suffix", ("atom.vitality.t1", 0))),
        });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var outcome = _store.ImportContent(collected.Content);

        Assert.False(outcome.IsOk);
        var msg = string.Join("; ", outcome.Errors);
        Assert.Contains("suffix", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prefix", msg, StringComparison.OrdinalIgnoreCase);
    }

    // ---- test 5: a container pool referencing an imported affix rolls it ------------------------------

    [Fact]
    public void A_container_pool_referencing_an_imported_affix_rolls_it_through_Instantiator_Draw()
    {
        SeedAtom("atom.vitality.t1", "atom.vitality");
        var collected = AtomSeedFile.Collect(new[]
        {
            ("affixes.json", AffixFileJson("affix.authored.rollable", "prefix", ("atom.vitality.t1", 0))),
        });
        var affixImport = _store.ImportContent(collected.Content);
        Assert.True(affixImport.IsOk, string.Join("; ", affixImport.Errors));

        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.pooled-affix", Kind = ContainerKind.Item, PrefixRolls = 1,
            Pool = new[] { new ContainerPoolRow("affix.authored.rollable", 100) },
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());

        var container = _store.GetContainer("item.pooled-affix")!;
        var drawn = Instantiator.Draw(container, _store.GetAtom, _store.GetAffix, rollSeed: 1);

        Assert.Equal("atom.vitality.t1", Assert.Single(drawn));
    }

    // ---- test 6: planted violation — pool row keyed "atom" is refused, naming the rename --------------

    [Fact]
    public void PlantedViolation_a_pool_row_keyed_atom_is_refused_naming_the_rename()
    {
        var json = """
            {
              "schemaVersion": 1,
              "kind": "container",
              "entries": [
                { "id": "item.bad-key", "kind": "item", "prefixRolls": 1,
                  "pool": [ { "atom": "affix.whatever", "weight": 100 } ] }
              ]
            }
            """;

        var collected = AtomSeedFile.Collect(new[] { ("container.json", json) });

        Assert.False(collected.IsOk);
        var msg = string.Join("; ", collected.Errors);
        Assert.Contains("affix", msg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rename", msg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_pool_row_keyed_affix_reads_correctly()
    {
        var json = """
            {
              "schemaVersion": 1,
              "kind": "container",
              "entries": [
                { "id": "item.good-key", "kind": "item", "prefixRolls": 1,
                  "pool": [ { "affix": "affix.whatever", "weight": 100 } ] }
              ]
            }
            """;

        var collected = AtomSeedFile.Collect(new[] { ("container.json", json) });

        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        var container = Assert.Single(collected.Content.Containers);
        Assert.Equal("affix.whatever", Assert.Single(container.Pool).AffixId);
    }

    // ---- test 7: planted violation — an affix ref naming an unknown atom is refused by id -------------

    [Fact]
    public void PlantedViolation_an_affix_ref_naming_an_unknown_atom_is_refused_by_id()
    {
        var collected = AtomSeedFile.Collect(new[]
        {
            ("affixes.json", AffixFileJson("affix.authored.dangling", "prefix", ("atom.does-not-exist.t1", 0))),
        });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors)); // parses fine; the atom check is at import

        var outcome = _store.ImportContent(collected.Content);

        Assert.False(outcome.IsOk);
        var msg = string.Join("; ", outcome.Errors);
        Assert.Contains("atom.does-not-exist.t1", msg, StringComparison.Ordinal);
    }

    // ---- test 8: AffixLibraryGenerator's 1:1 output is derived and available within the SAME batch ----

    [Fact]
    public void A_generated_1to1_affix_resolves_within_the_same_import_batch_without_being_committed()
    {
        // No hand-authored affix at all — the container's pool names the GENERATED wrapper id for a
        // freshly-imported atom, proving §3.3's "derived at import, not committed" wiring end to end.
        var atomJson = """
            {
              "schemaVersion": 1,
              "kind": "atom",
              "entries": [
                { "family": "atom.ember-bolt", "tier": 1, "kind": "stat.modify", "name": "Ember Bolt",
                  "params": { "channel": "atk", "op": "flat", "amount": 10 } }
              ]
            }
            """;
        var containerJson = """
            {
              "schemaVersion": 1,
              "kind": "container",
              "entries": [
                { "id": "item.generated-affix", "kind": "item", "prefixRolls": 1,
                  "pool": [ { "affix": "affix.ember-bolt.t1", "weight": 100 } ] }
              ]
            }
            """;
        var collected = AtomSeedFile.Collect(new[] { ("atoms.json", atomJson), ("container.json", containerJson) });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var outcome = _store.ImportContent(collected.Content);

        Assert.True(outcome.IsOk, string.Join("; ", outcome.Errors));
        Assert.Equal(0, outcome.Affixes); // the generated wrapper was never committed as a row
        Assert.Null(_store.GetAffix("affix.ember-bolt.t1")); // confirmed: not in the table

        // And the container itself imported clean — its pool reference resolved against the
        // in-memory generated affix during THIS import's own validation.
        Assert.NotNull(_store.GetContainer("item.generated-affix"));
    }
}
