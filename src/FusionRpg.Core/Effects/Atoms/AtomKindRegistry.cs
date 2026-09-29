using System.Text.Json;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// The closed vocabulary: 9 attach points, 18 kinds. Eleven map to a shipped FA opcode;
/// <c>stat.derived</c> is the one addition, and it earns its place because patron auras, star
/// merges, expedition injuries, and contract ranks already write derived channels with no opcode.
///
/// Runtime cells are audited fact (2026-08-22 sweep), not aspiration: <c>InjectorEffectActionSink</c>
/// implements all ten opcodes, while <c>BattleEffectSink</c> states outright that "battle mode
/// consumes FA10 only; other actions are inert here".
/// </summary>
public static class AtomKindRegistry
{
    // Structural (tunables-ssot.md T2), all three: closed-vocabulary cardinalities, not balance —
    // each must match what this registry actually builds below, not a dial a balance pass turns.
    // E35 (spec-match-modify.md §2.1): 5 -> 6 with AttachPoint.Match. E41 (spec-ui-attach-point.md
    // §2a): 6 -> 7 with AttachPoint.Ui, Wave 8's stated combined end state (spec-match-modify.md
    // §2.1) — this module asserts only its own +1 delta, never the wave total.
    // base-defense `siege-construction` (decision 27, 2026-09-06): 7 -> 8 with AttachPoint.Siege, the
    // tactical-board counterpart to Board's Lawn-only reach (AttachPoint.Siege's own doc comment).
    // passive-tree `element-conversion` (D56, spec-element-conversion.md §2a, 2026-09-07): 8 -> 9 with
    // AttachPoint.Element, the first (and, today, only) Element-attached kind.
    public const int AttachPointCount = 9;
    // Structural (tunables-ssot.md T2) — see AttachPointCount above.
    // E35: 12 -> 13 with match.modify, the first Match-attached kind.
    // E36 (spec-wave-control.md §2.1): 13 -> 14 with wave.control, the second Match-attached kind.
    // No AttachPointCount change — E36 adds no attach point, it reuses E35's.
    // E37 (spec-projectile-control.md §2b): 14 -> 15 with bullet.modify, on the EXISTING Board attach
    // point (no AttachPointCount change here either — this module adds no attach point, §2b's own
    // "No new attach point" rule).
    // E41 (spec-ui-attach-point.md §2a): 15 -> 16 with ui.present, the first (and, today, only)
    // Ui-attached kind — AttachPointCount moves alongside it this time (see above).
    // base-defense `siege-construction` (decision 27, 2026-09-06): 16 -> 17 with structure.place, the
    // first (and, today, only) Siege-attached kind — AttachPointCount moves alongside it too.
    // passive-tree `element-conversion` (D56, spec-element-conversion.md §2b, 2026-09-07): 17 -> 18
    // with element.convert, the 18th kind — not "the 17th" (structure.place, unrelated, took that slot
    // the day before this spec was written; see that spec's own §0) — AttachPointCount moves alongside
    // it too.
    public const int KindCount = 18;
    // Structural (tunables-ssot.md T2) — see AttachPointCount above.
    // E34 (spec-trigger-vocabulary.md §2.1): 8 -> 13 with OnWave/OnMatchStart/OnMatchEnd/
    // OnSunCollect/OnGridPlace. Verified live before editing, per this module's own caution about
    // stale citations elsewhere in the spec.
    public const int TriggerCount = 13;

    /// <summary>Event triggers plus OnTimer plus OnActivate (A18b) — for
    /// resource.delta/status.apply/shield.grant, the exactly-three kinds this reaches. Board kinds
    /// stay on the narrower <see cref="AtomTriggers.Events"/> deliberately (H3: Battle is
    /// <see cref="RuntimeState.None"/> for all of them regardless of trigger, so widening their
    /// trigger list would authorize content nothing in battle can execute). E34 does NOT add its five
    /// new triggers here — resource.delta/status.apply/shield.grant resolve their target from the
    /// event, and a match-scoped event has no ptr, so widening these three would make G5's unguarded
    /// FindObjectsOfType&lt;Zombie&gt;() loop authorable (spec-trigger-vocabulary.md §2.3).</summary>
    static readonly string[] AllTriggers =
        { AtomTriggers.OnSpawn, AtomTriggers.OnDamageDealt, AtomTriggers.OnDamageTaken,
          AtomTriggers.OnDeath, AtomTriggers.OnTimer, AtomTriggers.OnActivate };

    /// <summary>E34 (spec-trigger-vocabulary.md §2.3): the board-attach kinds' own event set, widened
    /// by the five new match/board-economy triggers. These kinds already act with no entity in hand —
    /// explicit row/col params for the board kinds, FA9's own match-scoped write for resource.economy
    /// — the same property <see cref="AtomTriggers.MatchEvents"/>/<see cref="AtomTriggers.BoardEconomyEvents"/>
    /// require. Exactly the six kinds §2.3 names: spawn.entity, board.action, grid.spawn, grid.clear,
    /// box.set, resource.economy. status.clear stays on the plain <see cref="AtomTriggers.Events"/> —
    /// §2.3 does not name it.</summary>
    static readonly string[] EventsPlusMatchAndEconomy =
        AtomTriggers.Events.Concat(AtomTriggers.MatchEvents).Concat(AtomTriggers.BoardEconomyEvents).ToArray();

    /// <summary>
    /// The primary stat channels — eleven since E16, <b>twenty-three</b> since E38.
    ///
    /// <para>It was eight, and the documented nine were partly fiction: attackInterval,
    /// produceInterval and zombieSpeed were cheat-document keys written straight to the Unity field,
    /// bypassing the modifier bag, so no effect could reach them. E16 promoted all three, which is
    /// what makes "shoots faster" authorable at all. E38 (spec-entity-fields-12plus.md) repeated the
    /// same promotion for twelve more — including takeDmgMultiplier, the "takes +X% damage" knob
    /// every debuff design wants. Read from <see cref="StatChannels.All"/> rather than copied, so
    /// the two lists cannot drift.</para>
    /// </summary>
    public static readonly string[] PrimaryChannels = Stats.StatChannels.All;

    // ---- E29 vocabularies (spec-kind-value-guard.md §2) ---------------------------------------------
    //
    // Each is a `Func`, called fresh on every Validate — never cached into a field here — so a
    // vocabulary that grows (a new status registered, a new derived channel) widens what validates
    // with no guard edit (rule 2: "the guard resolves the SSOT; it never holds a copy"). Validation
    // runs at import/bind time, not the hit path, so re-deriving a registry per call costs nothing
    // that matters; `PvzStatsSheetComposer.CachedDerivedRegistry` shows the heavier AsyncLocal-cached
    // shape if that ever changes.

    /// <summary>267 registered derived channels (§1.1's headline case — `stat.derived` had no value
    /// check at all; `crit.rat` for `crit.rate` validated, bound, compiled, and wrote nothing).</summary>
    static IReadOnlyCollection<string> DerivedChannels() =>
        Stats.Derived.DerivedStatRegistry.CreateDefault().AllRegistered.Select(d => d.ChannelId).ToList();

    /// <summary>status-rail C3: live inject via <see cref="Status.StatusCatalogHub.Current"/> (24 ids
    /// today including <c>nerve.*</c>). Falls back to Bootstrap when hosts have not configured.
    /// A status legal here but inert on one runtime refuses at EXECUTE with a reason, never at load.</summary>
    static IReadOnlyCollection<string> StatusIds() =>
        Status.StatusCatalogHub.Current.All().Select(d => d.StatusId).ToList();

    /// <summary>5 — §2.1 corrects `AtomKindRegistry`'s own stale "which FA9 does not" claim about
    /// maxSun/maxMoney: `ExecEconomy` passes `currency` through unfiltered and `CheatActions.SetEconomy`
    /// (`CheatActions.cs:599-621`) handles both.</summary>
    static readonly string[] EconomyCurrencies = { "sun", "money", "points", "maxSun", "maxMoney" };

    /// <summary>3 authorable spellings for 2 behaviours. `ExecEconomy` (`InjectorEffectActionSink.cs`)
    /// treats literally any non-"add"/"+" string as "set" — the most damaging silent no-op in the set,
    /// because a typo like `op: "addd"` succeeds loudly at the wrong thing instead of failing at the
    /// right one. "set" is the vocabulary's canonical spelling for "not add".</summary>
    static readonly string[] EconomyOps = { "add", "+", "set" };

    /// <summary>6 — <see cref="Stats.Derived.DerivedStatChannels.ResourceIds"/>, the stat layer's own
    /// SSOT. Declared as the full 6 regardless of E28's own rollout state (only `hp` executes today) —
    /// a runtime gap is E28's reporting concern, not a reason to narrow what the schema accepts
    /// (rule 4; and this vocabulary must widen the moment E28 fix #1 ships, with no guard edit).</summary>
    static IReadOnlyCollection<string> ResourceChannels() => Stats.Derived.DerivedStatChannels.ResourceIds;

