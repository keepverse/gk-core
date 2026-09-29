using FusionRpg.Core.Commanders;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// `ai-empire-species` EP4.13 — **the one place code asks "what level is this species, for this empire,
/// in this save?"** (`spec-ai-empire-species.md` section "Reading").
///
/// <para><b>One table, no Dave-special branch.</b> It reads the re-keyed `rpg_actor_progression` by
/// `(save_id, empire_id, kind='species', type_id)`. That table was rebuilt once by `save-identity`'s
/// migration with every existing row landing on `(SaveId, EmpireId.Dave)`, so a Dave read returns exactly
/// the level it returned before the re-key - which is why no separate `rpg_empire_species_progression`
/// exists: two tables for one concept would BE the storage branch this reader removes. An empire with no
/// row reads level 1, the same answer the progression writer's create-then-read path gives.</para>
///
/// <para><b>Why a reader at all.</b> Two storages must never become two readers: every species-level read
/// goes through here, and a Guard test forbids a direct `GetRpgActor(..., RpgActorKinds.Species, ...)`
/// outside this file and the progression writer (`SpeciesLevelReaderGuardTests`). The writer still owns
/// levels - this only asks.</para>
///
/// <para><b>It does not hold its own query text (EP-F1).</b> The read is the store's existing wide row
/// read, <see cref="ReadEmpireActorUnlocked"/> (`RpgStore.Progression.cs`), projected to its
/// <c>Level</c>. A local `SELECT level FROM rpg_actor_progression` here would be a second copy of the
/// narrow level shape `zomboss-commander-clock` SP7.3 keeps to one seam
/// (`ZombossCommanderLevelSingleReaderGuardTests`) - and a second reader of the same row is the shape the
/// species guard exists to prevent anyway. One query, two concepts of interest (a level for this reader, a
/// whole row for everyone else).</para>
/// </summary>
public sealed partial class RpgStore
{
    /// <summary>The species level for one empire of one save. Level 1 means "no row", which is a real
    /// answer (a species nobody has levelled), never an error.</summary>
    public long SpeciesLevelOf(SaveId save, EmpireId empire, int creatureTypeId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return SpeciesLevelOfUnlocked(db, save, empire, creatureTypeId);
        }
    }

    /// <summary>Same read on a caller's own connection/transaction, for a caller already inside one -
    /// the shape every other locked/unlocked pair in this store uses.</summary>
    internal long SpeciesLevelOfUnlocked(
        SqliteConnection db, SaveId save, EmpireId empire, int creatureTypeId) =>
        ReadEmpireActorUnlocked(db, new EmpireRef(save, empire), RpgActorKinds.Species, creatureTypeId)?.Level ?? 1;
}
