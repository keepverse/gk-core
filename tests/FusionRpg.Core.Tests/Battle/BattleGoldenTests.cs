using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// C3b: the blessed goldens (after ALL of C2 — trait/status/funnel semantics locked). Three
/// canonical battles + a 32-seed hash sweep. A diff here is a determinism break or a balance
/// change and MUST be a conscious RulesetVersion/EngineVersion bump, never a silent re-bless.
/// </summary>
public class BattleGoldenTests
{
    // Re-blessed 2026-08-21 at RulesetVersion 2 (combat-unification battle-adoption): the SSOT
    // resolver + re-tuned baselines + shields + report shape changed every byte, ONCE, by
    // design. Predicted-delta review: outcomes/shapes held (stomp Victory, wipe Defeat with
    // the coward retreating — verified pre-bless); mirror-match symmetry 48–56%.
    // v1 hashes (EngineVersion 1, RulesetVersion 1) remain decodable by their stamps.
    //
    // Re-blessed AGAIN, same day, for one reason: the hash input no longer includes the
    // platform stamp (see Hash). The previous values had baked in "X64/.NET 8.0.30", so they
    // were only ever green on the machine that blessed them — CI or any teammate would have
    // read a portability failure as a determinism break. Battle MATH did not move: the only
    // failures in the re-bless run were these two hash tests, while every shape, rate, shield,
    // and expedition test stayed green with no seed re-selection.
    // RulesetVersion 2 hashes (bMilli=0) remain decodable by their stamp for history.
    //
    // Re-blessed 2026-08-24 at RulesetVersion 3 (T4.2, power-dial: power-scale.v2.json's
    // bMilli 0 -> 400 — the ONE golden-moving change the whole power program was built around).
    // Every actor here sits away from the Theta=20 pin (levels 1/2/5/6/10), so every magnitude
    // legitimately moved; nothing here is a rate, so PS-3 does not apply to these hashes at all.
    // Triaged BEFORE this re-bless, not after: the full CORE suite's only failures were these
    // hash goldens, the two literal RulesetVersion==2 assertions, and the three B=0-specific
    // BattleMagnitudeParityTests (reframed separately, not re-blessed) — every rate-specific
    // test (RateParityTests.cs, BattleAdoptionTests.cs's BattleRateTests) stayed green with zero
    // changes, confirming zero rate goldens moved. Golden_outcomes_hold_their_shapes (Victory/
    // Defeat/retreat) also stayed green, unchanged — the shapes held, only the numbers moved.
    //
    // Re-blessed 2026-08-25 at RulesetVersion 4 (defense-shape): `combat.defense` stops SUBTRACTING
    // and starts DIVIDING (combat-damage-ssot.md §6.3, DefenseShape.Divisive), so every mitigated
    // magnitude moves. Triaged BEFORE the re-bless, exactly as the 2026-08-24 pass was: the whole
    // battle suite was 314/319 with the ONLY five failures being these hash goldens and the three
    // trace fixtures. Every rate test stayed green untouched — `Parity_hit_rate_is_ninety_percent`
    // and `Parity_crit_rate_is_five_to_ten_percent` still hold their locked bands (0.90±0.02,
    // 0.05–0.10), so no rate golden moved and PS-3 does not apply to these hashes.
    // `Golden_outcomes_hold_their_shapes` also stayed green with no change: stomp is still Victory,
    // wipe still Defeat, the coward still walks away. Shapes held; only numbers moved.
    //
    // Why the shape changed at all: subtractive mitigation floors damage at zero once defense
    // outruns offense, which is total immunity — the same defect removed from `ampFactor` in the
    // same session, measured over 50,000 simulated fights at 17.1% of LANDED hits dealing nothing.
    // Divisive approaches zero asymptotically and never reaches it. Negative defense still
    // amplifies (mirrored branch), so glass profiles are unaffected in kind.
    //
    // Re-blessed 2026-09-13 at RulesetVersion 5 (battle-hub-fuse T6): BattleStatComposer is deleted;
    // BattleEngine composes exclusively through BattleHubCompose (ActorHub). The hash moves on the
    // version stamp alone (BattleReport.RulesetVersion is part of the hashed payload), independent of
    // whether any actual magnitude changed — and T5's own pre-delete parity matrix already proved Hub
    // == old composer channel-for-channel on these exact fixtures (exact on battle channels; these
    // goldens carry no traits/statuses touching the three narrowing-bounded aptitude channels, so no
    // magnitude moved either). `Golden_outcomes_hold_their_shapes` stayed green, unchanged: stomp is
    // still Victory, wipe still Defeat, the coward still walks away.
    // Re-blessed 2026-09-19 (action-enrich AE1.5, H1 hard edge — the third re-bless, after ST2.3 and
    // ST1.3, which moved nothing): the hit's base moves from the actor's `atk` to the ACTION, resolved
    // through `ActionBaseDerivation` x `P(Theta)` against `gk-core/data/tuning/action-base.v2.json`. One cause,
    // one commit. Only stomp (levels 10/2) and wipe (levels 1/6) move; close (5/5) and the 32-seed
    // sweep HOLD, because the new base is calibrated to reproduce the old atk baseline at those levels
    // (`BaseAtk(5)=30 / (P(5)=215/1000) = 140`) — so this is a representation change at the pin, not a
    // balance swing. The old stomp/wipe values are one `git show` away; both move by the base only
    // (no rate, no shape: `Golden_outcomes_hold_their_shapes` stayed green unchanged).
    const string StompHash = "AF6A4F0C785DB30CB465A356B9F58D3C626AD85AB5827B74B33D1D023D0A59CD";
    const string CloseHash = "2734593679CC89F6E1DDC81064FB1A7923B3D0D986B1E52C2D538AECACB085EC";
    const string WipeHash = "D8801D6403BD89B702299A083808AA81936F1BC2B1EA686E802EB8770F79CEF4";
    const string SeedSweepHash = "38CCD2CDEF7C5009928B288700ED2938D31F21109159B2789A648EF2C1E5782F";

