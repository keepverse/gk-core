using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// ST4.5c (manager ruling on the ST4.5a diagnosis): the <c>power-trigger-frequency</c> seed kind. The
/// rows it carries are the frequency half of <c>CostFunction.Conditionality</c>, so this reader is the
/// boundary that keeps a trigger from being authored with no rate at all.
/// </summary>
public class AtomSeedFileTriggerFrequencyTests
{
    static SeedCollectResult Collect(string entries) =>
        AtomSeedFile.Collect(new[]
        {
            ("trigger-frequencies.test.json", $$"""
                {
                  "schemaVersion": 1,
                  "kind": "power-trigger-frequency",
                  "entries": [{{entries}}]
                }
                """),
        });

    [Fact]
    public void A_trigger_frequency_entry_lands_as_a_row()
    {
        var result = Collect("""{ "trigger": "OnDamageDealt", "perMinute": 60 }""");

        Assert.True(result.IsOk, string.Join("; ", result.Errors.Select(e => e.ToString())));
        var row = Assert.Single(result.Content.TriggerFrequencies);
        Assert.Equal("OnDamageDealt", row.Trigger);
        Assert.Equal(60, row.PerMinute);
    }

    [Fact]
    public void A_missing_trigger_or_perMinute_is_refused()
    {
        var noTrigger = Collect("""{ "perMinute": 60 }""");
        Assert.False(noTrigger.IsOk);
        Assert.Empty(noTrigger.Content.TriggerFrequencies);
        Assert.Contains(noTrigger.Errors, e => e.Detail.Contains("explicit trigger", StringComparison.Ordinal));

        var noRate = Collect("""{ "trigger": "OnTimer" }""");
        Assert.False(noRate.IsOk);
        Assert.Empty(noRate.Content.TriggerFrequencies);
        Assert.Contains(noRate.Errors, e => e.Detail.Contains("explicit perMinute", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_positive_perMinute_is_refused_by_name_and_never_becomes_a_row()
    {
        // The domain rule, not a balance one: Conditionality multiplies a triggered atom's price by
        // perMinute/60, so a zero row is exactly the silent 0 this seed kind exists to close.
        foreach (var rate in new[] { "0", "-5" })
        {
            var result = Collect($$"""{ "trigger": "OnDamageDealt", "perMinute": {{rate}} }""");

            Assert.False(result.IsOk);
            Assert.Empty(result.Content.TriggerFrequencies);
            var error = Assert.Single(result.Errors);
            Assert.Equal("OnDamageDealt", error.EntryId);
            Assert.Contains("prices every atom carrying it at zero", error.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_same_trigger_authored_twice_is_refused()
    {
        var result = Collect("""
            { "trigger": "OnDeath", "perMinute": 6 },
            { "trigger": "OnDeath", "perMinute": 7 }
            """);

        Assert.False(result.IsOk);
        Assert.Contains(result.Errors, e => e.Reason == AtomRejectionReason.DuplicateKey);
    }
}
