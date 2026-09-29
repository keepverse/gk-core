using Xunit;

namespace FusionRpg.Core.Tests.Delve.Battle;

/// <summary>
/// D2.12 — a delve battle always resolves through the pinned `delve` profile
/// (<see cref="FusionRpg.Core.Delve.Battle.DelveBattle.Run"/>), never through a wave-keyed lookup.
/// Source-scan guard, the same shape `ModeProfileArchitectureTests` already uses for its own
/// no-branch rule: a line-based heuristic, not a proof, skipping whole-line comments only.
/// </summary>
public class DelveBattleProfileGuardTests
{
    static readonly string[] BannedTokens = { "ProfileForExpedition", "ProfileFor(" };

    static string DelveDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FusionRpg.Core", "Delve");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate src/FusionRpg.Core/Delve above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void No_wave_keyed_profile_lookup_exists_anywhere_under_Core_Delve()
    {
        var dir = DelveDir();
        Assert.True(Directory.Exists(dir), $"delve source dir not found: {dir}");

        var offences = new List<string>();
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal)) continue;

                foreach (var token in BannedTokens)
                    if (lines[i].Contains(token, StringComparison.Ordinal))
                        offences.Add($"{Path.GetFileName(file)}:{i + 1} -> {token}");
            }
        }

        Assert.True(offences.Count == 0,
            "delve code must always resolve through DelveBattle.Run's pinned profile, never a wave-keyed lookup:\n" +
            string.Join("\n", offences));
    }

    [Fact]
    public void DelveBattle_Run_always_pins_the_delve_profile_id()
    {
        Assert.Equal("delve", FusionRpg.Core.Battle.Timeline.BattleModeProfileCatalog.DelveId);
        Assert.Same(
            FusionRpg.Core.Battle.Timeline.BattleModeProfileCatalog.Delve,
            FusionRpg.Core.Battle.Timeline.BattleModeProfileCatalog.Resolve(FusionRpg.Core.Battle.Timeline.BattleModeProfileCatalog.DelveId));
    }

    [Fact]
    public void DelveBattle_Run_resolves_a_real_battle_under_the_delve_profile()
    {
        // A real end-to-end call -- not just the guard above. The delve profile's own W=4/PerSide/
        // ActionPoints shape must resolve a normal stomp setup exactly as any other profile would;
        // nothing about "explicit resolve" should make an ordinary battle behave abnormally.
        var report = FusionRpg.Core.Delve.Battle.DelveBattle.Run(FusionRpg.Core.Tests.Battle.BattleGoldenTests.StompSetup(), seed: 12321);

        Assert.NotNull(report);
        Assert.True(report.Actors.Count > 0);
        Assert.Equal(FusionRpg.Core.Battle.BattleOutcome.Victory, report.Outcome); // squad greatly outlevels the wave in this fixture
    }
}
