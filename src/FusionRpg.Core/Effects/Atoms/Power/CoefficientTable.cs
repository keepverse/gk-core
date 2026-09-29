using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Effects.Atoms.Power;

/// <summary>
/// One authored price coefficient. <paramref name="Channel"/> is empty for a kind priced the same
/// whatever it writes.
/// </summary>
/// <param name="CoeffMilli">Points per reference unit, per-mille.</param>
/// <param name="ReferenceScale">
/// What "one unit" means for this channel — the part that cannot be skipped. <c>+10 hp</c> is ten
/// hit points; <c>+10 fire power</c> is ten <i>resolver</i> points at 0.1 sigmoid units. A
/// coefficient table without normalisation prices those alike and is wrong by an order of magnitude.
/// </param>
public sealed record PowerCoefficientRow(
    string KindId, string Channel, int CoeffMilli, int ReferenceScale);

/// <summary>Expected fires per battle-minute for one trigger.</summary>
/// <remarks>
/// Data rather than a constant, deliberately: it is a balance number, the sweep must be able to
/// propose against it, and as a code constant it would move every golden with <b>no content-hash
/// change</b> — the one outcome E8 exists to prevent.
/// </remarks>
public sealed record TriggerFrequencyRow(string Trigger, int PerMinute);

/// <summary>
/// `P0.3` (spec-power-vector.md, "predicates ARE priced — owner decision, 2026-08-27"): one leaf's
/// four-factor conditionality chain — <c>reachability × susceptibility × coincidence × uptime</c>,
/// each per-mille. Keyed on <paramref name="LeafId"/> and <paramref name="ArgKey"/> because
/// <c>hasStatus</c> differs per status and <c>hpBelowMilli</c> per threshold — the SAME reasoning
/// <see cref="PowerCoefficientRow"/>'s <c>Channel</c> key already applies one level up.
/// </summary>
/// <param name="LeafId">The <c>LeafId</c> enum member's own name (e.g. <c>"HasStatus"</c>) — a plain
/// string at the data layer, matching how every other table here keys on strings, never an enum.</param>
/// <param name="ArgKey">The leaf's own arg — a status id, a threshold, or empty for an arg-independent
/// leaf. Mirrors <see cref="PowerCoefficientRow.Channel"/>'s "empty means priced the same regardless" shape.</param>
public sealed record PredicateFrequencyRow(
    string LeafId, string ArgKey, int ReachabilityMilli, int SusceptibilityMilli, int CoincidenceMilli, int UptimeMilli);

/// <summary>
/// E44 (spec-power-sweep.md §4.2, closing definitions.md §13 D2): a genuinely non-linear correction
/// between two channels that price additively today but compose multiplicatively in real combat —
/// crit rate × crit damage is the named, mandatory pair; shield capacity × toughness is the second.
///
/// <para><b>Why this exists and the two prior attempts did not close D2.</b> Attempt 1 (marginal read
/// over an additive sum) and attempt 2 (aggregate channel totals, then price — what
/// <see cref="ActorPowerCache.Compose"/> already did before this row type existed) are both still
/// <i>additive underneath</i>: a sum, and a linear <c>price()</c> applied to a sum, both have zero
/// cross terms by construction, so <c>marginal(x, A)</c> collapsed to <c>p(x)</c> regardless of
/// <c>A</c> either way. This row is read by <see cref="ActorPowerCache"/> to add a correction term
/// that is proportional to the <b>product</b> of the two sides' own priced channel totals — the one
/// shape with an actual cross term, per the concrete direction spec-power-sweep.md's own §"a concrete
/// direction worth trying" names.</para>
///
/// <para><b>Narrow and closed by construction, not a general nonlinear price engine</b> — exactly the
/// pairs this table lists, never inferred from channel name shape. <see cref="KindA"/>/<see cref="ChannelA"/>
/// and <see cref="KindB"/>/<see cref="ChannelB"/> are unordered as a pair (the lookup in
/// <see cref="ActorPowerCache"/> checks both are present on the actor; which one is named "A" is not
/// meaningful).</para>
/// </summary>
/// <param name="CoeffMilli">Per-mille strength of the correction, applied to the product of the two
/// sides' own priced points (see <see cref="ActorPowerCache"/>'s <c>Interaction</c> for the exact
/// arithmetic) — the same "coefficient over a data row" shape <see cref="PowerCoefficientRow"/>
/// already uses, so a later sweep can propose against this table exactly the way it proposes against
/// that one. <b>Flat and unfitted here</b>, the same discipline the 20 flat-1000 rows in
/// <see cref="PowerTables.Authored"/> follow — but NOT flat-1000 itself: 1000‰ means something
/// different for a term that scales a PRODUCT of two already-priced points than it does for a term
/// that scales a single normalized magnitude, and copying the number without re-deriving what it means
/// here was this construction's first mistake, caught and corrected — see
/// <see cref="PowerTables.InteractionCoeffMilli"/>'s own comment for the corrected value and why.
/// Fitting either table is E44's separate, out-of-scope half (spec-power-sweep.md §4.1).</param>
/// <param name="Category">Which of the five categories the correction lands in. Not derived from
/// either side's <c>AtomKind.Categories</c> (both sides here are <c>stat.derived</c>, whose kind-level
/// categories are Offense|Survivability|Control regardless of which channel it writes) — the
/// interaction itself has a specific meaning (a crit synergy is offense; a shield synergy is
/// survivability) that a kind-wide flag set would blur.</param>
public sealed record PowerInteractionRow(
    string KindA, string ChannelA, string KindB, string ChannelB, int CoeffMilli, PowerCategory Category);

