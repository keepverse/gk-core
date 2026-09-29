using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

/// <summary>Bump when EffectEvent / IntentPlan / Grant DTO shapes break. v2 adds FA10 ApplyResourceDelta.</summary>
public static class FoundationContractVersion
{
    public const int Current = 2;
}

/// <summary>
/// ⛔ <b>The one declaration site for the 13 trigger strings.</b> Owner ruling 2026-09-16: "we only
/// have atom ssot" — the atom vocabulary is the SSOT for what an atom's <c>when</c> may name, and
/// <c>FusionRpg.Core.Effects.Atoms.AtomTriggers</c> is that surface. The literals live HERE, one
/// assembly down, because <c>EffectBag</c> matches triggers BY STRING across the Core/Contracts
/// boundary: Core references Contracts, Contracts references nothing, so Contracts is the only
/// assembly both sides can see. <c>AtomTriggers</c>' members are aliases of these
/// (<c>= EffectTriggers.X</c>), so the atom-facing name stays the SSOT while the literal exists once.
///
/// <para>⚠️ Until 2026-09-16 both classes declared all 13 literals, each with a comment telling the
/// next reader they "must be byte-identical." That is two hand-kept copies of a closed vocabulary —
/// the exact shape this repo bans — held together by a pairwise parity test. The test was the right
/// guard for a duplicate that could not be removed and the wrong answer once it could. Aliasing
/// removed the duplicate; the parity test is kept, and now passes by construction rather than by
/// vigilance, so it fails if anyone re-introduces a literal on the Core side.</para>
///
/// <para><b>Adding a trigger is a reviewed vocabulary change</b>, not an edit: the list is closed
/// (spec-trigger-vocabulary.md), <c>AtomTriggers.All</c> and its category arrays must gain the member
/// too, and <c>/effects/contract</c> reflects this class's public constants directly onto the wire.</para>
/// </summary>
public static class EffectTriggers
{
    public const string OnSpawn = "OnSpawn";
    public const string OnDamageDealt = "OnDamageDealt";
    public const string OnDamageTaken = "OnDamageTaken";
    public const string OnDeath = "OnDeath";
    public const string OnGranted = "OnGranted";
    public const string OnRemoved = "OnRemoved";
    public const string OnTimer = "OnTimer";

    /// <summary>E33 (spec-activation-edge.md §2.1): an actor's own decision to act. Added A18b.
    /// <c>AtomTriggers.OnActivate</c> aliases this constant, so the two cannot differ — the older note
    /// here asked the reader to keep them byte-identical by hand, which is no longer a thing that can
    /// go wrong.</summary>
    public const string OnActivate = "OnActivate";

    // E34 (spec-trigger-vocabulary.md §2.1): five match/board-economy triggers — input only, no kind
    // or executor of their own (that is E35/E36's job). AtomTriggers aliases all five, same as
    // OnActivate above.
    public const string OnWave = "OnWave";
    public const string OnMatchStart = "OnMatchStart";
    public const string OnMatchEnd = "OnMatchEnd";
    public const string OnSunCollect = "OnSunCollect";
    public const string OnGridPlace = "OnGridPlace";
}

public static class EffectActions
{
    public const string ModifyStat = "ModifyStat";
    public const string ApplyStatus = "ApplyStatus";
    public const string ClearStatus = "ClearStatus";
    public const string SpawnEntity = "SpawnEntity";
    public const string BoardAction = "BoardAction";
    public const string SpawnGridItem = "SpawnGridItem";
    public const string ClearGridItem = "ClearGridItem";
    public const string SetBoxType = "SetBoxType";
    public const string Economy = "Economy";
    public const string ApplyResourceDelta = "ApplyResourceDelta";
    public const string GrantShield = "GrantShield";

