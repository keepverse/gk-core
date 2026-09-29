using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>One atom resolved through all five steps — a slot substituted (or not), a tier chosen
/// (or already fixed by a concrete ref), and its magnitudes rolled. <b>Not</b> an
/// <see cref="InstanceAtomRow"/>: instance persistence is `instance-producer`'s job (module 4, T3.6),
/// not this resolver's — <see cref="Resolver.Resolve"/>'s own signature carries no `thetaContent`/
/// `tuning`. <see cref="Resolver.Resolve"/>'s optional <c>contentScaleMilli</c> parameter (T3.6, added
/// without breaking any T3.3 call site — default 1000 = ×1.000, unchanged) lets a caller that HAS
/// already derived the ratio apply it inline at roll time, the same one-call-site discipline
/// `Instantiator.Freeze` already established, rather than re-parsing this record's JSON after the
/// fact to scale it a second time.</summary>
public sealed record ResolvedAtom(string AtomId, string ValuesJson);

/// <summary>The pool half of one container instantiation, resolved through the five-step order.</summary>
public sealed record ResolvedDraw(IReadOnlyList<ResolvedAtom> Atoms);

/// <summary>
/// `resolution-order` (T3.3, `spec-resolution-order.md`): the affix-aware replacement for
/// <see cref="Instantiator.Draw"/>'s single-stream, atom-only draw. Implements the five-step order
/// `definitions.md:204-212` makes normative — <c>slots → affixes → atoms → tiers → values</c> — each
/// step on its <b>own</b> named RNG stream, so a future sixth step never shifts an existing layer's
/// draws (`definitions.md:231-236`).
///
/// <para><see cref="Instantiator.Draw"/> is <b>not deleted</b> — it stays the single-concrete-ref-only
/// entry point every existing caller (<c>ActionSeeder</c>, etc.) already depends on. This is a
/// parallel, affix-aware entry point; wiring it into <c>Instantiator.TryInstantiate</c>/
/// <c>InstanceRow</c> is `instance-producer`'s job (module 4, T3.6), not this module's.</para>
/// </summary>
public static class Resolver
{
    /// <summary>
    /// Resolve one container's pool half. <paramref name="domainMembers"/> mirrors
    /// <see cref="AffixValidator"/>'s own shape — the caller owns the real vocabularies
    /// (<c>ElementRoster</c>, etc.), this resolver stays free of I/O and of a hardcoded domain list.
    /// <paramref name="variant"/> is a resolution <b>parameter</b>, never a container — <c>null</c>
    /// for "normal"/"shiny" (both have zero resolution effect from this resolver's own perspective).
    /// </summary>
    /// <param name="lookupPool">E30 (spec-channel-pool.md §3.2a, decided 2026-09-03): resolves a pool
    /// id to its row, so a pooled <c>channel</c> reference can be drawn on its own named stream
    /// (<c>channel.pool</c>, the fifth). <c>null</c> in every context with no pool catalog to ask, in
    /// which case a pooled-channel atom throws rather than silently freezing its raw pool-object JSON
    /// unread — validation is expected to have refused a pooled reference reaching this resolver
    /// without a caller able to resolve it, so this is a "should never happen" guard, not a
    /// user-facing rejection.</param>
    public static ResolvedDraw Resolve(
        ContainerRow container,
        Func<string, AtomRow?> lookupAtom,
        Func<string, AffixRow?> lookupAffix,
        Func<string, IReadOnlyList<string>> domainMembers,
        long rollSeed,
        VariantShift? variant = null,
        long contentScaleMilli = 1000,
        Func<string, ChannelPoolRow?>? lookupPool = null)
    {
        if (container.Pool.Count == 0) return new ResolvedDraw(Array.Empty<ResolvedAtom>());

        // Step 1 — slots. Resolved for EVERY affix in the WHOLE pool, drawn or not, so the number of
        // slot draws never depends on step 2's outcome — the exact independence the four-stream
        // design exists to guarantee (definitions.md:231-236).
        var slotRng = new AtomRandom(unchecked((ulong)rollSeed), "affix.slot." + container.ContainerId);
        var slots = ResolveSlots(container, lookupAffix, domainMembers, slotRng, variant);

        // Step 2 — affixes. One shared stream for both budgets (unlike Instantiator.Draw's own
        // per-budget streams, T3.2) — this module's contract names exactly four streams total, not
        // six, so the prefix and suffix draws share `affix.draw` and run in a fixed prefix-then-
        // suffix order for reproducibility.
        var affixRng = new AtomRandom(unchecked((ulong)rollSeed), "affix.draw." + container.ContainerId);
        var prefixRolls = variant?.ShiftPrefixRolls(container.PrefixRolls) ?? container.PrefixRolls;
        var suffixRolls = variant?.ShiftSuffixRolls(container.SuffixRolls) ?? container.SuffixRolls;

        // ep-1 / A1 (spec-affix-schema.md, decided 2026-09-03): two passes, state carried between
        // them — a `Mixed` affix drawn in the prefix pass spends ONE of each budget simultaneously
        // (never doubling either), and can never be drawn a second time in the suffix pass. Both
        // passes still consume the single `affix.draw` stream in fixed prefix-then-suffix order, so a
        // pool with no `Mixed` affix rolls byte-identically to before this fix.
        var (prefixDrawn, suffixBudgetAfterPrefix) =
            DrawPrefixPass(container.Pool, lookupAffix, lookupAtom, affixRng, prefixRolls, suffixRolls);
        var suffixDrawn = DrawSuffixPass(
            container.Pool, lookupAffix, lookupAtom, affixRng, suffixBudgetAfterPrefix,
            alreadyDrawn: new HashSet<string>(prefixDrawn, StringComparer.Ordinal));
        var drawn = new List<string>(prefixDrawn.Count + suffixDrawn.Count);
        drawn.AddRange(prefixDrawn);
        drawn.AddRange(suffixDrawn);

        // Step 3 — atoms. Deterministic given steps 1-2's output; no RNG of its own (definitions.md
        // lists it without a stream).
        var expanded = ExpandRefs(drawn, lookupAffix, slots);

        // Step 4 — tiers. Only refs that came from a slot need one (a concrete ref's id already bakes
        // its tier in) — drawing for the others would waste a roll on a choice that was never made.
        var tierRng = new AtomRandom(unchecked((ulong)rollSeed), "affix.tier." + container.ContainerId);
        var (minTier, maxTier) = variant?.ShiftTierWindow(container.MinTier, container.MaxTier)
                                  ?? (container.MinTier, container.MaxTier);
        var tiered = ResolveTiers(expanded, minTier, maxTier, tierRng);

        // Step 5 — values. One shared stream, consumed in the tiered list's own fixed order.
        var valueRng = new AtomRandom(unchecked((ulong)rollSeed), "atom.value." + container.ContainerId);

        // E30 (spec-channel-pool.md §3.2a): the fifth named stream, derived exactly like the other
        // four — a pooled channel's draw never shares a stream with any other layer, so adding it
        // never shifts an existing layer's draws (definitions.md:231-236), and a container with no
        // pooled-channel atom consumes it not at all (it is only ever read inside RollValues, and
        // only for an atom whose channel is actually a pool reference).
        var channelPoolRng = new AtomRandom(unchecked((ulong)rollSeed), "channel.pool." + container.ContainerId);

        return RollValues(tiered, lookupAtom, valueRng, contentScaleMilli, lookupPool, channelPoolRng);
    }

