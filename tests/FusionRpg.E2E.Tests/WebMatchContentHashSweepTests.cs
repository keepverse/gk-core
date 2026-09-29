using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using FusionRpg.Server;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// E8's consumer: the boot sweep refuses to re-resolve a logged match across <b>edited effect
/// content</b>, the same discipline it already applies to the engine/ruleset versions and the
/// platform stamp.
///
/// <para>This is the seam, not the algorithm — the canonical form and the verdict have their own
/// tests. What is proven here is that the verdict is actually consulted, that a refusal is terminal,
/// and that a <b>registry</b> change is deliberately not a refusal.</para>
/// </summary>
[Collection("e2e")]
public class WebMatchContentHashSweepTests
{
    readonly RpgStore _store;
    readonly WebMatchService _matches;

    public WebMatchContentHashSweepTests(RpgApiFactory factory)
    {
        _store = factory.Services.GetRequiredService<RpgStore>();
        _matches = factory.Services.GetRequiredService<WebMatchService>();
    }

    /// <summary>An unresolved log row: logged, never ingested — the crash window the sweep heals.</summary>
    /// <remarks>
    /// Carries a <b>resolvable</b> setup (B40.6). These tests' subject is the content-hash verdict, so
    /// the row must resolve; an empty <c>{}</c> setup could never heal and the old "not refused"
    /// assertion silently passed on a row the sweep had actually failed to resolve.
    /// </remarks>
    (long Id, string Corr) Unresolved(string tag, string? contentStamp) =>
        SweepSetupFixture.AppendUnresolved(_store, _matches, "chash-", tag, contentStamp);

    string? RefusalFor(string corr) => _store.TryGetWebMatchLog(1, corr)?.SweepRefused;

    [Fact]
    public void A_match_logged_against_different_content_is_refused_terminally()
    {
        var stale = new ContentHashStamp(
            ContentHashRegistry.CurrentSchemaVersion,
            new string('d', 64),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["effect_atom"] = new string('e', 16) });

        var row = Unresolved("stale", stale.ToCompact());

        _matches.SweepUnresolved();

        var refusal = RefusalFor(row.Corr);
        Assert.NotNull(refusal);
        // Both hashes reported, so the reason can only have come from the content check — the
        // version and platform branches word their refusals entirely differently.
        Assert.Contains(stale.Hash, refusal!);
        Assert.Contains(_store.ComputeContentHash().Hash, refusal!);

        // Terminal, not a skip: a refused row must leave the unresolved window for good, or enough
        // of them at the low end crowd every newer row out of the ORDER BY id ASC LIMIT window.
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == row.Id);
    }

    [Fact]
    public void A_registry_version_change_is_not_a_refusal()
    {
        // E18 and E9 register tables after E11 has already stamped the Checkpoint D corpus. If a
        // version bump refused, that corpus would hard-fail by construction rather than by edit.
        var older = new ContentHashStamp(
            ContentHashRegistry.CurrentSchemaVersion - 1,
            new string('d', 64),
            new Dictionary<string, string>(StringComparer.Ordinal) { ["effect_atom"] = new string('e', 16) });

        var row = Unresolved("registry", older.ToCompact());

        _matches.SweepUnresolved();

        // Not merely "unrefused": the row must actually HEAL (leave the unresolved window), which is
        // what distinguishes a registry tolerance from a row the sweep failed to resolve at all.
        Assert.Null(RefusalFor(row.Corr));
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == row.Id);
    }

    [Fact]
    public void An_unstamped_row_is_not_refused()
    {
        // Rows logged before this module existed carry no stamp. Refusing them would strand
        // crash-recovery work that predates the feature.
        var row = Unresolved("legacy", null);

        _matches.SweepUnresolved();

        Assert.Null(RefusalFor(row.Corr));
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == row.Id);
    }

    [Fact]
    public void A_row_stamped_with_the_current_content_is_not_refused()
    {
        var row = Unresolved("current", _store.ComputeContentHash().ToCompact());

        _matches.SweepUnresolved();

        Assert.Null(RefusalFor(row.Corr));
        Assert.DoesNotContain(_store.ListUnresolvedWebMatches(500), e => e.Id == row.Id);
    }
}
