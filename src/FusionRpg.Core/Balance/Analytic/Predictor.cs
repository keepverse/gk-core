using FusionRpg.Core.Combat;
using FusionRpg.Core.Diagnostics;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Balance.Analytic;

/// <summary>
/// class-system-todo.md P4.6 — the entry point: two actors in, a win probability out, no trials.
/// Ports <c>gk-core/tools/CombatSim/Analytic.cs</c>'s <c>Predict</c> (read in full this session; every term
/// below is traced to a specific line there) by composing this program's own building blocks
/// (<see cref="StrikeMixture"/>, <see cref="FirstPassage"/>, <see cref="Race"/>, <see cref="PhaseModel"/>)
/// rather than recomputing any of their math here — matching spec-deterministic-core.md §2's own rule
/// one level up: this module's only job is composition and the probability arithmetic that composition
/// needs, never a re-derivation of a piece another file already owns.
/// </summary>
public static class Predictor
{
    /// <param name="Snapshot">Combat-derived stats, the same object <see cref="StrikeMixture"/> and
    /// <see cref="PhaseModel"/> read.</param>
    /// <param name="Hp">Raw max HP (before any shield).</param>
    /// <param name="BaseDamage">The authored base damage a swing is thrown at.</param>
    /// <param name="ShieldMaxHp">The shield's already-resolved pool (<c>grant.BaseHp + capacity</c>) —
    /// 0 for no active grant. Never a raw capacity value standing in for a grant that never happened
    /// (<see cref="PhaseModel.ShieldEffectiveHp"/>'s own contract).</param>
    public sealed record Actor(string Name, CombatActorSnapshot Snapshot, double Hp, double BaseDamage, long ShieldMaxHp);

    public readonly record struct DuelPrediction(
        string A, string B, double WinShareA,
        double NetAttritionA, double NetAttritionB,
        double RecoveryA, double RecoveryB,
        double RateAgainstA, double RateAgainstB,
        double VarAgainstA, double VarAgainstB,
        double RoundsA, double RoundsB);

    /// <summary>The action menu and each side's own starting pools — shared menu, per-side state,
    /// matching <c>gk-core/tools/CombatSim/ActionEconomy.cs</c>'s own shape (one <c>ActionSet</c>, one
    /// <c>ActorPools</c> per actor).</summary>
    public sealed record ActionEconomy(
        IReadOnlyList<ActionSchedule.ActionOption> Options,
        IReadOnlyDictionary<string, ActionSchedule.PoolState> PoolsA,
        IReadOnlyDictionary<string, ActionSchedule.PoolState> PoolsB,
        int MaxRounds = 400,
        ActionSchedule.SchedulePolicy? Policy = null);

    /// <summary>One status profile, shared by both sides (either can apply it to the other) — mirrors
    /// <c>gk-core/tools/CombatSim/StatusModel.cs</c>'s <c>StatusProfile</c>. <see cref="Category"/>/<see cref="IsDot"/>/
    /// <see cref="IsCc"/> resolve from the shipped <see cref="StatusCategoryRegistry"/>, never authored here.</summary>
    public sealed record StatusProfile(string StatusId, double MagnitudeShareOfBase, double BaseDurationRounds, double GrantChance = 1.0)
    {
        public string Category => Status.StatusCategoryRegistry.GetRequiredCategory(StatusId);
        public bool IsDot => Category == Status.StatusL2bCategory.Dot;
        public bool IsCc => Category == Status.StatusL2bCategory.Cc;
    }

    /// <summary>One swing's own distribution, generalised to cover both the single-action case (one
    /// <see cref="StrikeMixture.Result"/>) and the action-economy case (a weighted mixture across
    /// several possible actions — a mixture of mixtures, flattened to one atom list here the same way
    /// <c>Analytic.MixedStrike</c> flattens into one <c>List&lt;Atom&gt;</c>).</summary>
    readonly record struct MixedSwing(double Mean, double Variance, double PHit, double EffectiveBase, IReadOnlyList<StrikeAtom> Atoms);

    static MixedSwing SingleActionSwing(StrikeMixture.Result strike, double baseDamage) =>
        new(strike.Mean, strike.Variance, 1.0 - strike.Miss.Probability, baseDamage,
            new[] { strike.Miss, strike.Parried, strike.Blocked, strike.Clean, strike.CleanCrit });

