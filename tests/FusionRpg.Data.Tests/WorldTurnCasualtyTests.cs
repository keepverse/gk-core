using FusionRpg.Contracts;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// commander-roster EP3.12 (`spec-legion-commander.md` "Casualty — no commander special"): a commander
/// member reduced to zero in a siege is handled as **any unique** is — no commander-specific stat path
/// and no second rule. **`injury-tiers` (`deployment-hierarchy`) is the program that defines how a
/// unique is wounded or killed**; until it ships, the interim rule is
/// <c>RpgStore.ApplyCommanderCasualtiesUnlocked</c>'s: detach the fallen commander from its legion and
/// set its specimen `Recovering`, the non-lethal default the delve's own downed-unique path already
/// writes.
///
/// <para>Both facts are read back through the store's normal read path, never from the pass's own
/// return value: the legion comes from <see cref="RpgStore.LoadWorldState"/> (so the detachment is
/// proven to have been PERSISTED) and the phase from <see cref="RpgStore.GetUniqueActor"/>.</para>
/// </summary>
public class WorldTurnCasualtyTests : IDisposable
{
    const long PlayerId = 1;
    const string LegionId = "e-dave-legion-1";
    const string HostileSector = "black-gate";
    const string HostileEntity = "e-zomboss-band-1";
    static readonly string[] AllCommanders = { "dave", "wild", "zomboss" };

