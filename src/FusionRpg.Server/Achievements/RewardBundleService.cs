using FusionRpg.Core.Achievements;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;

namespace FusionRpg.Server.Achievements;

// Reward-bundle fan-out (spec-reward-bundles.md): bundle_ref → title container →
// the one existing produce-and-bind path. No second roller, no second store.
// Bundles are effect_container rows; titles reuse the 18 existing atom kinds.
public sealed record RewardBundleGrant(string InstanceId, string BindingId, string Fingerprint);

public sealed class RewardBundleService
{
    readonly RpgStore _store;
    readonly PowerTuning _tuning;
    readonly long _catalogRevision;

    public RewardBundleService(RpgStore store, PowerTuning tuning, long catalogRevision)
    {
        _store = store;
        _tuning = tuning;
        _catalogRevision = catalogRevision;
    }

    static IReadOnlyList<string> NoDomains(string domain) => Array.Empty<string>();

    /// <summary>
    /// Grant a bundle to an owner. Resolution <c>bundle.&lt;name&gt;</c> → container
    /// <c>&lt;scope-prefix&gt;.&lt;name&gt;</c> is total and stated: empire scope reads
    /// empire-title containers, unique-actor scope reads actor-title containers.
    /// Dangling refs and undrawable pools refuse naming the row/group (whole-row
    /// rejection); grants bind instance + ownership atomically via ProduceAndBind.
    /// </summary>
    public AtomRejection Grant(
        string bundleRef, string scope, OwnerScope owner,
        long rollSeed, int thetaContent, out RewardBundleGrant? grant)
    {
        grant = null;
        var prefix = scope switch
        {
            AchievementScopes.Empire => ContainerRow.PrefixOf(ContainerKind.EmpireTitle),
            AchievementScopes.UniqueActor => ContainerRow.PrefixOf(ContainerKind.ActorTitle),
            _ => null,
        };
        if (prefix is null)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"bundle '{bundleRef}': unknown scope '{scope}'");
        const string marker = "bundle.";
        if (!bundleRef.StartsWith(marker, StringComparison.Ordinal) || bundleRef.Length == marker.Length)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"bundle '{bundleRef}' is not 'bundle.<name>'");
        var containerId = prefix + "." + bundleRef[marker.Length..];
        var container = _store.GetContainer(containerId);
        if (container is null)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"bundle '{bundleRef}' has no container '{containerId}'");
        var produced = _store.ProduceAndBind(
            container, NoDomains, rollSeed, thetaContent, _tuning, owner,
            slot: null, priority: 1, source: $"bundle:{bundleRef}",
            out var instanceId, out var bindingId,
            origin: InstanceOrigin.Grant, catalogRevision: _catalogRevision);
        if (!produced.IsOk) return produced;
        var instance = _store.GetInstance(instanceId!);
        grant = new RewardBundleGrant(instanceId!, bindingId!, instance!.ContentFingerprint());
        return AtomRejection.Ok;
    }
}
