namespace FusionRpg.Core.Items.Sockets;

/// <summary>
/// What the fill is short of, named exactly so the hint can say "needs 1 more Ember Shard" rather
/// than "needs something". strain-splice-host SSH1.1 (host-gate): moved here from
/// <c>CombinationDistance.cs</c> (<c>Items.Surfaces</c>) — the record belongs beside the one matcher
/// that produces it, and <c>Surfaces</c> already depends on <c>Sockets</c>, never the reverse.
///
/// <para>⛔ <b>No tier field</b> (SSH7.8): a shortfall is a FAMILY and a count, because the tier a
/// fill must reach is the LADDER's and depends on which rung is in question — a per-family floor
/// would be a second tier source. <see cref="TierShortfall"/> carries the position/need pair the
/// bench renders for the next rung.</para>
/// </summary>
public readonly record struct MissingIngredient(string FamilyId, int Quantity);

/// <summary>
/// strain-splice-host SSH7.2 (spec-tier-ladder §2): the next rung is out of reach at this POSITION.
/// <paramref name="Have"/> and <paramref name="Need"/> are insert tiers after the fill's claimed tiers
/// are sorted ascending, so the socket bench can say "rung 2 needs a t2 in the third slot, you have
/// t1" instead of only "missing something".
/// </summary>
public readonly record struct TierShortfall(int Position, int Have, int Need);

/// <summary>
/// D41's multiset match, computed once. <see cref="Satisfied"/> is exactly
/// <c>CombinationEvaluator</c>'s old <c>MultisetSatisfied</c> answer; <see cref="Missing"/> is exactly
/// <c>CombinationDistance</c>'s old <c>MultisetShortfall</c> answer — the SAME claiming pass now
/// produces both, so a fill this reports zero-missing for is exactly a fill <see cref="Satisfied"/>
/// is true for (the property host-gate exists to guarantee). <see cref="Used"/> is only meaningful
/// when <see cref="Satisfied"/> is true — on a failed match it may hold a partial, order-dependent
/// claim, which is safe because neither caller ever reads it in that case (the evaluator discards it
/// via <c>out _</c> while filtering, and only re-reads it for a recipe already proven satisfied).
/// </summary>
public readonly record struct MultisetMatch(
    bool Satisfied, IReadOnlyList<SocketFill> Used, IReadOnlyList<MissingIngredient> Missing,
    int Rung = 0, IReadOnlyList<TierShortfall>? TierShortfalls = null)
{
    /// <summary>The positional shortfalls of the rung ABOVE <see cref="Rung"/>, or of rung 1 when the
    /// families matched and none was reached. Empty when <see cref="Satisfied"/> — the fill already
    /// meets a rung, so nothing is short of anything.</summary>
    public IReadOnlyList<TierShortfall> ShortOfNext => TierShortfalls ?? Array.Empty<TierShortfall>();
}

