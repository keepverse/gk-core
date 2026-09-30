using System.Text.Json.Nodes;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Power;
using FusionRpg.Core.Items.Sockets;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// strain-splice-host SSH6.2 (`combo-budget` §1): what a combination buys against what it costs.
/// The tuning, the ladder and the price table are the REAL shipped ones (the shipped sockets revision,
/// `materials.v4.json` + the real recipe corpus); only the atom catalog and the ladder's own spread are
/// fixtures, because power has to be controllable to make a cell fail on purpose.
///
/// <para>One class-level fixture note: `PowerTables.Current` is what prices both sides, so a fixture
/// atom — <c>stat.modify</c> on <c>maxHp</c>, the same shape
/// <c>RarityPowerCeilings.PriceReferenceSlate</c>'s own reference affix uses — is priced by the SHIPPED
/// coefficients rather than by a stub.</para>
/// </summary>
public class ComboPricingTests
{
    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The shipped ten rungs, verbatim from `gk-data/packs/fusion/data/seed/rarity/ladder.v1.json` (the same
    /// fixture `RarityPowerCeilingTests` pins, so a seed edit shows up as a diff rather than a
    /// re-derived expectation). Five of its nine steps buy nothing — the pairs that share an affix
    /// count — which is why the exclusion rule is exercised on real data below.</summary>
    static IReadOnlyList<RarityRow> ShippedLadder() => new[]
    {
        new RarityRow("chaff", 10, 0, 0, 1, 1),
        new RarityRow("sprout", 20, 0, 1, 1, 1),
        new RarityRow("grafted", 30, 0, 1, 1, 3),
        new RarityRow("cultivated", 40, 1, 1, 1, 3),
        new RarityRow("fused", 50, 1, 1, 2, 4),
        new RarityRow("chimeric", 60, 1, 2, 2, 4),
        new RarityRow("heirloom", 70, 1, 2, 3, 5),
        new RarityRow("firstseed", 80, 2, 2, 3, 5),
        new RarityRow("sunwoven", 90, 2, 2, 4, 5),
        new RarityRow("almanac", 100, 3, 2, 4, 5),
    };

    static SocketTuning Sockets() => SocketGeometryTests.Shipped();

    /// <summary>Parse the shipped tuning, mutate the JSON tree, re-parse — a VALUE edit on real tuning,
    /// never a hand-written fixture file (`MaterialCorpusTests.Mutated`'s own pattern).</summary>
    static SocketTuning MutatedSockets(Action<JsonNode> mutate)
    {
        var root = JsonNode.Parse(SocketGeometryTests.Raw())!;
        mutate(root);
        return SocketTuning.Parse(root.ToJsonString());
    }

    static void SetGrant(JsonNode root, string rung, int min, int max)
    {
        var grant = root["rarityGrant"]![rung]!;
        grant["socketMin"] = min;
        grant["socketMax"] = max;
    }

    static MaterialRecipeCatalog Materials() => MaterialCorpusTests.Catalog();

    static AtomRow Atom(string atomId, long amount) => new()
    {
        AtomId = atomId,
        KindId = "stat.modify",
        FamilyId = atomId.Split('.')[1],
        Tier = 1,
        ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":" + amount + "}",
    };

    static ComboContainerBuild.ComboContainerLookups Lookups(params AtomRow[] atoms)
    {
        var byId = atoms.ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        return new ComboContainerBuild.ComboContainerLookups(
            id => byId.TryGetValue(id, out var atom) ? atom : null);
    }

    static ComboPricingRequest Request(
        string comboId = "combo.strain-might-offense",
        IReadOnlyList<string>? grants = null, string? hostRole = null,
        int tier = 1, int[]? ingredientTiers = null, bool attuned = false) =>
        new(comboId,
            grants ?? new[] { "atom.might" },
            tier,
            ingredientTiers ?? new[] { 1, 1, 2, 2 },
            hostRole,
            attuned);

    static ComboPricingInputs Inputs(
        SocketTuning sockets, IReadOnlyList<RarityRow> ladder, AtomRow atom, long maxRatioMilli = 1000) =>
        Inputs(sockets, ladder, Lookups(atom), maxRatioMilli);

    static ComboPricingInputs Inputs(
        SocketTuning sockets, IReadOnlyList<RarityRow> ladder,
        ComboContainerBuild.ComboContainerLookups lookups, long maxRatioMilli = 1000) =>
        new(sockets, ladder, Materials(), lookups, maxRatioMilli);

    // ── §1 the comparison ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_comparison_is_cross_multiplied()
    {
        // power 1000 at a floor of 1000 is ONE point per soul, and the reference buys 1000/1001 — so the
        // cell fails by one part in 1001. Cross-multiplied: 1000·1001·1000 = 1_001_000_000 >
        // 1000·1000·1000 = 1_000_000_000.
        Assert.False(ComboPricing.Passes(
            power: 1000, priceFloorSouls: 1000,
            referenceDeltaPower: 1000, referenceElevateSouls: 1001,
            maxRatioToRarityRouteMilli: 1000));

        // The same cell as two truncated quotients: floor(1000·1000/1000) = 1000 and
        // floor(1000·1000/1001) = 999, and 1000 <= 999·1000 — the rounding, not the arithmetic, is what
        // would have passed it. This is the defect the cross-multiplied form exists to prevent.
        Assert.True(1000L <= 999L * 1000L);

        // D23's own boundary: at exactly equal power-per-soul the cell passes, because 1000 is "a word
        // may EQUAL, never undercut, the rarity route".
        Assert.True(ComboPricing.Passes(1000, 1000, 1000, 1000, 1000));
    }

    // ── §1 the rarity route ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_rarity_step_that_buys_nothing_is_excluded_by_name()
    {
        var ladder = ShippedLadder();
        var steps = ComboPricing.Steps(ladder, Materials());

        Assert.Equal(ladder.Count - 1, steps.Count);

        // The real ladder has exactly one step that buys nothing: 'firstseed' and 'sunwoven' share both
        // their affix count (four) and their reference tier (the middle of 3..5 / 4..5), so the slate
        // above prices identically. Excluded BY NAME — the reason carries both rungs — never dropped.
        var noop = Assert.Single(steps.Where(s => s.Excluded));
        Assert.Equal(("firstseed", "sunwoven"), (noop.FromRungId, noop.ToRungId));
        Assert.Equal(0, noop.DeltaPower);
        Assert.Contains("firstseed", noop.Reason);
        Assert.Contains("sunwoven", noop.Reason);

        // The chosen step is a real one, and its two terms are the spec's own: the reference slate of
        // the rung above minus this rung, over this rung's `elevate` souls.
        var chosen = ComboPricing.ChosenStep(steps);
        Assert.False(chosen.Excluded);
        Assert.True(chosen.DeltaPower > 0);
        Assert.True(chosen.ElevateSouls > 0);
        Assert.Equal(
            RarityPowerCeilings.PriceReferenceSlate(
                RarityPowerCeilings.WindowOf(ladder[chosen.ToRungIndex])) -
            RarityPowerCeilings.PriceReferenceSlate(
                RarityPowerCeilings.WindowOf(ladder[chosen.FromRungIndex])),
            chosen.DeltaPower);

        // `chaff` carries no affix, so its own slate is 0 and the first step is worth all of
        // `sprout`'s — the zero baseline the exclusion rule is a consequence of.
        Assert.Equal(
            RarityPowerCeilings.PriceReferenceSlate(RarityPowerCeilings.WindowOf(ladder[1])),
            steps[0].DeltaPower);
    }

    [Fact]
    public void All_rarity_steps_excluded_is_refused_by_name()
    {
        // Three rungs, one affix each: every step buys 0 power, so there is no rarity route at all — a
        // content defect the report must state, never a pass every cell inherits.
        var ladder = new[]
        {
            new RarityRow("chaff", 10, 1, 0, 1, 1),
            new RarityRow("sprout", 20, 1, 0, 1, 1),
            new RarityRow("grafted", 30, 1, 0, 1, 1),
        };
        var steps = ComboPricing.Steps(ladder, Materials());
        Assert.All(steps, s => Assert.True(s.Excluded));

        var refusal = Assert.Throws<ComboPricingRejection>(() => ComboPricing.ChosenStep(steps));
        Assert.Contains("no rarity step buys any power", refusal.Message);
        Assert.Contains("chaff", refusal.Message);

        // And the wiring: `Measure` refuses the same way rather than returning a report.
        Assert.Throws<ComboPricingRejection>(() =>
            ComboPricing.Measure(new[] { Request() },
                Inputs(Sockets(), ladder, Atom("atom.might.t1", 100))));
    }

    [Fact]
    public void The_top_rung_gives_no_step()
    {
        var ladder = ShippedLadder();
        var steps = ComboPricing.Steps(ladder, Materials());
        var top = ladder
            .OrderBy(r => FusionRpg.Core.Items.RarityLadder.RungIndexOf(r.RarityId))
            .Last().RarityId;

        // One step per adjacent pair: the top rung has no rho+1 to buy, so it never appears as the
        // FROM side — and it is the TO side exactly once.
        Assert.Equal(ladder.Count - 1, steps.Count);
        Assert.DoesNotContain(steps, s => s.FromRungId == top);
        Assert.Equal(1, steps.Count(s => s.ToRungId == top));
    }

    // ── §1 the price floor ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Price_floor_takes_the_cheapest_admitting_rung_and_best_rolled_chassis()
    {
        var ladder = ShippedLadder();
        var atom = Atom("atom.might.t1", 100);
        var reference = ComboPricing.ChosenStep(ComboPricing.Steps(ladder, Materials()));

        // (a) BEST-ROLLED CHASSIS, on the shipped table. `chaff` rolls 0..0 (four bores at 50 souls) and
        // `fused` rolls 2..4 (none) — so the floor is fused's, with NO bore at all. A reader that used
        // the window's MIN would bore two sockets on fused as well (2 x 250) and take chaff's four (200).
        var shipped = ComboPricing.MeasureCell(Request(), Inputs(Sockets(), ladder, atom), reference);
        Assert.Equal(4, shipped.FloorRungIndex);                        // 'fused'
        Assert.Empty(shipped.Legs.Where(l => l.Name == "bore"));
        Assert.Equal(4, shipped.Legs.Count(l => l.Name == "gem"));
        Assert.Equal(shipped.PriceFloorSouls, shipped.Legs.Sum(l => l.Souls));

        // (b) CHEAPEST ADMITTING RUNG. On the first three rungs the bore counts are 4 / 4 / 2 and the
        // souls price climbs by the bore coefficient a rung, so four bores at rung 0 (4 x 991 = 3964,
        // materials.v6) beat two at rung 2 (5946): the floor is the FIRST rung, not the first one that
        // needs no bore and not the last that does.
        var cheap = ComboPricing.MeasureCell(
            Request(), Inputs(Sockets(), ladder.Take(3).ToList(), atom), reference);
        Assert.Equal(0, cheap.FloorRungIndex);                          // 'chaff'
        var bore = Assert.Single(cheap.Legs.Where(l => l.Name == "bore"));
        Assert.Equal(4, bore.Quantity);
        Assert.Equal(3964, bore.Souls);                                 // 4 x materials.v6's 991-soul leg at rung 0
        Assert.Equal(4, cheap.Legs.Count(l => l.Name == "gem"));
        Assert.Equal(cheap.PriceFloorSouls, cheap.Legs.Sum(l => l.Souls));
    }

    [Fact]
    public void A_zero_floor_is_refused_by_name()
    {
        // The refusal itself, named: a floor of 0 is never divided by.
        var direct = Assert.Throws<ComboPricingRejection>(
            () => ComboPricing.RequirePositiveFloor("combo.strain-might-offense", 0));
        Assert.Contains("combo.strain-might-offense", direct.Message);

        // And the path that reaches it: this combination's ingredient plan is EMPTY and the shipped
        // table already carries rungs whose window reaches the circuit (fused and up), so on those the
        // floor is 0 bores + 0 imbues + 0 gems — nothing at all costs anything.
        var ladder = ShippedLadder();
        var wiring = Assert.Throws<ComboPricingRejection>(() => ComboPricing.MeasureCell(
            Request(ingredientTiers: Array.Empty<int>()),
            Inputs(Sockets(), ladder, Atom("atom.might.t1", 100)),
            ComboPricing.ChosenStep(ComboPricing.Steps(ladder, Materials()))));
        Assert.Contains("combo.strain-might-offense", wiring.Message);
        Assert.Contains("0 souls", wiring.Message);
    }

    // ── §1 the report ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_combination_cheaper_than_the_rarity_route_fails_the_report_by_cell()
    {
        var dear = Atom("atom.might.t1", 1_000_000);
        var cheap = Atom("atom.swiftness.t1", 1);
        var lookups = Lookups(dear, cheap);

        // Two cells, one reference: a word that buys 100_000_000 points for a 380-soul floor, and a
        // second at a millionth of that power. The reference buys 1_100_000 per 1000 souls, so the first
        // is far cheaper than the rarity route and the second is not — the verdict is PER CELL.
        var report = ComboPricing.Measure(
            new[] { Request(), Request(comboId: "combo.strain-might-offense-cheap",
                                       grants: new[] { "atom.swiftness" }) },
            Inputs(Sockets(), ShippedLadder(), lookups));

        // The cell is priced by the container's own atoms, through the one composer — not by a second
        // power read: the assertion rebuilds it the way the spec states the term.
        var container = ComboContainerBuild.TryBuild(
            "combo.strain-might-offense", new[] { "atom.might" }, 1, lookups, out _)!;
        var atoms = container.Atoms.Select(a => lookups.LookupAtom(a.AtomId)!).ToList();

        Assert.False(report.Passes);
        var failing = Assert.Single(report.FailingCells);
        Assert.Equal("combo.strain-might-offense", failing.ComboId);
        Assert.Equal(ActorPowerCache.Compose(atoms).Total, failing.Power);
        Assert.True(failing.Power > 0);
        Assert.True(failing.PriceFloorSouls > 0);
        Assert.True(failing.RatioMilli > failing.ReferenceMilli);

        var passing = Assert.Single(report.Cells.Where(c => c.ComboId == "combo.strain-might-offense-cheap"));
        Assert.True(passing.Passes);
        Assert.True(passing.RatioMilli <= passing.ReferenceMilli);
        Assert.Equal(report.Reference, passing.Reference);
    }

    // ── §4-§5 the published provenance (SSH6.6) ─────────────────────────────────────────────────

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Core")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root not found");
    }

    static string Digest() => new('a', 64);

    static ComboPricingLoadedRevisions Loaded(
        int socketsVersion = 2, int strainSpliceVersion = 1, int materialsVersion = 4, string? digest = null) =>
        new(socketsVersion, strainSpliceVersion, materialsVersion,
            SocketLimits.SocketCircuitSize, digest ?? Digest());

    static ComboPricingMeasuredAgainst Measured(
        int socketsVersion = 2, int strainSpliceVersion = 1, int materialsVersion = 4,
        int circuitSize = SocketLimits.SocketCircuitSize, string? digest = null) =>
        new(socketsVersion, strainSpliceVersion, materialsVersion, circuitSize, digest ?? Digest());

    [Fact]
    public void The_optional_combo_pricing_section_parses_on_the_published_revision()
    {
        // SSH6.8 published `comboPricing` into `sockets.v3.json`: the D23 bound (1000, a closed authored
        // value) plus the measurement's own provenance. The version FIELDS are readings of the shipped
        // files, so they are asserted structurally (present, positive, matching the loaded revisions),
        // never pinned to a literal that the next publish would stale.
        var sockets = Sockets();
        Assert.Equal(1000, sockets.ComboPricingMaxRatioToRarityRouteMilli);
        var measuredOnShipped = sockets.ComboPricingMeasuredAgainst;
        Assert.NotNull(measuredOnShipped);
        Assert.True(measuredOnShipped!.SocketsVersion > 0);
        Assert.True(measuredOnShipped.StrainSpliceVersion > 0);
        Assert.True(measuredOnShipped.MaterialsVersion > 0);
        Assert.Equal(SocketLimits.SocketCircuitSize, measuredOnShipped.CircuitSize);
        Assert.Equal(System.Security.Cryptography.SHA256.HashSizeInBytes * 2,
            measuredOnShipped.CombinationCorpusDigest.Length);
        Assert.True(measuredOnShipped.CombinationCorpusDigest.All(Uri.IsHexDigit));

        // A document that DROPS the section parses back to the absent state (the pre-SSH6.8 shape) —
        // proving the field is optional, not that today's file lacks it.
        var absent = MutatedSockets(root => root["comboPricing"] = null);
        Assert.Null(absent.ComboPricingMeasuredAgainst);
        Assert.Null(absent.ComboPricingMaxRatioToRarityRouteMilli);

        var digest = Digest();
        var withPricing = MutatedSockets(root => root["comboPricing"] = new JsonObject
        {
            ["maxRatioToRarityRouteMilli"] = 1000,
            ["measuredAgainst"] = new JsonObject
            {
                ["socketsVersion"] = 2,
                ["strainSpliceVersion"] = 1,
                ["materialsVersion"] = 4,
                ["circuitSize"] = SocketLimits.SocketCircuitSize,
                ["combinationCorpusDigest"] = digest,
            },
        });

        Assert.Equal(1000, withPricing.ComboPricingMaxRatioToRarityRouteMilli);
        var measured = withPricing.ComboPricingMeasuredAgainst;
        Assert.NotNull(measured);
        Assert.Equal(2, measured!.SocketsVersion);
        Assert.Equal(1, measured.StrainSpliceVersion);
        Assert.Equal(4, measured.MaterialsVersion);
        Assert.Equal(SocketLimits.SocketCircuitSize, measured.CircuitSize);
        Assert.Equal(digest, measured.CombinationCorpusDigest);

        // A digest that is not a SHA-256 hex string is refused by name: nothing else can be compared.
        var refusal = Assert.Throws<SocketTuningRejection>(() => MutatedSockets(
            root => root["comboPricing"] = new JsonObject
            {
                ["maxRatioToRarityRouteMilli"] = 1000,
                ["measuredAgainst"] = new JsonObject
                {
                    ["socketsVersion"] = 2, ["strainSpliceVersion"] = 1, ["materialsVersion"] = 4,
                    ["circuitSize"] = SocketLimits.SocketCircuitSize,
                    ["combinationCorpusDigest"] = "not-a-digest",
                },
            }));
        Assert.Contains("SHA-256", refusal.Message);
    }

    [Fact]
    public void A_single_rung_ladder_loads_without_provenance()
    {
        // One rung: there is nothing to bind against, so the absent section is the normal state.
        Assert.Null(ComboPricingProvenance.Check(measuredAgainst: null, Loaded(), ladderRungCount: 1));

        // And a present, MATCHING provenance is fine too.
        Assert.Null(ComboPricingProvenance.Check(Measured(), Loaded(), ladderRungCount: 1));
    }

    [Fact]
    public void A_multi_rung_ladder_refuses_unmeasured_pricing()
    {
        var refusal = ComboPricingProvenance.Check(measuredAgainst: null, Loaded(), ladderRungCount: 3);
        Assert.NotNull(refusal);
        Assert.Equal(ComboPricingProvenanceRefusal.UnmeasuredRule, refusal!.Rule);
        Assert.Contains("3 rungs", refusal.Detail);
        Assert.Contains("combo-budget", refusal.Detail);
    }

    [Fact]
    public void A_ladder_published_after_the_measurement_is_refused_until_re_measured()
    {
        // Measured while the ladder was one rung; the ladder has since published a second (SSH7.1's own
        // rule: every ladder publish invalidates the provenance).
        var refusal = ComboPricingProvenance.Check(
            Measured(strainSpliceVersion: 1), Loaded(strainSpliceVersion: 2), ladderRungCount: 2);
        Assert.NotNull(refusal);
        Assert.Equal(ComboPricingProvenanceRefusal.StaleRule, refusal!.Rule);
        Assert.Contains("strainSpliceVersion measured 1, loaded 2", refusal.Detail);

        // The same check passes once the measurement names the loaded revision.
        Assert.Null(ComboPricingProvenance.Check(
            Measured(strainSpliceVersion: 2), Loaded(strainSpliceVersion: 2), ladderRungCount: 2));

        // Every other field is compared the same way, and called out by name.
        Assert.Contains("materialsVersion measured 1, loaded 4",
            ComboPricingProvenance.Check(Measured(materialsVersion: 1), Loaded(), 2)!.Detail);
        Assert.Contains("circuitSize measured 8, loaded 4",
            ComboPricingProvenance.Check(Measured(circuitSize: 8), Loaded(), 2)!.Detail);
        Assert.Contains("combinationCorpusDigest",
            ComboPricingProvenance.Check(Measured(digest: new string('b', 64)), Loaded(), 2)!.Detail);
        Assert.Contains("socketsVersion measured 1, loaded 2",
            ComboPricingProvenance.Check(Measured(socketsVersion: 1), Loaded(), 2)!.Detail);
    }

    [Fact]
    public void Provenance_versions_are_filename_revisions_not_the_internal_version_field()
    {
        // The wart this rule exists for: the FILENAME revisions are monotonic (v1 < v2 < v3 …) while the
        // files' own internal `version` fields are not — an earlier publish bumped one in place, so a
        // lower revision can carry a higher internal version. A measurement recorded against the internal
        // field would refuse every revision that is in fact newer.
        var tuningDir = Path.Combine(RepoRoot(), "data", "tuning");
        int InternalVersion(string path) => System.Text.Json.JsonDocument
            .Parse(File.ReadAllText(path)).RootElement.GetProperty("version").GetInt32();
        int FilenameRevision(string path) =>
            SocketTuningFiles.RevisionOf(Path.GetFileName(path));

        var byRevision = Directory.GetFiles(tuningDir, "sockets.v*.json")
            .Select(path => (Path: path, Rev: FilenameRevision(path)))
            .OrderBy(c => c.Rev)
            .ToList();

        // Find the adjacent pair that actually exhibits the inversion, structurally: the higher FILENAME
        // revision carrying the LOWER internal version. This is the property, not a transcription of
        // whichever files happen to be on disk.
        var inverted = byRevision.Zip(byRevision.Skip(1))
            .FirstOrDefault(pair => InternalVersion(pair.Second.Path) < InternalVersion(pair.First.Path));
        Assert.True(inverted != default,
            "no adjacent sockets revisions invert internal-vs-filename order, so this test no longer " +
            "exercises the discrepancy it guards");

        var lowerInternal = InternalVersion(inverted.First.Path);
        // Measured against the INTERNAL field of the higher-filename revision, while the filename
        // revision the loaded one actually is differs — refused, naming the field.
        var loadedRevision = inverted.Second.Rev;
        var refusal = ComboPricingProvenance.Check(
            Measured(socketsVersion: lowerInternal), Loaded(socketsVersion: loadedRevision),
            ladderRungCount: 2);
        Assert.NotNull(refusal);
        Assert.Equal(ComboPricingProvenanceRefusal.StaleRule, refusal!.Rule);
        Assert.Contains("socketsVersion measured", refusal.Detail);

        // Measured against the FILENAME revision, it passes.
        Assert.Null(ComboPricingProvenance.Check(
            Measured(socketsVersion: loadedRevision), Loaded(socketsVersion: loadedRevision),
            ladderRungCount: 2));
    }

    // ── §1 "Which lever" (R20) ──────────────────────────────────────────────────────────────────

    /// <summary>Re-price the whole catalog with the given coefficients — exactly what
    /// `socket-pricing` publishes, so the derivation is checked against the resolver rather than
    /// against its own arithmetic.</summary>
    static ComboPricingInputs InputsWithCoefficients(
        SocketTuning sockets, IReadOnlyList<RarityRow> ladder,
        ComboContainerBuild.ComboContainerLookups lookups,
        IReadOnlyDictionary<ComboPricingLever, long> coefficients, long maxRatioMilli = 1000)
    {
        var text = MaterialCorpusTests.Mutated(node =>
        {
            foreach (var (lever, value) in coefficients)
            {
                var operation = lever switch
                {
                    ComboPricingLever.Bore => "bore",
                    ComboPricingLever.Imbue => "imbue",
                    ComboPricingLever.ForgeGem => "forge-gem",
                    _ => throw new ArgumentOutOfRangeException(nameof(coefficients)),
                };
                node["operations"]![operation]!["souls"]!["coefficient"] = value;
            }
        });
        var catalog = MaterialRecipeCatalog.Load(
            MaterialCorpusTests.RecipeCorpus(), MaterialTuning.Parse(text));
        return new ComboPricingInputs(sockets, ladder, catalog, lookups, maxRatioMilli);
    }

    [Fact]
    public void A_cell_with_no_bore_or_imbue_on_its_floor_route_names_forge_gem_as_its_lever()
    {
        var ladder = ShippedLadder();
        var dear = Atom("atom.might.t1", 1_000_000);
        var lookups = Lookups(dear);

        // The shipped floor is `fused`'s, which needs neither a bore nor an imbue (SSH6.2), so the gem
        // leg is the ONLY thing on this cell's route that any coefficient can move.
        var report = ComboPricing.Measure(new[] { Request() }, Inputs(Sockets(), ladder, lookups));
        var cell = Assert.Single(report.FailingCells);
        Assert.Empty(cell.Legs.Where(l => l.Lever != ComboPricingLever.ForgeGem));
        Assert.Equal(new[] { ComboPricingLever.ForgeGem }, ComboPricing.LeversOf(cell));

        var derivation = ComboPricing.Derive(report, Inputs(Sockets(), ladder, lookups));
        var fix = Assert.Single(derivation.Cells);
        Assert.Equal("combo.strain-might-offense", fix.ComboId);
        Assert.Equal(ComboPricingLever.ForgeGem, fix.SmallestLever);
        Assert.Equal(new[] { ComboPricingLever.ForgeGem }, fix.Levers);
        Assert.Empty(derivation.UnfixableCellIds);

        // A cell whose floor DOES carry a bore names that lever too — the legs on the route decide, and
        // the cheap-rung floor is four bores at rung 0.
        var bored = ComboPricing.Measure(
            new[] { Request() }, Inputs(Sockets(), ladder.Take(3).ToList(), lookups));
        var boredCell = Assert.Single(bored.FailingCells);
        Assert.Equal(new[] { ComboPricingLever.Bore, ComboPricingLever.ForgeGem },
                     ComboPricing.LeversOf(boredCell));
    }

    [Fact]
    public void a_gem_only_cell_is_fixed_by_the_derived_forge_gem_coefficient()
    {
        var ladder = ShippedLadder();
        var dear = Atom("atom.might.t1", 1_000_000);
        var lookups = Lookups(dear);
        var inputs = Inputs(Sockets(), ladder, lookups);

        var derivation = ComboPricing.Derive(ComboPricing.Measure(new[] { Request() }, inputs), inputs);
        var forgeGem = derivation.Levers.Single(l => l.Lever == ComboPricingLever.ForgeGem);
        Assert.True(forgeGem.Moved);
        Assert.Equal(657, forgeGem.CurrentCoefficient);        // materials.v6's published souls coefficient

        // The derived coefficient — the smallest one the arithmetic allows — makes the cell pass...
        var atDerived = ComboPricing.Measure(new[] { Request() },
            InputsWithCoefficients(Sockets(), ladder, lookups,
                new Dictionary<ComboPricingLever, long> { [ComboPricingLever.ForgeGem] = forgeGem.DerivedCoefficient }));
        Assert.True(Assert.Single(atDerived.Cells).Passes,
            "derived=" + forgeGem.DerivedCoefficient + " floor=" + atDerived.Cells[0].PriceFloorSouls);

        // ...and one below it does not, which is what "derived" has to mean.
        var oneBelow = ComboPricing.Measure(new[] { Request() },
            InputsWithCoefficients(Sockets(), ladder, lookups,
                new Dictionary<ComboPricingLever, long> { [ComboPricingLever.ForgeGem] = forgeGem.DerivedCoefficient - 1 }));
        Assert.False(Assert.Single(oneBelow.Cells).Passes);
    }

    [Fact]
    public void The_derived_coefficient_makes_every_cell_pass()
    {
        var ladder = ShippedLadder();
        var dear = Atom("atom.might.t1", 1_000_000);
        var dearer = Atom("atom.swiftness.t1", 4_000_000);
        var lookups = Lookups(dear, dearer);
        var requests = new[]
        {
            Request(),
            Request(comboId: "combo.strain-might-offense-dear", grants: new[] { "atom.swiftness" }),
        };

        // The shipped floor (no bore, no imbue) leaves every cell to the gem leg, so the derived
        // `forge-gem` coefficient is the one that has to carry all of them.
        var inputs = Inputs(Sockets(), ladder, lookups);
        var before = ComboPricing.Measure(requests, inputs);
        Assert.Equal(2, before.FailingCells.Count);

        var derivation = ComboPricing.Derive(before, inputs);
        Assert.Empty(derivation.UnfixableCellIds);
        Assert.Equal(2, derivation.Cells.Count);
        var derived = derivation.Levers.ToDictionary(l => l.Lever, l => l.DerivedCoefficient);
        Assert.True(derivation.AnyLeverMoved);

        var after = ComboPricing.Measure(requests, InputsWithCoefficients(Sockets(), ladder, lookups, derived));
        Assert.True(after.Passes, string.Join("; ", after.FailingCells.Select(
            c => c.ComboId + " floor=" + c.PriceFloorSouls + " ratio=" + c.RatioMilli + " ref=" + c.ReferenceMilli)));

        // The derivation is per lever over the cells it can move: the dearest cell needs the most, and
        // it is the one that sets `forge-gem`.
        var forgeGem = derivation.Levers.Single(l => l.Lever == ComboPricingLever.ForgeGem);
        Assert.Equal(Math.Max(
            derivation.Cells[0].RequiredCoefficient, derivation.Cells[1].RequiredCoefficient),
            forgeGem.DerivedCoefficient);
    }

    [Fact]
    public void a_cell_no_leg_can_move_is_named_not_published()
    {
        // Unreachable through `Measure` today (every shipped gem leg is forge-gem-priced), so the
        // mechanism is proved on a hand-built report: a cell whose only leg is priced by the upcycle
        // chain carries no lever, and the derivation must NAME it rather than invent a coefficient to
        // publish for it (`AnyLeverMoved` stays false).
        var report = new ComboPricingReport(
            new[]
            {
                new ComboPricingCell(
                    ComboId: "combo.strain-chain-priced", Tier: 1, Attuned: false,
                    Power: 1_000_000, PriceFloorSouls: 180,
                    Legs: new[] { new ComboPriceLeg("gem", 1, 180, 0, Lever: null) },
                    FloorRungIndex: 4, Reference: default, Passes: false),
            },
            Reference: default,
            Steps: Array.Empty<ComboReferenceStep>());

        var derivation = ComboPricing.Derive(report, Inputs(Sockets(), ShippedLadder(), Atom("atom.might.t1", 1)));

        Assert.Equal(new[] { "combo.strain-chain-priced" }, derivation.UnfixableCellIds);
        var fix = Assert.Single(derivation.Cells);
        Assert.Null(fix.SmallestLever);
        Assert.Empty(fix.Levers);
        Assert.False(derivation.AnyLeverMoved);
    }
}
