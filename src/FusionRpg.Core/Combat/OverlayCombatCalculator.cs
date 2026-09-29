using FusionRpg.Contracts;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat;

public sealed class OverlayCombatRequest
{
    /// <summary>
    /// combat-numerics (lawn-combat-wire T4): was <c>double</c>. Every real caller already hands this
    /// an integral magnitude — <c>OverlayCombatMath.Finalize</c> passes <c>Math.Abs(signedAmount)</c>
    /// (a <c>long</c>), <c>BasicAttack.cs</c> passes the action base (<c>ActionBaseMath.BasePerHit</c>,
    /// a <c>long</c>),
    /// <c>DebugCombatActions.cs</c> passes <c>Math.Abs(amount)</c> — so <c>long</c> here is a pure
    /// representation fix (an integral magnitude carried in <c>long</c>, whose range holds it), not a
    /// behavior change. This is the entry point of the file's own magnitude path.
    /// </summary>
    public long BaseOverlayDamage { get; init; }
    public IReadOnlyList<ElementPayloadComponent> Components { get; init; } = Array.Empty<ElementPayloadComponent>();
    public CombatActorSnapshot Attacker { get; init; } = CombatActorSnapshot.AttackerLess();
    public CombatActorSnapshot Defender { get; init; } = new(ActorDerivedSnapshot.StubNeutral(), ActorElementTypes.Neutral);

    /// <summary>
    /// <c>skill.effectiveness.{category}</c> (Feeder class, spec-skill-modifiers.md §2) — scales
    /// <see cref="BaseOverlayDamage"/> BEFORE the power/defense delta, so <c>combat.defense</c>
    /// already answers it. Default <c>1.0</c> is a true no-op, and every call site that does not name
    /// a channel is byte-identical. <b>Stays <c>double</c> deliberately (combat-numerics, lawn-combat-
    /// wire T4):</b> unlike <see cref="BaseOverlayDamage"/>, this one has a real, live production
    /// caller today — <c>Actions/BasicAttack.cs</c> S3 sets it from
    /// <c>MultiplierFromPerMille(SkillEffectivenessPm(...))</c>, and <c>Battle/SkillChannelReaderTests.cs</c>
    /// asserts <c>MultiplierFromPerMille</c>'s <c>double</c> return directly — both files are outside
    /// this task's permitted scope, so this property and <see cref="MultiplierFromPerMille"/> keep
    /// their shipped <c>double</c> shape rather than risk a compile break in a caller this module may
    /// not edit. Moving the application point after mitigation would also make the family `Contest`
    /// and oblige a `.reduction` half — a breaking change, not a refactor.
    /// </summary>
    public double EffectivenessMultiplier { get; init; } = 1.0;

    /// <summary>
    /// Converts a <c>skill.effectiveness.{category}</c> channel value (per-mille, <b>0 = neutral</b>
    /// because that is the channel's registered default) into the multiplier above:
    /// <c>1.0 + pm / 1000</c>. So 0 gives exactly 1.0 and the call site is byte-identical, 250 gives
    /// +25%, and -250 gives -25%.
    ///
    /// <para><b>Why this conversion lives in Combat/ and not at the call site.</b> The caller is
    /// <c>Actions/BasicAttack.cs</c>; keeping the per-mille-to-multiplier conversion next to the
    /// resolver that consumes it gives one definition of the seam — a `long` per-mille in and a
    /// `double` multiplier out. (This placement was once justified by an action-layer floating-point
    /// ban; that ban was removed by the 2026-09-15 owner ruling, and floating point is allowed at the
    /// call site too.)</para>
    /// </summary>
    public static double MultiplierFromPerMille(long perMille) => 1.0 + perMille / 1000.0;

    /// <summary>Debug/test: when set, overrides the hit roll.</summary>
    public bool? ForceHit { get; init; }

    /// <summary>Debug/test: when set and hit, overrides the crit roll.</summary>
    public bool? ForceCrit { get; init; }

    /// <summary>Host resolution profile — overlay default keeps behavior byte-identical.</summary>
    public CombatProfile Profile { get; init; } = CombatProfile.Overlay;
}

