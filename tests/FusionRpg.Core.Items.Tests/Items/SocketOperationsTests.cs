using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// spec-sockets.md §11/§12 — the four operations and the refusals, plus the corpus facts the shipped
/// <c>gk-data/packs/fusion/data/seed/items/gems/**</c> and <c>gk-data/packs/fusion/data/seed/items/socket-words/**</c> files carry today.
/// </summary>
public class SocketOperationsTests
{
    static SocketTuning Tuning => SocketGeometryTests.Shipped();

    static IReadOnlyList<SocketSlot> Sockets(params (string affinity, bool crafted, string? gem)[] rows) =>
        rows.Select((r, i) => new SocketSlot(i, r.affinity, r.crafted, r.gem, r.gem is null ? null : "inst-" + i))
            .ToList();

    static InsertDef Gem(string id = "gem.ember-shard.t3", string element = "fire", bool unique = false) =>
        new(id, "atom.elemental-power", element, 3, unique);

    // ── The op_kind namespace is module 15's ────────────────────────────────────────────────────

    [Fact]
    public void This_module_defines_no_op_kind_of_its_own()
    {
        // The four socket verbs live in module 15's closed namespace. Minting one here would fork it.
        foreach (var id in new[] { "socket-add", "socket-insert", "socket-remove", "socket-imbue" })
            Assert.True(MutationOpKinds.TryParse(id, out _), $"'{id}' should be in module 15's namespace");

        Assert.DoesNotContain(
            typeof(SocketOperations).GetNestedTypes(System.Reflection.BindingFlags.Public),
            t => t.IsEnum);
    }

    [Fact]
    public void The_word_attune_is_not_reused_as_an_operation_name()
    {
        // D24's naming constraint, as a test: §4.2/§7.1/§7.2 already use "attuned" for an insert whose
        // element matches its socket's affinity, so `socket.attune` would give one word two meanings.
        Assert.False(MutationOpKinds.TryParse("socket-attune", out _));
        Assert.False(MutationOpKinds.TryParse("attune", out _));
        Assert.Contains("socket-imbue", MutationOpKinds.AllIds);
    }

    // ── §12, the three refusals, as namespaced content rules ────────────────────────────────────

    [Fact]
    public void The_three_refusals_are_namespaced_rules_not_three_new_enum_members()
    {
        // ⛔ spec-sockets.md §12 asked for NotSocketable / NoFreeSocket / SocketOccupied as enum
        // members, moving the closed list "34 -> 37". AtomRejectionReason.ContentRuleViolated's own
        // declaration says it is the LAST member by design — "a caller that wants a new rule
        // registers a namespace, it never mints another code". The code wins over the older spec text.
        //
        // ⚠ And the spec's own count is off by one: the shipped list is 33 + None +
        // ContentRuleViolated = 35, which AtomKindRegistryTests already asserts. So §12's "34 -> 37"
        // was arithmetically wrong as well as procedurally superseded. It stays 35.
        Assert.Equal(35, Enum.GetValues<AtomRejectionReason>().Length);

        SocketRules.EnsureRegistered();
        foreach (var rule in new[]
                 {
                     SocketRules.NotSocketable, SocketRules.NoFreeSocket, SocketRules.Occupied,
                     SocketRules.NotImbuable, SocketRules.EntryExceedsRoleCeiling,
                 })
        {
            Assert.True(ContentRuleNamespaces.IsRegistered(rule));
            var r = SocketRules.Violated(rule, "detail");
            Assert.Equal(AtomRejectionReason.ContentRuleViolated, r.Reason);
            Assert.StartsWith(rule + ":", r.Detail, StringComparison.Ordinal);
        }

        // ...and they stay DISTINCT, which was §12's actual requirement — each names a different
        // operator fix, and folding two together is the failure it argued against.
        Assert.Equal(5, new[]
        {
            SocketRules.NotSocketable, SocketRules.NoFreeSocket, SocketRules.Occupied,
            SocketRules.NotImbuable, SocketRules.EntryExceedsRoleCeiling,
        }.Distinct().Count());
    }

