using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Power;

namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// ⛔ <b>Refused by name, never guessed and never clamped.</b> Every rule this module enforces is a
/// content or tuning defect — a word whose container cannot build, a floor of zero, a rarity ladder
/// with no step that buys anything — and each one is raised with the id or the rung it names.
/// A default here would price a word nobody wrote against a route nobody can walk.
/// </summary>
public sealed class ComboPricingRejection : Exception
{
    public ComboPricingRejection(string message) : base(message) { }
}

/// <summary>
/// The souls coefficients a failing cell's floor route can be moved by (spec-combo-budget §1 "Which
/// lever", R20). <c>Bore</c> and <c>Imbue</c> move only where those legs are actually ON the route —
/// the cheap-rung cells that need neither have nothing there to move — and <c>ForgeGem</c> covers the
/// gem legs, which is what R20 makes a legitimate lever rather than a dead end.
/// </summary>
public enum ComboPricingLever
{
    Bore = 0,
    Imbue,
    ForgeGem,
}

/// <summary>
/// One priced line of a cell's floor route: what it is, how many units of it, its TOTAL in souls, the
/// rung it was resolved at (a reading — `gem` legs carry the ingredient's own tier − 1), and which
/// souls coefficient can move it. Souls is the leg's total rather than its unit price, so the legs sum
/// to <c>PriceFloorSouls</c> exactly and a report can print the route without re-multiplying anything.
///
/// <para><see cref="Lever"/> is <c>null</c> for a leg no published coefficient moves — today only a gem
/// priced through the UPCYCLE chain rather than by <c>forge-gem</c> (<see cref="ComboPricing.LeversOf"/>
/// says so by name instead of guessing at the price's source).</para>
/// </summary>
public readonly record struct ComboPriceLeg(
    string Name, long Quantity, long Souls, int RungIndex, ComboPricingLever? Lever);

/// <summary>
/// One combination to price (spec-combo-budget §1). <see cref="Tier"/> is the granted tier under the
/// revision being measured — the caller owns the ladder: rung k of the tier ladder is the tier it
/// passes, and the attuned variant passes the rung's tier PLUS the strain-splice tuning's own
/// <c>attunedTierBonus</c> while setting <see cref="Attuned"/> (which is what buys the imbues).
/// </summary>
/// <param name="IngredientTiers">
/// The insert tier each of the combination's ingredients must meet under the revision being measured
/// (today: <c>StrainSpliceTuning.MinTierPlan</c>). Four entries for a Strain or Splice; an EMPTY plan
/// is what makes a floor of zero reachable, and the floor check refuses it by name.
/// </param>
public sealed record ComboPricingRequest(
    string ComboId,
    IReadOnlyList<string> Grants,
    int Tier,
    IReadOnlyList<int> IngredientTiers,
    string? HostRole = null,
    bool Attuned = false);

/// <summary>Everything the measurement reads, injected — Core reads no file (tunables-ssot §7.2).</summary>
public sealed record ComboPricingInputs(
    SocketTuning Sockets,
    IReadOnlyList<RarityRow> RarityLadder,
    MaterialRecipeCatalog Materials,
    ComboContainerBuild.ComboContainerLookups Lookups,
    long MaxRatioToRarityRouteMilli,
    PowerTables? Tables = null);

/// <summary>
/// The rarity route's own step: what ONE rung of base buys (<see cref="DeltaPower"/>) per
/// <see cref="ElevateSouls"/> souls of <c>elevate</c> at the rung being left. A step whose
/// <see cref="DeltaPower"/> is not positive is <see cref="Excluded"/> and says so —
/// <see cref="Reason"/> is the name the report prints.
/// </summary>
public readonly record struct ComboReferenceStep(
    int FromRungIndex,
    string FromRungId,
    int ToRungIndex,
    string ToRungId,
    long DeltaPower,
    long ElevateSouls,
    bool Excluded,
    string Reason);

