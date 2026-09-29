using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Materials;

namespace FusionRpg.Core.Items.Drops;

/// <summary>
/// What the importer resolves for the validator, so this file stays free of I/O and of any store.
/// Every delegate is optional: omitting one registers no check at all, matching
/// <see cref="ContainerValidator"/>'s own precedent, so a caller with only part of the catalog
/// loaded is not forced to fake the rest.
/// </summary>
/// <param name="TrophySlotExists">
/// species-gear-chain T30b — whether the injected trophy registry (species-gear-chain T34) carries a
/// (scope, slot) pair, i.e. whether a parametric trophy entry's own authored slot is inside
/// 1..perSpecies/perFamily. Wired to <c>Materials.MaterialCatalog.IsTrophySlotIssuable</c> in
/// production.
/// </param>
public sealed record DropContentLookups(
    Func<string, bool>? BaseTypeSetExists = null,
    Func<string, bool>? MaterialExists = null,
    Func<string, bool>? CurrencyExists = null,
    Func<string, bool>? ContainerExists = null,
    Func<int, bool>? RarityOrdinalExists = null,
    Func<string, bool>? RarityIdExists = null,
    Func<string, int, bool>? TrophySlotExists = null);

/// <summary>
/// Import-time judgement over a loot corpus (ssot-generation.md §6, re-coded per spec-drop-volume.md's
/// own success criterion: <b>no new member of the closed 33-code list</b>).
///
/// <para>I12 asked for eight new reason codes. Not one is minted. Shipped codes are reused where the
/// semantics already match (<see cref="AtomRejectionReason.BadParamValue"/>,
/// <see cref="AtomRejectionReason.UnsatisfiablePool"/>, <see cref="AtomRejectionReason.DuplicateSeq"/>),
/// and everything else is a namespaced <c>ContentRuleViolated{drop.*}</c> under §2b.1's one code —
/// verified 2026-09-04: <c>AtomRejection.cs</c> carries 33 codes plus <c>None</c>, and
/// <c>ContentRuleViolated</c> is the 34th and last by design.</para>
///
/// <para><b>All-or-nothing.</b> E14's policy: one bad row and nothing is imported. This returns the
/// FIRST rejection, with enough detail to fix the row.</para>
/// </summary>
public static class DropTableValidator
{
    static DropTableValidator() => ContentRuleNamespaces.Register("drop");

    /// <summary>Standalone-first, rule 2 (§4.6): a table the web client cannot reach is content that
    /// only exists inside the game process.</summary>
    public const string WebSource = "web";

    /// <summary>The three legal <c>source_allow</c> members.</summary>
    public static readonly IReadOnlyList<string> KnownSources = new[] { "web", "injector", "sim" };

    /// <summary>
    /// ⛔ §4.1: PvZ-run content level is explicitly UNDESIGNED — <c>mappedRunLevel</c> "was never
    /// implemented anywhere". §11 Q8 names two candidates (the player's own level, or a flat session
    /// level the PvZ side reports) and picks neither. A <c>pvz-run</c> loot source is therefore
    /// <b>refused by name</b>, never defaulted to 1. Whoever owns standalone-first PvZ drops decides.
    /// </summary>
    public const string UndesignedSourceKind = "pvz-run";

    // D3.10 (spec-dungeon-loot.md §1, :108-109): three real, designed kinds -- `dungeon-room` (fights,
    // caches, elites, the boss room), `dungeon-clear` (the boss's own first-clear grant, source id =
    // the domain), `dungeon-quest` (a completed quest's own reward). Each also gains a `LootCorrelation
    // .Derive` arm (`LootPipeline.cs`) -- neither list is complete without the other.
    // drop-tables `siege-loot` (2026-09-07): base-defense's first loot_source binding, its own
    // `LootCorrelation.Derive` arm added alongside this in `LootPipeline.cs`.
    // species-gear-chain T30 (`creature-drop-tables` E1): a creature kill. The source row is
    // synthesised at kill time by `CreatureKillLootSource` (per-kill source id), never authored --
    // a static row per kill is un-authorable the same way a static row per sector was -- and its
    // `LootCorrelation.Derive` arm lands in the same commit, per this list's own rule above.
    public static readonly IReadOnlyList<string> KnownSourceKinds =
        new[] { "web-wave", "expedition-tier", "world-sector", UndesignedSourceKind, "dungeon-room", "dungeon-clear", "dungeon-quest", "siege-assault", CreatureKillLootSource.SourceKind };

