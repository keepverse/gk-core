using System.Linq;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Battle;

/// <summary>
/// A18e (spec-battle-live-stat-modifiers.md §1): a sourced, per-actor, per-channel modifier ledger so
/// a triggered `stat.modify` grant can affect live combat, composed through the SAME
/// Flat→Increased(sum)→More(product)→Override phased math the overlay's primary stat system already
/// uses (<see cref="PhasedComposeStrategy"/>) — never a parallel percent-math implementation for
/// battle specifically, never <c>DerivedComposer</c> (wrong op vocabulary; that composer belongs to
/// `stat.derived`, a different kind).
/// </summary>
public sealed class BattleStatModifierLedger
{
    static readonly PhasedComposeStrategy Strategy = new();

    /// <summary>The primary channel whose phased recompose projects into
    /// <c>combat.defense.omni</c> — named once so <c>ExecModifyStat</c>, the status handlers and
    /// <see cref="PushDefenseToDerived"/> cannot drift on the spelling.</summary>
    public const string DefenseChannel = "defense";

    readonly Dictionary<(string ActorKey, string Channel), List<(string SourceGrantId, StatModifier Mod)>> _mods = new();

    /// <summary>Sourced by grant id, so <see cref="RemoveBySource"/> can revert exactly one source's
    /// own contribution without disturbing another source on the same channel.</summary>
    public void Add(string actorKey, string channel, string sourceGrantId, StatModifier mod)
    {
        var key = (actorKey, channel);
        if (!_mods.TryGetValue(key, out var list))
            _mods[key] = list = new List<(string, StatModifier)>();
        list.Add((sourceGrantId, mod));
    }

    /// <summary>Removes every (channel, mod) tuple this source added, across all of an actor's
    /// channels — proven directly in this module's own tests; nothing built by A17–A20 calls it in
    /// production yet (no grant is ever withdrawn), named honestly in the spec rather than hidden.</summary>
    public void RemoveBySource(string actorKey, string sourceGrantId)
    {
        foreach (var key in _mods.Keys)
        {
            if (key.ActorKey != actorKey) continue;
            _mods[key].RemoveAll(t => t.SourceGrantId == sourceGrantId);
        }
    }

    public IReadOnlyList<StatModifier> For(string actorKey, string channel) =>
        _mods.TryGetValue((actorKey, channel), out var list)
            ? list.Select(t => t.Mod).ToList()
            : Array.Empty<StatModifier>();

    /// <summary>The one entry point every live-read call site uses — <see cref="ActorState.LiveAtk"/>,
    /// the `Derived`-channel recompose, and `BattleEffectSink`'s own `ModifyStat` branch — never
    /// <see cref="PhasedComposeStrategy"/> directly, so there is exactly one place this module's own
    /// recompose math lives.</summary>
    public long Recompose(string actorKey, string channel, long baseline) =>
        (long)Math.Round(Strategy.ComposeChannel(baseline, For(actorKey, channel)));

    /// <summary>
    /// W11 (battle-derived-wire T6) — the ONE writer of <c>combat.defense.omni</c> from the primary
    /// <see cref="DefenseChannel"/>. Both the <c>stat.modify</c> executor and a status's own
    /// <c>defense</c> StatMod call this, and it writes through
    /// <see cref="BattleDerivedModifierLedger"/> — the ledger whose <c>Recompose</c> is the per-round
    /// writer for every <c>combat.*</c> channel — so an aura targeting the same channel and this
    /// projection SUM on the next round instead of clobbering each other (audit §4.1's two-ledger
    /// conflict, closed).
    ///
    /// <para><b>The value pushed is a CONTRIBUTION, not the absolute defense.</b> It is the phased
    /// recompose MINUS <see cref="IBattleStatTarget.BaselineDefense"/>, because the frozen base
    /// (<see cref="IBattleStatTarget.BaseDerived"/>) already carries that baseline for this channel —
    /// the registry default is 0 and <c>BattleBaselineSubsystem</c> seeds it from the same setup
    /// field. Writing the absolute value instead would discard every other contribution
    /// <see cref="BattleDerivedModifierLedger.Recompose"/> knows about, which is exactly the defect
    /// this closes.</para>
    ///
    /// <para><b>One source id, not one per contributing status/grant.</b> The projection is ONE value
    /// per actor, recomputed from the whole <see cref="BattleStatModifierLedger"/> — pushing it under
    /// each caller's own source would count the same phased total once per source. The constant
    /// <see cref="ContributionSourceIds.BattleDefenseProjection"/> makes every push REPLACE the
    /// previous one, so two sources' mods sum once in the ledger and once in the derived value.</para>
    /// </summary>
    public static void PushDefenseToDerived(
        string actorKey, BattleStatModifierLedger ledger,
        BattleDerivedModifierLedger derivedLedger, IBattleStatTarget target)
    {
        if (target is null) return;
        var phased = ledger.Recompose(actorKey, DefenseChannel, target.BaselineDefense);
        derivedLedger.Set(actorKey, DerivedStatChannels.CombatDefenseOmni,
            ContributionSourceIds.BattleDefenseProjection, phased - target.BaselineDefense);
        derivedLedger.Recompose(actorKey, target.BaseDerived, target.Derived);
    }
}
