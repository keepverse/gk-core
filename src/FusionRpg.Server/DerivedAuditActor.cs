using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.PassiveTree.State;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// Seeds a playable UniqueActor whose Hub producers fill the sheet for derived audit + e2e.
/// Never fabricates sheet channel rows — only durable inputs Hub already consumes.
/// </summary>
public static class DerivedAuditActor
{
    public const string InstanceId = "derived-audit";
    public const int TypeId = 9;
    /// <summary>Debug seed specimen progression index — structural fixture, not a balance dial.</summary>
    public const long AuditSeedSpecimenLevel = 80;
    /// <summary>Equal share units per aptitude for Hub producer coverage — debug seed only.</summary>
    const long AuditSeedShareUnits = 120;

    /// <summary>Representative channels bound via equip <c>stat.derived</c> (not the no-producer arms).</summary>
    static readonly (string Channel, long Amount)[] EquipChannelSeeds =
    {
        (DerivedStatChannels.CombatPowerOmni, 250),
        (DerivedStatChannels.CombatPowerFire, 180),
        (DerivedStatChannels.CombatCritRateOmni, 40),
        (DerivedStatChannels.CombatDodgeOmni, 35),
        (DerivedStatChannels.CombatShieldCapacityOmni, 400),
        (DerivedStatChannels.ResourceMax("stamina"), 90),
        (DerivedStatChannels.ResourceRegen("qi"), 12),
        (DerivedStatChannels.ProgressionBonusMaxHp, 500),
        // No `progression.bonus.atk` seed: the channel was retired 2026-09-18 (solid-enforcement
        // `retire-atk` R4) and is not registered, so binding it here would audit a channel nothing
        // reads. The seed's other three rows are unchanged.
        (DerivedStatChannels.ProgressionBonusDefense, 25),
    };

    public static object Seed(RpgStore store, long? playerId = null)
    {
        var pid = playerId ?? store.GetCurrentPlayerId();
        var actor = store.EnsureUniqueActorForAudit(pid, InstanceId, "plant", TypeId, AuditSeedSpecimenLevel);

        var commander = BroadAllocation(AllocationScope.Commander);
        var unique = BroadAllocation(AllocationScope.UniqueCreature);
        store.SaveAllocation(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(pid), commander);
        store.SaveAllocation(AllocationScope.UniqueCreature, actor.InstanceId, unique);

        BindMultiChannelEquip(store, actor.InstanceId);
        TrySeedTreeNodes(store, pid);

        return new { instanceId = actor.InstanceId, playerId = pid, level = actor.Level };
    }

    public static DerivedAuditCoverageReport Coverage(RpgStore store, string? instanceId = null)
    {
        var id = string.IsNullOrWhiteSpace(instanceId) ? InstanceId : instanceId.Trim();
        var actor = store.GetUniqueActor(id)
            ?? throw new InvalidOperationException($"unique actor '{id}' not found — POST /api/debug/derived-audit-actor first");

        DerivedSurfaceDto cook;
        try { cook = DerivedSurfaceCook.Build("en", actor.Side); }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException("derived surface catalogs not configured on this host");
        }

