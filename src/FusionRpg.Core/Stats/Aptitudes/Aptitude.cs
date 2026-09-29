using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>
/// class-system/spec-primary-stats.md §3 — the three posture groupings. A posture is READ (see
/// `DominantPosture`, P1.3), never stored: no actor field, no allocation scope, no persisted value is
/// ever typed as <see cref="Posture"/>. It exists only to group the twelve for display and for the
/// closed form's archetype seeds (`force`/`finesse`/`bastion` in gk-core/tools/CombatSim).
/// </summary>
public enum Posture { Force, Finesse, Bastion }

/// <summary>One aptitude — a SOURCE, never a registered channel (class-system-map.md §2aa: `share`
/// normalises over the actor's own total, so a granted aptitude would dilute the other eleven).
/// <paramref name="Ordinal"/> is append-only, matching every other roster in this codebase
/// (<see cref="FusionRpg.Core.Combat.Element.ElementRow"/>, resources/roster.json) — an existing
/// aptitude's ordinal never changes, a retired one's is never reused.</summary>
public sealed record AptitudeRow(string Id, Posture Posture, int Ordinal, string Role, string Reading);

/// <summary>
/// The twelve aptitudes, shipped in code exactly like <see cref="FusionRpg.Core.Combat.Element.ElementTable"/>
/// ships its six elements — <c>gk-data/packs/fusion/data/seed/aptitudes/roster.json</c> is the checked-in mirror for tooling
/// that cannot reference this assembly, not the load source (tunables-ssot.md §7.2: Core reads no
/// file). Ids and postures are ported verbatim from gk-core/tools/CombatSim's POC — `AptitudeTuning`'s edges
/// already name these twelve as `source` values, and residual-fit compares this program's output
/// against that tool's, so a spelling drift here would silently break that comparison.
/// </summary>
public static class AptitudeCatalog
{
    // Structural, not tunable (tunables-ssot T2): the SHAPE of the closed aptitude vocabulary, three
    // postures of four aptitudes each. Changing either redefines which aptitudes exist, a reviewed
    // vocabulary change, never a balance pass.
    public const int PostureCount = 3;
    public const int PerPosture = 4;

    /// <summary>Computed, never hand-typed — the count is a PRODUCT of the two facts above, so a
    /// thirteenth aptitude or a fourth posture changes this by construction rather than by a second,
    /// forgettable edit.</summary>
    public static readonly int Count = PostureCount * PerPosture;

    public static IReadOnlyList<AptitudeRow> All { get; } = new[]
    {
        new AptitudeRow("Might",       Posture.Force,   0, "universal offence — power",                       "Hit harder."),
        new AptitudeRow("Fortitude",   Posture.Force,   1, "mitigation — defense · absorption · reduction",   "Take less of everything."),
        new AptitudeRow("Vigor",       Posture.Force,   2, "shield — shield.capacity/regen/toughness",        "More to lose before you lose."),
        new AptitudeRow("Onslaught",   Posture.Force,   3, "breaks guard + reflect",                          "Their guard stops mattering."),
        new AptitudeRow("Agility",     Posture.Finesse, 4, "dodge",                                           "Be somewhere else."),
        new AptitudeRow("Composure",   Posture.Finesse, 5, "crit-denial",                                     "Nothing lands clean on you."),
        new AptitudeRow("Pierce",      Posture.Finesse, 6, "breaks mitigation + shield",                      "Armour stops mattering."),
        new AptitudeRow("Focus",       Posture.Finesse, 7, "utility — qi, efficiency, cooldowns",             "Do it again, sooner, cheaper."),
        new AptitudeRow("Bulwark",     Posture.Bastion, 8, "guard — parry/block rate and strength",           "Stop it outright, sometimes."),
        new AptitudeRow("Retribution", Posture.Bastion, 9, "reflect",                                         "Hitting you costs them."),
        new AptitudeRow("Precision",   Posture.Bastion, 10, "breaks dodge — accuracy",                         "They cannot dodge."),
        new AptitudeRow("Ferocity",    Posture.Bastion, 11, "breaks crit-denial — crit.rate/damage",           "Sometimes it is much worse."),
    };

    static readonly Dictionary<string, AptitudeRow> ById = All.ToDictionary(a => a.Id, StringComparer.Ordinal);

    public static bool IsAptitudeId(string id) => ById.ContainsKey(id);

    public static bool TryGet(string id, out AptitudeRow row) => ById.TryGetValue(id, out row!);

    public static AptitudeRow Get(string id) =>
        ById.TryGetValue(id, out var row) ? row : throw new KeyNotFoundException($"unknown aptitude id '{id}'");

    public static IEnumerable<AptitudeRow> InPosture(Posture posture) => All.Where(a => a.Posture == posture);

    /// <summary>V2's collision rule, callable from a test as well as a guard: an aptitude id may not
    /// equal or prefix a registered derived channel id, and no registered channel id may equal or
    /// prefix an aptitude id — ids share no namespace today (Proper case vs dotted lower-case), but
    /// this is checked rather than assumed (class-system-todo.md V2).</summary>
    public static IReadOnlyList<string> ChannelCollisions(DerivedStatRegistry registry)
    {
        var channelIds = registry.AllRegistered.Select(d => d.ChannelId).ToList();
        var families = DerivedStatChannels.CombatChannelFamilies;
        var collisions = new List<string>();
        foreach (var apt in All)
        {
            foreach (var channelId in channelIds)
                if (string.Equals(apt.Id, channelId, StringComparison.OrdinalIgnoreCase))
                    collisions.Add($"{apt.Id} == channel {channelId}");
            foreach (var family in families)
                if (string.Equals(apt.Id, family, StringComparison.OrdinalIgnoreCase))
                    collisions.Add($"{apt.Id} == family {family}");
        }
        return collisions;
    }
}
