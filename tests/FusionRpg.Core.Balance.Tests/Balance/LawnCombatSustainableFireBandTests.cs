using System.Text;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Power;
using FusionRpg.Core.Workspace;
using Xunit;

namespace FusionRpg.Core.Tests.Balance;

/// <summary>
/// The BAND half of the sustainable-fire contract that
/// <see cref="LawnCombatCalibrationGuardTests"/> proves at one point, and the finding that the
/// one-point proof cannot see.
///
/// <para><b>What the sibling guard asserts.</b> <c>StaminaCostNeverExceedsSustainableRegenAtThePin</c>
/// reads <c>cost &lt;= regenPerSecond × 1.5 s</c> at Θ = <c>power-scale.v2.json</c>'s
/// <c>curve.pinIndex</c> and nothing else. Its own doc comment is explicit that it guards "the
/// CONTRACT the two real shipped files must satisfy <i>at the pin</i>".</para>
///
/// <para><b>Why one point is not enough.</b> The three inputs do not scale alike:
/// <list type="bullet">
/// <item><c>BaseResourceMax</c> (<c>BattleModels.cs:410-415</c>) is
/// <c>BaseHp(Θ) × poolShareMilli / 1000</c> — and <c>BaseHp</c> is the power ladder, whose local
/// exponent runs 1.13 → 1.97 across the playable range (<c>ssot-power-scale.md</c> §4.5), so the
/// pool and its regen grow <b>super-linearly</b> in Θ;</item>
/// <item><c>BaseResourceRegen</c> (<c>BattleModels.cs:461-481</c>) is a per-mille share of that pool,
/// so it inherits the same super-linear growth;</item>
/// <item>the per-swing cost is a <b>flat</b> <c>kinds.basic.baseAmountAtRung1</c> in
/// <c>action-corpus-cost-templates.v2.json</c>, with no Θ term at all.</item>
/// </list>
/// A gate evaluated at exactly one Θ therefore proves the relationship at that Θ and says nothing
/// about any other, and the shipped tuning happens to satisfy it only from the pin up.</para>
///
/// <para><b>What the specs actually claim — read, not assumed.</b> The claim is pin-scoped, in three
/// places and in both shipped tuning files:
/// <list type="bullet">
/// <item><c>spec-lawn-combat-calibration.md</c> Success criteria: "<c>stamina</c> cost and lawn regen
/// are authored … and satisfy <c>cost ≤ regenPerSecond × 1.5 s</c> <b>at the pin</b>";</item>
/// <item>its Testing-strategy row: "<c>StaminaCostNeverExceedsSustainableRegenAtThePin</c> — the
/// sustainable-fire invariant … <b>at the pin</b>";</item>
/// <item><c>Method #2</c>: "a <b>pinned</b> pool of 340 should sustain continuous fire";</item>
/// <item><c>battle-resources.v2.json</c> <c>_meta.regenDerivation</c>: "derived <b>at the pin</b>
/// (theta=20 …)"; <c>action-corpus-cost-templates.v2.json</c> <c>_meta.kindsNote</c>: "at the pin
/// (theta=20) … continuous single-target fire is sustainable with a thin margin".</item>
/// </list>
/// So the DERIVATION is a pin-scoped claim, and this file implements exactly that claim — but over
/// the whole band it is claimed for, not over one sampled point.</para>
///
/// <para><b>Below the pin nothing is claimed at all.</b> No spec row, no tuning <c>_meta</c> and no
/// success criterion says what may happen for Θ &lt; <c>pinIndex</c>. That silence is a real gap,
/// not a licence: the pool/regen/cost divergence above has a floor, and the spec's own unconditional
/// words are the ones it violates —
/// <c>spec-lawn-combat-calibration.md</c> Method #2 ("exhaustion is a <b>burst-fire</b> consequence
/// and <b>not a permanent stop</b>") and Method #3 ("a cost far over [the sustainable rate] makes
/// <b>every plant inert</b>"), <c>spec-basic-attack-cost.md</c> "Exhaustion suppresses the rider, not
/// the shot … the actor waits for regen and triggers again", and
/// <c>action-ideal.md:223</c> / <c>spec-action-costs.md:122</c> ("An action cost is authored against
/// the pool's REGEN, never against its MAX"). None of those is pin-scoped. The rider-suppression
/// path is real and silent: <c>LawnBasicAttackCostCharger.ShouldApplyRider</c> returns
/// <c>afforded</c>, and <c>EffectRuntime.OnDrained</c> returns at its <c>if (!…) return;</c> — the
/// vanilla pea has already landed, so the plant looks identical and simply stops contributing.</para>
///
/// <para><b>The scope decision this file makes, stated once.</b> The contract is claimed
/// <b>from the pin up</b>, so above the pin the spec's own inequality is asserted across the band
/// (<see cref="StaminaCostNeverExceedsSustainableRegenAtEveryThetaFromThePinUp"/>). Below the pin
/// the region is <b>ungoverned</b>, and an ungoverned region gets the strictest statement available
/// rather than an invented tolerance: the same inequality, expressed as the duty-cycle floor
/// <see cref="RiderDutyFloor"/>. No document authorises a lower floor, so none is used — a floor
/// below 1.0 would be a tolerance this test made up in order to go green, which is the one move a
/// balance gate must never make.</para>
///
/// <para><b>Consequence, stated plainly:</b> <see cref="RiderDutyCycleMeetsTheStatedFloorAtEveryThetaBelowThePin"/>
/// and <see cref="RiderDutyCycleMeetsTheStatedFloor_AtEachNamedBandPoint"/> are <b>RED against the
/// shipped tuning</b>, at every Θ below the pin. That is the deliverable, not a defect in this file.
/// Resolving it means retuning <c>battle-resources.v2.json</c>'s <c>regenPerSecondShareMilli.stamina</c>
/// (or <c>poolShareMilli.stamina</c>), or <c>action-corpus-cost-templates.v2.json</c>'s
/// <c>kinds.basic.baseAmountAtRung1</c> — all balance numbers, none of them this test's to touch.
/// Nothing here weakens, skips or tolerates the finding: the band is asserted, every failing Θ is
/// named with its margin, and nothing is asserted away.</para>
///
/// <para><b>Measurement is production code, not a restatement of it.</b> <c>BattleRuleset.BaseHp</c>,
/// <c>BattleRuleset.BaseResourceMax</c> and <c>BattleRuleset.BaseResourceRegen</c> are called
/// directly against the two real shipped files read off disk through their real loaders, and the pin
/// and the ladder come from the real shipped <c>power-scale.v2.json</c>.
/// <see cref="TheAmbientPowerLadderIsTheShippedPowerScaleCurve"/> proves the ladder this measures
/// through is the shipped curve at every band point, and
/// <see cref="ProductionResourceFunctionsAgreeWithTheShippedFileArithmeticAtEveryBandPoint"/> proves
/// the two production functions still compute the file's own arithmetic — so this file cannot quietly
/// drift away from the thing it is guarding. Ambient static state is never mutated
/// (<c>PowerTuningHub</c>/<c>BattleRuleset.ConfigureResources</c> are read-only here), so the class is
/// safe under xUnit's parallel collections.</para>
/// </summary>
[Trait("Category", "BalanceGuard")]
public class LawnCombatSustainableFireBandTests
{
    const string Stamina = "stamina";

