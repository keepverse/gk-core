namespace FusionRpg.Core.Stats.Plugins;

/// <summary>Tab B/C absolute writers — Override phase per channel.</summary>
public sealed class CheatAbsoluteStatPlugin : IStatModifierPlugin
{
    public const string Id = "cheat.absolute";
    public string PluginId => Id;
    public int Order => 950;

    readonly StatModifierFactory _mods = new();

    public void Contribute(StatContext ctx, IModifierBagEditor bag)
    {
        bag.WithdrawPlugin(Id);

        if (ctx.CheatAbsolute != null)
            foreach (var kv in ctx.CheatAbsolute)
            {
                if (kv.Value < 0) continue; // convention: negative = skip
                bag.Upsert(_mods.Override(Id, Id, "tabBC", kv.Key, kv.Value, priority: 100));
            }

        // E16: the three real-valued channels — attackInterval, produceInterval, zombieSpeed. They
        // used to be written straight to the Unity field from their cheat keys, behind the
        // composer's back; now they arrive here as Overrides like P-HP and P-ATK always have, so
        // there is one path to the field and the single-writer law holds.
        //
        // E38 (spec-entity-fields-12plus.md §2b): this loop used to skip any non-positive value,
        // which was harmless while every real-valued key refused zero anyway (E16's three, and
        // E38's own P-SPEED/P-MOVE/Z-SPD/Z-SPD-O). It stopped being harmless the moment this map
        // started carrying keys where zero is a LEGAL value (P-SHIELD, P-ATK-CD, P-PROD-CD, P-LEVEL,
        // P-SHOOTLVL, Z-ARMOR-F, Z-TAKEMULT — "no shield", "ready now", "immune" are things an
        // operator can mean) and one where NEGATIVE is legal too (P-ATK-ADD — an unguarded signed
        // delta, §2b, decided 2026-09-03). A blanket sign filter here would silently break both.
        // CheatState.BuildPlantAbsoluteReal / BuildZombieAbsoluteReal already apply the correct
        // per-key guard (three different shapes — see that method's own doc comment) before a value
        // ever reaches this dictionary, so nothing needs re-checking here.
        if (ctx.CheatAbsoluteReal != null)
            foreach (var kv in ctx.CheatAbsoluteReal)
                bag.Upsert(_mods.Override(Id, Id, "tabBC", kv.Key, kv.Value, priority: 100));
    }
}
