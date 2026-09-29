using System.Text.Json;
using System.Text.Json.Serialization;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Tools.CombatSim;

/// <summary>How an aptitude point reaches a channel (ssot-power-scale.md §4.6, Rule PS-3).</summary>
public enum ReadMode
{
    /// <summary>Contest — linear in `Θ`. Value = k × points, so a one-step gap is worth the same at
    /// Θ=10 and Θ=10,000. What `accuracy`/`dodge`/`crit.rate` already declare (`SigmoidPoints`).</summary>
    Contest,
    /// <summary>Magnitude — reads `P(Θ)`. Value = k × share × P(Θ), so the number the player sees grows
    /// superlinearly. What `power`/`defense`/`shield.*` already declare (`GameUnits`).</summary>
    Magnitude
}

public sealed record AptitudeEdge(string Channel, string Source, double K, ReadMode Read);

/// <summary>
/// The aptitude → derived mapping, inverted the way class-system-ideal.md §7a.1 describes: each
/// channel names its source, coefficient and read mode. One row answers "what feeds this?".
/// </summary>
public sealed class AptitudeModel
{
    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }
    public List<AptitudeEdge> Edges { get; set; } = new();

    /// <summary>How many CONTEST POINTS one whole allocation is worth (`read.contest.spanPoints`).
    /// Distinct from `stats.accuracyScale`, which is how many contest points move a probability.
    /// Defaults to the value the hypothesis models were measured at.</summary>
    public double ContestSpan { get; set; } = 100.0;

    /// <summary>`gamma` in `value = k · share^gamma · scale`. <b>1.0 is linear in share</b> — two
    /// aptitudes at 50% equal one at 100%. Above 1.0 concentration pays superlinearly (specialising
    /// is worth more than the points spent); below 1.0 spreading pays. This is the single dial that
    /// controls how much specialising is worth, and it is smooth and differentiable, so it stays
    /// inside the closed form.</summary>
    public double ContestShareExponent { get; set; } = 1.0;

    /// <inheritdoc cref="ContestShareExponent"/>
    public double MagnitudeShareExponent { get; set; } = 1.0;

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static AptitudeModel Load(string nameOrPath)
    {
        var path = Resolve(nameOrPath, "models");
        var m = JsonSerializer.Deserialize<AptitudeModel>(File.ReadAllText(path), Options)
                ?? throw new InvalidOperationException($"{path}: empty model");
        var registry = DerivedStatRegistry.CreateDefault();
        var bad = m.Edges.Select(e => e.Channel).Where(c => !registry.TryResolveChannel(c, out _)).ToList();
        if (bad.Count > 0)
            throw new InvalidOperationException($"{path}: unregistered channel(s): {string.Join(", ", bad)}");
        return m;
    }

    internal static string Resolve(string nameOrPath, string dirName)
    {
        if (File.Exists(nameOrPath)) return nameOrPath;
        var file = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? nameOrPath : nameOrPath + ".json";
        foreach (var dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, dirName),
                     Path.Combine(TuningBootstrap.RepoRoot, "tools", "CombatSim", dirName)
                 })
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"'{nameOrPath}' not found under {dirName}/");
    }

    /// <summary>
    /// Turn an aptitude point allocation into channel values at a given `Θ`.
    ///
    /// <para><b>Contest</b> edges read the ABSOLUTE point count — points accrue linearly with `Θ`
    /// (§7a.2: 3 per level), so the value grows ∝ `Θ`, which is what PS-3 requires of a contest.</para>
    ///
    /// <para><b>Magnitude</b> edges read the point SHARE (this aptitude ÷ all points spent) times
    /// `P(Θ)`. The share is what the player chose; the scale comes from the ladder. That is what makes
    /// a magnitude grow ∝ `P(Θ)` rather than ∝ `Θ`, and it is the whole reason the two read modes
    /// cannot be collapsed into one.</para>
    /// </summary>
    public Dictionary<string, double> Resolve(IReadOnlyDictionary<string, double> points, int theta, PowerLadder ladder)
    {
        var total = points.Values.Sum();
        if (total <= 0) total = 1;
        var pTheta = (double)ladder.Value(theta);
        var pPin = (double)ladder.Value(20);

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var e in Edges)
        {
            if (!points.TryGetValue(e.Source, out var pts) || pts <= 0) continue;
            // CONTEST reads the point SHARE, not the absolute count.
            //
            // Absolute points grow ∝ Θ (3 per level), so the DIFFERENCE between two builds grows ∝ Θ
            // too — and `sigmoid(delta/100)` then saturates to 0 or 1, making every contest
            // deterministic at high Θ. Measured: the cycle held only in a narrow band around Θ=100
            // and collapsed to 0/100 by Θ=300.
            //
            // ssot-power-scale.md §2 is explicit about the property this must preserve: the shipped
            // baselines (BaseAccuracy = 220 + 26L, BaseDodge = 26L) are built so **level cancels at
            // parity** — a level-20 duel and a level-2000 duel have the same hit rate. The Θ term is
            // a shared baseline that cancels between two actors at the same depth; what differentiates
            // them is allocation, and that gap must stay bounded. Reading share does exactly that.
            //
            // MAGNITUDE reads P(Θ) itself, so the numbers the player sees grow superlinearly (PS-3).
            //
            // The share exponent (gamma) is what makes the DISTRIBUTION tunable rather than fixed:
            // at 1.0 a point is a point wherever it lands, above 1.0 concentration pays superlinearly.
            // A power function is smooth and differentiable, so raising it does not cost the closed
            // form (Analytic.cs) — which is the property that lets a balance pass move it at all.
            var share = pts / total;
            var value = e.Read == ReadMode.Contest
                ? e.K * Math.Pow(share, ContestShareExponent) * ContestSpan
                : e.K * Math.Pow(share, MagnitudeShareExponent) * pTheta;
            result[e.Channel] = result.TryGetValue(e.Channel, out var prior) ? prior + value : value;
        }
        return result;
    }
}

