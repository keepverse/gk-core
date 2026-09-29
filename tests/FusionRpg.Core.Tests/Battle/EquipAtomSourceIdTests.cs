using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Scope;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

public class EquipAtomSourceIdTests
{
    [Fact]
    public void DerivedAtomsFor_uses_equip_role_item_SourceId()
    {
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.equip-test", "", 1),
            KindId = "stat.derived",
            FamilyId = "atom.equip-test",
            Variant = "",
            Tier = 1,
            Name = "Equip Test",
            ParamsJson = $"{{\"channel\":\"{DerivedStatChannels.CombatPowerFire}\",\"op\":\"flat\",\"amount\":200}}",
        };
        var source = EquipAtomSource.FromEquippedResolver(_ => new[]
        {
            new EquippedAtomInput("armament-primary", "item-stem", atom)
        });

        var bound = Assert.Single(source.DerivedAtomsFor("spec-1"));
        Assert.Equal(ContributionSourceIds.Equip("armament-primary", "item-stem"), bound.SourceId);
        Assert.Equal(200L, bound.Amount);
    }

    [Fact]
    public void FromResolver_legacy_mints_equip_unknown_atomId_not_bare_atom_id()
    {
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.equip-test", "", 1),
            KindId = "stat.derived",
            FamilyId = "atom.equip-test",
            Variant = "",
            Tier = 1,
            Name = "Equip Test",
            ParamsJson = $"{{\"channel\":\"{DerivedStatChannels.CombatPowerFire}\",\"op\":\"flat\",\"amount\":30}}",
        };
        var source = EquipAtomSource.FromResolver(_ => new[] { atom });
        var bound = Assert.Single(source.DerivedAtomsFor("s42"));
        Assert.Equal(ContributionSourceIds.Equip("unknown", atom.AtomId), bound.SourceId);
        Assert.StartsWith("equip:unknown:", bound.SourceId, StringComparison.Ordinal);
    }

    [Fact]
    public void A_socketed_insert_moves_the_channel_through_ActorHub_and_is_attributed_to_the_insert()
    {
        var affix = DerivedAtom("atom.host-affix", 5);
        var gem = DerivedAtom("atom.insert-test", 45);
        var source = EquipAtomSource.FromEquippedResolver(_ => new[]
        {
            new EquippedAtomInput("armament-primary", "item-stem", affix),
            new EquippedAtomInput("armament-primary", "item-stem", gem, SocketIndex: 1),
        });

        // T21's own acceptance, literally: socket -> deploy -> ActorHub.ResolveDerivedWithContributions
        // -> the channel moved, by exactly the gem's own number over the host's own affix.
        var hub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hub.Register(new AtomDerivedSubsystem(ctx => source.DerivedAtomsFor(ctx.EntityKey)));
        var ctx = new StatContext
        {
            Side = StatSide.Plant,
            EntityKey = "specimens/s1",
            Baseline = new EntityBaseline { Hp = 100, MaxHp = 100, Atk = 10 },
        };

        var (snapshot, contributions) = hub.ResolveDerivedWithContributions(ctx);
        Assert.Equal(50, snapshot.Get(DerivedStatChannels.CombatPowerFire));

        // The gem's number is attributed to the INSERT (role, host item, socket index), never to the
        // host's own affix — the wrong-but-plausible defect MintSourceId's Insert arm exists to prevent.
        var insert = Assert.Single(contributions.ContributionsFor(DerivedStatChannels.CombatPowerFire),
            c => c.SourceId.StartsWith("insert:", StringComparison.Ordinal));
        Assert.Equal(ContributionSourceIds.Insert("armament-primary", "item-stem", 1), insert.SourceId);
        Assert.Equal("insert:armament-primary:item-stem#1", insert.SourceId);
        Assert.Equal(45, insert.Value);

        // The same host without the socketed insert reads the affix alone, so the 45 above is the
        // gem's contribution and not a re-attributed host number.
        var hostOnly = EquipAtomSource.FromEquippedResolver(_ => new[]
        {
            new EquippedAtomInput("armament-primary", "item-stem", affix),
        });
        var hostHub = new FusionRpg.Core.Stats.Derived.ActorHub(StatSystemBootstrap.CreateDefault());
        hostHub.Register(new AtomDerivedSubsystem(ctx => hostOnly.DerivedAtomsFor(ctx.EntityKey)));
        Assert.Equal(5, hostHub.ResolveDerived(ctx).Get(DerivedStatChannels.CombatPowerFire));
    }

    [Fact]
    public void The_combination_contributes_under_its_own_source_id()
    {
        var atom = new AtomRow
        {
            AtomId = AtomRow.DeriveId("atom.combo-test", "", 1),
            KindId = "stat.derived",
            FamilyId = "atom.combo-test",
            Variant = "",
            Tier = 1,
            Name = "Combo Test",
            ParamsJson = $"{{\"channel\":\"{DerivedStatChannels.CombatPowerFire}\",\"op\":\"flat\",\"amount\":40}}",
        };
        var source = EquipAtomSource.FromEquippedResolver(_ => new[]
        {
            new EquippedAtomInput("armament-primary", "item-stem", atom,
                ComboId: "combo.strain-might-offense", Circuit: 1)
        });

        var bound = Assert.Single(source.DerivedAtomsFor("spec-1"));
        Assert.Equal(ContributionSourceIds.Combo("armament-primary", "item-stem",
            "combo.strain-might-offense", 1), bound.SourceId);
        Assert.Equal("combo:armament-primary:item-stem:combo.strain-might-offense#c1", bound.SourceId);
        // Never the equip or insert arm: a combination's number is attributed to the combination.
        Assert.StartsWith("combo:", bound.SourceId, StringComparison.Ordinal);
    }

    static AtomRow DerivedAtom(string familyId, long amount) => new()
    {
        AtomId = AtomRow.DeriveId(familyId, "", 1),
        KindId = "stat.derived",
        FamilyId = familyId,
        Variant = "",
        Tier = 1,
        Name = familyId,
        ParamsJson = $"{{\"channel\":\"{DerivedStatChannels.CombatPowerFire}\",\"op\":\"flat\",\"amount\":{amount}}}",
    };
}
