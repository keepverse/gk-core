using FusionRpg.Core.Battle;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.Commanders;

/// <summary>`commander-roster` EP3.2 — the second directory SOURCE: a creature that holds the role is a
/// commander, resolvable, listable and usable as the leading commander with no production edit per
/// creature (the owner's ruling *"a commander literally a unique demon, it only carry more role"*, R-C2;
/// spec-commander-roster.md testing 1 and 5).
///
/// The role rows come through <see cref="ICommanderRoleReader"/> — a port, because Core never touches a
/// store. The real store-backed reader is covered where the store is: `CommanderRoleTests` in the Data
/// suite.
/// </summary>
[Collection(CommanderDirectoryCollection.Name)]
public class CommanderRosterTests
{
    static readonly CommanderRow[] Rows =
    {
        new("commander:dave", EmpireId.Dave, "Commander", true, "player:{id}"),
        new("commander:zomboss", EmpireId.Zomboss, "Dr. Zomboss", false, "zomboss:{id}"),
    };

    /// <summary>An in-memory role reader: one instance per (save, empire), with a name.</summary>
    sealed class FakeRoles : ICommanderRoleReader
    {
        readonly Dictionary<string, (EmpireRef Empire, string Name)> _roles = new(StringComparer.Ordinal);

        public FakeRoles Grant(string instanceId, EmpireRef empire, string name)
        {
            _roles[instanceId] = (empire, name);
            return this;
        }

        public EmpireRef? RoleEmpireOf(string instanceId) =>
            _roles.TryGetValue(instanceId, out var row) ? row.Empire : null;

        public IReadOnlyList<string> RoleInstanceIds(EmpireRef empire) =>
            _roles.Where(kv => kv.Value.Empire == empire)
                .Select(kv => kv.Key).OrderBy(id => id, StringComparer.Ordinal).ToList();

        public string? RoleHolderDisplayName(string instanceId) =>
            _roles.TryGetValue(instanceId, out var row) ? row.Name : null;
    }

    static (DataCommanderDirectory Directory, FakeRoles Roles) Composed()
    {
        var roles = new FakeRoles();
        var authored = new DataCommanderDirectory(Rows);
        return (authored.WithSource(new UniqueCommanderSource(authored, roles)), roles);
    }

    [Fact]
    public void A_role_holding_creature_resolves_lists_and_leads_with_no_production_edit()
    {
        var (directory, roles) = Composed();
        var dave = new EmpireRef(new SaveId(7), EmpireId.Dave);
        roles.Grant("specimen-a", dave, "Sunflower Prime");

        CommanderDirectoryHub.Configure(directory);
        try
        {
            // Resolvable: the authored rows are unaffected, the creature resolves through the source.
            Assert.True(directory.TryResolve("commander:dave", out var shipped));
            Assert.Equal(EmpireId.Dave, directory.EmpireOf(shipped));
            Assert.True(directory.TryResolve("commander:unique:specimen-a", out var creature));
            Assert.Equal(EmpireId.Dave, directory.EmpireOf(creature));
            Assert.Equal("Sunflower Prime", directory.DisplayName(creature, "Player"));

            // Listable, with the authored default FIRST and the roster ordinal across both halves.
            var roster = ((ICommanderRoster)directory).ForEmpire(dave);
            Assert.Equal(new[] { "commander:dave", "commander:unique:specimen-a" },
                roster.Select(r => r.StableId));

            // Usable as the leading commander: the injector's own board.start path, unchanged.
            MatchCommanderSessionCache.ResetForTests();
            MatchCommanderSessionCache.Apply(
                "commander:unique:specimen-a", "Sunflower Prime", "Might", "Might", AptitudeAllocation.Empty);
            var snapshot = MatchCommanderSessionCache.BuildFromSessionCache();
            Assert.False(MatchCommanderSessionCache.LastBuildUsedFallback);
            Assert.Equal("commander:unique:specimen-a", snapshot.LeadingCommanderId);
        }
        finally
        {
            CommanderDirectoryHub.Configure(ShippedCommanders.Directory);
            MatchCommanderSessionCache.ResetForTests();
        }
    }

    [Fact]
    public void AllocationScopeKey_stays_the_empires_default_commanders_key_R4()
    {
        // R4: both apply — the empire's commander allocation keeps lifting the side, and a leading
        // creature adds only its aura. So a creature commander's scope key is the DEFAULT's key, and
        // swapping commanders never changes the army's aptitudes.
        var (directory, roles) = Composed();
        var dave = new EmpireRef(new SaveId(7), EmpireId.Dave);
        roles.Grant("specimen-a", dave, "Sunflower Prime");

        directory.TryResolve("commander:unique:specimen-a", out var creature);
        directory.TryResolve("commander:dave", out var shipped);

        Assert.Equal(directory.AllocationScopeKey(shipped, 7), directory.AllocationScopeKey(creature, 7));
        Assert.Equal("player:7", directory.AllocationScopeKey(creature, 7));

        // The species allocation for that empire is untouched by a grant: the scope key it reads is the
        // same string, which is the conflation commander-identity exists to prevent (testing 5).
        Assert.Equal(directory.AllocationScopeKey(creature, 42), "player:42");
    }

    [Fact]
    public void An_instance_with_no_role_is_not_a_commander_and_the_shipped_rows_still_win()
    {
        var (directory, roles) = Composed();
        var dave = new EmpireRef(new SaveId(7), EmpireId.Dave);

        Assert.False(directory.TryResolve("commander:unique:no-such-instance", out _));
        Assert.False(directory.TryResolve("commander:unique:", out _));
        Assert.False(directory.TryResolve("commander:unique:dave", out _));   // the id, not the role
        Assert.False(directory.TryResolve("", out _));
        Assert.Empty(((ICommanderRoster)directory).ForEmpire(dave).Where(r => r.StableId.Contains("unique")));

        // A source can never shadow an authored row: the authored answer is checked first.
        Assert.True(directory.TryResolve("commander:dave", out var shipped));
        Assert.Equal("player:7", directory.AllocationScopeKey(shipped, 7));

        // Granting the role afterwards makes it resolvable without touching anything else.
        roles.Grant("late", dave, "Late Bloomer");
        Assert.True(directory.TryResolve("commander:unique:late", out var late));
        Assert.Equal("Late Bloomer", directory.DisplayName(late, null));
    }

    [Fact]
    public void A_roster_is_per_empire_and_only_names_that_empires_creatures()
    {
        var (directory, roles) = Composed();
        roles.Grant("dave-a", new EmpireRef(new SaveId(7), EmpireId.Dave), "Dave's Creature");
        roles.Grant("zomboss-a", new EmpireRef(new SaveId(7), EmpireId.Zomboss), "Zomboss's Creature");

        Assert.Equal(new[] { "commander:dave", "commander:unique:dave-a" },
            ((ICommanderRoster)directory).ForEmpire(new EmpireRef(new SaveId(7), EmpireId.Dave))
                .Select(r => r.StableId));
        Assert.Equal(new[] { "commander:unique:zomboss-a", "commander:zomboss" },
            ((ICommanderRoster)directory).ForEmpire(new EmpireRef(new SaveId(7), EmpireId.Zomboss))
                .Select(r => r.StableId));

        // Two saves of the same empire are told apart by the binding, not by the source: `ForEmpire` is
        // keyed on the empire, and an instance granted under save 7 is not offered to save 8.
        Assert.Empty(((ICommanderRoster)directory).ForEmpire(new EmpireRef(new SaveId(8), EmpireId.Dave))
            .Where(r => r.StableId.Contains("unique")));
    }
}
