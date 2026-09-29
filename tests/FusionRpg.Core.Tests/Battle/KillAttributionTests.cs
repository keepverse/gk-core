using System.Linq;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Match;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Tests.Commanders;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// `solid-remediation` T4.3 (D9) — every kill credits exactly one empire, in every mode.
///
/// <para><b>Closure, not counting.</b> The audit's own verification wording is *"every kill credits
/// exactly one owner — a closure assertion, never a count"*. So nothing here asserts how many kills, how
/// many entities, or how many species exist. What is asserted is that the mapping is **total over the
/// closed vocabularies the code owns** (<see cref="StatSide"/>, <see cref="EmpireId"/>,
/// <see cref="BoardSide"/>) and that nothing falls through it. Those counts are declarations a human
/// changes, not populations content grows — the distinction `validation-ssot.md` draws.</para>
/// </summary>
[Trait("VerificationId", "core.kill-attribution")]
public class KillAttributionTests
{
    /// <summary>Membership in the directory's rows: every empire an attribution can produce is one the
    /// shipped registry carries a default commander for. Never a hardcoded empire list.</summary>
    static void AssertIsAnEmpireOfTheDirectory(EmpireId empire) =>
        Assert.Equal(empire, ShippedCommanders.Directory.EmpireOf(ShippedCommanders.Directory.DefaultFor(empire)));

    static IReadOnlyList<StatSide> AllSides() =>
        Enum.GetValues(typeof(StatSide)).Cast<StatSide>().ToArray();

    [Fact]
    public void Every_side_credits_exactly_one_empire_and_none_falls_through()
    {
        // The closure assertion itself. Not "two sides map to two empires" — that would pass a mapping
        // that answered Dave for both. Every side resolves, and the resolved value is an empire the
        // shipped commander directory carries.
        foreach (var side in AllSides())
        {
            var empire = KillAttribution.EmpireOf(side);
            AssertIsAnEmpireOfTheDirectory(empire);
        }

        // And the mapping is injective across the closed set, which is what makes "exactly one owner"
        // mean something: if both sides answered the same empire, one army would earn for the other.
        var empires = AllSides().Select(s => KillAttribution.EmpireOf(s)).ToArray();
        Assert.Equal(empires.Distinct().Count(), empires.Length);
    }

    [Fact]
    public void The_general_horde_has_an_owner_even_though_it_has_no_specimen_row()
    {
        // D9 stated directly. A vanilla zombie is unregistered, so specimen ownership resolves null —
        // that is the input that used to mean "skip, nobody owns this". It now credits Zomboss, and the
        // null it still carries means only "no specimen row", which is a different fact.
        var credit = KillAttribution.Credit(StatSide.Zombie, specimenOwner: null);

        Assert.Equal(EmpireId.Zomboss, credit.Empire);
        Assert.Null(credit.SpecimenOwner);
    }

    [Fact]
    public void A_vanilla_plant_belongs_to_the_players_empire_by_the_same_rule()
    {
        var credit = KillAttribution.Credit(StatSide.Plant, specimenOwner: null);

        Assert.Equal(EmpireId.Dave, credit.Empire);
        Assert.Null(credit.SpecimenOwner);
    }

    [Fact]
    public void Mind_control_moves_the_credit_to_the_side_the_entity_now_fights_for()
    {
        // A hypnotised zombie kills for the player. If the empire were read off the visual side, the
        // player's own mind-controlled attacker would be earning for Zomboss.
        Assert.Equal(EmpireId.Dave, KillAttribution.EmpireOf(StatSide.Zombie, mindControlled: true));
        Assert.Equal(EmpireId.Zomboss, KillAttribution.EmpireOf(StatSide.Plant, mindControlled: true));

        // Still total, and still exactly one empire per side under the flip.
        var flipped = AllSides().Select(s => KillAttribution.EmpireOf(s, mindControlled: true)).ToArray();
        Assert.Equal(flipped.Distinct().Count(), flipped.Length);
    }

