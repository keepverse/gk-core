using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// The real, callable composition of `mintAt` (party-dungeon-todo.md D4.12/D3.11/D3.3-D3.5,
/// `[[loot-content-view-unwired]]`) — `LootMintAt.Mint` (Core, pure, no I/O) plus the two Data-layer
/// facts it cannot read itself (a unique's persisted `ContainerRow`, the atom catalog) plus the ONE
/// piece of persistence a mint needs to hand back a real, dereferenceable `InstanceId`:
/// `SaveInstanceUnlocked`. Matches `Func&lt;LootGrant, int, LootMintResult&gt;` exactly — the shape
/// `DelveLoot.RollRoom`/`.RollQuestReward` already take as `mintAt`, so
/// <c>(grant, theta) => store.MintGrant(grant, theta, roleFamilyCells, tuning)</c> is a real, working
/// `mintAt` argument today.
///
/// <para><b>Deliberately stops at `SaveInstanceUnlocked`.</b> `AcquireItemUnlocked` (ownership) and
/// `PersistLootUnlocked` (the drop log + `item_generation` stamp) are NOT called here — those compose
/// with this method's own result in the CALLER's transaction, exactly the shape
/// `QuestRewardBankingComposabilityTests.cs` already proves for the three of them together. A `mintAt`
/// that also acquired and logged would collapse three independently-composable writes back into one,
/// undoing the reason each was split into its own `Unlocked` primitive this same session.</para>
///
/// <para><b>`roleFamilyCells` is a caller-supplied parameter, not something this method loads itself.</b>
/// Deriving it is `RoleFamilyTable.Derive(AffixFamilySeedFile.LoadAll(dir), FamilyOverrides.Parse(...),
/// RoleRelocationTable.Parse(...))` — three pure Core reads — and the established layering in this
/// codebase (`BaseTypeSeedFile.LoadAll` + `RpgStore.ImportBaseTypes`, `Program.cs:478-491`) is that
/// PATH resolution and boot-time composition belong to `Program.cs`, never to `RpgStore` itself. Boot
/// (or a test) computes the cells once and hands them in — this method takes data, not a directory,
/// matching every sibling importer in this file exactly. <b>Honest gap, named:</b> `Program.cs` itself
/// does not yet compute or thread `roleFamilyCells` into a live caller of this method — the same
/// "provably correct, zero production callers (yet)" posture this whole session has repeatedly and
/// correctly used for a real, tested, pluggable piece whose own boot wiring is a distinct next step.</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>Same composition on the caller's own connection/transaction — the shape a real
    /// `CloseDelve`/quest-reward banking call needs, matching every other `*Unlocked` primitive this
    /// file already exposes for that exact reason.</summary>
    internal LootMintResult MintGrantUnlocked(
        SqliteConnection db, SqliteTransaction tx, LootGrant grant, int thetaContent,
        IReadOnlyList<RoleFamilyCell> roleFamilyCells, PowerTuning tuning,
        InstanceOrigin origin = InstanceOrigin.Drop, long catalogRevision = 0,
        string? instanceId = null, string? createdUtc = null)
    {
        if (grant is null) throw new ArgumentNullException(nameof(grant));
        if (roleFamilyCells is null) throw new ArgumentNullException(nameof(roleFamilyCells));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        // One whole-catalog read, grouped in memory -- the N+1-avoidance lesson ResolveBindings
        // already states for this exact shape ("~2N connections... these are two queries total").
        // Skipped entirely for a Unique grant, which never touches the atom-family lookup at all.
        var atomsByFamily = grant.Kind == DropEntryKind.Equipment
            ? ListAtoms().GroupBy(a => a.FamilyId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<AtomRow>)g.ToList(), StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyList<AtomRow>>(StringComparer.Ordinal);

        var lookups = new LootMintLookups(
            LookupAtom: GetAtom,
            LookupAffix: GetAffix,
            Equipment: new EquipmentContainerLookups(roleFamilyCells,
                family => atomsByFamily.TryGetValue(family, out var list) ? list : Array.Empty<AtomRow>()),
            ContainerFor: GetContainer);

        var rejection = LootMintAt.Mint(grant, thetaContent, lookups, tuning, out var instance, origin, catalogRevision);
        if (!rejection.IsOk || instance is null) return new LootMintResult(rejection, null);

        var id = SaveInstanceUnlocked(db, tx, instance, instanceId, createdUtc);
        return new LootMintResult(AtomRejection.Ok, id);
    }

    /// <summary>Public, self-committing form — the shape a standalone caller or a test wants; a
    /// `CloseDelve`-composed caller uses <see cref="MintGrantUnlocked"/> instead. Matches every other
    /// "public locked + internal Unlocked" pair already established in this file family
    /// (`SaveInstance`/`SaveInstanceUnlocked`, `PersistLoot`/`PersistLootUnlocked`).</summary>
    public LootMintResult MintGrant(
        LootGrant grant, int thetaContent, IReadOnlyList<RoleFamilyCell> roleFamilyCells, PowerTuning tuning,
        InstanceOrigin origin = InstanceOrigin.Drop, long catalogRevision = 0,
        string? instanceId = null, string? createdUtc = null)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var result = MintGrantUnlocked(db, tx, grant, thetaContent, roleFamilyCells, tuning,
                origin, catalogRevision, instanceId, createdUtc);
            tx.Commit();
            return result;
        }
    }
}
