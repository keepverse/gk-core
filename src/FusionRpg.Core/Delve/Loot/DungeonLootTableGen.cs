using System.Text.Json;
using System.Text.Json.Nodes;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Uniques;

namespace FusionRpg.Core.Delve.Loot;

/// <summary>
/// D4.28 (spec-unique-pipeline.md §5, "The `boss-unique` drop group and first clear by id") — the
/// group's own binding-and-eligibility logic, plus the domain anchor's `firstClearRef` validation.
/// D3.15 (spec-dungeon-loot.md, Structure: "the planner-JSON → `DropTableRow` generator, spec §9's
/// own 'stage-1b infrastructure'") shares this file: the basic per-(climate, room-kind) table
/// generator below (<see cref="BoundRoomKinds"/>/<see cref="Build(IReadOnlyList{string},Weights)"/>)
/// and this class's own boss-unique group builder are the SAME "generate the dungeon's drop tables"
/// responsibility, just two different groups within it — one shared static class, matching both
/// tasks' own identical Files line rather than a same-named sibling type.
///
/// <para><b>Deliberately narrow, and honestly so.</b> A dedicated research pass confirmed three real,
/// substantial gaps this task does NOT close, each named at its own call site below rather than
/// papered over: (1) no code anywhere resolves a unique's own theme to a domain's climate
/// (`UniqueDomainListing`, the spec's own named method for this, does not exist — `UniqueSeed` carries
/// no theme/climate field at all) — the climate match arrives here as a caller-supplied fact, the same
/// "read model owned elsewhere" shape this whole program already uses everywhere a real upstream
/// piece is missing. (2) No planner-JSON → `DropTableRow` band/weight resolver exists ANYWHERE in this
/// codebase — not even for the 40 already-shipped item-corpus tables, which still author raw
/// `dropBand` strings today — so the real weight for `dropBand: exceptional`/`staple`
/// (`gk-data/packs/fusion/data/seed/items/_registry/bands.v1.json:463-484`: 7 and 1000) is a caller-supplied `int`, never
/// re-derived or hardcoded here. (3) No real per-domain planner JSON exists on disk at all
/// (`gk-data/packs/fusion/data/seed/dungeon/domains/` is empty) — this file builds the GROUP from an in-memory candidate
/// list, provable today against a small hand-built input; running it over real domains is D4.30's own
/// separately-blocked job.</para>
/// </summary>
public static class DungeonLootTableGen
{
    static DungeonLootTableGen() => ContentRuleNamespaces.Register("dungeon");

    /// <summary>The four room kinds `lootBinding` binds a table for — `RoomTableBinding`'s own
    /// private `NoTableKinds` (spec-dungeon-loot.md §5's own table) seven-member complement, named
    /// publicly here since that file exposes no list of its own. `boss` binds the SAME fight-side
    /// table every other kind does; its separate `dungeon-clear` relic grant
    /// (`DelveLoot.InstantiateBossFirstClearGrant`, already shipped) is a different source kind
    /// entirely, untouched by this generator.</summary>
    public static readonly IReadOnlyList<string> BoundRoomKinds = new[] { "fight", "elite", "boss", "cache" };

    /// <summary>spec-dungeon-loot.md §5's own naming convention, verbatim: `drop.dungeon.<climate>.<kind>`.</summary>
    public static string TableId(string climate, string roomKind) => $"drop.dungeon.{climate}.{roomKind}";

    /// <summary>The entry-kind weight mix, caller-supplied — never hardcoded here, matching this
    /// program's own established `OutcomeResolver.WeightFor`/`PickOutcome` precedent (a plain,
    /// validated table the CALLER reads from tuning, this file never reads `dungeon.v1.json` itself
    /// so a magic-number audit never needs to special-case a Core/Delve/Loot literal).</summary>
    public readonly record struct Weights(int EquipmentWeight, int NothingWeight);

