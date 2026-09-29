using System.Text.Json;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Microsoft.Data.Sqlite;

namespace FusionRpg.Data;

/// <summary>One row this import refused, and why. Never dropped silently.</summary>
public readonly record struct SpeciesImportError(string SpeciesId, string Detail)
{
    public override string ToString() => $"{SpeciesId}: {Detail}";
}

/// <summary>What one species import did, or refused to do — same "all or nothing, refusal names the
/// first failure and the total count" shape <c>ImportOutcome</c> already established for atoms.</summary>
public sealed record SpeciesImportOutcome(
    bool Committed, IReadOnlyList<SpeciesImportError> Errors, int Written, int Unchanged, int Deleted)
{
    public bool IsOk => Errors.Count == 0;
}

/// <summary>
/// `species-import` (T4.6, `spec-species-generator.md`'s downstream consumer, creature-seed module 13) —
/// `gk-data/packs/fusion/data/generated/creatures/**` -> the `creature_species`/`creature_species_magnitude` tables, one transaction.
/// </summary>
public sealed partial class RpgStore
{
    void EnsureSpeciesSchemaUnlocked(SqliteConnection db)
    {
        Exec(db, """
            CREATE TABLE IF NOT EXISTS creature_species (
              species_id TEXT NOT NULL PRIMARY KEY,
              rarity TEXT NOT NULL,
              theta INTEGER NOT NULL,
              p_theta INTEGER NOT NULL,
              attack_interval_ms INTEGER NOT NULL,
              attack_interval_source TEXT NOT NULL,
              range_cells INTEGER NOT NULL,
              variant_count INTEGER NOT NULL,
              revision INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS creature_species_magnitude (
              species_id TEXT NOT NULL,
              channel TEXT NOT NULL,
              value INTEGER NOT NULL,
              PRIMARY KEY (species_id, channel)
            );
            """);

        // catalog-runtime pass-through columns (T4.8's own real precondition, resolved 2026-09-02) —
        // a database created before this migration has creature_species without them, so the addition
        // is explicit, matching effect_instance's own theta_content/content_scale_milli precedent
        // (T3.4). Defaults only ever apply to pre-migration rows read back after this point; a fresh
        // ImportSpecies call always supplies real values.
        EnsureColumn(db, "creature_species", "side", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "creature_species", "game_type_id", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(db, "creature_species", "element_primary", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "creature_species", "element_secondary", "TEXT");
        EnsureColumn(db, "creature_species", "deploy_mode", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(db, "creature_species", "acquisition", "INTEGER NOT NULL DEFAULT 0");
        // CS13: R-CS3/R-CS4's mark. Nullable, defaulted on read to `creature` — a pre-mark row must
        // read as an ordinary creature, never as excluded.
        EnsureColumn(db, "creature_species", "species_kind", "TEXT");
        // spec-species-rank.md §4: NULLABLE, with NO default — a `NOT NULL DEFAULT` would fabricate a
        // rank for a species whose threatBand/rarity was unresolved, and "skip, don't fabricate" is
        // the whole rule (Assumption 4). A pre-rank database reads NULL, which the read-back below
        // maps to "no rank" rather than to the bottom rung.
        EnsureColumn(db, "creature_species", "rank", "TEXT");
        EnsureColumn(db, "creature_species", "variants_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(db, "creature_species", "trait_pool_json", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumn(db, "creature_species", "name", "TEXT");
    }

    /// <summary>
    /// Import a whole roster in one transaction — all or nothing (E14a's own guarantee, applied here):
    /// every row is checked before the first write, so one bad row writes nothing and the refusal
    /// names it plus the total count. A stored species absent from <paramref name="species"/> is
    /// DELETED (the roster this import describes is the roster that exists — a species removed
    /// upstream, e.g. de-classified, must not linger as a stale row nothing ever prunes).
    /// </summary>
    public SpeciesImportOutcome ImportSpecies(IReadOnlyList<ConcreteSpecies> species)
    {
        if (species is null) throw new ArgumentNullException(nameof(species));

        var errors = new List<SpeciesImportError>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in species)
        {
            if (string.IsNullOrWhiteSpace(s.SpeciesId))
                errors.Add(new SpeciesImportError(s.SpeciesId ?? "", "speciesId is empty"));
            else if (!seenIds.Add(s.SpeciesId))
                errors.Add(new SpeciesImportError(s.SpeciesId, "speciesId appears twice in this import"));
        }

        if (errors.Count > 0)
            return new SpeciesImportOutcome(false, errors, 0, 0, 0);

        // Resolved BEFORE the write transaction opens (matches T5.5/T5.6's own "compute first, write
        // second" discipline) — GetAlmanacSeed opens its own connection under its own lock(_gate),
        // and _gate is per-thread reentrant, but a second connection reading while this one holds an
        // open write transaction is complexity this import has no reason to carry.
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var s in species)
        {
            var almanac = GetAlmanacSeed(s.Side, s.GameTypeId);
            names[s.SpeciesId] = !string.IsNullOrWhiteSpace(almanac?.DisplayName) ? almanac.DisplayName
                : !string.IsNullOrWhiteSpace(almanac?.TypeName) ? almanac.TypeName
                : $"Creature {s.GameTypeId}";
        }

        // Synthesized magnitude atoms are built AND validated here, before any write — E14a's
        // all-or-nothing shape: one bad row writes nothing, and the refusal names the species plus
        // the channel (a >int32 magnitude, an illegal channel, a malformed id) via the same
        // SpeciesImportError list as every other pre-write refusal. The write loop below then only
        // writes rows this phase already accepted.
        var synthesized = new Dictionary<string, IReadOnlyList<(string Channel, AtomRow Atom)>>(StringComparer.Ordinal);
        foreach (var s in species)
        {
            var built = SyntheticStatDerivedAtoms.BuildMagnitudeAtoms(s.SpeciesId, s.Magnitudes);
            foreach (var (channel, atom) in built)
            {
                var check = AtomRowValidator.Validate(atom, CurveInputOf, ComposeKindOf);
                if (!check.IsOk)
                    errors.Add(new SpeciesImportError(s.SpeciesId,
                        $"synthesized magnitude atom for channel '{channel}' is invalid: {check}"));
            }
            synthesized[s.SpeciesId] = built;
        }

        if (errors.Count > 0)
            return new SpeciesImportOutcome(false, errors, 0, 0, 0);

        // rank needs NO refusal in this pre-write validation (spec-species-rank.md §4): it is a
        // nullable column and a NULL — a rank the pipeline skipped — is a legal, non-failing value.
        // One line, stated on purpose, so a later reader does not read the omission as an oversight.

        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var tx = db.BeginTransaction();

            var written = 0;
            var unchanged = 0;

            foreach (var s in species)
            {
                var withName = s with { Name = names[s.SpeciesId] };
                var stored = ReadStoredUnlocked(db, tx, s.SpeciesId);
                if (stored is not null && SameContent(stored, withName))
                {
                    unchanged++;
                    // Upgrade imports converge without touching revisions: an unchanged species keeps
                    // its rows AND its revision, but a container missing from before this module
                    // shipped is still synthesized. The read sees committed state (this species has
                    // no writes in this transaction yet), so the second connection cannot deadlock —
                    // the same nested-read shape MintGrantUnlocked already proves.
                    if (s.Magnitudes.Count > 0
                        && GetContainer(SpeciesMagnitudeContainerId(s.SpeciesId)) is null)
                        SynthesizeMagnitudeContainerUnlocked(db, tx, s.SpeciesId, synthesized[s.SpeciesId]);
                    continue;
                }

                ExecIn(db, tx, """
                    INSERT INTO creature_species
                      (species_id, rarity, theta, p_theta, attack_interval_ms, attack_interval_source,
                       range_cells, variant_count, revision, side, game_type_id, element_primary,
                       element_secondary, deploy_mode, acquisition, variants_json, trait_pool_json, name,
                       species_kind, rank)
                    VALUES ($id, $rarity, $theta, $pTheta, $intervalMs, $intervalSource, $range, $variants, 1,
                            $side, $gameTypeId, $elPrimary, $elSecondary, $deployMode, $acquisition,
                            $variantsJson, $traitPoolJson, $name, $speciesKind, $rank)
                    ON CONFLICT(species_id) DO UPDATE SET
                      rarity = excluded.rarity, theta = excluded.theta, p_theta = excluded.p_theta,
                      attack_interval_ms = excluded.attack_interval_ms,
                      attack_interval_source = excluded.attack_interval_source,
                      range_cells = excluded.range_cells, variant_count = excluded.variant_count,
                      side = excluded.side, game_type_id = excluded.game_type_id,
                      element_primary = excluded.element_primary, element_secondary = excluded.element_secondary,
                      deploy_mode = excluded.deploy_mode, acquisition = excluded.acquisition,
                      variants_json = excluded.variants_json, trait_pool_json = excluded.trait_pool_json,
                      name = excluded.name,
                      species_kind = excluded.species_kind,
                      rank = excluded.rank,
                      revision = creature_species.revision + 1;
                    """,
                    ("$id", s.SpeciesId), ("$rarity", s.Rarity.ToString()), ("$theta", s.Theta),
                    ("$pTheta", s.PTheta), ("$intervalMs", s.AttackIntervalMs),
                    ("$intervalSource", s.AttackIntervalSource), ("$range", s.RangeCells),
                    ("$variants", s.VariantCount),
                    ("$side", s.Side), ("$gameTypeId", s.GameTypeId),
                    ("$elPrimary", s.ElementPrimary.ToString()),
                    ("$elSecondary", (object?)s.ElementSecondary?.ToString() ?? DBNull.Value),
                    ("$deployMode", s.DeployMode.ToString()), ("$acquisition", (int)s.Acquisition),
                    ("$variantsJson", JsonSerializer.Serialize(s.Variants)),
                    ("$traitPoolJson", JsonSerializer.Serialize(s.TraitPool)),
                    // CS13: R-CS3/R-CS4's mark, persisted so the runtime snapshot can refuse it.
                    ("$speciesKind", s.SpeciesKind is { Length: > 0 } kind ? kind : "creature"),
                    // spec-species-rank.md §1: written as the enum's own id, or SQL NULL when the rank
                    // was skipped — never a fabricated bottom rung.
                    ("$rank", (object?)s.Rank?.ToString() ?? DBNull.Value),
                    ("$name", (object?)names[s.SpeciesId] ?? DBNull.Value));

                ExecIn(db, tx, "DELETE FROM creature_species_magnitude WHERE species_id = $id;", ("$id", s.SpeciesId));
                foreach (var (channel, value) in s.Magnitudes)
                    ExecIn(db, tx,
                        "INSERT INTO creature_species_magnitude (species_id, channel, value) VALUES ($id, $ch, $v);",
                        ("$id", s.SpeciesId), ("$ch", channel), ("$v", value));

                // species-gear-chain T18: synthesize the magnitude container in the SAME transaction,
                // from the prebuilt rows the pre-write phase already accepted — never a committed
                // table would be a derived population on disk with drift nothing detects). Upsert,
                // never append: a re-import replaces, matching the DELETE+INSERT above. A species with
                // no magnitudes synthesizes nothing, and the consumer's fail-closed return still holds.
                SynthesizeMagnitudeContainerUnlocked(db, tx, s.SpeciesId, synthesized[s.SpeciesId]);

                written++;
            }

            var deleted = 0;
            using (var cmd = db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT species_id FROM creature_species;";
                using var r = cmd.ExecuteReader();
                var storedIds = new List<string>();
                while (r.Read()) storedIds.Add(r.GetString(0));
                foreach (var staleId in storedIds.Where(id => !seenIds.Contains(id)))
                {
                    ExecIn(db, tx, "DELETE FROM creature_species WHERE species_id = $id;", ("$id", staleId));
                    ExecIn(db, tx, "DELETE FROM creature_species_magnitude WHERE species_id = $id;", ("$id", staleId));
                    DeleteMagnitudeContainerUnlocked(db, tx, staleId);
                    deleted++;
                }
            }

            tx.Commit();
            return new SpeciesImportOutcome(true, Array.Empty<SpeciesImportError>(), written, unchanged, deleted);
        }
    }

