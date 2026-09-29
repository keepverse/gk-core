using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>
/// `lawn-combat-wire` T11 (spec-lawn-combat-calibration.md, 2026-09-14) — guards the CONTRACT the two
/// real shipped files (`battle-resources.v2.json`, `action-corpus-cost-templates.v2.json`) must satisfy
/// at the pin, never a pinned reading of either number: a balance pass may retune `stamina`'s regen
/// share or `kinds.basic.baseAmountAtRung1` freely, as long as the sustainable-fire inequality still
/// holds and the other four resources still carry an explicit, deliberate zero.
///
/// <para>Reads both files straight off disk through their real loaders — the same "real shipped file"
/// pattern <see cref="Actions.ActionCorpusCostTemplateTests"/> already uses for v1 — rather than the
/// ambient <c>ContractTuningTestBootstrap.DefaultBattleResources</c> fixture, which deliberately stays
/// at the pre-T11 all-zero baseline for the rest of this assembly (see
/// <c>ResourceSubTickRegenTests.BattleStaysByteIdenticalUnderThisAssemblysAllZeroRegenFixture</c>).
/// Never mutates <see cref="BattleRuleset.ConfigureResources"/>'s ambient static state — everything
/// here reads a locally-parsed <see cref="BattleResourceTuning"/> instance instead, so it is safe under
/// xUnit's parallel test collections.</para>
/// </summary>
[Trait("Category", "BalanceGuard")]
public class LawnCombatCalibrationGuardTests
{
    const int Pin = 20; // power-scale.v2.json curve.pinIndex; BaseHp(20) == 680, curve.pinValue

    /// <summary>A Peashooter's shipped attack interval (measured live 2026-09-13,
    /// spec-lawn-combat-calibration.md's own anchors table) — vanilla, unrelated to any RPG tuning
    /// file, so it is a literal here rather than something read off disk.</summary>
    const double PeashooterAttackIntervalSeconds = 1.5;

    static string RepoRoot([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                        // tests/.../Balance
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));    // repo root
    }

    static BattleResourceTuning LoadBattleResourcesV2() =>
        BattleResourceTuningLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "battle-resources.v2.json")));

    static ActionCorpusCostTemplate LoadCostTemplateV2() =>
        ActionCorpusCostTemplateLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "action-corpus-cost-templates.v2.json")));

    /// <summary>
    /// The contract itself (spec-lawn-combat-calibration.md acceptance: "cost ≤ regenPerSecond × 1.5s
    /// at the pin"). Computed from the real shipped files' own numbers — never a hardcoded expectation
    /// of 25/17 — so a future balance pass that retunes either value only fails this test if it breaks
    /// the sustainable-fire relationship, not because the numbers moved.
    /// </summary>
    [Fact]
    public void StaminaCostNeverExceedsSustainableRegenAtThePin()
    {
        var resources = LoadBattleResourcesV2();
        var costTemplate = LoadCostTemplateV2();

        var poolMax = checked(BattleRuleset.BaseHp(Pin) * resources.ShareOf("stamina")) / 1000;
        var regenPerSecond = checked(poolMax * resources.RegenShareOf("stamina")) / 1000;
        var sustainableCostCeiling = regenPerSecond * PeashooterAttackIntervalSeconds;

        var cost = costTemplate.ResolveFor(ActionKind.Basic, ActionCategory.Attack).BaseAmountAtRung1;

        Assert.True(regenPerSecond > 0, "stamina must actually regenerate — a zero rate here would " +
            "make the whole invariant vacuous (any cost satisfies cost <= 0).");
        Assert.True(cost > 0, "a swing must cost something, or there is no economy to calibrate");
        Assert.True(cost <= sustainableCostCeiling,
            $"cost ({cost}) exceeds regenPerSecond ({regenPerSecond}) * {PeashooterAttackIntervalSeconds}s " +
            $"= {sustainableCostCeiling} — continuous single-target fire would never be sustainable");
    }

    /// <summary>
    /// The scarcity side of the contract: `poise`'s original scarcity argument
    /// (battle-resources.v1.json's own now-superseded `_meta.regenIsAbsentOnPurpose`) still holds, and
    /// hunger/spirit/qi have no cost mechanism spending them yet — so all four stay an explicit,
    /// deliberate 0, never a missing row (a missing row would silently read 0 too, but for the wrong
    /// reason: BattleResourceTuningLoader.Parse enforces the block is complete once it exists at all).
    /// </summary>
    [Fact]
    public void OnlyStaminaRegeneratesEveryOtherResourceStaysExplicitlyZero()
    {
        var resources = LoadBattleResourcesV2();

        foreach (var id in new[] { "hunger", "spirit", "qi", "poise" })
            Assert.Equal(0, resources.RegenShareOf(id));

        Assert.True(resources.RegenShareOf("stamina") > 0);
    }

    /// <summary>v1 is kept on disk, untouched, for revert (this file's own `_meta.rebalance`
    /// convention) — it must still parse and still carry no regen block at all, proving
    /// `BattleResourceTuningLoader.Parse`'s absent-block default (all resources implicitly 0) is what
    /// makes reverting to v1 safe rather than a parse failure.</summary>
    [Fact]
    public void V1StillParsesWithNoRegenBlockAndDefaultsEveryShareToZero()
    {
        var v1 = BattleResourceTuningLoader.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "battle-resources.v1.json")));

        foreach (var id in new[] { "stamina", "hunger", "spirit", "qi", "poise" })
            Assert.Equal(0, v1.RegenShareOf(id));
    }
}
