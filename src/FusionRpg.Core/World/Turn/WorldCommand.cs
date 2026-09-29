namespace FusionRpg.Core.World.Turn;

/// <summary>
/// What a commander may order. Kinds are added by the module that implements them — movement adds
/// its own in W9 — so this list is the contract between the store, the wire, and the engine.
/// </summary>
public static class WorldCommandKinds
{
    /// <summary>Do nothing this turn. The default an absent or idle commander submits.</summary>
    public const string StandFast = "stand-fast";

    /// <summary>March a legion along an ordered lane path (world-movement).</summary>
    public const string Move = "move";

    /// <summary>Attack the guard holding one slot of the sector the legion stands in.</summary>
    public const string Clear = "clear";

    /// <summary>Take the sector the legion stands in, once nothing is left defending it.</summary>
    public const string Claim = "claim";

    /// <summary>Change a legion's posture — march, scout or hold (world-movement).</summary>
    public const string Stance = "stance";

    /// <summary>
    /// Spend a legion's own carried loam into the sector it stands on, 1:1 (spec-loam-legions.md,
    /// G1's bootstrap spend) — a player-issued choice, not an automatic stance effect.
    /// </summary>
    public const string Sustain = "sustain";

    /// <summary>
    /// Found a structure on a compatible, empty slot in the sector a legion stands on
    /// (spec-loam-structures.md), spending the issuing legion's own `CarriedLoam`.
    /// </summary>
    public const string Build = "build";

    /// <summary>
    /// Give up a sector this faction holds, on purpose — the player's own deliberate release,
    /// distinct from `LoamPhases.Pressure`'s automatic pick of the weakest link when upkeep cannot
    /// be paid (world-stage W24). Needs no entity: a faction cedes ground, not a legion.
    /// </summary>
    public const string Cede = "cede";

    /// <summary>
    /// Bind a warden onto a sector this faction owns (spec-loam-texture.md's Wardens; world-stage
    /// W28). **Retired:** `WorldCommandAdmission` refuses every order of this kind with
    /// `warden.retired` (warden-freeze-fix, RulesetVersion 13), and a binding no longer exempts a
    /// sector from `LoamPhases.Pressure`'s fade or recovery. The owner withdrew the mechanic on
    /// 2026-09-13 (warden-mortality-ideal.md). The kind stays in <see cref="All"/> because stored
    /// command logs still carry it and must still hydrate. Named `bind-warden`, not `ward` — `ward`
    /// names the still-unbuilt *lane* action that raises `WorldLaneDto.WardLevel`, a different
    /// mechanic entirely; the collision was repaired once already and must not return through this
    /// kind's name.
    /// </summary>
    public const string BindWarden = "bind-warden";

    /// <summary>
    /// Found a legion at the sector's Seat, spending its `RecruitStock` (world-map W51,
    /// spec-sector-development.md §1). Needs no entity — `raise` founds a *new* legion, it does not
    /// command an existing one. Resolves in `Snapshot`, right after `Build`: ownership is only
    /// decided once the rest of the turn has run, so every other legality check (whose ground, a
    /// Seat, no hostile entity standing in it, enough stock) is resolution-time in
    /// `RaiseResolver`, not admission-time, the same discipline `BuildResolver` already applies.
    /// </summary>
    public const string Raise = "raise";

    /// <summary>
    /// Start a sector-wide project on a sector this faction holds (world-map W52,
    /// spec-sector-development.md §3), spending the sector's own `LoamStock` — a project raises the
    /// whole sector (development, defense, capacity), never one slot's output, which is what `build`
    /// is for. Needs no entity, the same shape `raise` already uses: a project belongs to the
    /// sector, not a legion. Resolves in `Snapshot`, right after `raise`, for the identical
    /// ownership-race reason `BuildResolver`/`RaiseResolver` both already state.
    /// </summary>
    public const string Develop = "develop";

    /// <summary>
    /// Attack the district around a hostile sector's Seat (base-defense-ideal.md decision 26;
    /// spec-siege-seam.md §5). Distinct from <see cref="Clear"/>: `clear` defends one slot's guard,
    /// `assault` fights for the legions standing in the district's core. Needs an entity (who is
    /// attacking) and a sector (which district) — the legality that can change between filing and
    /// resolving (is the legion still there, is the sector still hostile) is reveal-time, the same
    /// discipline `clear`'s own admission rule already states.
    /// </summary>
    public const string Assault = "assault";

