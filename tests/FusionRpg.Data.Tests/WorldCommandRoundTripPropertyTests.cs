using System.Reflection;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Turn;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// world-stage W23 — the plan's own "single highest-value test in Phase 0": closes the defect
/// *class* `stance` and (before W22) `Amount`/`StructureId` were each lost to individually, rather
/// than the two known instances. For every admissible kind in <see cref="WorldCommandKinds.All"/>
/// (all but the retired `bind-warden`, which has its own refusal test below), a command
/// with **every** optional member of <see cref="WorldCommand"/> populated is submitted, then read
/// back through all three hydration paths (<see cref="RpgStore.ListWorldCommands"/>,
/// <see cref="RpgStore.ListLoggedWorldCommands"/>, and the internal path only <see
/// cref="RpgStore.CommitWorldTurn"/> reaches) and every property is asserted equal — via
/// reflection over <see cref="WorldCommand"/>'s own properties, never a hand-maintained field list,
/// so a member added later is covered automatically rather than by remembering to update this file.
/// </summary>
public class WorldCommandRoundTripPropertyTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public WorldCommandRoundTripPropertyTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _store.CreateWorld(1, WorldTemplateCatalog.Build(WorldTemplateCatalog.FirstLightId, 1, "w"));
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    /// <summary>
    /// One command per kind, every optional member populated with a value real enough to pass
    /// admission for every kind at once — `homeworld` slot 3 is a real rootbed, `well` is a real
    /// structure id, `l-home-ember` a real lane, `scout` a real stance, `creature-1` an opaque warden id
    /// (no admissible kind reads it since `bind-warden` was retired, RulesetVersion 13 — it is carried
    /// only so the field's own round trip stays covered),
    /// `raise-development-placeholder` a real project id (world-map W52). Cargo fields
    /// (`cargo-commands` §Design 3): `Seq` 0, `TargetEntityId` the acting legion itself (known +
    /// dave-owned, so the transfer arm admits; resolution drops it gracefully with no cargo
    /// aboard), `CacheId` a non-blank id (reachability is resolve-time), `CargoKind`
    /// `'instance'` + `InstanceId` (the load arm's shape; resolution refuses `cargo.weight-unknown`
    /// with no probe set, writing nothing).
    /// Admission only checks the fields *its own* kind cares about (verified by reading
    /// `WorldCommandAdmission.cs` before writing this — `move`/`stand-fast` check nothing
    /// kind-specific at all), so setting every field on every kind is accepted rather than refused
    /// for carrying "extra" data.
    /// </summary>
    static WorldCommand FullyPopulated(string kind) => new()
    {
        CommanderId = "dave",
        CommandId = "c-" + kind,
        Kind = kind,
        EntityId = "e-dave-legion-1",
        SectorId = "homeworld",
        SlotIndex = 3,
        Stance = "scout",
        LanePath = new[] { "l-home-ember" },
        Amount = 100,
        StructureId = "well",
        WardenId = "creature-1",
        ProjectId = "raise-development-placeholder",
        Seq = 0,
        TargetEntityId = "e-dave-legion-1",
        CacheId = "cache-1",
        CargoKind = "instance",
        InstanceId = "i-1",
        ContainerId = "c-1",
        Qty = 1
    };

    /// <summary>
    /// Every kind admission can accept. `bind-warden` is retired (warden-freeze-fix, RulesetVersion
    /// 13): it stays a known kind so stored logs hydrate, but admission refuses it, so it cannot be
    /// filed to round-trip. Its one kind-specific field, `WardenId`, is still populated on every
    /// kind below by <see cref="FullyPopulated"/>, so the field itself stays covered.
    /// </summary>
    static IEnumerable<string> AdmissibleKinds =>
        WorldCommandKinds.All.Where(k => !string.Equals(k, WorldCommandKinds.BindWarden, StringComparison.Ordinal));

    static readonly PropertyInfo[] Properties = typeof(WorldCommand).GetProperties();

    static void AssertSameOnEveryProperty(WorldCommand expected, WorldCommand actual)
    {
        foreach (var property in Properties)
            Assert.Equal(property.GetValue(expected), property.GetValue(actual));
    }

    [Fact]
    public void Every_kind_with_every_optional_member_populated_survives_ListWorldCommands_and_ListLoggedWorldCommands()
    {
        foreach (var kind in AdmissibleKinds)
        {
            var submitted = FullyPopulated(kind);
            var (ok, reason, _) = _store.SubmitWorldCommand("w", submitted);
            Assert.True(ok, $"{kind}: {reason}");

            var viaListWorldCommands = _store.ListWorldCommands("w", 0)
                .Single(c => c.CommandId == submitted.CommandId);
            AssertSameOnEveryProperty(submitted, viaListWorldCommands);

            var viaListLoggedWorldCommands = _store.ListLoggedWorldCommands("w", 0)
                .Single(l => l.Command.CommandId == submitted.CommandId).Command;
            AssertSameOnEveryProperty(submitted, viaListLoggedWorldCommands);
        }
    }

    /// <summary>
    /// The internal `ListWorldCommandsUnlocked` path — reachable only from inside
    /// `CommitWorldTurn`, never directly callable from a test — is proven the same way W22's own
    /// analogous test was: `TurnEngine`'s `Reveal` phase re-admits every command through
    /// `WorldCommandAdmission.Admit` against whatever that internal path hydrated. If any field
    /// were lost there, the kind-specific admission check that depends on it (`sustain`'s `Amount`,
    /// `build`'s `StructureId`/`SlotIndex`, `stance`'s `Stance`, `clear`'s `SlotIndex`) would refuse
    /// it post-hydration and the turn report would carry that drop instead of an accepted line.
    /// </summary>
    [Fact]
    public void Every_kind_survives_the_engines_own_internal_hydration_path_used_at_commit()
    {
        // One kind, one committed turn, at a time — several kinds sharing one entity in the same
        // turn can legitimately interfere with each other at resolution (a `move` relocates the
        // entity before a same-turn `clear` targeting its old sector runs, which is a real semantic
        // conflict, not a round-trip defect) — this test isolates the one property under test:
        // does this kind's own field survive the internal hydration path, not "do several kinds
        // co-resolve sensibly."
        // `move` runs last: every other kind's fixture assumes the legion still stands at
        // `homeworld` (a real, resolvable position for `clear`/`claim`/`build`/`sustain`'s own
        // resolution-time checks) — actually relocating it via `move` would make every kind tested
        // afterward fail on position, not on a lost field, which is not what this test is proving.
        var turn = 0;
        foreach (var kind in AdmissibleKinds.OrderBy(k => string.Equals(k, WorldCommandKinds.Move, StringComparison.Ordinal) ? 1 : 0))
        {
            var commandId = "c-" + kind;
            var submitted = FullyPopulated(kind) with { CommandId = commandId };
            var (ok, reason, _) = _store.SubmitWorldCommand("w", submitted);
            Assert.True(ok, $"{kind}: {reason}");

            var commit = _store.CommitWorldTurn("w", "dave", turn);
            Assert.True(commit.Ok, commit.Reason);
            Assert.True(commit.Advanced);

            // `CommandAccepted` is written exactly once, at `Reveal`, immediately after
            // `WorldCommandAdmission.Admit` succeeds against whatever the internal hydration path
            // produced — sufficient proof on its own that no field admission depends on (this
            // kind's own `Amount`/`StructureId`/`Stance`/`SlotIndex`/`EntityId`) was lost there.
            // A *later* resolution-time drop (a resolver's own game-state check, e.g. "nothing to
            // clear here") is a different, unrelated failure mode this test does not need to avoid
            // — it would still show `CommandAccepted` even if the resolver later declines to act.
            var report = _store.GetWorldTurnReport("w", turn)!;
            Assert.Contains(report.Entries, e => e.Kind == TurnReportKinds.CommandAccepted && e.Subject == commandId);

            turn++;
        }
    }

    [Fact]
    public void A_fully_populated_bind_warden_is_refused_as_retired_and_writes_no_row()
    {
        // The one kind the loops above skip: refused at the store's own submit gate, before any row
        // is written, with the named reason rather than a generic one.
        var submitted = FullyPopulated(WorldCommandKinds.BindWarden);

        var (ok, reason, replayed) = _store.SubmitWorldCommand("w", submitted);

        Assert.False(ok);
        Assert.False(replayed);
        Assert.Equal(WorldCommandAdmission.WardenRetired, reason);
        Assert.DoesNotContain(_store.ListWorldCommands("w", 0), c => c.CommandId == submitted.CommandId);
    }
}
