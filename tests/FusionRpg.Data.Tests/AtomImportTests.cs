using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E14a acceptance (spec-authoring-and-validation.md). Two claims carry this module:
/// <b>all or nothing</b> — one bad row and the catalog is exactly as it was — and <b>importing the
/// same files twice changes nothing</b>, including the content hash and the catalog revision.
///
/// <para>The second is the harder one and the reason E8 came first: a repeat import that moved the
/// hash would make every replay verdict downstream report a content mismatch for content nobody
/// touched.</para>
/// </summary>
public class AtomImportTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    // One test writes a real seed tree on disk; that directory is the importer's subject, so it stays
    // a real temp dir. Only the store is in memory.
    readonly string _dir;

    public AtomImportTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _dir = Path.Combine(Path.GetTempPath(), "fusionrpg-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        _testStore.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    // ---- fixtures -----------------------------------------------------------------------------

    const string AtomFile = """
        {
          "schemaVersion": 1,
          "kind": "atom",
          "entries": [
            {
              "kind": "stat.modify",
              "family": "atom.vitality",
              "tier": 1,
              "name": "Vitality I",
              "params": { "channel": "maxHp", "op": "flat", "amount": 45 },
              "tags": { "category": "survivability" }
            },
            {
              "kind": "stat.modify",
              "family": "atom.vitality",
              "tier": 2,
              "name": "Vitality II",
              "params": { "channel": "maxHp", "op": "flat", "amount": 90 },
              "tags": { "category": "survivability" }
            }
          ]
        }
        """;

    const string ContainerFile = """
        {
          "schemaVersion": 1,
          "kind": "container",
          "entries": [
            {
              "id": "item.ring-of-vigour",
              "kind": "item",
              "slot": "ring",
              "poolRolls": 0,
              "atoms": [ { "atom": "atom.vitality.t1" } ]
            }
          ]
        }
        """;

    static SeedContent Read(params (string Path, string Json)[] files)
    {
        var result = AtomSeedFile.Collect(files);
        Assert.True(result.IsOk, string.Join("; ", result.Errors));
        return result.Content;
    }

    // ---- the happy path -------------------------------------------------------------------------

    [Fact]
    public void An_import_writes_every_kind_of_row_it_was_given()
    {
        var content = Read(("atoms.json", AtomFile), ("containers.json", ContainerFile));

        var outcome = _store.ImportContent(content);

        Assert.True(outcome.Committed);
        Assert.Empty(outcome.Errors);
        Assert.Equal(2, outcome.Atoms);
        Assert.Equal(1, outcome.Containers);
        Assert.NotNull(_store.GetAtom("atom.vitality.t1"));
        Assert.NotNull(_store.GetAtom("atom.vitality.t2"));
        Assert.NotNull(_store.GetContainer("item.ring-of-vigour"));
    }

    [Fact]
    public void A_container_may_reference_an_atom_authored_in_the_same_import()
    {
        // The common case for new content: an item and its affixes arrive together. Validating the
        // container against the stored table alone would reject every genuinely new item.
        var outcome = _store.ImportContent(Read(("a.json", AtomFile), ("c.json", ContainerFile)));

        Assert.True(outcome.Committed);
        Assert.Single(_store.GetContainer("item.ring-of-vigour")!.Atoms);
    }

    // ---- all or nothing ---------------------------------------------------------------------------

    [Fact]
    public void One_bad_row_imports_nothing_at_all()
    {
        // A partial import produces a content hash for a state nobody authored — the exact thing E8
        // exists to make impossible to miss.
        const string oneBad = """
            {
              "schemaVersion": 1,
              "kind": "atom",
              "entries": [
                {
                  "kind": "stat.modify", "family": "atom.good", "tier": 1, "name": "Good",
                  "params": { "channel": "maxHp", "op": "flat", "amount": 10 }
                },
                {
                  "kind": "no.such.kind", "family": "atom.bad", "tier": 1, "name": "Bad",
                  "params": {}
                }
              ]
            }
            """;

        var before = _store.ComputeContentHash().Hash;

        var outcome = _store.ImportContent(Read(("mixed.json", oneBad)));

        Assert.False(outcome.Committed);
        Assert.Null(_store.GetAtom("atom.good.t1"));
        Assert.Null(_store.GetAtom("atom.bad.t1"));
        Assert.Equal(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void A_bad_container_takes_the_atoms_in_its_import_down_with_it()
    {
        // The failure is in a different table from the rows that would have been written. A per-table
        // transaction would have committed the atoms before the container was ever judged.
        const string danglingRef = """
            {
              "schemaVersion": 1,
              "kind": "container",
              "entries": [
                { "id": "item.broken", "kind": "item", "atoms": [ { "atom": "atom.does-not-exist.t1" } ] }
              ]
            }
            """;

        var outcome = _store.ImportContent(Read(("a.json", AtomFile), ("c.json", danglingRef)));

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Errors, e => e.Reason == AtomRejectionReason.UnknownAtom);
        Assert.Null(_store.GetAtom("atom.vitality.t1"));
    }

    [Fact]
    public void A_refused_import_names_the_file_the_bad_row_came_from()
    {
        const string bad = """
            {
              "schemaVersion": 1, "kind": "atom",
              "entries": [ { "kind": "no.such.kind", "family": "atom.bad", "tier": 1, "params": {} } ]
            }
            """;

        var outcome = _store.ImportContent(Read(("data/seed/atoms/broken.json", bad)));

        var error = Assert.Single(outcome.Errors);
        Assert.Equal("data/seed/atoms/broken.json", error.SourcePath);
        Assert.Equal("atom.bad.t1", error.EntryId);
    }

    // ---- idempotency ------------------------------------------------------------------------------

    [Fact]
    public void Importing_the_same_files_twice_moves_neither_the_hash_nor_the_revision()
    {
        _store.ImportContent(Read(("a.json", AtomFile), ("c.json", ContainerFile)));
        var hash = _store.ComputeContentHash().Hash;
        var revision = _store.GetCatalogRevision();

        var second = _store.ImportContent(Read(("a.json", AtomFile), ("c.json", ContainerFile)));

        Assert.True(second.Committed);
        Assert.Equal(0, second.RowsChanged);
        Assert.Equal(hash, _store.ComputeContentHash().Hash);
        Assert.Equal(revision, _store.GetCatalogRevision());
    }

    [Fact]
    public void Reformatting_a_seed_file_is_not_a_content_change()
    {
        // JSON columns are stored canonically, so re-indenting a file, reordering keys inside an
        // object, or writing 45.0 where 45 was written before all land on the same bytes. Storing the
        // raw text instead would bump `revision` — a hashed column — for an edit that changed nothing.
        _store.ImportContent(Read(("a.json", AtomFile)));
        var hash = _store.ComputeContentHash().Hash;

        const string reformatted = """
            {
              "schemaVersion": 1,
              "kind": "atom",
              "entries": [
                { "kind": "stat.modify", "family": "atom.vitality", "tier": 1, "name": "Vitality I",
                  "params": { "op": "flat", "amount": 45, "channel": "maxHp" },
                  "tags": {
                        "category": "survivability"
                  } },
                { "kind": "stat.modify", "family": "atom.vitality", "tier": 2, "name": "Vitality II",
                  "params": { "amount": 90, "channel": "maxHp", "op": "flat" },
                  "tags": { "category": "survivability" } }
              ]
            }
            """;

        var second = _store.ImportContent(Read(("a.json", reformatted)));

        Assert.Equal(0, second.RowsChanged);
        Assert.Equal(hash, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void A_real_edit_bumps_the_revision_exactly_once_however_many_rows_changed()
    {
        // Once per transaction, not once per row — a fifty-row file would otherwise move the revision
        // fifty times, and every receiver negotiating on it would re-download fifty times.
        _store.ImportContent(Read(("a.json", AtomFile)));
        var revision = _store.GetCatalogRevision();

        const string bothEdited = """
            {
              "schemaVersion": 1, "kind": "atom",
              "entries": [
                { "kind": "stat.modify", "family": "atom.vitality", "tier": 1, "name": "Vitality I",
                  "params": { "channel": "maxHp", "op": "flat", "amount": 50 } },
                { "kind": "stat.modify", "family": "atom.vitality", "tier": 2, "name": "Vitality II",
                  "params": { "channel": "maxHp", "op": "flat", "amount": 100 } }
              ]
            }
            """;

        var outcome = _store.ImportContent(Read(("a.json", bothEdited)));

        Assert.Equal(2, outcome.RowsChanged);
        Assert.Equal(revision + 1, _store.GetCatalogRevision());
        Assert.Equal(revision + 1, outcome.CatalogRevision);
    }

    [Fact]
    public void A_refused_import_does_not_bump_the_revision()
    {
        _store.ImportContent(Read(("a.json", AtomFile)));
        var revision = _store.GetCatalogRevision();

        const string bad = """
            {
              "schemaVersion": 1, "kind": "atom",
              "entries": [ { "kind": "no.such.kind", "family": "atom.bad", "tier": 1, "params": {} } ]
            }
            """;
        _store.ImportContent(Read(("bad.json", bad)));

        Assert.Equal(revision, _store.GetCatalogRevision());
    }

    // ---- curves and rarities ------------------------------------------------------------------------

    [Fact]
    public void A_curve_authored_in_the_same_import_satisfies_an_atom_that_scales_through_it()
    {
        // Without this, no seed file could ever use a curve: the atom validator refuses an unknown
        // curve id, and a curve committed in an earlier transaction is not the same guarantee.
        const string curves = """
            {
              "schemaVersion": 1, "kind": "curve",
              "entries": [
                { "id": "curve.hp.level", "input": "level",
                  "points": [ { "x": 1, "mult": 1000 }, { "x": 10, "mult": 2000 } ] }
              ]
            }
            """;
        const string scaled = """
            {
              "schemaVersion": 1, "kind": "atom",
              "entries": [
                { "kind": "stat.modify", "family": "atom.scaled", "tier": 1, "name": "Scaled",
                  "params": { "channel": "maxHp", "op": "flat",
                              "amount": { "min": 10, "max": 20, "roll": "onInstantiate",
                                          "curve": "curve.hp.level" } } }
              ]
            }
            """;

        var outcome = _store.ImportContent(Read(("c.json", curves), ("a.json", scaled)));

        Assert.True(outcome.Committed, string.Join("; ", outcome.Errors));
        Assert.NotNull(_store.GetCurve("curve.hp.level"));
        Assert.NotNull(_store.GetAtom("atom.scaled.t1"));
    }

    [Fact]
    public void Two_rarity_bands_claiming_one_ordinal_refuse_the_import()
    {
        // Append-only ordinals are load-bearing for sorting and for the budget lookup. Deciding the
        // clash by insertion order would silently re-price every container naming the loser.
        const string clash = """
            {
              "schemaVersion": 1, "kind": "rarity",
              "entries": [
                { "id": "rare", "ordinal": 2, "poolRolls": 2, "minTier": 1, "maxTier": 3 },
                { "id": "epic", "ordinal": 2, "poolRolls": 3, "minTier": 2, "maxTier": 4 }
              ]
            }
            """;

        var outcome = _store.ImportContent(Read(("r.json", clash)));

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Errors, e => e.Reason == AtomRejectionReason.DuplicateKey);
        Assert.Empty(_store.ListRarities());
    }

    [Fact]
    public void A_hand_built_batch_with_the_same_atom_twice_is_refused()
    {
        // The reader catches this across files; this is the same mistake made by a caller that built
        // the content itself. Keeping the last one would make the import order-dependent.
        var content = new SeedContent();
        content.Atoms.Add(Vitality(45));
        content.Atoms.Add(Vitality(90));

        var outcome = _store.ImportContent(content);

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Errors, e => e.Reason == AtomRejectionReason.DuplicateKey);
        Assert.Null(_store.GetAtom("atom.vitality.t1"));
    }

    // ---- the check run -------------------------------------------------------------------------------

    [Fact]
    public void A_check_run_resolves_everything_and_still_writes_nothing()
    {
        // Not the same as validating the files: this resolves the container's atom reference against
        // the real catalog and lets the database itself refuse a write, then rolls back.
        var before = _store.ComputeContentHash().Hash;

        var outcome = _store.ImportContent(
            Read(("a.json", AtomFile), ("c.json", ContainerFile)), dryRun: true);

        Assert.Empty(outcome.Errors);
        Assert.False(outcome.Committed);
        Assert.Equal(3, outcome.RowsChanged); // two atoms and the container
        Assert.Null(_store.GetAtom("atom.vitality.t1"));
        Assert.Equal(before, _store.ComputeContentHash().Hash);
        Assert.Equal(0, _store.GetCatalogRevision());
    }

    [Fact]
    public void A_check_run_reports_the_same_refusal_a_real_import_would()
    {
        const string bad = """
            {
              "schemaVersion": 1, "kind": "atom",
              "entries": [ { "kind": "no.such.kind", "family": "atom.bad", "tier": 1, "params": {} } ]
            }
            """;

        var checkRun = _store.ImportContent(Read(("bad.json", bad)), dryRun: true);
        var realRun = _store.ImportContent(Read(("bad.json", bad)));

        Assert.Equal(realRun.Errors.Select(e => e.ToString()), checkRun.Errors.Select(e => e.ToString()));
    }

    // ---- files on disk ---------------------------------------------------------------------------------

    [Trait("Category", "DiskSemantics")]
    [Fact]
    public void A_seed_tree_on_disk_imports_end_to_end()
    {
        // Everything the importer tool does except parse its own arguments.
        var seed = Path.Combine(_dir, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "atoms"));
        Directory.CreateDirectory(Path.Combine(seed, "containers"));
        File.WriteAllText(Path.Combine(seed, "atoms", "vitality.json"), AtomFile);
        File.WriteAllText(Path.Combine(seed, "containers", "rings.json"), ContainerFile);

        var files = Directory.GetFiles(seed, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var outcome = _store.ImportContent(collected.Content);

        Assert.True(outcome.Committed, string.Join("; ", outcome.Errors));
        Assert.NotNull(_store.GetContainer("item.ring-of-vigour"));
    }

    [Fact]
    public void A_hand_built_batch_reusing_one_id_across_two_kinds_is_refused()
    {
        // One id namespace across all four kinds. Four namespaces that only overlap by accident is
        // the more expensive rule to hold, and a container named after an atom is a mistake anyway.
        var content = new SeedContent();
        content.Atoms.Add(Vitality(45));
        content.Containers.Add(new ContainerRow { ContainerId = "atom.vitality.t1" });

        var outcome = _store.ImportContent(content);

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Errors, e => e.Reason == AtomRejectionReason.DuplicateKey);
    }

    [Fact]
    public void A_hand_built_batch_with_the_same_curve_twice_is_refused()
    {
        var content = new SeedContent();
        content.Curves.Add(new CurveSeed("curve.x", CurveInput.Level, new[] { new CurvePoint(1, 1000) }));
        content.Curves.Add(new CurveSeed("curve.x", CurveInput.Tier, new[] { new CurvePoint(1, 2000) }));

        var outcome = _store.ImportContent(content);

        Assert.False(outcome.Committed);
        Assert.Null(_store.GetCurve("curve.x"));
    }

    // ---- the roster (E18) ----------------------------------------------------------------------------

    const string ElementFile = """
        {
          "schemaVersion": 1,
          "kind": "element",
          "entries": [
            { "id": "fire", "displayName": "Fire", "ordinal": 0 },
            { "id": "ice",  "displayName": "Ice",  "ordinal": 1 }
          ]
        }
        """;

    const string MatrixFile = """
        {
          "schemaVersion": 1,
          "kind": "element-matrix",
          "entries": [
            { "matrix": "combat", "attacker": "fire", "defender": "ice", "unit": 1 },
            { "matrix": "shield", "attacker": "fire", "defender": "ice", "unit": 1 }
          ]
        }
        """;

    [Fact]
    public void A_roster_imports_with_its_matrices()
    {
        var outcome = _store.ImportContent(Read(("e.json", ElementFile), ("m.json", MatrixFile)));

        Assert.True(outcome.Committed, string.Join("; ", outcome.Errors));
        Assert.Equal(2, outcome.Elements);
        Assert.Equal(1, _store.GetElementTable().CombatUnit("fire", "ice"));
    }

    [Fact]
    public void Re_importing_an_unchanged_roster_moves_neither_the_revision_nor_the_hash()
    {
        // Found by running the importer twice. The matrices are written delete-then-insert, so the
        // first cut counted every rewritten cell as a change: 26 rows "changed", the revision bumped,
        // and the content hash correctly stood still. That pair of symptoms means a change counter is
        // lying — and a bumped revision makes every connected receiver re-download the full push.
        _store.ImportContent(Read(("e.json", ElementFile), ("m.json", MatrixFile)));
        var revision = _store.GetCatalogRevision();
        var hash = _store.ComputeContentHash().Hash;

        var second = _store.ImportContent(Read(("e.json", ElementFile), ("m.json", MatrixFile)));

        Assert.Equal(0, second.RowsChanged);
        Assert.Equal(revision, _store.GetCatalogRevision());
        Assert.Equal(hash, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void A_real_roster_edit_still_registers()
    {
        // The other half — without it, "0 rows changed" could mean the roster write is dead.
        _store.ImportContent(Read(("e.json", ElementFile), ("m.json", MatrixFile)));

        const string iceRow = "{ \"id\": \"ice\",  \"displayName\": \"Ice\",  \"ordinal\": 1 }";
        var grown = ElementFile.Replace(
            iceRow,
            iceRow + ", { \"id\": \"air\", \"displayName\": \"Air\", \"ordinal\": 2 }",
            StringComparison.Ordinal);

        var second = _store.ImportContent(Read(("e.json", grown), ("m.json", MatrixFile)));

        Assert.True(second.Committed, string.Join("; ", second.Errors));
        Assert.True(second.RowsChanged > 0);
        Assert.Equal(3, _store.GetElementTable().Elements.Count);
    }

    [Fact]
    public void An_import_carrying_no_roster_leaves_the_stored_one_alone()
    {
        // Absent means "leave it", never "empty it" — the folders are swept independently and a run
        // that touched only atoms must not retire every element.
        _store.ImportContent(Read(("e.json", ElementFile), ("m.json", MatrixFile)));

        _store.ImportContent(Read(("a.json", AtomFile)));

        Assert.Equal(2, _store.GetElementTable().Elements.Count);
    }

    [Fact]
    public void Matchup_cells_naming_an_element_the_import_does_not_carry_are_refused()
    {
        const string dangling = """
            {
              "schemaVersion": 1, "kind": "element-matrix",
              "entries": [
                { "matrix": "combat", "attacker": "fire", "defender": "void", "unit": 1 }
              ]
            }
            """;

        var outcome = _store.ImportContent(Read(("e.json", ElementFile), ("m.json", dangling)));

        Assert.False(outcome.Committed);
        Assert.Contains(outcome.Errors, e => e.Detail.Contains("void", StringComparison.Ordinal));
    }

    static AtomRow Vitality(int amount) => new()
    {
        AtomId = "atom.vitality.t1",
        KindId = "stat.modify",
        FamilyId = "atom.vitality",
        Tier = 1,
        Name = "Vitality",
        ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{amount}}}""",
    };
}
