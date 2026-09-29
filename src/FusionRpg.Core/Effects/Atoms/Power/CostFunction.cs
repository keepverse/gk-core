using FusionRpg.Core.Stats;
using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms.Power;

/// <summary>Why an atom could not be priced. Unpriced is never zero.</summary>
public readonly record struct PriceVerdict(bool Ok, string Reason)
{
    public static PriceVerdict Priced => new(true, "");
    public static PriceVerdict No(string reason) => new(false, reason);
}

/// <summary>A price and, when it could not be computed, the reason.</summary>
public readonly record struct PricedAtom(PowerVector Power, PriceVerdict Verdict)
{
    public bool Ok => Verdict.Ok;
}

/// <summary>
/// What one atom costs (spec-power-vector.md, E9).
///
/// <code>
/// power[category] = coeff(kind, channel) × normalize(magnitude, referenceScale) × conditionality
/// </code>
///
/// <para><b>Pure and integer.</b> The same row prices to the same vector on every machine and in
/// every process, because the price is stored, hashed, budgeted and compared. A <c>double</c> would
/// make two runs disagree in the last bit and move a content hash for nothing.</para>
///
/// <para><b>It is knowingly wrong on multiplicative pairs, by design.</b> Crit rate × crit damage,
/// the element ring and shield layers all multiply — two strong element slots give 1.25 × 1.25 =
/// +562.5‰ where naive addition says +500‰. A per-atom cost function prices each half in isolation
/// and underprices both. E9 does not solve that; <b>E10's marginal read</b> does, where it matters.
/// Stored atom power stays context-free and approximately right, which is all budgets and display
/// need — and the ±25% drift tolerance exists because of exactly this, not out of vagueness.</para>
/// </summary>
public static class CostFunction
{
    /// <summary>How deep a spawn may price the actor it makes. See <see cref="ActorPowerCache"/>.</summary>
    public const int MaxSpawnDepth = 1;

    /// <param name="lookupPool">E30 (spec-channel-pool.md §3.4): resolves a pool id to its row, so a
    /// <c>channel: {"pool": ..., "count": ...}</c> reference can be priced as
    /// <c>count × weighted_mean(price(member))</c>. <c>null</c> in every context with no pool catalog
    /// to ask (the default), in which case a pooled atom prices exactly as it did before this module —
    /// unpriced, naming the channel form as the reason, never a crash or a silent zero.</param>
    public static PricedAtom Price(
        AtomRow atom, PowerTables? tables = null, int depth = 0, Func<string, ChannelPoolRow?>? lookupPool = null)
    {
        if (atom is null) return new PricedAtom(PowerVector.Zero, PriceVerdict.No("null row"));

        var t = tables ?? PowerTables.Current;
        var kind = AtomKindRegistry.Get(atom.KindId);
        if (kind is null)
            return new PricedAtom(PowerVector.Zero, PriceVerdict.No($"unknown kind '{atom.KindId}'"));

        var pars = Read(atom.ParamsJson);
        var when = Read(atom.WhenJson);

        if (pars.TryGetValue("channel", out var channelEl) && channelEl.ValueKind == JsonValueKind.Object)
        {
            var read = ChannelRefJson.TryRead(channelEl, out var channelRef);
            if (read.IsOk && channelRef.IsPool)
            {
                if (lookupPool is null)
                    return new PricedAtom(PowerVector.Zero,
                        PriceVerdict.No($"{atom.KindId}: channel is a pool reference and no pool catalog was supplied to price it"));
                return PricePooled(atom, kind, pars, when, channelRef, t, lookupPool, depth);
            }
        }

        var coeff = t.Find(atom.KindId, StringOf(pars, "channel"));
        if (coeff is null)
            return new PricedAtom(PowerVector.Zero,
                PriceVerdict.No($"no coefficient for {atom.KindId}"
                                + (StringOf(pars, "channel") is { } c ? $" channel '{c}'" : "")));

        return PriceForChannel(atom, kind, pars, when, coeff, t, depth);
    }

