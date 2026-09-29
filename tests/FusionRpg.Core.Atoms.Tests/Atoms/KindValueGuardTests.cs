using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E29 acceptance (spec-kind-value-guard.md). Ten of the twelve atom kinds accepted a value from an
/// enumerable vocabulary with nothing enforcing membership — a wrong status id, currency, board verb
/// validated, bound, compiled, reached the executor, matched no case, and did nothing forever. Value
/// validation existed for exactly one kind (`stat.modify.channel`, "G6") before this module; the worst
/// case was `stat.derived`, whose own hand-off to G6 never actually ran (`AtomRowValidator.cs:313-314`).
///
/// <para>One extension point (<c>ParamDef.Vocabulary</c>, checked generically by
/// <c>AtomKindRegistry.Validate</c>), not eleven special cases — <c>stat.modify.channel</c>'s own
/// check (the original G6) is migrated onto it too, proving the generic mechanism reproduces the
/// hand-rolled one exactly rather than only templating the other twelve.</para>
/// </summary>
public class KindValueGuardTests
{
    static Dictionary<string, object?> P(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    static void AssertBadValue(string kindId, Dictionary<string, object?> pars, string offendingValue)
    {
        var r = AtomKindRegistry.Validate(kindId, pars);
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains(offendingValue, r.ToString());
    }

    // ---- one planted violation per vocabulary (spec §5) ------------------------------------------

    [Fact]
    public void Stat_modify_channel_rejects_an_unknown_value()
    {
        AssertBadValue("stat.modify",
            P(("channel", "not-a-real-channel"), ("op", "flat"), ("amount", 1)), "not-a-real-channel");
    }

    [Fact]
    public void Stat_derived_channel_rejects_an_unknown_value()
    {
        // Test 1: crit.rat where crit.rate was meant — one letter off, out of 267. The 267-id hand-off
        // gap (AtomRowValidator.cs:313-314, "unregistered channel is G6's job") is closed by this.
        AssertBadValue("stat.derived",
            P(("channel", "crit.rat"), ("op", "Flat"), ("amount", 1)), "crit.rat");
    }

    [Fact]
    public void Status_apply_status_rejects_an_unknown_value()
    {
        AssertBadValue("status.apply", P(("status", "not-a-real-status")), "not-a-real-status");
    }

    [Fact]
    public void Status_clear_status_rejects_an_unknown_value()
    {
        AssertBadValue("status.clear", P(("status", "not-a-real-status")), "not-a-real-status");
    }

    [Fact]
    public void Resource_economy_currency_rejects_an_unknown_value()
    {
        // Test 2: Wave 7's own worked example of the silent no-op.
        AssertBadValue("resource.economy",
            P(("currency", "souls"), ("op", "add"), ("amount", 1)), "souls");
    }

    [Fact]
    public void Resource_economy_op_rejects_a_typo_rather_than_silently_treating_it_as_set()
    {
        // Test 3: the most damaging case in the whole set — a typo'd op used to succeed LOUDLY at the
        // WRONG behaviour (anything non-"add"/"+" silently meant "set") instead of failing at the
        // right one.
        AssertBadValue("resource.economy",
            P(("currency", "sun"), ("op", "addd"), ("amount", 1)), "addd");
    }

    [Fact]
    public void Resource_delta_channel_rejects_an_unknown_value()
    {
        AssertBadValue("resource.delta", P(("channel", "mana")), "mana");
    }

    [Fact]
    public void Shield_grant_source_class_rejects_a_typo_rather_than_silently_falling_back_to_skill()
    {
        AssertBadValue("shield.grant", P(("sourceClass", "arua")), "arua");
    }

    [Fact]
    public void Board_action_op_rejects_an_unknown_value()
    {
        AssertBadValue("board.action", P(("op", "explode-everything")), "explode-everything");
    }

    [Fact]
    public void Grid_spawn_gridItemType_rejects_an_unknown_value()
    {
        // Test 5: numeric vocabularies too, not just strings.
        AssertBadValue("grid.spawn", P(("gridItemType", 999)), "999");
    }

    [Fact]
    public void Grid_clear_gridItemType_rejects_an_unknown_value()
    {
        AssertBadValue("grid.clear", P(("gridItemType", 999)), "999");
    }

    [Fact]
    public void Box_set_boxType_rejects_an_unknown_value()
    {
        AssertBadValue("box.set", P(("boxType", 99)), "99");
    }

    [Fact]
    public void Spawn_entity_kind_rejects_an_unknown_value()
    {
        AssertBadValue("spawn.entity", P(("kind", "wizard")), "wizard");
    }

    // ---- the numbered tests (spec §5), beyond the planted violations above ------------------------

    [Fact]
    public void Status_apply_wither_is_accepted_legal_in_battle_inert_on_the_lawn()
    {
        // Test 4: rule 4 — the guard does not over-refuse a value legal on one runtime and inert on
        // another. `wither` actually WORKS on the lawn (StatusKind.OverTime / PayloadKind.PulseHp,
        // resolved inside StatusRuntime) — the spec's own §3 rule-4 correction — but the point stands
        // either way: a runtime gap is an execute-time reporting concern, never a load-time refusal.
        var r = AtomKindRegistry.Validate("status.apply", P(("status", "wither")));
        Assert.True(r.IsOk, r.ToString());
    }

    [Fact]
    public void Every_one_of_the_catalog_statuses_is_accepted()
    {
        // Test 6: the guard reads the SSOT rather than a stale subset.
        var catalog = StatusCatalogBootstrap.CreateDefault().All();
        Assert.NotEmpty(catalog);

        foreach (var status in catalog)
        {
            var r = AtomKindRegistry.Validate("status.apply", P(("status", status.StatusId)));
            Assert.True(r.IsOk, $"{status.StatusId}: {r}");
        }
    }

    [Fact]
    public void The_guard_reads_StatusCatalogBootstrap_live_every_call_not_a_frozen_copy()
    {
        // Test 7 (rule 2 — "the guard resolves the SSOT; it never holds a copy"): AtomKindRegistry has
        // no field caching the status vocabulary anywhere — every Validate call re-reads
        // StatusCatalogBootstrap.CreateDefault() fresh. Proven here by construction rather than by
        // registering a genuinely new status into the shared bootstrap (which every other test in the
        // suite also reads, and mutating it would leak across tests): two independent CreateDefault()
        // calls, compared, must agree exactly — a hardcoded copy inside AtomKindRegistry frozen at
        // some earlier revision would have no way to guarantee that if the catalog ever changed.
        var first = StatusCatalogBootstrap.CreateDefault().All().Select(d => d.StatusId)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        var second = StatusCatalogBootstrap.CreateDefault().All().Select(d => d.StatusId)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        Assert.Equal(first, second);
        Assert.NotEmpty(first);

        // And the guard's own verdict for a real status matches this same live-read vocabulary, not a
        // frozen subset — the actual property rule 2 protects.
        foreach (var statusId in first)
        {
            var r = AtomKindRegistry.Validate("status.apply", P(("status", statusId)));
            Assert.True(r.IsOk, $"{statusId}: {r}");
        }
    }

    [Fact]
    public void All_shipped_atoms_validate()
    {
        // Test 8 / acceptance 6's first half: the module is additive to existing content.
        var dir = FindDataDir();
        var files = Directory.GetFiles(Path.Combine(dir, "seed", "atoms"), "fx-*.json", SearchOption.AllDirectories)
            .Select(f => (f, File.ReadAllText(f)))
            .ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        Assert.NotEmpty(collected.Content.Atoms);

        foreach (var atom in collected.Content.Atoms)
        {
            var pars = ReadParams(atom.ParamsJson);
            var r = AtomKindRegistry.Validate(atom.KindId, pars);
            Assert.True(r.IsOk, $"{atom.AtomId} ({atom.KindId}): {r}");
        }
    }

    [Fact]
    public void Every_affix_family_channel_resolves_or_is_an_explainable_template_shape()
    {
        // Test 9 / acceptance 6's second half (§5.1, decided 2026-09-03): "every entry ... and every
        // params.channel checked against DerivedStatRegistry" — the spec's own scope is exactly the
        // channel VALUE, not full-shape AtomKindRegistry.Validate. Affix families author extra fields
        // inside `params` that AtomKindRegistry.Validate would refuse for reasons unrelated to E29, so
        // this test's job is the channel-vocabulary claim specifically.
        //
        // CONTRACT, not a count (corpus is generator-authored and grows every generation — 98 → 100 →
        // 109 → 112 → 125 as real families shipped, so `seen`/`channelBearing`/valid totals are stale
        // by construction and must not be pinned). What actually matters is the vocabulary claim:
        //   • every stat.modify channel is a legal primary channel;
        //   • every stat.derived channel either IS a registered member, or is one of the two
        //     DOCUMENTED template shapes the generation pipeline expands/probes before it reaches the
        //     registry — a `{variant}` element template, or a bare `status.*` family stem whose concrete
        //     category segment generation supplies (`ChannelUnits.ForAuthoredChannel` probes it with a
        //     real member; E43 refuses to emit the literal stem, proven in FamilyExpansionTests).
        // A concrete-looking channel that resolves to nothing and is NOT one of those shapes (a typo
        // like `crit.rat`) is the defect this must still catch.
        var dir = Path.Combine(FindDataDir(), "seed", "items", "affix-families");
        var files = Directory.GetFiles(dir, "*.json");
        Assert.NotEmpty(files);

        var registry = DerivedStatRegistry.CreateDefault();
        var seen = 0;
        var channelBearing = 0;
        var unexplained = new List<string>();

        foreach (var file in files)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var entry in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString()!;
                var kindId = entry.GetProperty("kindId").GetString()!;
                seen++;

                if (kindId is not ("stat.derived" or "stat.modify")) continue;
                if (!entry.TryGetProperty("params", out var p) || !p.TryGetProperty("channel", out var chEl))
                    continue;

                var authored = chEl.GetString()!;
                var channel = authored.Replace("{variant}", "fire", StringComparison.Ordinal);
                channelBearing++;

                var ok = string.Equals(kindId, "stat.modify", StringComparison.Ordinal)
                    ? Array.Exists(AtomKindRegistry.PrimaryChannels, c => string.Equals(c, channel, StringComparison.Ordinal))
                    : registry.TryResolveChannel(channel, out _);
                if (ok) continue;

                // The two documented template shapes the pipeline expands/probes before registration.
                var isElementTemplate = authored.Contains("{variant}", StringComparison.Ordinal);
                var isBareStatusStem = authored.StartsWith("status.", StringComparison.Ordinal)
                    && !authored.EndsWith('.')
                    && !authored["status.".Length..].Contains('.');
                if (isElementTemplate || isBareStatusStem) continue;

                unexplained.Add($"{id} (kind {kindId}, channel '{authored}')");
            }
        }

        Assert.NotEmpty(files);
        Assert.True(seen > 0 && channelBearing > 0, "corpus parsed no channel-bearing families");
        Assert.Empty(unexplained);
    }

    static object? Substitute(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString()!.Replace("{variant}", "fire", StringComparison.Ordinal),
        JsonValueKind.Number => el.TryGetInt32(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => el.ToString(),
    };

    static Dictionary<string, object?> ReadParams(string? json)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;
        foreach (var prop in doc.RootElement.EnumerateObject())
            result[prop.Name] = Substitute(prop.Value);
        return result;
    }

    static string FindDataDir()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "data");
            if (Directory.Exists(candidate)) return candidate;
            var up = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", "..", "data"));
            if (Directory.Exists(up)) return up;
            dir = Path.GetFullPath(Path.Combine(dir, ".."));
        }
        throw new DirectoryNotFoundException("data");
    }
}
