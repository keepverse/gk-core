namespace FusionRpg.Core.Delve.Domains;

/// <summary>
/// Every fact D4.17's own rows 2, 3, 9 and 10 need, PLUS the five sibling modules' own already-shipped
/// preflight functions (rows 4-8) taken as plain delegates. `delve-graph-roll` (`Roll`+validators),
/// `supplies-and-objects` (`ObjectPreflight.Run`), `encounter-generator` (`EncounterPreflight.Run`),
/// `event-deck` (`EventDeckPreflight.Run`) and `delve-quests` (`QuestPreflight`'s own three row/pool
/// checks, D4.13) each already exist with a REAL signature that does not match the spec's own
/// simplified pseudocode citation for `DomainPreflight.Run` (confirmed by reading each file directly)
/// — bridging a bare `DomainRow` into every one of those five real, differently-shaped signatures is
/// each ROW'S OWN adapter, correctly built only with the domain's real content on hand (`domain
/// -catalog`'s own D4.30, "run the six first-ship domains through the pipeline"), not guessed at here
/// without a single real domain to test the bridge against. Rows 1 (schema), 2 (refs), 3 (palette
/// coverage) and 9 (loot table existence) are self-contained enough to build directly; row 10
/// (`RungOffer.For`) needs `PowerTuning`/`DungeonTuning`/`DomainThetaInputs`/`ParentWorldTerms`/
/// `PlayerClears` — five real parameters, not the spec's own two — so it is delegated too.
/// </summary>
public sealed record DomainPreflightInputs(
    IReadOnlyDictionary<string, int> DangerBandOrdinals,
    IReadOnlyCollection<string> KnownLayoutIds,
    IReadOnlyCollection<string> KnownSpeciesIds,
    Func<string, int> ThreatBandOrdinalFor,
    int BossFloorRungOrdinal,
    Func<DomainRow, IReadOnlyList<(string Kind, string Climate)>> CellsLayoutCanPlace,
    Func<DomainRow, IReadOnlyList<(string Kind, string Climate)>> CellsPaletteFills,
    Func<DomainRow, IReadOnlyList<DomainRefusal>> CheckGraphs,
    Func<DomainRow, IReadOnlyList<DomainRefusal>> CheckObjects,
    Func<DomainRow, IReadOnlyList<DomainRefusal>> CheckEncounters,
    Func<DomainRow, IReadOnlyList<DomainRefusal>> CheckEvents,
    Func<DomainRow, IReadOnlyList<DomainRefusal>> CheckQuests,
    IReadOnlyCollection<string> KnownDropTableIds,
    IReadOnlyCollection<string> BoundLootKinds,
    Func<DomainRow, IReadOnlyDictionary<string, string>> LootBindingFor,
    Func<DomainRow, int> OfferedRungCountFor);

/// <summary>
/// D4.17 (spec-domain-catalog.md §2) — the ten-row preflight chain, model-free and store-free (every
/// fact arrives through <see cref="DomainPreflightInputs"/>, never a live read). Every row refuses
/// BEFORE any write — this function performs none itself, by construction (it returns a plain list of
/// refusals; nothing here opens a connection or a transaction) — and every domain is checked, not just
/// the first that fails, matching §2's own "every domain is checked."
/// </summary>
public static class DomainPreflight
{
    public static IReadOnlyList<DomainRefusal> Run(IReadOnlyList<DomainRow> domains, DomainPreflightInputs live)
    {
        if (domains is null) throw new ArgumentNullException(nameof(domains));
        if (live is null) throw new ArgumentNullException(nameof(live));

        var refusals = new List<DomainRefusal>();
        foreach (var d in domains.OrderBy(x => x.DomainId, StringComparer.Ordinal))
        {
            // Row 1: anchor shape, ids, vocabularies -- DomainCatalog.Load's own validation
            // (D4.15) IS this row; never a second copy of the same checks.
            var loaded = DomainCatalog.Load(new[] { d }, live.DangerBandOrdinals);
            if (loaded.Rejections.Count > 0)
            {
                refusals.AddRange(loaded.Rejections.Select(r => new DomainRefusal(d.DomainId, "domain.schema", r.Detail)));
                continue;
            }

            // Row 2: every pool ref exists; layout known; boss threatBand >= bossFloorRung.
            if (!live.KnownLayoutIds.Contains(d.LayoutTemplateId))
            {
                refusals.Add(new DomainRefusal(d.DomainId, "domain.ref-missing", $"layoutTemplateId '{d.LayoutTemplateId}'"));
                continue;
            }
            if (!live.KnownSpeciesIds.Contains(d.BossSpeciesRef))
            {
                refusals.Add(new DomainRefusal(d.DomainId, "domain.ref-missing", $"bossSpeciesRef '{d.BossSpeciesRef}'"));
                continue;
            }
            if (live.ThreatBandOrdinalFor(d.BossSpeciesRef) < live.BossFloorRungOrdinal)
            {
                refusals.Add(new DomainRefusal(d.DomainId, "domain.boss-below-floor",
                    $"bossSpeciesRef '{d.BossSpeciesRef}' threatBand below bossFloorRung"));
                continue;
            }

            // Row 3: every (kind, climate) cell the layout can place has >= 1 archetype in roomPalette.
            var demanded = live.CellsLayoutCanPlace(d);
            var filled = new HashSet<(string Kind, string Climate)>(live.CellsPaletteFills(d));
            var emptyCell = demanded.FirstOrDefault(c => !filled.Contains(c));
            if (emptyCell != default)
            {
                refusals.Add(new DomainRefusal(d.DomainId, "domain.palette-cell-empty", $"({emptyCell.Kind}, {emptyCell.Climate})"));
                continue;
            }

            // Rows 4-8: delegated to their own owning module's real preflight (see this file's own
            // doc comment for why the bridge is not built here).
            var delegated = live.CheckGraphs(d)
                .Concat(live.CheckObjects(d))
                .Concat(live.CheckEncounters(d))
                .Concat(live.CheckEvents(d))
                .Concat(live.CheckQuests(d))
                .ToList();
            if (delegated.Count > 0)
            {
                refusals.AddRange(delegated);
                continue;
            }

            // Row 9: lootBinding[kind] names a real drop_table row, for every bound kind.
            var binding = live.LootBindingFor(d);
            var missingKind = live.BoundLootKinds.FirstOrDefault(k => !binding.TryGetValue(k, out var tableId) || !live.KnownDropTableIds.Contains(tableId));
            if (missingKind is not null)
            {
                refusals.Add(new DomainRefusal(d.DomainId, "domain.loot-table-missing", missingKind));
                continue;
            }

            // Row 10: RungOffer.For(domain, noClears) offers >= 1 rung.
            if (live.OfferedRungCountFor(d) == 0)
                refusals.Add(new DomainRefusal(d.DomainId, "domain.no-rung-offered", d.DangerBand));
        }

        return refusals;
    }
}
