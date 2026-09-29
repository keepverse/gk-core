namespace FusionRpg.Core.Delve.Wild;

/// <summary>
/// D4.8 (spec-wild-room.md §9, "Refusals") — the wild-room module's own named rule ids, "before any
/// write, never a fallback." Three of the spec's fourteen already live beside the logic that raises
/// them, declared in earlier tasks this same module, and are deliberately NOT redeclared here:
/// <see cref="DelvePriceRules.PriceUndesigned"/> (`delve.price-undesigned`, D3.13/D3.30),
/// <see cref="Battle.Capture.CaptureRefusal.NotLanded"/> (`capture.not-landed`, D4.6), and
/// <see cref="AltarRefusal.BannerUnknown"/> (`altar.banner-unknown`, D4.7). This file holds the
/// remaining eleven — a catalog of the NAMES §9 lists; wiring each one to its own real throw/refuse
/// site is each verb's own module's job (`TalkTree`/`CaptureAction`/`AltarPull`'s own callers,
/// several still unbuilt), not something a bare constants list can do by itself.
/// </summary>
public static class WildRefusal
{
    /// <summary>"before any offer verb is shown" — no free contract slot (D4.2's own `NoFreeSlot`).</summary>
    public const string NoSlot = "wild.no-slot";

    public const string SoulsInsufficient = "wild.souls-insufficient";
    public const string SpiritInsufficient = "wild.spirit-insufficient";
    public const string ContractNotReleasable = "wild.contract-not-releasable";

    /// <summary>"a band not in `disposition.v1.json` — a corpus refusal at import,
    /// `DispositionCatalog.Get` throws." That throw already exists (D4.1, confirmed);
    /// this constant names it for a caller that wants the id rather than the raw exception message.</summary>
    public const string DispositionUnknown = "wild.disposition-unknown";

    /// <summary>"`threaten` at `far-above`, `offer:*` on a capture-only pack, `fight` on a cage" —
    /// three distinct causes, one id: the verb genuinely was not in <see cref="TalkTree.Offered"/>'s
    /// own returned set (D4.2) for this step/eligibility.</summary>
    public const string VerbNotOffered = "wild.verb-not-offered";

    /// <summary>Past `wild.talk.maxSteps` — <see cref="TalkTree.Offered"/> already returns empty
    /// there (D4.2); this names that state for a caller.</summary>
    public const string StepExhausted = "wild.step-exhausted";

    public const string CaptureAboveThreshold = "capture.above-threshold";
    public const string CaptureNoSeal = "capture.no-seal";
    public const string CaptureTargetWithdrawn = "capture.target-withdrawn";

    /// <summary>"v1 sells single pulls, a ten-pull is ask-first" — reachable only if a future caller
    /// exposes `count` as a request field; `AltarPull.TryPull` (D4.7) does not, so this id has no
    /// live throw site yet, named for when one does.</summary>
    public const string AltarCount = "altar.count";
}
