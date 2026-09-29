using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Cost;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle.Board;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `siege-construction` 15.3b (2026-09-06) — real content for the four acquisition paths,
/// plus the mechanism that actually FIRES it. This module is the first real, non-`BasicAttack` caller
/// of the whole action→atom activation pipeline anywhere in this codebase's history — a severe,
/// previously-undiscovered gap found while building this: `BattleRunState.BindContainers` grants an
/// actor's held-action containers PERMANENTLY at battle setup, but nothing anywhere ever fires a
/// committed action's own `OnActivate` atoms except the hardcoded `BasicAttack.cs` special case
/// (confirmed by exhaustive grep: `Bag.OnEvent(` has exactly two production call sites, both in
/// `BasicAttack.cs`, and every production caller of `BattleEngine.Resolve` omits `containerResolver`
/// entirely — a real container-bearing action would throw at `BattleRunState`'s own constructor).
/// **That gap is the whole action program's own foundational scope, not this module's** — this file
/// deliberately does not attempt a general fix. Instead, exactly like `BasicAttack.cs` itself, it is a
/// second, narrow, hardcoded activation path — <see cref="ConstructionActivation.Fire"/> — scoped to
/// construction only.
///
/// <para><b>Content scope, stated rather than assumed</b>: one real structure (`moat`,
/// `StructureCatalog.cs`) is buildable via all four paths. A fuller siege roster is
/// `structure-corpus`'s (module 24) own job — this module proves the mechanism, not a content roster.</para>
/// </summary>
public static class ConstructionActions
{
    public const string BuiltActionId = "action.siege.construct-built";
    public const string AssembledActionId = "action.siege.construct-assembled";
    public const string SummonedActionId = "action.siege.construct-summoned";
    public const string LabouredActionId = "action.siege.construct-laboured";

    public const string BuiltContainerId = "container.siege.construct-built";
    public const string AssembledContainerId = "container.siege.construct-assembled";
    public const string SummonedContainerId = "container.siege.construct-summoned";
    public const string LabouredContainerId = "container.siege.construct-laboured";

    /// <summary>`Assembled`'s own item precondition — a placeholder id (`structure-corpus`'s own job to
    /// author a real, craftable consumable for). Named so a future item-catalog pass finds it by
    /// searching for the id, not by re-deriving which action needs one.</summary>
    public const string AssembledConsumableItemId = "item.siege.rampart-kit";

    // Declared BEFORE CompiledEffects on purpose: C# runs static field/property initializers in
    // TEXTUAL order, and CompiledEffects's own initializer (BuildEffects) reads this map via
    // RemapEffectId — declaring it after produced a real NullReferenceException at class-load time
    // (caught by this file's own tests, not by inspection), the classic static-init-order trap.
    static readonly Dictionary<string, string> AtomIdToEffectId = new(StringComparer.Ordinal)
    {
        ["atom.siege-construct-built.t1"] = EffectIdFor(BuiltActionId),
        ["atom.siege-construct-assembled.t1"] = EffectIdFor(AssembledActionId),
        ["atom.siege-construct-summoned.t1"] = EffectIdFor(SummonedActionId),
        ["atom.siege-construct-laboured.t1"] = EffectIdFor(LabouredActionId),
    };

    /// <summary>
    /// The four `structure.place` atoms, compiled through the REAL `AtomCompiler`/`AtomPushCodec`
    /// pipeline (the same one every other atom in the game goes through) rather than hand-built
    /// `EffectDef`s that could silently drift from what real content authoring produces.
    /// </summary>
    public static IReadOnlyList<EffectDef> CompiledEffects { get; } = BuildEffects();

