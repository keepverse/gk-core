using FusionRpg.Core.Commanders;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Stats.Derived;

/// <summary>
/// GG-49 SourceId grammar for Hot derived contributions. Producers mint via these helpers;
/// sheet projections parse them into fiction labels. Empty / bare "generic" ids are defects.
/// </summary>
public static class ContributionSourceIds
{
    public const string Progression = "rpg.progression";

    /// <summary>UniqueActor Hub base resource.max/regen seed (Condition /sheet pools).</summary>
    public const string ResourceBaseline = "rpg.resource.base";

    /// <summary>Battle baseline seeds (defense/accuracy/dodge/crit/affinity/tempo) re-homed from
    /// BattleStatComposer — same dotted-const shape as <see cref="ResourceBaseline"/>, not a new
    /// grammar family. Follow-up: confirm the fiction label for sheet surfaces.</summary>
    public const string BattleBaseline = "rpg.battle.base";

    /// <summary>W11 (battle-derived-wire T6): the ONE projection of battle's primary `defense`
    /// channel into `combat.defense.omni`, recomputed from <c>BattleStatModifierLedger</c>. One id
    /// because the projection is one value per actor, not one per contributing status/grant — a
    /// per-source id would count the same phased total once per source.
    /// <c>BattleStatModifierLedger.PushDefenseToDerived</c> is its only writer.</summary>
    public const string BattleDefenseProjection = "rpg.battle.defense";

    public static string Equip(string role, string itemRefId)
    {
        var r = string.IsNullOrWhiteSpace(role) ? "unknown" : role.Trim();
        var item = string.IsNullOrWhiteSpace(itemRefId) ? "unknown" : itemRefId.Trim();
        return $"equip:{r}:{item}";
    }

    /// <summary>
    /// A socketed insert's contribution (species-gear-chain T21, §8.1 amendment): the insert binds
    /// at its host's role (owner decision, Round-3 #5), and the socket index rides in the id because
    /// two identical gems in two sockets are two contributions — a list collapsing them would lie
    /// about where the number came from. Minting <c>equip:{role}:{host}</c> for an insert instead
    /// would make it indistinguishable from the host's own affixes: wrong-but-plausible attribution,
    /// the defect this arm exists to prevent.
    /// </summary>
    public static string Insert(string role, string hostItemRefId, int socketIndex)
    {
        var r = string.IsNullOrWhiteSpace(role) ? "unknown" : role.Trim();
        var item = string.IsNullOrWhiteSpace(hostItemRefId) ? "unknown" : hostItemRefId.Trim();
        return $"insert:{r}:{item}#{socketIndex}";
    }

    /// <summary>
    /// A satisfied Strain/Splice's contribution (strain-splice-host combo-bind, arm 2) — the
    /// <c>combo:</c> row <c>actor-hub-ssot.md</c> §8.1 reserved, plus the circuit suffix the
    /// eight-socket topology requires: an eight-socket host can carry the SAME combination in circuit
    /// 0 and circuit 1, and the reserved form would collapse two contributions into one id — the same
    /// reason <see cref="Insert"/> carries <c>#{socketIndex}</c>. The host item ref is the specimen
    /// wearing the host, so a combination is attributable to its item like an insert is.
    /// </summary>
    public static string Combo(string role, string hostItemRefId, string comboId, int circuit)
    {
        if (circuit < 0)
            throw new ArgumentOutOfRangeException(nameof(circuit), circuit, "a circuit index is 0-based");
        var r = string.IsNullOrWhiteSpace(role) ? "unknown" : role.Trim();
        var item = string.IsNullOrWhiteSpace(hostItemRefId) ? "unknown" : hostItemRefId.Trim();
        return $"combo:{r}:{item}:{NullToUnknown(comboId)}#c{circuit}";
    }

    public static string Tree(string treeId, string nodeId) =>
        $"tree.{NullToUnknown(treeId)}.{NullToUnknown(nodeId)}";

    public static string Aptitude(string share) => $"aptitude.{NullToUnknown(share)}";

