using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.PassiveTree.Binding;

public sealed record ResolvedAtom(string KindId, string ChannelId, string Op, string? Trigger, string? WhenJson, string AtomId, string ParamsJson);

/// <summary>
/// Affix id -> atom rows (task B4, spec-tree-binder.md §1). A node is an affix inside a `skill`
/// container, never a bare atom — an affix is a named bundle of atom refs drawn together, which is
/// why a reflect node (two `stat.derived` atoms that must arrive together) needs an affix as its
/// unit rather than one row per atom. Resolves against the shipped seed content
/// (`AtomSeedFile.Collect`'s own `SeedContent`) — the same source `gk-forge/tools/ElementEnumGen` and every
/// other seed-reading tool already reads, never a second parser.
/// </summary>
public static class AffixComposer
{
    /// <summary>Resolves 1..3 affix ids, in the order given, to their atom refs in `seq` order
    /// within each affix. Refuses (never silently drops) a missing affix id, a missing atom id, or
    /// an atom whose kind is not registered in <see cref="AtomKindRegistry"/> (18 kinds as of D56,
    /// 2026-09-07 — `element.convert` included: the conversion refusal this comment used to describe
    /// was removed the day the kind was actually built, per `spec-element-conversion.md` §2d's own
    /// "clears with zero code change in `tree-binder`" claim — checked directly and found to need one
    /// real line removed, since the old refusal keyed on the STRING "convert", never on the registry,
    /// so it would have kept refusing this kind forever even once registered).</summary>
    public static IReadOnlyList<ResolvedAtom> Resolve(
        IReadOnlyList<string> affixIds,
        IReadOnlyDictionary<string, AffixRow> affixesById,
        IReadOnlyDictionary<string, AtomRow> atomsById)
    {
        if (affixIds.Count is < 1 or > 3)
            throw new BindRefusal($"a node's affixIds must be 1..3, got {affixIds.Count} (R6)");

        var resolved = new List<ResolvedAtom>();
        foreach (var affixId in affixIds)
        {
            if (!affixesById.TryGetValue(affixId, out var affix))
                throw new BindRefusal($"affix '{affixId}' does not exist in the shipped seed content");

            foreach (var reference in affix.Refs.OrderBy(r => r.Seq))
            {
                if (reference.AtomId is null)
                    throw new BindRefusal($"affix '{affixId}' ref seq {reference.Seq} names no atom id " +
                                          "(a slot-pattern ref — not a resolvable atom for a passive node)");
                if (!atomsById.TryGetValue(reference.AtomId, out var atomRow))
                    throw new BindRefusal($"affix '{affixId}' ref seq {reference.Seq} names atom " +
                                          $"'{reference.AtomId}', which does not exist in the shipped seed content");

                resolved.Add(ParseAtom(affixId, atomRow));
            }
        }
        return resolved;
    }

    static ResolvedAtom ParseAtom(string affixId, AtomRow row)
    {
        // D16/D56: `element.convert` is now a real, registered kind (spec-element-conversion.md,
        // built 2026-09-07) -- REMOVED 2026-09-07 the string-keyed "row.KindId.Contains('convert')"
        // refusal this comment used to describe. That check fired on the LITERAL SUBSTRING, never on
        // registry membership, so it would have kept refusing element.convert forever even after
        // registration -- the exact opposite of spec-element-conversion.md §2d's own claim ("clears
        // itself with zero code change... never keys on a hardcoded [count]"). The registry check
        // below is now the ONLY gate, matching that claim for real.
        if (AtomKindRegistry.Get(row.KindId) is null)
            throw new BindRefusal($"affix '{affixId}' atom '{row.AtomId}' has unregistered kind '{row.KindId}'");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(row.ParamsJson); }
        catch (JsonException ex)
        {
            // A malformed params blob is a content defect, not a bind-time crash: every other
            // unreadable input in this class is a named BindRefusal, and a raw JsonException
            // escaping here would kill the whole tree run (the same class of defect P4.1 fixes
            // for the pool shape below).
            throw new BindRefusal($"affix '{affixId}' atom '{row.AtomId}' has unparseable params JSON: {ex.Message}");
        }
        using (doc)
        {
            var root = doc.RootElement;
            var channel = ReadChannelOrRefuse(affixId, row, root);
            var op = root.TryGetProperty("op", out var opEl) ? opEl.GetString() ?? "" : "";

            string? trigger = null;
            if (!string.IsNullOrEmpty(row.WhenJson) && row.WhenJson != "{}")
            {
                using var whenDoc = JsonDocument.Parse(row.WhenJson);
                if (whenDoc.RootElement.TryGetProperty("trigger", out var trigEl))
                    trigger = trigEl.GetString();
            }

            // WhenJson passes through ONLY when the row carries one (status rows with a grant
            // chance today): magnitude rows keep the established null, so committed outputs that
            // never had a `whenJson` stay byte-identical. The chance a status row needs at bind
            // dies at this line if it is dropped — that is exactly the 164-empty-node mechanism.
            var whenJson = !string.IsNullOrEmpty(row.WhenJson) && row.WhenJson != "{}"
                ? row.WhenJson
                : null;

            return new ResolvedAtom(row.KindId, channel, op, trigger, whenJson, row.AtomId, row.ParamsJson);
        }
    }

    /// <summary>
    /// Reads <c>params.channel</c> in either shape E30 defines — a concrete channel string, or a pool
    /// reference object (`spec-channel-pool.md` §3.2) — and REFUSES a pool by name rather than
    /// resolving it here.
    ///
    /// <para><b>Why refuse and not resolve.</b> The pool's draw happens at roll time, and
    /// `spec-channel-pool.md` §4 states plainly that the resolver is not this module's to implement.
    /// Its price is <c>count × weighted_mean(price(member))</c> (§3.4), not any single member's, so
    /// picking one member at bake time would store a number the runtime roll can contradict. Refusing
    /// by name is the honest disposition the plan's own P4.1 acceptance allows ("resolved to a concrete
    /// channel OR refused as a <c>BindRefusal</c> naming the rule").</para>
    ///
    /// <para><b>The defect this replaces.</b> The old line was a bare
    /// <c>chEl.GetString()</c>, which throws <c>InvalidOperationException</c> on an object — not a
    /// <c>BindRefusal</c>, so <c>TreeBinderRun.BindTree</c>'s catch never saw it and the entire run died
    /// (reproduced 2026-09-13: exit <c>-532462766</c> on the first pool-shaped family). Eight generated
    /// <c>stat.derived</c> families carry this shape and the language stage picked them 461 times, so the
    /// crash cost the whole corpus, not one tree.</para>
    /// </summary>
    static string ReadChannelOrRefuse(string affixId, AtomRow row, JsonElement root)
    {
        if (!root.TryGetProperty("channel", out var chEl))
            return "";

        var read = ChannelRefJson.TryRead(chEl, out var channelRef);
        if (!read.IsOk)
            throw new BindRefusal($"affix '{affixId}' atom '{row.AtomId}' has an unreadable 'channel' " +
                                  $"param: {read.Detail}");

        if (channelRef.IsPool)
            throw new BindRefusal($"affix '{affixId}' atom '{row.AtomId}' channel is a pool reference " +
                                  $"('{channelRef.PoolId}', count {channelRef.Count}) — a pool resolves at roll " +
                                  "time (spec-channel-pool.md §3.2/§3.4), so this bake-time binder cannot " +
                                  "price it as one concrete channel");

        return channelRef.Concrete ?? "";
    }
}
