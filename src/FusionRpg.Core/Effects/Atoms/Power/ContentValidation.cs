using FusionRpg.Core.Stats;
namespace FusionRpg.Core.Effects.Atoms.Power;

/// <summary>One thing wrong, or possibly wrong, with the shipped content.</summary>
/// <param name="Blocking">
/// True for a validation, false for a lint. Keeping the two apart is what stops lint noise from
/// blocking a legitimate edge case — and what stops a real budget breach being filed as a warning.
/// </param>
public sealed record ContentFinding(string Subject, string Rule, string Detail, bool Blocking)
{
    public override string ToString() =>
        $"{(Blocking ? "FAIL" : "warn")} {Rule} [{Subject}]: {Detail}";
}

/// <summary>
/// What a validation pass looked at, and what it found.
///
/// <para><see cref="Evaluated"/> is carried because a pass that examined nothing and a pass that
/// found nothing look identical from a green tick. At E14b's position the only containers are E11's
/// migration output, none of which carry a rarity — so the budget check genuinely enumerates almost
/// nothing, and it must <b>say so</b> rather than pass silently and look thorough.</para>
/// </summary>
public sealed record ContentReport(int Evaluated, IReadOnlyList<ContentFinding> Findings)
{
    public IEnumerable<ContentFinding> Failures => Findings.Where(f => f.Blocking);
    public IEnumerable<ContentFinding> Warnings => Findings.Where(f => !f.Blocking);
    public bool Ok => !Failures.Any();

    public string Render(string what) =>
        $"{what}: {Evaluated} evaluated, {Failures.Count()} failure(s), {Warnings.Count()} warning(s)"
        + (Findings.Count == 0 ? "" : "\n  " + string.Join("\n  ", Findings));
}

/// <summary>
/// The three content checks (spec-authoring-and-validation.md, E14b).
///
/// <para><b>Validations fail; lints warn.</b> A budget breach is a mistake; a tier gap is usually a
/// typo but occasionally deliberate. Filing them together would mean either blocking on a guess or
/// shrugging at a real error.</para>
/// </summary>
public static class ContentValidation
{
    /// <summary>±25% per category, with a floor so small numbers do not trip on rounding.</summary>
    public const int DriftTolerancePercent = 25;

    /// <summary>Below this, a category is too small for a percentage to mean anything.</summary>
    public const int DriftFloor = 1;

    /// <summary>E30 (spec-channel-pool.md §3.4, decided 2026-09-03): a pool whose priciest member is
    /// more than 25% away from the pool's own weighted mean. Deliberately == <see cref="DriftTolerancePercent"/>
    /// restated in per-mille — a pool's members are priced by the same cost function with the same
    /// known ~12.5%-on-multiplicative-pairs error <see cref="Drift"/>'s own tolerance already answers,
    /// so a second, differently-chosen band would be a second answer to a question already answered.</summary>
    public const int PoolSpreadTolerancePerMille = 250;

    // ---- 1. the budget ----------------------------------------------------------------------------

    /// <summary>
    /// Rarity R may spend at most N power. A content test that fails naming the offender — and
    /// <b>never</b> a generation input: which atoms roll is E5's pool and tier weights.
    /// </summary>
    public static ContentReport Budget(
        IReadOnlyList<ContainerRow> containers,
        Func<string, IReadOnlyList<AtomRow>> atomsOf,
        Func<string, int?> ceilingFor)
    {
        var findings = new List<ContentFinding>();
        var evaluated = 0;

        foreach (var container in containers)
        {
            if (string.IsNullOrEmpty(container.Rarity)) continue;
            if (ceilingFor(container.Rarity!) is not { } ceiling) continue;

            evaluated++;
            var spent = ActorPowerCache.Compose(atomsOf(container.ContainerId)).Total;
            if (spent > ceiling)
                findings.Add(new ContentFinding(container.ContainerId, "budget",
                    $"spends {spent} against a ceiling of {ceiling} for rarity '{container.Rarity}' "
                    + $"— {spent - ceiling} over", Blocking: true));
        }

        return new ContentReport(evaluated, findings);
    }

