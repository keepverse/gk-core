using System.Text.Json;
using FusionRpg.Core.Effects.Atoms.Power;

namespace FusionRpg.Core.Items.Uniques;

public sealed class UniqueTuningRejection : Exception
{
    public UniqueTuningRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser over <c>gk-core/data/tuning/uniques.v1.json</c> — no file I/O (tunables-ssot.md §7.2: "Core
/// never reads a file. Hosts load and inject"), matching <see cref="Sockets.SocketTuning"/>,
/// <see cref="Mutation.EnhancementTuning"/> and <see cref="Materials.MaterialTuning"/>.
///
/// <para><b>No key has a default.</b> A missing key throws at load rather than resolving to a
/// silently-invented budget premium or parity band.</para>
///
/// <para><b>Two structural invariants are checked at parse time.</b> (1) The drift tolerance is pinned
/// to <see cref="ContentValidation.DriftTolerancePercent"/> in both directions — definitions §7 owns
/// that number and this file reuses it, so a copy that drifts fails at boot rather than silently
/// enforcing a different budget than every other content check. (2) The parity band must be a real
/// band inside 0…1000‰ with the lower bound below the upper: an inverted or degenerate band would make
/// every reading simultaneously "too strong" and "a trophy", which reads as a metric working.</para>
/// </summary>
public sealed class UniqueTuning
{
    UniqueTuning(
        int rungFloorOrdinal, int maxIdentityAtoms, int identitySpreadPerMille,
        long budgetPremiumAeHundredths, int budgetDriftTolerancePercent, int narrowCeilingPerMille,
        int maxRolesPerFrame, IReadOnlyList<string> forbiddenRoles,
        int parityLowerBoundPerMille, int parityUpperBoundPerMille,
        int outOfBandMagnitudeCapPerMille)
    {
        RungFloorOrdinal = rungFloorOrdinal;
        MaxIdentityAtoms = maxIdentityAtoms;
        IdentitySpreadPerMille = identitySpreadPerMille;
        BudgetPremiumAeHundredths = budgetPremiumAeHundredths;
        BudgetDriftTolerancePercent = budgetDriftTolerancePercent;
        NarrowCeilingPerMille = narrowCeilingPerMille;
        MaxRolesPerFrame = maxRolesPerFrame;
        ForbiddenRoles = forbiddenRoles;
        ParityLowerBoundPerMille = parityLowerBoundPerMille;
        ParityUpperBoundPerMille = parityUpperBoundPerMille;
        OutOfBandMagnitudeCapPerMille = outOfBandMagnitudeCapPerMille;
    }

    /// <summary>ssot-uniques.md §4.1: the lowest rung ordinal a unique may carry. Also the whole
    /// content of the <c>unique_eligible</c> budget key — see <see cref="IsRungEligible"/>.</summary>
    public int RungFloorOrdinal { get; }

    public int MaxIdentityAtoms { get; }
    public int IdentitySpreadPerMille { get; }
    public long BudgetPremiumAeHundredths { get; }
    public int BudgetDriftTolerancePercent { get; }
    public int NarrowCeilingPerMille { get; }
    public int MaxRolesPerFrame { get; }
    public IReadOnlyList<string> ForbiddenRoles { get; }
    public int ParityLowerBoundPerMille { get; }
    public int ParityUpperBoundPerMille { get; }
    public int OutOfBandMagnitudeCapPerMille { get; }

    /// <summary>
    /// The <c>unique_eligible</c> budget key, derived rather than authored: 1 at or above the floor,
    /// 0 below it. ssot-uniques.md §5.3 wanted a per-rung 0/1 table; a table would be a second source
    /// of truth for a fact one ordinal comparison already decides, and it is exactly the kind of row
    /// SC7 calls a lie in a table when the two disagree.
    /// </summary>
    public bool IsRungEligible(int ordinal) => ordinal >= RungFloorOrdinal;

    public static UniqueTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new UniqueTuningRejection("uniques tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new UniqueTuningRejection($"uniques tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new UniqueTuningRejection("uniques tuning: root must be an object");

            var rungFloor = Int(root, "rungFloorOrdinal");
            var maxIdentity = Int(root, "maxIdentityAtoms");
            var spread = Int(root, "identitySpreadPerMille");
            var premium = Int(root, "budgetPremiumAeHundredths");
            var drift = Int(root, "budgetDriftTolerancePercent");
            var narrow = Int(root, "narrowCeilingPerMille");
            var maxRoles = Int(root, "maxRolesPerFrame");
            var parityLo = Int(root, "parityLowerBoundPerMille");
            var parityHi = Int(root, "parityUpperBoundPerMille");
            var outOfBand = Int(root, "outOfBandMagnitudeCapPerMille");

            if (!root.TryGetProperty("forbiddenRoles", out var forbiddenEl) ||
                forbiddenEl.ValueKind != JsonValueKind.Array)
                throw new UniqueTuningRejection("uniques tuning: no 'forbiddenRoles' array");

            var forbidden = new List<string>();
            foreach (var el in forbiddenEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.String)
                    throw new UniqueTuningRejection("uniques tuning: forbiddenRoles must be role id strings");
                var id = el.GetString()!;
                if (!ItemRoles.TryParse(id, out _))
                    throw new UniqueTuningRejection(
                        $"uniques tuning: forbiddenRoles names '{id}', which is not a role in the core registry — " +
                        "a ban on a role that does not exist bans nothing and reads as protection");
                forbidden.Add(id);
            }

            // definitions §7 owns the drift number; this file REUSES it. A copy that drifts would
            // enforce a different budget tolerance than every other content check in the tree, with no
            // symptom -- the same device module 9 used for powerDisplayBandPercent.
            if (drift != ContentValidation.DriftTolerancePercent)
                throw new UniqueTuningRejection(
                    $"uniques tuning: budgetDriftTolerancePercent is {drift} but definitions §7's shared " +
                    $"tolerance (ContentValidation.DriftTolerancePercent) is " +
                    $"{ContentValidation.DriftTolerancePercent}; this file reuses that number, it does not " +
                    "own a second copy of it");

            if (rungFloor < 0)
                throw new UniqueTuningRejection($"uniques tuning: rungFloorOrdinal {rungFloor} is negative");
            if (maxIdentity < 1)
                throw new UniqueTuningRejection(
                    $"uniques tuning: maxIdentityAtoms {maxIdentity} would allow a unique with no identity " +
                    "at all, which is a rare with a name");
            if (spread is < 0 or > 1000)
                throw new UniqueTuningRejection($"uniques tuning: identitySpreadPerMille {spread} is outside 0..1000");
            if (premium < 0)
                throw new UniqueTuningRejection($"uniques tuning: budgetPremiumAeHundredths {premium} is negative");
            if (narrow is < 0 or > 1000)
                throw new UniqueTuningRejection($"uniques tuning: narrowCeilingPerMille {narrow} is outside 0..1000");
            if (maxRoles < 1)
                throw new UniqueTuningRejection($"uniques tuning: maxRolesPerFrame {maxRoles} allows no uniques at all");
            if (outOfBand < 1000)
                throw new UniqueTuningRejection(
                    $"uniques tuning: outOfBandMagnitudeCapPerMille {outOfBand} is below 1000‰, which would " +
                    "make an identity atom weaker than the band it is allowed to leave");

            if (parityLo is < 0 or > 1000 || parityHi is < 0 or > 1000)
                throw new UniqueTuningRejection(
                    $"uniques tuning: parity band [{parityLo}, {parityHi}]‰ leaves 0..1000");
            if (parityLo >= parityHi)
                throw new UniqueTuningRejection(
                    $"uniques tuning: parity band [{parityLo}, {parityHi}]‰ is inverted or degenerate — every " +
                    "reading would be both 'strictly better' and 'a trophy' at once, which reads as a metric " +
                    "working");

            return new UniqueTuning(
                rungFloor, maxIdentity, spread, premium, drift, narrow, maxRoles, forbidden,
                parityLo, parityHi, outOfBand);
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new UniqueTuningRejection($"uniques tuning: missing or non-numeric '{key}'");
        return el.GetInt32();
    }
}