/// <summary>
/// standing-coeff-tuning (T16, spec's own D4 finding): overrides <see cref="AtomKind.Categories"/>'
/// kind-wide flag set for one combat channel FAMILY — the prefix before the trailing element slot
/// (e.g. <c>"combat.dodge"</c> for <c>"combat.dodge.fire"</c>) — the same "a kind-wide flag set would
/// blur this" reasoning <see cref="PowerInteractionRow.Category"/> already established for interaction
/// rows, applied here to the base per-channel price instead. Family-level, not per-channel (the
/// spec's own "family/mask first" default) — every one of <c>DerivedStatChannels.CombatChannelFamilies</c>'
/// 28 families covers all ~196 combat channels via the shared element-roster expansion, so one row per
/// family is enough; no per-slot duplication like <see cref="PowerInteractionRow"/> needs (that type
/// pairs two SPECIFIC full channels, this one matches by stripping the trailing slot at lookup time).
/// </summary>
public sealed record PowerCategoryOverrideRow(string KindId, string ChannelFamily, PowerCategory Category);

/// <summary>
/// The coefficients and trigger frequencies a price is computed from.
///
/// <para><b>Authored values live in rows; a sweep writes proposals to a side table and never touches
/// these.</b> That is what makes "hand-authored now, fitted later" mechanically possible rather than
/// aspirational — humans decide what ships.</para>
/// </summary>
public sealed class PowerTables
{
    readonly Dictionary<(string Kind, string Channel), PowerCoefficientRow> _coefficients;
    readonly Dictionary<string, int> _frequency;
    readonly Dictionary<(string LeafId, string ArgKey), PredicateFrequencyRow> _predicateFrequency;
    readonly Dictionary<(string Kind, string Family), PowerCategoryOverrideRow> _categoryOverrides;