    /// <summary>
    /// Rung-keyed sibling of the rarity-keyed <see cref="Budget(IReadOnlyList{ContainerRow}, Func{string, IReadOnlyList{AtomRow}}, Func{string, int?})"/>
    /// above (A-G1, spec-tier-access-gate.md §3.2): a generated action has a rung, not a rarity --
    /// `effect_container.rarity` is free TEXT with no FK, so a container reached through an action's
    /// own <c>Rung</c> needs its own ceiling lookup rather than borrowing the rarity path. Same rule:
    /// an over-budget container is a finding naming the container id, never clamped, never a silent
    /// pass; a container the caller cannot resolve a rung or a ceiling for is skipped, not zeroed --
    /// the same "skip, do not guess" direction the rarity overload already takes for a missing ceiling.
    /// </summary>
    /// <param name="rungOf">The rung a container was reached through, or <c>null</c> if this container
    /// is not addressed by any rung right now (the batch caller's business, never guessed here).</param>
    /// <param name="ceilingForRung">The rung's own <c>powerBudgetMilli</c>, or <c>null</c> when no
    /// rung table carrying that column is loaded (e.g. `action-rungs.v1.json`).</param>
    public static ContentReport Budget(
        IReadOnlyList<ContainerRow> containers,
        Func<string, IReadOnlyList<AtomRow>> atomsOf,
        Func<string, int?> rungOf,
        Func<int, long?> ceilingForRung)
    {
        var findings = new List<ContentFinding>();
        var evaluated = 0;

        foreach (var container in containers)
        {
            if (rungOf(container.ContainerId) is not { } rung) continue;
            if (ceilingForRung(rung) is not { } ceiling) continue;

            evaluated++;
            var spent = ActorPowerCache.Compose(atomsOf(container.ContainerId)).Total;
            if (spent > ceiling)
                findings.Add(new ContentFinding(container.ContainerId, "budget",
                    $"spends {spent} against a ceiling of {ceiling} for rung {rung} "
                    + $"— {spent - ceiling} over", Blocking: true));
        }

        return new ContentReport(evaluated, findings);
    }

    // ---- 2. power drift ----------------------------------------------------------------------------

    /// <summary>
    /// Recompute every atom's power and compare it to what is stored.
    ///
    /// <para>Beyond ±25% per category <b>without a note</b> is a failure; with a note it is reported
    /// and allowed. That is what keeps "computed base plus stored override" honest rather than
    /// decorative — and the running list of overrides is also the running list of shapes the cost
    /// function is bad at, which is a feature rather than a backlog.</para>
    ///
    /// <para>The tolerance is 25% for a stated reason. Not 5%: the cost function is knowingly wrong
    /// by ~12.5% on multiplicative pairs, so a tight tolerance would fail every crit and element atom
    /// on day one. Not 50%: that cannot detect a real mistake. 25% catches order-of-magnitude errors
    /// — the class the units trap produces — while tolerating the interaction error the marginal read
    /// exists to handle.</para>
    /// </summary>
    public static ContentReport Drift(IReadOnlyList<AtomRow> atoms, PowerTables? tables = null)
    {
        var t = tables ?? PowerTables.Current;
        var findings = new List<ContentFinding>();
        var evaluated = 0;

        foreach (var atom in atoms)
        {
            if (string.IsNullOrWhiteSpace(atom.PowerJson)) continue;

            var recomputed = CostFunction.Price(atom, t);
            if (!recomputed.Ok)
            {
                findings.Add(new ContentFinding(atom.AtomId, "drift",
                    $"stored power exists but the atom no longer prices: {recomputed.Verdict.Reason}",
                    Blocking: true));
                continue;
            }

            evaluated++;
            var stored = PowerVector.FromJson(atom.PowerJson);
            var hasNote = !string.IsNullOrWhiteSpace(atom.PowerNote);

            for (var i = 0; i < PowerVector.Categories.Length; i++)
            {
                if (!Drifted(stored[i], recomputed.Power[i])) continue;

                findings.Add(new ContentFinding(atom.AtomId, "drift",
                    $"{PowerVector.Categories[i]}: stored {stored[i]}, computed {recomputed.Power[i]}"
                    + (hasNote ? $" — allowed by note: {atom.PowerNote}" : ""),
                    Blocking: !hasNote));
            }
        }

        return new ContentReport(evaluated, findings);
    }