    /// <summary>
    /// species-progression SP3.8 (species-layer-delivery step 6.1's own prerequisite) — the per-scope
    /// sibling of <see cref="Aptitude(string)"/>. `Commander` keeps the unchanged, unscoped form
    /// (every existing commander-scope contribution stays byte-identical); every OTHER scope carries
    /// its own scope text (<see cref="AllocationScopeText.ToText"/>) so a species/aspect/unique-scope
    /// allocation's contribution is never conflated with the commander's. Nothing emits the per-scope
    /// form until step 6.1 (species-layer-delivery) wires it — this helper exists so that module can
    /// mint it without inventing its own grammar.
    /// </summary>
    public static string Aptitude(AllocationScope scope, string share) =>
        scope == AllocationScope.Commander
            ? Aptitude(share)
            : $"aptitude.{AllocationScopeText.ToText(scope)}.{NullToUnknown(share)}";

    public static string Status(string instanceId) => $"status:{NullToUnknown(instanceId)}";

    public static string Grant(string effectOrGrantId) => $"grant:{NullToUnknown(effectOrGrantId)}";

    public static string Primary(string sourceKind, string sourceId) =>
        $"primary:{NullToUnknown(sourceKind)}|{NullToUnknown(sourceId)}";

    /// <summary>
    /// species-progression SP3.1 (species-layer-projector, map C3): 1a's fixed core
    /// (`species-passive.{speciesId}`'s own `atoms`, never a roll or a pick). Replaces T4.6's
    /// `species-passive:{speciesId}` (minted locally in the now-retired <c>SpeciesPassiveAtomSource</c>
    /// — SP3.6 deleted it — outside this grammar and with no <see cref="FictionLabel"/> arm — map's
    /// own C3 defect) — that id could not tell 1a from 1b; this one and <see cref="SpeciesPlayer"/> can.
    /// </summary>
    public static string SpeciesBase(string speciesId) =>
        $"species-base:{ValidateSpeciesId(speciesId)}";

    /// <summary>
    /// species-progression SP3.1: 1b's non-core atoms (pool rolls, forced picks) for one ledger
    /// instance. <paramref name="mechanism"/> names how the instance came to have that roll (the
    /// existing pool/pick vocabulary, not new grammar) — never the same SourceId as the 1a core it was
    /// rolled onto, so the two layers stay attributable independently.
    /// </summary>
    public static string SpeciesPlayer(string speciesId, string mechanism) =>
        $"species-player:{ValidateSpeciesId(speciesId)}:{NullToUnknown(mechanism)}";

    /// <summary>
    /// species-progression SP3.1: one funded aptitude edge from a 2b (empire species progression)
    /// allocation — the species term alone, never merged with the commander (owner ruling R2,
    /// 2026-09-18). <paramref name="empire"/>'s token is its own <see cref="EmpireId.Value"/>
    /// unchanged (today's lower-case tokens, e.g. <c>"dave"</c>/<c>"zomboss"</c> — never re-spelled).
    /// The save is never in the id: an actor composes inside exactly one save, so two saves' rows
    /// never meet in one fold.
    /// </summary>
    public static string SpeciesEmpire(EmpireId empire, string speciesId, string aptitudeId) =>
        $"species-empire:{NullToUnknown(empire.Value)}:{ValidateSpeciesId(speciesId)}:{NullToUnknown(aptitudeId)}";