    /// <summary>The single-channel pricing core, extracted so <see cref="PricePooled"/> can run it
    /// once per pool member and combine the results — every rule (magnitude, direction flip,
    /// conditionality, spawn body) applies identically whether the channel arrived concrete or as one
    /// draw of a pool.</summary>
    static PricedAtom PriceForChannel(
        AtomRow atom, AtomKind kind, IReadOnlyDictionary<string, JsonElement> pars,
        IReadOnlyDictionary<string, JsonElement> when, PowerCoefficientRow coeff, PowerTables t, int depth,
        string? channelOverride = null)
    {
        var channel = channelOverride ?? StringOf(pars, "channel");

        // ST4.5d (manager ruling on the ST4.5a diagnosis): a trigger with no frequency row multiplies
        // this atom's price by exactly zero — `Conditionality`'s `DivRound(0 * 1000, 60)` — and the
        // verdict used to come back `Priced`, so a whole family could be free with nothing to see.
        // Refuse it by name instead, the way `Find`'s own doc says a missing coefficient must be
        // treated ("as unpriced, not as zero"). The POWER this returns is the same zero either way, so
        // no price moves; what changes is that the atom is now UNSETTABLE-in-silence — it lands in
        // `BackfillAtomPower`'s `Unpriced` list, in `ContentValidation.Drift`'s blocking finding, and
        // in the calibration report's per-action unpriced ids, all of which name this trigger.
        if (StringOf(when, "trigger") is { Length: > 0 } trigger && t.FrequencyOf(trigger) <= 0)
            return new PricedAtom(PowerVector.Zero, PriceVerdict.No(
                $"{atom.KindId}: trigger '{trigger}' has no power_trigger_frequency row, so " +
                "conditionality would price this atom at zero — unpriced, never silently free"));

        // The MAGNITUDE, not the delta. `-100 hp` on a hit is 100 points of offense, not −100 of it:
        // the sign says which way the resource moves, and which kind of worth that is, is what the
        // category already carries. Pricing the signed value made every damage atom worth a negative
        // amount — so a budget over a damage item RELAXED as the item got deadlier.
        var magnitude = Math.Abs(MeanMagnitude(atom, kind, pars));
        var normalisedMilli = PowerMath.DivRound(magnitude * PowerMath.One, Math.Max(1, coeff.ReferenceScale));
        var conditionalityMilli = Conditionality(when, pars, t);

        var basePoints = PowerMath.MulMilli(normalisedMilli, coeff.CoeffMilli);
        var points = PowerMath.MulMilli(basePoints, conditionalityMilli);

        // E16: on a lower-is-better channel, going DOWN is the buff. `quickening` reduces an attack
        // interval, and pricing the raw magnitude would file the game's most wanted affix as a
        // penalty — negative power, failing no budget, sorting last in every UI.
        if (StatChannels.IsLowerBetter(channel) && SignOf(atom, kind, pars) > 0)
            points = -points;

        // standing-coeff-tuning (T16): same per-family override ActorPowerCache.Compose applies — the
        // two pricing paths must agree on a lone atom's category, or "marginal on an empty actor ≈
        // stored power" (E10's own invariant) breaks the moment a channel has a family override.
        var category = t.CategoryOverrideFor(atom.KindId, channel) ?? kind.Categories;
        var vector = PowerVector.FromCategory(category, points);

        // A spawn is worth the body it makes, priced from its own hp/atk rather than treated as base
        // stats worth nothing — else `spawn.entity{hp: 5000}` prices at zero (D3).
        if (string.Equals(atom.KindId, "spawn.entity", StringComparison.Ordinal))
            vector += SpawnBody(pars, conditionalityMilli, t, depth);

        return new PricedAtom(vector, PriceVerdict.Priced);
    }

    /// <summary>
    /// E30 §3.4: <c>price(pooled) = count × weighted_mean(price(member) for member in pool)</c>,
    /// weights being the pool's own per-mille weights — the EXPECTED value of the roll, so a pooled
    /// atom and the concrete atoms it can resolve to price consistently (an author cannot dodge a
    /// budget by pooling). Exact under the existing integer contract: every intermediate stays `long`
    /// per-mille, widened before multiplying, divided by 1000 last and exactly once, and overflow
    /// throws (checked arithmetic, per `AGENTS.md`'s numeric rule — never a silent wrap).
    /// </summary>
    static PricedAtom PricePooled(
        AtomRow atom, AtomKind kind, IReadOnlyDictionary<string, JsonElement> pars,
        IReadOnlyDictionary<string, JsonElement> when, ChannelRef channelRef, PowerTables t,
        Func<string, ChannelPoolRow?> lookupPool, int depth)
    {
        var pool = lookupPool(channelRef.PoolId!);
        if (pool is null)
            return new PricedAtom(PowerVector.Zero, PriceVerdict.No($"{atom.KindId}: unknown pool '{channelRef.PoolId}'"));

        checked
        {
            long totalWeight = 0;
            var weightedSum = new long[5]; // Offense, Survivability, Control, Utility, Economy — PowerVector's own index order
            var anyPriced = false;

            foreach (var member in pool.Members)
            {
                var coeff = t.Find(atom.KindId, member.Channel);
                if (coeff is null) continue; // an unpriceable member contributes nothing to the mean, not a crash

                var priced = PriceForChannel(atom, kind, pars, when, coeff, t, depth, channelOverride: member.Channel);
                if (!priced.Ok) continue;

                anyPriced = true;
                totalWeight += member.WeightMilli;
                for (var i = 0; i < 5; i++)
                    weightedSum[i] += (long)priced.Power[i] * member.WeightMilli; // widened before multiplying
            }

            if (!anyPriced || totalWeight <= 0)
                return new PricedAtom(PowerVector.Zero,
                    PriceVerdict.No($"{atom.KindId}: pool '{pool.PoolId}' has no priceable member"));

            // weighted_mean × count = (weightedSum × count) / totalWeight — one division, combining
            // the mean and the count-scale into a single rounding point (PS's "divide last, exactly
            // once"), rather than rounding the mean first and then rounding the scale a second time.
            var result = new int[5];
            for (var i = 0; i < 5; i++)
                result[i] = (int)PowerMath.DivRound(weightedSum[i] * channelRef.Count, totalWeight);

            var vector = new PowerVector(result[0], result[1], result[2], result[3], result[4]);
            return new PricedAtom(vector, PriceVerdict.Priced);
        }
    }

