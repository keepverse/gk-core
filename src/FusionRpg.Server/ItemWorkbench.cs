using System.Globalization;
using System.Text;
using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Gems;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Items.Sockets;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

/// <summary>One milestone atom row as the host injects it (species-gear-chain T13): the atom id
/// plus the row's own amount range, rolled at apply time on the op's stream.</summary>
public sealed record MilestoneAtom(string AtomId, long MinAmount, long MaxAmount);

/// <summary>species-gear-chain T13: a track family the injected atom corpus does not carry. Thrown,
/// never skipped — a fabricated row would grant combat numbers nothing authored.</summary>
public sealed class MilestoneUnknownFamily : Exception
{
    public MilestoneUnknownFamily(string message) : base(message) { }
}

/// <summary>One resolved cost or yield line, as a caller renders it.</summary>
public sealed record WorkbenchCostDto(string Class, string MaterialId, long Qty);

/// <summary>
/// species-gear-chain T42 — one requested assurance spend (`assurance.assure`/`.protect`/`.repair`,
/// an id from <see cref="FusionRpg.Core.Items.Materials.MaterialCatalog.AssuranceVerbs"/>) plus how
/// many. The ONLY shape an assurance spend enters the workbench through — never a boolean flag
/// (T39's own "free ward" defect was exactly a flag with nothing paid for it).
/// </summary>
public readonly record struct AssuranceLine(string MaterialId, int Count);

/// <summary>One socket after the operation.</summary>
/// <param name="InsertName">
/// item-content `item-naming` (T4): the gem corpus's authored name for whatever sits in this cell
/// (<c>"Ember Shard"</c>), resolved through the same <c>GemInsertCorpus</c> delegate this bench
/// prices <c>socket-insert</c> against. <c>""</c> for an empty cell or a container the corpus does
/// not carry — the surface then says so rather than printing <c>gem.g1-001</c> as a name. Additive
/// with a default, so no existing construction site changes.
/// </param>
public sealed record WorkbenchSocketDto(
    int Index, string Affinity, bool Crafted, string? Insert, string InsertName = "");

/// <summary>
/// What one workbench operation did, in the shape a surface can draw without a second read: what was
/// spent, what was returned, what the item's persisted state now is, and — when the answer is no —
/// which named rule refused it.
/// </summary>
public sealed record WorkbenchOutcomeDto(
    bool Ok,
    string Verb,
    string Reason,
    string InstanceId,
    string RecipeId,
    int OpSeq,
    bool Replayed,
    string Outcome,
    int EnhanceLevel,
    int PityCounter,
    int SuccessMilli,
    IReadOnlyList<WorkbenchCostDto> Spent,
    IReadOnlyList<WorkbenchCostDto> Granted,
    IReadOnlyList<WorkbenchSocketDto> Sockets);

/// <summary>
/// species-gear-chain T37 — what an upgrade WOULD do, with nothing spent and nothing consumed: the
/// successor chassis, both implicits (the swap a card must show before the commit, spec § 2a Rule 2)
/// and the souls price. `Ok` false carries `Reason` naming the refusing rule.
/// </summary>
public sealed record WorkbenchUpgradePreviewDto(
    bool Ok,
    string Reason,
    string InstanceId,
    string SuccessorBaseTypeId,
    string OutgoingImplicitFamily,
    string IncomingImplicitFamily,
    int CarriedAffixCount,
    long SoulsCost,
    IReadOnlyList<WorkbenchCostDto> Spent);

/// <summary>
/// species-gear-chain T24 — what the executor needs to apply <b>craft wear</b>: the instance's own
/// derived <c>durability_max</c> (base type + rung ⇒ <c>DurabilityTable.DeriveMax</c>) and the
/// craft-wear rate, which is the <c>craftWearPerAttemptMilli</c> key and never battle wear's.
/// Supplied by the host exactly as <c>BaseTypeSocketMaxCorpus</c>/<c>forgeMintCells</c> are; <c>null</c>
/// keeps every craft's durability untouched, the documented no-op every unwired delegate gets here.
/// </summary>
public sealed record CraftWearSource(
    Func<string, int, long?> MaxFor,
    long WearPerAttemptMilli,
    /// <summary>
    /// species-gear-chain T61 — the item's derived POTENTIAL ceiling, from the same base type + rung the
    /// durability max comes from (`PotentialTable.DeriveMax`). Null leaves craft wear's potential gate
    /// exactly as it was before this landed: no source, no wear.
    /// </summary>
    Func<string, int, long?>? PotentialMaxFor = null);

/// <summary>
/// ⭐ <b>The workbench executor</b> — the production caller item modules 14, 15 and 16 each named as
/// their one shared blocker.
///
/// <para>All three shipped a real, tested half of one loop and none shipped the joint:
/// <c>MaterialRecipeCatalog</c>/<c>SalvagePolicy</c> price and convert but nothing debits;
/// <c>EnhancePolicy</c>/<c>RerollPolicy</c>/<c>TransferPolicy</c> decide but nothing records;
/// <c>SocketOperations</c> transitions but nothing writes. <c>TrySpendRecipe</c>,
/// <c>AppendMutationOp</c> and <c>SetSockets</c> all had zero production callers. This class calls
/// them, against a real stored item, and <see cref="RpgStore.TrySpendAndApply"/> commits the debit and
/// the write together.</para>
///
/// <para><b>Shape: ONE executor, per-verb methods.</b> That is what the three specs describe rather
/// than a choice made here. <c>spec-salvage-craft.md</c> §"The spend transaction" is a single
/// six-step transaction (<i>replay → resolve → gate → spend → <b>perform</b> → log</i>) whose step 5
/// is explicitly <i>"the owning module's mutation or mint"</i> — one pattern, a pluggable body per
/// verb. Its SC7 line says the same from the other end: <i>"adding an operation verb is code, because
/// a verb needs <b>an executor</b> and a module that owns it."</i> Parallel per-verb executors would
/// have to re-derive debit-then-act-then-persist three times, and the day the two copies disagreed
/// one of them would be spending without recording.</para>
///
/// <para>⛔ <b>The gate order is the same for every verb</b>, so a refusal costs the same nothing
/// whichever verb asked: resolve the target and its ownership → let Core decide → resolve the price →
/// spend and apply atomically. Nothing is debited before Core has said yes.</para>
///
/// <para>⚠ <b>D26 holds through this class.</b> Every cost input handed to
/// <see cref="MaterialRecipeCatalog.Resolve"/> is read off the TARGET — its rung, its item level, its
/// frame, its own <c>+n</c>. The player id reaches the store only as a balance to debit and an
/// ownership check; it never reaches a price.</para>
/// </summary>
public sealed class ItemWorkbench
{
    /// <summary>
    /// A material has no rarity rung, and <c>materials.v1.json</c> gives <c>upcycle</c> no rung leg —
    /// proven by <c>Upcycle_cost_is_invariant_across_every_rung_index</c> rather than assumed. Index 0
    /// is the ladder's own first slot, used because <see cref="RecipeContext"/> requires a value in
    /// range; it is <b>not</b> a claim that upcycling is a <c>chaff</c> operation.
    /// </summary>
    const int MaterialHasNoRung = 0;

    /// <summary>Neither the item lane nor <c>enhancement.v1.json</c> declares a rules version today, so
    /// D2 clause 5's column ships stamped with this until one exists. Structural placeholder, not a
    /// balance number.</summary>
    const int ItemRulesVersionUnset = 0;

    readonly RpgStore _store;
    readonly MaterialTuning _materials;
    readonly MaterialRecipeCatalog _recipes;
    readonly EnhancementTuning _enhancement;
    readonly SocketTuning _sockets;
    readonly Func<string, int?>? _baseTypeSocketMax;
    // species-gear-chain T24: the craft-wear inputs. null = no wear (the documented no-op).
    readonly CraftWearSource? _craftWear;
    readonly Func<string, CardInsertLookup?>? _lookupInsert;
    readonly Func<string, IReadOnlyList<FusionRpg.Core.Items.Mutation.MilestoneTrackEntry>?>? _baseTypeEnhanceTrack;
    readonly Func<string, int, MilestoneAtom?>? _milestoneAtomFor;
    readonly IReadOnlyList<FusionRpg.Core.Items.RoleFamilyCell>? _forgeMintCells;
    readonly FusionRpg.Core.Power.PowerTuning? _forgePowerTuning;

    /// <summary>species-gear-chain T37 — Rule 1's production input: affix family id to the roles that
    /// may roll it. Null refuses every upgrade by name.</summary>
    readonly IReadOnlyDictionary<string, IReadOnlySet<string>>? _affixFamilyRoles;

    /// <summary>A shared empty set: `ItemUpgradeNode`/`RoleAllowListLegality` need a non-null value and
    /// neither mutates it.</summary>
    static readonly IReadOnlySet<string> EmptyStringSet = new HashSet<string>();

    /// <summary>species-gear-chain T37 — the base-type registry's implicit family per id, when the host
    /// wired it. Null means "show no implicit", never a guess.</summary>
    readonly Func<string, string?>? _baseTypeImplicitFamily;
    readonly Func<string, FusionRpg.Core.Effects.Atoms.AtomRow?>? _lookupAtom;
    readonly Func<string, FusionRpg.Core.Effects.Atoms.AffixRow?>? _lookupAffix;
    readonly Func<string, GemSeed?>? _lookupGemSeed;
    readonly PowerTuning? _gemPowerTuning;
    // species-gear-chain T34d-wire: the species-binding inputs. null = species-less for every piece
    // (the documented no-op, same posture as every delegate above) — never a guessed default.
    readonly Func<string, FusionRpg.Core.Items.Thresholds.ContainerSpeciesLookup>? _speciesIdForContainer;
    readonly Func<string, int?>? _speciesRungIndexFor;
    readonly Func<string, IReadOnlyList<string>>? _familiesForSpecies;

    /// <param name="baseTypeSocketMax">
    /// The base type's own declared <c>socketMax</c>, by base-type id. ⚠ <b>Module 6 shipped the
    /// 740-entry corpus but no <c>item_base_type</c> table</b>, so this arrives as a delegate — the
    /// same seam <c>LootContentView.SocketMaxFor</c> already uses for step 10. <c>null</c> refuses
    /// every <c>socket-add</c> by name rather than guessing a ceiling, which is
    /// <c>LootPipeline.Sockets</c>'s own stated rule: <i>"half a socket rule would grant the wrong
    /// count, which is worse than granting none."</i>
    /// </param>
    /// <param name="lookupInsert">
    /// ⭐ <b>The gem catalog, by container id</b> — the same <see cref="GemInsertCorpus"/> delegate
    /// <see cref="ItemCardEndpoints"/> and <see cref="ItemSurfaceEndpoints"/> read. <c>socket-insert</c>
    /// used to describe the insert by its container id alone with a hardcoded <c>Element: ""</c>;
    /// the corpus carries the real element (<c>gk-data/packs/fusion/data/seed/items/gems/*.json</c>), so it is read rather
    /// than assumed. A container the corpus does not carry keeps <c>""</c> — the honest "no element",
    /// and also what a genuinely element-free insert authors (<c>SocketModel.cs:72</c>).
    /// </param>
    /// <param name="baseTypeEnhanceTrack">
    /// species-gear-chain T13: the base type's authored <c>enhanceTrack</c> ([atLevel, family]),
    /// by base-type id — read off the base-type corpus by the host, the same delegate seam
    /// <c>baseTypeSocketMax</c> uses. <c>null</c> (not supplied) means NO milestone append, and the
    /// attempt still succeeds: milestones are bonus atoms on a success, and an unwired corpus must
    /// never fail an enhance.
    /// </param>
    /// <param name="milestoneAtomFor">
    /// species-gear-chain T13: the milestone atom row for (family, tier) — atom id plus the row's
    /// amount range — read off the injected atom corpus (T12's <c>family-expand.milestones.json</c>).
    /// The lookup's contract distinguishes two absences: a family the milestone corpus never named
    /// throws <see cref="MilestoneUnknownFamily"/> (a content gap, loud, never a fabricated row),
    /// while a KNOWN family the generator never expanded (aegis, bloom, evasion, hardy, keen, quicken —
    /// no reference base for their channels; **six**, corrected 2026-09-23, this list said five) returns
    /// null, and the attempt succeeds with NO append.
    /// Failing an enhance for a generator limitation would punish the player for an authoring gap;
    /// the generator's own refusal list says why those five wait.
    /// <c>null</c> (not supplied) means NO milestone append at all, and the attempt still succeeds:
    /// milestones are bonus atoms on a success, and an unwired corpus must never fail an enhance.
    /// </param>
    public ItemWorkbench(
        RpgStore store,
        MaterialTuning materials,
        MaterialRecipeCatalog recipes,
        EnhancementTuning enhancement,
        SocketTuning sockets,
        Func<string, int?>? baseTypeSocketMax = null,
        Func<string, CardInsertLookup?>? lookupInsert = null,
        Func<string, IReadOnlyList<FusionRpg.Core.Items.Mutation.MilestoneTrackEntry>?>? baseTypeEnhanceTrack = null,
        Func<string, int, MilestoneAtom?>? milestoneAtomFor = null,
        IReadOnlyList<FusionRpg.Core.Items.RoleFamilyCell>? forgeMintCells = null,
        FusionRpg.Core.Power.PowerTuning? forgePowerTuning = null,
        Func<string, FusionRpg.Core.Effects.Atoms.AtomRow?>? lookupAtom = null,
        Func<string, FusionRpg.Core.Effects.Atoms.AffixRow?>? lookupAffix = null,
        Func<string, GemSeed?>? lookupGemSeed = null,
        PowerTuning? gemPowerTuning = null,
        CraftWearSource? craftWear = null,
        Func<string, FusionRpg.Core.Items.Thresholds.ContainerSpeciesLookup>? speciesIdForContainer = null,
        Func<string, int?>? speciesRungIndexFor = null,
        Func<string, IReadOnlyList<string>>? familiesForSpecies = null,
        /// <summary>
        /// species-gear-chain T37 — each affix family id to the roles that may roll it, from the shipped
        /// `gk-data/packs/fusion/data/seed/items/affix-families/**` corpus. Rule 1's production input
        /// (<see cref="RoleAllowListLegality"/>); NULL refuses every upgrade by name rather than letting
        /// an unevaluated legality rule pass.
        /// </summary>
        IReadOnlyDictionary<string, IReadOnlySet<string>>? affixFamilyRoles = null,
        /// <summary>
        /// species-gear-chain T37 — the base-type registry's own implicit family per id (the fact no
        /// store table carries), so the upgrade's plan can name the implicit the successor brings.
        /// NULL shows no implicit rather than inventing one.
        /// </summary>
        Func<string, string?>? baseTypeImplicitFamily = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _materials = materials ?? throw new ArgumentNullException(nameof(materials));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _enhancement = enhancement ?? throw new ArgumentNullException(nameof(enhancement));
        _sockets = sockets ?? throw new ArgumentNullException(nameof(sockets));
        _baseTypeSocketMax = baseTypeSocketMax;
        _craftWear = craftWear;
        _lookupInsert = lookupInsert;
        _baseTypeEnhanceTrack = baseTypeEnhanceTrack;
        _milestoneAtomFor = milestoneAtomFor;
        // species-gear-chain T14: forge's mint inputs. NULL (not supplied) refuses every forge with
        // the named rule below — minting without the role-family cells or the power tuning would
        // fabricate affix pools and magnitudes. No live caller supplies these yet (the same honest
        // gap RpgStore.Mint.cs names for Program.cs); tests derive both from the real corpus.
        _forgeMintCells = forgeMintCells;
        _forgePowerTuning = forgePowerTuning;
        // species-gear-chain T15: the reroll catalogs. NULL (not supplied) refuses every reroll with
        // the named rule below — redrawing against an unwired catalog would fabricate values, and
        // the atom/affix rows are content no verb may assume.
        _lookupAtom = lookupAtom;
        _lookupAffix = lookupAffix;
        // species-gear-chain T22: socket-insert's mint inputs — the gem seed (family/element/band
        // off the same corpus lookup the verb already reads, never re-derived from the tier) plus
        // the catalogs and tuning `Instantiator` needs. NULL (not supplied) refuses every insert
        // with socket.mint-unavailable: minting without them would fabricate the insert's atoms.
        // Deliberately NOT the forge pair: forge stays gated on its own cells (its named gap), and
        // sharing one tuning field would let a future forge-wiring silently change what gems mint.
        _lookupGemSeed = lookupGemSeed;
        _gemPowerTuning = gemPowerTuning;
        // species-gear-chain T34d-wire ("unwired is not done", coordinator ruling 2026-09-20).
        _speciesIdForContainer = speciesIdForContainer;
        _speciesRungIndexFor = speciesRungIndexFor;
        _familiesForSpecies = familiesForSpecies;
        _affixFamilyRoles = affixFamilyRoles;
        _baseTypeImplicitFamily = baseTypeImplicitFamily;
    }

