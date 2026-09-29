using System.Linq;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Aura;

/// <summary>
/// aura-skill T16: the twelve auras, one per aptitude, as DATA — grant channel(s), contest channel(s),
/// and the aptitude id that reads `share` for them (`AuraMagnitude.Compute`'s second axis).
///
/// <para><b>Not authored as `world-buff.*` DB containers, and that is a stated scope limit, not an
/// oversight.</b> `spec-aura-content.md` §2 itself proves a `world-buff.*` container is not read by
/// anything today — `TraitAtomSource.FromContainers` only accepts `ContainerKind.Trait`, and making a
/// `world-buff.*` row reachable through the live-lawn scope/grant pipeline
/// (`BattlefieldOwnSideReactor`/`ScopeCompatibility`) is explicitly `aura-delivery-path`'s own job, a
/// SEPARATE module the spec says this one "cannot ship before" — and `aura-delivery-path` remains
/// unspecced and deferred (this program's own earlier finding, R4/audit D5). Authoring a `world-buff.*`
/// row nobody can read would be exactly the "content-side face of D5" the spec warns against, not
/// progress. This catalog instead feeds the delivery mechanism THIS program actually built and proved
/// end to end — T12's `ActiveCommanderAura`/`BattleDerivedModifierLedger`, battle-scoped — so the
/// twelve auras are real, tested, and reachable, even though the live-lawn container path is not.</para>
///
/// <para><b>Every aura writes `.omni`</b> — settled by arithmetic, not preference
/// (`spec-aura-content.md` §4's own derivation): `CombatDerivedReader` reads `omni + element`
/// additively with weights summing to 1.0, so an omni write and an all-six-element write are
/// numerically IDENTICAL at 1/6th the authoring cost, and parry/block/reflect/crit-resist are read
/// omni-only in production — an element-slot version of those four auras would be read by nothing.</para>
///
/// <para><b>Opposition closure holds over the non-exempt set, not absolutely.</b> Two declared
/// exemptions: Retribution's `combat.reflect.resist.damage` contest channel is real but unbacked — no
/// aura grants it. Focus reverses entirely (`RelationKind.Self`, buffs the commander's own action
/// cooldowns, contests nothing) — its content is a documented gap, not a channel pair, since it does
/// not compose through this catalog's grant/contest shape at all.</para>
/// </summary>
public sealed record AuraContentRow(
    string AuraId,
    string AptitudeId,
    IReadOnlyList<string> GrantChannels,
    IReadOnlyList<string> ContestChannels,
    bool IsReversed = false);

public static class AuraContentCatalog
{
    public static readonly IReadOnlyList<AuraContentRow> All = new[]
    {
        new AuraContentRow("Might", "Might",
            new[] { DerivedStatChannels.CombatPowerOmni },
            new[] { DerivedStatChannels.CombatDefenseOmni }),

        new AuraContentRow("Fortitude", "Fortitude",
            new[] { DerivedStatChannels.CombatDefenseOmni },
            new[] { DerivedStatChannels.CombatPowerOmni }),

        new AuraContentRow("Vigor", "Vigor",
            new[] { DerivedStatChannels.CombatShieldCapacityOmni },
            new[] { DerivedStatChannels.CombatShieldPenOmni }),

        new AuraContentRow("Onslaught", "Onslaught",
            new[] { DerivedStatChannels.CombatBlockBreakOmni, DerivedStatChannels.CombatParryBreakOmni },
            new[] { DerivedStatChannels.CombatBlockRateOmni, DerivedStatChannels.CombatParryRateOmni }),

        new AuraContentRow("Agility", "Agility",
            new[] { DerivedStatChannels.CombatDodgeOmni },
            new[] { DerivedStatChannels.CombatAccuracyOmni }),

        new AuraContentRow("Composure", "Composure",
            new[] { DerivedStatChannels.CombatCritResistOmni, DerivedStatChannels.CombatCritResistDamageOmni },
            new[] { DerivedStatChannels.CombatCritRateOmni }),

        new AuraContentRow("Pierce", "Pierce",
            new[] { DerivedStatChannels.CombatShieldPenOmni },
            new[] { DerivedStatChannels.CombatShieldCapacityOmni }),

        new AuraContentRow("Bulwark", "Bulwark",
            new[] { DerivedStatChannels.CombatBlockRateOmni, DerivedStatChannels.CombatParryRateOmni },
            new[] { DerivedStatChannels.CombatBlockBreakOmni, DerivedStatChannels.CombatParryBreakOmni }),

        // Exempt from opposition closure: the contest channel is real but unbacked -- no aura grants it.
        new AuraContentRow("Retribution", "Retribution",
            new[] { DerivedStatChannels.CombatReflectDamageOmni },
            new[] { DerivedStatChannels.CombatReflectResistDamageOmni }),

        new AuraContentRow("Precision", "Precision",
            new[] { DerivedStatChannels.CombatAccuracyOmni },
            new[] { DerivedStatChannels.CombatDodgeOmni }),

        new AuraContentRow("Ferocity", "Ferocity",
            new[] { DerivedStatChannels.CombatCritRateOmni, DerivedStatChannels.CombatCritDamageOmni },
            new[] { DerivedStatChannels.CombatCritResistOmni }),

        // Reverses entirely (spec §4.1): RelationKind.Self, buffs the commander's OWN action
        // cooldowns (divisive form, matching shipped TurnReadiness.EffectiveRate) -- does not compose
        // through the grant/contest channel shape at all, so both lists are empty by construction.
        new AuraContentRow("Focus", "Focus", Array.Empty<string>(), Array.Empty<string>(), IsReversed: true),
    };

    public static bool IsKnown(string? auraId) => auraId != null && All.Any(a => a.AuraId == auraId);

    public static AuraContentRow Resolve(string auraId) =>
        All.FirstOrDefault(a => a.AuraId == auraId)
        ?? throw new KeyNotFoundException($"No aura content row for '{auraId}'.");

    const string ContainerPrefix = "world-buff.aura-";

    /// <summary>
    /// backlog-clear BP2/AU2: the `world-buff.*` container id an aura's content lives at
    /// (`spec-aura-content.md` §6: "twelve `world-buff.aura-*` rows"). Every `AuraId` above is a
    /// single lowercase-safe word, so a plain `ToLowerInvariant()` is the whole transform — no general
    /// kebab-case helper is worth adding for that. One direction only lives here; the producer never
    /// needs the reverse without also knowing it came from this prefix.
    /// </summary>
    public static string ContainerId(string auraId) => ContainerPrefix + auraId.ToLowerInvariant();

    /// <summary>The inverse of <see cref="ContainerId"/>, for reading a binding's own container id
    /// back into the aura it belongs to (BP2's reconcile needs this to tell one aura's binding from
    /// another's without a second durable column). Returns null for anything not shaped like an aura
    /// container — including another `world-buff.*` row this catalog does not own.</summary>
    public static string? AuraIdForContainer(string? containerId)
    {
        if (string.IsNullOrEmpty(containerId) || !containerId.StartsWith(ContainerPrefix, StringComparison.Ordinal))
            return null;
        var suffix = containerId[ContainerPrefix.Length..];
        var row = All.FirstOrDefault(a => string.Equals(a.AuraId, suffix, StringComparison.OrdinalIgnoreCase));
        return row?.AuraId;
    }
}