/// <summary>
/// Overlay damage pipeline — combat-damage-ssot.md §6.
///
/// <para><b>combat-numerics (lawn-combat-wire T4) numeric scope:</b> the magnitude BOUNDARY of this
/// file is <c>long</c>/checked end to end — <see cref="OverlayCombatRequest.BaseOverlayDamage"/> in,
/// the parry/block chip removal (<see cref="ClampedContest"/>), and the final <c>signedDelta</c> out.
/// The CONTINUOUS mitigation-chain interior (power, defense, penetration/absorption, pierce/amp
/// factors, hit/crit/parry/block probabilities) is <c>double</c>, and that is correct: floating point
/// is allowed for any quantity (owner ruling 2026-09-15 removed the project-wide floating-point ban),
/// so there is no <c>double</c> here to "fix". The inputs are <c>double</c> too —
/// <c>CombatDerivedReader.cs</c> channel reads over <c>ActorDerivedSnapshot</c>'s double storage,
/// <c>CombatPolicy.cs</c>'s tuning shape constants (e.g. <c>defenseDivisorK: 0.45</c>), and
/// <c>CombatProbability.cs</c>'s <c>Sigmoid</c>/<c>RollSuccess</c>.</para>
/// <para>The rules that DO apply are integer RANGE rules at the boundary: what changed here is the
/// entry magnitude, the two named defects (divide-before-multiply, unchecked overflow exit), and
/// every narrowing-to-<c>long</c> conversion in this file, now <c>checked</c> so an out-of-range
/// result throws instead of wrapping.</para>
/// </summary>
public sealed class OverlayCombatCalculator
{
    readonly IElementHub _elementHub;

    public OverlayCombatCalculator(IElementHub? elementHub = null) =>
        _elementHub = elementHub ?? ElementHub.Default;

    public (long SignedDelta, OverlayCombatBreakdown Breakdown) Compute(
        OverlayCombatRequest request,
        ICombatRng rng)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (rng == null) throw new ArgumentNullException(nameof(rng));

        // spec-skill-modifiers.md §2: effectiveness scales the base damage BEFORE anything reads it —
        // matchup bonus, power/defense delta, and the min-chip floor all key off this one value, so a
        // "louder" hit is louder everywhere the shipped BaseOverlayDamage already was, not just in the
        // final sum. Default multiplier 1.0 makes this line a no-op for every shipped caller.
        var effectiveBaseDamage = request.BaseOverlayDamage * request.EffectivenessMultiplier;

        // Omni fallback (combat-unification, resolver-core): an EMPTY component list is a
        // legal untyped attack resolved over the omni halves only — matchup 0. Replaces the
        // former hard throw; content-boundary validation (ElementPayload.Validate) stays
        // strict, and the overlay dispatcher never builds an empty request (payload-null is
        // pass-through in OverlayCombatMath).
        var omniFallback = request.Components.Count == 0;
        if (!omniFallback)
            ElementPayload.Validate(request.Components);

        var matchupBonus = omniFallback
            ? 0.0
            : _elementHub.ResolvePayloadBonus(
                request.Components,
                request.Defender.ElementTypes,
                effectiveBaseDamage);

        var weightedDelta = 0.0;
        // Offense and mitigation accumulated separately, for DefenseShape.Divisive only. The
        // Subtractive path still uses weightedDelta computed exactly the way it always was.
        var weightedOffense = 0.0;
        var weightedDefense = 0.0;
        // Power WITHOUT the matchup bonus and WITHOUT effectiveness — see DivisiveMitigation's
        // ladderScale. Both excluded terms are effectiveness-scaled, and effectiveness must not
        // reach the divisor.
        var weightedPowerOnly = 0.0;
        var ampDelta = 0.0;
        // D14: parry/block deltas accumulate per component exactly as ampDelta does, for exactly the
        // same reason its own comment gives -- component weights sum to 1.0 (ElementPayload.Validate),
        // so accumulating omni + element weighted is identical to adding omni once and weighting only
        // the element half. No "add omni once" path is needed and none is written.
        var parryRateDelta = 0.0;
        var blockRateDelta = 0.0;
        var parryStrengthDelta = 0.0;
        var blockStrengthDelta = 0.0;
        var pHitFinal = 0.0;
        var pCritFinal = 0.0;
        var critMultFinal = 0.0;
        var pierceScale = CombatPolicy.Default.PierceScale;
        var ampScale = CombatPolicy.Default.AmpScale;

