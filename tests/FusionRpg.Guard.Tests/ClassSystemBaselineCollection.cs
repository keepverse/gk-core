using Xunit;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// Serializes the tests that share the class-system baseline files. xUnit runs test CLASSES in
/// parallel by default; without this, the regen test could rewrite a baseline while a sibling
/// test read it (seen 2026-09-12 as a missing `coverage.tuningSync` in
/// <c>ClassSystemBaselineRegenTests.DominanceBaseline_coverageNamesEveryAxisHonestly</c>). Tests in
/// one collection never run concurrently with each other.
/// </summary>
[CollectionDefinition("class-system-baselines", DisableParallelization = true)]
public sealed class ClassSystemBaselineCollection
{
}
