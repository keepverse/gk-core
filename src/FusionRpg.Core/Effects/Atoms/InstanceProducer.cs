namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// `instance-producer` (T3.6, `spec-instance-producer.md`, ⭐ the payoff): composes a real
/// <see cref="InstanceRow"/> — fixed core frozen (E6), pool half resolved through the affix-aware
/// five-step order (module 2, `Resolver.Resolve`) instead of <see cref="Instantiator.Draw"/>'s
/// single-ref-only path. Every real decision already lives one layer down; this is the wiring.
///
/// <para><b>Core stays free of I/O</b> (the same discipline every module in this program follows) —
/// this composes the row, it does not persist it. The spec's own pseudocode signature takes an
/// `RpgStore` directly and returns a binding id in one call; that cannot compile inside Core (`Data`
/// depends on `Core`, never the other way — verified against both `.csproj` files, not assumed), and
/// <c>BindingRow</c> itself is a <c>FusionRpg.Data</c> type. This module's real shape therefore splits
/// at the layer boundary: this composes the <see cref="InstanceRow"/>; the real caller
/// (<c>RpgStore.ProduceAndBind</c>, `FusionRpg.Data`) wraps it in a <c>BindingRow</c> and persists
/// both atomically. A deliberate, documented deviation from the spec's stated file placement, not an
/// oversight.</para>
/// </summary>
/// <summary>
/// One already-resolved pool pick forced into a <see cref="InstanceProducer.Compose"/> call —
/// creature-standalone WAVE F2.1/F2.4: an atom (or affix-bundle's worth of atoms) lifted VERBATIM from a
/// parent specimen's own materialised roll (F2.2 finds it; this type only carries it), never
/// re-rolled or re-frozen.
///
/// <para><b>Correction, 2026-09-07, caught before F2.4 shipped on top of it:</b> an earlier revision
/// of this type validated <see cref="AffixId"/> against the TARGET container's own pool — but
/// inheritance is inherently cross-species (a sacrifice's own species-passive pool feeding a
/// DIFFERENT output species' roll), and real content confirms every species' pool uses opaque,
/// per-species-authored affix ids with zero overlap between species
/// (`gk-data/packs/fusion/data/seed/creatures/species-effects/plant/pilot-batch.json`: `affix.authored.affix-draw-008`) — a
/// same-pool check would refuse nearly every real inheritance pick, defeating the mechanic's entire
/// point (a fused child is supposed to carry something its OWN species could never roll on its own).
/// <see cref="AffixId"/> is kept only as provenance/logging — which pool member on the SOURCE
/// specimen's own species this pick came from — never validated against the target being composed.
/// Legitimacy comes from where the caller sourced the pick (F2.4: a real specimen's own real
/// materialised roll, via F2.2), not from anything <see cref="InstanceProducer.Compose"/> itself can
/// or should judge.</para>
/// </summary>
public readonly record struct ForcedPoolPick(string AffixId, IReadOnlyList<InstanceAtomRow> Atoms);

public static class InstanceProducer
{
    // WAVE F2.1 (creature-standalone, 2026-09-07): one code with a namespaced payload
    // (item-ideal.md §2b.1), never a second entry in the closed 33-code AtomRejectionReason list.
    static InstanceProducer() => ContentRuleNamespaces.Register("fusion-inherit");