    /// <summary>
    /// The recipe corpus this bench prices against, for the READ route that lists it
    /// (item-content T4). Exposed rather than loaded a second time in the endpoint file: a picker
    /// offering a recipe the executor does not know would refuse on click, which is worse than not
    /// offering it. Read-only — <see cref="MaterialRecipeCatalog.Recipes"/> is an
    /// <c>IReadOnlyDictionary</c> and nothing here can mutate it.
    /// </summary>
    public MaterialRecipeCatalog Recipes => _recipes;

    /// <summary>
    /// The gem corpus this bench resolves an insert's element and name through, for the READ route
    /// that lists what a player holds (item-content T4). Exposed rather than passed to the endpoint
    /// file a second time: a picker named by a different catalog than the one that prices filling the
    /// socket is exactly how two surfaces come to disagree about what a gem is called.
    /// </summary>
    public Func<string, CardInsertLookup?>? LookupInsert => _lookupInsert;

    // ---- module 14 `salvage-craft` -----------------------------------------------------------------

    /// <summary>
    /// <b>salvage</b> — the converter, committed. An item goes in; materials come out and the item's
    /// disposition becomes <c>salvaged</c>, in one transaction.
    ///
    /// <para>No debit and no recipe: <c>SalvagePolicy.Yield</c> is a credit, and R1's rung−1 rule and
    /// the "no souls, ever" rule are its, not this method's. Idempotency is the disposition itself —
    /// see <see cref="RpgStore.TrySalvageItem"/>.</para>
    /// </summary>
    public WorkbenchOutcomeDto Salvage(long playerId, string instanceId)
    {
        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return Refused("salvage", instanceId, "", refusal);

        var yield = SalvagePolicy.Yield(
            new SalvageInput(
                target.RungIndex, target.Generation.ItemLevel, target.Generation.Frame,
                target.DrawnAffixCount, target.ElementalAffixCounts, target.Head.EnhanceLevel),
            _materials);

        var now = ServerClock.UtcNowDateTime.ToString("O");
        var salvaged = _store.TrySalvageItem(
            playerId, PlayerKey(playerId), instanceId, yield, now, Guid.NewGuid().ToString("N"));

        return salvaged.Ok
            ? new WorkbenchOutcomeDto(true, "salvage", "", instanceId, "", 0, false, "salvaged",
                target.Head.EnhanceLevel, target.Head.PityCounter, 0,
                Array.Empty<WorkbenchCostDto>(), Lines(salvaged.Granted), Array.Empty<WorkbenchSocketDto>())
            : Refused("salvage", instanceId, "", salvaged.Reason);
    }

    /// <summary>
    /// <b>upcycle</b> — module 14's material-to-material verb, and the one operation in this class with
    /// no item instance at all: five of grade <c>g</c> become one of grade <c>g+1</c>. It is here
    /// because it is the shortest complete debit→act→persist cycle the corpus can already run.
    ///
    /// <para>Retired claim, corrected 2026-09-15 (species-gear-chain T14): this comment used to say
    /// <c>forge</c> <b>cannot</b> run because its recipes name <c>item.*</c> containers and no module
    /// authored an <c>effect_container</c> for a base type. That stopped holding the day
    /// <c>EquipmentContainerBuild.From</c> learned to assemble the container from the base type
    /// itself, and <c>LootMintAt</c>'s Equipment arm to instantiate exactly that
    /// (<c>EquipmentContainerBuild.cs:47</c>, <c>LootMintAt.cs:76-88</c>) — <c>Forge</c> below is the
    /// verb this comment once ruled out.</para>
    /// </summary>
    public WorkbenchOutcomeDto Upcycle(long playerId, string recipeId, string correlationId)
    {
        if (Replay(playerId, correlationId, "upcycle", instanceId: null) is { } replayed) return replayed;

        if (!TryRecipe(recipeId, CraftOperation.Upcycle, out var recipe, out var recipeRefusal))
            return Refused("upcycle", "", recipeId, recipeRefusal);

        if (recipe.OutputKind != "material" || recipe.OutputRef is not { Length: > 0 } outputMaterial)
            return Refused("upcycle", "", recipeId,
                $"recipe '{recipeId}' has no material output — an upcycle that mints nothing is a spend with no product");

        var ctx = new RecipeContext(MaterialHasNoRung, IlvlTierLadder.MinTier, 0, recipe.Frame, 0);
        var lines = _recipes.Resolve(recipeId, ctx);

        var applied = _store.TrySpendAndApply(
            playerId, recipeId, lines, correlationId,
            grants: new[] { new WorkbenchGrant(outputMaterial, recipe.OutputQty) },
            playerKey: PlayerKey(playerId));

        return applied.Ok
            ? new WorkbenchOutcomeDto(true, "upcycle", applied.Reason, "", recipeId, 0, applied.Replayed,
                "upcycled", 0, 0, 0, Lines(lines),
                new[] { new WorkbenchCostDto(nameof(MaterialClass.Substrate), outputMaterial, recipe.OutputQty) },
                Array.Empty<WorkbenchSocketDto>())
            : Refused("upcycle", "", recipeId, applied.Reason);
    }

    /// <summary>
    /// <b>forge-gem</b> — species-gear-chain T9 (spec-gem-tier.md §2): three of a family at tier
    /// <c>k</c> become one of the same family at tier <c>k+1</c>, priced by the row's own forge-gem
    /// cost lines. Same-family ONLY (owner decision 2026-09-13): a cross-family upcycle would need
    /// an authored exchange rate the corpus does not carry, and inventing one here is exactly the
    /// silent-design failure this program exists to remove.
    ///
    /// <para>The rung input is the OUTPUT tier (owner decision 2026-09-13), and that scale mismatch
    /// is deliberate and documented: <c>RecipeContext.TargetRungIndex</c> is a 0..9 rung index, but
    /// the forge-gem legs price on the gem tier so that a t4 upcycle costs more than a t2 — see the
    /// tuning file's own <c>forgeGemRungNote</c>. No number moves here; the legs already shipped.</para>
    ///
    /// <para>No item instance, no mutation op — like upcycle, a stock-to-stock mint in one
    /// transaction. Idempotency is the correlation row (see <see cref="Replay"/>).</para>
    /// </summary>
    public WorkbenchOutcomeDto ForgeGem(
        long playerId, string recipeId, string insertContainerId, string correlationId)
    {
        if (Replay(playerId, correlationId, "forge-gem", instanceId: null) is { } replayed) return replayed;

        if (!TryRecipe(recipeId, CraftOperation.ForgeGem, out var recipe, out var recipeRefusal))
            return Refused("forge-gem", "", recipeId, recipeRefusal);

        if (recipe.OutputKind != "gem" || recipe.OutputRef is not { Length: > 0 } outputGem)
            return Refused("forge-gem", "", recipeId,
                $"recipe '{recipeId}' has no gem output — a forge-gem that mints nothing is a spend with no product");

        var input = _lookupInsert?.Invoke(insertContainerId);
        if (input is null)
            return Refused("forge-gem", "", recipeId,
                $"insert '{insertContainerId}' is not in the gem corpus — an unresolvable input has no family and no tier to check");
        var output = _lookupInsert?.Invoke(outputGem);
        if (output is null)
            return Refused("forge-gem", "", recipeId,
                $"recipe '{recipeId}' names output '{outputGem}', which is not in the gem corpus");

        if (!string.Equals(input.Value.Def.FamilyId, output.Value.Def.FamilyId, StringComparison.Ordinal))
            return Refused("forge-gem", "", recipeId,
                $"input '{insertContainerId}' is family '{input.Value.Def.FamilyId}', output '{outputGem}' is family " +
                $"'{output.Value.Def.FamilyId}' — cross-family upcycle has no authored exchange rate");
        if (output.Value.Def.Tier != input.Value.Def.Tier + 1)
            return Refused("forge-gem", "", recipeId,
                $"output tier {output.Value.Def.Tier} is not one above input tier {input.Value.Def.Tier} — " +
                "an upcycle climbs the ladder one rung, never skips or stalls");

        var held = _store.ListStock(PlayerKey(playerId))
            .Where(s => string.Equals(s.ContainerId, insertContainerId, StringComparison.Ordinal))
            .Sum(s => s.Qty);
        var need = _sockets.UpcycleInputPerOutput;
        if (held < need)
            return Refused("forge-gem", "", recipeId,
                $"holding {held} of '{insertContainerId}', need {need} — an upcycle consumes its inputs whole");

        var ctx = new RecipeContext(output.Value.Def.Tier, output.Value.Def.Tier, 0, recipe.Frame, 0);
        var lines = _recipes.Resolve(recipeId, ctx);

        var applied = _store.TrySpendAndApply(
            playerId, recipeId, lines, correlationId,
            stock: new[]
            {
                // The grant path is materials-only by construction (WorkbenchGrant hands a material
                // id to GrantMaterialsUnlocked) — a gem container rides stock like every other
                // fungible container, spent and minted as signed deltas in the same transaction.
                new WorkbenchStockDelta(insertContainerId, -need),
                new WorkbenchStockDelta(outputGem, recipe.OutputQty),
            },
            playerKey: PlayerKey(playerId));

        return applied.Ok
            ? new WorkbenchOutcomeDto(true, "forge-gem", applied.Reason, "", recipeId, 0, applied.Replayed,
                "forge-gemmed", 0, 0, 0, Lines(lines),
                new[] { new WorkbenchCostDto("stock", insertContainerId, need) },
                Array.Empty<WorkbenchSocketDto>())
            : Refused("forge-gem", "", recipeId, applied.Reason);
    }

    // ---- module 14 `salvage-craft`, second owned verb ------------------------------------------------

