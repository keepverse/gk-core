using System.Globalization;
using System.Text.Json;
using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects;

public sealed class EffectDef
{
    public string EffectId { get; init; } = "";
    public string EffectType { get; init; } = EffectTypes.Triggered;
    public string Name { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public string SourceTag { get; init; } = "";
    public List<string> Triggers { get; init; } = new();
    public List<EffectActionRow> Actions { get; init; } = new();

    public EffectDefDto ToDto() => new()
    {
        EffectId = EffectId,
        EffectType = EffectType,
        Name = Name,
        Enabled = Enabled,
        SourceTag = SourceTag,
        Triggers = new List<string>(Triggers),
        Actions = Actions.Select(a => new EffectDefActionDto
        {
            Seq = a.Seq,
            Action = a.Action,
            Params = new Dictionary<string, object?>(a.Params)
        }).ToList()
    };
}

public sealed class EffectActionRow
{
    public int Seq { get; init; }
    public string Action { get; init; } = "";
    public Dictionary<string, object?> Params { get; init; } = new();
}

public sealed class EffectGrant
{
    public string GrantId { get; set; } = "";
    public string EffectId { get; set; } = "";
    public string OwnerKind { get; set; } = "match";
    public string OwnerKey { get; set; } = EffectOwnerKeys.Match;
    public string PluginId { get; set; } = "debug";
    public int Priority { get; set; }
    public Dictionary<string, object?> Overlay { get; set; } = new();

    public static EffectGrant FromDto(EffectGrantDto dto) => new()
    {
        GrantId = string.IsNullOrWhiteSpace(dto.GrantId) ? Guid.NewGuid().ToString("N") : dto.GrantId,
        EffectId = dto.EffectId,
        OwnerKind = string.IsNullOrWhiteSpace(dto.OwnerKind) ? "match" : dto.OwnerKind,
        OwnerKey = string.IsNullOrWhiteSpace(dto.OwnerKey) ? EffectOwnerKeys.Match : dto.OwnerKey,
        PluginId = dto.PluginId,
        Priority = dto.Priority,
        Overlay = JsonOverlay.FromObject(dto.Overlay)
    };

    public EffectGrantDto ToDto() => new()
    {
        GrantId = GrantId,
        EffectId = EffectId,
        OwnerKind = OwnerKind,
        OwnerKey = OwnerKey,
        PluginId = PluginId,
        Priority = Priority,
        Overlay = new Dictionary<string, object?>(Overlay)
    };
}

public interface IEffectClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemEffectClock : IEffectClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class FakeEffectClock : IEffectClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);

    public void Advance(TimeSpan ts) => UtcNow = UtcNow.Add(ts);
    public void AdvanceMs(int ms) => Advance(TimeSpan.FromMilliseconds(ms));
}

public interface IEffectRandom
{
    double NextDouble();
}

/// <summary>
/// Deterministic effect RNG. Backed by the owned xoshiro256** stream, not System.Random —
/// SeededRng's own header states the rule: System.Random's seeded sequence is not guaranteed
/// stable across .NET versions, so a runtime upgrade would silently move every chance-gated
/// replay with no content change and no contentHash change to make it visible.
/// </summary>
public sealed class SeededEffectRandom : IEffectRandom
{
    readonly Battle.SeededRng _rng;

    public SeededEffectRandom(int seed)
        : this(unchecked((ulong)seed), "effect.proc") { }

    public SeededEffectRandom(ulong runSeed, string streamName)
        => _rng = Battle.SeededRng.DeriveStream(runSeed, streamName);

    /// <summary>
    /// [0, 1) from the top 53 bits — the standard IEEE-754 double construction. Built here rather
    /// than added to SeededRng, which is spec-fixed and integer-only on purpose.
    /// </summary>
    public double NextDouble() => (_rng.NextULong() >> 11) * (1.0 / 9007199254740992.0);
}

public interface IEffectActionSink
{
    /// <summary>Execute or record one planned action. Return false to stop the sequence.</summary>
    bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item);
}

/// <summary>
/// combat-ai `resolvable-here` (module 5, CAI1.12, spec-resolvable-here.md §1): a sink declares the
/// <c>EffectActions.*</c> opcode set it actually runs. <b>Implemented by the sink, next to
/// <see cref="IEffectActionSink.Execute"/>; a sink that does not declare is refused at construction</b>
/// — there is no permissive default, because "assume it executes everything" is exactly the silent
/// no-op this layer refuses (<c>BindGate.cs:24</c>'s own posture).
///
/// <para>A sink's declaration must be derived from its own dispatch rather than written beside it, so
/// the two cannot drift. Battle keeps the declaration and the executor the same object
/// (<c>BattleEffectSink._handlers.Keys</c>); the injector declares a list held to its dispatch switch
/// by a source-scan test. Both are read here and by <c>PlaceExecutionProfile.FromSink</c> — never
/// authored in a profile row, never copied into tuning.</para>
/// </summary>
public interface IDeclaresExecution
{
    /// <summary>The <c>EffectActions.*</c> opcodes this sink's own dispatch runs — for battle, the
    /// dispatch table's own key set.</summary>
    IReadOnlySet<string> ExecutedActions { get; }
}

