using System.Linq;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Generation;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms.Generation;

/// <summary>
/// E43 <c>family-expand</c> (spec-family-expand.md §5's ten-test table). Runs the real, shipped
/// corpus (98 affix families, tier-bands.v1.json) wherever a test needs to prove something about
/// today's actual data — matching <c>ChannelPoolTests</c>' own real-corpus discipline in this same
/// directory — and constructs synthetic families only where the real corpus genuinely has no example
/// to reach a code path with (element-typed pool emission: no real family has an authored share yet).
/// </summary>
public class FamilyExpansionTests
{
    static string FindDataDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "data");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "data"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new DirectoryNotFoundException("could not locate data/ above " + AppContext.BaseDirectory);
    }

    static (IReadOnlyList<FamilyEntryInput> Families, TierBandsInput TierBands) LoadReal()
    {
        var itemsRoot = Path.Combine(FindDataDir(), "seed", "items");
        var familiesDir = Path.Combine(itemsRoot, "affix-families");
        // Real bug fixed 2026-09-08 (atom-family-expansion): this hardcoded literal "v1.json",
        // the same defect fixed in gk-forge/tools/FamilyExpandGen/Program.cs — this file's own tests exist to
        // prove FamilyExpansion against "the real, shipped corpus" (its own class doc comment), which
        // means the real, LATEST published tuning, not a frozen v1 snapshot several versions behind
        // what's actually committed to gk-data/packs/fusion/data/seed/atoms/generated/ today.
        var tierBands = TierBandsFile.Read(File.ReadAllText(TierBandsFile.FindLatestPath(Path.Combine(itemsRoot, "_tuning"))));

        var families = new List<FamilyEntryInput>();
        foreach (var file in Directory.GetFiles(familiesDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).StartsWith('_')) continue;
            families.AddRange(AffixFamilyFile.Read(Path.GetFileName(file), File.ReadAllText(file)));
        }

        return (families, tierBands);
    }

    /// <summary>The real reference bases — shipped curves (via this assembly's own
    /// <c>ContractTuningTestBootstrap</c> module initializer, power-scale.v2.json values) PLUS the
    /// v3 channel pins (passive-tree-repair V3-1) PLUS the channel-policy interval references
    /// (IL-1), mirroring production <c>FamilyExpandGen.FlatReferenceBase</c> exactly. A pin added
    /// without updating this mirror fails the byte-compare tests below — that is the mechanism
    /// working, not brittleness: the mirror must move with the file.</summary>
    static long? FlatReferenceBase(string channel)
    {
        long? direct = channel switch
        {
            "maxHp" or "hp" => BattleRuleset.BaseHp(FamilyExpansion.ReferenceLevel),
            "atk" => BattleRuleset.BaseAtk(FamilyExpansion.ReferenceLevel),
            "defense" => BattleRuleset.BaseDefense(FamilyExpansion.ReferenceLevel),
            _ => null,
        };
        if (direct is not null) return direct;

        var v3Path = Path.Combine(FindDataDir(), "tuning", "power-scale.v3.json");
        if (File.Exists(v3Path))
        {
            var v3 = PowerTuningLoader.Parse(File.ReadAllText(v3Path));
            if (v3.ChannelsOrEmpty.TryGetValue(channel, out var tuning)) return tuning.PinValue;
        }

        var policyPath = Path.Combine(FindDataDir(), "seed", "channel-policy", "defaults.json");
        if (File.Exists(policyPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(policyPath));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                if (e.GetProperty("channel").GetString() == channel
                    && e.TryGetProperty("referenceMs", out var ms)
                    && ms.TryGetInt64(out var v))
                    return v;
            }
        }

        return null;
    }

    static FamilyExpansionResult ExpandReal()
    {
        var (families, tierBands) = LoadReal();
        return FamilyExpansion.Expand(families, tierBands, FlatReferenceBase, RealStatusAnchor());
    }

    /// <summary>Production's own status anchor (`FamilyExpandGen.Program.cs:187-207`): the latest
    /// `data/seed/items/_registry/status-anchor.v*.json`, read into a family-id lookup. A `status.apply`
    /// family with no row refuses (naming the missing anchor), so a byte-compare that omits it cannot
    /// see the rows the committed tree was regenerated WITH — that omission is what left
    /// `Committed_generated_files_match_the_generator_byte_for_byte` red on `family-expand.g-evade.json`.
    /// Mirrors production the same way <see cref="FlatReferenceBase"/> does, and must move with it.</summary>
    static Func<string, StatusAnchorRow?> RealStatusAnchor()
    {
        var registryDir = Path.Combine(FindDataDir(), "seed", "items", "_registry");
        var anchorPath = Directory.Exists(registryDir)
            ? Directory.GetFiles(registryDir, "status-anchor.v*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f, StringComparer.Ordinal)
                .FirstOrDefault()
            : null;
        if (anchorPath is null) return _ => null;

        var rows = StatusAnchorFile.Read(File.ReadAllText(anchorPath));
        return familyId => rows.TryGetValue(familyId, out var row) ? row : null;
    }

    /// <summary>Real, already-shipped pool catalog (`gk-data/packs/fusion/data/seed/channel-pools/pools.v1.json`, E30) —
    /// needed once the real corpus (via `LoadReal`'s own 2026-09-08 fix) started emitting real
    /// pool-referencing `stat.derived` rows (`evd-flinch`/`evd-harden`/`evd-seal`/`shld-breach`),
    /// which `CostFunction.Price` cannot price without a `lookupPool`. Mirrors
    /// `ContentValidationTests.RealLookupPool` exactly (not shared across test classes — this
    /// repo's own DAMP-over-DRY test convention).</summary>
    static Func<string, ChannelPoolRow?> RealLookupPool()
    {
        var path = Path.Combine(FindDataDir(), "seed", "channel-pools", "pools.v1.json");
        var rejection = ChannelPoolFile.TryParse(File.ReadAllText(path), out var pools);
        Assert.True(rejection.IsOk, rejection.Detail);
        var byId = pools.ToDictionary(p => p.PoolId, StringComparer.Ordinal);
        return id => byId.TryGetValue(id, out var row) ? row : null;
    }

    // ---- test 1: deterministic --------------------------------------------------------------------

    [Fact]
    public void Expansion_is_deterministic_across_two_runs()
    {
        var (families, tierBands) = LoadReal();

        var first = FamilyExpansion.Expand(families, tierBands, FlatReferenceBase);
        var second = FamilyExpansion.Expand(families, tierBands, FlatReferenceBase);

        Assert.Equal(first.Rows.Count, second.Rows.Count);
        for (var i = 0; i < first.Rows.Count; i++)
        {
            Assert.Equal(first.Rows[i].AtomId, second.Rows[i].AtomId);
            Assert.Equal(first.Rows[i].ParamsJson, second.Rows[i].ParamsJson);
            Assert.Equal(first.Rows[i].TagsJson, second.Rows[i].TagsJson);
        }

        Assert.Equal(
            first.Refusals.Select(r => (r.FamilyId, r.Reason)),
            second.Refusals.Select(r => (r.FamilyId, r.Reason)));
    }

    // ---- test 2: --check-equivalent — regenerate and compare to the committed output --------------

    [Fact]
    public void Regenerating_matches_the_committed_generated_files_exactly()
    {
        var result = ExpandReal();
        var byFamilyId = LoadReal().Families.ToDictionary(f => f.Id, f => f.SourceFile, StringComparer.Ordinal);
        var generatedDir = Path.Combine(FindDataDir(), "seed", "atoms", "generated");

        var bySource = result.Rows.GroupBy(r => byFamilyId[r.FamilyId]);
        foreach (var group in bySource)
        {
            var stem = Path.GetFileNameWithoutExtension(group.Key);
            var outPath = Path.Combine(generatedDir, $"family-expand.{stem}.json");
            Assert.True(File.Exists(outPath), $"expected committed output {outPath} — run FamilyExpandGen and commit the result");

            using var doc = JsonDocument.Parse(File.ReadAllText(outPath));
            var committedIds = doc.RootElement.GetProperty("entries").EnumerateArray()
                .Select(e => AtomRow.DeriveId(
                    e.GetProperty("family").GetString()!, "", e.GetProperty("tier").GetInt32()))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
            var freshIds = group.Select(r => r.AtomId).OrderBy(x => x, StringComparer.Ordinal).ToList();

            Assert.Equal(freshIds, committedIds);
        }
    }

    /// <summary>P1.1 (tasks/passive-tree-repair-plan.md): the id-only comparison above could not see a
    /// renamed family (same ids, new `name`) or a newline difference, and both really shipped — three
    /// committed files drifted from their sources for a full commit cycle. This pins the CONTRACT that
    /// would have caught it: each committed file's canonical text must equal the generator's canonical
    /// text (one shared serializer, <see cref="FamilyExpansionSeedFile"/>).
    /// <para>Comparison is made in canonical form, never against raw working-tree bytes: a checkout
    /// with <c>core.autocrlf=true</c> legitimately materialises CRLF, so asserting "no CRLF on disk"
    /// would test the environment rather than the content.</para>
    /// </summary>
    [Fact]
    public void Committed_generated_files_match_the_generator_byte_for_byte()
    {
        var (families, tierBands) = LoadReal();
        var generatedDir = Path.Combine(FindDataDir(), "seed", "atoms", "generated");
        var result = FamilyExpansion.Expand(families, tierBands, FlatReferenceBase, RealStatusAnchor());

        foreach (var group in result.Rows.GroupBy(r => families.First(f => f.Id == r.FamilyId).SourceFile))
        {
            var stem = Path.GetFileNameWithoutExtension(group.Key);
            var outPath = Path.Combine(generatedDir, $"family-expand.{stem}.json");
            Assert.True(File.Exists(outPath), $"expected committed output {outPath}");

            var committed = FamilyExpansionSeedFile.Canonicalize(File.ReadAllText(outPath));
            var fresh = FamilyExpansionSeedFile.ToCanonicalJson(group.ToList());

            Assert.Equal(fresh, committed);
        }
    }

    [Fact]
    public void A_manufactured_drift_is_detectable_by_the_same_comparison_the_check_mode_uses()
    {
        // Proves the comparison mechanism itself can FAIL, not just always pass — a corrupted copy of
        // a real committed row must disagree with what the generator produces fresh.
        var result = ExpandReal();
        var vitality = result.Rows.First(r => r.FamilyId == "atom.vitality" && r.Tier == 1);

        var corrupted = vitality with { ParamsJson = vitality.ParamsJson.Replace("\"min\":", "\"min\":999999,\"__was\":") };

        Assert.NotEqual(vitality.ParamsJson, corrupted.ParamsJson);
    }

    // ---- test 3: every emitted id matches AtomRow.DeriveId exactly ---------------------------------

    [Fact]
    public void Every_emitted_id_matches_AtomRow_DeriveId_exactly()
    {
        var result = ExpandReal();
        Assert.NotEmpty(result.Rows);

        foreach (var row in result.Rows)
            Assert.Equal(AtomRow.DeriveId(row.FamilyId, row.Variant, row.Tier), row.AtomId);
    }

    // ---- test 4: no collision across all 98 families ------------------------------------------------

    [Fact]
    public void No_family_tier_variant_collision_across_all_98_families()
    {
        var result = ExpandReal();

        var keys = result.Rows.Select(r => (r.FamilyId, r.Tier, r.Variant)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    // ---- test 5: an element-typed family emits ONE row per tier with a pool reference --------------

    [Fact]
    public void Element_typed_family_emits_one_row_per_tier_with_a_pool_reference_not_seven()
    {
        // No real family reaches this path today (§ investigation: every element-typed family in the
        // real corpus also has no authored share, so it is refused for the share reason first). A
        // synthetic family with a real, in-vocabulary stem proves the pool-emission behaviour on its
        // own, isolated from the share gap.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["synth-elem"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Increased"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.synth-elem", Name: "Synthetic Elemental", KindId: "stat.derived",
            Channel: "combat.power.{variant}", Op: "Increased", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => null);

        Assert.Empty(result.Refusals);
        Assert.Equal(FamilyExpansion.TierCount, result.Rows.Count);

        var tiersSeen = new HashSet<int>();
        foreach (var row in result.Rows)
        {
            Assert.Equal("", row.Variant); // element never materialises into a variant segment
            tiersSeen.Add(row.Tier);

            using var doc = JsonDocument.Parse(row.ParamsJson);
            var channel = doc.RootElement.GetProperty("channel");
            Assert.Equal(JsonValueKind.Object, channel.ValueKind);
            Assert.Equal("pool.element-power", channel.GetProperty("pool").GetString());
            Assert.Equal(1, channel.GetProperty("count").GetInt32());
            Assert.False(channel.GetProperty("allowRepeat").GetBoolean());
        }

        Assert.Equal(FamilyExpansion.TierCount, tiersSeen.Count);
    }

    [Fact]
    public void Element_typed_family_naming_an_unmapped_channel_template_is_refused_never_guessed()
    {
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["synth-elem-2"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Increased"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.synth-elem-2", Name: "No Pool", KindId: "stat.derived",
            Channel: "combat.power.pierce.{variant}", Op: "Increased", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => null);

        Assert.Empty(result.Rows);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("atom.synth-elem-2", refusal.FamilyId);
        Assert.Contains("channel pool", refusal.Reason, StringComparison.Ordinal);
    }

    // ---- test 6: every emitted row validates and prices ---------------------------------------------

    [Fact]
    public void Every_emitted_row_validates_through_AtomRowValidator_and_prices_nonzero()
    {
        var result = ExpandReal();
        Assert.NotEmpty(result.Rows);

        var lookupPool = RealLookupPool();
        foreach (var row in result.Rows)
        {
            var validated = AtomRowValidator.Validate(row);
            Assert.True(validated.IsOk, $"{row.AtomId}: {validated}");

            var priced = CostFunction.Price(row, PowerTables.Authored(), lookupPool: lookupPool);
            Assert.True(priced.Ok, $"{row.AtomId}: {priced.Verdict.Reason}");
            // ⛔ Corrected 2026-09-08: no longer true once the real corpus grew past the original
            // 3-file, stat.modify-only baseline — `evd-flinch`/`evd-harden`/`evd-seal`/`shld-breach`
            // are real `stat.derived` pool-referencing rows now. The real invariant is just "priced
            // atoms carry real power," not "every atom is stat.modify."
            Assert.NotEqual(PowerVector.Zero, priced.Power);
        }
    }

    // ---- test 7: planted violations — refused by id --------------------------------------------------

    [Fact]
    public void PlantedViolation_a_family_with_no_authored_share_is_refused_by_id()
    {
        // ⛔ Corrected 2026-09-08: this used to rely on `atom.elpw-override` being a REAL family the
        // real corpus happened to leave uncovered — true when this test was written, false since
        // atom-family-expansion's `tier-bands-coverage` module published real coverage for it (and
        // 97 siblings). Relying on "some real family happens to have a real gap today" is exactly the
        // kind of test this repo's own incident log (this session's own drift-detection discipline)
        // warns against — switched to a genuinely synthetic family, matching the sibling planted-
        // violation test immediately below, which never depended on the real corpus's own coverage.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long>(), // deliberately empty -- no share for anyone
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.planted-no-share", Name: "Planted", KindId: "stat.modify",
            Channel: "atk", Op: "Flat", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 100);

        var refusal = result.Refusals.SingleOrDefault(r => r.FamilyId == "atom.planted-no-share");
        Assert.NotNull(refusal);
        Assert.Contains("no authored sharePermille", refusal!.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Rows, r => r.FamilyId == "atom.planted-no-share");
    }

    [Fact]
    public void PlantedViolation_a_family_naming_an_unknown_pool_is_refused_by_id_distinct_from_the_share_reason()
    {
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["garbage-pool-family"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.garbage-pool-family", Name: "Garbage", KindId: "stat.derived",
            Channel: "combat.made-up-thing.{variant}", Op: "Flat", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 100);

        Assert.Empty(result.Rows);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("atom.garbage-pool-family", refusal.FamilyId);
        Assert.Contains("no matching E30 channel pool", refusal.Reason, StringComparison.Ordinal);
    }

    // ---- test 8: no generated output is named fx-* ---------------------------------------------------

    [Fact]
    public void No_generated_output_file_name_ever_begins_with_fx_dash()
    {
        var (families, _) = LoadReal();

        foreach (var sourceFile in families.Select(f => f.SourceFile).Distinct())
        {
            var stem = Path.GetFileNameWithoutExtension(sourceFile);
            var outName = $"family-expand.{stem}.json";
            Assert.False(outName.StartsWith("fx-", StringComparison.OrdinalIgnoreCase), outName);
        }

        var generatedDir = Path.Combine(FindDataDir(), "seed", "atoms", "generated");
        if (Directory.Exists(generatedDir))
            foreach (var file in Directory.GetFiles(generatedDir))
                Assert.False(Path.GetFileName(file).StartsWith("fx-", StringComparison.OrdinalIgnoreCase), file);
    }

    // ---- test 10: the 21 pre-existing shipped atoms are untouched — additive only -------------------

    [Fact]
    public void Generated_atom_ids_never_collide_with_the_shipped_fx_star_atom_ids()
    {
        var atomsDir = Path.Combine(FindDataDir(), "seed", "atoms");
        var shippedFiles = new[] { "fx-board.json", "fx-core.json", "fx-status.json" }
            .Select(name => Path.Combine(atomsDir, name))
            .Where(File.Exists)
            .Select(f => (f, File.ReadAllText(f)));

        var collected = AtomSeedFile.Collect(shippedFiles);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        var shippedIds = collected.Content.Atoms.Select(a => a.AtomId).ToHashSet(StringComparer.Ordinal);
        // spec-family-expand.md §5 test 10 names 21; the real corpus measures 20 (verified here rather
        // than trusted from the doc — DESIGN-GATE discipline). The count itself is not this test's
        // point, and is never pinned (population-pin SE3.3, 2026-09-19); disjointness from E43's own
        // generated ids, below, is.
        Assert.NotEmpty(shippedIds);

        var result = ExpandReal();
        foreach (var row in result.Rows)
            Assert.DoesNotContain(row.AtomId, shippedIds);
    }

    // ---------------------------------------------------------------------------------------------
    // family-tags-closure (D28, 2026-09-07): a family's own authored `tags` now reaches TagsJson
    // alongside provenance, closing the gap `AffixFamilyFile.cs`'s own doc comment named as item's to
    // pick up. AffixTags.ParseTags/EligibilityRule already read whatever keys exist — nothing there
    // changes; these tests prove the STAMP, and one proves the shipped GATE now actually gates.
    // ---------------------------------------------------------------------------------------------

    static IReadOnlyDictionary<string, string> ParseTagsJson(string? json)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return tags;
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String)
                tags[prop.Name] = prop.Value.GetString()!;
        return tags;
    }

    [Fact]
    public void MilestoneFamilyFile_Read_uses_runtimeFamily_as_the_family_never_the_ledger_id()
    {
        const string json = """
        {
          "entries": [
            {
              "id": "enh.001", "name": "Enhancement Vigor", "runtimeFamily": "atom.enhance-vigor",
              "kindId": "stat.modify", "powerBand": "medium",
              "params": { "channel": "maxHp", "op": "Flat" }, "tags": ["defensive"]
            }
          ]
        }
        """;

        var family = Assert.Single(MilestoneFamilyFile.Read("milestones.json", json));
        Assert.Equal("atom.enhance-vigor", family.Id);
        Assert.Equal("Enhancement Vigor", family.Name);
        Assert.Equal("maxHp", family.Channel);
        Assert.Equal("Flat", family.Op);
        Assert.Equal(new[] { "defensive" }, family.Tags);
        Assert.Equal("milestones.json", family.SourceFile);
    }

    [Fact]
    public void MilestoneFamilyFile_Read_refuses_a_missing_runtimeFamily_rather_than_falling_back_to_id()
    {
        const string json = """
        {
          "entries": [
            { "id": "enh.001", "name": "No Stem", "kindId": "stat.modify", "powerBand": "medium" }
          ]
        }
        """;

        var ex = Assert.Throws<FormatException>(() => MilestoneFamilyFile.Read("milestones.json", json));
        Assert.Contains("runtimeFamily", ex.Message);
    }

    [Fact]
    public void MilestoneFamilyFile_Read_reads_the_real_milestone_corpus_with_runtimeFamily_identities()
    {
        // Join-shaped, not a count: every emitted row's family must be a real milestone
        // runtimeFamily, and every milestone entry must parse — the corpus grows, the closure holds.
        var json = File.ReadAllText(Path.Combine(FindDataDir(), "seed", "items", "enhancement-milestones", "milestones.json"));
        var families = MilestoneFamilyFile.Read("milestones.json", json);
        Assert.All(families, f => Assert.StartsWith("atom.enhance-", f.Id, StringComparison.Ordinal));
        var known = families.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var emittedPath = Path.Combine(FindDataDir(), "seed", "atoms", "generated", "family-expand.milestones.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(emittedPath));
        foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            Assert.Contains(entry.GetProperty("family").GetString()!, known);
    }

    [Fact]
    public void Expansion_refuses_a_scalar_stat_modify_channel_outside_the_registry_vocabulary()
    {
        // Found live on atom.enhance-quicken (actionSpeed): the Increased op slips past the
        // Flat-only reference-base check, and without this refusal the generator emits rows the
        // importer rejects as BadParamValue — breaking first boot. Widening StatChannels.All is
        // the atom program's call, never the generator's.
        var (families, tierBands) = LoadReal();
        var withWeight = tierBands with
        {
            ChannelWeightPermille = new Dictionary<string, long>(tierBands.ChannelWeightPermille, StringComparer.Ordinal)
                { ["test-quicken"] = 1000 },
        };
        var quicken = new FamilyEntryInput(
            "atom.test-quicken", "Test Quicken", "stat.modify", "actionSpeed", "Increased", "low",
            "test.json", Array.Empty<string>());
        var result = FamilyExpansion.Expand(
            families.Append(quicken).ToList(), withWeight, FlatReferenceBase);
        var refusal = Assert.Single(result.Refusals, r => r.FamilyId == "atom.test-quicken");
        Assert.Contains("actionSpeed", refusal.Reason);
        Assert.DoesNotContain(result.Rows, r => r.FamilyId == "atom.test-quicken");
    }

    [Fact]
    public void AffixFamilyFile_Read_parses_a_real_tags_array_in_authored_order()
    {
        const string json = """
        {
          "entries": [
            {
              "id": "atom.parser-check", "name": "Parser Check", "kindId": "stat.modify",
              "powerBand": "medium", "params": { "channel": "x", "op": "Flat" },
              "tags": ["offensive", "utility"]
            }
          ]
        }
        """;

        var families = AffixFamilyFile.Read("g-parser-check.json", json);

        var family = Assert.Single(families);
        Assert.Equal(new[] { "offensive", "utility" }, family.Tags);
    }

    [Fact]
    public void AffixFamilyFile_Read_treats_an_absent_tags_key_as_legally_empty()
    {
        const string json = """
        {
          "entries": [
            {
              "id": "atom.no-tags-key", "name": "No Tags", "kindId": "stat.modify",
              "powerBand": "medium", "params": { "channel": "x", "op": "Flat" }
            }
          ]
        }
        """;

        var family = Assert.Single(AffixFamilyFile.Read("g-no-tags.json", json));
        Assert.Empty(family.Tags);
    }

    [Fact]
    public void AffixFamilyFile_Read_refuses_a_non_string_tag_rather_than_dropping_it_silently()
    {
        const string json = """
        {
          "entries": [
            {
              "id": "atom.bad-tag", "name": "Bad Tag", "kindId": "stat.modify",
              "powerBand": "medium", "params": { "channel": "x", "op": "Flat" },
              "tags": [123]
            }
          ]
        }
        """;

        Assert.Throws<FormatException>(() => AffixFamilyFile.Read("g-bad-tag.json", json));
    }

    [Fact]
    public void A_family_with_a_real_tag_stamps_it_alongside_provenance_not_instead_of_it()
    {
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["tagged-stem"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });
        var family = new FamilyEntryInput(
            Id: "atom.tagged-stem", Name: "Tagged", KindId: "stat.modify",
            Channel: "maxHp", Op: "Flat", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: new[] { "offensive" });

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 100);

        Assert.Empty(result.Refusals);
        Assert.NotEmpty(result.Rows);
        foreach (var row in result.Rows)
        {
            var tags = ParseTagsJson(row.TagsJson);
            Assert.True(tags.ContainsKey("offensive"));
            Assert.Equal("synthetic.json", tags["generatedFrom"]);
            Assert.Equal("E43", tags["generator"]);
        }
    }

    [Fact]
    public void A_family_with_no_tags_emits_only_the_two_provenance_keys_no_phantom_key()
    {
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["untagged-stem"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });
        var family = new FamilyEntryInput(
            Id: "atom.untagged-stem", Name: "Untagged", KindId: "stat.modify",
            Channel: "maxHp", Op: "Flat", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 100);

        Assert.Empty(result.Refusals);
        var tags = ParseTagsJson(Assert.Single(result.Rows.Take(1)).TagsJson);
        Assert.Equal(2, tags.Count);
        Assert.True(tags.ContainsKey("generatedFrom"));
        Assert.True(tags.ContainsKey("generator"));
    }

    [Fact]
    public void A_colon_form_tag_splits_into_a_real_key_value_pair_for_AnyOfTags_even_though_no_real_family_uses_it_yet()
    {
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["kv-stem"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });
        var family = new FamilyEntryInput(
            Id: "atom.kv-stem", Name: "KeyValue", KindId: "stat.modify",
            Channel: "maxHp", Op: "Flat", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: new[] { "element:fire" });

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 100);

        var tags = ParseTagsJson(result.Rows[0].TagsJson);
        Assert.Equal("fire", tags["element"]);
    }

    [Fact]
    public void A_tag_colliding_with_a_reserved_provenance_key_is_refused_not_silently_overwritten()
    {
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["collide-stem"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });
        var family = new FamilyEntryInput(
            Id: "atom.collide-stem", Name: "Collider", KindId: "stat.modify",
            Channel: "maxHp", Op: "Flat", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: new[] { "generator" });

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 100);

        Assert.Empty(result.Rows);
        var refusal = Assert.Single(result.Refusals);
        Assert.Equal("atom.collide-stem", refusal.FamilyId);
        Assert.Contains("generator", refusal.Reason);
    }

    [Fact]
    public void The_real_corpus_offensive_defensive_utility_tags_reach_real_generated_output()
    {
        var (families, tierBands) = LoadReal();
        var withTags = families.Where(f => f.Tags.Count > 0).ToList();
        Assert.NotEmpty(withTags); // proves the parser side is real, not just theoretically wired

        var byId = families.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var result = FamilyExpansion.Expand(families, tierBands, _ => 100);

        var checkedCount = 0;
        foreach (var row in result.Rows)
        {
            if (!byId.TryGetValue(row.FamilyId, out var source) || source.Tags.Count == 0) continue;
            var tags = ParseTagsJson(row.TagsJson);
            foreach (var expected in source.Tags)
                Assert.True(tags.ContainsKey(expected),
                    $"{row.AtomId}: expected real family tag '{expected}' from {source.Id} in TagsJson, got [{string.Join(",", tags.Keys)}]");
            checkedCount++;
        }
        Assert.True(checkedCount > 0, "no tagged real family produced a row to check — corpus or share gate drifted");
    }

    [Fact]
    public void EligibilityRule_RequireTags_now_actually_gates_using_a_real_stamped_family_tag()
    {
        // module 8's own shipped mechanism (EligibilityRule/EligibilityResolver) — proving the fix
        // closes the real, end-to-end gap D28 named, not just that a dictionary key exists.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long>
            {
                ["offense-gate-stem"] = 1000,
                ["utility-gate-stem"] = 1000,
            },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });
        var offensive = new FamilyEntryInput(
            Id: "atom.offense-gate-stem", Name: "Offense Gate", KindId: "stat.modify",
            Channel: "maxHp", Op: "Flat", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: new[] { "offensive" });
        var utility = new FamilyEntryInput(
            Id: "atom.utility-gate-stem", Name: "Utility Gate", KindId: "stat.modify",
            Channel: "maxHp", Op: "Flat", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: new[] { "utility" });

        var result = FamilyExpansion.Expand(new[] { offensive, utility }, tierBands, _ => 100);
        Assert.Empty(result.Refusals);

        var offensiveAtom = result.Rows.First(r => r.FamilyId == "atom.offense-gate-stem");
        var utilityAtom = result.Rows.First(r => r.FamilyId == "atom.utility-gate-stem");

        var rule = new EligibilityRule(
            RequireTags: new[] { "offensive" }, AnyOfTags: Array.Empty<string>(),
            Allow: Array.Empty<string>(), Deny: Array.Empty<string>());

        var offensiveTags = ParseTagsJson(offensiveAtom.TagsJson);
        var utilityTags = ParseTagsJson(utilityAtom.TagsJson);

        Assert.True(EligibilityResolver.IsEligible("affix.offense", offensiveTags, rule));
        Assert.False(EligibilityResolver.IsEligible("affix.utility", utilityTags, rule));
    }

    // ---- R9: a verb op is carried verbatim, never read as a modifier -------------------------------

    [Fact]
    public void A_verb_op_on_a_non_modifier_kind_expands_with_the_verb_verbatim_never_looked_up()
    {
        // P2.2 (R9): board.action's `cherry` is the family's own opcode, not a modifier — the
        // opWeight table must never see it, and the emitted row must carry it exactly as authored.
        // The stubbed curve stands in for the BattleRuleset curve P3.1 publishes; the point here
        // is the field separation, not the number.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["verb-cherry"] = 1000 },
            OpWeightPermille: new Dictionary<string, long>()); // deliberately empty: a lookup would fail

        var family = new FamilyEntryInput(
            Id: "atom.verb-cherry", Name: "Verb Cherry", KindId: "board.action",
            Channel: "verb-channel", Op: "cherry", PowerBand: "high", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 100);

        Assert.Empty(result.Refusals);
        Assert.Equal(FamilyExpansion.TierCount, result.Rows.Count);
        foreach (var row in result.Rows)
        {
            using var doc = JsonDocument.Parse(row.ParamsJson);
            Assert.Equal("cherry", doc.RootElement.GetProperty("op").GetString());
        }
    }

    [Fact]
    public void A_modifier_op_on_a_stat_kind_still_guards_through_opWeight()
    {
        // The other half of the separation: stat.modify owns a real modifier op, so an unknown
        // one still refuses at the weight table — P2.2 narrows the gate, never removes it.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["mod-guard"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.mod-guard", Name: "Mod Guard", KindId: "stat.modify",
            Channel: "atk", Op: "cherry", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 100);

        Assert.Empty(result.Rows);
        var refusal = Assert.Single(result.Refusals);
        Assert.Contains("no opWeightPermille entry for op 'cherry'", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_structural_op_is_refused_by_principle_never_as_a_table_miss()
    {
        // EX-2: Replace/Flag on a stat kind name the ladder verdict (constant vs f(Theta)),
        // so the refusal teaches instead of implying a missing weight row.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["mod-struct"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.mod-struct", Name: "Mod Structural", KindId: "stat.derived",
            Channel: "atk", Op: "Replace", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 100);

        Assert.Empty(result.Rows);
        var refusal = Assert.Single(result.Refusals);
        Assert.Contains("excluded by principle", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("re-author as Flat", refusal.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no opWeightPermille entry", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_verb_kind_without_a_curve_is_refused_naming_the_curve_never_the_verb()
    {
        // Real shape of the 11 shipped verb families today (channel-less, no curve yet): the
        // refusal must name the missing game-units curve (P3.1's work), not misreport the verb
        // as an unknown modifier op. Proves the old reason is gone, not just reworded.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["verb-nocurve"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.verb-nocurve", Name: "Verb No Curve", KindId: "resource.economy",
            Channel: "", Op: "add", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => null);

        Assert.Empty(result.Rows);
        var refusal = Assert.Single(result.Refusals);
        Assert.Contains("no referenceBaseGameUnits", refusal.Reason, StringComparison.Ordinal);
        Assert.Contains("verb op 'add'", refusal.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("opWeightPermille", refusal.Reason, StringComparison.Ordinal);
    }
    [Fact]
    public void A_non_modifier_kind_with_no_op_names_the_absence_never_a_verb()
    {
        // status.clear owns no op param at all — an empty op on such a kind is an absence, and
        // the refusal must say so rather than quote an empty verb. (status.apply no longer
        // reaches this message: it resolves from the anchor first, or refuses naming it.)
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["noop-kind"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.noop-kind", Name: "No Op", KindId: "status.clear",
            Channel: "", Op: null, PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => null);

        Assert.Empty(result.Rows);
        var refusal = Assert.Single(result.Refusals);
        Assert.Contains("carries no op", refusal.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("verb op", refusal.Reason, StringComparison.Ordinal);
    }

    // ---- SA-1: status.apply resolves from the anchor, never a curve -------------------------------

    static Func<string, StatusAnchorRow?> StubAnchor() =>
        id => id == "atom.stub-apply"
            ? new StatusAnchorRow(id, ChanceT1Permille: 25, DurationT1Ms: 2000, StatusId: "blight",
                ChanceRatioPermille: 1750, DurationRatioPermille: 1400, TriggerId: "OnDamageDealt")
            : null;

    [Fact]
    public void A_status_apply_family_expands_from_the_anchor_with_scalar_duration_and_chance()
    {
        // SA-1: channel-less status.apply families price from authored t1s (status-anchor.v1.json),
        // not a curve. Duration rides the 1.40 ladder as a scalar (hand-authored fx-status.json
        // precedent); chance rides the 1.75 ladder in WhenJson (the carrier the compiler already
        // reads). No amount band, no op: the kind owns no modifier op.
        //
        // ⚠️ This used to assert the emitted duration as a DOUBLE (2.0, and 7.683 to 3 places),
        // which pinned the defect rather than the contract: `duration` is a ParamKind.Value and
        // AtomJson refuses a non-integer magnitude, so every tier this test declared correct was in
        // fact rejected at import and took the whole seed tree down with it. Restated as the
        // contract the validator actually enforces — a whole number of seconds, and the trigger
        // status.apply requires — rather than re-pointed at new numbers.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["stub-apply"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.stub-apply", Name: "Stub Apply", KindId: "status.apply",
            Channel: "", Op: null, PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => null, StubAnchor());

        Assert.Empty(result.Refusals);
        Assert.Equal(FamilyExpansion.TierCount, result.Rows.Count);
        var t1 = result.Rows.Single(r => r.Tier == 1);
        using (var doc = JsonDocument.Parse(t1.ParamsJson))
        {
            Assert.Equal("blight", doc.RootElement.GetProperty("status").GetString());
            // A whole number of seconds, and an INTEGER in the JSON — GetInt64 throws on 2.0.
            Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("duration").ValueKind);
            Assert.Equal(2, doc.RootElement.GetProperty("duration").GetInt64()); // 2000 ms -> 2 s
            Assert.Equal(1, doc.RootElement.GetProperty("level").GetInt32());
            Assert.False(doc.RootElement.TryGetProperty("op", out _), "status.apply rows carry no op");
            Assert.False(doc.RootElement.TryGetProperty("amount", out _), "status.apply rows carry no amount band");
        }
        using (var when = JsonDocument.Parse(t1.WhenJson))
        {
            Assert.Equal(25, when.RootElement.GetProperty("chance").GetInt64());
            // status.apply REQUIRES a trigger; the anchor authors it, the expander never invents one.
            Assert.Equal("OnDamageDealt", when.RootElement.GetProperty("trigger").GetString());
        }
        var t5 = result.Rows.Single(r => r.Tier == 5);
        using (var doc5 = JsonDocument.Parse(t5.ParamsJson))
        {
            Assert.Equal(8, doc5.RootElement.GetProperty("duration").GetInt64()); // 7683 ms -> 8 s
        }
        using (var when5 = JsonDocument.Parse(t5.WhenJson))
        {
            Assert.Equal(236, when5.RootElement.GetProperty("chance").GetInt64()); // stepwise 25>44>77>135>236
        }
    }

    [Fact]
    public void A_status_apply_family_without_an_anchor_row_refuses_naming_the_mapping()
    {
        // The family-to-status mapping is authored, never inferred: an unmapped family refuses
        // naming the anchor file, not a curve and not a verb table.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["stub-unmapped"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.stub-unmapped", Name: "Stub Unmapped", KindId: "status.apply",
            Channel: "", Op: null, PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => null, StubAnchor());

        Assert.Empty(result.Rows);
        var refusal = Assert.Single(result.Refusals);
        Assert.Contains("no status-anchor row", refusal.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_anchor_file_parses_defaults_overrides_and_mapping()
    {        const string json = """
            {
              "defaults": { "chanceT1Permille": 25, "durationT1Ms": 2000, "triggerId": "OnDamageDealt" },
              "ratios": { "chanceRatioPermille": 1750, "durationRatioPermille": 1400 },
              "overrides": { "atom.x": { "chanceT1Permille": 50 } },
              "familyStatus": { "atom.x": "blight", "atom.y": "wither" }
            }
            """;
        var rows = StatusAnchorFile.Read(json);
        Assert.Equal(new StatusAnchorRow("atom.x", 50, 2000, "blight", 1750, 1400, "OnDamageDealt"), rows["atom.x"]);
        Assert.Equal(new StatusAnchorRow("atom.y", 25, 2000, "wither", 1750, 1400, "OnDamageDealt"), rows["atom.y"]);
        // A missing triggerId is a load rejection naming it (T5), never a silent default — without a
        // trigger every status.apply row this file feeds is refused by AtomRowValidator.
        Assert.Throws<FormatException>(() => StatusAnchorFile.Read("""
            {
              "defaults": { "chanceT1Permille": 25, "durationT1Ms": 2000 },
              "ratios": { "chanceRatioPermille": 1750, "durationRatioPermille": 1400 },
              "familyStatus": { "atom.x": "blight" }
            }
            """));
        Assert.Throws<FormatException>(() => StatusAnchorFile.Read("""{"defaults": {}}"""));
        Assert.Throws<FormatException>(() => StatusAnchorFile.Read(
            """{"defaults": {"chanceT1Permille": 25, "durationT1Ms": 2000}, "familyStatus": {"atom.x": "blight"}}"""));
    }

    [Fact]
    public void Seed_file_omits_when_for_plain_rows_and_keeps_it_for_status_rows()
    {        // The serializer arm: existing rows byte-untouched (no "when" key), status rows persist
        // their grant chance through the file the binder actually reads.
        var plain = new AtomRow
        {
            AtomId = "atom.plain.t1", KindId = "stat.modify", FamilyId = "atom.plain",
            Variant = "", Tier = 1, Name = "Plain T1", WhenJson = "{}",
            ParamsJson = """{"channel":"atk","op":"flat"}""", TagsJson = "{}", Enabled = true,
        };
        var status = plain with
        {
            AtomId = "atom.status.t1", KindId = "status.apply", FamilyId = "atom.status",
            Name = "Status T1", WhenJson = """{"chance":25}""",
        };
        var text = FamilyExpansionSeedFile.ToCanonicalJson(new[] { status, plain });
        using var doc = JsonDocument.Parse(text);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Equal(25, entries[1].GetProperty("when").GetProperty("chance").GetInt32());
        Assert.False(entries[0].TryGetProperty("when", out _));
    }

    // ---- IL-1: interval channels price against ms references --------------------------------------

    [Fact]
    public void A_flat_family_on_an_interval_channel_expands_against_its_ms_reference()
    {
        // IL-1: attackInterval/produceInterval are Milliseconds (ledger + ChannelUnits.cs:28-29).
        // E43 prices the magnitude only; the inversion (LowerIsBetter, ModifierOp) stays
        // downstream's job. Hand-check: share 35 x base 1500 / 1000 = 53 (m1), band 670/1330.
        var tierBands = new TierBandsInput(
            BaseSharePermille: 35,
            ChannelWeightPermille: new Dictionary<string, long> { ["stub-tempo"] = 1000 },
            OpWeightPermille: new Dictionary<string, long> { ["Flat"] = 1000 });

        var family = new FamilyEntryInput(
            Id: "atom.stub-tempo", Name: "Stub Tempo", KindId: "stat.modify",
            Channel: "attackInterval", Op: "Flat", PowerBand: "medium", SourceFile: "synthetic.json",
            Tags: Array.Empty<string>());

        var result = FamilyExpansion.Expand(new[] { family }, tierBands, _ => 1500);

        Assert.Empty(result.Refusals);
        var t1 = result.Rows.Single(r => r.Tier == 1);
        using var doc = JsonDocument.Parse(t1.ParamsJson);
        Assert.Equal("attackInterval", doc.RootElement.GetProperty("channel").GetString());
        Assert.Equal(36, doc.RootElement.GetProperty("amount").GetProperty("min").GetInt64());
        Assert.Equal(70, doc.RootElement.GetProperty("amount").GetProperty("max").GetInt64());
    }
}
