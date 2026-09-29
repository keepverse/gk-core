using FusionRpg.Core.Creatures;
using FusionRpg.Core.Narrative.Vocabulary;
using FusionRpg.Core.World;
using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Movement;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.Narrative.Doctrine;

/// <summary>One lean key's share: <paramref name="Count"/> of the observed <paramref name="Total"/> for that
/// key family, in per-mille.</summary>
public sealed record DoctrineShare(string Key, int Count, int Total)
{
    /// <summary>Per-mille, integer: a share is a bounded ratio, and 1000‰ is the whole observed set.</summary>
    public int ShareMilli => Total == 0 ? 0 : (int)(Count * 1000L / Total);
}

/// <summary>The reading: every key family's share, and the single lean the study bar advances on (or none).</summary>
public sealed record DoctrineReadingResult(IReadOnlyList<DoctrineShare> Shares, string? LeanKey, int LeanShareMilli)
{
    public static readonly DoctrineReadingResult NothingSeen = new(Array.Empty<DoctrineShare>(), null, 0);
}

/// <summary>
/// counter-doctrine's fog-correct reading (npc-story-events NR6.2, spec-counter-doctrine.md §1, Owner ruling
/// R18). The Rotwright reads **only what his faction observed**: his own latest observation record and this
/// turn's battles he took part in or saw. It never reads a battle OUTCOME, an enemy entity's identity or
/// history, or a character — a battle is a sighting, nothing more — which is why two worlds differing only
/// in who won, or in which antagonist entity fought, read identically.
///
/// <para><b>Pure and memoryless.</b> Every call recomputes from the world as it stands and the report it is
/// handed; nothing is cached and nothing is written. The share a turn produces is judged on what he SAW of
/// that turn, so keeping a lean out of his sight is real counter-play (R18).</para>
/// </summary>
public static class DoctrineReading
{
    public const string ElementPrefix = "element:";
    public const string PostureHold = "posture:hold";
    public const string GroundPrefix = "ground:";