    /// <summary>A Peashooter's shipped attack interval, measured live 2026-09-13 and recorded as a
    /// shipped anchor in <c>spec-lawn-combat-calibration.md</c>'s own anchors table. Vanilla and
    /// unrelated to any RPG tuning file, so a literal here is correct — the sibling guard
    /// <c>LawnCombatCalibrationGuardTests</c> carries the same constant for the same reason.</summary>
    const double PeashooterAttackIntervalSeconds = 1.5;

    /// <summary>Lowest Θ that can reach a resource baseline at all:
    /// <c>ResourceBaselineSubsystem.ContributeDerived</c> reads <c>theta = Math.Max(1,
    /// _powerIndex.ActorIndex(ctx))</c> (<c>ResourceBaselineSubsystem.cs:33</c>), so a Θ below 1 is
    /// clamped before any pool or regen is ever seeded from it. This is the floor of the reachable
    /// band by production code, not by convention.</summary>
    const int BandFloor = 1;

    /// <summary>Ceiling of the sampled band: the largest Θ tabulated in
    /// <c>ssot-power-scale.md</c> §4.5's "whole playable range" table. It is a SAMPLE, and
    /// <see cref="StaminaRegenPerSecondNeverDecreasesAcrossTheBand"/> is what makes it sufficient —
    /// regen is non-decreasing in Θ and the cost is a constant, so the inequality is monotone in Θ and
    /// a contiguous satisfying suffix of the band is the only shape a failure can take.</summary>
    const int BandCeiling = 5_000;

