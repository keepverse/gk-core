using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Materials;

namespace FusionRpg.Core.Items.Drops;

/// <summary>
/// One loot event, as the SERVER sees it.
///
/// <para>⛔ <b>There is no correlation-id field, and that is the design.</b> §4.4: the correlation id
/// is derived from the source record on the server, never supplied by the client — a client that can
/// pick its own loot correlation can mint on demand. This is a deliberate difference from summon and
/// expedition dispatch, which DO take a client correlation id: those are player-initiated commands, a
/// loot event is a consequence of a recorded fact.</para>
///
/// <para><see cref="ThetaActor"/> is read by the caller through
/// <c>IPowerIndexProvider.ActorIndex(ctx)</c> and passed in, so this file declares no
/// <c>f(level)</c> of any kind (D18).</para>
/// </summary>
/// <param name="KilledSpeciesId">
/// species-gear-chain T34c (`species-materials` d) — the killed species' own lower-cased runtime id
/// (the `family-map.json`/trophy-registry casing), or <c>null</c> for a non-creature-kill source.
/// Resolves a species-scope trophy group's own grant directly, and feeds
/// <see cref="LootContentView.FamiliesForSpecies"/> for a family-scope group's R22 roll — ONE species
/// lookup at the call site that already knows it (`CreatureKillLootSource`'s own caller), never two.
/// </param>
public sealed record LootRequest(
    string PlayerId,
    string SourceKind,
    string SourceId,
    ulong SourceSeed,
    int ThetaActor,
    long CatalogRevision = 0,
    long DropTableRevision = 0,
    IReadOnlyDictionary<string, int>? SquadFrameMilli = null,
    string? KilledSpeciesId = null);

/// <summary>One thing the event granted. Equipment carries the whole step 6…10 decision record.</summary>
public sealed record LootGrant(
    int Index,
    DropEntryKind Kind,
    string RefId,
    long Count,
    string AffixChannel,
    string? BaseTypeId = null,
    string? Frame = null,
    string? Role = null,
    string? RarityId = null,
    int RarityOrdinal = 0,
    int ItemLevel = 0,
    int MinTier = 0,
    int MaxTier = 0,
    int PrefixRolls = 0,
    int SuffixRolls = 0,
    int SocketCount = 0,
    ulong RollSeed = 0,
    bool EnvelopeNarrowed = false,
    bool PityForced = false,
    string? InstanceId = null);

/// <summary>The sealed outcome. Step 12 (REVEAL) is presentation only — this was decided at step 2.</summary>
public sealed record LootManifest(
    string CorrelationId,
    string TableId,
    ulong LootSeed,
    int ItemLevel,
    IReadOnlyList<LootGrant> Grants,
    IReadOnlyList<string> Notes,
    string ContextJson,
    LootPityState PityIn,
    LootPityState PityOut,
    string? FirstClearGrant,
    bool Replayed,
    string? ReplayedResultJson);

/// <summary>Step 9's mint, injected so this file stays pure and free of any store.</summary>
public readonly record struct LootMintResult(AtomRejection Rejection, string? InstanceId);