/// <summary>
/// One priced cell. <see cref="Power"/> and <see cref="PriceFloorSouls"/> are the two sides;
/// <see cref="Passes"/> is the cross-multiplied verdict (see <see cref="ComboPricing.Passes"/>), and
/// <see cref="RatioMilli"/> / <see cref="ReferenceMilli"/> are the PRINTED readings — the quotient
/// forms, deliberately not what the verdict is computed from.
/// </summary>
public sealed record ComboPricingCell(
    string ComboId,
    int Tier,
    bool Attuned,
    long Power,
    long PriceFloorSouls,
    IReadOnlyList<ComboPriceLeg> Legs,
    int FloorRungIndex,
    ComboReferenceStep Reference,
    bool Passes)
{
    /// <summary>Power per 1000 souls — a READING, printed beside the verdict, never the verdict.</summary>
    public long RatioMilli =>
        PriceFloorSouls <= 0 ? 0 : checked(Power * 1000L) / PriceFloorSouls;

    /// <summary>The chosen step's own reference, in the same unit — a READING.</summary>
    public long ReferenceMilli =>
        Reference.ElevateSouls <= 0 ? 0 : checked(Reference.DeltaPower * 1000L) / Reference.ElevateSouls;
}

/// <summary>The report: every cell, the chosen reference step, and every step including the excluded
/// ones (which are printed by name, never dropped).</summary>
public sealed record ComboPricingReport(
    IReadOnlyList<ComboPricingCell> Cells,
    ComboReferenceStep Reference,
    IReadOnlyList<ComboReferenceStep> Steps)
{
    public bool Passes => FailingCells.Count == 0;

    public IReadOnlyList<ComboPricingCell> FailingCells =>
        Cells.Where(c => !c.Passes).ToList();
}

/// <summary>The souls coefficient of one lever: today's value and the derived one (R20). Equal when
/// the lever does not need to move.</summary>
public sealed record ComboLeverCoefficient(
    ComboPricingLever Lever, long CurrentCoefficient, long DerivedCoefficient)
{
    public bool Moved => DerivedCoefficient > CurrentCoefficient;
}

/// <summary>
/// What one failing cell needs. <see cref="Levers"/> is every coefficient that can move THIS cell's
/// floor route (empty means no lever can, and the id is listed instead of guessed at);
/// <see cref="SmallestLever"/> + <see cref="RequiredCoefficient"/> are the cheapest of them — the one
/// the report names for the cell.
/// </summary>
public sealed record ComboPricingCellFix(
    string ComboId,
    IReadOnlyList<ComboPricingLever> Levers,
    ComboPricingLever? SmallestLever,
    long RequiredCoefficient);

/// <summary>
/// The derived fix for a whole report: one row per lever (the smallest coefficient that makes every
/// cell on that lever's route pass, computed against the other legs at their CURRENT values — so the
/// three together are sufficient, never under-stated), one row per failing cell, and the ids no lever
/// can fix.
/// </summary>
public sealed record ComboPricingDerivation(
    IReadOnlyList<ComboLeverCoefficient> Levers,
    IReadOnlyList<ComboPricingCellFix> Cells,
    IReadOnlyList<string> UnfixableCellIds)
{
    public bool AnyLeverMoved => Levers.Any(l => l.Moved);
}