    /// <summary>3 — `EffectBag.cs:597-599`: any string that isn't "aura"/"innate" (case-insensitive)
    /// silently becomes "skill" today, so a typo'd `sourceClass: "arua"` succeeds as the wrong
    /// priority/refill-on-merge combination instead of failing loudly. "skill" is the vocabulary's
    /// canonical spelling for the fallback. (`shield.grant.element` needs no vocabulary here — it is
    /// already strict-parsed via `ElementRoster.TryParse` and refused at `EffectBag.cs:585-594`.)</summary>
    static readonly string[] ShieldSourceClasses = { "aura", "innate", "skill" };

    /// <summary>4 canonical spellings `DebugActions.BoardAction`'s switch matches after
    /// `ExecBoardAction`'s own substring normalization (`Contains("cherry") → cherry`, etc. —
    /// `InjectorEffectActionSink.cs`). The one shipped `board.action` atom already authors the
    /// canonical form; this is additive for existing content, and makes the normalization's non-
    /// canonical aliases (`"CreateCherryBomb"`, …) unauthorable going forward — a deliberate narrowing,
    /// not an oversight: a named refusal at load beats a substring match nobody can see failing.</summary>
    static readonly string[] BoardActionOps = { "freeze", "doom", "fireline", "cherry" };

    /// <summary>12 — the shipped `GridItemType` IL2CPP enum, reflected off the game's own
    /// `Assembly-CSharp.dll` 2026-09-03 (Core has no Unity reference, so this is Core's own mirror of
    /// that enum, not a second copy of anything already in Core — matching how `ElementTypeId` etc.
    /// already work). Ordinals: CraterDay=0, CraterNight=1, (2 unused), Ladder=3, ScaryPot=4,
    /// ScaryPot_plant=5, ScaryPot_zombie=6, Grave=7, IceBlock=8, ScaryPot_hypnoZombie=9,
    /// ScaryPot_obsidian=10, ScaryPot_gold=11, ScaryPot_red=12.</summary>
    static readonly string[] GridItemTypeValues =
        { "0", "1", "3", "4", "5", "6", "7", "8", "9", "10", "11", "12" };

    /// <summary>8 — the shipped `BoxType` IL2CPP enum, reflected the same way 2026-09-03:
    /// Grass=0, Water=1, Dirt=2, Roof=3, Stone=4, River=5, Dirt_water=6, Lava=7. This is also the
    /// evidence behind E28's content fix — `fx.set_dirt_box` authored `boxType: 1` (Water) and meant
    /// Dirt (2).</summary>
    static readonly string[] BoxTypeValues = { "0", "1", "2", "3", "4", "5", "6", "7" };

    /// <summary>E40 (spec-spawn-non-grid.md §2a): 7 — the closed <c>spawn.entity.kind</c> domain,
    /// widened from the original three (<c>plant</c>/<c>zombie</c>/<c>bullet</c>, the only kinds
    /// `ExecSpawnEntity`'s switch had arms for) to add <c>pet</c>/<c>bucket</c>/<c>coin</c>/
    /// <c>mower</c>. A widening, not a new kind: FA4's opcode, plan-item shape, coefficient row and
    /// executor switch all already exist, and a parallel kind would need every one of them again for
    /// no new semantics (§2a's own reasoning). This module adds no <see cref="KindCount"/> and no
    /// <see cref="AttachPointCount"/> entry.
    ///
    /// <para><c>coin</c> IS a member of this array — it passes the generic per-value membership check
    /// in <see cref="Validate"/> below — but that same method carries a separate, coin-specific block
    /// that refuses it anyway, by name and reason (§3: <c>CreateItem.SetCoin</c>'s call safety outside
    /// the game's own drop flow is UNVERIFIED, and this repo cannot run the live lawn session that
    /// would settle it). Leaving <c>coin</c> OUT of this array instead would refuse it as an
    /// unrecognised spelling — "not one of the N legal values" — which is the wrong reason and reads
    /// as a typo, not as a withheld capability pending proof.</para></summary>
    static readonly string[] SpawnEntityKinds =
        { "plant", "zombie", "bullet", "pet", "bucket", "coin", "mower" };

    /// <summary>E35 (spec-match-modify.md §2.3): the eleven `Board.config` fields
    /// `CheatActions.ApplyBoardConfig` writes (`CheatActions.cs:653-664`) — this kind's own value
    /// check, the same shape as G6's channel check, until E29's registry-backed per-kind check lands.
    /// Eight are integer per-mille ratios, two are integer ms, and `zombieStartAmmor` is the one true
    /// `long` magnitude on this kind (§2.3's own unit table).</summary>
    static readonly string[] MatchModifyFields =
    {
        "zombieHealthMultiplier", "zombieDamageMultiplier", "zombieSpeedMultiplier",
        "zombieCountMultiplier", "zombieStartAmmor", "plantModifyMin", "plantModifyMax",
        "zombieModifyMin", "zombieModifyMax", "waveInterval", "conveyInterval",
    };

    /// <summary>E36 (spec-wave-control.md §2.2): 4 — the ops this kind's own executor implements
    /// (`ExecWaveControl`, InjectorEffectActionSink.cs). Deliberately NOT wired as this ParamDef's
    /// <c>Vocabulary</c> the way every other string-vocabulary param above is: the generic Vocabulary
    /// message only echoes a count, and this kind's one real failure mode — naming the floor op
    /// "freeze" when it does not stop the clock — is exactly the `fx.set_dirt_box` class of defect
    /// (a name that says one thing while the executor does another), which needs to be named
    /// explicitly rather than merely refused. See the wave.control-specific block in
    /// <see cref="Validate"/>.</summary>
    static readonly string[] WaveControlOps = { "summon", "huge", "setTimer", "hold" };

    /// <summary>E36 (spec-wave-control.md §2.1): <c>Concat(AtomTriggers.Events, AtomTriggers.MatchEvents)</c>
    /// verbatim — the four board-hit events plus the three match-scoped ones, OnWave chief among them
    /// (this is the kind E34's OnWave exists to drive). Deliberately narrower than
    /// <see cref="EventsPlusMatchAndEconomy"/>: wave.control has no board-economy meaning
    /// (OnSunCollect/OnGridPlace), so widening to that six-member set would authorize content with no
    /// executor reason to exist.</summary>
    static readonly string[] EventsPlusMatch =
        AtomTriggers.Events.Concat(AtomTriggers.MatchEvents).ToArray();

    /// <summary>
    /// E37 (spec-projectile-control.md §2a, criterion 0): the real, complete <c>BulletMoveWay</c>
    /// enum — 18 members, swept 2026-09-04 with <c>ilspycmd -t BulletMoveWay</c> against three
    /// independent sources that agree byte-for-byte (both `study/` reference interop DLLs and the
    /// live game's own generated interop at
    /// <c>H:\Games\PVZ-Fusion-3.9_MelonLoader\MelonLoader\Il2CppAssemblies\Assembly-CSharp.dll</c>).
    /// Recorded in <c>docs/research/effect-runtime/03-status-and-spawn-surface.md</c>. Supersedes the
    /// old, never-swept <c>right|left|up|down|track</c> guess this spec's own history names as the
    /// exact defect it exists to avoid repeating (E17's SetCharm* precedent). Authored as the EXACT
    /// enum member name, unrenamed — the sink parses it straight through
    /// <c>Enum.TryParse&lt;BulletMoveWay&gt;(value, ignoreCase: false)</c>, so a spelling this array
    /// does not carry can never reach an unmatched cast at execute (it is refused at load instead, by
    /// the generic Vocabulary loop in <see cref="Validate"/>).
    /// </summary>
    static readonly string[] BulletMoveWayValues =
    {
        "MoveRight", "Puff", "MoveRight_threePeater", "Track", "Fly", "Free", "Left", "Split_left",
        "Throw", "Cannon", "PeaNut", "Stable", "SmoothTrack", "Sin", "Spin", "Jump", "SuperGatling",
        "None",
    };

    /// <summary>E37 (spec-projectile-control.md §2b): the three ops <c>bullet.modify</c>'s own
    /// resolved-read executor (<c>CheatPrefixes.BulletInitCheat</c>) implements. <c>scale</c> is
    /// per-mille (amount 1500 = x1.5); <c>set</c>/<c>add</c> are whole damage units.</summary>
    static readonly string[] BulletModifyOps = { "set", "add", "scale" };

    /// <summary>E41 (spec-ui-attach-point.md §2b): the three ops <c>ui.present</c>'s own executor
    /// (<c>EffectBag.ExecPresentUi</c>) implements. Wired as this ParamDef's own Vocabulary — unlike
    /// <see cref="WaveControlOps"/>, none of the three has a plausible-but-wrong name worth a
    /// dedicated named refusal, so the generic membership loop in <see cref="Validate"/> is enough.</summary>
    static readonly string[] UiPresentOps = { "number", "banner", "meter" };

