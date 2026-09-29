using System.Text.Json;
using System.Text.Json.Serialization;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// A stat value in a scenario. Accepts either a bare number (fixed) or <c>{"min":a,"max":b}</c>
/// (sampled uniformly per trial) so a scenario file stays readable when most stats are constants.
/// </summary>
[JsonConverter(typeof(StatRangeConverter))]
public sealed record StatRange(double Min, double Max)
{
    public static StatRange Fixed(double v) => new(v, v);

    public double Sample(Random rng) => Max <= Min ? Min : Min + rng.NextDouble() * (Max - Min);

    public bool IsFixed => Max <= Min;

    public override string ToString() => IsFixed ? Min.ToString("0.##") : $"{Min:0.##}..{Max:0.##}";
}

public sealed class StatRangeConverter : JsonConverter<StatRange>
{
    public override StatRange Read(ref Utf8JsonReader reader, Type _, JsonSerializerOptions __)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return StatRange.Fixed(reader.GetDouble());

        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("a stat value must be a number or {\"min\":a,\"max\":b}");

        double min = 0, max = 0;
        var sawMin = false;
        var sawMax = false;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) continue;
            var prop = reader.GetString();
            reader.Read();
            if (string.Equals(prop, "min", StringComparison.OrdinalIgnoreCase)) { min = reader.GetDouble(); sawMin = true; }
            else if (string.Equals(prop, "max", StringComparison.OrdinalIgnoreCase)) { max = reader.GetDouble(); sawMax = true; }
        }
        if (!sawMin && !sawMax) throw new JsonException("stat range needs at least one of min/max");
        if (!sawMin) min = max;
        if (!sawMax) max = min;
        return new StatRange(min, max);
    }

    public override void Write(Utf8JsonWriter writer, StatRange value, JsonSerializerOptions _)
    {
        if (value.IsFixed) { writer.WriteNumberValue(value.Min); return; }
        writer.WriteStartObject();
        writer.WriteNumber("min", value.Min);
        writer.WriteNumber("max", value.Max);
        writer.WriteEndObject();
    }
}

/// <summary>How the attack's element payload is chosen each trial.</summary>
public enum ElementMode
{
    /// <summary>No payload at all. NOTE: OverlayCombatMath.Finalize returns early for an empty
    /// payload, so this bypasses the whole mitigation chain — useful to demonstrate that, not to
    /// measure it.</summary>
    None,
    /// <summary>One concrete element, uniformly chosen per trial.</summary>
    SingleRandom,
    /// <summary>The elements named in <see cref="Scenario.FixedElements"/>, equally weighted.</summary>
    Fixed
}

public sealed class Scenario
{
    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }
    public int Trials { get; set; } = 10_000;
    public int Seed { get; set; } = 42;

    /// <summary>Pre-mitigation authored hit size (the packet's SignedAmount magnitude).</summary>
    public StatRange BaseDamage { get; set; } = StatRange.Fixed(100);

    public ElementMode Elements { get; set; } = ElementMode.SingleRandom;
    public List<string>? FixedElements { get; set; }

    /// <summary>Defender's own element types — drives the matchup matrix. null = neutral.</summary>
    public string? DefenderElement { get; set; }

    /// <summary>Any registered channel id → value. Validated against the live registry on load.</summary>
    public Dictionary<string, StatRange> Attacker { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, StatRange> Defender { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Shield pool granted to the defender each trial. 0 = no shield.</summary>
    public StatRange ShieldHp { get; set; } = StatRange.Fixed(0);

    /// <summary>Wire actorResolve into the dispatcher, enabling the reflection path.</summary>
    public bool Reflection { get; set; } = true;

    /// <summary>`fight` mode only: HP pools. The attacker's pool exists so reflected damage can
    /// actually kill it — the whole point of a thorns build.</summary>
    public StatRange AttackerHp { get; set; } = StatRange.Fixed(10_000);
    public StatRange DefenderHp { get; set; } = StatRange.Fixed(10_000);

    /// <summary>Give up on a fight after this many swings and count it a stalemate. A stalemate is
    /// a real outcome, not an error: it means neither side can finish the other.</summary>
    public int MaxRounds { get; set; } = 500;

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Scenario Load(string path)
    {
        var s = JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), Options)
                ?? throw new InvalidOperationException($"{path}: empty scenario");
        s.Validate(path);
        return s;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>
    /// Fail loudly on a typo'd channel id. A silently-ignored stat would make every number this
    /// tool prints wrong in a way nobody could see — the one failure mode a balance tool must not have.
    /// </summary>
    public void Validate(string origin)
    {
        var registry = DerivedStatRegistry.CreateDefault();
        var bad = new List<string>();
        foreach (var id in Attacker.Keys.Concat(Defender.Keys))
            if (!registry.TryResolveChannel(id, out _))
                bad.Add(id);
        if (bad.Count > 0)
            throw new InvalidOperationException(
                $"{origin}: unregistered channel id(s): {string.Join(", ", bad)}");

        if (Trials <= 0) throw new InvalidOperationException($"{origin}: trials must be > 0");
        if (Elements == ElementMode.Fixed && (FixedElements == null || FixedElements.Count == 0))
            throw new InvalidOperationException($"{origin}: elements=fixed needs fixedElements");
        foreach (var e in FixedElements ?? new List<string>())
            if (!ElementRoster.TryParse(e, out _))
                throw new InvalidOperationException($"{origin}: unknown element '{e}'");
        if (DefenderElement != null && !ElementRoster.TryParse(DefenderElement, out _))
            throw new InvalidOperationException($"{origin}: unknown defenderElement '{DefenderElement}'");
    }

    public Scenario Clone()
    {
        var c = (Scenario)MemberwiseClone();
        c.Attacker = new Dictionary<string, StatRange>(Attacker, StringComparer.Ordinal);
        c.Defender = new Dictionary<string, StatRange>(Defender, StringComparer.Ordinal);
        c.FixedElements = FixedElements == null ? null : new List<string>(FixedElements);
        return c;
    }
}
