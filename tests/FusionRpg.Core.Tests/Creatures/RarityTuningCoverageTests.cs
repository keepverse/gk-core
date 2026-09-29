using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using FusionRpg.Core.Creatures.Fusion;
using FusionRpg.Core.Creatures.Patron;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// seed-to-concrete T4.1 (spec-rarity-migration.md §5, §6, §7 step 4, Testing strategy) — reads the
/// REAL shipped <c>gk-core/data/tuning/*.json</c> files (not a mock bootstrap) so these checks fail the moment
/// a future edit to the actual balance surface breaks ten-rung coverage, the 1000‰ summon-rate
/// invariant, or the "no rung strictly worse than the one below" shape.
/// </summary>
public class RarityTuningCoverageTests
{
    static string TuningDir => Path.Combine(FindRepoRoot(), "data", "tuning");
    static string Read(string name) => File.ReadAllText(Path.Combine(TuningDir, name));

    static readonly FusionTuning Fusion = FusionTuningLoader.Parse(Read("fusion.v2.json"));
    static readonly ContractTuning Contracts = ContractTuningLoader.Parse(Read("contracts.v1.json"));
    static readonly SoulEarnTuning Souls = SoulEarnTuningLoader.Parse(Read("souls.v1.json"));
    static readonly PatronTuning Patron = PatronTuningLoader.Parse(Read("patron.v1.json"));
    static readonly SummoningTuning Summoning = SummoningTuningLoader.Parse(Read("summoning.v1.json"));

    [Fact]
    public void Every_rarity_keyed_tuning_table_has_ten_entries()
    {
        // Reads CreatureRarityLadder.RungCount live rather than the literal 10 it happens to be today
        // (population-pin SE3.3, 2026-09-19) -- a rung added to the closed rarity ladder moves this
        // assertion with it instead of needing a second manual edit.
        Assert.Equal(CreatureRarityLadder.RungCount, Fusion.StarCap.Count);
        Assert.Equal(CreatureRarityLadder.RungCount, Fusion.SlotsByRarity.Count);
        Assert.Equal(CreatureRarityLadder.RungCount, Contracts.BaseUpkeepPerDay.Count);
        Assert.Equal(CreatureRarityLadder.RungCount, Contracts.RitualPriceSouls.Count);
        Assert.Equal(CreatureRarityLadder.RungCount, Souls.DiscoveryDelta.Count);
        Assert.Equal(CreatureRarityLadder.RungCount, Patron.RarityBaseMilli.Count);

        foreach (var rung in CreatureRarityLadder.All)
        {
            Assert.True(Fusion.StarCap.ContainsKey(rung), $"StarCap missing {rung}");
            Assert.True(Fusion.SlotsByRarity.ContainsKey(rung), $"SlotsByRarity missing {rung}");
            Assert.True(Contracts.BaseUpkeepPerDay.ContainsKey(rung), $"BaseUpkeepPerDay missing {rung}");
            Assert.True(Contracts.RitualPriceSouls.ContainsKey(rung), $"RitualPriceSouls missing {rung}");
            Assert.True(Souls.DiscoveryDelta.ContainsKey(rung), $"DiscoveryDelta missing {rung}");
            Assert.True(Patron.RarityBaseMilli.ContainsKey(rung), $"RarityBaseMilli missing {rung}");
        }

        // RecipeCost is the one NAMED exception (spec §7 step 4 / CreatureRecipeCatalog.OutputEligibilityFloor):
        // it only covers rungs a fusion OUTPUT can occupy — Cultivated..Almanac, seven rungs, not ten.
        Assert.Equal(
            CreatureRarityLadder.RungCount - (int)CreatureRecipeCatalog.OutputEligibilityFloor,
            Fusion.RecipeCost.Count);
        foreach (var rung in CreatureRarityLadder.All.Where(r => CreatureRarityLadder.AtLeast(r, CreatureRecipeCatalog.OutputEligibilityFloor)))
            Assert.True(Fusion.RecipeCost.ContainsKey(rung), $"RecipeCost missing {rung}");

        // WAVE F2.3: InheritCostByRarity covers the SAME rungs RecipeCost does — the acceptance
        // criterion's own wording ("covers every rung recipeCost covers"), a different lookup key
        // (a fusion PICK's own source rarity) over the identical rung set, not a second ten-rung table.
        Assert.Equal(Fusion.RecipeCost.Count, Fusion.InheritCostByRarity.Count);
        foreach (var rung in Fusion.RecipeCost.Keys)
            Assert.True(Fusion.InheritCostByRarity.ContainsKey(rung), $"InheritCostByRarity missing {rung}");
    }

    /// <summary>The roller's NATURAL (no hard pity, no soft-ramp — pull 0) distribution must still sum
    /// to exactly 1000‰: eight named per-mille rates plus Sunwoven's un-ramped base rate, with Chaff
    /// as the implicit remainder (spec §5: "ten rates must still sum to 1000‰"). The top rung must be
    /// naturally reachable, not only via hard pity (Q15's own point: "a naive spread makes the top
    /// rung a rounding error").</summary>
    [Fact]
    public void Summon_rates_sum_to_1000_permille()
    {
        var r = Summoning.Roller;
        var namedSum = r.AlmanacPerMille + r.SunwovenBasePerMille + r.HeirloomPerMille + r.FirstseedPerMille +
                       r.ChimericPerMille + r.FusedPerMille + r.CultivatedPerMille + r.GraftedPerMille +
                       r.SproutPerMille;
        Assert.True(namedSum > 0 && namedSum < 1000,
            $"named rates must leave room for Chaff's implicit remainder, got sum={namedSum}");
        var chaffShare = 1000 - namedSum;
        Assert.True(chaffShare > 0, "Chaff's implicit remainder must be positive");
        Assert.Equal(1000, namedSum + chaffShare);

        Assert.True(r.AlmanacPerMille > 0, "the true top rung must be naturally reachable, not pity-only");
    }