        if (omniFallback)
        {
            var atk = request.Attacker.Derived;
            var def = request.Defender.Derived;

            // spec-mitigation-chain.md §2: penetration/absorption scale defense INSIDE the delta.
            var penDeltaOmni = atk.Get(DerivedStatChannels.CombatPenetrationOmni)
                               - def.Get(DerivedStatChannels.CombatAbsorptionOmni);
            var effectiveDefenseOmni = def.Get(DerivedStatChannels.CombatDefenseOmni) * PierceFactor(penDeltaOmni, pierceScale);

            weightedDelta = atk.Get(DerivedStatChannels.CombatPowerOmni) - effectiveDefenseOmni;
            weightedOffense = atk.Get(DerivedStatChannels.CombatPowerOmni);
            weightedPowerOnly = weightedOffense; // omni fallback has no matchup term at all
            weightedDefense = effectiveDefenseOmni;
            ampDelta += atk.Get(DerivedStatChannels.CombatAmplificationOmni) - def.Get(DerivedStatChannels.CombatReductionOmni);
            // Through ElementalResolver.Contest, which is CombatProbability.Sigmoid on the
            // difference — the same call, named. Bit-identical: the subtraction still happens in the
            // caller and the delegation adds no arithmetic, so no golden can move.
            pHitFinal = ElementalResolver.Contest(
                atk.Get(DerivedStatChannels.CombatAccuracyOmni), def.Get(DerivedStatChannels.CombatDodgeOmni),
                CombatProbabilityPolicy.AccuracyScale);
            pCritFinal = ElementalResolver.Contest(
                atk.Get(DerivedStatChannels.CombatCritRateOmni), def.Get(DerivedStatChannels.CombatCritResistOmni),
                CombatProbabilityPolicy.CritRateScale);
            critMultFinal = 1.0 + ElementalResolver.Contest(
                atk.Get(DerivedStatChannels.CombatCritDamageOmni), def.Get(DerivedStatChannels.CombatCritResistDamageOmni),
                CombatProbabilityPolicy.CritDamageScale);

            // An untyped attack has no element half, so parry/block read the omni channels directly --
            // the same shape the branch above uses for every other family, and byte-identical to what
            // this file computed before D14 moved the typed case into the loop.
            parryRateDelta = def.Get(DerivedStatChannels.CombatParryRateOmni) - atk.Get(DerivedStatChannels.CombatParryBreakOmni);
            blockRateDelta = def.Get(DerivedStatChannels.CombatBlockRateOmni) - atk.Get(DerivedStatChannels.CombatBlockBreakOmni);
            parryStrengthDelta = def.Get(DerivedStatChannels.CombatParryStrengthOmni) - atk.Get(DerivedStatChannels.CombatParryShredOmni);
            blockStrengthDelta = def.Get(DerivedStatChannels.CombatBlockStrengthOmni) - atk.Get(DerivedStatChannels.CombatBlockShredOmni);
        }

