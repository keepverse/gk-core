namespace FusionRpg.Core.Actions.Rungs;

/// <summary>
/// ST4.1 (`spec-budget-calibration-report.md` contract 2): one action's real price, as the calibration
/// reads it. <see cref="RealizedPowerMilli"/> is the SAME figure A-G1's budget check compares against
/// the rung's <c>powerBudgetMilli</c> — <c>ActorPowerCache.Compose</c> over the container's fixed atoms
/// — so the report calibrates the real check rather than a second pricing of its own (contract 1: one
/// pricing path, SOLID S).
///
/// <para><see cref="AuthoredRung"/> is the action's own <c>Rung</c> column, which is the rung the check
/// reads. It is deliberately NOT a holder's effective rung: the budget is a property of the content,
/// while whose ladder a cost is paid from is ST2's concern.</para>
///
/// <para><see cref="UnpricedAtomIds"/> is ST4.5e: the container atoms <c>ComposeWithFindings</c> could
/// NOT price, named rather than folded into <see cref="RealizedPowerMilli"/> as a zero. It is
/// <c>null</c> when the reader did not report any — a caller that does not ask for findings (or a
/// fixture) — and a EMPTY list is the stronger statement that it asked and found none.</para>
/// </summary>
public readonly record struct PricedAction(
    string ActionId,
    int AuthoredRung,
    long RealizedPowerMilli,
    IReadOnlyList<string>? UnpricedAtomIds = null);
