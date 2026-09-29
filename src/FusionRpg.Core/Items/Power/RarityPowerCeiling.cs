using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items.Uniques;

namespace FusionRpg.Core.Items.Power;

/// <summary>
/// One rung's absolute power ceiling, and everything a caller needs to quote it honestly.
///
/// <para><b>The <c>provisional</c> flag rides in the result object, never in a comment</b>
/// (spec-rarity-bands.md's own rule for this column, copying module 9's
/// <c>flat_coefficients_are_reported_not_hidden</c> pattern). <see cref="Render"/> is the only
/// sanctioned way to print the absolute figure, because it cannot print it without the flag —
/// spec-rarity-bands.md's illegal-uses row is *"quoting the absolute AE figure without the
/// `provisional` flag"*, and a method that physically cannot do that is stronger than a convention.</para>
/// </summary>
/// <param name="CeilingPoints"><c>null</c> is <b>unpriced</b>, never zero: a rung with no seeded
/// share and a rung whose share really is 0‰ are different answers, and collapsing them is the same
/// class of defect <c>CoefficientTable.Find</c>'s own doc warns about one layer down.</param>
/// <param name="PinAe">The price of one reference top-rung slate, shared by every rung in the table —
/// the single coefficient-DEPENDENT term in the formula.</param>
public readonly record struct RarityCeilingRead(
    string RarityId,
    long? CeilingPoints,
    int? LadderShareMilli,
    long PinAe,
    bool Provisional,
    string? UnpricedReason)
{
    public bool Unpriced => CeilingPoints is null;

    public string Render() =>
        CeilingPoints is { } c
            ? $"{c}{(Provisional ? " (provisional)" : "")}"
            : $"unpriced — {UnpricedReason}";
}

/// <summary>
/// ⭐ Module 9's <c>ceilingFor</c> — the reader module 7 seeded a column for and explicitly assigned
/// here (<c>spec-rarity-bands.md</c>'s Users row: *"9 (`item-power-reads` — `ceilingFor`)"*, and its
/// Testing Strategy's *"asserted at the consumer, not claimed here"*).
///
/// <code>
/// power_ceiling(rung) = pinAE × ladderShareMilli(rung) / 1000        (spec-rarity-bands.md:403-412)
/// </code>
///
/// <list type="bullet">
/// <item><description><b><c>ladderShareMilli</c></b> — the seeded <c>rarity_budget.power_ceiling</c>
/// row, ‰ of top. Coefficient-INDEPENDENT: it is §7.3's measured hp ladder, an hp measurement rather
/// than a price. Module 7 seeds it from <c>gk-core/data/tuning/item-rarity.v1.json</c>, so a balance pass
/// moves the whole column with a file save.</description></item>
/// <item><description><b><c>pinAE</c></b> — one reference top-rung slate priced through
/// <see cref="ActorPowerCache.Compose"/>, <b>the same function <see cref="ContentValidation.Budget"/>
/// prices a real container with</b>. That identity is the whole safety argument: a uniform
/// coefficient rescale moves an atom's price and <c>pinAE</c> by the same factor, so every SHARE read
/// off this column is invariant under X6 and only the absolute threshold moves.</description></item>
/// </list>
///
/// <para><b>This is a content-lint threshold, not a progression ceiling.</b> It bounds what an
/// AUTHORED container of a given rung may spend and produces a finding naming the offender
/// (<c>ContentValidation</c>'s own rule: *"a content test that fails naming the offender — and
/// <b>never</b> a generation input"*). Nothing a player earns is measured against it, so AGENTS.md's
/// no-hard-ceilings rule is not in play; the number itself is a tunable ‰ column either way.</para>
///
/// <para><b>No second curve and no second unit.</b> The reference slate's affix count, tier window and
/// magnitude all come from the seeded ladder and from <see cref="UniqueBudget"/>/
/// <see cref="RarityOverlapSimulator"/>'s existing AE definition — one rolled affix at the middle of
/// the rung's tier window. A second magnitude table here is how the item budget and the rarity ladder
/// start measuring different things (the reason <c>RarityOverlapSimulator.TierBand</c> was made public
/// in the first place).</para>
/// </summary>
public sealed class RarityPowerCeilings
{
    /// <summary>The <c>rarity_budget</c> key this reader consumes — registered to
    /// <c>"item-power-reads (9)"</c> in <see cref="RarityBudgetKeys"/>, named here rather than
    /// re-typed at each call site.</summary>
    public const string BudgetKey = "power_ceiling";