    [Fact]
    public void A_non_gem_container_is_not_socketable()
    {
        var r = SocketOperations.TryInsert(Sockets(("", false, null)), 0, Gem("item.some-sword"), "x", out _);
        Assert.Contains(SocketRules.NotSocketable, r.Detail);

        // A combination row is not an ingredient either — it is the BONUS.
        var combo = SocketOperations.TryInsert(Sockets(("", false, null)), 0, Gem("combo.pure-fire-3"), "x", out _);
        Assert.Contains(SocketRules.NotSocketable, combo.Detail);
    }

    [Fact]
    public void An_item_with_no_sockets_is_not_socketable()
    {
        var r = SocketOperations.TryInsert(Array.Empty<SocketSlot>(), null, Gem(), "x", out _);
        Assert.Contains(SocketRules.NotSocketable, r.Detail);

        var add = SocketOperations.TryAdd(Array.Empty<SocketSlot>(), entrySocketMax: 0, out _);
        Assert.Contains(SocketRules.NotSocketable, add.Detail);
    }

    [Fact]
    public void Auto_pick_on_a_full_item_is_no_free_socket_and_an_explicit_index_is_occupied()
    {
        var full = Sockets(("", false, "gem.a.t1"), ("", false, "gem.b.t1"));

        var auto = SocketOperations.TryInsert(full, null, Gem(), "x", out _);
        Assert.Contains(SocketRules.NoFreeSocket, auto.Detail);

        var explicitIndex = SocketOperations.TryInsert(full, 1, Gem(), "x", out _);
        Assert.Contains(SocketRules.Occupied, explicitIndex.Detail);
        // The two are deliberately NOT folded together: "add a socket" is the wrong advice when what
        // the player needs is to empty one.
        Assert.DoesNotContain(SocketRules.NoFreeSocket, explicitIndex.Detail);
    }

    [Fact]
    public void Socket_add_past_the_base_types_own_max_is_no_free_socket()
    {
        var at = Sockets(("", false, null), ("", false, null));
        var r = SocketOperations.TryAdd(at, entrySocketMax: 2, out _);
        Assert.Contains(SocketRules.NoFreeSocket, r.Detail);
    }

    [Fact]
    public void An_out_of_range_index_is_bad_param_value_not_a_socket_rule()
    {
        var one = Sockets(("", false, null));
        Assert.Equal(AtomRejectionReason.BadParamValue,
            SocketOperations.TryInsert(one, 5, Gem(), "x", out _).Reason);
        Assert.Equal(AtomRejectionReason.BadParamValue,
            SocketOperations.TryInsert(one, -1, Gem(), "x", out _).Reason);
        Assert.Equal(AtomRejectionReason.BadParamValue,
            SocketOperations.TryRemove(one, 9, out _).Reason);
    }

    [Fact]
    public void A_second_copy_of_a_unique_tagged_insert_is_a_duplicate_key()
    {
        var held = Sockets(("", false, "gem.unique.t5"), ("", false, null));
        var r = SocketOperations.TryInsert(held, 1, Gem("gem.unique.t5", unique: true), "x", out _);
        Assert.Equal(AtomRejectionReason.DuplicateKey, r.Reason);
    }

    [Fact]
    public void Removing_from_an_empty_socket_is_no_free_socket()
    {
        var r = SocketOperations.TryRemove(Sockets(("", false, null)), 0, out _);
        Assert.Contains(SocketRules.NoFreeSocket, r.Detail);
    }

    [Fact]
    public void A_socket_never_rejects_an_insert_for_element()
    {
        // "Wrong type" is deliberately absent from the table. Every socket accepts every insert.
        var sockets = Sockets(("fire", false, null));
        foreach (var element in new[] { "fire", "ice", "earth", "air", "light", "dark", "omni", "" })
        {
            var r = SocketOperations.TryInsert(sockets, 0, Gem($"gem.x-{element}.t1", element), "inst", out _);
            Assert.True(r.IsOk, $"element '{element}' was refused: {r.Detail}");
        }
    }