    /// <summary>
    /// Compose one instance: the fixed core frozen exactly as <see cref="Instantiator.TryInstantiate"/>
    /// already does, the pool half drawn through <see cref="Resolver.Resolve"/> instead of
    /// <see cref="Instantiator.Draw"/> — the affix-aware replacement this whole program exists to wire
    /// in. <c>PowerJson</c> stays null on every row: power is backfilled later (E9), never computed on
    /// this path (`effect-pipeline-ideal.md` A3).
    ///
    /// <para><paramref name="forcedPicks"/> (WAVE F2.1/F2.4, creature-mechanism-gaps-ideal.md §3.4):
    /// atoms a fusion output inherits verbatim from a sacrificed parent's own roll, never re-rolled —
    /// deliberately NOT validated against this container's own pool (see
    /// <see cref="ForcedPoolPick"/>'s own 2026-09-07 correction — inheritance is cross-species by
    /// design, and same-pool membership would refuse nearly every real pick). The caller (F2.4) is
    /// where provenance is enforced: only pass picks sourced from a real specimen's own real
    /// materialised roll. The roll budget shrinks by exactly the forced-pick count —
    /// <see cref="ContainerRow.SuffixRolls"/> first, then <see cref="ContainerRow.PrefixRolls"/>, both
    /// clamped at 0, the same operation <see cref="VariantShift.ShiftSuffixRolls"/>/
    /// <see cref="VariantShift.ShiftPrefixRolls"/> already perform for a different reason
    /// (shiny/corrupted variants), reused rather than invented. A forced-pick count exceeding the
    /// container's own total roll budget refuses (<c>fusion-inherit.exceeds-roll-budget</c>) before
    /// any roll happens. <c>null</c>/empty (every caller before this wave, and the normal
    /// species-materialise path) reproduces today's exact output byte-for-byte — the shrink is a
    /// no-op and <see cref="Resolver.Resolve"/> sees the container's own original roll counts.</para>
    /// </summary>
    public static AtomRejection Compose(
        ContainerRow container,
        Func<string, AtomRow?> lookupAtom,
        Func<string, AffixRow?> lookupAffix,
        Func<string, IReadOnlyList<string>> domainMembers,
        long rollSeed,
        int thetaContent,
        FusionRpg.Core.Power.PowerTuning tuning,
        out InstanceRow? instance,
        VariantShift? variant = null,
        InstanceOrigin origin = InstanceOrigin.Drop,
        long catalogRevision = 0,
        Func<string, ChannelPoolRow?>? lookupPool = null,
        IReadOnlyList<ForcedPoolPick>? forcedPicks = null)
    {
        instance = null;

        var check = ContainerValidator.Validate(container, lookupAtom, lookupAffix);
        if (!check.IsOk) return check;

        var picks = forcedPicks ?? Array.Empty<ForcedPoolPick>();
        if (picks.Count > container.PrefixRolls + container.SuffixRolls)
            return AtomRejection.ContentRule("fusion-inherit.exceeds-roll-budget",
                $"{picks.Count} forced pick(s) exceed container '{container.ContainerId}''s own roll " +
                $"budget ({container.PrefixRolls} prefix + {container.SuffixRolls} suffix)");

        // Shrink SuffixRolls first, then PrefixRolls, by exactly the forced-pick count — a named,
        // documented default (no existing rule dictates which side a forced pick "costs," since it
        // never drew from either), not an invented mechanism. The AUTHORED container above (pool,
        // atoms, tiers, groups) stays exactly what was validated; only this cloned copy's two roll
        // counts differ, and only Resolver.Resolve ever sees it.
        var suffixShrink = Math.Min(picks.Count, container.SuffixRolls);
        var prefixShrink = picks.Count - suffixShrink;
        var effectiveContainer = picks.Count == 0
            ? container
            : container with
            {
                SuffixRolls = Math.Max(0, container.SuffixRolls - suffixShrink),
                PrefixRolls = Math.Max(0, container.PrefixRolls - prefixShrink),
            };

        var contentScaleMilli = FusionRpg.Core.Power.ContentScale.Milli(thetaContent, tuning);

        var rows = new List<InstanceAtomRow>();
        foreach (var entry in container.Atoms.OrderBy(a => a.Seq))
        {
            var atom = lookupAtom(entry.AtomId)!;
            var freeze = Instantiator.Freeze(
                atom, entry.OverridesJson, rollSeed, entry.Seq, contentScaleMilli, out var valuesJson);
            if (!freeze.IsOk) return freeze;

            rows.Add(new InstanceAtomRow(entry.Seq, entry.AtomId, valuesJson));
        }

        var nextSeq = rows.Count == 0 ? 1 : rows.Max(r => r.Seq) + 1;

        // Forced picks: already-resolved atoms lifted verbatim from a parent's own roll — copied via
        // `with` so every field (PowerJson, IdentityDigestHex included) survives untouched except the
        // Seq renumbering every instance's own atom list already requires.
        foreach (var pick in picks)
            foreach (var a in pick.Atoms)
            {
                rows.Add(a with { Seq = nextSeq });
                nextSeq++;
            }

        var drawn = Resolver.Resolve(
            effectiveContainer, lookupAtom, lookupAffix, domainMembers, rollSeed, variant, contentScaleMilli, lookupPool);

        foreach (var a in drawn.Atoms)
        {
            rows.Add(new InstanceAtomRow(nextSeq, a.AtomId, a.ValuesJson));
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
}