    /// <summary>Whether two category values differ by more than the tolerance, above the floor.</summary>
    public static bool Drifted(int stored, int computed)
    {
        var delta = Math.Abs(stored - computed);
        if (delta <= DriftFloor) return false;

        // Relative to what was STORED — the number being checked against. Using the larger of the
        // two makes the tolerance quietly asymmetric: a 26% overshoot measures itself against the
        // bigger figure and comes out at 21%, so the band is wider going up than coming down.
        var basis = Math.Abs(stored) == 0 ? Math.Abs(computed) : Math.Abs(stored);
        return basis == 0 || delta * 100 > basis * DriftTolerancePercent;
    }

    // ---- pool spread (E30) -----------------------------------------------------------------------

    /// <summary>
    /// E30 (spec-channel-pool.md §3.4): where a pool's members disagree on coefficient, the weighted
    /// mean is the honest price and the spread is reportable — never blocking, a pool of wildly
    /// unequal channels is a content defect, not a pricing one. <b>Neutral today, by construction</b>:
    /// every shipped <c>stat.derived</c> coefficient row is channel-less (`CoefficientTable.cs`), so
    /// every declared pool's spread is exactly 0 until per-channel coefficients exist (E44).
    /// </summary>
    public static ContentReport PoolSpread(
        IReadOnlyList<ChannelPoolRow> pools, PowerTables? tables = null, string kindId = "stat.derived")
    {
        var t = tables ?? PowerTables.Current;
        var findings = new List<ContentFinding>();
        var evaluated = 0;

        foreach (var pool in pools)
        {
            var priced = pool.Members
                .Select(m => (m.Channel, m.WeightMilli, Coeff: t.Find(kindId, m.Channel)))
                .Where(x => x.Coeff is not null)
                .Select(x => (x.Channel, x.WeightMilli, CoeffMilli: (long)x.Coeff!.CoeffMilli))
                .ToList();
            if (priced.Count == 0) continue;

            evaluated++;
            long totalWeight = priced.Sum(p => (long)p.WeightMilli);
            if (totalWeight <= 0) continue;
            long weightedSum = priced.Sum(p => p.CoeffMilli * p.WeightMilli);
            var mean = PowerMath.DivRound(weightedSum, totalWeight);
            if (mean == 0) continue;

            foreach (var (channel, _, coeffMilli) in priced)
            {
                var deltaPerMille = PowerMath.DivRound(Math.Abs(coeffMilli - mean) * 1000, Math.Abs(mean));
                if (deltaPerMille > PoolSpreadTolerancePerMille)
                    findings.Add(new ContentFinding(pool.PoolId, "pool-spread",
                        $"member '{channel}' coeff {coeffMilli}‰ is {deltaPerMille}‰ from the pool's " +
                        $"weighted mean {mean}‰ (tolerance {PoolSpreadTolerancePerMille}‰)", Blocking: false));
            }
        }

        return new ContentReport(evaluated, findings);
    }

    // ---- 3. the lints -------------------------------------------------------------------------------

    /// <summary>
    /// The cheap checks that catch real mistakes. Every one warns; none blocks.
    /// </summary>
    /// <param name="affixes">
    /// T3.1 (affix-schema): pool rows reference affixes, not bare atoms, so resolving "which atom
    /// does this container's pool actually touch" needs the affix catalog. Optional and defaults to
    /// empty so every pre-affix caller keeps compiling unchanged (same widening discipline as
    /// <c>Instantiator.Draw</c>'s own "visibility widened, no behavior changed" precedent) — an
    /// omitted affix list makes <see cref="OrphanAtoms"/> unable to see through a pool reference, so
    /// it may over-report an atom as orphaned; it never under-reports, so it stays a safe lint.
    /// </param>
    public static ContentReport Lint(
        IReadOnlyList<AtomRow> atoms, IReadOnlyList<ContainerRow> containers,
        IReadOnlyList<AffixRow>? affixes = null)
    {
        var findings = new List<ContentFinding>();
        var affixesById = (affixes ?? Array.Empty<AffixRow>()).ToDictionary(a => a.AffixId, StringComparer.Ordinal);

        TierGaps(atoms, findings);
        WeakerTiers(atoms, findings);
        DuplicateAffixes(atoms, findings);
        BackwardsIntervals(atoms, findings);
        LonelyPoolGroups(containers, findings);
        OrphanAtoms(atoms, containers, affixesById, findings);
        OrphanAffixes(affixes ?? Array.Empty<AffixRow>(), containers, findings);

        return new ContentReport(atoms.Count + containers.Count, findings);
    }