    /// <summary>Every distinct (affix, slot name) pair across the WHOLE pool, resolved once each, in
    /// pool-then-seq order — the draw order must be stable so the same seed reproduces identically.</summary>
    static Dictionary<(string AffixId, string SlotName), string> ResolveSlots(
        ContainerRow container, Func<string, AffixRow?> lookupAffix,
        Func<string, IReadOnlyList<string>> domainMembers, AtomRandom slotRng, VariantShift? variant)
    {
        var resolved = new Dictionary<(string, string), string>();
        var corruptedRerollSpent = false;

        foreach (var poolRow in container.Pool)
        {
            var affix = lookupAffix(poolRow.AffixId);
            if (affix is null) continue; // ContainerValidator already refuses this at load

            foreach (var r in affix.Refs.Where(r => r.IsSlot).OrderBy(r => r.Seq))
            {
                var key = (poolRow.AffixId, r.SlotName!);
                if (resolved.ContainsKey(key)) continue;

                var members = domainMembers(r.SlotDomain!);
                var pick = members[slotRng.NextInclusive(0, members.Count - 1)];

                // `corrupted`: burns a SECOND draw on the first element-domain slot the resolve
                // encounters, keeping the second pick and discarding the first — a literal reroll,
                // spent at most once per resolve regardless of how many element slots exist.
                if (!corruptedRerollSpent && variant?.RerollsOneElementSlot == true
                    && string.Equals(r.SlotDomain, "element", StringComparison.Ordinal))
                {
                    pick = members[slotRng.NextInclusive(0, members.Count - 1)];
                    corruptedRerollSpent = true;
                }

                resolved[key] = pick;
            }
        }

        return resolved;
    }