    // ── D24, socket-imbue ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Socket_imbue_sets_an_affinity_only_on_an_empty_crafted_socket()
    {
        var crafted = Sockets(("", true, null));
        Assert.True(SocketOperations.TryImbue(crafted, 0, "ice", out var next).IsOk);
        Assert.Equal("ice", next[0].Affinity);

        // Never on a filled socket — it would retroactively attune a committed insert.
        var filled = Sockets(("", true, "gem.a.t1"));
        Assert.Contains(SocketRules.NotImbuable, SocketOperations.TryImbue(filled, 0, "ice", out _).Detail);

        // Never on a drop-declared socket — that affinity is the base type's statement.
        var declared = Sockets(("fire", false, null));
        Assert.Contains(SocketRules.NotImbuable, SocketOperations.TryImbue(declared, 0, "ice", out _).Detail);
    }

    [Fact]
    public void Imbuing_to_omni_is_refused_because_omni_is_not_an_affinity()
    {
        var crafted = Sockets(("", true, null));
        var r = SocketOperations.TryImbue(crafted, 0, "omni", out _);
        Assert.Equal(AtomRejectionReason.BadParamValue, r.Reason);
        Assert.Contains("omni", r.Detail, StringComparison.Ordinal);
    }

    // ── The state transitions ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Socket_add_opens_a_crafted_empty_socket_at_the_next_index()
    {
        var start = Sockets(("fire", false, "gem.a.t1"));
        Assert.True(SocketOperations.TryAdd(start, entrySocketMax: 3, out var next).IsOk);

        Assert.Equal(2, next.Count);
        Assert.Equal(1, next[1].Index);
        Assert.True(next[1].Crafted);
        Assert.Equal("", next[1].Affinity);
        Assert.True(next[1].IsEmpty);
    }

    [Fact]
    public void Auto_pick_takes_the_lowest_empty_index_deterministically()
    {
        var partial = Sockets(("", false, "gem.a.t1"), ("", false, null), ("", false, null));
        Assert.True(SocketOperations.TryInsert(partial, null, Gem(), "inst-new", out var next).IsOk);
        Assert.Equal("gem.ember-shard.t3", next[1].InsertContainerId);
        Assert.True(next[2].IsEmpty);
    }

    [Fact]
    public void The_ssot_7_3_six_operation_sequence_replays_to_the_state_it_records()
    {
        // ssot-sockets.md §7.3's worked trail, run through the real transitions.
        IReadOnlyList<SocketSlot> s = Sockets(("earth", false, null), ("earth", false, null));

        Assert.True(SocketOperations.TryInsert(s, 0, Gem("gem.stone-heart.t3", "earth"), "i1", out s).IsOk);
        Assert.True(SocketOperations.TryInsert(s, 1, Gem("gem.stone-heart.t3", "earth"), "i2", out s).IsOk);
        Assert.True(SocketOperations.TryAdd(s, entrySocketMax: 4, out s).IsOk);
        Assert.True(SocketOperations.TryInsert(s, 2, Gem("gem.ember-shard.t3"), "i3", out s).IsOk);
        Assert.True(SocketOperations.TryRemove(s, 2, out s).IsOk);
        Assert.True(SocketOperations.TryInsert(s, 2, Gem("gem.stone-heart.t5", "earth"), "i4", out s).IsOk);

        Assert.Equal(3, s.Count);
        Assert.Equal(new[] { "gem.stone-heart.t3", "gem.stone-heart.t3", "gem.stone-heart.t5" },
            s.Select(x => x.InsertContainerId));
        Assert.True(s[2].Crafted);
    }

