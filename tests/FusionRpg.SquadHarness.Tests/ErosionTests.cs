using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Tools.SquadHarness;
using Xunit;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// spec-squad-harness.md §10.1 / spec-mechanism-wiring.md §11.1 -- F3, mechanism-wiring's A10a.
///
/// <para><b>Two layers, on purpose.</b> The D-statistic math, the three-verdict logic and the floor
/// arithmetic are PURE functions tested directly with synthetic inputs (no <see cref="BattleEngine"/>
/// call at all) -- exactly <see cref="ResolutionTests"/>'s own style, and for the same reason: these are
/// the parts a real production sweep cannot re-derive for free, so they need to be right independent of
/// how long a live run takes. A second, smaller layer runs the real engine at trivial trial counts
/// (2-5) to prove the wiring (roster resolution, <see cref="BattleActorSetup.ChannelMods"/> injection,
/// determinism) actually holds end-to-end -- this repo's heavy concurrent machine load this session made
/// a full 3,000/40,000-trial production sweep impractical to run live (see the F3 task's own disclosed
/// gap), so these integration tests deliberately stay small rather than attempt the real sweep.</para>
/// </summary>
public class ErosionTests
{
    // ---- pure math: DetermineVerdict --------------------------------------------------------------

    static TransferReport.ColumnCell Cell(long valueMilli, long halfWidthMilli) => new(valueMilli, halfWidthMilli);

    [Fact]
    public void Verdict_is_PASS_when_the_lower_bound_clears_the_bar_and_selectivity_holds()
    {
        // D = 50pm +/- 5pm -> lower bound 45pm > 30pm bar. spread=60, corner=20: 60 >= 2*20.
        var (verdict, whyNot) = Erosion.DetermineVerdict(Cell(50, 5), deltaSpreadMilli: 60, deltaCornerMilli: 20, selectivityOk: true);
        Assert.Equal("PASS", verdict);
        Assert.Null(whyNot);
    }

