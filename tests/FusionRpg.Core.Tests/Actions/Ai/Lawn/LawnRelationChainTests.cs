using System;
using System.Collections.Generic;
using FusionRpg.Contracts;
using FusionRpg.Core.Actions.Ai.Lawn;
using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai.Lawn;

/// <summary>
/// combat-ai `lawn-actor-view` (CAI4.1, spec-lawn-actor-view.md "The relation chain"): the composed
/// oracle. What it owns is the ORDER and the null contract — not either link's rule.
/// </summary>
public class LawnRelationChainTests
{
    /// <summary>A link that answers only for the ptrs it was given, and counts how often it was asked,
    /// so the precedence and the short-circuit are proven rather than assumed.</summary>
    sealed class StubLink : IOwnSideOracle
    {
        readonly Dictionary<string, RelationKind> _answers;
        public int Calls;

        public StubLink(params (string Ptr, RelationKind Relation)[] answers)
        {
            _answers = new Dictionary<string, RelationKind>(StringComparer.Ordinal);
            foreach (var (ptr, relation) in answers) _answers[ptr] = relation;
        }

        public RelationKind? RelationOf(string ptr)
        {
            Calls++;
            return _answers.TryGetValue(ptr, out var relation) ? relation : null;
        }
    }

    /// <summary>Hypnosis: the player DEPLOYED this creature, but it sits on the zombie side, so the two
    /// oracles disagree and both are individually right. Specimen ownership must win.</summary>
    [Fact]
    public void Specimen_ownership_wins_when_both_links_can_answer()
    {
        var specimen = new StubLink(("hypno:1", RelationKind.Ally));
        var mechanical = new StubLink(("hypno:1", RelationKind.Enemy));

        var chain = new LawnRelationChain(specimen, mechanical);

        Assert.Equal(RelationKind.Ally, chain.RelationOf("hypno:1"));
        Assert.Equal(1, specimen.Calls);
        Assert.Equal(0, mechanical.Calls); // short-circuit: a second read cannot contradict the first
    }

    [Fact]
    public void The_chain_falls_through_to_the_mechanical_link_when_the_specimen_links_answer_is_null()
    {
        var specimen = new StubLink();
        var mechanical = new StubLink(("plant:7", RelationKind.Self));

        var chain = new LawnRelationChain(specimen, mechanical);

        Assert.Equal(RelationKind.Self, chain.RelationOf("plant:7"));
        Assert.Equal(1, specimen.Calls);
        Assert.Equal(1, mechanical.Calls);
    }

    /// <summary>A raw vanilla unit: no owner row, no mechanical read. The chain returns null — "I
    /// cannot tell" — and the CALLER resolves it to Enemy; defaulting here would erase the distinction
    /// between "not mine" and "unknown".</summary>
    [Fact]
    public void An_unknown_ptr_returns_null_and_is_never_defaulted()
    {
        var specimen = new StubLink();
        var mechanical = new StubLink();
        var chain = new LawnRelationChain(specimen, mechanical);

        Assert.Null(chain.RelationOf("vanilla:unowned"));
        Assert.Equal(1, specimen.Calls);
        Assert.Equal(1, mechanical.Calls); // asked, answered nothing — still not a default here
    }

    [Fact]
    public void Both_links_are_required()
    {
        var link = new StubLink();
        Assert.Throws<ArgumentNullException>(() => new LawnRelationChain(null!, link));
        Assert.Throws<ArgumentNullException>(() => new LawnRelationChain(link, null!));
    }
}
