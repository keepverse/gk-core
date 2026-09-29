using System.IO;
using System.Linq;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Binding;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Binding;

/// <summary>Task B4 — `AffixComposer` (spec-tree-binder.md §1). Verified against the REAL shipped
/// affix registry (`gk-data/packs/fusion/data/seed/effects/affixes/all.json`), not only synthetic fixtures.</summary>
public class AffixComposerTests
{
    static string RepoRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir is not null && !File.Exists(Path.Combine(dir, "CONTRIBUTING.md")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    static SeedContent LoadRealSeedContent()
    {
        var root = RepoRoot();
        var files = new[]
        {
            Path.Combine(root, "data", "seed", "effects", "affixes", "all.json"),
            Path.Combine(root, "data", "seed", "atoms", "fx-board.json"),
            Path.Combine(root, "data", "seed", "atoms", "fx-core.json"),
            Path.Combine(root, "data", "seed", "atoms", "fx-status.json"),
        }.Where(File.Exists).Select(f => (f, File.ReadAllText(f))).ToArray();

        var collected = AtomSeedFile.Collect(files);
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));
        return collected.Content;
    }

    static (System.Collections.Generic.IReadOnlyDictionary<string, AffixRow> Affixes,
           System.Collections.Generic.IReadOnlyDictionary<string, AtomRow> Atoms) Index(SeedContent content) =>
        (content.Affixes.ToDictionary(a => a.AffixId), content.Atoms.ToDictionary(a => a.AtomId));

    [Fact]
    public void The_real_frostbite_venom_affix_resolves_to_its_two_atom_refs_in_seq_order()
    {
        var content = LoadRealSeedContent();
        var (affixes, atoms) = Index(content);

        // The reflect-shaped case §1 names by name: a node needing two atoms that must arrive
        // together, expressed as ONE affix, never two separate node atoms. Both refs here are
        // status.apply atoms (cold-on-hit seq 0, poison-on-hit seq 1) — a different param shape
        // from stat.modify's channel/op/amount (no "channel" key at all), which is exactly why
        // AffixComposer.ParseAtom must default gracefully rather than assume every kind has one.
        var resolved = AffixComposer.Resolve(
            new[] { "affix.authored.affix-draw-000" }, affixes, atoms);

        Assert.Equal(2, resolved.Count);
        Assert.Equal("status.apply", resolved[0].KindId);
        Assert.Equal("status.apply", resolved[1].KindId);
        // Neither carries a "channel" key in its real seed params -- resolved gracefully to empty,
        // never a crash or a fabricated value.
        Assert.Equal("", resolved[0].ChannelId);
        Assert.Equal("", resolved[1].ChannelId);
        Assert.Equal("OnDamageDealt", resolved[0].Trigger);
    }

    [Fact]
    public void Two_or_three_affixIds_resolve_and_concatenate_in_authored_order()
    {
        var content = LoadRealSeedContent();
        var (affixes, atoms) = Index(content);

        var resolved = AffixComposer.Resolve(
            new[] { "affix.authored.affix-draw-000", "affix.authored.affix-draw-001" }, affixes, atoms);

        // draw-000 has 2 refs, draw-001 has 2 refs -> 4 total, in that order.
        Assert.Equal(4, resolved.Count);
    }

    [Fact]
    public void Zero_or_four_affixIds_is_refused()
    {
        var (affixes, atoms) = Index(LoadRealSeedContent());

        Assert.Throws<BindRefusal>(() => AffixComposer.Resolve(Array.Empty<string>(), affixes, atoms));
        Assert.Throws<BindRefusal>(() => AffixComposer.Resolve(
            new[] { "a", "b", "c", "d" }, affixes, atoms));
    }

    [Fact]
    public void A_missing_affix_id_is_refused_naming_it()
    {
        var (affixes, atoms) = Index(LoadRealSeedContent());

        var ex = Assert.Throws<BindRefusal>(() =>
            AffixComposer.Resolve(new[] { "affix.does-not-exist" }, affixes, atoms));
        Assert.Contains("affix.does-not-exist", ex.Message);
    }

    [Fact]
    public void A_missing_atom_id_referenced_by_a_real_affix_is_refused_naming_it()
    {
        var content = LoadRealSeedContent();
        var (affixes, _) = Index(content);
        var emptyAtoms = new System.Collections.Generic.Dictionary<string, AtomRow>();

        var ex = Assert.Throws<BindRefusal>(() =>
            AffixComposer.Resolve(new[] { "affix.authored.affix-draw-000" }, affixes, emptyAtoms));
        Assert.Contains("atom.fx-cold-on-hit.t1", ex.Message);
    }

    /// <summary>D56 (spec-element-conversion.md, 2026-09-07): `element.convert` shipped as a real,
    /// registered kind. Resolves successfully — exactly like `status.apply` already does — with EMPTY
    /// channel/op, since its real params (`fromElement`/`toElement`/`shareMilli`) carry no `channel`/
    /// `op` at all; `AffixComposer` degrades gracefully for a non-channel-writing kind rather than
    /// crashing, matching the documented `status.apply` precedent. Superseded 2026-09-07: this test
    /// used to prove the OPPOSITE (a refusal) before the kind existed; kept renamed rather than
    /// deleted, since the fixture and the "why not refused any more" reasoning are still useful.</summary>
    [Fact]
    public void A_conversion_kind_atom_resolves_successfully_now_that_D56_shipped_it()
    {
        var affix = new AffixRow("affix.synthetic", null,
            new[] { new AffixRefRow(0, "atom.synthetic-convert") });
        var conversionAtom = new AtomRow
        {
            AtomId = "atom.synthetic-convert",
            KindId = "element.convert", // one of the 18 registered kinds since D56 (2026-09-07)
            FamilyId = "atom.synthetic-convert",
            Tier = 1,
            Name = "synthetic",
            ParamsJson = """{"toElement":"ice","shareMilli":400}""",
            WhenJson = "{}",
        };
        var affixes = new System.Collections.Generic.Dictionary<string, AffixRow> { [affix.AffixId] = affix };
        var atoms = new System.Collections.Generic.Dictionary<string, AtomRow> { [conversionAtom.AtomId] = conversionAtom };

        var resolved = AffixComposer.Resolve(new[] { affix.AffixId }, affixes, atoms);

        var atomResult = Assert.Single(resolved);
        Assert.Equal("element.convert", atomResult.KindId);
        Assert.Equal("", atomResult.ChannelId); // no channel field in element.convert's own params
        Assert.Equal("", atomResult.Op);
    }

    [Fact]
    public void An_unregistered_non_conversion_kind_is_refused_generically()
    {
        var affix = new AffixRow("affix.synthetic2", null,
            new[] { new AffixRefRow(0, "atom.synthetic-bad-kind") });
        var badAtom = new AtomRow
        {
            AtomId = "atom.synthetic-bad-kind",
            KindId = "not.a.real.kind",
            FamilyId = "atom.synthetic-bad-kind",
            Tier = 1,
            Name = "synthetic",
            ParamsJson = """{"channel":"atk","op":"flat"}""",
            WhenJson = "{}",
        };
        var affixes = new System.Collections.Generic.Dictionary<string, AffixRow> { [affix.AffixId] = affix };
        var atoms = new System.Collections.Generic.Dictionary<string, AtomRow> { [badAtom.AtomId] = badAtom };

        var ex = Assert.Throws<BindRefusal>(() =>
            AffixComposer.Resolve(new[] { affix.AffixId }, affixes, atoms));
        Assert.Contains("not.a.real.kind", ex.Message);
        Assert.DoesNotContain("18th atom kind", ex.Message);
    }

    [Fact]
    public void A_registered_kind_and_channel_resolve_correctly()
    {
        var affix = new AffixRow("affix.synthetic3", null,
            new[] { new AffixRefRow(0, "atom.synthetic-ok") });
        var okAtom = new AtomRow
        {
            AtomId = "atom.synthetic-ok",
            KindId = "stat.modify",
            FamilyId = "atom.synthetic-ok",
            Tier = 1,
            Name = "synthetic",
            ParamsJson = """{"channel":"atk","op":"flat","amount":{"min":1,"max":1}}""",
            WhenJson = "{}",
        };
        var affixes = new System.Collections.Generic.Dictionary<string, AffixRow> { [affix.AffixId] = affix };
        var atoms = new System.Collections.Generic.Dictionary<string, AtomRow> { [okAtom.AtomId] = okAtom };

        var resolved = AffixComposer.Resolve(new[] { affix.AffixId }, affixes, atoms);

        var atom = Assert.Single(resolved);
        Assert.Equal("stat.modify", atom.KindId);
        Assert.Equal("atk", atom.ChannelId);
        Assert.Equal("flat", atom.Op);
    }

    /// <summary>P4.1 (tasks/passive-tree-repair-plan.md, R3). The crash: `ParseAtom` read
    /// `params.channel` with `JsonElement.GetString()`, which throws `InvalidOperationException` on a
    /// JSON object — and E30's pool-reference form (`{"pool":"...","count":1}`) IS an object. That
    /// exception is not a `BindRefusal`, so `BindTree`'s catch did not see it and the whole binder run
    /// died (reproduced 2026-09-13: exit -532462766 on the first pool-shaped family). Eight generated
    /// `stat.derived` families carry this shape and the language stage picked them 461 times.
    /// <para>A pool reference is not a concrete channel, so this binder cannot price it: the roll
    /// belongs to effect-pipeline module 2 and a pool's price is a weighted MEAN over members, never
    /// one member. The refusal must therefore be a NAMED `BindRefusal`, never a crash and never a
    /// silently chosen member.</para></summary>
    [Fact]
    public void A_pool_shaped_channel_is_refused_by_name_never_crashing()
    {
        var affix = new AffixRow("affix.synthetic-pool", null,
            new[] { new AffixRefRow(0, "atom.synthetic-pool") });
        var poolAtom = new AtomRow
        {
            AtomId = "atom.synthetic-pool",
            KindId = "stat.derived",
            FamilyId = "atom.synthetic-pool",
            Tier = 1,
            Name = "synthetic pool",
            ParamsJson = """{"channel":{"pool":"pool.element-defense","count":1,"allowRepeat":false},"op":"flat","amount":{"min":1,"max":1}}""",
            WhenJson = "{}",
        };
        var affixes = new System.Collections.Generic.Dictionary<string, AffixRow> { [affix.AffixId] = affix };
        var atoms = new System.Collections.Generic.Dictionary<string, AtomRow> { [poolAtom.AtomId] = poolAtom };

        var ex = Assert.Throws<BindRefusal>(() => AffixComposer.Resolve(new[] { affix.AffixId }, affixes, atoms));
        Assert.Contains("pool.element-defense", ex.Message);
        Assert.Contains("pool", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The other half of P4.1: a malformed `channel` value (neither a string nor a valid pool
    /// object) is also a named refusal, not a raw `JsonException`/`InvalidOperationException` escaping
    /// as an unhandled crash.</summary>
    [Fact]
    public void A_malformed_channel_value_is_refused_by_name_never_crashing()
    {
        var affix = new AffixRow("affix.synthetic-bad-channel", null,
            new[] { new AffixRefRow(0, "atom.synthetic-bad-channel") });
        var badAtom = new AtomRow
        {
            AtomId = "atom.synthetic-bad-channel",
            KindId = "stat.modify",
            FamilyId = "atom.synthetic-bad-channel",
            Tier = 1,
            Name = "synthetic bad channel",
            ParamsJson = """{"channel":{"count":1},"op":"flat","amount":{"min":1,"max":1}}""",
            WhenJson = "{}",
        };
        var affixes = new System.Collections.Generic.Dictionary<string, AffixRow> { [affix.AffixId] = affix };
        var atoms = new System.Collections.Generic.Dictionary<string, AtomRow> { [badAtom.AtomId] = badAtom };

        var ex = Assert.Throws<BindRefusal>(() => AffixComposer.Resolve(new[] { affix.AffixId }, affixes, atoms));
        // Discriminating, not just "names channel": the pool shape must be refused for BEING a pool
        // (no 'pool' id, so ChannelRefJson fails the pool-object rule), and must name the atom so the
        // author can find it. Asserting the word "channel" alone would pass on any channel error.
        Assert.Contains(badAtom.AtomId, ex.Message);
        Assert.Contains("channel", ex.Message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pool", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