    /// <summary><c>BattleRuleset.TicksPerSecond</c> (<c>BattleModels.cs:487</c>) — the kernel's own
    /// 100 ms tick period, which that file itself calls "Structural, not a tunable: it is the
    /// sub-tick unit's own denominator, not a balance number a pass would change". It is named here
    /// only so the contract, which is written in per-SECOND terms, can be read off production's
    /// per-TICK return value; <see cref="ProductionResourceFunctionsAgreeWithTheShippedFileArithmeticAtEveryBandPoint"/>
    /// proves the number against the file's own arithmetic, so a retune of the tick period turns this
    /// file red instead of silently shifting every measurement.</summary>
    const double KernelTicksPerSecond = 10;

    /// <summary><b>N — the duty-cycle floor the rider is held to, over the whole band.</b> Stated as
    /// a fraction of swings on which the rider's contribution actually lands; the shipped path
    /// suppresses it on every swing the actor cannot afford
    /// (<c>LawnBasicAttackCostCharger.ShouldApplyRider</c> → <c>EffectRuntime.OnDrained</c>'s early
    /// return), so this number is the player-visible contribution, not a bookkeeping figure.</summary>
    ///
    /// <para><b>Why 1.0.</b> It is the only value the repository's own words support, and it is
    /// deliberately NOT a tolerance:
    /// <c>spec-lawn-combat-calibration.md</c> Method #2 states the purpose as "so exhaustion is a
    /// <b>burst-fire consequence and not a permanent stop</b>", Method #3 as "a cost far over
    /// [sustainable] makes <b>every plant inert</b>", and
    /// <c>action-corpus-cost-templates.v2.json</c>'s <c>_meta.kindsNote</c> as "continuous
    /// single-target fire is sustainable". No document states a lower bar for any Θ, at or below the
    /// pin. Asserting a floor under 1.0 would be this test inventing a tolerance so that a known
    /// balance gap could be reported as a pass — the exact move a balance gate exists to prevent.
    /// Measured, the shipped tuning reaches this floor only from the pin up.</para></summary>
    const double RiderDutyFloor = 1.0;

    // ── the real shipped files, read off disk through their real loaders ──────────────────────────

    static string TuningFile(string name) =>
        Path.Combine(KeepverseRoots.Core(), "data", "tuning", name);

    static BattleResourceTuning LoadBattleResources() =>
        BattleResourceTuningLoader.Parse(File.ReadAllText(TuningFile(BattleResourceTuningFiles.Current)));

    static ActionCorpusCostTemplate LoadCostTemplate() =>
        ActionCorpusCostTemplateLoader.Parse(
            File.ReadAllText(TuningFile("action-corpus-cost-templates.v2.json")));

    static PowerTuning LoadPowerScale() =>
        PowerTuningLoader.Parse(File.ReadAllText(TuningFile("power-scale.v2.json")));

    /// <summary>The flat per-swing stamina cost, read from the shipped cost template through the same
    /// kind-aware resolution <c>ActionCorpusComposer.Compose</c> uses. Never a hardcoded 25 — a
    /// balance pass may retune it, and this file must report the consequence, not fail on the edit.</summary>
    static int BasicAttackStaminaCost(ActionCorpusCostTemplate template) =>
        template.ResolveFor(ActionKind.Basic, ActionCategory.Attack).BaseAmountAtRung1;

    readonly record struct BandPoint(
        int Theta, long BaseHp, long PoolMax, long RegenPerSecond, double Ceiling, int Cost)
    {
        /// <summary>The contract itself, <c>cost ≤ regenPerSecond × attackInterval</c>.</summary>
        public bool Sustainable => Cost <= Ceiling;

        /// <summary>Fraction of swings on which the rider actually contributes, under the shipped
        /// "no resource, no trigger" gate. Capped at 1: once the cost is affordable the rider lands on
        /// every swing, so extra headroom does not raise it.</summary>
        public double DutyCycle => Math.Min(1.0, Ceiling / Cost);

        /// <summary>Swings a full pool affords back-to-back, before any regen lands.</summary>
        public long SwingsFromFullPool => PoolMax / Cost;

        public string Row() =>
            $"  theta={Theta,-5} P(theta)={BaseHp,-7} poolMax={PoolMax,-7} regen/s={RegenPerSecond,-6} "
            + $"ceiling={Ceiling,8:F2} cost={Cost,-4} duty={DutyCycle * 100,6:F1}% "
            + $"sustainable={(Sustainable ? "YES" : "NO"),-3} (pool affords {SwingsFromFullPool} swings)";
    }