/// <summary>Everything the pipeline reads, resolved by the host. No I/O in this file.</summary>
public sealed record LootContentView(
    IReadOnlyDictionary<string, LootSourceRow> Sources,
    IReadOnlyDictionary<string, DropTableRow> Tables,
    IReadOnlyList<RarityRung> Ladder,
    Func<string, string, IReadOnlyList<string>> BaseTypesFor,
    Func<string, string, string, bool>? FirstClearAlreadyGranted = null,
    Func<string, string, string?>? RecordedManifestFor = null,
    Func<string, int, int, int>? DrawableAffixGroups = null,
    Func<LootGrant, LootMintResult>? Mint = null,
    /// <summary>D3.11 (spec-dungeon-loot.md §3): resolves an `Equipment` entry's own optional `RefId`
    /// to a domain-only base-type subset — the runtime counterpart to
    /// <see cref="DropContentLookups.BaseTypeSetExists"/>'s import-time check of the same `RefId`.
    /// `null` (the default) keeps step 6's existing, unrestricted `BaseTypesFor` path byte-identical —
    /// this delegate only ever NARROWS it, never replaces it on its own.</summary>
    Func<string, IReadOnlyList<string>>? BaseTypeSetFor = null,
    /// <summary>⭐ Step 10, live as of module 16. The base type's own <c>socketMax</c>, by id.</summary>
    Func<string, int>? SocketMaxFor = null,
    /// <summary>⭐ Step 10, live as of module 16. Both this and <see cref="SocketMaxFor"/> must be
    /// supplied or the step stays the documented no-op it was — half a socket rule would silently
    /// grant the wrong count rather than none.</summary>
    FusionRpg.Core.Items.Sockets.SocketTuning? SocketTuning = null,
    /// <summary>species-gear-chain T4: the item's kind (ordinary / set / …) by base-type id, read by
    /// the host off <c>item_set_member</c> — the same read <c>SocketHost.IsSetPiece</c> already makes.
    /// <c>null</c> (not supplied) keeps the step the documented no-op: defaulting an unsupplied kind
    /// to ordinary would silently grant set pieces the ordinary window, which is exactly the
    /// mis-grant this table exists to prevent.</summary>
    Func<string, string>? SocketKindFor = null,
    /// <summary>species-gear-chain T34c — the killed species' own family ids, in
    /// `family-map.json`'s own order (R22: a multi-family species counts as a member of EVERY listed
    /// family). Called only when a family-scope trophy group's own draw needs it. `null` (not
    /// supplied) treats every species as belonging to no family — the family group is then skipped
    /// entirely for every kill (§ Design 5 step 3's own no-family rule), never a default that could
    /// silently fabricate a filler family.</summary>
    Func<string, IReadOnlyList<string>>? FamiliesForSpecies = null,
    /// <summary>D4.27 (spec-unique-pipeline.md §5): resolves a `unique` entry's own `RefId` (a
    /// container id, `item.&lt;slug&gt;`) to its AUTHORED rarity id — "rung = the container's own
    /// `Rarity`, never drawn". Nothing else in this file can answer this (no lookup from a ref id to a
    /// `ContainerRow`/its rarity exists anywhere in `Core` — confirmed by a dedicated search; the real
    /// resolver lives in the Data layer, `RpgStore.GetContainer`, which this I/O-free file cannot call
    /// itself). `null` (the default) refuses any unique draw (`drop.unique-rarity-unresolved`) rather
    /// than guessing a rarity — the same "no default, refuse" posture `DelvePrices.Merchant` already
    /// uses for its own undesigned price.</summary>
    Func<string, string?>? UniqueRarityFor = null,
    /// <summary>D3.15/D4.12/D3.11 (this session, 2026-09-07): resolves a `unique` entry's own `RefId`
    /// to its AUTHORED `(Frame, BaseTypeId)` pair, the identical resolver shape as
    /// <see cref="UniqueRarityFor"/> for the identical reason — `ContainerRow.Frame`/`.BaseTypeId` are
    /// Data-layer fields this I/O-free Core file cannot read itself. One combined delegate, not two:
    /// the pair is only ever meaningful together (a `LootGrant` with a `BaseTypeId` but no `Frame`, or
    /// the reverse, is not a real state `item_generation` can stamp), so a caller cannot half-supply
    /// it the way <see cref="SocketMaxFor"/>/<see cref="SocketTuning"/> structurally can. `null` (not
    /// supplied) or a resolved-but-unresolvable ref both REFUSE the draw (`drop.unknown-unique`) —
    /// `item_generation.frame`/`.base_type_id` are `NOT NULL` columns, so a unique instance minted
    /// without this pair could never actually be persisted; refusing loudly here, at the one point that
    /// knows it, is strictly better than minting a grant nothing downstream can save.</summary>
    Func<string, (string Frame, string BaseTypeId)?>? UniqueBaseTypeFor = null,
    /// <summary>Empire-development Task 1.3a (spec-relic-item-kind.md §Design 3): mints a `Relic`
    /// entry's own grant into a durable <c>rpg_item</c> row — and nothing else. Kept SEPARATE from
    /// <see cref="Mint"/>, not overloaded, because the two write different tables
    /// (<c>item_generation</c>+<c>rpg_item</c> via the instance path vs. <c>rpg_item</c> alone for a
    /// thing with no base type, role or frame) and a shared delegate would need a branch inside the
    /// host implementation that this design keeps at the call-site instead. `null` (the default)
    /// leaves a relic draw unminted but still resolved — a relic grant needs no resolver-derived
    /// field (unlike `Unique`, which refuses without its rarity/base-type resolvers), so there is
    /// nothing unpersistable to refuse. The host closes over its own player id, the same way
    /// `DelveLoot` already closes `Mint` over the room Θ.</summary>
    Func<LootGrant, LootMintResult>? MintRelic = null);