        foreach (var c in request.Components)
        {
            var componentBonus = _elementHub.ResolveComponentBonus(
                c.Element,
                request.Defender.ElementTypes,
                effectiveBaseDamage);

            var power = CombatDerivedReader.Power(request.Attacker.Derived, c.Element);

            // spec-mitigation-chain.md §2.1: penetration scales the defender's mitigation -- a target
            // with no defense gains nothing from an attacker's penetration. pierceFactor is bounded
            // (0,1]: penetration can push defense arbitrarily close to zero but never below it or
            // beyond (absorption cancels it back toward 1.0, never past).
            var penDelta = CombatDerivedReader.Penetration(request.Attacker.Derived, c.Element)
                          - CombatDerivedReader.Absorption(request.Defender.Derived, c.Element);
            var defense = CombatDerivedReader.Defense(request.Defender.Derived, c.Element) * PierceFactor(penDelta, pierceScale);

            var effectiveDelta = (power - defense) + componentBonus;
            weightedDelta += c.Weight * effectiveDelta;

            // Divisive's own accumulators, kept SEPARATE rather than deriving weightedDelta from
            // them: floating-point addition is not associative, so `w·((p−d)+b)` and
            // `w·(p+b) − w·d` are not guaranteed bit-identical. Recomputing weightedDelta from a
            // split would risk moving a golden on the Subtractive path for no reason at all.
            weightedOffense += c.Weight * (power + componentBonus);
            weightedPowerOnly += c.Weight * power;
            weightedDefense += c.Weight * defense;

            // spec-mitigation-chain.md §2.3: amplification/reduction apply ONCE to the already-summed
            // final damage, not per component -- accumulating omni+element here, weighted, produces
            // the same result as "add omni once" since weights sum to 1.0 (ElementPayload.Validate).
            ampDelta += c.Weight * (CombatDerivedReader.Amplification(request.Attacker.Derived, c.Element)
                                    - CombatDerivedReader.Reduction(request.Defender.Derived, c.Element));

            // Same three contests as the omni-fallback branch above, over this component's
            // element instead of the omni halves. Each reader already returns omni + element, which
            // is the additive rule ElementalResolver.Total states and a test now pins.
            pHitFinal += c.Weight * ElementalResolver.Contest(
                CombatDerivedReader.Accuracy(request.Attacker.Derived, c.Element),
                CombatDerivedReader.Dodge(request.Defender.Derived, c.Element),
                CombatProbabilityPolicy.AccuracyScale);

            pCritFinal += c.Weight * ElementalResolver.Contest(
                CombatDerivedReader.CritRate(request.Attacker.Derived, c.Element),
                CombatDerivedReader.CritResist(request.Defender.Derived, c.Element),
                CombatProbabilityPolicy.CritRateScale);

            critMultFinal += c.Weight * (1.0 + ElementalResolver.Contest(
                CombatDerivedReader.CritDamage(request.Attacker.Derived, c.Element),
                CombatDerivedReader.CritResistDamage(request.Defender.Derived, c.Element),
                CombatProbabilityPolicy.CritDamageScale));

            // D14: the defender parries and blocks, so the roles are inverted here relative to every
            // other family in this loop -- defender minus attacker, not attacker minus defender.
            parryRateDelta += c.Weight * (CombatDerivedReader.ParryRate(request.Defender.Derived, c.Element)
                                          - CombatDerivedReader.ParryBreak(request.Attacker.Derived, c.Element));
            blockRateDelta += c.Weight * (CombatDerivedReader.BlockRate(request.Defender.Derived, c.Element)
                                          - CombatDerivedReader.BlockBreak(request.Attacker.Derived, c.Element));
            parryStrengthDelta += c.Weight * (CombatDerivedReader.ParryStrength(request.Defender.Derived, c.Element)
                                              - CombatDerivedReader.ParryShred(request.Attacker.Derived, c.Element));
            blockStrengthDelta += c.Weight * (CombatDerivedReader.BlockStrength(request.Defender.Derived, c.Element)
                                              - CombatDerivedReader.BlockShred(request.Attacker.Derived, c.Element));
        }

        // spec-evasion-chain.md §3 (T5.3) — one roll, cumulative bands: miss / parried / blocked /
        // clean hit, resolved from the SAME single draw the hit roll already made (zero additional
        // RNG consumption). Rate contests are linear and permille, not sigmoid: rate/break already
        // land in permille units (matching blockCapPermille et al.'s own units), and a sigmoid would
        // give 0.5 at delta=0 — a 50% parry chance for every actor before any content ever authors
        // parry.rate, which is not "empty bands are a no-op", it is a new default nobody chose.
        // D14: the two deltas were read here from the omni channels alone; they are now accumulated
        // above over whichever path ran, so this line is the same contest over a total that includes
        // the element half. ElementalResolver.RateFromZero is the shape, and it is deliberately not a
        // sigmoid -- see its own doc comment, and the paragraph below.
        var pParryRaw = ElementalResolver.RateFromZero(parryRateDelta, 0.0, 1000.0);
        var pBlockRaw = ElementalResolver.RateFromZero(blockRateDelta, 0.0, 1000.0);