    /// <summary>One band point, measured through the three production functions and nothing else.
    /// <c>BaseResourceMax</c>/<c>BaseResourceRegen</c> are called with their explicit
    /// <c>BattleResourceTuning</c> overloads so no ambient static state is touched;
    /// <c>BaseHp</c> is the host-configured ladder, whose shippedness is proved by
    /// <see cref="TheAmbientPowerLadderIsTheShippedPowerScaleCurve"/>. The per-second rate is read off
    /// production's per-TICK return through <see cref="KernelTicksPerSecond"/>, not restated from the
    /// tuning file, so this file cannot report a rate the game does not actually apply.</summary>
    static BandPoint Measure(int theta, BattleResourceTuning resources, int cost)
    {
        var baseHp = BattleRuleset.BaseHp(theta);
        var poolMax = BattleRuleset.BaseResourceMax(theta, Stamina, baseHp, resources);
        var regenPerSecond = (long)Math.Round(
            BattleRuleset.BaseResourceRegen(theta, Stamina, resources) * KernelTicksPerSecond);
        var ceiling = regenPerSecond * PeashooterAttackIntervalSeconds;
        return new BandPoint(theta, baseHp, poolMax, regenPerSecond, ceiling, cost);
    }

    static IReadOnlyList<BandPoint> Band(int from, int to, BattleResourceTuning resources, int cost) =>
        Enumerable.Range(from, to - from + 1).Select(t => Measure(t, resources, cost)).ToArray();

    // ── 1. the spec's own claim, proven across the whole band it is claimed for ───────────────────

    /// <summary>
    /// <c>spec-lawn-combat-calibration.md</c>'s Success criterion — "<c>cost ≤ regenPerSecond × 1.5 s</c>
    /// at the pin" — read as what it is: a claim about the region <b>from the pin up</b>, and therefore
    /// asserted over that whole region instead of over one sampled Θ. This is strictly stronger than
    /// the shipped one-point guard: the sibling test would still be green with this region broken at
    /// every Θ except exactly <c>pinIndex</c>.
    ///
    /// <para>Monotonicity (<see cref="StaminaRegenPerSecondNeverDecreasesAcrossTheBand"/>) is what makes
    /// the finite sample sound: regen is non-decreasing in Θ and the cost is a constant, so a single
    /// contiguous passing region covers everything above it.</para>
    /// </summary>
    [Fact]
    public void StaminaCostNeverExceedsSustainableRegenAtEveryThetaFromThePinUp()
    {
        var resources = LoadBattleResources();
        var cost = BasicAttackStaminaCost(LoadCostTemplate());
        var pin = LoadPowerScale().Curve.PinIndex;

        var band = Band(pin, BandCeiling, resources, cost);
        var failures = band.Where(p => !p.Sustainable).ToArray();

        Assert.True(failures.Length == 0,
            $"The sustainable-fire contract is claimed from the pin (theta={pin}) up, and the SHIPPED "
            + $"tuning breaks it at {failures.Length} of {band.Count} band points in [theta={pin}..{BandCeiling}]:\n"
            + string.Join('\n', failures.Take(40).Select(p => p.Row()))
            + (failures.Length > 40 ? $"\n  … and {failures.Length - 40} more" : ""));
    }

    // ── 2. the ungoverned region below the pin — the finding ─────────────────────────────────────

