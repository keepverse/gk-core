namespace FusionRpg.Core.Creatures;

/// <summary>
/// The per-gate rank floors (spec-species-rank.md §6) — every gate's threshold is the BOTTOM rung
/// until a floor is explicitly tuned above it, so landing rank as a gate moves zero goldens and
/// changes zero behavior (Assumption 3). The values live in
/// <c>gk-core/data/tuning/creature-rank.v1.json</c>'s <c>floors</c> block (a balance number belongs in
/// tuning, never in code); this type is only the read side, configured once per process the same way
/// every other tuning-backed policy in Core is.
///
/// <para><b>Null maps to the bottom rung HERE, at each gate.</b> A skipped rank is a legal corpus
/// state (<c>ConcreteSpecies.Rank == null</c>), and substituting it per-call-site would be five
/// chances to get the mapping wrong — so every gate asks
/// <see cref="Passes"/> and the mapping happens once. That line is also what makes the shipped
/// default a pass-through: at bottom, <c>AtLeast(bottom, bottom)</c> is true for every rung.</para>
///
/// <para><b>This is a floor, not a cap.</b> It can only ever be raised to admit less; nothing here
/// bounds a magnitude or a progression curve (docs/architecture/numeric-types.md's "no hard progression ceilings" is about
/// values a player grows, not about which species a gate offers).</para>
/// </summary>
public static class CreatureRankFloors
{
    /// <summary>The five gate ids, spelled exactly as <c>creature-rank.v1.json</c>'s own
    /// <c>floors</c> keys and as <see cref="CreatureRankTuning.GateIds"/> declares them. Kept as the
    /// only named constants so a gate site never inlines the string a second time;
    /// <see cref="Configure"/> refuses a tuning file that does not answer every one of them.</summary>
    public const string FusionPromotion = "fusionPromotion";
    public const string FusionRecipeEligibility = "fusionRecipeEligibility";
    public const string ExpeditionWildBand = "expeditionWildBand";
    public const string WaveBand = "waveBand";
    public const string CageEligibility = "cageEligibility";

    /// <summary>Every gate this policy names — the closed set <see cref="Configure"/> validates
    /// against the file, so a renamed or dropped tuning key fails loudly at boot instead of silently
    /// becoming a bottom-rung pass-through.</summary>
    public static IReadOnlyList<string> DeclaredGates { get; } = new[]
    {
        FusionPromotion, FusionRecipeEligibility, ExpeditionWildBand, WaveBand, CageEligibility,
    };

    static IReadOnlyDictionary<string, CreatureRank>? _configured;

    /// <summary>True once a host has read the tuning file — mirrors
    /// <c>CreatureSpeciesCatalog.IsConfigured</c>'s role for tests and boot assertions.</summary>
    public static bool IsConfigured => _configured is not null;

    /// <summary>Take the floors from the parsed rank tuning. Refuses (does not default) if the file
    /// does not answer every <see cref="DeclaredGates"/> id.</summary>
    public static void Configure(CreatureRankTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var floors = new Dictionary<string, CreatureRank>(StringComparer.Ordinal);
        foreach (var gate in DeclaredGates)
        {
            var floor = tuning.FloorFor(gate); // throws naming the file's own gate vocabulary
            if (!CreatureRankIds.TryParse(floor, out var parsed))
                throw new InvalidOperationException(
                    $"creature-rank floors: gate '{gate}' names rank '{floor}', which is not a CreatureRank");
            floors[gate] = parsed;
        }
        _configured = floors;
    }

    /// <summary>Back to bottom-everywhere — test teardown only, mirroring
    /// <c>CreatureSpeciesCatalog.ResetToUnconfigured</c>.</summary>
    public static void ResetToUnconfigured() => _configured = null;

    /// <summary>The floor for a gate. The BOTTOM rung both when the file says so and when nothing was
    /// configured — the two cases are the same behavior by construction, which is what makes the
    /// shipped default a pass-through rather than an "optional seam".</summary>
    public static CreatureRank FloorFor(string gateId) =>
        _configured is not null && _configured.TryGetValue(gateId, out var floor)
            ? floor
            : CreatureRankLadder.All[0];

    /// <summary>A gate's own test: does this species clear the gate's floor? A null (skipped) rank is
    /// mapped to the bottom rung here — never passed to the ladder as a null, and never fabricated as
    /// a higher rung (spec §6's explicit null→bottom line).</summary>
    public static bool Passes(string gateId, CreatureRank? rank) =>
        CreatureRankLadder.AtLeast(rank ?? CreatureRankLadder.All[0], FloorFor(gateId));
}
