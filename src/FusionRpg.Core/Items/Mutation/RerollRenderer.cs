using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// The reroll DRAW, owned by the policy's own module (species-gear-chain T15,
/// spec-craft-executor-completion.md §"Reroll needs NO closed-enum amendment"): pure, seeded,
/// no store, no file I/O. `RerollPolicy` validates; this renders — the split keeps the refusal
/// rules testable without drawing and the draws testable without a database.
///
/// <para>Stream discipline: the op seed drives everything, and every draw's stream position follows
/// the seq it REPLACES (a redraw replays from the replaced row's stream slot), so draws never
/// collide with each other and a retry replays identically. Per-budget seeds derive from the op seed
/// in the `reroll:*` namespace — never the instantiate streams, so a reroll can never replay a drop.</para>
/// </summary>
public static class RerollRenderer
{
    /// <summary>The affix list a reroll reads, derived from the instance's own atoms: one single-atom
    /// affix per row (the same 1:1 wrap the mint path draws through), group defaulted to the atom's
    /// own `(family_id, variant)` exactly as `Instantiator.GroupOf` defaults an undeclared pool group.
    /// An atom row the catalog no longer carries is a content gap, refused by name — a catalog
    /// revision can produce it for an already-owned item, and redrawing a bundle the renderer cannot
    /// read would fabricate values.</summary>
    public static (AtomRejection Rejection, IReadOnlyList<DrawnAffix> Drawn) DrawnFrom(
        InstanceRow instance, Func<string, AtomRow?> lookupAtom)
    {
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));

        var drawn = new List<DrawnAffix>();
        foreach (var row in instance.Atoms)
        {
            var atom = lookupAtom(row.AtomId);
            if (atom is null)
                return (MutationRules.Violated("reroll.atom-unknown",
                    $"atom '{row.AtomId}' at seq {row.Seq} is not in the atom catalog — " +
                    "a reroll cannot redraw a row it cannot read"), drawn);
            var affix = AffixLibraryGenerator.SingleAtomAffix(atom);
            drawn.Add(new DrawnAffix(row.Seq, affix.AffixId,
                atom.FamilyId + "|" + atom.Variant,
                affix.Class ?? AffixClass.Prefix, atom.Tier));
        }
        return (AtomRejection.Ok, drawn);
    }

    /// <summary>Reroll-one: the SAME affix id re-frozen with the op seed (`MutationOpKind.RerollValue`).
    /// The value redraws inside its own range; the identity never moves.</summary>
    public static (AtomRejection Rejection, IReadOnlyList<int> Suppressed, IReadOnlyList<AtomAppend> Appended) RenderOne(
        ContainerRow container, IReadOnlyList<DrawnAffix> drawn, int targetSeq, long opSeed, long contentScaleMilli,
        Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix)
    {
        var picked = drawn.Where(a => a.Seq == targetSeq).ToList();
        if (picked.Count == 0)
            return (MutationRules.Violated("reroll.unknown-target",
                $"seq {targetSeq} is not one of this item's affixes"), Array.Empty<int>(), Array.Empty<AtomAppend>());
        var targets = RerollPolicy.TargetsFor(container, drawn, new[] { targetSeq });
        var valid = RerollPolicy.ValidateTargets(targets);
        if (!valid.IsOk) return (valid, Array.Empty<int>(), Array.Empty<AtomAppend>());
        var rerollable = RerollPolicy.ValidateRerollable(picked, lookupAffix);
        if (!rerollable.IsOk) return (rerollable, Array.Empty<int>(), Array.Empty<AtomAppend>());

        // The atom behind the affix, read off the affix's own refs — never derived by string
        // surgery on the id. A multi-atom bundle has no single value to redraw (which atom's range?),
        // so it refuses with its own rule rather than picking one silently.
        var affix = lookupAffix(picked[0].AffixId);
        var atomIds = affix?.Refs.Where(r => r.AtomId is { Length: > 0 }).Select(r => r.AtomId!).ToList()
            ?? new List<string>();
        if (atomIds.Count != 1)
            return (MutationRules.Violated("reroll.bundle-value-undefined",
                $"affix '{picked[0].AffixId}' wraps {atomIds.Count} atoms — a value reroll redraws one " +
                "atom's range, and a bundle has no single one"), Array.Empty<int>(), Array.Empty<AtomAppend>());
        var atom = lookupAtom(atomIds[0]);
        if (atom is null)
            return (MutationRules.Violated("reroll.atom-unknown",
                $"atom '{atomIds[0]}' is not in the atom catalog"), Array.Empty<int>(), Array.Empty<AtomAppend>());

        var frozen = FreezeToValues(atom, opSeed, targetSeq, contentScaleMilli);
        if (!frozen.Rejection.IsOk) return (frozen.Rejection, Array.Empty<int>(), Array.Empty<AtomAppend>());
        return (AtomRejection.Ok, new[] { targetSeq },
            new[] { new AtomAppend(0, atom.AtomId, frozen.Values) });
    }

    /// <summary>Reroll-all: fresh identities per budget (`MutationOpKind.RerollAffix`), mirroring
    /// `Instantiator.Draw`'s own two-pass shape — the prefix pass spends `TargetPrefix` rolls with the
    /// retained groups excluded, the suffix pass spends `TargetSuffix` excluding both the retained
    /// groups and the prefix's new picks — then `ValidatePostOp` proves the result is an item the
    /// generator could have dropped.</summary>
    public static (AtomRejection Rejection, IReadOnlyList<int> Suppressed, IReadOnlyList<AtomAppend> Appended) RenderAll(
        ContainerRow container, IReadOnlyList<DrawnAffix> drawn, IReadOnlyList<int> targetSeqs, long opSeed,
        long contentScaleMilli, Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix)
    {
        var wanted = new HashSet<int>(targetSeqs);
        var targets = RerollPolicy.TargetsFor(container, drawn, wanted);
        var valid = RerollPolicy.ValidateTargets(targets);
        if (!valid.IsOk) return (valid, Array.Empty<int>(), Array.Empty<AtomAppend>());
        var picked = drawn.Where(a => wanted.Contains(a.Seq)).ToList();
        if (picked.Count != wanted.Count)
            return (MutationRules.Violated("reroll.unknown-target",
                "a named seq is not one of this item's affixes"), Array.Empty<int>(), Array.Empty<AtomAppend>());
        var rerollable = RerollPolicy.ValidateRerollable(picked, lookupAffix);
        if (!rerollable.IsOk) return (rerollable, Array.Empty<int>(), Array.Empty<AtomAppend>());

        var prefix = Instantiator.DrawBudget(container, lookupAtom, lookupAffix, DeriveSeed(opSeed, "reroll:prefix"),
            AffixClass.Prefix, targets.TargetPrefix,
            excludeGroups: RerollPolicy.RetainedGroups(drawn, wanted, AffixClass.Prefix),
            crossBudget: container.SuffixRolls);
        var suffix = Instantiator.DrawBudget(container, lookupAtom, lookupAffix, DeriveSeed(opSeed, "reroll:suffix"),
            AffixClass.Suffix, targets.TargetSuffix,
            excludeGroups: RerollPolicy.RetainedGroups(drawn, wanted, AffixClass.Suffix),
            excludeAffixIds: new HashSet<string>(prefix.AffixIds, StringComparer.Ordinal));

        // Stream positions follow the replaced seqs (sorted old ↔ sorted new, zipped): distinct and
        // deterministic, replay-identical on retry.
        var replaced = picked.Select(a => a.Seq).OrderBy(s => s).ToList();
        var fresh = prefix.AtomIds.Concat(suffix.AtomIds).OrderBy(id => id, StringComparer.Ordinal).ToList();
        var appended = new List<AtomAppend>();
        for (var i = 0; i < fresh.Count; i++)
        {
            var atom = lookupAtom(fresh[i])!;
            var frozen = FreezeToValues(atom, opSeed, replaced[i % replaced.Count], contentScaleMilli);
            if (!frozen.Rejection.IsOk) return (frozen.Rejection, Array.Empty<int>(), Array.Empty<AtomAppend>());
            appended.Add(new AtomAppend(0, fresh[i], frozen.Values));
        }

        var post = PostOpProvesGeneratable(container, drawn, wanted, fresh, lookupAtom);
        if (!post.IsOk) return (post, Array.Empty<int>(), Array.Empty<AtomAppend>());
        return (AtomRejection.Ok, picked.Select(a => a.Seq).ToList(), appended);
    }

    internal static (AtomRejection Rejection, IReadOnlyDictionary<string, long> Values) FreezeToValues(
        AtomRow atom, long rollSeed, int seq, long contentScaleMilli)
    {
        var rejection = Instantiator.Freeze(atom, null, rollSeed, seq, contentScaleMilli, out var valuesJson);
        if (!rejection.IsOk) return (rejection, new Dictionary<string, long>(StringComparer.Ordinal));
        using var doc = JsonDocument.Parse(valuesJson);
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt64(out var v))
                values[prop.Name] = v;
        return (AtomRejection.Ok, values);
    }

    static AtomRejection PostOpProvesGeneratable(
        ContainerRow container, IReadOnlyList<DrawnAffix> drawn, ISet<int> wanted,
        IReadOnlyList<string> fresh, Func<string, AtomRow?> lookupAtom)
    {
        var next = drawn.Where(a => !wanted.Contains(a.Seq)).ToList();
        foreach (var id in fresh)
        {
            var atom = lookupAtom(id)!;
            var affix = AffixLibraryGenerator.SingleAtomAffix(atom);
            next.Add(new DrawnAffix(-1, affix.AffixId, atom.FamilyId + "|" + atom.Variant,
                affix.Class ?? AffixClass.Prefix, atom.Tier));
        }
        return RerollPolicy.ValidatePostOp(container, next);
    }

    /// <summary>FNV-1a 64-bit over the op seed in a reroll-namespaced scope — deterministic across
    /// processes, unlike `string.GetHashCode`. Separate scopes per budget so the two passes never
    /// share a sequence.</summary>
    internal static long DeriveSeed(long opSeed, string scope)
    {
        unchecked
        {
            var h = 14695981039346656037UL ^ (ulong)opSeed;
            foreach (var ch in scope)
            {
                h ^= (ulong)ch;
                h *= 1099511628211UL;
            }
            return unchecked((long)h);
        }
    }
}
