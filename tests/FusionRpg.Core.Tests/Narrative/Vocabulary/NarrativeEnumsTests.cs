using System;
using System.Linq;
using FusionRpg.Core.Narrative.Vocabulary;
using Xunit;

namespace FusionRpg.Core.Tests.Narrative.Vocabulary;

/// <summary>
/// npc-story-events `narrative-vocabulary` (`spec-narrative-vocabulary.md` §3, §4): the runtime-only closed
/// vocabularies and the structural bounds. Each member count is a DECLARATION pinned with the reason it is
/// closed — widening one is a reviewed change — and every enum's wire id is pinned, because a stored row
/// carries the id and never the ordinal. No test here reads a seed file or counts a population.
/// </summary>
[Trait("VerificationId", "core.narrative")]
public class NarrativeEnumsTests
{
    // ---- declarations: each count pinned with the reason it is closed ------------------------------

    [Fact]
    public void HostClockKind_has_four_members() =>
        // The game's three clocks (Delve room, world turn, expedition collect) plus the homeworld return;
        // map principle 4 forbids a fourth real-time clock, so this grows only with a new PLACE.
        Assert.Equal(4, Enum.GetValues(typeof(HostClockKind)).Length);

    [Fact]
    public void StoryScope_has_two_members() =>
        // Save scope (a mechanic first seen) and World scope (one world's story, which outlives the
        // world's own attention and outcome). Ideal §6.7.
        Assert.Equal(2, Enum.GetValues(typeof(StoryScope)).Length);

    [Fact]
    public void CharacterFate_has_four_members() =>
        // The character's OWN story fate, written only by a storylet outcome or a join. Dormancy and a
        // fallen world are WorldNarrativePhase, never a fate (owner ruling 2026-09-19 round 4). Ideal §6.3.
        Assert.Equal(4, Enum.GetValues(typeof(CharacterFate)).Length);

    [Fact]
    public void CharacterState_has_five_members() =>
        // CharacterFate plus whether a `met` fact exists; derived, never stored. It is the value domain of
        // the CharacterStateIs leaf.
        Assert.Equal(5, Enum.GetValues(typeof(CharacterState)).Length);

    [Fact]
    public void WorldNarrativePhase_has_three_members() =>
        // Live / Dormant / Frozen — the narrative reading of world-continuity's two axes. There is no
        // "abandoned": world-continuity deletes nothing and has no abandon state (round-4 ruling).
        Assert.Equal(3, Enum.GetValues(typeof(WorldNarrativePhase)).Length);

    [Fact]
    public void StoryFactKind_has_twenty_seven_members() =>
        // The map's fact list plus the facts a ledger needs to derive fate, pins and scene eligibility
        // without a second store: 22 at first ship plus sanctum.returned, quest.progressed,
        // mechanic.first-seen, reward.owed and reward.paid (Alignment 2026-09-20).
        Assert.Equal(27, Enum.GetValues(typeof(StoryFactKind)).Length);

    // ---- wire ids: lower case, kebab/dotted, unique, never a member name, never an ordinal ---------

    static (string Name, string[] Ids, string[] MemberNames, Func<string, string?> RoundTrip)[] WireIdSets =
    {
        WireIds<HostClockKind>(x => x.ToId(), id => HostClockKindIds.TryParse(id, out var v) ? v.ToId() : null),
        WireIds<StoryScope>(x => x.ToId(), id => StoryScopeIds.TryParse(id, out var v) ? v.ToId() : null),
        WireIds<CharacterFate>(x => x.ToId(), id => CharacterFateIds.TryParse(id, out var v) ? v.ToId() : null),
        WireIds<CharacterState>(x => x.ToId(), id => CharacterStateIds.TryParse(id, out var v) ? v.ToId() : null),
        WireIds<WorldNarrativePhase>(x => x.ToId(), id => WorldNarrativePhaseIds.TryParse(id, out var v) ? v.ToId() : null),
        WireIds<StoryFactKind>(x => x.ToId(), id => StoryFactKindIds.TryParse(id, out var v) ? v.ToId() : null),
    };

