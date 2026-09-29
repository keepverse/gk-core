using System.Text.Json;
using System.Text.Json.Nodes;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Dungeon.Tuning;
using Xunit;

namespace FusionRpg.Core.Tests.Dungeon;

/// <summary>D1.3 — <c>dungeon.v1.json</c> and <c>encounter.v1.json</c>: every key required (T5),
/// no copied number (S2-10), no real-time unit (R6), the R8 rung-neighbour validator, and the
/// registry ↔ tuning agreement in both directions (spec-dungeon-registries.md "Testing strategy").
/// Reads the real, shipped files — a fixture copy could drift from what ships.</summary>
public class DungeonTuningTests
{
    static DungeonRegistries Registries() => DungeonRegistryLoader.LoadAll(DungeonTestFiles.RegistryDir());
    static string RealJson() => File.ReadAllText(DungeonTestFiles.DungeonTuningPath());
    static string RealEncounterJson() => File.ReadAllText(DungeonTestFiles.EncounterTuningPath());

    [Fact]
    public void The_real_dungeon_tuning_file_parses_against_the_real_registries()
    {
        var tuning = DungeonTuningLoader.Parse(RealJson(), Registries());
        Assert.Equal(1, tuning.SchemaVersion);
        // pin: immutable gk-core/data/tuning/dungeon.v3.json
        Assert.Equal(11, tuning.Nodes.Count);
        // pin: immutable gk-core/data/tuning/dungeon.v3.json
        Assert.Equal(10, tuning.Rungs.Count);
        Assert.Equal(3, tuning.RaidModes.Count);
    }

    [Fact]
    public void The_real_encounter_tuning_file_parses_against_the_real_registries()
    {
        var tuning = EncounterTuningLoader.Parse(RealEncounterJson(), Registries(), DungeonTestFiles.ThreatRungIds());
        Assert.Equal(4, tuning.SlotCountBand.Count); // lone/few/several/many
        Assert.Equal(3, tuning.SpreadOffClimateMilli.Count); // mono/dual/rainbow
    }

    // ---------------------------------------------------------------------------------------
    // Every-key-required: walk the real tree, delete one leaf at a time, assert a rejection
    // naming something. pack.footprint.* / pack.stack.* are free-form item-registry vocabulary
    // (spec: "read at load and checked [by the item registry] — never restated in a dungeon
    // registry") and are exempted, matching DungeonTuningLoader's own IntTableFree comment.
    // ---------------------------------------------------------------------------------------

    static bool IsFreeFormPath(string path) =>
        path.StartsWith("pack.footprint.role.", StringComparison.Ordinal) ||
        path.StartsWith("pack.footprint.massStep.", StringComparison.Ordinal) ||
        path.StartsWith("pack.footprint.consumableClass.", StringComparison.Ordinal) ||
        path.StartsWith("pack.stack.consumableClass.", StringComparison.Ordinal) ||
        path.StartsWith("pack.stack.materialClass.", StringComparison.Ordinal) ||
        // _meta is documentation, never read by the parser.
        path is "_meta" || path.StartsWith("_meta.", StringComparison.Ordinal);

