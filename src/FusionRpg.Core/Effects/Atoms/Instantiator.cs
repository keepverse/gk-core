using System.Text;
using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>Where an instance came from. Recorded so a support question has an answer.</summary>
public enum InstanceOrigin
{
    Drop = 0,
    Craft,
    Grant,
    Migration,
}

/// <summary>One atom inside an instance, with its <c>OnInstantiate</c> rolls already frozen.</summary>
/// <param name="ValuesJson">
/// Frozen results. <c>Fixed</c> values are copied verbatim; <c>OnApply</c> values are
/// <b>left unresolved</b> — they belong to the hit, not to the item.
/// </param>
/// <param name="PowerJson">Nullable — E9 owns power, lands later, and backfills.</param>
/// <param name="IdentityDigestHex">
/// <see cref="AtomIdentityDigest.Of"/> of the atom row this seq drew, frozen at roll time (item
/// module 1, R2). Null on a row saved before this migration — an absent digest is treated as
/// compatible rather than retroactively invalidating every already-circulating item.
/// </param>
public sealed record InstanceAtomRow(
    int Seq, string AtomId, string ValuesJson, string? PowerJson = null, string? IdentityDigestHex = null);

/// <summary>A container template turned into a specific owned thing.</summary>
public sealed record InstanceRow
{
    public string InstanceId { get; init; } = "";
    public string ContainerId { get; init; } = "";

    /// <summary>Replays the drop exactly. The whole reason an instance is reproducible.</summary>
    public long RollSeed { get; init; }

    /// <summary>
    /// The catalog the rolls were taken against. Reproducibility is claimed over
    /// <c>(container_id, catalog_revision, roll_seed)</c> — without this column there is nothing to
    /// compare, and a content edit silently changes what an already-owned item means.
    /// </summary>
    public long CatalogRevision { get; init; }

    public string CreatedUtc { get; init; } = "";
    public InstanceOrigin Origin { get; init; } = InstanceOrigin.Drop;
    public IReadOnlyList<InstanceAtomRow> Atoms { get; init; } = Array.Empty<InstanceAtomRow>();

    /// <summary>
    /// content-scale (T3.4): the Θ_content this instance rolled at, and the exact per-mille ratio
    /// applied to every <c>OnInstantiate</c>/<c>Fixed</c> magnitude in <see cref="Atoms"/> — recorded
    /// so any value can be divided back to its relative roll and audited (spec-content-scale.md
    /// §2.2). 1000 = ×1.000, the pin (Θ_content = 20).
    /// </summary>
    public int ThetaContent { get; init; }
    public long ContentScaleMilli { get; init; }

    /// <summary>
    /// The comparison used for reproducibility, deliberately <b>excluding</b> `instance_id` and
    /// `created_utc`, which are generated. Without that exclusion the test could never pass.
    /// <see cref="ThetaContent"/> is included on purpose — a different drop depth is a different
    /// instance for reproducibility purposes, even if the relative roll happens to match.
    /// </summary>
    public string ContentFingerprint()
    {
        var sb = new StringBuilder();
        sb.Append(ContainerId).Append('\n');
        sb.Append(ThetaContent).Append('|').Append(ContentScaleMilli).Append('\n');
        foreach (var a in Atoms)
            sb.Append(a.Seq).Append('|').Append(a.AtomId).Append('|')
              .Append(a.ValuesJson).Append('|').Append(a.PowerJson ?? "").Append('\n');
        return sb.ToString();
    }
}