    /// <summary>
    /// One real, minimal, valid table per (climate, bound room kind) — <c>domain-catalog</c>'s own §2
    /// row 9 ("`lootBinding[kind]` names a `drop_table` row for every bound kind") needs these to
    /// exist before any real domain anchor can pass preflight. Deliberately structural, not a balance
    /// pass: rarity and volume are already composed OUTSIDE this table (`gk-core/data/tuning/dungeon.v1.json`'s
    /// own `loot.rooms.*` rung columns and `RarityShift`, D3.11/D3.14, already shipped), so every table
    /// here is the SAME shape (two `equipment` slots, one per <see cref="FusionRpg.Core.Items.ItemFrame"/>,
    /// plus a `nothing` remainder) — enough to be real and non-degenerate, not a curated loot list. An
    /// `equipment` entry must name both `Frame` and `Role` (`DropTableValidator.cs:219-223`) —
    /// `core-guard` picked as the always-populated placeholder role.
    ///
    /// <para>⚠ <b>Corrected 2026-09-07 (owner decision):</b> this doc comment used to claim `standard`
    /// (the commander's own singular banner/root-totem slot, `roles.commanderOnly`) can never
    /// structurally belong in a normal equipment drop table. That claim is WRONG in principle — the
    /// owner confirmed commander gear IS meant to be lootable, just at a properly RARE weight, with
    /// boss/treasure-type tables offering better odds than a normal room's table. The narrower reason
    /// `core-guard` still stays the placeholder HERE: every `role: "standard"` base-type entry that
    /// exists today (`gk-data/packs/fusion/data/seed/items/base-types/{plant,humanoid}-standard.json`) is authored
    /// `"enabled": false` — content-not-ready, not "can never exist" — so drawing one right now would
    /// resolve to nothing live. **Separately found while checking this:** neither real C# reader of
    /// this corpus (`BaseTypeSeedFile.cs`, `FusionRpg.Server.ItemBaseTypeCorpus.Load`) actually checks
    /// `enabled` at all — so those "disabled" rows may already be importing as live base types
    /// regardless, an unrelated, unfixed gap named for the owner, not fixed here. Once `standard`
    /// base types are genuinely enabled AND this generator's own flat, single-weight-per-slot shape
    /// grows real per-role weighting (today every equipment slot in this table shares one
    /// `EquipmentWeight` — there is no way to express "much rarer than a normal role" here yet),
    /// `standard` should be added back with a low, boss/treasure-favoring weight — not at parity with
    /// `core-guard`, which is what a naive revert of this fix would have produced.</para>
    /// </summary>
    public static IReadOnlyList<DropTableRow> Build(IReadOnlyList<string> climates, Weights weights)
    {
        if (climates is null) throw new ArgumentNullException(nameof(climates));
        if (climates.Count == 0) throw new ArgumentException("at least one climate is required", nameof(climates));
        if (weights.EquipmentWeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(weights),
                "equipment weight must be positive — a zero-or-negative weight leaves the table drawing only 'nothing', which is not a real table");
        if (weights.NothingWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(weights), "nothing weight cannot be negative");

        var tables = new List<DropTableRow>();
        foreach (var climate in climates)
        {
            if (string.IsNullOrWhiteSpace(climate))
                throw new ArgumentException("a climate id cannot be blank", nameof(climates));

            foreach (var kind in BoundRoomKinds)
            {
                var entries = new List<DropTableEntryRow>
                {
                    new(0, DropEntryKind.Equipment, "", weights.EquipmentWeight, Frame: "plant", Role: "core-guard"),
                    new(1, DropEntryKind.Equipment, "", weights.EquipmentWeight, Frame: "humanoid", Role: "core-guard"),
                    new(2, DropEntryKind.Nothing, "", weights.NothingWeight),
                };
                var group = new DropTableGroupRow($"{kind}-drop", 0, 1, entries);
                tables.Add(new DropTableRow(
                    TableId(climate, kind), new[] { DropTableValidator.WebSource },
                    MinIlvl: null, MaxIlvl: null, Enabled: true, Revision: 0, Groups: new[] { group }));
            }
        }
        return tables;
    }

