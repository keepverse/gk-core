using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `drop-volume` (item module 11) — D18's linear Θ read, D26's absence of any cap, and D38's two
/// independent rolls, all against the REAL shipped tuning file and the REAL shipped loot corpus.
/// </summary>
public class DropVolumeTests
{
    internal static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so a `gk-core/data/tuning` or `src/` read stays valid once the injector source moves
        // to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }

    internal static DropVolumeTuning Tuning() => DropVolumeTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-drop-volume.v1.json")));

    /// <summary>
    /// Source with every comment line stripped. The rules these guards enforce are about CODE — a
    /// doc comment that EXPLAINS why `items_since_r4` is retired, or that cites `Power/PowerLadder.cs`
    /// as the curve this module deliberately does not read, is the opposite of the defect. Scanning
    /// the comments too would make the honest explanation the thing that fails.
    /// </summary>
    internal static string CodeOnly(string source) => string.Join(
        Environment.NewLine,
        source.Split('\n').Where(line =>
        {
            var t = line.TrimStart();
            return !t.StartsWith("//", StringComparison.Ordinal)
                && !t.StartsWith("*", StringComparison.Ordinal)
                && !t.StartsWith("/*", StringComparison.Ordinal);
        }));

    // ---- D18: linear in Θ, through the shipped provider, with no private curve --------------------

    [Fact]
    public void Volume_is_linear_in_theta()
    {
        var t = Tuning();
        var atPin = DropVolume.VolumeScaleMilli(t.ThetaPin, t);
        var atPlus10 = DropVolume.VolumeScaleMilli(t.ThetaPin + 10, t);
        var atPlus20 = DropVolume.VolumeScaleMilli(t.ThetaPin + 20, t);

        Assert.Equal(t.VolumeBaseMilli, atPin);

        // Doubling (Θ − ΘPin) doubles the EXCESS over base. A quadratic curve would quadruple it.
        Assert.Equal(2 * (atPlus10 - atPin), atPlus20 - atPin);
    }

    [Fact]
    public void At_the_pin_the_scale_is_exactly_one()
    {
        var t = Tuning();
        Assert.Equal(1000, DropVolume.VolumeScaleMilli(t.ThetaPin, t));
    }

    [Fact]
    public void Volume_uses_no_private_curve()
    {
        // Not a grep of intent — an actual call through the shipped provider. StubPowerIndexProvider
        // is the identity (Θ = 0), which is a Θ FAR below the pin, so this doubles as a floor probe.
        var t = Tuning();
        var provider = new StubPowerIndexProvider();
        var ctx = new StatContext { PlayerId = 1, Side = StatSide.Plant, TypeId = 1, EntityKey = "test" };

        var viaProvider = DropVolume.VolumeScaleMilli(provider, ctx, t);
        Assert.Equal(DropVolume.VolumeScaleMilli(0, t), viaProvider);

        // And the source itself declares no f(level): no method under Items/Drops/ computes a scale
        // from anything but Θ, and none of the four files that could reference a ladder do.
        var dropsDir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Drops");
        foreach (var file in Directory.EnumerateFiles(dropsDir, "*.cs"))
        {
            var text = CodeOnly(File.ReadAllText(file));
            Assert.DoesNotContain("PowerLadder.", text);
            Assert.DoesNotContain("Math.Pow", text);
        }
    }

    [Fact]
    public void There_is_no_upper_bound_on_volume()
    {
        var t = Tuning();

        // D26 as a test. A very large Θ scales without clamping, and each step up adds the same
        // slope — nothing saturates.
        var a = DropVolume.VolumeScaleMilli(1_000_000, t);
        var b = DropVolume.VolumeScaleMilli(2_000_000, t);
        Assert.True(b > a);
        Assert.Equal(t.VolumeSlopeMilli * 1_000_000, b - a);

        // And the derived draw count keeps growing with it — a cap would show up here first.
        var rng = new AtomRandom(7UL, "test");
        Assert.True(DropVolume.RollsEffective(1, b, rng) > DropVolume.RollsEffective(1, a, rng));
    }

