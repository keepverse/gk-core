using System.Linq;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// D3 (`battle-mode-parity`, solid-remediation T3.4) — every atom trigger either fires in battle or is
/// a named exception with a reason.
///
/// <para><b>What was wrong.</b> Battle raised 4 of 13: `OnActivate` and `OnDamageDealt` from
/// `BasicAttack`, plus `OnGranted`/`OnRemoved` which `EffectBag` raises itself and are therefore
/// mode-independent. The lawn raises all 13. The same authored atom behaved differently depending on
/// which mode it was in, which is rule 2 of the responsibility register.</para>
///
/// <para><b>Why the two exceptions are exceptions and not gaps.</b> `OnSunCollect` and `OnGridPlace`
/// are PvZ <i>lawn economy</i> events — the sun bank and the plant grid. Both belong to the foundation
/// game, which `AGENTS.md` is explicit the RPG layer observes rather than reimplements, and a battle
/// has neither a sun bank nor a planting grid to raise them from. Firing them in battle would mean
/// inventing a sun economy for a mode that has none, which is a feature, not parity.</para>
/// </summary>
[Trait("VerificationId", "core.battle-mode-parity")]
public class BattleTriggerCoverageTests
{
    /// <summary>
    /// Raised by battle itself, at a named site.
    /// </summary>
    static readonly (string Trigger, string Where)[] RaisedInBattle =
    {
        (AtomTriggers.OnActivate, "BasicAttack — action activation"),
        (AtomTriggers.OnDamageDealt, "BasicAttack — the attacker half of a hit"),
        (AtomTriggers.OnDamageTaken, "BattleRunState.ApplyHp — the defender half, damage only, post-pipeline"),
        (AtomTriggers.OnSpawn, "BattleRunState — initial roster and mid-battle arrivals"),
        (AtomTriggers.OnDeath, "BattleRunState — the unsourced sweep and the attributed kill"),
        (AtomTriggers.OnTimer, "BattleEngine — the round clock, which is battle's timer"),
        (AtomTriggers.OnMatchStart, "BattleEngine — before the round loop"),
        (AtomTriggers.OnMatchEnd, "BattleEngine — after the round loop"),
        (AtomTriggers.OnWave, "BattleEngine — a battle IS one wave (BattleSetup.WaveId)"),
    };

    /// <summary>
    /// Raised by <c>EffectBag</c> itself rather than by any mode, so battle gets them for free and they
    /// were never actually missing.
    /// </summary>
    static readonly string[] RaisedByTheBagForEveryMode =
    {
        AtomTriggers.OnGranted,
        AtomTriggers.OnRemoved,
    };

    /// <summary>
    /// Deliberately not raised in battle, each with the reason the acceptance criteria demand.
    /// </summary>
    static readonly (string Trigger, string Reason)[] NamedExceptions =
    {
        (AtomTriggers.OnSunCollect,
            "the sun bank is PvZ lawn economy (pvz.*, match-scoped); a battle has no sun to collect, " +
            "and giving it one would be inventing an economy rather than reaching parity"),
        (AtomTriggers.OnGridPlace,
            "the planting grid is PvZ lawn economy; a battle board places actors by the engine, never " +
            "by a player spending sun on a grid cell"),
    };

    /// <summary>
    /// <b>The closed vocabulary, pinned — and this is the mirror of the population rule, not an
    /// exception to it.</b> The trigger list is a declaration the code owns and a human edits;
    /// `AtomKind.cs` already records that it was deduplicated down to one literal per trigger precisely
    /// so the two lists "cannot disagree even in principle". A fourteenth trigger is a reviewed change
    /// to that declaration, never something that ships because content grew, so pinning 13 fails
    /// exactly when it should.
    /// </summary>
    [Fact]
    public void The_trigger_vocabulary_is_a_closed_thirteen()
    {
        Assert.Equal(13, AtomTriggers.All.Length);
    }

    /// <summary>
    /// Closure: every trigger in the vocabulary is accounted for exactly once — raised by battle,
    /// raised by the bag for every mode, or a named exception. This is what stops a fourteenth trigger
    /// being added and silently never firing in battle, which is how D3 happened.
    /// </summary>
    [Fact]
    public void Every_trigger_is_accounted_for_exactly_once()
    {
        var accounted = RaisedInBattle.Select(r => r.Trigger)
            .Concat(RaisedByTheBagForEveryMode)
            .Concat(NamedExceptions.Select(e => e.Trigger))
            .ToArray();

        Assert.Equal(accounted.Length, accounted.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            AtomTriggers.All.OrderBy(t => t, StringComparer.Ordinal),
            accounted.OrderBy(t => t, StringComparer.Ordinal));
    }

    /// <summary>
    /// Every exception carries a reason. An exception with an empty reason is how "named exception"
    /// decays back into "not done yet".
    /// </summary>
    [Fact]
    public void Every_exception_names_its_reason()
    {
        foreach (var (trigger, reason) in NamedExceptions)
            Assert.False(string.IsNullOrWhiteSpace(reason), $"{trigger} is excepted with no reason");
    }

    /// <summary>
    /// Each battle-raised trigger names where it is raised. A table entry with no site is a claim; with
    /// one it is a pointer the next reader can check.
    /// </summary>
    [Fact]
    public void Every_battle_raised_trigger_names_its_site()
    {
        foreach (var (trigger, where) in RaisedInBattle)
            Assert.False(string.IsNullOrWhiteSpace(where), $"{trigger} claims to be raised but names no site");
    }

    /// <summary>
    /// The nine D3 named are no longer unraised: four were already there, and the other nine minus the
    /// two lawn-economy exceptions are now wired. Stated as a set difference rather than a count so it
    /// survives the vocabulary growing.
    /// </summary>
    [Fact]
    public void Nothing_in_the_vocabulary_is_unaccounted_for()
    {
        var unaccounted = AtomTriggers.All
            .Except(RaisedInBattle.Select(r => r.Trigger), StringComparer.Ordinal)
            .Except(RaisedByTheBagForEveryMode, StringComparer.Ordinal)
            .Except(NamedExceptions.Select(e => e.Trigger), StringComparer.Ordinal)
            .ToArray();

        Assert.True(unaccounted.Length == 0,
            "triggers neither raised in battle nor excepted with a reason: " + string.Join(", ", unaccounted));
    }
}
