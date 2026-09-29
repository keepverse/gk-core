using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// combat-unification `species-skills` **S1** — the neutral invariant, written BEFORE either channel
/// reader exists so it can fail for the right reason.
///
/// <para>The claim `species-skills` rests on: <b>a battle in which no actor carries a non-neutral
/// <c>skill.*</c> value is byte-identical.</b> That is what lets the two readers land with no
/// <c>RulesetVersion</c> bump and no golden re-blessed. If it ever stops holding, the reads have a
/// bug — the goldens have not "moved".</para>
///
/// <para><b>Neutral is 0, not 1000.</b> Both channels register `FlatSum` with a default of **0**
/// (`DerivedStatRegistry.cs:186,189`). For cooldown that is 0‰ of reduction; for effectiveness the
/// channel is a *bonus* added to the multiplier's own 1.0 no-op
/// (`OverlayCombatRequest.EffectivenessMultiplier`, which already participates in the formula and is
/// left at 1.0 by all three of its construction sites). `spec-species-skills.md` said "1000‰
/// effectiveness", which is the resulting multiplier, not the channel value — recorded here because
/// the two are easy to conflate and a reader that treated 0 as "×0" would zero all damage.</para>
/// </summary>
public class SkillChannelNeutralityTests
{
    static string Hash(BattleReport report)
    {
        var json = JsonSerializer.Serialize(
            report with { EnvironmentStamp = "", ContentHash = null, Warnings = null });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>Explicitly-neutral mods on every category, for both families.</summary>
    static IReadOnlyList<BattleChannelMod> NeutralSkillMods() =>
        DerivedStatChannels.ActionCategories
            .SelectMany(c => new[]
            {
                new BattleChannelMod(DerivedStatChannels.SkillCooldown(c), 0),
                new BattleChannelMod(DerivedStatChannels.SkillEffectiveness(c), 0),
            })
            .ToArray();

    static BattleSetup WithNeutralSkillChannels(BattleSetup setup) => setup with
    {
        Squad = setup.Squad.Select(a => a with { ChannelMods = NeutralSkillMods() }).ToArray(),
        Wave = setup.Wave.Select(a => a with { ChannelMods = NeutralSkillMods() }).ToArray(),
    };

    /// <summary>
    /// The load-bearing one. Explicitly setting every `skill.*` channel to its neutral value must
    /// produce the same battle, byte for byte, as not setting them at all.
    ///
    /// <para>Today this passes because nothing reads the channels. After S2 and S3 wire the two
    /// readers it passes only if both genuinely collapse to the arithmetic identity at neutral — which
    /// is the whole safety argument for landing them without a version bump.</para>
    /// </summary>
    [Theory]
    [InlineData("stomp", 1001)]
    [InlineData("close", 2002)]
    [InlineData("wipe", 3003)]
    public void ExplicitlyNeutralSkillChannelsAreIndistinguishableFromAbsent(string which, ulong seed)
    {
        var setup = which switch
        {
            "stomp" => BattleGoldenTests.StompSetup(),
            "close" => BattleGoldenTests.CloseSetup(),
            _ => BattleGoldenTests.WipeSetup(),
        };

        var absent = Hash(BattleEngine.Resolve(setup, seed));
        var neutral = Hash(BattleEngine.Resolve(WithNeutralSkillChannels(setup), seed));

        Assert.Equal(absent, neutral);
    }

    /// <summary>
    /// A non-neutral value must be ACCEPTED by the compose path without throwing, even while nothing
    /// reads it. Proven separately from the equality above so that, once S2/S3 land, a failure here
    /// says "the channel broke composition" and a failure there says "the read is not neutral at
    /// neutral" — two different bugs that would otherwise arrive as one red line.
    /// </summary>
    [Fact]
    public void ANonNeutralSkillChannelComposesWithoutThrowing()
    {
        var setup = BattleGoldenTests.StompSetup();
        var loaded = setup with
        {
            Squad = setup.Squad.Select(a => a with
            {
                ChannelMods = new[]
                {
                    new BattleChannelMod(DerivedStatChannels.SkillCooldown(DerivedStatChannels.ActionCategoryAttack), 250),
                    new BattleChannelMod(DerivedStatChannels.SkillEffectiveness(DerivedStatChannels.ActionCategoryAttack), 300),
                }
            }).ToArray()
        };

        var report = BattleEngine.Resolve(loaded, 1001);
        Assert.NotNull(report);
        Assert.True(report.Rounds > 0, "a battle that resolved zero rounds proves nothing");
    }
}