    /// <summary>
    /// <c>(chance/1000) × triggerFrequency × icdFactor × targetCountFactor</c>, in per-mille
    /// throughout with a single rounding at the end.
    ///
    /// <para><b>Every factor stays in per-mille</b> (D1). Rounding <c>chance/1000</c> to an integer
    /// as the formula went made it 0 for every proc below 1000‰ — the entire conditional half of the
    /// catalog priced at zero, and it read as a design limitation rather than as arithmetic.</para>
    ///
    /// <para><b>A triggerless atom is unconditional.</b> Permanent modifiers are not event-driven, and
    /// without this short-circuit the 26 passive families price at zero.</para>
    /// </summary>
    public static long Conditionality(
        IReadOnlyDictionary<string, JsonElement> when,
        IReadOnlyDictionary<string, JsonElement> pars,
        PowerTables tables)
    {
        var trigger = StringOf(when, "trigger");
        // A predicate on a TRIGGERLESS atom is not priced here — out of this decision's stated scope.
        // spec-power-vector.md's own reasoning and every worked example (`hasStatus(rot)` gating an
        // on-hit proc) are about EVENT-DRIVEN atoms; a permanent modifier's own early return already
        // exists for the same class of reason (dividing by a trigger frequency that does not apply),
        // and inventing pricing for an unspecified case risks moving goldens for content nobody
        // reviewed this decision against.
        if (string.IsNullOrEmpty(trigger)) return PowerMath.One;

        var chanceMilli = IntOf(when, "chance") ?? 1000;

        // Frequency is normalised against a nominal one-per-second baseline (60/min), so a trigger
        // that fires as often as a hit is worth 1.0 and rarer ones are worth proportionally less.
        var perMinute = tables.FrequencyOf(trigger);
        var frequencyMilli = PowerMath.DivRound((long)perMinute * PowerMath.One, 60);

        var factor = PowerMath.CombineMilli(chanceMilli, frequencyMilli);
        factor = PowerMath.CombineMilli(factor, IcdFactorMilli(IntOf(when, "icd_ms") ?? 0, perMinute));
        factor = PowerMath.CombineMilli(factor, TargetFactorMilli(pars));
        factor = PowerMath.CombineMilli(factor, PredicateFrequencyMilli(when, tables));
        return factor;
    }

    /// <summary>
    /// `P0.3`: the fifth conditionality factor — "a predicate is priced exactly the way a trigger
    /// already is" (owner, 2026-08-27). <c>1000‰ (unconditional)</c> when the atom declares no
    /// predicate, is malformed, or fails E3 validation — never a silent discount for content this
    /// function cannot parse.
    /// </summary>
    static long PredicateFrequencyMilli(IReadOnlyDictionary<string, JsonElement> when, PowerTables tables)
    {
        if (!when.TryGetValue("predicate", out var predEl) || predEl.ValueKind != JsonValueKind.Object)
            return PowerMath.One;

        var readRejection = AtomJson.TryReadPredicate(predEl, out var tree);
        if (!readRejection.IsOk || tree is null)
            return PowerMath.One;

        return PredicatePricer.PriceTree(tree, tables, PowerPredicateTuningHub.Current.DiscountFloorMilli);
    }

