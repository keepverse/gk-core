using FusionRpg.Core.Creatures.Layers;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>
/// `species-progression` SP0.6 — `player_species` has no reader or writer left in `src/`. The eager
/// boot roll (`MaterialisePlayerSpecies`, retired SP0.4/SP0.6) and the debug reforge writer
/// (`ReforgePlayerSpecies`, retired here alongside `/api/debug/reforge-world` itself) were the table's
/// only writers; its only reader (`ListPlayerSpecies`) went with them. Layer 1b's real carrier is the
/// append-only ledger (`RpgStore.SpeciesMods.cs`, `rpg_player_species_mod`) since SP0.1/SP0.3.
///
/// <para>The table itself STAYS — dropping it is a destructive schema change and not in this plan
/// (spec-species-mod-ledger.md, Migration). <see cref="EnsurePlayerSpeciesSchemaUnlocked"/> keeps
/// creating it (idempotent `CREATE TABLE IF NOT EXISTS`) so an existing save's own `player_species`
/// rows, if any, are never orphaned by a missing table — they are simply unread from here on.</para>
/// </summary>
public sealed partial class RpgStore
{
    void EnsurePlayerSpeciesSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS player_species (
              player_id INTEGER NOT NULL,
              species_id TEXT NOT NULL,
              instance_id TEXT NOT NULL,
              materialised_utc TEXT NOT NULL,
              catalog_revision INTEGER NOT NULL,
              PRIMARY KEY (player_id, species_id)
            );
            CREATE INDEX IF NOT EXISTS ix_player_species_player ON player_species(player_id);
            """);
    }

    /// <summary>
    /// `species-progression` SP3.6 (species-layer-projector, map C3) — <see cref="GetSpecimenLedgerRoll"/>'s
    /// own return shape, widened from the shared <see cref="FusionRpg.Core.Creatures.Materialise.MaterialisedRoll"/>
    /// (SpeciesId + Instance only) to also carry the ledger row's <see cref="SpeciesModMechanism"/> —
    /// <see cref="SpeciesLayerProjector.ProjectPlayerMod"/>'s own required parameter, which
    /// <c>MaterialisedRoll</c> (also used by the unrelated eager-boot <c>SpeciesMaterialiser</c>, before
    /// any mechanism is known) has no reason to carry.
    /// </summary>
    public sealed record LedgerRoll(string SpeciesId, InstanceRow Instance, SpeciesModMechanism Mechanism);

    /// <summary>
    /// `species-progression` SP0.5 (spec-species-mod-ledger.md, ruling behaviour 1) — the specimen's
    /// OWN empire's <b>ledger</b> instance (<c>rpg_player_species_mod</c>) for its species, and nothing
    /// else. Replaces `GetSpecimenMaterialisedRoll` (retired: it read the eager <c>player_species</c>
    /// roll, which SP0.3/SP0.4 stopped writing and SP0.6 removed outright) as the sheet-compose seam
    /// (<see cref="FusionRpg.Server.UniqueActorHubCompose"/>). Deliberately never falls back to
    /// <see cref="FusionRpg.Core.Creatures.Materialise.SpeciesRollPreview"/>: a preview is not a
    /// materialised fact, so a specimen whose species has no ledger row (a non-fuser) composes
    /// <b>nothing</b> here — unlike <see cref="PickSourceAtoms"/>, the fusion pick-source read, which
    /// legitimately previews what COULD be picked because nothing has happened yet to offer instead.
    ///
    /// <para>The owner is the specimen's OWN empire (<see cref="SpecimenOwnerEmpire"/>, falling back to
    /// <see cref="HumanEmpireOf"/> only when the specimen predates empire stamping), never the save's
    /// human empire unconditionally — an AI empire's specimen (Zomboss et al.) reads its own empire's
    /// ledger, never the human's.</para>
    ///
    /// <para><c>null</c> is the honest "nothing to compose" outcome — the specimen doesn't exist,
    /// carries no creature profile, or its owning empire has never fused (picked into) that species —
    /// never a fabricated empty roll presented as real.</para>
    /// </summary>
    public LedgerRoll? GetSpecimenLedgerRoll(string specimenInstanceId)
    {
        var actor = GetUniqueActor(specimenInstanceId);
        if (actor is null) return null;
        var profile = GetCreatureProfile(specimenInstanceId);
        if (profile is null) return null;

        var empire = SpecimenOwnerEmpire(specimenInstanceId)?.Empire ?? HumanEmpireOf(actor.PlayerId);
        var owner = new EmpireRef(new SaveId(actor.PlayerId), empire);

        SpeciesModRow? ledger = null;
        foreach (var row in ListSpeciesMods(owner))
            if (string.Equals(row.SpeciesId, profile.SpeciesId, StringComparison.Ordinal))
                ledger = row;
        if (ledger is null) return null;

        var instance = GetInstance(ledger.InstanceId);
        return instance is null ? null : new LedgerRoll(profile.SpeciesId, instance, ledger.Mechanism);
    }
}