    /// <summary>The four containers, each holding exactly one of <see cref="CompiledEffects"/> — the
    /// minimal, in-memory resolver `IContainerEffectResolver`'s own doc comment names as the sanctioned
    /// shape ("tests construct one directly, same as `ActionCatalog` today").</summary>
    public static IContainerEffectResolver ContainerResolver { get; } = new DictionaryContainerEffectResolver(
        new Dictionary<string, IReadOnlyList<string>>
        {
            [BuiltContainerId] = new[] { EffectIdFor(BuiltActionId) },
            [AssembledContainerId] = new[] { EffectIdFor(AssembledActionId) },
            [SummonedContainerId] = new[] { EffectIdFor(SummonedActionId) },
            [LabouredContainerId] = new[] { EffectIdFor(LabouredActionId) },
        });

    /// <summary>The four action rows — `ActionCategory.Support` for all, `Summon`/`Construct` tags per
    /// decision (§7's correction, already authored on `ActionTag`/`ActionTagPreference` this session).
    /// `Rung` 0 / no `RungBand`: a siege-time construction action has no rung-budget concept the way a
    /// combat skill does.</summary>
    public static IReadOnlyList<ActionRow> Actions { get; } = new[]
    {
        new ActionRow
        {
            ActionId = BuiltActionId, Name = "Construct (Built)", Kind = ActionKind.Skill,
            ContainerId = BuiltContainerId, Category = ActionCategory.Support,
            Tags = new[] { ActionTag.Construct }, Targeting = new ActionTargetSpec { Mode = ActionTargetMode.Single },
        },
        new ActionRow
        {
            ActionId = AssembledActionId, Name = "Construct (Assembled)", Kind = ActionKind.Skill,
            ContainerId = AssembledContainerId, Category = ActionCategory.Support,
            Tags = new[] { ActionTag.Construct }, Targeting = new ActionTargetSpec { Mode = ActionTargetMode.Single },
        },
        new ActionRow
        {
            ActionId = SummonedActionId, Name = "Construct (Summoned)", Kind = ActionKind.Skill,
            ContainerId = SummonedContainerId, Category = ActionCategory.Support,
            Tags = new[] { ActionTag.Summon }, Targeting = new ActionTargetSpec { Mode = ActionTargetMode.Single },
        },
        new ActionRow
        {
            ActionId = LabouredActionId, Name = "Construct (Laboured)", Kind = ActionKind.Skill,
            ContainerId = LabouredContainerId, Category = ActionCategory.Support,
            Tags = new[] { ActionTag.Construct }, Targeting = new ActionTargetSpec { Mode = ActionTargetMode.Single },
        },
    };

    /// <summary>Summoned (`qi`) and Laboured (`stamina`+`hunger`) both fit the EXISTING
    /// `ActionCostRow`/`CostLedger` mechanism directly — decision 27's own framing ("actor resource,
    /// not empire"). `Assembled`'s item cost is a SEPARATE mechanism (`StockLedger`, see
    /// <see cref="AssembledConsumableItemId"/>), and `Built`'s `Rubble`/`Ironwork` cost is spent from a
    /// WORLD-scoped resource no `ActionCostRow` can express at all — <see cref="ConstructionActivation"/>
    /// gates that one directly, by design, not by inventing a fourth `ActionCostRow` resource kind for
    /// a cost source nothing else in this codebase's cost system has ever needed.</summary>
    public static IReadOnlyList<ActionCostRow> Costs { get; } = new[]
    {
        new ActionCostRow(SummonedActionId, "qi", ValueSpec.Of(SiegeTuningPolicy.Construction.SummonQiCost), ActionCostTiming.OnCommit),
        new ActionCostRow(LabouredActionId, "stamina", ValueSpec.Of(SiegeTuningPolicy.Construction.LabourMoatStaminaCost), ActionCostTiming.OnCommit),
        new ActionCostRow(LabouredActionId, "hunger", ValueSpec.Of(SiegeTuningPolicy.Construction.LabourMoatHungerCost), ActionCostTiming.OnCommit),
    };