    /// <summary>
    /// The exact JSON shape <see cref="FusionRpg.Core.Items.Drops.LootCorpusReader.Parse"/> reads
    /// back (`{"tables": [...], "sources": []}`) — this generator emits no `loot_source` rows of its
    /// own; a domain's boss `dungeon-clear` source is this file's own already-shipped concern above,
    /// and a room's `dungeon-room` source is resolved per-room at roll time (`RoomTableBinding.For`),
    /// never a static seed row. Field names and nesting verified against `LootCorpusReader.ReadTable`/
    /// `ReadGroup`/`ReadEntry` directly, not assumed, so a round-trip through `Parse` is byte-faithful.
    /// </summary>
    public static string ToJson(IReadOnlyList<DropTableRow> tables)
    {
        if (tables is null) throw new ArgumentNullException(nameof(tables));

        var tablesArray = new JsonArray();
        foreach (var t in tables)
        {
            var groupsArray = new JsonArray();
            foreach (var g in t.Groups)
            {
                var entriesArray = new JsonArray();
                foreach (var e in g.Entries)
                {
                    var entryNode = new JsonObject
                    {
                        ["seq"] = e.Seq,
                        ["entryKind"] = LootCorpusReader.KindName(e.Kind),
                        ["ref"] = e.RefId,
                        ["weight"] = e.Weight,
                        ["minCount"] = e.MinCount,
                        ["maxCount"] = e.MaxCount,
                        ["enabled"] = e.Enabled,
                        ["affixChannel"] = e.AffixChannel,
                    };
                    if (e.MinIlvl is { } lo) entryNode["minIlvl"] = lo;
                    if (e.MaxIlvl is { } hi) entryNode["maxIlvl"] = hi;
                    if (e.RarityFloor is { Length: > 0 } floor) entryNode["rarityFloor"] = floor;
                    if (e.Frame is { Length: > 0 } frame) entryNode["frame"] = frame;
                    if (e.Role is { Length: > 0 } role) entryNode["role"] = role;
                    entriesArray.Add(entryNode);
                }
                groupsArray.Add(new JsonObject
                {
                    ["groupKey"] = g.GroupKey,
                    ["seq"] = g.Seq,
                    ["rolls"] = g.Rolls,
                    ["entries"] = entriesArray,
                });
            }

            var allowArray = new JsonArray();
            foreach (var s in t.SourceAllow) allowArray.Add(s);

            var tableNode = new JsonObject
            {
                ["tableId"] = t.TableId,
                ["sourceAllow"] = allowArray,
                ["enabled"] = t.Enabled,
                ["revision"] = t.Revision,
                ["groups"] = groupsArray,
            };
            if (t.MinIlvl is { } tLo) tableNode["minIlvl"] = tLo;
            if (t.MaxIlvl is { } tHi) tableNode["maxIlvl"] = tHi;
            tablesArray.Add(tableNode);
        }

        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = "dungeon-drop-table-generated",
            ["tables"] = tablesArray,
            ["sources"] = new JsonArray(),
        };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            root.WriteTo(writer);
        // LF, never the writer's host newline (CanonicalEol): this text is committed and the repo checks
        // files out as LF, so a CRLF write is stale against its own fresh checkout.
        return System.Text.Encoding.UTF8.GetString(stream.ToArray()).ToLf();
    }

    /// <summary>One unique's own eligibility-relevant facts, resolved by the caller. `ClimateMatches`
    /// stands in for the unbuilt `UniqueDomainListing` (see class doc) — true when the theme's own
    /// `elementAffinity[]` contains the domain's climate or is empty (spec §5, verbatim).</summary>
    public readonly record struct BossUniqueCandidate(
        string SeedId, string ContainerId, int RungOrdinal, bool Enabled,
        UniqueAcquisition Acquisition, bool ClimateMatches);

    /// <summary>Spec §5, verbatim: "Eligible = rung ≥ 80, `enabled`, `acquisition ∈ {drop,
    /// source-locked}` (a `deterministic` unique never sits in a table) ... the theme's
    /// `elementAffinity[]` contains the domain's climate or is empty — a filter, never a weight." The
    /// "≥ 80" is `uniques.v1.json`'s own `rungFloorOrdinal` tunable (D4.23), read through
    /// <see cref="UniqueTuning.IsRungEligible"/> rather than a second, independently-hardcoded 80 —
    /// the exact "no second owner of a domain fact" defect this program repeatedly names elsewhere.</summary>
    public static bool IsEligible(BossUniqueCandidate c, UniqueTuning tuning) =>
        tuning.IsRungEligible(c.RungOrdinal)
        && c.Enabled
        && c.Acquisition is UniqueAcquisition.Drop or UniqueAcquisition.SourceLocked
        && c.ClimateMatches;

    /// <summary>
    /// Spec §5, verbatim: "one `{entryKind: unique, ref: item.&lt;slug&gt;, dropBand: exceptional}`
    /// per eligible unique beside `{nothing, staple}`." Refuses `unique.group-starved` (the spec's own
    /// named reason) rather than shipping a group that can only ever draw `nothing` — silently
    /// authoring a dead group is the exact failure this whole program's "refuse, never a default"
    /// posture exists to prevent.
    /// </summary>
    public static AtomRejection BuildBossUniqueGroup(
        IEnumerable<BossUniqueCandidate> candidates, UniqueTuning tuning, int exceptionalWeight, int stapleWeight,
        out IReadOnlyList<DropTableEntryRow>? entries)
    {
        entries = null;
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        UniqueRules.EnsureRegistered(); // "unique" namespace may not be registered yet if nothing else has touched UniqueRules first

        var eligible = candidates.Where(c => IsEligible(c, tuning)).ToList();
        if (eligible.Count == 0)
            return AtomRejection.ContentRule(UniqueRules.Namespace + ".group-starved",
                "no eligible unique for this domain's boss-unique group");

        var seq = 0;
        var rows = new List<DropTableEntryRow>(eligible.Count + 1);
        foreach (var c in eligible)
            rows.Add(new DropTableEntryRow(seq++, DropEntryKind.Unique, c.ContainerId, exceptionalWeight, AffixChannel: AffixChannels.Boss));
        rows.Add(new DropTableEntryRow(seq, DropEntryKind.Nothing, "", stapleWeight));

        entries = rows;
        return AtomRejection.Ok;
    }

    /// <summary>One `entryKind: unique` reference, naming the table it was placed in — the input
    /// <see cref="ValidateSourceLockedOnce"/> checks. A generator emits one of these per unique entry
    /// it writes into any table (boss-unique or otherwise), not just this file's own group.</summary>
    public readonly record struct SourceLockReference(string UniqueId, string TableId);

    /// <summary>One `source-locked` id referenced by more than one table, naming every table it
    /// appears in (so the fix is obvious, not just "somewhere is wrong").</summary>
    public readonly record struct SourceLockViolation(string UniqueId, IReadOnlyList<string> TableIds);

    /// <summary>
    /// "The lock lives in which table references the id" (ideal, verbatim) — a `source-locked` unique
    /// must be listed by EXACTLY one domain table. Confirmed by a dedicated research pass that this
    /// cardinality is enforced NOWHERE today: the Python `acquisition.py` module checks reachability
    /// (is this id obtainable at all), never a per-id reference count, and the C# corpus validator's
    /// own `ValidateDropReferences` explicitly skips every non-general-channel (i.e. source-locked)
    /// reference. This is the first real check of the actual rule, in either language.
    /// </summary>
    public static IReadOnlyList<SourceLockViolation> ValidateSourceLockedOnce(IEnumerable<SourceLockReference> references)
    {
        if (references is null) throw new ArgumentNullException(nameof(references));

        return references
            .GroupBy(r => r.UniqueId, StringComparer.Ordinal)
            .Select(g => new
            {
                g.Key,
                Tables = g.Select(r => r.TableId).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList(),
            })
            .Where(x => x.Tables.Count > 1)
            .Select(x => new SourceLockViolation(x.Key, x.Tables))
            .OrderBy(v => v.UniqueId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>One known unique's facts, keyed by container id — everything
    /// <see cref="ValidateFirstClearRef"/> needs to check a domain anchor's own claim against.</summary>
    public readonly record struct KnownUnique(int RungOrdinal, UniqueAcquisition Acquisition);

    /// <summary>
    /// Spec §5, verbatim: "the domain anchor gains `firstClearRef` (VALIDATED against rung-80+
    /// `deterministic` unique ids · `none` legal)." `null`/absent IS "none" — `DomainAnchor
    /// .FirstClearRef` is itself the nullable field, so there is no separate literal `"none"` string
    /// to compare against here.
    /// </summary>
    public static AtomRejection ValidateFirstClearRef(string? firstClearRef, IReadOnlyDictionary<string, KnownUnique> knownUniques, UniqueTuning tuning)
    {
        if (knownUniques is null) throw new ArgumentNullException(nameof(knownUniques));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (firstClearRef is null) return AtomRejection.Ok;

        UniqueRules.EnsureRegistered();

        if (!knownUniques.TryGetValue(firstClearRef, out var u))
            return AtomRejection.ContentRule("dungeon.first-clear-ref-unknown",
                $"firstClearRef '{firstClearRef}' does not name a known unique container");
        if (u.Acquisition != UniqueAcquisition.Deterministic)
            return AtomRejection.ContentRule("dungeon.first-clear-ref-not-deterministic",
                $"firstClearRef '{firstClearRef}' names a {u.Acquisition} unique, not deterministic");
        if (!tuning.IsRungEligible(u.RungOrdinal))
            return AtomRejection.ContentRule("dungeon.first-clear-ref-below-rung-floor",
                $"firstClearRef '{firstClearRef}' is rung {u.RungOrdinal}, below the floor of {tuning.RungFloorOrdinal}");

        return AtomRejection.Ok;
    }
}
