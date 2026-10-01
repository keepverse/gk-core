using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// rift-gate decision 16: the embed marker is written by **three** copies — the Core constant, the
/// launcher's literal (that project shares no assembly with Core, like <c>PipeName</c>) and the web
/// reader. A drift here fails silently: the page would simply stop offering Leave. This pins all three
/// to one value, so the drift becomes a red test instead of a missing button nobody notices.
///
/// <para><b>One test, three repositories.</b> These reads were combined onto a single
/// <c>FindRepoRoot()</c> that returned <c>KeepverseRoots.Core()</c>, so the launcher and injector
/// copies resolved to <c>gk-core/src/…</c> and the web copy to <c>gk-core/web/…</c>. Measured, each is
/// carried by a DIFFERENT repository: Core and Server by gk-core, the Launcher and Injector by
/// gk-fusion, the web tree by gk-web. The file was already half-migrated —
/// <c>The_marker_reader_is_a_query_reader_not_a_hash_reader</c> and
/// <c>The_leave_control_is_gated_on_the_embed_marker</c> read through <c>KeepverseRoots.Web()</c>
/// while their neighbours read through the combined root. Every read below now names its own
/// repository, because the deciding input is which repository carries the path and a single root
/// cannot express that.</para>
/// </summary>
public class RiftGateEmbedMarkerGuardTests
{
    [Fact]
    public void The_marker_key_and_value_agree_across_core_launcher_and_web()
    {
        // gk-core carries the constant.
        var core = File.ReadAllText(Path.Combine(
            KeepverseRoots.Core(), "src", "FusionRpg.Core", "Overlay", "OverlayEmbedMarker.cs"));
        Assert.Contains("QueryKey = \"embed\"", core, StringComparison.Ordinal);
        Assert.Contains("QueryValue = \"1\"", core, StringComparison.Ordinal);

        // gk-fusion carries the launcher's literal copy.
        var launcher = File.ReadAllText(Path.Combine(
            KeepverseRoots.Fusion(), "src", "FusionRpg.Launcher", "OverlayWindow.xaml.cs"));
        Assert.Contains("EmbedQueryKey = \"embed\"", launcher, StringComparison.Ordinal);
        Assert.Contains("EmbedQueryValue = \"1\"", launcher, StringComparison.Ordinal);

        // gk-web carries the reader.
        var web = File.ReadAllText(Path.Combine(
            KeepverseRoots.Web(), "web", "fusion-rpg-web", "src", "shell", "overlayEmbed.ts"));
        Assert.Contains("EMBED_QUERY_KEY = \"embed\"", web, StringComparison.Ordinal);
        Assert.Contains("EMBED_QUERY_VALUE = \"1\"", web, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_hosts_mark_the_navigated_url_through_the_shared_helper()
    {
        // The injector links Core directly, so it must call the one helper. gk-fusion carries it.
        var injector = File.ReadAllText(Path.Combine(
            KeepverseRoots.Fusion(), "src", "FusionRpg.Injector", "Hud", "OverlayViewHost.cs"));
        Assert.Contains("OverlayEmbedMarker.MarkedUrl", injector, StringComparison.Ordinal);
        // ...and must never hand-append a fragment marker (the FE's HashRouter owns the hash).
        Assert.DoesNotContain("Navigate(_url + \"#", injector, StringComparison.Ordinal);

        // The launcher cannot reference Core, so it carries the literal helper — still a query flag.
        var launcher = File.ReadAllText(Path.Combine(
            KeepverseRoots.Fusion(), "src", "FusionRpg.Launcher", "OverlayWindow.xaml.cs"));
        Assert.Contains("AppendEmbedMarker(url)", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Navigate(url + \"#", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void The_marker_reader_is_a_query_reader_not_a_hash_reader()
    {
        var web = File.ReadAllText(Path.Combine(
            KeepverseRoots.Web(), "web", "fusion-rpg-web", "src", "shell", "overlayEmbed.ts"));
        Assert.Contains("searchParams", web, StringComparison.Ordinal);
        Assert.DoesNotContain("location.hash", web, StringComparison.Ordinal);
    }
}

/// <summary>
/// rift-gate overlay-hide: the FE's Leave control renders ONLY for an embedded visit and reaches the
/// host through the one leave route. The behaviour is covered by the web suite; these keep the wiring
/// from drifting (an ungated control would show a dead button in a plain browser).
///
/// <para>Same shape as the class above: one test here spans gk-web (the bus file), gk-core (the command
/// names and the endpoint) and gk-fusion (the launcher pipe and the overlay spec). The
/// <c>docs/launcher/overlay-spec.md</c> read is the one that reads most like a gk-core path and is
/// furthest from it — measured per FILE, it is gk-fusion's, because the workspace root's <c>docs/</c>
/// holds the development documentation while gk-fusion's holds the launcher contract. A
/// directory-level check gets that backwards for exactly the same reason it gets
/// <c>data/seed/creatures/_registry</c> backwards.</para>
/// </summary>
public class RiftGateLeaveControlGuardTests
{
    [Fact]
    public void The_leave_control_is_gated_on_the_embed_marker()
    {
        var control = File.ReadAllText(Path.Combine(
            KeepverseRoots.Web(), "web", "fusion-rpg-web", "src", "shell", "OverlayLeave.tsx"));

        Assert.Contains("useOverlayEmbed", control, StringComparison.Ordinal);
        Assert.Contains("postOverlayLeave", control, StringComparison.Ordinal);
        // Honest absence: no marker means no control at all, not a dead button.
        Assert.Contains("if (!embedded) return null", control, StringComparison.Ordinal);
    }

    [Fact]
    public void The_leave_route_is_the_one_named_for_the_player_action()
    {
        // gk-web carries the bus file.
        var bus = File.ReadAllText(Path.Combine(
            KeepverseRoots.Web(), "web", "fusion-rpg-web", "src", "lib", "bus", "overlay.ts"));
        Assert.Contains("/api/overlay/leave", bus, StringComparison.Ordinal);

        // The internal command vocabulary is the Core constant; the route stays the player's word.
        var names = File.ReadAllText(Path.Combine(
            KeepverseRoots.Core(), "src", "FusionRpg.Core", "Overlay", "OverlayCommandNames.cs"));
        Assert.Contains("Hide = \"overlay.hide\"", names, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overlay_spec_verb_list_names_every_shipped_pipe_verb()
    {
        // The spec is the contract for this feature and says "update it before changing behaviour".
        // Adding a verb without updating the resolved-decision list is how the doc ended up claiming
        // `hide` had been dropped while the code shipped it. Pin the list to the enum instead.
        // Both the spec and the launcher pipe server are gk-fusion's.
        var spec = File.ReadAllText(Path.Combine(
            KeepverseRoots.Fusion(), "docs", "launcher", "overlay-spec.md"));
        var pipe = File.ReadAllText(Path.Combine(
            KeepverseRoots.Fusion(), "src", "FusionRpg.Launcher", "Services", "OverlayPipeServer.cs"));

        foreach (var verb in new[] { "toggle", "ping", "hide" })
        {
            Assert.Contains($"\"{verb}\" => OverlayPipeCommand", pipe, StringComparison.Ordinal);
            Assert.Contains($"`{verb}`", spec, StringComparison.Ordinal);
        }

        // The stale claim that `hide` was dropped must not come back.
        Assert.DoesNotContain("`show` / `hide` dropped", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaving_is_not_an_onboarding_acknowledgement()
    {
        // Assert on API names and routes, never on prose: both files legitimately SAY "not an
        // acknowledgement" in a comment, and forbidding the word would forbid the explanation.
        var bus = File.ReadAllText(Path.Combine(
            KeepverseRoots.Web(), "web", "fusion-rpg-web", "src", "lib", "bus", "overlay.ts"));
        Assert.DoesNotContain("useAcknowledge", bus, StringComparison.Ordinal);
        Assert.DoesNotContain("/stories/", bus, StringComparison.Ordinal);
        Assert.Contains("/api/overlay/leave", bus, StringComparison.Ordinal);

        // gk-core carries the endpoint.
        var endpoint = File.ReadAllText(Path.Combine(
            KeepverseRoots.Core(), "src", "FusionRpg.Server", "OverlayEndpoints.cs"));
        Assert.DoesNotContain("Acknowledge", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("rpg_onboarding", endpoint, StringComparison.Ordinal);
        Assert.Contains("/api/overlay/leave", endpoint, StringComparison.Ordinal);
    }
}