    /// <summary>
    /// base-defense `siege-construction`/`siege-ai` (2026-09-07, MAJOR finding this session):
    /// `DistrictAssaultResolver.BuildAnimateSetups` never set any legion member's `EquippedActionIds`,
    /// so no real siege actor could EVER hold a construction action — this is the pre-compiled form
    /// `BattleActorSetup.AdditionalHeldActions` needs to fix that, WITHOUT touching that risky, shared
    /// `EquippedActionIds`/`ActionCatalog` resolution path at all (an actor that gains these never
    /// loses its basic attack, since this is a purely additive sibling field).
    ///
    /// <para><b>Compiled once, via the REAL `ActionCompiler.Compile` pipeline — never hand-built</b>,
    /// so cost/scope/envelope derivation stays byte-identical to how every other authored action in
    /// the game compiles (no second, silently-diverging implementation of that logic). Every row's
    /// `Rung` (authored `0`, "no rung-budget concept the way a combat skill does" per this class's own
    /// top comment) is overridden to `1` against a dedicated, single-row `RungTable` — `RungTable`'s
    /// own indexing is 1-based (index 0 == rung 1, confirmed by reading `RungTable.cs` directly), so a
    /// `Rung: 0` row cannot resolve against ANY table, real or fake. The SAME override + throwaway
    /// table shape `ConstructionLiveWiringTests.BuiltOnlyCatalog` already proved safe for `Built`
    /// alone; this widens it to all four rows, sharing this file's own already-declared `Actions`/
    /// `Costs`/`ContainerResolver`, never a second copy of any of them.</para>
    ///
    /// <para>Throws at first use (a static `Lazy`, evaluated once) rather than returning a partial or
    /// null list — matching <see cref="CompiledEffects"/>'s own established "compile-time content
    /// error is loud, not silently degraded" posture in this exact file.</para>
    /// </summary>
    public static IReadOnlyList<CompiledAction> CompiledActionsForGrant => _compiledActionsForGrant.Value;

    static readonly Lazy<IReadOnlyList<CompiledAction>> _compiledActionsForGrant = new(() =>
    {
        var rungTable = new RungTable(cap: 1, new[]
        {
            new RungRow(1, MinTier: 1, MaxTier: 1, PoolRolls: 1, QPowerMilli: 1000, CostMulti: 1000, CdMulti: 1000, StructureBudget: Array.Empty<string>()),
        });

        var compiled = new List<CompiledAction>(Actions.Count);
        foreach (var row in Actions)
        {
            var costs = Costs.Where(c => c.ActionId == row.ActionId).ToList();
            var containerAtomIds = ContainerResolver.EffectIdsFor(row.ContainerId);
            var (rejection, action) = ActionCompiler.Compile(
                row with { Rung = 1 }, costs, Array.Empty<ActionScopeRow>(), containerAtomIds,
                boardAvailable: true, rungTable);
            if (action is null)
                throw new InvalidOperationException(
                    $"ConstructionActions: '{row.ActionId}' failed to compile for live grant — {rejection}");
            compiled.Add(action);
        }
        return compiled;
    });

    static string EffectIdFor(string actionId) => "fx." + actionId;

    static IReadOnlyList<EffectDef> BuildEffects()
    {
        var atoms = new[]
        {
            AtomFor(BuiltActionId, "siege-construct-built", instant: false),
            AtomFor(AssembledActionId, "siege-construct-assembled", instant: true),
            AtomFor(SummonedActionId, "siege-construct-summoned", instant: true),
            AtomFor(LabouredActionId, "siege-construct-laboured", instant: false),
        };

        var compiled = AtomCompiler.Compile(atoms, RuntimeId.Battle, catalogRevision: 1);
        if (compiled.Rejected.Count > 0)
            throw new InvalidOperationException(
                "ConstructionActions: a structure.place atom was rejected at compile time — " +
                string.Join("; ", compiled.Rejected.Select(r => $"{r.AtomId}: {r.Reason} ({r.Detail})")));

        // AtomCompiler groups by ICD key (defaulting to AtomId, so each atom here is its own group) and
        // names the resulting def's EffectId after that same key — override to the action-keyed id our
        // resolver maps to, matching EffectIdFor exactly, rather than letting the atom id leak through.
        var defs = new List<EffectDef>();
        foreach (var dto in compiled.Defs)
        {
            dto.EffectId = RemapEffectId(dto.EffectId);
            defs.Add(AtomPushCodec.ToDef(dto));
        }
        return defs;
    }

