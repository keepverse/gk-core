using FusionRpg.Core.Actions.Corpus;
using FusionRpg.Core.Items.Display;
using Xunit;
using FusionRpg.TestSupport;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// item-content <c>granted-action-text</c> (T14), the corpus-completeness guard — the same shape as
/// <c>ItemCardTests.Every_real_atom_renders_at_min_mid_and_max_with_no_raw_id</c>: iterate the REAL
/// shipped corpus, not a fixture, and assert every row resolves to text a player can read.
///
/// <para>What it pins, and why each half is here:</para>
/// <list type="number">
/// <item>Every committed action brief carries a <c>descriptionKey</c> — the parser now requires one,
/// so this is the corpus-side proof that no row slipped through with an empty string.</item>
/// <item>Every key resolves in <c>gk-content/content/display/en.json</c> to real, non-empty prose. A key that
/// resolves to nothing is <c>DisplayRules.MissingDisplayKey</c>, and card block 9 would render a
/// blank line under the action's name.</item>
/// <item>The resolved text is not the key, not an id, and carries no unsubstituted
/// <c>{placeholder}</c> — the "tooltips only a designer can read" failure mode
/// (<c>ssot-presentation.md</c> §8.1) that this whole program exists to prevent.</item>
/// </list>
///
/// <para>⚠ <b>The corpus is 24 rows, not 114.</b> <c>item-content-ideal.md</c> §6.2 (and the plan and
/// spec that quote it) say "114 actions across the committed corpus". Measured: <c>committed-round-1</c>
/// holds 19 and <c>committed-round-2</c> holds 5. 77 distinct named action ids exist across the whole
/// <c>gk-data/packs/fusion/data/seed/actions/</c> tree, but <c>_manifest.json</c> declares <c>_rounds/</c> and
/// <c>_candidates/</c> excluded from the committed corpus and <c>Program.cs</c> imports only the two
/// committed files. <c>ActionCorpusRealContentQualityTests</c> already asserts the same 24.</para>
/// </summary>
public class GrantedActionTextTests
{
    static string RepoRoot() => ContentRoot.Path;

    /// <summary>The two files <c>Program.cs</c>'s own corpus import reads — the committed corpus, and
    /// nothing else. <c>_rounds/</c> and <c>_candidates/</c> are pre-acceptance scratch that
    /// <c>gk-data/packs/fusion/data/seed/actions/_manifest.json</c> declares excluded.</summary>
    static readonly string[] CommittedCorpus = { "committed-round-1.json", "committed-round-2.json" };

    static IReadOnlyList<ActionCorpusBrief> RealBriefs() =>
        CommittedCorpus
            .SelectMany(f => ActionCorpusBriefJson.Parse(
                File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "actions", f))))
            .ToList();

    static Func<string, string?> RealStrings() => DisplayStringCatalog.Parse(
        File.ReadAllText(Path.Combine(KeepverseRoots.AuthoredContent(), "content", "display", "en.json")));

    [Fact]
    public void Every_committed_action_carries_a_description_key_that_resolves_to_real_prose()
    {
        var briefs = RealBriefs();
        // Liveness, not size. This asserted `Assert.Equal(24, briefs.Count)` and its own comment said
        // "liveness" — but the committed action corpus is a POPULATION that grows whenever content
        // ships, so pinning its size guards nothing: the day a 25th action lands the test fails and the
        // "fix" is to type 25. That is the validation-ssot rule this repo states outright, and it is
        // what happened here (24 -> 25, solid-remediation T0.3). What the assertion actually wants is
        // that the real files were found and parsed, which is what this says instead.
        Assert.NotEmpty(briefs);

        var strings = RealStrings();
        foreach (var brief in briefs)
        {
            Assert.False(string.IsNullOrWhiteSpace(brief.DescriptionKey),
                $"{brief.Id}: no descriptionKey authored");

            var text = strings(brief.DescriptionKey);
            Assert.False(string.IsNullOrWhiteSpace(text),
                $"{brief.Id}: descriptionKey '{brief.DescriptionKey}' resolves to nothing in content/display/en.json");

            Assert.NotEqual(brief.DescriptionKey, text);
            Assert.DoesNotContain(brief.Id, text!, StringComparison.Ordinal);
            Assert.DoesNotContain('{', text!);
            Assert.DoesNotContain('}', text!);

            // Prose, not a stat line. The shortest real description in the corpus is 65 characters;
            // a bare channel dump ("+45 hp") clears every check above and fails this one.
            Assert.True(text!.Length >= 40, $"{brief.Id}: description is too short to be prose — \"{text}\"");
            Assert.EndsWith(".", text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The action's NAME resolves too. Card block 9 renders name and description together
    /// (<c>ssot-presentation.md</c> §9.14), so a resolved description beside an unresolved name is
    /// still a broken block — and the key is the action id itself, which is what
    /// <c>RpgStore.ReadGrantedActions</c> now emits.
    /// </summary>
    [Fact]
    public void Every_committed_action_name_key_resolves_and_matches_the_corpus_name()
    {
        var strings = RealStrings();
        foreach (var brief in RealBriefs())
        {
            var text = strings(brief.Id);
            Assert.False(string.IsNullOrWhiteSpace(text),
                $"{brief.Id}: the action's own id is its name key and it resolves to nothing");
            Assert.Equal(brief.Name, text);
        }
    }

    /// <summary>
    /// The guard above must be capable of failing. A brief with an unauthored key has to be caught,
    /// or the loop over 24 green rows proves only that the loop ran.
    /// </summary>
    [Fact]
    public void The_guard_catches_an_unresolvable_description_key()
    {
        var strings = RealStrings();
        Assert.Null(strings("action.family.cactus.001.desc.not-authored"));
    }

    /// <summary>
    /// The parser refuses a corpus row with no <c>descriptionKey</c> rather than importing a card
    /// block that renders blank — the "named gap, never a silent one" rule this lane already uses.
    /// </summary>
    [Fact]
    public void A_brief_with_no_description_key_is_refused_at_parse()
    {
        const string json = """
            {
              "schemaVersion": 1,
              "kind": "action-seed",
              "entries": [
                {
                  "id": "action.general.9999",
                  "name": "Nameless Thing",
                  "category": "attack",
                  "scope": "general",
                  "rungBand": [1, 4],
                  "atomFamilies": ["atom.vitality"],
                  "targetMode": "self",
                  "relation": "enemy"
                }
              ]
            }
            """;

        var ex = Assert.Throws<ActionCorpusBriefRejection>(() => ActionCorpusBriefJson.Parse(json));
        Assert.Contains("descriptionKey", ex.Message, StringComparison.Ordinal);
    }
}
