using FusionRpg.Core.Delve.Difficulty;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>One clear, Core's own shape — `FusionRpg.Data.DomainClearJsonRow` (D4.18) is the SQLite
/// storage row; `FusionRpg.Core` cannot reference `FusionRpg.Data` (the DAL boundary runs one way),
/// so the caller (the future endpoint, D4.22) projects the Data row into this one field-for-field.</summary>
public sealed record DomainClearFact(string RungIdOrTailLabel, bool Oath, long DelveId);

/// <summary>One player's one domain's progress, Core's own shape — only what <see cref="DomainOffers.For"/>
/// actually reads (`FoundVia`/`FoundRef`/`Revision` are storage/audit concerns this projection has no
/// use for). See <see cref="DomainClearFact"/>'s own doc comment for why this is a separate type from
/// `FusionRpg.Data.DomainProgressRow` rather than that same type reused.</summary>
public sealed record DomainProgressFact(string DomainId, IReadOnlyList<DomainClearFact> Clears);

/// <summary>
/// Every fact <see cref="DomainOffers.For"/> needs beyond <c>progress</c>/<c>catalog</c>/<c>delves</c>
/// — five caller-supplied functions rather than a live read, the same
/// <see cref="DomainPreflightInputs"/> idiom this module already established for D4.17.
/// <see cref="ComposeRungs"/> wraps <see cref="RungOffer.For"/> with this call's own
/// `PowerTuning`/`DungeonTuning`/`DomainThetaInputs`/`ParentWorldTerms` — four real, per-request
/// parameters `RungOffer.For` itself already demands, none of which a domain-progress projection can
/// fabricate. <see cref="RungLabelFor"/> and <see cref="BossDisplayNameFor"/> have no source anywhere
/// in the codebase today (confirmed: `DifficultyRungDef` carries only `RungId`/`Ordinal`, no display
/// name; no almanac keyed by a creature species id exists, only PVZ's own `(side, type_id)`-keyed
/// `type_almanac`). <see cref="ProvisionableFor"/> is `delve-stage`'s own not-yet-specified pricing
/// (spec-delve-stage.md §18 ask 6, filed 2026-09-05 against this DTO after the original spec, Phase 5
/// entirely unbuilt).
/// </summary>
public sealed record DomainOfferLive(
    IReadOnlyCollection<string> KnownRungIds,
    Func<string, Staleness> StalenessFor,
    Func<DomainRow, PlayerClears, RungOfferSet> ComposeRungs,
    Func<string, string> RungLabelFor,
    Func<string, string> BossDisplayNameFor,
    Func<string, IReadOnlyList<string>> RaidModesForLayout,
    Func<DomainRow, IReadOnlyList<ProvisionableOfferDto>> ProvisionableFor);

/// <summary>
/// D4.19 (spec-domain-catalog.md §4) — the player-facing offer projection. Pure: no store read here,
/// every fact arrives through <paramref name="progress"/>/<paramref name="catalog"/>/<c>delves</c>/
/// <see cref="DomainOfferLive"/>.
/// </summary>
public static class DomainOffers
{
    public const string EntryKeyStanding = "standing";
    public const string EntryKeySingleDescent = "single-descent";

    // RpgStore.Delve.cs's own DelveStates.{Active,Archived} -- FusionRpg.Core cannot reference
    // FusionRpg.Data, so the two literal strings this module reads are named here, verbatim, rather
    // than through that type (the DAL boundary this whole architecture keeps one-directional).
    const string DelveStateActive = "Active";
    const string DelveStateArchived = "Archived";

