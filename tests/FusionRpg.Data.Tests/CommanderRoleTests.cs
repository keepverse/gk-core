using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Saves;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>`commander-roster` EP3.1 — `rpg_commander_role` (spec-commander-roster.md "The role is a
/// binding", testing 2 and 3).
///
/// The role is ADDITIVE: grant inserts, revoke deletes, and the creature is unchanged either way —
/// same `rpg_unique_actors` row, same level, gear, allocation and phase. The creature is the noun;
/// commander is an adjective. There is no roster size limit: a roster is a population.
/// </summary>
public class CommanderRoleTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly long _playerId;
    readonly EmpireRef _empire;

    public CommanderRoleTests()
    {
        _testStore = DataTestStore.Create();          // in memory, per the test-substrate standard
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _empire = new EmpireRef(new SaveId(_playerId), _store.HumanEmpireOf(_playerId));
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void Grant_makes_a_specimen_a_commander_and_revoke_takes_it_back()
    {
        var actor = _store.CreateUniqueActor(_playerId, "plant", 3);

        Assert.False(_store.HasCommanderRole(_empire, actor.InstanceId));
        Assert.Empty(_store.ListCommanderRoleInstanceIds(_empire));

        var granted = _store.GrantCommanderRole(_empire, actor.InstanceId);

        Assert.True(granted.Ok, granted.Reason);
        Assert.Equal("", granted.Reason);
        Assert.True(_store.HasCommanderRole(_empire, actor.InstanceId));
        Assert.Equal(new[] { actor.InstanceId }, _store.ListCommanderRoleInstanceIds(_empire));

        // Idempotent both ways: the primary key IS the binding, so a re-grant is one row and revoking
        // a role the creature does not hold is a no-op rather than a refusal.
        Assert.True(_store.GrantCommanderRole(_empire, actor.InstanceId).Ok);
        Assert.Single(_store.ListCommanderRoleInstanceIds(_empire));
        Assert.True(_store.RevokeCommanderRole(_empire, actor.InstanceId).Ok);
        Assert.True(_store.RevokeCommanderRole(_empire, actor.InstanceId).Ok);

        Assert.False(_store.HasCommanderRole(_empire, actor.InstanceId));
        Assert.Empty(_store.ListCommanderRoleInstanceIds(_empire));
    }

    [Fact]
    public void The_roster_is_a_population_so_three_commanders_are_three_rows_in_ordinal_order()
    {
        // No size limit (spec: "a roster is a population; a count limit would be a ceiling"), and the
        // list is ordinal by instance id so a roster read is reproducible.
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var actor = _store.CreateUniqueActor(_playerId, "plant", i + 1);
            Assert.True(_store.GrantCommanderRole(_empire, actor.InstanceId).Ok);
            ids.Add(actor.InstanceId);
        }

        var listed = _store.ListCommanderRoleInstanceIds(_empire);

        Assert.Equal(3, listed.Count);
        Assert.Equal(ids.OrderBy(id => id, StringComparer.Ordinal), listed);
    }

    [Fact]
    public void Grant_then_revoke_leaves_the_creature_byte_identical()
    {
        var actor = _store.CreateUniqueActor(_playerId, "zombie", 2);

        // Every raw row of every table that carries an `instance_id` — the creature's own row, its
        // pools, recovery, allocation and gear — not just the fields this test remembered to name.
        var before = _store.SnapshotInstanceRowsForTests(actor.InstanceId);
        var phaseBefore = _store.GetUniqueActor(actor.InstanceId)!.Phase;
        Assert.NotEmpty(before);   // envelope: the snapshot really saw the creature

        Assert.True(_store.GrantCommanderRole(_empire, actor.InstanceId).Ok);
        Assert.True(_store.RevokeCommanderRole(_empire, actor.InstanceId).Ok);

        var after = _store.SnapshotInstanceRowsForTests(actor.InstanceId);
        Assert.Equal(before, after);
        Assert.Equal(phaseBefore, _store.GetUniqueActor(actor.InstanceId)!.Phase);
    }

    [Fact]
    public void The_three_refusals_are_named_and_write_nothing()
    {
        var retired = _store.CreateUniqueActor(_playerId, "plant", 5);
        var retiredOutcome = _store.TryRetireUniqueActor(retired.InstanceId);
        Assert.True(retiredOutcome.Ok, retiredOutcome.Reason);
        Assert.Equal("Retired", _store.GetUniqueActor(retired.InstanceId)!.Phase);

        // A save that is not this one: the ownership predicate is (save, empire), so the id alone is
        // not enough to grant anything.
        var otherSave = new EmpireRef(new SaveId(_playerId + 1), _empire.Empire);

        var retiredRefusal = _store.GrantCommanderRole(_empire, retired.InstanceId);
        var notOwnerRefusal = _store.GrantCommanderRole(otherSave, retired.InstanceId);
        var unknownRefusal = _store.GrantCommanderRole(_empire, "no-such-instance");

        Assert.False(retiredRefusal.Ok);
        Assert.Equal(RpgStore.CommanderRoleRetired, retiredRefusal.Reason);
        Assert.False(notOwnerRefusal.Ok);
        Assert.Equal(RpgStore.CommanderRoleNotOwner, notOwnerRefusal.Reason);
        Assert.False(unknownRefusal.Ok);
        Assert.Equal(RpgStore.CommanderRoleUnknown, unknownRefusal.Reason);

        // Nothing was written by any of the three refusals.
        Assert.Empty(_store.ListCommanderRoleInstanceIds(_empire));
        Assert.Empty(_store.ListCommanderRoleInstanceIds(otherSave));

        // A revoke refuses the same way for an unknown or unowned instance, and a RETIRED specimen's
        // role can still be removed: taking a binding away is never blocked by a phase.
        Assert.Equal(RpgStore.CommanderRoleUnknown,
            _store.RevokeCommanderRole(_empire, "no-such-instance").Reason);
        Assert.Equal(RpgStore.CommanderRoleNotOwner,
            _store.RevokeCommanderRole(otherSave, retired.InstanceId).Reason);
        Assert.True(_store.RevokeCommanderRole(_empire, retired.InstanceId).Ok);
    }

    [Fact]
    public void A_creature_in_any_other_phase_may_be_granted_the_role()
    {
        // Where it may then LEAD is the place's rule (lawn-commander-seat, legion-commander), never
        // this table's: only `Retired` refuses, and `Roster`/`Deploying`/`Recovering` all grant.
        var actor = _store.CreateUniqueActor(_playerId, "plant", 1);

        Assert.Equal("Roster", _store.GetUniqueActor(actor.InstanceId)!.Phase);
        Assert.True(_store.GrantCommanderRole(_empire, actor.InstanceId).Ok);
        Assert.True(_store.HasCommanderRole(_empire, actor.InstanceId));
    }

    [Fact]
    public void A_real_role_row_makes_a_creature_a_commander_through_the_directory_source()
    {
        // The real-role-row half of `commander-roster` testing 1: the source is fed by THIS store's own
        // `rpg_commander_role` rows, so the creature is resolvable, listable and nameable with no code
        // change per creature — nothing about `specimen` is hard-coded anywhere below.
        var authored = new DataCommanderDirectory(new[]
        {
            new CommanderRow("commander:dave", _empire.Empire, "Commander", true, "player:{id}"),
        });
        var directory = authored.WithSource(new UniqueCommanderSource(authored, _store));
        var actor = _store.CreateUniqueActor(_playerId, "plant", 4);
        // `CreateUniqueActor` writes no `rpg_creature_profiles` row (that path is `MintCreature`), so
        // this creature has no readable name yet: the roster label falls back to the instance id rather
        // than going blank. The nickname-else-species rule itself is the shared helper the actor sheet
        // and this reader both call, asserted directly here.
        Assert.Equal("Sir Sprout", CreatureDisplayName.For("Sir Sprout", null));
        Assert.Null(CreatureDisplayName.For(null, "no-such-species"));

        var stableId = UniqueCommanderSource.UniquePrefix + actor.InstanceId;
        Assert.False(directory.TryResolve(stableId, out _));            // no role yet
        Assert.True(_store.GrantCommanderRole(_empire, actor.InstanceId).Ok);
        Assert.True(directory.TryResolve(stableId, out var creature));

        Assert.Equal(_empire.Empire, directory.EmpireOf(creature));
        Assert.Equal(actor.InstanceId, directory.DisplayName(creature, "Player"));   // never blank
        Assert.Equal(
            new[] { "commander:dave", stableId },
            ((ICommanderRoster)directory).ForEmpire(_empire).Select(r => r.StableId));

        // R4: the species allocation for this empire reads the SAME scope key before and after the
        // grant, because a creature commander's key is the empire's default commander's key.
        directory.TryResolve("commander:dave", out var shipped);
        Assert.Equal(
            directory.AllocationScopeKey(shipped, _playerId),
            directory.AllocationScopeKey(creature, _playerId));

        // Revoking takes it back out of the roster, and the directory answers exactly as it did before.
        Assert.True(_store.RevokeCommanderRole(_empire, actor.InstanceId).Ok);
        Assert.False(directory.TryResolve(stableId, out _));
        Assert.Equal(new[] { "commander:dave" },
            ((ICommanderRoster)directory).ForEmpire(_empire).Select(r => r.StableId));
    }

    [Fact]
    public void Revoking_the_seated_default_resets_it_in_the_same_transaction()
    {
        // The seat read validates its stored stable id THROUGH the directory, so the hub must carry the
        // role source for a creature's seat to read back — production wires this in Program.cs; the Data
        // bootstrap configures the authored rows only, so this test composes the source and restores it.
        var previous = CommanderDirectoryHub.Current;
        var authored = DataCommanderDirectory.Parse(File.ReadAllText(Path.Combine(
            FindRepoRoot(), "data", "seed", "commanders", "_registry", "default-commanders.v1.json")));
        CommanderDirectoryHub.Configure(authored.WithSource(new UniqueCommanderSource(authored, _store)));
        try
        {
        var actor = _store.CreateUniqueActor(_playerId, "plant", 9);
        var stableId = UniqueCommanderSource.UniquePrefix + actor.InstanceId;
        Assert.True(_store.GrantCommanderRole(_empire, actor.InstanceId).Ok);

        var authoredDefault = CommanderDirectoryHub.Current.DefaultFor(_empire.Empire).StableId;
        Assert.True(_store.SetDefaultLawnCommanderId(_playerId, stableId).Ok);
        Assert.Equal(stableId, _store.GetDefaultLawnCommanderId(_playerId));

        // The revoke resets the seat in the SAME call, so the default never points at a non-commander.
        Assert.True(_store.RevokeCommanderRoleAndResetDefault(_empire, actor.InstanceId).Ok);
        Assert.Equal(authoredDefault, _store.GetDefaultLawnCommanderId(_playerId));
        Assert.False(_store.HasCommanderRole(_empire, actor.InstanceId));

        // A refused revoke changes nothing at all — not the role, not the seat.
        Assert.True(_store.GrantCommanderRole(_empire, actor.InstanceId).Ok);
        Assert.True(_store.SetDefaultLawnCommanderId(_playerId, stableId).Ok);
        var refused = _store.RevokeCommanderRoleAndResetDefault(_empire, "no-such-instance");
        Assert.False(refused.Ok);
        Assert.Equal(RpgStore.CommanderRoleUnknown, refused.Reason);
        Assert.Equal(stableId, _store.GetDefaultLawnCommanderId(_playerId));
        Assert.True(_store.HasCommanderRole(_empire, actor.InstanceId));
        }
        finally { CommanderDirectoryHub.Configure(previous); }
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    [Fact]
    public void Delve_admission_ignores_the_role_a_role_holder_reads_exactly_as_before()
    {
        // Spec testing 6: a role-holder joins a delve party exactly as any unique (R-C2). Admission is
        // decided from the creature's own rows — its phase, level, gear and allocation — and NO delve,
        // encounter or admission path reads `rpg_commander_role` (the only readers are the directory and
        // this roster). So the proof is that those rows are untouched by a grant: the same snapshot that
        // EP3.1's byte-identity test uses, taken across the grant.
        var actor = _store.CreateUniqueActor(_playerId, "zombie", 3);
        static IReadOnlyList<string> CreatureRows(string instanceId, RpgStore store) =>
            store.SnapshotInstanceRowsForTests(instanceId)
                .Where(row => !row.StartsWith("rpg_commander_role:", StringComparison.Ordinal))
                .ToList();

        var before = CreatureRows(actor.InstanceId, _store);
        var allBefore = _store.SnapshotInstanceRowsForTests(actor.InstanceId);
        var dtoBefore = _store.GetUniqueActor(actor.InstanceId)!;
        Assert.NotEmpty(before);

        Assert.True(_store.GrantCommanderRole(_empire, actor.InstanceId).Ok);

        var allAfter = _store.SnapshotInstanceRowsForTests(actor.InstanceId);
        // The ONLY new row anywhere for this instance is the role binding itself: every row an
        // admission decision reads is byte-identical, which is what "ignores the role" means here.
        Assert.Equal(before, CreatureRows(actor.InstanceId, _store));
        Assert.Equal(
            allBefore.Concat(new[] { allAfter.Except(allBefore).Single() }).OrderBy(r => r, StringComparer.Ordinal),
            allAfter.OrderBy(r => r, StringComparer.Ordinal));
        Assert.StartsWith("rpg_commander_role:", allAfter.Except(allBefore).Single());
        var dtoAfter = _store.GetUniqueActor(actor.InstanceId)!;
        Assert.Equal(dtoBefore.Phase, dtoAfter.Phase);
        Assert.Equal(dtoBefore.Level, dtoAfter.Level);
        Assert.Equal(dtoBefore.Side, dtoAfter.Side);
        Assert.Equal(dtoBefore.TypeId, dtoAfter.TypeId);
        // `CreateUniqueActor` writes no `rpg_creature_profiles` row (that path is `MintCreature`), so
        // there is no profile to compare here — the actor row and the DTO above are the admission
        // substrate, and both are byte-identical across the grant.
    }
}
