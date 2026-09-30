using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Thresholds;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `threshold-grants` (item module 12) at the DAL — ssot-sets.md §4.2's three tables, the per-source
/// binding read the reconcile takes, and §4.5 step 2's DISTINCT-role recount as SQL. Driven with the
/// REAL shipped set corpus (`gk-data/packs/fusion/data/seed/items/sets/**`).
/// </summary>
public class ItemSetStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ItemSetStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static IReadOnlyList<SetDef> Corpus() =>
        Directory.EnumerateFiles(Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "sets"), "*.json")
            .Where(p => !Path.GetFileName(p).EndsWith(".ledger.json", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .SelectMany(p => SetCorpus.Parse(File.ReadAllText(p)))
            .ToList();

    static AtomRow Vitality() => new()
    {
        AtomId = AtomRow.DeriveId("atom.vitality", "", 1),
        KindId = "stat.modify",
        FamilyId = "atom.vitality",
        Tier = 1,
        Name = "Vitality t1",
        WhenJson = "{}",
        ParamsJson = """{"channel":"maxHp","op":"flat","amount":45}""",
        TagsJson = """{"category":"survivability"}""",
    };

    /// <summary>Bind one member base type into its role, exactly the way the equip path does.</summary>
    string EquipMember(OwnerScope owner, string containerId, ItemRole role)
    {
        _store.UpsertAtom(Vitality());
        var upsert = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(role),
            Atoms = new[] { new ContainerAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1)) },
        });
        Assert.True(upsert.IsOk, upsert.ToString());

        var instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = containerId,
            RollSeed = 1,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1), """{"amount":45}""") },
        });

        var bind = _store.Bind(new BindingRow
        {
            InstanceId = instanceId,
            OwnerKind = owner.Kind,
            OwnerKey = owner.Key,
            Slot = ItemRoles.Id(role),
            Priority = 0,
            // The shipped spelling module 4's ApplyEquipProjection actually writes — NOT ssot-sets
            // §4.5's illustrative `equip`, which matches no real binding.
            Source = "equip-assign",
        });
        Assert.True(bind.IsOk, bind.ToString());
        return instanceId;
    }

    [Fact]
    public void The_real_shipped_corpus_round_trips_through_the_three_tables()
    {
        var corpus = Corpus();
        _store.ImportSetCorpus(corpus);

        var back = _store.ListSets();
        Assert.Equal(corpus.Count, back.Count);
        Assert.NotEmpty(back);
        Assert.Equal(corpus.Sum(s => s.Members.Count), back.Sum(s => s.Members.Count));
        Assert.Equal(corpus.Sum(s => s.Tiers.Count), back.Sum(s => s.Tiers.Count));

        foreach (var original in corpus)
        {
            var stored = back.Single(s => s.SetId == original.SetId);
            Assert.Equal(original.DisplayName, stored.DisplayName);
            Assert.Equal(original.Members.OrderBy(m => m.ContainerId, StringComparer.Ordinal),
                         stored.Members.OrderBy(m => m.ContainerId, StringComparer.Ordinal));
            Assert.Equal(original.Tiers.OrderBy(t => t.PiecesRequired),
                         stored.Tiers.OrderBy(t => t.PiecesRequired));
            // item-lore T7: the set's lore KEY survives the round trip; the sentence deliberately does
            // not (it lives in the string catalog, not in SQL).
            Assert.Equal(original.FlavourKey, stored.FlavourKey);
            Assert.Null(stored.FlavourText);
            // species-gear-chain T28 (set-species-binding b): speciesId survives the round trip
            // identically -- a real id for a creature.*-themed set, null for build.*/theme.*.
            Assert.Equal(original.SpeciesId, stored.SpeciesId);
        }

        // Flavor is optional set metadata; every present key must survive the database projection.
        Assert.All(back.Where(s => s.FlavourKey is { Length: > 0 }),
            s => Assert.StartsWith("flavor.set.", s.FlavourKey!, StringComparison.Ordinal));
    }

    [Fact]
    public void SpeciesId_is_present_for_every_creature_themed_set_and_absent_for_every_other()
    {
        // species-gear-chain T28: closure over the REAL corpus -- every creature.*-themed set
        // resolved a real speciesId (T27/T28's repair already ran), and no build.*/theme.* set
        // invented one. A count is a reading (it grows with content); asserted here is the
        // CONTRACT -- non-null iff the theme is creature.*.
        var corpus = Corpus();
        _store.ImportSetCorpus(corpus);
        var back = _store.ListSets();
        Assert.NotEmpty(back.Where(s => s.SpeciesId is { Length: > 0 }));
        Assert.All(back, stored =>
        {
            var original = corpus.Single(s => s.SetId == stored.SetId);
            if (original.SpeciesId is { Length: > 0 })
                Assert.False(string.IsNullOrWhiteSpace(stored.SpeciesId));
            else
                Assert.Null(stored.SpeciesId);
        });
    }

    [Fact]
    public void The_import_is_idempotent_and_replaces_rather_than_accumulating()
    {
        var corpus = Corpus();
        _store.ImportSetCorpus(corpus);
        _store.ImportSetCorpus(corpus);
        Assert.Equal(corpus.Count, _store.ListSets().Count);
        Assert.Equal(corpus.Sum(s => s.Members.Count), _store.ListSets().Sum(s => s.Members.Count));
    }

    [Fact]
    public void A_tier_container_id_is_unique_across_the_whole_catalog()
    {
        // item_set_tier.container_id is UNIQUE in SQL, and the shipped corpus satisfies it — two sets
        // cannot name one tier container, which is what makes withdrawing by container id safe.
        var corpus = Corpus();
        _store.ImportSetCorpus(corpus);
        var ids = _store.ListSets().SelectMany(s => s.Tiers).Select(t => t.ContainerId).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void The_distinct_role_recount_is_SQL_and_it_matches_the_pure_evaluator()
    {
        var corpus = Corpus();
        _store.ImportSetCorpus(corpus);

        var set = corpus.Single(s => s.SetId == "frostbitten-vanguard-001");
        var owner = new OwnerScope(OwnerKind.UniqueActor, "spec-1");

        var worn = new List<EquippedPiece>();
        foreach (var m in set.Members.Take(3))
        {
            EquipMember(owner, m.ContainerId, m.Role);
            worn.Add(new EquippedPiece(m.Role, m.ContainerId));
        }

        var sqlCounts = _store.CountSetPieces(owner);
        Assert.Equal(3, sqlCounts[set.SetId]);

        // ⛔ And against ssot-sets §4.5's own `source = 'equip'` the same wearer counts NOTHING —
        // the doc predates module 4, which tags every equip binding `equip-assign`.
        Assert.Empty(_store.CountSetPieces(owner, "equip"));

        // The pure evaluator, over the same wearer, agrees exactly.
        var grant = ThresholdEvaluator.Grant(SetEvaluator.Consumer(set), SetEvaluator.Hits(worn, corpus));
        Assert.Equal(sqlCounts[set.SetId], grant.Count);
    }

    [Fact]
    public void Two_copies_of_one_member_in_two_roles_count_once_in_SQL_too()
    {
        // ssot-sets.md §4.5: the join requires m.role = b.slot, so a duplicate in another role never
        // matches. The cheese closes in the query, not in a special case.
        var corpus = Corpus();
        _store.ImportSetCorpus(corpus);

        var set = corpus.Single(s => s.SetId == "frostbitten-vanguard-001");
        var member = set.Members[0];
        var owner = new OwnerScope(OwnerKind.UniqueActor, "spec-2");

        EquipMember(owner, member.ContainerId, member.Role);
        EquipMember(owner, member.ContainerId, ItemRole.JewelMinorB);   // a second copy, a different role

        Assert.Equal(1, _store.CountSetPieces(owner)[set.SetId]);
    }

    [Fact]
    public void Bindings_are_read_back_per_source_so_two_partial_sets_never_collide()
    {
        var owner = new OwnerScope(OwnerKind.UniqueActor, "spec-3");
        Assert.True(_store.UpsertAtom(Vitality()).IsOk);

        // Two tier bindings under two different sources, plus one under `equip`.
        foreach (var (containerId, source) in new[]
                 {
                     ("item.tier-ember-02", "set:ember-legion"),
                     ("item.tier-tide-02", "set:tidebound"),
                     ("item.worn-blade", "equip"),
                 })
        {
            Assert.True(_store.UpsertContainer(new ContainerRow
            {
                ContainerId = containerId,
                Kind = ContainerKind.Item,
                Atoms = new[] { new ContainerAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1)) },
            }).IsOk);

            var instanceId = _store.SaveInstance(new InstanceRow
            {
                ContainerId = containerId,
                RollSeed = 7,
                CatalogRevision = _store.GetCatalogRevision(),
                Origin = InstanceOrigin.Drop,
                Atoms = new[] { new InstanceAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1), """{"amount":45}""") },
            });

            Assert.True(_store.Bind(new BindingRow
            {
                InstanceId = instanceId,
                OwnerKind = owner.Kind,
                OwnerKey = owner.Key,
                Priority = 0,
                Source = source,
            }).IsOk);
        }

        Assert.Equal(new[] { "item.tier-ember-02" },
            _store.ListBoundContainerIdsBySource(owner, "set:ember-legion"));
        Assert.Equal(new[] { "item.tier-tide-02" },
            _store.ListBoundContainerIdsBySource(owner, "set:tidebound"));
        Assert.Empty(_store.ListBoundContainerIdsBySource(owner, "frame-mix"));

        // And the reconcile over one source withdraws only that source's row.
        var diff = ThresholdEvaluator.Reconcile(
            _store.ListBoundContainerIdsBySource(owner, "set:ember-legion"), Array.Empty<string>());
        Assert.Equal(new[] { "item.tier-ember-02" }, diff.ToWithdraw);
    }

    [Fact]
    public void The_actor_effect_list_orders_tier_containers_ordinally_so_the_pad_is_load_bearing()
    {
        // RpgStore.ListBindings sorts by (priority DESC, container_id ASC) — ORDINAL. Proven here with
        // real rows rather than by reading the SQL: unpadded, the ten-piece tier would come first.
        var owner = new OwnerScope(OwnerKind.UniqueActor, "spec-4");
        Assert.True(_store.UpsertAtom(Vitality()).IsOk);

        foreach (var pieces in new[] { 10, 2, 4 })
        {
            var containerId = "item.x-" + pieces.ToString("D2");
            Assert.True(_store.UpsertContainer(new ContainerRow
            {
                ContainerId = containerId,
                Kind = ContainerKind.Item,
                Atoms = new[] { new ContainerAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1)) },
            }).IsOk);

            var instanceId = _store.SaveInstance(new InstanceRow
            {
                ContainerId = containerId,
                RollSeed = pieces,
                CatalogRevision = _store.GetCatalogRevision(),
                Origin = InstanceOrigin.Drop,
                Atoms = new[] { new InstanceAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1), """{"amount":45}""") },
            });

            Assert.True(_store.Bind(new BindingRow
            {
                InstanceId = instanceId, OwnerKind = owner.Kind, OwnerKey = owner.Key,
                Priority = 0, Source = "set:x",
            }).IsOk);
        }

        Assert.Equal(new[] { "item.x-02", "item.x-04", "item.x-10" },
            _store.ListBoundContainerIdsBySource(owner, "set:x"));
    }

    [Fact]
    public void The_three_tables_exist_with_the_columns_ssot_sets_declares()
    {
        foreach (var (table, expected) in new[]
                 {
                     ("item_set", new[] { "set_id", "display_name", "level_req", "enabled", "revision" }),
                     ("item_set_member", new[] { "set_id", "container_id", "role", "frame" }),
                     ("item_set_tier", new[] { "set_id", "pieces_required", "container_id", "is_capability" }),
                 })
        {
            var columns = Columns(table);
            foreach (var c in expected) Assert.Contains(c, columns);
        }
    }

    IReadOnlyList<string> Columns(string table)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var r = cmd.ExecuteReader();
        var cols = new List<string>();
        while (r.Read()) cols.Add(r.GetString(1));
        return cols;
    }
}