    public PowerTables(
        IReadOnlyList<PowerCoefficientRow> coefficients, IReadOnlyList<TriggerFrequencyRow> frequencies,
        IReadOnlyList<PredicateFrequencyRow>? predicateFrequencies = null,
        IReadOnlyList<PowerInteractionRow>? interactions = null,
        IReadOnlyList<PowerCategoryOverrideRow>? categoryOverrides = null)
    {
        Coefficients = coefficients;
        Frequencies = frequencies;
        PredicateFrequencies = predicateFrequencies ?? Array.Empty<PredicateFrequencyRow>();
        Interactions = interactions ?? Array.Empty<PowerInteractionRow>();
        CategoryOverrides = categoryOverrides ?? Array.Empty<PowerCategoryOverrideRow>();
        _coefficients = new Dictionary<(string, string), PowerCoefficientRow>();
        foreach (var c in coefficients) _coefficients[(c.KindId, c.Channel)] = c;
        _frequency = frequencies.ToDictionary(f => f.Trigger, f => f.PerMinute, StringComparer.Ordinal);
        _predicateFrequency = new Dictionary<(string, string), PredicateFrequencyRow>();
        foreach (var p in PredicateFrequencies) _predicateFrequency[(p.LeafId, p.ArgKey)] = p;
        _categoryOverrides = new Dictionary<(string, string), PowerCategoryOverrideRow>();
        foreach (var c in CategoryOverrides) _categoryOverrides[(c.KindId, c.ChannelFamily)] = c;
    }

    public IReadOnlyList<PowerCoefficientRow> Coefficients { get; }
    public IReadOnlyList<TriggerFrequencyRow> Frequencies { get; }
    public IReadOnlyList<PredicateFrequencyRow> PredicateFrequencies { get; }

    /// <summary>E44/D2's closed, named set of cross-channel corrections — see <see cref="PowerInteractionRow"/>.
    /// Empty for a caller that never passed any (every pre-E44 construction site), which is exactly
    /// "no correction applies", not an error: <see cref="ActorPowerCache.Compose"/> degrades to its
    /// pre-E44 additive-per-channel behaviour rather than throwing on a table nobody populated.</summary>
    public IReadOnlyList<PowerInteractionRow> Interactions { get; }

    /// <summary>T16's closed, named set of per-family category overrides — see
    /// <see cref="PowerCategoryOverrideRow"/>. Empty for a caller that never passed any (every pre-T16
    /// construction site), which degrades <see cref="CategoryOverrideFor"/> to always returning null —
    /// exactly "no override applies," never an error.</summary>
    public IReadOnlyList<PowerCategoryOverrideRow> CategoryOverrides { get; }

    /// <summary>
    /// The category override for one channel, if its family has an authored row — else null, which
    /// tells the caller to fall back to the kind's own <see cref="AtomKind.Categories"/>. The family is
    /// the channel with its trailing element slot stripped (<c>"combat.dodge.fire"</c> →
    /// <c>"combat.dodge"</c>) — every combat channel this repo generates is <c>{family}.{elementId}</c>,
    /// the same shape every per-family table in <c>DerivedStatChannels</c> already assumes.
    /// </summary>
    public PowerCategory? CategoryOverrideFor(string kindId, string? channel)
    {
        if (string.IsNullOrEmpty(channel)) return null;
        var dot = channel.LastIndexOf('.');
        if (dot <= 0) return null;
        var family = channel[..dot];
        return _categoryOverrides.TryGetValue((kindId, family), out var row) ? row.Category : null;
    }

    /// <summary>
    /// The row for a kind and channel, falling back to the kind's channel-less row.
    ///
    /// <para>Null when neither exists — and the caller must treat that as <b>unpriced</b>, not as
    /// zero. A missing coefficient silently pricing at zero is how a whole family becomes free.</para>
    /// </summary>
    public PowerCoefficientRow? Find(string kindId, string? channel)
    {
        if (!string.IsNullOrEmpty(channel)
            && _coefficients.TryGetValue((kindId, channel!), out var exact))
            return exact;

        return _coefficients.TryGetValue((kindId, ""), out var generic) ? generic : null;
    }

    /// <summary>Fires per battle-minute. Zero for an unlisted trigger.
    ///
    /// <para><b>Zero is a refusal, not a free atom.</b> This used to say "which the ICD factor
    /// handles", and that was wrong: <c>Conditionality</c> multiplies by <c>DivRound(perMinute × 1000,
    /// 60)</c>, so an unlisted trigger makes the whole factor zero and the atom prices at nothing —
    /// silently, until ST4.5d made <c>CostFunction.PriceForChannel</c> refuse it by name. Seeded since
    /// ST4.5c, so the shipped triggers all have a row (<c>gk-data/packs/fusion/data/seed/power/trigger-frequencies.v1.json</c>);
    /// a trigger content authors without one is now reported, never free.</para></summary>
    public int FrequencyOf(string? trigger) =>
        trigger is not null && _frequency.TryGetValue(trigger, out var f) ? f : 0;