    /// <summary>
    /// The AE unit's own kind and channel. <b>Structural, not tunable</b>: AE is DEFINED as one rolled
    /// <c>vitality</c>/<c>maxHp</c> affix at the middle of the window (<c>RarityOverlapSimulator</c>'s
    /// "single channel family on purpose", <c>ssot-sets.md</c> §3.5, <c>ssot-uniques.md</c> §3.7).
    /// Changing either does not rebalance the game — it redefines the unit every AE figure in the
    /// program is already denominated in, so it is not a balance-pass dial.
    /// </summary>
    const string AeKindId = "stat.modify";
    const string AeChannel = "maxHp";

    readonly IReadOnlyDictionary<string, RarityCeilingRead> _byRarity;

    RarityPowerCeilings(
        IReadOnlyDictionary<string, RarityCeilingRead> byRarity, string? topRungId, long pinAe,
        bool provisional, string? unavailableReason)
    {
        _byRarity = byRarity;
        TopRungId = topRungId;
        PinAe = pinAe;
        Provisional = provisional;
        UnavailableReason = unavailableReason;
    }

    /// <summary>The rung <c>pinAE</c> was priced on — the highest seeded ordinal, which
    /// <c>spec-rarity-bands.md</c> names as <c>almanac</c>. Read from the ladder rather than written
    /// down, so inserting a rung above it (the registry's ordinals are pre-spaced by 10 for exactly
    /// that) re-pins automatically instead of silently pricing against the old top.</summary>
    public string? TopRungId { get; }

    /// <summary>The price of one reference top-rung slate. 0 when the ladder could not be priced —
    /// see <see cref="UnavailableReason"/>; never quote it without <see cref="Provisional"/>.</summary>
    public long PinAe { get; }

    /// <summary>
    /// True while every coefficient in the table <c>pinAE</c> was priced under is flat at
    /// <c>CoeffMilli = 1000</c> — i.e. X6 (<c>E44 power-sweep</c>) has not landed. <b>Measured off the
    /// live table, not asserted</b>: when a fitted table ships this flips on its own, which is what
    /// spec-rarity-bands.md's *"X6 landing: re-price <c>pinAE</c> and clear the flag"* asks for, with
    /// nobody having to remember to clear it.
    /// </summary>
    public bool Provisional { get; }

    /// <summary>Why no rung can be priced at all, or <c>null</c> when the table is live.</summary>
    public string? UnavailableReason { get; }

    /// <summary><c>pinAE</c> printed the only sanctioned way — it cannot leave this class without its
    /// <c>provisional</c> flag, which is spec-rarity-bands.md's illegal-uses row made mechanical.</summary>
    public string RenderPinAe() =>
        UnavailableReason is { } why
            ? $"unpriced — {why}"
            : $"{PinAe} on '{TopRungId}'{(Provisional ? " (provisional)" : "")}";

