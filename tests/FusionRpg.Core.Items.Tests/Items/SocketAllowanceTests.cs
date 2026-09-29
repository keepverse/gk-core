using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Sockets;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T4 (spec-socket-allowance-by-kind) against the REAL shipped
/// `gk-core/data/tuning/sockets.v2.json` (`SocketTuningFiles.Current`): the allowance is a per-kind shift of
/// the rarity WINDOW
/// before the roll. The kind set is a CLOSED VOCABULARY the code owns (validation-ssot.md §1);
/// nothing here asserts a population count or a shipped item's sockets.
/// </summary>
public class SocketAllowanceTests
{
    // ── the shipped table ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_shipped_table_carries_exactly_the_closed_kind_vocabulary()
    {
        var tuning = SocketGeometryTests.Shipped();
        Assert.Equal(new[] { "ordinary", "set", "unique", "boss" }, tuning.AllowanceByKind.Keys);
        Assert.Equal(new[] { "ordinary", "set", "unique", "boss" }, SocketKinds.All);
    }

    [Fact]
    public void Ordinary_is_the_unchanged_baseline_for_every_rung()
    {
        // The regression contract (SC5): an ordinary item's derivation is byte-identical to today.
        var tuning = SocketGeometryTests.Shipped();
        foreach (var rung in RarityLadder.RungIds)
            Assert.Equal(tuning.RarityGrant[rung], tuning.ShiftedWindow("ordinary", rung));
    }

    [Fact]
    public void Set_narrows_from_fused_up_and_unique_boss_widen()
    {
        var tuning = SocketGeometryTests.Shipped();
        // Below fused the set row is 0 ON PURPOSE: chaff/sprout sit at {0,0} and any −1 there is
        // a Negative load rejection, not a quieter reduction (see the tuning note).
        foreach (var rung in new[] { "chaff", "sprout", "grafted", "cultivated" })
            Assert.Equal(tuning.RarityGrant[rung], tuning.ShiftedWindow("set", rung));
        // v2's doubled grants (SSH5.10): set shifts BOTH ends down one; unique/boss shift both up one.
        Assert.Equal(new SocketGrantWindow(1, 3), tuning.ShiftedWindow("set", "fused"));
        Assert.Equal(new SocketGrantWindow(1, 5), tuning.ShiftedWindow("set", "heirloom"));
        Assert.Equal(new SocketGrantWindow(3, 7), tuning.ShiftedWindow("set", "almanac"));
        Assert.Equal(new SocketGrantWindow(1, 3), tuning.ShiftedWindow("unique", "grafted"));
        Assert.Equal(new SocketGrantWindow(5, 9), tuning.ShiftedWindow("unique", "almanac"));
        Assert.Equal(tuning.ShiftedWindow("unique", "fused"), tuning.ShiftedWindow("boss", "fused"));
    }

    [Fact]
    public void Every_kind_table_is_monotonic_and_overlapping_like_the_authored_one()
    {
        // The parser already enforces this at load; this pins the SHIPPED values satisfy it for
        // all four kinds × all ten rungs (the contract in the Testing strategy table).
        var tuning = SocketGeometryTests.Shipped();
        foreach (var kind in SocketKinds.All)
        for (var i = 1; i < RarityLadder.RungIds.Count; i++)
        {
            var low = tuning.ShiftedWindow(kind, RarityLadder.RungIds[i - 1]);
            var high = tuning.ShiftedWindow(kind, RarityLadder.RungIds[i]);
            Assert.True(high.Min >= low.Min && high.Max >= low.Max, $"{kind} monotonic");
            Assert.True(high.Min <= low.Max, $"{kind} OD4 overlap");
        }
    }

    // ── the inversion is bounded and REPORTED (SC6) ──────────────────────────────────────

    [Fact]
    public void The_inversion_cells_above_the_ceiling_are_reported_never_hidden()
    {
        var notes = SocketGeometryTests.Shipped().StructuralBoundNotes;
        Assert.Contains(notes, n => n.Contains("unique") && n.Contains("sunwoven"));
        Assert.Contains(notes, n => n.Contains("unique") && n.Contains("almanac"));
        Assert.Contains(notes, n => n.Contains("boss") && n.Contains("almanac"));
        Assert.DoesNotContain(notes, n => n.Contains("ordinary") || n.Contains("set"));
    }

    [Fact]
    public void A_bounded_inversion_still_resolves_through_the_downstream_clamps()
    {
        var tuning = SocketGeometryTests.Shipped();
        // almanac unique derives {5,9} in v2: the roll may exceed 4, but the base type's own socketMax
        // (at or below its role ceiling) clamps it — the result is bounded at 4 for EVERY seed.
        for (ulong seed = 1; seed <= 300; seed++)
            Assert.InRange(SocketGeometry.SocketsAtDrop(4, "almanac", seed, tuning, "unique"), 3, 4);
    }

    // ── composition order: shift, roll, base clamp ───────────────────────────────────────