        var avoidanceBandCap = CombatPolicy.Default.AvoidanceBandCapPermille / 1000.0;
        var (pParry, pBlock) = CapAvoidanceBand(pHitFinal, pParryRaw, pBlockRaw, avoidanceBandCap);

        bool miss, parried, blocked;
        if (request.ForceHit is { } forcedHit)
        {
            // Debug override predates parry/block: it must keep consuming zero draws and keep
            // meaning "skip the whole outcome resolution", exactly as every existing caller assumes.
            miss = !forcedHit;
            parried = false;
            blocked = false;
        }
        else if (pHitFinal <= 0.0)
        {
            // Matches CombatProbability.RollSuccess's own probability<=0 early return: always miss,
            // no draw. CombatSsotContractTests.Saturated_probabilities_consume_no_draw pins this.
            miss = true;
            parried = false;
            blocked = false;
        }
        else if (pHitFinal >= 1.0 && pParry <= 0.0 && pBlock <= 0.0)
        {
            // Matches RollSuccess's probability>=1 early return: always a clean hit, no draw --
            // ONLY when there is no parry/block band to still distinguish (both zero here means
            // there is nothing left a draw could resolve; if either is nonzero, a real hit still
            // could be parried/blocked instead of clean, so the draw below is still needed).
            miss = false;
            parried = false;
            blocked = false;
        }
        else
        {
            var r = rng.Next(1_000_000) / 1_000_000.0;
            (miss, parried, blocked) = ResolveBand(r, pHitFinal, pParry, pBlock);
        }
        var cleanHit = !miss && !parried && !blocked;
        var crit = cleanHit && (request.ForceCrit ?? CombatProbability.RollSuccess(rng, pCritFinal));

