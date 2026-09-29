namespace FusionRpg.Core.Delve.Domains;

/// <summary>One refusal — names the domain, the rule and the detail (spec-domain-catalog.md §8,
/// verbatim: "every refusal is... naming `domainId` and the rule"). A plain record, not an exception:
/// <see cref="DomainPreflight"/> collects every row's own refusals in one pass rather than stopping at
/// the first (spec §2's own table runs "every domain is checked"); <see cref="DelveStart"/> (D4.21)
/// reuses the same shape for its own seven ordered refusal groups — one refusal type for both
/// producers rather than two near-identical records drifting apart.</summary>
public sealed record DomainRefusal(string DomainId, string Rule, string Detail);
