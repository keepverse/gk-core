using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Display;

/// <summary>
/// N3 (spec-item-card.md): "a channel's unit is inseparable from its reader." <see cref="UnitClass"/>
/// itself already shipped (`Stats/Derived/StatClass.cs`, class-system's own eleven-member closed
/// ledger) — this is not a new enum, it is the item layer's lookup FACADE over it, extended with the
/// small set of PRIMARY channels `DerivedStatRegistry` does not cover (it is scoped to derived
/// channels only). A channel with a reader and no resolvable unit is <c>null</c> here, and the
/// caller's own validation turns that into `MissingUnitClass` — this class never guesses.
/// </summary>
public static class ChannelUnits
{
    /// <summary>The 8 primary `stat.modify` channels plus the two not-yet-promoted ones already
    /// declared on an atom family (`atom-family-library.md` §3.1) — `DerivedStatRegistry` is scoped to
    /// derived channels and does not carry these.</summary>
    static readonly IReadOnlyDictionary<string, UnitClass> Primary = new Dictionary<string, UnitClass>(StringComparer.Ordinal)
    {
        ["maxHp"] = UnitClass.GameUnits,
        ["hp"] = UnitClass.GameUnits,
        ["atk"] = UnitClass.GameUnits,
        ["defense"] = UnitClass.GameUnits,
        ["arm1"] = UnitClass.GameUnits,
        ["arm1Max"] = UnitClass.GameUnits,
        ["arm2"] = UnitClass.GameUnits,
        ["arm2Max"] = UnitClass.GameUnits,
        ["attackInterval"] = UnitClass.Milliseconds,
        ["produceInterval"] = UnitClass.Milliseconds,
        ["zombieSpeed"] = UnitClass.GameUnitsPerSecond,
    };

    /// <summary>
    /// Channel FAMILIES an affix family authors that <see cref="DerivedStatRegistry"/> does not
    /// register — matched by prefix, exactly the shape ssot-presentation.md §5.3 N3 specifies
    /// ("covering the 8 primary channels and the 12+4 derived families <b>by prefix pattern</b>").
    ///
    /// <para>⛔ <b>Every row's unit is the unit of the term it lands in, read off the consumer, not
    /// off the channel's name.</b> Both rows below are `g.elem-power`'s own mints
    /// (`gk-data/packs/fusion/data/seed/items/affix-families/g-elem-power.json`), authored ahead of a runtime that reads
    /// them — <see cref="FamilyExpansion"/> already refuses all three families that name them, by
    /// id, for having no shipped E30 pool. What makes a unit nameable anyway is that both channels
    /// state, in their own authoring notes, WHICH existing term they add into:</para>
    ///
    /// <list type="bullet">
    /// <item><c>combat.power.pierce.*</c> — <i>"a flat amount that offsets the matching-element
    /// <c>combat.defense.*</c> term on the far side of the same (power − defense) sum"</i>. That sum
    /// is <c>OverlayCombatCalculator.cs:145</c>, <c>(power - defense) + componentBonus</c> — a plain
    /// additive delta in game units, and both of its existing halves are
    /// <see cref="UnitClass.GameUnits"/> in <c>DerivedStatChannels.CombatFamilyUnitClass</c>. A term
    /// added into that sum cannot carry a different unit.</item>
    /// <item><c>combat.power.overflow.*</c> — <i>"a family authored as its own ledger so a later
    /// formula is free to treat 'overflow' power differently from base power"</i>: a second, flat
    /// power ledger (`op: Flat`, rendered "+{value} bonus {element} power"), same arithmetic shape
    /// as <c>combat.power.*</c> itself.</item>
    /// </list>
    ///
    /// <para>⛔ <b>Not <see cref="UnitClass.ReciprocalPoints"/>, despite the word "pierce".</b> The
    /// asymptotic <c>PierceFactor</c> channel is <c>combat.penetration.*</c>, a different, already
    /// registered H.1 family. Grouping <c>combat.power.pierce.*</c> with it by name would be exactly
    /// the "wrongly grouped … by tuning-file section rather than by formula shape" error
    /// <c>CombatFamilyUnitClass</c>'s own comment records having already been corrected once.</para>
    ///
    /// <para>These live here rather than in <c>DerivedStatChannels.CombatChannelFamilies</c> because
    /// joining that list REGISTERS 8 real channel slots per family in the derived-stat catalog —
    /// a derived-stats-program change with its own seed-catalog and doc-drift guards. Declaring a
    /// display unit is not the same act as registering a channel, and only the first is this lane's.
    /// When the derived-stats program registers these two families for real, delete the rows: the
    /// <see cref="For"/> lookup below already prefers the registry.</para>
    /// </summary>
    static readonly IReadOnlyList<(string Prefix, UnitClass Unit)> AuthoredChannelFamilies =
        new (string, UnitClass)[]
        {
            ("combat.power.pierce.", UnitClass.GameUnits),
            ("combat.power.overflow.", UnitClass.GameUnits),
        };