    /// <summary>
    /// A tier gap — 1, 2, 4 — is almost always a typo.
    ///
    /// <para><b>Keyed on (family, variant), not family.</b> <c>elemental_power</c> holds seven
    /// variants over five tiers, so a family-level check would hide a real gap in <c>ice</c> and
    /// invent false ones everywhere else.</para>
    /// </summary>
    static void TierGaps(IReadOnlyList<AtomRow> atoms, List<ContentFinding> into)
    {
        foreach (var group in atoms.GroupBy(a => (a.FamilyId, a.Variant)))
        {
            var tiers = group.Select(a => a.Tier).Distinct().OrderBy(t => t).ToList();
            for (var i = 1; i < tiers.Count; i++)
            {
                if (tiers[i] == tiers[i - 1] + 1) continue;
                into.Add(new ContentFinding(Key(group.Key), "tier-gap",
                    $"tiers {string.Join(", ", tiers)} — {tiers[i - 1] + 1} is missing", Blocking: false));
            }
        }
    }

    /// <summary>A tier that is not stronger than the one below it is not a tier.</summary>
    static void WeakerTiers(IReadOnlyList<AtomRow> atoms, List<ContentFinding> into)
    {
        foreach (var group in atoms.GroupBy(a => (a.FamilyId, a.Variant)))
        {
            var ordered = group.OrderBy(a => a.Tier).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var below = Magnitude(ordered[i - 1]);
                var here = Magnitude(ordered[i]);
                if (below is null || here is null || here > below) continue;

                into.Add(new ContentFinding(ordered[i].AtomId, "flat-tier",
                    $"tier {ordered[i].Tier} is {here}, no stronger than tier "
                    + $"{ordered[i - 1].Tier}'s {below}", Blocking: false));
            }
        }
    }

    /// <summary>
    /// Two families writing the same channel with the same op — one affix under two names.
    /// </summary>
    static void DuplicateAffixes(IReadOnlyList<AtomRow> atoms, List<ContentFinding> into)
    {
        var seen = new Dictionary<(string Kind, string Channel, string Op), string>();

        foreach (var atom in atoms)
        {
            var pars = CostFunction.Read(atom.ParamsJson);
            var channel = Text(pars, "channel");
            var op = Text(pars, "op");
            if (channel is null || op is null) continue;

            var key = (atom.KindId, channel, op);
            if (seen.TryGetValue(key, out var first))
            {
                if (string.Equals(first, atom.FamilyId, StringComparison.Ordinal)) continue;
                into.Add(new ContentFinding(atom.FamilyId, "duplicate-affix",
                    $"writes {channel}/{op} and so does '{first}'", Blocking: false));
            }
            else
                seen[key] = atom.FamilyId;
        }
    }

    /// <summary>
    /// A positive <c>Increased</c> on a lower-is-better channel (E16).
    ///
    /// <para>Almost always an author meaning "faster" and getting "slower": <c>Increased</c> on
    /// <c>attackInterval</c> lengthens the gap between shots. A lint rather than a validation,
    /// because a designer deliberately authoring a drawback is legitimate content — but it should
    /// have to be deliberate.</para>
    /// </summary>
    static void BackwardsIntervals(IReadOnlyList<AtomRow> atoms, List<ContentFinding> into)
    {
        foreach (var atom in atoms)
        {
            var pars = CostFunction.Read(atom.ParamsJson);
            var channel = Text(pars, "channel");
            if (channel is null || !StatChannels.IsLowerBetter(channel)) continue;

            var kind = AtomKindRegistry.Get(atom.KindId);
            if (kind is null) continue;

            var magnitude = CostFunction.MeanMagnitude(atom, kind, pars);
            if (magnitude <= 0) continue;

            into.Add(new ContentFinding(atom.AtomId, "backwards-interval",
                $"+{magnitude} on '{channel}', where lower is better — this makes the entity SLOWER. "
                + "A negative magnitude is what \"faster\" means here.", Blocking: false));
        }
    }

    /// <summary>A pool group with one member does nothing — the one-per-group rule never bites.</summary>
    static void LonelyPoolGroups(IReadOnlyList<ContainerRow> containers, List<ContentFinding> into)
    {
        foreach (var container in containers)
        {
            var groups = container.Pool.GroupBy(p => p.Group ?? p.AffixId);
            foreach (var group in groups.Where(g => g.Count() == 1 && g.Key is not null))
                into.Add(new ContentFinding(container.ContainerId, "lonely-group",
                    $"pool group '{group.Key}' has one member, so the one-per-group rule never applies",
                    Blocking: false));
        }
    }

    /// <summary>An atom no container references. Legal — and worth surfacing.</summary>
    static void OrphanAtoms(
        IReadOnlyList<AtomRow> atoms, IReadOnlyList<ContainerRow> containers,
        IReadOnlyDictionary<string, AffixRow> affixesById, List<ContentFinding> into)
    {
        // A pool row references an affix now (T3.1); "referenced" means every CONCRETE ref inside
        // that affix bundle. An affix not in the supplied catalog (or a slot-bearing ref, which has
        // no single concrete atom by construction) contributes nothing — see Lint's own doc comment
        // on why that is a safe direction for a non-blocking lint to fail in.
        var referenced = containers
            .SelectMany(c => c.Atoms.Select(a => a.AtomId).Concat(
                c.Pool.SelectMany(p => affixesById.TryGetValue(p.AffixId, out var affix)
                    ? affix.Refs.Where(r => r.AtomId is not null).Select(r => r.AtomId!)
                    : Enumerable.Empty<string>())))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var atom in atoms.Where(a => !referenced.Contains(a.AtomId)))
            into.Add(new ContentFinding(atom.AtomId, "orphan",
                "no container references it", Blocking: false));
    }

    /// <summary>
    /// An affix no container's pool references — the T3.8 (`affix-metrics`) "unreachable affix"
    /// finding, in its buildable-today form. The spec's own richer phrasing ("tag-eligible for
    /// nothing") names a check against `eligibility-tags` (module 8, `docs/architecture/effect-
    /// pipeline-map.md`) — not yet built, so not yet checkable; this is the container-reachability
    /// half only, the same shape <see cref="OrphanAtoms"/> already proves for a bare atom.
    /// </summary>
    static void OrphanAffixes(
        IReadOnlyList<AffixRow> affixes, IReadOnlyList<ContainerRow> containers, List<ContentFinding> into)
    {
        if (affixes.Count == 0) return; // no affix catalog supplied — same "safe direction" as OrphanAtoms

        var referenced = containers
            .SelectMany(c => c.Pool.Select(p => p.AffixId))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var affix in affixes.Where(a => !referenced.Contains(a.AffixId)))
            into.Add(new ContentFinding(affix.AffixId, "orphan-affix",
                "no container pool references it", Blocking: false));
    }

    static string Key((string Family, string Variant) k) =>
        k.Variant.Length == 0 ? k.Family : $"{k.Family}|{k.Variant}";

    static long? Magnitude(AtomRow atom)
    {
        var kind = AtomKindRegistry.Get(atom.KindId);
        if (kind is null) return null;
        return Math.Abs(CostFunction.MeanMagnitude(atom, kind, CostFunction.Read(atom.ParamsJson)));
    }

    static string? Text(IReadOnlyDictionary<string, System.Text.Json.JsonElement> map, string name) =>
        map.TryGetValue(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
            ? el.GetString() : null;
}