/// <summary>
/// Roll moment 2: draw the pool, append the fixed core, and freeze every <c>OnInstantiate</c> value.
///
/// <para>Moment 3 (bind) deliberately rolls nothing — if a value would change at equip, it is
/// <c>OnApply</c>, and this never touches it.</para>
/// </summary>
public static class Instantiator
{
    /// <summary>
    /// Instantiate. Re-running with the same <c>(container, catalogRevision, rollSeed, thetaContent)</c>
    /// reproduces the instance byte-identically over <see cref="InstanceRow.ContentFingerprint"/>.
    ///
    /// <para><paramref name="thetaContent"/> and <paramref name="tuning"/> are required, not
    /// defaulted (spec-content-scale.md §2.4: "Absence is a rejection, not a default of 1.0" — a
    /// silent 1.0 is a drop that quietly ignores depth). Making them non-optional parameters is that
    /// rejection: there is no call this compiles without a real Θ_content and a loaded tuning, so
    /// there is no runtime "missing" state left to reject, and no need to grow
    /// <see cref="AtomRejectionReason"/>'s closed, guard-tested 33-member list for a case that C#'s
    /// own type system already forecloses. Every caller that has no depth to supply cannot reach this
    /// method at all — that decision belongs one layer up, at whoever resolves Θ_content from a wave,
    /// expedition or world context.</para>
    /// </summary>
    public static AtomRejection TryInstantiate(
        ContainerRow container,
        Func<string, AtomRow?> lookupAtom,
        Func<string, AffixRow?> lookupAffix,
        long rollSeed,
        int thetaContent,
        FusionRpg.Core.Power.PowerTuning tuning,
        out InstanceRow? instance,
        InstanceOrigin origin = InstanceOrigin.Drop,
        long catalogRevision = 0)
    {
        instance = null;

        var check = ContainerValidator.Validate(container, lookupAtom, lookupAffix);
        if (!check.IsOk) return check;

        // Computed once (spec §2.2: "One call site") — never re-derived per atom, so every value in
        // this instance scales by the exact same ratio.
        var contentScaleMilli = FusionRpg.Core.Power.ContentScale.Milli(thetaContent, tuning);

        var rows = new List<InstanceAtomRow>();

        // The fixed core keeps its authored seq. Determinism comes first, literally: a trait always
        // contains what it says, and the drawn half is appended after it.
        foreach (var entry in container.Atoms.OrderBy(a => a.Seq))
        {
            var atom = lookupAtom(entry.AtomId)!;
            var freeze = Freeze(atom, entry.OverridesJson, rollSeed, entry.Seq, contentScaleMilli, out var valuesJson);
            if (!freeze.IsOk) return freeze;

            rows.Add(new InstanceAtomRow(entry.Seq, entry.AtomId, valuesJson,
                IdentityDigestHex: AtomIdentityDigest.Of(atom)));
        }

        var drawn = Draw(container, lookupAtom, lookupAffix, rollSeed);
        var nextSeq = rows.Count == 0 ? 1 : rows.Max(r => r.Seq) + 1;

        foreach (var atomId in drawn)
        {
            var atom = lookupAtom(atomId)!;
            var freeze = Freeze(atom, null, rollSeed, nextSeq, contentScaleMilli, out var valuesJson);
            if (!freeze.IsOk) return freeze;

            rows.Add(new InstanceAtomRow(nextSeq, atomId, valuesJson,
                IdentityDigestHex: AtomIdentityDigest.Of(atom)));
            nextSeq++;
        }

        instance = new InstanceRow
        {
            InstanceId = "",       // generated by the store; excluded from the fingerprint
            ContainerId = container.ContainerId,
            RollSeed = rollSeed,
            CatalogRevision = catalogRevision,
            Origin = origin,
            Atoms = rows,
            ThetaContent = thetaContent,
            ContentScaleMilli = contentScaleMilli,
        };
        return AtomRejection.Ok;
    }

    /// <summary>
    /// What one budget's draw spent and produced. <see cref="CrossBudgetSpent"/> is the A1 state a
    /// <see cref="AffixClass.Mixed"/> pick creates: the number of the <b>paired</b> budget's rolls this
    /// pass consumed, which the paired pass must subtract from its own count before drawing.
    /// </summary>
    /// <param name="AffixIds">The affix ids drawn, in draw order — what the paired pass excludes so a
    /// <c>Mixed</c> bundle already spent here can never be drawn a second time.</param>
    /// <param name="AtomIds">Those affixes' concrete refs, flat and in bundle <c>seq</c> order.</param>
    public sealed record BudgetDraw(
        IReadOnlyList<string> AffixIds, IReadOnlyList<string> AtomIds, int CrossBudgetSpent)
    {
        public static readonly BudgetDraw Empty =
            new(Array.Empty<string>(), Array.Empty<string>(), 0);
    }

