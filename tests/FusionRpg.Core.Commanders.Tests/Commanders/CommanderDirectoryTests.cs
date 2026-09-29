using System;
using System.IO;
using System.Runtime.CompilerServices;
using FusionRpg.Core.Commanders;
using Xunit;

namespace FusionRpg.Core.Tests.Commanders;

/// <summary>
/// `commander-identity` SE4.1: the data-backed <see cref="ICommanderDirectory"/> is the one place a
/// commander's empire, display name and allocation scope key are decided. Byte-identical to the
/// retired <c>CommanderId</c> helpers except the one ruled change (the player's own commander shows
/// the player's name).
/// </summary>
public class CommanderDirectoryTests
{
    static DataCommanderDirectory Shipped()
    {
        var path = Path.Combine(RepoRoot(), "data", "seed", "commanders", "_registry",
            "default-commanders.v1.json");
        return DataCommanderDirectory.Parse(File.ReadAllText(path));
    }

    static string RepoRoot([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;
        return Path.GetFullPath(Path.Combine(testsDir, "..", "..", ".."));
    }

    [Theory]
    [InlineData("commander:dave")]
    [InlineData("commander:zomboss")]
    public void Shipped_registry_resolves_each_authored_stable_id(string stableId)
    {
        var directory = Shipped();
        Assert.True(directory.TryResolve(stableId, out var commander));
        Assert.Equal(stableId, commander.StableId);
    }

    [Fact]
    public void Shipped_registry_allocation_scope_keys_are_byte_identical_to_the_retired_helpers()
    {
        var directory = Shipped();
        directory.TryResolve("commander:dave", out var dave);
        directory.TryResolve("commander:zomboss", out var zomboss);

        // Exactly the strings CommanderIds.AllocationScopeKey produced (CommanderId.cs:68-72). A change
        // here is a save migration, so it is pinned literally.
        Assert.Equal("player:42", directory.AllocationScopeKey(dave, 42));
        Assert.Equal("zomboss:42", directory.AllocationScopeKey(zomboss, 42));
        Assert.NotEqual(directory.AllocationScopeKey(dave, 1), directory.AllocationScopeKey(dave, 2));
    }

    [Fact]
    public void Empire_of_each_shipped_row_is_the_authored_empire()
    {
        var directory = Shipped();
        directory.TryResolve("commander:dave", out var dave);
        directory.TryResolve("commander:zomboss", out var zomboss);

        Assert.Equal(EmpireId.Dave, directory.EmpireOf(dave));
        Assert.Equal(EmpireId.Zomboss, directory.EmpireOf(zomboss));
    }

    [Fact]
    public void Zomboss_displays_his_authored_name_not_a_players()
    {
        var directory = Shipped();
        directory.TryResolve("commander:zomboss", out var zomboss);
        // identity-rename T12: the authored row names the antagonist by TOKEN, so the expectation
        // is the registry's own display value - a lead rename stays one row edit and this test
        // follows it (owner rulings R9/R11).
        Assert.Equal(
            FusionRpg.Core.Narrative.LeadNamesHub.Current.Display(
                FusionRpg.Core.Narrative.LeadTokens.Antagonist),
            directory.DisplayName(zomboss, "Crazy Dave"));
    }

    [Fact]
    public void AToken_display_name_that_names_nobody_throws_rather_than_rendering_braces()
    {
        // identity-rename T12: a `{token}` row resolves through the lead-names hub, so a token nobody
        // declares is a data defect, not a label — the hub's own exception, never "{lead_sidekick}"
        // shown to a player.
        var directory = new DataCommanderDirectory(new[]
        {
            new CommanderRow("commander:x", EmpireId.Zomboss, "{lead_sidekick}", false, "x:{id}")
        });
        Assert.True(directory.TryResolve("commander:x", out var commander));
        Assert.Throws<FusionRpg.Core.Narrative.UnknownLeadNameException>(
            () => directory.DisplayName(commander, "Nene"));
    }

    [Fact]
    public void ALiteral_display_name_passes_through_untouched()
    {
        // The other half of the contract: only a brace-wrapped value is a token, so every row written
        // as a plain name keeps behaving exactly as it did before T12.
        var directory = new DataCommanderDirectory(new[]
        {
            new CommanderRow("commander:wild", EmpireId.Zomboss, "Wild", false, "wild:{id}")
        });
        Assert.True(directory.TryResolve("commander:wild", out var commander));
        Assert.Equal("Wild", directory.DisplayName(commander, null));
    }

    [Fact]
    public void The_players_own_commander_displays_the_players_name()
    {
        // Rift-gate decision 7 / owner ruling 2026-09-18: Dave stays the first commander, but his
        // display name becomes the player's own name (one identity, no new actor).
        var directory = Shipped();
        directory.TryResolve("commander:dave", out var dave);
        Assert.Equal("Nene Scarlet", directory.DisplayName(dave, "Nene Scarlet"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unknown_player_falls_back_to_a_neutral_label_never_another_persons_name(string? playerName)
    {
        var directory = Shipped();
        directory.TryResolve("commander:dave", out var dave);

        var display = directory.DisplayName(dave, playerName);
        Assert.Equal("Commander", display);
        Assert.DoesNotContain("Dave", display, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Zomboss", display, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_for_resolves_each_shipped_empire()
    {
        var directory = Shipped();
        Assert.Equal("commander:dave", directory.DefaultFor(EmpireId.Dave).StableId);
        Assert.Equal("commander:zomboss", directory.DefaultFor(EmpireId.Zomboss).StableId);
    }

    [Fact]
    public void Default_for_an_unregistered_empire_throws_rather_than_guesses()
    {
        var directory = Shipped();
        Assert.Throws<InvalidOperationException>(() => directory.DefaultFor(new EmpireId("penny")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dave")]
    [InlineData("zomboss")]
    [InlineData("commander:penny")]
    [InlineData("not-a-commander")]
    public void Unknown_stable_ids_are_refused(string? stableId)
    {
        var directory = Shipped();
        Assert.False(directory.TryResolve(stableId, out _));
    }

    [Fact]
    public void An_unknown_commander_ref_throws_rather_than_returns_a_default()
    {
        var directory = Shipped();
        Assert.Throws<ArgumentException>(() => directory.EmpireOf(new CommanderRef("commander:penny")));
        Assert.Throws<ArgumentException>(() => directory.AllocationScopeKey(new CommanderRef("commander:penny"), 1));
    }

    [Fact]
    public void A_third_commander_row_resolves_with_no_interface_edit()
    {
        // The Open/Closed shape at directory level: a creature commander is a new row. SE4.4 runs this
        // row through a lawn allocation + kill attribution.
        var rows = new[]
        {
            new CommanderRow("commander:dave", EmpireId.Dave, "Commander", true, "player:{id}"),
            new CommanderRow("commander:zomboss", EmpireId.Zomboss, "Dr. Zomboss", false, "zomboss:{id}"),
            new CommanderRow("commander:test-creature", EmpireId.Dave, "Test Creature", false, "player:{id}"),
        };
        var directory = new DataCommanderDirectory(rows);

        Assert.True(directory.TryResolve("commander:test-creature", out var creature));
        Assert.Equal(EmpireId.Dave, directory.EmpireOf(creature));
        Assert.Equal("Test Creature", directory.DisplayName(creature, "Nene"));
        Assert.Equal("player:1", directory.AllocationScopeKey(creature, 1));
    }

    [Fact]
    public void A_row_without_the_id_placeholder_is_refused_at_construction()
    {
        var rows = new[] { new CommanderRow("commander:dave", EmpireId.Dave, "Commander", true, "player") };
        Assert.Throws<ArgumentException>(() => new DataCommanderDirectory(rows));
    }

    [Fact]
    public void Duplicate_stable_ids_are_refused_at_construction()
    {
        var rows = new[]
        {
            new CommanderRow("commander:dave", EmpireId.Dave, "Commander", true, "player:{id}"),
            new CommanderRow("commander:dave", EmpireId.Dave, "Commander Again", true, "player:{id}"),
        };
        Assert.Throws<ArgumentException>(() => new DataCommanderDirectory(rows));
    }
}