/// <summary>
/// strain-splice-host SSH6.2 (`combo-budget` §1, R12/R20): what a combination BUYS against what it
/// COSTS, with no second curve anywhere — every term is an existing one:
///
/// <list type="bullet">
/// <item>power — <see cref="ActorPowerCache.Compose"/> over the atoms
/// <see cref="ComboContainerBuild.TryBuild"/> mints for <c>(comboId, grants, tier)</c>;</item>
/// <item>price — <see cref="MaterialRecipeCatalog.Resolve"/>, the one resolver, read for its SOULS
/// leg (the unit of price: no exchange rate between material classes exists, so souls is the common
/// one and every other leg is a reading the owner sees);</item>
/// <item>the rarity route — <see cref="RarityPowerCeilings.PriceReferenceSlate"/> (the rung's own
/// reference slate, priced through the same `Compose`) minus the rung below, over the rung's own
/// <c>elevate</c>;</item>
/// <item>geometry — <see cref="SocketLimits.SocketCircuitSize"/> and the tuning's own
/// <c>rarityGrant</c> windows.</item>
/// </list>
///
/// <para>⭐ <b>The comparison is cross-multiplied, never two truncated quotients.</b> Computing the
/// ratio and the reference first rounds twice, and a truncated ratio can pass a cell that fails by
/// less than one per-mille — <c>Passes</c> is therefore a bare inequality between two widened
/// products, and the quotients are only ever printed.</para>
///
/// <para>⚠ <b>Worst case over the craft route, on both sides.</b> The floor is the cheapest crafted
/// way to wear the word (the best-rolled chassis at the cheapest admitting rung), and the rarity side
/// is priced by <c>elevate</c>, not by a lucky drop. Drops are free on both sides and are excluded
/// symmetrically — this bounds the CRAFT route and says so rather than claiming to bound a drop.</para>
/// </summary>
public static class ComboPricing
{
    /// <summary>
    /// The verdict, cross-multiplied in <c>long</c> and <c>checked</c>:
    /// <c>power · elevate(ρ*) · 1000 ≤ ΔPower(ρ*) · priceFloor · maxRatioToRarityRouteMilli</c>.
    ///
    /// <para>Widen before multiplying and throw on overflow, never wrap: all four magnitudes grow with
    /// Θ, so the products are the one place in this module that can outgrow their type.</para>
    /// </summary>
    public static bool Passes(
        long power, long priceFloorSouls, long referenceDeltaPower, long referenceElevateSouls,
        long maxRatioToRarityRouteMilli)
    {
        if (priceFloorSouls <= 0)
            throw new ComboPricingRejection(
                $"a price floor of {priceFloorSouls} souls is not divisible by — four gems always cost " +
                "something, so a zero floor means the plan or the price table is missing, not that the " +
                "word is free");

        checked
        {
            var left = power * referenceElevateSouls * 1000L;
            var right = referenceDeltaPower * priceFloorSouls * maxRatioToRarityRouteMilli;
            return left <= right;
        }
    }

    /// <summary>The floor is never 0 — refused by NAME, then returned unchanged.</summary>
    public static long RequirePositiveFloor(string comboId, long priceFloorSouls) =>
        priceFloorSouls > 0
            ? priceFloorSouls
            : throw new ComboPricingRejection(
                $"'{comboId}' resolves to a price floor of 0 souls: no bore, no imbue and no gem leg " +
                "costs anything, so it could never be compared against the rarity route");

    /// <summary>
    /// Every step of the rarity ladder — one per adjacent pair, so the TOP rung contributes none
    /// (there is no <c>ρ+1</c> to buy). Steps that buy nothing are marked <see cref="Excluded"/> with
    /// the reason, never dropped: an early rung with no affix slots prices its slate at 0
    /// (<see cref="RarityPowerCeilings.PriceReferenceSlate"/> returns 0 for a rung with no slots), and
    /// a step that buys nothing is an infinitely dear rarity route — letting it into the minimum would
    /// demand every word be free.
    /// </summary>
    public static IReadOnlyList<ComboReferenceStep> Steps(
        IReadOnlyList<RarityRow> rarityLadder, MaterialRecipeCatalog materials, PowerTables? tables = null)
    {
        if (rarityLadder is null) throw new ArgumentNullException(nameof(rarityLadder));
        if (materials is null) throw new ArgumentNullException(nameof(materials));

        var ordered = rarityLadder
            .OrderBy(row => RungIndexOrRefuse(row.RarityId))
            .ToList();

        var steps = new List<ComboReferenceStep>(Math.Max(0, ordered.Count - 1));
        for (var i = 0; i + 1 < ordered.Count; i++)
        {
            var from = ordered[i];
            var to = ordered[i + 1];
            var delta = checked(RarityPowerCeilings.PriceReferenceSlate(
                                   RarityPowerCeilings.WindowOf(to), tables)
                               - RarityPowerCeilings.PriceReferenceSlate(
                                   RarityPowerCeilings.WindowOf(from), tables));
            var elevate = SoulsLeg(materials, CraftOperation.Elevate, RungContext(i));

            var (excluded, reason) = delta <= 0
                ? (true, $"the step '{from.RarityId}' -> '{to.RarityId}' buys {delta} power")
                : elevate <= 0
                    ? (true, $"the step '{from.RarityId}' -> '{to.RarityId}' costs 0 souls of elevate")
                    : (false, "");

            steps.Add(new ComboReferenceStep(
                FromRungIndex: i, FromRungId: from.RarityId,
                ToRungIndex: i + 1, ToRungId: to.RarityId,
                DeltaPower: delta, ElevateSouls: elevate,
                Excluded: excluded, Reason: reason));
        }

        return steps;
    }

