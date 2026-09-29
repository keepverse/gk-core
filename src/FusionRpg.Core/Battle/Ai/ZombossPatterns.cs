namespace FusionRpg.Core.Battle.Ai;

/// <summary>
/// class-system-todo.md P7.5 — the nine named Zomboss builds (spec-zomboss-patterns.md, read in full
/// this session). Copies <see cref="World.Ai.FactionPolicies"/>'s own shape and its own stated
/// rationale exactly (§4): "Throws rather than returning null. A null would read as 'this Zomboss has
/// no build', which is indistinguishable from the human — and a typo would then look like a design
/// decision for the rest of the campaign." Code is the load source, matching
/// <c>AptitudeCatalog</c>/<c>roster.json</c>'s own precedent — `gk-data/packs/fusion/data/seed/zomboss/patterns.json` is a
/// checked-in MIRROR for tooling that cannot reference this assembly, not the source of truth
/// (tunables-ssot.md §7.2: Core reads no file).
///
/// <para><b>Nine = 3 pure + 6 mixed</b> (§3). The 3 pure share compositions are ported verbatim from
/// `gk-core/tools/CombatSim/builds/{force,finesse,bastion}.json` — this program's own already-measured
/// archetype shares (read-only; `gk-core/tools/CombatSim` is under separate, concurrent development this
/// session, so nothing there was edited), converted from the tool's own double shares to permille
/// `long`s (docs/architecture/numeric-types.md: never a `double` on a magnitude-adjacent path). The 6 mixed patterns are three
/// valid (defence-posture, breaks-posture) pairs from §3's own table — the ONLY three that are not
/// self-cancelling, verified against the roster's own counter-cycle (`gk-data/packs/fusion/data/seed/aptitudes/roster.json`'s
/// "role" column: Onslaught breaks Bulwark+Retribution, so FORCE counters BASTION; Pierce breaks
/// Fortitude+Vigor, so FINESSE counters FORCE; Precision+Ferocity break Agility+Composure, so BASTION
/// counters FINESSE) — each pair gets a guard-leaning (60/40) and an aggro-leaning (40/60) variant,
/// six total, symmetric across all three pairs.</para>
/// </summary>
public static class ZombossPatterns
{
    static readonly IReadOnlyDictionary<string, ZombossPattern> ById =
        new Dictionary<string, ZombossPattern>(StringComparer.Ordinal)
        {
            // ── 3 pure — this posture's own kit, ported from gk-core/tools/CombatSim's own measured archetypes ──
            // AuraId (T17): each pattern's own highest-SharePermille aptitude, ties broken
            // alphabetically -- derived from the weights already authored above, not a second pick.
            ["force-pure"] = new("force-pure", new Dictionary<string, long>
            {
                ["Might"] = 396, ["Vigor"] = 150, ["Onslaught"] = 153, ["Retribution"] = 300,
            }, AuraId: "Might"),
            ["finesse-pure"] = new("finesse-pure", new Dictionary<string, long>
            {
                ["Agility"] = 429, ["Composure"] = 391, ["Pierce"] = 150, ["Focus"] = 30,
            }, AuraId: "Agility"),
            ["bastion-pure"] = new("bastion-pure", new Dictionary<string, long>
            {
                ["Bulwark"] = 180, ["Fortitude"] = 170, ["Precision"] = 248, ["Ferocity"] = 402,
            }, AuraId: "Ferocity"),

            // ── 6 mixed — the three NON-self-cancelling (defence, breaks) pairs, two variants each ──
            // "armoured counter-puncher": FORCE-defence + BASTION-breaks (Bastion's breaks counter
            // FINESSE, not FORCE, so this is legal — spec-zomboss-patterns.md §3's own first row).
            ["force-defence-bastion-breaks-guard"] = new("force-defence-bastion-breaks-guard", new Dictionary<string, long>
            {
                ["Fortitude"] = 300, ["Vigor"] = 300, ["Precision"] = 200, ["Ferocity"] = 200,
            }, AuraId: "Fortitude"), // tie with Vigor at 300, alphabetical
            ["force-defence-bastion-breaks-aggro"] = new("force-defence-bastion-breaks-aggro", new Dictionary<string, long>
            {
                ["Fortitude"] = 200, ["Vigor"] = 200, ["Precision"] = 300, ["Ferocity"] = 300,
            }, AuraId: "Ferocity"), // tie with Precision at 300, alphabetical
            // "evasive guard-breaker": FINESSE-defence + FORCE-breaks (Force's breaks counter BASTION,
            // not FINESSE — §3's second row).
            ["finesse-defence-force-breaks-guard"] = new("finesse-defence-force-breaks-guard", new Dictionary<string, long>
            {
                ["Agility"] = 300, ["Composure"] = 300, ["Onslaught"] = 400,
            }, AuraId: "Onslaught"),
            ["finesse-defence-force-breaks-aggro"] = new("finesse-defence-force-breaks-aggro", new Dictionary<string, long>
            {
                ["Agility"] = 200, ["Composure"] = 200, ["Onslaught"] = 600,
            }, AuraId: "Onslaught"),
            // "parrying armour-piercer": BASTION-defence + FINESSE-breaks (Finesse's breaks counter
            // FORCE, not BASTION — §3's third row).
            ["bastion-defence-finesse-breaks-guard"] = new("bastion-defence-finesse-breaks-guard", new Dictionary<string, long>
            {
                ["Bulwark"] = 300, ["Retribution"] = 300, ["Pierce"] = 400,
            }, AuraId: "Pierce"),
            ["bastion-defence-finesse-breaks-aggro"] = new("bastion-defence-finesse-breaks-aggro", new Dictionary<string, long>
            {
                ["Bulwark"] = 200, ["Retribution"] = 200, ["Pierce"] = 600,
            }, AuraId: "Pierce"),
        };

    /// <summary>In ordinal id order, so anything that enumerates patterns is reproducible — copied
    /// from <see cref="World.Ai.FactionPolicies"/>, comment included: reproducible enumeration is what
    /// keeps a seeded encounter generator deterministic, and it is the kind of property that is free
    /// to keep and expensive to add back.</summary>
    public static IReadOnlyList<string> All { get; } =
        ById.Keys.OrderBy(id => id, StringComparer.Ordinal).ToList();

    public static bool IsKnown(string? patternId) => patternId != null && ById.ContainsKey(patternId);

    /// <summary>Throws rather than returning null — see this type's own doc comment for why.</summary>
    public static ZombossPattern Resolve(string patternId) =>
        ById.TryGetValue(patternId, out var pattern)
            ? pattern
            : throw new KeyNotFoundException($"No Zomboss pattern '{patternId}'.");
}
