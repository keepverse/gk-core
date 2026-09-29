using System.Text.Json;

namespace FusionRpg.Core.Items.Mutation;

/// <summary>
/// One of spec-enhance-reroll.md §4's three risk bands. <c>ToLevel</c> is <c>null</c> on the
/// open-ended top band — a closed top band would be a hard stop on <c>+X</c>, which AGENTS.md
/// forbids and D7 rules out by name.
/// </summary>
public readonly record struct EnhanceBand(
    string Id, int FromLevel, int? ToLevel, int SuccessStartMilli, int SuccessEndMilli,
    int SpanLevels, bool CanDowngrade);

public sealed class EnhancementTuningRejection : Exception
{
    public EnhancementTuningRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser over <c>gk-core/data/tuning/enhancement.v1.json</c> — no file I/O (tunables-ssot.md §7.2:
/// "Core never reads a file. Hosts load and inject"), the same shape
/// <see cref="Materials.MaterialTuning"/> and <see cref="ItemRarityTuning"/> already use.
///
/// <para><b>No key has a default.</b> A missing one throws at load rather than resolving to a
/// silently-invented odds or price number.</para>
///
/// <para><b>THE soft cap lives in this file.</b> The gain asymptote, the falling success curve and
/// the reroll price are all configurable here and none of them ever refuses a level. The one legal
/// ceiling in the module is <see cref="MutationLimits.MutationSeqCap"/>, which is structural and
/// says so.</para>
/// </summary>
public sealed class EnhancementTuning
{
    EnhancementTuning(
        int scalarPerLevelMilli, int asymptoteK, int milestoneStride,
        int ilvlCapFloor, int ilvlCapDivisor,
        IReadOnlyList<EnhanceBand> bands, int downgradeFromLevel,
        int craftPityThreshold, int transferRatioMilli, int transferItemLevelWindow,
        int rerollCostRungSlopeMilli, int rerollCostAffixBaseMilli, int rerollCostAffixStepMilli,
        IReadOnlyList<int> milestoneTierLadder)
    {
        ScalarPerLevelMilli = scalarPerLevelMilli;
        AsymptoteK = asymptoteK;
        MilestoneStride = milestoneStride;
        IlvlCapFloor = ilvlCapFloor;
        IlvlCapDivisor = ilvlCapDivisor;
        Bands = bands;
        DowngradeFromLevel = downgradeFromLevel;
        CraftPityThreshold = craftPityThreshold;
        TransferRatioMilli = transferRatioMilli;
        TransferItemLevelWindow = transferItemLevelWindow;
        RerollCostRungSlopeMilli = rerollCostRungSlopeMilli;
        RerollCostAffixBaseMilli = rerollCostAffixBaseMilli;
        RerollCostAffixStepMilli = rerollCostAffixStepMilli;
        MilestoneTierLadder = milestoneTierLadder;
    }

    /// <summary>I6 §3.3's linear track — the NAIVE curve, kept because §4b's horizon table is
    /// computed against it. The shipped gain is <see cref="EnhancePolicy.GainMicro"/>.</summary>
    public int ScalarPerLevelMilli { get; }

    /// <summary>§4a's <c>K</c>. Module 7 owns <c>enhance_cap</c>; this module owns <c>K</c>.</summary>
    public int AsymptoteK { get; }

    /// <summary>A milestone every N levels, forever — never a five-entry list, which is a hard stop
    /// at +20 wearing content's clothes.</summary>
    public int MilestoneStride { get; }

    public int IlvlCapFloor { get; }
    public int IlvlCapDivisor { get; }
    public IReadOnlyList<EnhanceBand> Bands { get; }
    public int DowngradeFromLevel { get; }
    public int CraftPityThreshold { get; }
    public int TransferRatioMilli { get; }
    public int TransferItemLevelWindow { get; }
    public int RerollCostRungSlopeMilli { get; }
    public int RerollCostAffixBaseMilli { get; }
    public int RerollCostAffixStepMilli { get; }

    /// <summary>species-gear-chain T13 (OQ2): which atom tier a milestone ORDINAL grants — the 1st
    /// milestone reads element 0, the 2nd element 1, and ordinals past the array read the last
    /// element (the atom system's own tier max bounds it structurally; a plateau is a chosen last
    /// value, not a clamp). Starting shallow ([1,2,2,3,4]) so a +4 and a +20 milestone are never
    /// the same row.</summary>
    public IReadOnlyList<int> MilestoneTierLadder { get; }

    public static EnhancementTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new EnhancementTuningRejection("enhancement tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new EnhancementTuningRejection($"enhancement tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new EnhancementTuningRejection("enhancement tuning: root is not an object");

            var bands = ReadBands(root);
            var tuning = new EnhancementTuning(
                Positive(root, "scalarPerLevelMilli"),
                Positive(root, "asymptoteK"),
                Positive(root, "milestoneStride"),
                Positive(root, "ilvlCapFloor"),
                Positive(root, "ilvlCapDivisor"),
                bands,
                Positive(root, "downgradeFromLevel"),
                Positive(root, "craftPityThreshold"),
                Positive(root, "transferRatioMilli"),
                Positive(root, "transferItemLevelWindow"),
                NonNegative(root, "rerollCostRungSlopeMilli"),
                NonNegative(root, "rerollCostAffixBaseMilli"),
                Positive(root, "rerollCostAffixStepMilli"),
                ReadTierLadder(root));

            if (tuning.TransferRatioMilli >= 1000)
                throw new EnhancementTuningRejection(
                    $"enhancement tuning: transferRatioMilli={tuning.TransferRatioMilli} is lossless or better — " +
                    "I6 §7.4 is explicit that a lossless transfer turns +X into a portable currency and the decision disappears");

            // ssot-rarity.md §8.1's mechanism, checked at LOAD rather than discovered as a balance
            // regression: the reroll price must be more sensitive to affix count than to rung, or a
            // low rung stops being the cheap crafting base the whole low-rung-relevance argument
            // rests on ("cheap to own and expensive to use, and the mechanism inverts").
            var rungSpread = (long)RerollRungLegMilli(RarityLadder.RungIds.Count - 1, tuning) * 1000
                             / RerollRungLegMilli(0, tuning);
            var affixSpread = (long)AffixLegMilli(MaxShippedAffixCount, tuning) * 1000
                              / AffixLegMilli(1, tuning);
            if (affixSpread <= rungSpread)
                throw new EnhancementTuningRejection(
                    $"enhancement tuning: the reroll affix-count leg spreads x{affixSpread / 1000.0:0.00} across " +
                    $"1..{MaxShippedAffixCount} affixes but the rung leg spreads x{rungSpread / 1000.0:0.00} across the " +
                    "ten rungs — ssot-rarity.md §9.7 requires the price scale with affix count, NOT rung alone");

            return tuning;
        }
    }

    /// <summary>
    /// The largest <c>prefix_rolls + suffix_rolls</c> the shipped ladder authors (`almanac`, 3+2).
    /// Structural, not a balance number: it exists only so the §9.7 dominance check above has a
    /// concrete top of range to measure over. A ladder edit that raises it makes the check stricter,
    /// never looser.
    /// </summary>
    internal const int MaxShippedAffixCount = 5;

    internal static int RerollRungLegMilli(int rungIndex, EnhancementTuning t) =>
        checked(1000 + t.RerollCostRungSlopeMilli * rungIndex);

    internal static int AffixLegMilli(int affixCount, EnhancementTuning t) =>
        checked(t.RerollCostAffixBaseMilli + t.RerollCostAffixStepMilli * affixCount);

    /// <summary>species-gear-chain T13: the per-ordinal milestone tier ladder. Required, non-empty,
    /// every tier in [1..5] (the atom system's own tier range) — a missing ladder is a rejection,
    /// never an implied flat tier-1.</summary>
    static IReadOnlyList<int> ReadTierLadder(JsonElement root)
    {
        if (!root.TryGetProperty("milestoneTierLadder", out var el) || el.ValueKind != JsonValueKind.Array)
            throw new EnhancementTuningRejection("enhancement tuning: missing or non-array 'milestoneTierLadder'");
        var ladder = new List<int>();
        foreach (var v in el.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Number)
                throw new EnhancementTuningRejection("enhancement tuning: 'milestoneTierLadder' carries a non-numeric tier");
            var tier = v.GetInt32();
            if (tier < 1 || tier > 5)
                throw new EnhancementTuningRejection($"enhancement tuning: 'milestoneTierLadder' tier {tier} is outside the atom tier range [1..5]");
            ladder.Add(tier);
        }
        if (ladder.Count == 0)
            throw new EnhancementTuningRejection("enhancement tuning: 'milestoneTierLadder' is empty — ordinals past the end read the last element, so an empty ladder grants nothing, ever");
        return ladder;
    }