    /// <summary>
    /// aura-skill-todo.md Phase 5 / TC2 — the derived-channel write. Unlike every opcode above it,
    /// this one is DECLARATIVE: nothing executes it. A `stat.derived` atom is a permanent modifier
    /// that declares no trigger (AtomKindRegistry: "Permanent modifier: declares no trigger"), so the
    /// bag never fires it; the grant's mere presence is the effect, folded at resolve time by
    /// `GrantedDerivedAtomReader` -> `AtomDerivedSubsystem` -> `ActorHub`. It therefore needs no sink
    /// executor, which is why adding it does not touch `InjectorEffectActionSink` or `BattleEffectSink`.
    /// </summary>
    public const string ModifyDerivedStat = "ModifyDerivedStat";

    /// <summary>
    /// E35 (spec-match-modify.md §2.5): match.modify's opcode — sets one `Board.config` field for the
    /// match through `CheatState` + `CheatActions.ApplyBoardConfig`. `/effects/contract`'s `actions`
    /// array already reflects every public const on this class (`DebugEndpoints.cs`,
    /// `PublicConstStrings(typeof(EffectActions))`), so this constant publishing itself is the whole
    /// of this module's own "grow the published list" obligation — no endpoint edit needed.
    /// </summary>
    public const string ModifyMatch = "ModifyMatch";

    /// <summary>
    /// E36 (spec-wave-control.md §2.1): wave.control's opcode — summon|huge|setTimer|hold, all four
    /// against existing `CheatActions`/`DebugActions` entry points, no new host write. Refused at
    /// `EffectEventDto.ChainDepth > 0` (§2.3) — `summon`/`huge` cause spawns, which re-emit the events
    /// that could re-trigger this same atom, and that loop cannot be diagnosed after the fact. Same
    /// reflection-published obligation as `ModifyMatch` above — declaring this constant is the whole
    /// of the "grow the published list" requirement.
    /// </summary>
    public const string WaveControl = "WaveControl";

    /// <summary>
    /// E37 (spec-projectile-control.md §2b): <c>bullet.modify</c>'s opcode — changes the damage/type/
    /// moveWay of a bullet the GAME fires (not one <c>spawn.entity{kind:bullet}</c> creates). Like
    /// <see cref="ModifyDerivedStat"/> this is DECLARATIVE: a permanent modifier with no trigger, read
    /// as a resolved grant inside <c>Bullet.InitData</c>'s existing postfix
    /// (<c>CheatPrefixes.BulletInitCheat</c>) rather than executed by either sink. Same reflection-
    /// published obligation as <see cref="ModifyMatch"/>/<see cref="WaveControl"/> — declaring this
    /// constant is the whole of the "grow the published /effects/contract list" requirement.
    /// </summary>
    public const string BulletModify = "BulletModify";

    /// <summary>
    /// E41 (spec-ui-attach-point.md §2b): <c>ui.present</c>'s opcode. Bag-side, the same shape
    /// <see cref="GrantShield"/> and <see cref="ApplyResourceDelta"/> already use — <c>EffectBag.FireGrant</c>
    /// handles it inline and it never becomes an <c>EffectActionPlanItem</c> that reaches
    /// <c>InjectorEffectActionSink</c>'s stat/resource/status/shield/board arms, which is what makes the
    /// <c>Ui</c> attach point's read-only rule structural rather than a convention nobody enforces.
    /// `op:number` reuses the existing <c>IDamageFxSink</c> floater path; `op:meter`/`op:banner` go
    /// through the new <c>IUiPresentSink</c>. Same reflection-published obligation as
    /// <see cref="ModifyMatch"/>/<see cref="WaveControl"/>/<see cref="BulletModify"/> — declaring this
    /// constant is the whole of the "grow the published /effects/contract list" requirement.
    /// </summary>
    public const string PresentUi = "PresentUi";