    /// <summary>Core combat only: strikes, shields, reflection (full joint covariance), recovery, the
    /// normal race. No actions, no status — the scope <c>docs/research/class-system/_baseline-
    /// residual.json</c> measures (spec-deterministic-core.md §3's <c>predict</c> with neither
    /// <c>--actions</c> nor <c>--status</c>). A thin wrapper over the general overload with both left null.</summary>
    /// <param name="roundLimit">An encounter-design timer, never a balance parameter
    /// (<c>Analytic.RoundLimit</c>'s own <c>⛔ NOT A BALANCE METRIC</c> warning, carried here verbatim:
    /// a survival build that wins in 60 rounds has won, and judging balance with a clock manufactures a
    /// result plain win rate does not show). <c>null</c> or non-positive means no clock.</param>
    public static DuelPrediction Predict(Actor a, Actor b, double? roundLimit = null) =>
        Predict(a, b, roundLimit, economy: null, status: null);

    /// <summary>The full composition: strikes (optionally action-costed), shields, reflection, status
    /// (DoT and/or CC), recovery, the normal race, the RoundLimit timeout. <paramref name="economy"/>
    /// null means every swing is free (matching <c>Analytic.Predict</c>'s own two-argument overload);
    /// <paramref name="status"/> null means the fourth axis is not in play at all.</summary>
    public static DuelPrediction Predict(Actor a, Actor b, double? roundLimit, ActionEconomy? economy, StatusProfile? status)
    {
        if (a is null) throw new ArgumentNullException(nameof(a));
        if (b is null) throw new ArgumentNullException(nameof(b));

        // A hits B; B may bounce onto A. B hits A; A may bounce onto B.
        var swingA = economy is null
            ? SingleActionSwing(StrikeMixture.Compute(a.BaseDamage, a.Snapshot, b.Snapshot), a.BaseDamage)
            : MixedStrike(economy.Options, economy.PoolsA, a.BaseDamage, a.Snapshot, b.Snapshot, EffectiveHpTarget(b), economy.MaxRounds, economy.Policy);
        var swingB = economy is null
            ? SingleActionSwing(StrikeMixture.Compute(b.BaseDamage, b.Snapshot, a.Snapshot), b.BaseDamage)
            : MixedStrike(economy.Options, economy.PoolsB, b.BaseDamage, b.Snapshot, a.Snapshot, EffectiveHpTarget(a), economy.MaxRounds, economy.Policy);

        // Status, the fourth axis. A landed hit may apply a DoT, whose whole tail is attributable to
        // the swing that caused it -- added to the mean only (Analytic.Swing's own reasoning: the DoT
        // tail is spread over several rounds, which is what averages a random payload out, so it adds
        // no per-round variance of its own worth modelling). CC removes rounds from the side it lands
        // ON, applied once below as a rate multiplier, not inside the swing.
        //
        // dealtMeanA/dealtMeanB are each side's OWN total output (raw strike + that SAME side's own
        // DoT tail) -- both still flow toward the OPPONENT (dealtMeanA lands on B, dealtMeanB lands on
        // A), matching Analytic.Swing's own SwingStats.DealtMean, which already bakes a swing's DoT
        // into that SAME swing's mean before Predict ever reads it. Found and fixed via
        // gk-core/tools/ProvePredictor: an earlier draft added dotAtoB (A's own DoT) to dealtMeanB and dotBtoA
        // to dealtMeanA -- backwards, mixing "damage A deals" with "damage B deals" into one number.
        // Invisible for matchups where neither side's DoT lands meaningfully; a ~0.999 win-share swing
        // for FORCE vs BASTION specifically (FORCE's action-mixed EffectiveBase feeding a large DoT
        // that belongs on FORCE's OWN side, not BASTION's).
        var dealtMeanA = swingA.Mean;
        var dealtMeanB = swingB.Mean;
        double ccOnA = 0, ccOnB = 0;
        if (status is not null)
        {
            if (status.IsDot)
            {
                var dotAtoB = StatusUptime.Expected(status.StatusId, status.MagnitudeShareOfBase, status.BaseDurationRounds, status.GrantChance, a.Snapshot, b.Snapshot, swingA.EffectiveBase);
                var dotBtoA = StatusUptime.Expected(status.StatusId, status.MagnitudeShareOfBase, status.BaseDurationRounds, status.GrantChance, b.Snapshot, a.Snapshot, swingB.EffectiveBase);
                dealtMeanA += StatusUptime.ExpectedDotPerRound(dotAtoB, swingA.PHit); // A's own DoT stays on A's own output.
                dealtMeanB += StatusUptime.ExpectedDotPerRound(dotBtoA, swingB.PHit); // B's own DoT stays on B's own output.
            }
            else if (status.IsCc)
            {
                var ccAtoB = StatusUptime.Expected(status.StatusId, status.MagnitudeShareOfBase, status.BaseDurationRounds, status.GrantChance, a.Snapshot, b.Snapshot, swingA.EffectiveBase);
                var ccBtoA = StatusUptime.Expected(status.StatusId, status.MagnitudeShareOfBase, status.BaseDurationRounds, status.GrantChance, b.Snapshot, a.Snapshot, swingB.EffectiveBase);
                ccOnB = StatusUptime.CcDisabledShare(ccAtoB) * swingA.PHit; // CC A inflicts on B
                ccOnA = StatusUptime.CcDisabledShare(ccBtoA) * swingB.PHit; // CC B inflicts on A
            }
        }
        var actA = 1.0 - ccOnA;
        var actB = 1.0 - ccOnB;

        // Shields are a phase: one effective-HP term, no second solve (PhaseModel.ShieldEffectiveHp's
        // own doc; class-analytic-balance-2026-08-25.md §6.1).
        //
        // ⚠ INCOMING DAMAGE HERE IS `dealtMean*`, NOT `swing*.Mean` (fixed 2026-09-17, solid-remediation).
        // These two sites, and the recovery pair below, used to read the RAW strike mean while `rateB`
        // below read the DoT-inclusive `dealtMeanA` -- so the same fight counted a DoT as damage when
        // computing how fast HP fell, and pretended it did not exist when computing how long the shield
        // lasted and how much regen had to out-heal. A DoT tick IS incoming damage: a shield absorbs it
        // and recovery must out-heal it. The port was internally inconsistent with itself, not merely
        // different from the reference.
        //
        // Found by `gk-core/tools/ProvePredictor`'s actions+status gate, which had been red at 9.222E-004 since
        // some point after class-system P4.6 recorded it passing at 9.146e-5. Localized by tracing the
        // reference's own internals: on FORCE v BASTION every term matched except `reflectShareA`
        // (0.700588866432 -> 0.718918527467) and `recovA`, both of which are functions of incoming
        // damage -- and the status inflates BASTION's output 13060.93 -> 57491.58, which is why that one
        // pair breached the gate while the others stayed at 1e-7.
        var shieldA = PhaseModel.ShieldEffectiveHp(a.ShieldMaxHp, dealtMeanB, attacker: b.Snapshot, defender: a.Snapshot);
        var shieldB = PhaseModel.ShieldEffectiveHp(b.ShieldMaxHp, dealtMeanA, attacker: a.Snapshot, defender: b.Snapshot);
        var hpA = a.Hp + shieldA;
        var hpB = b.Hp + shieldB;

        // A shield suppresses its OWNER's own reflection (reflectReadsPostShield: true, live per
        // gk-core/data/tuning/combat.v1.json:26) -- each side's reflect share is gated by ITS OWN HP-phase
        // share, not the opponent's.
        var reflectShareA = PhaseModel.ReflectionHpPhaseShare(a.Hp, shieldA);
        var reflectShareB = PhaseModel.ReflectionHpPhaseShare(b.Hp, shieldB);

        // The full joint (dealt, bounce) distribution per swing -- needed for the exact covariance the
        // race's rho term reads (P4.2; dropping it costs ~5 points of win rate on a reflect matchup).
        var jointA = PhaseModel.JointReflect(swingA.Atoms, swingA.Mean, reflector: b.Snapshot, reflectedUpon: a.Snapshot);
        var jointB = PhaseModel.JointReflect(swingB.Atoms, swingB.Mean, reflector: a.Snapshot, reflectedUpon: b.Snapshot);

        var hpRegenA = a.Snapshot.Derived.Get(DerivedStatChannels.ResourceRegen("hp"));
        var hpRegenB = b.Snapshot.Derived.Get(DerivedStatChannels.ResourceRegen("hp"));
        var shieldRegenA = CombatDerivedReader.ShieldRegen(a.Snapshot.Derived, null);
        var shieldRegenB = CombatDerivedReader.ShieldRegen(b.Snapshot.Derived, null);
        // class-system-todo.md P7.4: poise, live -- reads the same channel PoiseLedger/ActorResourcePools
        // pays against (resource.regen.poise, registered since P1.12; unified onto this one pool by
        // battle-tempo poise-unification, 2026-09-05). Resolves to 0 for every actor today
        // (no aptitude edge feeds it yet, P7.2's own named gap), so this is currently a no-op that
        // makes the termination check CORRECT once one does, not a behavior change today.
        var poiseRegenA = a.Snapshot.Derived.Get(DerivedStatChannels.ResourceRegen("poise"));
        var poiseRegenB = b.Snapshot.Derived.Get(DerivedStatChannels.ResourceRegen("poise"));
        // `dealtMean*`, not `swing*.Mean` -- see the shield block above for why. Recovery is "damage
        // minus regen", and a DoT tick is damage.
        var recovA = PhaseModel.RecoveryPerRound(hpRegenA, shieldRegenA, a.ShieldMaxHp, dealtMeanB, attacker: b.Snapshot, defender: a.Snapshot, poiseRegenA);
        var recovB = PhaseModel.RecoveryPerRound(hpRegenB, shieldRegenB, b.ShieldMaxHp, dealtMeanA, attacker: a.Snapshot, defender: b.Snapshot, poiseRegenB);

        // Per round both sides swing once (scaled by actA/actB -- a disabled actor does not swing).
        // B's HP falls by A's direct damage (dealtMeanA, which already carries any DoT A applies) plus
        // the bounce B's OWN hit on A triggers off A's reflect stat (jointB.Back*), gated by A's own
        // HP-phase share. Caught by ProvePredictor's exact cross-check against Analytic.Predict: an
        // earlier draft had jointA/jointB swapped here, which is invisible when neither side has a
        // meaningful reflect stat and badly wrong once one does (FORCE's Retribution aptitude, up to
        // 0.67 win share off against BASTION before the fix).
        var rateB = dealtMeanA * actA + jointB.BackMean * reflectShareA * actB;
        var rateA = dealtMeanB * actB + jointA.BackMean * reflectShareB * actA;
        var varB = swingA.Variance * actA + jointB.BackVariance * reflectShareA * actB;
        var varA = swingB.Variance * actB + jointA.BackVariance * reflectShareB * actA;

        var firstPassageA = FirstPassage.Compute(hpA, rateA - recovA, varA);
        var firstPassageB = FirstPassage.Compute(hpB, rateB - recovB, varB);

        // Covariance between the two sides' per-round increments: each side's own (dealt, bounce)
        // covariance, scaled by the OTHER side's reflect share (the cross term the increments share --
        // see Predictor.cs's own design notes / class-system-todo.md P4.6 evidence for the derivation).
        var covRounds = jointA.CovDealtBack * reflectShareB + jointB.CovDealtBack * reflectShareA;
        var rho = varA > 0 && varB > 0 ? Math.Clamp(covRounds / Math.Sqrt(varA * varB), -1.0, 1.0) : 0.0;

        var win = Race.PWinsA(firstPassageA, firstPassageB, rho);
        // The termination invariant (the HARD criterion, class-system-ideal.md §0.0.3) is a structural
        // guarantee owned by the termination GUARD (P5.1), applied before a build ever reaches here --
        // "neither side can ever die" is provably unreachable for any build the guard has accepted.
        // Race.PWinsA reports that state honestly as NaN (its own contract, Race.cs); Predictor mirrors
        // Analytic.Predict's own pragmatic choice of 0.5 for it here, at the composition boundary,
        // rather than changing what the general-purpose primitive reports.
        if (double.IsNaN(win)) win = 0.5;

        if (roundLimit is { } limit && limit > 0)
        {
            // A wins only if it kills B before the bell AND before B kills A -- two separate
            // conditions, so each side's win probability is its race share times its own chance of
            // finishing in time (Analytic.Predict's own epsilon floor, 1e-9, kept for the same reason:
            // a zero-variance FirstPassage result must not divide by zero here).
            var aInTime = Race.Phi((limit - firstPassageB.Mean) / Math.Sqrt(Math.Max(1e-9, firstPassageB.Variance)));
            var bInTime = Race.Phi((limit - firstPassageA.Mean) / Math.Sqrt(Math.Max(1e-9, firstPassageA.Variance)));
            var pA = win * aInTime;
            var pB = (1.0 - win) * bInTime;
            win = pA + pB <= 1e-12 ? 0.5 : pA / (pA + pB);
        }

        // class-system-todo.md V5/P7.2 — peerDamagePerRound, the other half of guard-economy's own
        // r = poiseRegen / peerDamage (§5d.3's own rule, "regen sized against PEER DAMAGE, never the
        // pool"). rateAgainstA/B ARE peer damage per round, already computed above for the race itself
        // -- this call makes that existing number observable via the shipped telemetry seam rather than
        // adding a second way to compute it. Fires on every Predict call, matching ActorHub.ResolveDerived's
        // own "record on every resolve" precedent -- a plain dictionary write behind the same Enabled
        // kill switch, not a new cost class.
        PerfProbe.RecordValue("balance.peerDamagePerRoundAgainstA", rateA);
        PerfProbe.RecordValue("balance.peerDamagePerRoundAgainstB", rateB);

        return new DuelPrediction(a.Name, b.Name, win,
            NetAttritionA: rateA - recovA, NetAttritionB: rateB - recovB,
            RecoveryA: recovA, RecoveryB: recovB,
            RateAgainstA: rateA, RateAgainstB: rateB,
            VarAgainstA: varA, VarAgainstB: varB,
            RoundsA: firstPassageA.Mean, RoundsB: firstPassageB.Mean);
    }

