namespace FusionRpg.Core.Delve.Domains;

/// <summary>An in-flight delve replaces the rung/tail offer entirely (spec §4: "rungs replaced by
/// `resume: {delveId}`").</summary>
public sealed record DomainResumeDto(long DelveId);

/// <summary>One offered rung — `label` names the RUNG itself (e.g. "Very Hard"); `bandName` names
/// the COMPOSED difficulty this domain resolves to at that rung (e.g. "Abyssal") — two different
/// vocabularies, spec §4's own `{ rungId, label, bandName, oathOffered, permadeath }` shape.</summary>
public sealed record DomainRungOfferDto(string RungId, string Label, string BandName, bool OathOffered, bool Permadeath);

/// <summary>One offered tail step past rung 10 — spec §4's `{ n, label, bandName }`.</summary>
public sealed record DomainTailOfferDto(int N, string Label, string BandName);

/// <summary>One provisioning option the picker can price without composing anything itself
/// (spec-delve-stage.md §18 ask 6, 2026-09-05 — filed against this DTO after the original spec).</summary>
public sealed record ProvisionableOfferDto(string ContainerId, string Label, long Price, int Cells);

/// <summary>
/// D4.19 (spec-domain-catalog.md §4) — the player-facing offer `delve-stage` renders. Names only:
/// no `Θ`, no ordinal, no raw `dangerBand`/`entry` value — `entryKey` is `"standing"` (a `many`
/// domain) or `"single-descent"` (a `once` domain), never the literal engine words.
/// </summary>
public sealed record DomainOfferDto(
    string DomainId, string Name, string Flavor, string Climate, string EntranceLabel, string EntryKey,
    bool Sealed, DomainResumeDto? Resume,
    IReadOnlyList<DomainRungOfferDto> Rungs, IReadOnlyList<DomainTailOfferDto> TailSteps,
    IReadOnlyList<string> RaidModes, string BossName, IReadOnlyList<string> Cleared,
    IReadOnlyList<ProvisionableOfferDto> Provisionable);
