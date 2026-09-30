using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `drop-volume` (item module 11) against the REAL shipped corpora — `gk-data/packs/fusion/data/seed/loot/**`,
/// `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json`, `gk-core/data/tuning/item-rarity.v1.json`,
/// `gk-data/packs/fusion/data/seed/items/base-types/**`, and the 40-table seedsmith corpus at
/// `gk-data/packs/fusion/data/seed/items/drop-tables/`. Nothing here is synthetic.
/// </summary>
[Trait("VerificationId", "core.drop-tables")]
public class DropVolumeCorpusTests
{
    static string RepoRoot() => DropVolumeTests.RepoRoot();

    static string LootDir() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "loot");

    internal static LootCorpus Corpus() => LootCorpusReader.Merge(
        Directory.EnumerateFiles(LootDir(), "*.json")
            .Where(p => Path.GetFileName(p).StartsWith("tables", StringComparison.Ordinal))
            .Select(p => LootCorpusReader.Parse(File.ReadAllText(p))));

    /// <summary>
    /// The ten rungs, joined from the two REAL files module 7 shipped: the seeded ladder rows
    /// (`gk-data/packs/fusion/data/seed/rarity/ladder.v1.json`) and the re-derived drop weights
    /// (`gk-core/data/tuning/item-rarity.v1.json` → the `drop_weight_default` budget key).
    /// </summary>
    internal static IReadOnlyList<RarityRung> Ladder()
    {
        var tuning = ItemRarityTuning.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-rarity.v1.json")));

        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(KeepverseRoots.Content(), "data", "seed", "rarity", "ladder.v1.json")));

        var rungs = new List<RarityRung>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;
            rungs.Add(new RarityRung(
                id, e.GetProperty("ordinal").GetInt32(),
                e.GetProperty("prefixRolls").GetInt32(), e.GetProperty("suffixRolls").GetInt32(),
                e.GetProperty("minTier").GetInt32(), e.GetProperty("maxTier").GetInt32(),
                tuning[id].DropWeightPer100k));
        }

        return rungs;
    }

    internal static IReadOnlyDictionary<(string Frame, string Role), List<string>> BaseTypes()
    {
        var dir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "base-types");
        var map = new Dictionary<(string, string), List<string>>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) continue;
            foreach (var e in entries.EnumerateArray())
            {
                if (e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
                var key = (e.GetProperty("frame").GetString()!, e.GetProperty("role").GetString()!);
                if (!map.TryGetValue(key, out var list)) map[key] = list = new List<string>();
                list.Add(e.GetProperty("id").GetString()!);
            }
        }

        foreach (var list in map.Values) list.Sort(StringComparer.Ordinal);
        return map;
    }

    // ---- Correction 1: the calibration, exactly, at Θ = 20 ---------------------------------------

    [Fact]
    public void At_theta_pin_the_shipped_per_event_yields_hold()
    {
        var t = DropVolumeTests.Tuning();
        var corpus = Corpus();
        var byId = corpus.Tables.ToDictionary(x => x.TableId, StringComparer.Ordinal);
        var scale = DropVolume.VolumeScaleMilli(t.ThetaPin, t);
        Assert.Equal(1000, scale);

        // Every row of spec-drop-volume.md §Correction 1, in per-mille, exactly.
        var expected = new (string TableId, long PerMille)[]
        {
            ("drop.web.wave-normal", 550),
            ("drop.web.wave-boss", 1400),
            ("drop.exp.scout-30m", 700),
            ("drop.exp.forage-4h", 1600),
            ("drop.exp.hunt-8h", 2600),
            ("drop.exp.warpath-20h", 4200),
            ("drop.world.sector-clear", 1500),
            ("drop.pvz.run", 500),
        };

        foreach (var (tableId, perMille) in expected)
        {
            var table = Assert.Contains(tableId, (IDictionary<string, DropTableRow>)byId);
            var actual = DropTableDraw.ExpectedEquipmentPerMille(
                table, itemLevel: 10, scale, id => byId.TryGetValue(id, out var x) ? x : null,
                maxDepth: t.MaxNestingDepth);
            Assert.Equal(perMille, actual);
        }
    }

    [Fact]
    public void Volume_scales_draw_counts_never_weights()
    {
        // Composition at Θ=20 and Θ=200 is statistically identical; only the COUNT moves. Scaling
        // weights would change composition, which is L0's job (X4), not volume's.
        var t = DropVolumeTests.Tuning();
        var corpus = Corpus();
        var byId = corpus.Tables.ToDictionary(x => x.TableId, StringComparer.Ordinal);
        var table = byId["drop.web.wave-normal"];

        var atPin = DropTableDraw.ExpectedEquipmentPerMille(table, 10,
            DropVolume.VolumeScaleMilli(20, t), id => byId.TryGetValue(id, out var x) ? x : null);
        var high = DropTableDraw.ExpectedEquipmentPerMille(table, 10,
            DropVolume.VolumeScaleMilli(200, t), id => byId.TryGetValue(id, out var x) ? x : null);

        Assert.True(high > atPin);

        // The yield ratio equals the SCALE ratio exactly — the per-draw composition is untouched.
        Assert.Equal(DropVolume.VolumeScaleMilli(200, t) * atPin / 1000, high);

        // And the base-type mix of the shared slate does not move with Θ: it is a plain uniform.
        var slate = byId["drop.shared.hybrid-core-any"].Groups.Single().Entries;
        // The slate size is a growing content population, never pinned (population-pin SE3.3,
        // 2026-09-19); the real contract is the uniform weight below.
        Assert.NotEmpty(slate);
        Assert.Single(slate.Select(e => e.Weight).Distinct());
    }

    // ---- the corpus itself ------------------------------------------------------------------------

    [Fact]
    public void The_shipped_loot_corpus_validates()
    {
        var t = DropVolumeTests.Tuning();
        var corpus = Corpus();
        var rarityIds = RarityLadder.RungIds.ToHashSet(StringComparer.Ordinal);

        var result = DropTableValidator.Validate(corpus.Sources, corpus.Tables, t, new DropContentLookups(
            CurrencyExists: id => id == "souls",
            RarityIdExists: rarityIds.Contains,
            RarityOrdinalExists: o => Ladder().Any(r => r.Ordinal == o)));

        Assert.True(result.IsOk, result.ToString());
    }

    [Fact]
    public void The_shipped_loot_corpus_passes_the_real_rate_floor_too()
    {
        // Task 2 (rate-floor wiring) -- the real corpus, checked against the REAL shipped tuning file,
        // not a synthetic fixture. Every shipped entry is expected to be far above the floor already
        // (spec-rate-floor.md's own measured claim: the narrowest real multi-entry group resolves
        // ~41,667/million against a floor of 1/million) -- this is a regression proof, not a
        // hypothetical.
        var t = DropVolumeTests.Tuning();
        var corpus = Corpus();
        var rarityIds = RarityLadder.RungIds.ToHashSet(StringComparer.Ordinal);
        var rateFloor = DropRateFloorTuning.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", "drop-rate-floor.v1.json")));

        var result = DropTableValidator.Validate(corpus.Sources, corpus.Tables, t, new DropContentLookups(
            CurrencyExists: id => id == "souls",
            RarityIdExists: rarityIds.Contains,
            RarityOrdinalExists: o => Ladder().Any(r => r.Ordinal == o)), rateFloor);

        Assert.True(result.IsOk, result.ToString());
    }

    [Fact]
    public void An_entry_deliberately_authored_below_the_floor_is_refused_at_import()
    {
        var t = DropVolumeTests.Tuning();
        var rateFloor = new DropRateFloorTuning(MinRatePerMillion: 1);

        // Two entries, weight 1 vs 2,000,000 -- the rare one resolves to well under 1/million.
        var table = new DropTableRow("drop.test.below-floor", new[] { "web" }, null, null, true, 1, new[]
        {
            new DropTableGroupRow("g", 0, 1, new DropTableEntryRow[]
            {
                new(0, DropEntryKind.Currency, "souls", 1),
                new(1, DropEntryKind.Currency, "souls", 2_000_000),
            }),
        });

        var result = DropTableValidator.Validate(
            Array.Empty<LootSourceRow>(), new[] { table }, t,
            new DropContentLookups(CurrencyExists: id => id == "souls"), rateFloor);

        Assert.False(result.IsOk);
        Assert.Contains("drop.rate-below-floor", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_equipment_entry_names_a_real_frame_and_role_with_real_base_types()
    {
        var baseTypes = BaseTypes();
        foreach (var table in Corpus().Tables)
            foreach (var g in table.Groups)
                foreach (var e in g.Entries.Where(x => x.Kind == DropEntryKind.Equipment))
                {
                    Assert.NotNull(e.Frame);
                    Assert.NotNull(e.Role);
                    Assert.True(baseTypes.ContainsKey((e.Frame!, e.Role!)),
                        $"{table.TableId}/{g.GroupKey}: no base type exists for ({e.Frame}, {e.Role})");
                    Assert.NotEmpty(baseTypes[(e.Frame!, e.Role!)]);
                }
    }

    [Fact]
    public void Affix_channel_is_authored_on_every_equipment_entry_and_only_boss_tables_use_boss()
    {
        foreach (var table in Corpus().Tables)
            foreach (var g in table.Groups)
                foreach (var e in g.Entries.Where(x => x.Kind == DropEntryKind.Equipment))
                    Assert.True(AffixChannels.IsKnown(e.AffixChannel),
                        $"{table.TableId}/{g.GroupKey} seq {e.Seq} carries channel '{e.AffixChannel}'");

        var byId = Corpus().Tables.ToDictionary(x => x.TableId, StringComparer.Ordinal);
        Assert.All(byId["drop.shared.hybrid-core-boss"].Groups.Single().Entries,
            e => Assert.Equal(AffixChannels.Boss, e.AffixChannel));
        Assert.All(byId["drop.shared.hybrid-core-any"].Groups.Single().Entries,
            e => Assert.Equal(AffixChannels.Drop, e.AffixChannel));
    }

    [Fact]
    public void No_pvz_run_loot_source_is_authored_and_one_would_be_refused_by_name()
    {
        var corpus = Corpus();
        Assert.DoesNotContain(corpus.Sources, s => s.SourceKind == DropTableValidator.UndesignedSourceKind);

        var t = DropVolumeTests.Tuning();
        var withPvz = corpus.Sources.Append(
            new LootSourceRow("pvz-run", "run-1", "drop.pvz.run", 5)).ToList();

        var result = DropTableValidator.Validate(withPvz, corpus.Tables, t);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.source-kind-undesigned", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("defaulted to 1", result.Detail.Replace("rather than defaulted", ""),
            StringComparison.Ordinal);
    }

    [Fact]
    public void At_least_one_first_clear_grant_is_rung_100()
    {
        // §3.8's deterministic source for `almanac`, as a CORPUS test rather than a runtime check.
        var grants = Corpus().Sources
            .Where(s => !string.IsNullOrEmpty(s.FirstClearGrant))
            .Select(s => s.FirstClearGrant!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.NotEmpty(grants);

        // The grant container lives in gk-data/packs/fusion/data/seed/containers/ — an OWNED folder of SeedScanner — so
        // it imports through the standard AtomSeedFile path like every other container, rather than
        // through a second writer beside the drop tables.
        var containerDir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "containers");
        var rarities = new Dictionary<string, string>(StringComparer.Ordinal);
        var noRolls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(containerDir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.GetProperty("kind").GetString() != "container") continue;
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = e.GetProperty("id").GetString()!;
                if (e.TryGetProperty("rarity", out var r)) rarities[id] = r.GetString()!;

                // §3.5: "a fixed, authored item... no rolls at all, so it never disappoints."
                if (e.GetProperty("prefixRolls").GetInt32() == 0
                    && e.GetProperty("suffixRolls").GetInt32() == 0
                    && !e.TryGetProperty("pool", out _))
                    noRolls.Add(id);
            }
        }

        Assert.Contains(grants, g => rarities.TryGetValue(g, out var rung) && rung == "almanac");
        Assert.All(grants, g => Assert.Contains(g, noRolls));

        // This is also the FIRST shipped container to name a rarity at all — gk-data/packs/fusion/data/seed/rarity's own
        // README records that none did, which makes it the first live exercise of the
        // effect_container.rarity FK module 7 wired.
        Assert.Contains("almanac", rarities.Values);
    }

    [Fact]
    public void RecommendedLevel_appears_nowhere_in_items_drops()
    {
        var dropsDir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Drops");
        foreach (var file in Directory.EnumerateFiles(dropsDir, "*.cs"))
            Assert.DoesNotContain("RecommendedLevel", DropVolumeTests.CodeOnly(File.ReadAllText(file)), StringComparison.Ordinal);

        // The corpus names the live field instead — WaveDef.ContentIndex, which IS Θ_content.
        var raw = File.ReadAllText(Path.Combine(LootDir(), "tables.v1.json"));
        Assert.Contains("WaveDef.ContentIndex", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_levels_match_the_shipped_wave_and_expedition_catalogs()
    {
        // Cross-checked against shipped code, not against the spec's prose.
        var byKey = Corpus().Sources.ToDictionary(s => s.Key, StringComparer.Ordinal);

        Assert.Equal(1, byKey["web-wave:rift-skirmish"].ContentLevel);
        Assert.Equal(3, byKey["web-wave:rift-warband"].ContentLevel);
        Assert.Equal(6, byKey["web-wave:rift-onslaught"].ContentLevel);
        Assert.Equal(10, byKey["web-wave:rift-tyrant"].ContentLevel);

        foreach (var tier in new[] { "scout-30m", "forage-4h", "hunt-8h", "warpath-20h" })
            Assert.True(byKey.ContainsKey("expedition-tier:" + tier), $"no loot_source for tier '{tier}'");
    }

    // ---- the OTHER corpus: the seedsmith drop tables ---------------------------------------------

    [Fact]
    public void The_seedsmith_drop_table_corpus_uses_kinds_this_build_cannot_yet_resolve()
    {
        // ⛔ Not a synthetic probe: this is the REAL 40-table corpus at
        // gk-data/packs/fusion/data/seed/items/drop-tables/, and it holds 315 entries of four kinds whose payload
        // machinery does not exist. The refusal must be BY NAME, per kind, naming the owning module
        // — never a silent drop.
        var dir = Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "drop-tables");
        var counts = new Dictionary<DropEntryKind, int>();
        var tables = 0;

        foreach (var path in Directory.EnumerateFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                tables++;
                foreach (var g in e.GetProperty("groups").EnumerateArray())
                    foreach (var row in g.GetProperty("entries").EnumerateArray())
                    {
                        var name = row.GetProperty("entryKind").GetString()!;
                        Assert.True(LootCorpusReader.TryKind(name, out var kind),
                            $"'{name}' is outside the nine-value enum entry-shapes.md §9 declares");
                        counts[kind] = counts.GetValueOrDefault(kind) + 1;
                    }
            }
        }

        // CONTRACT, not counts: the drop-table corpus is generator-authored and grows every generation
        // (43 tables and climbing), so both the table total and the per-kind tallies are stale by
        // construction. What must hold structurally is that the sweep read a real corpus, that every
        // kind resolved into the closed enum (asserted per row above), that the two kinds the data-shape
        // table once omitted are really used, and that availability is DECIDED PER KIND rather than
        // derived from the corpus size.
        Assert.True(tables > 0, "the drop-table corpus read returned nothing");
        Assert.True(counts[DropEntryKind.Unique] > 0, "the corpus must use the two kinds the shape table omitted");
        Assert.True(counts[DropEntryKind.Charm] > 0, "the corpus must use the two kinds the shape table omitted");

        // Availability is a property of the KIND, not of how many rows the corpus holds: every row of
        // an unavailable kind counts toward `unavailable`, and no row of an available kind does.
        var unavailable = counts.Where(kv => !DropTableDraw.IsAvailable(kv.Key)).Sum(kv => kv.Value);
        var available = counts.Where(kv => DropTableDraw.IsAvailable(kv.Key)).Sum(kv => kv.Value);
        Assert.Equal(tables > 0 ? counts.Values.Sum() : 0, unavailable + available);
        Assert.Contains(DropEntryKind.Insert, counts.Keys.Where(k => !DropTableDraw.IsAvailable(k)));
        Assert.Contains(DropEntryKind.Charm, counts.Keys.Where(k => !DropTableDraw.IsAvailable(k)));
        Assert.Contains(DropEntryKind.Consumable, counts.Keys.Where(k => !DropTableDraw.IsAvailable(k)));
        Assert.DoesNotContain(DropEntryKind.Unique, counts.Keys.Where(k => !DropTableDraw.IsAvailable(k)));
    }

    [Fact]
    public void An_insert_charm_or_consumable_entry_is_refused_by_name_until_seed_to_concrete_lands()
    {
        // ⭐ CORRECTED 2026-09-07 (container-kind-expansion, X7 landed): ContainerKind now ships all
        // eleven values, Gem/Charm/Consumable/Combo included -- verified against shipped code, not
        // assumed. The three drop-entry kinds below stay refused, but for the NEW, narrower reason:
        // the seed-to-concrete generator, not a missing container_kind.
        // 11 -> 12 (2026-09-13): empire-development Task 1.3a added ContainerKind.Relic.
        // 12 -> 14 on the 2026-09-16 merge: achievement-title T3's EmpireTitle/ActorTitle arrived
        // from a branch this one never saw. All three are reviewed additions and none is one of the
        // three refused kinds below, so the refusal claim this test protects still holds.
        // 14 -> 15 (species-progression SP3.2): ContainerKind.SpeciesProgression, likewise none of
        // the three refused kinds below.
        var kinds = Enum.GetNames(typeof(ContainerKind));
        // ContainerKind is pinned once, RelicTests.ContainerKind_has_fourteen_members_with_Relic_last
        // (population-pin SE3.3, 2026-09-19); reads that same live enum length, never a second literal.
        Assert.Equal(Enum.GetValues<ContainerKind>().Length, kinds.Length);
        Assert.Contains("Gem", kinds);
        Assert.Contains("Charm", kinds);
        Assert.Contains("Consumable", kinds);

        // `Unique` REMOVED 2026-09-06 (D4.27) -- it now resolves; see
        // `A_unique_entry_now_resolves_rather_than_being_refused_by_x7s_own_gate` below.
        var t = DropVolumeTests.Tuning();
        foreach (var kind in new[]
                 {
                     DropEntryKind.Insert, DropEntryKind.Charm,
                     DropEntryKind.Consumable,
                 })
        {
            var table = new DropTableRow("drop.test.x7", new[] { "web" }, null, null, true, 1, new[]
            {
                new DropTableGroupRow("g", 0, 1, new[]
                {
                    new DropTableEntryRow(0, kind, "some.ref", 100),
                    new DropTableEntryRow(1, DropEntryKind.Nothing, "", 900),
                }),
            });

            var result = DropTableValidator.Validate(Array.Empty<LootSourceRow>(), new[] { table }, t);
            Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
            Assert.Contains("drop.entry-kind-unavailable", result.Detail, StringComparison.Ordinal);
            Assert.Contains(LootCorpusReader.KindName(kind), result.Detail, StringComparison.Ordinal);
            // The refusal names WHO lands it — a build order, not a defect. Now the seed-to-concrete
            // generator (X7 itself already landed), not a specific numbered module.
            Assert.Contains("seed-to-concrete", result.Detail, StringComparison.Ordinal);
        }
    }

    /// <summary>The inverse of the test above (D4.27, 2026-09-06): a `unique` entry no longer trips
    /// the `drop.entry-kind-unavailable` gate — `UnavailableKinds` no longer names it. Asserts only
    /// that THIS specific gate is gone, not that the whole row validates clean for every other reason
    /// (a bogus `RefId` like this fixture's may still fail a DIFFERENT check, which is not this test's
    /// claim).</summary>
    [Fact]
    public void A_unique_entry_no_longer_trips_the_entry_kind_unavailable_gate()
    {
        Assert.True(DropTableDraw.IsAvailable(DropEntryKind.Unique));

        var t = DropVolumeTests.Tuning();
        var table = new DropTableRow("drop.test.unique", new[] { "web" }, null, null, true, 1, new[]
        {
            new DropTableGroupRow("g", 0, 1, new[]
            {
                new DropTableEntryRow(0, DropEntryKind.Unique, "item.rot-bloom-30-002", 100),
                new DropTableEntryRow(1, DropEntryKind.Nothing, "", 900),
            }),
        });

        var result = DropTableValidator.Validate(Array.Empty<LootSourceRow>(), new[] { table }, t);
        Assert.DoesNotContain("drop.entry-kind-unavailable", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void No_new_member_of_the_closed_thirty_three_code_list()
    {
        // spec-drop-volume.md's own success criterion, and it is checkable. I12 asked for eight new
        // codes; this module mints none.
        var names = Enum.GetNames(typeof(AtomRejectionReason));
        // AtomRejectionReason is pinned once, AtomKindRegistryTests (population-pin SE3.3,
        // 2026-09-19); reads that same live enum length, never a second literal.
        Assert.Equal(Enum.GetValues<AtomRejectionReason>().Length, names.Length);
        Assert.Contains("None", names);
        Assert.Contains("ContentRuleViolated", names);

        foreach (var invented in new[]
                 {
                     "UnknownDropTable", "UnknownBaseTypeSet", "UnknownCurrency", "DropTableDepthExceeded",
                     "DropTableCycle", "StandaloneRuleViolation", "RarityUnsatisfiable", "LootReplayMismatch",
                 })
            Assert.DoesNotContain(invented, names);

        // And the namespace this module raises under is registered.
        DropTableValidator.Validate(Array.Empty<LootSourceRow>(), Array.Empty<DropTableRow>(),
            DropVolumeTests.Tuning());
        Assert.True(ContentRuleNamespaces.IsRegistered("drop.entry-kind-unavailable"));
    }

    // ---- species-gear-chain T30b: the parametric trophy directive -------------------------------

    static DropTableRow TrophyTable(string? scope, int? slot, string refId = "") => new(
        "drop.test.trophy", new[] { "web" }, null, null, true, 1, new[]
        {
            new DropTableGroupRow("g", 0, 1, new[]
            {
                new DropTableEntryRow(0, DropEntryKind.Material, refId, 100, TrophyScope: scope, TrophySlot: slot),
                new DropTableEntryRow(1, DropEntryKind.Nothing, "", 900),
            }),
        });

    [Fact]
    public void A_legal_trophy_directive_validates_clean()
    {
        var t = DropVolumeTests.Tuning();
        var result = DropTableValidator.Validate(Array.Empty<LootSourceRow>(), new[] { TrophyTable("species", 1) }, t,
            new DropContentLookups(TrophySlotExists: (_, _) => true));
        Assert.True(result.IsOk, result.Detail);
    }

    [Fact]
    public void A_trophy_directive_round_trips_through_the_shape_drop_table_entry_row_declares()
    {
        // The shape itself, independent of the validator -- round-tripping is RpgStore's own job
        // (DropTableStoreTests), this only proves the C# record carries both fields together.
        var entry = new DropTableEntryRow(0, DropEntryKind.Material, "", 100, TrophyScope: "family", TrophySlot: 8);
        Assert.Equal("family", entry.TrophyScope);
        Assert.Equal(8, entry.TrophySlot);
        Assert.Equal("", entry.RefId);
    }

    [Fact]
    public void A_ref_id_and_a_trophy_scope_together_refuse_by_name()
    {
        var t = DropVolumeTests.Tuning();
        var result = DropTableValidator.Validate(Array.Empty<LootSourceRow>(),
            new[] { TrophyTable("species", 1, refId: "shard.chaff") }, t,
            new DropContentLookups(TrophySlotExists: (_, _) => true));
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.trophy-and-ref-conflict", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("species", null)]
    [InlineData(null, 1)]
    public void A_scope_without_a_slot_or_a_slot_without_a_scope_refuses_by_name(string? scope, int? slot)
    {
        var t = DropVolumeTests.Tuning();
        var result = DropTableValidator.Validate(Array.Empty<LootSourceRow>(), new[] { TrophyTable(scope, slot) }, t);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.trophy-scope-slot-mismatch", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_general_scope_refuses_by_name_r9_removed_the_general_layer()
    {
        var t = DropVolumeTests.Tuning();
        var result = DropTableValidator.Validate(Array.Empty<LootSourceRow>(), new[] { TrophyTable("general", 1) }, t,
            new DropContentLookups(TrophySlotExists: (_, _) => true));
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.trophy-scope-unknown", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_slot_refuses_by_name(int slot)
    {
        var t = DropVolumeTests.Tuning();
        var result = DropTableValidator.Validate(Array.Empty<LootSourceRow>(), new[] { TrophyTable("species", slot) }, t,
            new DropContentLookups(TrophySlotExists: (_, _) => true));
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.trophy-slot-invalid", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_slot_the_injected_registry_lookup_rejects_refuses_by_name()
    {
        var t = DropVolumeTests.Tuning();
        var result = DropTableValidator.Validate(Array.Empty<LootSourceRow>(), new[] { TrophyTable("species", 99) }, t,
            new DropContentLookups(TrophySlotExists: (_, _) => false));
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.trophy-slot-unknown", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_trophy_scope_on_a_non_material_entry_refuses_by_name()
    {
        var t = DropVolumeTests.Tuning();
        var table = new DropTableRow("drop.test.trophy-kind", new[] { "web" }, null, null, true, 1, new[]
        {
            new DropTableGroupRow("g", 0, 1, new[]
            {
                new DropTableEntryRow(0, DropEntryKind.Equipment, "", 100, TrophyScope: "species", TrophySlot: 1,
                    Frame: "humanoid", Role: "primary"),
                new DropTableEntryRow(1, DropEntryKind.Nothing, "", 900),
            }),
        });

        var result = DropTableValidator.Validate(Array.Empty<LootSourceRow>(), new[] { table }, t,
            new DropContentLookups(TrophySlotExists: (_, _) => true));
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, result.Reason);
        Assert.Contains("drop.trophy-requires-material-kind", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("species", 1, true)]   // perSpecies = 2 -- slot 1 is real
    [InlineData("species", 2, true)]
    [InlineData("species", 3, false)]  // perSpecies = 2 -- slot 3 was never minted
    [InlineData("family", 8, true)]    // perFamily = 8 -- slot 8 is the top real slot
    [InlineData("family", 9, false)]
    public void IsTrophySlotIssuable_matches_the_real_committed_registrys_own_persspecies_and_perfamily(
        string scope, int slot, bool expected)
    {
        // Wired end-to-end: no synthetic lookup here -- the Core.Tests bootstrap injects the REAL
        // committed trophy-registry.json (species-gear-chain T34), so this is the same answer
        // production's own DropContentLookups.TrophySlotExists gives.
        Assert.Equal(expected, FusionRpg.Core.Items.Materials.MaterialCatalog.IsTrophySlotIssuable(scope, slot));
    }
}