    /// <summary>
    /// Weighted draw, <b>at most one affix per group</b>, <c>prefix_rolls</c> + <c>suffix_rolls</c>
    /// times across two budgets, then each drawn affix expands to its concrete atom id(s) — returned
    /// flat, matching every existing caller's atom-id-list contract.
    ///
    /// <para><b>Public since T31 (action program, A13):</b> "the generator already exists" — a
    /// container's own weighted pool-plus-group-exclusion roll is exactly what the action-seeding
    /// runtime generator needs for "which atoms", and it must not be reinvented. Visibility widened,
    /// no behavior changed at the time.</para>
    ///
    /// <para><b>T3.1 (affix-schema):</b> the pool draws affix ids, not bare atom ids
    /// (`definitions.md` §4a). This method still returns atom ids — expansion happens inside it — so
    /// <c>ActionSeeder</c> and every other existing caller are unaffected. A <b>slot-bearing</b>
    /// bundle still throws: resolving a slot needs a domain member and a tier draw, which is
    /// <see cref="Resolver.Resolve"/>'s five-step order, not this atom-id-list entry point.</para>
    ///
    /// <para><b>T3.2 (prefix/suffix split):</b> the single <c>pool_rolls</c> budget is two —
    /// <c>Prefix</c>-eligible rows (class <see cref="AffixClass.Prefix"/> or <see cref="AffixClass.Mixed"/>)
    /// draw against <c>prefix_rolls</c>; <c>Suffix</c>-eligible rows (<see cref="AffixClass.Suffix"/> or
    /// <see cref="AffixClass.Mixed"/>) draw against <c>suffix_rolls</c> — each with its own
    /// group-exclusion and its own named RNG stream, so one budget's rolls never shift the other's.</para>
    ///
    /// <para><b>⭐ A1 `Mixed` semantics, wired 2026-09-05 (item module 15's follow-up).</b> The two
    /// budgets are no longer <i>independent</i>: state is carried from the prefix pass to the suffix
    /// pass exactly as <see cref="Resolver"/> already does it, so a <see cref="AffixClass.Mixed"/>
    /// affix spends <b>one of each budget simultaneously</b> — never doubling either, never drawn
    /// twice, and never picked at all once the paired budget is exhausted. This replaces the
    /// "interim, honestly-documented simplification" this comment used to describe, which could pick
    /// the same <c>Mixed</c> affix in one pass, both, or neither. <b>A pool with no <c>Mixed</c> affix
    /// draws byte-identically to before</b>: the extra eligibility filter and the paired-pass
    /// exclusion are both no-ops there, and the two RNG stream names are unchanged.</para>
    /// </summary>
    public static List<string> Draw(
        ContainerRow container, Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix,
        long rollSeed)
    {
        var picked = new List<string>();
        if (container.Pool.Count == 0) return picked;

        var prefix = container.PrefixRolls > 0
            ? DrawBudget(container, lookupAtom, lookupAffix, rollSeed, AffixClass.Prefix,
                container.PrefixRolls, crossBudget: container.SuffixRolls)
            : BudgetDraw.Empty;

        // Each `Mixed` affix the prefix pass drew already spent one suffix roll — the suffix pass
        // rolls for what is left, and never for the bundle that spent it.
        var suffixRolls = container.SuffixRolls - prefix.CrossBudgetSpent;
        var suffix = suffixRolls > 0
            ? DrawBudget(container, lookupAtom, lookupAffix, rollSeed, AffixClass.Suffix, suffixRolls,
                excludeAffixIds: new HashSet<string>(prefix.AffixIds, StringComparer.Ordinal))
            : BudgetDraw.Empty;

        picked.AddRange(prefix.AtomIds);
        picked.AddRange(suffix.AtomIds);
        return picked;
    }

