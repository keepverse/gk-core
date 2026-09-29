using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// E8 against a real database (spec-content-hash.md). Core owns the algorithm and has its own tests;
/// this covers the half that only exists once rows are involved — which tables are read, that a
/// content edit moves the hash, and that <b>player state does not</b>.
///
/// <para>The line the whole module rests on: content is hashed, player state is not. An instance is a
/// consequence of content plus a seed, so hashing content and storing the seed already pins it.</para>
/// </summary>
public class ContentHashStoreTests : IDisposable
{
    readonly List<DataTestStore> _stores = new();

    public void Dispose()
    {
        foreach (var s in _stores)
            s.Dispose();
    }

    RpgStore NewStore()
    {
        var test = DataTestStore.Create();
        _stores.Add(test);
        return test.Store;
    }

    static AtomRow Vitality(int amount = 45, int tier = 1, bool enabled = true) => new()
    {
        AtomId = AtomRow.DeriveId("atom.vitality", "", tier),
        KindId = "stat.modify",
        FamilyId = "atom.vitality",
        Tier = tier,
        Name = $"Vitality t{tier}",
        WhenJson = "{}",
        ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{amount}}}""",
        TagsJson = """{"category":"survivability"}""",
        Enabled = enabled,
    };

    static AtomRow Searing(int tier = 3) => new()
    {
        AtomId = AtomRow.DeriveId("atom.searing-strike", "fire", tier),
        KindId = "resource.delta",
        FamilyId = "atom.searing-strike",
        Variant = "fire",
        Tier = tier,
        Name = "Searing Strike",
        WhenJson = """{"trigger":"OnDamageDealt","chance":250,"icd_ms":500}""",
        ParamsJson = """{"amount":-120,"element":"fire"}""",
    };

    static ContainerRow Blade(string id = "item.blade") => new()
    {
        ContainerId = id,
        Kind = ContainerKind.Item,
        Slot = "weapon",
        Atoms = new[] { new ContainerAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1)) },
    };

    static string Hash(RpgStore s) => s.ComputeContentHash().Hash;

    // ---- stability ------------------------------------------------------------------------------

    [Fact]
    public void The_same_content_hashes_the_same_on_two_separate_loads()
    {
        var a = NewStore();
        var b = NewStore();

        foreach (var s in new[] { a, b })
        {
            Assert.True(s.UpsertAtom(Vitality()).IsOk);
            Assert.True(s.UpsertAtom(Searing()).IsOk);
            Assert.True(s.UpsertRarity(new RarityRow("rare", 3, 2, 0, 1, 3)).Ok);
        }

        Assert.Equal(Hash(a), Hash(b));
    }

    [Trait("Category", "Heavy")]
    [Fact]
    public void Insert_order_does_not_change_the_hash()
    {
        var forward = NewStore();
        forward.UpsertAtom(Vitality());
        forward.UpsertAtom(Searing());

        var backward = NewStore();
        backward.UpsertAtom(Searing());
        backward.UpsertAtom(Vitality());

        Assert.Equal(Hash(forward), Hash(backward));
    }

    [Fact]
    public void Recomputing_over_an_unchanged_database_is_stable()
    {
        var s = NewStore();
        s.UpsertAtom(Vitality());

        Assert.Equal(Hash(s), Hash(s));
    }

    [Trait("Category", "Heavy")]
    [Fact]
    public void An_empty_catalog_is_a_specific_recognisable_hash_not_an_accident()
    {
        var a = NewStore();
        var b = NewStore();

        // Every covered table empty must still produce a hash, and the same one everywhere — an
        // empty catalog is a state an operator can recognise rather than a stable-looking void.
        var empty = Hash(a);
        // Formula, not a literal (population-pin SE3.4, 2026-09-20): a SHA-256 digest, hex-encoded, is
        // always exactly this many characters -- a structural fact about the algorithm, never content.
        Assert.Equal(System.Security.Cryptography.SHA256.HashSizeInBytes * 2, empty.Length);
        Assert.Equal(empty, Hash(b));

        a.UpsertAtom(Vitality());
        Assert.NotEqual(empty, Hash(a));
    }

    // ---- a content edit is visible ---------------------------------------------------------------

    [Fact]
    public void Rewriting_an_identical_row_does_not_move_the_hash()
    {
        // E14a requires "import twice, content hash unchanged". `revision` is a hashed column and
        // the upsert bumped it on every write, changed or not — so a re-import of an unchanged file
        // looked exactly like a content edit. A revision is how many times a row CHANGED, not how
        // many times it was written.
        var s = NewStore();
        s.UpsertAtom(Vitality());
        var before = Hash(s);

        s.UpsertAtom(Vitality());

        Assert.Equal(before, Hash(s));
    }

    [Fact]
    public void Rewriting_an_identical_container_does_not_move_the_hash()
    {
        // A container's children are replaced wholesale, so the idempotency check has to cover them
        // — comparing only the parent columns would call every re-import unchanged even when the
        // atom list moved, which is the opposite mistake and a far worse one.
        var s = NewStore();
        s.UpsertAtom(Vitality());
        s.UpsertContainer(Blade());
        var before = Hash(s);

        s.UpsertContainer(Blade());

        Assert.Equal(before, Hash(s));
    }

    [Fact]
    public void A_changed_child_row_still_moves_the_hash()
    {
        var s = NewStore();
        s.UpsertAtom(Vitality());
        s.UpsertAtom(Vitality(tier: 2));
        s.UpsertContainer(Blade());
        var before = Hash(s);

        s.UpsertContainer(Blade() with
        {
            Atoms = new[] { new ContainerAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 2)) },
        });

        Assert.NotEqual(before, Hash(s));
    }

    [Fact]
    public void Rewriting_an_identical_curve_does_not_move_the_hash()
    {
        var s = NewStore();
        var points = new[] { new CurvePoint(1, 1000), new CurvePoint(10, 2000) };
        s.UpsertCurve("curve.atk.level", CurveInput.Level, points);
        var before = Hash(s);

        s.UpsertCurve("curve.atk.level", CurveInput.Level, points);

        Assert.Equal(before, Hash(s));
    }

    [Fact]
    public void Importing_the_whole_catalog_twice_is_a_no_op()
    {
        // E14a's acceptance row, at the level where it is actually decided.
        var s = NewStore();

        void ImportAll()
        {
            s.UpsertAtom(Vitality());
            s.UpsertAtom(Searing());
            s.UpsertCurve("curve.x", CurveInput.Level, new[] { new CurvePoint(1, 1000) });
            s.UpsertRarity(new RarityRow("rare", 3, 2, 0, 1, 3));
            s.UpsertContainer(Blade());
        }

        ImportAll();
        var before = Hash(s);

        ImportAll();

        Assert.Equal(before, Hash(s));
    }

    [Trait("Category", "Heavy")]
    [Fact]
    public void One_magnitude_changed_by_one_moves_the_hash()
    {
        var s = NewStore();
        s.UpsertAtom(Vitality(amount: 45));
        var before = Hash(s);

        s.UpsertAtom(Vitality(amount: 46));

        Assert.NotEqual(before, Hash(s));
    }

    [Fact]
    public void Disabling_a_row_moves_the_hash()
    {
        // Disabled rows are hashed on purpose: disabling an atom is a content change that can move a
        // golden, and excluding it would hide exactly the edit this module exists to surface.
        var s = NewStore();
        s.UpsertAtom(Vitality(enabled: true));
        var before = Hash(s);

        s.UpsertAtom(Vitality(enabled: false));

        Assert.NotEqual(before, Hash(s));
    }

    [Trait("Category", "Heavy")]
    [Fact]
    public void A_container_edit_moves_the_hash()
    {
        var s = NewStore();
        s.UpsertAtom(Vitality());
        s.UpsertContainer(Blade());
        var before = Hash(s);

        s.UpsertContainer(Blade() with { Slot = "offhand" });

        Assert.NotEqual(before, Hash(s));
    }

    [Fact]
    public void A_curve_edit_moves_the_hash()
    {
        var s = NewStore();
        s.UpsertCurve("curve.atk.level", CurveInput.Level, new[] { new CurvePoint(1, 1000), new CurvePoint(10, 2000) });
        var before = Hash(s);

        s.UpsertCurve("curve.atk.level", CurveInput.Level, new[] { new CurvePoint(1, 1000), new CurvePoint(10, 2100) });

        Assert.NotEqual(before, Hash(s));
    }

    [Fact]
    public void A_rarity_edit_moves_the_hash()
    {
        var s = NewStore();
        s.UpsertRarity(new RarityRow("rare", 3, 2, 0, 1, 3));
        var before = Hash(s);

        s.UpsertRarity(new RarityRow("rare", 3, 3, 0, 1, 3));

        Assert.NotEqual(before, Hash(s));
    }

    [Fact]
    public void Every_covered_table_actually_participates()
    {
        // A table registered but never read would be a silent blind spot: its edits would produce a
        // clean hash forever. Each is proven to move the digest attributed to it.
        var s = NewStore();
        s.UpsertAtom(Vitality());
        s.UpsertContainer(Blade());
        s.UpsertCurve("curve.x", CurveInput.Level, new[] { new CurvePoint(1, 1000) });
        s.UpsertRarity(new RarityRow("rare", 3, 2, 0, 1, 3));

        var stamp = s.ComputeContentHash();

        foreach (var table in ContentHashRegistry.Current)
            Assert.True(stamp.TableDigests.ContainsKey(table.TableName), table.TableName);

        var empty = ContentHash.Hex(ContentHash.EmptyDigest());
        Assert.NotEqual(empty, stamp.TableDigests["effect_atom"]);
        Assert.NotEqual(empty, stamp.TableDigests["effect_container"]);
        Assert.NotEqual(empty, stamp.TableDigests["effect_container_atom"]);
        Assert.NotEqual(empty, stamp.TableDigests["effect_curve"]);
        Assert.NotEqual(empty, stamp.TableDigests["rarity"]);

        // Nothing wrote a pool row, so this one must still be the empty digest.
        Assert.Equal(empty, stamp.TableDigests["effect_container_pool"]);
    }

    [Fact]
    public void A_content_edit_names_the_table_it_happened_in()
    {
        var s = NewStore();
        s.UpsertAtom(Vitality(amount: 45));
        s.UpsertRarity(new RarityRow("rare", 3, 2, 0, 1, 3));
        var before = s.ComputeContentHash();

        s.UpsertAtom(Vitality(amount: 46));

        var verdict = ContentHashComparison.Compare(before.ToCompact(), s.ComputeContentHash());

        Assert.Equal(ContentHashVerdict.Mismatch, verdict.Verdict);
        Assert.Equal(new[] { "effect_atom" }, verdict.ChangedTables);
    }

    // ---- player state is not content --------------------------------------------------------------

    [Fact]
    public void Creating_an_instance_and_a_binding_leaves_the_hash_alone()
    {
        var s = NewStore();
        s.UpsertAtom(Vitality());
        s.UpsertContainer(Blade());
        var before = Hash(s);

        var instanceId = s.SaveInstance(new InstanceRow
        {
            ContainerId = "item.blade",
            RollSeed = 12345,
            CatalogRevision = s.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1), """{"amount":45}""") },
        });
        Assert.False(string.IsNullOrEmpty(instanceId));

        var bind = s.Bind(new BindingRow
        {
            InstanceId = instanceId,
            OwnerKind = OwnerKind.Player,
            OwnerKey = "1",
            Slot = "weapon",
            Source = "test",
        });
        Assert.True(bind.IsOk, bind.ToString());

        Assert.Equal(before, Hash(s));
    }

    [Fact]
    public void Bumping_the_catalog_revision_alone_leaves_the_hash_alone()
    {
        // content_meta holds the revision, not content — hashing it would make an import bump look
        // like an edit.
        var s = NewStore();
        s.UpsertAtom(Vitality());
        var before = Hash(s);

        s.BumpCatalogRevision();

        Assert.Equal(before, Hash(s));
    }

    // ---- the stamp -------------------------------------------------------------------------------

    [Fact]
    public void The_computed_stamp_round_trips_and_compares_equal_to_itself()
    {
        var s = NewStore();
        s.UpsertAtom(Vitality());
        var stamp = s.ComputeContentHash();

        Assert.Equal(ContentHashRegistry.CurrentSchemaVersion, stamp.SchemaVersion);
        Assert.StartsWith("content:", stamp.Short);
        Assert.Equal(ContentHashVerdict.Match,
            ContentHashComparison.Compare(stamp.ToCompact(), s.ComputeContentHash()).Verdict);
    }

    [Fact]
    public void An_unknown_registry_version_is_refused_before_any_query_runs()
    {
        var s = NewStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => s.ComputeContentHash(99));
    }

    // ---- the web match log carries the stamp ------------------------------------------------------

    [Fact]
    public void The_web_match_log_stores_and_returns_the_content_stamp()
    {
        // The boot sweep can only refuse a cross-content re-resolve if the stamp survives the round
        // trip. Without this the column exists and the sweep always sees null.
        var s = NewStore();
        s.UpsertAtom(Vitality());
        var stamp = s.ComputeContentHash().ToCompact();

        var (created, entry) = s.AppendWebMatchLog(
            1, "corr-1", "match-1", "{}", 42, 1, 1, 1, "env-stamp", stamp);

        Assert.True(created);
        Assert.Equal(stamp, entry.ContentHash);
        Assert.Equal(stamp, s.TryGetWebMatchLog(1, "corr-1")!.ContentHash);
        Assert.Equal(stamp, s.ListUnresolvedWebMatches().Single(e => e.MatchKey == "match-1").ContentHash);
    }

    [Fact]
    public void A_web_match_log_row_written_without_a_stamp_stays_null()
    {
        // Rows older than this module carry no stamp and must keep healing, not start refusing.
        var s = NewStore();

        var (_, entry) = s.AppendWebMatchLog(1, "corr-legacy", "match-legacy", "{}", 42, 1, 1, 1);

        Assert.Null(entry.ContentHash);
        Assert.Equal(ContentHashVerdict.Match,
            ContentHashComparison.Compare(entry.ContentHash, s.ComputeContentHash()).Verdict);
    }
}
