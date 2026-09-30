using System;
using System.IO;
using System.Text.Json.Nodes;
using FusionRpg.Core.Actions.Ai;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `CAI-F1` (2026-09-23, lane `cai2`): the revision the hosts load is the one that exists, it
/// parses, and the lawn section CAI4.7 owes is in it. `CAI-F1`'s own Verify line asks for exactly this —
/// *"publish a revision and prove a reader picks it up (read the value back through the normal path)"* —
/// so the value is read back through <see cref="CombatAiTuningLoader"/>, the shipped parser, from the file
/// <see cref="CombatAiTuningFiles.Current"/> names. No second reader is invented for the test.
/// </summary>
public class CombatAiTuningRevisionTests
{
    static string RepoTuning(string fileName) => File.ReadAllText(
        Path.Combine(FindRepoRoot(), "data", "tuning", fileName));

    /// <summary>The reader's filename resolves to a real, parseable document — the failure `CAI-F1`
    /// existed to make impossible (a published file with no reader, or a reader at the wrong file).</summary>
    [Fact]
    public void The_revision_the_hosts_load_exists_and_parses()
    {
        var tuning = CombatAiTuningLoader.Parse(RepoTuning(CombatAiTuningFiles.Current));

        Assert.True(tuning.Version >= 1);
        Assert.True(tuning.Profiles.ContainsKey("*/default"));   // the required root row
        Assert.True(tuning.Profiles.ContainsKey("siege/default"));
    }

    /// <summary>CAI4.7's lawn section, read back by name. Four BALANCE keys and their spec seeds; the four
    /// STRUCTURAL values are code consts and must NOT appear here (`tunables-ssot.md` §1).</summary>
    [Fact]
    public void The_lawn_section_carries_the_four_balance_keys_and_their_seeds()
    {
        var doc = JsonNode.Parse(RepoTuning(CombatAiTuningFiles.Current))!;
        var trigger = doc["lawn"]?["trigger"];

        Assert.NotNull(trigger);
        Assert.Equal(7, (int)trigger!["swingsPerDecision"]!);
        Assert.Equal(50, (int)trigger["ticksPerDecision"]!);
        Assert.Equal(10, (int)trigger["postCastLockTicks"]!);
        Assert.Equal("lawn.ai.offset", (string)trigger["offsetStream"]!);

        // The structural four stay consts: a schema field for a value that lives in code is the
        // dead-config shape the spec's own plan correction 2 forbids.
        foreach (var structural in new[] { "carryCasts", "decisionsPerFrame", "castTokens", "castTokenTimeoutTicks" })
            Assert.Null(trigger[structural]);
    }

    /// <summary>
    /// CAI4.8's consumer reads the lawn section through its OWN loader, against the SHIPPED document --
    /// the proof that the keys a host will use are the keys the file carries, not a hand-written fixture.
    /// </summary>
    [Fact]
    public void The_lawn_section_parses_through_the_lawn_loader()
    {
        var lawn = CombatAiLawnTuning.Parse(RepoTuning(CombatAiTuningFiles.Current));

        Assert.Equal(7, lawn.SwingsPerDecision);
        Assert.Equal(50, lawn.TicksPerDecision);
        Assert.Equal(10, lawn.PostCastLockTicks);
        Assert.Equal("lawn.ai.offset", lawn.OffsetStream);
    }

    /// <summary>An absent lawn section is REFUSED rather than defaulted: a host that named the file must
    /// fail at boot rather than run on invented numbers.</summary>
    [Fact]
    public void A_revision_without_a_lawn_section_is_refused()
    {
        Assert.Throws<CombatAiTuningRejection>(() => CombatAiLawnTuning.Parse(RepoTuning("combat-ai.v1.json")));
    }

    /// <summary>The publish was a pure ADDITION: `v2` carries `v1`'s profiles and router unchanged, which
    /// is why the goldens cannot move and why a match pinned to `v1` still replays under `v1`.</summary>
    [Fact]
    public void The_revision_is_a_pure_addition_over_v1()
    {
        var v1 = JsonNode.Parse(RepoTuning("combat-ai.v1.json"))!;
        var current = JsonNode.Parse(RepoTuning(CombatAiTuningFiles.Current))!;

        Assert.True(current["version"]!.GetValue<int>() > v1["version"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(v1["profiles"], current["profiles"]));
        Assert.True(JsonNode.DeepEquals(v1["router"], current["router"]));
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
