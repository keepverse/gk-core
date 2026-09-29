using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Items.Drops;

/// <summary>
/// The two lookups <see cref="LootMintAt.Mint"/> needs beyond the grant and Θ_content themselves — the
/// atom/affix catalog (<see cref="Instantiator.TryInstantiate"/>'s own two delegates, unchanged), the
/// Equipment-kind pool source (<see cref="EquipmentContainerLookups"/>), and the Unique/Relic-kind container
/// resolver (a Data-layer read, `RpgStore.GetContainer`, injected as a delegate so this file stays
/// I/O-free — the same "read model owned elsewhere" idiom
/// <see cref="FusionRpg.Core.Items.Drops.LootContentView.UniqueRarityFor"/> already established for the
/// identical fact). Either optional lookup left `null` means the host does not support that kind today
/// — refused by name, never a null-reference crash.
/// </summary>
public sealed record LootMintLookups(
    Func<string, AtomRow?> LookupAtom,
    Func<string, AffixRow?> LookupAffix,
    EquipmentContainerLookups? Equipment,
    Func<string, ContainerRow?>? ContainerFor);

/// <summary>
/// The real, missing implementation behind `LootContentView.Mint` / `DelveLoot.RollRoom`'s and
/// `.RollQuestReward`'s own `mintAt: Func&lt;LootGrant, int, LootMintResult&gt;` parameter
/// (party-dungeon-todo.md D4.12/D3.11/D3.3-D3.5, the shared blocker cross-referenced under
/// `[[loot-content-view-unwired]]`).
///
/// <para><b>Three of ten <see cref="DropEntryKind"/> members are built: <see cref="DropEntryKind.Equipment"/>,
/// <see cref="DropEntryKind.Unique"/> and <see cref="DropEntryKind.Relic"/></b> — Equipment and Unique
/// are the only two any real, already-blocked party-dungeon task needs today (an ordinary room/quest
/// drop is Equipment; a boss first-clear relic is Unique), and Relic is empire-development Task 1.3a's
/// own addition (spec-relic-item-kind.md §Design 3): it instantiates straight from its own relic
/// container — fixed core and optional pool, never a base type, role, frame or rarity draw.
/// Every other kind refuses BY NAME (`drop.mint-kind-unsupported`, naming the kind in the message) —
/// never a silent mishandle, never a guessed minting mechanic for a kind that belongs to whichever
/// program eventually builds the general dispatcher (general drop-volume/the armoury, D26, is
/// explicitly out of THIS program's own scope — `tasks/party-dungeon-plan.md` §8; confirmed by reading
/// `docs/architecture/item/spec-drop-volume.md`'s own D26 ruling directly: "the item system balances
/// items, not the game").</para>
///
/// <para><b>Traced against the real, unmodified `LootPipeline.Resolve` (not assumed):</b> today, only
/// its own `MintEquipment`/`MintUnique` inner functions ever call `view.Mint` at all (`MintRelic`
/// calls `view.MintRelic` instead) — every other
/// entry kind either never reaches a grant (`Table` recurses, `Nothing` is skipped, `Insert`/`Charm`/
/// `Consumable` are refused earlier at `DropTableDraw.IsAvailable`) or is added to `manifest.Grants`
/// directly, with no instance and no `Mint` call (`Material`/`Currency`). So the seven-kind refusal
/// below is a defensive completeness property of THIS function's own public contract — its signature
/// accepts any `LootGrant` regardless of kind, matching `DelveLoot.RollRoom`'s own general
/// `Func&lt;LootGrant, int, LootMintResult&gt;` parameter shape — not a path the real pipeline can
/// reach today. Both are worth having: the pipeline's own shape today, and a `mintAt` that never
/// mishandles a kind if that shape ever changes or a caller invokes it directly.</para>
///
/// <para>Pure, no I/O — this is Core-layer by design: both `ContainerFor` (Unique/Relic) and `Equipment`
/// (the derived role-family table) are caller-supplied, exactly like every other Data-layer fact this
/// whole pipeline already reads through a delegate. The REAL, callable `mintAt` a live server plugs
/// into `RollRoom`/`RollQuestReward` composes this with a Data-layer resolver AND the one piece of
/// persistence this function itself never performs — `RpgStore.MintGrant`/`.MintGrantUnlocked` calls
/// `SaveInstanceUnlocked` on the result, never `AcquireItemUnlocked`/`PersistLootUnlocked`, which stay
/// the CALLER's own job per this session's established `Unlocked`-composition convention. That
/// composition is necessarily Data-layer, not Core: `FusionRpg.Core` carries no `ProjectReference` to
/// `FusionRpg.Data` (confirmed directly — the dependency runs the other way), so nothing in this file
/// could call `GetContainer` itself even if it wanted to.</para>
/// </summary>
public static class LootMintAt
{
    static LootMintAt() => ContentRuleNamespaces.Register("drop");