    /// <summary>
    /// Build the table from the seeded ladder rows and the seeded ‰ column.
    /// </summary>
    /// <param name="ladder">The <c>rarity</c> table's own rows — <c>RpgStore.ListRarities()</c>, or the
    /// batch a content import is about to write.</param>
    /// <param name="ladderShareMilliOf">The seeded <c>rarity_budget.power_ceiling</c> for a rung, or
    /// <c>null</c> when that rung carries no row. Supplied as a delegate for the same reason
    /// <see cref="ContentValidation.Budget"/> takes one: SQL lives only in <c>FusionRpg.Data</c>.</param>
    public static RarityPowerCeilings Build(
        IReadOnlyList<RarityRow> ladder,
        Func<string, int?> ladderShareMilliOf,
        PowerTables? tables = null)
    {
        if (ladder is null) throw new ArgumentNullException(nameof(ladder));
        if (ladderShareMilliOf is null) throw new ArgumentNullException(nameof(ladderShareMilliOf));

        var t = tables ?? PowerTables.Current;
        var provisional = t.Coefficients.Count > 0 && t.Coefficients.All(c => c.CoeffMilli == PowerMath.One);
        var empty = new Dictionary<string, RarityCeilingRead>(StringComparer.Ordinal);

        if (ladder.Count == 0)
            return new RarityPowerCeilings(empty, null, 0, provisional,
                "the rarity ladder has no seeded rows, so there is no top rung to price pinAE on");

        var top = ladder.OrderByDescending(r => r.Ordinal).ThenBy(r => r.RarityId, StringComparer.Ordinal).First();
        var topWindow = WindowOf(top);
        if (!IsPriceableWindow(topWindow))
            return new RarityPowerCeilings(empty, top.RarityId, 0, provisional,
                $"top rung '{top.RarityId}' has tier window {top.MinTier}-{top.MaxTier} and "
                + $"{topWindow.AffixCount} affix(es); pinAE needs a window inside "
                + $"t1-t{RarityOverlapSimulator.TierCount} carrying at least one affix");

        var pinAe = PriceReferenceSlate(topWindow, t);
        var reads = new Dictionary<string, RarityCeilingRead>(StringComparer.Ordinal);
        foreach (var row in ladder)
        {
            if (ladderShareMilliOf(row.RarityId) is not { } shareMilli)
            {
                reads[row.RarityId] = new RarityCeilingRead(row.RarityId, null, null, pinAe, provisional,
                    $"rung '{row.RarityId}' carries no seeded rarity_budget.{BudgetKey} row");
                continue;
            }

            // Widen before multiplying (pinAe is already long), divide by 1000 LAST and exactly once,
            // and let an overflow throw rather than wrap (AGENTS.md's numeric rules). DivRound is the
            // Power namespace's own single rounding rule, so a ceiling does not depend on the order
            // two equal factors happened to be applied in.
            long ceiling;
            checked { ceiling = PowerMath.DivRound(pinAe * shareMilli, PowerMath.One); }
            reads[row.RarityId] = new RarityCeilingRead(row.RarityId, ceiling, shareMilli, pinAe, provisional, null);
        }

        return new RarityPowerCeilings(reads, top.RarityId, pinAe, provisional, null);
    }

    /// <summary>
    /// <c>pinAE</c>: one reference slate for a rung, priced through the consumers' own cost function.
    ///
    /// <para>The slate is the rung's own count-band floor worth of affixes (<c>prefix_rolls +
    /// suffix_rolls</c>), each one AE — the midpoint magnitude of the middle tier of the rung's
    /// authored window, via <see cref="UniqueBudget.ReferenceMagnitude"/>. <see cref="ActorPowerCache.Compose"/>
    /// aggregates same-channel atoms before pricing, so the slate prices as ONE actor exactly the way a
    /// real container does in <see cref="ContentValidation.Budget"/>; that identity is what makes the
    /// ratio argument literally true rather than merely plausible.</para>
    ///
    /// <para>⚠ The count is the seeded FLOOR, because the shipped schema has no <c>_max</c> column
    /// (module 7's recorded ask-first). A floor makes <c>pinAE</c> — and therefore every ceiling —
    /// <b>smaller</b>, so the budget check errs toward over-reporting, never toward a silent pass.</para>
    /// </summary>
    public static long PriceReferenceSlate(RarityRungWindow rung, PowerTables? tables = null)
    {
        if (rung.AffixCount <= 0) return 0;

        var magnitude = UniqueBudget.ReferenceMagnitude(rung);
        var slate = new AtomRow[rung.AffixCount];
        for (var i = 0; i < slate.Length; i++) slate[i] = ReferenceAffix(rung.RarityId, i, magnitude);

        return ActorPowerCache.Compose(slate, tables).Total;
    }