    /// <summary>
    /// The floored four-factor chain for one leaf, per-mille: <c>max(floorMilli, reachability ×
    /// susceptibility × coincidence × uptime)</c>. <b>1000‰ (never discounted) for an unlisted
    /// leaf/arg</b> — the safe default: an unauthored row can only ever FAIL to give a deserved
    /// discount, never hand out an undeserved one (the same reasoning
    /// <see cref="FusionRpg.Core.Actions.Rungs.RungMonotonicity.PredicatePricingLanded"/> records at
    /// the call site). This is the opposite default from <see cref="FrequencyOf"/> deliberately —
    /// <c>0</c> is safe there because the ICD factor neutralises it; <c>1000</c> is the neutral value
    /// here.
    /// </summary>
    public long PredicateFrequencyOf(LeafId leafId, string argKey, int floorMilli)
    {
        if (!_predicateFrequency.TryGetValue((leafId.ToString(), argKey), out var row))
            return PowerMath.One;

        var chain = PowerMath.CombineMilli(row.ReachabilityMilli, row.SusceptibilityMilli);
        chain = PowerMath.CombineMilli(chain, row.CoincidenceMilli);
        chain = PowerMath.CombineMilli(chain, row.UptimeMilli);
        return Math.Max(floorMilli, chain);
    }

    // ---- the authored defaults --------------------------------------------------------------------

    /// <summary>
    /// The hand-authored starting values, which a host with no database runs on.
    ///
    /// <para>Calibration behind the combat numbers: <c>critical-hunter</c> grants +150 crit-rate
    /// points and moves crit from ~7.6% to ~26.9%, and the patron aura divides ‰ by ten, so its 150‰
    /// clamp is +15 points. Those two facts are why a resolver point is worth roughly ten times a hit
    /// point here, and why the reference scales differ by an order of magnitude rather than by a
    /// rounding.</para>
    /// </summary>
    public static PowerTables Authored()
    {
        var coefficients = new List<PowerCoefficientRow>
        {
            // Primary stat channels: reference scale is the raw stat unit.
            new("stat.modify", "hp", 1000, 10),
            new("stat.modify", "maxHp", 1000, 10),
            new("stat.modify", "atk", 1000, 2),
            new("stat.modify", "defense", 1000, 2),
            new("stat.modify", "arm1", 1000, 10),
            new("stat.modify", "arm1Max", 1000, 10),
            new("stat.modify", "arm2", 1000, 10),
            new("stat.modify", "arm2Max", 1000, 10),
            new("stat.modify", "", 1000, 10),

            // Derived combat channels are resolver points: ten times denser than a hit point.
            new("stat.derived", "", 1000, 1),

            new("resource.delta", "", 1000, 10),
            new("resource.economy", "", 1000, 25),
            new("status.apply", "", 1000, 1),
            new("status.clear", "", 1000, 1),
            new("shield.grant", "", 1000, 10),
            new("spawn.entity", "", 1000, 1),
            new("board.action", "", 1000, 1),
            new("grid.spawn", "", 1000, 1),
            new("grid.clear", "", 1000, 1),
            new("box.set", "", 1000, 1),
        };

        var frequencies = new List<TriggerFrequencyRow>
        {
            new(AtomTriggers.OnDamageDealt, 60),
            new(AtomTriggers.OnDamageTaken, 40),
            new(AtomTriggers.OnSpawn, 4),
            new(AtomTriggers.OnDeath, 6),
            new(AtomTriggers.OnTimer, 12),
        };

        return new PowerTables(coefficients, frequencies,
            interactions: AuthoredInteractions(), categoryOverrides: AuthoredCategoryOverrides());
    }