    /// <summary>
    /// base-defense `siege-construction` (decision 27, 2026-09-06): <c>structure.place</c>'s opcode.
    /// Acts on the tactical siege board (<c>FusionRpg.Core.Battle.Board.BoardState</c>), never Unity —
    /// the first opcode `BattleEffectSink` handles that `InjectorEffectActionSink` never implements at
    /// all (there is no Lawn executor for this one; see `FusionRpg.Core.Effects.Atoms.AttachPoint.Siege`'s
    /// own doc comment). Same reflection-published
    /// obligation as <see cref="ModifyMatch"/>/<see cref="WaveControl"/>/<see cref="BulletModify"/>/
    /// <see cref="PresentUi"/> — declaring this constant is the whole of the "grow the published
    /// `/effects/contract` list" requirement.
    /// </summary>
    public const string PlaceStructure = "PlaceStructure";
}

public static class EffectTypes
{
    public const string Passive = "Passive";
    public const string Triggered = "Triggered";
}

/// <summary>
/// Owner key grammar: <c>match</c>, <c>plant:{typeId}</c>, <c>zombie:{typeId}</c>, <c>entity:{ptr}</c>,
/// <c>player:{id}</c>, plus the side-wide <c>plant:*</c> / <c>zombie:*</c> pair.
///
/// <para><c>instance:{id}</c> is the one DURABLE key in the grammar and the one the hot path refuses
/// (<c>EffectBag.Grant</c> throws on it) — it names a persistent actor that has no live pointer yet, so
/// it only ever travels between the Server and a deploy binder. <c>UniqueOwnerBinder</c> rewrites it to
/// <see cref="Entity"/> once the pointer exists.</para>
///
/// <para>⚠️ <b>The literals below are the ONE spelling.</b> <c>FusionRpg.Core.Effects.EffectOwnerKey</c>'s
/// members alias these (<c>= EffectOwnerKeys.PlantSide</c>), the same shape <see cref="EffectTriggers"/>
/// uses — Core references Contracts, which references nothing, so a literal both assemblies can see has
/// to live here. A second hand-spelled copy is the duplicate this repo bans; a producer that needs
/// <c>plant:*</c> reads the constant, never a string literal.</para>
///
/// <para><b>Side-wide means ONE side, never both.</b> <c>plant:*</c> is not a synonym for
/// <c>match</c>: it is refused for every zombie. The apply gate and the grant-store lookup that
/// implement that live in <c>FusionRpg.Core.Stats.StatApplyScope</c> (<c>Matches</c> /
/// <c>OwnerKeyCovers</c> / <c>IsMatchWide</c>); read its doc before widening or reading either key.</para>
/// </summary>
public static class EffectOwnerKeys
{
    public const string Match = "match";

    /// <summary>The <c>ownerKind</c> that accompanies an <see cref="Instance"/> key, matching the shape
    /// <c>UniqueEquipmentCatalog.Grant</c> / <c>RelicCatalog.TryGetGrant</c> already ship.</summary>
    public const string InstanceKind = "instance";

    /// <summary>Side-wide plant key: every plant, any type id — the scope the grammar could not express
    /// before 2026-09-20 (creature-standalone PT7: the patron aura asked for <see cref="Match"/> and
    /// therefore also buffed the zombies). The <c>ownerKind</c> that accompanies it is the side name
    /// <c>FusionRpg.Core.Effects.Atoms.OwnerScope.Name(OwnerKind.Plant)</c> renders — the kind and the
    /// key must name the same side or the grant-store lookup finds the grant under neither.</summary>
    public const string PlantSide = "plant:*";

    /// <inheritdoc cref="PlantSide"/>
    public const string ZombieSide = "zombie:*";

    public static string PlantType(int typeId) => "plant:" + typeId;
    public static string ZombieType(int typeId) => "zombie:" + typeId;
    public static string Entity(string ptr) => "entity:" + ptr;
    public static string Player(long id) => "player:" + id;