    [Fact]
    public void The_shift_applies_before_the_roll_not_after_it()
    {
        var tuning = SocketGeometryTests.Shipped();
        // heirloom set derives {1,5} from v2's {2,6}: the base clamp still applies after the roll
        // (entryMax caps at its own value).
        for (ulong seed = 1; seed <= 300; seed++)
        {
            Assert.InRange(SocketGeometry.SocketsAtDrop(4, "heirloom", seed, tuning, "set"), 1, 5);
            Assert.InRange(SocketGeometry.SocketsAtDrop(1, "heirloom", seed, tuning, "set"), 1, 1);
        }
        // unique grafted derives {1,3} from {0,2}: no seed can roll a 0.
        for (ulong seed = 1; seed <= 300; seed++)
            Assert.InRange(SocketGeometry.SocketsAtDrop(4, "grafted", seed, tuning, "unique"), 1, 3);
    }

    // ── load rejections ─────────────────────────────────────────────────────────────────

    [Fact]
    public void An_absent_section_throws_naming_it()
    {
        var stripped = SocketGeometryTests.Raw()
            .Replace("\"socketAllowanceByKind\": {", "\"socketAllowanceByKind_REMOVED\": {");
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(stripped));
        Assert.Contains("socketAllowanceByKind", ex.Message);
    }

    [Fact]
    public void An_unknown_kind_throws_naming_it()
    {
        // "boss": (quoted with colon) occurs once in the whole file — the row key.
        var raw = SocketGeometryTests.Raw().Replace("\"boss\":", "\"void\":");
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(raw));
        Assert.Contains("void", ex.Message);
    }

    [Fact]
    public void A_missing_kind_row_throws()
    {
        // An absent kind is an undefined allowance, not a zero shift. Removed structurally
        // (JsonDocument) rather than by string surgery, so the JSON stays valid.
        using var doc = JsonDocument.Parse(SocketGeometryTests.Raw());
        var kept = doc.RootElement.GetProperty("socketAllowanceByKind").EnumerateObject()
            .Where(p => p.Name != "boss")
            .ToDictionary(p => p.Name, p => (object?)JsonSerializer.Deserialize<JsonElement>(p.Value.GetRawText()));
        using var root = JsonDocument.Parse(SocketGeometryTests.Raw());
        var all = root.RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)JsonSerializer.Deserialize<JsonElement>(p.Value.GetRawText()));
        all["socketAllowanceByKind"] = kept;
        var ex = Assert.Throws<SocketTuningRejection>(
            () => SocketTuning.Parse(JsonSerializer.Serialize(all)));
        Assert.Contains("boss", ex.Message);
    }

    [Fact]
    public void A_negative_shift_throws_naming_kind_and_rung()
    {
        var raw = SocketGeometryTests.Raw().Replace("\"fused\": -1,", "\"fused\": -3,");
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(raw));
        Assert.Contains("set", ex.Message);
        Assert.Contains("fused", ex.Message);
        Assert.Contains("negative", ex.Message);
    }

    [Fact]
    public void A_shift_breaking_overlap_throws()
    {
        var raw = SocketGeometryTests.Raw().Replace("\"heirloom\": -1,", "\"heirloom\": 2,");
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(raw));
        Assert.Contains("set", ex.Message);
        Assert.Contains("do not overlap", ex.Message);
    }

    [Fact]
    public void A_shift_breaking_monotonicity_throws()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(SocketGeometryTests.Raw())!.AsObject();
        node["socketAllowanceByKind"]!["set"]!["chaff"] = 2;
        var ex = Assert.Throws<SocketTuningRejection>(() => SocketTuning.Parse(node.ToJsonString()));
        Assert.Contains("monotonic", ex.Message);
    }

    [Fact]
    public void An_inverted_window_is_structurally_unreachable_with_scalar_deltas()
    {
        // Spec-mandated note, not a test gap: a per-rung SCALAR delta preserves min ≤ max by
        // construction ({min+d, max+d} inverts only if the authored window was already inverted,
        // which its own check refuses first). The inverted-window check stays in the parser as
        // defense for a future asymmetric {min,max}-delta shape — this pins WHY no test triggers it.
        var tuning = SocketGeometryTests.Shipped();
        foreach (var kind in SocketKinds.All)
        foreach (var rung in RarityLadder.RungIds)
        {
            var w = tuning.ShiftedWindow(kind, rung);
            Assert.True(w.Max >= w.Min, $"{kind}/{rung} inverted");
        }
    }

    [Fact]
    public void Unknown_kinds_and_rungs_throw_at_call_time()
    {
        var tuning = SocketGeometryTests.Shipped();
        Assert.Throws<SocketTuningRejection>(() => tuning.ShiftedWindow("void", "chaff"));
        Assert.Throws<SocketTuningRejection>(() => tuning.ShiftedWindow("ordinary", "mythic"));
        Assert.Throws<SocketTuningRejection>(
            () => SocketGeometry.SocketsAtDrop(4, "chaff", 1UL, tuning, "void"));
    }
}
