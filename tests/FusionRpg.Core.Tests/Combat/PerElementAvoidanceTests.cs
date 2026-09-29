using System.Linq;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

/// <summary>
/// D14 (`elemental-resolver`, T2.3) — parry, block and reflect read <c>omni + element</c> like the other
/// nine families.
///
/// <para><b>Why this file has to exist, and what it is really testing.</b> Extending the three families
/// moved <b>zero</b> of 13,944 tests. That is the correct result — no content authors a per-element
/// parry, block or reflect channel today, so every element half reads 0 and every total is unchanged.
/// But it also means the whole suite would stay green if the extension had silently done nothing.
/// These tests write the per-element channels by hand and prove the reader consumes them, so the wiring
/// is asserted rather than assumed.</para>
///
/// <para>Each test names the <b>rule</b>: the element half is read, it is ADDED to omni rather than
/// replacing or multiplying it, and an actor with no element half is exactly where it was before D14.</para>
/// </summary>
[Trait("VerificationId", "core.elemental-resolver")]
public class PerElementAvoidanceTests
{
    static ElementTypeId AnElement => Enum.GetValues<ElementTypeId>().First();
    static ElementTypeId AnotherElement => Enum.GetValues<ElementTypeId>().Skip(1).First();

    static ActorDerivedSnapshot Snapshot(params (string Channel, double Value)[] values) =>
        ActorDerivedSnapshot.FromValues(values.Select(v => new KeyValuePair<string, double>(v.Channel, v.Value)));

    /// <summary>
    /// The twelve readers, as (name, omni channel, per-element channel factory, reader). One case per
    /// family so a failure names which one regressed, rather than one test that says "something".
    /// </summary>
    public static TheoryData<string, string, string, int> Families()
    {
        var data = new TheoryData<string, string, string, int>();
        var rows = new (string Name, string Omni, Func<ElementTypeId, string> Per)[]
        {
            ("ParryRate", DerivedStatChannels.CombatParryRateOmni, DerivedStatChannels.CombatParryRate),
            ("ParryBreak", DerivedStatChannels.CombatParryBreakOmni, DerivedStatChannels.CombatParryBreak),
            ("ParryStrength", DerivedStatChannels.CombatParryStrengthOmni, DerivedStatChannels.CombatParryStrength),
            ("ParryShred", DerivedStatChannels.CombatParryShredOmni, DerivedStatChannels.CombatParryShred),
            ("BlockRate", DerivedStatChannels.CombatBlockRateOmni, DerivedStatChannels.CombatBlockRate),
            ("BlockBreak", DerivedStatChannels.CombatBlockBreakOmni, DerivedStatChannels.CombatBlockBreak),
            ("BlockStrength", DerivedStatChannels.CombatBlockStrengthOmni, DerivedStatChannels.CombatBlockStrength),
            ("BlockShred", DerivedStatChannels.CombatBlockShredOmni, DerivedStatChannels.CombatBlockShred),
            ("ReflectRate", DerivedStatChannels.CombatReflectRateOmni, DerivedStatChannels.CombatReflectRate),
            ("ReflectResistRate", DerivedStatChannels.CombatReflectResistRateOmni, DerivedStatChannels.CombatReflectResistRate),
            ("ReflectDamage", DerivedStatChannels.CombatReflectDamageOmni, DerivedStatChannels.CombatReflectDamage),
            ("ReflectResistDamage", DerivedStatChannels.CombatReflectResistDamageOmni, DerivedStatChannels.CombatReflectResistDamage),
        };

        for (var i = 0; i < rows.Length; i++)
            data.Add(rows[i].Name, rows[i].Omni, rows[i].Per(AnElement), i);
        return data;
    }