    static IEnumerable<string> AllObjectKeyPaths(JsonNode? node, string prefix)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
                yield return path;
                foreach (var sub in AllObjectKeyPaths(value, path)) yield return sub;
            }
        }
        else if (node is JsonArray arr)
        {
            foreach (var item in arr)
                foreach (var sub in AllObjectKeyPaths(item, prefix)) yield return sub;
        }
    }

    static bool RemoveAtPath(JsonNode root, string path)
    {
        var segments = path.Split('.');
        JsonNode? cursor = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (cursor is not JsonObject obj || !obj.TryGetPropertyValue(segments[i], out cursor)) return false;
        }
        return cursor is JsonObject parent && parent.Remove(segments[^1]);
    }

    [Fact]
    public void Deleting_any_required_leaf_in_dungeon_tuning_rejects_naming_something()
    {
        var registries = Registries();
        var root = JsonNode.Parse(RealJson())!;
        var allPaths = AllObjectKeyPaths(root, "").Where(p => !IsFreeFormPath(p)).ToList();
        Assert.True(allPaths.Count > 50, "sanity: expected a large tuning tree");

        var failures = new List<string>();
        foreach (var path in allPaths)
        {
            var clone = JsonNode.Parse(root.ToJsonString())!;
            if (!RemoveAtPath(clone, path)) continue;
            try
            {
                DungeonTuningLoader.Parse(clone.ToJsonString(), registries);
                failures.Add(path); // no rejection -- a real gap
            }
            catch (DungeonTuningRejection) { /* expected */ }
            catch (DungeonRegistryRejection) { /* also acceptable -- band/registry cross-check */ }
        }

        Assert.True(failures.Count == 0,
            $"deleting these leaves did not reject: {string.Join(", ", failures.Take(20))}" +
            (failures.Count > 20 ? $" (+{failures.Count - 20} more)" : ""));
    }

    [Fact]
    public void Deleting_any_required_leaf_in_encounter_tuning_rejects_naming_something()
    {
        var registries = Registries();
        var root = JsonNode.Parse(RealEncounterJson())!;
        var allPaths = AllObjectKeyPaths(root, "").Where(p => p != "_meta" && !p.StartsWith("_meta.", StringComparison.Ordinal)).ToList();
        Assert.True(allPaths.Count > 15, "sanity: expected a real tuning tree");

        var failures = new List<string>();
        foreach (var path in allPaths)
        {
            var clone = JsonNode.Parse(root.ToJsonString())!;
            if (!RemoveAtPath(clone, path)) continue;
            try
            {
                EncounterTuningLoader.Parse(clone.ToJsonString(), registries, DungeonTestFiles.ThreatRungIds());
                failures.Add(path);
            }
            catch (EncounterTuningRejection) { }
            catch (DungeonRegistryRejection) { }
        }

        Assert.True(failures.Count == 0, $"deleting these leaves did not reject: {string.Join(", ", failures.Take(20))}");
    }

    [Fact]
    public void A_fractional_value_where_an_integer_is_required_rejects()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["depth"]!["bossBandDelta"] = 1.5;
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    // ---------------------------------------------------------------------------------------
    // No copied number (S2-10) — neither new file may carry any of expeditions'/summoning's own
    // keys, and no real-time unit exists anywhere (R6).
    // ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData("wildJoinMilli")]
    [InlineData("costPerPull")]
    [InlineData("pullPriceSouls")]
    // \b word boundary matches the D1.1 success-criterion grep exactly (spec-dungeon-registries.md
    // §Success criteria 4: "bossBand\b") -- a plain substring check would false-positive on the
    // legitimate depth.bossBandDelta key, which is a different, correctly-owned number.
    [InlineData(@"\bbossBand\b")]
    public void The_dungeon_tuning_file_contains_no_copied_number_key(string bannedPattern)
    {
        Assert.DoesNotMatch(bannedPattern, RealJson());
    }

    [Theory]
    [InlineData("wildJoinMilli")]
    [InlineData("costPerPull")]
    public void The_encounter_tuning_file_contains_no_copied_number_key(string bannedKey)
    {
        Assert.DoesNotContain(bannedKey, RealEncounterJson());
    }

    [Fact]
    public void Neither_tuning_file_carries_a_realtime_unit_key()
    {
        foreach (var json in new[] { RealJson(), RealEncounterJson() })
        {
            var root = JsonNode.Parse(json)!;
            foreach (var path in AllObjectKeyPaths(root, ""))
            {
                var lastSegment = path.Split('.')[^1];
                foreach (var suffix in new[] { "Day", "Hour", "Minute", "Ms" })
                    Assert.False(lastSegment.EndsWith(suffix, StringComparison.Ordinal),
                        $"'{path}' carries a real-time unit ({suffix}) -- R6 removed the wall clock.");
            }
        }
    }

    [Fact]
    public void RejectDayHourMinuteMs_actually_fires_on_a_fixture()
    {
        var fixture = JsonNode.Parse(RealJson())!;
        fixture["risk"]!["downedRecoveryDays"] = 2; // the retired shape R6 removed
        var ex = Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(fixture.ToJsonString(), Registries()));
        Assert.Contains("real-time unit", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    // R8 — the rung-neighbour validator. The shipped table passes; two adjacent rungs that agree
    // on every reward-bearing column reject.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void The_shipped_rung_table_passes_R8()
    {
        var tuning = DungeonTuningLoader.Parse(RealJson(), Registries());
        Assert.Equal(0, tuning.Rungs["hard"].BandDelta);
        Assert.Equal(1000, tuning.Rungs["hard"].EliteWeightMultMilli);
        Assert.Equal(1000, tuning.Rungs["hard"].RestWeightMultMilli);
        Assert.Equal(1000, tuning.Rungs["hard"].HungerMultMilli);
        Assert.Equal(1000, tuning.Rungs["hard"].SpiritDrainMultMilli);
        Assert.Equal(1000, tuning.Rungs["hard"].MerchantMarkupMultMilli);
        Assert.Equal(0, tuning.Rungs["hard"].EnemyCountDeltaFight);
    }

    [Fact]
    public void Two_adjacent_rungs_identical_on_every_reward_column_reject()
    {
        var root = JsonNode.Parse(RealJson())!;
        var rungs = root["difficulty"]!["rungs"]!;
        // Clone "hard" onto "very-hard" verbatim -- every reward-bearing column now ties.
        rungs["very-hard"] = JsonNode.Parse(rungs["hard"]!.ToJsonString());
        var ex = Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
        Assert.Contains("R8", ex.Message);
    }

    // ---------------------------------------------------------------------------------------
    // Registry <-> tuning agreement, both directions.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void An_extra_node_kind_in_tuning_rejects()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["nodes"]!["ghost-kind"] = JsonNode.Parse("""{"weightMilli":1,"earliestRowMilli":0,"latestRowMilli":1000}""");
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    [Fact]
    public void A_missing_node_kind_in_tuning_rejects()
    {
        var root = JsonNode.Parse(RealJson())!;
        ((JsonObject)root["nodes"]!).Remove("trap");
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    [Fact]
    public void An_extra_raid_mode_in_tuning_rejects()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["raid"]!["modes"]!["hexad"] = JsonNode.Parse(root["raid"]!["modes"]!["quad"]!.ToJsonString());
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    [Fact]
    public void An_extra_difficulty_rung_in_tuning_rejects()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["difficulty"]!["rungs"]!["mythic"] = JsonNode.Parse(root["difficulty"]!["rungs"]!["hard"]!.ToJsonString());
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    [Fact]
    public void An_extra_band_member_in_tuning_rejects()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["bands"]!["hazardBand"]!["extreme"] = JsonNode.Parse("""{"hungerPerMille":999}""");
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    [Fact]
    public void An_unknown_override_tag_in_wild_provisionOverrideTag_rejects()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["wild"]!["provisionOverrideTag"] = "not-a-real-tag";
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    // delve-attrition (spec-delve-attrition.md's Tunables table): both lists are documented as
    // "resource ids subset of ResourceIds, loader-checked" -- found unchecked while building
    // RestResolver (D2.20), fixed in DungeonTuning.cs's own Parse alongside every other closed-set
    // id it already validates.

    [Fact]
    public void A_non_resource_id_in_rest_healsPools_rejects()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["rest"]!["healsPools"]!.AsArray().Add("not-a-real-resource");
        var ex = Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
        Assert.Contains("not-a-real-resource", ex.Message);
    }

    [Fact]
    public void A_non_resource_id_in_attrition_persistAcrossDelves_rejects()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["attrition"]!["persistAcrossDelves"]!.AsArray().Add("not-a-real-resource");
        var ex = Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
        Assert.Contains("not-a-real-resource", ex.Message);
    }

    [Fact]
    public void The_real_shipped_healsPools_and_persistAcrossDelves_are_both_real_resource_ids()
    {
        // The positive case: the real file already passes (proven independently by
        // The_real_dungeon_tuning_file_parses_against_the_real_registries), but pin the actual
        // content too so a future edit that widens either list to a bad id fails here by name.
        var tuning = DungeonTuningLoader.Parse(RealJson(), Registries());
        Assert.Equal(new[] { "hp", "hunger", "spirit" }, tuning.RestHealsPools);
        Assert.Equal(new[] { "hunger" }, tuning.AttritionPersistAcrossDelves);
    }

    [Fact]
    public void Wild_outcome_rows_must_sum_to_1000()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["wild"]!["outcome"]!["eager"]!["attacksMilli"] = 999_999;
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    // D4.1 (spec-wild-room.md §1) -- found while building Disposition.Shift: the loader already
    // length-checks the sibling wild.deltaBands ("4 edges -> 5 resulting deltaBand members") but
    // never checked this one, and the shipped file had shipped with only 4 entries (missing the
    // "even" band's own shift) -- a real, silently-under-indexable gap, fixed alongside this check.

    [Fact]
    public void Wild_deltaShiftRungs_must_have_exactly_5_entries_one_per_deltaBand_member()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["wild"]!["deltaShiftRungs"]!.AsArray().RemoveAt(0);
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    [Fact]
    public void The_real_shipped_wild_deltaShiftRungs_has_5_entries_one_per_named_band()
    {
        var tuning = DungeonTuningLoader.Parse(RealJson(), Registries());
        Assert.Equal(5, tuning.WildDeltaShiftRungs.Count);
        Assert.Equal(new[] { -2, -1, 0, 1, 2 }, tuning.WildDeltaShiftRungs);
    }

    [Fact]
    public void Solo_raid_mode_must_not_carry_a_boss_shield_key()
    {
        var root = JsonNode.Parse(RealJson())!;
        root["raid"]!["modes"]!["solo"]!["bossShieldPerPartyMilli"] = 300;
        Assert.Throws<DungeonTuningRejection>(() => DungeonTuningLoader.Parse(root.ToJsonString(), Registries()));
    }

    // ---------------------------------------------------------------------------------------
    // Stem check (S2-12): tuning may name "weight"/"chance" only on nodes.*.weightMilli.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void No_registry_member_matches_the_weight_or_chance_stem()
    {
        // S2-12's actual target: a REGISTRY enum member disguising a raw frequency as an id
        // (the "weightBand" incident it renamed away from). Tuning KEYS are a different surface —
        // capture.chanceMilli and nodes.*.weightMilli are both correctly-named `Milli` magnitudes,
        // not the defect this rule guards against.
        var registries = Registries();
        var offenders = new List<string>();
        void Check(IEnumerable<string> members, string file)
        {
            foreach (var m in members)
                if (m.Contains("weight", StringComparison.OrdinalIgnoreCase) || m.Contains("chance", StringComparison.OrdinalIgnoreCase))
                    offenders.Add($"{file}:{m}");
        }
        Check(registries.RoomKinds.Select(r => r.RoomKindId), "room-kinds");
        Check(registries.DoorKinds.Select(r => r.DoorKindId), "door-kinds");
        Check(registries.OverrideTags, "override-tags");
        Check(registries.ObjectiveTemplates.Select(r => r.ObjectiveTemplateId), "objective-templates");
        Check(registries.DifficultyRungs.Select(r => r.RungId), "difficulty-rungs");
        Check(registries.Disposition, "disposition");
        Check(registries.InteractionVerbs.Select(r => r.VerbId), "interaction-verbs");
        Check(registries.RaidModes, "raid-modes");
        foreach (var (bandName, def) in registries.Bands) Check(def.Members, $"bands.{bandName}");
        Assert.Empty(offenders);
    }

    [Fact]
    public void No_second_room_selection_weight_key_exists_outside_nodes()
    {
        // "in tuning only nodes.*.weightMilli may [own a bare selection weight]" -- guards against
        // a duplicate room-kind weight surfacing under a second name (review N13's pattern).
        var root = JsonNode.Parse(RealJson())!;
        var bareWeightMilliKeys = AllObjectKeyPaths(root, "")
            .Where(p => p.EndsWith(".weightMilli", StringComparison.Ordinal) || p == "weightMilli")
            .Where(p => !System.Text.RegularExpressions.Regex.IsMatch(p, @"^nodes\.[a-z-]+\.weightMilli$"))
            .ToList();
        Assert.Empty(bareWeightMilliKeys);
    }

    // ---------------------------------------------------------------------------------------
    // Hub discipline.
    // ---------------------------------------------------------------------------------------

    // DungeonTuningHub/DungeonRegistryHub's "not configured" guard is exercised for real by
    // DungeonRegistryTests -- its very first run against this suite caught a genuine bug
    // (DungeonTuningLoader read the BandCatalog hub before it was configured) via exactly this
    // message shape ("BandCatalog.Configure(...) has not run"). Not repeated here: the static
    // hub is shared process-wide, so a "throws before Configure" test here would be racing every
    // other test in this class for which hub state exists first.

    [Fact]
    public void EncounterTuningHub_configure_then_read_round_trips()
    {
        var tuning = EncounterTuningLoader.Parse(RealEncounterJson(), Registries(), DungeonTestFiles.ThreatRungIds());
        EncounterTuningHub.Configure(tuning);
        Assert.Equal(tuning, EncounterTuningHub.Tuning);
    }
}
