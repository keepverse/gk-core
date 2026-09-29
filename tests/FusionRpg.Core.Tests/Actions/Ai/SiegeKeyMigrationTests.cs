using System;
using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Actions.Ai;
using FusionRpg.Core.Battle.Board;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `profile-schema` (module 2, CAI1.8, spec-profile-schema.md §6/§7, H7): the migration-
/// fidelity contract. The module's whole claim is "the same numbers at a new address" — these tests
/// read the REAL committed files, not a fixture, because the claim is about what actually shipped.
/// </summary>
public class SiegeKeyMigrationTests
{
    static string RepoRoot([CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static string ReadTuning(string fileName) => File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", fileName));

    [Fact]
    public void The_ten_migrated_values_match_siege_v1()
    {
        var tuning = CombatAiTuningLoader.Parse(ReadTuning("combat-ai.v1.json"));
        var siege = tuning.Profiles["siege/default"];

        // 70/50/15/10/10/1/120/2/32/0 -- the shipped siege.v1.json ai block, unchanged at a new address.
        Assert.Equal(70, siege.Scoring.WeightHitChance);
        Assert.Equal(50, siege.Scoring.WeightObjective);
        Assert.Equal(15, siege.Scoring.WeightKill);
        Assert.Equal(10, siege.Scoring.WeightLowHp);
        Assert.Equal(10, siege.Scoring.WeightCannotCounter);
        Assert.Equal(1, siege.Scoring.WeightRound);
        Assert.Equal(120, siege.Scoring.WeightRisk);
        Assert.Equal(2, siege.Scoring.AggressionRange);
        Assert.Equal(32, siege.Scoring.MaxCandidatesScored);
        Assert.Equal(0L, siege.AntiRepeat.RetargetLatencyTicks);
    }

    [Fact]
    public void Siege_v2_no_longer_carries_the_ten()
    {
        var siegeV2 = SiegeTuningLoader.Parse(ReadTuning("siege.v2.json"));
        // The narrowed AiTuning (4 members) has no field for any of the ten -- this compiles only
        // because they are gone. A source-text check confirms the JSON itself carries none of them.
        var text = ReadTuning("siege.v2.json");
        foreach (var deadKey in new[]
                 {
                     "\"weightHitChance\"", "\"weightObjective\"", "\"weightKill\"", "\"weightLowHp\"",
                     "\"weightCannotCounter\"", "\"weightRound\"", "\"weightRisk\"",
                     "\"aggressionRange\"", "\"maxCandidatesScored\"", "\"retargetLatencyTicks\"",
                 })
            Assert.DoesNotContain(deadKey, text, StringComparison.Ordinal);

        Assert.NotNull(siegeV2); // parses successfully with the narrowed reader
    }

    [Fact]
    public void Siege_v2_still_carries_the_two_geometry_keys()
    {
        var siegeV2 = SiegeTuningLoader.Parse(ReadTuning("siege.v2.json"));
        Assert.Equal(20, siegeV2.Ai.ObjectiveReferenceDistanceCells);
        Assert.Equal(4, siegeV2.Ai.ThreatRadiusCells);
    }

    /// <summary>CAI3.1 (stance-wiring, module 11): the two dead keys are DELETED as of `siege.v3.json`,
    /// and the reader switched in the same commit (H7) — so this asserts the shipped file, not the
    /// record. `siege.v2.json` stays on disk (published versions are immutable) and still parses to the
    /// same `Ai` record, which is what makes the deletion a file change rather than a behaviour change.</summary>
    [Fact]
    public void Siege_v3_is_the_shipped_file_and_no_longer_carries_the_two_dead_keys()
    {
        var v3Text = ReadTuning("siege.v3.json");
        foreach (var deadKey in new[] { "\"stanceDefault\"", "\"autoResolveHandicapMilli\"" })
            Assert.DoesNotContain(deadKey, v3Text, StringComparison.Ordinal);

        var v3 = SiegeTuningLoader.Parse(v3Text);
        var v2 = SiegeTuningLoader.Parse(ReadTuning("siege.v2.json"));

        // The deletion is a file change, not a behaviour change: nothing read either key.
        Assert.Equal(v2.Ai, v3.Ai);
        Assert.Equal(20, v3.Ai.ObjectiveReferenceDistanceCells);
        Assert.Equal(4, v3.Ai.ThreatRadiusCells);
    }

    [Fact]
    public void Profile_defaults_are_the_identity_set()
    {
        var tuning = CombatAiTuningLoader.Parse(ReadTuning("combat-ai.v1.json"));
        foreach (var profile in new[] { tuning.Profiles["*/default"], tuning.Profiles["siege/default"] })
        {
            Assert.Equal(SelectionMode.Argmax, profile.Selection.Mode);
            Assert.Equal(1000, profile.Selection.KeepPctMilli);
            Assert.Empty(profile.Reserves);
            Assert.Equal(0, profile.AntiRepeat.CommitmentBonus);
            Assert.Equal(0, profile.AntiRepeat.RepeatDecayHalfLifeTicks);
            foreach (var bound in profile.Personality.BoundByAxis.Values)
                Assert.Equal(0, bound);
            Assert.Equal(AiTier.Smart, profile.TierByActorClass[AiActorClass.Unique]);
            Assert.Equal(AiTier.Performance, profile.TierByActorClass[AiActorClass.General]);
        }

        // Guards are off at their own identity values (spec Tunables table): 1/0/0, not all-zero.
        var siege = tuning.Profiles["siege/default"];
        Assert.Equal(1, siege.Guards.MinTargetsForArea);
        Assert.Equal(0, siege.Guards.KillMarginMilli);
        Assert.Equal(0, siege.Guards.FightEndingLiveCount);
        Assert.Equal(AiTier.Smart, siege.TierOverride);
        Assert.Null(tuning.Profiles["*/default"].TierOverride);
    }

    [Fact]
    public void Both_hosts_load_the_same_file()
    {
        // Server/Program.cs and Injector/Host/RpgHost.cs both read "combat-ai.v1.json" -- a source
        // scan proves the literal, since neither host is reachable from a Core test directly.
        var serverSource = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Server", "Program.cs"));
        var injectorSource = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Injector", "Host", "RpgHost.cs"));
        // CAI-F1: the hosts must reference the REVISION CONSTANT, never a literal filename. A literal is
        // what made an H7 publish impossible for every combat-ai lane; the constant is what makes the
        // filename and the readers move together.
        Assert.Contains("CombatAiTuningFiles.Current", serverSource, StringComparison.Ordinal);
        Assert.Contains("CombatAiTuningFiles.Current", injectorSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"combat-ai.v", serverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"combat-ai.v", injectorSource, StringComparison.Ordinal);
        Assert.Contains("siege.v3.json", serverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("\"siege.v1.json\"", serverSource, StringComparison.Ordinal);
    }
}