    /// <summary>
    /// <b>forge</b> — species-gear-chain T14: a spend that mints. `Upcycle`'s shape (spend → grant,
    /// no instance, no mutation op) with the grant replaced by a mint through the SHIPPED path —
    /// `EquipmentContainerBuild.From` assembles the container from the output base type,
    /// `Instantiator.TryInstantiate` freezes the rolls with `InstanceOrigin.Craft`, and
    /// `TryForgeAndApply` debits the recipe price and saves the instance in ONE transaction (a spend
    /// with no item is theft, an item with no spend is duplication).
    ///
    /// <para>The seed is FNV-1a over (recipeId, correlationId) — never `GetHashCode`, randomised per
    /// process — so a retry mints the identical item even before replay short-circuits it. There is
    /// no instance yet, which is the same asymmetry `Upcycle` already documents (`Replay(...,
    /// instanceId: null)`).</para>
    ///
    /// <para>The item level is DERIVED from the substrate grade the recipe already spends (OQ4): the
    /// grade leg is what distinguishes `recipe.001` (crude) from `recipe.023` (fine), and a flat level
    /// would make the three forge tiers produce identical items. `GradeForItemLevel` inverts as
    /// grade g ⟺ level in [(g−1)·N, g·N), so the mint level is the band top `g·N − 1` — the inversion
    /// stated here so a reader can check it against `MaterialTuning.GradeForItemLevel`.</para>
    /// </summary>
    public WorkbenchOutcomeDto Forge(long playerId, string recipeId, string correlationId)
    {
        if (Replay(playerId, correlationId, "forge", instanceId: null) is { } replayed) return replayed;

        if (!TryRecipe(recipeId, CraftOperation.Forge, out var recipe, out var recipeRefusal))
            return Refused("forge", "", recipeId, recipeRefusal);

        if (recipe.OutputKind != "container" || recipe.OutputRef is not { Length: > 0 } baseTypeId)
            return Refused("forge", "", recipeId,
                $"recipe '{recipeId}' has no base-type output — a forge that mints nothing is a spend with no product");

        if (_forgeMintCells is null || _forgePowerTuning is null)
            return Refused("forge", "", recipeId,
                "forge.mint-unavailable — the mint needs role-family cells and power tuning no caller " +
                "supplied (the same honest gap RpgStore.Mint.cs names for Program.cs)");

        var baseType = _store.GetBaseType(baseTypeId);
        if (baseType is null)
            return Refused("forge", "", recipeId,
                $"output base type '{baseTypeId}' was never imported — a forge cannot mint from an id the corpus does not carry");

        // The grade leg names the item level (OQ4): exactly one substrate leg, else the recipe does
        // not say which tier of item it forges and guessing would re-price it silently. Authored
        // lines carry no class — the substrate leg is the one whose material id opens with it.
        var substrateLegs = recipe.CostLines
            .Where(l => l.MaterialId.StartsWith("substrate.", StringComparison.Ordinal)).ToList();
        if (substrateLegs.Count != 1)
            return Refused("forge", "", recipeId,
                $"recipe '{recipeId}' names {substrateLegs.Count} substrate legs — a forge row names exactly one, the grade its item level derives from");
        var grade = MaterialCatalog.GradeOf(substrateLegs[0].MaterialId);
        if (grade < 1)
            return Refused("forge", "", recipeId,
                $"recipe '{recipeId}' substrate '{substrateLegs[0].MaterialId}' carries no grade");
        var itemLevel = checked(grade * _materials.ItemLevelPerGrade - 1);

        var ctx = new RecipeContext(0, 0, itemLevel, baseType.Value.Frame, 0);
        var lines = _recipes.Resolve(recipeId, ctx);

        var grant = new LootGrant(
            Index: 0,
            Kind: DropEntryKind.Equipment,
            RefId: recipeId,
            Count: 1,
            AffixChannel: "",
            BaseTypeId: baseTypeId,
            Frame: baseType.Value.Frame,
            Role: baseType.Value.Role,
            RarityId: null,
            RarityOrdinal: 0,
            ItemLevel: itemLevel,
            RollSeed: ForgeSeed(recipeId, correlationId));

        var applied = _store.TryForgeAndApply(
            playerId, recipeId, lines, correlationId, grant, itemLevel,
            _forgeMintCells, _forgePowerTuning, PlayerKey(playerId));

        return applied.Ok
            ? new WorkbenchOutcomeDto(true, "forge", applied.Reason, applied.OutcomeRef, recipeId, 0,
                applied.Replayed, "forged", 0, 0, 0, Lines(lines),
                new[] { new WorkbenchCostDto(nameof(MaterialClass.Substrate), substrateLegs[0].MaterialId, lines.First(l => l.MaterialId == substrateLegs[0].MaterialId).Qty) },
                Array.Empty<WorkbenchSocketDto>())
            : Refused("forge", "", recipeId, applied.Reason);
    }

    /// <summary>FNV-1a 64-bit over recipe + correlation — deterministic across processes, unlike
    /// `string.GetHashCode`. Named here rather than shared because the domain (forge mint) differs
    /// from wave seeds; same algorithm, separate stream namespace.</summary>
    internal static ulong ForgeSeed(string recipeId, string correlationId)
    {
        unchecked
        {
            var h = 14695981039346656037UL;
            foreach (var ch in recipeId + "\0" + correlationId)
            {
                h ^= (ulong)ch;
                h *= 1099511628211UL;
            }
            return h;
        }
    }

    // ---- module 15 `enhance-reroll` ----------------------------------------------------------------

    /// <summary>
    /// <b>enhance</b> — <c>+n → +n+1</c> through <see cref="EnhancePolicy.Resolve"/>, priced by module
    /// 14's <c>temper</c> rows, recorded by <c>AppendMutationOp</c>.
    ///
    /// <para><b>A failed attempt still spends</b> (spec §4: <i>"materials spent, level unchanged, pity
    /// counter +1"</i>), so both outcomes take the same path and both append an op — the failure is a
    /// result, not an error, and D2 clause 4 records it as one.</para>
    ///
    /// <para>⚠ <b><c>MutationResult.Values</c> is deliberately empty here, and that is a finding rather
    /// than a shortcut.</b> D2 clause 1 says the head's <c>values_json</c> is the SSOT and enhancement
    /// rewrites in place — but <c>AppendMutationOp</c> never applies <c>Result.Values</c> to
    /// <c>effect_instance_atom</c>, and the shipped card composes the gain from the persisted
    /// <c>enhance_level</c> over the rung's <c>enhance_cap</c> instead
    /// (<c>RpgStore.ItemCard.cs:256-261</c>). Writing values here as well would double-count the gain
    /// on every surface that reads the card. The two models are reconciled by the level being the one
    /// stored fact; the divergence is recorded in tasks/item-todo.md rather than papered over.</para>
    /// </summary>
    public WorkbenchOutcomeDto Enhance(
        long playerId, string instanceId, string recipeId, string correlationId,
        IReadOnlyList<AssuranceLine>? assuranceLines = null)
    {
        if (Replay(playerId, correlationId, "enhance", instanceId) is { } replayed) return replayed;

        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return Refused("enhance", instanceId, recipeId, refusal);

        if (!TryRecipe(recipeId, CraftOperation.Temper, out _, out var recipeRefusal))
            return Refused("enhance", instanceId, recipeId, recipeRefusal);

        // species-gear-chain T42: the ONE place a request's assurance lines become debited cost
        // lines — coalesced to one per id (a duplicate sums, never double-counts as two lines),
        // verb-checked against CostClassMatrix.Allows, and id-checked against what Enhance actually
        // reads (assure/protect; repair belongs to a repair attempt, T44). WardLoaded/AssureLoaded
        // below are derived from this SAME coalesced set — never a second, forgeable input.
        if (!TryCoalesceAssuranceLines(assuranceLines, CraftOperation.Temper,
                new[] { AssureId, ProtectId }, out var assuranceCostLines, out var assuranceRefusal))
            return Refused("enhance", instanceId, recipeId, assuranceRefusal);

        var assureLoaded = assuranceCostLines.Where(l => l.MaterialId == AssureId).Sum(l => l.Qty);
        // species-gear-chain T43: ONE fact (was a debited assurance.protect loaded on this attempt),
        // TWO call sites — EnhanceContext.WardLoaded (suppresses the downgrade half of a peril
        // failure, T39/T42) and CraftWearFor below (suppresses craft wear past exhaustion, T43).
        // Never a second "craft ward".
        var protectLoaded = assuranceCostLines.Any(l => l.MaterialId == ProtectId);

        var ctx = new EnhanceContext(
            target.RungIndex, target.Generation.ItemLevel, target.Head.EnhanceLevel,
            target.Head.PityCounter, WardLoaded: protectLoaded, AssureLoaded: checked((int)assureLoaded));

        // The op's own named stream, seeded from (instance, correlation) so a retry decides the same
        // thing — and domain-separated per op kind, so adding a roll to reroll never shifts this one.
        var rng = SeededRng.DeriveStream(
            unchecked((ulong)RpgStore.DeriveOpSeed(instanceId, correlationId)),
            MutationOpKinds.StreamName(MutationOpKind.Enhance));

        // The Hub is read only when an assure charge is actually loaded — the overwhelmingly common
        // unassured attempt never depends on CraftAssuranceTuningHub being configured at all, exactly
        // like every other optional delegate/Hub seam in this file.
        var assureBonusMilli = ctx.AssureLoaded > 0 ? CraftAssuranceTuningHub.Tuning.AssureBonusMilli : 0;
        var attempt = EnhancePolicy.Resolve(ctx, rng, _enhancement, out var policyRefusal, assureBonusMilli);
        if (!policyRefusal.IsOk)
            return Refused("enhance", instanceId, recipeId, policyRefusal.ToString());

        // species-gear-chain T42 (§ Design 4 "spent on load, whatever the outcome"): the assurance
        // lines join the recipe's own cost lines UNCONDITIONALLY, before the one TrySpendAndApply
        // call below — so a protect line debits on a success exactly as on a failure, and a short
        // assurance stack refuses the WHOLE attempt through the shipped shortfall path, the same as
        // a short material stack always has.
        var lines = _recipes.Resolve(recipeId, RecipeContextFor(playerId, target))
            .Concat(assuranceCostLines).ToList();
        var levelAfter = attempt.LevelAfter;
        var head = HeadOf(target.Instance, levelAfter);

        // species-gear-chain T13: the milestone append. Success-only (a failed attempt changes no
        // level, so there is no milestone to grant), additive to the mutation, never replacing it.
        // Unwired delegates, a trackless base type, a non-milestone level, and a generator-limited
        // family all resolve to NO append with the attempt untouched — only a family the milestone
        // corpus never named throws, via the lookup's own contract.
        var appended = ResolveMilestoneAppend(target.Generation.BaseTypeId, levelAfter, rng,
            successOnly: attempt.Outcome == EnhanceOutcome.Success);

        var mutation = new WorkbenchMutation(
            instanceId, MutationOpKind.Enhance,
            new MutationResult(OutcomeId(attempt.Outcome), levelAfter - target.Head.EnhanceLevel,
                Array.Empty<AtomValueSet>(), Array.Empty<int>(), appended),
            MutationCanonical.StateHash(head),
            target.Head.OriginValuesJson ?? OriginValuesJson(target.Instance),
            ServerClock.UtcNowDateTime.ToString("O"),
            target.Instance.CatalogRevision, ItemRulesVersionUnset,
            PityCounter: attempt.PityCounterAfter,
            DurabilityCurrent: CraftWearFor(target, protectLoaded),
            PotentialCurrent: PotentialAfter(target, CraftOperations.Id(CraftOperation.Temper)));

        var applied = _store.TrySpendAndApply(
            playerId, recipeId, lines, correlationId, mutation, playerKey: PlayerKey(playerId));

        return applied.Ok
            ? new WorkbenchOutcomeDto(true, "enhance", applied.Reason, instanceId, recipeId,
                applied.OpSeq, applied.Replayed, OutcomeId(attempt.Outcome),
                levelAfter, attempt.PityCounterAfter, attempt.SuccessMilli,
                Lines(lines), Array.Empty<WorkbenchCostDto>(), Sockets(instanceId))
            : Refused("enhance", instanceId, recipeId, applied.Reason);
    }

    static readonly string AssureId = MaterialCatalog.AssuranceId("assure");
    static readonly string ProtectId = MaterialCatalog.AssuranceId("protect");
    static readonly string RepairId = MaterialCatalog.AssuranceId("repair");

    /// <summary>
    /// species-gear-chain T42/T43 — a request's assurance lines, coalesced to one
    /// <see cref="MaterialCostLine"/> per id (duplicates SUM, never double-append as two lines) and
    /// validated twice: once against <see cref="CostClassMatrix.Allows"/> for <paramref name="op"/>
    /// (a verb Assurance may not spend on at all refuses by name, the same
    /// <see cref="CostClassMatrix.CostClassForbiddenRule"/> every other cost-class mismatch already
    /// raises), and once per line against the closed assurance id set AND against
    /// <paramref name="recognizedIds"/> — which of the three ids THIS caller actually reads (Enhance:
    /// assure+protect; Promote/Reroll/SocketImbue, T43: protect only — `assure` raises a success
    /// chance none of them roll). Empty/absent input is legal and returns an empty line list,
    /// unconditionally spent alongside nothing extra.
    /// </summary>
    static bool TryCoalesceAssuranceLines(
        IReadOnlyList<AssuranceLine>? requested, CraftOperation op, IReadOnlyList<string> recognizedIds,
        out IReadOnlyList<MaterialCostLine> lines, out string refusal)
    {
        lines = Array.Empty<MaterialCostLine>();
        refusal = "";
        if (requested is not { Count: > 0 }) return true;

        if (!CostClassMatrix.Allows(op, MaterialClass.Assurance))
        {
            refusal = MutationRules.Violated(CostClassMatrix.CostClassForbiddenRule,
                $"operation '{CraftOperations.Id(op)}' may not spend an Assurance line").ToString();
            return false;
        }

        var coalesced = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in requested)
        {
            if (string.IsNullOrWhiteSpace(line.MaterialId) || line.Count <= 0)
            {
                refusal = MutationRules.Violated("enhance.assurance-line-malformed",
                    $"assurance line '{line.MaterialId ?? "(null)"}' x{line.Count} is malformed — an id and a positive count are both required").ToString();
                return false;
            }
            if (!MaterialCatalog.IsAssuranceId(line.MaterialId))
            {
                refusal = MutationRules.Violated("enhance.assurance-line-not-assurance",
                    $"'{line.MaterialId}' is not one of the closed assurance ids").ToString();
                return false;
            }
            if (!recognizedIds.Contains(line.MaterialId, StringComparer.Ordinal))
            {
                refusal = MutationRules.Violated("enhance.assurance-line-unrecognized",
                    $"'{line.MaterialId}' is not an assurance id operation '{CraftOperations.Id(op)}' reads " +
                    $"(only {string.Join("/", recognizedIds)})").ToString();
                return false;
            }

            coalesced[line.MaterialId] = checked(coalesced.GetValueOrDefault(line.MaterialId) + line.Count);
        }