    /// <summary>
    /// The reading over one turn. <paramref name="turnBattles"/> is this turn's report entries (the caller
    /// passes the `battle` lines it already has — the reading filters by kind); the two faction ids name the
    /// antagonist who studies and the player he studies.
    /// </summary>
    public static DoctrineReadingResult Of(
        WorldState world, IReadOnlyList<TurnReportEntry> turnBattles,
        string antagonistFactionId, string playerFactionId)
    {
        ArgumentNullException.ThrowIfNull(world);

        var threshold = (int)NarrativeTuningHub.Tuning.Doctrine["leanThresholdMilli"];

        // His latest observation: the study step runs in `Events`, before this turn's `Intel`, so what he
        // last saw is stamped turn - 1 (spec §1). One turn late is correct, not a bug.
        var seenTurn = world.CurrentTurn - 1;
        var observed = (world.Intel ?? Array.Empty<FactionIntel>())
            .FirstOrDefault(f => string.Equals(f.FactionId, antagonistFactionId, StringComparison.Ordinal))
            ?.Sectors.Where(s => s.LastSeenTurn == seenTurn)
            .ToList() ?? new List<IntelSnapshot>();

        var observedEntityIds = new HashSet<string>(StringComparer.Ordinal);

        // Source 1: forces he stood on the ground with and COUNTED. A glimpse records a strength band and no
        // composition, so it contributes nothing here.
        foreach (var snapshot in observed)
            foreach (var force in snapshot.Forces)
                if (force.Exact && string.Equals(force.OwnerFactionId, playerFactionId, StringComparison.Ordinal))
                    observedEntityIds.Add(force.EntityId);

        // Source 2: this turn's battles he took part in (a side his faction owns) or saw (its sector was in
        // his latest observation). Either way both sides join the observed set; the winner is never read.
        foreach (var entry in turnBattles ?? Array.Empty<TurnReportEntry>())
        {
            if (!string.Equals(entry.Kind, TurnReportKinds.Battle, StringComparison.Ordinal)) continue;

            var sides = BattleSidesOf(entry.Subject);
            if (sides.Count == 0) continue;

            var tookPart = sides.Any(id => OwnedBy(world, id, antagonistFactionId));
            var sawIt = entry.SectorId is { } sector
                && observed.Any(s => string.Equals(s.SectorId, sector, StringComparison.Ordinal));
            if (!tookPart && !sawIt) continue;

            foreach (var id in sides)
                if (OwnedBy(world, id, playerFactionId)) observedEntityIds.Add(id);
        }

        // Members are read from the entity AS IT STANDS (spec §1: intel carries no species roster, and a
        // roster can only shrink between his observation and this turn's Events). A force he counted that
        // has since died contributes nothing, which is the honest reading of "what he sees now".
        var observedForces = world.Entities
            .Where(e => observedEntityIds.Contains(e.EntityId)
                && string.Equals(e.OwnerFactionId, playerFactionId, StringComparison.Ordinal))
            .ToList();

        var shares = new List<DoctrineShare>();

        var byElement = new Dictionary<string, int>(StringComparer.Ordinal);
        var members = 0;
        foreach (var entity in observedForces)
            foreach (var member in entity.Members)
            {
                members++;
                var key = ElementPrefix + ElementWireId(member.SpeciesId);
                byElement[key] = byElement.GetValueOrDefault(key) + 1;
            }
        foreach (var (key, count) in byElement)
            shares.Add(new DoctrineShare(key, count, members));

        if (observedForces.Count > 0)
            shares.Add(new DoctrineShare(
                PostureHold,
                observedForces.Count(e => string.Equals(e.Stance, MovementPolicy.Hold, StringComparison.Ordinal)),
                observedForces.Count));

        var observedSectors = observed
            .Where(s => string.Equals(s.OwnerFactionId, playerFactionId, StringComparison.Ordinal))
            .ToList();
        var bySlot = new Dictionary<string, int>(StringComparer.Ordinal);
        var slots = 0;
        foreach (var sector in observedSectors)
            foreach (var slot in sector.Slots)
            {
                slots++;
                var key = GroundPrefix + slot.SlotTypeId;
                bySlot[key] = bySlot.GetValueOrDefault(key) + 1;
            }
        foreach (var (key, count) in bySlot)
            shares.Add(new DoctrineShare(key, count, slots));

        // A lean needs a non-empty observed set, a key at or above the tuning threshold, and strictly more
        // than every other key (30% fire against 30% ice is a tie, and a tie is no lean).
        var ordered = shares
            .Where(s => s.Total > 0)
            .OrderByDescending(s => s.ShareMilli)
            .ThenBy(s => s.Key, StringComparer.Ordinal)
            .ToList();
        if (ordered.Count == 0) return DoctrineReadingResult.NothingSeen;

        var top = ordered[0];
        var tiedAtTop = ordered.Skip(1).Any(s => s.ShareMilli == top.ShareMilli && top.ShareMilli > 0);
        var lean = top.ShareMilli >= threshold && !tiedAtTop ? top.Key : null;
        return new DoctrineReadingResult(shares, lean, top.ShareMilli);
    }

    /// <summary>The two sides of a battle entry, from its id: `t{turn}:{kind}:{location}:{attacker}[|{defender}]`
    /// (<see cref="BattleKinds.IdFor"/>). The id names both sides' entities, so no other field is needed and
    /// no name is ever looked up.</summary>
    static IReadOnlyList<string> BattleSidesOf(string? battleId)
    {
        if (string.IsNullOrWhiteSpace(battleId)) return Array.Empty<string>();
        var segments = battleId.Split(':');
        if (segments.Length < 4) return Array.Empty<string>();

        var sides = new List<string>();
        sides.AddRange(segments[3].Split('|').Where(id => id.Length > 0));
        return sides.Count == 0 ? Array.Empty<string>() : sides;
    }

    static bool OwnedBy(WorldState world, string entityId, string factionId) =>
        world.Entities.Any(e => string.Equals(e.EntityId, entityId, StringComparison.Ordinal)
            && string.Equals(e.OwnerFactionId, factionId, StringComparison.Ordinal));

    /// <summary>The element id a doctrine key uses — the same lower-case spelling the shipped element table
    /// carries and <see cref="DoctrineCatalog.ElementKeys"/> pins. An unknown species reads as its own
    /// species id only in the sense that no known element is invented: the caller gets the species' primary
    /// element when the catalog knows it, and a named gap when it does not.</summary>
    static string ElementWireId(string speciesId)
    {
        if (!CreatureSpeciesCatalog.IsKnown(speciesId)) return "unknown";
        return CreatureSpeciesCatalog.Get(speciesId).ElementPrimary.ToString().ToLowerInvariant();
    }
}