/// <summary>A build: aptitude points plus the ladder-relative pools it fields.</summary>
public sealed class Build
{
    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }
    public string? Element { get; set; }

    /// <summary>Aptitude → points. Shares are what matter; absolute totals are set by `Θ`.</summary>
    public Dictionary<string, double> Points { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// BASELINE pools, in multiples of `P(Θ)` — what an actor has before spending a single point.
    ///
    /// <para>These are not the whole pool. <c>resource.max.hp</c> and
    /// <c>combat.shield.capacity.omni</c> are added on top from the aptitude distribution, so hp and
    /// shield are things a build BUYS. A baseline still has to exist for the same reason
    /// <c>BaseAccuracy = 220 + 26L</c> faces <c>BaseDodge = 26L</c> (ssot-power-scale.md §2): without
    /// one, a build that spends nothing on hp has none and the allocation is not a choice, it is a
    /// requirement.</para>
    ///
    /// <para><b>Until 2026-08-25 these were the ENTIRE pools</b> — hp was a flat constant no aptitude
    /// could raise. That made mitigation the only survival lever in the model and gave it no
    /// competitor, which is exactly what the free-build marginal test then reported as
    /// "defence dominates". A measurement of a fight nobody could build for.</para>
    /// </summary>
    public double HpPerLadder { get; set; } = 15;

    public double DamagePerLadder { get; set; } = 1.5;
    public double ShieldPerLadder { get; set; }

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static Build Load(string nameOrPath)
    {
        var path = AptitudeModel.Resolve(nameOrPath, "builds");
        return JsonSerializer.Deserialize<Build>(File.ReadAllText(path), Options)
               ?? throw new InvalidOperationException($"{path}: empty build");
    }

    /// <summary>Materialize this build at a given `Θ` into the flat stat block the duel runner takes.</summary>
    public Archetype At(int theta, AptitudeModel model, PowerLadder ladder)
    {
        // §7a.2: 3 aptitude points per Θ. Shares are preserved; the absolute count is the ladder's.
        var budget = 3.0 * theta;
        var declared = Points.Values.Sum();
        var scale = declared <= 0 ? 0 : budget / declared;
        var scaled = Points.ToDictionary(kv => kv.Key, kv => kv.Value * scale, StringComparer.Ordinal);

        var channels = model.Resolve(scaled, theta, ladder);
        var p = (double)ladder.Value(theta);

        // Pools = baseline + what the build bought. Both terms are P(Θ)-scaled, so the ratio between
        // them is Θ-free and the invariance theorem still holds (class-analytic-balance §3).
        //
        // hp and shield are read out of the SAME channel dictionary every combat stat comes from, and
        // then removed from it: they are the actor's pools, not stats the calculator reads. Leaving
        // them in would be harmless today but would silently double-count the moment anything else
        // starts reading `resource.max.hp`.
        double Take(string channel)
        {
            if (!channels.Remove(channel, out var v)) return 0;
            return v;
        }
        var hp = HpPerLadder * p + Take(ResourceMaxHp);
        // DOUBLE-COUNT FIXED 2026-08-26. This used to add `combat.shield.capacity.omni` into the
        // granted pool — but ShieldRuntime computes `maxHp = grant.BaseHp + capacity` and reads the
        // channel ITSELF, so the capacity landed twice and every shielded actor had 2x the shield it
        // was buying. The comment below already said ShieldRuntime reads the channel; the code added
        // it anyway. Grant the BASELINE only and let the runtime apply what the build bought.
        var shield = ShieldPerLadder * p;

        return new Archetype
        {
            Name = Name,
            Element = Element,
            Hp = StatRange.Fixed(hp),
            BaseDamage = StatRange.Fixed(DamagePerLadder * p),
            ShieldHp = StatRange.Fixed(shield),
            Stats = channels.ToDictionary(kv => kv.Key, kv => StatRange.Fixed(kv.Value), StringComparer.Ordinal)
        };
    }

    const string ResourceMaxHp = "resource.max.hp";

    /// <summary>Shield capacity stays in <c>Stats</c> as well as seeding the pool — <c>ShieldRuntime</c>
    /// reads the channel itself (<c>maxHp = grant.BaseHp + capacity</c>), so removing it would change
    /// shield behaviour rather than just moving a number.</summary>
    const string ShieldCapacityOmni = "combat.shield.capacity.omni";
}