    public static AtomRejection Validate(
        IReadOnlyList<LootSourceRow> sources,
        IReadOnlyList<DropTableRow> tables,
        DropVolumeTuning tuning,
        DropContentLookups? lookups = null,
        DropRateFloorTuning? rateFloorTuning = null)
    {
        if (sources is null) throw new ArgumentNullException(nameof(sources));
        if (tables is null) throw new ArgumentNullException(nameof(tables));
        lookups ??= new DropContentLookups();

        var byId = new Dictionary<string, DropTableRow>(StringComparer.Ordinal);
        foreach (var t in tables)
        {
            if (!byId.TryAdd(t.TableId, t))
                return AtomRejection.ContentRule("drop.duplicate-table", $"table_id '{t.TableId}' appears twice");
        }

        foreach (var t in tables)
        {
            var r = ValidateTable(t, byId, tuning, lookups);
            if (!r.IsOk) return r;

            // rate-floor (drop-tables module 1, D2): opt-in via the same "null skips this specific
            // check" convention `lookups`'s own individual delegates already use in this file --
            // existing callers that have not yet loaded the tunable are unaffected, byte-identical.
            if (rateFloorTuning is { } floorTuning)
                foreach (var g in t.Groups)
                    foreach (var floorResult in DropRateFloor.ValidateGroupAcrossBreakpoints(g.Entries, floorTuning))
                        if (!floorResult.IsOk) return floorResult;
        }

        foreach (var s in sources)
        {
            var r = ValidateSource(s, byId, lookups);
            if (!r.IsOk) return r;
        }

        var assuranceReach = ValidateAssuranceBossOnly(tables, byId);
        if (!assuranceReach.IsOk) return assuranceReach;

        return ValidateStandaloneContainment(sources, byId);
    }

    /// <summary>
    /// craft-assurance T45, the reachability half. <see cref="ValidateGroup"/> already refuses an
    /// off-channel <c>assurance.*</c> entry where it is authored; this closes the loophole where the
    /// entry itself is correctly boss-channel but a table nesting it is reached through a
    /// <c>drop</c>-channel <see cref="DropEntryKind.Table"/> entry one or more levels up — "boss-only"
    /// must hold for <b>every</b> step of the path, not just the leaf. Cycle-safe by construction: by
    /// the time this runs, every table in <paramref name="tables"/> has already passed
    /// <see cref="ValidateNesting"/> as its own root, so the whole graph is acyclic and a memoized
    /// depth-first walk terminates.
    /// </summary>
    static AtomRejection ValidateAssuranceBossOnly(
        IReadOnlyList<DropTableRow> tables, IReadOnlyDictionary<string, DropTableRow> byId)
    {
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);

        bool ReachesAssurance(DropTableRow t)
        {
            if (memo.TryGetValue(t.TableId, out var cached)) return cached;
            memo[t.TableId] = false; // acyclic per the doc comment above; this is only a re-entry guard
            var found = false;
            foreach (var g in t.Groups)
            foreach (var e in g.Entries)
            {
                if (e.Kind == DropEntryKind.Material && MaterialCatalog.IsAssuranceId(e.RefId)) { found = true; break; }
                if (e.Kind == DropEntryKind.Table && byId.TryGetValue(e.RefId, out var nested) && ReachesAssurance(nested)) { found = true; break; }
            }
            memo[t.TableId] = found;
            return found;
        }

        foreach (var t in tables)
        foreach (var g in t.Groups)
        foreach (var e in g.Entries)
        {
            if (e.Kind != DropEntryKind.Table) continue;
            if (e.AffixChannel == AffixChannels.Boss) continue;
            if (!byId.TryGetValue(e.RefId, out var nested)) continue; // drop.unknown-drop-table already covers this

            if (ReachesAssurance(nested))
                return AtomRejection.ContentRule("drop.assurance-non-boss",
                    $"table '{t.TableId}' group '{g.GroupKey}' seq {e.Seq} nests '{nested.TableId}' through a "
                    + $"'{e.AffixChannel}'-channel table entry, and '{nested.TableId}' (directly or through "
                    + "further nesting) carries an assurance.* material entry — every table entry on the path "
                    + $"to it must itself be '{AffixChannels.Boss}'-channel (R10 — boss-only sourcing)");
        }

