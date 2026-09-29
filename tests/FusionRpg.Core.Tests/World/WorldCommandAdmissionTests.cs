using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// W5 (spec-turn-engine.md §Commands): admission is the cheap gate at submit time — well-formed,
/// references exist, the commander owns the subject. Legality *at reveal* is the engine's job and
/// drops a command into the report instead of throwing.
/// </summary>
public class WorldCommandAdmissionTests
{
    static readonly WorldState World =
        WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, seed: 1);

    static WorldCommand StandFast(string commander = "dave", string id = "c1", string? entityId = null) =>
        new() { CommanderId = commander, CommandId = id, Kind = WorldCommandKinds.StandFast, EntityId = entityId };

    [Fact]
    public void A_faction_wide_stand_fast_is_admitted()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, StandFast());
        Assert.True(ok, reason);
    }

    [Fact]
    public void Standing_one_of_your_own_legions_fast_is_admitted()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, StandFast(entityId: "e-dave-legion-1"));
        Assert.True(ok, reason);
    }

    [Fact]
    public void An_unknown_kind_is_refused()
    {
        var cmd = StandFast() with { Kind = "teleport-everything" };
        var (ok, reason) = WorldCommandAdmission.Admit(World, cmd);
        Assert.False(ok);
        Assert.Equal("kind.unknown", reason);
    }

    [Fact]
    public void An_unknown_commander_is_refused()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, StandFast(commander: "nobody"));
        Assert.False(ok);
        Assert.Equal("commander.unknown", reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_command_id_is_refused(string id)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, StandFast(id: id));
        Assert.False(ok);
        Assert.Equal("command.id-missing", reason);
    }

    [Fact]
    public void An_overlong_command_id_is_refused()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, StandFast(id: new string('x', 200)));
        Assert.False(ok);
        Assert.Equal("command.id-too-long", reason);
    }

    [Fact]
    public void A_command_naming_a_missing_entity_is_refused()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, StandFast(entityId: "e-ghost"));
        Assert.False(ok);
        Assert.Equal("entity.unknown", reason);
    }

    [Fact]
    public void You_cannot_command_someone_elses_army()
    {
        // e-wild-pack-1 belongs to the wild faction — Dave does not get to order it about.
        var (ok, reason) = WorldCommandAdmission.Admit(World, StandFast(entityId: "e-wild-pack-1"));
        Assert.False(ok);
        Assert.Equal("entity.not-yours", reason);
    }

    [Fact]
    public void The_owning_faction_may_command_its_own_entity()
    {
        var cmd = StandFast(commander: "wild", entityId: "e-wild-pack-1");
        var (ok, reason) = WorldCommandAdmission.Admit(World, cmd);
        Assert.True(ok, reason);
    }

    // world-stage W24: `cede` — a faction's own deliberate release, needs no entity.

    static WorldCommand Cede(string commander, string? sectorId) => new()
    {
        CommanderId = commander, CommandId = "c-cede", Kind = WorldCommandKinds.Cede, SectorId = sectorId
    };

    [Fact]
    public void Cede_is_a_known_kind()
    {
        Assert.Contains(WorldCommandKinds.Cede, WorldCommandKinds.All);
        Assert.True(WorldCommandKinds.IsKnown(WorldCommandKinds.Cede));
    }

    [Fact]
    public void Ceding_your_own_sector_is_admitted_with_no_entity_named()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Cede("dave", "homeworld"));
        Assert.True(ok, reason);
    }

    [Fact]
    public void Ceding_a_sector_you_do_not_own_is_refused()
    {
        // black-gate is unowned at world creation (first-light) — not dave's to give up.
        var (ok, reason) = WorldCommandAdmission.Admit(World, Cede("dave", "black-gate"));
        Assert.False(ok);
        Assert.Equal("sector.not-yours", reason);
    }

    [Fact]
    public void Ceding_with_no_sector_named_is_refused()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Cede("dave", sectorId: null));
        Assert.False(ok);
        Assert.Equal("sector.missing", reason);
    }

    [Fact]
    public void Ceding_an_unknown_sector_is_refused_by_the_shared_check()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Cede("dave", "nowhere"));
        Assert.False(ok);
        Assert.Equal("sector.unknown", reason);
    }

    [Fact]
    public void A_cede_order_changes_no_hash_by_merely_existing_in_the_log()
    {
        // WorldCanonical never hashes commands (WorldCanonical.cs) — admitting and logging a cede
        // order, with nothing yet resolving it, must produce the identical state hash as a turn
        // with no orders at all.
        var withCede = TurnEngine.Step(World, new[] { Cede("dave", "homeworld") }, seed: 1);
        var withNothing = TurnEngine.Step(World, Array.Empty<WorldCommand>(), seed: 1);
        Assert.Equal(withNothing.StateHash, withCede.StateHash);
    }

    // world-stage W28: `bind-warden` — names a sector, carries the binding id, needs no entity.
    // Retired by warden-freeze-fix (RulesetVersion 13): the owner withdrew the warden on 2026-09-13
    // (warden-mortality-ideal.md), so admission refuses every bind with `warden.retired`.

    static WorldCommand BindWarden(string commander, string? sectorId, string? wardenId = "creature-1") => new()
    {
        CommanderId = commander, CommandId = "c-bind", Kind = WorldCommandKinds.BindWarden,
        SectorId = sectorId, WardenId = wardenId
    };

    [Fact]
    public void BindWarden_stays_a_known_kind_so_stored_logs_still_hydrate()
    {
        Assert.Contains(WorldCommandKinds.BindWarden, WorldCommandKinds.All);
        Assert.True(WorldCommandKinds.IsKnown(WorldCommandKinds.BindWarden));
    }

    [Fact]
    public void Binding_a_warden_to_your_own_sector_is_refused_as_retired()
    {
        // The one shape RulesetVersion 12 admitted: own sector, id carried, no entity named.
        var (ok, reason) = WorldCommandAdmission.Admit(World, BindWarden("dave", "homeworld"));
        Assert.False(ok);
        Assert.Equal("warden.retired", reason);
        Assert.Equal(WorldCommandAdmission.WardenRetired, reason);
    }

    [Theory]
    [InlineData("dave", "black-gate", "creature-1")] // someone else's ground (12: sector.not-yours)
    [InlineData("dave", null, "creature-1")]         // no sector (12: sector.missing)
    [InlineData("dave", "nowhere", "creature-1")]    // unknown sector (12: sector.unknown)
    [InlineData("dave", "homeworld", null)]          // no warden id (12: warden.missing)
    [InlineData("dave", "homeworld", "")]
    [InlineData("dave", "homeworld", "   ")]
    [InlineData("nobody", "homeworld", "creature-1")] // unknown commander (12: commander.unknown)
    public void No_field_of_a_bind_warden_order_makes_it_admissible_the_refusal_is_always_retired(
        string commander, string? sectorId, string? wardenId)
    {
        // The retirement is checked before any shape check, so every bind gets the one named reason
        // rather than whichever field happened to be wrong first.
        var (ok, reason) = WorldCommandAdmission.Admit(World, BindWarden(commander, sectorId, wardenId));
        Assert.False(ok);
        Assert.Equal("warden.retired", reason);
    }

    [Fact]
    public void An_unknown_kind_is_still_refused_as_unknown_not_as_retired()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, BindWarden("dave", "homeworld") with { Kind = "bind-wardens" });
        Assert.False(ok);
        Assert.Equal("kind.unknown", reason);
    }

    // world-map W51: `raise` — names a sector, needs no entity. Ownership, a Seat, a hostile entity
    // and RecruitStock are all resolution-time (RaiseResolver at Snapshot), not admission-time — the
    // identical discipline `build` already applies, so admission here only checks a sector was named.

    static WorldCommand Raise(string commander, string? sectorId) => new()
    {
        CommanderId = commander, CommandId = "c-raise", Kind = WorldCommandKinds.Raise, SectorId = sectorId
    };

    [Fact]
    public void Raise_is_a_known_kind()
    {
        Assert.Contains(WorldCommandKinds.Raise, WorldCommandKinds.All);
        Assert.True(WorldCommandKinds.IsKnown(WorldCommandKinds.Raise));
    }

    [Fact]
    public void Raising_at_a_named_sector_is_admitted_regardless_of_ownership()
    {
        // Deliberately not an ownership check here — "black-gate" is unowned at first-light's world
        // creation, and admission still passes it through: RaiseResolver is what says "not yours",
        // at Snapshot, re-validated against the state the turn actually produced.
        var (ok, reason) = WorldCommandAdmission.Admit(World, Raise("dave", "black-gate"));
        Assert.True(ok, reason);
    }

    [Fact]
    public void Raising_with_no_sector_named_is_refused()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Raise("dave", sectorId: null));
        Assert.False(ok);
        Assert.Equal("sector.missing", reason);
    }

    [Fact]
    public void Raising_at_an_unknown_sector_is_refused_by_the_shared_check()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Raise("dave", "nowhere"));
        Assert.False(ok);
        Assert.Equal("sector.unknown", reason);
    }

    // world-map W52: `develop` — names a sector and a known project, needs no entity. Ownership,
    // an already-in-progress project and `LoamStock` are all resolution-time (`DevelopResolver` at
    // Snapshot), not admission-time — the identical discipline `raise` already applies, so admission
    // here only checks a sector was named and the project id is one the catalog knows.

    const string RealProjectId = "raise-development-placeholder";

    static WorldCommand Develop(string commander, string? sectorId, string? projectId = RealProjectId) => new()
    {
        CommanderId = commander, CommandId = "c-develop", Kind = WorldCommandKinds.Develop,
        SectorId = sectorId, ProjectId = projectId
    };

    [Fact]
    public void Develop_is_a_known_kind()
    {
        Assert.Contains(WorldCommandKinds.Develop, WorldCommandKinds.All);
        Assert.True(WorldCommandKinds.IsKnown(WorldCommandKinds.Develop));
    }

    [Fact]
    public void Developing_a_named_sector_with_a_known_project_is_admitted_regardless_of_ownership()
    {
        // Deliberately not an ownership check here — "black-gate" is unowned at first-light's world
        // creation, and admission still passes it through: DevelopResolver is what says "not yours",
        // at Snapshot, re-validated against the state the turn actually produced.
        var (ok, reason) = WorldCommandAdmission.Admit(World, Develop("dave", "black-gate"));
        Assert.True(ok, reason);
    }

    [Fact]
    public void Developing_with_no_sector_named_is_refused()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Develop("dave", sectorId: null));
        Assert.False(ok);
        Assert.Equal("sector.missing", reason);
    }

    [Fact]
    public void Developing_at_an_unknown_sector_is_refused_by_the_shared_check()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Develop("dave", "nowhere"));
        Assert.False(ok);
        Assert.Equal("sector.unknown", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-such-project")]
    public void Developing_with_no_known_project_named_is_refused(string? projectId)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Develop("dave", "homeworld", projectId));
        Assert.False(ok);
        Assert.Equal("project.unknown", reason);
    }

    // empire-inventory-surfaces `cargo-commands` §Design 1-3 (Task 4A.1): six kinds, five arms.
    // Closed code-owned vocabulary with a stated reason (validation-ssot: the kind set is owned
    // by this file's own `All` list and changes only when a module ships a kind — pin it).

    static WorldCommand Cargo(string kind, string id = "c-cargo") => new()
    {
        CommanderId = "dave",
        CommandId = id,
        Kind = kind,
        EntityId = "e-dave-legion-1",
    };

    [Fact]
    public void The_cargo_vocabulary_is_six_kebab_case_kinds_distinct_from_sector_claim()
    {
        Assert.Contains(WorldCommandKinds.LoadCargo, WorldCommandKinds.All);
        Assert.Contains(WorldCommandKinds.UnloadCargo, WorldCommandKinds.All);
        Assert.Contains(WorldCommandKinds.TransferCargo, WorldCommandKinds.All);
        Assert.Contains(WorldCommandKinds.DepositCargo, WorldCommandKinds.All);
        Assert.Contains(WorldCommandKinds.WithdrawCargo, WorldCommandKinds.All);
        Assert.Contains(WorldCommandKinds.ClaimCache, WorldCommandKinds.All);
        // 12 shipped kinds + 6 cargo kinds — a closed vocabulary this file owns, not a population.
        // pin: closed-vocabulary WorldCommandKinds.All — a new world command kind is a reviewed change
        Assert.Equal(18, WorldCommandKinds.All.Count);
        Assert.DoesNotContain(WorldCommandKinds.ClaimCache, new[] { WorldCommandKinds.Claim });
        Assert.Equal("claim-cache", WorldCommandKinds.ClaimCache);
    }

    [Theory]
    [InlineData("load-cargo")]
    [InlineData("unload-cargo")]
    [InlineData("transfer-cargo")]
    [InlineData("deposit-cargo")]
    [InlineData("withdraw-cargo")]
    [InlineData("claim-cache")]
    public void Every_cargo_kind_without_an_acting_legion_is_refused(string kind)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Cargo(kind) with { EntityId = null });
        Assert.False(ok);
        Assert.Equal("entity.missing", reason);
    }

    [Fact]
    public void Loading_an_instance_with_its_id_is_admitted()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.LoadCargo) with { CargoKind = "instance", InstanceId = "i-1" });
        Assert.True(ok, reason);
    }

    [Fact]
    public void Loading_a_stack_with_container_and_positive_qty_is_admitted()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.LoadCargo) with { CargoKind = "stack", ContainerId = "c-1", Qty = 3 });
        Assert.True(ok, reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("crate")]
    public void Loading_with_no_known_shape_is_refused(string? cargoKind)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.LoadCargo) with { CargoKind = cargoKind });
        Assert.False(ok);
        Assert.Equal("cargo.kind-unknown", reason);
    }

    [Theory]
    [InlineData("instance", null, "c-1", 1)]
    [InlineData("instance", "", "c-1", 1)]
    [InlineData("stack", null, null, 1)]
    [InlineData("stack", null, "", 1)]
    [InlineData("stack", null, "c-1", 0)]
    [InlineData("stack", null, "c-1", -2)]
    public void Loading_with_no_resolvable_reference_is_refused(
        string cargoKind, string? instanceId, string? containerId, long qty)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.LoadCargo) with
            {
                CargoKind = cargoKind, InstanceId = instanceId, ContainerId = containerId, Qty = qty
            });
        Assert.False(ok);
        Assert.Equal("cargo.ref-missing", reason);
    }

    [Fact]
    public void Unloading_a_named_row_is_admitted()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.UnloadCargo) with { Seq = 0 });
        Assert.True(ok, reason);
    }

    [Fact]
    public void Unloading_with_no_row_named_is_refused()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World, Cargo(WorldCommandKinds.UnloadCargo));
        Assert.False(ok);
        Assert.Equal("cargo.seq-missing", reason);
    }

    [Fact]
    public void Handing_a_row_to_an_owned_legion_is_admitted()
    {
        // The only other dave-owned subject in FirstLight is the acting legion itself — a
        // same-legion transfer admits here and no-ops at resolve (the verb's own branch).
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.TransferCargo) with { TargetEntityId = "e-dave-legion-1", Seq = 0 });
        Assert.True(ok, reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Handing_a_row_nowhere_is_refused(string? target)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.TransferCargo) with { TargetEntityId = target, Seq = 0 });
        Assert.False(ok);
        Assert.Equal("cargo.target-missing", reason);
    }

    [Theory]
    [InlineData("e-ghost")]
    [InlineData("e-wild-pack-1")]
    public void Handing_a_row_to_an_unknown_or_foreign_legion_is_refused_as_not_yours(string target)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.TransferCargo) with { TargetEntityId = target, Seq = 0 });
        Assert.False(ok);
        Assert.Equal("entity.not-yours", reason);
    }

    [Fact]
    public void Handing_a_row_with_no_row_named_is_refused()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.TransferCargo) with { TargetEntityId = "e-dave-legion-1" });
        Assert.False(ok);
        Assert.Equal("cargo.seq-missing", reason);
    }

    [Theory]
    [InlineData("deposit-cargo")]
    [InlineData("withdraw-cargo")]
    public void Vault_moves_naming_sector_and_row_are_admitted(string kind)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(kind) with { SectorId = "homeworld", Seq = 0 });
        Assert.True(ok, reason);
    }

    [Theory]
    [InlineData("deposit-cargo")]
    [InlineData("withdraw-cargo")]
    public void Vault_moves_with_no_sector_named_are_refused(string kind)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(kind) with { SectorId = null, Seq = 0 });
        Assert.False(ok);
        Assert.Equal("sector.missing", reason);
    }

    [Theory]
    [InlineData("deposit-cargo")]
    [InlineData("withdraw-cargo")]
    public void Vault_moves_with_no_row_named_are_refused(string kind)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(kind) with { SectorId = "homeworld" });
        Assert.False(ok);
        Assert.Equal("cargo.seq-missing", reason);
    }

    [Fact]
    public void Claiming_a_named_cache_is_admitted()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.ClaimCache) with { CacheId = "cache-1" });
        Assert.True(ok, reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Claiming_with_no_cache_named_is_refused(string? cacheId)
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.ClaimCache) with { CacheId = cacheId });
        Assert.False(ok);
        Assert.Equal("cache.missing", reason);
    }

    [Fact]
    public void Claiming_with_no_command_id_is_refused_because_correlation_is_the_command_id()
    {
        var (ok, reason) = WorldCommandAdmission.Admit(World,
            Cargo(WorldCommandKinds.ClaimCache) with { CacheId = "cache-1", CommandId = "" });
        Assert.False(ok);
        Assert.Equal("command.id-missing", reason);
    }
}
