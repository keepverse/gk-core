using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Data;

namespace FusionRpg.Server.Notifications;

/// <summary>cache-notify-source spec §2 — tells the owner when their fallen gear is at risk, while
/// it can still be fetched. Read-only over `RpgStore.CacheDecay.cs`'s own reads (NS6.1); never
/// writes, never pushes (the pump/publisher own that).</summary>
public sealed class CacheNotificationSource : IWorldTurnNotificationSource
{
    public string SourceId => "cache-notify-source";

    /// <summary>The two categories this source can emit. Named here so the catalogue-coherence guard
    /// (`NotificationCatalogCoherenceTests`) can join code to catalogue instead of trusting a literal in a
    /// test — the same shape `WorldTurnNotificationClassifier.KnownCategories` gives the world domain. A row
    /// added to a draft below without being added here fails that guard.</summary>
    public const string CreatedCategory = "cache.created";
    public const string DecayedCategory = "cache.decayed";

    public static IReadOnlySet<string> KnownCategories =>
        new HashSet<string>(new[] { CreatedCategory, DecayedCategory }, StringComparer.Ordinal);

    /// <summary>The keys the catalogue rows for those categories must declare — separate names from the
    /// categories themselves because a category id and a message key are different vocabularies that happen
    /// to share a spelling here.</summary>
    public const string CreatedMessageKey = "cache.created";
    public const string DecayedMessageKey = "cache.decayed";

    readonly RpgStore _store;

    public CacheNotificationSource(RpgStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    public IEnumerable<AddressedDraft> Collect(WorldTurnNotificationContext ctx)
    {
        var ownerPlayerId = ctx.Header.PlayerId;
        // The save is the routing key (R17); the cache reads below take the raw id, this module's
        // own seam takes the typed one.
        var saveId = new SaveId(ownerPlayerId);

        // Window [t, t+1], deduplicated by key (spec §2). The real write paths disagree on which
        // turn number they stamp: the world-map legion-death path
        // (RpgStore.CargoFate.cs's StartLegionCacheClockUnlocked) reads `current_turn` BEFORE
        // CommitWorldTurn's own advance, stamping exactly t = ctx.ResolvedTurn; the generic
        // lawn/delve clock start (TryStartDecayClockUnlocked) and the decay tick itself
        // (RpgStore.WorldTurns.cs:700) both run AFTER that advance, stamping t + 1. A window
        // covering both turns, with dedup keys making delivery exactly-once regardless of which
        // pump run first captures a given event, is correct either way — the same defensive shape
        // the spec's own code style already uses for the tick loop, applied here to both loops.
        foreach (var started in _store.ListCacheClocksStarted(ownerPlayerId, ctx.ResolvedTurn, ctx.ResolvedTurn + 1))
        {
            yield return new AddressedDraft(saveId, new NotificationDraft(
                DedupKey: $"cache:{started.CacheId}:created",
                CategoryId: CreatedCategory,
                Severity: NotifySeverity.Important,
                SourceId: SourceId,
                MessageKey: CreatedMessageKey,
                Args: PlaceArgs(started.PlaceKind, started.PlaceRef).Count("itemCount", started.ItemCount).Build(),
                SubjectKey: $"cache:{started.CacheId}",
                WorldId: ctx.WorldId,
                WorldTurn: ctx.ResolvedTurn));
        }

        foreach (var tick in _store.ListCacheDecayTicks(ownerPlayerId, ctx.ResolvedTurn, ctx.ResolvedTurn + 1))
        {
            if (tick.Destroyed == 0) continue; // a tick where everything survived is not news
            yield return new AddressedDraft(saveId, new NotificationDraft(
                DedupKey: $"cache:{tick.CacheId}:decay:{tick.Tick}",
                CategoryId: DecayedCategory,
                Severity: tick.Remaining == 0 ? NotifySeverity.Important : NotifySeverity.Routine,
                SourceId: SourceId,
                MessageKey: DecayedMessageKey,
                Args: PlaceArgs(tick.PlaceKind, tick.PlaceRef).Count("destroyed", tick.Destroyed).Count("remaining", tick.Remaining).Build(),
                SubjectKey: tick.Remaining == 0 ? $"cache:{tick.CacheId}:emptied" : $"cache:{tick.CacheId}",
                WorldId: ctx.WorldId,
                WorldTurn: ctx.ResolvedTurn));
            // WorldTurn is the pump's resolved turn, not the tick, so the item lands on the world
            // rail's worldLatestTurn filter beside that turn's world items (notify-client §6). The
            // real tick number lives in the dedup key, not in the wire args.
        }
    }

    /// <summary>Place argument (spec §2): `world_sector`/`world_lane` become a `ref` (which the web
    /// mount also uses as `target`); `lawn`/`delve_room`/`siege` become a `domainToken` of the bare
    /// place-kind string, which the cache translator (NS6.3) words — their own `place_ref` is an
    /// opaque session/room id with no human meaning. The vocabulary is
    /// `RpgStore.CorpseCachePlaceKinds`'s own closed set.</summary>
    static NotifyArgsBuilder PlaceArgs(string placeKind, string placeRef) => placeKind switch
    {
        "world_sector" => Args.Ref("place", NotifyRefKind.Sector, placeRef),
        "world_lane" => Args.Ref("place", NotifyRefKind.Lane, placeRef),
        _ => Args.DomainToken("place", placeKind)
    };
}