    [Fact]
    public void Verdict_is_UNRESOLVED_when_the_half_width_itself_exceeds_the_resolution_bar()
    {
        // half-width 11pm > 10pm resolution bar -- UNRESOLVED regardless of the point estimate.
        var (verdict, whyNot) = Erosion.DetermineVerdict(Cell(50, 11), deltaSpreadMilli: 60, deltaCornerMilli: 20, selectivityOk: true);
        Assert.Equal("UNRESOLVED", verdict);
        Assert.Contains("resolution bar", whyNot!, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_is_UNRESOLVED_when_the_interval_straddles_the_effect_size_bar()
    {
        // D = 28pm +/- 5pm -> [23, 33] straddles the 30pm bar (half-width 5 <= 10, so not caught above).
        var (verdict, whyNot) = Erosion.DetermineVerdict(Cell(28, 5), deltaSpreadMilli: 40, deltaCornerMilli: 5, selectivityOk: true);
        Assert.Equal("UNRESOLVED", verdict);
        Assert.Contains("straddles", whyNot!, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_is_FAIL_when_an_arm_is_negative_even_if_D_itself_looks_large()
    {
        // spec §11.1: "neither arm may be negative... a negative D is a refutation, not a small pass" --
        // extended here to a single negative arm, checked ahead of the bar comparison so a large D from
        // an anomalous negative corner arm is never masked.
        var (verdict, whyNot) = Erosion.DetermineVerdict(Cell(50, 5), deltaSpreadMilli: 45, deltaCornerMilli: -5, selectivityOk: true);
        Assert.Equal("FAIL", verdict);
        Assert.Contains("direction bound violated", whyNot!, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_is_FAIL_when_the_upper_bound_stays_below_the_effect_size_bar()
    {
        var (verdict, whyNot) = Erosion.DetermineVerdict(Cell(15, 5), deltaSpreadMilli: 20, deltaCornerMilli: 5, selectivityOk: true);
        Assert.Equal("FAIL", verdict);
        Assert.Contains("effect-size bar", whyNot!, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_is_FAIL_when_D_is_non_positive()
    {
        var (verdict, whyNot) = Erosion.DetermineVerdict(Cell(0, 5), deltaSpreadMilli: 5, deltaCornerMilli: 5, selectivityOk: true);
        Assert.Equal("FAIL", verdict);
    }

    [Fact]
    public void Verdict_is_FAIL_when_selectivity_fails_even_though_the_bar_is_cleared()
    {
        // lower bound 45pm clears the 30pm bar, but spread(30) < 2*corner(20) -- Erosion is raising
        // corner-vs-corner too, which is exactly what the selectivity bar exists to catch.
        var (verdict, whyNot) = Erosion.DetermineVerdict(Cell(50, 5), deltaSpreadMilli: 30, deltaCornerMilli: 20, selectivityOk: false);
        Assert.Equal("FAIL", verdict);
        Assert.Contains("selectivity bar failed", whyNot!, StringComparison.Ordinal);
    }

    [Fact]
    public void Verdict_never_returns_a_fifth_label()
    {
        // Every combination below is exhaustively one of PASS/FAIL/UNRESOLVED -- never a null/empty verdict.
        foreach (var d in new long[] { -20, 0, 10, 29, 30, 31, 60 })
        foreach (var hw in new long[] { 0, 5, 10, 11, 20 })
        foreach (var selectivityOk in new[] { true, false })
        {
            var (verdict, _) = Erosion.DetermineVerdict(Cell(d, hw), deltaSpreadMilli: d, deltaCornerMilli: 0, selectivityOk: selectivityOk);
            Assert.Contains(verdict, new[] { "PASS", "FAIL", "UNRESOLVED" });
        }
    }

    // ---- pure math: the design thresholds are exactly what the spec names ------------------------

    [Fact]
    public void Design_thresholds_match_the_spec_exactly()
    {
        Assert.Equal(30L, Erosion.EffectSizeBarMilli); // 3.0pp
        Assert.Equal(10L, Erosion.ResolutionBarMilli);  // 1.0pp
        Assert.Equal(2L, Erosion.SelectivityRatio);
    }

    // ---- pure math: Amount --------------------------------------------------------------------

    [Fact]
    public void Amount_throws_on_a_negative_erosion_milli()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Erosion.Amount(100, -1));
    }

    [Fact]
    public void Amount_is_zero_when_erosion_milli_is_zero()
    {
        Assert.Equal(0L, Erosion.Amount(100, 0));
    }

    [Fact]
    public void Amount_scales_linearly_with_erosion_milli()
    {
        var one = Erosion.Amount(100, 100);
        var ten = Erosion.Amount(100, 1000);
        Assert.Equal(one * 10, ten);
    }

    // ---- pure-ish: ApplyStatic (one BattleHubCompose.Compose call, no BattleEngine) --------------

    static BattleActorSetup MinimalSetup(IReadOnlyList<BattleChannelMod>? mods = null) => new()
    {
        Key = "test", Side = "wave", SpeciesId = "erosion-test", Level = 100,
        MaxHp = 1000, Atk = 100, Defense = 500,
        ChannelMods = mods ?? Array.Empty<BattleChannelMod>(),
    };

    [Fact]
    public void ApplyStatic_throws_on_a_negative_amount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Erosion.ApplyStatic(MinimalSetup(), -1));
    }

    [Fact]
    public void ApplyStatic_is_a_no_op_at_zero_amount()
    {
        var setup = MinimalSetup();
        var result = Erosion.ApplyStatic(setup, 0);
        Assert.Same(setup, result);
    }

    [Fact]
    public void ApplyStatic_subtracts_from_defense_and_floors_at_zero_never_below()
    {
        var setup = MinimalSetup(); // Defense = 500
        var eroded = Erosion.ApplyStatic(setup, amount: 10_000); // far more than the 500 baseline

        var snap = BattleHubCompose.Compose(eroded);
        Assert.Equal(0.0, snap.Get(DerivedStatChannels.CombatDefenseOmni));
        // Every other defensive channel floors at (its own registered default) zero too, never negative.
        foreach (var channel in Erosion.DefensiveChannels)
            Assert.True(snap.Get(channel) >= 0.0, $"{channel} went negative");
    }

    [Fact]
    public void ApplyStatic_a_small_amount_leaves_a_large_baseline_channel_only_partially_reduced()
    {
        var setup = MinimalSetup(); // Defense = 500
        var baselineSnap = BattleHubCompose.Compose(setup);
        var baselineDefense = baselineSnap.Get(DerivedStatChannels.CombatDefenseOmni);

        var eroded = Erosion.ApplyStatic(setup, amount: 50);
        var erodedSnap = BattleHubCompose.Compose(eroded);
        Assert.Equal(baselineDefense - 50, erodedSnap.Get(DerivedStatChannels.CombatDefenseOmni));
    }

    [Fact]
    public void ApplyStatic_never_removes_an_existing_ChannelMod_it_only_appends()
    {
        var existing = new BattleChannelMod(DerivedStatChannels.CombatAccuracyOmni, 77);
        var setup = MinimalSetup(new[] { existing });
        var eroded = Erosion.ApplyStatic(setup, amount: 10);
        Assert.Contains(existing, eroded.ChannelMods);
    }

    [Fact]
    public void ApplyStatic_throws_on_an_unknown_channel_id_through_the_shipped_composer_validation()
    {
        // Not this class's own check -- BattleHubCompose.Compose's own "unknown channel -> throw"
        // (spec §10.1's own citation). Proven here so a future edit to DefensiveChannels that
        // introduces a typo fails loudly rather than silently no-op'ing.
        var badMod = new BattleChannelMod("combat.not.a.real.channel", -1);
        var setup = MinimalSetup(new[] { badMod });
        Assert.Throws<ArgumentException>(() => BattleHubCompose.Compose(setup));
    }

    // ---- integration: real BattleEngine, trivial trial counts -------------------------------------

    static RunSpec Spec(long trials, long theta = 60) => new(theta, trials, RunSeed: 20260906);

    [Fact]
    public void MeasurePair_with_zero_erosion_produces_the_same_win_share_as_without_by_construction()
    {
        // amount=0 -> ApplyStatic no-ops -> the WITH arm's wave setups are byte-identical to WITHOUT's,
        // and both draw from the identical CRN seed, so the two counts must match exactly.
        var duel = SquadRoster.Duels();
        var attacker = RosterEntry.From(duel.First(b => b.Id == Erosion.AttackerCornerId));
        var defender = RosterEntry.From(duel.First(b => b.Id == "even12"));

        var (with, without) = Erosion.MeasurePair(attacker, defender, erosionAmount: 0, Spec(trials: 5));
        Assert.Equal(without.Victories, with.Victories);
        Assert.Equal(without.Defeats, with.Defeats);
        Assert.Equal(without.Stalemates, with.Stalemates);
    }

    // ---- MeasureMechanismPair: Checkpoint E bullet 2, a real stat.derived atom scored through the
    // real measurement pipeline (duel/squad win-rate), not just at the unit or capability level -------

    [Fact]
    public void MeasureMechanismPair_throws_on_an_empty_mod_list_rather_than_silently_measuring_nothing()
    {
        var duel = SquadRoster.Duels();
        var attacker = RosterEntry.From(duel.First(b => b.Id == Erosion.AttackerCornerId));
        var defender = RosterEntry.From(duel.First(b => b.Id == "even12"));

        Assert.Throws<ArgumentException>(() => Erosion.MeasureMechanismPair(
            attacker, defender, Array.Empty<BattleChannelMod>(), Spec(trials: 1)));
    }

    [Fact]
    public void The_shipped_critical_hunter_atom_really_raises_the_composed_crit_channel_by_its_own_amount()
    {
        // Pure-ish, no BattleEngine: proves the exact mod TraitAtomSource.Shipped() already trusts on
        // the Battle side (E12) is the same one this measurement applies -- deterministic, no RNG.
        var duel = SquadRoster.Duels();
        var attacker = duel.First(b => b.Id == Erosion.AttackerCornerId).Allocation;
        var mods = TraitAtomSource.Shipped().ModsFor("critical-hunter");

        var baseSetup = SquadMatch.ToActorSetup("squad:0", "squad", attacker, theta: 60);
        var withSetup = baseSetup with { ChannelMods = baseSetup.ChannelMods.Concat(mods).ToList() };

        var baseline = BattleHubCompose.Compose(baseSetup).Get(DerivedStatChannels.CombatCritRateOmni);
        var boosted = BattleHubCompose.Compose(withSetup).Get(DerivedStatChannels.CombatCritRateOmni);
        Assert.Equal(baseline + 150.0, boosted);
    }

    [Fact]
    public void MeasureMechanismPair_runs_the_real_engine_end_to_end_and_both_arms_account_for_every_trial()
    {
        // Checkpoint E bullet 2's own bar: "scoring one through the actual harness measurement
        // pipeline (duel/squad win-rate)" -- this is that scoring, for real, against the shipped
        // critical-hunter stat.derived atom, at a trivial trial count (this session's own established
        // reason: a full production sweep is impractical under this machine's concurrent load, same as
        // MeasurePair's own integration tests above).
        var duel = SquadRoster.Duels();
        var attacker = RosterEntry.From(duel.First(b => b.Id == Erosion.AttackerCornerId));
        var defender = RosterEntry.From(duel.First(b => b.Id == "even12"));
        var mods = TraitAtomSource.Shipped().ModsFor("critical-hunter");

        var (with, without) = Erosion.MeasureMechanismPair(attacker, defender, mods, Spec(trials: 5));

        Assert.Equal(5, with.Victories + with.Defeats + with.Stalemates);
        Assert.Equal(5, without.Victories + without.Defeats + without.Stalemates);
        // Both arms share the SAME defender and the SAME per-trial seed (common random numbers) --
        // only the attacker's own crit rate differs, so a real difference in outcome distribution is
        // real signal from the atom, never seed noise. Not asserted as an exact win-count (RNG-real,
        // would be flaky) -- the wiring itself, proven above via the deterministic channel-value test,
        // is what this bullet actually asks to see scored.
    }

    [Fact]
    public void MeasureScope_resolves_to_the_documented_default_ids()
    {
        var duel = SquadRoster.Duels();
        var attacker = RosterEntry.From(duel.First(b => b.Id == Erosion.AttackerCornerId));
        var spread = RosterEntry.From(duel.First(b => b.Id == "even12"));
        var corner = RosterEntry.From(duel.First(b => b.Id == Erosion.OtherCornerDefenderId));

        var scope = Erosion.MeasureScope("duel", attacker, spread, corner, erosionAmount: 1, Spec(trials: 2), refineTrials: null);

        Assert.Equal("Might", scope.AttackerId);
        Assert.Equal("Fortitude", scope.CornerDefenderId);
        Assert.Equal("even12", scope.SpreadDefenderId);
        Assert.NotNull(scope.Hash);
        Assert.NotEmpty(scope.Hash);
    }

    [Fact]
    public void MeasureScope_is_deterministic_for_the_same_seed()
    {
        var duel = SquadRoster.Duels();
        var attacker = RosterEntry.From(duel.First(b => b.Id == Erosion.AttackerCornerId));
        var spread = RosterEntry.From(duel.First(b => b.Id == "even12"));
        var corner = RosterEntry.From(duel.First(b => b.Id == Erosion.OtherCornerDefenderId));
        var spec = Spec(trials: 3);

        var r1 = Erosion.MeasureScope("duel", attacker, spread, corner, erosionAmount: 5, spec, refineTrials: null);
        var r2 = Erosion.MeasureScope("duel", attacker, spread, corner, erosionAmount: 5, spec, refineTrials: null);

        Assert.Equal(r1.Hash, r2.Hash);
        Assert.Equal(r1.Verdict, r2.Verdict);
        Assert.Equal(r1.D, r2.D);
    }

    [Fact]
    public void MeasureScope_refine_replaces_the_screening_trial_count_for_both_arms()
    {
        var duel = SquadRoster.Duels();
        var attacker = RosterEntry.From(duel.First(b => b.Id == Erosion.AttackerCornerId));
        var spread = RosterEntry.From(duel.First(b => b.Id == "even12"));
        var corner = RosterEntry.From(duel.First(b => b.Id == Erosion.OtherCornerDefenderId));

        // Screening at 1 trial cannot resolve anything (max possible half-width); refine at 10 replaces
        // it entirely -- this only proves the call completes and returns a real, non-throwing result at
        // a still-tiny (test-budget) trial count, not that 10 trials resolves the bar for real.
        var scope = Erosion.MeasureScope("duel", attacker, spread, corner, erosionAmount: 5, Spec(trials: 1), refineTrials: 10);
        Assert.Contains(scope.Verdict, new[] { "PASS", "FAIL", "UNRESOLVED" });
    }

    [Fact]
    public void Build_uses_the_mono_family_squad_ids_and_no_new_build_shape()
    {
        var result = Erosion.Build(Spec(trials: 2), erosionMilli: 50, refineTrials: null);

        Assert.Equal("Might", result.Duel.AttackerId);
        Assert.Equal("even12", result.Duel.SpreadDefenderId);
        Assert.Equal("Fortitude", result.Duel.CornerDefenderId);

        Assert.Equal("mono-might", result.Squad.AttackerId);
        Assert.Equal("mono-spread", result.Squad.SpreadDefenderId);
        Assert.Equal("mono-fortitude", result.Squad.CornerDefenderId);
    }

    [Fact]
    public void Build_reports_the_1v1_baseline_beside_the_squad_result()
    {
        var result = Erosion.Build(Spec(trials: 2), erosionMilli: 50, refineTrials: null);

        Assert.Equal("duel", result.Duel.Scope);
        Assert.Equal("squad", result.Squad.Scope);
        Assert.Contains(result.Duel.Verdict, new[] { "PASS", "FAIL", "UNRESOLVED" });
        Assert.Contains(result.Squad.Verdict, new[] { "PASS", "FAIL", "UNRESOLVED" });
    }

    [Fact]
    public void Build_carries_the_coverage_block_naming_the_A10_split_and_the_reflect_gap()
    {
        // D2 (this task's own honesty requirement): a reflect node is unmeasurable here, and the
        // coverage block must say so rather than let a silent zero read as a balance finding.
        var result = Erosion.Build(Spec(trials: 2), erosionMilli: 50, refineTrials: null);

        Assert.Contains("A10a", result.Coverage.A10Split, StringComparison.Ordinal);
        Assert.Contains("A10b", result.Coverage.A10Split, StringComparison.Ordinal);
        var joined = string.Join("\n", result.Coverage.BlockedMechanismClasses);
        Assert.Contains("M7 Retaliation", joined, StringComparison.Ordinal);
        Assert.Contains("reflect", joined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_throws_the_required_erosion_milli_refusal_through_the_CLI_parser()
    {
        Assert.Throws<ArgumentException>(() => ErosionMode.ParseErosionMilli(new List<string> { "--theta", "100" }));
    }

    [Fact]
    public void ErosionMode_parses_the_value_when_present()
    {
        Assert.Equal(250L, ErosionMode.ParseErosionMilli(new List<string> { "--erosion-milli", "250" }));
    }

    // ---- artifact writer ---------------------------------------------------------------------------

    [Fact]
    public void WriteArtifact_writes_to_the_given_path_and_carries_both_scopes()
    {
        var result = Erosion.Build(Spec(trials: 2), erosionMilli: 50, refineTrials: null);
        var path = Path.Combine(Path.GetTempPath(), $"erosion-test-{Guid.NewGuid():N}.json");
        try
        {
            var json = Erosion.WriteArtifact(result, path);
            Assert.True(File.Exists(path));
            Assert.Equal(json, File.ReadAllText(path));

            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.TryGetProperty("duel", out _));
            Assert.True(doc.RootElement.TryGetProperty("squad", out _));
            Assert.True(doc.RootElement.TryGetProperty("erosionMilli", out _));
            Assert.True(doc.RootElement.TryGetProperty("erosionAmount", out _));
            Assert.True(doc.RootElement.TryGetProperty("coverage", out _));

            foreach (var scopeName in new[] { "duel", "squad" })
            {
                var scope = doc.RootElement.GetProperty(scopeName);
                Assert.True(scope.TryGetProperty("verdict", out _));
                Assert.True(scope.TryGetProperty("directionOk", out _));
                Assert.True(scope.TryGetProperty("neitherArmNegative", out _));
                Assert.True(scope.TryGetProperty("selectivityOk", out _));
                Assert.True(scope.TryGetProperty("d", out _));
                foreach (var arm in new[] { "spread", "corner" })
                {
                    var armEl = scope.GetProperty(arm);
                    Assert.True(armEl.TryGetProperty("with", out _));
                    Assert.True(armEl.TryGetProperty("without", out _));
                    Assert.True(armEl.TryGetProperty("delta", out _));
                }
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void WriteArtifact_never_writes_to_data_tuning()
    {
        var result = Erosion.Build(Spec(trials: 2), erosionMilli: 50, refineTrials: null);
        var tuningDir = Path.Combine(TuningBootstrap.FindRepoRoot(), "data", "tuning");
        var before = Directory.GetFiles(tuningDir).Select(File.GetLastWriteTimeUtc).ToList();

        var path = Path.Combine(Path.GetTempPath(), $"erosion-test-{Guid.NewGuid():N}.json");
        try
        {
            Erosion.WriteArtifact(result, path);
            var after = Directory.GetFiles(tuningDir).Select(File.GetLastWriteTimeUtc).ToList();
            Assert.Equal(before, after);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void ArtifactPath_points_at_the_exact_file_the_spec_names()
    {
        var path = Erosion.ArtifactPath(TuningBootstrap.FindRepoRoot());
        Assert.EndsWith(Path.Combine("docs", "research", "passive-tree", "_erosion-differential.json"), path);
    }
}