    /// <summary>
    /// One budget's weighted draw, one affix per group, on that budget's own named RNG stream.
    ///
    /// <para><b>Public since item module 15 (`enhance-reroll`, spec-enhance-reroll.md §2):</b> a
    /// partial reroll spends <paramref name="count"/> rolls rather than the whole budget, and seeds
    /// <paramref name="excludeGroups"/> with the groups of that budget's <i>retained</i> affixes
    /// (<c>RerollPolicy.RetainedGroups</c> computes exactly that set) — which is what makes
    /// one-per-group survive a partial redraw. <see cref="Draw"/> passes the full counts and no
    /// exclusions, so instantiation is unchanged.</para>
    /// </summary>
    /// <param name="budget">Which budget is being spent — <see cref="AffixClass.Prefix"/> or
    /// <see cref="AffixClass.Suffix"/>. Never <see cref="AffixClass.Mixed"/>: that is an affix's
    /// class, not a budget a container authors.</param>
    /// <param name="count">How many rolls to spend. The whole budget at instantiation; <c>T</c> for a
    /// partial reroll. Structural, not a magnitude — it is bounded by the container's own authored
    /// budget and indexes a loop, so it is an <c>int</c> like <see cref="ContainerRow.PrefixRolls"/>.</param>
    /// <param name="excludeGroups">Groups already spoken for — seeded before the first roll, so a
    /// retained affix's group can never be drawn into again.</param>
    /// <param name="crossBudget">A1: how many of the <b>paired</b> budget's rolls are still available
    /// for a <see cref="AffixClass.Mixed"/> pick to spend. <c>0</c> makes every <c>Mixed</c> row
    /// ineligible for this pass — picking one would spend a roll the container never had.</param>
    /// <param name="excludeAffixIds">A1: affix ids the paired pass already drew. A <c>Mixed</c> bundle
    /// spends one roll of each budget, so it must not be offered again to the second pass.</param>
    public static BudgetDraw DrawBudget(
        ContainerRow container, Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix,
        long rollSeed, AffixClass budget, int count,
        IReadOnlySet<string>? excludeGroups = null,
        int crossBudget = 0,
        IReadOnlySet<string>? excludeAffixIds = null)
    {
        if (budget is not (AffixClass.Prefix or AffixClass.Suffix))
            throw new ArgumentOutOfRangeException(nameof(budget), budget,
                "Mixed is an affix's class, never a budget — a container authors a prefix budget and a suffix budget");
        if (count < 0)
            throw new ArgumentOutOfRangeException(nameof(count), count, "a budget cannot spend a negative number of rolls");
        if (crossBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(crossBudget), crossBudget,
                "the paired budget cannot hold a negative number of rolls");
        if (count == 0) return BudgetDraw.Empty;

        // A stream named for the container AND the budget so the prefix draw and the suffix draw
        // never share a sequence, and the same container always replays identically.
        var rng = new AtomRandom(unchecked((ulong)rollSeed),
            AtomStreams.Pool + "." + StreamNameOf(budget) + "." + container.ContainerId);

        var skipGroups = excludeGroups ?? EmptyIds;
        var skipAffixes = excludeAffixIds ?? EmptyIds;

        var remaining = new List<BudgetCandidate>();
        foreach (var row in container.Pool)
        {
            if (row.Weight <= 0 || skipAffixes.Contains(row.AffixId)) continue;
            var affix = lookupAffix(row.AffixId)!;
            if (!EligibleFor(affix.Class, budget)) continue;
            var group = GroupOf(row, affix, lookupAtom);
            if (skipGroups.Contains(group)) continue;
            remaining.Add(new BudgetCandidate(row, affix, group));
        }

        var affixIds = new List<string>();
        var atomIds = new List<string>();
        var crossSpent = 0;

        for (var roll = 0; roll < count; roll++)
        {
            // A1: once the paired budget is spent, a further `Mixed` pick would spend a roll the
            // container never had, so it drops out of eligibility for the REST of this pass — never
            // re-added, even if nothing else remains. Identical to Resolver.DrawPrefixPass.
            var eligible = crossSpent < crossBudget
                ? remaining
                : remaining.Where(c => c.Affix.Class != AffixClass.Mixed).ToList();
            if (eligible.Count == 0) break;

            var total = eligible.Sum(c => c.Row.Weight);
            var target = rng.NextInclusive(1, total);

            var running = 0;
            var chosen = eligible[^1];
            foreach (var candidate in eligible)
            {
                running += candidate.Row.Weight;
                if (running < target) continue;
                chosen = candidate;
                break;
            }

            affixIds.Add(chosen.Row.AffixId);
            atomIds.AddRange(ExpandConcreteRefs(chosen.Affix));
            if (chosen.Affix.Class == AffixClass.Mixed) crossSpent++;
            // One per group: drop the whole group, not just the row, or a second tier of the same
            // variant could still come up.
            remaining.RemoveAll(c => string.Equals(c.Group, chosen.Group, StringComparison.Ordinal));
        }

        return new BudgetDraw(affixIds, atomIds, crossSpent);
    }