    static string RemapEffectId(string compiledId) =>
        AtomIdToEffectId.TryGetValue(compiledId, out var mapped) ? mapped : compiledId;

    static AtomRow AtomFor(string actionId, string family, bool instant) => new()
    {
        AtomId = $"atom.{family}.t1",
        KindId = "structure.place",
        FamilyId = $"atom.{family}",
        Tier = 1,
        Name = actionId,
        WhenJson = "{\"trigger\":\"OnActivate\"}",
        ParamsJson = $"{{\"structureId\":\"moat\",\"instant\":{(instant ? "true" : "false")}}}",
    };
}

/// <summary>
/// The second hardcoded activation path this codebase has (the first is `BasicAttack.cs`). Fires a
/// construction action's own `structure.place` atom directly — it does not go through the generic
/// container-grant/OnActivate broadcast an actor's OTHER held actions would also react to, because
/// that generic path does not exist yet (see this file's own top-level doc comment). Scoped
/// deliberately: this fires exactly one action's own effect, for exactly one actor, at exactly one
/// cell — it is not a general "activate any action" mechanism.
/// </summary>
public static class ConstructionActivation
{
    /// <summary>
    /// Fires the given construction action's `structure.place` atom for `actorPtr` at `targetCell`.
    /// The actor must already have the action's container GRANTED (via the existing
    /// `BattleRunState.BindContainers`, which needs `ConstructionActions.ContainerResolver` supplied to
    /// `BattleEngine.Resolve` and `ConstructionActions.CompiledEffects` upserted into the host's own
    /// `EffectBag.Catalog` — both wired in `DistrictAssaultResolver`) or this call is a silent no-op:
    /// `Bag.OnEvent` fires whatever is granted for the trigger, and fires nothing if nothing is.
    ///
    /// <para><b><paramref name="firingAction"/>/<paramref name="stockLedger"/> (2026-09-07, resolved
    /// after re-investigating `Assembled`'s own remaining gap): both OPTIONAL, and omitting either
    /// reproduces the original no-commit behavior byte-for-byte</b> — `Built`'s own live call site
    /// (`BasicAttack.cs`) omits both deliberately, since `Built`'s cost is the separate
    /// `ConstructionCost`/sector-Rubble mechanism, not `holdsStock`.</para>
    ///
    /// <para>Supplying <paramref name="firingAction"/> spends its compiled `StockDemands` via
    /// <see cref="ActionStockCommit.TryCommit"/> BEFORE the event fires — the commit-time half
    /// `Assembled`'s own item precondition needs, matching this class's own doc comment ("at commit,
    /// not at landing"). Every action shipped today (`Built`/`Assembled`/`Summoned`/`Laboured`) has
    /// ZERO compiled `StockDemands` (no `holdsStock` condition is authored on any of them yet), so this
    /// is currently inert for all four real paths — the wiring is real and tested (with a synthetic
    /// demand), the content is not. A missing <paramref name="stockLedger"/> falls back to
    /// <see cref="NoStockLedger"/> (refuses, never grants free stock), matching every other seam's own
    /// "unwired means safe, not permissive" posture.</para>
    ///
    /// <para>A failed commit (the `holdsStock` precondition was true when usability was checked but is
    /// no longer true at this exact commit tick — a genuine, rare TOCTOU race, not the normal path)
    /// is the SAME silent no-op this function already has for an ungranted container: nothing fires,
    /// nothing throws. Still genuinely un-started, separate from this wiring: no live `IIntentSource`
    /// yet decides to USE `Assembled` at all (`BasicAttack.cs`'s own `TryDeclareBuilt` hook is
    /// deliberately scoped to `Built` only) — that needs its own cell-choosing policy, analogous to
    /// `ConstructionAi.ChooseBuiltSite`, which cannot be exercised meaningfully until the item itself
    /// exists regardless.</para>
    /// </summary>
    public static void Fire(
        BattleEffectHost host, string actorPtr, int targetRow, int targetCol, long tick = 0,
        CompiledAction? firingAction = null, IStockLedger? stockLedger = null)
    {
        if (host is null) throw new ArgumentNullException(nameof(host));
        if (string.IsNullOrEmpty(actorPtr)) throw new ArgumentException("actorPtr must be set.", nameof(actorPtr));

        if (firingAction is not null &&
            !new ActionStockCommit(stockLedger ?? NoStockLedger.Instance).TryCommit(actorPtr, firingAction).IsSpent)
            return; // precondition no longer true at commit -- silent no-op, same posture as "nothing granted"

        host.Bag.OnEvent(new EffectEventDto
        {
            Trigger = AtomTriggers.OnActivate,
            ActorPtr = actorPtr,
            TargetRow = targetRow,
            TargetCol = targetCol,
            Tick = tick,
        });
    }
}