        lines = coalesced.Select(kv => new MaterialCostLine(MaterialClass.Assurance, kv.Key, kv.Value)).ToList();
        return true;
    }

    /// <summary>
    /// <b>promote</b> — species-gear-chain T26: move the item one rung up
    /// <see cref="FusionRpg.Core.Items.RarityLadder"/>, priced by the shipped <c>elevate</c> rows that
    /// already exist (no bespoke cost curve, no new priced verb).
    ///
    /// <para><b>Additive by construction:</b> the mutation carries no value sets, no suppressions and no
    /// appends, so every affix survives identically — promotion is a rung move, not a reroll. <b>No
    /// private failure chance:</b> the only risk an attempt carries is the craft-wear the risk ladder
    /// owns (T24), which this verb reads through the same one decision point. <b>The top refuses
    /// cleanly</b> before the ladder's own throw is reachable.</para>
    /// </summary>
    public WorkbenchOutcomeDto Promote(
        long playerId, string instanceId, string recipeId, string correlationId,
        IReadOnlyList<AssuranceLine>? assuranceLines = null)
    {
        if (Replay(playerId, correlationId, "promote", instanceId) is { } replayed) return replayed;

        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return Refused("promote", instanceId, recipeId, refusal);

        if (!TryRecipe(recipeId, CraftOperation.Elevate, out _, out var recipeRefusal))
            return Refused("promote", instanceId, recipeId, recipeRefusal);

        // species-gear-chain T43: Promote is deterministic (no success die), so it reads only
        // `assurance.protect` — never `assure`, which has nothing to raise here.
        if (!TryCoalesceAssuranceLines(assuranceLines, CraftOperation.Elevate,
                new[] { ProtectId }, out var assuranceCostLines, out var assuranceRefusal))
            return Refused("promote", instanceId, recipeId, assuranceRefusal);
        var protectLoaded = assuranceCostLines.Any(l => l.MaterialId == ProtectId);

        var rungId = FusionRpg.Core.Items.RarityLadder.RungIds[target.RungIndex];
        if (FusionRpg.Core.Items.RarityLadder.IsTopRung(rungId))
            return Refused("promote", instanceId, recipeId,
                $"promote.top-rung: '{rungId}' has no rung above it — the ladder's own top, refused here");

        var nextRungId = FusionRpg.Core.Items.RarityLadder.OneRungAbove(rungId);

        // The rung move is recorded in the ORDINAL vocabulary the rest of the item tables use
        // (`item_generation.rarity_ordinal`), so the card and the cache path read the same numbers.
        // Both ordinals come from the ladder rows for the rung ids `TryResolve` already resolved, so
        // the mark can never name a rung the item was not standing on.
        var rarities = _store.ListRarities();
        var toOrdinal = rarities.First(r => string.Equals(r.RarityId, nextRungId, StringComparison.Ordinal)).Ordinal;
        var fromOrdinal = rarities.First(r => string.Equals(r.RarityId, rungId, StringComparison.Ordinal)).Ordinal;

        var mutation = new WorkbenchMutation(
            instanceId, MutationOpKind.Promotion,
            new MutationResult("promoted", 0, Array.Empty<AtomValueSet>(), Array.Empty<int>(),
                Array.Empty<AtomAppend>()),
            target.Head.StateHash, target.Head.OriginValuesJson,
            ServerClock.UtcNowDateTime.ToString("O"),
            target.Instance.CatalogRevision, ItemRulesVersionUnset,
            // T24's craft wear, through the one decision point — T43 suppresses it for a debited
            // assurance.protect line, never a private failure chance here.
            DurabilityCurrent: CraftWearFor(target, protectLoaded),
            PotentialCurrent: PotentialAfter(target, CraftOperations.Id(CraftOperation.Elevate)),
            PromotedRarityOrdinal: toOrdinal,
            PromotedFromOrdinal: fromOrdinal);

        var lines = _recipes.Resolve(recipeId, RecipeContextFor(playerId, target))
            .Concat(assuranceCostLines).ToList();
        var applied = _store.TrySpendAndApply(
            playerId, recipeId, lines, correlationId, mutation, playerKey: PlayerKey(playerId));

        return applied.Ok
            ? new WorkbenchOutcomeDto(true, "promote", applied.Reason, instanceId, recipeId,
                applied.OpSeq, applied.Replayed, "promoted",
                target.Head.EnhanceLevel, target.Head.PityCounter, 0,
                Lines(lines), Array.Empty<WorkbenchCostDto>(), Sockets(instanceId))
            : Refused("promote", instanceId, recipeId, applied.Reason);
    }

    /// <summary>
    /// <b>upgrade</b> — species-gear-chain T37 (`item-upgrade-tree` a): consume an instance and produce
    /// its AUTHORED successor chassis carrying the same affixes. The decision is
    /// <see cref="ItemUpgradePolicy.Decide"/> over the authored edge table (`ItemUpgradeEdgeHub`) with
    /// Rule 1 evaluated by <see cref="RoleAllowListLegality"/>, and the write is one transaction in
    /// `RpgStore.TryUpgradeAndApply`: the op row belongs to the CONSUMED item, which then takes the
    /// salvage-shaped disposition, while the successor gets its own instance and generation rows.
    ///
    /// <para>Souls are the only cost — the consumed item IS the material cost — priced on the class rung
    /// by `operations.upgrade`. No reroll and no private failure chance: the affixes carried are the
    /// consumed instance's own atom rows, unchanged, and a refusal consumes nothing.</para>
    /// </summary>
    public WorkbenchOutcomeDto Upgrade(long playerId, string instanceId, string correlationId)
    {
        const string verb = "upgrade";
        if (Replay(playerId, correlationId, verb, instanceId) is { } replayed) return replayed;

        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return Refused(verb, instanceId, verb, refusal);

        if (_affixFamilyRoles is null)
            return Refused(verb, instanceId, verb,
                "upgrade.legality-unavailable: the affix-family role allow-lists are not wired — Rule 1 " +
                "cannot be evaluated, and an unevaluated legality rule must not pass");

        if (_forgeMintCells is null)
            return Refused(verb, instanceId, verb,
                "upgrade.container-unavailable: the role-family cells are not wired, so the successor's " +
                "container cannot be built");

        var successorFrame = "";
        var successorRole = "";
        var decision = DecideUpgrade(target, out var successorBaseTypeId, out successorFrame, out successorRole,
            out var refusalCode, out var refusalDetail);
        if (decision is null || !decision.Value.Ok)
            return Refused(verb, instanceId, verb,
                refusalCode is not null ? $"{refusalCode}: {refusalDetail}"
                    : $"{decision!.Value.RefusalCode}: {decision.Value.RefusalDetail}");

        // The successor's container comes from the ONE container builder, for the successor chassis;
        // its pool never decides the affixes (see `TryUpgradeAndApply`'s own note) — the consumed
        // instance's atom rows below are the carried affix set.
        var lookups = new EquipmentContainerLookups(_forgeMintCells, family => _store.ListAtomsByFamily(family));        var grant = new LootGrant(0, DropEntryKind.Equipment, successorBaseTypeId, 1,
            target.Generation.AffixChannel,
            BaseTypeId: successorBaseTypeId, Frame: successorFrame, Role: successorRole);
        var built = EquipmentContainerBuild.From(grant, lookups);

        var successorInstanceId = Guid.NewGuid().ToString("N");
        var successorInstance = new InstanceRow
        {
            InstanceId = successorInstanceId,
            ContainerId = built.Container.ContainerId,
            RollSeed = target.Instance.RollSeed,
            CatalogRevision = target.Instance.CatalogRevision,
            CreatedUtc = ServerClock.UtcNowDateTime.ToString("O"),
            Origin = target.Instance.Origin,
            // ⛔ Carried identically: the consumed instance's own atom rows, never a fresh pool.
            Atoms = target.Instance.Atoms,
            ThetaContent = target.Instance.ThetaContent,
            ContentScaleMilli = target.Instance.ContentScaleMilli,
        };
        // Provenance keeps pointing at the original acquisition, and the chassis is the only field that
        // moves — plus the promoted mark, which answers a rarity question this verb does not change.
        var successorGeneration = target.Generation with
        {
            InstanceId = successorInstanceId,
            BaseTypeId = successorBaseTypeId,
        };

        // `DecideUpgrade` has already proved the leg is present (it refuses `upgrade.tuning-missing-souls-leg`
        // otherwise), so the price line reads it directly rather than repeating the check in a second place.
        var souls = _materials.Operations[CraftOperation.Upgrade].Souls!.Value;

        var grade = _materials.GradeForItemLevel(target.Generation.ItemLevel);
        var lines = new List<MaterialCostLine>
        {
            MaterialCostLine.Souls(souls.BaseQty(grade, target.RungIndex, target.Head.EnhanceLevel)),
        };

        var mutation = new WorkbenchMutation(instanceId, MutationOpKind.Upgrade,
            new MutationResult("upgraded", 0, Array.Empty<AtomValueSet>(), Array.Empty<int>(),
                Array.Empty<AtomAppend>()),
            target.Head.StateHash, target.Head.OriginValuesJson, ServerClock.UtcNowDateTime.ToString("O"),
            target.Instance.CatalogRevision, ItemRulesVersionUnset);

        // T60: the successor inherits the input's USED durability fraction, computed from the two maxes the
        // same source already derives (the input's stored pair, the successor's derived max). A missing input
        // (no wear source, no stored max, a zero max) carries nothing rather than inventing a number.
        long? carriedDurability = null;
        long? carriedPotential = null;
        if (_craftWear is { } wear &&
            wear.MaxFor(target.Generation.BaseTypeId, target.RungIndex) is { } inputMax && inputMax > 0 &&
            wear.MaxFor(successorBaseTypeId, target.RungIndex) is { } successorMax &&
            _store.GetDurability(instanceId).Current is { } inputCurrent)
        {
            carriedDurability = checked(successorMax * inputCurrent / inputMax);
        }

        // T60 (potential half): the SAME carry for the other head pair, off `PotentialMaxFor` (T61's
        // sibling derivation). The raw read is the truth, exactly as above: a NULL pair means "never
        // derived", which for potential means no point was ever spent — a zero used fraction, so
        // carrying nothing leaves the successor at its own full max. Omitting this was the potential-reset
        // loop spec § Open question 3 names as the escape hatch the risk ladder exists to close.
        if (_craftWear is { } potentialWear &&
            potentialWear.PotentialMaxFor is { } potentialMax &&
            potentialMax(target.Generation.BaseTypeId, target.RungIndex) is { } inputPotentialMax &&
            inputPotentialMax > 0 &&
            potentialMax(successorBaseTypeId, target.RungIndex) is { } successorPotentialMax &&
            _store.GetPotential(instanceId).Current is { } inputPotentialCurrent)
        {
            carriedPotential = checked(successorPotentialMax * inputPotentialCurrent / inputPotentialMax);
        }

        var applied = _store.TryUpgradeAndApply(playerId, verb, lines, correlationId, mutation,
            successorInstance, successorGeneration, successorDurabilityCurrent: carriedDurability,
            successorPotentialCurrent: carriedPotential,
            playerKey: PlayerKey(playerId));

        return applied.Ok
            ? new WorkbenchOutcomeDto(true, verb, applied.Reason, successorInstanceId, verb,
                applied.OpSeq, applied.Replayed, "upgraded", target.Head.EnhanceLevel,
                target.Head.PityCounter, 0, Lines(lines), Array.Empty<WorkbenchCostDto>(),
                Sockets(successorInstanceId))
            : Refused(verb, instanceId, verb, applied.Reason);
    }

    /// <summary>
    /// species-gear-chain T37 — the upgrade's ONE decision path, shared by the verb and its preview:
    /// the authored edge, the successor's own row, Rule 1 through the affix-family role allow-lists,
    /// and <see cref="ItemUpgradePolicy.Decide"/>. Returns null (with a named infrastructure refusal)
    /// when the host has not wired what the decision needs; otherwise the decision itself, whose own
    /// refusals are the ones a caller reports.
    ///
    /// <para>⚠ Requirement profiles are not wired to base types at runtime yet (T35/T36 shipped the
    /// resolver and its tuning, and no profile is persisted), so <c>SuccessorRequirement</c> is null:
    /// rule 4 is executable and tested at the policy level but NOT armed in production, which the
    /// spec's Open questions and the evidence fragment both say rather than imply.</para>
    /// </summary>
    ItemUpgradeDecision? DecideUpgrade(WorkbenchTarget target, out string successorBaseTypeId,
        out string successorFrame, out string successorRole, out string? refusalCode, out string? refusalDetail)
    {
        successorBaseTypeId = "";
        successorFrame = "";
        successorRole = "";
        refusalCode = null;
        refusalDetail = null;

        if (_affixFamilyRoles is null)
        {
            refusalCode = "upgrade.legality-unavailable";
            refusalDetail = "the affix-family role allow-lists are not wired — Rule 1 cannot be evaluated, " +
                "and an unevaluated legality rule must not pass";
            return null;
        }

        if (_forgeMintCells is null)
        {
            refusalCode = "upgrade.container-unavailable";
            refusalDetail = "the role-family cells are not wired, so the successor's container cannot be built";
            return null;
        }

        if (ItemUpgradeEdgeHub.EdgeFor(target.Generation.BaseTypeId) is not { Length: > 0 } edge)
        {
            refusalCode = "upgrade.no-successor";
            refusalDetail = $"'{target.Generation.BaseTypeId}' has no authored successorOf edge";
            return null;
        }
        successorBaseTypeId = edge;

        if (_store.GetBaseType(edge) is not { } info)
        {
            refusalCode = "upgrade.successor-unknown";
            refusalDetail = $"'{edge}' is not a base type in the corpus";
            return null;
        }
        successorFrame = info.Frame;
        successorRole = info.Role;

        // No rung data: the runtime cannot read a base type's class or band, and under the owner ruling
        // the authored edge is the authority. The content-time invariant is the closure check + the
        // seed validator, never a number invented here.
        var input = new ItemUpgradeNode(target.Generation.BaseTypeId, "", target.Generation.Frame, 0, 0,
            _baseTypeImplicitFamily?.Invoke(target.Generation.BaseTypeId) ?? "",
            edge, EmptyStringSet, target.Generation.Role);
        var successor = new ItemUpgradeNode(edge, "", successorFrame, 0, 0,
            _baseTypeImplicitFamily?.Invoke(edge) ?? "", null,
            EmptyStringSet, successorRole);
        var catalog = new Dictionary<string, ItemUpgradeNode>(StringComparer.Ordinal) { [edge] = successor };

        // The carried affixes, read from the instance's own atom rows: identities only — the decision
        // never needs their values, and the WRITE carries the rows themselves.
        var affixes = new List<ItemUpgradeAffix>(target.Instance.Atoms.Count);
        foreach (var atom in target.Instance.Atoms)
        {
            var row = _store.GetAtom(atom.AtomId);
            affixes.Add(new ItemUpgradeAffix("", row?.FamilyId ?? "", row?.Tier ?? 0, 0));
        }

        var legality = new RoleAllowListLegality(id =>
            _affixFamilyRoles.TryGetValue(id, out var roles) ? roles : EmptyStringSet);

        var decision = ItemUpgradePolicy.Decide(
            new ItemUpgradeRequest(input, affixes, SuccessorRequirement: null, OwnerLevel: 1), catalog, legality);
        if (!decision.Ok) return decision;

        // The souls leg is the upgrade's only price line. The ROW is required while the LEG is optional
        // (I9 §7.4 leaves cells as em dashes), so a tuning can parse with the leg missing — and this check
        // lives HERE, in the shared decision, so the preview and the commit refuse identically. It closed a
        // real divergence: the preview used to answer `ok` with a 0-soul price while the commit refused
        // `upgrade.tuning-missing-souls-leg`, i.e. a preview quoting something the commit would not honour.
        if (_materials.Operations[CraftOperation.Upgrade].Souls is null)
        {
            refusalCode = "upgrade.tuning-missing-souls-leg";
            refusalDetail = "operations.upgrade carries no souls leg, so this verb has no price for it";
            return null;
        }

        return decision;
    }

    /// <summary>
    /// <b>upgrade preview</b> — species-gear-chain T37: the SAME decision the verb makes, with nothing
    /// spent and nothing consumed, so a surface can show the outgoing and incoming implicit and the
    /// successor chassis BEFORE the player commits (the spec's § 2a Rule 2 and Success criterion 5).
    /// Read-only by construction: no spend, no op row, no instance write.
    /// </summary>
    public WorkbenchUpgradePreviewDto UpgradePreview(long playerId, string instanceId)
    {
        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return new WorkbenchUpgradePreviewDto(false, refusal, instanceId, "", "", "", 0, 0,
                Array.Empty<WorkbenchCostDto>());

        var decision = DecideUpgrade(target, out var successorBaseTypeId, out _, out _, out var code, out var detail);
        if (decision is null)
            return new WorkbenchUpgradePreviewDto(false, $"{code}: {detail}", instanceId, "", "", "", 0, 0,
                Array.Empty<WorkbenchCostDto>());
        if (!decision.Value.Ok)
            return new WorkbenchUpgradePreviewDto(false,
                $"{decision.Value.RefusalCode}: {decision.Value.RefusalDetail}", instanceId, "", "", "", 0, 0,
                Array.Empty<WorkbenchCostDto>());

        var plan = decision.Value.Plan!;
        var souls = _materials.Operations[CraftOperation.Upgrade].Souls;
        var grade = _materials.GradeForItemLevel(target.Generation.ItemLevel);
        var qty = souls is { } leg ? leg.BaseQty(grade, target.RungIndex, target.Head.EnhanceLevel) : 0;
        var lines = qty > 0 ? new[] { MaterialCostLine.Souls(qty) } : Array.Empty<MaterialCostLine>();

        return new WorkbenchUpgradePreviewDto(true, "", instanceId, plan.Successor.BaseTypeId,
            plan.OutgoingImplicitFamily, plan.IncomingImplicitFamily, plan.CarriedAffixes.Count, qty, Lines(lines));
    }

    /// <summary>
    /// <b>repair</b> — species-gear-chain T23: restore a worn item's <c>durability_current</c> through
    /// <see cref="RepairPolicy.Resolve"/>, priced by the operation's own `repair` recipe row.
    ///
    /// <para><b>D1:</b> the attempt can destroy the item, and that roll happens BEFORE any restore is
    /// computed, on the op's own named stream. On destruction the op records the attempt and the item's
    /// disposition becomes `destroyed` in the same transaction — the salvage path's own write, never a
    /// row deletion. <b>D2:</b> the workbench tier's cap is full, so the material on hand is what sets
    /// the coverage, and a short set yields the partial/eroding result rather than a refusal.</para>
    /// </summary>
    public WorkbenchOutcomeDto Repair(
        long playerId, string instanceId, string recipeId, string correlationId,
        IReadOnlyList<AssuranceLine>? assuranceLines = null)
    {
        if (Replay(playerId, correlationId, "repair", instanceId) is { } replayed) return replayed;

        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return Refused("repair", instanceId, recipeId, refusal);

        if (!TryRecipe(recipeId, CraftOperation.Repair, out _, out var recipeRefusal))
            return Refused("repair", instanceId, recipeId, recipeRefusal);

        // species-gear-chain T44: repair reads only `assurance.repair` — R10 refuses `protect` (and
        // `assure`) by name, the same "not a recognized id for this verb" gate every other verb's
        // own recognized set already enforces. The destroy chance below never reads this leg.
        if (!TryCoalesceAssuranceLines(assuranceLines, CraftOperation.Repair,
                new[] { RepairId }, out var assuranceCostLines, out var assuranceRefusal))
            return Refused("repair", instanceId, recipeId, assuranceRefusal);
        var repairLoaded = assuranceCostLines.Where(l => l.MaterialId == RepairId).Sum(l => l.Qty);

        var pair = _store.GetDurability(instanceId);
        if (pair.Max is not { } max || pair.Current is not { } current)
            return Refused("repair", instanceId, recipeId,
                "repair.durability-undetermined — the item carries no derived durability pair to restore");

        // The attempt's own named stream, seeded from (instance, correlation) so a retry decides the
        // same thing — and domain-separated, so this roll never shifts another op's sequence.
        var rng = SeededRng.DeriveStream(
            unchecked((ulong)RpgStore.DeriveOpSeed(instanceId, correlationId)),
            MutationOpKinds.StreamName(MutationOpKind.Repair));

        // The Hub is read only when a repair charge is actually loaded — the overwhelmingly common
        // unassured repair never depends on CraftAssuranceTuningHub being configured at all.
        var repairCoverageBonusMilli = repairLoaded > 0 ? CraftAssuranceTuningHub.Tuning.RepairCoverageBonusMilli : 0;
        var coverage = RepairPolicy.CoverageWithAssurance(
            RepairPolicy.FullRestoreMilli, repairLoaded, repairCoverageBonusMilli);

        var outcome = RepairPolicy.Resolve(
            current, max,
            materialCoverageMilli: coverage,
            tierCapMilli: RepairPolicy.FullRestoreMilli,
            // D1's chance comes off the durability domain's own file (module 7's territory); an
            // unconfigured Hub throws by name rather than defaulting every repair to risk-free.
            // R10: never touched by assurance.repair — the destroy chance is the sink, unconditionally.
            destructionChanceMilli: DeploymentHierarchyTuningHub.Tuning.RepairDestroyChanceMilli,
            rng);

        var outcomeId = outcome.Destroyed ? "destroyed" : "repaired";
        var mutation = new WorkbenchMutation(
            instanceId, MutationOpKind.Repair,
            new MutationResult(outcomeId, 0, Array.Empty<AtomValueSet>(), Array.Empty<int>(),
                Array.Empty<AtomAppend>()),
            target.Head.StateHash, target.Head.OriginValuesJson,
            ServerClock.UtcNowDateTime.ToString("O"),
            target.Instance.CatalogRevision, ItemRulesVersionUnset,
            // A destroyed item keeps the durability it had: the attempt cost it the item, not the pair.
            DurabilityCurrent: outcome.Destroyed ? current : outcome.RestoredCurrent,
            PotentialCurrent: PotentialAfter(target, CraftOperations.Id(CraftOperation.Repair)),
            DestroyItem: outcome.Destroyed);

        var lines = _recipes.Resolve(recipeId, RecipeContextFor(playerId, target))
            .Concat(assuranceCostLines).ToList();
        var applied = _store.TrySpendAndApply(
            playerId, recipeId, lines, correlationId, mutation, playerKey: PlayerKey(playerId));

        return applied.Ok
            ? new WorkbenchOutcomeDto(true, "repair", applied.Reason, instanceId, recipeId,
                applied.OpSeq, applied.Replayed, outcomeId,
                target.Head.EnhanceLevel, target.Head.PityCounter, 0,
                Lines(lines), Array.Empty<WorkbenchCostDto>(), Sockets(instanceId))
            : Refused("repair", instanceId, recipeId, applied.Reason);
    }

    /// <summary>
    /// <b>reroll-one / reroll-all</b> — species-gear-chain T15: the already-built
    /// `RerollPolicy` (validate) + `RerollRenderer` (draw) behind verbs, priced by their own
    /// reroll recipes. `Enhance`'s shape (resolve → recipe → cost → mutation → spend-and-apply),
    /// with the level machinery replaced by the redraw: RerollOne re-freezes the SAME affix id
    /// (`MutationOpKind.RerollValue`), RerollAll draws fresh identities per budget
    /// (`MutationOpKind.RerollAffix`), and both suppress-then-append through the T13-owned seam —
    /// which is why this task follows it. A reroll that changes nothing about the level changes
    /// nothing about the head: the state hash recomputes over the unchanged level, and the recorded
    /// op carries the redrawn atoms.
    ///
    /// <para>Unwired catalogs refuse by name (`reroll.unwired-catalogs`); a policy or renderer
    /// refusal renders as its own rule. The op seed is `DeriveOpSeed(instance, correlation)` like
    /// every other verb — the renderer's per-budget streams derive from it, so a retry replays.</para>
    /// </summary>
    public WorkbenchOutcomeDto RerollOne(
        long playerId, string instanceId, string recipeId, int targetSeq, string correlationId,
        IReadOnlyList<AssuranceLine>? assuranceLines = null) =>
        Reroll(playerId, instanceId, recipeId, new[] { targetSeq }, correlationId,
            CraftOperation.RerollOne, MutationOpKind.RerollValue, isSingle: true, assuranceLines);

    /// <summary>See <see cref="RerollOne"/> — same shape, a seq SET, fresh identities.</summary>
    public WorkbenchOutcomeDto RerollAll(
        long playerId, string instanceId, string recipeId, IReadOnlyList<int> targetSeqs, string correlationId,
        IReadOnlyList<AssuranceLine>? assuranceLines = null) =>
        Reroll(playerId, instanceId, recipeId, targetSeqs, correlationId,
            CraftOperation.RerollAll, MutationOpKind.RerollAffix, isSingle: false, assuranceLines);

    WorkbenchOutcomeDto Reroll(
        long playerId, string instanceId, string recipeId, IReadOnlyList<int> targetSeqs,
        string correlationId, CraftOperation operation, MutationOpKind kind, bool isSingle,
        IReadOnlyList<AssuranceLine>? assuranceLines = null)
    {
        var verb = operation == CraftOperation.RerollOne ? "reroll-one" : "reroll-all";
        if (Replay(playerId, correlationId, verb, instanceId) is { } replayed) return replayed;

        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return Refused(verb, instanceId, recipeId, refusal);

        if (!TryRecipe(recipeId, operation, out _, out var recipeRefusal))
            return Refused(verb, instanceId, recipeId, recipeRefusal);

        // species-gear-chain T43: neither reroll verb rolls a success die (a redraw always lands),
        // so both read only `assurance.protect` — the same craft-wear suppression Enhance/Promote
        // already get.
        if (!TryCoalesceAssuranceLines(assuranceLines, operation,
                new[] { ProtectId }, out var assuranceCostLines, out var assuranceRefusal))
            return Refused(verb, instanceId, recipeId, assuranceRefusal);
        var protectLoaded = assuranceCostLines.Any(l => l.MaterialId == ProtectId);

        if (_lookupAtom is null || _lookupAffix is null)
            return Refused(verb, instanceId, recipeId,
                "reroll.unwired-catalogs — a redraw without the atom and affix catalogs would fabricate values");

        var (drawRejection, drawn) = FusionRpg.Core.Items.Mutation.RerollRenderer.DrawnFrom(
            target.Instance, _lookupAtom);
        if (!drawRejection.IsOk)
            return Refused(verb, instanceId, recipeId, drawRejection.ToString());

        var opSeed = unchecked((ulong)RpgStore.DeriveOpSeed(instanceId, correlationId));
        var (renderRejection, suppressed, appended) = isSingle
            ? FusionRpg.Core.Items.Mutation.RerollRenderer.RenderOne(
                target.Container, drawn, targetSeqs[0], (long)opSeed, target.Instance.ContentScaleMilli,
                _lookupAtom, _lookupAffix)
            : FusionRpg.Core.Items.Mutation.RerollRenderer.RenderAll(
                target.Container, drawn, targetSeqs, (long)opSeed, target.Instance.ContentScaleMilli,
                _lookupAtom, _lookupAffix);
        if (!renderRejection.IsOk)
            return Refused(verb, instanceId, recipeId, renderRejection.ToString());

        var lines = _recipes.Resolve(recipeId, RecipeContextFor(playerId, target))
            .Concat(assuranceCostLines).ToList();
        var head = HeadOf(target.Instance, target.Head.EnhanceLevel);

        var mutation = new WorkbenchMutation(
            instanceId, kind,
            new MutationResult("rerolled", 0,
                Array.Empty<AtomValueSet>(), suppressed.ToList(), appended.ToList()),
            MutationCanonical.StateHash(head),
            target.Head.OriginValuesJson ?? OriginValuesJson(target.Instance),
            ServerClock.UtcNowDateTime.ToString("O"),
            target.Instance.CatalogRevision, ItemRulesVersionUnset,
            DurabilityCurrent: CraftWearFor(target, protectLoaded),
            PotentialCurrent: PotentialAfter(target, CraftOperations.Id(operation)));

        var applied = _store.TrySpendAndApply(
            playerId, recipeId, lines, correlationId, mutation, playerKey: PlayerKey(playerId));

        return applied.Ok
            ? new WorkbenchOutcomeDto(true, verb, applied.Reason, instanceId, recipeId,
                applied.OpSeq, applied.Replayed, "rerolled",
                target.Head.EnhanceLevel, target.Head.PityCounter, 0,
                Lines(lines), Array.Empty<WorkbenchCostDto>(), Sockets(instanceId))
            : Refused(verb, instanceId, recipeId, applied.Reason);
    }

    /// <summary>
    /// species-gear-chain T13: the milestone atom for a new level, or nothing. The four quiet
    /// paths — failure (no level gained), unwired corpus, trackless base type, non-milestone
    /// level, generator-limited family — all resolve to an empty list with the attempt untouched.
    /// Only a family the milestone corpus never named throws, via the lookup's own contract (a
    /// content gap, loud). The amount rolls inside the row's own [min..max] on the op's stream —
    /// the row authors a range with roll:onApply, and the stream is already domain-separated per
    /// op kind, so this draw never shifts another operation's sequence.
    /// </summary>
    IReadOnlyList<AtomAppend> ResolveMilestoneAppend(
        string baseTypeId, int levelAfter, SeededRng rng, bool successOnly)
    {
        if (!successOnly) return Array.Empty<AtomAppend>();
        if (_baseTypeEnhanceTrack is null || _milestoneAtomFor is null)
            return Array.Empty<AtomAppend>();
        if (!EnhancePolicy.IsMilestoneLevel(levelAfter, _enhancement))
            return Array.Empty<AtomAppend>();

        var track = _baseTypeEnhanceTrack(baseTypeId);
        var resolved = FusionRpg.Core.Items.Mutation.MilestoneTrack.FamilyFor(track, levelAfter, _enhancement);
        if (resolved is null) return Array.Empty<AtomAppend>();

        var (family, ordinal) = resolved.Value;
        var ladder = _enhancement.MilestoneTierLadder;
        var tier = ladder[Math.Min(ordinal, ladder.Count) - 1];

        var atom = _milestoneAtomFor(family, tier);
        if (atom is null) return Array.Empty<AtomAppend>();

        var span = checked(atom.MaxAmount - atom.MinAmount);
        var amount = atom.MinAmount + rng.NextInt(checked((int)span + 1));
        return new[]
        {
            // Seq is a store-allocated placeholder here (0): AppendMutationOpUnlocked replaces it
            // with the instance's next atom seq in-transaction AND rewrites the recorded result to
            // match, so the ledger never carries a seq the row does not have.
            new AtomAppend(0, atom.AtomId,
                new Dictionary<string, long>(StringComparer.Ordinal) { ["amount"] = amount }),
        };
    }

    // ---- module 16 `sockets` -----------------------------------------------------------------------

    /// <summary><b>socket-add</b> (module 14's <c>bore</c> price) — open one empty, crafted socket.</summary>
    public WorkbenchOutcomeDto SocketAdd(
        long playerId, string instanceId, string recipeId, string correlationId,
        IReadOnlyList<AssuranceLine>? assuranceLines = null)
    {
        if (Replay(playerId, correlationId, "socket-add", instanceId) is { } replayed) return replayed;

        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return Refused("socket-add", instanceId, recipeId, refusal);

        if (!TryRecipe(recipeId, CraftOperation.Bore, out _, out var recipeRefusal))
            return Refused("socket-add", instanceId, recipeId, recipeRefusal);

        if (_baseTypeSocketMax?.Invoke(target.Generation.BaseTypeId) is not { } entrySocketMax)
            return Refused("socket-add", instanceId, recipeId,
                $"ContentRuleViolated{{socket.base-type-socket-max-unavailable}}: no socketMax for base type " +
                $"'{target.Generation.BaseTypeId}' — the item_base_type table carries (id, frame, role) " +
                "but no socketMax, and " +
                "half a socket rule would grant the wrong count rather than none");

        var current = _store.GetSockets(instanceId);
        var rejection = SocketOperations.TryAdd(current, entrySocketMax, out var next);
        if (!rejection.IsOk) return Refused("socket-add", instanceId, recipeId, rejection.ToString());

        return ApplySocketWrite(playerId, target, recipeId, correlationId, "socket-add",
            MutationOpKind.SocketAdd, next, stock: null,
            operation: CraftOperation.Bore, assuranceLines: assuranceLines);
    }

    /// <summary>
    /// <b>socket-insert</b> (module 14's flat-ten-souls <c>socket</c> price) — put an insert the player
    /// already holds into an open socket, and take it out of stock in the same transaction.
    ///
    /// <para>⭐ <b>The element is real as of 2026-09-06</b> — it comes from module 16's shipped gem
    /// corpus through <c>lookupInsert</c>, the same delegate the card and surface routes read, rather
    /// than the hardcoded <c>""</c> this method used to pass. <c>""</c> survives only as the fallback
    /// for a container the corpus does not carry, which is also what a genuinely element-free insert
    /// authors.</para>
    ///
    /// <para>⭐ <b>The instance is real as of species-gear-chain T22</b> — the insert mints through
    /// <c>GemContainerBuild.TryBuildOne</c> (the held container's own seed: family × element × band)
    /// and <c>Instantiator.TryInstantiate</c> with <c>InstanceOrigin.Craft</c>, at the ladder pin where
    /// depth changes nothing (see <see cref="InsertMintPinTheta"/>), and the socket row, the instance
    /// rows and the debit commit in ONE transaction (<c>TrySpendSocketInsertAndApply</c>). The
    /// <c>""</c> this method used to stamp no longer appears on the column. An insert the corpus does
    /// not carry, or carries but cannot build (a family the atom catalog never authored), refuses BY
    /// NAME before anything is written — no spend row, no socket row, no instance row.</para>
    /// </summary>
    public WorkbenchOutcomeDto SocketInsert(
        long playerId, string instanceId, string recipeId, string insertContainerId, int? socketIndex,
        string correlationId, IReadOnlyList<AssuranceLine>? assuranceLines = null)
    {
        if (Replay(playerId, correlationId, "socket-insert", instanceId) is { } replayed) return replayed;

        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return Refused("socket-insert", instanceId, recipeId, refusal);

        if (!TryRecipe(recipeId, CraftOperation.Socket, out _, out var recipeRefusal))
            return Refused("socket-insert", instanceId, recipeId, recipeRefusal);

        var held = _store.ListStock(PlayerKey(playerId))
            .FirstOrDefault(s => string.Equals(s.ContainerId, insertContainerId, StringComparison.Ordinal));
        if (held is null || held.Qty <= 0)
            return Refused("socket-insert", instanceId, recipeId,
                $"ContentRuleViolated{{socket.insert-not-held}}: '{insertContainerId}' is not in this player's stock");

        var insert = _lookupInsert?.Invoke(insertContainerId)?.Def
                     ?? new InsertDef(insertContainerId, insertContainerId, Element: "",
                         Tier: GemInsertCorpus.UnauthoredInsertTier);
        // The id is born here, stamped into the socket row by TryInsert below, and saved under the
        // same id inside the spend transaction — so the column never carries "" and never names an
        // id with no row. Guid.N matches SaveInstanceUnlocked's own generation; a fresh id per
        // attempt is safe because a refusal writes nothing and a replay never re-runs this line.
        var gemInstanceId = Guid.NewGuid().ToString("N");
        var current = _store.GetSockets(instanceId);
        var rejection = SocketOperations.TryInsert(current, socketIndex, insert, gemInstanceId, out var next);
        if (!rejection.IsOk) return Refused("socket-insert", instanceId, recipeId, rejection.ToString());

        // AFTER the socket rules: an occupied socket refuses as occupied even for an unresolvable
        // gem, preserving every existing refusal's precedence. The mint itself writes nothing — a
        // refusal here leaves no spend row, no socket row and no instance row.
        if (!TryMintInsert(insertContainerId, instanceId, correlationId, out var gemInstance, out var mintRefusal))
            return Refused("socket-insert", instanceId, recipeId, mintRefusal);

        return ApplySocketWrite(playerId, target, recipeId, correlationId, "socket-insert",
            MutationOpKind.SocketInsert, next,
            stock: new[] { new WorkbenchStockDelta(insertContainerId, -1) },
            mintedInsert: gemInstance, mintedInsertId: gemInstanceId,
            operation: CraftOperation.Socket, assuranceLines: assuranceLines);
    }

    /// <summary>
    /// The ladder pin (Θc=20, <c>PowerTuning.FixedPinIndex</c> — internal to Core, so repeated here
    /// with its why rather than as a magic number): the depth where contentScale is exactly ×1.000
    /// under EVERY valid tuning (<c>PowerTuning.Build</c> rejects any other pin), which is what makes
    /// it the honest theta for a depthless mint. A gem has no depth — it never dropped anywhere —
    /// so it mints where depth changes nothing, and every copy of one gem freezes byte-identical
    /// values everywhere (spec-sockets §7: fixed containers stack). A host-θ scale would make
    /// identical gems freeze different magnitudes: the second axis §6 exists to stop. Not a tunable:
    /// a balance pass moves magnitudes, never the ladder's neutral index (ask-first ADR).
    /// </summary>
    const int InsertMintPinTheta = 20;

    /// <summary>
    /// species-gear-chain T22: the insert's own instance, decided here (pure Core over injected
    /// lookups) and persisted by the store. Returns false with a reason naming the insert when the
    /// mint inputs are unwired, the corpus never carried it, it cannot build, or it cannot
    /// instantiate — every arm before any write, so a refusal is stateless by construction.
    /// </summary>
    bool TryMintInsert(
        string insertContainerId, string hostInstanceId, string correlationId,
        out InstanceRow? gemInstance, out string reason)
    {
        gemInstance = null;

        if (_lookupGemSeed is null || _lookupAtom is null || _lookupAffix is null || _gemPowerTuning is null)
        {
            reason = "socket.mint-unavailable — the mint needs the gem-seed lookup, the atom/affix " +
                "catalogs and the power tuning no caller supplied (forge names the same gap for its " +
                "own cells); minting without them would fabricate the insert's atoms";
            return false;
        }

        var seed = _lookupGemSeed(insertContainerId);
        if (seed is null)
        {
            reason = $"socket.insert-unknown: '{insertContainerId}' is not in the gem corpus — an insert " +
                "that is held but never authored cannot be instantiated, and guessing its family " +
                "would grant combat numbers nothing authored";
            return false;
        }

        var container = GemContainerBuild.TryBuildOne(seed, new GemContainerBuild.GemContainerLookups(_lookupAtom), out var buildRefusal);
        if (container is null)
        {
            reason = $"socket.insert-unresolvable: {buildRefusal}";
            return false;
        }

        var minted = Instantiator.TryInstantiate(
            container, _lookupAtom, _lookupAffix, RpgStore.DeriveOpSeed(hostInstanceId, correlationId),
            InsertMintPinTheta, _gemPowerTuning, out var instance, InstanceOrigin.Craft,
            _store.GetCatalogRevision());
        if (!minted.IsOk || instance is null)
        {
            reason = $"socket.insert-unresolvable: '{insertContainerId}' failed to instantiate — {minted}";
            return false;
        }

        gemInstance = instance;
        reason = "";
        return true;
    }

    /// <summary>The one essence namespace an imbue row may name — the corpus's own prefix
    /// (`recipegen/imbue.py`), asserted here rather than re-spelled at each comparison.</summary>
    const string ImbueEssencePrefix = "essence.";

    /// <summary>
    /// <b>socket-imbue</b> (D24, module 14's <c>imbue</c> price) — declare a crafted, empty socket's
    /// element affinity.
    ///
    /// <para>⭐ <b>Payable since SSH8.2:</b> the corpus authors one <c>imbue</c> row per (bore frame,
    /// concrete element) — derived by <c>recipegen/imbue.py</c>, never briefed — so the verb that used
    /// to refuse <c>material.recipe-unknown</c> now resolves a real price on bore's own curve (D24).</para>
    ///
    /// <para>⛔ <b>The essence must name the element</b> (spec-socket-pricing §2): the recipe's own
    /// <c>essence.&lt;element&gt;</c> line has to agree with the affinity the caller asks for, or the
    /// player would pay for one element and receive another. Refused by name in the existing `socket`
    /// namespace — no new rejection-enum member.</para>
    /// </summary>
    public WorkbenchOutcomeDto SocketImbue(
        long playerId, string instanceId, string recipeId, int socketIndex, string element, string correlationId,
        IReadOnlyList<AssuranceLine>? assuranceLines = null)
    {
        if (Replay(playerId, correlationId, "socket-imbue", instanceId) is { } replayed) return replayed;

        if (!TryResolve(playerId, instanceId, out var target, out var refusal))
            return Refused("socket-imbue", instanceId, recipeId, refusal);

        if (!TryRecipe(recipeId, CraftOperation.Imbue, out var recipe, out var recipeRefusal))
            return Refused("socket-imbue", instanceId, recipeId, recipeRefusal);

        var wanted = element.Trim().ToLowerInvariant();
        var paid = recipe.CostLines
            .Select(line => line.MaterialId)
            .FirstOrDefault(id => id.StartsWith(ImbueEssencePrefix, StringComparison.Ordinal));
        if (paid is null || !string.Equals(paid, ImbueEssencePrefix + wanted, StringComparison.Ordinal))
        {
            var spends = paid ?? "no essence line";
            return Refused("socket-imbue", instanceId, recipeId,
                SocketRules.Violated(SocketRules.ImbueElementMismatch,
                    $"recipe '{recipeId}' spends '{spends}' and the socket is being attuned to " +
                    $"'{wanted}' — the essence a player pays for must name the element it buys").ToString());
        }

        var current = _store.GetSockets(instanceId);
        var rejection = SocketOperations.TryImbue(current, socketIndex, wanted, out var next);
        if (!rejection.IsOk) return Refused("socket-imbue", instanceId, recipeId, rejection.ToString());

        return ApplySocketWrite(playerId, target, recipeId, correlationId, "socket-imbue",
            MutationOpKind.SocketImbue, next, stock: null,
            operation: CraftOperation.Imbue, assuranceLines: assuranceLines);
    }

    /// <summary>The one socket persistence path, shared by all three socket verbs — <c>item_socket</c>
    /// is the SSOT and the <c>socket-*</c> op is the audit receipt beside it (D2 clause 13), and both
    /// commit with the debit.
    /// <para><paramref name="mintedInsert"/>/<paramref name="mintedInsertId"/> ride along only for
    /// <c>socket-insert</c> (species-gear-chain T22): the insert's own instance joins the same
    /// transaction through <c>TrySpendSocketInsertAndApply</c>. Every other verb leaves both
    /// <c>null</c> and takes the mint-free path exactly as before.</para></summary>
    /// <summary>
    /// SSH4.8 (spec-combo-bind §2.1): a socket write on an EQUIPPED host is a key-set edge — a
    /// combination can start or stop firing — so every specimen wearing the host has its bindings
    /// refreshed HERE, in the same request, through the same projection that carry the host and its
    /// inserts. The workbench's three socket verbs all commit through <see cref="ApplySocketWrite"/>, so
    /// this is called once, from the one place the socket row commits.
    /// </summary>
    void RefreshCombinationBindings(string hostInstanceId)
    {
        foreach (var specimenId in _store.SpecimensWearing(hostInstanceId))
        {
            var actor = _store.GetUniqueActor(specimenId);
            if (actor is null) continue;   // a wearer with no actor row is not a projection target
            _store.MaterializeRolledEquipRuntime(specimenId, checked((int)actor.Level));
        }
    }

    WorkbenchOutcomeDto ApplySocketWrite(
        long playerId, WorkbenchTarget target, string recipeId, string correlationId, string verb,
        MutationOpKind kind, IReadOnlyList<SocketSlot> next, IReadOnlyList<WorkbenchStockDelta>? stock,
        InstanceRow? mintedInsert = null, string? mintedInsertId = null,
        CraftOperation operation = default, IReadOnlyList<AssuranceLine>? assuranceLines = null)
    {
        // species-gear-chain T43: none of the three socket verbs roll a success die, so all three
        // read only `assurance.protect` — the same craft-wear suppression Enhance/Promote/Reroll get.
        if (!TryCoalesceAssuranceLines(assuranceLines, operation,
                new[] { ProtectId }, out var assuranceCostLines, out var assuranceRefusal))
            return Refused(verb, target.Item.InstanceId, recipeId, assuranceRefusal);
        var protectLoaded = assuranceCostLines.Any(l => l.MaterialId == ProtectId);

        var lines = _recipes.Resolve(recipeId, RecipeContextFor(playerId, target))
            .Concat(assuranceCostLines).ToList();

        // Sockets never touch the host's atoms, so the host's state hash is unchanged by construction
        // — carried forward rather than recomputed, which is the claim `socketing_never_writes_a_host
        // _atom_row` already makes at the Core level.
        var mutation = new WorkbenchMutation(
            target.Item.InstanceId, kind, MutationResult.Nothing(verb),
            target.Head.StateHash, target.Head.OriginValuesJson,
            ServerClock.UtcNowDateTime.ToString("O"),
            target.Instance.CatalogRevision, ItemRulesVersionUnset,
            Sockets: next,
            DurabilityCurrent: CraftWearFor(target, protectLoaded),
            PotentialCurrent: PotentialAfter(target, CraftOperations.Id(CraftOperation.Imbue)));

        var applied = mintedInsert is not null && mintedInsertId is { Length: > 0 }
            ? _store.TrySpendSocketInsertAndApply(
                playerId, recipeId, lines, correlationId, mutation, mintedInsert, mintedInsertId,
                stock, PlayerKey(playerId))
            : _store.TrySpendAndApply(
                playerId, recipeId, lines, correlationId, mutation, stock: stock, playerKey: PlayerKey(playerId));

        // SSH4.8 (spec-combo-bind §2.1): the socket row committed, so the host's fill changed — a word
        // may have started (or stopped) firing. Refresh the WEARER's bindings in this same request,
        // through the same projection: the read side self-corrects removals but not additions, and a
        // word completed on an equipped host must bind without a re-equip. Discrete trigger, never a poll.
        if (applied.Ok) RefreshCombinationBindings(target.Item.InstanceId);

        return applied.Ok
            ? new WorkbenchOutcomeDto(true, verb, applied.Reason, target.Item.InstanceId, recipeId,
                applied.OpSeq, applied.Replayed, verb, target.Head.EnhanceLevel, target.Head.PityCounter, 0,
                Lines(lines), Array.Empty<WorkbenchCostDto>(), Sockets(target.Item.InstanceId))
            : Refused(verb, target.Item.InstanceId, recipeId, applied.Reason);
    }

    // ---- replay ------------------------------------------------------------------------------------
    /// <summary>
    /// ⭐ <b>D2 §9 clause 8, and it has to run BEFORE the price is re-derived.</b> Found by the retry
    /// test, not by reading: a successful operation moves the very state its price is derived from —
    /// <c>temper</c> costs <c>15 × (n+1)</c>, so re-pricing a retried enhance against the item's new
    /// <c>+n</c> resolves a different cost, and <see cref="RpgStore.TrySpendRecipe"/> then correctly
    /// refuses it as <c>correlation.mismatch</c>. The mismatch rule is right and stays; what was wrong
    /// was asking it a question about a cost the caller had already been charged a different one for.
    ///
    /// <para>So a correlation that already has a spend-log row short-circuits here and returns the
    /// <b>recorded</b> cost and the item's <b>current persisted</b> state — nothing is re-decided, nothing
    /// is re-priced and nothing is re-rolled, which is the same discipline clause 4 puts on replay.</para>
    /// </summary>
    WorkbenchOutcomeDto? Replay(long playerId, string correlationId, string verb, string? instanceId)
    {
        if (_store.FindMaterialSpend(playerId, correlationId) is not { } prior) return null;

        var op = instanceId is { Length: > 0 }
            ? _store.ReadMutationOps(instanceId)
                .FirstOrDefault(o => string.Equals(o.CorrelationId, correlationId, StringComparison.Ordinal))
            : null;

        // species-gear-chain T37 — the DTO names the instance the outcome is ABOUT, and that is the
        // spend log's `outcome_ref`, not the verb's input. The two differ exactly when a verb consumes
        // its input while producing another: an upgrade's op row belongs to the consumed instance (its
        // own history) and `perform` records the SUCCESSOR as the outcome ref, so a replay that named
        // the input would hand back a destroyed instance. This generalises T14's own resolution (which
        // only fired for the instance-less verbs): `outcome_ref` is the ONE recorded produced id, read
        // for every verb and used only when it resolves to a real instance — upcycle records its recipe
        // id and forge-gem records "" there, and neither may leak into InstanceId. For every
        // non-consuming verb the two ids are the same instance, so nothing moves.
        var recordedInstanceId = prior.OutcomeRef is { Length: > 0 } && _store.GetInstance(prior.OutcomeRef) is not null
            ? prior.OutcomeRef
            : instanceId;
        var recordedHead = recordedInstanceId is { Length: > 0 } ? _store.GetInstanceMutationHead(recordedInstanceId) : null;

        return new WorkbenchOutcomeDto(
            true, verb, "replay", recordedInstanceId ?? "", prior.RecipeId,
            op?.Seq ?? 0, Replayed: true, op?.Result.Outcome ?? "replay",
            recordedHead?.EnhanceLevel ?? 0, recordedHead?.PityCounter ?? 0, 0,
            ParseCostJson(prior.CostJson), Array.Empty<WorkbenchCostDto>(),
            recordedInstanceId is { Length: > 0 } ? Sockets(recordedInstanceId) : Array.Empty<WorkbenchSocketDto>());
    }

    /// <summary>Read back <c>rpg_material_spend_log.cost_json</c> — what the operation actually paid,
    /// rather than what it would cost to run again now.</summary>
    static IReadOnlyList<WorkbenchCostDto> ParseCostJson(string costJson)
    {
        var lines = new List<WorkbenchCostDto>();
        try
        {
            using var doc = JsonDocument.Parse(costJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return lines;
            foreach (var line in doc.RootElement.EnumerateArray())
                lines.Add(new WorkbenchCostDto(
                    line.GetProperty("class").GetString() ?? "",
                    line.GetProperty("id").GetString() ?? "",
                    line.GetProperty("qty").GetInt64()));
        }
        catch (JsonException)
        {
            // A log row we cannot parse is still a real spend — reporting no lines is honest, and
            // inventing them from today's tuning would be the re-pricing this method exists to avoid.
        }

        return lines;
    }

    // ---- target resolution -------------------------------------------------------------------------

    /// <summary>Everything a workbench verb reads off one stored item, resolved once.</summary>
    /// <param name="BoundSpeciesId">
    /// species-gear-chain T34d-wire — the container's own species binding (lower-case), resolved via
    /// <see cref="_speciesIdForContainer"/> against every set naming this container
    /// (<see cref="FusionRpg.Core.Items.Thresholds.SetCorpus.SpeciesIdFor"/>). <c>null</c> for a
    /// species-less piece (no delegate supplied, or the container names no species-themed set) — the
    /// legal, common case; two sets disagreeing about the SAME container refuses in
    /// <see cref="TryResolve"/> before a target is ever built, so this field is never "ambiguous".
    /// </param>
    sealed record WorkbenchTarget(
        RpgItemRow Item, InstanceRow Instance, ContainerRow Container, ItemGenerationRow Generation,
        InstanceMutationHead Head, int RungIndex, int DrawnAffixCount,
        IReadOnlyDictionary<string, int> ElementalAffixCounts,
        string? BoundSpeciesId = null);

    bool TryResolve(long playerId, string instanceId, out WorkbenchTarget target, out string reason)
    {
        target = null!;

        var item = _store.GetItem(instanceId);
        if (item is null) { reason = $"item.unknown: no owned item '{instanceId}'"; return false; }

        if (!string.Equals(item.PlayerId, PlayerKey(playerId), StringComparison.Ordinal))
        {
            reason = $"item.not-owned: '{instanceId}' belongs to player '{item.PlayerId}'";
            return false;
        }

        // `RpgItemRow.Locked`'s own contract: "refuse salvage/transfer while true. Never enforced by
        // this row alone — callers that spend an item check it." This is that caller.
        if (item.Locked) { reason = $"item.locked: '{instanceId}' is locked by its owner"; return false; }
        if (!string.Equals(item.Disposition, "owned", StringComparison.Ordinal))
        {
            reason = $"item.not-owned: '{instanceId}' is '{item.Disposition}'";
            return false;
        }

        var instance = _store.GetInstance(instanceId);
        if (instance is null) { reason = $"item.instance-missing: no effect_instance '{instanceId}'"; return false; }

        var container = _store.GetContainer(instance.ContainerId);
        if (container is null)
        {
            reason = $"item.container-missing: '{instance.ContainerId}' is not in the catalog";
            return false;
        }

        var generation = _store.GetItemGeneration(instanceId);
        if (generation is null)
        {
            // Every cost input is a property of the target (D26), and two of them — item level and
            // frame — live only here. Guessing either would price the item wrong in silence.
            reason = $"item.generation-missing: '{instanceId}' has no item_generation stamp, so its item " +
                     "level and frame are unknown and no cost can be priced against it";
            return false;
        }

        // species-gear-chain T26: the INSTANCE's own rung wins once a promotion has moved it.
        // `item_generation.rarity_ordinal` is the current rung and every promotion rewrites it; the
        // container's rarity is where the item was minted and is shared by every instance off that
        // container. Reading the container alone would make a second promotion repeat the first —
        // the rung would never climb past one step. A generation row with no resolvable ordinal
        // (a hand-seeded row, ordinal 0) falls back to the container, which is where it started.
        var instanceRarity = generation.RarityOrdinal > 0
            ? _store.ListRarities().FirstOrDefault(r => r.Ordinal == generation.RarityOrdinal)?.RarityId
            : null;
        var rungId = instanceRarity ?? container.Rarity;

        var rungIndex = rungId is { Length: > 0 } rarity
            ? RarityLadder.RungIds.ToList().IndexOf(rarity)
            : -1;
        if (rungIndex < 0)
        {
            reason = $"item.rarity-unknown: container '{container.ContainerId}' carries rarity " +
                     $"'{rungId}', which is not one of the ten rungs";
            return false;
        }

        var head = _store.GetInstanceMutationHead(instanceId)
                   ?? new InstanceMutationHead(instanceId, 0, 0, 0, null, null);

        var (drawn, elemental) = Affixes(instance, container);

        // species-gear-chain T34d-wire: "unwired is not done" (coordinator ruling, 2026-09-20).
        // Resolved ONCE here, alongside everything else this method already resolves — never
        // re-derived per verb. `null` (no delegate supplied) keeps every existing caller byte-
        // identical (Success criterion 2's own "species-less costs exactly as before" promise,
        // restated one layer up from T32's own RecipeContext field).
        string? boundSpeciesId = null;
        if (_speciesIdForContainer != null)
        {
            var lookup = _speciesIdForContainer(container.ContainerId);
            if (lookup.Ambiguous)
            {
                reason = $"item.species-ambiguous: '{container.ContainerId}' belongs to sets naming " +
                         "different species — refused rather than guessed";
                return false;
            }
            boundSpeciesId = lookup.SpeciesId;
        }

        target = new WorkbenchTarget(item, instance, container, generation, head, rungIndex, drawn,
            elemental, boundSpeciesId);
        reason = "";
        return true;
    }

    /// <summary>
    /// The cost context, all five original fields read off the TARGET (D26). <c>TargetTier</c> comes
    /// from the item's own level through D29's ladder rather than from a stored column, because no
    /// shipped recipe authors a <c>qty_curve_id</c> and the tier axis therefore has no priced leg yet
    /// — module 14's own carried gap, unchanged here.
    ///
    /// <para>species-gear-chain T34d-wire — the four species fields (T32/T34b's own additions to
    /// <see cref="RecipeContext"/>) are populated HERE, the one site every real
    /// Enhance/Promote/Repair/RerollOne/RerollAll/SocketImbue call already funnels through, so no
    /// verb wires them twice. <c>playerId</c> is needed only for <c>TrophyStock</c>'s own per-id
    /// balance reads (<see cref="RpgStore.GetMaterialQty"/>) — never a whole-inventory snapshot.</para>
    /// </summary>
    RecipeContext RecipeContextFor(long playerId, WorkbenchTarget t)
    {
        int? speciesRungIndex = null;
        IReadOnlyList<string>? families = null;

        if (t.BoundSpeciesId is { Length: > 0 } speciesId)
        {
            speciesRungIndex = _speciesRungIndexFor?.Invoke(speciesId);
            families = _familiesForSpecies?.Invoke(speciesId) ?? Array.Empty<string>();
        }

        return new(
            t.RungIndex, IlvlTierLadder.MaxTierAt(t.Generation.ItemLevel), t.Generation.ItemLevel,
            t.Generation.Frame, t.Head.EnhanceLevel,
            SpeciesRungIndex: speciesRungIndex,
            BoundSpeciesId: t.BoundSpeciesId,
            BoundSpeciesFamilies: families,
            // A per-id delegate, never a snapshot: MaterialRecipeCatalog.Resolve queries only the
            // concrete ids the bound species' own family list could ever name (T34d-wire's own
            // "never a full-table scan exposed as a magnitude" acceptance criterion).
            TrophyStock: t.BoundSpeciesId != null
                ? materialId => _store.GetMaterialQty(playerId, materialId)
                : null);
    }

    /// <summary>
    /// The instance's drawn affixes and their elements. <b>Drawn</b> is "not in the container's fixed
    /// core": <c>Instantiator</c> freezes the core and the pool draw into one atom list with no flag
    /// separating them, so the container's own <c>Atoms</c> list is the only thing that can tell them
    /// apart. The element comes from the atom row's variant, which is where a concrete element lives.
    /// </summary>
    (int Drawn, IReadOnlyDictionary<string, int> Elemental) Affixes(InstanceRow instance, ContainerRow container)
    {
        var core = container.Atoms.Select(a => a.AtomId).ToHashSet(StringComparer.Ordinal);
        var elemental = new Dictionary<string, int>(StringComparer.Ordinal);
        var drawn = 0;

        foreach (var atom in instance.Atoms)
        {
            if (core.Contains(atom.AtomId)) continue;
            drawn++;
            var variant = _store.GetAtom(atom.AtomId)?.Variant;
            if (variant is { Length: > 0 } && ElementRoster.TryParse(variant, out _))
                elemental[variant] = elemental.TryGetValue(variant, out var n) ? n + 1 : 1;
        }

        return (drawn, elemental);
    }

    bool TryRecipe(string recipeId, CraftOperation expected, out MaterialRecipe recipe, out string reason)
    {
        if (!_recipes.Recipes.TryGetValue(recipeId, out recipe!))
        {
            var refused = _recipes.Refusals.FirstOrDefault(r =>
                string.Equals(r.RecipeId, recipeId, StringComparison.Ordinal));
            reason = refused is null
                ? $"material.recipe-unknown: '{recipeId}' is not in the loaded catalog"
                : $"material.recipe-unknown: '{recipeId}' was refused at load — {refused.Rule}: {refused.Detail}";
            return false;
        }

        if (recipe.Operation != expected)
        {
            reason = $"material.operation-mismatch: recipe '{recipeId}' is a " +
                     $"'{CraftOperations.Id(recipe.Operation)}', not a '{CraftOperations.Id(expected)}'";
            return false;
        }

        reason = "";
        return true;
    }

    // ---- rendering ---------------------------------------------------------------------------------

    IReadOnlyList<WorkbenchSocketDto> Sockets(string instanceId) =>
        _store.GetSockets(instanceId)
            .Select(s => new WorkbenchSocketDto(
                s.Index, s.Affinity, s.Crafted, s.InsertContainerId,
                // item-content T4. The same corpus `socket-insert` already resolves an insert's
                // element from — one lookup, so a cell can never be named by a different catalog
                // than the one that priced filling it.
                s.InsertContainerId is { Length: > 0 } id
                    ? _lookupInsert?.Invoke(id)?.Name ?? ""
                    : ""))
            .ToList();

    static IReadOnlyList<WorkbenchCostDto> Lines(IReadOnlyList<MaterialCostLine> lines) =>
        lines.Select(l => new WorkbenchCostDto(l.Class.ToString(), l.MaterialId, l.Qty)).ToList();

    /// <summary>
    /// species-gear-chain T24 — the craft's wear, decided once for every instance-mutating verb (T43
    /// suppresses it here for a debited `assurance.protect`; one place, never a second decay path).
    /// Null when the attempt must not touch durability: no wear inputs wired, potential still remains
    /// (T10's Assured stage — a not-yet-derived pair counts as remaining, never as exhausted), the base
    /// type's max cannot be derived, or the pair cannot be read.
    ///
    /// <para>species-gear-chain T43 — <paramref name="protectLoaded"/> is whether THIS attempt
    /// debited an `assurance.protect` line (derived by the caller from the SAME coalesced lines that
    /// get spent, never a second input). Past exhaustion, a protected attempt leaves
    /// <c>durability_current</c> completely unchanged — the current value is still returned (so the
    /// mutation still carries a value and the derive-on-read path stays exercised), it is simply
    /// never handed to <see cref="CraftRiskPolicy.AfterWear"/>. One effect, one id
    /// (<c>assurance.protect</c>), two call sites in this file: <see cref="Enhance"/>'s own
    /// <c>EnhanceContext.WardLoaded</c> (suppresses a peril downgrade) and this one (suppresses craft
    /// wear) — never a second "craft ward".</para>
    /// </summary>
    /// <summary>
    /// species-gear-chain T61 — the item's craft potential, DERIVED when absent: NULL means "not yet derived",
    /// so a host with a ceiling derives it, while a STORED value is the truth (it is what a spend wrote). Null
    /// only when nothing is stored AND the host offers no ceiling — the pre-T61 shape, which then decides no
    /// wear rather than guessing a number.
    /// </summary>
    long? PotentialCurrentFor(WorkbenchTarget target)
    {
        var potential = _store.GetPotential(target.Item.InstanceId);
        if (potential.Current is { } stored) return stored;
        if (_craftWear?.PotentialMaxFor?.Invoke(target.Generation.BaseTypeId, target.RungIndex) is not { } ceiling)
            return null;
        return _store.GetOrDerivePotential(target.Item.InstanceId, () => ceiling).Current;
    }

    /// <summary>
    /// species-gear-chain T61 half B — what the item's potential becomes after this operation spends its own
    /// verb's authored cost (`potentialCostPerVerb`), floored at zero. Null when there is no pair to spend from
    /// or the verb has no authored cost — never a zero-valued write, which would invent a pair where none
    /// exists.
    /// </summary>
    long? PotentialAfter(WorkbenchTarget target, string operationId)
    {
        if (PotentialCurrentFor(target) is not { } current) return null;
        if (!DeploymentHierarchyTuningHub.Tuning.PotentialCostPerVerb.TryGetValue(operationId, out var cost) || cost <= 0)
            return null;
        return Math.Max(0, current - cost);
    }

    long? CraftWearFor(WorkbenchTarget target, bool protectLoaded = false)
    {
        if (_craftWear is null) return null;

        // ⭐ T61: potential is DERIVED here, exactly as durability is two lines down. Before this, the pair
        // was read RAW (`GetPotential`), nothing in production ever wrote or derived it, and `CanDecay(null)`
        // is false by design — so every craft was Assured and craft wear could never engage at all. A host
        // with no potential source keeps the old behaviour (no source, no wear) rather than guessing.
        if (PotentialCurrentFor(target) is not { } potentialCurrent) return null;
        if (!CraftRiskPolicy.CanDecay(potentialCurrent)) return null;

        var derived = _craftWear.MaxFor(target.Generation.BaseTypeId, target.RungIndex);
        if (derived is not { } max) return null;

        var pair = _store.GetOrDeriveDurability(target.Item.InstanceId, () => max);
        if (pair.Max is not { } storedMax || pair.Current is not { } current) return null;

        if (protectLoaded) return current;

        return CraftRiskPolicy.AfterWear(current,
            CraftRiskPolicy.WearFor(storedMax, _craftWear.WearPerAttemptMilli));
    }

    static WorkbenchOutcomeDto Refused(string verb, string instanceId, string recipeId, string reason) =>
        new(false, verb, reason, instanceId, recipeId, 0, false, "refused", 0, 0, 0,
            Array.Empty<WorkbenchCostDto>(), Array.Empty<WorkbenchCostDto>(), Array.Empty<WorkbenchSocketDto>());

    static string PlayerKey(long playerId) => playerId.ToString(CultureInfo.InvariantCulture);

    static string OutcomeId(EnhanceOutcome outcome) => outcome switch
    {
        EnhanceOutcome.Success => "success",
        EnhanceOutcome.Failure => "failure",
        EnhanceOutcome.FailureWithDowngrade => "failure-downgrade",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };

    static InstanceHead HeadOf(InstanceRow instance, int enhanceLevel) => new(
        enhanceLevel,
        instance.Atoms
            .OrderBy(a => a.Seq)
            .Select(a => new InstanceAtomHead(a.Seq, a.AtomId, ReadValues(a.ValuesJson)))
            .ToList());

    static IReadOnlyDictionary<string, long> ReadValues(string valuesJson)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(valuesJson)) return values;

        try
        {
            using var doc = JsonDocument.Parse(valuesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return values;
            foreach (var property in doc.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var v))
                    values[property.Name] = v;
        }
        catch (JsonException)
        {
            // An OnApply spec is an object, not a number — it contributes no scalar to the head hash
            // and is not an error. The atom id and seq are still hashed, so two instances that differ
            // only in an unresolved spec still differ.
        }

        return values;
    }

    /// <summary>
    /// D2 rung 1′, written lazily at the FIRST mutation (§11.3's lean): the instance's frozen numbers,
    /// keyed by <c>seq</c>. An item nobody ever crafts never pays for a second copy of its own values.
    /// </summary>
    static string OriginValuesJson(InstanceRow instance)
    {
        var sb = new StringBuilder("{");
        var first = true;
        foreach (var atom in instance.Atoms.OrderBy(a => a.Seq))
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(atom.Seq.ToString(CultureInfo.InvariantCulture)).Append("\":")
              .Append(string.IsNullOrWhiteSpace(atom.ValuesJson) ? "{}" : atom.ValuesJson);
        }

        return sb.Append('}').ToString();
    }
}
