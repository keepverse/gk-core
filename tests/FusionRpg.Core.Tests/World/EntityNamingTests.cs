using FusionRpg.Core.World;
using Xunit;

namespace FusionRpg.Core.Tests.World;

/// <summary>
/// world-stage W8 (spec-world-playback.md §4): a legion's display name is not derivable from its id
/// client-side ("e-dave-legion-1" is not "Legion I"), so `EntityNaming.DisplayName` computes it
/// server-side, deterministically, from stable state — no persisted counter, no hashed field.
///
/// Builds isolated <see cref="WorldState"/> fixtures directly (a minimal, valid-enough graph for a
/// pure function that only reads <c>world.Entities</c>) rather than depending on any shipped
/// template's exact entity ids, which the ordinal rule must not care about.
/// </summary>
public class EntityNamingTests
{
    static WorldState Bare() => new() { WorldId = "w", TemplateId = "t", Seed = 1 };

    static WorldEntity Legion(string id, string owner) => new()
    {
        EntityId = id, Kind = WorldEntityKind.Legion, OwnerFactionId = owner,
        AtSectorId = "home", Stance = "march", MovementRemaining = 1000
    };

    [Fact]
    public void The_only_legion_of_a_faction_is_numbered_one()
    {
        var w = Bare() with { Entities = new[] { Legion("e-dave-legion-1", "dave") } };
        Assert.Equal("Legion I", EntityNaming.DisplayName(w, w.Entities[0]));
    }

    [Fact]
    public void A_second_legion_is_numbered_by_stable_id_order_not_insertion_order()
    {
        // Inserted in the OPPOSITE of id order, to prove the ordinal comes from stable id sort
        // rather than list position.
        var w = Bare() with
        {
            Entities = new[]
            {
                Legion("e-dave-legion-2", "dave"),
                Legion("e-dave-legion-1", "dave"),
            }
        };

        var first = w.Entities.Single(e => e.EntityId == "e-dave-legion-1");
        var second = w.Entities.Single(e => e.EntityId == "e-dave-legion-2");
        Assert.Equal("Legion I", EntityNaming.DisplayName(w, first));
        Assert.Equal("Legion II", EntityNaming.DisplayName(w, second));
    }

    [Fact]
    public void Different_owners_number_independently()
    {
        var w = Bare() with
        {
            Entities = new[]
            {
                Legion("e-dave-legion-1", "dave"),
                Legion("e-zomboss-legion-1", "zomboss"),
            }
        };

        Assert.Equal("Legion I", EntityNaming.DisplayName(w, w.Entities[0]));
        Assert.Equal("Legion I", EntityNaming.DisplayName(w, w.Entities[1]));
    }

    [Fact]
    public void Different_kinds_number_independently_within_the_same_faction()
    {
        var legion = Legion("e-dave-legion-1", "dave");
        var caravan = legion with { EntityId = "e-dave-caravan-1", Kind = WorldEntityKind.Caravan };
        var w = Bare() with { Entities = new[] { legion, caravan } };

        Assert.Equal("Legion I", EntityNaming.DisplayName(w, legion));
        Assert.Equal("Caravan I", EntityNaming.DisplayName(w, caravan));
    }

    [Theory]
    [InlineData(1, "I")]
    [InlineData(4, "IV")]
    [InlineData(9, "IX")]
    [InlineData(14, "XIV")]
    [InlineData(40, "XL")]
    public void Roman_numeral_ordinals_follow_standard_subtractive_notation(int ordinal, string expectedNumeral)
    {
        // Zero-padded ids sort lexicographically in the same order as their numeric suffix, up to
        // two digits — enough for every case this table exercises.
        var entities = Enumerable.Range(1, ordinal)
            .Select(i => Legion($"e-dave-legion-{i:D2}", "dave"))
            .ToList();
        var w = Bare() with { Entities = entities };

        var nth = entities.Single(e => e.EntityId == $"e-dave-legion-{ordinal:D2}");
        Assert.Equal($"Legion {expectedNumeral}", EntityNaming.DisplayName(w, nth));
    }
}