/// <summary>
/// `Built`'s own cost gate — the ONE acquisition path that spends a WORLD-scoped resource
/// (`WorldSector.RubbleStock`/`IronworkStock`) rather than an actor resource pool or a carried item.
/// Confirmed (2026-09-06) that no existing mechanism can express this: `ActionCostRow.ResourceId` only
/// resolves against the closed six actor resources (`ActorResourcePools.IndexOf` throws for anything
/// else), and `IStockLedger` is actor-inventory-scoped, not sector-scoped. Rather than inventing a
/// fourth `ActionCostRow` resource KIND for a cost source nothing else in the whole cost system has
/// ever needed, this is a small, dedicated, pure gate — the SAME shape
/// <see cref="ConstructionPlacement.CanPlace"/> already established for the shared placement rule: a
/// pure validator plus a pure mutator, called directly by whichever caller commits `Built` (siege-ai's
/// own future intent source), never routed through `CostLedger`.
/// </summary>
public static class ConstructionCost
{
    /// <summary>True when the sector can afford the structure's own `ConstructRubbleCost`/
    /// `ConstructIronworkCost` (`StructureCatalog`). Never negative inputs — a caller passing a
    /// negative stock or cost has a bug upstream this gate should not paper over.</summary>
    public static bool CanAffordBuilt(long sectorRubble, long sectorIronwork, StructureDef def)
    {
        if (sectorRubble < 0) throw new ArgumentOutOfRangeException(nameof(sectorRubble));
        if (sectorIronwork < 0) throw new ArgumentOutOfRangeException(nameof(sectorIronwork));
        if (def is null) throw new ArgumentNullException(nameof(def));
        return sectorRubble >= def.ConstructRubbleCost && sectorIronwork >= def.ConstructIronworkCost;
    }

    /// <summary>Debits the structure's own cost from the sector's stocks. Throws — never clamps to
    /// zero — if the caller did not check <see cref="CanAffordBuilt"/> first, matching
    /// `SiegeDepot.SpendLoam`/`SpendIronwork`'s own "spending past the balance is a caller bug, not a
    /// silent floor" precedent.</summary>
    public static (long Rubble, long Ironwork) SpendBuilt(long sectorRubble, long sectorIronwork, StructureDef def)
    {
        if (!CanAffordBuilt(sectorRubble, sectorIronwork, def))
            throw new InvalidOperationException(
                $"ConstructionCost: cannot afford '{def.StructureId}' (needs {def.ConstructRubbleCost} rubble / " +
                $"{def.ConstructIronworkCost} ironwork against a balance of {sectorRubble} / {sectorIronwork}).");
        return (checked(sectorRubble - def.ConstructRubbleCost), checked(sectorIronwork - def.ConstructIronworkCost));
    }
}