    public static AtomRejection Mint(
        LootGrant grant, int thetaContent, LootMintLookups lookups, PowerTuning tuning,
        out InstanceRow? instance,
        InstanceOrigin origin = InstanceOrigin.Drop, long catalogRevision = 0)
    {
        instance = null;
        if (grant is null) throw new ArgumentNullException(nameof(grant));
        if (lookups is null) throw new ArgumentNullException(nameof(lookups));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        switch (grant.Kind)
        {
            case DropEntryKind.Equipment:
            {
                if (lookups.Equipment is not { } eq)
                    return AtomRejection.ContentRule("drop.mint-kind-unsupported",
                        $"grant {grant.Index} is Equipment but the host supplies no EquipmentContainerLookups");

                var built = EquipmentContainerBuild.From(grant, eq);
                return Instantiator.TryInstantiate(
                    built.Container, lookups.LookupAtom,
                    id => built.Affixes.TryGetValue(id, out var a) ? a : lookups.LookupAffix(id),
                    unchecked((long)grant.RollSeed), thetaContent, tuning, out instance, origin, catalogRevision);
            }

            case DropEntryKind.Unique:
            {
                if (lookups.ContainerFor is not { } containerFor)
                    return AtomRejection.ContentRule("drop.mint-kind-unsupported",
                        $"grant {grant.Index} is Unique but the host supplies no ContainerFor resolver");

                var container = containerFor(grant.RefId);
                if (container is null)
                    return AtomRejection.ContentRule("drop.unknown-unique",
                        $"unique container '{grant.RefId}' does not exist");

                return Instantiator.TryInstantiate(
                    container, lookups.LookupAtom, lookups.LookupAffix,
                    unchecked((long)grant.RollSeed), thetaContent, tuning, out instance, origin, catalogRevision);
            }

            case DropEntryKind.Relic:
            {
                // Empire-development Task 1.3a (spec-relic-item-kind.md §Design 3): a relic
                // instantiates straight from its own relic container — fixed core and optional pool
                // (many relics carry zero atoms and exist purely as Wonder-build cost tokens), never
                // a base type, role, frame or rarity draw. Same TryInstantiate path Unique uses; the
                // "never item_generation" half of the contract is the Data-layer host's job
                // (RpgStore.MintRelicUnlocked writes rpg_item only), not this pure function's.
                if (lookups.ContainerFor is not { } relicContainerFor)
                    return AtomRejection.ContentRule("drop.mint-kind-unsupported",
                        $"grant {grant.Index} is Relic but the host supplies no ContainerFor resolver");

                var relicContainer = relicContainerFor(grant.RefId);
                if (relicContainer is null)
                    return AtomRejection.ContentRule("drop.unknown-relic",
                        $"relic container '{grant.RefId}' does not exist");

                return Instantiator.TryInstantiate(
                    relicContainer, lookups.LookupAtom, lookups.LookupAffix,
                    unchecked((long)grant.RollSeed), thetaContent, tuning, out instance, origin, catalogRevision);
            }

            default:
                return AtomRejection.ContentRule("drop.mint-kind-unsupported",
                    $"grant {grant.Index} is '{grant.Kind}' — mintAt supports only Equipment, Unique and Relic today; " +
                    "the general drop-volume/armoury dispatcher (D26) is a separate, unbuilt program");
        }
    }
}
