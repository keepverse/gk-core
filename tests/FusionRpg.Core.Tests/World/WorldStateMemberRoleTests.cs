using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>`commander-roster` EP3.7 — the member-role vocabulary is CLOSED, and `Commander` joined it
/// with the legion-commander module (spec-legion-commander.md). A literal pin is allowed here and only
/// for this reason: a closed vocabulary the code owns and a human changes by review (persistence reads
/// and writes the name), never a population.</summary>
public class WorldStateMemberRoleTests
{
    [Fact]
    public void The_member_role_vocabulary_is_the_three_ruled_members()
    {
        Assert.Equal(
            new[] { "Fighter", "Bearer", "Commander" },
            Enum.GetNames<WorldEntityMemberRole>());
    }

    [Fact]
    public void A_member_defaults_to_Fighter_so_every_pre_existing_member_reads_as_one()
    {
        Assert.Equal(WorldEntityMemberRole.Fighter, new WorldEntityMember().Role);
    }
}