    static IReadOnlyList<EnhanceBand> ReadBands(JsonElement root)
    {
        if (!root.TryGetProperty("bands", out var bandsEl) || bandsEl.ValueKind != JsonValueKind.Array)
            throw new EnhancementTuningRejection("enhancement tuning: missing or non-array 'bands'");

        var bands = new List<EnhanceBand>();
        foreach (var el in bandsEl.EnumerateArray())
        {
            var id = Str(el, "id");
            var from = Positive(el, "fromLevel");
            int? to = el.TryGetProperty("toLevel", out var toEl) && toEl.ValueKind == JsonValueKind.Number
                ? toEl.GetInt32()
                : null;
            var start = Positive(el, "successStartMilli");
            var end = NonNegative(el, "successEndMilli");

            var span = to is { } closed ? closed - from : Positive(el, "spanLevels");

            if (span < 0)
                throw new EnhancementTuningRejection($"enhancement tuning: band '{id}' ends before it starts");
            if (end <= 0)
                throw new EnhancementTuningRejection(
                    $"enhancement tuning: band '{id}' bottoms out at {end} per-mille — a zero success chance is the " +
                    "luck wall D7 forbids by name. successEndMilli is a SOFT floor and must stay above zero");
            if (start > 1000 || end > 1000)
                throw new EnhancementTuningRejection($"enhancement tuning: band '{id}' success exceeds 1000 per-mille");
            if (end > start)
                throw new EnhancementTuningRejection($"enhancement tuning: band '{id}' success rises with level");

            var canDowngrade = el.TryGetProperty("canDowngrade", out var d) && d.ValueKind == JsonValueKind.True;
            bands.Add(new EnhanceBand(id, from, to, start, end, span, canDowngrade));
        }

        if (bands.Count == 0)
            throw new EnhancementTuningRejection("enhancement tuning: 'bands' is empty");
        if (bands[0].FromLevel != 1)
            throw new EnhancementTuningRejection("enhancement tuning: the first band must start at +1");
        if (bands[^1].ToLevel is not null)
            throw new EnhancementTuningRejection(
                "enhancement tuning: the top band is closed — a closed top band is a hard stop on +X, " +
                "which AGENTS.md forbids and spec-enhance-reroll.md §4 rules out by name");

        for (var i = 1; i < bands.Count; i++)
        {
            if (bands[i - 1].ToLevel is not { } prevTo || bands[i].FromLevel != prevTo + 1)
                throw new EnhancementTuningRejection(
                    $"enhancement tuning: band '{bands[i].Id}' does not start one level above '{bands[i - 1].Id}' — " +
                    "a gap between bands is a level with no defined odds");
        }

        return bands;
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new EnhancementTuningRejection($"enhancement tuning: missing or non-string '{key}'");
        return el.GetString()!;
    }

    static int Positive(JsonElement parent, string key)
    {
        var v = NonNegative(parent, key);
        if (v <= 0) throw new EnhancementTuningRejection($"enhancement tuning: '{key}' must be positive — got {v}");
        return v;
    }

    static int NonNegative(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new EnhancementTuningRejection($"enhancement tuning: missing or non-integer '{key}'");
        if (v < 0) throw new EnhancementTuningRejection($"enhancement tuning: '{key}' must not be negative — got {v}");
        return v;
    }
}