/// <summary>
/// strain-splice-host SSH1.1 (host-gate §1/§3) — <b>one matcher</b>, called by both
/// <see cref="CombinationEvaluator"/> (what fires) and <c>CombinationDistance</c> (in
/// <c>Items.Surfaces</c> — how far from firing, and the compendium preview), so the two can never
/// disagree about the same fill against the same recipe. Before this extraction,
/// <c>CombinationEvaluator.HostMatches</c>/<c>MultisetSatisfied</c> and
/// <c>CombinationDistance.Reachable</c>'s host checks/<c>MultisetShortfall</c> were four separately
/// maintained near-duplicates. Pure: no RNG, no store, no clock, same fill and catalog always answer
/// the same way.
/// </summary>
public static class ComboMatcher
{
    /// <summary>
    /// The host-only half of "can this recipe ever fire here": socket count vs.
    /// <see cref="ComboRecipe.MinSockets"/>, host role, host frame. Extracted verbatim from
    /// <c>CombinationEvaluator.HostMatches</c> and the first three checks of
    /// <c>CombinationDistance.Reachable</c> — the two were byte-identical before this extraction.
    /// Everything else <c>Reachable</c> checks (set-exclusivity, ingredient count vs. sockets,
    /// threshold vs. sockets, ring/eclipse's two-socket minimum) is a per-SHAPE reachability rule, not
    /// a host predicate, and stays owned by <c>CombinationDistance</c> — <see cref="HostAdmits"/> is
    /// the shared subset, not the whole reachability gate.
    /// </summary>
    public static bool HostAdmits(SocketHost host, ComboRecipe recipe)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));

        if (recipe.MinSockets > host.SocketCount) return false;
        if (recipe.HostRole.Length > 0 && !string.Equals(recipe.HostRole, ItemRoles.Id(host.Role), StringComparison.Ordinal))
            return false;
        if (recipe.HostFrame.Length > 0 && !string.Equals(recipe.HostFrame, host.Frame, StringComparison.Ordinal))
            return false;
        return true;
    }

    /// <summary>
    /// D41's multiset match, read through the tier LADDER (strain-splice-host SSH7.2, spec-tier-ladder
    /// §2). Each ingredient claims <see cref="ComboIngredient.Quantity"/> distinct fills of its own
    /// FAMILY — tier is the ladder's business now, never the ingredient's — and the claimed tiers,
    /// sorted ascending, are compared POSITIONALLY against the highest rung whose floors they meet.
    ///
    /// <para>⚠ <b><paramref name="ladder"/> omitted is not a second rule.</b> It is rung 1 and
    /// nothing else, with the floors the importer read from tuning onto
    /// <see cref="ComboRecipe.BaseFloors"/> — which is exactly the shipped flat floor, so every
    /// pre-ladder caller keeps the behaviour it has always had
    /// (`rung_one_reproduces_the_shipped_flat_floor`). A recipe that carries NO floors is REFUSED
    /// rather than read as "every tier qualifies": the ladder belongs to tuning, and a caller that
    /// has one must pass it.</para>
    ///
    /// <para>Claiming takes the HIGHEST available tiers of each family, so the rung reported is the
    /// best the fill can reach; a fill whose families all match but whose rung 1 is not met reports
    /// <see cref="TierShortfall"/>s and no missing family.</para>
    /// </summary>
    public static MultisetMatch Match(
        ComboRecipe recipe, IReadOnlyList<SocketFill> fill, IReadOnlyList<TierLadderRung>? ladder = null)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));
        if (fill is null) throw new ArgumentNullException(nameof(fill));

        // A recipe with NO ingredients can never satisfy, and it names no floor either — refused as an
        // unsatisfied match rather than as a tuning defect, which is what it was before the ladder.
        if (recipe.Ingredients.Count == 0)
            return new MultisetMatch(false, Array.Empty<SocketFill>(), Array.Empty<MissingIngredient>());

        var rungs = ladder ?? FloorsFromTheRecipe(recipe);
        var claimed = new HashSet<int>();
        var used = new List<SocketFill>();
        var missing = new List<MissingIngredient>();

        // Family + quantity only, family id order so the claim is independent of the catalog's own
        // ordering and of how the caller happened to list the ingredients.
        foreach (var need in recipe.Ingredients.OrderBy(i => i.FamilyId, StringComparer.Ordinal))
        {
            var want = need.Quantity;
            if (want <= 0)
            {
                missing.Add(new MissingIngredient(need.FamilyId, 1));
                continue;
            }

            var candidates = fill
                .Where(f => !claimed.Contains(f.SocketIndex))
                .Where(f => string.Equals(f.Insert.FamilyId, need.FamilyId, StringComparison.Ordinal))
                .OrderByDescending(f => f.Insert.Tier)
                .ThenBy(f => f.SocketIndex)
                .Take(want)
                .ToList();

            foreach (var c in candidates)
            {
                claimed.Add(c.SocketIndex);
                used.Add(c);
            }

            var shortfall = want - candidates.Count;
            // A shortfall is a FAMILY and a count (SSH7.8): the tier a fill must reach is the rung's,
            // never the ingredient's, so the bench reads the position/need pair from
            // <see cref="MultisetMatch.ShortOfNext"/> instead of a per-ingredient floor.
            if (shortfall > 0) missing.Add(new MissingIngredient(need.FamilyId, shortfall));
        }

        if (recipe.Ingredients.Count == 0 || missing.Count > 0)
            return new MultisetMatch(false, used, missing);

        var tiers = used.Select(f => f.Insert.Tier).OrderBy(t => t).ToList();
        var rung = HighestRungMet(tiers, rungs);
        if (rung > 0) return new MultisetMatch(true, used, missing, rung);

        // Families claimed, no rung met: the preview says WHICH POSITIONS are short of which tier.
        var first = PositionalShortfalls(tiers, rungs[0].Floors);
        return new MultisetMatch(false, used, missing, 0, first);
    }

    /// <summary>
    /// The rung's own grant delta, or 0 when no ladder is passed (rung 1 IS the flat floor, which
    /// grants no delta). A rung the ladder does not carry is refused rather than clamped.
    /// </summary>
    public static int GrantDelta(IReadOnlyList<TierLadderRung>? ladder, int rung)
    {
        if (ladder is null) return 0;
        foreach (var candidate in ladder)
            if (candidate.Rung == rung) return candidate.GrantDelta;
        throw new InvalidOperationException(
            $"the tier ladder carries no rung {rung} (it has " +
            $"{string.Join(", ", ladder.Select(r => r.Rung))}) — a rung outside it is a defect, " +
            "never rounded to the nearest one");
    }

    /// <summary>
    /// The one-rung ladder a caller with no ladder gets: rung 1, at the floors the IMPORTER read from
    /// tuning (`<see cref="ComboRecipe.BaseFloors"/>`, SSH7.5). A recipe built by a caller that states
    /// no floors REFUSES (SSH7.8 removed the old per-ingredient fallback along with
    /// <c>ComboIngredient.MinTier</c>), because the ladder is tuning's and a floorless rung 1 would
    /// read every fill as qualified.
    /// </summary>
    static IReadOnlyList<TierLadderRung> FloorsFromTheRecipe(ComboRecipe recipe)
    {
        var floors = recipe.BaseFloors is { Count: > 0 } stated
            ? stated.OrderBy(t => t).ToList()
            : new List<int>();

        if (floors.Count == 0 || floors.All(t => t <= 0))
            throw new InvalidOperationException(
                $"recipe '{recipe.ComboId}' carries no tier floors — rung 1 would require nothing, so " +
                "every fill whose families matched would fire. Pass the tuning's ladder explicitly " +
                "(`Match(recipe, fill, ladder)`) or import the recipe through `CombinationCorpus`, " +
                "which fills BaseFloors from it");

        return new[] { new TierLadderRung(Rung: 1, Floors: floors, GrantDelta: 0) };
    }

    /// <summary>The highest rung whose floors the ascending claimed tiers meet, or 0.</summary>
    static int HighestRungMet(IReadOnlyList<int> tiers, IReadOnlyList<TierLadderRung> rungs)
    {
        for (var rung = rungs.Count - 1; rung >= 0; rung--)
            if (Meets(tiers, rungs[rung].Floors)) return rungs[rung].Rung;
        return 0;
    }

    static bool Meets(IReadOnlyList<int> tiers, IReadOnlyList<int> floors)
    {
        if (tiers.Count != floors.Count) return false;
        for (var i = 0; i < floors.Count; i++)
            if (tiers[i] < floors[i]) return false;
        return true;
    }

    /// <summary>Every position of <paramref name="floors"/> the fill is short at — the ONE positional
    /// rule, used both to decide rung 1 and to tell the bench what the next rung needs.</summary>
    static IReadOnlyList<TierShortfall> PositionalShortfalls(
        IReadOnlyList<int> tiers, IReadOnlyList<int> floors)
    {
        var shortfalls = new List<TierShortfall>();
        for (var position = 0; position < floors.Count && position < tiers.Count; position++)
            if (tiers[position] < floors[position])
                shortfalls.Add(new TierShortfall(position, tiers[position], floors[position]));
        for (var position = tiers.Count; position < floors.Count; position++)
            shortfalls.Add(new TierShortfall(position, 0, floors[position]));
        return shortfalls;
    }

    /// <summary>Convenience boolean form of <see cref="Match"/> for a caller that needs only whether
    /// the recipe fires, never the claimed fills or the missing list. Reads the OPENED count — a fill
    /// can never carry more entries than <see cref="SocketHost.SocketCount"/> sockets are open, so
    /// this answers "does it fire right now", never "could it ever".</summary>
    public static bool Fits(ComboRecipe recipe, IReadOnlyList<SocketFill> fill,
                            IReadOnlyList<TierLadderRung>? ladder = null) =>
        Match(recipe, fill, ladder).Satisfied;

    /// <summary>
    /// strain-splice-host SSH1.3 (host-gate §2) — the CAPACITY-aware half of "can this recipe ever
    /// fire here", for the compendium's own "is this a goal, or Undiscovered" question. Deliberately
    /// separate from <see cref="HostAdmits"/>: that function reads
    /// <see cref="SocketHost.SocketCount"/> (what is OPENED right now, the right question for
    /// <see cref="CombinationEvaluator"/>'s "does it fire on the real fill"), while this reads
    /// <see cref="SocketHost.Capacity"/> (what the chassis could EVER hold, the right question for
    /// reachability) — an unbored chassis with real capacity is a real goal, not
    /// <c>Undiscovered</c>, even though nothing is open on it yet.
    /// </summary>
    public static bool CanEverHold(SocketHost host, ComboRecipe recipe)
    {
        if (recipe is null) throw new ArgumentNullException(nameof(recipe));

        if (recipe.MinSockets > host.Capacity) return false;
        if (recipe.HostRole.Length > 0 && !string.Equals(recipe.HostRole, ItemRoles.Id(host.Role), StringComparison.Ordinal))
            return false;
        if (recipe.HostFrame.Length > 0 && !string.Equals(recipe.HostFrame, host.Frame, StringComparison.Ordinal))
            return false;
        return true;
    }
}