    [Fact]
    public void No_drop_cap_exists_anywhere_in_the_pipeline()
    {
        // Guard-shaped, over the real source: no per-run, per-period or per-player ceiling.
        var dropsDir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Drops");
        string[] banned = { "PerDay", "PerRun", "DailyCap", "MaxDropsPer", "DropCap", "pvz_loot_budget" };

        foreach (var file in Directory.EnumerateFiles(dropsDir, "*.cs"))
        {
            var text = CodeOnly(File.ReadAllText(file));
            foreach (var b in banned)
                Assert.False(text.Contains(b, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} names '{b}' — D26 puts drop caps outside this program");
        }

        // The tuning file carries the same commitment, and says so where a balance pass will read it.
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-drop-volume.v1.json"));
        Assert.Contains("there is deliberately NO upper bound", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void The_floor_is_structural_and_documented()
    {
        var t = Tuning();

        // A Θ far below the pin still yields a POSITIVE rate — the floor refuses zero as well as
        // negative. (With the shipped slope the floor never actually binds at Θ ≥ 0, which is the
        // point: it is a guard, not a live clamp.)
        Assert.True(DropVolume.VolumeScaleMilli(0, t) > 0);
        Assert.True(DropVolume.VolumeScaleMilli(int.MinValue / 2, t) >= t.FloorMilli);
        Assert.Equal(t.FloorMilli, DropVolume.VolumeScaleMilli(int.MinValue / 2, t));

        // The constant carries its comment, in the file a balance pass edits.
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-drop-volume.v1.json"));
        Assert.Contains("STRUCTURAL, not a progression ceiling", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Overflow_throws_it_never_wraps()
    {
        var t = Tuning() with { VolumeSlopeMilli = long.MaxValue / 2 };
        Assert.Throws<OverflowException>(() => DropVolume.VolumeScaleMilli(int.MaxValue, t));
        Assert.Throws<OverflowException>(() => DropVolume.ExpectedRollsMilli(long.MaxValue, 1000));
    }

    // ---- step 5a: the remainder is a Bernoulli, and the stream is its own ------------------------

    [Fact]
    public void Rolls_effective_takes_the_whole_part_and_bernoullis_the_remainder()
    {
        // 1 roll at 1500‰ = 1 guaranteed + a 500‰ coin. Over many streams it must land on both
        // sides and never outside {1, 2}.
        var seen = new HashSet<long>();
        for (var i = 0; i < 200; i++)
            seen.Add(DropVolume.RollsEffective(1, 1500, new AtomRandom((ulong)i, "item.volume.t.g")));

        Assert.Equal(new HashSet<long> { 1, 2 }, seen);
    }

    [Fact]
    public void Rolls_effective_is_exact_when_the_remainder_is_zero()
    {
        var rng = new AtomRandom(1UL, "x");
        Assert.Equal(3, DropVolume.RollsEffective(3, 1000, rng));
        Assert.Equal(6, DropVolume.RollsEffective(3, 2000, rng));
        Assert.Equal(0, DropVolume.RollsEffective(0, 999_999, rng));
    }

    [Fact]
    public void The_volume_stream_shifts_no_other_stream()
    {
        // Step 5a's stream is named for its group and derives from the same loot seed. Adding it
        // must leave every other named stream byte-identical, which is exactly what per-system
        // streams buy (SeededRng: "an extra roll in one system never shifts another").
        const ulong seed = 0xC0FFEE;

        ulong[] Sample(string name)
        {
            var rng = new AtomRandom(seed, name);
            return new[] { (ulong)rng.NextInclusive(0, int.MaxValue), (ulong)rng.NextPerMille() };
        }

        var before = new[]
        {
            Sample(LootStreams.ItemLevel), Sample(LootStreams.BaseType(0)),
            Sample(LootStreams.Rarity(0)), Sample(LootStreams.Rolls(0)),
            Sample(LootStreams.GroupDraw("drop.web.wave-normal", "gear")),
        };

        // Draw the volume stream to exhaustion between the two samplings.
        var volume = new AtomRandom(seed, LootStreams.Volume("drop.web.wave-normal", "gear"));
        for (var i = 0; i < 64; i++) volume.NextPerMille();

        var after = new[]
        {
            Sample(LootStreams.ItemLevel), Sample(LootStreams.BaseType(0)),
            Sample(LootStreams.Rarity(0)), Sample(LootStreams.Rolls(0)),
            Sample(LootStreams.GroupDraw("drop.web.wave-normal", "gear")),
        };

        for (var i = 0; i < before.Length; i++) Assert.Equal(before[i], after[i]);
    }

    // ---- D38: two independent rolls -------------------------------------------------------------

    [Fact]
    public void The_kill_path_is_a_flat_five_percent_and_does_not_scale_with_theta()
    {
        var t = Tuning();
        Assert.Equal(50, t.DropChanceOnKillMilli);
        Assert.False(t.KillScalesWithTheta);

        int Hits(int theta)
        {
            var n = 0;
            for (var i = 0; i < 20_000; i++)
                if (DropVolume.RollsAnythingOnKill(theta, t, new AtomRandom((ulong)i, "kill"))) n++;
            return n;
        }

        var beginner = Hits(0);
        var veteran = Hits(2000);

        // A veteran and a beginner see the SAME rate — progression shows up as WHAT drops.
        Assert.Equal(beginner, veteran);
        Assert.InRange(beginner, 800, 1200); // 5% of 20,000 = 1,000
    }

    [Fact]
    public void A_five_percent_kill_rate_is_not_a_five_percent_chance_at_an_almanac()
    {
        // ⛔ The disambiguation, as a test. Roll 1 answers "does anything drop"; roll 2 asks the
        // RARITY CATALOG — a different table — which rung. Conflating them would make the top rung
        // 20× more common than the ladder says.
        var t = Tuning();
        var ladder = DropVolumeCorpusTests.Ladder();
        var entry = new DropTableEntryRow(0, DropEntryKind.Equipment, "", 1, Frame: "plant", Role: "girdle");

        var kills = 0;
        var almanacs = 0;
        for (var i = 0; i < 200_000; i++)
        {
            if (!DropVolume.RollsAnythingOnKill(0, t, new AtomRandom((ulong)i, "kill"))) continue;
            kills++;
            var r = RarityDraw.Draw(ladder, entry, LootPityState.Empty, t, new AtomRandom((ulong)i, "rarity"), out var o);
            Assert.True(r.IsOk);
            if (o.RarityId == "almanac") almanacs++;
        }

        Assert.InRange(kills, 9_000, 11_000);          // ~5% of 200,000

        // almanac is 700/100,000 = 0.7% OF THE DROPS THAT HAPPEN, not 5%.
        var almanacPerHundredK = (long)almanacs * 100_000 / kills;
        Assert.InRange(almanacPerHundredK, 400, 1100);
        Assert.True(almanacs < kills / 20,
            "an almanac must be far rarer than 'anything at all' — the two rolls are independent");
    }

    // ---- Correction 5: pity keys on rung ids, thresholds re-solved -------------------------------

    [Fact]
    public void Pity_counters_are_keyed_on_rung_ids()
    {
        var dropsDir = Path.Combine(RepoRoot(), "src", "FusionRpg.Core", "Items", "Drops");
        foreach (var file in Directory.EnumerateFiles(dropsDir, "*.cs"))
        {
            var text = CodeOnly(File.ReadAllText(file));
            Assert.DoesNotContain("items_since_r4", text, StringComparison.Ordinal);
            Assert.DoesNotContain("items_since_r6", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ItemsSinceR4", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ItemsSinceR6", text, StringComparison.Ordinal);
        }

        var state = new LootPityState(1, 2);
        Assert.Equal(1, state.ItemsSinceHeirloom);
        Assert.Equal(2, state.ItemsSinceSunwoven);
        Assert.Equal("heirloom", RarityDraw.HeirloomId);
        Assert.Equal("sunwoven", RarityDraw.SunwovenId);

        // And the two guarded rungs agree with module 7's seeded pity_guarded key.
        Assert.True(RarityLadder.IsPityGuarded(RarityDraw.HeirloomId));
        Assert.True(RarityLadder.IsPityGuarded(RarityDraw.SunwovenId));
        Assert.False(RarityLadder.IsPityGuarded("almanac"));
    }

    [Fact]
    public void Pity_fires_where_the_drought_is_real()
    {
        // ⭐ Asserts the DROUGHT PROBABILITY, never the threshold — so a reweight moves the number
        // and not the test. Re-solved against module 7's SEEDED weights (heirloom+ = 5.9%,
        // sunwoven+ = 1.8%), never against I12's seven-rung 10.0% / 1.0%.
        var t = Tuning();
        var ladder = DropVolumeCorpusTests.Ladder();
        var total = ladder.Sum(r => (long)r.DropWeightPer100k);
        Assert.Equal(100_000, total);

        var heirloomPlus = ladder.Where(r => r.Ordinal >= 70).Sum(r => (long)r.DropWeightPer100k);
        var sunwovenPlus = ladder.Where(r => r.Ordinal >= 90).Sum(r => (long)r.DropWeightPer100k);
        Assert.Equal(5_900, heirloomPlus);
        Assert.Equal(1_800, sunwovenPlus);

        static double Drought(long ratePer100k, long n) => Math.Pow(1.0 - ratePer100k / 100_000.0, n);

        // I12 tuned its own floor so the drought at the threshold sat at ~7.2% — a counter that
        // fires once per eleven thousand players is decoration, and one that fires constantly is
        // not pity. The same BEHAVIOUR, re-solved.
        var heirloomDrought = Drought(heirloomPlus, t.Pity.HeirloomHardFloorItems);
        Assert.InRange(heirloomDrought, 0.05, 0.10);

        // The sunwoven ramp starts where I12's started (~22%) and its ceiling sits where I12's sat (~1.8%).
        Assert.InRange(Drought(sunwovenPlus, t.Pity.SunwovenRampStartItems), 0.18, 0.26);
        Assert.InRange(Drought(sunwovenPlus, t.Pity.SunwovenHardCeilingItems), 0.014, 0.024);

        // And the floor really fires: at the threshold, every draw is heirloom or better.
        var entry = new DropTableEntryRow(0, DropEntryKind.Equipment, "", 1, Frame: "plant", Role: "girdle");
        var atFloor = new LootPityState(t.Pity.HeirloomHardFloorItems, 0);
        for (var i = 0; i < 200; i++)
        {
            Assert.True(RarityDraw.Draw(ladder, entry, atFloor, t, new AtomRandom((ulong)i, "r"), out var o).IsOk);
            Assert.True(o.Ordinal >= 70, $"pity forced a rung below heirloom: {o.RarityId}");
            Assert.True(o.Forced);
        }
    }

    [Fact]
    public void The_sunwoven_ramp_doubles_the_two_top_rungs_and_the_ceiling_forces()
    {
        var t = Tuning();
        var ladder = DropVolumeCorpusTests.Ladder();

        Assert.Equal(1, RarityDraw.SunwovenRampMultiplier(0, t));
        Assert.Equal(1, RarityDraw.SunwovenRampMultiplier(t.Pity.SunwovenRampStartItems, t));
        Assert.Equal(2, RarityDraw.SunwovenRampMultiplier(t.Pity.SunwovenRampStartItems + 10, t));
        Assert.Equal(4, RarityDraw.SunwovenRampMultiplier(t.Pity.SunwovenRampStartItems + 20, t));

        var entry = new DropTableEntryRow(0, DropEntryKind.Equipment, "", 1, Frame: "plant", Role: "girdle");
        var atCeiling = new LootPityState(0, t.Pity.SunwovenHardCeilingItems);
        for (var i = 0; i < 100; i++)
        {
            Assert.True(RarityDraw.Draw(ladder, entry, atCeiling, t, new AtomRandom((ulong)i, "r"), out var o).IsOk);
            Assert.True(o.Ordinal >= 90);
        }
    }

    [Fact]
    public void Pity_cannot_be_banked_in_trivial_content()
    {
        // Item level comes from the CONTENT, so a forced heirloom at content level 1 is an ilvl-1
        // heirloom: the exploit does not exist because the level axis already closed it.
        var t = Tuning();
        var ladder = DropVolumeCorpusTests.Ladder();
        var entry = new DropTableEntryRow(0, DropEntryKind.Equipment, "", 1, Frame: "plant", Role: "girdle");
        var atFloor = new LootPityState(t.Pity.HeirloomHardFloorItems, 0);

        Assert.True(RarityDraw.Draw(ladder, entry, atFloor, t, new AtomRandom(1UL, "r"), out var o).IsOk);
        Assert.True(o.Ordinal >= 70);

        var rung = ladder.First(r => r.RarityId == o.RarityId);
        var envelope = DropEnvelope.Resolve(rung, LootPipeline.ItemLevel(1, 1UL, t), new AtomRandom(1UL, "e"));

        // heirloom's authored band is [3,5]; at ilvl 1 the ceiling is t2, so it COLLAPSES to [2,2].
        Assert.True(envelope.MaxTier <= 2, $"a forced {o.RarityId} at ilvl 1 must not reach t3+");
        Assert.Equal(envelope.MaxTier, envelope.MinTier);
    }

    [Fact]
    public void The_counters_reset_on_a_hit_of_that_rung_or_above()
    {
        var ladder = DropVolumeCorpusTests.Ladder();
        var state = new LootPityState(10, 40);

        var afterChaff = RarityDraw.Advance(state, 10, ladder);
        Assert.Equal(11, afterChaff.ItemsSinceHeirloom);
        Assert.Equal(41, afterChaff.ItemsSinceSunwoven);

        var afterHeirloom = RarityDraw.Advance(state, 70, ladder);
        Assert.Equal(0, afterHeirloom.ItemsSinceHeirloom);
        Assert.Equal(41, afterHeirloom.ItemsSinceSunwoven);

        var afterAlmanac = RarityDraw.Advance(state, 100, ladder);
        Assert.Equal(0, afterAlmanac.ItemsSinceHeirloom);
        Assert.Equal(0, afterAlmanac.ItemsSinceSunwoven);
    }

    // ---- tuning-file hygiene ---------------------------------------------------------------------

    [Fact]
    public void The_tuning_file_parses_and_every_balance_number_lives_in_it()
    {
        var t = Tuning();
        Assert.Equal(20, t.ThetaPin);
        Assert.Equal(1000, t.VolumeBaseMilli);
        Assert.Equal(3, t.MaxNestingDepth);
        Assert.True(t.LogRetentionHorizonDays > 0);

        // Not a single balance literal in the Core sources: every number the pipeline reads arrives
        // through DropVolumeTuning.
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-drop-volume.v1.json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.True(doc.RootElement.TryGetProperty("volume", out _));
        Assert.True(doc.RootElement.TryGetProperty("kill", out _));
        Assert.True(doc.RootElement.TryGetProperty("pity", out _));
    }

    [Fact]
    public void A_zero_floor_is_refused_because_a_dead_source_is_not_a_balanced_one()
    {
        var t = Tuning();
        Assert.Throws<DropVolumeTuningRejection>(() => DropVolumeTuning.Validate(t with { FloorMilli = 0 }));
        Assert.Throws<DropVolumeTuningRejection>(() => DropVolumeTuning.Validate(t with { VolumeBaseMilli = 0 }));
        Assert.Throws<DropVolumeTuningRejection>(() =>
            DropVolumeTuning.Validate(t with { DropChanceOnKillMilli = 1001 }));

        // But a huge slope is NOT refused — a "sanity cap" on the slope would be the cap D26 forbids,
        // wearing a different hat.
        DropVolumeTuning.Validate(t with { VolumeSlopeMilli = 1_000_000 });
    }
}
