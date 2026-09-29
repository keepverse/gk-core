using FusionRpg.Core.Creatures;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

public sealed class CreatureProgressionSourceTests
{
    [Fact]
    public void EmpireGeneral_roundTripsWithStableScopeKey()
    {
        var source = CreatureProgressionSource.EmpireGeneral("  imp  ");

        Assert.Equal("creature.progression.v1", source.Kind);
        Assert.Equal("general:imp", source.Id);
        Assert.Equal("imp", source.ScopeKey);
        Assert.Equal(source, CreatureProgressionSource.Parse(source.Kind, source.Id));
    }

    [Fact]
    public void UniqueSpecimen_requiresInstanceAndOccurrenceIdentity()
    {
        var source = CreatureProgressionSource.UniqueSpecimen("specimen-7", "occ-42");

        Assert.Equal("creature.progression.v1", source.Kind);
        Assert.Equal("unique:specimen-7:occ-42", source.Id);
        Assert.Equal("specimen-7", source.ScopeKey);
        Assert.Equal(source, CreatureProgressionSource.Parse(source.Kind, source.Id));
    }

    [Fact]
    public void Commander_roundTrips()
    {
        var source = CreatureProgressionSource.Commander("crazy-dave");

        Assert.Equal("creature.progression.v1", source.Kind);
        Assert.Equal("commander:crazy-dave", source.Id);
        Assert.Equal("crazy-dave", source.ScopeKey);
        Assert.Equal(source, CreatureProgressionSource.Parse(source.Kind, source.Id));
    }

    [Theory]
    [InlineData("creature.progression.v1", "unique:specimen:occ:extra")]
    [InlineData("creature.progression.v1", "unique:specimen")]
    [InlineData("creature.progression.v1", "general:imp:extra")]
    [InlineData("creature.progression.v1", "general:")]
    [InlineData("unknown", "anything")]
    public void Parse_rejectsWrongGrammar(string kind, string id)
    {
        Assert.Throws<FormatException>(() => CreatureProgressionSource.Parse(kind, id));
    }

    [Fact]
    public void Parse_rejectsSeparatorsInsideIdentity()
    {
        Assert.Throws<ArgumentException>(() => CreatureProgressionSource.UniqueSpecimen("spec:imen", "occ"));
        Assert.Throws<ArgumentException>(() => CreatureProgressionSource.Commander(""));
    }
}
