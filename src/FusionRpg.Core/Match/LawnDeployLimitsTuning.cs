using System.Text.Json;

namespace FusionRpg.Core.Match;

/// <summary>
/// The lawn's unique-deploy concurrency limits (`gk-core/data/tuning/lawn-deploy.v1.json`) — owner ruling D6:
/// at most 5 unique creatures per side, 10 on the board.
///
/// <para><b>Structural, and the file says so.</b> These are per-frame limits that protect the frame
/// budget, not progression ceilings: they cap no magnitude and no roster size, and their register row is
/// <c>docs/architecture/power/ssot-power-scale.md</c> §11.3. Raising either is a measured perf decision
/// (the 300-zombie A/B <c>lawn-scale-live-proof</c> owns), never a balance pass.</para>
/// </summary>
public sealed record LawnDeployLimits(int MaxConcurrentUniquesPerEmpire, int MaxConcurrentUniquesOnBoard);

/// <summary>The whole document. <see cref="SchemaVersion"/> is the shape version,
/// <see cref="Version"/> the published tuning revision.</summary>
public sealed record LawnDeployLimitsTuning(int SchemaVersion, int Version, LawnDeployLimits Limits);

public sealed class LawnDeployLimitsTuningRejection : Exception
{
    public LawnDeployLimitsTuningRejection(string message) : base(message) { }
}

/// <summary>Process-wide holder, matching <c>LawnDeployEventsTuningHub</c>'s plain-holder shape: the
/// gate policy stays a pure function taking <see cref="LawnDeployLimits"/> as a parameter, and this hub
/// is only where a caller reads the loaded value from.</summary>
public static class LawnDeployLimitsTuningHub
{
    static LawnDeployLimitsTuning? _tuning;

    public static void Configure(LawnDeployLimitsTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static LawnDeployLimitsTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "LawnDeployLimitsTuningHub.Configure(...) has not run. Read data/tuning/lawn-deploy.v1.json at " +
        "startup — there is no built-in default to fall back to.");

    public static bool IsConfigured => _tuning != null;

    /// <summary>Tests only.</summary>
    public static void Reset() => _tuning = null;
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2), mirroring
/// <c>LawnDeployEventsTuningLoader</c>'s explicit, path-qualified-failure shape.</summary>
public static class LawnDeployLimitsTuningLoader
{
    public static LawnDeployLimitsTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new LawnDeployLimitsTuningRejection("lawn-deploy tuning is empty");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("limits", out var limitsEl) || limitsEl.ValueKind != JsonValueKind.Object)
            throw new LawnDeployLimitsTuningRejection("lawn-deploy tuning is missing the 'limits' object");

        var limits = new LawnDeployLimits(
            MaxConcurrentUniquesPerEmpire: Int(limitsEl, "maxConcurrentUniquesPerEmpire"),
            MaxConcurrentUniquesOnBoard: Int(limitsEl, "maxConcurrentUniquesOnBoard"));

        // A limit of zero or less is not a limit -- the gate would treat it as "unlimited" and the file
        // would be lying about what it states. Refused at load, loudly, rather than at the gate.
        foreach (var (name, value) in new[]
                 {
                     ("maxConcurrentUniquesPerEmpire", limits.MaxConcurrentUniquesPerEmpire),
                     ("maxConcurrentUniquesOnBoard", limits.MaxConcurrentUniquesOnBoard),
                 })
        {
            if (value <= 0)
                throw new LawnDeployLimitsTuningRejection(
                    $"'limits.{name}' is {value}; a concurrency limit must be positive (0 is not 'no limit' "
                    + "here -- the file states the number it enforces)");
        }

        // The schema invariant the shipped file is asserted against: a board limit below the per-empire
        // one could never admit at the per-empire limit it states, so the file contradicts itself.
        if (limits.MaxConcurrentUniquesOnBoard < limits.MaxConcurrentUniquesPerEmpire)
            throw new LawnDeployLimitsTuningRejection(
                $"'limits.maxConcurrentUniquesOnBoard' ({limits.MaxConcurrentUniquesOnBoard}) sits below "
                + $"'limits.maxConcurrentUniquesPerEmpire' ({limits.MaxConcurrentUniquesPerEmpire}) — the "
                + "board limit would make the per-empire limit unreachable, so the file contradicts itself");

        return new LawnDeployLimitsTuning(
            SchemaVersion: Int(root, "schemaVersion"),
            Version: Int(root, "version"),
            Limits: limits);
    }

    static int Int(JsonElement node, string name)
    {
        if (!node.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new LawnDeployLimitsTuningRejection($"lawn-deploy tuning is missing integer '{name}'");
        return v;
    }
}
