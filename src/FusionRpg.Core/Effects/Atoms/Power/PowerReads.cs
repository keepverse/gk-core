using FusionRpg.Core.Combat.Shield;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Effects.Atoms.Power;

/// <summary>
/// The display scalar (spec-power-reads.md, E10).
///
/// <para><c>geomean(vᵢ + 1) − 1</c> over <b>all five</b> categories. The plain geometric mean is
/// wrong here: any zero factor makes the product exactly zero, and most atoms touch one or two of
/// five — so nearly every atom would score 0 and "balanced beats glass cannon" would be comparing 0
/// to 0. The <c>+1</c> makes an untouched category a factor of one rather than an annihilator.</para>
///
/// <para><b>Integer, and exact.</b> A fifth root by <c>Math.Pow</c> is not bit-reproducible across
/// runtimes — two machines could disagree in the last digit for identical content. The root is
/// computed by integer binary search instead: the largest <c>r</c> with <c>r⁵ ≤ product</c>, which is
/// the same answer everywhere, forever. <b>Correction (completeness-audit.md C5):</b> nothing stamps
/// this scalar into a hashed report today. ⚠ <b>Corrected again 2026-09-23:</b> this paragraph used to
/// say <i>"<c>PowerScalar.Of</c> has no production caller"</i>, and that is literally false —
/// <c>ItemPowerReads.CardPower</c> calls it (`ItemPowerReads.cs:87`). What is true, and what the
/// sentence was trying to say, is that no HOST reaches that caller: <c>CardPower</c>'s own only caller
/// is <c>MutationPreview.Display</c> (`MutationPreview.cs:42`), which nothing in <c>src/</c> calls, and
/// the card route deliberately leaves <c>ItemCardInput.Power</c> null
/// (`RpgStore.ItemCard.cs:174-176`, `ItemCardEndpoints.cs:481`). So the reproducibility guarantee is
/// still the right property for a number a UI or an API will eventually display, and it is still not
/// load-bearing anywhere — but a reader checking the claim will find a caller, so it now says which
/// kind of "no caller" it means. Do not restate the old claim without a real caller to point at.</para>
///
/// <para><b>What it does not mean.</b> Two things touching a different <i>number</i> of categories
/// are scored on different bases and are <b>not</b> meaningfully comparable. Pokémon GO's CP has
/// close to this shape and is documented as misleading for exactly this reason. The shape is
/// borrowed; the claim that the number is precise is not. It sorts like-for-like and nothing more —
/// anything needing a real comparison uses the vector or the marginal read.</para>
/// </summary>
public static class PowerScalar
{
    // Structural (tunables-ssot.md T2) — the closed-vocabulary category count PowerVector's shape
    // is built from below, not a balance dial.
    public const int Categories = 5;

    /// <summary>
    /// The scalar, in whole points. Exactly 0 when every category is 0.
    /// </summary>
    public static int Of(PowerVector v)
    {
        if (v.IsZero) return 0;

        // (vᵢ + 1), clamped at 1 so a negative category cannot flip the product's sign. A negative
        // price is not a thing the cost function produces, and if one ever appears it must not turn
        // an item's whole score inside out on the way to a UI.
        //
        // The product is a BigInteger because it genuinely does not fit: five categories near 6000
        // each already saturate a 64-bit integer, and a late-game actor summing over several items
        // reaches that. The first cut used `long` with `checked` and threw — a crash on the display
        // path for an actor that was merely strong.
        var product = System.Numerics.BigInteger.One;
        for (var i = 0; i < Categories; i++)
            product *= Math.Max(1, (long)v[i] + 1);

        var root = IntegerFifthRoot(product) - 1;
        return root > int.MaxValue ? int.MaxValue : (int)root;
    }

    /// <summary>
    /// The largest <c>r</c> with <c>r⁵ ≤ value</c>, by binary search on exact integers.
    ///
    /// <para>Deliberately not <c>Math.Pow(value, 0.2)</c>: <c>pow</c> is permitted to differ in the
    /// last bit between runtimes and between hardware, and this result is hashed.</para>
    /// </summary>
    public static System.Numerics.BigInteger IntegerFifthRoot(System.Numerics.BigInteger value)
    {
        if (value <= 1) return value;

        // The geometric mean never exceeds the largest factor, so the search is bounded tightly
        // rather than by doubling from one.
        System.Numerics.BigInteger low = 1, high = value;
        while (low < high)
        {
            var mid = low + (high - low + 1) / 2;
            var p = mid * mid;
            p = p * p * mid;
            if (p <= value) low = mid; else high = mid - 1;
        }
        return low;
    }
}