    /// <summary>
    /// A durable actor key. Deliberately NOT resolvable on the hot path: <c>StatApplyScope.Matches</c>
    /// returns false for it and <c>EffectBag.Grant</c> throws, so a producer that stamps one and never
    /// binds it fails loudly rather than applying to the wrong scope.
    /// </summary>
    public static string Instance(string instanceId) => "instance:" + instanceId;
}

public sealed class EffectEventDto
{
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "";
    [JsonPropertyName("matchKey")] public string? MatchKey { get; set; }
    [JsonPropertyName("side")] public string? Side { get; set; }
    [JsonPropertyName("actorPtr")] public string? ActorPtr { get; set; }
    [JsonPropertyName("targetPtr")] public string? TargetPtr { get; set; }
    [JsonPropertyName("typeId")] public int? TypeId { get; set; }
    [JsonPropertyName("targetTypeId")] public int? TargetTypeId { get; set; }
    [JsonPropertyName("damage")] public long? Damage { get; set; }
    [JsonPropertyName("killerPtr")] public string? KillerPtr { get; set; }
    [JsonPropertyName("tick")] public long Tick { get; set; }
    [JsonPropertyName("scenarioId")] public string? ScenarioId { get; set; }
    [JsonPropertyName("chainDepth")] public int ChainDepth { get; set; }
    [JsonPropertyName("sourceGrantId")] public string? SourceGrantId { get; set; }
    /// <summary>Merged physical hits this event represents (v2 coalescing); 1 = a single hit.</summary>
    [JsonPropertyName("hitCount")] public int HitCount { get; set; } = 1;
    /// <summary>lawn-hit-attribution (T6): the SWING this OnDamageDealt event belongs to — a bullet's
    /// own ptr (hex) for a projectile hit, or <c>actorPtr:frame</c> for melee. Distinct from
    /// <see cref="ActorPtr"/> (the firing creature): one swing can carry N dealt events (one bullet
    /// piercing N victims, or one multi-target bite), all sharing this id, so a future consumer can
    /// fire one action trigger for N damage applications rather than N triggers. Additive nullable —
    /// breaks no existing shape, every trigger that predates this field carries null.</summary>
    [JsonPropertyName("swingId")] public string? SwingId { get; set; }
    /// <summary>E34 (spec-trigger-vocabulary.md §2.2): the wave number for OnWave. Additive nullable —
    /// breaks no existing shape, so FoundationContractVersion.Current stays at its current value.</summary>
    [JsonPropertyName("wave")] public int? Wave { get; set; }

    /// <summary>
    /// base-defense `siege-construction` (decision 27, 2026-09-06): the tactical siege board cell this
    /// event targets — `structure.place`'s own target, chosen at declare time (adjacent to the acting
    /// unit, `ConstructionPlacement.CanPlace`-validated), never authored content. Plain ints, not
    /// `GridPos`: this project cannot reference `FusionRpg.Core.Actions` (the dependency runs the other
    /// way), the same boundary `grid.spawn`'s own bare `row`/`col` atom PARAMS already cross — those are
    /// static content, though, while these two are per-event and dynamic, which is why they live on the
    /// EVENT rather than an atom param. Both null for every trigger that predates this field (every
    /// existing `EffectEventDto` construction site) — additive nullable, breaks no existing shape, so
    /// `FoundationContractVersion.Current` stays at its current value.
    /// </summary>
    [JsonPropertyName("targetRow")] public int? TargetRow { get; set; }

    /// <summary>See <see cref="TargetRow"/>.</summary>
    [JsonPropertyName("targetCol")] public int? TargetCol { get; set; }

    /// <summary>
    /// lawn-hit-entry (T9a, D8): true for exactly ONE of the N drained events sharing one
    /// <see cref="SwingId"/> — a piercing bullet hitting five zombies produces five events, all
    /// <see cref="SwingId"/>-equal, and exactly one has this true. A consumer that wants "the cost
    /// is paid once per swing" (e.g. a future stamina charge) gates on this flag; the elemental
    /// rider itself must NOT gate on it (every victim still needs its own delta). Additive,
    /// defaults <c>true</c> — every trigger that predates this field, and every trigger with no
    /// swing semantics at all (OnSpawn, taken-side, etc.), reads as "always fires", matching how
    /// those triggers already behaved with no dedupe concept.
    /// </summary>
    [JsonPropertyName("isFirstOfSwing")] public bool IsFirstOfSwing { get; set; } = true;