    /// <summary>
    /// <c>min(1, triggerFrequency⁻¹ / (icd_ms/60000))</c> — how much of the trigger's natural rate an
    /// internal cooldown actually lets through.
    ///
    /// <para>1 when there is no ICD, <b>and</b> when the frequency is zero: the formula divides by the
    /// frequency, so an unlisted trigger would otherwise divide by zero rather than simply be rare.</para>
    /// </summary>
    public static long IcdFactorMilli(int icdMs, int perMinute)
    {
        if (icdMs <= 0 || perMinute <= 0) return PowerMath.One;

        // Fires the ICD permits per minute, capped at the trigger's own rate.
        var permitted = PowerMath.DivRound(60_000L, icdMs);
        return permitted >= perMinute
            ? PowerMath.One
            : PowerMath.DivRound(permitted * PowerMath.One, perMinute);
    }

    /// <summary>
    /// <c>min(maxTargets, expectedTargets)</c>, floored at 1 (D4).
    ///
    /// <para>An omitted target count is one target, not none. Zero would price every single-target
    /// atom — which is most of them — at nothing.</para>
    /// </summary>
    public static long TargetFactorMilli(IReadOnlyDictionary<string, JsonElement> pars)
    {
        var expected = IntOf(pars, "expectedTargets") ?? IntOf(pars, "maxTargets") ?? 1;
        return Math.Max(1, expected) * PowerMath.One;
    }

    /// <summary>
    /// The body a spawn makes, at depth 1.
    ///
    /// <para>Mutually recursive by construction — an atom's price calls the actor price function,
    /// the same shape as a card game pricing a summon by the body it makes. <b>Depth 1</b> truncates:
    /// a spawned actor's own spawn atoms are priced and then stop. Without that a chain of summoners
    /// prices forever.</para>
    /// </summary>
    static PowerVector SpawnBody(
        IReadOnlyDictionary<string, JsonElement> pars, long conditionalityMilli, PowerTables tables, int depth)
    {
        if (depth >= MaxSpawnDepth) return PowerVector.Zero;

        // Floored at 1 (D3): an omitted count defaulting to 0 prices the whole spawn at zero, which
        // is the defect the body pricing exists to fix.
        var count = Math.Max(1, IntOf(pars, "count") ?? 1);

        var hp = IntOf(pars, "maxHp") ?? IntOf(pars, "hp") ?? 0;
        var atk = IntOf(pars, "atk") ?? 0;
        if (hp == 0 && atk == 0) return PowerVector.Zero;

        var body = ActorPowerCache.PriceBody(hp, atk, tables);
        return body.ScaleMilli(PowerMath.CombineMilli(conditionalityMilli, count * PowerMath.One));
    }

    /// <summary>
    /// The magnitude a price is computed from — the <b>mean</b> of an authored range.
    ///
    /// <para>Variance itself has value and this ignores it, deliberately: an atom rolling 100–200 and
    /// one fixed at 150 are priced alike, which is wrong in the direction that does not matter for a
    /// budget.</para>
    /// </summary>
    public static long MeanMagnitude(
        AtomRow atom, AtomKind kind, IReadOnlyDictionary<string, JsonElement> pars)
    {
        foreach (var def in kind.Params.Defs)
        {
            if (def.Kind != ParamKind.Value) continue;
            if (!pars.TryGetValue(def.Name, out var raw)) continue;
            if (!AtomJson.TryReadValueSpec(raw, out var spec).IsOk) continue;

            return PowerMath.DivRound((long)spec.Min + spec.Max, 2);
        }

        // No magnitude at all — a status application, a board op, a shield whose amount rides the
        // overlay. One reference unit, so it prices as "one of whatever this kind does".
        return 1;
    }

    /// <summary>
    /// Which way the atom moves its channel, before the direction flip. Reads the authored sign,
    /// which <see cref="Price"/> has already taken the absolute value of.
    /// </summary>
    static int SignOf(AtomRow atom, AtomKind kind, IReadOnlyDictionary<string, JsonElement> pars) =>
        Math.Sign(MeanMagnitude(atom, kind, pars));

    // ---- json helpers -----------------------------------------------------------------------------

    internal static Dictionary<string, JsonElement> Read(string? json)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
            foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
        }
        catch (JsonException) { /* E4 already refused this row */ }
        return d;
    }

    static string? StringOf(IReadOnlyDictionary<string, JsonElement> map, string name) =>
        map.TryGetValue(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    static int? IntOf(IReadOnlyDictionary<string, JsonElement> map, string name) =>
        map.TryGetValue(name, out var el) && el.ValueKind == JsonValueKind.Number
        && el.TryGetInt32(out var v) ? v : null;
}