    /// <summary>
    /// Load an armoury/stock item aboard a legion (empire-inventory-surfaces `cargo-commands`
    /// §Design 1 — armoury/stock → legion). Resolves Data-side post-Step, never in TurnEngine.
    /// </summary>
    public const string LoadCargo = "load-cargo";

    /// <summary>
    /// Unload one cargo row back to the armoury/stock (empire-inventory-surfaces `cargo-commands`
    /// §Design 1 — legion → armoury/stock). Resolves Data-side post-Step, never in TurnEngine.
    /// </summary>
    public const string UnloadCargo = "unload-cargo";

    /// <summary>
    /// Seat the legion's commander (`commander-roster`'s legion half, spec-legion-commander.md): adds
    /// ONE `WorldEntityMemberRole.Commander` member to the named legion. Admission is Data-side (the
    /// role and the specimen's base are store facts) and RESOLUTION is Core-side at `Snapshot`, beside
    /// `bind-warden` — ownership settles last in the turn, so a legion lost the same turn is
    /// re-validated there.
    /// </summary>
    public const string AttachCommander = "attach-commander";

    /// <summary>
    /// Remove the legion's commander member (`spec-legion-commander.md`). Same split as
    /// <see cref="AttachCommander"/>: the specimen returns home, and the legion keeps fighting.
    /// </summary>
    public const string DetachCommander = "detach-commander";

    /// <summary>
    /// Move one cargo row legion → legion, same empire only (empire-inventory-surfaces
    /// `cargo-commands` §Design 1). Resolves Data-side post-Step, never in TurnEngine.
    /// </summary>
    public const string TransferCargo = "transfer-cargo";

    /// <summary>
    /// Move one cargo row legion → sector vault (empire-inventory-surfaces `cargo-commands`
    /// §Design 1 — legion → sector vault). Resolves Data-side post-Step, never in TurnEngine.
    /// </summary>
    public const string DepositCargo = "deposit-cargo";

    /// <summary>
    /// Move one storage row sector vault → legion (empire-inventory-surfaces `cargo-commands`
    /// §Design 1 — sector vault → legion). Resolves Data-side post-Step, never in TurnEngine.
    /// </summary>
    public const string WithdrawCargo = "withdraw-cargo";

    /// <summary>
    /// Claim a fallen cache into a legion's cargo (empire-inventory-surfaces `cargo-commands`
    /// §Design 1 — fallen cache → legion; the converged kind name per Locked anchors). Distinct
    /// from <see cref="Claim"/> (sector-claim) by construction, never a second "claim".
    /// Resolves Data-side post-Step, never in TurnEngine.
    /// </summary>
    public const string ClaimCache = "claim-cache";

    public static readonly IReadOnlyList<string> All =
        new[] { StandFast, Move, Clear, Claim, Stance, Sustain, Build, Cede, BindWarden, Raise, Develop, Assault,
            LoadCargo, UnloadCargo, TransferCargo, DepositCargo, WithdrawCargo, ClaimCache };

    public static bool IsKnown(string? kind) =>
        kind != null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>
/// One order, as plain data. Every commander — the human, Zomboss, a clan — submits this same
/// shape through the same path, which is what keeps the AI honest and the engine ignorant of who
/// is playing.
///
/// Payload fields are optional and typed rather than a JSON blob: the set is small, the store
/// serializes it, and a typo becomes a compile error instead of a runtime surprise.
/// </summary>
public sealed record WorldCommand
{
    /// <summary>The faction issuing the order.</summary>
    public string CommanderId { get; init; } = "";

    /// <summary>Unique per commander per turn — the idempotency key for submission.</summary>
    public string CommandId { get; init; } = "";

    public string Kind { get; init; } = "";

    /// <summary>Subject of the order, when it has one.</summary>
    public string? EntityId { get; init; }

    /// <summary>Required by `clear`: the sector the order is about, so a stale client is caught.</summary>
    public string? SectorId { get; init; }

    public int? SlotIndex { get; init; }

    /// <summary>The posture a `stance` order is asking for.</summary>
    public string? Stance { get; init; }

