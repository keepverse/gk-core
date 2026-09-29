using System.Text.Json;
using FusionRpg.Tools.RpgSim;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// rpg-simulator RS2.2 — the verdict and digest contract's tests. No server, no HTTP: these are the
/// properties the artifact and the hash must have, including the one that keeps the digest from being
/// vacuous (<see cref="The_digest_moves_when_a_read_value_moves"/>) and the one that keeps it from
/// moving for a reason that is not a determinism break
/// (<see cref="An_excluded_field_does_not_move_the_digest"/>).
///
/// <para>The contract is <c>gk-core/tools/RpgSim/readback-verdict.md</c>; the machine is
/// <c>ReadingDigest.cs</c> + <c>ScenarioVerdict.cs</c>.</para>
/// </summary>
public class RpgSimVerdictContractTests
{
    // ---- the exclusion list ------------------------------------------------------------------ //

    [Fact]
    public void Every_baseline_exclusion_carries_a_reason()
    {
        Assert.NotEmpty(ReadingDigest.Baseline);
        Assert.All(ReadingDigest.Baseline, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Field));
            // The list is load-bearing: an entry with no stated reason is an entry nobody can review.
            Assert.True(e.Reason.Length > 40, $"'{e.Field}' needs a reason, not a placeholder");
        });
        // No field appears twice with two different stories.
        Assert.Equal(ReadingDigest.Baseline.Count, ReadingDigest.Baseline.Select(e => e.Field).Distinct().Count());
    }

    [Fact]
    public void A_suffix_pattern_covers_the_six_spellings_of_the_same_wall_clock()
    {
        using var payload = JsonDocument.Parse("""
            { "state": "Collected", "dispatchedUtc": "2026-09-23T01:00:00Z", "dueUtc": "2026-09-23T02:00:00Z",
              "collectedUtc": "2026-09-23T01:30:00Z", "createdUtc": "2026-09-23T00:00:00Z",
              "serverUtc": "2026-09-23T01:30:00Z", "lastHeartbeatUtc": "2026-09-23T01:30:00Z" }
            """);
        var canonical = CanonicalJson.Of(ReadingDigest.CanonicalForm(
            new[] { new ReadingDigest.DigestEntry("read.expeditions#$", "GET /api/expeditions/1", payload.RootElement) },
            ReadingDigest.Baseline));

        foreach (var stamp in new[] { "dispatchedUtc", "dueUtc", "collectedUtc", "createdUtc", "serverUtc", "lastHeartbeatUtc" })
            Assert.Contains($"\"{stamp}\":\"<excluded>\"", canonical);
        Assert.Contains("\"state\":\"Collected\"", canonical);
    }

    [Fact]
    public void A_scenario_declared_exclusion_merges_and_keeps_its_reason()
    {
        var merged = ReadingDigest.MergeExclusions(new[]
        {
            new ScenarioExclusion { Field = "mood", Reason = "a per-run cosmetic the scenario chose not to digest" }
        });
        Assert.Contains(merged, e => e.Field == "mood" && e.Reason.Contains("cosmetic"));
        Assert.Contains(merged, e => e.Field == "*Utc");
    }

    // ---- the hash ---------------------------------------------------------------------------- //

    [Fact]
    public void The_digest_does_not_depend_on_the_order_the_scenario_declared_its_includes()
    {
        var a = Entry("read.souls#$.balance", "GET /api/souls/1", "1000");
        var b = Entry("read.roster#$.items", "GET /api/creatures/1", """[{"speciesId":"x"}]""");

        Assert.Equal(
            ReadingDigest.Compute(new[] { a, b }, ReadingDigest.Baseline),
            ReadingDigest.Compute(new[] { b, a }, ReadingDigest.Baseline));
    }

    [Fact]
    public void The_digest_moves_when_a_read_value_moves()
    {
        var before = ReadingDigest.Compute(new[] { Entry("read.souls#$.balance", "GET /api/souls/1", "1000") },
            ReadingDigest.Baseline);
        var after = ReadingDigest.Compute(new[] { Entry("read.souls#$.balance", "GET /api/souls/1", "999") },
            ReadingDigest.Baseline);

        // Non-vacuity: the digest is a reading of the value, not a constant with a pretty name.
        Assert.NotEqual(before, after);
    }

    [Fact]
    public void An_excluded_field_does_not_move_the_digest()
    {
        var one = ReadingDigest.Compute(new[] { Entry("read.expeditions#$", "GET /api/expeditions/1",
            """{ "state": "Collected", "collectedUtc": "2026-09-23T01:30:00Z" }""") }, ReadingDigest.Baseline);
        var two = ReadingDigest.Compute(new[] { Entry("read.expeditions#$", "GET /api/expeditions/1",
            """{ "state": "Collected", "collectedUtc": "2027-01-01T09:00:00Z" }""") }, ReadingDigest.Baseline);

        Assert.Equal(one, two);
    }

    [Fact]
    public void The_digest_covers_the_source_route_as_well_as_the_value()
    {
        var value = """{ "balance": 1000 }""";
        Assert.NotEqual(
            ReadingDigest.Compute(new[] { Entry("read.souls#", "GET /api/souls/1", value) }, ReadingDigest.Baseline),
            ReadingDigest.Compute(new[] { Entry("read.souls#", "GET /api/souls/2", value) }, ReadingDigest.Baseline));
    }

    [Fact]
    public void Canonical_json_keeps_a_url_and_a_marker_readable()
    {
        var entry = Entry("read.r#", "GET /api/runs?playerId=1&take=2", """{ "note": "a<b&c", "collectedUtc": "2026-01-01T00:00:00Z" }""");
        var canonical = CanonicalJson.Of(ReadingDigest.CanonicalForm(new[] { entry }, ReadingDigest.Baseline));

        // Relaxed escaping (see CanonicalJson.Options): a moved-pointer report has to be readable, and
        // escaping here would print a URL's `&` as \u0026 and the blank marker as \u003Cexcluded\u003E.
        Assert.Contains("playerId=1&take=2", canonical);
        Assert.Contains("a<b&c", canonical);
        Assert.Contains("\"collectedUtc\":\"<excluded>\"", canonical);
    }

    // ---- the falsifier's report -------------------------------------------------------------- //

    [Fact]
    public void A_moved_digest_names_the_pointer_and_both_values()
    {
        using var left = JsonDocument.Parse("""
            { "read.expeditions#$.items[0].state": { "source": "GET /api/expeditions/1", "value": "Dispatched" } }
            """);
        using var right = JsonDocument.Parse("""
            { "read.expeditions#$.items[0].state": { "source": "GET /api/expeditions/1", "value": "Collected" } }
            """);

        var comparison = ReadingDigest.Compare(left.RootElement, right.RootElement);
        Assert.False(comparison.Same);
        var moved = Assert.Single(comparison.MovedPointers);
        Assert.Contains("state", moved);
        Assert.Contains("\"Dispatched\"", moved);
        Assert.Contains("\"Collected\"", moved);
        Assert.Contains("MOVED", comparison.Report());
    }

    [Fact]
    public void Two_identical_canonical_forms_compare_same_with_nothing_moved()
    {
        using var form = JsonDocument.Parse("""{ "r#$.a": { "source": "GET /api/x/1", "value": 1 } }""");
        var comparison = ReadingDigest.Compare(form.RootElement, form.RootElement);
        Assert.True(comparison.Same);
        Assert.Empty(comparison.MovedPointers);
        Assert.Contains("identical", comparison.Report());
    }

    [Fact]
    public void A_new_pointer_or_a_changed_array_length_is_reported_by_name()
    {
        using var left = JsonDocument.Parse("""{ "r#$.items": { "value": [1, 2] } }""");
        using var right = JsonDocument.Parse("""{ "r#$.items": { "value": [1, 2, 3] } }""");
        Assert.Contains(ReadingDigest.Compare(left.RootElement, right.RootElement).MovedPointers,
            m => m.Contains("array length 2 -> 3"));

        using var fewer = JsonDocument.Parse("""{ "r#$.items": { "value": [] } }""");
        using var more = JsonDocument.Parse("""{ "r#$.items": { "value": [], "extra": 1 } }""");
        Assert.Contains(ReadingDigest.Compare(fewer.RootElement, more.RootElement).MovedPointers,
            m => m.Contains("<absent> -> 1"));
    }

    // ---- the artifact's own rules ------------------------------------------------------------ //

    [Fact]
    public void A_verdict_reading_without_a_source_is_refused()
    {
        var verdict = ValidVerdict();
        verdict.Readings.Add(new ScenarioReading { Name = "read.soulless", Source = "", Method = "POST",
            Value = JsonDocument.Parse("{}").RootElement });
        Assert.Contains(verdict.Validate(), e => e.Contains("source required"));
    }

    [Fact]
    public void A_digest_without_its_exclusion_list_is_refused()
    {
        var verdict = ValidVerdict();
        verdict.Digest = "deadbeef";
        Assert.Contains(verdict.Validate(), e => e.Contains("must print the exclusion list it used"));
    }

    [Fact]
    public void A_failing_run_must_name_its_failure()
    {
        var verdict = ValidVerdict();
        verdict.Ok = false;
        Assert.Contains(verdict.Validate(), e => e.Contains("must name what failed"));
    }

    [Fact]
    public void A_verdict_that_happened_must_say_the_host_settled()
    {
        // "The host settled" is polled, never assumed (RS2.4): a reading taken mid-flight describes a race.
        var missing = ValidVerdict();
        missing.Settle = null;
        Assert.Contains(missing.Validate(), e => e.Contains("settle: required"));

        var unsettled = ValidVerdict();
        unsettled.Settle = new SettleRecord
        {
            Settled = false, Polls = 300, ElapsedMs = 30_000, Reason = "the host did not settle within 30s"
        };
        Assert.Contains(unsettled.Validate(), e => e.Contains("did not settle"));

        // A refusal is the one case with no settle to report: nothing ran.
        var refused = new ScenarioVerdict
        {
            ScenarioId = "x", Host = "inproc", Clock = "ambient", Refused = true, Ok = false,
            Failures = { "refused: simEnabled:false" }
        };
        Assert.Empty(refused.Validate());
    }

    [Fact]
    public void A_well_formed_verdict_serializes_its_readings_source_and_its_exclusion_reasons()
    {
        var verdict = ValidVerdict();
        verdict.Digest = ReadingDigest.Compute(
            new[] { Entry("read.souls#$", "GET /api/souls/1", """{"balance":1000}""") }, ReadingDigest.Baseline);
        verdict.DigestExclusions = ReadingDigest.Baseline
            .Select(e => new ExclusionRecord { Field = e.Field, Reason = e.Reason }).ToList();

        Assert.Empty(verdict.Validate());
        var json = verdict.ToJson();
        Assert.Contains("\"source\": \"GET /api/souls/{playerId}\"", json);
        Assert.Contains("\"host\": \"inproc\"", json);
        Assert.Contains("Ambient wall clock", json);
        Assert.Contains("\"digest\":", json);
    }

    // ---- helpers ----------------------------------------------------------------------------- //

    static ReadingDigest.DigestEntry Entry(string reference, string source, string valueJson) =>
        new(reference, source, JsonDocument.Parse(valueJson).RootElement);

    static ScenarioVerdict ValidVerdict() => new()
    {
        ScenarioId = "contract-test",
        Seed = 20260922,
        Clock = "ambient — no server clock seam yet (RS3, gated)",
        Host = "inproc",
        Ok = true,
        // The settle report every non-refused verdict carries (RS2.4).
        Settle = new SettleRecord { Settled = true, Polls = 2, ElapsedMs = 107 },
        Readings =
        {
            new ScenarioReading
            {
                Name = "read.souls",
                Source = "GET /api/souls/{playerId}",
                Method = "GET",
                Value = JsonDocument.Parse("""{ "balance": 1000 }""").RootElement
            }
        }
    };
}