    /// <summary>
    /// The step that attains the MINIMUM reference — the step the bound is measured against. Compared
    /// by cross-multiplication (<c>Δa/ela &lt; Δb/elb ⇔ Δa·elb &lt; Δb·ela</c>), never by dividing
    /// first, for the same reason <see cref="Passes"/> is. Every step excluded is a refusal: there is
    /// no rarity route to compare against, which is a content defect, not a pass.
    /// </summary>
    public static ComboReferenceStep ChosenStep(IReadOnlyList<ComboReferenceStep> steps)
    {
        if (steps is null) throw new ArgumentNullException(nameof(steps));

        var candidates = steps.Where(s => !s.Excluded).ToList();
        if (candidates.Count == 0)
            throw new ComboPricingRejection(
                "no rarity step buys any power (" +
                string.Join("; ", steps.Select(s => s.Reason).Where(r => r.Length > 0)) +
                "), so there is no rarity route to price a combination against");

        var best = candidates[0];
        foreach (var step in candidates.Skip(1))
            if (BuysMorePerSoul(step, best)) best = step;
        return best;

        static bool BuysMorePerSoul(ComboReferenceStep a, ComboReferenceStep b)
        {
            checked
            {
                return a.DeltaPower * b.ElevateSouls < b.DeltaPower * a.ElevateSouls;
            }
        }
    }

    /// <summary>Price every request against one chosen reference step, computed once.</summary>
    public static ComboPricingReport Measure(
        IReadOnlyList<ComboPricingRequest> requests, ComboPricingInputs inputs)
    {
        if (requests is null) throw new ArgumentNullException(nameof(requests));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var steps = Steps(inputs.RarityLadder, inputs.Materials, inputs.Tables);
        var reference = ChosenStep(steps);
        var cells = requests.Select(r => MeasureCell(r, inputs, reference)).ToList();
        return new ComboPricingReport(cells, reference, steps);
    }

    /// <summary>Price one cell: power from the container, floor over the admitting rungs with the
    /// best-rolled chassis, and the cross-multiplied verdict against the chosen reference step.</summary>
    public static ComboPricingCell MeasureCell(
        ComboPricingRequest request, ComboPricingInputs inputs, ComboReferenceStep reference)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        RequireHostAdmits(request, inputs.Sockets);
        var power = Power(request, inputs);
        var (floor, legs, floorRung) = Floor(request, inputs);
        RequirePositiveFloor(request.ComboId, floor);

        var passes = Passes(power, floor, reference.DeltaPower, reference.ElevateSouls,
                            inputs.MaxRatioToRarityRouteMilli);

