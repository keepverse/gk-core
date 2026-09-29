using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// <c>effect_channel_policy</c> (E16) — the table that was tracked for weeks with no owning module.
///
/// <para>Its assignment was settled by <b>E1's own code-or-data rule</b>: a thing may be data if
/// adding a row changes behaviour without new code. Changing a cap or default on an existing channel
/// is a value change with a live consumer, so it is data. Adding a <i>channel</i> needs a composer
/// case and a writer case — a new reader — so it stays code, and this table refuses to pretend
/// otherwise.</para>
/// </summary>
[Trait("VerificationId", "data.channel-policy")]
public class ChannelPolicyStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ChannelPolicyStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose() => _testStore.Dispose();

    [Fact]
    public void An_empty_table_reports_the_shipped_policy_for_every_channel()
    {
        var policies = _store.GetChannelPolicies();

        Assert.Equal(StatChannels.All.Length, policies.Count);
    }

    [Fact]
    public void The_shipped_direction_matches_the_code_that_composes_it()
    {
        // Two sources of truth about which way is better would be the same defect the documented
        // nine channels were: a doc promising something the code does not do.
        foreach (var policy in _store.GetChannelPolicies())
            Assert.Equal(
                (int)StatChannels.DirectionOf(policy.ChannelId),
                policy.Direction);
    }

    [Fact]
    public void A_direction_can_be_authored_and_reads_back()
    {
        // Defense ships HigherIsBetter (0) — flipping to 1 is a genuine, verifiable change, not a no-op.
        var edited = new[]
        {
            RpgStore.ShippedPolicy(StatChannels.Defense) with { Direction = 1 },
        };

        Assert.True(_store.UpsertChannelPolicies(edited).Ok);

        var stored = _store.GetChannelPolicies().Single(p => p.ChannelId == StatChannels.Defense);
        Assert.Equal(1, stored.Direction);
    }

    [Fact]
    public void A_row_may_not_invent_a_channel()
    {
        // The whole reason channel identity stayed code. A row naming a channel nothing composes
        // would be accepted and then do nothing — the silent no-op this program exists to refuse.
        var verdict = _store.UpsertChannelPolicies(new[]
        {
            RpgStore.ShippedPolicy("fireRate"),
        });

        Assert.False(verdict.Ok);
        Assert.Contains("code-or-data rule", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_direction_outside_the_two_is_refused()
    {
        var verdict = _store.UpsertChannelPolicies(new[]
        {
            RpgStore.ShippedPolicy(StatChannels.Atk) with { Direction = 7 },
        });

        Assert.False(verdict.Ok);
    }

    // ---- the hash --------------------------------------------------------------------------------

    [Fact]
    public void The_registry_is_at_version_nine_and_covers_the_policy_table()
    {
        // Bumped 4 -> 5 by cap-consolidation (T1, 2026-08-24): default_value/cap_milli/compose_kind
        // retired as dead columns — a table-SHAPE change, asserted separately from golden stability
        // (spec-cap-consolidation.md §3.1) so "hash changed" is never mistaken for "gameplay changed".
        // Bumped 5 -> 6 by the action program (T30, 2026-08-28): rpg_action/rpg_action_cost/
        // rpg_action_effect_scope joined the hash. Bumped 6 -> 7 by the action program (P0.3,
        // 2026-08-28): power_predicate_frequency joined the hash. Bumped 7 -> 8 by effect-pipeline
        // T3.1 (affix-schema, 2026-09-02): effect_container_pool's atom_id column renamed to
        // affix_id (a pool row now references an affix, not a bare atom), plus effect_affix and
        // effect_affix_ref joined the hash. Bumped 8 -> 9 by T3.2 (prefix/suffix split, same date):
        // effect_container's and rarity's single pool_rolls column split into prefix_rolls/
        // suffix_rolls. All four bumps are unrelated to effect_channel_policy, but this literal is a
        // deliberate drift canary (same reason the 4->5 bump updated it rather than asserting the
        // property symbolically) so a future version bump is caught here too.
        Assert.Equal(9, ContentHashRegistry.CurrentSchemaVersion);
        Assert.Contains("effect_channel_policy",
            ContentHashRegistry.Current.Select(t => t.TableName));
    }

    [Fact]
    public void Editing_a_direction_moves_the_content_hash()
    {
        // The reason the table and the registry bump ship together (originally: the 0.95 resist cap
        // was a code constant, editing it moved every battle golden while the stamp stood still —
        // acceptable only while a constant edit is visible in a diff). Direction is the one column
        // left with a live consumer, so it is what now carries this claim.
        var before = _store.ComputeContentHash().Hash;

        _store.UpsertChannelPolicies(new[]
        {
            RpgStore.ShippedPolicy(StatChannels.Defense) with { Direction = 1 },
        });

        Assert.NotEqual(before, _store.ComputeContentHash().Hash);
    }

    [Fact]
    public void Writing_the_same_policy_twice_does_not_move_the_hash()
    {
        var policy = new[] { RpgStore.ShippedPolicy(StatChannels.Defense) with { Direction = 1 } };
        _store.UpsertChannelPolicies(policy);
        var hash = _store.ComputeContentHash().Hash;

        _store.UpsertChannelPolicies(policy);

        Assert.Equal(hash, _store.ComputeContentHash().Hash);
    }

    // ---- the revision ------------------------------------------------------------------------------

    [Fact]
    public void A_real_edit_bumps_the_catalog_revision()
    {
        // C4 (completeness-audit.md): this direct API had no revision bump at all — an E19 receiver
        // would never re-negotiate after a policy edit made through it.
        var before = _store.GetCatalogRevision();

        Assert.True(_store.UpsertChannelPolicies(new[]
        {
            RpgStore.ShippedPolicy(StatChannels.Defense) with { Direction = 1 },
        }).Ok);

        Assert.True(_store.GetCatalogRevision() > before);
    }

    [Fact]
    public void Writing_the_same_policy_twice_does_not_bump_the_revision_the_second_time()
    {
        var policy = new[] { RpgStore.ShippedPolicy(StatChannels.Defense) with { Direction = 1 } };
        _store.UpsertChannelPolicies(policy);
        var revision = _store.GetCatalogRevision();

        _store.UpsertChannelPolicies(policy);

        Assert.Equal(revision, _store.GetCatalogRevision());
    }

    // ---- the three dead columns (cap-consolidation, T1) ---------------------------------------------

    [Fact]
    public void NoDeadColumns()
    {
        // effect_channel_policy carries channel_id and direction only -- default_value, cap_milli and
        // compose_kind retired as columns nothing ever read (spec-cap-consolidation.md §1.2, §3).
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(effect_channel_policy);";
        using var r = cmd.ExecuteReader();

        var columns = new List<string>();
        while (r.Read())
            columns.Add(r.GetString(1)); // column 1 of table_info is the column name

        Assert.Equal(
            new[] { "channel_id", "direction" }.OrderBy(x => x, StringComparer.Ordinal),
            columns.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void ContentHashChangedGoldensDidNot()
    {
        // The registry bump (V4 -> V5) and gameplay-golden stability are two SEPARATE claims —
        // spec-cap-consolidation.md §3.1 requires asserting them separately so neither is read as
        // evidence about the other. This proves the SHAPE half: V4 and V5 disagree on
        // effect_channel_policy's column list (structurally, at the registry level, independent of any
        // live database). The golden-stability half is proven by the full FusionRpg.Core.Tests run
        // (battle/status suites) staying green through this same change — a claim this test cannot
        // make on its own, which is exactly the point: neither test alone is sufficient.
        var v4Table = ContentHashRegistry.For(4).Single(t => t.TableName == "effect_channel_policy");
        var v5Table = ContentHashRegistry.For(5).Single(t => t.TableName == "effect_channel_policy");

        Assert.Equal(5, v4Table.Columns.Count);
        Assert.Equal(2, v5Table.Columns.Count);
        Assert.NotEqual(
            v4Table.Columns.Select(c => c.Name),
            v5Table.Columns.Select(c => c.Name));
    }
}
