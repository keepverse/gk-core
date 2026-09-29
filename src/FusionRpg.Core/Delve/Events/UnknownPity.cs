using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Events;

/// <summary>One party's own unknown-node pity counters (spec-event-deck.md §4; R11: "per party, never
/// per delve" — the caller owns exactly one of these per party and never shares it across parties or
/// delves). `long`, matching `LootPity.cs`'s own `LootPityState` — the closest sibling pity-counter
/// type already shipped in this codebase.</summary>
public readonly record struct UnknownPityState(long MissesCache, long MissesMerchant, long MissesFight)
{
    public static UnknownPityState Empty => new(0, 0, 0);
}

/// <summary>What one `unknown` room resolved to. `ArchetypeId` is populated only when <see
/// cref="UnknownPity.Resolve"/> is given a real <see cref="DomainAnchor"/> to re-pick from; it stays
/// `null` when `Kind` is <see cref="UnknownPity.EventKind"/> (falls through to the ordinary event pool
/// instead, §2-3's own job) or when no domain was supplied — an honest "not wired here yet" gap, never
/// a silent wrong answer.</summary>
public sealed record UnknownResolution(string Kind, string? ArchetypeId, UnknownPityState NextPity);

/// <summary>
/// `event-deck` D3.4 (spec-event-deck.md §4) — an `unknown` room resolves to `cache · merchant ·
/// fight` (the Slay-the-Spire-model base+step pity ladder: one counter per kind, reset on a hit,
/// incremented on every miss, checked in registry order) or, failing all three, falls through to an
/// ordinary event draw on the unknown archetype's own pool (§2-3, not this file's job). Every kind's
/// own counter and step-multiplier stay keyed by the registry's own string id (`cache`/`merchant`/
/// `fight`, `room-kinds.v1.json`'s own `unknown.unknownResolvesTo` order) rather than a private enum
/// duplicating it — the same "no second owner of a name" discipline `DelveStreams.cs` names N13.
/// </summary>
public static class UnknownPity
{
    public const string CacheKind = "cache";
    public const string MerchantKind = "merchant";
    public const string FightKind = "fight";

    /// <summary>The unknown room falls through all three pity checks — resolves to an ordinary event
    /// draw instead (§2-3's own job, not this file's).</summary>
    public const string EventKind = "event";

    /// <summary>Registry order (`room-kinds.v1.json`'s `unknown.unknownResolvesTo`) — spec §4's own
    /// pseudocode iterates literally `for kind in [cache, merchant, fight]`.</summary>
    static readonly IReadOnlyList<string> KindsInRegistryOrder = new[] { CacheKind, MerchantKind, FightKind };

    static long MissesFor(UnknownPityState pity, string kind) => kind switch
    {
        CacheKind => pity.MissesCache,
        MerchantKind => pity.MissesMerchant,
        FightKind => pity.MissesFight,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not an unknown-pity kind"),
    };

    static long StepMultMilliFor(DifficultyRungTuning rung, string kind) => kind switch
    {
        CacheKind => rung.UnknownPityStepMultMilliCache,
        MerchantKind => rung.UnknownPityStepMultMilliMerchant,
        FightKind => rung.UnknownPityStepMultMilliFight,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not an unknown-pity kind"),
    };

    /// <summary>A hit resets exactly the kind that hit; every OTHER kind (whether already rolled-and-
    /// missed this call, or never reached) advances — spec §4, verbatim: "resolve(kind); misses[...] =
    /// 0; other kinds += 1; stop".</summary>
    static UnknownPityState ResetOneAdvanceOthers(UnknownPityState pity, string hitKind) => new(
        MissesCache: hitKind == CacheKind ? 0 : checked(pity.MissesCache + 1),
        MissesMerchant: hitKind == MerchantKind ? 0 : checked(pity.MissesMerchant + 1),
        MissesFight: hitKind == FightKind ? 0 : checked(pity.MissesFight + 1));

    /// <summary>No kind hit this call — every counter advances. Spec §4, verbatim: "else → event...;
    /// every kind += 1".</summary>
    static UnknownPityState AdvanceAll(UnknownPityState pity) => new(
        checked(pity.MissesCache + 1), checked(pity.MissesMerchant + 1), checked(pity.MissesFight + 1));

    /// <summary>
    /// Spec §4's own pseudocode, verbatim: for each kind in registry order, `chance‰ = base + step ×
    /// rung's own step-mult ÷ 1000 × misses`; the room's own <paramref name="seed"/> derives ONE stream
    /// (`DelveStreams.Unknown(row, col)`), drawing at most three `NextPerMille()` off it in sequence —
    /// a hit stops the loop early, so a kind after the hit draws no roll at all this call. `chance‰` is
    /// `long` and MAY pass 1000 (certainty) — a bounded ratio doing its job, not a cap (never clamped).
    /// <paramref name="domain"/> is optional: when supplied, a hit also re-picks a real archetype id
    /// (see <see cref="RepickArchetype"/>); when omitted, `ArchetypeId` stays `null` — no production
    /// caller exists yet to supply a real `DomainAnchor`, the same "zero production callers today, but
    /// provably correct" posture `ConsumableCatalog.Load` already ships from.
    /// </summary>
    public static UnknownResolution Resolve(
        int row, int col, UnknownPityState pity,
        IReadOnlyDictionary<string, UnknownPityTuning> pityTuning, DifficultyRungTuning rung,
        ulong seed, DomainAnchor? domain = null)
    {
        if (pityTuning is null) throw new ArgumentNullException(nameof(pityTuning));
        if (rung is null) throw new ArgumentNullException(nameof(rung));

        var stream = SeededRng.DeriveStream(seed, DelveStreams.Unknown(row, col));
        foreach (var kind in KindsInRegistryOrder)
        {
            if (!pityTuning.TryGetValue(kind, out var t))
                throw new KeyNotFoundException($"nodes.unknown.pity has no entry for '{kind}'");

            var misses = MissesFor(pity, kind);
            var mult = StepMultMilliFor(rung, kind);
            var chanceMilli = checked(t.BaseMilli + (t.StepMilli * mult / 1000) * misses);
            var roll = stream.NextPerMille();
            if (roll < chanceMilli)
            {
                var archetypeId = domain is null ? null : RepickArchetype(domain, kind, row, col, seed);
                return new UnknownResolution(kind, archetypeId, ResetOneAdvanceOthers(pity, kind));
            }
        }

        return new UnknownResolution(EventKind, null, AdvanceAll(pity));
    }

    /// <summary>
    /// §4, verbatim: "re-picks an archetype of that kind from the palette cell `(kind, climate)`" —
    /// delegated to <see cref="PaletteCellPick.Pick"/> (the ONE owner of the idiom, extracted
    /// 2026-09-22 when §5's `encounter` consequence needed the identical shape) on the domain's own
    /// climate: an unknown room is climate-neutral by construction, so it has none of its own. Drawn on
    /// `DelveStreams.Unknown(row, col) + ":archetype"`, seeded off the SAME room seed. An empty cell
    /// throws <see cref="DelveGraphRollRejection"/> — "the roller's own refusal" (spec §4, verbatim).
    /// </summary>
    static string RepickArchetype(DomainAnchor domain, string kind, int row, int col, ulong seed) =>
        PaletteCellPick.Pick(
            domain, kind, domain.Climate, row, col, seed, DelveStreams.Unknown(row, col) + ":archetype");
}