/// <summary>Server-derived correlation ids (§4.4). One shape per source kind, none client-reachable.</summary>
public static class LootCorrelation
{
    public static string Derive(string sourceKind, string sourceId) => sourceKind switch
    {
        "web-wave" => $"loot:{sourceId}",
        "expedition-tier" => $"loot:exp:{sourceId}",
        "world-sector" => $"loot:sector:{sourceId}",
        DropTableValidator.UndesignedSourceKind => $"loot:pvz:{sourceId}",
        // D3.10 (spec-dungeon-loot.md §1, :108-109; party-dungeon-ideal.md :1490-1491): `dungeon-room`'s
        // own sourceId already arrives pre-joined as "{delveId}:{r}:{c}" (LootPipeline.cs:102's own
        // literal construction, cited in spec) -- Derive only prepends the "loot:delve:" prefix, the
        // exact same shape every other arm above already uses (prefix, then sourceId verbatim).
        "dungeon-room" => $"loot:delve:{sourceId}",
        // `dungeon-quest`'s own sourceId is likewise pre-joined as "{delveId}:quest:{questId}" (the
        // ideal's own literal example: "loot:delve:{delveId}:quest:{questId}") -- same prepend-only shape.
        "dungeon-quest" => $"loot:delve:{sourceId}",
        // `dungeon-clear`'s own sourceId is JUST the domain id (spec, verbatim: "for dungeon-clear the
        // source id is the domain"), not pre-joined -- ASSUMED to append ":clear" after it, per the
        // ideal's own literal example ("loot:delve:{delveId}:clear") read as a template whose own
        // "{delveId}" slot is filled by whatever sourceId this call receives (the domain id here, not
        // literally a delve id) -- the one arm below that departs from "prefix only" above, stated
        // explicitly since the departure is not self-evident from the other three.
        "dungeon-clear" => $"loot:delve:{sourceId}:clear",
        // drop-tables `siege-loot` (2026-09-07): base-defense's first loot_source binding. sourceId
        // arrives pre-joined as "{sectorId}:{turn}" (SiegeLoot.cs's own construction) -- turn-qualified
        // so a district retaken more than once (won, lost, retaken, won again) is a distinct loot event
        // each time, never a replay of an earlier siege's manifest.
        "siege-assault" => $"loot:siege:{sourceId}",
        // species-gear-chain T30 (`creature-drop-tables` E1): a creature kill. sourceId arrives
        // pre-joined as "{speciesId}:{killRef}" (`CreatureKillLootSource`'s own construction) --
        // kill-qualified so two kills never share a correlation id and a retried kill replays
        // instead of minting twice. Same prepend-only shape as every arm above.
        "creature-kill" => $"loot:kill:{sourceId}",
        _ => throw new ArgumentException($"no correlation shape for source kind '{sourceKind}'", nameof(sourceKind)),
    };
}

/// <summary>
/// I12's twelve ordered steps, plus module 11's <b>5a</b>.
///
/// <code>
///  0  LOOT EVENT           server-side fact; correlation id derived FROM the source record
///  1  IDEMPOTENCY GATE     hit on (player_id, correlation_id) → return the manifest, mint nothing
///  2  SEAL THE SEED        loot_seed = DeriveStream(sourceSeed, "loot:"+correlationId).NextULong()
///  3  ITEM LEVEL           content level + jitter                      [item.ilvl]
///  4  DROP TABLE           loot_source → table_id; reject if its ilvl band excludes it
///  5a VOLUME SCALE     ⭐  rollsEffective per group from Θ_actor        [item.volume.{table}.{group}]
///  5  GROUP DRAWS          each group draws rollsEffective times       [item.table.{t}.{g}]
///  6  BASE TYPE            frame → role → base type                    [item.base.{i}]
///  7  RARITY               weighted ladder draw, shifted, floored, pity-checked   [item.rarity.{i}]
///  8  ENVELOPE             (rolls, min_tier, max_tier) = band ∩ ilvl cap          [item.rolls.{i}]
///  9  AFFIX DRAW + FREEZE  Instantiator.TryInstantiate(...) carrying `channel`
/// 10  SOCKETS              I4's count rule, last so it can never shift an affix   [item.socket]
/// 11  PERSIST              ONE transaction (the STORE's job — this returns the manifest to persist)
/// 12  REVEAL               presentation only — the outcome was sealed at step 2
/// </code>
///
/// <para>The three contested orderings and why they hold: <b>level before table</b> (the band is a
/// filter, not a post-hoc correction); <b>base before rarity</b> (uniques and set pieces are defined
/// ON a base type); <b>sockets last</b> (a socket count consuming a stream earlier would move every
/// affix roll at that band with no content-hash change).</para>
/// </summary>
public static class LootPipeline
{
    static LootPipeline() => ContentRuleNamespaces.Register("drop");

    /// <summary>Notes written to <c>item_drop_log.notes</c> — the vocabulary, in one place.</summary>
    public const string NoteEnvelopeNarrowed = "envelope_narrowed";
    public const string NotePityForced = "pity_forced";

    public static AtomRejection Resolve(
        LootRequest request,
        LootContentView view,
        DropVolumeTuning tuning,
        LootPityState pity,
        out LootManifest? manifest)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (view is null) throw new ArgumentNullException(nameof(view));
        manifest = null;

        // ---- 0  LOOT EVENT ------------------------------------------------------------------------
        var correlationId = LootCorrelation.Derive(request.SourceKind, request.SourceId);