    static double Read(int index, ActorDerivedSnapshot snap, ElementTypeId element) => index switch
    {
        0 => CombatDerivedReader.ParryRate(snap, element),
        1 => CombatDerivedReader.ParryBreak(snap, element),
        2 => CombatDerivedReader.ParryStrength(snap, element),
        3 => CombatDerivedReader.ParryShred(snap, element),
        4 => CombatDerivedReader.BlockRate(snap, element),
        5 => CombatDerivedReader.BlockBreak(snap, element),
        6 => CombatDerivedReader.BlockStrength(snap, element),
        7 => CombatDerivedReader.BlockShred(snap, element),
        8 => CombatDerivedReader.ReflectRate(snap, element),
        9 => CombatDerivedReader.ReflectResistRate(snap, element),
        10 => CombatDerivedReader.ReflectDamage(snap, element),
        11 => CombatDerivedReader.ReflectResistDamage(snap, element),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>
    /// The element half is read at all. This is the assertion that would have caught a no-op extension:
    /// before D14 every one of these returned the omni value and ignored the second channel entirely.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public void The_element_half_is_read(string name, string omniChannel, string elementChannel, int index)
    {
        var snap = Snapshot((omniChannel, 40.0), (elementChannel, 25.0));

        Assert.Equal(65.0, Read(index, snap, AnElement));
    }

    /// <summary>
    /// It is ADDED, not substituted. A reader that returned the element channel when present and the
    /// omni one otherwise would pass the test above and fail this one.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public void The_element_half_is_added_to_omni_rather_than_replacing_it(string name, string omniChannel, string elementChannel, int index)
    {
        var omniOnly = Read(index, Snapshot((omniChannel, 40.0)), AnElement);
        var both = Read(index, Snapshot((omniChannel, 40.0), (elementChannel, 25.0)), AnElement);

        Assert.Equal(40.0, omniOnly);
        Assert.Equal(omniOnly + 25.0, both);
    }

    /// <summary>
    /// Doubling omni adds its own increase and nothing more — the anti-snowball rule, stated per family
    /// rather than only on the resolver. Multiplication would make the gain depend on the element half.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public void Doubling_omni_never_scales_the_element_half(string name, string omniChannel, string elementChannel, int index)
    {
        var single = Read(index, Snapshot((omniChannel, 40.0), (elementChannel, 900.0)), AnElement);
        var doubled = Read(index, Snapshot((omniChannel, 80.0), (elementChannel, 900.0)), AnElement);

        Assert.Equal(40.0, doubled - single);
    }

    /// <summary>
    /// Only the attack's own element is read. An actor who stacked one element gets nothing from it when
    /// hit by a different one — which is what makes the per-element channels a specialisation rather
    /// than a flat bonus, and what keeps the matrix meaningful.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public void A_different_element_reads_only_the_omni_half(string name, string omniChannel, string elementChannel, int index)
    {
        var snap = Snapshot((omniChannel, 40.0), (elementChannel, 900.0));

        Assert.Equal(40.0, Read(index, snap, AnotherElement));
    }

    /// <summary>
    /// An actor with no element half reads exactly its omni value — the pre-D14 behaviour, which is why
    /// extending the three families moved no golden. Asserted so the "nothing changed for existing
    /// content" claim is a test rather than an argument.
    /// </summary>
    [Theory]
    [MemberData(nameof(Families))]
    public void An_actor_with_no_element_half_is_where_it_was_before(string name, string omniChannel, string elementChannel, int index)
    {
        var snap = Snapshot((omniChannel, 40.0));

        foreach (var element in Enum.GetValues<ElementTypeId>())
            Assert.Equal(40.0, Read(index, snap, element));
    }

    /// <summary>
    /// The end-to-end shape: a per-element parry channel reaches the resolved parry band. The reader
    /// tests above prove the read; this proves the value survives the weighted accumulation in
    /// <see cref="OverlayCombatCalculator"/> and reaches an outcome a player would feel.
    ///
    /// <para>The draw is fixed at 0.3, which `ResolveBand` places inside the parry band for these
    /// inputs (parry occupies <c>[pHit − pParry, pHit)</c>, here <c>[0.2, 0.5)</c>) — a chosen point,
    /// not a tuned one, and the third case below is what proves the choice is not doing the work.</para>
    /// </summary>
    [Fact]
    public void A_per_element_parry_channel_reaches_the_resolved_band()
    {
        var element = AnElement;
        var attacker = new CombatActorSnapshot(Snapshot(), ActorElementTypes.Neutral);

        OverlayCombatRequest Against(CombatActorSnapshot defender) => new()
        {
            BaseOverlayDamage = 1000,
            Components = new[] { new ElementPayloadComponent(element, 1.0) },
            Attacker = attacker,
            Defender = defender
        };

        var calculator = new OverlayCombatCalculator(ElementHub.Default);
        var draw = new FixedCombatRng(300_000);

        var omniOnly = new CombatActorSnapshot(
            Snapshot((DerivedStatChannels.CombatParryRateOmni, 300.0)), ActorElementTypes.Neutral);
        var elementOnly = new CombatActorSnapshot(
            Snapshot((DerivedStatChannels.CombatParryRate(element), 300.0)), ActorElementTypes.Neutral);
        var noParry = new CombatActorSnapshot(Snapshot(), ActorElementTypes.Neutral);

        var (_, omniBreakdown) = calculator.Compute(Against(omniOnly), draw);
        var (_, elementBreakdown) = calculator.Compute(Against(elementOnly), draw);
        var (_, noneBreakdown) = calculator.Compute(Against(noParry), draw);

        // The additive rule at the outcome rather than the read: 300 points of parry resolve the same
        // whether they were spent on omni or on the element this attack actually carries.
        Assert.True(omniBreakdown.Parried, "an omni parry of 300 did not parry at the chosen draw");
        Assert.Equal(omniBreakdown.Parried, elementBreakdown.Parried);

        // And the draw is not what produced it: the same draw against a defender with no parry at all
        // does not parry. Without this the two assertions above would pass on a band that always fires.
        Assert.False(noneBreakdown.Parried, "a defender with no parry stat parried — the draw, not the channel, is deciding");
    }

    sealed class FixedCombatRng : ICombatRng
    {
        readonly int _value;
        public FixedCombatRng(int value) => _value = value;
        public int Next(int exclusiveMax) => Math.Min(_value, exclusiveMax - 1);
    }
}
