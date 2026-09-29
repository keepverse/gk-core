using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// A-E1 (spec-eligibility-axis.md §6a gate 2): the six new <c>rpg_action</c> columns follow
/// <c>effect_instance</c>'s own T3.4 migration precedent (<c>RpgStore.AtomInstances.cs:100-106</c>) —
/// <c>EnsureColumn</c>, additive, defaults apply only to rows written before this module shipped. This
/// file proves both halves: a fresh round trip through the real public API, and a database created
/// with the PRE-module column set still loads after the columns are added underneath it.
/// </summary>
public class EligibilityAxisMigrationTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public EligibilityAxisMigrationTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        SeedContainerAndAtom("skill.test");
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>Same shape as <c>ActionStoreTests</c>' own helper — <see cref="RpgStore.UpsertAction"/>
    /// validates the container exists before it will accept a row at all.</summary>
    void SeedContainerAndAtom(string containerId)
    {
        var atomId = AtomRow.DeriveId("atom.eligibility-test", "", 1);
        var atomResult = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom.eligibility-test",
            Variant = "",
            Tier = 1,
            Name = "Eligibility Test",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        });
        Assert.True(atomResult.IsOk, atomResult.ToString());

        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());
    }

    [Fact]
    public void All_six_new_fields_round_trip_through_UpsertAction_and_GetAction()
    {
        var row = new ActionRow
        {
            ActionId = "action.species.test.001",
            Name = "Test eligibility row",
            Kind = ActionKind.Skill,
            ContainerId = "skill.test",
            Scope = EligibilityScope.Species,
            ScopeKey = "cherrybomb",
            Category = ActionCategory.Attack,
            PairingRole = PairingRole.Enabler,
            StructureAxes = new[] { "riderStatus" },
            AtomFamilies = new[] { "atom.searing-strike", "atom.volley" },
            RungBand = new RungBand(1, 10),
        };

        var rejection = _store.UpsertAction(row);
        Assert.True(rejection.IsOk, rejection.Detail);

        var read = _store.GetAction("action.species.test.001");
        Assert.NotNull(read);
        Assert.Equal(EligibilityScope.Species, read!.Scope);
        Assert.Equal("cherrybomb", read.ScopeKey);
        Assert.Equal(ActionCategory.Attack, read.Category);
        Assert.Equal(PairingRole.Enabler, read.PairingRole);
        Assert.Equal(new[] { "riderStatus" }, read.StructureAxes);
        Assert.Equal(new[] { "atom.searing-strike", "atom.volley" }, read.AtomFamilies);
        Assert.NotNull(read.RungBand);
        Assert.Equal(1, read.RungBand!.Floor);
        Assert.Equal(10, read.RungBand.Ceiling);
        Assert.Equal(10, read.RungBand.Collapse());
    }

    [Fact]
    public void An_unset_row_defaults_to_general_scope_and_none_pairing_with_no_category_or_band()
    {
        var row = new ActionRow
        {
            ActionId = "action.general.0001",
            Name = "Plain basic",
            Kind = ActionKind.Basic,
            ContainerId = "skill.test",
        };
        var rejection = _store.UpsertAction(row);
        Assert.True(rejection.IsOk, rejection.Detail);

        var read = _store.GetAction("action.general.0001")!;
        Assert.Equal(EligibilityScope.General, read.Scope);
        Assert.Null(read.ScopeKey);
        Assert.Null(read.Category);
        Assert.Equal(PairingRole.None, read.PairingRole);
        Assert.Empty(read.StructureAxes);
        Assert.Empty(read.AtomFamilies);
        Assert.Null(read.RungBand);
    }

    /// <summary>The migration itself: a database whose <c>rpg_action</c> table predates this module
    /// (the exact 31-column shape <c>RpgStore.Actions.cs</c> had before A-E1) still loads through
    /// <see cref="RpgStore.GetAction"/> once <see cref="RpgStore.Init"/> runs the new
    /// <c>EnsureColumn</c> calls over it — matching what a real player save does on the first boot
    /// after this module ships. Uses its own directory (not <see cref="_dir"/>'s already-`Init`ed
    /// store) so the raw SQL below is the FIRST thing to touch <c>rpg_action</c>.</summary>
    [Fact]
    public void A_pre_module_database_gains_the_new_columns_and_still_loads_with_their_defaults()
    {
        // The memory equivalent of "a save from before the migration existed": hand-build the OLD
        // rpg_action shape (pre-A-E1) and one row through raw SQL before Init runs, then boot in
        // memory. No file is created.
        using var test = DataTestStore.CreateWithPreInitHot(seed =>
        {
            using var create = seed.CreateCommand();
            create.CommandText = """
                CREATE TABLE rpg_action (
                  action_id TEXT NOT NULL PRIMARY KEY,
                  name TEXT NOT NULL DEFAULT '',
                  kind TEXT NOT NULL,
                  rung INTEGER NOT NULL DEFAULT 0,
                  tags_json TEXT NOT NULL DEFAULT '[]',
                  enabled INTEGER NOT NULL DEFAULT 1,
                  revision INTEGER NOT NULL DEFAULT 0,
                  grantable INTEGER NOT NULL DEFAULT 0,
                  default_attack_eligible INTEGER NOT NULL DEFAULT 0,
                  container_id TEXT NOT NULL,
                  time_cost_ticks INTEGER NOT NULL DEFAULT 0,
                  speed_channel TEXT NOT NULL DEFAULT '',
                  cooldown_channel TEXT,
                  windup_ticks INTEGER NOT NULL DEFAULT 0,
                  resolve_offsets_json TEXT NOT NULL DEFAULT '[0]',
                  recovery_ticks INTEGER NOT NULL DEFAULT 0,
                  commitment TEXT NOT NULL DEFAULT 'LateBound',
                  interruptible TEXT NOT NULL DEFAULT 'OnCC',
                  interrupt_refund_milli INTEGER NOT NULL DEFAULT 0,
                  slot_consuming INTEGER NOT NULL DEFAULT 1,
                  priority_band INTEGER NOT NULL DEFAULT 0,
                  cooldown_class TEXT NOT NULL DEFAULT 'None',
                  cooldown_key TEXT,
                  cooldown_ticks INTEGER NOT NULL DEFAULT 0,
                  starts_at TEXT NOT NULL DEFAULT 'Resolve',
                  interrupt_cooldown_milli INTEGER NOT NULL DEFAULT 1000,
                  target_spec_json TEXT,
                  min_range INTEGER NOT NULL DEFAULT 0,
                  max_range INTEGER NOT NULL DEFAULT 0,
                  range_channel TEXT,
                  requires_line_of_sight INTEGER NOT NULL DEFAULT 0,
                  conditions_json TEXT
                );
                """;
            create.ExecuteNonQuery();

            using var insert = seed.CreateCommand();
            insert.CommandText = """
                INSERT INTO rpg_action (action_id, name, kind, container_id, speed_channel)
                VALUES ('action.legacy.001', 'Pre-migration action', 'basic', '', 'speed');
                """;
            insert.ExecuteNonQuery();
        });

        // Init ran EnsureActionSchemaUnlocked: its CREATE TABLE IF NOT EXISTS was a no-op against the
        // table above, and its EnsureColumn calls added the six new columns underneath the old row.
        var read = test.Store.GetAction("action.legacy.001");
        Assert.NotNull(read);
        Assert.Equal(EligibilityScope.General, read!.Scope);
        Assert.Null(read.ScopeKey);
        Assert.Null(read.Category);
        Assert.Equal(PairingRole.None, read.PairingRole);
        Assert.Empty(read.StructureAxes);
        Assert.Empty(read.AtomFamilies);
        Assert.Null(read.RungBand);
    }
}