    // species-progression SP3.3: BuildMagnitudeAtoms/Kebab moved into Core's
    // SyntheticStatDerivedAtoms (one builder, shared with SpeciesLayerProjector.ToContainer) — this
    // file now only calls it directly at both former call sites. Byte-identical atom ids/params
    // (SpeciesMagnitudeSynthTests, CreatureLawnDeployMagnitudeTests).

    /// <summary>
    /// species-gear-chain T18: the magnitude container synthesizer. One `stat.derived` atom per
    /// channel (flat, fixed amount — magnitudes are baked values, never rolled), packed into the
    /// `trait.species-magnitude-{id}` container the reconciler binds. Atom ids carry the channel in
    /// the variant slot (`atom.species-magnitude-{id}.{channel-slug}.t1`, dots flattened to dashes —
    /// the variant slot because tiers name power and these rows are all tier 1 by construction).
    /// The family lower-cases the species id: atom families are kebab-case by validator rule while
    /// species ids are camelCase (`Peashooter`), and the two namespaces meet only inside this
    /// synthesizer, deterministically both ways. The CONTAINER id keeps the raw species id —
    /// `SpeciesMagnitudeContainerId` is the consumer's join key and must match it exactly.
    /// Atoms validate exactly as `UpsertAtom` validates — but in the import's pre-write phase, not
    /// here: by the time this runs, every row was already accepted, so a failure here would mean the
    /// write diverged from the validation, which must never happen silently. The `check` below is
    /// that tripwire (defense, not flow): it throws rather than writing a row the pre-phase refused.
    /// Deterministic and idempotent: same rows in, same rows out, replaced wholesale — a re-import
    /// converges, never duplicates. A species with no magnitudes synthesizes nothing (and any stale
    /// container from a shrunk species is deleted), so the consumer's fail-closed return still holds
    /// exactly where it should.
    /// </summary>
    void SynthesizeMagnitudeContainerUnlocked(
        Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx,
        string speciesId, IReadOnlyList<(string Channel, AtomRow Atom)> built)
    {
        var containerId = SpeciesMagnitudeContainerId(speciesId);
        if (built.Count == 0)
        {
            DeleteMagnitudeContainerUnlocked(db, tx, speciesId);
            return;
        }

        var seq = 0;
        var members = new List<ContainerAtomRow>();
        foreach (var (channel, atom) in built)
        {
            var check = AtomRowValidator.Validate(atom, CurveInputOf, ComposeKindOf);
            if (!check.IsOk)
                throw new InvalidOperationException(
                    $"species '{speciesId}' writes a magnitude atom for channel '{channel}' the pre-write " +
                    $"phase refused: {check} — the write diverged from validation");
            UpsertAtomUnlocked(db, atom, tx);
            members.Add(new ContainerAtomRow(++seq, atom.AtomId));
        }

        WriteContainerUnlocked(db, tx, new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Trait,
            Atoms = members,
        });
    }

    /// <summary>Remove a shrunk or deleted species' synthesized container and its species-scoped
    /// atoms (family `atom.species-magnitude-{id}` — owned by this synthesizer, never shared).
    /// Bindings pointing at the withdrawn atoms are picked up by the consumer's existing stale
    /// path on next deploy, exactly like a removed species today.</summary>
    void DeleteMagnitudeContainerUnlocked(
        Microsoft.Data.Sqlite.SqliteConnection db, Microsoft.Data.Sqlite.SqliteTransaction tx,
        string speciesId)
    {
        var containerId = SpeciesMagnitudeContainerId(speciesId);
        ExecIn(db, tx, "DELETE FROM effect_container_atom WHERE container_id = $id;", ("$id", containerId));
        ExecIn(db, tx, "DELETE FROM effect_container_pool WHERE container_id = $id;", ("$id", containerId));
        ExecIn(db, tx, "DELETE FROM effect_container WHERE container_id = $id;", ("$id", containerId));
        var familyPrefix = $"atom.species-magnitude-{SyntheticStatDerivedAtoms.Kebab(speciesId)}.";
        ExecIn(db, tx, "DELETE FROM effect_atom WHERE atom_id LIKE $pattern ESCAPE '\\';",
            ("$pattern", familyPrefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%"));
    }

    /// <summary>
    /// `catalog-runtime`'s own snapshot source (T4.8, `spec-catalog-runtime.md` §3) — every stored
    /// species, converted to the shape `CreatureSpeciesCatalog.Configure` needs. The ONE place
    /// `ConcreteSpecies` -> `CreatureSpeciesDef` happens, so a host never hand-rolls the conversion.
    ///
    /// <para><c>SpeciesId</c> is lower-cased here — the anchor pipeline's own casing (`"Peashooter"`)
    /// and `CreatureSpeciesCatalog.Validate`'s established lower-kebab rule (matching the compiled
    /// catalog's own real ids, e.g. `"driverzombie"`) are two different, already-shipped
    /// conventions; this is the one seam where the anchor pipeline's casing meets the catalog's own
    /// rule, so every other layer keeps reading/writing the anchor's own real casing unchanged.</para>
    ///
    /// <para><c>CreatureTypeId</c> is computed here, once — <c>GameTypeId + CreatureSpeciesCatalog.CreatureTypeIdFloor</c>
    /// — never stored a second time (`ConcreteSpecies` deliberately does not carry it, its own doc
    /// comment already says why).</para>
    ///
    /// <para>⛔ <c>TraitPool</c> conversion (not <c>s.TraitPool</c> verbatim) — found for real
    /// 2026-09-02, not assumed: the anchor's own `traits` field is an OPEN, free-form array
    /// (`anchor/schema.py`'s own `_open_array_prop`, unvalidated LLM flavor text — `pea.json`'s own
    /// real values are `"Projectile-launching"`, `"Defensive"`, `"Rapid-fire"`), while
    /// `CreatureSpeciesDef.TraitPool` is validated against `CreatureTraitCatalog`'s CLOSED, curated
    /// gameplay vocabulary (`"regenerator"`, `"berserker"`, `"loyal"`, ...) — two different
    /// vocabularies that happen to share a field name. Wiring one straight into the other (an
    /// `s.TraitPool` passthrough) was tried once and caught by
    /// `SpeciesCatalogDiffTests.The_store_backed_snapshot_itself_passes_CreatureSpeciesCatalog_Validate`,
    /// which threw exactly the mismatch this comment describes. <c>ConcreteSpecies.TraitPool</c> keeps
    /// carrying the anchor's own raw flavor strings unchanged (a legitimate, separate use —
    /// `species_effects.py` reads the anchor's own `traits` field as LLM brief context) — the bridge
    /// into the gameplay-validated vocabulary instead lives in
    /// <see cref="Core.Creatures.Generation.CreatureTraitPoolCuration"/> (2026-09-06, trait-roll), called
    /// from <see cref="Core.Creatures.Generation.ConcreteSpeciesMapper.ToCreatureSpeciesDef"/> below — by
    /// species id (porting the legacy compiled catalog's own already-authored pick forward) or by a
    /// deterministic rarity/gameTypeId-seeded fallback, never by reinterpreting the flavor text
    /// itself.</para>
    /// </summary>
    public IReadOnlyList<Core.Creatures.CreatureSpeciesDef> BuildCreatureSpeciesSnapshot()
    {
        var ids = ListSpeciesIds();
        var snapshot = new List<Core.Creatures.CreatureSpeciesDef>(ids.Count);
        foreach (var id in ids)
        {
            var s = GetSpecies(id);
            if (s is null) continue; // deleted between the two reads — a fresh Configure call retries

            // T6.1-adjacent (2026-09-06, catalog-runtime's Injector flip): this mapping now lives in
            // Core.Creatures.Generation.ConcreteSpeciesMapper, shared with the Injector's own
            // ConcreteSpeciesSeedReader-sourced path, so the two hosts compute the identical roster
            // from the identical shape rather than risking a second copy silently drifting the way
            // AttackIntervalMs itself once did (missing from this exact block until 2026-09-05).
            snapshot.Add(Core.Creatures.Generation.ConcreteSpeciesMapper.ToCreatureSpeciesDef(s));
        }
        return snapshot;
    }

    public ConcreteSpecies? GetSpecies(string speciesId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadStoredUnlocked(db, null, speciesId);
        }
    }

    public IReadOnlyList<string> ListSpeciesIds()
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT species_id FROM creature_species ORDER BY species_id;";
            using var r = cmd.ExecuteReader();
            var ids = new List<string>();
            while (r.Read()) ids.Add(r.GetString(0));
            return ids;
        }
    }

    static ConcreteSpecies? ReadStoredUnlocked(SqliteConnection db, SqliteTransaction? tx, string speciesId)
    {
        ConcreteSpecies? head;
        using (var cmd = db.CreateCommand())
        {
            if (tx is not null) cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT species_id, rarity, theta, p_theta, attack_interval_ms, attack_interval_source,
                       range_cells, variant_count, side, game_type_id, element_primary, element_secondary,
                       deploy_mode, acquisition, variants_json, trait_pool_json, name, species_kind, rank
                FROM creature_species WHERE species_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", speciesId);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;

            if (!CreatureRarityIds.TryParse(r.GetString(1), out var rarity))
                throw new InvalidOperationException($"stored species '{speciesId}' has an unparseable rarity '{r.GetString(1)}'");
            if (!Enum.TryParse<ElementTypeId>(r.GetString(10), out var elementPrimary))
                throw new InvalidOperationException($"stored species '{speciesId}' has an unparseable elementPrimary '{r.GetString(10)}'");
            ElementTypeId? elementSecondary = null;
            if (!r.IsDBNull(11))
            {
                if (!Enum.TryParse<ElementTypeId>(r.GetString(11), out var parsedSec))
                    throw new InvalidOperationException($"stored species '{speciesId}' has an unparseable elementSecondary '{r.GetString(11)}'");
                elementSecondary = parsedSec;
            }
            if (!Enum.TryParse<CreatureDeployMode>(r.GetString(12), out var deployMode))
                throw new InvalidOperationException($"stored species '{speciesId}' has an unparseable deployMode '{r.GetString(12)}'");
            // spec-species-rank.md §1: a NULL (skipped) rank reads back as null, never a fabricated
            // bottom rung; a present-but-unparseable id is a real row defect and throws, the same as
            // rarity/elementPrimary above.
            CreatureRank? rank = null;
            if (!r.IsDBNull(18))
            {
                if (!CreatureRankIds.TryParse(r.GetString(18), out var parsedRank))
                    throw new InvalidOperationException($"stored species '{speciesId}' has an unparseable rank '{r.GetString(18)}'");
                rank = parsedRank;
            }

            head = new ConcreteSpecies
            {
                SpeciesId = r.GetString(0), Rarity = rarity, Theta = r.GetInt32(2), PTheta = r.GetInt64(3),
                AttackIntervalMs = r.GetInt64(4), AttackIntervalSource = r.GetString(5),
                RangeCells = r.GetInt64(6), VariantCount = r.GetInt32(7),
                Side = r.GetString(8), GameTypeId = r.GetInt32(9),
                ElementPrimary = elementPrimary, ElementSecondary = elementSecondary,
                DeployMode = deployMode, Acquisition = (CreatureAcquisition)r.GetInt32(13),
                // CS13: a NULL (pre-mark) row reads as `creature` — never as excluded.
                SpeciesKind = r.IsDBNull(17) ? "creature" : r.GetString(17),
                Rank = rank,
                Variants = JsonSerializer.Deserialize<string[]>(r.GetString(14)) ?? Array.Empty<string>(),
                TraitPool = JsonSerializer.Deserialize<string[]>(r.GetString(15)) ?? Array.Empty<string>(),
                Name = r.IsDBNull(16) ? null : r.GetString(16),
            };
        }

        var magnitudes = new Dictionary<string, long>(StringComparer.Ordinal);
        using (var cmd = db.CreateCommand())
        {
            if (tx is not null) cmd.Transaction = tx;
            cmd.CommandText = "SELECT channel, value FROM creature_species_magnitude WHERE species_id = $id;";
            cmd.Parameters.AddWithValue("$id", speciesId);
            using var r = cmd.ExecuteReader();
            while (r.Read()) magnitudes[r.GetString(0)] = r.GetInt64(1);
        }

        return head with { Magnitudes = magnitudes };
    }

    static bool SameContent(ConcreteSpecies stored, ConcreteSpecies incoming) =>
        stored.Rarity == incoming.Rarity && stored.Theta == incoming.Theta && stored.PTheta == incoming.PTheta
        && stored.AttackIntervalMs == incoming.AttackIntervalMs
        && stored.AttackIntervalSource == incoming.AttackIntervalSource
        && stored.RangeCells == incoming.RangeCells && stored.VariantCount == incoming.VariantCount
        && stored.Side == incoming.Side && stored.GameTypeId == incoming.GameTypeId
        && stored.ElementPrimary == incoming.ElementPrimary && stored.ElementSecondary == incoming.ElementSecondary
        && stored.DeployMode == incoming.DeployMode && stored.Acquisition == incoming.Acquisition
        && stored.Rank == incoming.Rank
        && stored.Variants.SequenceEqual(incoming.Variants) && stored.TraitPool.SequenceEqual(incoming.TraitPool)
        && stored.Name == incoming.Name
        && stored.Magnitudes.Count == incoming.Magnitudes.Count
        && stored.Magnitudes.All(kv => incoming.Magnitudes.TryGetValue(kv.Key, out var v) && v == kv.Value);
}