    [Fact]
    public void A_registered_specimen_keeps_its_owner_without_it_deciding_the_empire()
    {
        // save-identity SE4.22: no player row represents Zomboss any more — his specimen shares its
        // save's own row. So the owner's EmpireRef must NOT pick the empire from the save alone: this
        // one is Zomboss's own deployed unique on save 1, and reading the save as "Dave's" would credit
        // every one of its kills to the human. Side decides; the owner is carried alongside.
        var zombossOwner = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        var credit = KillAttribution.Credit(StatSide.Zombie, specimenOwner: zombossOwner);

        Assert.Equal(EmpireId.Zomboss, credit.Empire);
        Assert.Equal(zombossOwner, credit.SpecimenOwner);
    }

    [Fact]
    public void Two_specimens_of_the_SAME_save_on_opposite_sides_credit_opposite_empires()
    {
        // The pair that would collapse if the save alone decided it: both specimens are on save 1, with
        // different empires, and they must land in different empires regardless.
        var dave = new EmpireRef(new SaveId(1), EmpireId.Dave);
        var zomboss = new EmpireRef(new SaveId(1), EmpireId.Zomboss);
        var daves = KillAttribution.Credit(StatSide.Plant, specimenOwner: dave);
        var zombosss = KillAttribution.Credit(StatSide.Zombie, specimenOwner: zomboss);

        Assert.Equal(EmpireId.Dave, daves.Empire);
        Assert.Equal(EmpireId.Zomboss, zombosss.Empire);
        Assert.NotEqual(daves.Empire, zombosss.Empire);
    }

    [Fact]
    public void The_side_to_empire_mapping_is_the_one_T4_1_already_established()
    {
        // Not a second copy of "zombies are Zomboss's". If these ever disagree, the species scope key
        // and kill attribution have forked, which is the parallel-path defect this program removes.
        foreach (var side in AllSides())
            Assert.Equal(
                FusionRpg.Core.Stats.Aptitudes.SpeciesAllocation.EmpireForSide(side),
                KillAttribution.EmpireOf(side));
    }

    // ---- the refusals: an unknown owner is never defaulted -------------------------------------

    [Fact]
    public void A_bullet_has_no_empire_and_says_so_rather_than_defaulting()
    {
        // BoardSide is closed at three, and exactly one of them is not an army. A default here would
        // have every projectile quietly earning for one empire, with no symptom.
        Assert.Throws<ArgumentOutOfRangeException>(() => KillAttribution.EmpireOf(BoardSide.Bullet));

        // The other two resolve, so the refusal is specific rather than a broken overload.
        Assert.Equal(EmpireId.Dave, KillAttribution.EmpireOf(BoardSide.Plant));
        Assert.Equal(EmpireId.Zomboss, KillAttribution.EmpireOf(BoardSide.Zombie));
    }