        var sheet = UniqueActorHubCompose.ProjectSheet(store, actor);
        var treeAtoms = TreeBoundAtoms.ForPlayer(
            store,
            new Power.ServerPowerIndexProvider(store, PowerTuningHub.Tuning),
            actor.PlayerId);
        var registry = DerivedStatRegistry.CreateDefault();
        return DerivedAuditCoverage.Build(cook, registry, sheet, treeAtoms.Count > 0);
    }

    public static string CoverageJson(RpgStore store, string? instanceId = null, bool writeArtifact = false, string? artifactPath = null)
    {
        var report = Coverage(store, instanceId);
        var payload = new
        {
            instanceId = string.IsNullOrWhiteSpace(instanceId) ? InstanceId : instanceId.Trim(),
            registryCount = report.RegistryCount,
            cookExpandCount = report.CookExpandCount,
            sheetCount = report.SheetCount,
            presentCount = report.PresentCount,
            touchedCount = report.TouchedCount,
            treeAtomsPresent = report.TreeAtomsPresent,
            present = report.Present,
            touched = report.Touched,
            missingCook = report.MissingCook,
            missingRegistry = report.MissingRegistry,
            cookExpandIds = report.CookExpandIds,
            registryIds = report.RegistryIds,
            sheetIds = report.SheetIds,
            classified = new
            {
                expectedStatusSession = report.Classified.ExpectedStatusSession,
                expectedNoProducer = report.Classified.ExpectedNoProducer,
                expectedInjectorTreeGap = report.Classified.ExpectedInjectorTreeGap,
                gapUnwired = report.Classified.GapUnwired
            },
            gap = new { unwired = report.Classified.GapUnwired }
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        if (writeArtifact && !string.IsNullOrWhiteSpace(artifactPath))
        {
            var dir = Path.GetDirectoryName(artifactPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(artifactPath, json);
        }
        return json;
    }

    static AptitudeAllocation BroadAllocation(AllocationScope scope)
    {
        var acc = AptitudeAllocation.Empty;
        foreach (var apt in AptitudeCatalog.All)
            acc += AptitudeAllocation.Single(scope, apt.Id, AuditSeedShareUnits);
        return acc;
    }

    static void BindMultiChannelEquip(RpgStore store, string specimenId)
    {
        var atomIds = new List<string>();
        for (var i = 0; i < EquipChannelSeeds.Length; i++)
        {
            var (channel, amount) = EquipChannelSeeds[i];
            var atomId = $"atom.derived-audit-equip.c{i + 1}.t1";
            var upsert = store.UpsertAtom(new AtomRow
            {
                AtomId = atomId,
                KindId = "stat.derived",
                FamilyId = "atom.derived-audit-equip",
                Variant = $"c{i + 1}",
                Tier = 1,
                Name = $"Derived Audit Equip {i + 1}",
                ParamsJson = $"{{\"channel\":\"{channel}\",\"op\":\"flat\",\"amount\":{amount}}}",
            });
            if (!upsert.IsOk)
                throw new InvalidOperationException($"UpsertAtom failed for {atomId}: {upsert}");
            atomIds.Add(atomId);
        }

        var containerId = "item.derived-audit-equip";
        var atoms = atomIds.Select((id, idx) => new ContainerAtomRow(idx + 1, id)).ToArray();
        var containerUpsert = store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Item,
            Atoms = atoms,
        });
        if (!containerUpsert.IsOk)
            throw new InvalidOperationException($"UpsertContainer failed: {containerUpsert}");

        var tuning = PowerTuningHub.Tuning;
        var owner = new OwnerScope(OwnerKind.UniqueActor, specimenId);
        var role = ItemRoles.Id(ItemRole.ArmamentPrimary);
        const string itemRef = "item-derived-audit-equip";
        var produce = store.ProduceAndBind(
            store.GetContainer(containerId)!,
            _ => Array.Empty<string>(),
            rollSeed: 42,
            thetaContent: 40,
            tuning,
            owner,
            slot: role,
            priority: 0,
            source: "derived-audit",
            out _,
            out _);
        if (!produce.IsOk)
            throw new InvalidOperationException($"ProduceAndBind failed: {produce}");

        store.SaveAssignment(specimenId, ItemRole.ArmamentPrimary, "rolled", itemRef);
    }

    static void TrySeedTreeNodes(RpgStore store, long playerId)
    {
        PassiveTreeTuning tuning;
        try { tuning = PassiveTreeTuningHub.Tuning; }
        catch (InvalidOperationException) { return; }

        var trees = store.ListTreeCatalogTrees()
            .Where(t => t.Category != TreeCategory.Species)
            .ToList();
        if (trees.Count == 0) return;

        var loaded = store.LoadTreeCatalog(trees.Select(t => t.TreeId).ToList());
        var nodes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var tree in loaded)
        {
            // Prefer tier-1 magnitude nodes that open under aptitude gates we just funded.
            foreach (var node in tree.Nodes.Where(n => n.Tier <= 1).Take(2))
                nodes[node.NodeId] = Math.Max(0, nodes.GetValueOrDefault(node.NodeId));
        }

        if (nodes.Count == 0) return;
        store.SaveTreeNodeState(AllocationScope.Commander, AptitudeEndpoints.ScopeKey(playerId), nodes);
    }
}
