using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Server.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>Every notify-service test configures the process-wide `NotificationCatalogHub`/
/// `NotificationTuningHub` with its own scenario catalog (extra rigor: tests touching shared static
/// state share one serialized xUnit collection rather than risk a race between parallel test
/// classes). `DisableParallelization` makes every class below run one at a time.</summary>
[CollectionDefinition("NotificationHub", DisableParallelization = true)]
public class NotificationHubCollection { }

/// <summary>A fake `IPlayerPush` that records every push verbatim, matching this project's own
/// `RecordingPush`/`IDelveLivePush` precedent (`DelveBattleSessionManagerTests.cs`) rather than a
/// mocking library this test project does not depend on. Thread-safe: the pump can push while a
/// test reads.</summary>
public sealed class FakePlayerPush : IPlayerPush
{
    readonly object _gate = new();
    readonly List<(SaveId SaveId, string EventName, object Payload)> _pushes = new();

    /// <summary>Throws on the NEXT push only, then resets - for the "crash between store and push" test.</summary>
    public bool ThrowOnNextPush;

    public void Push(SaveId saveId, string eventName, object payload)
    {
        lock (_gate)
        {
            if (ThrowOnNextPush)
            {
                ThrowOnNextPush = false;
                throw new InvalidOperationException("forced push failure (test)");
            }
            _pushes.Add((saveId, eventName, payload));
        }
    }

    public List<(SaveId SaveId, string EventName, object Payload)> Pushes
    {
        get { lock (_gate) return new List<(SaveId, string, object)>(_pushes); }
    }
}

/// <summary>A fake `IWorldTurnNotificationSource` whose `Collect` is supplied per test.</summary>
public sealed class FakeWorldTurnSource : IWorldTurnNotificationSource
{
    public string SourceId { get; init; } = "fake-source";
    public Func<WorldTurnNotificationContext, IEnumerable<AddressedDraft>> OnCollect { get; init; } = _ => Array.Empty<AddressedDraft>();
    public IEnumerable<AddressedDraft> Collect(WorldTurnNotificationContext ctx) => OnCollect(ctx);
}

/// <summary>Builds a tiny real catalog/tuning pair and configures both Hubs - shared by every test
/// in the `NotificationHub` collection.</summary>
public static class NotificationHubFixture
{
    public const string TestCategory = "test.category";
    public const string TestMessageKey = "test.key";
    public const string CriticalCategory = "test.critical-category";

    public static void Configure(int retainPerCategory = 100, int repeatWindowWorldTurns = 3)
    {
        var json = $$"""
            {
              "categories": [
                { "id": "{{TestCategory}}", "domain": "test", "displayName": "Test", "messageKeys": ["{{TestMessageKey}}"] },
                { "id": "{{CriticalCategory}}", "domain": "test", "displayName": "Test Critical", "messageKeys": ["{{TestMessageKey}}"] }
              ],
              "promotions": { "toast": ["{{CriticalCategory}}"], "critical": ["{{CriticalCategory}}"] }
            }
            """;
        NotificationCatalogHub.Configure(NotificationCatalogLoader.Parse(json));
        NotificationTuningHub.Configure(new NotificationTuning(retainPerCategory, repeatWindowWorldTurns));
    }

    public static NotificationDraft Draft(
        string dedupKey, NotifySeverity severity = NotifySeverity.Important, string? subjectKey = null,
        int? worldTurn = null, string category = TestCategory) =>
        new(dedupKey, category, severity, "test-source", TestMessageKey, Array.Empty<NotifyArg>(), subjectKey, "w", worldTurn);
}

/// <summary>`WorldTemplateCatalog.Build`'s intel-seed path reads `WorldTuningHub`/`LoamPolicy`, which
/// this Server.Tests assembly's own module initializer does not configure (it covers Power/
/// Aptitude/DerivedStat/Rung/Aura only). Matches the per-file `ConfigureWorldTuningOnce` shape
/// already used by `WorldBindWardenEndpointTests.cs` and its siblings.</summary>
public static class WorldTuningTestSupport
{
    static bool _configured;

    public static void ConfigureOnce()
    {
        if (_configured) return;
        var tuningDir = Path.Combine(FindRepoRoot(), "data", "tuning");
        string Read(string name) => File.ReadAllText(Path.Combine(tuningDir, name));
        FusionRpg.Core.World.Loam.LoamPolicy.Configure(
            FusionRpg.Core.World.Loam.LoamTuningLoader.Parse(Read("loam.v5.json")));
        var worldTuning = FusionRpg.Core.World.WorldTuningLoader.Parse(Read("world.v6.json"));
        FusionRpg.Core.World.WorldTuningHub.Configure(worldTuning);
        FusionRpg.Core.World.Growth.RecruitPolicy.Configure(worldTuning.Growth);
        FusionRpg.Core.World.Ai.WorldAiPolicy.Configure(
            FusionRpg.Core.World.Ai.WorldAiTuningLoader.Parse(Read("ai.v3.json")));
        _configured = true;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "FusionRpg.Injector"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("could not find repo root above " + AppContext.BaseDirectory);
    }
}

/// <summary>notify-service NS3.4: `WorldEndpoints.cs`'s `/commit` route now takes a
/// `WorldTurnNotificationPump`. ASP.NET Core's minimal-API route compiler infers EVERY mapped
/// route's parameters eagerly (at first `Endpoints` enumeration, not per-request), so any test file
/// that calls `app.MapWorld()` needs these registered too or the whole app fails to start - even
/// tests that never touch `/commit`. This is the same class of regression NS1.6 already fixed for
/// `PlayerConnectionRegistry`/`RpgHub`. None of these services are ever exercised by the 15 files
/// that call this (they test other World routes); it exists purely so DI resolution succeeds.</summary>
public static class NotificationEndpointDiStubs
{
    public static void AddNotificationEndpointStubs(this IServiceCollection services)
    {
        NotificationHubFixture.Configure();
        services.AddSingleton<IPlayerPush>(new FakePlayerPush());
        services.AddSingleton(sp => new NotificationContract(NotificationCatalogHub.Catalog));
        services.AddSingleton<NotificationPublisher>();
        services.AddSingleton<IEnumerable<IWorldTurnNotificationSource>>(Array.Empty<IWorldTurnNotificationSource>());
        services.AddSingleton<WorldTurnNotificationPump>();
    }
}