    [Fact]
    public void No_operation_can_reach_the_hosts_atom_rows()
    {
        // spec-sockets.md §1: nothing on the host is ever rewritten, and it is verifiable rather than
        // promised — no method here returns or accepts anything that could reach an atom row.
        foreach (var m in typeof(SocketOperations).GetMethods(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            Assert.NotEqual(typeof(AtomAppend), m.ReturnType);
            Assert.NotEqual(typeof(MutationResult), m.ReturnType);
            foreach (var p in m.GetParameters())
            {
                Assert.NotEqual(typeof(InstanceHead), p.ParameterType);
                Assert.NotEqual(typeof(MutationResult), p.ParameterType);
            }
        }
    }

    [Fact]
    public void Bind_ordinal_is_socket_index_plus_one_and_content_derived()
    {
        // spec-sockets.md §2. Two identical inserts in two sockets no longer tie under
        // (priority DESC, container_id ASC, seq ASC).
        Assert.Equal(1, SocketOperations.BindOrdinalFor(0));
        Assert.Equal(4, SocketOperations.BindOrdinalFor(3));
        Assert.NotEqual(SocketOperations.BindOrdinalFor(0), SocketOperations.BindOrdinalFor(1));
    }

    [Fact]
    public void Building_a_fill_refuses_an_unresolvable_insert_rather_than_skipping_it()
    {
        var sockets = Sockets(("fire", false, "gem.missing.t1"));
        var r = SocketOperations.TryBuildFill(sockets, new Dictionary<string, InsertDef>(), out var fill);
        Assert.Equal(AtomRejectionReason.UnknownContainer, r.Reason);
        Assert.Empty(fill);
    }

    // ── The shipped corpus ──────────────────────────────────────────────────────────────────────

    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void The_shipped_gem_corpus_carries_only_concrete_elements_or_omni_or_none()
    {
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "gems");
        var seen = 0;

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
            {
                seen++;
                if (!e.TryGetProperty("element", out var el)) continue;
                var element = el.GetString() ?? "";
                Assert.True(
                    element.Length == 0 || element == "omni"
                        || FusionRpg.Core.Stats.Derived.ElementRoster.TryParse(element, out _),
                    $"gem element '{element}' is not a concrete element, 'omni' or absent");
            }
        }

        // CONTRACT, not a count: the gem corpus grows with every generation, so a literal `seen` is
        // stale by construction. The invariant is that every shipped gem's element is concrete, 'omni'
        // or absent — asserted per row above — and that the sweep genuinely read a corpus.
        Assert.True(seen > 0, "the gem corpus read returned nothing");
    }

    [Fact]
    public void No_shipped_gem_declares_an_omni_affinity()
    {
        // `omni` is not an affinity (element-hub-ssot.md §4). This module's first run found one real
        // authoring defect — `gem.g1-007` carried `"affinityElement": "omni"` — and pinned the
        // offender list so it could not grow. The corpus has since been regenerated so it stops
        // forwarding an element pick into a gem's affinity (`39fbed34`, cause 8b/3), so the offender
        // set is now EMPTY. The assertion is the CONTRACT this test's own name states — no shipped gem
        // declares `omni` — never a count over a growing corpus (population-pin SE3.3, 2026-09-19).
        var dir = Path.Combine(RepoRoot(), "data", "seed", "items", "gems");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
                if (e.TryGetProperty("affinityElement", out var a) && a.GetString() == "omni")
                    offenders.Add(e.GetProperty("id").GetString()!);
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_legacy_socket_word_corpus_is_retired_not_merely_unread()
    {
        // SSH2.6 (`e79c0fde8`, 2026-09-20) retired the partition for real: `combogen-migrate --write`
        // replaced the 25 legacy `sockword.*` entries with the combination corpus and DELETED the file.
        // This test's earlier shape read that file and recorded that it "awaits module 21's
        // retirement"; the retirement has happened, so the surviving invariant is the ABSENCE — a
        // resurrected `sockwords.json` is a regression, and the 2026-09-20 merge did raise a
        // modify/delete conflict on this very path. The retired kind's own removal from
        // `KindCatalog.cs` and the ledger record's content are SSH2.6's tests' subject, not this one's.
        var path = Path.Combine(RepoRoot(), "data", "seed", "items", "socket-words", "sockwords.json");
        Assert.False(File.Exists(path),
            "the legacy sockwords.json was retired by SSH2.6 (combogen-migrate --write); it must not come back");

        // The retirement left its run record beside the former partition — losing that would make the
        // retirement indistinguishable from a hand deletion (SSH2.6's own boundary: "a verb, never a
        // hand deletion").
        Assert.True(File.Exists(Path.Combine(
            RepoRoot(), "data", "seed", "items", "socket-words", "combogen-migrate.ledger.json")));
    }
}
