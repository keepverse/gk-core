using System.Text.Json;
using FusionRpg.Core.Delve.Domains;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Domains;

/// <summary>D4.19 (spec-domain-catalog.md §4) — `DomainOfferDto`: a scan for an engine word (the
/// todo's own literal Verify line). Two scans, not one: property NAMES (the `QuestDto`/D4.14 idiom —
/// catches a field added later before it ships) and a REAL instance serialized to JSON (the spec's
/// own stronger claim, verbatim: "`once`/`many`, `Θ`, `bandDelta`, `dangerBand`, `PartyIndex` never
/// appear as VALUES" — a property-name scan alone would miss a `bandName` string that leaked one).
///
/// <para>The real, current `gk-web/web/fusion-rpg-web/src/i18n/vocabularyGuard.ts` `BANNED_WORDS` list
/// (confirmed by reading it) does not yet contain ANY of these words — `delve-stage`'s own extension
/// (`bandDelta`, `dangerBand`, `PartyIndex`, spec-delve-stage.md §8) is Phase 5, unbuilt. This test
/// uses domain-catalog's own §4 sentence directly, the same choice `QuestDtoTests.cs` already made
/// for its own sibling DTO.</para></summary>
public class DomainOfferDtoTests
{
    static readonly string[] BannedWords = { "theta", "bandDelta", "dangerBand", "partyindex", "once", "many", "ordinal" };

    static DomainOfferDto Sample() => new(
        DomainId: "domain.forest-shallow-001", Name: "Wyrmroot Hollow", Flavor: "Something coils beneath the roots.",
        Climate: "fire", EntranceLabel: "Lair", EntryKey: DomainOffers.EntryKeyStanding,
        Sealed: false, Resume: null,
        Rungs: new[] { new DomainRungOfferDto("very-hard", "Very Hard", "Abyssal", OathOffered: true, Permadeath: false) },
        TailSteps: new[] { new DomainTailOfferDto(1, "Abyss +1", "Abyssal") },
        RaidModes: new[] { "solo", "duo" }, BossName: "Grand Warden", Cleared: new[] { "medium" },
        Provisionable: new[] { new ProvisionableOfferDto("container.satchel", "Satchel", 250, 4) });

    [Fact]
    public void No_property_name_anywhere_in_the_dto_tree_names_an_engine_word()
    {
        var types = new[]
        {
            typeof(DomainOfferDto), typeof(DomainRungOfferDto), typeof(DomainTailOfferDto),
            typeof(DomainResumeDto), typeof(ProvisionableOfferDto),
        };
        var fieldNames = types.SelectMany(t => t.GetProperties()).Select(p => p.Name.ToLowerInvariant()).ToList();
        Assert.NotEmpty(fieldNames);
        foreach (var banned in BannedWords)
            Assert.DoesNotContain(fieldNames, n => n.Contains(banned));
    }

    [Fact]
    public void A_real_instance_serialized_to_json_never_contains_a_banned_word_as_a_value()
    {
        var json = JsonSerializer.Serialize(Sample()).ToLowerInvariant();
        foreach (var banned in BannedWords)
            Assert.DoesNotContain(banned.ToLowerInvariant(), json);
    }

    [Fact]
    public void EntryKey_constants_are_the_two_spec_named_strings_never_the_raw_entry_value()
    {
        Assert.Equal("standing", DomainOffers.EntryKeyStanding);
        Assert.Equal("single-descent", DomainOffers.EntryKeySingleDescent);
    }

    [Fact]
    public void DomainOfferDto_carries_exactly_the_fields_the_spec_names_no_more()
    {
        var expected = new[]
        {
            "DomainId", "Name", "Flavor", "Climate", "EntranceLabel", "EntryKey", "Sealed", "Resume",
            "Rungs", "TailSteps", "RaidModes", "BossName", "Cleared", "Provisionable",
        };
        var actual = typeof(DomainOfferDto).GetProperties().Select(p => p.Name).ToList();
        Assert.Equal(expected.OrderBy(x => x), actual.OrderBy(x => x));
    }
}
