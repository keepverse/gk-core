using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// Serializes every class that reads or writes <c>LawnDeployLimitsTuningHub</c>, which is
/// process-wide.
///
/// <para><b>Why, from the shape rather than from caution.</b> The unique-deploy admission gate
/// (<c>RpgStore.TryBeginUniqueDeploy</c>) reads that hub on every call — there is deliberately no
/// argument to forget to pass — so a class that moves the hub to prove one branch proves it for
/// every other deploy in the process while it is moved. <c>CorpseCache.LawnAttritionTuningCollection</c>
/// exists for exactly this reason against <c>LawnAttritionTuningHub</c>: a class mutating a
/// process-wide static while sibling classes read it is the shape that made
/// <c>WorldMarchCostProjectionTests</c> fail about one run in five (measured and fixed 2026-09-17).
/// Serialized up front instead of after someone loses an afternoon to it.</para>
///
/// <para><b>Scope of the residual risk, stated rather than implied.</b> This collection serializes
/// its members against each other only. An uncollected sibling that deploys while this collection has
/// the hub configured would also see the cap — bounded by the fact that the gate's limits come from
/// the shipped file (5 per empire / 10 on board) and that the existing sibling lawn-deploy tests put
/// at most three specimens in flight each.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class LawnUniqueDeployLimitsTuningCollection
{
    public const string Name = "LawnUniqueDeployLimitsTuning";
}