    static BattleActorSetup Actor(string key, string side, int level,
        ElementTypeId? elem = null, params string[] traits) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "golden-species",
        TypeId = 10_001,
        Level = level,
        ElementPrimary = elem,
        TraitIds = traits,
        MaxHp = BattleRuleset.BaseHp(level),
        Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level)
    };

    /// <summary>Overleveled elemental squad stomps a small wave.</summary>
    internal static BattleSetup StompSetup() => new()
    {
        WaveId = "golden-stomp",
        Squad = new[]
        {
            Actor("squad:0", "squad", 10, ElementTypeId.Fire, "berserker", "swift"),
            Actor("squad:1", "squad", 10, ElementTypeId.Light, "critical-hunter")
        },
        Wave = new[]
        {
            Actor("wave:0", "wave", 2, ElementTypeId.Ice),
            Actor("wave:1", "wave", 2, ElementTypeId.Earth),
            Actor("wave:2", "wave", 2)
        }
    };

    /// <summary>Even match exercising guardian/loyal/regenerator vs bloodthirsty/soul-eater.</summary>
    internal static BattleSetup CloseSetup() => new()
    {
        WaveId = "golden-close",
        Squad = new[]
        {
            Actor("squad:0", "squad", 5, ElementTypeId.Air, "regenerator"),
            Actor("squad:1", "squad", 5, ElementTypeId.Earth, "guardian", "loyal")
        },
        Wave = new[]
        {
            Actor("wave:0", "wave", 5, ElementTypeId.Dark, "bloodthirsty"),
            Actor("wave:1", "wave", 5, ElementTypeId.Fire, "soul-eater")
        }
    };

    /// <summary>Hopeless squad wiped by an elite wave; the coward walks away.</summary>
    internal static BattleSetup WipeSetup() => new()
    {
        WaveId = "golden-wipe",
        Squad = new[]
        {
            Actor("squad:0", "squad", 1, null, "coward"),
            Actor("squad:1", "squad", 1, ElementTypeId.Ice)
        },
        Wave = new[]
        {
            Actor("wave:0", "wave", 6, ElementTypeId.Fire, "void-touched"),
            Actor("wave:1", "wave", 6, ElementTypeId.Dark, "immortal")
        }
    };

    /// <summary>
    /// Hashes the report with the platform stamp blanked. The stamp is a property of the
    /// MACHINE, not of the battle — leaving it in would bind every golden to whatever
    /// architecture/OS/runtime blessed it, so a CI runner or a `dotnet` upgrade would fire the
    /// "determinism break" alarm for a non-reason. Locked by Goldens_do_not_depend_on_the_platform.
    /// </summary>
    /// <summary>
    /// The determinism hash. <b>Four provenance fields are blanked</b>: the platform stamp, because
    /// `Math.Exp` is not bit-identical across architectures; the content hash (E12), because it
    /// records which content was consulted rather than what the engine computed; and `Warnings`
    /// (aura-skill T3, audit D3), because it names content the resolver could not find rather than
    /// anything the battle math computed; and killer actor keys, because they identify the cause
    /// of a death without changing the resolved combat state.
    ///
    /// <para>Folding any of these in makes the goldens move for a reason that is not a determinism
    /// break — the platform stamp made them green only on the machine that blessed them, the content
    /// hash would make every added row look like one, a warning would make an unrelated content gap
    /// look like a battle-math regression, and attribution would make an observability-only change
    /// look like one.</para>
    /// </summary>
    static string Hash(BattleReport report)
    {
        var json = JsonSerializer.Serialize(
            report with
            {
                EnvironmentStamp = "",
                ContentHash = null,
                Warnings = null,
                Events = report.Events
                    .Select(e => e with { KillerActorKey = null })
                    .ToArray()
            });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void Goldens_do_not_depend_on_the_content_stamp()
    {
        // E12 stamps the report with the content that produced it. If that reached the hash input,
        // adding one item row would move every battle golden and a real determinism break would be
        // indistinguishable from an author doing their job.
        var report = BattleEngine.Resolve(StompSetup(), 1001) with { ContentHash = "v4|abc|x=1" };

        Assert.Equal(Hash(report), Hash(report with { ContentHash = "v4|totally-different|y=2" }));
        Assert.Equal("v4|abc|x=1", report.ContentHash); // and it still reaches the report
    }

    [Fact]
    public void Goldens_do_not_depend_on_the_platform()
    {
        // The hash input must not carry the machine stamp — that is what makes these hashes
        // portable across CI, teammates, and runtime patches.
        var report = BattleEngine.Resolve(StompSetup(), 1001);
        var hashed = JsonSerializer.Serialize(report with { EnvironmentStamp = "" });
        Assert.DoesNotContain(BattleEnvironment.Stamp, hashed);

        // ...and a report that differs ONLY by stamp must hash identically.
        Assert.Equal(Hash(report), Hash(report with { EnvironmentStamp = "X64/other/net99" }));

        // The stamp still reaches the report itself — this decouples the golden, not the guard.
        Assert.Equal(BattleEnvironment.Stamp, report.EnvironmentStamp);
    }

    [Fact]
    public void Golden_battles_are_locked()
    {
        var actual =
            $"stomp:{Hash(BattleEngine.Resolve(StompSetup(), 1001))}\n" +
            $"close:{Hash(BattleEngine.Resolve(CloseSetup(), 2002))}\n" +
            $"wipe:{Hash(BattleEngine.Resolve(WipeSetup(), 3003))}";
        var expected = $"stomp:{StompHash}\nclose:{CloseHash}\nwipe:{WipeHash}";
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Golden_outcomes_hold_their_shapes()
    {
        Assert.Equal(BattleOutcome.Victory, BattleEngine.Resolve(StompSetup(), 1001).Outcome);
        var wipe = BattleEngine.Resolve(WipeSetup(), 3003);
        Assert.Equal(BattleOutcome.Defeat, wipe.Outcome);
        Assert.True(wipe.Actors.Single(a => a.Key == "squad:0").Retreated, "the golden coward must walk away");
    }

    [Fact]
    public void Thirty_two_seed_sweep_is_locked()
    {
        var sb = new StringBuilder();
        for (ulong seed = 0; seed < 32; seed++)
            sb.Append(Hash(BattleEngine.Resolve(CloseSetup(), seed)));
        var aggregate = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        Assert.Equal(SeedSweepHash, aggregate);
    }
}
