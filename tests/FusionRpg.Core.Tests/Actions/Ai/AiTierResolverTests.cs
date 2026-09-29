using System;
using System.Collections.Generic;
using System.Reflection;
using FusionRpg.Core.Actions.Ai;
using Xunit;

namespace FusionRpg.Core.Tests.Actions.Ai;

/// <summary>
/// combat-ai `ai-tiers-personality` (module 3, CAI1.9, spec-ai-tiers-personality.md): D6's class
/// split as the ONLY place a tier is decided, and D2's "never a difficulty input" made structural.
/// </summary>
public class AiTierResolverTests
{
    static readonly Dictionary<AiActorClass, AiTier> TierByActorClass = new()
    {
        [AiActorClass.Unique] = AiTier.Smart,
        [AiActorClass.General] = AiTier.Performance,
    };

    static CombatAiProfile Profile(AiTier? tierOverride = null) => new(
        "test/default", AiPlace.Battle, AiRole.Default, tierOverride, TierByActorClass,
        new[] { new AiProfileRow(TargetSelector.Nearest, AiRowCondition.Always, 0, "", AiCensusCondition.None, 0, new AiActionFilter(null, null, null, null)) },
        new AiScoringBlock(70, 50, 15, 10, 10, 1, 120, 2, 32),
        new AiSelectionBlock(SelectionMode.Argmax, 1000, "ai.select"),
        Array.Empty<AiReserveFloor>(),
        new AiWasteGuards(1, 0, 0),
        new AiAntiRepeat(0, 0, 0),
        Trigger: null,
        new AiPersonalityBounds(new Dictionary<PersonalityAxis, int>
        {
            [PersonalityAxis.Aggression] = 0, [PersonalityAxis.Recklessness] = 0,
            [PersonalityAxis.Focus] = 0, [PersonalityAxis.Thrift] = 0,
        }));

    [Fact]
    public void Profile_override_wins_over_actor_class()
    {
        var profile = Profile(tierOverride: AiTier.Smart);
        Assert.Equal(AiTier.Smart, AiTierResolver.For(profile, AiActorClass.General));
    }

    [Fact]
    public void Unique_resolves_smart_and_general_resolves_performance()
    {
        var profile = Profile();
        Assert.Equal(AiTier.Smart, AiTierResolver.For(profile, AiActorClass.Unique));
        Assert.Equal(AiTier.Performance, AiTierResolver.For(profile, AiActorClass.General));
    }

    /// <summary>The byte-identity default: with no class resolver wired, `AiActorClassOf?.Invoke(...)
    /// ?? AiActorClass.Unique` always yields Unique -- asserted here so a later change of direction
    /// (defaulting to General) fails loudly rather than silently downgrading every unwired actor.</summary>
    [Fact]
    public void Unwired_class_resolver_defaults_to_unique()
    {
        AiActorClassOf? unwired = null;
        var resolved = unwired?.Invoke("anyone") ?? AiActorClass.Unique;
        Assert.Equal(AiActorClass.Unique, resolved);
    }

    [Fact] public void AiActorClass_has_two_members() =>
        // Unique / General -- D6's class split. A third is a reviewed vocabulary change.
        Assert.Equal(2, Enum.GetValues(typeof(AiActorClass)).Length);

    [Fact] public void AiTier_has_two_members() =>
        // Smart / Performance -- a "dumb" third tier is reserved by the ideal, deliberately unregistered.
        Assert.Equal(2, Enum.GetValues(typeof(AiTier)).Length);

    /// <summary>D2 made structural, not promised in prose: `For` takes a profile and a class and
    /// nothing else — a signature assertion via reflection, so a later widened signature fails the
    /// build here rather than being caught in review.</summary>
    [Fact]
    public void Nothing_in_tier_resolution_reads_a_difficulty_input()
    {
        var method = typeof(AiTierResolver).GetMethod("For", BindingFlags.Public | BindingFlags.Static)!;
        var parameters = method.GetParameters();
        Assert.Equal(2, parameters.Length);
        Assert.Equal(typeof(CombatAiProfile), parameters[0].ParameterType);
        Assert.Equal(typeof(AiActorClass), parameters[1].ParameterType);
    }
}