        return new ComboPricingCell(
            ComboId: request.ComboId, Tier: request.Tier, Attuned: request.Attuned,
            Power: power, PriceFloorSouls: floor, Legs: legs, FloorRungIndex: floorRung,
            Reference: reference, Passes: passes);
    }

    // ── the derived levers (SSH6.3, spec §1 "Which lever", R20) ───────────────────────────────────

    /// <summary>
    /// The coefficients that can move a cell: exactly the ones its own floor route uses, in a stable
    /// order. A cell whose route carries no bore and no imbue has ONE lever — the gem leg's
    /// `forge-gem` souls coefficient — which is R20's whole point: such a cell is derivable, not a dead
    /// end.
    /// </summary>
    public static IReadOnlyList<ComboPricingLever> LeversOf(ComboPricingCell cell)
    {
        if (cell is null) throw new ArgumentNullException(nameof(cell));
        return Enum.GetValues<ComboPricingLever>()
            .Where(lever => cell.Legs.Any(l => l.Lever == lever))
            .ToList();
    }

    /// <summary>
    /// The smallest souls coefficient per lever that makes every failing cell on that lever's route
    /// pass — DERIVED, so the publisher copies a computed number rather than a guess (R20). Each cell's
    /// requirement is computed with the other legs at their current values; because raising a
    /// coefficient only raises a floor, applying all three is at least as strong as any one of them, so
    /// the triple is sufficient. A cell no lever can move is returned by id, never silently dropped.
    /// </summary>
    public static ComboPricingDerivation Derive(ComboPricingReport report, ComboPricingInputs inputs)
    {
        if (report is null) throw new ArgumentNullException(nameof(report));
        if (inputs is null) throw new ArgumentNullException(nameof(inputs));

        var current = new Dictionary<ComboPricingLever, long>();
        foreach (var lever in Enum.GetValues<ComboPricingLever>())
            current[lever] = CurrentCoefficient(inputs.Materials.Tuning, lever);

        var derived = new Dictionary<ComboPricingLever, long>(current);
        var fixes = new List<ComboPricingCellFix>();
        var unfixable = new List<string>();

        foreach (var cell in report.FailingCells)
        {
            var candidates = new List<(ComboPricingLever Lever, long Coefficient)>();
            foreach (var lever in LeversOf(cell))
            {
                var legTotal = cell.Legs.Where(l => l.Lever == lever).Sum(l => l.Souls);
                if (legTotal <= 0) continue;
                candidates.Add((lever, RequiredCoefficient(cell, inputs, current[lever], legTotal)));
            }

            if (candidates.Count == 0)
            {
                unfixable.Add(cell.ComboId);
                fixes.Add(new ComboPricingCellFix(cell.ComboId, Array.Empty<ComboPricingLever>(), null, 0));
                continue;
            }

            var cheapest = candidates
                .OrderBy(c => c.Coefficient).ThenBy(c => c.Lever)
                .First();
            fixes.Add(new ComboPricingCellFix(
                cell.ComboId, candidates.Select(c => c.Lever).ToList(),
                cheapest.Lever, cheapest.Coefficient));

            foreach (var candidate in candidates)
                derived[candidate.Lever] = Math.Max(derived[candidate.Lever], candidate.Coefficient);
        }

        var levers = Enum.GetValues<ComboPricingLever>()
            .Select(l => new ComboLeverCoefficient(l, current[l], derived[l]))
            .ToList();
        return new ComboPricingDerivation(levers, fixes, unfixable);
    }

    /// <summary>
    /// The smallest coefficient for ONE lever on ONE cell. The cell needs a floor of
    /// <c>ceil(power · elevate · 1000 / (ΔPower · maxRatio))</c> souls; the lever's legs contribute
    /// <paramref name="legTotal"/> of the floor and the rest cannot move, so the factor is their ratio —
    /// per-mille, rounded UP, which is the smallest coefficient that is enough.
    /// </summary>
    static long RequiredCoefficient(
        ComboPricingCell cell, ComboPricingInputs inputs, long currentCoefficient, long legTotal)
    {
        if (inputs.MaxRatioToRarityRouteMilli <= 0)
            throw new ComboPricingRejection(
                $"maxRatioToRarityRouteMilli is {inputs.MaxRatioToRarityRouteMilli}; a non-positive " +
                "bound makes every cell unpassable, so a derivation against it would divide by zero");

        checked
        {
            var need = CeilDiv(cell.Power * cell.Reference.ElevateSouls * 1000L,
                               cell.Reference.DeltaPower * inputs.MaxRatioToRarityRouteMilli);
            var cannotMove = cell.PriceFloorSouls - legTotal;
            if (cannotMove >= need) return currentCoefficient;   // the other legs already carry it

            // The lever's souls scale LINEARLY with its coefficient, so the smallest sufficient
            // coefficient is ONE ceiling of the exact ratio. Rounding through an intermediate
            // per-mille factor first (`ceil(c * ceil(n·1000/legTotal) / 1000)`) can land up to
            // ~1 + c/1000 ABOVE the true minimum for a large coefficient — which is exactly where a
            // publish sits after a dear-cell derivation.
            return CeilDiv((need - cannotMove) * currentCoefficient, legTotal);
        }
    }

    /// <summary>The lever's coefficient as published today, and the guard that makes the derivation's
    /// linear scaling true: a lever whose souls leg is not `rung`-linear does not scale with its
    /// coefficient, so deriving one would be a guess.</summary>
    static long CurrentCoefficient(MaterialTuning tuning, ComboPricingLever lever)
    {
        var operation = lever switch
        {
            ComboPricingLever.Bore => CraftOperation.Bore,
            ComboPricingLever.Imbue => CraftOperation.Imbue,
            ComboPricingLever.ForgeGem => CraftOperation.ForgeGem,
            _ => throw new ArgumentOutOfRangeException(nameof(lever), lever, null),
        };
        var leg = tuning.Operations[operation].Souls
            ?? throw new ComboPricingRejection(
                $"'{CraftOperations.Id(operation)}' has no souls leg in the materials tuning, so its " +
                "coefficient cannot be derived");
        if (leg.Variable != CostVariable.Rung)
            throw new ComboPricingRejection(
                $"'{CraftOperations.Id(operation)}' prices its souls on '{leg.Variable}', not on the " +
                "rung — a coefficient on a leg that does not scale with it cannot be derived linearly");
        return leg.Coefficient;
    }

    static long CeilDiv(long numerator, long denominator)
    {
        if (denominator <= 0)
            throw new ComboPricingRejection(
                $"cannot divide {numerator} by {denominator}: a non-positive denominator means the " +
                "price or the reference is missing, not that the answer is zero");
        return checked((numerator + denominator - 1) / denominator);
    }

    // ── power ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Power = the atoms of the combination's own container, priced by the one composer. A grant with
    /// no real atom at the tier is refused by name — it is the content gap
    /// <see cref="ComboContainerBuild.TryBuild"/> already names, and substituting another atom would
    /// price a word that visibly fires and silently does nothing.
    /// </summary>
    static long Power(ComboPricingRequest request, ComboPricingInputs inputs)
    {
        var container = ComboContainerBuild.TryBuild(
            request.ComboId, request.Grants, request.Tier, inputs.Lookups, out var refusal);
        if (container is null)
            throw new ComboPricingRejection(
                $"'{request.ComboId}' cannot be priced at tier {request.Tier}: {refusal}");

        var atoms = new List<AtomRow>(container.Atoms.Count);
        foreach (var row in container.Atoms)
        {
            var atom = inputs.Lookups.LookupAtom(row.AtomId);
            if (atom is null)
                throw new ComboPricingRejection(
                    $"'{request.ComboId}' grants atom '{row.AtomId}', which the catalog answered during " +
                    "the container build and no longer holds at the price — a moving catalog, not a missing row");
            atoms.Add(atom);
        }

        return ActorPowerCache.Compose(atoms, inputs.Tables).Total;
    }

    // ── the price floor ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cheapest crafted route, over the rungs that admit the word:
    /// <c>bores(ρ)·bore(ρ) + imbues·imbue(ρ) + Σ gem(minTier(i))</c>, where
    /// <c>bores(ρ) = max(0, circuitSize − rarityGrant[ρ].Max)</c> — the BEST-ROLLED chassis, so a
    /// rung whose window reaches the circuit needs no bore at all, and one whose window does not
    /// needs the difference rather than its worst roll. The minimum is taken over every rung the role
    /// admits; ties keep the lower rung, because that is the one a player reaches first.
    /// </summary>
    static (long Souls, IReadOnlyList<ComboPriceLeg> Legs, int RungIndex) Floor(
        ComboPricingRequest request, ComboPricingInputs inputs)
    {
        var circuit = SocketLimits.SocketCircuitSize;
        var (gemLegs, gemTotal) = GemFloor(request.IngredientTiers, inputs);

        var best = long.MaxValue;
        var bestRung = -1;
        IReadOnlyList<ComboPriceLeg> bestLegs = Array.Empty<ComboPriceLeg>();
        var imbues = request.Attuned ? circuit : 0;

        foreach (var row in inputs.RarityLadder.OrderBy(r => RungIndexOrRefuse(r.RarityId)))
        {
            var rungIndex = RungIndexOrRefuse(row.RarityId);
            if (!inputs.Sockets.RarityGrant.TryGetValue(row.RarityId, out var window))
                throw new ComboPricingRejection(
                    $"the rarity ladder carries rung '{row.RarityId}' and the socket tuning's " +
                    "rarityGrant table does not — one of the two is missing a row, and a floor computed " +
                    "over the difference would be silently wrong");

            var bores = Math.Max(0, circuit - window.Max);
            var legs = new List<ComboPriceLeg>();
            var total = gemTotal;

            if (bores > 0)
            {
                var souls = SoulsLeg(inputs.Materials, CraftOperation.Bore, RungContext(rungIndex));
                var leg = checked(bores * souls);
                total = checked(total + leg);
                legs.Add(new ComboPriceLeg("bore", bores, leg, rungIndex, ComboPricingLever.Bore));
            }
            if (imbues > 0)
            {
                var souls = SoulsLeg(inputs.Materials, CraftOperation.Imbue, RungContext(rungIndex));
                var leg = checked(imbues * souls);
                total = checked(total + leg);
                legs.Add(new ComboPriceLeg("imbue", imbues, leg, rungIndex, ComboPricingLever.Imbue));
            }
            legs.AddRange(gemLegs);

            if (total < best)
            {
                best = total;
                bestRung = rungIndex;
                bestLegs = legs;
            }
        }

        if (bestRung < 0)
            throw new ComboPricingRejection(
                $"'{request.ComboId}' has no rarity rung to be worn at: the rarity ladder is empty");

        return (best, bestLegs, bestRung);
    }

    /// <summary>One leg per ingredient, plus their total.</summary>
    static (IReadOnlyList<ComboPriceLeg> Legs, long Total) GemFloor(
        IReadOnlyList<int> ingredientTiers, ComboPricingInputs inputs)
    {
        var legs = new List<ComboPriceLeg>(ingredientTiers.Count);
        var total = 0L;
        foreach (var tier in ingredientTiers)
        {
            if (tier < 1)
                throw new ComboPricingRejection(
                    $"an ingredient's min tier is {tier}; the insert ladder starts at 1, so this plan " +
                    "is missing rather than cheap");
            var (souls, viaForgeGem) = GemSouls(tier, inputs);
            legs.Add(new ComboPriceLeg("gem", 1, souls, tier - 1,
                                       viaForgeGem ? ComboPricingLever.ForgeGem : null));
            total = checked(total + souls);
        }
        return (legs, total);
    }

    /// <summary>
    /// What one gem at tier <paramref name="tier"/> costs: the <c>forge-gem</c> souls leg at that
    /// OUTPUT tier, or the upcycle chain from the tier below when that is cheaper —
    /// <c>min(forge-gem(t), upcycleInputPerOutput · gem(t−1) + upcycle(t))</c>. The ratio is the SOCKET
    /// tuning's own <c>insertTiers.upcycleInputPerOutput</c> — the k→k+1 stack arithmetic the spec's
    /// own formula cites — and both legs come from the one resolver, so this chooses between two priced
    /// routes and never adds a second curve.
    /// </summary>
    static (long Souls, bool ViaForgeGem) GemSouls(int tier, ComboPricingInputs inputs)
    {
        var forge = SoulsLeg(inputs.Materials, CraftOperation.ForgeGem, GemContext(tier));
        if (tier <= 1) return (forge, true);

        var (previous, _) = GemSouls(tier - 1, inputs);
        var chain = checked(inputs.Sockets.UpcycleInputPerOutput * previous
                            + SoulsLeg(inputs.Materials, CraftOperation.Upcycle, GemContext(tier)));
        // A tie keeps the forge-gem route: it is the one a coefficient can move, and a leg no lever can
        // move must never win a tie against one that can.
        return forge <= chain ? (forge, true) : (chain, false);
    }

    // ── the one resolver ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The souls leg of an operation, taken over every recipe the corpus authors for it — the cheapest
    /// AUTHORED band, because the floor is the cheapest crafted route rather than one hand-picked
    /// recipe. Refused by name when the operation has no recipe at all: an unpriced verb would make
    /// every cell on its route free.
    /// </summary>
    static long SoulsLeg(MaterialRecipeCatalog materials, CraftOperation operation, RecipeContext ctx)
    {
        var recipes = materials.Recipes.Values
            .Where(r => r.Operation == operation)
            .OrderBy(r => r.RecipeId, StringComparer.Ordinal)
            .ToList();
        if (recipes.Count == 0)
            throw new ComboPricingRejection(
                $"the recipe corpus authors no '{CraftOperations.Id(operation)}' recipe, so its souls " +
                "leg cannot be priced and every cell on that route would read as free");

        var cheapest = long.MaxValue;
        foreach (var recipe in recipes)
        {
            var souls = materials.Resolve(recipe.RecipeId, ctx)
                .Where(l => l.Class == MaterialClass.Souls)
                .Select(l => l.Qty)
                .DefaultIfEmpty(0L)
                .Max();
            if (souls < cheapest) cheapest = souls;
        }
        return cheapest;
    }

    static RecipeContext RungContext(int rungIndex) => new(
        TargetRungIndex: rungIndex,
        TargetTier: rungIndex + 1,
        TargetItemLevel: 0,
        TargetFrame: "any",
        EnhanceLevel: 0);

    /// <summary>`forge-gem` prices on the gem's OUTPUT tier, so the rung INDEX it prices at is the
    /// tier below — the coefficient is rung-linear (`coefficient · (rungIndex + 1)`).</summary>
    static RecipeContext GemContext(int tier) => RungContext(tier - 1);

    // ── admission ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The word needs a chassis that can hold its whole circuit. A pinned <c>hostRole</c> must reach
    /// the ingredient count (the R11 host-set rule, read from the tuning, never from a role list); an
    /// unpinned word is admitted when ANY role does. Neither is a price question, so this refuses
    /// rather than skipping the cell.
    /// </summary>
    static void RequireHostAdmits(ComboPricingRequest request, SocketTuning sockets)
    {
        var wanted = sockets.StrainSpliceIngredientCount;

        if (request.HostRole is null)
        {
            if (sockets.SocketCeiling.Values.Any(ceiling => ceiling >= wanted)) return;
            throw new ComboPricingRejection(
                $"no role's ceiling reaches the combination width {wanted}, so '{request.ComboId}' " +
                "has no chassis that could ever wear it");
        }

        if (!ItemRoles.TryParse(request.HostRole, out var role))
            throw new ComboPricingRejection(
                $"'{request.ComboId}' pins host role '{request.HostRole}', which is not an id of the " +
                "closed role vocabulary");

        var ceiling = sockets.CeilingFor(role);
        if (ceiling < wanted)
            throw new ComboPricingRejection(
                $"'{request.ComboId}' pins host role '{request.HostRole}' (ceiling {ceiling}), which " +
                $"cannot hold its {wanted} ingredients");
    }

    static int RungIndexOrRefuse(string rarityId)
    {
        var index = RarityLadder.RungIndexOf(rarityId);
        if (index < 0)
            throw new ComboPricingRejection(
                $"'{rarityId}' is not a rung of the item rarity ladder " +
                $"({string.Join(", ", RarityLadder.RungIds)})");
        return index;
    }
}