    static (string Name, string[] Ids, string[] MemberNames, Func<string, string?> RoundTrip) WireIds<TEnum>(
        Func<TEnum, string> toId, Func<string, string?> roundTrip) where TEnum : struct, Enum
    {
        var values = Enum.GetValues<TEnum>();
        return (typeof(TEnum).Name, values.Select(toId).ToArray(), values.Select(v => v.ToString()).ToArray(), roundTrip);
    }

    [Fact]
    public void Every_wire_id_is_lowercase_kebab_case_and_unique()
    {
        foreach (var vocab in WireIdSets)
        {
            Assert.Equal(vocab.Ids.Length, vocab.Ids.Distinct(StringComparer.Ordinal).Count());
            foreach (var id in vocab.Ids)
            {
                // kebab-case, dot-separated families (flag.set, quest.offered, mechanic.first-seen).
                Assert.Matches("^[a-z][a-z0-9-]*([.][a-z][a-z0-9-]*)*$", id);
                Assert.DoesNotContain("--", id, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Every_wire_id_round_trips_and_an_unknown_id_fails()
    {
        foreach (var vocab in WireIdSets)
        {
            foreach (var id in vocab.Ids)
                Assert.Equal(id, vocab.RoundTrip(id));

            // An unknown id, an empty id, a member NAME (never a wire id) and an ordinal all fail: stored
            // rows carry the wire id, so a row that carried the ordinal or the C# name must not parse.
            Assert.Null(vocab.RoundTrip("nonsense"));
            Assert.Null(vocab.RoundTrip(string.Empty));
            Assert.Null(vocab.RoundTrip("0"));
            foreach (var memberName in vocab.MemberNames)
                Assert.Null(vocab.RoundTrip(memberName));
        }
    }

    [Fact]
    public void An_undefined_member_has_no_wire_id()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ((HostClockKind)99).ToId(); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ((StoryScope)99).ToId(); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ((CharacterFate)99).ToId(); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ((CharacterState)99).ToId(); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ((WorldNarrativePhase)99).ToId(); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = ((StoryFactKind)99).ToId(); });
    }

    [Fact]
    public void Host_clock_ids_are_the_four_tuning_clock_keys()
    {
        // `cooldown.perStorylet.{clock}` / `cooldown.perKind.{clock}` in data/tuning/narrative.v{n}.json are
        // these four strings (spec §4), so a rename here is a data migration, not a refactor.
        Assert.Equal(
            new[] { "delve.room", "world.turn", "expedition.collect", "sanctum.return" },
            Enum.GetValues<HostClockKind>().Select(x => x.ToId()).ToArray());
    }

    [Fact]
    public void Story_fact_kind_ids_are_the_ledger_wire_ids()
    {
        // The 27 ids a story-ledger row's `kind` column may carry, in §3's own group order.
        Assert.Equal(
            new[]
            {
                "met", "helped", "refused", "betrayed", "spared",
                "flag.set", "chapter.reached", "arc.started",
                "storylet.seen", "choice.picked",
                "scene.acknowledged",
                "sanctum.returned",
                "mechanic.first-seen",
                "reward.owed", "reward.paid",
                "character.joined", "character.departed", "character.fell",
                "quest.offered", "quest.completed", "quest.failed", "quest.abandoned", "quest.expired", "quest.progressed",
                "sector.lost", "delve.wiped", "siege.failed",
            },
            Enum.GetValues<StoryFactKind>().Select(x => x.ToId()).ToArray());
    }

    // ---- WorldNarrativePhases.Of: a case per input pair, the whole 3x3 domain ----------------------

    [Theory]
    [InlineData("active", "contested", WorldNarrativePhase.Live)]
    [InlineData("active", "won", WorldNarrativePhase.Live)]
    [InlineData("active", "fallen", WorldNarrativePhase.Frozen)]
    [InlineData("hibernating", "contested", WorldNarrativePhase.Dormant)]
    [InlineData("hibernating", "won", WorldNarrativePhase.Dormant)]
    [InlineData("hibernating", "fallen", WorldNarrativePhase.Frozen)]
    [InlineData("idle", "contested", WorldNarrativePhase.Dormant)]
    [InlineData("idle", "won", WorldNarrativePhase.Dormant)]
    [InlineData("idle", "fallen", WorldNarrativePhase.Frozen)]
    public void Of_maps_every_attention_and_outcome_pair(string attention, string outcome, WorldNarrativePhase expected) =>
        // `fallen` wins over attention: a fallen world is read-only history whatever its attention column
        // says (owner ruling 2026-09-19 round 4).
        Assert.Equal(expected, WorldNarrativePhases.Of(attention, outcome));

    [Fact]
    public void Of_rejects_an_unknown_attention_rather_than_defaulting()
    {
        var ex = Assert.Throws<ArgumentException>(() => WorldNarrativePhases.Of("sleeping", "contested"));
        Assert.Contains("sleeping", ex.Message, StringComparison.Ordinal);

        // Rejected even when the outcome alone would decide: an unreadable row is a defect, and guessing
        // Live would draw story at a world the player cannot see.
        Assert.Throws<ArgumentException>(() => WorldNarrativePhases.Of("sleeping", "fallen"));
    }

    [Fact]
    public void Of_rejects_an_unknown_outcome_rather_than_defaulting()
    {
        var ex = Assert.Throws<ArgumentException>(() => WorldNarrativePhases.Of("active", "forgotten"));
        Assert.Contains("forgotten", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// `spec-narrative-vocabulary.md` §4, "Structural bounds, not tunables": each value is a `const` with a
/// comment saying why changing it breaks the design contract rather than rebalancing it. Pinned here so a
/// later edit to one is a reviewed change.
/// </summary>
[Trait("VerificationId", "core.narrative")]
public class SelectionBoundsTests
{
    [Fact]
    public void Structural_bounds_are_the_specs_values()
    {
        // One spine beat per pulse IS the tier rule; one conversation per character per return is the Hades
        // rule the hub host is built on; 2/4 are the storylet contract's choice floor and ceiling.
        Assert.Equal(1, SelectionBounds.SpineBeatsPerPulse);
        Assert.Equal(1, SelectionBounds.ConversationsPerCharacterPerReturn);
        Assert.Equal(2, SelectionBounds.MinChoices);
        Assert.Equal(4, SelectionBounds.MaxChoices);
        Assert.True(SelectionBounds.MinChoices < SelectionBounds.MaxChoices);

        // 1000 per-mille is certainty: the end of the bounded ratio `min(1000, base + n x step)`.
        Assert.Equal(1000L, SelectionBounds.FireChance.CertainMilli);
    }

    [Fact]
    public void Milli_bounds_are_long_and_counts_are_int()
    {
        // Numeric types (spec "Numeric types"): every *Milli is long, because `base + n x step` grows with n
        // and int per-mille exceeds its range at Theta 3213; counts of a closed vocabulary are int.
        Assert.Equal(typeof(long), typeof(SelectionBounds.FireChance).GetField(nameof(SelectionBounds.FireChance.CertainMilli))!.FieldType);
        Assert.Equal(typeof(int), typeof(SelectionBounds).GetField(nameof(SelectionBounds.SpineBeatsPerPulse))!.FieldType);
        Assert.Equal(typeof(int), typeof(SelectionBounds).GetField(nameof(SelectionBounds.MinChoices))!.FieldType);
        Assert.Equal(typeof(int), typeof(SelectionBounds).GetField(nameof(SelectionBounds.MaxChoices))!.FieldType);
        Assert.Equal(typeof(int), typeof(SelectionBounds).GetField(nameof(SelectionBounds.ConversationsPerCharacterPerReturn))!.FieldType);
    }
}
