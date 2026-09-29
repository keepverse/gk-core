using System.Text.RegularExpressions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `armoury`'s DAL half: the stock counter, the event log, the loadout library and the abuse-guard
/// row ceiling. The query surface, comparison algorithm and salvage guards are Core, DB-free, and
/// tested in <c>FusionRpg.Core.Tests.Items</c> instead.
/// </summary>
public class ArmouryTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ArmouryTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    /// <summary>
    /// `rpg_item`'s FK to `effect_instance` is a real, enforced constraint (`Microsoft.Data.Sqlite`
    /// defaults `PRAGMA foreign_keys` on) — every fixture needing an owned item needs a real instance
    /// first, matching what an actual acquisition always does.
    /// </summary>
    string SeedInstance()
    {
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.armoury-test", "", 1),
            KindId = "stat.modify", FamilyId = "atom.armoury-test", Variant = "", Tier = 1,
            Name = "Armoury Test", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.armoury-test", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, "atom.armoury-test.t1") },
        }).IsOk);

        var container = _store.GetContainer("item.armoury-test")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());

        return _store.SaveInstance(inst!);
    }

    // ---- stock: the counter that makes D1 affordable -------------------------------------------

    [Fact]
    public void Stock_items_are_a_counter_not_a_row_per_copy()
    {
        _store.AdjustStock("p1", "item.iron-band", delta: 5);
        _store.AdjustStock("p1", "item.iron-band", delta: 3);

        var stock = Assert.Single(_store.ListStock("p1"));
        Assert.Equal(8, stock.Qty);
    }

    [Fact]
    public void Stock_quantity_never_goes_negative()
    {
        _store.AdjustStock("p1", "item.iron-band", delta: 2);
        _store.AdjustStock("p1", "item.iron-band", delta: -10);

        var stock = Assert.Single(_store.ListStock("p1"));
        Assert.Equal(0, stock.Qty);
    }

    // ---- the abuse-guard row ceiling --------------------------------------------------------------

    [Fact]
    public void The_structural_row_ceiling_is_an_abuse_guard_and_says_so()
    {
        var text = File.ReadAllText(FindSourceFile("RpgStore.Items.cs"));
        var ceilingDecl = Regex.Match(text, @"const int InventoryCeiling = ([\d_]+);");
        Assert.True(ceilingDecl.Success, "InventoryCeiling constant not found");

        // The comment immediately above it must say WHY it is exempt from AGENTS.md's no-hard-
        // ceilings rule — a structural bug guard, not a progression cap. Required, not optional.
        var declIndex = ceilingDecl.Index;
        var precedingComment = text[Math.Max(0, declIndex - 900)..declIndex];
        Assert.Contains("abuse guard", precedingComment, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("never a progression", precedingComment, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_capacity_cap_exists_outside_the_named_abuse_guard()
    {
        // Grep-shaped guard (spec-drop-volume.md's own precedent): every file under the armoury's
        // DAL and Core surface may declare exactly one capacity-shaped constant, InventoryCeiling.
        var capNamePattern = new Regex(@"const\s+int\s+(\w*(?:Cap|Ceiling|MaxRows|RowLimit)\w*)\s*=",
            RegexOptions.IgnoreCase);

        var files = new List<string> { FindSourceFile("RpgStore.Items.cs") };
        var itemsDir = FindSourceDir("src/FusionRpg.Core/Items");
        files.AddRange(Directory.GetFiles(itemsDir, "*.cs", SearchOption.TopDirectoryOnly));

        var found = new List<string>();
        foreach (var f in files)
            foreach (Match m in capNamePattern.Matches(File.ReadAllText(f)))
                found.Add(m.Groups[1].Value);

        Assert.Equal(new[] { "InventoryCeiling" }, found);
    }

    static string FindSourceFile(string fileName)
    {
        var dir = FindRepoRoot();
        var matches = Directory.GetFiles(dir, fileName, new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            })
            // Judge the path RELATIVE to the resolved root: from the main checkout this still
            // excludes nested worktrees (.kilo/worktrees/...) below the root, while from inside
            // a worktree the tree's own src/ files are no longer rejected for living under .kilo.
            .Where(f => IsScannedSourcePath(Path.GetRelativePath(dir, f)))
            .ToList();
        Assert.True(matches.Count == 1, $"expected exactly one {fileName}, found {matches.Count}");
        return matches[0];
    }

    /// <summary>
    /// True when a path found by a repo-wide scan is the tree's own source, not build output or a
    /// nested checkout. A managed Agent Manager worktree (or any nested git worktree) lives *inside*
    /// the repo root and carries its own copy of every source file, so a scan that only skips
    /// <c>bin</c>/<c>obj</c> finds the file twice as soon as one exists.
    /// </summary>
    internal static bool IsScannedSourcePath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
            return false;
        foreach (var segment in fullPath.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".kilo", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".claude", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".opencode", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".pytest_cache", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals(".tmp-seedsmith-reconcile", StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    static string FindSourceDir(string relative)
    {
        var dir = Path.Combine(FindRepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(dir), $"expected directory {dir} to exist");
        return dir;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    // ---- events: what makes "where did my item go" answerable -----------------------------------

    [Fact]
    public void Acquiring_an_item_writes_the_owner_row_and_an_acquired_event_in_one_call()
    {
        var instanceId = SeedInstance();

        var r = _store.AcquireItem(new RpgItemRow
        {
            InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = "2026-01-01T00:00:00Z",
        });

        Assert.True(r.IsOk, r.ToString());
        Assert.NotNull(_store.GetItem(instanceId));

        var events = _store.ListItemEvents(instanceId);
        Assert.Single(events);
        Assert.Equal("acquired", events[0].Kind);
    }

    [Fact]
    public void A_fabricated_instance_id_is_refused_by_the_enforced_foreign_key()
    {
        // rpg_item's FK to effect_instance is real and enforced (Microsoft.Data.Sqlite defaults
        // PRAGMA foreign_keys on) -- an ownership row can never outlive or precede its instance.
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => _store.SaveItem(new RpgItemRow
        {
            InstanceId = "does-not-exist", PlayerId = "p1", AcquiredUtc = "2026-01-01T00:00:00Z",
        }));
    }

    // ---- loadouts: the library ----------------------------------------------------------------

    [Fact]
    public void A_loadout_round_trips_with_its_entries()
    {
        _store.SaveLoadout(
            new RpgItemLoadoutRow("lo-1", "p1", "Offense", Frame: null, "2026-01-01T00:00:00Z", Revision: 0),
            new[]
            {
                new RpgItemLoadoutEntryRow("lo-1", "armament-primary", "item", "inst-1"),
                new RpgItemLoadoutEntryRow("lo-1", "core-guard", "stock", "item.iron-band"),
            });

        var loadout = Assert.Single(_store.ListLoadouts("p1"));
        Assert.Equal("Offense", loadout.Name);

        var entries = _store.GetLoadoutEntries("lo-1");
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Role == "armament-primary" && e.RefKind == "item" && e.RefId == "inst-1");
        Assert.Contains(entries, e => e.Role == "core-guard" && e.RefKind == "stock" && e.RefId == "item.iron-band");
    }

    [Fact]
    public void Resaving_a_loadout_replaces_its_entries_rather_than_accumulating_them()
    {
        var loadout = new RpgItemLoadoutRow("lo-1", "p1", "Offense", null, "2026-01-01T00:00:00Z", 0);
        _store.SaveLoadout(loadout, new[] { new RpgItemLoadoutEntryRow("lo-1", "armament-primary", "item", "a") });
        _store.SaveLoadout(loadout, new[] { new RpgItemLoadoutEntryRow("lo-1", "armament-primary", "item", "b") });

        var entries = _store.GetLoadoutEntries("lo-1");
        var entry = Assert.Single(entries);
        Assert.Equal("b", entry.RefId);
    }

    // ---- loadouts: delete (build-preset BP1.6 -- the store had no delete) -------------------------

    [Fact]
    public void Deleting_a_loadout_you_do_not_own_refuses_and_leaves_both_tables_untouched()
    {
        _store.SaveLoadout(
            new RpgItemLoadoutRow("lo-1", "p1", "Offense", null, "2026-01-01T00:00:00Z", 0),
            new[] { new RpgItemLoadoutEntryRow("lo-1", "armament-primary", "item", "a") });

        Assert.False(_store.DeleteLoadout("p2", "lo-1"));

        Assert.Single(_store.ListLoadouts("p1"));
        Assert.Single(_store.GetLoadoutEntries("lo-1"));
    }

    [Fact]
    public void Deleting_an_unknown_loadout_id_refuses()
    {
        Assert.False(_store.DeleteLoadout("p1", "no-such-loadout"));
    }

    [Fact]
    public void Deleting_your_own_loadout_removes_both_tables_rows_in_one_transaction()
    {
        _store.SaveLoadout(
            new RpgItemLoadoutRow("lo-1", "p1", "Offense", null, "2026-01-01T00:00:00Z", 0),
            new[]
            {
                new RpgItemLoadoutEntryRow("lo-1", "armament-primary", "item", "a"),
                new RpgItemLoadoutEntryRow("lo-1", "core-guard", "stock", "item.iron-band"),
            });

        Assert.True(_store.DeleteLoadout("p1", "lo-1"));

        Assert.Empty(_store.ListLoadouts("p1"));
        Assert.Empty(_store.GetLoadoutEntries("lo-1"));
    }

    // ---- loadouts: validate-on-read, the half the library shipped without ------------------------

    /// <summary>`spec-armoury.md`'s own named test. The entry must come back MARKED, not omitted —
    /// asserting the count first is the point: a shorter list is the defect, and a test that only
    /// looked for the marker would pass on a silently dropped row too.</summary>
    [Fact]
    public void A_loadout_entry_whose_item_was_salvaged_returns_missing()
    {
        var instanceId = SeedInstance();
        _store.SaveItem(new RpgItemRow { InstanceId = instanceId, PlayerId = "p1", AcquiredUtc = "2026-01-01T00:00:00Z" });
        _store.SaveLoadout(
            new RpgItemLoadoutRow("lo-1", "p1", "Offense", null, "2026-01-01T00:00:00Z", 0),
            new[] { new RpgItemLoadoutEntryRow("lo-1", "armament-primary", "item", instanceId) });

        Assert.Equal(FusionRpg.Core.Items.LoadoutEntryState.Present,
            Assert.Single(_store.GetLoadoutEntriesValidated("lo-1", "p1")).State);

        // Salvage: the instance and its ownership row are gone, the preset row is not.
        _store.DeleteInstance(instanceId);

        var after = Assert.Single(_store.GetLoadoutEntriesValidated("lo-1", "p1"));
        Assert.Equal("armament-primary", after.Role);
        Assert.Equal(FusionRpg.Core.Items.LoadoutEntryState.Missing, after.State);
    }

    [Fact]
    public void A_stock_entry_is_missing_only_once_the_last_copy_is_spent()
    {
        _store.AdjustStock("p1", "item.iron-band", delta: 1);
        _store.SaveLoadout(
            new RpgItemLoadoutRow("lo-1", "p1", "Offense", null, "2026-01-01T00:00:00Z", 0),
            new[] { new RpgItemLoadoutEntryRow("lo-1", "core-guard", "stock", "item.iron-band") });

        Assert.Equal(FusionRpg.Core.Items.LoadoutEntryState.Present,
            Assert.Single(_store.GetLoadoutEntriesValidated("lo-1", "p1")).State);

        _store.AdjustStock("p1", "item.iron-band", delta: -1);

        Assert.Equal(FusionRpg.Core.Items.LoadoutEntryState.Missing,
            Assert.Single(_store.GetLoadoutEntriesValidated("lo-1", "p1")).State);
    }

    /// <summary>Another player's copy is not this player's — validate-on-read is scoped to the owner,
    /// or a preset would report a hole filled by someone else's item.</summary>
    [Fact]
    public void Validate_on_read_is_scoped_to_the_owning_player()
    {
        var instanceId = SeedInstance();
        _store.SaveItem(new RpgItemRow { InstanceId = instanceId, PlayerId = "p2", AcquiredUtc = "2026-01-01T00:00:00Z" });
        _store.SaveLoadout(
            new RpgItemLoadoutRow("lo-1", "p1", "Offense", null, "2026-01-01T00:00:00Z", 0),
            new[] { new RpgItemLoadoutEntryRow("lo-1", "armament-primary", "item", instanceId) });

        Assert.Equal(FusionRpg.Core.Items.LoadoutEntryState.Missing,
            Assert.Single(_store.GetLoadoutEntriesValidated("lo-1", "p1")).State);
    }

    /// <summary>An unrecognised `ref_kind` cannot be resolved, so it reports a visible hole rather
    /// than passing silently.</summary>
    [Fact]
    public void An_unreadable_ref_kind_is_reported_missing_rather_than_assumed_present()
    {
        _store.SaveLoadout(
            new RpgItemLoadoutRow("lo-1", "p1", "Offense", null, "2026-01-01T00:00:00Z", 0),
            new[] { new RpgItemLoadoutEntryRow("lo-1", "core-guard", "sorcery", "whatever") });

        Assert.Equal(FusionRpg.Core.Items.LoadoutEntryState.Missing,
            Assert.Single(_store.GetLoadoutEntriesValidated("lo-1", "p1")).State);
    }

    [Fact]
    public void FindAssignmentHolders_names_the_cell_holding_a_pinned_copy()
    {
        // ⛔ Was `"item"` until 2026-09-06 (defect R2), matching the method's then-default. That is
        // `rpg_item_loadout_entry`'s kind; this query reads `rpg_item_assignment`, whose
        // instance-pinned kind is `rolled` — so the pair agreed while both were wrong, and every
        // really-worn copy was reported free.
        _store.SaveAssignment("spec-A", FusionRpg.Core.Items.ItemRole.ArmamentPrimary,
            FusionRpg.Core.Items.EquipRefKinds.Rolled, "inst-1");

        var held = _store.FindAssignmentHolders(new[] { "inst-1", "inst-unheld" });

        var cell = Assert.Contains("inst-1", (IDictionary<string, FusionRpg.Core.Items.LoadoutCell>)held);
        Assert.Equal("spec-A", cell.SpecimenId);
        Assert.Equal("armament-primary", cell.Role);
        Assert.DoesNotContain("inst-unheld", held.Keys);
    }

    [Fact]
    public void Source_scan_ignores_build_output_and_nested_checkouts()
    {
        var sep = Path.DirectorySeparatorChar;

        // The tree's own source is scanned.
        Assert.True(IsScannedSourcePath($"D:{sep}repo{sep}src{sep}FusionRpg.Data{sep}Sqlite{sep}RpgStore.Items.cs"));

        // Build output.
        Assert.False(IsScannedSourcePath($"D:{sep}repo{sep}tests{sep}bin{sep}Release{sep}RpgStore.Items.cs"));
        Assert.False(IsScannedSourcePath($"D:{sep}repo{sep}tests{sep}obj{sep}RpgStore.Items.cs"));

        // A nested git checkout / managed worktree carries its own copy of every source file — the
        // exact case that made a repo-wide scan find two RpgStore.Items.cs.
        Assert.False(IsScannedSourcePath($"D:{sep}repo{sep}.kilo{sep}worktrees{sep}solid-run{sep}src{sep}FusionRpg.Data{sep}Sqlite{sep}RpgStore.Items.cs"));
        Assert.False(IsScannedSourcePath($"D:{sep}repo{sep}.claude{sep}worktrees{sep}agent-x{sep}src{sep}RpgStore.Items.cs"));
        Assert.False(IsScannedSourcePath($"D:{sep}repo{sep}.opencode{sep}src{sep}RpgStore.Items.cs"));
        Assert.False(IsScannedSourcePath($"D:{sep}repo{sep}.git{sep}modules{sep}RpgStore.Items.cs"));
    }
}