    /// <summary>
    /// T16 (spec-standing-coeff-tuning.md, D4 finding 5): every <c>stat.derived</c> channel today
    /// prices through <c>AtomKindRegistry</c>'s kind-wide <c>Offense|Survivability|Control</c> trisect,
    /// so a purely defensive stat like dodge splits its price three ways instead of landing where it
    /// belongs. None of the 28 combat channel families this covers are a Control mechanic — Control's
    /// real combat-power content is <c>skill.cooldown</c>/<c>effectiveness</c> and
    /// <c>status.power</c>/<c>resist</c> (<c>CombatPowerMembership</c>'s own locked table, T8), not raw
    /// attack/defense math — so every row below is a clean two-way split: <c>Offense</c> for the
    /// ATTACKER'S half of a contest pair, <c>Survivability</c> for the DEFENDER'S. 16 of the 28
    /// families already carry that role in <c>DerivedStatChannels.CombatFamilyRole</c> (H.1); the
    /// other 12 (crit/power/defense/accuracy/dodge/shield-capacity/shield-toughness/shield-regen) are
    /// read the same way by plain domain meaning — none of them are ambiguous (dodge is the
    /// spec's own worked example: purely a defender stat, purely Survivability).
    /// <para>combat-math-dedup Task 12 (D16): the two sides are a PARTITION of
    /// <see cref="DerivedStatChannels.CombatChannelFamilies"/>, not a second list of the 28 ids. The
    /// 16 role-carrying families are read from the one role map
    /// (<see cref="DerivedStatChannels.CombatFamilyRole"/>); the remaining 12 carry no role and are
    /// named once below. Survivability is the complement over the family list, so a family added to
    /// that list cannot be omitted here and the two sides cannot overlap.
    /// <c>CoefficientTableCategoryOverrideTests</c> is the guard.</para>
    /// </summary>
    static IReadOnlyList<PowerCategoryOverrideRow> AuthoredCategoryOverrides()
    {
        // The five non-role attacker families, classified by plain domain meaning. The other 8
        // attacker families come from CombatFamilyRole; dodge (below, the spec's own worked example)
        // is purely a defender stat and lands in the complement.
        var offense = OffenseFamiliesWithoutRole
            .Concat(DerivedStatChannels.CombatFamilyRole
                .Where(kv => kv.Value == "attacker")
                .Select(kv => kv.Key))
            .ToArray();
        var offenseSet = offense.ToHashSet(StringComparer.Ordinal);
        var survivability = DerivedStatChannels.CombatChannelFamilies
            .Where(f => !offenseSet.Contains(f))
            .ToArray();

        var rows = new List<PowerCategoryOverrideRow>();
        foreach (var family in offense)
            rows.Add(new PowerCategoryOverrideRow("stat.derived", family, PowerCategory.Offense));
        foreach (var family in survivability)
            rows.Add(new PowerCategoryOverrideRow("stat.derived", family, PowerCategory.Survivability));
        return rows;
    }

    /// <summary>The attacker-half families that carry no <see cref="DerivedStatChannels.CombatFamilyRole"/>
    /// entry (combat-math-dedup Task 12, D16) — the 12 non-role families' offense half. A new family
    /// with no role entry must be classified here or the guard test fails.</summary>
    static readonly string[] OffenseFamiliesWithoutRole =
    {
        "combat.power", "combat.crit.rate", "combat.crit.damage", "combat.accuracy",
        DerivedStatChannels.CombatShieldPenPrefix,
    };

