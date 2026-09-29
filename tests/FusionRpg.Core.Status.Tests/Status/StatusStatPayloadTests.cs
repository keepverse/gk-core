using System.Text.Json;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Status;
using Xunit;
using FusionRpg.Contracts;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// E17's <c>ModifyStat</c> payload (spec-status-payload-completion.md).
///
/// <para><b>Four statuses declared this and did nothing.</b> <c>rally</c>, <c>expose</c>,
/// <c>command</c> and <c>shatter</c> all declare <c>StatusPayloadKind.ModifyStat</c>, and that kind
/// had zero consumers repo-wide — they created instances, played VFX, and changed no stat. Worse,
/// the <c>stat</c> overlay key was documented and used in a shipped example that <b>failed
/// validation</b>: <i>"unknown overlay key 'stat' for effect actions"</i>. Documentation of a
/// capability that did not exist.</para>
/// </summary>
public class StatusStatPayloadTests
{
    static JsonElement Json(string s) => JsonDocument.Parse(s).RootElement.Clone();

    // ---- the allowlist, and the shipped example ---------------------------------------------------

    [Fact]
    public void The_stat_key_is_accepted_on_an_overlay_now_that_it_has_a_consumer()
    {
        // The entry lands WITH its consumer, never before it — an allowlisted key nothing reads is
        // the defect, not the fix.
        var actions = new List<EffectActionRow>
        {
            new() { Seq = 1, Action = EffectActions.ApplyResourceDelta, Params = new() },
        };
        var overlay = new Dictionary<string, object?>
        {
            ["statusId"] = "blight",
            ["amount"] = -12,
            ["stat"] = Json("""{"atk":{"more":-0.1}}"""),
        };

        Assert.True(EffectOverlayMerge.TryValidateOverlayForDef(actions, overlay, out var error), error);
    }

    [Fact]
    public void The_shipped_example_overlay_validates()
    {
        // docs/architecture/examples/status/expose-row.overlay.json — split out of blight-row's (E17
        // originally carried the `stat` block there; C2 found blight never declares ModifyStat, so
        // the block moved to a status that actually does).
        var path = Path.Combine(RepoRoot(), "docs", "architecture", "examples", "status",
            "expose-row.overlay.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));

        var overlay = doc.RootElement.GetProperty("overlay")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => (object?)p.Value.Clone());

        var actions = new List<EffectActionRow>
        {
            new() { Seq = 1, Action = EffectActions.ApplyResourceDelta, Params = new() },
        };

        Assert.True(EffectOverlayMerge.TryValidateOverlayForDef(actions, overlay, out var error), error);
    }

    [Fact]
    public void An_unknown_overlay_key_is_still_refused()
    {
        // Adding one key must not have opened the gate.
        var actions = new List<EffectActionRow>
        {
            new() { Seq = 1, Action = EffectActions.ApplyResourceDelta, Params = new() },
        };
        var overlay = new Dictionary<string, object?> { ["stats"] = Json("{}") };

        Assert.False(EffectOverlayMerge.TryValidateOverlayForDef(actions, overlay, out _));
    }

    // ---- parsing ----------------------------------------------------------------------------------

    [Fact]
    public void A_stat_block_parses_into_channel_op_value()
    {
        Assert.True(StatusStatPayload.TryParse(
            Json("""{"atk":{"more":-0.1},"maxHp":{"flat":25}}"""), out var mods, out var error), error);

        Assert.Equal(2, mods.Count);
        Assert.Contains(mods, m => m.ChannelId == "atk" && m.Op == "more" && Math.Abs(m.Value + 0.1) < 1e-9);
        Assert.Contains(mods, m => m.ChannelId == "maxHp" && m.Op == "flat" && Math.Abs(m.Value - 25) < 1e-9);
    }

