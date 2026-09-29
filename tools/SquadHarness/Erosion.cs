using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Tools.SquadHarness;

/// <summary>
/// spec-squad-harness.md §10.1 / spec-mechanism-wiring.md §11.1 -- F3, `mechanism-wiring`'s **A10a**: the
/// Erosion differential. Four arms, one attacker, one Θ, one seed stream, resolved over the shipped
/// <see cref="BattleEngine"/> exactly like every other cell this module reports:
///
/// <code>
/// ΔW_spread = W(corner attacker WITH erosion  vs spread defender) - W(corner attacker WITHOUT erosion vs spread defender)
/// ΔW_corner = W(corner attacker WITH erosion  vs corner defender) - W(corner attacker WITHOUT erosion vs corner defender)
/// D         = ΔW_spread - ΔW_corner
/// </code>
///
/// <para><b>Needs no wiring at all.</b> §11.1: A10a needs neither G1 nor G3 -- <see cref="BattleActorSetup.ChannelMods"/>
/// is the caller's own additive derived-channel overlay, and <c>BattleHubCompose.Compose</c> already
/// validates every id against the Hub's own registered channel set (throwing on an unknown one, same
/// as the deleted composer did). This class only ever appends to that list; it never touches <c>src/</c>.</para>
///
/// <para><b>No new build shape is minted.</b> §10.1: "Both defenders already exist in the duel roster --
/// the twelve corners and <c>even12</c>." The attacker is one corner, held fixed across all four arms.
/// This resolves that to <see cref="AttackerCornerId"/>/<see cref="OtherCornerDefenderId"/> --
/// <see cref="BuildFactory.Roster"/>'s first two entries -- the same "resolve to the first in ordinal
/// order" convention <see cref="SquadRoster"/>'s own doc already uses for an unstated tie-break, so a
/// corner-vs-itself mirror match (trivially ~50%) is never accidentally chosen as the "corner defender."
/// At squad scope the exact same three ids already exist as <c>mono-might</c>/<c>mono-fortitude</c>/
/// <c>mono-spread</c> in <see cref="SquadRoster.Squads"/> -- not a new build, a different projection of
/// the same three duel builds (this class's own "one resolve, two projections" shape, matching
/// <see cref="TransferReport"/>'s).</para>
/// </summary>
public static class Erosion
{
    /// <summary>
    /// The eight defensive families doc 05 §4c names and spec-mechanism-wiring.md §11.1 repeats:
    /// <c>combat.defense.omni</c>, <c>combat.dodge.omni</c>, <c>combat.crit.resist.omni</c>,
    /// <c>combat.parry.rate.omni</c>, <c>combat.block.rate.omni</c>, <c>combat.absorption.omni</c>,
    /// <c>combat.reduction.omni</c>, <c>combat.shield.toughness.omni</c> -- the exact constants
    /// <c>CombatDerivedReader</c> (:39-65) and <c>OverlayCombatCalculator</c> (:109-184) already read, so
    /// this class targets nothing the resolver does not already consume.
    /// </summary>
    public static readonly IReadOnlyList<string> DefensiveChannels = new[]
    {
        DerivedStatChannels.CombatDefenseOmni,
        DerivedStatChannels.CombatDodgeOmni,
        DerivedStatChannels.CombatCritResistOmni,
        DerivedStatChannels.CombatParryRateOmni,
        DerivedStatChannels.CombatBlockRateOmni,
        DerivedStatChannels.CombatAbsorptionOmni,
        DerivedStatChannels.CombatReductionOmni,
        DerivedStatChannels.CombatShieldToughnessOmni,
    };

    /// <summary>spec-mechanism-wiring.md §11.1: "3.0pp and 2x are design thresholds and are stated as
    /// such, not tunables... changing either is a change to this spec." <c>Milli</c> here is the same
    /// per-mille-of-probability unit <see cref="Resolution.HalfWidthMilli"/> already returns (1.0pp = 10
    /// of these units -- pinned by <c>ResolutionTests</c>' own 1.8pp==18 check), so these compare directly
    /// against a <c>TransferReport.ColumnCell</c>'s fields with no unit conversion.</summary>
    public const long EffectSizeBarMilli = 30; // 3.0pp