    /// <summary>One pool row, with its affix and group pre-resolved — the shape both draw passes
    /// share, so the weighted pick itself (<see cref="PickOne"/>) never re-derives either.</summary>
    readonly record struct DrawCandidate(ContainerPoolRow Row, AffixRow Affix, string Group);

    /// <summary>The prefix pass (A1, spec-affix-schema.md): eligible affixes are <c>Prefix</c>, plus
    /// <c>Mixed</c> for as long as <paramref name="suffixBudget"/> is still positive — once it hits
    /// zero, a further `Mixed` pick would spend a suffix roll the container never had, so it drops
    /// out of eligibility for the REST of this pass (never re-added, even if nothing else remains).
    /// Each `Mixed` affix actually drawn decrements the returned budget by exactly one, which the
    /// suffix pass then rolls for — the "one of each, simultaneously" rule.</summary>
    static (List<string> Drawn, int SuffixBudgetRemaining) DrawPrefixPass(
        IReadOnlyList<ContainerPoolRow> pool, Func<string, AffixRow?> lookupAffix,
        Func<string, AtomRow?> lookupAtom, AtomRandom rng, int prefixRolls, int suffixBudget)
    {
        var picked = new List<string>();
        if (prefixRolls <= 0) return (picked, suffixBudget);

        var remaining = pool
            .Where(p => p.Weight > 0)
            .Select(p => new DrawCandidate(p, lookupAffix(p.AffixId)!, GroupOf(p, lookupAffix(p.AffixId)!, lookupAtom)))
            .Where(c => c.Affix.Class is AffixClass.Prefix or AffixClass.Mixed)
            .ToList();

        for (var roll = 0; roll < prefixRolls; roll++)
        {
            var eligible = suffixBudget > 0 ? remaining : remaining.Where(c => c.Affix.Class != AffixClass.Mixed).ToList();
            var chosen = PickOne(eligible, rng);
            if (chosen is null) break;

            picked.Add(chosen.Value.Row.AffixId);
            if (chosen.Value.Affix.Class == AffixClass.Mixed) suffixBudget -= 1;
            remaining.RemoveAll(c => string.Equals(c.Group, chosen.Value.Group, StringComparison.Ordinal));
        }

        return (picked, suffixBudget);
    }

    /// <summary>The suffix pass: eligible affixes are <c>Suffix</c> or <c>Mixed</c>, EXCLUDING every
    /// affix id the prefix pass already drew — the fix for the second half of the double-draw defect
    /// (a `Mixed` affix could otherwise be picked again here, on the same container instance).</summary>
    static List<string> DrawSuffixPass(
        IReadOnlyList<ContainerPoolRow> pool, Func<string, AffixRow?> lookupAffix,
        Func<string, AtomRow?> lookupAtom, AtomRandom rng, int suffixRolls, IReadOnlySet<string> alreadyDrawn)
    {
        var picked = new List<string>();
        if (suffixRolls <= 0) return picked;

        var remaining = pool
            .Where(p => p.Weight > 0 && !alreadyDrawn.Contains(p.AffixId))
            .Select(p => new DrawCandidate(p, lookupAffix(p.AffixId)!, GroupOf(p, lookupAffix(p.AffixId)!, lookupAtom)))
            .Where(c => c.Affix.Class is AffixClass.Suffix or AffixClass.Mixed)
            .ToList();

        for (var roll = 0; roll < suffixRolls; roll++)
        {
            var chosen = PickOne(remaining, rng);
            if (chosen is null) break;

            picked.Add(chosen.Value.Row.AffixId);
            remaining.RemoveAll(c => string.Equals(c.Group, chosen.Value.Group, StringComparison.Ordinal));
        }

        return picked;
    }

    /// <summary>The one weighted pick both passes share — total-weight target, first candidate whose
    /// running sum reaches it. Consumes exactly one <see cref="AtomRandom.NextInclusive"/> draw when
    /// <paramref name="candidates"/> is non-empty, none otherwise (so an exhausted pass never perturbs
    /// the shared stream for a roll that picked nothing).</summary>
    static DrawCandidate? PickOne(IReadOnlyList<DrawCandidate> candidates, AtomRandom rng)
    {
        if (candidates.Count == 0) return null;

        var total = candidates.Sum(c => c.Row.Weight);
        var target = rng.NextInclusive(1, total);

        var running = 0;
        var chosen = candidates[^1];
        foreach (var candidate in candidates)
        {
            running += candidate.Row.Weight;
            if (running < target) continue;
            chosen = candidate;
            break;
        }

        return chosen;
    }