    [Fact]
    public void The_parse_is_ordered_so_a_replay_cannot_differ_by_dictionary_internals()
    {
        StatusStatPayload.TryParse(Json("""{"maxHp":{"flat":1},"atk":{"more":2},"defense":{"flat":3}}"""),
            out var first, out _);
        StatusStatPayload.TryParse(Json("""{"defense":{"flat":3},"atk":{"more":2},"maxHp":{"flat":1}}"""),
            out var second, out _);

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_channel_nothing_composes_is_refused()
    {
        // It would be created, stored, withdrawn on expiry, and never once read.
        Assert.False(StatusStatPayload.TryParse(Json("""{"fireRate":{"flat":1}}"""), out _, out var error));
        Assert.Contains("not a composed channel", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_derived_combat_channel_is_accepted()
    {
        Assert.True(StatusStatPayload.TryParse(
            Json("""{"combat.power.fire":{"flat":25}}"""), out var mods, out var error), error);
        Assert.Single(mods);
    }

    [Fact]
    public void Override_is_not_an_op_a_status_may_use()
    {
        // A status is a temporary contribution. A timed Override would silently outrank every
        // permanent source for its duration and then snap back — and effects cannot emit Override
        // at all (E1), so this keeps one rule in one shape.
        Assert.False(StatusStatPayload.TryParse(Json("""{"atk":{"override":5}}"""), out _, out var error));
        Assert.Contains("flat | increased | more", error!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_numeric_value_is_refused()
    {
        Assert.False(StatusStatPayload.TryParse(Json("""{"atk":{"flat":"lots"}}"""), out _, out _));
    }

    [Fact]
    public void An_absent_block_is_no_mods_and_no_error()
    {
        Assert.True(StatusStatPayload.TryParse(null, out var mods, out var error));
        Assert.Empty(mods);
        Assert.Null(error);
    }

    // ---- derived-channel parse refusal (mechanism-wiring E2) --------------------------------------

    /// <summary>
    /// The earlier, stricter half of the rule <c>StatusDerivedSubsystem.TryParseOp</c> already enforces
    /// at resolve time (skip, never coerce). Here the same content error is caught at PARSE, before a
    /// `more` mod against a derived channel is ever created, stored, or withdrawn on expiry — never a
    /// silently-dropped mod that "just doesn't show up".
    /// </summary>
    [Fact]
    public void More_on_a_derived_channel_is_refused_at_parse()
    {
        Assert.False(StatusStatPayload.TryParse(
            Json("""{"combat.defense.omni":{"more":-0.1}}"""), out var mods, out var error));
        Assert.Empty(mods);
        Assert.Contains(StatusStatPayload.MoreOnDerivedChannelError, error!, StringComparison.Ordinal);
    }

    [Fact]
    public void More_on_a_primary_channel_still_parses()
    {
        // The refusal targets DERIVED channels only -- `atk` is one of the 23 primary channels, and
        // `more` against it is exactly StatusStatPayload's own shipped worked example.
        Assert.True(StatusStatPayload.TryParse(
            Json("""{"atk":{"more":-0.1}}"""), out var mods, out var error), error);
        Assert.Single(mods);
    }

    [Fact]
    public void Flat_and_increased_on_derived_channels_still_parse()
    {
        // There is no `More` on the derived side, but Flat/Increased are exactly what a derived channel
        // composes with (DerivedModifierOp.Flat | Increased | Replace | Flag).
        Assert.True(StatusStatPayload.TryParse(
            Json("""{"combat.defense.omni":{"flat":25},"status.resist.dot":{"increased":0.1}}"""),
            out var mods, out var error), error);
        Assert.Equal(2, mods.Count);
    }

    /// <summary>
    /// The one predicate both the parser and <c>StatusDerivedModReader</c> read (mechanism-wiring E2) --
    /// pinned directly so a mutant flipping it to always-true is caught here even before it reaches the
    /// `more`-refusal or the reader's own primary-channel exclusion test.
    /// </summary>
    [Theory]
    [InlineData("combat.defense.omni", true)]
    [InlineData("status.power.omni", true)]
    [InlineData("status.resist.dot", true)]
    [InlineData("atk", false)]
    [InlineData("maxHp", false)]
    [InlineData("status.power.fire", false)] // not one of the eight named status.power/resist channels
    public void IsDerivedChannel_matches_exactly_the_combat_and_status_derived_set(string channel, bool derived) =>
        Assert.Equal(derived, StatusStatPayload.IsDerivedChannel(channel));

    // ---- the modifiers it becomes -------------------------------------------------------------------

    [Fact]
    public void An_instance_contributes_source_tagged_modifiers()
    {
        // Timed modifiers are a bag entry withdrawn on expiry, never a direct write — the same law
        // as everything else here.
        var instance = Instance("inst-1", new StatusStatMod("atk", "more", -0.1));

        var mods = StatusStatPayload.ToModifiers(instance);

        var mod = Assert.Single(mods);
        Assert.Equal("status", mod.SourceKind);
        Assert.Equal("status:inst-1", mod.SourceId);
        Assert.Equal(ModifierOp.More, mod.Op);
        // E21: StatApplyScope's grammar requires the "entity:" prefix to match at all — a bare
        // pointer silently composed nothing (found by a seam test running the real StatSystem).
        Assert.Equal("entity:Z1", mod.ApplyOwnerKey);
    }

    [Fact]
    public void Two_stacks_of_one_status_are_two_withdrawable_contributions()
    {
        // The source id is the INSTANCE, not the status: one stack expiring must not take the
        // other's modifier with it.
        var a = Instance("inst-1", new StatusStatMod("atk", "flat", 5));
        var b = Instance("inst-2", new StatusStatMod("atk", "flat", 5));

        Assert.NotEqual(StatusStatPayload.SourceIdOf(a), StatusStatPayload.SourceIdOf(b));
        Assert.NotEqual(
            StatusStatPayload.ToModifiers(a).Single().SourceId,
            StatusStatPayload.ToModifiers(b).Single().SourceId);
    }

    [Fact]
    public void Each_op_maps_to_the_bag_op_that_composes_it()
    {
        Assert.Equal(ModifierOp.Flat,
            StatusStatPayload.ToModifiers(Instance("i", new StatusStatMod("atk", "flat", 1))).Single().Op);
        Assert.Equal(ModifierOp.Increased,
            StatusStatPayload.ToModifiers(Instance("i", new StatusStatMod("atk", "increased", 1))).Single().Op);
        Assert.Equal(ModifierOp.More,
            StatusStatPayload.ToModifiers(Instance("i", new StatusStatMod("atk", "more", 1))).Single().Op);
    }

    [Fact]
    public void A_status_with_no_stat_block_contributes_nothing()
    {
        Assert.Empty(StatusStatPayload.ToModifiers(Instance("i")));
    }

    // ---- the four statuses this was for ---------------------------------------------------------------

    [Theory]
    [InlineData("rally")]
    [InlineData("expose")]
    [InlineData("command")]
    [InlineData("shatter")]
    public void The_four_modify_stat_statuses_can_now_carry_a_payload(string statusId)
    {
        // They declared ModifyStat and it had zero consumers. This does not make them content — it
        // makes them authorable, which they were not.
        var def = StatusCatalogBootstrap.CreateDefault().GetRequired(statusId);

        Assert.Contains(StatusPayloadKind.ModifyStat, def.PayloadKinds);
    }

    static StatusInstance Instance(string id, params StatusStatMod[] mods) => new()
    {
        InstanceId = id,
        StatusId = "rally",
        HostPtr = "Z1",
        StatMods = mods,
    };

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "atoms"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }
}
