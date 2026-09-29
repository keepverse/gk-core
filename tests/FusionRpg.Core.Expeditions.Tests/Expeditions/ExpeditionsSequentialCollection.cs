using Xunit;

namespace FusionRpg.Core.Tests.Expeditions;

/// <summary>
/// Mega-merge QC 15: the Almanac-window test in <c>ExpeditionResolverTests</c> configures the
/// process-global <c>CreatureRankFloors</c>; xUnit runs test classes in parallel by default, so any
/// wild-band resolution in a sibling class inside that window dies in <c>Max()</c> over the emptied
/// band. One shared non-parallel collection for the project's three classes — the tests are
/// milliseconds each, so serialization costs nothing and the product code stays untouched.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public class ExpeditionsSequentialCollection
{
    public const string Name = "ExpeditionsSequential";
}
