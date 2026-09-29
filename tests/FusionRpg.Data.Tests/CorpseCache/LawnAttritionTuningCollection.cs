using Xunit;

namespace FusionRpg.Data.Tests.CorpseCache;

/// <summary>
/// Serializes every class that reads <c>LawnAttritionTuningHub</c>, which is process-wide.
///
/// <para><b>Why, stated from the incident rather than from caution.</b> `ContractTuningTestBootstrap`
/// installs an always-permadeath default at assembly load so the pre-existing lawn-death tests stay
/// deterministic. <see cref="LawnPermadeathGateTests"/> has to move that hub to prove the gate gates in
/// both directions. A class mutating a process-wide static while sibling classes read it is exactly the
/// shape that made `WorldMarchCostProjectionTests` fail about one run in five — measured and fixed the
/// same day, 2026-09-17 — so this one is serialized up front instead of after someone loses an
/// afternoon to it.</para>
///
/// <para>Deliberately narrow: only the classes that actually observe a lawn death and therefore reach
/// the ladder. Serializing on suspicion costs suite time and proves nothing.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class LawnAttritionTuningCollection
{
    public const string Name = "LawnAttritionTuning";
}
