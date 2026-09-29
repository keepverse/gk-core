using System.Reflection;
using FusionRpg.Core.Narrative.Doctrine;
using FusionRpg.Core.Narrative.Vocabulary;
using Xunit;

namespace FusionRpg.Core.Tests.Narrative.Doctrine;

/// <summary>
/// npc-story-events NR6.1 (spec-counter-doctrine.md §3): the doctrine vocabulary is closed and reviewed,
/// no doctrine key may name a magnitude (R13's "never how strong"), and the view a consumer reads exposes
/// only element biases and order weights.
///
/// <para>The committed file is the subject: its rows are the reviewed vocabulary, so the test compares the
/// file's ids against <see cref="DoctrinesCatalog.ReviewedIds"/> rather than against a second literal list.
/// Every refusal case is a document built in memory, never written to disk.</para>
/// </summary>
[Trait("VerificationId", "core.narrative")]
[Trait("Guard", "narrative")]
public sealed class DoctrineCatalogTests
{
    static string RegistryDir() => Path.Combine(RepoRoot(), "data", "seed", "narrative", "_registry");

    static string CommittedText() => File.ReadAllText(Path.Combine(RegistryDir(), "doctrines.v1.json"));

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "seed", "dungeon"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }

    static IReadOnlyList<DoctrineDef> Parse() => DoctrineCatalog.Parse(CommittedText());

    // ---- the reviewed vocabulary, pinned with its reason -------------------------------------------

    [Fact]
    public void The_reviewed_vocabulary_is_closed_and_pinned_with_its_reason()
    {
        // spec-counter-doctrine.md §3's table: six `ward.{element}` (one per shipped element, each
        // answering the element the ring says resists it) plus `siegecraft` and `raiders`. Eight, and a
        // ninth is a reviewed change to that table.
        var file = Parse().Select(d => d.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            DoctrinesCatalog.ReviewedIds.OrderBy(id => id, StringComparer.Ordinal),
            file);
        Assert.Equal(8, DoctrinesCatalog.ReviewedIds.Count);
        Assert.Equal(
            new[] { "fire", "ice", "air", "earth", "light", "dark" }.OrderBy(id => id, StringComparer.Ordinal),
            DoctrinesCatalog.ReviewedIds.Where(id => id.StartsWith("ward.", StringComparison.Ordinal))
                .Select(id => id["ward.".Length..]).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_reviewed_row_carries_an_effect_and_configures()
    {
        var rows = Parse();

        Assert.All(rows, row => Assert.True(
            row.SpeciesElementBiasMilli.Count > 0 || row.OrderWeightMilli.Count > 0,
            $"{row.Id} changes nothing"));

        DoctrinesCatalog.Configure(rows);
        Assert.Equal(8, DoctrinesCatalog.All.Count);
        Assert.True(DoctrinesCatalog.IsKnown("ward.fire"));
        Assert.Equal("ward.fire", DoctrinesCatalog.Get("ward.fire").Id);
        Assert.False(DoctrinesCatalog.IsKnown("ward.plasma"));
    }

    [Fact]
    public void Every_ward_answers_the_element_the_ring_says_resists_it()
    {
        // The bias is a RELATION, not a pinned number: a ward names a resisting element above neutral and
        // the warded element below it. Which element resists which is the ring's, so a ring change moves
        // this test rather than contradicting it.
        foreach (var row in Parse().Where(d => d.Id.StartsWith("ward.", StringComparison.Ordinal)))
        {
            var warded = row.Id["ward.".Length..];
            var strongest = row.SpeciesElementBiasMilli.OrderByDescending(p => p.Value).First().Key;

            Assert.NotEqual(warded, strongest);
            Assert.True(row.SpeciesElementBiasMilli[warded] < 1000, $"{row.Id} must bias away from {warded}");
            Assert.True(row.SpeciesElementBiasMilli[strongest] > 1000, $"{row.Id} must bias toward {strongest}");
        }
    }

    // ---- never how strong --------------------------------------------------------------------------

    [Theory]
    [InlineData("\"maxHp\": 5000")]
    [InlineData("\"atk\": 900")]
    [InlineData("\"level\": 40")]
    [InlineData("\"rarity\": \"mythic\"")]
    public void A_row_key_that_names_a_magnitude_is_refused_by_rule(string planted)
    {
        var json = CommittedText().Replace("\"negative\":", planted + ", \"negative\":", StringComparison.Ordinal);

        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => DoctrineCatalog.Parse(json));

        Assert.Contains(DoctrineCatalog.MagnitudeKeyRule, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"speciesElementBiasMilli\": { \"plasma\": 1400 }")]
    [InlineData("\"speciesElementBiasMilli\": { \"Fire\": 1400 }")]
    [InlineData("\"orderWeightMilli\": { \"teleport\": 1400 }")]
    public void An_effect_key_outside_its_closed_vocabulary_is_refused(string planted)
    {
        var json = CommittedText().Replace("\"orderWeightMilli\": { \"assault\": 1500, \"clear\": 1250, \"move\": 900 }", planted, StringComparison.Ordinal);

        var ex = Assert.Throws<NarrativeVocabularyRejection>(() => DoctrineCatalog.Parse(json));

        Assert.Contains(DoctrineCatalog.MagnitudeKeyRule, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_two_families_key_against_the_vocabularies_that_already_exist()
    {
        // The closure is not a new spelling: elements are the shipped element table's ids and order kinds
        // are `WorldCommandKinds.All`.
        Assert.Equal(6, DoctrineCatalog.ElementKeys.Count);
        Assert.Contains("fire", DoctrineCatalog.ElementKeys);
        Assert.Contains("dark", DoctrineCatalog.ElementKeys);
        Assert.Contains("assault", DoctrineCatalog.OrderKeys);
        Assert.Contains("clear", DoctrineCatalog.OrderKeys);
        Assert.DoesNotContain("Fire", DoctrineCatalog.ElementKeys);
    }

    // ---- the view exposes only biases and weights --------------------------------------------------

    [Fact]
    public void The_view_exposes_only_biases_and_weights()
    {
        var properties = typeof(DoctrineView)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "DoctrineId", "OrderWeightMilli", "SpeciesElementBiasMilli" }.OrderBy(n => n, StringComparer.Ordinal),
            properties);

        // And no member's name suggests a magnitude the doctrine is forbidden to carry.
        Assert.DoesNotContain(typeof(DoctrineView).GetMembers(BindingFlags.Public | BindingFlags.Instance), m =>
            m.Name.Contains("Hp", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Attack", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Damage", StringComparison.OrdinalIgnoreCase)
            || m.Name.Contains("Rarity", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_view_reads_a_named_bias_and_weights_an_unnamed_one_neutral()
    {
        DoctrinesCatalog.Configure(Parse());
        var view = DoctrineView.Of(DoctrinesCatalog.Get("siegecraft"));

        Assert.Equal("siegecraft", view.DoctrineId);
        Assert.Equal(1500, view.OrderWeightFor("assault"));
        Assert.Equal(1250, view.OrderWeightFor("clear"));
        Assert.Equal(1000, view.OrderWeightFor("build"));       // unnamed ⇒ neutral, not zero
        Assert.Equal(1000, view.ElementBiasFor("fire"));        // siegecraft biases no element
    }

    [Fact]
    public void A_row_outside_the_reviewed_set_or_missing_from_it_is_refused()
    {
        var rows = Parse();

        var stranger = rows.Append(new DoctrineDef(
            "ward.plasma", "x", "y",
            new Dictionary<string, int> { ["fire"] = 1200 }, new Dictionary<string, int>())).ToList();
        Assert.Throws<NarrativeVocabularyRejection>(() => DoctrinesCatalog.Configure(stranger));

        Assert.Throws<NarrativeVocabularyRejection>(() => DoctrinesCatalog.Configure(rows.Skip(1).ToList()));
    }
}