    /// <summary>1.0pp, same units as <see cref="EffectSizeBarMilli"/> -- see that constant's own doc.</summary>
    public const long ResolutionBarMilli = 10; // 1.0pp

    /// <summary>ΔW_spread must be at least this many times ΔW_corner -- §11.1's selectivity bar.</summary>
    public const long SelectivityRatio = 2;

    /// <summary>Resolved ambiguity (this class's own doc): the fixed corner attacker, held across all
    /// four arms -- <see cref="BuildFactory.Roster"/>'s first entry, never re-declared as a literal.</summary>
    public static readonly string AttackerCornerId = BuildFactory.Roster[0];

    /// <summary>Resolved ambiguity: the fixed "another corner" defender for ΔW_corner --
    /// <see cref="BuildFactory.Roster"/>'s second entry. Deliberately NOT the same id as
    /// <see cref="AttackerCornerId"/>: a corner mirroring itself is a ~50% coin flip by construction and
    /// would make ΔW_corner measure symmetry noise instead of "erosion against a corner's own floors."</summary>
    public static readonly string OtherCornerDefenderId = BuildFactory.Roster[1];

    /// <summary>
    /// The static Erosion subtraction, per doc 05 §4c bound 1: "E is a per-mille share of P(Θ), so it
    /// rides the one power ladder and never outruns the scale it is subtracting from." Widened before
    /// multiplying and divided by 1000 exactly once, per docs/architecture/numeric-types.md's magnitude rules; <c>checked</c> so an
    /// overflow throws rather than wraps.
    ///
    /// <para><b>Resolved ambiguity, stated as a default -- deliberately NOT a default value.</b> Neither
    /// spec names a concrete per-mille share (doc 05: "measured before it is specced"; §11.1 states
    /// bounds and a target effect size, never a magnitude). Exactly like <c>--seed</c> (spec's own
    /// "Commands" section: "a seed nobody chose behaves like one somebody did"), this class refuses to
    /// invent one: <see cref="ErosionMode.ParseErosionMilli"/> requires <c>--erosion-milli</c> on every
    /// invocation rather than silently baking in an unstated design choice as a default nobody picked.</para>
    /// </summary>
    public static long Amount(int theta, long erosionMilli)
    {
        if (erosionMilli < 0)
            throw new ArgumentOutOfRangeException(nameof(erosionMilli), erosionMilli,
                "erosion is a subtraction (doc05 §4c direction bound) -- a negative share would add mitigation instead of removing it");
        var pTheta = new PowerLadder(PowerTuningHub.Tuning).Value(theta);
        return checked(pTheta * erosionMilli) / 1000;
    }

