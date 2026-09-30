using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Workspace;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// spec-sockets.md §5.2 / D2 §6 — <c>item_socket</c> is the SSOT, and the recipe tables are a
/// multiset (D41). Against a real SQLite store, not a mock.
/// </summary>
[Trait("VerificationId", "data.item-socket")]
public class ItemSocketStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ItemSocketStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>
    /// A REAL <c>effect_instance</c> row. <c>item_socket.instance_id</c> carries a live foreign key
    /// with <c>ON DELETE CASCADE</c>, so a socket cannot exist without a host — writing against a
    /// made-up id throws, which is the constraint doing its job.
    /// </summary>
    string NewHost() => _store.SaveInstance(new InstanceRow
    {
        ContainerId = "item.bark-plating",
        RollSeed = 8812349,
        CatalogRevision = _store.GetCatalogRevision(),
        Origin = InstanceOrigin.Drop,
        Atoms = new[] { new InstanceAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1), """{"amount":45}""") },
    });

    static SocketTuning Tuning()
    {
        // See EquipProjectionSocketsTests.Sockets(): the CONTRIBUTING.md marker walk overshoots now
        // that the file is gk-workflow's, and data/tuning is gk-core's own.
        return SocketTuning.Parse(File.ReadAllText(
            Path.Combine(KeepverseRoots.Core(), "data", "tuning", SocketTuningFiles.Current)));
    }

    [Fact]
    public void Sockets_round_trip_with_their_affinity_crafted_flag_and_contents()
    {
        var rows = new List<SocketSlot>
        {
            new(0, "earth", Crafted: false, "gem.stone-heart.t3", "inst-a"),
            new(1, "", Crafted: true, null, null),
        };

        var host = NewHost();
        _store.SetSockets(host, rows);
        var read = _store.GetSockets(host);

        Assert.Equal(2, read.Count);
        Assert.Equal("earth", read[0].Affinity);
        Assert.False(read[0].Crafted);
        Assert.Equal("gem.stone-heart.t3", read[0].InsertContainerId);
        Assert.Equal("inst-a", read[0].InsertInstanceId);
        Assert.True(read[1].Crafted);
        Assert.True(read[1].IsEmpty);
    }

    [Fact]
    public void Existing_items_keep_their_socket_rows_across_a_tuning_revision()
    {
        // SSH5.10 (circuit-topology §1): a tuning PUBLISH is not a schema or item-state change.
        // `item_socket` is the SSOT for an existing item's sockets (D2 §6), so sockets.v2 must not
        // re-socket anything already dropped — the rows survive the store's own idempotent re-init
        // (the same call a relaunch makes) byte for byte.
        var host = NewHost();
        _store.SetSockets(host, new List<SocketSlot> { new(0, "ice", Crafted: true) });

        _store.Init();   // boot-time idempotent init again, as a relaunch after the v2 publish does

        var read = Assert.Single(_store.GetSockets(host));
        Assert.Equal("ice", read.Affinity);
        Assert.True(read.Crafted);
    }

    [Fact]
    public void SocketInsert_stores_a_real_insert_instance_id_and_never_an_empty_string()
    {
        var host = NewHost();
        _store.SetSockets(host, new List<SocketSlot>
        {
            new(0, "", Crafted: true, "gem.ember-shard.t1", "inst-insert-1"),
            new(1, "", Crafted: true, null, null),
        });

        // Read back through the normal API, not the writer's intent: a filled socket carries a real
        // instance id, and an empty one is SQL NULL — never the empty string the pre-T21 shape left
        // on this column (species-gear-chain T22). `SetSockets` binds through `?? DBNull.Value`.
        var read = _store.GetSockets(host);
        Assert.Equal(2, read.Count);
        Assert.False(read[0].IsEmpty);
        Assert.Equal("inst-insert-1", read[0].InsertInstanceId);
        Assert.True(read[1].IsEmpty);
        Assert.Null(read[1].InsertInstanceId);
    }

    [Fact]
    public void Item_socket_is_the_ssot_and_no_read_path_replays_the_op_log()
    {
        // C2 / D2 §6, asserted directly against ssot-sockets.md §5.2's superseded claim: GetSockets
        // takes only an instance id and reaches no operation log. If socket state were derived from
        // effect_instance_op, an item with rows and no ops would read back empty.
        var host = NewHost();
        _store.SetSockets(host, new List<SocketSlot> { new(0, "fire", true, "gem.ember-shard.t3", null) });

        Assert.Empty(_store.ReadMutationOps(host));
        Assert.Single(_store.GetSockets(host));

        var parameters = typeof(RpgStore).GetMethod(nameof(RpgStore.GetSockets))!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(string), parameters[0].ParameterType);
    }

    [Fact]
    public void A_sparse_socket_list_is_refused_rather_than_stored()
    {
        var gap = new List<SocketSlot> { new(0, "", false), new(2, "", false) };
        Assert.Throws<ArgumentException>(() => _store.SetSockets(NewHost(), gap));
    }

    [Fact]
    public void Setting_sockets_replaces_the_whole_row_set()
    {
        var host = NewHost();
        _store.SetSockets(host, new List<SocketSlot> { new(0, "", false, "gem.a.t1", null), new(1, "", false) });
        _store.SetSockets(host, new List<SocketSlot> { new(0, "ice", true) });

        var read = _store.GetSockets(host);
        Assert.Single(read);
        Assert.Equal("ice", read[0].Affinity);
        Assert.True(read[0].IsEmpty);
    }

    [Fact]
    public void The_generated_twenty_five_seed_and_read_back_in_evaluation_order()
    {
        var tuning = Tuning();
        var generated = ResonanceGenerator.Generate(tuning);
        _store.SeedComboRecipes(generated);

        var read = _store.GetComboRecipes();
        // Self-referential (population-pin SE3.4, 2026-09-20): this test seeded exactly `generated`
        // rows itself, so it proves the round trip through the store, not a fact about content.
        Assert.Equal(generated.Count, read.Count);
        Assert.Equal(18, read.Count(r => r.Shape == ComboShape.Pure));
        Assert.Equal(4, read.Count(r => r.Shape == ComboShape.Ring));
        Assert.Single(read, r => r.Shape == ComboShape.Eclipse);
        Assert.Equal(2, read.Count(r => r.Shape == ComboShape.Diversity));

        var shapes = read.Select(r => (int)r.Shape).ToList();
        Assert.Equal(shapes.OrderBy(s => s), shapes);

        // Idempotent: a second boot neither duplicates nor drops.
        // (SE3.4's population-pin repair caught the assertion above and missed this one - the same fix,
        // captured rather than pinned: the count must not MOVE, whatever it is.)
        var before = _store.GetComboRecipes().Count;
        _store.SeedComboRecipes(ResonanceGenerator.Generate(tuning));
        Assert.Equal(before, _store.GetComboRecipes().Count);
    }

    [Fact]
    public void A_strain_recipe_stores_its_ingredients_as_an_unordered_multiset()
    {
        // D41: the key is (combo_id, family_id) with a quantity — there is no position column to
        // read, and (SSH7.8) no tier column either, so a matcher cannot become order-sensitive by
        // accident and the tier lives only on the ladder.
        var strain = new ComboRecipe(
            "combo.strain-test", ComboShape.Strain, "", 0, "armament-primary", "", 4, 2,
            new[]
            {
                new ComboIngredient("atom.elemental-power", 3),
                new ComboIngredient("atom.vitality", 1),
            });

        _store.SeedComboRecipes(new[] { strain });
        var read = Assert.Single(_store.GetComboRecipes());

        Assert.Equal("combo.strain-test", read.ComboId);
        Assert.Equal(ComboShape.Strain, read.Shape);
        Assert.Equal(2, read.Ingredients.Count);
        Assert.Equal(4, read.Ingredients.Sum(i => i.Quantity));
        Assert.DoesNotContain(
            typeof(ComboIngredient).GetProperties(), p => p.Name.Contains("Position", StringComparison.Ordinal));
    }

    [Fact]
    public void The_pre_SSH78_ingredient_table_is_re_keyed_before_any_write_to_it()
    {
        // SSH7.8 / H2 (spec-tier-ladder §4, owner-approved 2026-09-21): the ingredient table was
        // keyed (combo_id, family_id, min_tier). An install written before this commit still carries
        // that shape, and `CREATE TABLE IF NOT EXISTS` cannot drop a column — so boot DROPS the table
        // before any write and rebuilds it keyed (combo_id, family_id); CombinationBoot re-seeds the
        // content on the same boot. A hand-seeded pre-init hot database is the memory equivalent of
        // "a save written by an older build".
        var legacy = DataTestStore.CreateWithPreInitHot(seed =>
        {
            using var ddl = seed.CreateCommand();
            ddl.CommandText = """
                CREATE TABLE socket_combo_recipe (
                  combo_id TEXT PRIMARY KEY, shape TEXT NOT NULL, element TEXT NOT NULL DEFAULT '',
                  threshold INTEGER NOT NULL DEFAULT 0, host_role TEXT NOT NULL DEFAULT '',
                  host_frame TEXT NOT NULL DEFAULT '', min_sockets INTEGER NOT NULL DEFAULT 0,
                  base_tier INTEGER NOT NULL DEFAULT 0, enabled INTEGER NOT NULL DEFAULT 1,
                  revision INTEGER NOT NULL DEFAULT 1);
                CREATE TABLE socket_combo_ingredient (
                  combo_id TEXT NOT NULL, family_id TEXT NOT NULL, min_tier INTEGER NOT NULL DEFAULT 1,
                  qty INTEGER NOT NULL DEFAULT 1, PRIMARY KEY (combo_id, family_id, min_tier),
                  FOREIGN KEY (combo_id) REFERENCES socket_combo_recipe(combo_id) ON DELETE CASCADE);
                INSERT INTO socket_combo_recipe(combo_id, shape, min_sockets)
                  VALUES ('combo.strain-legacy', 'strain', 4);
                INSERT INTO socket_combo_ingredient(combo_id, family_id, min_tier, qty)
                  VALUES ('combo.strain-legacy', 'atom.might', 3, 4);
                """;
            ddl.ExecuteNonQuery();
        });
        using (legacy)
        {
            var store = legacy.Store;

            // The column left the schema: the pre-migration table was dropped, not altered.
            using (var db = SqliteConnectionFactory.Open(store.HotPath))
            {
                using var info = db.CreateCommand();
                info.CommandText = "PRAGMA table_info(socket_combo_ingredient);";
                var columns = new List<string>();
                using var r = info.ExecuteReader();
                while (r.Read()) columns.Add(r.GetString(1));
                Assert.Equal(new[] { "combo_id", "family_id", "qty" }, columns);
            }

            // The pre-migration ingredient row went with the dropped table — boot re-seeds content
            // from the corpus, so a derived row is never carried across the re-key.
            var read = Assert.Single(store.GetComboRecipes());
            Assert.Equal("combo.strain-legacy", read.ComboId);
            Assert.Empty(read.Ingredients);

            // …and the boot-time re-seed lands on the NEW key: two rows of one family cannot both
            // exist, which the old (combo_id, family_id, min_tier) key allowed.
            store.SeedComboRecipes(new[]
            {
                new ComboRecipe("combo.strain-legacy", ComboShape.Strain, "", 0, "", "", 4, 1,
                    new[] { new ComboIngredient("atom.might", 4) }, BaseFloors: new[] { 1, 1, 1, 1 }),
            });
            var reseeded = Assert.Single(store.GetComboRecipes());
            var ingredient = Assert.Single(reseeded.Ingredients);
            Assert.Equal("atom.might", ingredient.FamilyId);
            Assert.Equal(4, ingredient.Quantity);

            // Idempotent: a second boot no longer sees `min_tier`, so it does not drop again.
            store.Init();
            Assert.Single(Assert.Single(store.GetComboRecipes()).Ingredients);
        }
    }

    [Fact]
    public void A_combination_absent_from_the_corpus_is_disabled_on_next_boot()
    {
        var strain = new ComboRecipe("combo.strain-might-offense", ComboShape.Strain, "", 0,
            "armament-primary", "", 4, 2, new[] { new ComboIngredient("atom.might", 4) });
        var removed = new ComboRecipe("combo.splice-might-agility", ComboShape.Splice, "", 0,
            "core-guard", "", 4, 2, new[] { new ComboIngredient("atom.vitality", 4) });
        var accepted = new HashSet<string>(StringComparer.Ordinal) { "combo.strain-might-offense" };

        _store.SeedComboRecipes(new[] { strain, removed });
        Assert.Equal(2, _store.GetComboRecipes().Count);

        // The next boot's corpus carries only `strain`: the removed one goes dark, it is not deleted.
        Assert.Equal(1, _store.DisableCombinationsNotIn(accepted));
        var read = Assert.Single(_store.GetComboRecipes());
        Assert.Equal("combo.strain-might-offense", read.ComboId);

        // Idempotent on every boot — an already-disabled row is left alone.
        Assert.Equal(0, _store.DisableCombinationsNotIn(accepted));

        // Re-authoring re-enables through the upsert (enabled = 1); it is never a second insert.
        _store.SeedComboRecipes(new[] { strain, removed });
        Assert.Equal(2, _store.GetComboRecipes().Count);
    }

    [Fact]
    public void Resonance_rows_are_never_disabled_by_the_import()
    {
        var resonances = ResonanceGenerator.Generate(Tuning());
        var strain = new ComboRecipe("combo.strain-might-offense", ComboShape.Strain, "", 0,
            "armament-primary", "", 4, 2, new[] { new ComboIngredient("atom.might", 4) });
        _store.SeedComboRecipes(resonances.Concat(new[] { strain }).ToList());

        // An empty accepted set is the worst case: every AUTHORED row is stale. A resonance id is not
        // in the authored space, so not one of them may be retired.
        Assert.Equal(1, _store.DisableCombinationsNotIn(new HashSet<string>(StringComparer.Ordinal)));

        var read = _store.GetComboRecipes();
        // Self-referential: this test seeded exactly `resonances` itself (population-pin SE3.4).
        Assert.Equal(resonances.Count, read.Count);
        Assert.DoesNotContain(read, r => r.ComboId == "combo.strain-might-offense");
        Assert.All(read, r => Assert.False(
            r.ComboId.StartsWith(StrainSpliceGrid.StrainPrefix, StringComparison.Ordinal) ||
            r.ComboId.StartsWith(StrainSpliceGrid.SplicePrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public void Socket_min_and_socket_max_seed_onto_every_rung_as_rarity_budget_rows()
    {
        var tuning = Tuning();
        _store.SeedRarityLadder(SampleRarityTuning());
        _store.SeedSocketGrants(tuning);

        foreach (var rung in RarityLadder.RungIds)
        {
            var window = tuning.RarityGrant[rung];
            Assert.Equal(window.Min, _store.GetRarityBudget(rung, "socket_min"));
            Assert.Equal(window.Max, _store.GetRarityBudget(rung, "socket_max"));
        }

        // Idempotent on every boot.
        _store.SeedSocketGrants(tuning);
        Assert.Equal(tuning.RarityGrant["almanac"].Max, _store.GetRarityBudget("almanac", "socket_max"));
    }

    [Fact]
    public void Socketing_writes_no_row_the_host_instance_owns()
    {
        // spec-sockets.md §1's table, as a test over the real schema: the only tables SetSockets
        // touches are item_socket's. effect_instance / effect_instance_atom are untouched, which is
        // what leaves InstanceRow.ContentFingerprint() byte-identical.
        var host = NewHost();
        var before = _store.GetInstanceMutationHead(host);

        _store.SetSockets(host, new List<SocketSlot>
        {
            new(0, "fire", false, "gem.ember-shard.t3", null),
            new(1, "fire", true, "gem.ember-shard.t3", null),
        });

        var after = _store.GetInstanceMutationHead(host);
        Assert.Equal(before!.MutationSeq, after!.MutationSeq);
        Assert.Equal(before.StateHash, after.StateHash);
        Assert.Equal(before.EnhanceLevel, after.EnhanceLevel);
        Assert.Empty(_store.ReadMutationOps(host));
        Assert.Equal(2, _store.GetSockets(host).Count);
    }

    static IReadOnlyDictionary<string, ItemRarityRungTuning> SampleRarityTuning()
    {
        // See Tuning() above: a named root, not a marker walk.
        return ItemRarityTuning.Parse(File.ReadAllText(
            Path.Combine(KeepverseRoots.Core(), "data", "tuning", "item-rarity.v1.json")));
    }
}
