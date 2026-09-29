using FusionRpg.Core.BuildPresets;
using Xunit;

namespace FusionRpg.Core.Tests.BuildPresets;

/// <summary>
/// build-preset BP1.9 (spec-preset-store.md) — the closed piece vocabulary and the pure save-time
/// shape rules. Both halves are Core-only: no store, no server, no Unity.
/// </summary>
[Trait("VerificationId", "core.build-preset")]
public class BuildPresetShapeTests
{
    [Fact]
    public void The_piece_kind_vocabulary_is_closed_at_five_members_and_every_id_round_trips()
    {
        var members = Enum.GetValues<BuildPresetPieceKind>();

        // PINNED DELIBERATELY. This is a DECLARED VOCABULARY the code owns, not a content population:
        // the ids are persisted strings, and a sixth kind is a reviewed enum change (map D2). Unlike a
        // species or item count, this number must never be "bumped" when content grows.
        Assert.Equal(5, members.Length);
        Assert.Equal(new[] { "aptitudes", "field", "gear", "patron", "skills" },
            members.Select(BuildPresetPieceKinds.Id).OrderBy(id => id, StringComparer.Ordinal));

        // No member is `default`: an unparseable id must not be mistakable for a real kind.
        Assert.DoesNotContain(default(BuildPresetPieceKind), members);
        foreach (var member in members)
        {
            var id = BuildPresetPieceKinds.Id(member);
            Assert.True(BuildPresetPieceKinds.TryParse(id, out var back), $"'{id}' must parse");
            Assert.Equal(member, back);
            // Whitespace around a stored string is still that id.
            Assert.True(BuildPresetPieceKinds.TryParse(" " + id + " ", out var padded));
            Assert.Equal(member, padded);
        }
    }

    [Fact]
    public void An_unknown_stored_id_parses_as_unknown_and_never_throws()
    {
        foreach (var stored in new[] { "unknown", "sixth-kind", "", "  ", null, "PATRON", "patron-" })
        {
            Assert.False(BuildPresetPieceKinds.TryParse(stored, out _));
            Assert.Equal("unknown", BuildPresetPieceKinds.Category(stored));
        }
    }

    [Fact]
    public void Id_throws_for_a_value_outside_the_closed_vocabulary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BuildPresetPieceKinds.Id((BuildPresetPieceKind)99));
    }

    /// <summary>Every shape rule refuses by its own code. Each case violates exactly one rule, so the
    /// reason is attributable; the documented order is exercised separately by the legal preset below.</summary>
    [Theory]
    [InlineData("build-preset.name.missing", "   ")]
    [InlineData("build-preset.empty", "Fire lean")]
    [InlineData("build-preset.patron.multiple", "Fire lean")]
    [InlineData("build-preset.patron.outside-field", "Fire lean")]
    [InlineData("build-preset.field.shape", "Fire lean")]
    [InlineData("build-preset.aptitudes.scope", "Fire lean")]
    [InlineData("build-preset.skills.shape", "Fire lean")]
    public void Each_shape_rule_refuses_by_its_own_code(string expected, string name)
    {
        var pieces = Violation(expected);
        var (ok, reason) = BuildPresetShape.Validate(name, pieces);
        Assert.False(ok);
        Assert.Equal(expected, reason);
    }

    static List<BuildPresetPieceRow> Violation(string rule) => rule switch
    {
        "build-preset.name.missing" => new() { Patron("spec-1") },
        "build-preset.empty" => new(),
        // Two patron rows: the second one is the violation, whether or not a field is present.
        "build-preset.patron.multiple" => new() { Patron("spec-1"), Patron("spec-2") },
        // The patron is not a member of the field piece, which would release it on apply.
        "build-preset.patron.outside-field" => new() { Patron("spec-9"), Field("spec-1", 0) },
        // A field's ids must be unique (the same specimen twice is not a field of two).
        "build-preset.field.shape" => new() { Field("spec-1", 0), Field("spec-1", 1) },
        // An aptitudes target must be `{scope}:{scopeKey}` with one of the gate's three scope words.
        "build-preset.aptitudes.scope" => new() { Aptitude("specimen:ua-1", "preset-c") },
        // A skills piece's ordinals must be contiguous from 0.
        "build-preset.skills.shape" => new() { Skill("player", 0, "Might"), Skill("player", 2, "Vigor") },
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "no fixture for this rule")
    };

    /// <summary>The other spellings of the same two rules: a field with a gap in its ordinals, and an
    /// aptitudes target with no scope delimiter at all.</summary>
    [Theory]
    [InlineData("build-preset.field.shape", "ordinal-gap")]
    [InlineData("build-preset.aptitudes.scope", "no-delimiter")]
    public void Each_shape_rule_refuses_by_its_own_code_again(string expected, string flavour)
    {
        var pieces = flavour switch
        {
            "ordinal-gap" => new List<BuildPresetPieceRow> { Field("spec-1", 0), Field("spec-2", 2) },
            "no-delimiter" => new List<BuildPresetPieceRow> { Aptitude("commander", "preset-c") },
            _ => throw new ArgumentOutOfRangeException(nameof(flavour), flavour, "no fixture")
        };
        var (ok, reason) = BuildPresetShape.Validate("Fire lean", pieces);
        Assert.False(ok);
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void A_legal_preset_of_all_five_kinds_passes()
    {
        var pieces = new List<BuildPresetPieceRow>
        {
            Patron("spec-1"),
            Field("spec-1", 0), Field("spec-2", 1), Field("spec-3", 2),
            Aptitude("commander:", "preset-c"),
            Aptitude("unique:ua-1", "preset-u"),
            Aptitude("species:fumeshroom", "preset-s"),
            Gear("ua-1", "loadout-1"),
            Skill("player", 0, "Might"), Skill("player", 1, "Vigor"),
        };
        var (ok, reason) = BuildPresetShape.Validate("Fire lean", pieces);
        Assert.True(ok, reason);
        Assert.Equal("", reason);
    }

    [Fact]
    public void A_preset_without_a_patron_or_a_field_still_passes()
    {
        // Both kinds are optional: a skills-only preset is legal shape (its skills are still checked).
        var pieces = new List<BuildPresetPieceRow> { Skill("player", 0, "Might") };
        Assert.True(BuildPresetShape.Validate("Skills only", pieces).Ok);
    }

    static BuildPresetPieceRow Patron(string instanceId) =>
        new(BuildPresetPieceKind.Patron, "", 0, instanceId);

    static BuildPresetPieceRow Field(string instanceId, long ordinal) =>
        new(BuildPresetPieceKind.Field, "", ordinal, instanceId);

    static BuildPresetPieceRow Aptitude(string target, string presetId) =>
        new(BuildPresetPieceKind.Aptitudes, target, 0, presetId);

    static BuildPresetPieceRow Gear(string target, string loadoutId) =>
        new(BuildPresetPieceKind.Gear, target, 0, loadoutId);

    static BuildPresetPieceRow Skill(string owner, long ordinal, string actionId) =>
        new(BuildPresetPieceKind.Skills, owner, ordinal, actionId);
}