    /// <summary>Group for a pool row's affix — mirrors <c>Instantiator.GroupOf</c> exactly: an explicit
    /// <see cref="ContainerPoolRow.Group"/> wins; a single-concrete-ref affix defaults to that atom's
    /// own <c>(family_id, variant)</c>. <see cref="ContainerValidator"/> already refuses to load a
    /// container where a multi-ref or slot-bearing affix omits an explicit group, so the fallback path
    /// here is only ever reached for the single-concrete-ref case it was written for.</summary>
    static string GroupOf(ContainerPoolRow row, AffixRow affix, Func<string, AtomRow?> lookupAtom)
    {
        if (!string.IsNullOrWhiteSpace(row.Group)) return row.Group!;
        var atom = lookupAtom(affix.Refs[0].AtomId!)!;
        return atom.FamilyId + "|" + atom.Variant;
    }

    /// <summary>One expanded ref, mid-resolution — either already a concrete atom id (its tier is
    /// baked in), or a family/variant pair still waiting on step 4's tier pick.</summary>
    readonly record struct ExpandedRef(string? ConcreteAtomId, string? Family, string? Variant);

    static List<ExpandedRef> ExpandRefs(
        IReadOnlyList<string> drawnAffixIds, Func<string, AffixRow?> lookupAffix,
        IReadOnlyDictionary<(string AffixId, string SlotName), string> slots)
    {
        var expanded = new List<ExpandedRef>();

        foreach (var affixId in drawnAffixIds)
        {
            var affix = lookupAffix(affixId)
                ?? throw new InvalidOperationException(
                    $"drawn affix '{affixId}' is not in the catalog — validation should have caught this");

            foreach (var r in affix.Refs.OrderBy(r => r.Seq))
            {
                if (!r.IsSlot)
                {
                    expanded.Add(new ExpandedRef(ConcreteAtomId: r.AtomId, Family: null, Variant: null));
                    continue;
                }

                var member = slots[(affixId, r.SlotName!)];
                var family = SubstitutePatternFamily(r.SlotAtomPattern!, r.SlotName!);
                expanded.Add(new ExpandedRef(ConcreteAtomId: null, Family: family, Variant: member));
            }
        }

        return expanded;
    }

    /// <summary>Mirrors <c>AffixValidator.SubstitutePattern</c>'s own family-extraction exactly — kept
    /// local rather than widening that method's visibility, since this is the only piece of it this
    /// module needs (the same "kept local" precedent T3.1's <c>AffixClassOfAtom</c> set).</summary>
    static string SubstitutePatternFamily(string pattern, string slotName)
    {
        var placeholder = "$" + slotName;
        var idx = pattern.IndexOf(placeholder, StringComparison.Ordinal);
        return pattern[..idx].TrimEnd('.');
    }

    static List<string> ResolveTiers(
        IReadOnlyList<ExpandedRef> expanded, int? minTier, int? maxTier, AtomRandom tierRng)
    {
        var lo = minTier ?? 1;
        var hi = maxTier ?? 1;

        var atomIds = new List<string>(expanded.Count);
        foreach (var e in expanded)
        {
            if (e.ConcreteAtomId is not null)
            {
                atomIds.Add(e.ConcreteAtomId);
                continue;
            }

            var tier = tierRng.NextInclusive(lo, hi);
            atomIds.Add(AtomRow.DeriveId(e.Family!, e.Variant!, tier));
        }

        return atomIds;
    }

    /// <summary>
    /// Resolves every <c>OnInstantiate</c>/<c>Fixed</c> value spec; leaves <c>OnApply</c> alone —
    /// the same three-roll-moment split <see cref="Instantiator.Freeze"/> uses, scaled by
    /// <paramref name="contentScaleMilli"/> the same way (default 1000 = ×1.000, i.e. unscaled, for
    /// every T3.3 call site that predates T3.6 and never supplies one).
    ///
    /// <para><b>E30 (spec-channel-pool.md §3.2a):</b> an atom whose <c>channel</c> is a pool
    /// reference expands into <paramref name="lookupPool"/>'s resolved <c>count</c> separate
    /// <see cref="ResolvedAtom"/>s here — same <c>atom_id</c>, the SAME one-rolled magnitude on every
    /// copy, a different concrete channel each. Every other param (including <c>amount</c>) is rolled
    /// exactly once, before the channel draw, and shared across every expanded copy — "+15% to all
    /// resistances" is one roll of 15%, not six independent ones.</para>
    /// </summary>
    static ResolvedDraw RollValues(
        IReadOnlyList<string> atomIds, Func<string, AtomRow?> lookupAtom, AtomRandom valueRng,
        long contentScaleMilli, Func<string, ChannelPoolRow?>? lookupPool, AtomRandom channelPoolRng)
    {
        var rows = new List<ResolvedAtom>(atomIds.Count);

        foreach (var atomId in atomIds)
        {
            var atom = lookupAtom(atomId)
                ?? throw new InvalidOperationException(
                    $"resolved atom '{atomId}' is not in the catalog — validation should have caught this");

            var kind = AtomKindRegistry.Get(atom.KindId)
                ?? throw new InvalidOperationException($"'{atom.KindId}' is not a known kind");

            var frozen = new Dictionary<string, object?>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(atom.ParamsJson);
            ChannelRef? pooledChannel = null;

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "channel", StringComparison.OrdinalIgnoreCase)
                    && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var readChannel = ChannelRefJson.TryRead(prop.Value, out var channelRef);
                    if (readChannel.IsOk && channelRef.IsPool)
                    {
                        // Deferred, not frozen inline — resolved once every OTHER param (the
                        // magnitude included) has been rolled, so the pool draw below can stamp the
                        // SAME frozen dict onto `count` copies.
                        pooledChannel = channelRef;
                        continue;
                    }
                }