        var powerAdjusted = CombatPolicy.Default.DefenseShape == DefenseShape.Divisive
            ? DivisiveMitigation(
                offense: effectiveBaseDamage + weightedOffense,
                defense: weightedDefense,
                k: CombatPolicy.Default.DefenseDivisorK,
                // The divisor reads the LADDER-scaled hit — authored base plus power — never
                // effectiveness or matchup. See DivisiveMitigation.
                ladderScale: request.BaseOverlayDamage + weightedPowerOnly)
            : effectiveBaseDamage + weightedDelta;
        double finalDamage;
        if (miss)
            finalDamage = 0;
        else if (parried || blocked)
        {
            // spec §3: "no block, no mitigation" — a parried or blocked hit ends resolution here;
            // the mitigation chain (penetration/defense/crit/amplification) never runs for it. Uses
            // ClampedContest exactly like shield (spec-evasion-chain.md §2) — permille long
            // throughout, so the double base/delta round to long at this one boundary, matching how
            // the whole pipeline already rounds only once at its own final long conversion. No
            // elemMod concept for either (deltaBase == boundsBase): a fully shredded proc removes
            // zero (floor 0 — no immunity-by-non-spend concern, block/parry has no pool to protect),
            // a maximal one removes at most 950‰, never all of it.
            // combat-numerics (T4): narrowing an unbounded double magnitude to `long` — `checked` so
            // an out-of-range value THROWS (docs/architecture/numeric-types.md: "overflow throws, never wraps") instead of the
            // previous unchecked cast, which on an out-of-range double silently returns an
            // unspecified value. Same treatment on every long-narrowing conversion below.
            var baseLong = checked((long)Math.Round(effectiveBaseDamage, MidpointRounding.AwayFromZero));
            // The neutral removal, before strength/shred moves it. At the shipped 1000‰ this is
            // exactly baseLong (x * 1.0 is exact in IEEE, so byte-identical); below 1000‰ it seats
            // the neutral point INSIDE the [0, cap] range so strength and shred both do something.
            // Bounds still scale against the full hit — what is capped is the share of THIS hit.
            //
            // combat-numerics (T4): was `effectiveBaseDamage * (ParryNeutralShareKPm / 1000.0)` —
            // divided by 1000 BEFORE multiplying (docs/architecture/numeric-types.md's named anti-pattern: "per-mille
            // intermediates are 1000x closer to the ceiling", and dividing first throws away
            // precision the multiply-first order preserves). Reordered to multiply by the per-mille
            // share first and divide by 1000 last, exactly once — same double arithmetic (this value
            // is still downstream of the double-typed EffectivenessMultiplier seam), same rounding
            // mode, byte-identical result for every shipped `ParryNeutralShareKPm` (500 today), but no
            // longer the shape the rule bans.
            var neutralBase = checked((long)Math.Round(
                effectiveBaseDamage * CombatPolicy.Default.ParryNeutralShareKPm / 1000.0,
                MidpointRounding.AwayFromZero));
            var removed = parried
                ? ClampedContest.Apply(
                    deltaBase: neutralBase,
                    delta: checked((long)Math.Round(parryStrengthDelta, MidpointRounding.AwayFromZero)),
                    hitCount: 1, boundsBase: baseLong,
                    floorKPm: 0, capKPm: CombatPolicy.Default.ParryCapPermille)
                : ClampedContest.Apply(
                    deltaBase: neutralBase,
                    delta: checked((long)Math.Round(blockStrengthDelta, MidpointRounding.AwayFromZero)),
                    hitCount: 1, boundsBase: baseLong,
                    floorKPm: 0, capKPm: CombatPolicy.Default.BlockCapPermille);
            finalDamage = Math.Max(0.0, effectiveBaseDamage - removed);
        }
        else
        {
            finalDamage = Math.Max(0, powerAdjusted);
            if (crit)
                finalDamage *= critMultFinal;

            // spec-mitigation-chain.md §2.2: amplification lands after crit; multiplication commutes,
            // so the order between critMultFinal and ampFactor is arithmetically irrelevant. ampFactor
            // stays a plain, unclamped multiplier on the way up (no ceiling — PS-8, AmpIsUnclamped);
            // the Math.Max(0.0, ...) floor is structural, not a balance cap, matching the
            // Math.Max(0, powerAdjusted) floor two lines above it: it stops overwhelming reduction
            // from flipping a positive finalDamage negative (which downstream would just read as "no
            // damage" via the signedDelta conversion below, but silently, not provably), not "capping
            // amplification" — amplification's own contribution to ampDelta is still fully unbounded.
            finalDamage *= CombatPolicy.Default.AmpShape == AmpShape.Reciprocal
                ? AmpFactorReciprocal(ampDelta, ampScale)
                : AmpFactor(ampDelta, ampScale);

            // Min-chip floor (owner decision 6): profile-scoped — a landed hit deals at
            // least ceil(share × base), min 1. Overlay profile is 0 → this branch is dead
            // there and behavior stays byte-identical.
            if (request.Profile.MinChipShareKPm > 0)
            {
                var chip = Math.Max(1.0,
                    Math.Ceiling(effectiveBaseDamage * request.Profile.MinChipShareKPm / 1000.0));
                if (finalDamage < chip)
                    finalDamage = chip;
            }
        }

        // combat-numerics (T4): was an UNCHECKED long-narrowing cast of `-Math.Round(finalDamage)` —
        // on a `finalDamage` outside `long`'s range (or NaN/Infinity), an unchecked double-to-long cast returns an
        // unspecified value rather than failing loudly. `checked` makes that throw `OverflowException`
        // instead (docs/architecture/numeric-types.md: "overflow throws, never wraps"); see
        // OverlayCombatOverflowTests.Compute_throws_on_a_magnitude_past_long_range.
        var signedDelta = finalDamage > 0 ? checked(-(long)Math.Round(finalDamage)) : 0L;
        var breakdown = new OverlayCombatBreakdown
        {
            Hit = !miss,
            Crit = crit,
            Parried = parried,
            Blocked = blocked,
            MatchupBonus = matchupBonus,
            WeightedDelta = weightedDelta,
            PowerAdjustedDamage = powerAdjusted,
            FinalSignedDelta = signedDelta,
            PHitFinal = pHitFinal,
            PCritFinal = pCritFinal,
            CritMultiplierFinal = critMultFinal
        };

