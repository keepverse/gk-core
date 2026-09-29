using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Sockets;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// spec-sockets.md §3/§5 against the REAL shipped <c>gk-core/data/tuning/sockets.v2.json</c> (named by
/// <see cref="SocketTuningFiles.Current"/>) and the real 740-entry base-type corpus — not a synthetic
/// fixture. A ceiling table that agreed with a fixture and disagreed with the corpus would be exactly
/// the defect module 6 already hit once.
/// </summary>
public class SocketGeometryTests
{
    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    static string TuningPath => Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current);

    internal static SocketTuning Shipped() => SocketTuning.Parse(File.ReadAllText(TuningPath));

    internal static string Raw() => File.ReadAllText(TuningPath);

    // ── §3, the re-issued fifteen-role table ────────────────────────────────────────────────────

    [Fact]
    public void Socket_max_is_defined_for_all_fifteen_roles()
    {
        var tuning = Shipped();
        // Formula, not a literal (population-pin SE3.3, 2026-09-19): every ItemRole except Standard,
        // same shape SlotRolesTests proves for the same closed enum.
        Assert.Equal(Enum.GetValues<ItemRole>().Length - 1, tuning.SocketCeiling.Count);

        foreach (ItemRole role in Enum.GetValues(typeof(ItemRole)))
        {
            if (role == ItemRole.Standard) continue;
            Assert.True(tuning.SocketCeiling.ContainsKey(role), $"no ceiling for '{ItemRoles.Id(role)}'");
        }

        // ⭐ The three §3 added to the stale twelve-id table, named individually so a regression to
        // the old table is a specific failure rather than a count mismatch. Values are v2's doubled
        // ceilings (SSH5.10).
        Assert.Equal(6, tuning.CeilingFor(ItemRole.WardArray));
        Assert.Equal(4, tuning.CeilingFor(ItemRole.Infusion));
        Assert.Equal(4, tuning.CeilingFor(ItemRole.Retinue));
    }

    [Fact]
    public void No_socket_max_row_exists_for_commander_standard()
    {
        Assert.False(Shipped().SocketCeiling.ContainsKey(ItemRole.Standard));
        Assert.Equal(0, Shipped().CeilingFor(ItemRole.Standard));

        // D14 — out of scope, not silently included. A row (even a zero one) reads as "in scope".
        var node = System.Text.Json.Nodes.JsonNode.Parse(Raw())!.AsObject();
        node["socketCeiling"]!["standard"] = 2;
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(node.ToJsonString()));
        Assert.Contains("D14", ex.Message);
    }

    [Fact]
    public void The_old_twelve_suffixed_role_ids_appear_nowhere()
    {
        var raw = File.ReadAllText(TuningPath);
        foreach (var stale in new[]
                 {
                     "core-protective", "sense-utility", "mantle-utility",
                     "manipulator-offense", "girdle-resource", "head-protective",
                 })
            Assert.DoesNotContain(stale, raw, StringComparison.Ordinal);
    }

    [Fact]
    public void A_ceiling_above_the_structural_maximum_throws_rather_than_clamping()
    {
        var raised = File.ReadAllText(TuningPath).Replace("\"armament-primary\": 8", "\"armament-primary\": 10");
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(raised));
        Assert.Contains("THROWS rather than clamping", ex.Message);
        Assert.Equal(8, SocketLimits.SocketMaxCeiling);
    }

    [Fact]
    public void The_structural_ceiling_in_the_file_and_in_code_cannot_drift_apart()
    {
        var moved = File.ReadAllText(TuningPath).Replace("\"structuralCeiling\": 8", "\"structuralCeiling\": 9");
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(moved));
        Assert.Contains("SocketLimits.SocketMaxCeiling", ex.Message);
    }

    [Fact]
    public void The_structural_ceiling_says_it_is_structural_and_names_why()
    {
        // AGENTS.md requires a structural limit to say so in the artefact a balance pass edits, not
        // only in code. Asserting the words is what stops the note being deleted in a tidy-up.
        using var doc = JsonDocument.Parse(File.ReadAllText(TuningPath));
        var note = doc.RootElement.GetProperty("structuralCeilingNote").GetString()!;
        Assert.Contains("STRUCTURAL", note, StringComparison.Ordinal);
        Assert.Contains("LEGIBILITY", note, StringComparison.Ordinal);
        Assert.Contains("contentScale", note, StringComparison.Ordinal);
    }

    // ── §3's second property, corrected against the corpus ──────────────────────────────────────

    [Fact]
    public void Socket_max_is_a_role_ceiling_and_a_base_type_may_vary_beneath_it()
    {
        // ⛔ spec-sockets.md §3 asks for `socket_max_is_fixed_per_role_and_never_varies_by_base_type`.
        // That test is UNWRITABLE against the shipped corpus: `armament-primary` is
        // {0:10, 1:10, 2:10, 3:10, 4:8} (re-measured 2026-09-06; module 6's first pass read
        // {0:18, 1:26, 2:4} and was superseded when it re-issued the table on 2026-09-04). The
        // enforceable invariant — and the one that actually defends §8.1 — is
        // "never EXCEEDS its role's ceiling", which is what SocketGeometry.ValidateEntry checks.
        var tuning = Shipped();

        Assert.True(SocketGeometry.ValidateEntry(ItemRole.ArmamentPrimary, 0, tuning).IsOk);
        Assert.True(SocketGeometry.ValidateEntry(ItemRole.ArmamentPrimary, 2, tuning).IsOk);
        Assert.True(SocketGeometry.ValidateEntry(ItemRole.ArmamentPrimary, 4, tuning).IsOk);
        Assert.True(SocketGeometry.ValidateEntry(ItemRole.ArmamentPrimary, 8, tuning).IsOk);

        var over = SocketGeometry.ValidateEntry(ItemRole.JewelMajor, 3, tuning);   // v2 ceiling is 2
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, over.Reason);
        Assert.Contains(SocketRules.EntryExceedsRoleCeiling, over.Detail);
    }

    [Fact]
    public void The_shipped_corpus_never_exceeds_a_role_ceiling()
    {
        var tuning = Shipped();
        var root = Path.Combine(RepoRoot(), "data", "seed", "items", "base-types");
        var checkedCount = 0;

        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("entries", out var entries)) continue;

            foreach (var e in entries.EnumerateArray())
            {
                if (e.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
                if (!e.TryGetProperty("role", out var roleEl) || !ItemRoles.TryParse(roleEl.GetString(), out var role))
                    continue;
                if (role == ItemRole.Standard) continue; // D14, retired rows
                if (!e.TryGetProperty("socketMax", out var smEl) || smEl.ValueKind != JsonValueKind.Number) continue;

                checkedCount++;
                var rejection = SocketGeometry.ValidateEntry(role, smEl.GetInt32(), tuning);
                Assert.True(rejection.IsOk,
                    $"{e.GetProperty("id").GetString()}: {rejection.Detail}");
            }
        }

        // CONTRACT, not a count: the base-type corpus is generator-authored and grows every generation
        // (721 → 1158 and climbing), so the checked total is stale by construction. What matters is
        // that the sweep actually READ the live socketMax-carrying corpus (not zero, not a stub) and
        // that every entry it read passes its role ceiling.
        Assert.True(checkedCount > 600, $"only {checkedCount} live entries checked — the corpus read is wrong");
    }

    [Fact]
    public void A_four_ingredient_combo_fits_every_role_whose_ceiling_reaches_four()
    {
        // SSH5.10: v2 doubles the ceilings, so the host set is LARGER than v1's pair — and it is
        // DERIVED, never named. The equality is the contract: the matcher's set is exactly the
        // tuning's own derivation over the loaded ceilings.
        var tuning = Shipped();
        Assert.Equal(4, tuning.StrainSpliceIngredientCount);
        var expected = tuning.SocketCeiling
            .Where(kv => kv.Value >= tuning.StrainSpliceIngredientCount)
            .Select(kv => kv.Key)
            .OrderBy(r => (int)r)
            .ToList();
        var actual = SocketGeometry.RolesThatCanHostAStrain(tuning).OrderBy(r => (int)r).ToList();
        Assert.Equal(expected, actual);
        Assert.True(actual.Count > 2, "v2's doubled ceilings lift more than the v1 pair to four");
    }

    // ── §5, where sockets come from ─────────────────────────────────────────────────────────────

    [Fact]
    public void Every_rung_carries_a_grant_window_and_adjacent_windows_overlap()
    {
        var tuning = Shipped();
        Assert.Equal(RarityLadder.RungIds.Count, tuning.RarityGrant.Count);

        for (var i = 1; i < RarityLadder.RungIds.Count; i++)
        {
            var low = tuning.RarityGrant[RarityLadder.RungIds[i - 1]];
            var high = tuning.RarityGrant[RarityLadder.RungIds[i]];
            Assert.True(high.Min >= low.Min && high.Max >= low.Max, "grant windows must be monotonic");
            Assert.True(high.Min <= low.Max, "OD4: adjacent windows must overlap");
        }

        // The overlap is not decorative: a mid-band item CAN out-socket the band above it.
        Assert.True(tuning.RarityGrant["heirloom"].Max > tuning.RarityGrant["sunwoven"].Min);
    }

    [Fact]
    public void A_resonance_threshold_above_one_circuit_is_refused_at_load()
    {
        // strain-splice-host SSH5.8 (circuit-topology §2): the bound is the CIRCUIT width, not the
        // structural ceiling — once the ceiling is 8, a threshold of 5 would be admitted by the old
        // check and name a shape no single circuit could ever hold.
        var node = System.Text.Json.Nodes.JsonNode.Parse(Raw())!.AsObject();
        node["resonance"]!["pureThresholds"] = new System.Text.Json.Nodes.JsonArray(2, 3, 5);
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(node.ToJsonString()));
        Assert.Contains(SocketLimits.SocketCircuitSize.ToString(), ex.Message);
    }

    [Fact]
    public void An_ingredient_count_that_is_not_the_circuit_size_is_refused_at_load()
    {
        // circuit-topology §2: a Strain consumes exactly one complete circuit, so the two numbers are
        // the same by construction.
        var shortRecipe = File.ReadAllText(TuningPath).Replace("\"ingredientCount\": 4", "\"ingredientCount\": 3");
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(shortRecipe));
        Assert.Contains("SocketCircuitSize", ex.Message);
    }

    [Fact]
    public void A_non_overlapping_grant_table_is_refused_at_load()
    {
        // A gap on the ladder while the higher rung still grants at least as much: monotonic, but
        // not overlapping — the OD4 failure the parser refuses by name.
        var node = System.Text.Json.Nodes.JsonNode.Parse(Raw())!.AsObject();
        var top = node["rarityGrant"]!["sunwoven"]!.AsObject();
        top["socketMin"] = 7;   // firstseed's own max is 6 in v2
        top["socketMax"] = 8;
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(node.ToJsonString()));
        Assert.Contains("do not overlap", ex.Message);
    }

    [Fact]
    public void Sockets_at_drop_never_exceeds_the_base_types_own_socket_max()
    {
        var tuning = Shipped();
        for (ulong seed = 1; seed <= 500; seed++)
        foreach (var rung in RarityLadder.RungIds)
        foreach (var entryMax in new[] { 0, 1, 2, 3, 4 })
        {
            var n = SocketGeometry.SocketsAtDrop(entryMax, rung, seed, tuning, "ordinary");
            Assert.InRange(n, 0, entryMax);
            Assert.True(n <= tuning.RarityGrant[rung].Max);
        }
    }

    [Fact]
    public void Sockets_at_drop_is_reproducible_and_uses_the_owned_prng()
    {
        var tuning = Shipped();
        var a = SocketGeometry.SocketsAtDrop(4, "almanac", 8812349UL, tuning, "ordinary");
        var b = SocketGeometry.SocketsAtDrop(4, "almanac", 8812349UL, tuning, "ordinary");
        Assert.Equal(a, b);

        // The draw is the LootStreams.Sockets stream off the roll seed, spelled once — reproduce it
        // here from SeededRng directly so a change to either side is a red test rather than a drift.
        var window = tuning.RarityGrant["almanac"];
        var rng = SeededRng.DeriveStream(8812349UL, LootStreams.Sockets);
        Assert.Equal(window.Min + rng.NextInt(window.Max - window.Min + 1), a);
    }

    [Fact]
    public void Socket_seed_is_domain_separated_from_the_affix_pool_stream()
    {
        // Adding a socket later cannot move an item's affixes: the socket stream is derived from the
        // instance's own roll_seed under its own name and shares no state with any item.* stream.
        Assert.Equal("item.socket", LootStreams.Sockets);
        Assert.NotEqual(LootStreams.Sockets, LootStreams.Rolls(0));
        Assert.NotEqual(LootStreams.Sockets, LootStreams.Rarity(0));

        var socket = SeededRng.DeriveStream(4242UL, LootStreams.Sockets);
        var rolls = SeededRng.DeriveStream(4242UL, LootStreams.Rolls(0));
        Assert.NotEqual(socket.NextULong(), rolls.NextULong());
    }

    [Fact]
    public void The_grant_windows_span_the_whole_zero_to_ceiling_range_across_the_ladder()
    {
        var tuning = Shipped();
        Assert.Equal(0, tuning.RarityGrant[RarityLadder.RungIds[0]].Max);
        Assert.Equal(
            SocketLimits.SocketMaxCeiling,
            tuning.RarityGrant[RarityLadder.RungIds[^1]].Max);
    }

    // ── circuit-topology v2 (SSH5.10) ────────────────────────────────────────────────────────

    [Fact]
    public void Sockets_zero_to_eight_are_capacity_for_every_role_ceiling()
    {
        // SSH5.10: the structural ceiling is 8, every in-scope role's ceiling is inside [0, 8], and
        // at least one role reaches the top — otherwise the doubled table would not have landed.
        var tuning = Shipped();
        Assert.Equal(8, SocketLimits.SocketMaxCeiling);
        Assert.All(tuning.SocketCeiling.Values, c => Assert.InRange(c, 0, SocketLimits.SocketMaxCeiling));
        Assert.Equal(SocketLimits.SocketMaxCeiling, tuning.SocketCeiling.Values.Max());
    }

    [Fact]
    public void Head_guard_ceiling_is_read_from_tuning_only()
    {
        // R11 / SSH5.10: the helm's v2 row is 4 — exactly one complete circuit — and it joins the
        // host set by DERIVATION. No role is named in code; this test reads the shipped row.
        var tuning = Shipped();
        Assert.Equal(4, tuning.SocketCeiling[ItemRole.HeadGuard]);
        Assert.Contains(ItemRole.HeadGuard, SocketGeometry.RolesThatCanHostAStrain(tuning));
        // …and the ordinary role's doubled row is read the same way.
        Assert.Equal(8, tuning.SocketCeiling[ItemRole.ArmamentPrimary]);
    }

    [Fact]
    public void A_four_socket_helm_has_one_complete_circuit_and_no_remainder()
    {
        // 4 sockets / SocketCircuitSize(4) = exactly one circuit with no remainder, so a
        // four-ingredient Strain fits the helm whole. Proven through the real evaluator.
        var helm = new SocketHost("item.helm", ItemRole.HeadGuard, "humanoid", 4, Capacity: 4);
        var strain = new ComboRecipe(
            "combo.strain-helm-test", ComboShape.Strain, "", 0, "", "", 4, 1,
            new[] { new ComboIngredient("atom.elemental-power", 4) }, BaseFloors: new[] { 1, 1, 1, 1 });
        var fill = Enumerable.Range(0, 4)
            .Select(i => new SocketFill(i, "", new InsertDef(
                "gem.fire-shard.t3", "atom.elemental-power", "fire", 3)))
            .ToList();

        var identity = Assert.Single(
            CombinationEvaluator.Evaluate(helm, fill, new[] { strain }, Shipped()),
            r => r.Shape == ComboShape.Strain);
        Assert.Equal(0, identity.Circuit);
        Assert.Equal(4 / SocketLimits.SocketCircuitSize, 1);
    }

    // ── §5's crafting layer (D23) ───────────────────────────────────────────────────────────────

    [Fact]
    public void Crafting_tops_the_count_up_and_stops_at_the_base_types_own_max()
    {
        Assert.Equal(3, SocketGeometry.SocketsNow(socketsAtDrop: 1, socketAddOperations: 2, entrySocketMax: 4));
        Assert.Equal(4, SocketGeometry.SocketsNow(1, 9, 4));
        // D23: available at EVERY rarity — a chaff item that rolled 0 can still be bored to its cap.
        Assert.Equal(2, SocketGeometry.SocketsNow(0, 2, 2));
    }

    [Fact]
    public void A_negative_count_is_refused_rather_than_absorbed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SocketGeometry.SocketsNow(1, -1, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => SocketGeometry.SocketsAtDrop(-1, "chaff", 1UL, Shipped(), "ordinary"));
    }

    // ── §10, removal ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Removal_is_tiered_and_the_item_always_survives()
    {
        var tuning = Shipped();
        Assert.Equal(SocketRemovalOutcome.Free, tuning.RemovalFor(1));
        Assert.Equal(SocketRemovalOutcome.Free, tuning.RemovalFor(2));
        Assert.Equal(SocketRemovalOutcome.Costed, tuning.RemovalFor(3));
        Assert.Equal(SocketRemovalOutcome.DestroysInsert, tuning.RemovalFor(4));
        Assert.Equal(SocketRemovalOutcome.DestroysInsert, tuning.RemovalFor(5));

        // There is deliberately no DestroysItem outcome — the whole enum is three values, and the
        // absence is the design: "you can always empty a socket; what varies is what you keep".
        Assert.Equal(3, Enum.GetValues<SocketRemovalOutcome>().Length);
    }

    [Fact]
    public void A_removal_table_with_no_commitment_tier_is_refused_at_load()
    {
        var toothless = File.ReadAllText(TuningPath).Replace("\"costedThroughTier\": 3", "\"costedThroughTier\": 5");
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(toothless));
        Assert.Contains("unreachable", ex.Message);
    }

    // ── Tuning hygiene ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_current_socket_revision_carries_no_combination_cap()
    {
        // R12 (strain-splice-host SSH5.10): v2 REMOVES maxCombosPerActor and its note; v1 keeps them
        // as history. An unknown key is ignored, so a fixture that adds the key still parses.
        Assert.DoesNotContain("maxCombosPerActor", Raw(), StringComparison.Ordinal);
        // The constant names a real, parseable file — asserted without hardcoding the revision here,
        // because a literal in this test would make it the very thing the SSH5.2 guard catches.
        Assert.NotNull(SocketTuning.Parse(File.ReadAllText(
            Path.Combine(RepoRoot(), "data", "tuning", SocketTuningFiles.Current))));

        var node = System.Text.Json.Nodes.JsonNode.Parse(Raw())!.AsObject();
        node["maxCombosPerActor"] = 3;
        Assert.NotNull(SocketTuning.Parse(node.ToJsonString()));
    }

    [Fact]
    public void Every_section_is_required_and_none_defaults()
    {
        var raw = File.ReadAllText(TuningPath);
        foreach (var section in new[] { "socketCeiling", "rarityGrant", "socketAllowanceByKind", "insertTiers", "removal", "resonance", "strainSplice" })
        {
            using var doc = JsonDocument.Parse(raw);
            var stripped = doc.RootElement.EnumerateObject()
                .Where(p => p.Name != section)
                .ToDictionary(p => p.Name, p => (object?)JsonSerializer.Deserialize<JsonElement>(p.Value.GetRawText()));
            var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(JsonSerializer.Serialize(stripped)));
            Assert.Contains(section, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Omni_is_never_accepted_as_a_resonance_element()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Raw())!.AsObject();
        node["resonance"]!["eclipsePair"]![0] = "omni";
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(node.ToJsonString()));
        Assert.Contains("omni", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_insert_tier_count_is_a_soft_content_axis_not_a_cap()
    {
        // AGENTS.md: a magnitude ceiling must be configurable. Raising the tier ladder is a file edit
        // and nothing in the module refuses the higher tier.
        var extended = File.ReadAllText(TuningPath).Replace("\"count\": 5", "\"count\": 12");
        var tuning = SocketTuning.Parse(extended);
        Assert.Equal(12, tuning.InsertTierCount);
        Assert.Equal(SocketRemovalOutcome.DestroysInsert, tuning.RemovalFor(12));
    }

    // ── strain-splice-host SSH1.5 (host-gate §4 row 3) ─────────────────────────────────────────────

    [Fact]
    public void No_socket_path_branches_on_head_guard()
    {
        // Ruling 3: whether a role hosts a word is DERIVED from its ceiling, never named. A source
        // scan, not a unit test on behaviour -- the property being proven is the ABSENCE of a branch,
        // which only reading the source can show.
        var socketsDir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Sockets");
        foreach (var file in Directory.EnumerateFiles(socketsDir, "*.cs"))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("HeadGuard", text, StringComparison.Ordinal);
            Assert.DoesNotContain("head-guard", text, StringComparison.Ordinal);
        }

        foreach (var file in new[]
                 {
                     Path.Combine(RepoRoot(), "src", "FusionRpg.Server", "WorkbenchEndpoints.cs"),
                     Path.Combine(RepoRoot(), "src", "FusionRpg.Server", "ItemWorkbench.cs"),
                 })
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("HeadGuard", text, StringComparison.Ordinal);
            Assert.DoesNotContain("head-guard", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_role_hosts_a_word_iff_its_ceiling_reaches_the_ingredient_count()
    {
        // The rule is asserted, never the shipped list: a fixture that moves ONE below-count role's
        // ceiling up to the ingredient count must move it into the derived set, with no code change
        // between the two Parse calls. No role is named.
        var tuning = Shipped();
        var wanted = tuning.StrainSpliceIngredientCount;
        var below = tuning.SocketCeiling.First(kv => kv.Value < wanted).Key;

        var raised = System.Text.Json.Nodes.JsonNode.Parse(Raw())!.AsObject();
        raised["socketCeiling"]![ItemRoles.Id(below)] = wanted;
        var atThreshold = SocketTuning.Parse(raised.ToJsonString());

        Assert.DoesNotContain(below, SocketGeometry.RolesThatCanHostAStrain(tuning));
        Assert.Contains(below, SocketGeometry.RolesThatCanHostAStrain(atThreshold));
    }

    [Fact]
    public void A_helm_host_is_admitted_by_the_one_matcher_at_four_sockets()
    {
        var recipe = new ComboRecipe(
            "combo.ssh15-helm", ComboShape.Strain, "", 0, ItemRoles.Id(ItemRole.HeadGuard), "", 4,
            BaseTier: 1, new[] { new ComboIngredient("atom.elemental-power", 4) });

        var atCeiling = new SocketHost("item.test", ItemRole.HeadGuard, "plant", 0, Capacity: 4);
        var belowCeiling = new SocketHost("item.test", ItemRole.HeadGuard, "plant", 0, Capacity: 3);

        Assert.True(ComboMatcher.CanEverHold(atCeiling, recipe));
        Assert.False(ComboMatcher.CanEverHold(belowCeiling, recipe));
    }

    // ── combo-budget §2 (SSH6.1): the circuit-aware geometry reading ─────────────────────────

    [Fact]
    public void Geometric_ceiling_counts_complete_circuits_not_roles()
    {
        var tuning = Shipped();
        // Per role: floor(ceiling / SocketCircuitSize). A six-socket role is ONE circuit plus a
        // two-socket remainder that only resonance can use, so it counts once, never twice.
        foreach (var (role, ceiling) in tuning.SocketCeiling)
            Assert.Equal(ceiling / SocketLimits.SocketCircuitSize,
                SocketGeometry.GeometricCombinationCeiling(tuning, new[] { role }));

        // The row's own examples, read from v2: 8 -> 2, 6 -> 1, 4 -> 1, 2 -> 0.
        Assert.Equal(2, SocketGeometry.GeometricCombinationCeiling(tuning, new[] { ItemRole.ArmamentPrimary }));
        Assert.Equal(1, SocketGeometry.GeometricCombinationCeiling(tuning, new[] { ItemRole.WardArray }));
        Assert.Equal(1, SocketGeometry.GeometricCombinationCeiling(tuning, new[] { ItemRole.HeadGuard }));
        Assert.Equal(0, SocketGeometry.GeometricCombinationCeiling(tuning, new[] { ItemRole.JewelMajor }));
        Assert.Equal(tuning.SocketCeiling.Values.Sum(c => c / SocketLimits.SocketCircuitSize),
            SocketGeometry.GeometricCombinationCeiling(tuning, tuning.SocketCeiling.Keys));

        // Reachability: one UNPINNED recipe admits every role and reaches the whole table; a pinned
        // one reaches only its own role. It is the catalog's reading, never a fixed host list.
        var unpinned = new ComboRecipe("combo.pure-fire-2", ComboShape.Pure, "fire", 2, "", "", 0, 1,
            Array.Empty<ComboIngredient>());
        Assert.Equal(SocketGeometry.GeometricCombinationCeiling(tuning, tuning.SocketCeiling.Keys),
            SocketGeometry.ReachableCombinationCeiling(tuning, new[] { unpinned }));

        var pinned = new ComboRecipe("combo.strain-x", ComboShape.Strain, "", 0,
            ItemRoles.Id(ItemRole.ArmamentPrimary), "", 4, 1, Array.Empty<ComboIngredient>());
        Assert.Equal(2, SocketGeometry.ReachableCombinationCeiling(tuning, new[] { pinned }));
    }
}