                var def = kind.Params.Defs.FirstOrDefault(d =>
                    string.Equals(d.Name, prop.Name, StringComparison.OrdinalIgnoreCase));

                if (def is null || def.Kind != ParamKind.Value)
                {
                    frozen[prop.Name] = prop.Value.Clone();
                    continue;
                }

                var read = AtomJson.TryReadValueSpec(prop.Value, out var spec);
                if (!read.IsOk)
                    throw new InvalidOperationException(
                        $"{atomId}.{prop.Name}: {read.Detail} — validation should have caught this");

                frozen[prop.Name] = spec.Roll switch
                {
                    RollPolicy.OnInstantiate => FusionRpg.Core.Power.ContentScale.Apply(spec.Resolve(valueRng), contentScaleMilli),
                    RollPolicy.Fixed => FusionRpg.Core.Power.ContentScale.Apply(spec.Min, contentScaleMilli),
                    _ => prop.Value.Clone(), // OnApply — belongs to the hit, not the item
                };
            }

            if (pooledChannel is not { } cref)
            {
                rows.Add(new ResolvedAtom(atomId, JsonSerializer.Serialize(frozen, JsonOpts)));
                continue;
            }

            if (lookupPool is null)
                throw new InvalidOperationException(
                    $"{atomId}: channel is a pool reference and no pool catalog was supplied to resolve it " +
                    "— validation should have refused this atom reaching the resolver without one");

            var pool = lookupPool(cref.PoolId!)
                ?? throw new InvalidOperationException(
                    $"{atomId}: unknown pool '{cref.PoolId}' — validation should have caught this");

            foreach (var channel in DrawPoolChannels(pool, cref.Count, cref.AllowRepeat, channelPoolRng))
            {
                var copy = new Dictionary<string, object?>(frozen, StringComparer.Ordinal) { ["channel"] = channel };
                rows.Add(new ResolvedAtom(atomId, JsonSerializer.Serialize(copy, JsonOpts)));
            }
        }

        return new ResolvedDraw(rows);
    }

    /// <summary>E30 (spec-channel-pool.md §3.2a): <paramref name="count"/> weighted draws on the
    /// pool's own <c>channel.pool</c> stream — without replacement unless <paramref name="allowRepeat"/>,
    /// the same weighted-pick shape <see cref="PickOne"/> already implements for affix draws, reused
    /// at this layer rather than reimplemented.</summary>
    static List<string> DrawPoolChannels(ChannelPoolRow pool, int count, bool allowRepeat, AtomRandom rng)
    {
        var picked = new List<string>(count);

        if (allowRepeat)
        {
            for (var i = 0; i < count; i++)
                picked.Add(WeightedPickChannel(pool.Members, rng));
            return picked;
        }

        var remaining = new List<ChannelPoolMember>(pool.Members);
        for (var i = 0; i < count && remaining.Count > 0; i++)
        {
            var chosen = WeightedPickChannel(remaining, rng);
            picked.Add(chosen);
            remaining.RemoveAll(m => string.Equals(m.Channel, chosen, StringComparison.Ordinal));
        }
        return picked;
    }

    static string WeightedPickChannel(IReadOnlyList<ChannelPoolMember> members, AtomRandom rng)
    {
        var total = members.Sum(m => m.WeightMilli);
        var target = rng.NextInclusive(1, total);

        var running = 0;
        var chosen = members[^1];
        foreach (var m in members)
        {
            running += m.WeightMilli;
            if (running < target) continue;
            chosen = m;
            break;
        }
        return chosen.Channel;
    }

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
}