    /// <summary>One pool row with its affix and group pre-resolved — mirrors
    /// <c>Resolver.DrawCandidate</c>, so the weighted pick never re-derives either.</summary>
    readonly record struct BudgetCandidate(ContainerPoolRow Row, AffixRow Affix, string Group);

    static readonly IReadOnlySet<string> EmptyIds = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The stream-name segment for a budget. The two literals are the RNG stream's identity —
    /// changing either re-rolls every already-owned item — so they are structural, never tunable.</summary>
    static string StreamNameOf(AffixClass budget) => budget == AffixClass.Prefix ? "prefix" : "suffix";

    /// <summary>Which budget an affix class draws against — <c>Mixed</c> is eligible in both. A
    /// <c>null</c> class (E32's "not authored, derive it") is eligible for neither: every consumer
    /// downstream of the import path sees an already-resolved class, so a <c>null</c> reaching here is
    /// an unimported row, not a wildcard.</summary>
    static bool EligibleFor(AffixClass? affixClass, AffixClass budget) => budget == AffixClass.Prefix
        ? affixClass is AffixClass.Prefix or AffixClass.Mixed
        : affixClass is AffixClass.Suffix or AffixClass.Mixed;

    /// <summary>Every concrete ref of a bundle, in authoring <c>seq</c> order — a single-ref affix
    /// yields its one atom, a multi-concrete-ref bundle (which is what a <see cref="AffixClass.Mixed"/>
    /// affix always is, since <c>AffixValidator</c> derives <c>Mixed</c> only from refs of two
    /// different kinds) yields all of them together.
    ///
    /// <para>A <b>slot</b> ref still throws. That is not an unbuilt module any more — module 2
    /// (`resolution-order`) landed 2026-09-02 — it is this entry point's own shape: <see cref="Draw"/>
    /// returns bare atom ids and rolls no domain member, no tier and no value, which is exactly what a
    /// slot needs. <see cref="Resolver.Resolve"/> (via <see cref="InstanceProducer.Compose"/>) is the
    /// path for a slot-bearing pool.</para></summary>
    static List<string> ExpandConcreteRefs(AffixRow affix)
    {
        var atomIds = new List<string>(affix.Refs.Count);
        foreach (var r in affix.Refs.OrderBy(r => r.Seq))
        {
            if (r.AtomId is null)
                throw new NotSupportedException(
                    $"affix '{affix.AffixId}' ref {r.Seq} is a slot ('{r.SlotName}' over domain " +
                    $"'{r.SlotDomain}') — resolving it needs a domain member and a tier draw, which " +
                    "Draw() does not roll. Use Resolver.Resolve / InstanceProducer.Compose instead.");
            atomIds.Add(r.AtomId);
        }
        return atomIds;
    }

    /// <summary>Group for a drawn affix: a single-ref affix defaults to that atom's own
    /// <c>(family_id, variant)</c>, matching the pre-affix default exactly; anything else must have
    /// declared an explicit <see cref="ContainerPoolRow.Group"/> — <see cref="ContainerValidator"/>
    /// already refuses to load a container where one didn't.</summary>
    static string GroupOf(ContainerPoolRow row, AffixRow affix, Func<string, AtomRow?> lookupAtom)
    {
        if (!string.IsNullOrWhiteSpace(row.Group)) return row.Group!;
        var atom = lookupAtom(affix.Refs[0].AtomId!)!;
        return atom.FamilyId + "|" + atom.Variant;
    }