    /// <summary>A member that cannot realistically be killed in one assault — used for the side that
    /// must survive, so a red test means "the rule moved" rather than "the fight went the other way".</summary>
    const long UnkillableHp = 1_000_000;

    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldTurnCasualtyTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        // A district assault ever being COMMITTED is what is new here. The hub this path reads
        // (`AptitudeTuningHub`) is configured ONCE for the whole assembly by
        // `ContractTuningTestBootstrap`, so this file no longer carries a private copy of that call
        // (test-substrate TVB-F16).
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>
    /// `first-light` with Dave's legion standing INSIDE a Zomboss-HELD district, facing a force that
    /// outclasses it, plus one `Commander` member carrying <paramref name="instanceId"/> at
    /// <paramref name="commanderHp"/> of its full HP. A near-invulnerable Fighter rides along so the
    /// LEGION outlives the fight either way: "the member died" and "the entity left the map" must not
    /// be able to look alike.
    /// </summary>
    static WorldState SiegeScenario(string worldId, string instanceId, long commanderHp)
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 99, worldId: worldId);
        var legion = built.Entities.Single(e => e.EntityId == LegionId);

        return WorldValidation.Validate(built with
        {
            Sectors = built.Sectors
                .Select(s => s.SectorId == HostileSector
                    ? s with { OwnerFactionId = "zomboss", Phase = SectorPhase.Held }
                    : s)
                .ToList(),
            Entities = built.Entities
                .Select(e => e.EntityId == LegionId
                    ? e with
                    {
                        AtSectorId = HostileSector,
                        OnLaneId = null,
                        OnLaneTowardSectorId = null,
                        Members = new[]
                        {
                            new WorldEntityMember
                            {
                                SpeciesId = legion.Members[0].SpeciesId, Level = 50, Hp = UnkillableHp,
                            },
                            new WorldEntityMember
                            {
                                InstanceId = instanceId, SpeciesId = legion.Members[0].SpeciesId, Level = 3,
                                Hp = commanderHp, Role = WorldEntityMemberRole.Commander,
                            },
                        },
                    }
                    : e)
                .Select(e => e.EntityId == HostileEntity
                    ? e with
                    {
                        AtSectorId = HostileSector,
                        Members = Enumerable.Range(0, 5)
                            .Select(_ => new WorldEntityMember { SpeciesId = "normalzombie", Level = 50, Hp = UnkillableHp })
                            .ToList(),
                    }
                    : e)
                .OrderBy(e => e.EntityId, StringComparer.Ordinal)
                .ToList(),
        });
    }

    /// <summary>`first-light` untouched except that Dave's legion carries one `Commander` member with
    /// its specimen already at zero effective HP — no assault order, so only the commit itself (and
    /// the sweep inside it) can explain what the test sees.</summary>
    static WorldState ZeroHpCommanderScenario(string worldId, string instanceId, WorldEntityMemberRole role)
    {
        var built = WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 99, worldId: worldId);
        var legion = built.Entities.Single(e => e.EntityId == LegionId);

        return WorldValidation.Validate(built with
        {
            Entities = built.Entities
                .Select(e => e.EntityId == LegionId
                    ? e with
                    {
                        Members = e.Members.Append(new WorldEntityMember
                        {
                            InstanceId = instanceId, SpeciesId = legion.Members[0].SpeciesId, Level = 3,
                            Hp = 100, Wounds = 100, Role = role,
                        }).ToList(),
                    }
                    : e)
                .ToList(),
        });
    }

    static IReadOnlyList<WorldCommand> Order(string kind, string? entityId = null, string? sectorId = null) =>
        new[]
        {
            new WorldCommand { CommanderId = "dave", CommandId = "c-dave", Kind = kind, EntityId = entityId, SectorId = sectorId },
            new WorldCommand { CommanderId = "wild", CommandId = "c-wild", Kind = WorldCommandKinds.StandFast },
            new WorldCommand { CommanderId = "zomboss", CommandId = "c-zomboss", Kind = WorldCommandKinds.StandFast },
        };

    /// <summary>
    /// Every commander commits the same turn. The scripted factions commit first — an explicit commit
    /// speaks for a faction and keeps the AI auto-fill out of it — and the human commits last, which
    /// is the one that releases the barrier.
    /// </summary>
    WorldTurnCommitResult CommitAll(string worldId)
    {
        var open = _store.GetWorldHeader(worldId)!.CurrentTurn;
        WorldTurnCommitResult last = default!;
        foreach (var commander in AllCommanders.Where(c => c != "dave").Concat(new[] { "dave" }))
            last = _store.CommitWorldTurn(worldId, commander, open);
        return last;
    }

    [Fact]
    public void A_commander_member_that_dies_in_a_siege_is_detached_and_its_specimen_set_Recovering()
    {
        var actor = _store.CreateUniqueActor(PlayerId, "plant", 3);
        const string worldId = "w-siege-casualty";
        var (ok, reason, _) = _store.CreateWorld(PlayerId, SiegeScenario(worldId, actor.InstanceId, commanderHp: 1));
        Assert.True(ok, reason);

        _store.SubmitWorldCommands(worldId, Order(WorldCommandKinds.Assault, LegionId, HostileSector));
        var committed = CommitAll(worldId);
        Assert.True(committed.Advanced, committed.Reason);

        // Read back through the store's normal paths: the phase moved, and the member is gone from the
        // persisted graph. The legion itself is still on the map (the Fighter is unkillable), so the two
        // facts cannot be confused with "the whole entity was destroyed".
        Assert.Equal(UniqueActorPhases.Recovering, _store.GetUniqueActor(actor.InstanceId)!.Phase);

        var legion = _store.LoadWorldState(worldId)!.Entities.SingleOrDefault(e => e.EntityId == LegionId);
        Assert.NotNull(legion);
        Assert.DoesNotContain(legion!.Members, m => m.Role == WorldEntityMemberRole.Commander);
    }

    /// <summary>
    /// The negative control for the test above, and the reason it can be trusted: the SAME siege, the
    /// same orders, a commander that survives it changes nothing. Without this, "Recovering" could be
    /// something every committed turn writes.
    /// </summary>
    [Fact]
    public void A_commander_member_that_survives_the_siege_is_left_alone()
    {
        var actor = _store.CreateUniqueActor(PlayerId, "plant", 3);
        const string worldId = "w-siege-survivor";
        var (ok, reason, _) = _store.CreateWorld(PlayerId, SiegeScenario(worldId, actor.InstanceId, commanderHp: UnkillableHp));
        Assert.True(ok, reason);

        _store.SubmitWorldCommands(worldId, Order(WorldCommandKinds.Assault, LegionId, HostileSector));
        var committed = CommitAll(worldId);
        Assert.True(committed.Advanced, committed.Reason);

        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(actor.InstanceId)!.Phase);
        var legion = _store.LoadWorldState(worldId)!.Entities.Single(e => e.EntityId == LegionId);
        Assert.Contains(legion.Members, m => m.Role == WorldEntityMemberRole.Commander);
    }

    /// <summary>
    /// The acceptance clause in its own words: "a commander member at zero hp is detached". The member
    /// is already down when the turn opens, so no battle is needed — the commit's own sweep is what the
    /// test measures, and it runs on the REAL host path (`CommitWorldTurn`), which is what makes this
    /// more than a unit test of a helper.
    /// </summary>
    [Fact]
    public void A_commander_member_left_at_zero_hp_is_detached_by_the_commit()
    {
        var actor = _store.CreateUniqueActor(PlayerId, "plant", 3);
        const string worldId = "w-zero-hp";
        var (ok, reason, _) = _store.CreateWorld(
            PlayerId, ZeroHpCommanderScenario(worldId, actor.InstanceId, WorldEntityMemberRole.Commander));
        Assert.True(ok, reason);

        _store.SubmitWorldCommands(worldId, Order(WorldCommandKinds.StandFast));
        var committed = CommitAll(worldId);
        Assert.True(committed.Advanced, committed.Reason);

        Assert.Equal(UniqueActorPhases.Recovering, _store.GetUniqueActor(actor.InstanceId)!.Phase);
        var legion = _store.LoadWorldState(worldId)!.Entities.Single(e => e.EntityId == LegionId);
        Assert.DoesNotContain(legion.Members, m => m.Role == WorldEntityMemberRole.Commander);
    }

    /// <summary>
    /// The rule is ROLE-gated, not `InstanceId`-gated: a downed `Fighter` carrying an instance id (the
    /// shape a general troop never has, but a guard or a future non-commander unique might) writes no
    /// phase. A `Fighter`'s death is already handled by the survivor list, and a `Bearer` carries cargo,
    /// not a specimen — neither has a phase this pass may move.
    /// </summary>
    [Fact]
    public void A_non_commander_member_at_zero_hp_moves_no_phase()
    {
        var actor = _store.CreateUniqueActor(PlayerId, "plant", 3);
        const string worldId = "w-zero-hp-fighter";
        var (ok, reason, _) = _store.CreateWorld(
            PlayerId, ZeroHpCommanderScenario(worldId, actor.InstanceId, WorldEntityMemberRole.Fighter));
        Assert.True(ok, reason);

        _store.SubmitWorldCommands(worldId, Order(WorldCommandKinds.StandFast));
        var committed = CommitAll(worldId);
        Assert.True(committed.Advanced, committed.Reason);

        Assert.Equal(UniqueActorPhases.Roster, _store.GetUniqueActor(actor.InstanceId)!.Phase);
    }
}