    /// <summary>
    /// lawn-hit-entry (T9c): mirrors <c>GameEventRec.InstakillShaped</c> — true when the engine's
    /// own <c>DamageType</c> marks this hit as instakill-shaped (lawnmower / board-wipe).
    /// <c>DamagePacketBuilder</c> reads this to refuse an event-linked ("proportional") rider for
    /// the hit; nothing else on this DTO changes. Additive, defaults <c>false</c>.
    /// </summary>
    [JsonPropertyName("instakillShaped")] public bool InstakillShaped { get; set; }
}

public sealed class EffectGrantDto
{
    [JsonPropertyName("grantId")] public string GrantId { get; set; } = "";
    [JsonPropertyName("effectId")] public string EffectId { get; set; } = "";
    [JsonPropertyName("ownerKind")] public string OwnerKind { get; set; } = "match";
    [JsonPropertyName("ownerKey")] public string OwnerKey { get; set; } = EffectOwnerKeys.Match;
    [JsonPropertyName("pluginId")] public string PluginId { get; set; } = "debug";
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("overlay")] public Dictionary<string, object?>? Overlay { get; set; }
}

public sealed class EffectActionPlanItem
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("effectId")] public string EffectId { get; set; } = "";
    [JsonPropertyName("grantId")] public string GrantId { get; set; } = "";
    [JsonPropertyName("sourceTag")] public string SourceTag { get; set; } = "";
    [JsonPropertyName("params")] public Dictionary<string, object?> Params { get; set; } = new();
    [JsonPropertyName("tags")] public Dictionary<string, string> Tags { get; set; } = new();
}

public sealed class IntentPlanDto
{
    [JsonPropertyName("contractVersion")] public int ContractVersion { get; set; } = FoundationContractVersion.Current;
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "";
    [JsonPropertyName("actions")] public List<EffectActionPlanItem> Actions { get; set; } = new();
    [JsonPropertyName("skipped")] public List<string> Skipped { get; set; } = new();
}

public sealed class EffectFiredDto
{
    [JsonPropertyName("grantId")] public string GrantId { get; set; } = "";
    [JsonPropertyName("effectId")] public string EffectId { get; set; } = "";
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "";
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("ok")] public bool Ok { get; set; } = true;
    [JsonPropertyName("error")] public string? Error { get; set; }
}

public sealed class EffectCatalogSnapshotDto
{
    [JsonPropertyName("contractVersion")] public int ContractVersion { get; set; } = FoundationContractVersion.Current;
    [JsonPropertyName("defs")] public List<EffectDefDto> Defs { get; set; } = new();
    [JsonPropertyName("grants")] public List<EffectGrantDto> Grants { get; set; } = new();
    [JsonPropertyName("revision")] public int Revision { get; set; }
}

public sealed class EffectDefDto
{
    [JsonPropertyName("effectId")] public string EffectId { get; set; } = "";
    [JsonPropertyName("effectType")] public string EffectType { get; set; } = EffectTypes.Triggered;
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("sourceTag")] public string SourceTag { get; set; } = "";
    [JsonPropertyName("triggers")] public List<string> Triggers { get; set; } = new();
    [JsonPropertyName("actions")] public List<EffectDefActionDto> Actions { get; set; } = new();
}

public sealed class EffectDefActionDto
{
    [JsonPropertyName("seq")] public int Seq { get; set; }
    [JsonPropertyName("action")] public string Action { get; set; } = "";
    [JsonPropertyName("params")] public Dictionary<string, object?> Params { get; set; } = new();
}