    /// <summary>
    /// Resolve every <c>OnInstantiate</c> value spec; copy <c>Fixed</c> ones; leave <c>OnApply</c>
    /// alone. Non-value params pass through untouched.
    ///
    /// <para>content-scale (T3.4) multiplies both <c>OnInstantiate</c> and <c>Fixed</c> results —
    /// both are magnitudes that end up on the item, so both are worth more when the item drops
    /// deeper. <c>OnApply</c> is exempt for an unrelated, pre-existing reason (it belongs to the hit,
    /// not the item) and stays exempt here too — content-scale never touches it.</para>
    ///
    /// <para><c>internal</c> (T3.6, `instance-producer`): <see cref="InstanceProducer"/> reuses this
    /// to freeze a container's FIXED core the same way <see cref="TryInstantiate"/> already does —
    /// <see cref="Resolver"/> owns the pool half (its own five-step order), this owns the core half,
    /// and both must freeze identically or the same instance would carry two different rounding
    /// rules depending on which half an atom came from.</para>
    /// </summary>
    internal static AtomRejection Freeze(
        AtomRow atom, string? overridesJson, long rollSeed, int seq, long contentScaleMilli, out string valuesJson)
    {
        valuesJson = "{}";

        var kind = AtomKindRegistry.Get(atom.KindId);
        if (kind is null) return AtomRejection.Fail(AtomRejectionReason.UnknownKind, atom.KindId);

        var merged = ReadObject(atom.ParamsJson);
        foreach (var (k, v) in ReadObject(overridesJson)) merged[k] = v;

        // Per (atom, seq) so two copies of one atom in a container do not freeze to the same number.
        var rng = new AtomRandom(unchecked((ulong)rollSeed),
            AtomStreams.Pool + ".freeze." + atom.AtomId + "." + seq);

        // D4.26 (party-dungeon, the extend-action-slot grant): a stat.derived atom whose channel is
        // a Count-unit channel (e.g. loadout.slots) is a discrete count, never a magnitude —
        // ContentScale.Apply(1, 4235) is 4, and a slot count must stay 1. Generic to ANY count-unit
        // channel, gated only on stat.derived + the channel's own registered UnitClass — this file
        // carries no branch for any one content module's own concept (Instantiator's own load-bearing
        // rule: it stays data, never grows a branch for a single caller). Read once, outside the
        // per-key loop below, since it depends on this atom's own "channel" param, a SIBLING of
        // whichever key is actually being frozen (the value param, "amount") — not something the
        // loop's own per-key def lookup sees.
        Stats.Derived.UnitClass? channelUnit = null;
        if (string.Equals(atom.KindId, "stat.derived", StringComparison.Ordinal)
            && merged.TryGetValue("channel", out var channelEl) && channelEl.ValueKind == JsonValueKind.String
            && Stats.Derived.DerivedStatRegistry.CreateDefault().TryGet(channelEl.GetString()!, out var channelDef))
        {
            channelUnit = channelDef.Unit;
        }
        var isCountChannel = channelUnit == Stats.Derived.UnitClass.Count;

        var frozen = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, raw) in merged)
        {
            var def = kind.Params.Defs.FirstOrDefault(d =>
                string.Equals(d.Name, key, StringComparison.OrdinalIgnoreCase));

            if (def is null || def.Kind != ParamKind.Value)
            {
                frozen[key] = raw;
                continue;
            }

            var read = AtomJson.TryReadValueSpec(raw, out var spec);
            if (!read.IsOk) return AtomRejection.Fail(read.Reason, $"{atom.AtomId}.{key}: {read.Detail}");

            if (isCountChannel)
            {
                // A count must be a single authored value, never rolled or ranged — there is no
                // sensible "which unscaled value" answer otherwise, so this refuses rather than guess.
                if (spec.Roll != RollPolicy.Fixed || spec.Min != spec.Max)
                    return AtomRejection.ContentRule("atom.count-scaled",
                        $"{atom.AtomId}.{key}: a stat.derived atom on a Count-unit channel must be a single fixed value, never rolled or ranged");
                frozen[key] = spec.Min; // unscaled — a count is not a magnitude
                continue;
            }

            frozen[key] = spec.Roll switch
            {
                RollPolicy.OnInstantiate => FusionRpg.Core.Power.ContentScale.Apply(spec.Resolve(rng), contentScaleMilli),
                RollPolicy.Fixed => FusionRpg.Core.Power.ContentScale.Apply(spec.Min, contentScaleMilli),
                // Left as authored: an OnApply range belongs to the hit, not the item — content-scale
                // never touches it, same reasoning that already exempted it from freezing at all.
                _ => raw,
            };
        }

        valuesJson = JsonSerializer.Serialize(frozen, JsonOpts);
        return AtomRejection.Ok;
    }

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    static Dictionary<string, JsonElement> ReadObject(string? json)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return d;

        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
            foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
        }
        catch (JsonException) { /* already refused upstream */ }

        return d;
    }
}
