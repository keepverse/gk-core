using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.3/D3.4 (spec-event-deck.md §4, §5) — the ONE owner of the "re-pick an archetype
/// from the palette cell `(kind, climate)`" idiom, extracted 2026-09-22. Two callers need it: §4's
/// unknown-node pity (<see cref="UnknownPity"/>'s own `:archetype` re-pick) and §5's `encounter`
/// consequence (<see cref="EventDeck"/>'s own `:encounter` re-pick). Two copies of the same filter-then-
/// pick would be the N13 "same name/shape under a second owner" defect this module already paid for once
/// (`EventDraw.RootStream`, deleted in favour of `DelveStreams.Event`), so the shared shape lives here.
///
/// <para><b>The idiom is `DelveGraphRoll.cs`'s own archetype step, mirrored rather than re-derived</b>
/// (spec §4's own words: "the exact filter-then-pick idiom `DelveGraphRoll.cs`'s own archetype step
/// already ships"): a climate-neutral kind (`RoomKindDef.ClimateNeutral`, e.g. `merchant`) admits any
/// archetype of that kind regardless of climate; every other kind requires an exact climate match.
/// Uniform weight — no per-archetype weight column exists anywhere, matching the roller's own comment
/// verbatim. An empty cell throws <see cref="DelveGraphRollRejection"/>, "the roller's own refusal"
/// (spec §4, verbatim), reusing `delve-graph-roll`'s own exception rather than minting a second one.</para>
///
/// <para><b>The climate is a caller-supplied parameter, never read off the domain here</b> — that is
/// the one thing the two callers genuinely disagree about, and hiding it would decide a real design
/// question in the wrong place. §4's pity path passes the DOMAIN's own climate (the unknown room is
/// climate-neutral by construction, so it has none of its own). §5's encounter path passes the ROOM's
/// own climate when it has one and the domain's when it does not — the palette cell is keyed on the
/// room actually in play (spec §2 rule 1's own `(kind, climate)` cell), and a climate-neutral room
/// would otherwise have no cell at all.</para>
/// </summary>
public static class PaletteCellPick
{
    /// <param name="domain">The domain whose `roomPalette` is the candidate set.</param>
    /// <param name="kind">The room-archetype kind to re-pick (`cache`/`merchant`/`fight` for §4;
    /// `fight` for §5's encounter consequence).</param>
    /// <param name="climate">The climate the cell is keyed on — see this type's own doc comment for
    /// which caller passes which.</param>
    /// <param name="streamName">The fully-qualified stream the pick draws on — always built from
    /// <see cref="DelveStreams"/> by the caller, never a private copy (N13).</param>
    public static string Pick(
        DomainAnchor domain, string kind, ElementTypeId? climate, int row, int col, ulong seed, string streamName)
    {
        if (domain is null) throw new ArgumentNullException(nameof(domain));
        if (string.IsNullOrWhiteSpace(kind)) throw new ArgumentException("kind required", nameof(kind));
        if (string.IsNullOrWhiteSpace(streamName)) throw new ArgumentException("streamName required", nameof(streamName));

        var kindDef = RoomKindCatalog.Get(kind);
        var candidates = domain.RoomPalette
            .Where(rp => rp.Kind == kind && (kindDef.ClimateNeutral || rp.Climate == climate))
            .ToList();
        if (candidates.Count == 0)
            throw new DelveGraphRollRejection(
                $"Domain '{domain.DomainId}' has no room palette entry for (kind='{kind}', climate='{climate}').");

        var options = candidates.Select(rp => new WeightedOption<string>(rp.RoomId, 1)).ToList();
        var rollSeed = unchecked((long)SeededRng.DeriveStream(seed, streamName).NextULong());
        return WeightedChoice.Pick(options, rollSeed, streamName);
    }
}