    /// <summary>Fiction label for InspectSplit — never raw-only when grammar matches.</summary>
    public static string FictionLabel(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return "(unattributed)";
        if (string.Equals(sourceId, Progression, StringComparison.Ordinal)) return "Progression";
        if (string.Equals(sourceId, ResourceBaseline, StringComparison.Ordinal)) return "Resource base";

        if (sourceId.StartsWith("equip:", StringComparison.Ordinal))
        {
            var parts = sourceId.Split(':', 3);
            var role = parts.Length > 1 ? parts[1] : "unknown";
            var item = parts.Length > 2 ? parts[2] : "";
            return string.IsNullOrEmpty(item) || item == "unknown"
                ? $"Equip · {role}"
                : $"Equip · {role} ({item})";
        }

        if (sourceId.StartsWith("insert:", StringComparison.Ordinal))
        {
            var rest = sourceId["insert:".Length..];
            var hash = rest.LastIndexOf('#');
            var head = hash < 0 ? rest : rest[..hash];
            var index = hash < 0 ? "?" : rest[(hash + 1)..];
            var parts = head.Split(':', 2);
            var role = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "unknown";
            var item = parts.Length > 1 ? parts[1] : "";
            return string.IsNullOrEmpty(item) || item == "unknown"
                ? $"Insert · {role} (socket {index})"
                : $"Insert · {role} ({item} socket {index})";
        }

        if (sourceId.StartsWith("combo:", StringComparison.Ordinal))
        {
            // combo:{role}:{hostItemRef}:{comboId}#c{circuit} — the circuit suffix is parsed off the
            // END first, exactly as Insert's socket index is, because the comboId itself contains no
            // '#' but the role/host/comboId head contains ':', so the split order is load-bearing.
            var rest = sourceId["combo:".Length..];
            var hash = rest.LastIndexOf('#');
            var head = hash < 0 ? rest : rest[..hash];
            var circuit = hash < 0 ? "?" : rest[(hash + 1)..];
            var parts = head.Split(':', 3);
            var role = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "unknown";
            var item = parts.Length > 1 ? parts[1] : "";
            var combo = parts.Length > 2 ? parts[2] : "";
            return string.IsNullOrEmpty(item) || item == "unknown"
                ? $"Combination · {combo} ({role}, {circuit})"
                : $"Combination · {combo} · {role} ({item}, {circuit})";
        }

        if (sourceId.StartsWith("aptitude.", StringComparison.Ordinal))
        {
            // species-progression SP3.8: two shapes share this prefix -- Commander's unchanged
            // `aptitude.{Share}` (one segment) and every other scope's `aptitude.{scopeText}.{Share}`
            // (two segments, Aptitude(AllocationScope, string)'s own new arm).
            var rest = sourceId["aptitude.".Length..];
            var dot = rest.IndexOf('.');
            return dot < 0
                ? $"Aptitude · {rest}"
                : $"Aptitude · {rest[..dot]} · {rest[(dot + 1)..]}";
        }

        if (sourceId.StartsWith("tree.", StringComparison.Ordinal))
            return $"Tree · {sourceId["tree.".Length..].Replace('.', '/')}";

        if (sourceId.StartsWith("status:", StringComparison.Ordinal))
            return $"Status · {sourceId["status:".Length..]}";

        if (sourceId.StartsWith("grant:", StringComparison.Ordinal))
            return $"Grant · {sourceId["grant:".Length..]}";

        if (sourceId.StartsWith("primary:", StringComparison.Ordinal))
            return $"Primary · {sourceId["primary:".Length..]}";

        if (sourceId.StartsWith("species-base:", StringComparison.Ordinal))
            return $"Species · {sourceId["species-base:".Length..]}";

        if (sourceId.StartsWith("species-player:", StringComparison.Ordinal))
        {
            var rest = sourceId["species-player:".Length..];
            var parts = rest.Split(':', 2);
            var speciesId = parts.Length > 0 ? parts[0] : "unknown";
            var mechanism = parts.Length > 1 ? parts[1] : "unknown";
            return $"Your species · {speciesId} ({mechanism})";
        }

        if (sourceId.StartsWith("species-empire:", StringComparison.Ordinal))
        {
            var rest = sourceId["species-empire:".Length..];
            var parts = rest.Split(':', 3);
            var speciesId = parts.Length > 1 ? parts[1] : "unknown";
            var aptitudeId = parts.Length > 2 ? parts[2] : "unknown";
            return $"Empire species · {speciesId} · {aptitudeId}";
        }

        return sourceId;
    }

    static string NullToUnknown(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "unknown" : s.Trim();

    /// <summary>
    /// Grammar safety (species-layer-projector spec, "Grammar safety"): a species id is asserted, not
    /// trusted, the same rule <c>CreatureProgressionSource.ValidatePart</c>
    /// (<c>CreatureProgressionSource.cs:59-66</c>) applies where a species id enters a progression
    /// source — a ':' inside it would corrupt every colon-delimited parse above.
    /// </summary>
    static string ValidateSpeciesId(string speciesId)
    {
        if (string.IsNullOrWhiteSpace(speciesId))
            throw new ArgumentException("species id is required", nameof(speciesId));
        var trimmed = speciesId.Trim();
        if (trimmed.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("species id cannot contain ':'", nameof(speciesId));
        return trimmed;
    }
}