    /// <summary>The rung's window in the shape the AE reckoner and the overlap harness already use —
    /// one conversion, so no caller re-derives "affix count is prefix + suffix".</summary>
    public static RarityRungWindow WindowOf(RarityRow row) =>
        new(row.RarityId, row.MinTier, row.MaxTier, row.PrefixRolls + row.SuffixRolls);

    /// <summary>
    /// The ceiling <see cref="ContentValidation.Budget"/> takes, as its own <c>Func&lt;string,int?&gt;</c>.
    ///
    /// <para><b>The narrowing throws, it does not clamp.</b> The rarity-keyed overload's delegate is
    /// <c>int?</c> (its <c>spent</c> side is <see cref="PowerVector.Total"/>, an <c>int</c>) while the
    /// rung-keyed sibling's is <c>long?</c>. A silent <c>(int)</c> cast on a magnitude is a cap wearing
    /// a cast's clothes, so this one is <c>checked</c>: a ceiling past <see cref="int.MaxValue"/> is an
    /// arithmetic fact that must fail loudly, not a budget that quietly wraps negative.</para>
    /// </summary>
    public int? CeilingFor(string rarityId) =>
        Of(rarityId).CeilingPoints is { } c ? checked((int)c) : null;

    /// <summary>The full read for one rung — ceiling, share, <c>pinAE</c> and the provisional flag.
    /// A rung this table has never heard of is unpriced with that as its reason, never 0.</summary>
    public RarityCeilingRead Of(string rarityId) =>
        _byRarity.TryGetValue(rarityId, out var read)
            ? read
            : new RarityCeilingRead(rarityId, null, null, PinAe, Provisional,
                UnavailableReason ?? $"'{rarityId}' is not a rung of the seeded rarity ladder");

    /// <summary>Every rung the ladder carries, in seeded order — for a report that must say how much
    /// of the ladder is actually priced rather than only that it found nothing.</summary>
    public IReadOnlyCollection<RarityCeilingRead> All => (IReadOnlyCollection<RarityCeilingRead>)_byRarity.Values;

    /// <summary>How many rungs resolve to a real ceiling. A pass that priced nothing and a pass that
    /// found nothing look identical from a green tick — <see cref="ContentReport.Evaluated"/>'s own
    /// reasoning, applied to the ceiling source itself.</summary>
    public int PricedRungs => _byRarity.Values.Count(r => !r.Unpriced);

    static bool IsPriceableWindow(RarityRungWindow rung) =>
        rung.AffixCount > 0
        && rung.MinTier >= 1
        && rung.MaxTier <= RarityOverlapSimulator.TierCount
        && rung.MinTier <= rung.MaxTier;

    /// <summary>One AE, as the atom row the cost function actually prices. Fixed magnitude (min == max),
    /// so <c>CostFunction.MeanMagnitude</c> reads exactly the AE figure and nothing rolls.</summary>
    static AtomRow ReferenceAffix(string rarityId, int index, long magnitude) => new()
    {
        AtomId = $"atom.reference-slate.{rarityId}.{index}",
        KindId = AeKindId,
        FamilyId = "atom.reference-slate",
        Variant = rarityId,
        Tier = 1,
        Name = $"reference AE {index} ({rarityId})",
        ParamsJson = "{\"channel\":\"" + AeChannel + "\",\"op\":\"flat\",\"amount\":"
                     + magnitude.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}",
    };
}