    /// <summary>E41 (spec-ui-attach-point.md §2b): the eleven <see cref="FusionRpg.Contracts.DamageFxTag"/>
    /// names, lowercased — <c>tag</c>'s own vocabulary, reusing the closed set <c>DamageFxPalette.Rgb</c>
    /// already colours rather than inventing a parallel one (§2b.1's own "no new palette" rule).
    /// Derived from the enum rather than re-listed: <see cref="FusionRpg.Contracts.DamageFxTag"/> is the
    /// closed vocabulary that owns the names, so a twelfth tag cannot be added to the enum and silently
    /// missed here. Source-scan guard: <c>UiPresentTests.UiPresent_tag_vocabulary_declares_no_literal_of_its_own</c>.</summary>
    static readonly string[] UiPresentTagValues =
        Enum.GetNames<FusionRpg.Contracts.DamageFxTag>()
            .Select(n => n.ToLowerInvariant())
            .ToArray();

    /// <summary>
    /// E41 (spec-ui-attach-point.md §2b.1, "the two vocabularies, and neither is a tuning table"):
    /// <c>bannerId</c>'s own SSOT. DECIDED 2026-09-03 that the real home is the shipped i18n catalog's
    /// <c>banner.</c> prefix (<c>gk-web/web/fusion-rpg-web/src/i18n/locales/en/messages.po</c>) — but that
    /// file, read directly while this module was built, is a gettext <c>.po</c> extracted by
    /// <c>@lingui/cli</c>: <c>msgid</c> IS the source sentence, not a stable <c>"banner.xxx"</c> key,
    /// and it carries no <c>banner.</c>-prefixed entries today. A grep for <c>.po</c>/<c>messages.po</c>
    /// across <c>src/</c> finds no C# reader anywhere (only a bundled web asset matches "i18n", not a
    /// catalog reader), and Core has no established pattern for reading a <c>web/</c>-relative file at
    /// runtime (checked: no repo-root resolver exists in <c>FusionRpg.Core</c>) — a deployed server
    /// does not even ship the <c>web/</c> sources. §2b.1's own "criteria-stated task" (whether the
    /// in-game HUD renderer can resolve a key at all) needs the injector build and is explicitly not
    /// this module's to attempt.
    ///
    /// <para><b>Best-effort default per that section:</b> the closed-vocabulary SHAPE ships now —
    /// Vocabulary-checked, refused at load if unknown, exactly like every other id in this file — but
    /// the SET starts empty, because no banner id is authored anywhere yet; legalising one
    /// prematurely would be inventing a parallel catalog, which §2b explicitly forbids. The day a
    /// banner id is authored — a resolved catalog reader, or the seed-file fallback §2b.1's "Fail"
    /// branch names (<c>data/seed/hud-banners/</c>) if the HUD renderer turns out to need one — it is
    /// added here and nothing else on this kind changes.</para>
    /// </summary>
    static readonly string[] UiPresentBannerCatalogIds = Array.Empty<string>();

    /// <summary>base-defense `siege-construction`: every known structure id, read fresh from the SSOT
    /// catalog rather than copied — a structure minted after this kind was authored validates against
    /// the same list this line always evaluates.</summary>
    static IReadOnlyCollection<string> StructureIds() =>
        StructureCatalog.All.Select(s => s.StructureId).ToList();

    static readonly Dictionary<string, AtomKind> Kinds = Build();

    public static IReadOnlyCollection<AtomKind> All => Kinds.Values;

    public static AtomKind? Get(string kindId) =>
        kindId is not null && Kinds.TryGetValue(kindId, out var k) ? k : null;

    /// <summary>Validate a kind id and its params. Unknown kind is a refusal, never a skip.</summary>
    public static AtomRejection Validate(string kindId, IReadOnlyDictionary<string, object?> parameters)
    {
        var kind = Get(kindId);
        if (kind is null)
            return AtomRejection.Fail(AtomRejectionReason.UnknownKind, kindId ?? "(null)");

        var pars = parameters ?? new Dictionary<string, object?>();

        var shape = kind.Params.Validate(pars);
        if (!shape.IsOk) return shape;

        // E29 (spec-kind-value-guard.md): a value from an enumerable vocabulary — status id,
        // currency, board verb, grid item type — that is not a member is a load-time refusal, not a
        // silent no-op. One generic loop over every param carrying a declared Vocabulary, reading
        // each SSOT fresh (never a copy) so a vocabulary that grows widens what validates with no
        // guard edit. This is G6 generalised: `stat.modify.channel`'s own check (the original G6) now
        // runs through this exact loop via its Vocabulary declaration below, rather than as its own
        // special case — proof the generic mechanism produces identical behaviour to the hand-rolled
        // one it replaces, not just a template for the other twelve.
        foreach (var def in kind.Params.Defs)
        {
            if (def.Vocabulary is null) continue;
            if (!pars.TryGetValue(def.Name, out var raw) || raw is null) continue; // absence is ParamSchema's job

            // E30 (spec-channel-pool.md §3.2): a JSON OBJECT value here is a pool reference, never a
            // scalar member of this string vocabulary — `Convert.ToString` on the whole object would
            // stringify it to `{"pool":"...","count":...}` and always fail this check, refusing every
            // valid pool reference before AtomRowValidator's own pool-specific check (§3.3) ever runs.
            // Skipped, not evaluated-and-passed: the pool form's OWN members are checked against this
            // exact same vocabulary by ValidateChannelPoolRef, so nothing here goes unchecked.
            if (raw is JsonElement { ValueKind: JsonValueKind.Object } or Dictionary<string, object?>)
                continue;

            var members = def.Vocabulary();
            var value = Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            if (!members.Contains(value, StringComparer.Ordinal))
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"{kindId}.{def.Name} '{value}' is not one of the {members.Count} legal values for this param");
        }