    /// <summary>
    /// Applies the STATIC Erosion snapshot difference (A10a, never A10b -- see <see cref="Coverage.A10SplitNote"/>)
    /// to one already-built <see cref="BattleActorSetup"/>: subtract <paramref name="amount"/> from each
    /// of the <see cref="DefensiveChannels"/>, floored at that channel's own registered default.
    ///
    /// <para><b>The floor is exactly zero, verified this session, not assumed.</b>
    /// <c>DerivedStatRegistry.RegisterCombatDefaults</c> registers every <c>combat.*</c> channel with
    /// <c>DefaultValue: 0</c> (<c>DerivedStatRegistry.cs:290</c>) -- doc 05 §4c's "clamps at its own
    /// registered default" is "never below zero" for this whole family, not a per-channel lookup this
    /// class needs to carry.</para>
    ///
    /// <para>Composes <paramref name="setup"/> once (read-only, via the same
    /// <c>BattleHubCompose.Compose</c> the real match will call again internally) purely to learn each
    /// channel's PRE-erosion value, so the subtraction can be clamped correctly regardless of what an
    /// aptitude allocation already put there -- the caller-overlay ChannelMods loop that both composers
    /// share is a bare additive fold with no clamping of its own, so a caller that wants "never below
    /// the floor" has to compute the clamp itself, exactly once, here.</para>
    /// </summary>
    public static BattleActorSetup ApplyStatic(BattleActorSetup setup, long amount)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), amount,
                "a negative erosion amount would add mitigation instead of removing it (doc05 §4c direction bound)");
        if (amount == 0) return setup;

        var baseline = BattleHubCompose.Compose(setup);
        var mods = DefensiveChannels
            .Select(ch => new BattleChannelMod(ch, -Math.Min(amount, Math.Max(0L, (long)Math.Round(baseline.Get(ch))))))
            .Where(m => m.Amount != 0)
            .ToList();
        if (mods.Count == 0) return setup;

        return setup with { ChannelMods = setup.ChannelMods.Concat(mods).ToList() };
    }

    /// <summary>One arm's WITH/WITHOUT pair plus their delta, in the same shape every other cell in this
    /// module carries (<see cref="TransferReport.ColumnCell"/>) -- never a private half-width type.</summary>
    public sealed record ErosionArm(
        TransferReport.ColumnCell With,
        TransferReport.ColumnCell Without,
        TransferReport.ColumnCell Delta);

    /// <summary>One scope's (duel or squad) full four-arm measurement and verdict.</summary>
    public sealed record ErosionScopeResult(
        string Scope,
        string AttackerId,
        string SpreadDefenderId,
        string CornerDefenderId,
        ErosionArm Spread,
        ErosionArm Corner,
        TransferReport.ColumnCell D,
        bool DirectionOk,
        bool NeitherArmNegative,
        bool SelectivityOk,
        string Verdict,
        string? WhyNot,
        string Hash);

    /// <summary>The whole A10a artifact -- duel and squad side by side, per acceptance bullet 4 ("the
    /// 1v1 baseline is shown beside it, so 'does it transfer' is answerable"), never two separate files a
    /// reader has to reconcile by hand.</summary>
    public sealed record ErosionResult(
        long Theta,
        long ErosionMilli,
        long ErosionAmount,
        long ScreeningTrials,
        long? RefineTrials,
        ErosionScopeResult Duel,
        ErosionScopeResult Squad,
        HarnessCoverage Coverage);

    static TransferReport.ColumnCell Cell(PairResult r) =>
        new(r.WinShareMilli, Resolution.HalfWidthMilli(checked(r.Victories + r.Defeats)));

    /// <summary>
    /// The half-width of a WITH-minus-WITHOUT delta, and later of D itself, is computed with
    /// <see cref="Resolution.CombinedHalfWidthMilli"/> -- the same independent-estimates root-sum-square
    /// rule <see cref="TransferReport"/> already uses, applied twice exactly as that class's own
    /// <c>Aggregate</c> applies its rule twice (pair -&gt; build, then build -&gt; class).
    ///
    /// <para><b>Resolved ambiguity, stated as a default.</b> §7's common random numbers pair the WITH and
    /// WITHOUT arms on the identical <c>seed(a, d, k)</c> (<see cref="MeasurePair"/> draws both from the
    /// same seed per trial), so the TRUE half-width of this delta is tighter than the independent-estimates
    /// formula gives -- spec §10.1 says so explicitly ("the paired difference is tighter than that bound;
    /// 40,000 is the conservative figure"). This class reports the CONSERVATIVE (independence-assuming)
    /// figure rather than an empirical paired-variance estimate: <see cref="Resolution"/>'s own rule is
    /// "round up, never down -- reporting a narrower interval than actually achieved is the failure mode
    /// this module exists to avoid," and the independent-estimates formula can only ever OVERSTATE this
    /// paired delta's true half-width, never understate it. A tighter, CRN-aware estimator is a genuine
    /// future refinement, not a correctness gap in what is reported here.</para>
    /// </summary>
    static TransferReport.ColumnCell Delta(TransferReport.ColumnCell with, TransferReport.ColumnCell without) =>
        new(with.WinShareMilli - without.WinShareMilli, Resolution.CombinedHalfWidthMilli(with.HalfWidthMilli, without.HalfWidthMilli));

    static ErosionArm ToArm(PairResult with, PairResult without)
    {
        var withCell = Cell(with);
        var withoutCell = Cell(without);
        return new ErosionArm(withCell, withoutCell, Delta(withCell, withoutCell));
    }

    static TransferReport.ColumnCell DiffOfDiffs(TransferReport.ColumnCell spreadDelta, TransferReport.ColumnCell cornerDelta) =>
        new(spreadDelta.WinShareMilli - cornerDelta.WinShareMilli,
            Resolution.CombinedHalfWidthMilli(spreadDelta.HalfWidthMilli, cornerDelta.HalfWidthMilli));

    /// <summary>
    /// Three verdicts, not two (spec-mechanism-wiring.md §11.1): PASS / FAIL / UNRESOLVED, and
    /// UNRESOLVED holds the checkpoint exactly as FAIL does -- never a silent pass.
    ///
    /// <para><b>Order, and why.</b> Resolution is checked first: a half-width that is itself too wide
    /// makes every other question moot. Then the straddle test (the interval spans the effect-size bar --
    /// genuinely unresolved, not failed). Only once the measurement itself is resolved enough to trust do
    /// the hard FAIL conditions run: a negative arm (§11.1's direction bound -- "a refutation, not a small
    /// pass," checked ahead of the bar comparison so it is never masked by an otherwise-large D), the
    /// bar itself, and finally selectivity. §11.1's own three-row table names FAIL only via the bar and
    /// <c>D &lt;= 0</c>; it does not literally spell out a selectivity failure or a negative arm as their
    /// own row. Resolved ambiguity: since PASS's own row requires ALL of {lower bound clears the bar,
    /// selectivity holds}, anything that fails a PASS precondition and is not literally UNRESOLVED is
    /// reported FAIL with the specific reason named -- never a fourth, unnamed bucket that could pass
    /// through by omission.</para>
    /// </summary>
    public static (string Verdict, string? WhyNot) DetermineVerdict(
        TransferReport.ColumnCell d, long deltaSpreadMilli, long deltaCornerMilli, bool selectivityOk)
    {
        if (d.HalfWidthMilli > ResolutionBarMilli)
            return ("UNRESOLVED",
                $"D's half-width {d.HalfWidthMilli}pm exceeds the {ResolutionBarMilli}pm (1.0pp) resolution bar -- refine, or report the harness cannot resolve it.");

        var lower = d.WinShareMilli - d.HalfWidthMilli;
        var upper = d.WinShareMilli + d.HalfWidthMilli;
        if (lower <= EffectSizeBarMilli && EffectSizeBarMilli <= upper)
            return ("UNRESOLVED",
                $"D's 95% interval [{lower}, {upper}]pm straddles the {EffectSizeBarMilli}pm (3.0pp) effect-size bar.");

        if (deltaSpreadMilli < 0 || deltaCornerMilli < 0)
            return ("FAIL",
                $"direction bound violated: an arm is negative (spread {deltaSpreadMilli}pm, corner {deltaCornerMilli}pm) -- " +
                "Erosion removes mitigation and must never cost the attacker win share.");

        if (d.WinShareMilli <= 0 || upper < EffectSizeBarMilli)
            return ("FAIL",
                $"D's 95% upper bound {upper}pm is below the {EffectSizeBarMilli}pm effect-size bar (D={d.WinShareMilli}pm) -- " +
                "doc 05 §4c's inference did not survive measurement.");

        if (!selectivityOk)
            return ("FAIL",
                $"selectivity bar failed: deltaSpread {deltaSpreadMilli}pm < {SelectivityRatio} x deltaCorner {deltaCornerMilli}pm -- " +
                "Erosion is raising corner-vs-corner too, not selectively punishing breadth.");

        return ("PASS", null);
    }

    static int CheckedTheta(long theta)
    {
        if (theta <= 0) throw new ArgumentOutOfRangeException(nameof(theta), theta, "theta must be positive");
        if (theta > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(theta), theta, "theta exceeds int.MaxValue (the engine boundary)");
        return (int)theta;
    }

    static void Tally(BattleOutcome outcome, ref long victories, ref long defeats, ref long stalemates)
    {
        switch (outcome)
        {
            case BattleOutcome.Victory: checked { victories++; } break;
            case BattleOutcome.Defeat: checked { defeats++; } break;
            default: checked { stalemates++; } break;
        }
    }

    static PairResult ToPairResult(string attackerId, string defenderId, long victories, long defeats, long stalemates)
    {
        var decided = checked(victories + defeats);
        var winShareMilli = decided == 0 ? 0L : checked(victories * 1000L) / decided;
        return new PairResult(attackerId, defenderId, victories, defeats, stalemates, winShareMilli);
    }

    /// <summary>
    /// One (attacker, defender) cell's WITH and WITHOUT arms, resolved together. Never a private
    /// reimplementation of match resolution: every trial still goes through
    /// <see cref="SquadMatch.ToActorSetup"/>, <see cref="Seeds.Mix"/> and <see cref="BattleEngine.Resolve"/>
    /// exactly as <see cref="SquadMatch.Measure"/> does -- the only difference is that the defender's
    /// setups are built once per trial and read TWICE, once as-is (WITHOUT) and once through
    /// <see cref="ApplyStatic"/> (WITH), both under the identical <c>seed(a, d, k)</c> (spec §7's common
    /// random numbers, which is what makes <see cref="Delta"/>'s reported half-width conservative rather
    /// than an understatement -- see that method's own doc). <see cref="SquadMatch.ToBattleSetup"/> itself
    /// is not reusable here because it builds exactly one wave list per call, with no seam to erode it.
    /// </summary>
    public static (PairResult With, PairResult Without) MeasurePair(
        RosterEntry attacker, RosterEntry defender, long erosionAmount, RunSpec spec)
    {
        var theta = CheckedTheta(spec.Theta);
        long withVictories = 0, withDefeats = 0, withStalemates = 0;
        long withoutVictories = 0, withoutDefeats = 0, withoutStalemates = 0;

        for (var k = 0L; k < spec.Trials; k++)
        {
            // Common random numbers (spec §7): identical seed for both arms of this trial -- only the
            // defender's ChannelMods differ.
            var seed = Seeds.Mix(spec.RunSeed, attacker.Id, defender.Id, k);
            var squad = attacker.Actors.Select((a, i) => SquadMatch.ToActorSetup($"squad:{i}", "squad", a, theta)).ToList();
            var waveWithout = defender.Actors.Select((a, i) => SquadMatch.ToActorSetup($"wave:{i}", "wave", a, theta)).ToList();

            var withoutReport = BattleEngine.Resolve(new BattleSetup { Squad = squad, Wave = waveWithout }, seed);
            Tally(withoutReport.Outcome, ref withoutVictories, ref withoutDefeats, ref withoutStalemates);

            var waveWith = waveWithout.Select(w => ApplyStatic(w, erosionAmount)).ToList();
            var withReport = BattleEngine.Resolve(new BattleSetup { Squad = squad, Wave = waveWith }, seed);
            Tally(withReport.Outcome, ref withVictories, ref withDefeats, ref withStalemates);
        }

        var without = ToPairResult(attacker.Id, defender.Id, withoutVictories, withoutDefeats, withoutStalemates);
        var with = ToPairResult(attacker.Id, defender.Id, withVictories, withDefeats, withStalemates);
        return (with, without);
    }

    /// <summary>
    /// passive-tree Checkpoint E bullet 2 (spec-mechanism-wiring.md, F3's own disclosed gap): a real
    /// `stat.derived` atom, scored through this module's OWN measurement pipeline (duel/squad win-rate),
    /// not just at the unit level (`EffectOfflineKitTests`) or the schema-capability level (F2's
    /// `Coverage.cs`). No existing shipped test exercised this before it — `BuildFactory.cs`/`SquadMatch.
    /// ToActorSetup` build the actor's aptitude via <see cref="BattleHubInputs.Aptitude"/> alone
    /// (battle-hub-fuse T6), with no notion of a passive-tree node's own bound atom at all.
    ///
    /// <para><b>Deliberately NOT full passive-tree-ownership modelling.</b> Wiring "this roster member
    /// owns node X, resolved through the real catalog/binder" into `SquadHarness` would be new,
    /// significant scope this checkpoint's own text does not ask for ("scoring ONE" atom "through the
    /// actual harness measurement pipeline"). This mirrors <see cref="ApplyStatic"/>'s own proven
    /// shape exactly — apply a real atom's own `BattleChannelMod`s to one arm, resolve both arms under
    /// common random numbers, report the real win-share delta — using the ALREADY-SHIPPED
    /// `stat.derived` atom this program's own E12 migration already trusts on the Battle side
    /// (<see cref="TraitAtomSource.Shipped"/>'s `critical-hunter`, `combat.crit.rate.omni +150`), so
    /// zero new content is authored to prove this.</para>
    ///
    /// <para>Applied to the ATTACKER, not the defender — a mechanism node is something the OWNING actor
    /// benefits from (unlike Erosion's own defender-side mitigation-removal shape), so <c>squadWith</c>
    /// carries the extra mods and <c>squadWithout</c> does not, both waves held identical.</para>
    /// </summary>
    public static (PairResult With, PairResult Without) MeasureMechanismPair(
        RosterEntry attacker, RosterEntry defender, IReadOnlyList<BattleChannelMod> mechanismMods, RunSpec spec)
    {
        if (mechanismMods.Count == 0)
            throw new ArgumentException("a mechanism atom with zero channel mods measures nothing", nameof(mechanismMods));

        var theta = CheckedTheta(spec.Theta);
        long withVictories = 0, withDefeats = 0, withStalemates = 0;
        long withoutVictories = 0, withoutDefeats = 0, withoutStalemates = 0;

        for (var k = 0L; k < spec.Trials; k++)
        {
            var seed = Seeds.Mix(spec.RunSeed, attacker.Id, defender.Id, k);
            var wave = defender.Actors.Select((a, i) => SquadMatch.ToActorSetup($"wave:{i}", "wave", a, theta)).ToList();
            var squadWithout = attacker.Actors.Select((a, i) => SquadMatch.ToActorSetup($"squad:{i}", "squad", a, theta)).ToList();

            var withoutReport = BattleEngine.Resolve(new BattleSetup { Squad = squadWithout, Wave = wave }, seed);
            Tally(withoutReport.Outcome, ref withoutVictories, ref withoutDefeats, ref withoutStalemates);

            var squadWith = squadWithout
                .Select(s => s with { ChannelMods = s.ChannelMods.Concat(mechanismMods).ToList() })
                .ToList();
            var withReport = BattleEngine.Resolve(new BattleSetup { Squad = squadWith, Wave = wave }, seed);
            Tally(withReport.Outcome, ref withVictories, ref withDefeats, ref withStalemates);
        }

        var without = ToPairResult(attacker.Id, defender.Id, withoutVictories, withoutDefeats, withoutStalemates);
        var with = ToPairResult(attacker.Id, defender.Id, withVictories, withDefeats, withStalemates);
        return (with, without);
    }

    static PairResult Relabel(PairResult r, string defenderSuffix) => r with { DefenderId = r.DefenderId + defenderSuffix };

    /// <summary>
    /// One scope's (duel or squad) full measurement: both arms, the D statistic, and the verdict.
    ///
    /// <para><b>Resolved ambiguity, stated as a default -- refine is a full replacement, applied
    /// uniformly.</b> <see cref="Screening.RunWithRefine"/>'s per-cell <see cref="Resolution.CannotCallWinner"/>
    /// trigger targets whether a CELL'S OWN win share straddles the 50% coin-flip line -- the right
    /// question for a 506-pair sweep where selective refine is the whole point of the optimization, and
    /// the wrong question here: this scope has exactly two cells (spread, corner), and what needs to
    /// resolve is D's own bar, not either cell's coin-flip distance. So when <paramref name="refineTrials"/>
    /// is given, both arms are re-measured at that trial count, REPLACING the screening pass -- the same
    /// "refine replaces, never adds" semantics <see cref="Screening"/> already established, just applied
    /// to all of this scope's (small) cell population instead of a selective subset.</para>
    /// </summary>
    public static ErosionScopeResult MeasureScope(
        string scope, RosterEntry attacker, RosterEntry spreadDefender, RosterEntry cornerDefender,
        long erosionAmount, RunSpec screenSpec, long? refineTrials)
    {
        // "Refine replaces, never adds" (Screening.cs's own rule): run at the refine trial count
        // directly when one is given, rather than paying for the screening pass and discarding it --
        // Seeds.Mix's dependence on (attacker, defender, k) alone means the refine run reuses the
        // identical first screenSpec.Trials draws by construction, so nothing is lost by skipping the
        // separate screening measurement.
        var effectiveSpec = refineTrials is { } rt ? screenSpec with { Trials = rt } : screenSpec;
        var (withSpread, withoutSpread) = MeasurePair(attacker, spreadDefender, erosionAmount, effectiveSpec);
        var (withCorner, withoutCorner) = MeasurePair(attacker, cornerDefender, erosionAmount, effectiveSpec);

        var spreadArm = ToArm(withSpread, withoutSpread);
        var cornerArm = ToArm(withCorner, withoutCorner);
        var d = DiffOfDiffs(spreadArm.Delta, cornerArm.Delta);

        var directionOk = d.WinShareMilli > 0;
        var neitherArmNegative = spreadArm.Delta.WinShareMilli >= 0 && cornerArm.Delta.WinShareMilli >= 0;
        var selectivityOk = spreadArm.Delta.WinShareMilli >= checked(SelectivityRatio * cornerArm.Delta.WinShareMilli);
        var (verdict, whyNot) = DetermineVerdict(d, spreadArm.Delta.WinShareMilli, cornerArm.Delta.WinShareMilli, selectivityOk);

        // DeterminismHash convention (§9.1), reused rather than re-derived: the four labelled cells go
        // into a HarnessRun exactly like every other roster sweep, so Hash() needs no erosion-specific
        // logic at all.
        var pairs = new[]
        {
            Relabel(withSpread, "+erosion"), withoutSpread,
            Relabel(withCorner, "+erosion"), withoutCorner,
        }.OrderBy(p => p.AttackerId, StringComparer.Ordinal).ThenBy(p => p.DefenderId, StringComparer.Ordinal).ToList();
        var actorIds = new[] { attacker.Id, spreadDefender.Id, cornerDefender.Id }.OrderBy(id => id, StringComparer.Ordinal).ToList();
        var run = new HarnessRun($"erosion-{scope}", actorIds, pairs);

        return new ErosionScopeResult(
            scope, attacker.Id, spreadDefender.Id, cornerDefender.Id,
            spreadArm, cornerArm, d, directionOk, neitherArmNegative, selectivityOk, verdict, whyNot,
            DeterminismHash.Hash(run));
    }

    /// <summary>
    /// Production entry point: the default rosters (§10.1's own resolved attacker/defenders), both
    /// scopes side by side.
    /// </summary>
    public static ErosionResult Build(RunSpec screenSpec, long erosionMilli, long? refineTrials)
    {
        var theta = CheckedTheta(screenSpec.Theta);
        var erosionAmount = Amount(theta, erosionMilli);

        var duelBuilds = SquadRoster.Duels();
        var attackerDuel = RosterEntry.From(duelBuilds.First(b => b.Id == AttackerCornerId));
        var spreadDuel = RosterEntry.From(duelBuilds.First(b => b.Id == "even12"));
        var cornerDuel = RosterEntry.From(duelBuilds.First(b => b.Id == OtherCornerDefenderId));

        var squadBuilds = SquadRoster.Squads(AllocationShape.PerActor);
        var attackerSquad = RosterEntry.From(squadBuilds.First(b => b.Id == $"mono-{AttackerCornerId.ToLowerInvariant()}"));
        var spreadSquad = RosterEntry.From(squadBuilds.First(b => b.Id == "mono-spread"));
        var cornerSquad = RosterEntry.From(squadBuilds.First(b => b.Id == $"mono-{OtherCornerDefenderId.ToLowerInvariant()}"));

        var duel = MeasureScope("duel", attackerDuel, spreadDuel, cornerDuel, erosionAmount, screenSpec, refineTrials);
        var squad = MeasureScope("squad", attackerSquad, spreadSquad, cornerSquad, erosionAmount, screenSpec, refineTrials);

        return new ErosionResult(theta, erosionMilli, erosionAmount, screenSpec.Trials, refineTrials, duel, squad, Coverage.Standard());
    }

    static readonly JsonSerializerOptions ArtifactOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string ArtifactPath(string repoRoot) =>
        Path.Combine(repoRoot, "docs", "research", "passive-tree", "_erosion-differential.json");

    /// <summary>Writes <c>_erosion-differential.json</c> -- spec's own Commands block names this exact
    /// path. Same shape discipline as <see cref="Artifacts.WriteTransfer"/>: an <c>at</c> provenance field
    /// that never enters the hash (each scope's <see cref="ErosionScopeResult.Hash"/> is computed over the
    /// underlying <see cref="HarnessRun"/> before this wrapper adds it), and a `note` explaining this is a
    /// research measurement, never a golden.</summary>
    public static string WriteArtifact(ErosionResult result, string? outPath = null)
    {
        var payload = new
        {
            at = DateTimeOffset.Now.ToString("o"),
            note = "docs/research measurement, not a golden -- see spec-squad-harness.md 'Testing strategy'. " +
                   "A10a (static Erosion via BattleActorSetup.ChannelMods) only -- never A10b, see coverage.a10Split.",
            theta = result.Theta,
            erosionMilli = result.ErosionMilli,
            erosionAmount = result.ErosionAmount,
            screeningTrials = result.ScreeningTrials,
            refineTrials = result.RefineTrials,
            duel = ToPayload(result.Duel),
            squad = ToPayload(result.Squad),
            coverage = result.Coverage,
        };

        var json = JsonSerializer.Serialize(payload, ArtifactOptions);
        var path = outPath ?? ArtifactPath(TuningBootstrap.FindRepoRoot());
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, json);
        return json;

        static object ToPayload(ErosionScopeResult s) => new
        {
            scope = s.Scope,
            attackerId = s.AttackerId,
            spreadDefenderId = s.SpreadDefenderId,
            cornerDefenderId = s.CornerDefenderId,
            spread = new { with = s.Spread.With, without = s.Spread.Without, delta = s.Spread.Delta },
            corner = new { with = s.Corner.With, without = s.Corner.Without, delta = s.Corner.Delta },
            d = s.D,
            directionOk = s.DirectionOk,
            neitherArmNegative = s.NeitherArmNegative,
            selectivityOk = s.SelectivityOk,
            verdict = s.Verdict,
            whyNot = s.WhyNot,
            hash = s.Hash,
        };
    }
}

/// <summary>CLI parsing for the <c>erosion</c> mode -- kept beside <see cref="Erosion"/> rather than
/// folded into <see cref="Modes"/>, since <c>--erosion-milli</c> has no meaning for any other mode.</summary>
public static class ErosionMode
{
    /// <summary>Required, no default -- see <see cref="Erosion.Amount"/>'s own doc for why. Refused
    /// loudly, exactly like <c>--seed</c>'s own refusal in <c>Program.cs</c>.</summary>
    public static long ParseErosionMilli(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count - 1; i++)
            if (args[i] == "--erosion-milli" && long.TryParse(args[i + 1], out var value))
                return value;
        throw new ArgumentException(
            "refused: --erosion-milli is required and has no default (doc 05 §4c: 'measured before it is specced' -- " +
            "an unstated magnitude would be a design choice nobody made)");
    }
}