        // ---- 1  IDEMPOTENCY GATE -----------------------------------------------------------------
        // A retry mints NOTHING. It returns the recorded manifest, advances no counter, writes no
        // ledger row. The store's UNIQUE(player_id, correlation_id) is the second net under this one.
        if (view.RecordedManifestFor?.Invoke(request.PlayerId, correlationId) is { } recorded)
        {
            manifest = new LootManifest(correlationId, "", 0, 0, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", pity, pity, null, Replayed: true, recorded);
            return AtomRejection.Ok;
        }

        if (!view.Sources.TryGetValue(request.SourceKind + ":" + request.SourceId, out var source))
            return AtomRejection.ContentRule("drop.unknown-loot-source",
                $"no loot_source row for '{request.SourceKind}:{request.SourceId}'");

        // ⛔ Refused BY NAME, never defaulted to 1 (§4.1, §11 Q8).
        if (string.Equals(source.SourceKind, DropTableValidator.UndesignedSourceKind, StringComparison.Ordinal))
            return AtomRejection.ContentRule("drop.source-kind-undesigned",
                $"loot_source '{source.Key}' is of kind '{DropTableValidator.UndesignedSourceKind}' and no "
                + "contentLevel source exists for it; refused rather than defaulted");

        // ---- 2  SEAL THE SEED --------------------------------------------------------------------
        var lootSeed = SeededRng.DeriveStream(request.SourceSeed, LootStreams.LootSeed(correlationId)).NextULong();

        // ---- 3  ITEM LEVEL -----------------------------------------------------------------------
        // ⛔ Reads the CONTENT and nothing else. `request` supplies no level to this step, by design:
        // the moment item level tracks player level, every piece of content yields the same gear and
        // the map flattens (§4.1).
        var itemLevel = ItemLevel(source.ContentLevel, lootSeed, tuning);

        // ---- 4  DROP TABLE -----------------------------------------------------------------------
        if (!view.Tables.TryGetValue(source.TableId, out var table))
            return AtomRejection.ContentRule("drop.unknown-drop-table",
                $"loot_source '{source.Key}' points at table '{source.TableId}', which is not loaded");
        if (!table.Enabled)
            return AtomRejection.ContentRule("drop.table-disabled",
                $"table '{table.TableId}' is disabled; the row is kept and never draws (E5's rule)");
        if (table.MinIlvl is { } lo && itemLevel < lo)
            return AtomRejection.ContentRule("drop.ilvl-band-excludes",
                $"item level {itemLevel} is below table '{table.TableId}'s min_ilvl {lo}");
        if (table.MaxIlvl is { } hi && itemLevel > hi)
            return AtomRejection.ContentRule("drop.ilvl-band-excludes",
                $"item level {itemLevel} is above table '{table.TableId}'s max_ilvl {hi}");

        // ---- 5a  VOLUME SCALE --------------------------------------------------------------------
        var scaleMilli = DropVolume.VolumeScaleMilli(request.ThetaActor, tuning);

        // species-gear-chain T34c (R22): ONE species lookup for the whole kill, read here and used by
        // both the group-skip rule and the family-scope resolver below — never re-derived per group.
        var killedFamilies = request.KilledSpeciesId is { Length: > 0 } killedSpeciesId
            ? view.FamiliesForSpecies?.Invoke(killedSpeciesId) ?? Array.Empty<string>()
            : Array.Empty<string>();

        var grants = new List<LootGrant>();
        var notes = new List<string>();
        var index = 0;
        var pityCursor = pity;

        var rejection = DrawTable(table, depth: 0);
        if (!rejection.IsOk) return rejection;

        // ---- first-clear grant (§3.5) — deterministic, fires once, never rolls ---------------------
        string? firstClear = null;
        if (source.FirstClearGrant is { Length: > 0 } grant
            && view.FirstClearAlreadyGranted?.Invoke(request.PlayerId, source.SourceKind, source.SourceId) != true)
        {
            firstClear = grant;
            grants.Add(new LootGrant(index++, DropEntryKind.Equipment, grant, 1, AffixChannels.Drop,
                ItemLevel: itemLevel, PrefixRolls: 0, SuffixRolls: 0));
        }

        manifest = new LootManifest(
            correlationId, table.TableId, lootSeed, itemLevel, grants, notes,
            ContextJson(request, scaleMilli), pity, pityCursor, firstClear,
            Replayed: false, ReplayedResultJson: null);
        return AtomRejection.Ok;

        AtomRejection DrawTable(DropTableRow t, int depth)
        {
            if (depth > tuning.MaxNestingDepth)
                return AtomRejection.ContentRule("drop.depth-exceeded",
                    $"table '{t.TableId}' nests past depth {tuning.MaxNestingDepth}");

            foreach (var group in t.Groups.OrderBy(g => g.Seq))
            {
                // species-gear-chain T34c (§ Design 5 step 3, R9): "a creature in no family gets NO
                // family leg — never a fabricated filler, never a general fallback". A group whose
                // only non-Nothing entries are family-scope trophy directives is treated as ABSENT
                // for a no-family kill: no volume draw, no group draw, no family-index draw — none of
                // this group's own named streams are touched for this kill at all, which is what
                // "skips the family group BY RULE" and "consumes no draw" both actually mean.
                var nonNothing = group.Entries.Where(e => e.Kind != DropEntryKind.Nothing).ToList();
                if (nonNothing.Count > 0 && nonNothing.All(e => e.TrophyScope == "family") &&
                    killedFamilies.Count == 0)
                    continue;

                // ---- 5a per group: the fractional remainder is a Bernoulli on its OWN named stream.
                var volumeRng = new AtomRandom(lootSeed, LootStreams.Volume(t.TableId, group.GroupKey));
                // ⭐ The volume term applies at the TOP LEVEL only. A nested table draws its own
                // authored rolls: compounding Θ once per nesting level would reintroduce exactly the
                // quadratic shape D18 exists to refuse, and §5.3 property 5 is explicit that nesting
                // is for REUSE, not for depth.
                var draws = DropVolume.RollsEffective(group.Rolls, depth == 0 ? scaleMilli : 1000, volumeRng);

                var drawRng = new AtomRandom(lootSeed, depth == 0
                    ? LootStreams.GroupDraw(t.TableId, group.GroupKey)
                    : LootStreams.NestedGroupDraw(t.TableId, group.GroupKey, depth));

                var guaranteed = group.Entries.All(e => e.Kind != DropEntryKind.Nothing);

                for (long d = 0; d < draws; d++)
                {
                    // ---- 5  GROUP DRAWS ----------------------------------------------------------
                    var drawn = DropTableDraw.Draw(group.Entries, itemLevel, drawRng);
                    if (drawn.Entry is null)
                    {
                        // A group that was GUARANTEED and lost everything is a rejection, not a
                        // silent nothing: "silently under-filling is the failure this program exists
                        // to remove". A group with a `nothing` row simply yields nothing.
                        if (guaranteed)
                            return AtomRejection.Fail(AtomRejectionReason.UnsatisfiablePool,
                                $"table '{t.TableId}' group '{group.GroupKey}' is guaranteed and has no drawable entry");
                        continue;
                    }

                    var entry = drawn.Entry;
                    if (entry.Kind == DropEntryKind.Nothing) continue;

                    if (!DropTableDraw.IsAvailable(entry.Kind))
                        return AtomRejection.ContentRule("drop.entry-kind-unavailable",
                            $"table '{t.TableId}' group '{group.GroupKey}' seq {entry.Seq} drew "
                            + $"'{entry.Kind.ToString().ToLowerInvariant()}' — "
                            + DropTableDraw.UnavailableKinds[entry.Kind]);

                    if (entry.Kind == DropEntryKind.Table)
                    {
                        if (!view.Tables.TryGetValue(entry.RefId, out var nested))
                            return AtomRejection.ContentRule("drop.unknown-drop-table",
                                $"table '{t.TableId}' nests '{entry.RefId}', which is not loaded");
                        var nestedResult = DrawTable(nested, depth + 1);
                        if (!nestedResult.IsOk) return nestedResult;
                        continue;
                    }

                    if (entry.Kind == DropEntryKind.Unique)
                    {
                        var unique = MintUnique(entry, out var uniqueRejection);
                        if (!uniqueRejection.IsOk) return uniqueRejection;
                        grants.Add(unique!);
                        continue;
                    }

                    if (entry.Kind == DropEntryKind.Relic)
                    {
                        var relic = MintRelic(entry, out var relicRejection);
                        if (!relicRejection.IsOk) return relicRejection;
                        grants.Add(relic!);
                        continue;
                    }

                    if (entry.Kind != DropEntryKind.Equipment)
                    {
                        var refId = entry.RefId;
                        if (entry.Kind == DropEntryKind.Material && entry.TrophyScope is { Length: > 0 } scope)
                        {
                            var resolved = ResolveTrophyGrantId(t.TableId, group.GroupKey, scope,
                                entry.TrophySlot!.Value, out var trophyRejection);
                            if (!trophyRejection.IsOk) return trophyRejection;
                            refId = resolved!;
                        }

                        // Kind is drawn; quantity is rolled; nothing is scaled. Inclusive integers.
                        var qtyRng = new AtomRandom(lootSeed, LootStreams.Quantity(index));
                        long count = qtyRng.NextInclusive(entry.MinCount, entry.MaxCount);
                        grants.Add(new LootGrant(index++, entry.Kind, refId, count, entry.AffixChannel));
                        continue;
                    }

                    var equipment = MintEquipment(entry, out var equipmentRejection);
                    if (!equipmentRejection.IsOk) return equipmentRejection;
                    grants.Add(equipment!);
                }
            }

            return AtomRejection.Ok;
        }

        /// <summary>
        /// species-gear-chain T34c (R22 cost side's own drop-time mirror) — turns a drawn trophy
        /// SCOPE TOKEN entry into the ONE concrete id this grant carries.
        ///
        /// <para><b>species scope:</b> refuses by name if this loot event names no killed species, or
        /// if the composed id has no registry row (never a silent nothing — § Design 5 step 2's own
        /// "refused by name" rule, restated for the drop side of species-materials T34b's own
        /// resolve-time refusal).</para>
        ///
        /// <para><b>family scope (R22):</b> a single-family species consumes NO draw — the family is
        /// already known. A multi-family species rolls <c>index = rng.NextInt(count)</c> on the named
        /// stream <see cref="LootStreams.TrophyFamily"/>, over its OWN family-map.json order, so the
        /// same <c>(SourceSeed, correlationId)</c> always resolves the same family. The zero-family
        /// case is unreachable here for a family-ONLY group (the caller's own group-skip rule already
        /// removes it before the draw), but a MIXED group could still draw this entry for a no-family
        /// species — refused by name rather than silently dropped, never reachable by rule alone.</para>
        /// </summary>
        string? ResolveTrophyGrantId(
            string tableId, string groupKey, string scope, int slot, out AtomRejection rejected)
        {
            rejected = AtomRejection.Ok;

            if (scope == "species")
            {
                if (request.KilledSpeciesId is not { Length: > 0 } speciesId)
                {
                    rejected = AtomRejection.ContentRule("drop.trophy-no-killed-species",
                        $"table '{tableId}' group '{groupKey}' drew a species trophy but this loot "
                        + "event names no killed species");
                    return null;
                }

                var speciesTrophyId = MaterialCatalog.ComposeTrophyId("species", speciesId, slot);
                if (!MaterialCatalog.IsTrophyId(speciesTrophyId))
                {
                    rejected = AtomRejection.ContentRule("drop.trophy-slot-unknown",
                        $"table '{tableId}' group '{groupKey}' resolved '{speciesTrophyId}', which the "
                        + "injected trophy registry does not hold");
                    return null;
                }
                return speciesTrophyId;
            }

            if (killedFamilies.Count == 0)
            {
                rejected = AtomRejection.ContentRule("drop.trophy-species-has-no-family",
                    $"table '{tableId}' group '{groupKey}' drew a family trophy but the killed species "
                    + "belongs to no family");
                return null;
            }

            var familyId = killedFamilies.Count == 1
                ? killedFamilies[0] // R22: single-family species consumes no draw from the stream
                : killedFamilies[SeededRng.DeriveStream(lootSeed, LootStreams.TrophyFamily(tableId, groupKey))
                    .NextInt(killedFamilies.Count)];

            var familyTrophyId = MaterialCatalog.ComposeTrophyId("family", familyId, slot);
            if (!MaterialCatalog.IsTrophyId(familyTrophyId))
            {
                rejected = AtomRejection.ContentRule("drop.trophy-slot-unknown",
                    $"table '{tableId}' group '{groupKey}' resolved '{familyTrophyId}', which the "
                    + "injected trophy registry does not hold");
                return null;
            }
            return familyTrophyId;
        }

        LootGrant? MintEquipment(DropTableEntryRow entry, out AtomRejection rejected)
        {
            var i = index++;

            // ---- 6  BASE TYPE ---------------------------------------------------------------------
            // ⏸ UNIFORM over the legal set, and this is a DEFERRAL with a reason, not a default.
            // I12 §3.3's smart loot is frame-weighted, and `frameWeight(f) = 250 + 750 ×
            // squadShareMilli(f)/1000` reads the deployed squad's FRAME MIX — `frame` exists on no
            // species type today (X1 `frame-classify`, resolved 2026-09-03 and UNBUILT), so a
            // frame-weighted draw over an unclassified roster is a uniform draw with extra code. It is
            // also the one bias that can break step 6, and step 6 feeds step 9's `affix_channel`,
            // which X4 weights composition off — landing a bias here before X4's weights exist means
            // the two get tuned against each other later, from opposite sides.
            // Trigger to revisit: X1 built AND X4 landed, whichever is later. Owner: this module.
            var legal = view.BaseTypesFor(entry.Frame!, entry.Role!);
            // D3.11: RefId ∩ legal when authored AND the host supplies a resolver -- null host delegate
            // or an empty RefId both keep this byte-identical to the pre-D3.11 path (spec, verbatim).
            if (entry.RefId is { Length: > 0 } refId && view.BaseTypeSetFor is { } baseTypeSetFor)
            {
                var domainSet = new HashSet<string>(baseTypeSetFor(refId), StringComparer.Ordinal);
                legal = legal.Where(domainSet.Contains).ToList();
            }
            if (legal.Count == 0)
            {
                rejected = AtomRejection.ContentRule("drop.no-legal-base-type",
                    $"no base type exists for frame '{entry.Frame}' role '{entry.Role}'");
                return null;
            }

            var baseRng = new AtomRandom(lootSeed, LootStreams.BaseType(i));
            var baseTypeId = legal[baseRng.NextInclusive(0, legal.Count - 1)];

            // ---- 7  RARITY ------------------------------------------------------------------------
            var rarityRng = new AtomRandom(lootSeed, LootStreams.Rarity(i));
            var rarityResult = RarityDraw.Draw(view.Ladder, entry, pityCursor, tuning, rarityRng, out var pityOutcome);
            if (!rarityResult.IsOk) { rejected = rarityResult; return null; }
            pityCursor = pityOutcome.Next;
            if (pityOutcome.Forced && !notes.Contains(NotePityForced)) notes.Add(NotePityForced);

            var rung = view.Ladder.First(r => string.Equals(r.RarityId, pityOutcome.RarityId, StringComparison.Ordinal));

            // ---- 8  ENVELOPE ----------------------------------------------------------------------
            var rollsRng = new AtomRandom(lootSeed, LootStreams.Rolls(i));
            var envelope = DropEnvelope.Resolve(rung, itemLevel, rollsRng,
                view.DrawableAffixGroups is { } groups
                    ? (min, max) => groups(baseTypeId, min, max)
                    : null);
            if (envelope.Narrowed && !notes.Contains(NoteEnvelopeNarrowed)) notes.Add(NoteEnvelopeNarrowed);

            // ---- 9  AFFIX DRAW + FREEZE -------------------------------------------------------------
            // The roll seed is itself derived, so BOTH reproduction contracts hold at once: the atom
            // layer's (same container/revision/roll_seed ⇒ byte-identical instance) and this lane's
            // (same loot_seed/revisions ⇒ identical manifest).
            var rollSeed = SeededRng.DeriveStream(lootSeed, LootStreams.RollSeed(i)).NextULong();

            var grant = new LootGrant(
                i, DropEntryKind.Equipment, entry.RefId, 1, entry.AffixChannel,
                baseTypeId, entry.Frame, entry.Role, rung.RarityId, rung.Ordinal, itemLevel,
                envelope.MinTier, envelope.MaxTier, envelope.PrefixRolls, envelope.SuffixRolls,
                SocketCount: Sockets(rollSeed, view, baseTypeId, rung.RarityId), RollSeed: rollSeed,
                EnvelopeNarrowed: envelope.Narrowed, PityForced: pityOutcome.Forced);

            if (view.Mint is { } mint)
            {
                var minted = mint(grant);
                if (!minted.Rejection.IsOk) { rejected = minted.Rejection; return null; }
                grant = grant with { InstanceId = minted.InstanceId };
            }

            rejected = AtomRejection.Ok;
            return grant;
        }

        /// <summary>
        /// D4.27 (spec-unique-pipeline.md §5): "`MintUnique` (beside `MintEquipment`): `rollSeed =
        /// DeriveStream(lootSeed, LootStreams.RollSeed(i)).NextULong()`, `ItemLevel` from step 3, rung
        /// = the container's own `Rarity` (authored, never drawn), then `view.Mint`." No base type, no
        /// envelope, no affix draw — a unique's own fixed core and variance pool are
        /// <see cref="FusionRpg.Core.Items.Uniques.UniqueContainerBuild.From"/>'s job, already done
        /// before this pipeline ever sees the container; this arm only assembles the grant and mints.
        /// </summary>
        LootGrant? MintUnique(DropTableEntryRow entry, out AtomRejection rejected)
        {
            var i = index++;

            if (view.UniqueRarityFor is not { } rarityFor)
            {
                rejected = AtomRejection.ContentRule("drop.unique-rarity-unresolved",
                    $"drew unique '{entry.RefId}' but the host supplies no UniqueRarityFor resolver");
                return null;
            }

            var rarityId = rarityFor(entry.RefId);
            if (rarityId is null)
            {
                rejected = AtomRejection.ContentRule("drop.unknown-unique",
                    $"unique container '{entry.RefId}' has no resolvable rarity");
                return null;
            }

            var rung = view.Ladder.FirstOrDefault(r => string.Equals(r.RarityId, rarityId, StringComparison.Ordinal));
            if (rung is null)
            {
                rejected = AtomRejection.ContentRule("drop.unknown-unique",
                    $"unique container '{entry.RefId}' resolved rarity '{rarityId}', which is not on the ladder");
                return null;
            }

            // D3.15/D4.12/D3.11: the SAME two-step "resolver missing, then resolver returns nothing"
            // shape as UniqueRarityFor above, for the identical reason -- item_generation.frame/
            // .base_type_id are NOT NULL, so a unique instance minted without this pair could never be
            // persisted; refusing here, before Mint ever runs, is strictly better than minting a grant
            // nothing downstream can save.
            if (view.UniqueBaseTypeFor is not { } baseTypeFor)
            {
                rejected = AtomRejection.ContentRule("drop.unique-base-type-unresolved",
                    $"drew unique '{entry.RefId}' but the host supplies no UniqueBaseTypeFor resolver");
                return null;
            }

            var baseType = baseTypeFor(entry.RefId);
            if (baseType is not { } resolvedBaseType)
            {
                rejected = AtomRejection.ContentRule("drop.unknown-unique",
                    $"unique container '{entry.RefId}' has no resolvable frame/base type");
                return null;
            }

            var rollSeed = SeededRng.DeriveStream(lootSeed, LootStreams.RollSeed(i)).NextULong();

            var grant = new LootGrant(
                i, DropEntryKind.Unique, entry.RefId, 1, entry.AffixChannel,
                BaseTypeId: resolvedBaseType.BaseTypeId, Frame: resolvedBaseType.Frame,
                RarityId: rung.RarityId, RarityOrdinal: rung.Ordinal, ItemLevel: itemLevel, RollSeed: rollSeed);

            if (view.Mint is { } mint)
            {
                var minted = mint(grant);
                if (!minted.Rejection.IsOk) { rejected = minted.Rejection; return null; }
                grant = grant with { InstanceId = minted.InstanceId };
            }

            rejected = AtomRejection.Ok;
            return grant;
        }

        /// <summary>
        /// Empire-development Task 1.3a (spec-relic-item-kind.md §Design 3): beside
        /// <c>MintUnique</c>. A relic has no Frame/BaseTypeId/Role and no rarity-ladder membership:
        /// it is never equipped and occupies no <c>item_base_type</c> row, so it cannot satisfy
        /// <c>item_generation</c>'s NOT NULL triple (<c>RpgStore.Loot.cs:24-26,113-118</c>) and must not
        /// try to. The grant carries nulls for all four fields — no change to <c>LootGrant</c> itself
        /// was needed — and the host (<c>view.MintRelic</c>) writes <c>rpg_item</c> (+ its
        /// <c>effect_instance</c>) only, mirroring <c>rpg_item</c>'s own "ownership is policy, not
        /// content" design (<c>RpgStore.Items.cs:40-44</c>) by writing ownership directly and skipping
        /// the drop-provenance/pity stamp a relic has no pity mechanism to feed.
        /// </summary>
        LootGrant? MintRelic(DropTableEntryRow entry, out AtomRejection rejected)
        {
            var i = index++;

            if (string.IsNullOrWhiteSpace(entry.RefId))
            {
                rejected = AtomRejection.ContentRule("drop.missing-ref",
                    $"seq {entry.Seq}: a 'relic' entry names one specific thing and carries no ref");
                return null;
            }

            var rollSeed = SeededRng.DeriveStream(lootSeed, LootStreams.RollSeed(i)).NextULong();

            var grant = new LootGrant(
                i, DropEntryKind.Relic, entry.RefId, 1, entry.AffixChannel,
                BaseTypeId: null, Frame: null, Role: null,
                RarityId: null, RarityOrdinal: 0,
                ItemLevel: itemLevel, RollSeed: rollSeed);

            if (view.MintRelic is { } mint)
            {
                var minted = mint(grant);
                if (!minted.Rejection.IsOk) { rejected = minted.Rejection; return null; }
                grant = grant with { InstanceId = minted.InstanceId };
            }

            rejected = AtomRejection.Ok;
            return grant;
        }
    }

