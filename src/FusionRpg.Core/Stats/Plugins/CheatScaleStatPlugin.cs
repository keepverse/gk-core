using FusionRpg.Contracts;

namespace FusionRpg.Core.Stats.Plugins;

/// <summary>Maps legacy StatsConfig / Tab A into Flat + More mods. Percent p → More(p-1).</summary>
public sealed class CheatScaleStatPlugin : IStatModifierPlugin
{
    public const string Id = "cheat.scale";
    public string PluginId => Id;
    public int Order => 900;

    readonly StatModifierFactory _mods = new();

    public void Contribute(StatContext ctx, IModifierBagEditor bag)
    {
        bag.WithdrawPlugin(Id);
        if (!ctx.ApplyStats || ctx.CheatScale == null) return;

        var side = ctx.Side == StatSide.Plant ? ctx.CheatScale.Plants : ctx.CheatScale.Zombies;
        EmitSide(bag, side);
    }

    void EmitSide(IModifierBagEditor bag, StatMod m)
    {
        const string kind = Id;
        const string src = "tabA";

        void MorePct(string channel, float percent)
        {
            var more = percent - 1f;
            if (Math.Abs(more) < 0.0001f) return;
            bag.Upsert(_mods.More(Id, kind, src, channel, more));
        }

        void Flat(string channel, long flat)
        {
            if (flat == 0) return;
            bag.Upsert(_mods.Flat(Id, kind, src, channel, flat));
        }

        MorePct(StatChannels.Hp, m.HpPercent);
        MorePct(StatChannels.MaxHp, m.HpPercent);
        Flat(StatChannels.Hp, m.HpFlat);
        Flat(StatChannels.MaxHp, m.HpFlat);

        MorePct(StatChannels.Atk, m.AttackPercent);
        Flat(StatChannels.Atk, m.AttackFlat);

        MorePct(StatChannels.Defense, m.DefensePercent);
        Flat(StatChannels.Defense, m.DefenseFlat);

        // Zombie armor follows HP% / flat (matches prior GameHooks behavior).
        MorePct(StatChannels.Arm1, m.HpPercent);
        MorePct(StatChannels.Arm1Max, m.HpPercent);
        MorePct(StatChannels.Arm2, m.HpPercent);
        MorePct(StatChannels.Arm2Max, m.HpPercent);
        Flat(StatChannels.Arm1, m.HpFlat);
        Flat(StatChannels.Arm1Max, m.HpFlat);
        Flat(StatChannels.Arm2, m.HpFlat);
        Flat(StatChannels.Arm2Max, m.HpFlat);
    }
}
