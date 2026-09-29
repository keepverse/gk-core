using FusionRpg.Core.Actions;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>
/// The one stance fake in this project, promoted out of <c>ActionUsabilityEvaluatorTests</c>'s private
/// nesting by combat-ai `stance-wiring` (CAI3.1) so the run-state seam test can reuse it rather than write
/// a second one — the row's own words ("reuse the existing <c>FixedStance</c> fake, do not write a second
/// one"). It answers the SAME <see cref="UsabilityResult"/> for every (actor, action) pair, which is what
/// makes "the seam is consulted" observable: the result cannot depend on anything but the check itself.
/// </summary>
sealed class FixedStance : IStanceCheck
{
    readonly UsabilityResult _result;
    public FixedStance(UsabilityResult result) => _result = result;
    public UsabilityResult? Check(string actorKey, string actionId) => _result;
}