        return (signedDelta, breakdown);
    }

    /// <summary>
    /// spec-evasion-chain.md §3 — one draw <paramref name="r"/> (already in [0,1)) against cumulative
    /// bands. `miss` uses the EXACT SAME comparison <c>CombatProbability.RollSuccess(rng, pHitFinal)</c>
    /// already used (<c>draw &lt; pHitFinal ⟺ hit</c>, so <c>draw &gt;= pHitFinal ⟺ miss</c>) — parry/
    /// block are carved out of the TOP of the "would-have-been-a-hit" region, just below
    /// <paramref name="pHitFinal"/>, never out of the miss region's low end. This is what makes
    /// RateGoldensUnchangedAtZero hold: at <paramref name="pParry"/> = <paramref name="pBlock"/> = 0
    /// this collapses to exactly today's <c>r &lt; pHitFinal</c> hit condition, by arithmetic, no
    /// special case. The three outcomes partition [0,1) with no gap and no overlap (BandsAreExclusive).
    /// </summary>
    public static (bool Miss, bool Parried, bool Blocked) ResolveBand(double r, double pHitFinal, double pParry, double pBlock)
    {
        var miss = r >= pHitFinal;
        var parried = !miss && r >= pHitFinal - pParry;
        var blocked = !miss && !parried && r >= pHitFinal - pParry - pBlock;
        return (miss, parried, blocked);
    }

    /// <summary>
    /// spec-evasion-chain.md §3.1 — the CUMULATIVE avoidance band (miss + parry + block) caps at
    /// <paramref name="avoidanceBandCap"/> (950‰ shipped) so an attack always retains its own
    /// independent ≥5% chance to land. Only <paramref name="pParryRaw"/>/<paramref name="pBlockRaw"/>
    /// scale down to make room — <paramref name="pHitFinal"/> (accuracy/dodge) is untouched, so a
    /// matchup where miss alone already exceeds the cap (extreme dodge stacking) is not newly capped
    /// by this module; T5.3 only bounds what it adds. At <paramref name="pParryRaw"/> =
    /// <paramref name="pBlockRaw"/> = 0 the scale branch never triggers — empty bands stay
    /// byte-identical by arithmetic, no guard clause (RateGoldensUnchangedAtZero).
    /// </summary>
    public static (double Parry, double Block) CapAvoidanceBand(double pHitFinal, double pParryRaw, double pBlockRaw, double avoidanceBandCap)
    {
        var missChance = 1.0 - pHitFinal;
        var rawParryBlock = pParryRaw + pBlockRaw;
        var roomForParryBlock = Math.Max(0.0, avoidanceBandCap - missChance);
        if (rawParryBlock > roomForParryBlock && rawParryBlock > 0)
        {
            var scale = roomForParryBlock / rawParryBlock;
            return (pParryRaw * scale, pBlockRaw * scale);
        }
        return (pParryRaw, pBlockRaw);
    }

    public static IReadOnlyList<ElementPayloadComponent> ParseComponents(IEnumerable<ElementPayloadComponentDto>? dtos)
    {
        if (dtos == null || !dtos.Any())
            return Array.Empty<ElementPayloadComponent>();

        var list = new List<ElementPayloadComponent>();
        foreach (var dto in dtos)
        {
            if (string.IsNullOrWhiteSpace(dto.Element))
                throw new ArgumentException("Element payload component missing element id.");
            if (!ElementRoster.TryParse(dto.Element, out var parsed))
                throw new ArgumentException($"Unknown element id '{dto.Element}'.");
            list.Add(new ElementPayloadComponent(parsed, dto.Weight));
        }

        return list;
    }

    /// <summary>
    /// spec-mitigation-chain.md §2.1. 1.0 at <paramref name="penDelta"/> = 0 (identity — byte-identical
    /// at defaults). Bounded (0,1]: <c>Math.Max(0.0, penDelta)</c> means a NEGATIVE delta (net
    /// absorption) floors at exactly 1.0 rather than granting defense a bonus past its own base value
    /// — absorption cancels penetration, it does not amplify defense. Structural, not a PS-8 ceiling:
    /// negative defense would turn mitigation into a second, unintended damage source.
    /// </summary>
    public static double PierceFactor(double penDelta, double pierceScale) =>
        1.0 / (1.0 + Math.Max(0.0, penDelta) / pierceScale);

    /// <summary>
    /// spec-mitigation-chain.md §2.2. 1.0 at <paramref name="ampDelta"/> = 0 (identity). Unclamped on
    /// the way up — arbitrarily large amplification keeps scaling, no ceiling (PS-8). The
    /// <c>Math.Max(0.0, ...)</c> is a structural floor against a sign flip from overwhelming
    /// reduction, not a cap on amplification's own contribution.
    /// </summary>
    public static double AmpFactor(double ampDelta, double ampScale) =>
        Math.Max(0.0, 1.0 + ampDelta / ampScale);

    /// <summary>
    /// <see cref="AmpShape.Reciprocal"/> — <see cref="AmpFactor"/>'s reducing half made asymptotic,
    /// mirroring <see cref="PierceFactor"/>'s own shape. Identical to <see cref="AmpFactor"/> for
    /// every <paramref name="ampDelta"/> ≥ 0 (both are <c>1 + d/s</c>, still unbounded upward, PS-8),
    /// so nothing on the amplifying side changes. Below zero it returns <c>1/(1 + |d|/s)</c>, which
    /// approaches zero without reaching it — so <c>reduction</c> always helps and can never confer
    /// the total immunity <see cref="AmpFactor"/> hands out at <c>ampDelta ≤ −ampScale</c>.
    /// </summary>
    public static double AmpFactorReciprocal(double ampDelta, double ampScale)
    {
        if (ampDelta >= 0) return 1.0 + ampDelta / ampScale;
        return 1.0 / (1.0 - ampDelta / ampScale);
    }

    /// <summary>
    /// <see cref="DefenseShape.Divisive"/>: <c>offense × K/(K + defense)</c> with
    /// <c>K = k × offense</c>. Identity at <c>defense = 0</c>, asymptotic to zero as defense grows
    /// — so there is no subtractive cliff where a defender simply becomes immune, and no clamp is
    /// needed to prevent negative damage (the curve never crosses zero). Scale-invariant: doubling
    /// offense and defense together leaves the mitigated FRACTION unchanged, which is the property
    /// a quadratic power ladder needs and a constant divisor cannot give (ssot-power-scale.md §2).
    /// </summary>
    /// <param name="ladderScale">What <c>K</c> is measured against — the authored hit plus power,
    /// both `P(Θ)`-scale magnitudes. Deliberately NOT <paramref name="offense"/>: offense also
    /// carries <c>skill.effectiveness</c> and the matchup bonus, and letting a per-action multiplier
    /// into the divisor would make effectiveness superlinear (it would scale the numerator AND
    /// shrink the mitigated fraction), breaking its locked `Feeder` classification. Measured before
    /// this split: a 1000x effectiveness took damage from ~0 to 826 against a defense wall. Reading
    /// only ladder quantities keeps the mitigated fraction constant with respect to effectiveness —
    /// so effectiveness stays exactly linear and `combat.defense` still answers it, proportionally
    /// rather than absolutely. Scale invariance is unaffected: base and power both climb with the
    /// ladder, so the fraction is unchanged when attacker and defender advance together.</param>
    public static double DivisiveMitigation(double offense, double defense, double k, double ladderScale)
    {
        if (offense <= 0) return offense;
        var denom = k * ladderScale;
        // k <= 0, or a degenerate ladder scale, disables mitigation rather than dividing by zero.
        if (denom <= 0) return offense;

        // NEGATIVE defense amplifies, mirrored around 1.0 — `2 - K/(K + |defense|)`. Clamping it to
        // zero instead would silently delete the glass-cannon mechanic the subtractive shape has
        // always had (`ActorDerivedProfiles.CombatGlass` ships defense.omni = -50, and a defense
        // debuff can push any actor below zero). Caught by Combat_glass_vs_neutral_increases_damage
        // rather than reasoned about in advance. Same construction League of Legends uses for
        // negative resistances, and it is continuous at 0: both branches give exactly 1.0 there.
        if (defense < 0)
            return offense * (2.0 - denom / (denom - defense));
        return offense * denom / (denom + defense);
    }
}
