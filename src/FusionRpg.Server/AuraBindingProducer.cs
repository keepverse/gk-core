using System.Globalization;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// `aura-binding-producer` BP2/BP3 (`spec-aura-binding-producer.md`) — the one production caller of
/// <see cref="RpgStore.Bind"/> for commander auras. Before this, `RpgStore.Bind` had 19 test callers
/// and zero real ones (grepped, not assumed); the aura-skill A5 live proof had to hand-write an
/// `effect_instance` + `effect_binding` row and delete it afterwards. This class is what makes that
/// unnecessary.
///
/// <para><b>Cold loop, `player:` scope only, never touches Unity</b> — §3.1: the producer hooks the
/// existing enable/disable endpoints, not loadout save (equipping is not activating) and not
/// `board.start` (the server is not in that conversation).</para>
///
/// <para><b>Carries no magnitude</b> (§4): it writes identity and ordering only — <c>priority = 0</c>
/// (no authored aura precedence yet), <c>source = "aura"</c> (so withdraw can target exactly this
/// feature's rows and nothing else's), <c>slot = null</c> (slots are for items). Whatever value an
/// aura's atoms carry is the container's own content (AU2's job), not this producer's.</para>
/// </summary>
public sealed class AuraBindingProducer
{
    public const string Source = "aura";

    readonly RpgStore _store;
    readonly UniqueActorService _uniqueActors;

    public AuraBindingProducer(RpgStore store, UniqueActorService uniqueActors)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _uniqueActors = uniqueActors ?? throw new ArgumentNullException(nameof(uniqueActors));
    }

    /// <summary>
    /// Reconciles the durable `aura`-sourced bindings for one player to match
    /// <paramref name="activeAuraIds"/> — the runtime's own post-eviction set, never re-derived here
    /// (§3.2: declarative, not incremental). Returns the refusals <see cref="RpgStore.ProduceAndBind"/>
    /// reported for any aura it could not bind (e.g. AU2's container not authored yet) — never thrown,
    /// so one aura's content gap cannot fail every other aura's reconcile in the same call.
    ///
    /// <para>Triggers <see cref="UniqueActorService.PushAtomUnionAsync"/> (G1's fix) exactly when a
    /// durable row actually changed — a no-op reconcile (the idempotent "enable the same aura twice"
    /// case) writes nothing and pushes nothing.</para>
    /// </summary>
    public async Task<IReadOnlyList<AtomRejection>> SyncAsync(long playerId, IReadOnlyList<string> activeAuraIds)
    {
        var owner = new OwnerScope(OwnerKind.Player, playerId.ToString(CultureInfo.InvariantCulture));

        var existing = new List<AuraBoundRow>();
        foreach (var binding in _store.ListBindings(owner))
        {
            if (!string.Equals(binding.Source, Source, StringComparison.Ordinal)) continue;

            var instance = _store.GetInstance(binding.InstanceId);
            var auraId = instance is null ? null : AuraContentCatalog.AuraIdForContainer(instance.ContainerId);
            if (auraId is null) continue; // not one of our containers -- never touch it

            existing.Add(new AuraBoundRow(auraId, binding.BindingId));
        }

        var plan = AuraBindingPlan.Compute(activeAuraIds, existing);

        var changed = false;
        foreach (var bindingId in plan.ToWithdraw)
            changed |= _store.Withdraw(bindingId);

        var refusals = new List<AtomRejection>();
        foreach (var auraId in plan.ToBind)
        {
            var containerId = AuraContentCatalog.ContainerId(auraId);
            var container = _store.GetContainer(containerId);
            if (container is null)
            {
                // AU2's content not authored for this aura yet -- refuse honestly rather than fabricate
                // a row. No partial state is left behind (nothing was written for this aura at all).
                refusals.Add(AtomRejection.Fail(AtomRejectionReason.UnknownContainer,
                    $"no container '{containerId}' for aura '{auraId}'"));
                continue;
            }

            // §3.4: rolls happen at instantiate, never at bind (E6). Pinned at Θc=20
            // (PowerTuning.FixedPinIndex) so ContentScale.Apply is the identity transform on every
            // authored value -- AU2's containers carry their final milli values already, and this
            // producer must not apply a second, unrelated scale on top of them.
            const int pinThetaContent = 20;

            var result = _store.ProduceAndBind(
                container,
                domainMembers: static _ => Array.Empty<string>(), // aura containers draw no pool -- fixed content only
                rollSeed: 0, // fixed content, no OnInstantiate range to seed
                thetaContent: pinThetaContent,
                tuning: PowerTuningHub.Tuning,
                owner: owner,
                slot: null,
                priority: 0,
                source: Source,
                out _, out _,
                origin: InstanceOrigin.Grant);

            if (!result.IsOk) refusals.Add(result);
            else changed = true;
        }

        if (changed)
            await _uniqueActors.PushAtomUnionAsync(playerId).ConfigureAwait(false);

        return refusals;
    }
}
