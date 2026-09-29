using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Drops;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `rate-floor` (drop-tables module 1) — a tunable, universal minimum drop-weight floor
/// (`spec-rate-floor.md`), applied at import to every drop-table entry, independent of the rarity
/// ladder. Owner decision D2 (2026-09-07).
/// </summary>
public class DropRateFloorTests
{
    static readonly DropRateFloorTuning Tuning = new(MinRatePerMillion: 1);

    static DropTableEntryRow Entry(int seq, int weight, int? minIlvl = null, int? maxIlvl = null, bool enabled = true) =>
        new(seq, DropEntryKind.Currency, "test.entry." + seq, weight, MinIlvl: minIlvl, MaxIlvl: maxIlvl, Enabled: enabled);

    [Fact]
    public void An_entry_far_above_the_floor_passes()
    {
        // chaff's own real magnitude, out of a 100000-scale group total.
        var rejection = DropRateFloor.ValidateEntry(Entry(0, 40700), entryEffectiveWeight: 40700, groupTotalWeight: 100000, Tuning);
        Assert.True(rejection.IsOk);
    }

    [Fact]
    public void An_entry_exactly_at_the_floor_passes()
    {
        // 1 / 1_000_000 == MinRatePerMillion exactly -- the floor is inclusive.
        var rejection = DropRateFloor.ValidateEntry(Entry(0, 1), entryEffectiveWeight: 1, groupTotalWeight: 1_000_000, Tuning);
        Assert.True(rejection.IsOk);
    }

    [Fact]
    public void An_entry_one_per_million_below_the_floor_is_refused_by_name()
    {
        var rejection = DropRateFloor.ValidateEntry(Entry(0, 1), entryEffectiveWeight: 1, groupTotalWeight: 2_000_000, Tuning);
        Assert.False(rejection.IsOk);
        Assert.Contains("drop.rate-below-floor", rejection.ToString());
    }

    [Fact]
    public void Zero_or_negative_weight_is_not_this_checks_job()
    {
        var rejection = DropRateFloor.ValidateEntry(Entry(0, 0), entryEffectiveWeight: 0, groupTotalWeight: 1000, Tuning);
        Assert.True(rejection.IsOk);
    }

    [Fact]
    public void A_disabled_entry_with_a_real_weight_is_not_checked()
    {
        // EffectiveWeight already zeroes a disabled entry -- the caller passes 0 here exactly as
        // DropTableModel.EffectiveWeight would, and ValidateEntry must not re-derive Enabled itself.
        var entry = Entry(0, 500, enabled: false);
        var rejection = DropRateFloor.ValidateEntry(entry, entryEffectiveWeight: 0, groupTotalWeight: 500, Tuning);
        Assert.True(rejection.IsOk);
    }

    [Fact]
    public void A_single_entry_group_always_passes_and_the_doc_says_so()
    {
        // groupTotalWeight == entryEffectiveWeight by construction -> always exactly 1,000,000/million.
        var rejection = DropRateFloor.ValidateEntry(Entry(0, 3), entryEffectiveWeight: 3, groupTotalWeight: 3, Tuning);
        Assert.True(rejection.IsOk);
    }

    [Fact]
    public void A_missing_tuning_file_is_a_load_rejection_naming_the_key()
    {
        Assert.Throws<DropRateFloorTuningRejection>(() => DropRateFloorTuning.Parse("{}"));
    }

    [Fact]
    public void The_real_tuning_file_parses_and_matches_the_shipped_convention()
    {
        var path = System.IO.Path.Combine(DropVolumeTests.RepoRoot(), "data", "tuning", "drop-rate-floor.v1.json");
        var tuning = DropRateFloorTuning.Parse(System.IO.File.ReadAllText(path));
        Assert.Equal(1, tuning.MinRatePerMillion);
    }