    [Fact]
    public void An_unrecognised_side_token_is_refused_never_credited_to_a_default_empire()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => KillAttribution.EmpireOf("bullet"));
        Assert.Throws<ArgumentOutOfRangeException>(() => KillAttribution.EmpireOf(""));

        // The tokens every mode actually uses do resolve.
        Assert.Equal(EmpireId.Dave, KillAttribution.EmpireOf("plant"));
        Assert.Equal(EmpireId.Zomboss, KillAttribution.EmpireOf("zombie"));
    }

    // ---- every mode -----------------------------------------------------------------------------

    [Fact]
    public void Every_registered_mode_resolves_an_owner_for_every_actor_that_dies_in_a_real_battle()
    {
        // D9's second half: "kill attribution is absent OUTSIDE the lawn". This runs a REAL battle per
        // mode and walks the die events it actually produced, rather than looping the mode list while
        // asserting something no mode can influence — the failure mode T3.6's own conformance test was
        // written to avoid.
        var modes = new[]
        {
            BattleModeProfileCatalog.ClassicRound,
            BattleModeProfileCatalog.GalaxySync,
            BattleModeProfileCatalog.HybridAtb,
            BattleModeProfileCatalog.Siege,
            BattleModeProfileCatalog.Delve,
        };

        var sawADeathInSomeMode = false;

        foreach (var profile in modes)
        {
            var report = BattleEngine.Resolve(Setup(), seed: 4242, profile: profile);
            var bySideKey = report.Actors.ToDictionary(a => a.Key, a => a.Side, StringComparer.Ordinal);

            foreach (var rec in report.Events.Where(e => e.Kind == BattleEventKinds.Die))
            {
                sawADeathInSomeMode = true;

                // The dead actor resolves an owner...
                Assert.True(bySideKey.TryGetValue(rec.ActorKey, out var deadSide));
                AssertIsAnEmpireOfTheDirectory(KillAttribution.EmpireOfBattleSide(deadSide!));

                // ...and so does whoever killed it, which is the credit D9 asks for.
                if (rec.KillerActorKey is null) continue;
                Assert.True(bySideKey.TryGetValue(rec.KillerActorKey, out var killerSide),
                    $"die event names killer '{rec.KillerActorKey}' with no actor row");
                var credit = KillAttribution.CreditForBattleSide(killerSide!);
                AssertIsAnEmpireOfTheDirectory(credit.Empire);

                // A kill credits the killer's empire, never the victim's — the one thing that would
                // make attribution present but wrong.
                Assert.NotEqual(KillAttribution.EmpireOfBattleSide(deadSide!), credit.Empire);
            }
        }

        // The loop above is vacuously true if no mode produced a death, which would make this test
        // green while proving nothing. Fail instead.
        Assert.True(sawADeathInSomeMode,
            "no registered mode produced a die event — this test would otherwise pass vacuously");
    }

    [Fact]
    public void The_battle_side_vocabulary_is_squad_and_wave_and_an_unknown_token_is_refused()
    {
        // Battle does NOT use the lawn's "plant"/"zombie" tokens (BattleModels.cs:10), so a single
        // string overload would have covered neither vocabulary properly.
        Assert.Equal(EmpireId.Dave, KillAttribution.EmpireOfBattleSide("squad"));
        Assert.Equal(EmpireId.Zomboss, KillAttribution.EmpireOfBattleSide("wave"));

        // BattleHubCompose treats anything-not-"wave" as squad because a compose must produce a
        // number. Attribution refuses instead: crediting the wrong empire is worse than failing loudly.
        Assert.Throws<ArgumentOutOfRangeException>(() => KillAttribution.EmpireOfBattleSide("plant"));
        Assert.Throws<ArgumentOutOfRangeException>(() => KillAttribution.EmpireOfBattleSide(""));
    }

    [Fact]
    public void Every_die_event_that_names_a_killer_also_carries_the_empire_it_credits()
    {
        // Without this the mechanism above would be a DARK feature by this program's own definition:
        // built, tested, correct and reached from nowhere. `killerPtr` says WHICH entity killed, never
        // WHOSE it was — which is how attribution stayed "absent outside the lawn" while the payload
        // already named a killer.
        var events = BattleReportEmitter.Emit(
            BattleEngine.Resolve(Setup(), seed: 4242, profile: BattleModeProfileCatalog.ClassicRound),
            matchKey: "kill-attribution-wiring");

        // Payload is `object?` on the envelope DTO; the emitter always builds it as a dictionary.
        var dieEventsWithAKiller = events
            .Where(e => e.Kind is "plant.die" or "zombie.die")
            .Select(e => e.Payload as Dictionary<string, object?>)
            .Where(p => p is not null && p.ContainsKey("killerPtr"))
            .Select(p => p!)
            .ToArray();

        Assert.NotEmpty(dieEventsWithAKiller);
        foreach (var payload in dieEventsWithAKiller)
        {
            Assert.True(payload.TryGetValue("creditEmpire", out var credited),
                "a die event names a killer but credits no empire");
            Assert.Contains(
                credited as string,
                new[] { EmpireId.Dave.Value, EmpireId.Zomboss.Value });
        }
    }

    // ---- fixtures ------------------------------------------------------------------------------

    static BattleActorSetup Actor(string key, string side) => new()
    {
        Key = key,
        Side = side,
        SpeciesId = "test-species",
        TypeId = 10_001,
        Level = 6,
        MaxHp = BattleRuleset.BaseHp(6),
        Atk = BattleRuleset.BaseAtk(6),
        Defense = BattleRuleset.BaseDefense(6),
    };

    static BattleSetup Setup() => new()
    {
        WaveId = "kill-attribution",
        Squad = new[] { Actor("squad:0", "squad") },
        Wave = new[] { Actor("wave:0", "wave") },
    };
}