    /// <summary>
    /// E44/D2's two closed pairs — crit rate × crit damage, shield capacity × shield toughness — each
    /// generated once per element slot (omni + the 6-member roster, matching every other 7-slot family
    /// in <c>DerivedStatChannels</c>) rather than hand-listed 14 times.
    ///
    /// <para><b>Why 5‰, not a copy-pasted 1000‰.</b> Unlike <see cref="PowerCoefficientRow"/>, where
    /// 1000‰ means "apply the normalized magnitude unchanged" — a genuinely neutral starting point —
    /// <see cref="PowerInteractionRow.CoeffMilli"/> scales a PRODUCT of two already-priced point
    /// values, so 1000‰ there is not neutral at all: at real authored magnitudes it made the
    /// correction dwarf the additive base by an order of magnitude (checked against
    /// <c>gk-data/packs/fusion/data/seed/items/_registry/bands.v1.json</c>'s own <c>sigmoidDerivedChannel</c> worked
    /// example — tier 1 mid = 6 points, tier 5 mid = 56 points, the real range this fires over). 5‰
    /// keeps the correction a modest, single-to-low-double-digit percent of the additive base even at
    /// tier 5's top of that range, while staying unambiguously non-zero and provable at the synthetic
    /// magnitudes <c>PowerInteractionTests</c> uses to make the effect legible. <b>Still flat and
    /// unfitted</b> — a starting value chosen to be plausible at content scale, not measured — a later
    /// sweep proposes against this table exactly the way it proposes against the 20 rows above
    /// (spec-power-sweep.md §4.1/§4.2). <b>Honestly:</b> because the correction is bilinear in the two
    /// sides' magnitudes while a believable "interaction error" is closer to a constant percentage, no
    /// single flat coefficient holds that percentage across all five tiers at once — it under-corrects
    /// at tier 1 and over-corrects (relatively) at tier 5. That is exactly the imprecision E44's
    /// out-of-scope fitting half exists to close, not something hidden here.</para>
    ///
    /// <para>"The element ring" — the third pair spec-power-sweep.md §4.2 names — is deliberately
    /// absent here. Its multiplicative-ness (`ElementHub`'s 1.25 × 1.25 = 1.5625 for two strong slots,
    /// spec-power-vector.md's own worked proof) is a property of an ATTACKER × DEFENDER contest,
    /// already priced correctly and non-linearly by <see cref="MatchupRead"/> — which reads a defender.
    /// <see cref="ActorPowerCache.Compose"/> prices one actor with no defender in scope; inventing a
    /// synthetic opponent so a "ring interaction" row would have something to multiply against would
    /// not price the element ring, it would price a guess. This is a scope boundary, not a gap in the
    /// construction below.</para>
    /// </summary>
    const int InteractionCoeffMilli = 5;

    static IReadOnlyList<PowerInteractionRow> AuthoredInteractions()
    {
        var rows = new List<PowerInteractionRow>();
        foreach (var slot in Slots())
        {
            rows.Add(new PowerInteractionRow(
                "stat.derived", $"combat.crit.rate.{slot}",
                "stat.derived", $"combat.crit.damage.{slot}",
                InteractionCoeffMilli, PowerCategory.Offense));
            rows.Add(new PowerInteractionRow(
                "stat.derived", $"{DerivedStatChannels.CombatShieldCapacityPrefix}.{slot}",
                "stat.derived", $"{DerivedStatChannels.CombatShieldToughnessPrefix}.{slot}",
                InteractionCoeffMilli, PowerCategory.Survivability));
        }
        return rows;
    }

    /// <summary>omni + the 6-element roster — the same 7 slots every other element-typed channel
    /// family in <c>DerivedStatChannels</c> generates over, read from <see cref="ElementRoster"/>
    /// rather than a second, hand-kept element list.</summary>
    static IEnumerable<string> Slots()
    {
        yield return ElementRoster.OmniId;
        foreach (var e in ElementRoster.Concrete) yield return e.ToElementId();
    }

    static PowerTables _current = Authored();
    static readonly AsyncLocal<PowerTables?> Scoped = new();

    /// <summary>What this context prices with. Same shape as the element roster, for the same reason.</summary>
    public static PowerTables Current => Scoped.Value ?? _current;

    public static void Use(PowerTables tables) =>
        _current = tables ?? throw new ArgumentNullException(nameof(tables));

    public static void ResetToAuthored() => _current = Authored();

    /// <summary>Swap for this async context only, so a test cannot disturb one running beside it.</summary>
    public static IDisposable UseScoped(PowerTables tables)
    {
        if (tables is null) throw new ArgumentNullException(nameof(tables));
        var previous = Scoped.Value;
        Scoped.Value = tables;
        return new Restore(previous);
    }

    sealed class Restore : IDisposable
    {
        readonly PowerTables? _previous;
        public Restore(PowerTables? previous) => _previous = previous;
        public void Dispose() => Scoped.Value = _previous;
    }
}