    static double EffectiveHpTarget(Actor defender) => defender.Hp; // shield's own contribution is a second-order effect on the ACTION MIX (Analytic.MixedStrike's own comment) -- raw HP only, deliberately.

    /// <summary>
    /// The action economy in closed form: walk the deterministic <see cref="ActionSchedule"/> sequence
    /// (no RNG -- costs are priced on nominal output, so the pool trajectory is a fixed number, not a
    /// distribution), stopping once the CUMULATIVE MEAN damage reaches the target's HP -- a depleting
    /// pool is a phase, and averaging over rounds that never happen is the exact mistake
    /// <c>Analytic.Walk</c>'s own doc comment measured at 9% mean / 17.9% max residual, all on short
    /// fights. Single-pass with an early stop, not <c>Analytic.MixedStrike</c>'s two-pass estimate-then-
    /// walk: since the walk is deterministic and forward-only, the first <c>k</c> entries of a
    /// <paramref name="maxRounds"/>-round walk are identical to a <c>k</c>-round walk's entries, so
    /// truncating one long walk at the target is the same result as the reference's second pass,
    /// without re-walking.
    /// </summary>
    static MixedSwing MixedStrike(
        IReadOnlyList<ActionSchedule.ActionOption> options, IReadOnlyDictionary<string, ActionSchedule.PoolState> pools,
        double baseDamage, CombatActorSnapshot attacker, CombatActorSnapshot defender, double targetHp, int maxRounds,
        ActionSchedule.SchedulePolicy? policy)
    {
        var outcomes = ActionSchedule.Walk(options, pools, baseDamage, maxRounds, policy);
        var cache = new Dictionary<double, StrikeMixture.Result?>();

        double cumDamage = 0, cumMean = 0, cumVar = 0, cumPHit = 0, cumBase = 0;
        var atoms = new List<StrikeAtom>();
        var used = 0;
        foreach (var outcome in outcomes)
        {
            used++;
            if (outcome.DamageMultiplier <= 0)
            {
                atoms.Add(new StrikeAtom(1.0, 0.0)); // "pass": weight folded in below, deals nothing.
                continue;
            }

            if (!cache.TryGetValue(outcome.DamageMultiplier, out var mix))
            {
                mix = StrikeMixture.Compute(baseDamage * outcome.DamageMultiplier, attacker, defender);
                cache[outcome.DamageMultiplier] = mix;
            }
            var m = mix!.Value;

            cumMean += m.Mean;
            cumVar += m.Variance;
            cumPHit += 1.0 - m.Miss.Probability;
            cumBase += baseDamage * outcome.DamageMultiplier;
            cumDamage += m.Mean;
            atoms.Add(m.Miss); atoms.Add(m.Parried); atoms.Add(m.Blocked); atoms.Add(m.Clean); atoms.Add(m.CleanCrit);
            if (cumDamage >= targetHp) break;
        }

        if (used == 0) return new MixedSwing(0, 0, 0, 0, Array.Empty<StrikeAtom>());

        // Every atom weighted by 1/used so the combined list still sums to 1 across `used` rounds --
        // mirrors Analytic.Walk's own "atoms" reconstruction (each action's own atoms scaled by how
        // often that action fired, over the rounds that actually happened).
        var weighted = atoms.ConvertAll(at => new StrikeAtom(at.Probability / used, at.Damage));
        return new MixedSwing(cumMean / used, cumVar / used, cumPHit / used, cumBase / used, weighted);
    }
}
