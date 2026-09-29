using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Attrition;

/// <summary>
/// `delve-attrition` D2.18 (spec-delve-attrition.md §3) — the supply meter, charged once per room
/// entry. Pure, `long` throughout: <c>cost = maxHunger × bands.hazardBand.{hazardBand}.hungerPerMille
/// × rung.hungerMilli / 1_000_000</c> — two ‰ factors multiplied before a single division by 10⁶,
/// never divided per-factor (docs/architecture/numeric-types.md's own "divide by 1000 last, exactly once" rule, doubled here
/// since two per-mille terms compose into one per-million divisor).
///
/// <para><b>`hazardBand` is a plain string, not a "room archetype" type.</b> No archetype type exists
/// anywhere in `src/` carrying a hazard-band property — `delve-graph-roll` (D1.x) produces an
/// `archetypeId` string, never a resolved hazard band object — so resolving "which archetype maps to
/// which hazard band" is left to the caller, the same "read model owned elsewhere" shape this whole
/// module already follows for `EncounterAnchor`/`EncounterDomain`. `rest` and `boss` archetypes name
/// `hazardBand: "none"` (0‰, real committed data) — no special case needed here, the formula already
/// zeroes out on its own.</para>
///
/// <para><b>Persistence across delves is D2.23's own store write, not this method's.</b> This method
/// is pure and stateless — no clock, no store, no I/O (§10) — so "hunger persists across delves" (S2-7)
/// is a property this method's own callers must uphold (by carrying the ending value forward as the
/// next delve's starting `maxHunger`-relative pool), never something charged twice or reset here.</para>
/// </summary>
public static class HungerCharge
{
    public static long ForRoom(string hazardBand, long maxHunger, IReadOnlyDictionary<string, long> hazardBandHungerPerMille, DifficultyRungTuning rung)
    {
        if (hazardBand is null) throw new ArgumentNullException(nameof(hazardBand));
        if (hazardBandHungerPerMille is null) throw new ArgumentNullException(nameof(hazardBandHungerPerMille));
        if (rung is null) throw new ArgumentNullException(nameof(rung));
        if (!hazardBandHungerPerMille.TryGetValue(hazardBand, out var hungerPerMille))
            throw new ArgumentException($"'{hazardBand}' is not a known hazard band.", nameof(hazardBand));

        checked
        {
            return maxHunger * hungerPerMille * rung.HungerMultMilli / 1_000_000;
        }
    }
}