    /// <summary>ssot-rarity.md §8.6's named failure mode: interpolating StarCap/SlotsByRarity/RecipeCost
    /// naively across ten rungs can make a mid rung strictly worse than the rung directly below it —
    /// a lower star cap, fewer trait slots, or (for a fusion output) a lower soul cost for a supposedly
    /// better result. Every one of the three tables must be non-decreasing up the ladder.</summary>
    [Fact]
    public void No_rung_is_strictly_worse_than_the_one_below()
    {
        var rungs = CreatureRarityLadder.All;
        for (var i = 1; i < rungs.Count; i++)
        {
            var below = rungs[i - 1];
            var here = rungs[i];
            Assert.True(Fusion.StarCap[here] >= Fusion.StarCap[below],
                $"StarCap regressed: {here}={Fusion.StarCap[here]} < {below}={Fusion.StarCap[below]}");
            Assert.True(Fusion.SlotsByRarity[here] >= Fusion.SlotsByRarity[below],
                $"SlotsByRarity regressed: {here}={Fusion.SlotsByRarity[here]} < {below}={Fusion.SlotsByRarity[below]}");
        }

        // RecipeCost only exists for Cultivated..Almanac — souls must climb monotonically up that
        // sub-range (a higher-rung fusion output must never cost fewer souls than a lower one).
        var recipeRungs = rungs.Where(r => Fusion.RecipeCost.ContainsKey(r)).ToList();
        for (var i = 1; i < recipeRungs.Count; i++)
        {
            var below = recipeRungs[i - 1];
            var here = recipeRungs[i];
            Assert.True(Fusion.RecipeCost[here].Souls >= Fusion.RecipeCost[below].Souls,
                $"RecipeCost souls regressed: {here}={Fusion.RecipeCost[here].Souls} < {below}={Fusion.RecipeCost[below].Souls}");
        }

        // WAVE F2.3: an inherited pick from a higher-rarity sacrifice must never cost fewer souls
        // than one from a lower-rarity sacrifice, same monotonicity rule as RecipeCost.
        var inheritRungs = rungs.Where(r => Fusion.InheritCostByRarity.ContainsKey(r)).ToList();
        for (var i = 1; i < inheritRungs.Count; i++)
        {
            var below = inheritRungs[i - 1];
            var here = inheritRungs[i];
            Assert.True(Fusion.InheritCostByRarity[here] >= Fusion.InheritCostByRarity[below],
                $"InheritCostByRarity regressed: {here}={Fusion.InheritCostByRarity[here]} < {below}={Fusion.InheritCostByRarity[below]}");
        }
    }

    /// <summary>WAVE F2.3's own acceptance line: a pick's cost is read from the PICK's own source
    /// rarity, never the fusion output's — proven here as "the two tables are genuinely independent
    /// lookups, not one aliased as the other," since <c>ExecuteFusion</c> (F2.4) does not exist yet to
    /// prove the full "fuse two differently-rarity'd sacrifices" scenario end to end.</summary>
    [Fact]
    public void InheritCostByRarity_is_a_real_second_table_not_an_alias_of_RecipeCost()
    {
        Assert.NotSame(Fusion.RecipeCost, Fusion.InheritCostByRarity);
        // Today's shipped values happen to match RecipeCost's own souls (F2.3's own starting-value
        // choice) — same numbers, but read through a different key (a pick's own source rarity), a
        // fact only meaningful once F2.4 wires two independently-rarity'd lookups against it.
        foreach (var rung in Fusion.RecipeCost.Keys)
            Assert.Equal(Fusion.RecipeCost[rung].Souls, Fusion.InheritCostByRarity[rung]);
    }

    /// <summary>spec §6 / Q15: the pity guard fields must name the RUNG they guard, not a leftover
    /// four-value name ("epic"/"legendary") that would leave a reader guessing which rarity it means
    /// after the migration.</summary>
    [Fact]
    public void Pity_guards_name_their_rungs()
    {
        var names = typeof(RollerTuning).GetProperties().Select(p => p.Name).ToList();
        Assert.Contains("HeirloomHardPity", names);
        Assert.Contains("SunwovenSoftStart", names);
        Assert.Contains("SunwovenHardPity", names);
        Assert.DoesNotContain(names, n => n.Contains("Epic", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("Legendary", StringComparison.OrdinalIgnoreCase));

        // spec §6: the two guards sit at ssot-rarity.md §3.3's own ordinal scale — rung index * 10,
        // one-based — where Heirloom = 70 and Sunwoven = 90. The C# enum's own (zero-based)
        // declaration-order value is a DIFFERENT scale (CreatureRarityLadder's internal one); this
        // checks the mapping between the two holds, not that either scale's raw number is 70/90.
        Assert.Equal(70, ((int)CreatureRarity.Heirloom + 1) * 10);
        Assert.Equal(90, ((int)CreatureRarity.Sunwoven + 1) * 10);
    }

    static string FindRepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so a `gk-core/data/tuning` or `src/` read stays valid once the injector source moves
        // to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }
}