    /// <summary>
    /// Step 3. <c>itemLevel = max(1, contentLevel + j)</c>, with <c>j ∈ {−1, 0, +1}</c> on
    /// <c>item.ilvl</c>. Takes no actor input, and that absence is the rule §4.1 states.
    /// </summary>
    public static int ItemLevel(int contentLevel, ulong lootSeed, DropVolumeTuning tuning)
    {
        var rng = new AtomRandom(lootSeed, LootStreams.ItemLevel);
        var roll = rng.NextPerMille();
        var jitter = roll < tuning.JitterDownBelowPerMille ? -1
            : roll < tuning.JitterFlatBelowPerMille ? 0
            : 1;
        return Math.Max(1, contentLevel + jitter);
    }

    /// <summary><c>level_req = max(1, itemLevel − 2)</c> — you should always be able to wear what the
    /// content you just beat dropped. The gate itself is shipped and fires at bind.</summary>
    public static int LevelReq(int itemLevel) => Math.Max(1, itemLevel - 2);

    /// <summary>
    /// ⭐ <b>Step 10 — LIVE as of module 16 (`sockets`), 2026-09-05.</b>
    ///
    /// <para>It shipped as a documented no-op that reserved its stream, because module 16 owned the
    /// count rule and <c>rarity.socket_min</c>/<c>socket_max</c> had no decided shape yet (SC7 refused
    /// them). Both are now closed: <see cref="Sockets.SocketGeometry.SocketsAtDrop"/> is the rule and
    /// the two <c>rarity_budget</c> keys are registered. Reserving the stream is exactly what makes
    /// this a wiring change and not a migration — <b>every affix roll at every band is byte-identical
    /// across the switch</b>, because the count was always drawn from its own derived stream.</para>
    ///
    /// <para>The host supplies the corpus lookup and the tuning; with either absent the step stays the
    /// no-op it was, and still advances. <b>Half a socket rule would grant the wrong count</b>, which
    /// is worse than granting none.</para>
    /// </summary>
    static int Sockets(ulong rollSeed, LootContentView view, string baseTypeId, string rungId)
    {
        if (view.SocketMaxFor is null || view.SocketTuning is null || view.SocketKindFor is null)
        {
            var socketRng = SeededRng.DeriveStream(rollSeed, LootStreams.Sockets);
            _ = socketRng.NextULong();
            return 0;
        }

        return FusionRpg.Core.Items.Sockets.SocketGeometry.SocketsAtDrop(
            view.SocketMaxFor(baseTypeId), rungId, rollSeed, view.SocketTuning, view.SocketKindFor(baseTypeId));
    }

    /// <summary>
    /// §4.3's replay input, written from the FIRST drop rather than retrofitted later.
    ///
    /// <para><c>smartLoot</c> and <c>squadFrameMix</c> are the two keys I12 §3.3's smart loot will
    /// write. They are reserved and written NOW — <c>smartLoot: false</c> and whatever mix the caller
    /// can observe — so §4.3's rule that "a settings change must not alter an already-sealed result"
    /// is true from the first drop, instead of a replay input being bolted onto a log that never had
    /// one.</para>
    /// </summary>
    public static string ContextJson(LootRequest request, long volumeScaleMilli)
    {
        var mix = new Dictionary<string, int>(StringComparer.Ordinal);
        if (request.SquadFrameMilli is not null)
            foreach (var kv in request.SquadFrameMilli.OrderBy(k => k.Key, StringComparer.Ordinal))
                mix[kv.Key] = kv.Value;

        return JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["smartLoot"] = false,
            ["squadFrameMix"] = mix,
            ["thetaActor"] = request.ThetaActor,
            ["volumeScaleMilli"] = volumeScaleMilli,
            ["catalogRevision"] = request.CatalogRevision,
            ["dropTableRevision"] = request.DropTableRevision,
        });
    }
}