    /// <summary>Ordered lane ids for a march (W9).</summary>
    public IReadOnlyList<string> LanePath { get; init; } = Array.Empty<string>();

    /// <summary>How much carried loam a `sustain` order spends (spec-loam-legions.md).</summary>
    public long? Amount { get; init; }

    /// <summary>Which structure a `build` order names (spec-loam-structures.md).</summary>
    public string? StructureId { get; init; }

    /// <summary>
    /// The value a `bind-warden` order writes into `WorldSector.WardenBindingId` — the bound creature
    /// contract's own instance id, unchanged (spec-loam-texture.md, world-stage W28/W29's two-step
    /// contract-then-order flow). Opaque to Core: nothing here validates it against a creature roster,
    /// the same way `StructureId` is validated only inside the `build` admission arm, not generically.
    /// </summary>
    public string? WardenId { get; init; }

    /// <summary>`attach-commander`: the specimen to seat — `rpg_unique_actors.instance_id`, stamped by
    /// the Data-side admission together with the two facts Core cannot read (its species and level;
    /// the member's `Hp` is this world layer's own recruit figure, not a derived combat stat). Core is
    /// opaque to the store, which is why these ride the command.</summary>
    public string? MemberInstanceId { get; init; }

    /// <summary>See <see cref="MemberInstanceId"/>.</summary>
    public string? MemberSpeciesId { get; init; }

    /// <summary>See <see cref="MemberInstanceId"/>.</summary>
    public int? MemberLevel { get; init; }

    /// <summary>Which project a `develop` order names (spec-sector-development.md §3, world-map W52).</summary>
    public string? ProjectId { get; init; }

    /// <summary>Which relic instance(s) a `build` order spends when the named structure is a Wonder
    /// (`StructureDef.RelicCost &gt; 0`, loam-relics-and-wonders `wonder-build-flow` §Design 1). Opaque
    /// to Core: this field carries rpg_item.instance_id strings, but Core never reads FusionRpg.Data,
    /// so it cannot verify ownership or reachability here — only the STRUCTURAL count (§Design 3).
    /// Ownership and reachability are verified in FusionRpg.Data before the command ever reaches
    /// TurnEngine.Step (§Design 4). Empty for every non-Wonder build order, unaffected.</summary>
    public IReadOnlyList<string> RelicInstanceIds { get; init; } = Array.Empty<string>();

    /// <summary>Cargo-row selector for `unload-cargo`/`transfer-cargo`/`deposit-cargo`/
    /// `withdraw-cargo` (empire-inventory-surfaces `cargo-commands` §Design 2). NOT `SlotIndex`
    /// (that names a sector slot — conflating the two id-spaces repeats the `ward`/`bind-warden`
    /// collision repaired once already).</summary>
    public int? Seq { get; init; }

    /// <summary>Destination legion for `transfer-cargo` (`EntityId` is the source;
    /// empire-inventory-surfaces `cargo-commands` §Design 2).</summary>
    public string? TargetEntityId { get; init; }

    /// <summary>Target cache for `claim-cache` (empire-inventory-surfaces `cargo-commands`
    /// §Design 2). Never trusted without the verb's own reachability re-check.</summary>
    public string? CacheId { get; init; }

    /// <summary>`load-cargo` item shape: `'instance'` | `'stack'` — the same two-kind split every
    /// overlay table uses (empire-inventory-surfaces `cargo-commands` §Design 2).</summary>
    public string? CargoKind { get; init; }

    /// <summary>`load-cargo` `kind='instance'` only: the armoury instance to load
    /// (empire-inventory-surfaces `cargo-commands` §Design 2).</summary>
    public string? InstanceId { get; init; }

    /// <summary>`load-cargo` `kind='stack'` only: the stock container to draw from
    /// (empire-inventory-surfaces `cargo-commands` §Design 2).</summary>
    public string? ContainerId { get; init; }

    /// <summary>`load-cargo` `kind='stack'` only: how much to draw. `long`, matching
    /// `LegionCargoRow.Qty` (empire-inventory-surfaces `cargo-commands` §Design 2).
    /// Deliberately absent: `weightEach` is never on the wire — weight resolves server-side at
    /// resolve time (spec §Design 4, D3), or a client could mint capacity.</summary>
    public long? Qty { get; init; }
}