    /// <summary>
    /// Matches by prefix, the way derived readers already match generated element channels
    /// (`DerivedStatChannels.cs`'s own `…Prefix` constants) — a new element needs no new unit row.
    ///
    /// <para>⛔ <b>Reads <see cref="DerivedStatRegistry.TryResolveChannel"/>, not <c>TryGet</c>
    /// (fixed 2026-09-06, item module 10's Card pass).</b> The two are not the same question:
    /// <c>TryGet</c> asks "is this channel in the explicit table", <c>TryResolveChannel</c> asks
    /// "does this channel have a reader", which is exactly this class's own stated contract
    /// ("a channel with a reader and no resolvable unit is null here"). Against <c>TryGet</c> every
    /// GENERATED status family — <c>status.duration.*</c>, <c>status.intensity.*</c>,
    /// <c>status.immune.*</c>, and their reduction halves — reported <c>null</c> and would have
    /// become a <c>MissingUnitClass</c> rejection for a channel the engine reads perfectly well.
    /// An unknown channel still resolves to <c>null</c>: <c>TryResolveChannel</c>'s arms are all
    /// prefix-gated and its last statement is <c>return false</c>, so nothing is ever guessed.</para>
    /// </summary>
    public static UnitClass? For(string channelId, DerivedStatRegistry? derivedRegistry = null)
    {
        if (string.IsNullOrEmpty(channelId)) return null;
        if (Primary.TryGetValue(channelId, out var primary)) return primary;

        var registry = derivedRegistry ?? DerivedStatRegistry.CreateDefault();
        if (registry.TryResolveChannel(channelId, out var def)) return def.Unit;

        // The registry wins wherever it answers; these are only the families it does not carry.
        foreach (var (prefix, unit) in AuthoredChannelFamilies)
            if (channelId.StartsWith(prefix, StringComparison.Ordinal)
                && channelId.Length > prefix.Length)
                return unit;

        return null;
    }

    /// <summary>The concrete element a <c>{variant}</c>-templated channel is probed with. Structural,
    /// not tunable: it only has to be a real member of the element roster so the generated half of the
    /// channel family exists — which element it is cannot change the answer, because every element
    /// half of one family shares the family's unit by construction.</summary>
    const string ProbeElement = "fire";

    /// <summary>The concrete status a bare <c>status.*</c> family STEM is probed with. Same rule.</summary>
    const string ProbeStatus = "omni";

    /// <summary>
    /// The unit of a channel <b>as an affix family authors it</b>, which is not always a runtime
    /// channel id. The corpus writes two shapes <see cref="For"/> cannot resolve directly and should
    /// not be taught to, because neither is a channel anything ever reads:
    ///
    /// <list type="bullet">
    /// <item>an element TEMPLATE — <c>combat.power.{variant}</c> — which materialises one channel per
    /// element at expansion time (<c>FamilyExpansion</c>, W7.9: the variant deliberately does not
    /// materialise in the atom id);</item>
    /// <item>a bare family STEM — <c>status.resist</c>, <c>status.immune</c> — whose concrete status
    /// segment is supplied per variant.</item>
    /// </list>
    ///
    /// <para>Both are resolved by probing one concrete member, because a channel family's unit is a
    /// property of the family and not of the member. Returns <c>null</c> — never a guess — for
    /// anything that resolves to no reader even after probing; that null is what the validator turns
    /// into <see cref="DisplayRules.MissingUnitClass"/>.</para>
    /// </summary>
    public static UnitClass? ForAuthoredChannel(string channelId, DerivedStatRegistry? derivedRegistry = null)
    {
        if (string.IsNullOrEmpty(channelId)) return null;

        var registry = derivedRegistry ?? DerivedStatRegistry.CreateDefault();

        var direct = For(channelId, registry);
        if (direct is not null) return direct;

        if (channelId.Contains("{variant}", StringComparison.Ordinal))
            return For(channelId.Replace("{variant}", ProbeElement, StringComparison.Ordinal), registry);

        // A bare `status.*` stem: append one concrete member so the registry's own prefix arm fires.
        if (channelId.StartsWith("status.", StringComparison.Ordinal) && !channelId.EndsWith('.'))
            return For(channelId + "." + ProbeStatus, registry);

        return null;
    }
}
