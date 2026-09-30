using System;
using System.IO;
using System.Text.Json.Nodes;
using FusionRpg.Core.Battle.Board;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Battle.Board;

/// <summary>
/// combat-ai `stance-wiring` (CAI3.1, spec-stance-wiring.md Outcome 2/3): the two REMOVED keys
/// (`ai.stanceDefault`, `ai.autoResolveHandicapMilli`) may be present or absent, and both file shapes must
/// parse to the SAME record. That equality is what makes their deletion a pure FILE change rather than a
/// behaviour change. Both shapes are real: `siege.v3.json` is the shipped file (the keys gone) and
/// `siege.v2.json` is the older file still on disk (the keys present, and immutable).
///
/// <para>Every document here is built by mutating a SHIPPED file, so the shapes under test are the real
/// ones rather than a hand-written miniature that could drift from the corpus.</para>
/// </summary>
public class SiegeTuningContractTests
{
    static string Shipped() => File.ReadAllText(
        Path.Combine(RepoRoot(), "data", "tuning", "siege.v3.json"));

    /// <summary>The older file shape: the shipped document with the two removed keys put back, at the
    /// values `siege.v2.json` ships. Derived from the shipped file rather than read from `v2` so the two
    /// shapes differ ONLY by those keys.</summary>
    static string WithTheTwoDeadKeys()
    {
        var doc = JsonNode.Parse(Shipped())!;
        doc["ai"]!["stanceDefault"] = "Guard";
        doc["ai"]!["autoResolveHandicapMilli"] = 1000;
        return doc.ToJsonString();
    }

    [Fact]
    public void A_file_with_the_two_dead_keys_and_one_without_parse_to_the_same_record()
    {
        var withKeys = SiegeTuningLoader.Parse(WithTheTwoDeadKeys());
        var withoutKeys = SiegeTuningLoader.Parse(Shipped());

        // The `Ai` record is all-scalar, so its value equality IS the whole claim: the deletion is a file
        // change, not a behaviour change, because nothing reads either field.
        Assert.Equal(withKeys.Ai, withoutKeys.Ai);

        // Whole-`SiegeTuning` equality is deliberately NOT asserted: that record carries dictionaries
        // (`SideByBaseTier`, `TierMultiplierMilli`), and a record's generated `Equals` compares those by
        // reference, so it would fail for a reason that has nothing to do with this change. The scalar
        // neighbours are compared instead, so a change anywhere else would still be caught here.
        Assert.Equal(withKeys.Version, withoutKeys.Version);
        Assert.Equal(withKeys.MaxCells, withoutKeys.MaxCells);
        Assert.Equal(withKeys.Fog, withoutKeys.Fog);
    }

    /// <summary>Absent is legal; WRONG is not. A malformed dead key must still fail loudly rather than be
    /// quietly replaced by the default — otherwise a typo in a file nobody reads would become invisible.</summary>
    [Fact]
    public void A_malformed_dead_key_is_still_rejected()
    {
        var doc = JsonNode.Parse(Shipped())!;
        doc["ai"]!["stanceDefault"] = "Bogus";

        var rejection = Assert.Throws<SiegeTuningRejection>(() => SiegeTuningLoader.Parse(doc.ToJsonString()));
        Assert.Contains("Bogus", rejection.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_integer_handicap_is_still_rejected()
    {
        var doc = JsonNode.Parse(Shipped())!;
        doc["ai"]!["autoResolveHandicapMilli"] = "not a number";

        Assert.Throws<SiegeTuningRejection>(() => SiegeTuningLoader.Parse(doc.ToJsonString()));
    }

    /// <summary>The two GEOMETRY keys are NOT dead — they are candidate inputs — so they stay required:
    /// this is the line between "a file may drop a key nothing reads" and "a file may not drop a key the
    /// scorer needs".</summary>
    [Fact]
    public void The_two_geometry_keys_are_still_required()
    {
        var doc = JsonNode.Parse(Shipped())!;
        doc["ai"]!.AsObject().Remove("threatRadiusCells");

        Assert.Throws<SiegeTuningRejection>(() => SiegeTuningLoader.Parse(doc.ToJsonString()));
    }

    static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        return KeepverseRoots.Core();
    }
}
