namespace FusionRpg.Core.Delve.Quests;

/// <summary>
/// D4.13 (spec-delve-quests.md §Testing strategy, "Metrics (G4 input)") — the buildable slice.
/// `QuestCoverage.Report(domain, rung, seeds)` — actually simulating 32 solo-autopilot delves per
/// shipped domain to measure "every pool row offered ≥ 1 time" and "autopilot completion ‰ per
/// template" — needs a full solo-delve-on-autopilot harness, which CHECKPOINT G3's own re-scoped
/// status (this session, after Phase 3 closed) already names as the one still-genuinely-blocked line:
/// the underlying modules exist, but no orchestrator sequences rooms→events→loot→pack→extraction end
/// to end yet (`delve-stage`'s own job, Phase 5, unbuilt). This file instead owns the one PURE,
/// already-testable piece the acceptance line names directly: the band check itself.
/// </summary>
public static class QuestCoverage
{
    /// <summary>
    /// D4.13's own acceptance line, verbatim: "`quests.autopilotCompletionBand` is a regression band,
    /// never a target." A regression band is a TWO-SIDED range a balance pass keeps content inside
    /// (moving `countBand` ordinals on anchors, never through code) — unlike a target, which would
    /// demand one specific value, a band only flags DRIFT in either direction: a template that got
    /// quietly harder (below `minMilli`) OR one a content edit made trivial (above `maxMilli`) are
    /// both worth a human look, not just the low side.
    /// </summary>
    public static bool WithinRegressionBand(long completionMilli, long minMilli, long maxMilli)
    {
        if (minMilli > maxMilli) throw new ArgumentException($"minMilli ({minMilli}) must not exceed maxMilli ({maxMilli})", nameof(minMilli));
        return completionMilli >= minMilli && completionMilli <= maxMilli;
    }
}
