using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Saves;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    /// <summary>spec-species-rank.md §5: the rank id + display name for a payload, copied off the
    /// runtime species catalog (never re-derived, never defaulted). A species the catalog does not
    /// know reads as null — a codex or profile row can outlive a catalog entry, and a payload must
    /// not fail on one.</summary>
    static (string? Rank, string? RankDisplayName) RankForPayload(string speciesId)
    {
        if (!CreatureSpeciesCatalog.IsKnown(speciesId)) return (null, null);
        var rank = CreatureSpeciesCatalog.Get(speciesId).Rank;
        return rank is { } r ? (r.ToId(), r.ToDisplayName()) : (null, null);
    }

    /// <summary>
    /// Atomic creature mint: UniqueActor (Roster) + creature profile + codex upsert in ONE transaction —
    /// a failure anywhere leaves nothing (spec-creature-core.md). Returns the specimen and whether
    /// this mint newly discovered the species for the player.
    /// </summary>
    public (CreatureSpecimenDto Specimen, bool NewlyDiscovered) MintCreature(long playerId, CreatureMintSpec spec)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null)
                throw new InvalidOperationException("player not found");
            using var tx = db.BeginTransaction();
            var specimen = MintCreatureUnlocked(db, playerId, spec, ServerClock.UtcNowDateTime.ToString("o"), out var newlyDiscovered);
            tx.Commit();
            return (specimen, newlyDiscovered);
        }
    }

    /// <summary>
    /// save-identity SE4.22 — the same atomic mint as <see cref="MintCreature"/>, for an empire named
    /// explicitly rather than defaulted to the save's human empire. <paramref name="owner"/>'s save IS
    /// the `players` row the specimen mints onto: after this module, no separate "Zomboss player" row
    /// exists, so an AI empire's mint (<c>RpgStore.MintForEmpire</c>) lands directly on the match's own
    /// save under that empire.
    /// </summary>
    internal (CreatureSpecimenDto Specimen, bool NewlyDiscovered) MintCreatureForEmpire(EmpireRef owner, CreatureMintSpec spec)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, owner.Save.Value) is null)
                throw new InvalidOperationException("player not found");
            using var tx = db.BeginTransaction();
            var specimen = MintCreatureUnlocked(
                db, owner.Save.Value, spec, ServerClock.UtcNowDateTime.ToString("o"), out var newlyDiscovered, owner.Empire);
            tx.Commit();
            return (specimen, newlyDiscovered);
        }
    }

    /// <summary>Mint inside an open transaction — used by MintCreature, MintCreatureForEmpire and the
    /// summon pull. <paramref name="empireOverride"/> stamps the specimen's owning empire explicitly (an
    /// AI empire's own mint); null — every pre-existing caller — keeps SE4.14's default of the save's
    /// human empire.</summary>
    CreatureSpecimenDto MintCreatureUnlocked(
        SqliteConnection db, long playerId, CreatureMintSpec spec, string now, out bool newlyDiscovered,
        EmpireId? empireOverride = null)
    {
        // Catalog discipline at the last write gate (spec-creature-core.md): unknown ids reject.
        var speciesId = (spec.SpeciesId ?? "").Trim();
        if (!Core.Creatures.CreatureSpeciesCatalog.IsKnown(speciesId))
            throw new ArgumentException($"Unknown creature species '{spec.SpeciesId}'.");
        if (spec.Side is not ("plant" or "zombie")) throw new ArgumentException("side must be plant|zombie");
        if (!Core.Creatures.CreatureRarityIds.TryParse(spec.Rarity, out _))
            throw new ArgumentException($"Unknown rarity '{spec.Rarity}'.");
        if (spec.Variant != "normal" && !Core.Creatures.CreatureSpeciesCatalog.KnownVariants.Contains(spec.Variant, StringComparer.Ordinal))
            throw new ArgumentException($"Unknown variant '{spec.Variant}'.");
        foreach (var trait in spec.TraitIds)
            if (!Core.Creatures.CreatureTraitCatalog.IsKnown(trait))
                throw new ArgumentException($"Unknown trait '{trait}'.");
        spec.SpeciesId = speciesId;
        var id = Guid.NewGuid().ToString("N");
        var stampedEmpire = empireOverride?.Value ?? HumanEmpireOfOrNull(db, playerId)?.Value;

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO rpg_unique_actors(
                  instance_id, player_id, empire_id, side, type_id, phase, level, xp,
                  match_key, last_ptr, deploy_correlation_id, revision, created_utc, updated_utc)
                VALUES($id, $pid, $emp, $side, $type, $phase, 1, 0, NULL, NULL, NULL, 0, $now, $now);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$pid", playerId);
            // SE4.14: a specimen minted into a seeded save is stamped with that save's human empire by
            // default. A row whose owner is not a save (pre-migration history, the legacy Zomboss row)
            // stays NULL until SE4.18 backfills it — never a guessed empire. SE4.22: an explicit
            // empireOverride (an AI empire's own mint) stamps THAT empire instead of the human default.
            cmd.Parameters.AddWithValue("$emp", (object?)stampedEmpire ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$side", spec.Side);
            cmd.Parameters.AddWithValue("$type", spec.GameTypeId);
            cmd.Parameters.AddWithValue("$phase", UniqueActorPhases.Roster);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO rpg_creature_profiles(
                  instance_id, species_id, rarity, variant, element_primary, element_secondary,
                  traits_json, origin, nickname, locked, created_utc, revision)
                VALUES($id, $species, $rarity, $variant, $ep, $es, $traits, $origin, $nick, 0, $now, 0);
                """;
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$species", spec.SpeciesId);
            cmd.Parameters.AddWithValue("$rarity", spec.Rarity);
            cmd.Parameters.AddWithValue("$variant", spec.Variant);
            cmd.Parameters.AddWithValue("$ep", spec.ElementPrimary);
            cmd.Parameters.AddWithValue("$es", (object?)spec.ElementSecondary ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$traits", JsonSerializer.Serialize(spec.TraitIds));
            cmd.Parameters.AddWithValue("$origin", spec.Origin);
            cmd.Parameters.AddWithValue("$nick", (object?)NullIfEmpty(spec.Nickname) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        // save-identity SE4.23 ("AI-empire specimens never touch a human-only table") — the codex is the
        // human's own almanac of the save, and contracts are the summoner's binding slots; an AI empire
        // "discovering" a species or filling its own contract capacity is meaningless. "Human" means the
        // stamped empire equals the save's OWN human empire, never a guess: a row with no seeded empires
        // (pre-R3 legacy) has none to compare against and keeps today's behaviour (write both), matching
        // every pre-existing caller — only an EXPLICIT non-human empireOverride (Zomboss's own mint)
        // skips them.
        var human = HumanEmpireOfOrNull(db, playerId);
        var isHumanMint = !human.HasValue || string.Equals(stampedEmpire, human.Value.Value, StringComparison.Ordinal);
        if (isHumanMint)
        {
            newlyDiscovered = UpsertCodexUnlocked(db, playerId, spec.SpeciesId, CreatureCodexStates.Discovered, now);
            // Contracts ride the same transaction: a new creature takes a free slot, or arrives unbound
            // when capacity is full (spec-creature-contracts.md).
            AutoBindNewSpecimenUnlocked(db, playerId, id, now);
        }
        else
        {
            newlyDiscovered = false;
        }
        return new CreatureSpecimenDto
        {
            Actor = ReadUniqueActorUnlocked(db, id)!,
            Profile = ReadCreatureProfileUnlocked(db, id)!
        };
    }

    /// <summary>
    /// Codex upsert with the monotonic lattice: discovered &gt; seen, never downgrade
    /// (spec-creature-core.md — a second copy must not turn `discovered` back into `seen`).
    /// Returns true when this call is the first-ever `discovered` for the species.
    /// </summary>
    public bool UpsertCreatureCodex(long playerId, string speciesId, string state)
    {
        if (state is not (CreatureCodexStates.Seen or CreatureCodexStates.Discovered))
            throw new ArgumentException("state must be seen|discovered");
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();
            var now = ServerClock.UtcNowDateTime.ToString("o");
            var result = UpsertCodexUnlocked(db, playerId, speciesId.Trim(), state, now);
            tx.Commit();
            return result;
        }
    }

    bool UpsertCodexUnlocked(SqliteConnection db, long playerId, string speciesId, string state, string now)
    {
        string? existing = null;
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT state FROM rpg_creature_codex WHERE player_id=$pid AND species_id=$sid;";
            read.Parameters.AddWithValue("$pid", playerId);
            read.Parameters.AddWithValue("$sid", speciesId);
            existing = read.ExecuteScalar() as string;
        }

        var newlyDiscovered = state == CreatureCodexStates.Discovered && existing != CreatureCodexStates.Discovered;
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_creature_codex(player_id, species_id, state, first_utc, updated_utc)
            VALUES($pid, $sid, $state, $now, $now)
            ON CONFLICT(player_id, species_id) DO UPDATE SET
              state = CASE WHEN rpg_creature_codex.state = 'discovered' THEN 'discovered' ELSE excluded.state END,
              updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$pid", playerId);
        cmd.Parameters.AddWithValue("$sid", speciesId);
        cmd.Parameters.AddWithValue("$state", state);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.ExecuteNonQuery();
        return newlyDiscovered;
    }

    public CreatureProfileDto? GetCreatureProfile(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return null;
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadCreatureProfileUnlocked(db, instanceId.Trim());
        }
    }

    public CreatureRosterDto ListCreatureRoster(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var items = new List<CreatureSpecimenDto>();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT a.instance_id, a.player_id, a.side, a.type_id, a.phase, a.level, a.xp,
                       a.match_key, a.last_ptr, a.deploy_correlation_id, a.revision, a.created_utc, a.updated_utc,
                       p.species_id, p.rarity, p.variant, p.element_primary, p.element_secondary,
                       p.traits_json, p.origin, p.nickname, p.locked, p.created_utc, p.revision,
                       p.star, p.promoted,
                       a.empire_id
                FROM rpg_unique_actors a
                JOIN rpg_creature_profiles p ON p.instance_id = a.instance_id
                WHERE a.player_id = $pid AND a.phase != 'Retired'
                ORDER BY a.created_utc ASC;
                """;
            cmd.Parameters.AddWithValue("$pid", playerId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // spec-species-rank.md §5: rank id + display name ride the roster row, copied off
                // the runtime catalog beside every other species fact here.
                var (rank, rankDisplayName) = RankForPayload(r.GetString(13));
                items.Add(new CreatureSpecimenDto
                {
                    Actor = MapUniqueActor(r),
                    Profile = new CreatureProfileDto
                    {
                        InstanceId = r.GetString(0),
                        SpeciesId = r.GetString(13),
                        Rarity = r.GetString(14),
                        Rank = rank,
                        RankDisplayName = rankDisplayName,
                        Variant = r.GetString(15),
                        ElementPrimary = r.GetString(16),
                        ElementSecondary = r.IsDBNull(17) ? null : r.GetString(17),
                        TraitIds = JsonSerializer.Deserialize<List<string>>(r.GetString(18)) ?? new List<string>(),
                        Origin = r.GetString(19),
                        Nickname = r.IsDBNull(20) ? null : r.GetString(20),
                        Locked = r.GetInt64(21) != 0,
                        CreatedUtc = r.GetString(22),
                        Revision = r.GetInt64(23),
                        Star = r.GetInt32(24),
                        Promoted = r.GetInt64(25) != 0
                    }
                });
            }

            return new CreatureRosterDto { PlayerId = playerId, Items = items };
        }
    }

    public CreatureCodexDto ListCreatureCodex(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            var entries = new List<CreatureCodexEntryDto>();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                SELECT species_id, state, first_utc, updated_utc
                FROM rpg_creature_codex WHERE player_id = $pid ORDER BY species_id;
                """;
            cmd.Parameters.AddWithValue("$pid", playerId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                // spec-species-rank.md §5: rank id + display name ride the codex entry, copied off
                // the runtime catalog (null-safe: a codex row can outlive a catalog entry).
                var (rank, rankDisplayName) = RankForPayload(r.GetString(0));
                entries.Add(new CreatureCodexEntryDto
                {
                    SpeciesId = r.GetString(0),
                    State = r.GetString(1),
                    Rank = rank,
                    RankDisplayName = rankDisplayName,
                    FirstUtc = r.GetString(2),
                    UpdatedUtc = r.GetString(3)
                });
            }

            return new CreatureCodexDto { PlayerId = playerId, Entries = entries };
        }
    }

    public (bool Ok, CreatureProfileDto? Profile) SetCreatureNickname(string instanceId, string? nickname)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE rpg_creature_profiles SET nickname = $nick, revision = revision + 1
                WHERE instance_id = $id;
                """;
            cmd.Parameters.AddWithValue("$nick", (object?)NullIfEmpty(nickname) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$id", instanceId.Trim());
            var n = cmd.ExecuteNonQuery();
            return n > 0 ? (true, ReadCreatureProfileUnlocked(db, instanceId.Trim())) : (false, null);
        }
    }

    public (bool Ok, CreatureProfileDto? Profile) SetCreatureLocked(string instanceId, bool locked)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE rpg_creature_profiles SET locked = $locked, revision = revision + 1
                WHERE instance_id = $id;
                """;
            cmd.Parameters.AddWithValue("$locked", locked ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", instanceId.Trim());
            var n = cmd.ExecuteNonQuery();
            return n > 0 ? (true, ReadCreatureProfileUnlocked(db, instanceId.Trim())) : (false, null);
        }
    }

    CreatureProfileDto? ReadCreatureProfileUnlocked(SqliteConnection db, string instanceId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT instance_id, species_id, rarity, variant, element_primary, element_secondary,
                   traits_json, origin, nickname, locked, created_utc, revision, star, promoted
            FROM rpg_creature_profiles WHERE instance_id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", instanceId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        // spec-species-rank.md §5: rank id + display name ride the profile, copied off the
        // runtime catalog (mint enforces a known species, so this is never null for a fresh mint).
        var (rank, rankDisplayName) = RankForPayload(r.GetString(1));
        return new CreatureProfileDto
        {
            InstanceId = r.GetString(0),
            SpeciesId = r.GetString(1),
            Rarity = r.GetString(2),
            Rank = rank,
            RankDisplayName = rankDisplayName,
            Variant = r.GetString(3),
            ElementPrimary = r.GetString(4),
            ElementSecondary = r.IsDBNull(5) ? null : r.GetString(5),
            TraitIds = JsonSerializer.Deserialize<List<string>>(r.GetString(6)) ?? new List<string>(),
            Origin = r.GetString(7),
            Nickname = r.IsDBNull(8) ? null : r.GetString(8),
            Locked = r.GetInt64(9) != 0,
            CreatedUtc = r.GetString(10),
            Revision = r.GetInt64(11),
            Star = r.GetInt32(12),
            Promoted = r.GetInt64(13) != 0
        };
    }
}
