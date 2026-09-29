namespace FusionRpg.Tools.RpgSim;

/// <summary>One call op: its surface, its method and its route template, plus where each request
/// part comes from. Everything is declared here, once, because the route's own signature owns it and a
/// scenario may not choose it.</summary>
/// <param name="PathFromContext">(placeholder, capture) pairs filling the template's
/// <c>{placeholder}</c> slots.</param>
/// <param name="Query">(field, source) pairs added to the query string.</param>
/// <param name="Body">(field, source) pairs sent as the JSON body.</param>
public sealed record ScenarioOp(string Name, string Surface, string Method, string RouteTemplate)
{
    public (string Placeholder, string Capture)[] PathFromContext { get; init; } =
        Array.Empty<(string, string)>();

    /// <summary>A source is <c>ctx:&lt;capture&gt;</c>, <c>arg:&lt;declared arg&gt;</c>, or
    /// <c>derived:correlationId</c> — an idempotency key the runner derives from the scenario seed and
    /// the step index, so the scenario owns no random value and a rerun is the same rerun.</summary>
    public (string Field, string Source)[] Query { get; init; } = Array.Empty<(string, string)>();

    public (string Field, string Source)[] Body { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>What a (field, source) pair may say. Closed, and refused by name in the runner.</summary>
public static class ScenarioSources
{
    public const string ContextPrefix = "ctx:";
    public const string ArgPrefix = "arg:";
    public const string CorrelationId = "derived:correlationId";
}

/// <summary>
/// The closed op vocabulary (`scenario-format.md` §2). An op is named after the route it calls: its
/// <b>surface</b> prefix is derived from, and checked against, the route's own surface — <c>sim.*</c>
/// is a <c>/api/sim/*</c> route, <c>test.*</c> is a <c>/api/test/*</c> route, <c>api.*</c> is any
/// other player-facing route. The tail names the route's action and is authored; the route is the
/// authority, and the validator refuses a step whose declared route is not this table's route for
/// that op. That is the property RS1 shipped (<c>RpgScenarioSlice0E2ETests.cs</c>'s <c>OpRoutes</c>)
/// with one copy of the table instead of one per reader.
///
/// <para><c>read.*</c>, <c>expect.*</c> and <c>digest</c> are the <b>verdict</b> vocabulary: they do
/// not call a route that changes state. A <c>read.*</c> names the FE-facing GET or hub message its
/// payload comes from; <c>expect.*</c> asserts over a reading; <c>digest</c> marks which readings
/// enter the hash.</para>
/// </summary>
public static class ScenarioVocabulary
{
    public const string Call = "call";
    public const string Read = "read";
    public const string Expect = "expect";
    public const string Digest = "digest";

    /// <summary>
    /// <c>clock.set</c> (RS3 increment 5b, owner ruling on RS-F16 candidate 2): a DECLARATION that the host
    /// must believe the machine clock is <c>offsetSeconds</c> ahead from this point on. It is not a call —
    /// the scenario names no route, because a route that sets a clock is deliberately not specified
    /// (<c>rpg-simulator-spec-clock-seam.md</c> §2) — and it is not a read or an assertion. The runner hands
    /// the value to the host's clock control and refuses the run by name when there is none.
    /// </summary>
    public const string Clock = "clock";

    public static readonly IReadOnlyList<ScenarioOp> Calls = new[]
    {
        new ScenarioOp("api.player.create", "api", "POST", "/api/players")
        {
            Body = new[] { ("name", "arg:name") }
        },
        new ScenarioOp("test.seed.souls", "test", "POST", "/api/test/seed-souls-demo")
        {
            Query = new[] { ("playerId", "ctx:playerId"), ("amount", "arg:amount") }
        },
        new ScenarioOp("api.creature.summon", "api", "POST", "/api/creatures/summon")
        {
            Body = new[]
            {
                ("playerId", "ctx:playerId"),
                ("count", "arg:count"),
                ("correlationId", ScenarioSources.CorrelationId)
            }
        },
        new ScenarioOp("api.expedition.dispatch", "api", "POST", "/api/expeditions/dispatch")
        {
            Body = new[]
            {
                ("playerId", "ctx:playerId"),
                ("tierId", "arg:tierId"),
                ("squad", "ctx:squadIds"),
                ("correlationId", ScenarioSources.CorrelationId)
            }
        },
        // `test.expedition.due` is GONE (RS3 increment 5b, owner ruling B3 (a) / RS-F16): the SIM timer
        // rewind's store bypass is retired, and a scenario makes an expedition due by declaring a clock
        // offset (`clock.set`) instead. A corpus that still names the op fails validation by name.
        new ScenarioOp("api.expedition.collect", "api", "POST", "/api/expeditions/{id}/collect")
        {
            PathFromContext = new[] { ("id", "expeditionId") },
            Body = new[] { ("playerId", "ctx:playerId") }
        },
    };

    static readonly Dictionary<string, ScenarioOp> ByName =
        Calls.ToDictionary(o => o.Name, StringComparer.Ordinal);

    public static ScenarioOp? CallOp(string op) => ByName.TryGetValue(op, out var d) ? d : null;

    /// <summary>The closed comparison set an <c>expect.*</c> step may name. Every one is a comparison,
    /// a membership test or a shape test — none computes a number, and none parses a value into a domain
    /// quantity. `equals`, `equalSet`, `allIn` and `allEqual` take their other side from a reading or a
    /// capture, which is what makes the fabrication line (squad vs roster) a measurement.</summary>
    public static readonly IReadOnlyList<string> Checks = new[]
    {
        "equals",        // path == value (literal) or other (reading#/pointer | {captured})
        "notEmpty",      // path is a non-empty array, object or string
        "allNonEmpty",   // every element of the array path is a non-empty string
        "atLeast",       // every numeric element (or the scalar) >= literal value
        "nonZero",       // every numeric element (or the scalar) != 0
        "memberOf",      // the scalar, or every array element, is in the literal set
        "contains",      // the array path contains value, or the string path contains it
        "allStartWith",  // every string element starts with value (a literal template)
        "equalSet",      // order-insensitive equality of two arrays (other)
        "allIn",         // every element of the array path is in other (an array)
        "allEqual",      // every element of the array path equals other (a scalar)
    };

    /// <summary>Checks whose other side is a literal. `equals` may use either `value` or `other`.</summary>
    public static readonly IReadOnlyList<string> ValueChecks =
        new[] { "equals", "atLeast", "memberOf", "contains", "allStartWith" };

    /// <summary>Checks whose other side must be a reading reference or a capture, never a constant:
    /// comparing a read-back to a constant would let a scenario assert what it hopes.</summary>
    public static readonly IReadOnlyList<string> OtherChecks =
        new[] { "equalSet", "allIn", "allEqual" };

    public static readonly IReadOnlyList<string> ClockModes = new[] { "ambient", "offset", "explicit" };

    public static string Prefix(string op)
    {
        var dot = op.IndexOf('.');
        return dot < 0 ? op : op[..dot];
    }

    public static string Kind(string op) => Prefix(op) switch
    {
        "sim" or "test" or "api" => Call,
        Read => Read,
        Expect => Expect,
        Digest => Digest,
        Clock => Clock,
        _ => "unknown"
    };

    /// <summary>The surface of a declared route ("POST /api/test/x" -&gt; "test"). Null when the route
    /// is not a method + path pair at all.</summary>
    public static string? RouteSurface(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return null;
        var trimmed = route.Trim();
        var space = trimmed.IndexOf(' ');
        // A route may be a method + path pair, or a bare hub message (`/hub/rpg`), which has no method.
        var path = space <= 0 ? trimmed : trimmed[(space + 1)..].Trim();
        if (path.StartsWith("/api/sim/", StringComparison.Ordinal)) return "sim";
        if (path.StartsWith("/api/test/", StringComparison.Ordinal)) return "test";
        if (path.StartsWith("/api/", StringComparison.Ordinal)) return "api";
        if (path.StartsWith("/hub/", StringComparison.Ordinal)) return "hub";
        return null;
    }

    public static string? MethodOf(string? route)
    {
        if (string.IsNullOrWhiteSpace(route)) return null;
        var trimmed = route.Trim();
        var space = trimmed.IndexOf(' ');
        return space <= 0 ? null : trimmed[..space].Trim().ToUpperInvariant();
    }

    /// <summary>
    /// The read-back rule (`readback-verdict.md` §2, <c>docs/contributing/live-probe-standard.md</c>
    /// §3): a verdict reads through the same query path the FE uses, or the hub message it receives.
    /// That excludes <c>/api/test/*</c> (the fixture/debug group — <c>/api/test/snapshot</c> is the
    /// standard's named anti-pattern) and <c>/api/sim/*</c> (the input feed, which has no result to
    /// read), and requires a GET or a hub message.
    /// </summary>
    public static bool IsFeFacingRead(string? route)
    {
        var surface = RouteSurface(route);
        if (surface is not ("api" or "hub")) return false;
        if (MethodOf(route) == "GET") return true;
        return surface == "hub";
    }
}