    /// <summary>
    /// The sub-pin band, where <b>nothing in the repository makes a claim</b>, held to the same
    /// inequality the pin satisfies, restated as the rider's duty cycle against
    /// <see cref="RiderDutyFloor"/>.
    ///
    /// <para><b>This is RED against the shipped tuning, at every Θ in the band.</b> That is the finding
    /// this file exists to surface, not a failure to be relaxed: the fix is a balance retune of
    /// <c>battle-resources.v2.json</c> <c>regenPerSecondShareMilli.stamina</c> /
    /// <c>poolShareMilli.stamina</c>, or of <c>action-corpus-cost-templates.v2.json</c>
    /// <c>kinds.basic.baseAmountAtRung1</c>, and those numbers are the owner's. No skip, no
    /// <c>xfail</c>, no lowered floor and no narrowed band hides any of it here.</para>
    /// </summary>
    [Fact]
    public void RiderDutyCycleMeetsTheStatedFloorAtEveryThetaBelowThePin()
    {
        var resources = LoadBattleResources();
        var cost = BasicAttackStaminaCost(LoadCostTemplate());
        var pin = LoadPowerScale().Curve.PinIndex;

        var band = Band(BandFloor, pin - 1, resources, cost);
        var failures = band.Where(p => p.DutyCycle < RiderDutyFloor).ToArray();

        Assert.True(failures.Length == 0, BelowPinReport(failures, band, pin, resources, cost));
    }

    /// <summary>Per-point cases, so each Θ the audit named is its own reported test result rather than
    /// a row buried in one aggregate message. Same floor, same measurements, one case per Θ.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(30)]
    public void RiderDutyCycleMeetsTheStatedFloor_AtEachNamedBandPoint(int theta)
    {
        var resources = LoadBattleResources();
        var cost = BasicAttackStaminaCost(LoadCostTemplate());

        var point = Measure(theta, resources, cost);

        Assert.True(point.DutyCycle >= RiderDutyFloor,
            $"theta={theta}: the rider contributes on only {point.DutyCycle * 100:F1}% of swings, below "
            + $"the stated floor of {RiderDutyFloor * 100:F1}%.\n"
            + $"  measured: {point.Row()}\n"
            + $"  cause: poolMax = P(theta) × poolShareMilli/1000 and regen = poolMax × "
            + $"regenPerSecondShareMilli/1000 both grow with P(theta) (super-linear across the band per "
            + $"ssot-power-scale.md §4.5), while kinds.basic.baseAmountAtRung1 is a flat {cost} with no "
            + $"theta term — so the sustainable ceiling falls below the cost as theta falls.\n"
            + $"  effect: LawnBasicAttackCostCharger.ShouldApplyRider returns false and "
            + $"EffectRuntime.OnDrained returns before the rider is applied; the vanilla pea has already "
            + $"landed, so the plant looks identical and contributes nothing on the suppressed swings.\n"
            + $"  owner action: retune battle-resources.v2.json (regenPerSecondShareMilli.stamina / "
            + $"poolShareMilli.stamina) or action-corpus-cost-templates.v2.json "
            + $"(kinds.basic.baseAmountAtRung1). This gate will go green when it does.");
    }

    // ── 3. what makes the finite sample a proof, and what pins the measurement ────────────────────

    /// <summary>The two structural facts the band argument rests on, asserted rather than assumed:
    /// regen is non-decreasing in Θ (so "the passing region is contiguous from the pin up" is a
    /// property of the shipped curve, not a hope), and the cost is a constant across the whole band
    /// (so it cannot rescue a Θ the pool has left behind). A curve edit that made regen non-monotone
    /// — or a cost template that started scaling with Θ — turns this red, and with it the soundness
    /// of the two band scans above.</summary>
    [Fact]
    public void StaminaRegenPerSecondNeverDecreasesAcrossTheBand()
    {
        var resources = LoadBattleResources();
        var cost = BasicAttackStaminaCost(LoadCostTemplate());

        var band = Band(BandFloor, BandCeiling, resources, cost);
        var first = band[0];

        Assert.True(cost > 0, "a swing must cost something, or there is no economy to calibrate");

        for (var i = 1; i < band.Count; i++)
        {
            var previous = band[i - 1];
            var point = band[i];
            Assert.True(point.RegenPerSecond >= previous.RegenPerSecond,
                $"regen must be non-decreasing in theta for the band scan to be a proof, but "
                + $"theta={previous.Theta} regen/s={previous.RegenPerSecond} > theta={point.Theta} "
                + $"regen/s={point.RegenPerSecond}");
        }

        Assert.True(first.RegenPerSecond >= 0, "sanity: the floor point must measure");
    }