public sealed class EffectExecuteContext
{
    public EffectEventDto Event { get; init; } = new();
    public EffectGrant Grant { get; init; } = new();
    public EffectDef Def { get; init; } = new();
}

public sealed class RecordingEffectSink : IEffectActionSink
{
    public List<EffectActionPlanItem> Items { get; } = new();
    public List<EffectFiredDto> Fired { get; } = new();
    public bool FailNext { get; set; }

    public bool Execute(EffectExecuteContext ctx, EffectActionPlanItem item)
    {
        Items.Add(item);
        if (FailNext)
        {
            FailNext = false;
            Fired.Add(new EffectFiredDto
            {
                GrantId = item.GrantId,
                EffectId = item.EffectId,
                Trigger = ctx.Event.Trigger,
                Action = item.Action,
                Ok = false,
                Error = "forced-fail"
            });
            return false;
        }

        Fired.Add(new EffectFiredDto
        {
            GrantId = item.GrantId,
            EffectId = item.EffectId,
            Trigger = ctx.Event.Trigger,
            Action = item.Action,
            Ok = true
        });
        return true;
    }

    public IntentPlanDto ToPlan(string trigger, IEnumerable<string>? skipped = null) => new()
    {
        ContractVersion = FoundationContractVersion.Current,
        Trigger = trigger,
        Actions = Items.ToList(),
        Skipped = skipped?.ToList() ?? new List<string>()
    };
}

public static class JsonOverlay
{
    public static Dictionary<string, object?> FromObject(object? overlay)
    {
        if (overlay == null) return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (overlay is Dictionary<string, object?> d)
        {
            var r = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in d) r[kv.Key] = UnwrapValue(kv.Value);
            return r;
        }

        if (overlay is Dictionary<string, object> d2)
        {
            var r = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in d2) r[kv.Key] = UnwrapValue(kv.Value);
            return r;
        }

        var json = JsonSerializer.Serialize(overlay);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (parsed == null) return map;
        foreach (var kv in parsed)
            map[kv.Key] = Unwrap(kv.Value);
        return map;
    }

    static object? UnwrapValue(object? v) => v switch
    {
        null => null,
        JsonElement el => Unwrap(el),
        Dictionary<string, object?> nested => FromObject(nested),
        _ => v
    };

    // E28 session finding (2026-09-03): the Number arm's ternary — `TryGetInt64(out var l) ? l :
    // el.GetDouble()` — always produced a boxed `double`, never `long`, regardless of which branch
    // ran. The `?:` operator resolves ONE static type for both branches before boxing, and `long`
    // widens implicitly to `double` but not the reverse, so the compiler silently converted `l` to
    // `double` every time. `(object)l` on the true branch breaks that unification. Matters more here
    // than in the sibling defect (AtomCompiler.Plain, fixed the same session): this is the grant
    // overlay's own magnitude path, and an integral JSON number read back as `double` loses
    // exact-integer precision above 2^53 (precision, not a ban on floating point) — `long` was always
    // the intended type for an integral value here, not an accident of formatting.
    static object? Unwrap(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? (object)l : el.GetDouble(),
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Object => FromObject(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(el.GetRawText())),
        JsonValueKind.Array => el.EnumerateArray().Select(Unwrap).ToList(),
        _ => el.GetRawText()
    };

    public static double GetDouble(Dictionary<string, object?> map, string key, double fallback = 0)
    {
        if (!map.TryGetValue(key, out var v) || v == null) return fallback;
        return Convert.ToDouble(v, CultureInfo.InvariantCulture);
    }

    public static int GetInt(Dictionary<string, object?> map, string key, int fallback = 0)
    {
        if (!map.TryGetValue(key, out var v) || v == null) return fallback;
        return Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// E35 (spec-match-modify.md §2.3): the `long`-preserving sibling of <see cref="GetInt"/> and
    /// <see cref="GetDouble"/> — needed because `GetDouble`'s `Convert.ToDouble` stops being
    /// integer-exact above 2^53 (docs/architecture/numeric-types.md's own overflow table), silently corrupting a magnitude
    /// like <c>zombieStartAmmor</c> that this repo's overflow rule says must stay `long` end to end.
    /// Only this kind's one true `long` magnitude needs it today.
    /// </summary>
    public static long GetLong(Dictionary<string, object?> map, string key, long fallback = 0)
    {
        if (!map.TryGetValue(key, out var v) || v == null) return fallback;
        return Convert.ToInt64(v, CultureInfo.InvariantCulture);
    }

    /// <summary>Null, not a fallback value, when absent — for a param whose absence is itself
    /// meaningful (E28: an unset grid.clear row/col must stay "no constraint", never collide with a
    /// real 0).</summary>
    public static int? GetIntOrNull(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v) || v == null) return null;
        return Convert.ToInt32(v, CultureInfo.InvariantCulture);
    }

    public static string? GetString(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v) || v == null) return null;
        return Convert.ToString(v, CultureInfo.InvariantCulture);
    }

    public static bool GetBool(Dictionary<string, object?> map, string key, bool fallback = false)
    {
        if (!map.TryGetValue(key, out var v) || v == null) return fallback;
        return Convert.ToBoolean(v, CultureInfo.InvariantCulture);
    }
}