    public static IReadOnlyList<DomainOfferDto> For(
        IReadOnlyList<DomainProgressFact> progress, DomainCatalog catalog,
        Func<string, (string? State, long? DelveId)> delves, DomainOfferLive live)
    {
        if (progress is null) throw new ArgumentNullException(nameof(progress));
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));
        if (delves is null) throw new ArgumentNullException(nameof(delves));
        if (live is null) throw new ArgumentNullException(nameof(live));

        var offers = new List<DomainOfferDto>();
        foreach (var p in progress.OrderBy(row => row.DomainId, StringComparer.Ordinal))
        {
            if (live.StalenessFor(p.DomainId) == Staleness.Stale) continue; // hidden, not "locked" (spec §4)

            var domain = catalog.Resolve(p.DomainId);
            if (domain is null) continue; // removed from the catalog since discovery -- nothing to render

            var (state, delveId) = delves(p.DomainId);
            var isOnce = string.Equals(domain.Entry, "once", StringComparison.Ordinal);
            var sealedRow = isOnce && string.Equals(state, DelveStateArchived, StringComparison.Ordinal);
            var resume = string.Equals(state, DelveStateActive, StringComparison.Ordinal) && delveId is { } id
                ? new DomainResumeDto(id)
                : null;

            var clears = SplitClears(p.Clears, live.KnownRungIds);

            IReadOnlyList<DomainRungOfferDto> rungs = Array.Empty<DomainRungOfferDto>();
            IReadOnlyList<DomainTailOfferDto> tailSteps = Array.Empty<DomainTailOfferDto>();
            if (resume is null) // "in progress: rungs replaced by resume" (spec §4, verbatim)
            {
                var offer = live.ComposeRungs(domain, clears);
                rungs = offer.Rungs.Where(r => r.Offered)
                    .Select(r => new DomainRungOfferDto(r.RungId, live.RungLabelFor(r.RungId), r.BandName!,
                        OathOffered: !r.IsPermadeath, Permadeath: r.IsPermadeath))
                    .ToList();
                tailSteps = offer.TailSteps.Where(t => t.Offered)
                    .Select(t => new DomainTailOfferDto(t.N, t.Label, t.BandName!))
                    .ToList();
            }

            offers.Add(new DomainOfferDto(
                DomainId: domain.DomainId, Name: domain.Name, Flavor: domain.Flavor, Climate: domain.Climate,
                EntranceLabel: domain.EntranceHint, EntryKey: isOnce ? EntryKeySingleDescent : EntryKeyStanding,
                Sealed: sealedRow, Resume: resume,
                Rungs: rungs, TailSteps: tailSteps,
                RaidModes: live.RaidModesForLayout(domain.LayoutTemplateId),
                BossName: live.BossDisplayNameFor(domain.BossSpeciesRef),
                Cleared: clears.RungIds.OrderBy(r => r, StringComparer.Ordinal).ToList(),
                Provisionable: live.ProvisionableFor(domain)));
        }

        return offers;
    }

    /// <summary>Storage carries one untyped `rungIdOrTailLabel` column per clear (D4.18); a rung id
    /// KNOWN to <see cref="RungOffer"/>'s own table goes to <see cref="PlayerClears.RungIds"/>, and
    /// this module's own convention for a tail step is its bare step number as a string ("1", "2",
    /// ...) — no other code writes this column yet (`DelveStart.Run`/`CloseDelve`'s own wiring is
    /// D4.20/D4.21's task), so nothing existing constrains the shape; named explicitly rather than
    /// guessed silently, and that future writer must follow this same convention.</summary>
    static PlayerClears SplitClears(IReadOnlyList<DomainClearFact> clears, IReadOnlyCollection<string> knownRungIds)
    {
        var rungIds = new HashSet<string>(StringComparer.Ordinal);
        var tailSteps = new HashSet<int>();
        foreach (var c in clears)
        {
            if (knownRungIds.Contains(c.RungIdOrTailLabel)) rungIds.Add(c.RungIdOrTailLabel);
            else if (int.TryParse(c.RungIdOrTailLabel, out var n)) tailSteps.Add(n);
        }
        return new PlayerClears(rungIds, tailSteps);
    }
}