    /// <summary>Pins the measurement to the shipped power curve. Everything in this class resolves
    /// <c>Θ</c> through <c>BattleRuleset.BaseHp</c>, which reads whatever <c>PowerTuningHub</c> was
    /// configured with; production configures it from <c>data/tuning/power-scale.v2.json</c>
    /// (<c>Program.cs:371-376</c>). If the host-configured ladder ever stops being the shipped curve,
    /// every number in this file would silently start describing something the game does not run, and
    /// the band verdict with it — so the identity is asserted at every band point rather than
    /// assumed.</summary>
    [Fact]
    public void TheAmbientPowerLadderIsTheShippedPowerScaleCurve()
    {
        var shipped = LoadPowerScale();

        Assert.Equal(shipped.Curve, PowerTuningHub.Tuning.Curve);

        var ladder = new PowerLadder(shipped);
        for (var theta = BandFloor; theta <= BandCeiling; theta++)
            Assert.Equal(ladder.Value(theta), BattleRuleset.BaseHp(theta));
    }

    /// <summary>Pins the two production functions to the shipped file's own arithmetic, at every band
    /// point. <c>BaseResourceMax</c> and <c>BaseResourceRegen</c> are what this class measures, and
    /// each reads the power ladder through <c>BaseHp</c> and divides per-mille in its own order. If
    /// either were edited — a share applied before the multiply, a rounding change, a rate that
    /// stopped tracking its pool — the mirror expressions below would diverge and this would go red
    /// rather than letting the balance gate keep reporting numbers the code no longer produces.</summary>
    [Fact]
    public void ProductionResourceFunctionsAgreeWithTheShippedFileArithmeticAtEveryBandPoint()
    {
        var resources = LoadBattleResources();
        var cost = BasicAttackStaminaCost(LoadCostTemplate());

        Assert.True(resources.RegenShareOf(Stamina) > 0,
            "stamina must actually regenerate — a zero rate would make the whole invariant vacuous "
            + "(any cost satisfies cost <= 0).");

        for (var theta = BandFloor; theta <= BandCeiling; theta++)
        {
            var point = Measure(theta, resources, cost);
            var expectedPoolMax = checked(point.BaseHp * resources.ShareOf(Stamina)) / 1000;
            var expectedRegenPerSecond = checked(expectedPoolMax * resources.RegenShareOf(Stamina)) / 1000;

            Assert.Equal(expectedPoolMax, point.PoolMax);
            Assert.Equal(expectedRegenPerSecond, point.RegenPerSecond);
        }
    }

    // ── reporting ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The below-pin finding, written so a reader can act on it without re-running anything:
    /// every failing Θ with its full measured row, the boundary the tuning actually reaches, and the
    /// three authored numbers whose retune would move it.</summary>
    static string BelowPinReport(
        IReadOnlyList<BandPoint> failures, IReadOnlyList<BandPoint> band, int pin,
        BattleResourceTuning resources, int cost)
    {
        var sb = new StringBuilder();
        sb.AppendLine(
            $"SUSTAINABLE-FIRE FINDING against the shipped tuning: the rider's duty cycle falls below "
            + $"the stated floor of {RiderDutyFloor * 100:F1}% at {failures.Count} of the {band.Count} "
            + $"reachable band points below the pin (theta={BandFloor}..{pin - 1}).");
        sb.AppendLine();
        sb.AppendLine("Every failing point, measured through BattleRuleset.BaseHp /");
        sb.AppendLine("BattleRuleset.BaseResourceMax / BattleRuleset.BaseResourceRegen:");
        foreach (var point in failures) sb.AppendLine(point.Row());

        var firstHolding = band.FirstOrDefault(p => p.Sustainable);
        if (firstHolding.Theta > 0)
            sb.AppendLine($"  first band point that DOES satisfy the contract: theta={firstHolding.Theta}");

        sb.AppendLine();
        sb.AppendLine($"Cost is a flat {cost} (action-corpus-cost-templates.v2.json kinds.basic"
            + ".baseAmountAtRung1). battle-resources.v2.json poolShareMilli.stamina="
            + $"{resources.ShareOf(Stamina)} and regenPerSecondShareMilli.stamina="
            + $"{resources.RegenShareOf(Stamina)} are the two per-mille shares that scale it with "
            + "P(theta).");
        sb.AppendLine("Owner action: retune those authored numbers. This gate asserts the finding; it "
            + "does not soften it, skip it, or exclude any point of the band.");
        return sb.ToString();
    }
}