        return AtomRejection.Ok;
    }

    static AtomRejection ValidateSource(
        LootSourceRow s, IReadOnlyDictionary<string, DropTableRow> byId, DropContentLookups lookups)
    {
        if (string.IsNullOrWhiteSpace(s.SourceKind) || string.IsNullOrWhiteSpace(s.SourceId))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue, "a loot_source needs both a kind and an id");

        if (!KnownSourceKinds.Contains(s.SourceKind, StringComparer.Ordinal))
            return AtomRejection.ContentRule("drop.unknown-source-kind",
                $"'{s.SourceKind}' is not one of {string.Join(", ", KnownSourceKinds)}");

        if (string.Equals(s.SourceKind, UndesignedSourceKind, StringComparison.Ordinal))
            return AtomRejection.ContentRule("drop.source-kind-undesigned",
                $"loot_source '{s.Key}' is of kind '{UndesignedSourceKind}', and no contentLevel source exists for it "
                + "(ssot-generation.md §4.1: mappedRunLevel was never implemented; §11 Q8 names two candidates — the "
                + "player's own level, or a flat session level the PvZ side reports — and picks neither). Refused by "
                + "name rather than defaulted to 1; whoever owns standalone-first PvZ drops decides");

        if (!byId.TryGetValue(s.TableId, out var table))
            return AtomRejection.ContentRule("drop.unknown-drop-table",
                $"loot_source '{s.Key}' points at table '{s.TableId}', which no table declares");

        if (s.ContentLevel < 1)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"loot_source '{s.Key}' has content_level {s.ContentLevel}; item level is content, and content starts at 1");

        if (table.MinIlvl is { } lo && s.ContentLevel + 1 < lo)
            return AtomRejection.ContentRule("drop.ilvl-band-excludes",
                $"loot_source '{s.Key}' at content_level {s.ContentLevel} can never reach table '{table.TableId}'"
                + $"'s min_ilvl {lo}, even with +1 jitter");
        if (table.MaxIlvl is { } hi && s.ContentLevel - 1 > hi)
            return AtomRejection.ContentRule("drop.ilvl-band-excludes",
                $"loot_source '{s.Key}' at content_level {s.ContentLevel} is above table '{table.TableId}'"
                + $"'s max_ilvl {hi}, even with −1 jitter");

        if (s.FirstClearGrant is { Length: > 0 } grant
            && lookups.ContainerExists is { } exists && !exists(grant))
            return AtomRejection.Fail(AtomRejectionReason.UnknownContainer,
                $"loot_source '{s.Key}' grants '{grant}' on first clear, and no container carries that id");

        return AtomRejection.Ok;
    }

    static AtomRejection ValidateTable(
        DropTableRow t, IReadOnlyDictionary<string, DropTableRow> byId,
        DropVolumeTuning tuning, DropContentLookups lookups)
    {
        if (string.IsNullOrWhiteSpace(t.TableId))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue, "a drop_table needs a table_id");

        foreach (var src in t.SourceAllow)
            if (!KnownSources.Contains(src, StringComparer.Ordinal))
                return AtomRejection.ContentRule("drop.unknown-source",
                    $"table '{t.TableId}' allows source '{src}', which is not one of {string.Join(", ", KnownSources)}");

        // §4.6 rule 2 — standalone-first, enforced in the table and not in a promise.
        if (!t.SourceAllow.Contains(WebSource, StringComparer.Ordinal))
            return AtomRejection.ContentRule("drop.standalone-rule-violation",
                $"table '{t.TableId}' declares source_allow [{string.Join(", ", t.SourceAllow)}] and omits "
                + "'web'; every table must be reachable from the standalone client");

        if (t.MinIlvl is { } lo && t.MaxIlvl is { } hi && lo > hi)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"table '{t.TableId}' has an inverted ilvl band [{lo}, {hi}]");

        if (t.Groups.Count == 0)
            return AtomRejection.Fail(AtomRejectionReason.UnsatisfiablePool,
                $"table '{t.TableId}' declares no groups — an empty table reuses UnsatisfiablePool "
                + "because the semantics are identical, rather than minting a code for it");

        var groupKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in t.Groups)
        {
            if (!groupKeys.Add(g.GroupKey))
                return AtomRejection.ContentRule("drop.duplicate-group",
                    $"table '{t.TableId}' declares group '{g.GroupKey}' twice");

            if (g.Rolls < 0)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"table '{t.TableId}' group '{g.GroupKey}' has rolls {g.Rolls}; a draw count cannot be negative");

            var r = ValidateGroup(t, g, lookups);
            if (!r.IsOk) return r;
        }

        return ValidateNesting(t, byId, tuning.MaxNestingDepth);
    }

    static AtomRejection ValidateGroup(DropTableRow t, DropTableGroupRow g, DropContentLookups lookups)
    {
        var seqs = new HashSet<int>();
        long total = 0;

        foreach (var e in g.Entries)
        {
            var where = $"table '{t.TableId}' group '{g.GroupKey}' seq {e.Seq}";

            if (!seqs.Add(e.Seq))
                return AtomRejection.Fail(AtomRejectionReason.DuplicateSeq, $"{where}: seq appears twice");

            if (!Enum.IsDefined(typeof(DropEntryKind), e.Kind))
                return AtomRejection.ContentRule("drop.unknown-entry-kind", $"{where}: entry kind {e.Kind} is not one of the ten");

            // ⛔ Refused BY NAME, never silently dropped and never quietly resolved to nothing.
            if (!DropTableDraw.IsAvailable(e.Kind))
                return AtomRejection.ContentRule("drop.entry-kind-unavailable",
                    $"{where}: entry kind '{e.Kind.ToString().ToLowerInvariant()}' has no payload machinery yet — "
                    + DropTableDraw.UnavailableKinds[e.Kind]);

            if (e.Weight < 0)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"{where}: weight {e.Weight} is negative — rejected, never clamped, the same as effect_container_pool");

            if (e.MinCount > e.MaxCount || e.MinCount < 0)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"{where}: count range [{e.MinCount}, {e.MaxCount}] is inverted or negative");

            if (e.MinIlvl is { } lo && e.MaxIlvl is { } hi && lo > hi)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"{where}: ilvl band [{lo}, {hi}] is inverted");

            if (!AffixChannels.IsKnown(e.AffixChannel))
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"{where}: affix_channel '{e.AffixChannel}' is not one of {{{string.Join(", ", AffixChannels.All)}}}");

            // craft-assurance T45 — R10's own consumables are boss-only sourcing by design (spec
            // §craft-assurance): an `assurance.*` material entry not on the boss channel is refused
            // by name, reusing the existing `drop.*` namespace rather than minting a code. This is
            // the entry-level half of the rule; `ValidateAssuranceBossOnly` below is the other half —
            // every `Table` entry on the path down to a nested assurance entry must ALSO be boss.
            if (e.Kind == DropEntryKind.Material && MaterialCatalog.IsAssuranceId(e.RefId) && e.AffixChannel != AffixChannels.Boss)
                return AtomRejection.ContentRule("drop.assurance-non-boss",
                    $"{where}: assurance material '{e.RefId}' only ever drops on the '{AffixChannels.Boss}' "
                    + $"channel, not '{e.AffixChannel}' (R10 — boss-only sourcing)");

            // species-gear-chain T30b (`creature-drop-tables` d) — the parametric trophy directive:
            // a scope + slot INSTEAD of a literal ref_id (spec-creature-drop-tables.md Open question
            // 3). Checked before the ordinary ref-based rules below so those can skip a trophy row
            // (its ref_id is legitimately empty — never an overloaded ref_id grammar, SOLID L).
            var hasTrophyScope = !string.IsNullOrEmpty(e.TrophyScope);
            var hasTrophySlot = e.TrophySlot is not null;

            if (hasTrophyScope != hasTrophySlot)
                return AtomRejection.ContentRule("drop.trophy-scope-slot-mismatch",
                    $"{where}: trophy_scope and trophy_slot must both be set or both absent");

            if (hasTrophyScope)
            {
                if (!string.IsNullOrWhiteSpace(e.RefId))
                    return AtomRejection.ContentRule("drop.trophy-and-ref-conflict",
                        $"{where}: a trophy-scoped entry may not also carry ref_id '{e.RefId}' — " +
                        "scope+slot and ref_id are never both authored (SOLID L: not an overloaded ref_id grammar)");

                if (e.Kind != DropEntryKind.Material)
                    return AtomRejection.ContentRule("drop.trophy-requires-material-kind",
                        $"{where}: a trophy scope+slot is only legal on a 'material' entry, not '{e.Kind.ToString().ToLowerInvariant()}'");

                if (e.TrophyScope != "species" && e.TrophyScope != "family")
                    return AtomRejection.ContentRule("drop.trophy-scope-unknown",
                        $"{where}: trophy_scope '{e.TrophyScope}' is not one of species/family (R9 removed the general layer)");

                if (e.TrophySlot is not { } slot || slot < 1)
                    return AtomRejection.ContentRule("drop.trophy-slot-invalid",
                        $"{where}: trophy_slot must be a positive integer, got {e.TrophySlot}");

                if (lookups.TrophySlotExists is { } slotExists && !slotExists(e.TrophyScope!, slot))
                    return AtomRejection.ContentRule("drop.trophy-slot-unknown",
                        $"{where}: no {e.TrophyScope} in the injected trophy registry carries slot {slot}");
            }

            if (DropTableDraw.NeedsRef(e.Kind) && !hasTrophyScope && string.IsNullOrWhiteSpace(e.RefId))
                return AtomRejection.ContentRule("drop.missing-ref",
                    $"{where}: a '{e.Kind.ToString().ToLowerInvariant()}' entry names one specific thing and carries no ref");

            if (e.Kind == DropEntryKind.Equipment)
            {
                if (string.IsNullOrWhiteSpace(e.Frame) || string.IsNullOrWhiteSpace(e.Role))
                    return AtomRejection.ContentRule("drop.equipment-slot-missing",
                        $"{where}: an 'equipment' entry grants a whole role and frame, so it must name both");

                if (e.RefId.Length > 0 && lookups.BaseTypeSetExists is { } setExists && !setExists(e.RefId))
                    return AtomRejection.ContentRule("drop.unknown-base-type-set",
                        $"{where}: ref '{e.RefId}' names no base-type set");
            }

            if (e.Kind == DropEntryKind.Material && !hasTrophyScope && lookups.MaterialExists is { } matExists && !matExists(e.RefId))
                return AtomRejection.ContentRule("drop.unknown-material", $"{where}: ref '{e.RefId}' names no material");

            if (e.Kind == DropEntryKind.Currency && lookups.CurrencyExists is { } curExists && !curExists(e.RefId))
                return AtomRejection.ContentRule("drop.unknown-currency", $"{where}: ref '{e.RefId}' names no currency");

            if (e.RarityFloor is { Length: > 0 } floor)
            {
                if (!RarityLadder.RungIds.Contains(floor, StringComparer.Ordinal))
                    return AtomRejection.ContentRule("rarity.unknown",
                        $"{where}: rarity_floor '{floor}' is not one of the ten rungs");
                if (lookups.RarityIdExists is { } idExists && !idExists(floor))
                    return AtomRejection.ContentRule("rarity.unknown",
                        $"{where}: rarity_floor '{floor}' is not seeded in the rarity table");
            }

            if (e.RarityWeightShift is { Count: > 0 } shift)
            {
                foreach (var ordinal in shift.Keys)
                    if (lookups.RarityOrdinalExists is { } ordExists && !ordExists(ordinal))
                        return AtomRejection.ContentRule("rarity.unknown",
                            $"{where}: rarity_weight_shift_json names ordinal {ordinal}, which no rung carries");
            }

            total = checked(total + (e.Enabled ? e.Weight : 0));
        }

        if (g.Entries.Count == 0 || total <= 0)
            return AtomRejection.Fail(AtomRejectionReason.UnsatisfiablePool,
                $"table '{t.TableId}' group '{g.GroupKey}' has no drawable entry — every row is disabled or weight 0");

        return AtomRejection.Ok;
    }

    /// <summary>
    /// Depth and cycle, kept as two separate rules for the same reason definitions §10 keeps
    /// <c>UnknownTrigger</c> and <c>TriggerNotAllowed</c> apart — they are different author mistakes.
    /// </summary>
    static AtomRejection ValidateNesting(
        DropTableRow root, IReadOnlyDictionary<string, DropTableRow> byId, int maxDepth)
    {
        var onPath = new HashSet<string>(StringComparer.Ordinal);
        return Walk(root, 0);

        AtomRejection Walk(DropTableRow t, int depth)
        {
            if (depth > maxDepth)
                return AtomRejection.ContentRule("drop.depth-exceeded",
                    $"table '{root.TableId}' nests past depth {maxDepth} at '{t.TableId}' — nesting is for reuse, not for depth");

            if (!onPath.Add(t.TableId))
                return AtomRejection.ContentRule("drop.cycle",
                    $"table '{root.TableId}' reaches itself through '{t.TableId}'");

            foreach (var g in t.Groups)
                foreach (var e in g.Entries)
                {
                    if (e.Kind != DropEntryKind.Table) continue;
                    if (!byId.TryGetValue(e.RefId, out var nested))
                        return AtomRejection.ContentRule("drop.unknown-drop-table",
                            $"table '{t.TableId}' group '{g.GroupKey}' seq {e.Seq} nests '{e.RefId}', which no table declares");

                    var r = Walk(nested, depth + 1);
                    if (!r.IsOk) return r;
                }

            onPath.Remove(t.TableId);
            return AtomRejection.Ok;
        }
    }

    /// <summary>
    /// §4.6 rules 3 and 4 — the strongest readable form of "PvZ must never be the best source of
    /// anything web mode also provides", and it is cheap: two reachability sets and a subset test.
    ///
    /// <para>Rule 3: every entry reachable from a PvZ source must also be reachable from a web source.
    /// Rule 4: "boosted earn" is a legal extension role for currency and materials, <b>never</b> for
    /// equipment drop rate or rarity weights — a PvZ-reachable equipment entry carrying a
    /// <c>rarity_weight_shift_json</c> is refused.</para>
    /// </summary>
    static AtomRejection ValidateStandaloneContainment(
        IReadOnlyList<LootSourceRow> sources, IReadOnlyDictionary<string, DropTableRow> byId)
    {
        var webEntries = new HashSet<string>(StringComparer.Ordinal);
        var pvzEntries = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in sources)
        {
            if (!byId.TryGetValue(s.TableId, out var table)) continue;
            var isPvz = string.Equals(s.SourceKind, UndesignedSourceKind, StringComparison.Ordinal);
            Collect(table, isPvz ? pvzEntries : webEntries, new HashSet<string>(StringComparer.Ordinal));
        }

        foreach (var key in pvzEntries)
            if (!webEntries.Contains(key))
                return AtomRejection.ContentRule("drop.standalone-rule-violation",
                    $"entry '{key}' is reachable from a PvZ source and from no web source");

        foreach (var s in sources)
        {
            if (!string.Equals(s.SourceKind, UndesignedSourceKind, StringComparison.Ordinal)) continue;
            if (!byId.TryGetValue(s.TableId, out var table)) continue;

            foreach (var g in table.Groups)
                foreach (var e in g.Entries)
                    if (e.Kind == DropEntryKind.Equipment && e.RarityWeightShift is { Count: > 0 })
                        return AtomRejection.ContentRule("drop.standalone-rule-violation",
                            $"table '{table.TableId}' group '{g.GroupKey}' seq {e.Seq} is PvZ-reachable and carries an "
                            + "equipment rarity weight shift; boosted earn applies to currency and materials, never to "
                            + "equipment drop rate or rarity — an equipment bonus compounds into permanent build power");
        }

        return AtomRejection.Ok;

        void Collect(DropTableRow t, HashSet<string> into, HashSet<string> seen)
        {
            if (!seen.Add(t.TableId)) return;
            foreach (var g in t.Groups)
                foreach (var e in g.Entries)
                {
                    into.Add($"{t.TableId}|{g.GroupKey}|{e.Seq}");
                    if (e.Kind == DropEntryKind.Table && byId.TryGetValue(e.RefId, out var nested))
                        Collect(nested, into, seen);
                }
        }
    }
}