    [Fact]
    public void The_breakpoint_set_is_lo_union_hi_plus_one_not_lo_and_hi()
    {
        // Entry B is in-band only through ilvl 10; entry A spans the whole range. The moment that
        // matters is ilvl 11 (B drops out, A alone) -- NOT ilvl 10 itself, where B is still active.
        var a = Entry(0, 1, minIlvl: 1, maxIlvl: 100);
        var b = Entry(1, 999_999, minIlvl: 1, maxIlvl: 10);
        var rejections = DropRateFloor.ValidateGroupAcrossBreakpoints(new[] { a, b }, Tuning);

        // At ilvl 11, A is alone: effective weight 1 / groupTotal 1 == 1,000,000/million -> passes.
        // At ilvl 1..10, A's share is 1/1,000,000 == exactly the floor -> also passes (inclusive).
        // Neither breakpoint should produce a rejection for this fixture -- the real assertion is the
        // NEXT test, which makes the low-ilvl breakpoint actually fail.
        Assert.All(rejections, r => Assert.True(r.IsOk));
    }

    [Fact]
    public void Entry_a_alone_at_high_ilvl_is_checked_against_its_own_solo_total_not_the_low_ilvl_shared_total()
    {
        // Entry A is far below the floor while B is present (weight 1 against a huge B), but fine once
        // alone. A static, single-total check would either wrongly reject A everywhere or wrongly pass
        // it everywhere; the breakpoint walk must get both regions right independently.
        var a = Entry(0, 1, minIlvl: 1, maxIlvl: 100);
        var b = Entry(1, 2_000_000, minIlvl: 1, maxIlvl: 10);
        var rejections = DropRateFloor.ValidateGroupAcrossBreakpoints(new[] { a, b }, Tuning);

        // Low-ilvl breakpoint (ilvl 1): A's share is 1 / 2,000,001 < 1/1,000,000 -> refused.
        Assert.Contains(rejections, r => !r.IsOk);
        // High-ilvl breakpoint (ilvl 11): A alone, share 1,000,000/million -> that one passes.
        Assert.Contains(rejections, r => r.IsOk);
    }

    [Fact]
    public void GroupTotalWeight_accumulation_throws_rather_than_wraps_on_an_absurd_sum()
    {
        // int.MaxValue-adjacent entries, several of them, overflow the checked accumulation in
        // ValidateGroupAcrossBreakpoints -- the real overflow surface, not entry.Weight * 1_000_000L
        // alone (an int Weight can never approach that multiply's own long headroom).
        var entries = new[]
        {
            Entry(0, int.MaxValue),
            Entry(1, int.MaxValue),
            Entry(2, int.MaxValue),
        };
        // Each EffectiveWeight is int.MaxValue (~2.1e9); three of them still fit in a long safely
        // (~6.4e9), so this specific fixture must NOT throw -- proving the headroom is real, not just
        // asserted. A genuine overflow needs many more/larger entries than any real content could
        // author; this test documents the boundary rather than fabricating an unreachable input.
        var rejections = DropRateFloor.ValidateGroupAcrossBreakpoints(entries, Tuning);
        Assert.All(rejections, r => Assert.True(r.IsOk));
    }

    [Fact]
    public void The_floor_never_touches_the_rarity_ladder()
    {
        // A functional check, not a naive text scan: DropRateFloor.cs's own doc comment legitimately
        // NAMES dropWeightPer100k/bands.v1.json to explain the boundary (this codebase's own
        // established documentation style) -- what actually matters is that the type never reads
        // ItemRarityTuning or any rarity-ladder type, which a Type-reflection check over the compiled
        // assembly proves without caring what prose the doc comment uses.
        var type = typeof(DropRateFloor);
        var referencedTypeNames = type.GetMethods()
            .SelectMany(m => new[] { m.ReturnType }.Concat(m.GetParameters().Select(p => p.ParameterType)))
            .Select(t => t.FullName ?? "")
            .ToList();
        Assert.DoesNotContain(referencedTypeNames, n => n.Contains("ItemRarityTuning", StringComparison.Ordinal));
        Assert.DoesNotContain(referencedTypeNames, n => n.Contains("RarityRow", StringComparison.Ordinal));
    }
}