        // E40 (spec-spawn-non-grid.md §3, "Do not claim the coin path before proving it"): kind=coin
        // IS a legal domain member (SpawnEntityKinds above), so a naming mistake reads as this kind's
        // own BadParamValue-with-a-typo-list, not as this refusal — but CreateItem.SetCoin's call
        // safety OUTSIDE the game's own drop flow is UNVERIFIED. The only clue in the tree is
        // GameCaptureHooks.cs's SetCoinHook, a capture-only Postfix ("No consumer outside debug
        // sessions... ~per-kill rate") that says nothing about call safety either way — silence, not
        // evidence. This repo cannot run the live lawn session that would settle it (spec's own
        // instruction), so the arm is refused HERE, at load, by name and reason, rather than shipped
        // inert — accepted at load, doing nothing or something unproven at execute. Lift this the
        // moment an owner-run live-lawn proof clears it.
        if (string.Equals(kindId, "spawn.entity", StringComparison.Ordinal)
            && pars.TryGetValue("kind", out var spawnKindRaw))
        {
            var spawnKindStr = spawnKindRaw is JsonElement spawnKindEl
                && spawnKindEl.ValueKind == JsonValueKind.String
                ? spawnKindEl.GetString()
                : spawnKindRaw?.ToString();

            if (string.Equals(spawnKindStr, "coin", StringComparison.OrdinalIgnoreCase))
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    "spawn.entity.kind 'coin' is refused at load: CreateItem.SetCoin's call safety " +
                    "outside the game's own drop flow is UNVERIFIED (spec-spawn-non-grid.md §3) — " +
                    "never shipped inert. Lift once an owner-run live-lawn proof clears it.");
        }

        // A18e (spec-battle-live-stat-modifiers.md §4): "effects cannot emit Override" was a doc
        // comment on this kind's own description with nothing enforcing it -- found while building
        // this module's own bind-time-refusal test. Same shape as G6's channel check, immediately
        // above: a validated-but-never-checked claim is the exact defect this method exists to catch.
        if (string.Equals(kindId, "stat.modify", StringComparison.Ordinal)
            && pars.TryGetValue("op", out var op)
            && string.Equals(op?.ToString(), "override", StringComparison.OrdinalIgnoreCase))
        {
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                "stat.modify ops are Flat|Increased|More — Override is not a legal op for this kind " +
                "(it has no revert path; a permanent Override would leak, the same reason OnGranted/" +
                "OnRemoved are lifecycle states, not authorable triggers, on this same kind).");
        }

        // E36 (spec-wave-control.md §2.2/§4): wave.control's own op vocabulary plus the two per-op
        // range checks (`wave` is an ORDINAL, `timerMs` is a non-negative duration) — the same "own
        // value check" shape as stat.modify's Override refusal immediately above. `op` carries no
        // Vocabulary declaration on its ParamDef (see WaveControlOps' own doc comment for why); this
        // is where it is actually enforced, with a message that names the fx.set_dirt_box-class
        // defect explicitly when the offending value is "freeze".
        if (string.Equals(kindId, "wave.control", StringComparison.Ordinal))
        {
            var opRaw = pars.TryGetValue("op", out var opVal) ? opVal : null;
            var opStr = opRaw is JsonElement opEl && opEl.ValueKind == JsonValueKind.String
                ? opEl.GetString()
                : opRaw?.ToString();

            if (opStr is not null && !WaveControlOps.Contains(opStr, StringComparer.OrdinalIgnoreCase))
            {
                var freezeNote = string.Equals(opStr, "freeze", StringComparison.OrdinalIgnoreCase)
                    ? " The floor op is 'hold': it floors the wave timer at a configured minimum " +
                      "every tick, it does not stop it (CheatActions.cs's own F-WAVE-FREEZE " +
                      "handling) — naming it 'freeze' would repeat the fx.set_dirt_box class of " +
                      "defect, a name that says one thing while the executor does another."
                    : "";
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"wave.control.op '{opStr}' is not one of the four legal ops: summon, huge, " +
                    $"setTimer, hold.{freezeNote}");
            }

            if (string.Equals(opStr, "summon", StringComparison.OrdinalIgnoreCase)
                && pars.TryGetValue("wave", out var waveRaw) && TryInt(waveRaw, out var wave)
                && wave < 1)
            {
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"wave.control.wave '{wave}' is a wave ORDINAL, not a magnitude — it must be >= 1");
            }

            if (string.Equals(opStr, "setTimer", StringComparison.OrdinalIgnoreCase)
                && pars.TryGetValue("timerMs", out var timerRaw) && TryInt(timerRaw, out var timerMs)
                && timerMs < 0)
            {
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"wave.control.timerMs '{timerMs}' must be >= 0 (integer ms, divided by 1000 " +
                    "once, at the Unity boundary)");
            }
        }

        // E41 (spec-ui-attach-point.md §2b/§4): ui.present's own per-op required-param and range
        // checks — the same "own value check" shape as wave.control's op-specific block immediately
        // above. `amount`/`ratio` cannot be plain ParamDef.Required=true because each is required only
        // under ONE op, not unconditionally; a range check (ratio's 0-1000 bound, a bounded ratio per
        // §3, never a progression ceiling) has no home in the generic Vocabulary membership loop
        // either, since it is a range, not a set.
        if (string.Equals(kindId, "ui.present", StringComparison.Ordinal))
        {
            var opRaw = pars.TryGetValue("op", out var opVal) ? opVal : null;
            var opStr = opRaw is JsonElement opEl && opEl.ValueKind == JsonValueKind.String
                ? opEl.GetString()
                : opRaw?.ToString();

            if (string.Equals(opStr, "number", StringComparison.OrdinalIgnoreCase)
                && !pars.ContainsKey("amount"))
                return AtomRejection.Fail(AtomRejectionReason.MissingParam,
                    "ui.present op:number needs an explicit amount");

            if (string.Equals(opStr, "meter", StringComparison.OrdinalIgnoreCase))
            {
                if (!pars.ContainsKey("ratio"))
                    return AtomRejection.Fail(AtomRejectionReason.MissingParam,
                        "ui.present op:meter needs an explicit ratio");

                if (pars.TryGetValue("ratio", out var ratioRaw) && TryRatioMilli(ratioRaw, out var ratioMilli)
                    && (ratioMilli < 0 || ratioMilli > 1000))
                    return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                        $"ui.present.ratio {ratioMilli} is per-mille, bounded 0-1000 (a fraction of the " +
                        "meter's own pool, never a magnitude that could exceed the pool it measures)");
            }
        }

        return AtomRejection.Ok;
    }

    /// <summary>
    /// E41: reads `ratio` either as a plain boxed number (tests, mirroring <see cref="TryInt"/>) or as
    /// a value-spec object's own `min` (the shape a content-authored <c>{"min":..,"max":..}</c> takes
    /// at this layer — <c>ratio</c> is <see cref="ParamKind.Value"/>, and the bound applies to both
    /// ends of an authored range the same way <c>Min &lt;= Max</c> already does).
    /// </summary>
    static bool TryRatioMilli(object? v, out int result)
    {
        switch (v)
        {
            case int i: result = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: result = (int)l; return true;
            case JsonElement { ValueKind: JsonValueKind.Number } je: return je.TryGetInt32(out result);
            case JsonElement { ValueKind: JsonValueKind.Object } je:
                if (je.TryGetProperty("min", out var minEl) && minEl.ValueKind == JsonValueKind.Number)
                    return minEl.TryGetInt32(out result);
                result = 0;
                return false;
            case Dictionary<string, object?> d when d.TryGetValue("min", out var minRaw):
                return TryRatioMilli(minRaw, out result);
            default: result = 0; return false;
        }
    }

    /// <summary>Read an int out of either a plain boxed number (tests) or a wire `JsonElement`
    /// (the real load path) — mirrors <c>AtomRowValidator.TryInt</c>, duplicated rather than shared
    /// because that one is private to a different class with a different constructor shape.</summary>
    static bool TryInt(object? v, out int result)
    {
        switch (v)
        {
            case int i: result = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: result = (int)l; return true;
            case JsonElement { ValueKind: JsonValueKind.Number } je: return je.TryGetInt32(out result);
            default: result = 0; return false;
        }
    }

    /// <summary>Validate that a kind may carry a trigger. Unknown or disallowed both reject.</summary>
    public static AtomRejection ValidateTrigger(string kindId, string? trigger)
    {
        var kind = Get(kindId);
        if (kind is null)
            return AtomRejection.Fail(AtomRejectionReason.UnknownKind, kindId ?? "(null)");
        if (!AtomTriggers.IsKnown(trigger))
            return AtomRejection.Fail(AtomRejectionReason.UnknownTrigger, trigger ?? "(null)");
        if (!kind.AllowsTrigger(trigger))
            return AtomRejection.Fail(AtomRejectionReason.TriggerNotAllowed, $"{kindId} cannot carry {trigger}");
        return AtomRejection.Ok;
    }

    static Dictionary<string, AtomKind> Build()
    {
        var kinds = new AtomKind[]
        {
            // ---- Stat ----------------------------------------------------------------------
            new("stat.modify", AttachPoint.Stat, new ParamSchema(
                    // G7: a missing channel used to silently default to "atk". Required now.
                    // E29 (was G6): an unknown PRIMARY channel used to pass validation and then write
                    // nothing, because ModifierBag.Upsert only checks for a non-empty name.
                    new ParamDef("channel", ParamKind.String, Required: true,
                        Vocabulary: () => PrimaryChannels),
                    new ParamDef("op", ParamKind.String, Required: true),
                    new ParamDef("amount", ParamKind.Value, Required: true)),
                // A18e (spec-battle-live-stat-modifiers.md §4): Battle was None -- battle's sink
                // ignored FA1 outright. A live, sourced/revertible-on-removal modifier ledger
                // (BattleStatModifierLedger) now composes triggered stat.modify grants through the
                // same PhasedComposeStrategy the overlay's own primary stat system uses. This
                // module's own explicit call, not A18b's: widening AllTriggers here is a kind gaining
                // trigger eligibility it never had, distinct from OnActivate existing at all.
                // Permanent, no-trigger modifiers (definitions.md §14.2) keep working exactly as
                // before -- but a naive widen broke them: AtomRowValidator.ValidateWhen infers "trigger
                // REQUIRED" from Triggers.Count > 0 (found by running the existing ChannelExtensionTests
                // fixture, not read from the code first), which had never needed to distinguish
                // "triggers ALLOWED" from "trigger REQUIRED" before this kind. TriggerOptional: true
                // (AtomKind.cs) is the fix -- stat.modify is the one kind that needs both shapes at once.
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.Full, RuntimeState.PlanOnly),
                AllTriggers,
                PowerCategory.Offense | PowerCategory.Survivability,
                "FA1. Ops are Flat|Increased|More — effects cannot emit Override (enforced at bind, " +
                "Validate's own stat.modify check). Battle's sink now handles FA1 through a live " +
                "modifier ledger (A18e). Permanent modifier: declares no trigger (definitions.md §14.2) " +
                "— OR is now triggered, contributing from first fire onward (A18e's own scope).",
                TriggerOptional: true),

            new("stat.derived", AttachPoint.Stat, new ParamSchema(
                    // E29 (spec-kind-value-guard.md §1.1): AtomRowValidator.cs's own registered-
                    // channel hand-off never ran for this kind ("unregistered channel is G6's job, not
                    // this check's" — but G6 was scoped to stat.modify only). `crit.rat` for
                    // `crit.rate`, one letter off out of 267, used to validate, bind, compile, and
                    // write nothing forever.
                    new ParamDef("channel", ParamKind.String, Required: true, Vocabulary: DerivedChannels),
                    new ParamDef("op", ParamKind.String, Required: true),
                    new ParamDef("amount", ParamKind.Value, Required: true)),
                // D6, 2026-08-22: quarantined to None/None/None because the kind had NO executor in
                // any runtime — no opcode, no EffectBag branch, no sink arm, and battle read derived
                // mods only from TraitBattleCatalog. A bind would have been accepted and then done
                // nothing forever, which is the exact failure this module exists to prevent.
                //
                // BATTLE re-opened 2026-08-23 by E12, which ships the first consumer:
                // `BattleStatComposer` read bound stat.derived atoms at squad build, through
                // `TraitAtomSource`. That composer's own additive-only ChannelMods loop made this
                // claim thinner than it read, though — it summed every op the same way, so Full for
                // Battle was really only true for Flat/Increased content, exactly the "Partial" defect
                // the Sim note below names for itself. battle-hub-fuse (T6) + battle-ops-parity (T7)
                // closed that gap for real: `BattleStatComposer` is deleted, `BattleHubCompose`
                // composes through the SAME `ActorHub`/`DerivedComposer` every other surface uses, so
                // Battle honours FlatReplace/MaxPriorityFlag/SumIncreased identically to Lawn now —
                // Full is no longer a claim ahead of the code, it is what the code does.
                //
                // LAWN re-opened 2026-08-30 (decisions.md, "Derived-write lawn executor" — owner
                // approved) now that it, too, has a real consumer: `AtomDerivedSubsystem`, an
                // `IActorStatSubsystem` registered on the injector's `ActorHub` at the reserved
                // order-350 `foundation.effect` slot, contributing bound stat.derived atoms into the
                // same `DerivedComposer` fold every other derived producer already uses. The flip is
                // deliberately the LAST step of that change, not the first: flipping before the
                // executor existed would have re-created D6's exact state (binds accepted, nothing
                // applied) inside the change meant to end it.
                //
                // SIM re-opened 2026-09-06 (mechanism-wiring E5, decisions.md "Derived-write lawn
                // executor" owner decision 2) — the flip is the LAST step of that change, after the
                // fold and the bind site both existed and were exercised, exactly the order the lawn
                // cell itself moved in:
                //   1. ActorDerivedLookup (ActorDerivedProfiles.cs) gained a contribution fold —
                //      AddContribution/Resolve, folding bound stat.derived atoms onto the pinned base
                //      via ActorDerivedSnapshot.OverlayAdd (E4).
                //   2. SimEffectHost and FoundationHarness both wired it (ContributeDerived/
                //      ResolveDerived, E4).
                //   3. ActorDerivedLookup.TryBind constructs a real BindContext(RuntimeId.Sim) and
                //      calls BindGate.Check, so a bind is genuinely attempted (E4).
                //   4. That flip (2026-09-06, E5): decided from the built fold rather than up front,
                //      Partial not Full. OverlayAdd was a plain sum with no notion of DerivedModifierOp
                //      at all — it read only BoundDerivedAtom.Amount, never .Op.
                //      The_four_derived_ops_decide_Full_versus_Partial (EffectOfflineKitTests.cs) proved,
                //      against the real DerivedComposer, that a plain sum reproduces Flat (FlatSum:
                //      default + Σflat) and Increased (SumIncreased: default + Σincreased) exactly,
                //      because both of those channel-compose kinds ARE sums in the real composer too —
                //      but it diverged for Replace (FlatReplace: the real composer picks the
                //      highest-priority Replace OUTRIGHT, discarding the baseline and every other
                //      contribution; the plain-sum fold summed all of them on top of the baseline
                //      instead) and Flag (MaxPriorityFlag: the real composer takes the MAX of the
                //      flag-ish contributions; the plain-sum fold summed them).
                //   5. sim-hub-parity (T15, 2026-09-13) — THIS flip, Partial to Full. ActorDerivedLookup.
                //      Resolve now folds per channel via the SAME DerivedComposer.
                //      ComposeChannelWithBaseline every other runtime's composer uses, with the pinned
                //      snapshot value standing in for DerivedStatDef.DefaultValue — Replace picks
                //      highest-priority outright, Flag takes the max, matching Hub exactly (SourceId
                //      tie-break when Priority is left default, same as every other real
                //      BoundDerivedAtom-sourced producer already gets — BoundDerivedAtom itself still
                //      carries no Priority field, so a Sim caller cannot express one). The SAME
                //      EffectOfflineKitTests.cs test now proves parity instead of divergence.
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.Full, RuntimeState.Full),
                AtomTriggers.None,
                PowerCategory.Offense | PowerCategory.Survivability | PowerCategory.Control,
                "No opcode — direct derived-channel mods. Derived ops are Flat|Increased|Replace|Flag; " +
                "there is no More on the derived side."),

            // ---- Resource ------------------------------------------------------------------
            new("resource.delta", AttachPoint.Resource, new ParamSchema(
                    // D7 (E11, 2026-08-22): `channel` was undeclared, and `ExecApplyResourceDelta`
                    // reads it (InjectorEffectActionSink.cs:132, defaulting to hp). `fx.overlay_damage`
                    // is exactly that effect — a channel and no magnitude, because the magnitude is
                    // per-grant overlay. So `amount` is optional here for the same reason it is on
                    // shield.grant (D10): a required magnitude makes overlay-driven content
                    // unauthorable. Absence is checked at BIND, against the overlay that will carry it.
                    // E29: 6 after E28 (see spec §2's own note — only "hp" executes until E28 fix #1
                    // ships; the schema declares the full SSOT regardless, a runtime gap being E28's
                    // reporting concern, not a reason to narrow what validates here).
                    new ParamDef("channel", ParamKind.String, Vocabulary: ResourceChannels),
                    new ParamDef("amount", ParamKind.Value, OverlayOrParam: true),
                    new ParamDef("element", ParamKind.String),
                    new ParamDef("target", ParamKind.Object),
                    // D7: the DoT and contagion payload lives HERE, not on status.apply. EffectBag
                    // calls StatusEffectBridge.TryApplyFromGrant only inside the ApplyResourceDelta
                    // branch, and these keys are in FA10's allowlist — not FA2's.
                    new ParamDef("statusId", ParamKind.String),
                    new ParamDef("periodMs", ParamKind.Value),
                    new ParamDef("durationMs", ParamKind.Value),
                    new ParamDef("tickBudget", ParamKind.Int),
                    new ParamDef("spread", ParamKind.Object)),
                // D6: Battle was Full, then downgraded to None because no ATOM could reach it —
                // BattleEngine never granted and never called OnEvent. A18c (spec-battle-resource-shield-grants.md)
                // grew that grant path: OnActivate/OnDamageDealt now fire, Bag.Status/Bag.StatusRng
                // are wired, and a real shipped def (fx.overlay_damage) proves plain amounts, the
                // DoT/contagion payload, and the owner-matching dual-fire all work end to end
                // (BattleResourceShieldGrantsTests, T46). Full again, for real this time.
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.Full, RuntimeState.PlanOnly),
                AllTriggers,
                PowerCategory.Offense | PowerCategory.Survivability,
                "FA10, hp add-only. The only opcode battle consumes. " +
                "Dealing damage is this plus a trigger — there is no separate damage attach point."),

            new("resource.economy", AttachPoint.Resource, new ParamSchema(
                    // E29 §2.1 correction 1: the description below used to claim maxSun/maxMoney were
                    // injector-only and outside FA9's own reach — false. `ExecEconomy` passes
                    // `currency` through unfiltered and `CheatActions.SetEconomy` handles both, so the
                    // vocabulary is 5, not 3. §7.1 (decided 2026-09-03): this is also where "no empire
                    // currency is atom-authorable" is recorded — loam/soul/essence.*/shard.* share no
                    // member with this vocabulary, so `currency: "loam"` is now a hard load-time
                    // refusal. Correct: an atom writes the match-scoped economy, never the empire
                    // ledger, and that boundary belongs on the atom layer's own definition, not a
                    // fourth document.
                    new ParamDef("currency", ParamKind.String, Required: true, Vocabulary: () => EconomyCurrencies),
                    // E29: any non-"add"/"+" string silently meant "set" — `op: "addd"` succeeded
                    // loudly at the wrong behaviour instead of failing at the right one.
                    new ParamDef("op", ParamKind.String, Required: true, Vocabulary: () => EconomyOps),
                    new ParamDef("amount", ParamKind.Value, Required: true),
                    // G4: was in the legacy allowlist and implemented nowhere. E15 shipped the
                    // counter 2026-08-22 (AtomRunner + RunnerState), so the "not available yet"
                    // guard is lifted — leaving it would have made the feature unauthorable by the
                    // very content it was built for.
                    new ParamDef("capPerMatch", ParamKind.Int)),
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.PlanOnly),
                EventsPlusMatchAndEconomy,
                PowerCategory.Economy,
                "FA9. Currencies are sun|money|points|maxSun|maxMoney — 5, not 3; the injector does " +
                "not narrow this (E29 §2.1 correction 1). Empire currencies (loam, soul, essence.*, " +
                "shard.*) share no member with this vocabulary and are not atom-authorable: an atom " +
                "writes the match-scoped economy, never the empire ledger. E34: also carries the five " +
                "match/board-economy triggers — FA9 has no target either."),

            // ---- Status --------------------------------------------------------------------
            // D7: re-derived from FA2's allowlist and ExecApplyStatus. The previous schema declared
            // statusId/durationMs/target and the DoT payload — none of which FA2 carries. FA2 allows
            // exactly { status, duration, level, chance, icd_ms, max_stacks, filters }, the executor
            // reads "status" as a string and "duration" as float SECONDS, and the target is resolved
            // from the event (ResolveStatusTargetPtr), never from a param.
            new("status.apply", AttachPoint.Status, new ParamSchema(
                    // E29: 21 catalog statuses, the union across runtimes (rule 4) — a status legal
                    // here but inert on a given runtime (e.g. the eight the lawn's Unity CC switch
                    // implements, `DebugActions.cs:861-909`) refuses at EXECUTE time with a name,
                    // never at load.
                    new ParamDef("status", ParamKind.String, Required: true, Vocabulary: StatusIds),
                    // Seconds, not milliseconds. FA2 predates the integer-ms rule and was not changed
                    // for it; declaring durationMs here would validate a key nothing reads.
                    new ParamDef("duration", ParamKind.Value),
                    new ParamDef("level", ParamKind.Int)),
                // A18d (spec-battle-status-apply.md): Battle was Partial -- StatusRuntime was mounted
                // but reachable only through scripted InitialStatuses at battle setup, never through an
                // atom-triggered event. BattleEffectSink now has its own ExecApplyStatus branch, proven
                // against a real shipped def (fx.poison_on_hit, BattleStatusApplyTests, T49). Full.
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.Full, RuntimeState.PlanOnly),
                AllTriggers,
                PowerCategory.Control | PowerCategory.Offense,
                "FA2 only. The DoT/contagion payload (statusId, periodMs, tickBudget, spread) lives on " +
                "FA10 `resource.delta` — StatusEffectBridge.TryApplyFromGrant is called from the " +
                "ApplyResourceDelta branch, so that content compiles to a different opcode. " +
                "G5 is a RUNTIME hole: an event resolving to an empty ptr hits the unguarded " +
                "FindObjectsOfType<Zombie>() loop, and no load-time param check can close it."),

            // D7 (E11, 2026-08-22): re-derived from the executor, not the doc. `ExecClearStatus`
            // reads `status` (InjectorEffectActionSink.cs:260) and `target` as a STRING it may omit,
            // falling back to the resolved event target. The schema declared `statusId` — a key
            // nothing reads — and made `target` a required object. Between them, the one shipped
            // FA3 effect (`fx.clear_butter`, params `{status: butter}`) was unauthorable as an atom.
            new("status.clear", AttachPoint.Status, new ParamSchema(
                    // E29: same 21-status vocabulary as status.apply (rule 4's union) — the lawn's
                    // own 4-of-21 executable-today gap (E28) is an execute-time reporting concern,
                    // not a reason to narrow what a status.clear atom may name.
                    new ParamDef("status", ParamKind.String, Required: true, Vocabulary: StatusIds),
                    new ParamDef("target", ParamKind.String)),
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.PlanOnly),
                AtomTriggers.Events,
                PowerCategory.Utility,
                "FA3."),

            // ---- Shield --------------------------------------------------------------------
            new("shield.grant", AttachPoint.Shield, new ParamSchema(
                    // D10: optional, not required. `fx.shield_grant` ships with EMPTY params —
                    // every magnitude is overlay — so a required `amount` would force migration to
                    // author a number the original never had, which is a behaviour change wearing a
                    // schema's clothes. Presence is a BIND-time check against the overlay.
                    new ParamDef("amount", ParamKind.Value, OverlayOrParam: true),
                    // E29: needs no Vocabulary here — already strict-parsed via ElementRoster.TryParse
                    // and refused at EffectBag.cs:585-594 (spec §2's own note).
                    new ParamDef("element", ParamKind.String),
                    // D7: honoured by ExecGrantShield - it selects PriorityAura/PriorityInnate and flips the
                    // refillOnMerge default. Undeclared, every atom-granted shield was
                    // PrioritySkill with refill=true, so the warded family lost a shipped capability.
                    // E29: any non-"aura"/"innate" string silently became "skill" — a typo like
                    // "arua" succeeded at the wrong priority/refill combination instead of failing.
                    new ParamDef("sourceClass", ParamKind.String, Vocabulary: () => ShieldSourceClasses),
                    new ParamDef("priority", ParamKind.Int),
                    new ParamDef("durationTicks", ParamKind.Int),
                    new ParamDef("refillOnMerge", ParamKind.Bool),
                    new ParamDef("target", ParamKind.Object)),
                // D6: shipped Full/Full/Full, then downgraded — ExecGrantShield requires
                // Bag.ShieldGate, which neither BattleEffectHost nor SimEffectHost set at the time.
                // T14 wired Battle's own Bag.ShieldGate (this reopening, 2026-08-28); A18c
                // (spec-battle-resource-shield-grants.md) grew the grant path on top of it (OnActivate/
                // OnDamageDealt now fire real grants) — Battle is Full again, proven via a real shipped
                // def (fx.shield_grant, BattleResourceShieldGrantsTests, T46). Sim's own ShieldGate is
                // still unwired — a separate, un-scoped gap this module does not touch.
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.Full, RuntimeState.None),
                AllTriggers,
                PowerCategory.Survivability,
                "The eleventh opcode: shipped, unnumbered, absent from the FA1-FA10 doc table, and " +
                "NOT in InjectorEffectActionSink — it executes bag-side in Core."),

            // ---- Board ---------------------------------------------------------------------
            new("spawn.entity", AttachPoint.Board, new ParamSchema(
                    // E29: the only three kinds ExecSpawnEntity's switch has arms for.
                    new ParamDef("kind", ParamKind.String, Required: true, Vocabulary: () => SpawnEntityKinds),
                    new ParamDef("typeId", ParamKind.Int),
                    // D7/D3: E9's spawn price is chance x count x power(body), and count was declared
                    // nowhere. Floor it at 1 in E4's validator - an omitted count defaulting to 0
                    // prices the whole spawn at zero, which is the defect the body pricing fixed.
                    // E28 fix #5: the sink now loops count spawns (floored at 1 — structural, not a
                    // progression cap: zero spawns is not a legal "less of the effect").
                    new ParamDef("count", ParamKind.Int),
                    new ParamDef("row", ParamKind.Int),
                    // G1: the sink forwards a different subset per kind and silently drops the rest.
                    // E40 (spec-spawn-non-grid.md §2a): pet/bucket/coin place at a cell the same way
                    // plant does — MiniPet.SetPet / ItemManager.SetBucket / CreateItem.SetCoin each
                    // take a col/row pair (CellCenter for pet/bucket; a raw column/row pair for coin,
                    // whose arm is refused separately below — declared here anyway so lifting that
                    // refusal later needs no schema edit).
                    new ParamDef("col", ParamKind.Int, HonouredOnlyWhen: "kind=plant|pet|bucket|coin"),
                    // E40: CreateMower.SetMower(MowerType, float x, int row) places by x/row, not
                    // col — the one widened kind that joins zombie/bullet's existing x reader rather
                    // than plant/pet/bucket's col reader.
                    new ParamDef("x", ParamKind.Value, HonouredOnlyWhen: "kind=zombie|bullet|mower"),
                    new ParamDef("hp", ParamKind.Value, HonouredOnlyWhen: "kind=zombie"),
                    new ParamDef("maxHp", ParamKind.Value, HonouredOnlyWhen: "kind=zombie"),
                    new ParamDef("mindControlled", ParamKind.Bool, HonouredOnlyWhen: "kind=zombie"),
                    // E28 fix #5: DebugActions.ApplyAbsoluteProps already has an absolute-atk hook for
                    // plants (P-ATK) and the Z-ATK cheat id already exists for zombies — the sink just
                    // never forwarded `atk` into the payload for either kind, and never gave the zombie
                    // branch an atk read at all.
                    // E37 (spec-projectile-control.md §2a): widened to `kind=bullet` too — a spawned
                    // bullet carries `damage` on the projectile itself (a different Unity target,
                    // `Bullet.Damage`, not a modifier-bag hook), and the sink now translates
                    // atk -> damage at the payload boundary (InjectorEffectActionSink's SpawnBulletOnce).
                    // Was scoped out as "bullets have no such hook" — that was the wiring gap this
                    // module closes, not a permanent limitation.
                    new ParamDef("atk", ParamKind.Value, HonouredOnlyWhen: "kind=plant|zombie|bullet"),
                    // E37 §2a: the three bullet-only spawn params, closed against the real swept
                    // BulletMoveWay set (BulletMoveWayValues above) — never the old, unswept
                    // right|left|up|down|track guess.
                    new ParamDef("y", ParamKind.Value, HonouredOnlyWhen: "kind=bullet"),
                    new ParamDef("moveWay", ParamKind.String, HonouredOnlyWhen: "kind=bullet",
                        Vocabulary: () => BulletMoveWayValues),
                    new ParamDef("fromType", ParamKind.Int, HonouredOnlyWhen: "kind=bullet")),
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.PlanOnly),
                EventsPlusMatchAndEconomy,
                PowerCategory.Offense | PowerCategory.Utility,
                "FA4. E34: also carries the five match/board-economy triggers — count/row/col are " +
                "explicit params, so this kind needs no entity in hand."),

            new("board.action", AttachPoint.Board, new ParamSchema(
                    // E29: the 4 canonical spellings ExecBoardAction's own normalization maps onto —
                    // a deliberate narrowing of the substring-matched aliases it also accepts
                    // ("CreateCherryBomb" etc, none shipped): a named refusal at load beats a
                    // substring match nobody can see failing.
                    new ParamDef("op", ParamKind.String, Required: true, Vocabulary: () => BoardActionOps),
                    new ParamDef("row", ParamKind.Int),
                    new ParamDef("col", ParamKind.Int),
                    new ParamDef("damage", ParamKind.Value)),
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.PlanOnly),
                EventsPlusMatchAndEconomy,
                PowerCategory.Offense | PowerCategory.Control,
                "FA5. Ops are freeze|doom|fireline|cherry. E34: also carries the five match/board-" +
                "economy triggers — row/col are explicit params."),

            new("grid.spawn", AttachPoint.Board, new ParamSchema(
                    // E29: the 12-member GridItemType vocabulary (Core's own mirror — see
                    // GridItemTypeValues' own doc comment for the reflected ordinals).
                    new ParamDef("gridItemType", ParamKind.Int, Required: true,
                        Vocabulary: () => GridItemTypeValues),
                    new ParamDef("row", ParamKind.Int),
                    new ParamDef("col", ParamKind.Int),
                    // E28 (spec-param-parity.md §3 row 6): the sink now forwards graveType to
                    // DebugActions.SpawnGrid, which already read and honoured it.
                    new ParamDef("graveType", ParamKind.Int)),
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.PlanOnly),
                EventsPlusMatchAndEconomy,
                PowerCategory.Utility,
                "FA6. E34: also carries the five match/board-economy triggers — row/col are explicit " +
                "params."),

            new("grid.clear", AttachPoint.Board, new ParamSchema(
                    // E29: same 12-member GridItemType vocabulary as grid.spawn.
                    new ParamDef("gridItemType", ParamKind.Int, Vocabulary: () => GridItemTypeValues),
                    new ParamDef("selector", ParamKind.String),
                    // E28 (spec-param-parity.md §3 row 4): DebugActions.ClearGridItem already accepts
                    // col/row (DebugActions.cs:639-668) — targeted clearing was reachable Unity-side
                    // and simply never declared, so an atom could not narrow which cell to clear and a
                    // multi-match runtime call refused outright ("multiple matches; pass col/row or
                    // random:true", DebugActions.cs:666).
                    new ParamDef("row", ParamKind.Int),
                    new ParamDef("col", ParamKind.Int)),
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.PlanOnly),
                EventsPlusMatchAndEconomy,
                PowerCategory.Utility,
                "FA7. E34: also carries the five match/board-economy triggers — row/col are explicit " +
                "params."),

            new("box.set", AttachPoint.Board, new ParamSchema(
                    // D7: ExecSetBox reads this with JsonOverlay.GetInt. Declared String, an atom authoring
                    // boxType: "dirt" validated and then silently set box type 1.
                    // E29: the 8-member BoxType vocabulary (Core's own mirror — see BoxTypeValues'
                    // own doc comment for the reflected ordinals; this is also E28's own content-fix
                    // evidence, `fx.set_dirt_box` authored `1` (Water) and meant `2` (Dirt)).
                    new ParamDef("boxType", ParamKind.Int, Required: true, Vocabulary: () => BoxTypeValues),
                    new ParamDef("row", ParamKind.Int),
                    new ParamDef("col", ParamKind.Int),
                    // E28 fix #7 (spec-param-parity.md §3 row 7): the executor now paints every listed
                    // cell. Each entry is `{row, col}` — the same shape `row`/`col` already have on
                    // this kind, just plural. Required AtomCompiler.Plain() to preserve array/object
                    // structure instead of stringifying it (fixed alongside), and AtomPushCodec.ToDef
                    // to unwrap the wire's JsonElement boxing recursively (fixed the same session —
                    // the cross-cutting defect this array param exposed while being specced).
                    new ParamDef("cells", ParamKind.Array)),
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.PlanOnly),
                EventsPlusMatchAndEconomy,
                PowerCategory.Utility,
                "FA8. E34: also carries the five match/board-economy triggers — row/col/cells are " +
                "explicit params."),

            // E37 (spec-projectile-control.md §2b): the bullet the GAME fires, not one an atom spawns
            // — spawn.entity{kind:bullet} above CREATES a bullet; this kind changes an EXISTING one's
            // damage/type/moveWay. No new attach point: still Board, the same seam spawn.entity uses.
            //
            // A permanent modifier (AtomTriggers.None, like stat.derived): the grant's presence is the
            // effect, read as a resolved grant inside the EXISTING Bullet.InitData postfix
            // (CheatPrefixes.BulletInitCheat), never rewritten, via GrantedBulletModifyAtomReader — the
            // same resolved-read shape stat.derived's GrantedDerivedAtomReader uses at
            // AtomDerivedSubsystem. Cheat state (D-DMG-SET) is applied AFTER this read, so it still
            // wins (§2b.1's mandatory permanentModifiers amendment in AtomKindRegistryTests.cs covers
            // the empty-Triggers guard this shape requires).
            new("bullet.modify", AttachPoint.Board, new ParamSchema(
                    new ParamDef("op", ParamKind.String, Required: true, Vocabulary: () => BulletModifyOps),
                    new ParamDef("amount", ParamKind.Value, Required: true),
                    new ParamDef("bulletType", ParamKind.Int),
                    new ParamDef("moveWay", ParamKind.String, Vocabulary: () => BulletMoveWayValues)),
                // Lawn: Full — CheatPrefixes.cs's BulletInitCheat postfix reads it. Battle: pending,
                // NEVER "never" (E1's living-table rule) — battle has no projectile today, that could
                // change. Sim: None, same reason as Battle.
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.None),
                AtomTriggers.None,
                PowerCategory.Offense,
                "op is set|add|scale. scale is per-mille (amount 1500 = x1.5). Read at Bullet.InitData, " +
                "keyed by the firing plant's owner key. Cheat state applied last, D-DMG-SET still wins."),

            // ---- Match -----------------------------------------------------------------------
            new("match.modify", AttachPoint.Match, new ParamSchema(
                    // E35 (spec-match-modify.md §2.3): the closed 11-field vocabulary, this kind's
                    // own value check until E29's registry-backed one lands.
                    new ParamDef("field", ParamKind.String, Required: true,
                        Vocabulary: () => MatchModifyFields),
                    new ParamDef("amount", ParamKind.Value, Required: true)),
                // No `op` param, deliberately (§2.2): ApplyBoardConfig assigns, it never reads live
                // host state to compute a delta — the overlay rule this kind's own note names.
                // Battle/Sim are None: neither has a Board.config or a consumer.
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.None),
                AtomTriggers.MatchEvents,
                PowerCategory.Offense | PowerCategory.Survivability | PowerCategory.Control,
                "Sets one Board.config field for the match. Set-only: the executor assigns, and a " +
                "multiply would need to read live host state, which the overlay rule forbids."),

            // E36 (spec-wave-control.md): the second Match-attached kind — lets content shape the
            // pressure the player is actually fighting (summon a wave, summon a huge wave, set the
            // wave timer, hold it), all four against existing CheatActions/DebugActions entry points.
            new("wave.control", AttachPoint.Match, new ParamSchema(
                    new ParamDef("op", ParamKind.String, Required: true),
                    new ParamDef("wave", ParamKind.Int, HonouredOnlyWhen: "op=summon"),
                    new ParamDef("timerMs", ParamKind.Int, HonouredOnlyWhen: "op=setTimer"),
                    new ParamDef("enabled", ParamKind.Bool, HonouredOnlyWhen: "op=hold")),
                // Battle/Sim are None: neither has a BoardSpawner, and flipping a cell without an
                // executor is D6 (the exact quarantine class this registry already applies elsewhere).
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.None),
                EventsPlusMatch,
                PowerCategory.Offense | PowerCategory.Control,
                "Ops are summon|huge|setTimer|hold. 'hold' floors the wave timer, it does not stop " +
                "it — CheatActions.cs's own F-WAVE-FREEZE handling (the floor value itself now lives " +
                "in data/tuning/match.v1.json's waveHoldFloorSeconds, not a bare literal). Refused at " +
                "ChainDepth > 0 — summon/huge cause spawns, which re-emit the events that could " +
                "re-trigger this same atom, and that loop cannot be diagnosed after the fact."),

            // ---- Ui -------------------------------------------------------------------------
            // E41 (spec-ui-attach-point.md): the first, read-only-by-construction attach point.
            // Every other Wave 8 kind changes what happens on the board; this one changes what the
            // player KNOWS happened — a floater number, a banner, a HUD meter. See §2a's own rule,
            // restated by AttachPoint.Ui's doc comment: a kind here may never appear in a plan item
            // any state executor handles (EffectBag.FireGrant's own bag-side branch is what makes
            // that structural, not this doc comment).
            new("ui.present", AttachPoint.Ui, new ParamSchema(
                    new ParamDef("op", ParamKind.String, Required: true, Vocabulary: () => UiPresentOps),
                    new ParamDef("amount", ParamKind.Value, HonouredOnlyWhen: "op=number"),
                    // §2b: one of the eleven DamageFxTag names, lowercased — optional on every op
                    // (not honoured-only-when, unlike amount/bannerId/meterId/ratio), since a present
                    // may want a colour regardless of which op produced it.
                    new ParamDef("tag", ParamKind.String, Vocabulary: () => UiPresentTagValues),
                    new ParamDef("bannerId", ParamKind.String, HonouredOnlyWhen: "op=banner",
                        Vocabulary: () => UiPresentBannerCatalogIds),
                    // meterId's Vocabulary is the SAME live function resource.delta's own `channel`
                    // param already reads (ResourceChannels, declared above) — not a copy, so the
                    // vocabulary widens the day a seventh resource lands in
                    // DerivedStatChannels.ResourceIds, with zero edits to this kind (E29 rule 2).
                    new ParamDef("meterId", ParamKind.String, HonouredOnlyWhen: "op=meter",
                        Vocabulary: ResourceChannels),
                    // Per-mille 0-1000, a bounded ratio (§3) — checked below, not via Vocabulary
                    // (a range check, not a membership check).
                    new ParamDef("ratio", ParamKind.Value, HonouredOnlyWhen: "op=meter"),
                    new ParamDef("durationMs", ParamKind.Int)),
                // Lawn: Full — no present sink exists in Battle/Sim today (RuntimeState.None, marked
                // pending per E1's own living-table rule, never "never": a present sink could land
                // there later, the same shape bullet.modify/match.modify already record for Battle).
                new RuntimeSupportMatrix(RuntimeState.Full, RuntimeState.None, RuntimeState.None),
                AllTriggers,
                // §2b.1's own MANDATORY guard amendment: PowerCategory.None requires the `cosmetic`
                // exemption in AtomKindRegistryTests.cs's Every_kind_declares_a_runtime_a_trigger_and_
                // a_power_category, alongside (never merged with) the existing permanentModifiers set
                // — a present writes no state, so a category on it would let a floater be budgeted as
                // if it contributed real power (§2c: priced at exactly zero, a DIFFERENT claim from no
                // category at all).
                PowerCategory.None,
                "op is number|banner|meter. A present is read-only: it never writes state and is " +
                "never read back into content. amount only under number, bannerId only under banner, " +
                "meterId/ratio only under meter."),

            // ---- Siege ---------------------------------------------------------------------
            // base-defense `siege-construction` (decision 27, 2026-09-06): the first Siege-attached
            // kind. Places a structure on the tactical siege board — never the PvZ lawn, which has no
            // executor for this kind at all (Lawn=None, unlike every Board-attached kind above, which
            // is the reverse). See AttachPoint.Siege's own doc comment for the layering this splits.
            new("structure.place", AttachPoint.Siege, new ParamSchema(
                    new ParamDef("structureId", ParamKind.String, Required: true, Vocabulary: StructureIds),
                    // Required, not defaulted: a silent default either way is exactly the class of
                    // "plausible-looking wrong" bug E29/E40 exist to refuse at load instead of guessing.
                    // true = finished immediately (Assembled/Summoned, decision 27: "immediate — you
                    // carried it here already" / "nothing new is needed"); false = under construction,
                    // reading the placed structure's own StructureDef.BuildTurns (Built/Laboured).
                    new ParamDef("instant", ParamKind.Bool, Required: true)),
                // Lawn: None always — there is no tactical board there. Sim: PlanOnly, the same
                // "produces a plan, no live executor yet" default every kind without a Sim consumer
                // carries.
                new RuntimeSupportMatrix(RuntimeState.None, RuntimeState.Full, RuntimeState.PlanOnly),
                AtomTriggers.Actions,
                PowerCategory.Utility,
                "The only Siege-attached kind. Places structureId at the firing event's own " +
                "TargetRow/TargetCol, validated through ConstructionPlacement.CanPlace against the " +
                "live BoardState. Never reads or writes WorldState directly — the placement crosses " +
                "back out through BattleOutcome.SlotResults[].StructurePlaced, the same seam " +
                "structure-state's own HP damage already uses, applied only once the world layer " +
                "receives the finished BattleOutcome."),

            // ---- Element -------------------------------------------------------------------
            // passive-tree `element-conversion` (D56, spec-element-conversion.md §2b): the first
            // Element-attached kind. Redistributes weight WITHIN an already-elemental ElementPayload —
            // never fabricates a component on a null payload, never invents a "Physical" element
            // (neither exists in the real type, ElementPayload.cs / ActorElementTypes.cs). Permanent
            // modifier: no trigger, matching stat.derived/bullet.modify's own shape. QUARANTINED (all
            // three runtimes None) until a real combat-dispatch reader exists — see the runtime-matrix
            // comment below.
            new("element.convert", AttachPoint.Element, new ParamSchema(
                    // Optional: omitted means "any component currently in the payload, largest first"
                    // (spec §2c). Present, it must name a real element — never "omni", never the
                    // nonexistent "Physical".
                    new ParamDef("fromElement", ParamKind.String, Required: false,
                        Vocabulary: () => ElementRoster.Concrete.Select(e => e.ToElementId()).ToArray()),
                    new ParamDef("toElement", ParamKind.String, Required: true,
                        Vocabulary: () => ElementRoster.Concrete.Select(e => e.ToElementId()).ToArray()),
                    // Per-mille of the affected component's OWN weight, 1..1000 -- range enforced by
                    // ElementConversion.Apply at resolve time (the same "structural check inside the
                    // executor" pattern this codebase already uses for a business-rule bound that is not
                    // a type/presence question). Flat Int, not a Value/ValueSpec -- spec-element-
                    // conversion.md §5's own open question 2 (flat per-mille vs a Theta-scaled
                    // coefficient) is a tree-plan/tree-binder PRICING question this kind's wire shape
                    // does not need to pre-decide.
                    new ParamDef("shareMilli", ParamKind.Int, Required: true)),
                // QUARANTINED, all three runtimes -- self-corrected 2026-09-07, hours after this kind
                // first shipped. This kind's own params validate (fromElement/toElement/shareMilli),
                // but NO real reader resolves a bound element.convert grant into anything yet -- the
                // combat-dispatch read point (spec §2b's own named open question: before
                // ApplyPacketToFunnel vs inside DamagePacketBuilder.FromOverlay vs a new stage) is
                // deliberately NOT decided or built here, since it is a real engineering decision with
                // more than one defensible answer, not this task's to force. Claiming Full/Full without
                // one was the exact "advertising support it does not have" defect
                // AtomKindRegistryTests' own `awaitingConsumer` set exists to prevent (empty since
                // 2026-08-23 -- this is its first occupant since then). Flip to a real state per
                // runtime only once each one's own reader/executor actually exists, mirroring
                // stat.derived's own per-runtime re-opening history exactly (D6 quarantined it in
                // 2026-08-22 for the identical reason; battle/lawn/sim were each re-opened only as a
                // real consumer landed, never assumed from a sibling runtime's own state).
                new RuntimeSupportMatrix(RuntimeState.None, RuntimeState.None, RuntimeState.None),
                AtomTriggers.None,
                PowerCategory.Offense,
                "The only Element-attached kind. Redistributes shareMilli of fromElement's (or, if " +
                "omitted, the largest remaining component's) own current weight to toElement, against " +
                "the attacking actor's own ElementPayload. A null payload is a no-op, never an error " +
                "and never a fabricated base (spec-element-conversion.md §2b/§3)."),
        };

        var map = new Dictionary<string, AtomKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in kinds) map[k.KindId] = k;
        return map;
    }
}