/// <summary>
/// The matchup-conditioned read (E10) — "how strong is this actor <b>against that one</b>", which is
/// a different question from "how strong is this actor" and cannot be retrofitted onto a stored
/// scalar.
///
/// <para><b>Two matrices, and they are not interchangeable.</b> The combat ring answers a combat
/// question and the shield matrix answers a shield question. Using one for the other is wrong by 25%
/// per slot — and because slots multiply, two strong slots are <c>1.25 × 1.25 = 1.5625</c>, +562.5‰,
/// where naive addition says +500‰. That compounding is the whole reason this read exists.</para>
/// </summary>
public static class MatchupRead
{
    static EffectsTuning? _tuning;

    /// <summary>Host-only (Injector/Server startup, or a test's inline construction).</summary>
    public static void Configure(EffectsTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static EffectsTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "MatchupRead.Configure(...) has not run. SlotShareMilli reads data/tuning/effects.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>How much one strong or weak slot moves the price, per-mille.</summary>
    public static int SlotShareMilli => Tuning.MatchupReadSlotShareMilli;

    /// <summary>
    /// The attacker's offense, conditioned on the defender's elements.
    ///
    /// <para>Slots <b>multiply</b>. Adding them would price two strong slots at +500‰ where the
    /// shipped element hub gives +562.5‰, and the gap grows with every slot.</para>
    /// </summary>
    public static PowerVector AgainstCombat(
        PowerVector attacker,
        IReadOnlyList<ElementTypeId> attackerElements,
        IReadOnlyList<ElementTypeId> defenderElements) =>
        attacker.ScaleMilli(CombatFactorMilli(attackerElements, defenderElements));

    /// <summary>The same question asked of a shield, which reads its own table.</summary>
    public static PowerVector AgainstShield(
        PowerVector attacker,
        IReadOnlyList<ElementTypeId> attackerElements,
        IReadOnlyList<ElementTypeId> shieldElements) =>
        attacker.ScaleMilli(ShieldFactorMilli(attackerElements, shieldElements));

    public static long CombatFactorMilli(
        IReadOnlyList<ElementTypeId> attackers, IReadOnlyList<ElementTypeId> defenders) =>
        Compound(attackers, defenders, (a, d) => ElementRingMatrix.GetRelation(a, d) switch
        {
            ElementMatchupRelation.Strong => 1,
            ElementMatchupRelation.Weak => -1,
            _ => 0,
        });

    public static long ShieldFactorMilli(
        IReadOnlyList<ElementTypeId> attackers, IReadOnlyList<ElementTypeId> shields) =>
        Compound(attackers, shields, ShieldElementMatrix.RelationUnit);

    static long Compound(
        IReadOnlyList<ElementTypeId> attackers,
        IReadOnlyList<ElementTypeId> defenders,
        Func<ElementTypeId, ElementTypeId, int> unitOf)
    {
        var factor = PowerMath.One;
        foreach (var a in attackers)
        foreach (var d in defenders)
        {
            var unit = unitOf(a, d);
            if (unit == 0) continue;
            factor = PowerMath.CombineMilli(factor, PowerMath.One + unit * SlotShareMilli);
        }
        return factor;
    }
}

/// <summary>
/// The marginal read (E10) — <c>vector(actor WITH atom) − vector(actor WITHOUT it)</c>.
///
/// <para><b>This is how multiplicative pairs get priced correctly.</b> The difference captures
/// whatever multiplies, by construction. Stored atom power stays context-free for budgets and
/// display, where approximately right is fine; the balance sweep and any AI read marginal, where
/// exactly right matters.</para>
///
/// <para>The gap between the two reads is itself a deliverable: it is the list of shapes the cost
/// function misprices, which is how the formula learns where it is wrong.</para>
/// </summary>
public static class MarginalRead
{
    public static PowerVector Of(
        IReadOnlyList<AtomRow> actorAtoms, AtomRow candidate, PowerTables? tables = null)
    {
        var without = ActorPowerCache.Compose(actorAtoms, tables);
        var with = ActorPowerCache.Compose(actorAtoms.Append(candidate).ToList(), tables);
        return with - without;
    }

    /// <summary>
    /// How far the marginal read sits from the stored context-free price — the gap E9 knowingly
    /// leaves and this read closes.
    /// </summary>
    public static PowerVector GapAgainstStored(
        IReadOnlyList<AtomRow> actorAtoms, AtomRow candidate, PowerTables? tables = null) =>
        Of(actorAtoms, candidate, tables) - CostFunction.Price(candidate, tables).Power;
}
