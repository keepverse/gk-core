using FusionRpg.Contracts;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Effects;

public static class EffectOwnerKey
{
    /// <summary>
    /// Side-wide owner keys: EVERY actor on one side, any type id — the "every plant, any species"
    /// scope the grammar could not express before 2026-09-20. Grammar and apply gate live in
    /// <see cref="StatApplyScope"/> (<c>Matches</c> / <c>IsMatchWide</c> / <c>IsKnownOwnerKey</c> /
    /// <c>OwnerKeyCovers</c>); these constants are the one spelling producers use.
    ///
    /// <para>Read the apply gate's own doc for why the side is the PREFIX (<c>plant:*</c>, not
    /// <c>side:plant</c>) and for why neither key is match-wide. Producers of the first one:
    /// <c>PatronSecondaryPlugin</c>'s <c>fx.patron_aura</c> (the injector's own match-start grant) and
    /// <c>FusionRpg.Server.PatronEndpoints.TryBuildPatronSessionGrant</c> (the same grant id, upserted
    /// into the server's session at each board.start) — it was match-wide and therefore also buffed the
    /// zombies (live-proven, creature-standalone PT7).</para>
    ///
    /// <para>These are ALIASES of <see cref="EffectOwnerKeys.PlantSide"/>/
    /// <see cref="EffectOwnerKeys.ZombieSide"/>, not a second literal: the string crosses the
    /// Core/Contracts boundary exactly like <see cref="EffectTriggers"/>'s vocabulary, so a literal both
    /// assemblies can see lives in Contracts and this file keeps the name the <c>EffectOwnerKey</c>
    /// family's other members (<see cref="MatchesEvent"/>) already use.</para>
    /// </summary>
    public const string PlantSide = EffectOwnerKeys.PlantSide;

    /// <inheritdoc cref="PlantSide"/>
    public const string ZombieSide = EffectOwnerKeys.ZombieSide;

    /// <summary>
    /// E34 (spec-trigger-vocabulary.md §2.4): the five match/board-economy triggers a type-keyed
    /// (<c>plant:{tid}</c> / <c>zombie:{tid}</c>) owner grant must always refuse. A type-keyed owner
    /// means "this entity type"; none of the five is about an entity type — and OnGridPlace's TypeId
    /// is a GRID ITEM type (§2.2) that happens to share this field with a zombie/plant type. Without
    /// this explicit arm, the zombie branch below names no trigger at all and its only gate (the side
    /// check a few lines down) refuses only when ev.Side is PRESENT and not "zombie" — a match-scoped
    /// event has no Side, so it would wave straight through to `(ev.TypeId ?? ev.TargetTypeId) == tid`,
    /// and a `zombie:7` grant would fire on every placement of grid item type 7. The plant branch
    /// already falls through to `false` for anything not in its own explicit list, so this arm is a
    /// stated refusal there rather than an unnamed one — the zombie branch is where it is load-bearing.
    /// </summary>
    static readonly string[] TypeKeyedRefusalTriggers =
    {
        EffectTriggers.OnWave, EffectTriggers.OnMatchStart, EffectTriggers.OnMatchEnd,
        EffectTriggers.OnSunCollect, EffectTriggers.OnGridPlace
    };

    static bool IsTypeKeyedRefusalTrigger(string? trigger) =>
        trigger is not null &&
        Array.Exists(TypeKeyedRefusalTriggers, t => string.Equals(t, trigger, StringComparison.OrdinalIgnoreCase));

    public static bool MatchesEvent(EffectGrant grant, EffectEventDto ev)
    {
        var key = grant.OwnerKey ?? "";
        if (string.Equals(key, EffectOwnerKeys.Match, StringComparison.OrdinalIgnoreCase))
            return true;

        if (key.StartsWith("plant:", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(key.AsSpan(6), out var tid)) return false;

            // E34 §2.4: explicit refusal for the five match/board-economy triggers. Falling through
            // to `false` below would already produce the right answer here — stated explicitly to
            // match the zombie branch's own arm, and so a future trigger added to that branch's
            // explicit list cannot silently start matching one of these five by accident.
            if (IsTypeKeyedRefusalTrigger(ev.Trigger))
                return false;

            if (string.Equals(ev.Trigger, EffectTriggers.OnSpawn, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ev.Trigger, EffectTriggers.OnDeath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ev.Trigger, EffectTriggers.OnDamageTaken, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(ev.Side, "plant", StringComparison.OrdinalIgnoreCase)) return false;
                return (ev.TypeId ?? ev.TargetTypeId) == tid;
            }

            if (string.Equals(ev.Trigger, EffectTriggers.OnDamageDealt, StringComparison.OrdinalIgnoreCase))
                return string.Equals(ev.Side, "plant", StringComparison.OrdinalIgnoreCase) && ev.TypeId == tid;

            // E33 (spec-activation-edge.md §2.3, point 1 — a wiring fix): nothing matched OnActivate
            // before this clause. Same shape as OnDamageDealt above — the actor's own type, never the
            // target's. No shipped behaviour changes: nothing raises OnActivate on the plant side yet.
            if (string.Equals(ev.Trigger, EffectTriggers.OnActivate, StringComparison.OrdinalIgnoreCase))
                return string.Equals(ev.Side, "plant", StringComparison.OrdinalIgnoreCase) && ev.TypeId == tid;

            return false;
        }

        if (key.StartsWith("zombie:", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(key.AsSpan(7), out var tid)) return false;

            // E34 §2.4: the real fix. Without this explicit arm, none of the checks below name these
            // five triggers, so a match-scoped event (no Side) waves through the side check further
            // down (it only refuses a PRESENT wrong side) and lands on
            // `(ev.TypeId ?? ev.TargetTypeId) == tid` — and OnGridPlace's TypeId IS the grid item type
            // (§2.2), so a `zombie:7` grant would fire on every placement of grid item type 7. This is
            // a refusal being ADDED here, not documented — the zombie branch had no narrowing for any
            // of these five before this module.
            if (IsTypeKeyedRefusalTrigger(ev.Trigger))
                return false;

            // E33 (spec-activation-edge.md §2.3, point 2 — a NARROWING BEHAVIOUR CHANGE on a branch
            // Battle's live path flows through: BasicAttack.cs raises OnActivate once per resolved
            // intent). Before this clause, the unnarrowed fall-through below also matched on
            // TargetTypeId when TypeId was null (the target's type standing in for the actor's — the
            // exact thing an owner-key match must never do) and matched when ev.Side was null (only a
            // PRESENT wrong side was ever refused). An activation must be gated on the actor's own
            // side and own type, nothing else — no shipped behaviour changes today only because
            // Battle's own OnActivate emit carries neither Side nor TypeId yet, not because the old
            // path was narrow enough on its own.
            if (string.Equals(ev.Trigger, EffectTriggers.OnActivate, StringComparison.OrdinalIgnoreCase))
                return string.Equals(ev.Side, "zombie", StringComparison.OrdinalIgnoreCase) && ev.TypeId == tid;

            // lawn-combat-wire L-N25: OnDamageDealt Side is the ATTACKER side; match the attacker's own
            // type only, exactly like the plant branch. The fall-through below used TargetTypeId when
            // TypeId was absent — the victim's type standing in for the attacker's.
            if (string.Equals(ev.Trigger, EffectTriggers.OnDamageDealt, StringComparison.OrdinalIgnoreCase))
                return string.Equals(ev.Side, "zombie", StringComparison.OrdinalIgnoreCase) && ev.TypeId == tid;

            if (!string.Equals(ev.Side, "zombie", StringComparison.OrdinalIgnoreCase) &&
                !(string.Equals(ev.Trigger, EffectTriggers.OnDamageDealt, StringComparison.OrdinalIgnoreCase) &&
                  string.Equals(ev.Side, "zombie", StringComparison.OrdinalIgnoreCase)))
            {
                // death/spawn/taken with zombie side
                if (ev.Side != null && !string.Equals(ev.Side, "zombie", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return (ev.TypeId ?? ev.TargetTypeId) == tid ||
                   (string.Equals(ev.Trigger, EffectTriggers.OnDamageTaken, StringComparison.OrdinalIgnoreCase) &&
                    ev.TargetTypeId == tid);
        }

        if (key.StartsWith("entity:", StringComparison.OrdinalIgnoreCase))
        {
            var want = StatApplyScope.Normalize(key);
            var matchesActor = !string.IsNullOrWhiteSpace(ev.ActorPtr) &&
                string.Equals(want, StatApplyScope.Normalize(EffectOwnerKeys.Entity(ev.ActorPtr)), StringComparison.Ordinal);
            var matchesTarget = !string.IsNullOrWhiteSpace(ev.TargetPtr) &&
                string.Equals(want, StatApplyScope.Normalize(EffectOwnerKeys.Entity(ev.TargetPtr)), StringComparison.Ordinal);

            // lawn-combat-wire (2026-09-15, real live-verified defect): OnDamageDealt is directional —
            // ev.ActorPtr is the ATTACKER, ev.TargetPtr the victim. Every entity-scoped grant bound to
            // BOTH combatants in a fight (lawn-basic-attack's own shape: every plant AND every zombie
            // holds one) matched on EITHER ptr before this line, so a single hit fired the attacker's
            // OWN grant (correct) AND the victim's OWN grant for its (unrelated, future) attacks
            // (wrong) — silently doubling every basic-attack's RPG delta since T10 shipped, hidden
            // until the same session's fx.overlay_damage fix (fdf3885c) made the delta non-zero enough
            // to notice. Narrowed to actor-only here. (The plant:{tid}/zombie:{tid} branches above do NOT
            // narrow this way — they fall back to TargetTypeId — unaudited, tracked separately.) OnDamageTaken
            // and every other trigger keep the broader either-ptr match (e.g. OnDeath kill-credit,
            // EffectBagAuditTests's own documented "Actor or Target" contract) — unaudited here, named
            // rather than silently swept into the same fix.
            if (string.Equals(ev.Trigger, EffectTriggers.OnDamageDealt, StringComparison.OrdinalIgnoreCase))
                return matchesActor;

            return matchesActor || matchesTarget;
        }

        if (key.StartsWith("player:", StringComparison.OrdinalIgnoreCase))
            return true; // match-scoped for now; player filter is grant-time

        // instance: (Hot-forbidden) and unknown grammar: never auto-match (was self-equals).
        return false;
    }

    public static bool PassesOverlayFilters(Dictionary<string, object?> overlay, EffectEventDto ev, EffectGrant? grant = null)
    {
        if (!overlay.TryGetValue("filters", out var f) || f == null) return true;
        var filters = JsonOverlay.FromObject(f);

        var filterSide = JsonOverlay.GetString(filters, "side");
        var filterTypeId = filters.ContainsKey("typeId")
            ? JsonOverlay.GetInt(filters, "typeId", int.MinValue)
            : (int?)null;

        if (!string.IsNullOrEmpty(filterSide) || filterTypeId.HasValue)
        {
            ResolveFilterTarget(ev, out var damagedSide, out var damagedTypeId);
            if (!string.IsNullOrEmpty(filterSide) &&
                !string.Equals(filterSide, damagedSide, StringComparison.OrdinalIgnoreCase))
                return false;
            if (filterTypeId.HasValue && damagedTypeId != filterTypeId.Value)
                return false;
        }

        if (JsonOverlay.GetBool(filters, "actorIsKiller", false))
        {
            if (string.IsNullOrEmpty(ev.KillerPtr))
                return false;
            var ownerKey = grant?.OwnerKey ?? "";
            if (ownerKey.StartsWith("entity:", StringComparison.OrdinalIgnoreCase))
            {
                var ptr = ownerKey[7..];
                if (!string.Equals(ev.KillerPtr, ptr, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            // match / type grants: KillerPtr present = credit exists
        }

        // spec-lawn-action-base.md section "Instakill refusal": a lawnmower / board-wipe hit must not
        // ride a rider that opted out. One key, one owner — this grammar — so a plain-amount grant
        // (which never enters DamagePacketBuilder's event-field branch) is covered too. Absent or
        // false, the grant is unaffected: every existing rider keeps its shipped behaviour.
        if (JsonOverlay.GetBool(filters, "excludeInstakill", false) && ev.InstakillShaped)
            return false;

        return true;
    }

    /// <summary>
    /// Overlay filters.side / typeId refer to the <b>damaged</b> entity on OnDamageDealt
    /// (event.Side is attacker). Other triggers use Side / TypeId as-is.
    /// </summary>
    public static void ResolveFilterTarget(EffectEventDto ev, out string? damagedSide, out int? damagedTypeId)
    {
        if (string.Equals(ev.Trigger, EffectTriggers.OnDamageDealt, StringComparison.OrdinalIgnoreCase))
        {
            damagedSide = InvertSide(ev.Side);
            damagedTypeId = ev.TargetTypeId ?? ev.TypeId;
            return;
        }

        damagedSide = ev.Side;
        damagedTypeId = ev.TypeId ?? ev.TargetTypeId;
    }

    static string? InvertSide(string? side)
    {
        if (string.Equals(side, "plant", StringComparison.OrdinalIgnoreCase)) return "zombie";
        if (string.Equals(side, "zombie", StringComparison.OrdinalIgnoreCase)) return "plant";
        return side;
    }
}

public static class EffectOverlayMerge
{
    /// <summary>Internal (widened 2026-09-06, `EffectActionsAllowlistGuardTests`) so a guard test can
    /// assert this covers every `EffectActions` constant — the fifth time a new opcode shipped without
    /// an entry here (see this field's own trailing comments for the first four, `PlaceStructure` most
    /// recently).</summary>
    internal static readonly Dictionary<string, HashSet<string>> AllowedByAction = new(StringComparer.OrdinalIgnoreCase)
    {
        [EffectActions.ModifyStat] = new(StringComparer.OrdinalIgnoreCase)
            { "channel", "flat", "increased", "more", "byChannel", "chance", "icd_ms", "max_stacks", "filters" },
        // aura-skill-todo.md Phase 5 / TC2. These are the keys of the COMPILED action row, which is
        // NOT the atom's authored ParamSchema: AtomCompiler.ToOpcodeShape rewrites the authored
        // {op:"flat", amount:150} into the op-as-key form {flat:150}, exactly as it already does for
        // stat.modify. So the whitelist mirrors ModifyStat's shape, minus `more` and `byChannel` --
        // the derived side has no More op (AtomDerivedSubsystem.TryParseOp accepts only
        // flat|increased|replace|flag) and no by-channel scaling.
        [EffectActions.ModifyDerivedStat] = new(StringComparer.OrdinalIgnoreCase)
            { "channel", "flat", "increased", "replace", "flag", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.ApplyStatus] = new(StringComparer.OrdinalIgnoreCase)
            { "status", "duration", "level", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.ClearStatus] = new(StringComparer.OrdinalIgnoreCase)
            { "status", "target", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.SpawnEntity] = new(StringComparer.OrdinalIgnoreCase)
        {
            "kind", "typeId", "hp", "maxHp", "atk", "mindControlled", "row", "col", "x",
            "chance", "icd_ms", "max_stacks", "filters"
        },
        [EffectActions.BoardAction] = new(StringComparer.OrdinalIgnoreCase)
            { "op", "row", "col", "x", "y", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.SpawnGridItem] = new(StringComparer.OrdinalIgnoreCase)
            { "gridItemType", "row", "col", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.ClearGridItem] = new(StringComparer.OrdinalIgnoreCase)
            { "selector", "gridItemType", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.SetBoxType] = new(StringComparer.OrdinalIgnoreCase)
            { "boxType", "cells", "row", "col", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.Economy] = new(StringComparer.OrdinalIgnoreCase)
            { "currency", "op", "amount", "capPerMatch", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.ApplyResourceDelta] = new(StringComparer.OrdinalIgnoreCase)
        {
            "channel", "amount", "mode", "mergedCount", "targetPtr",
            "target", "delivery", "burst", "procDepthLimit", "chainDepth",
            "chance", "icd_ms", "max_stacks", "filters",
            // E17: `stat` was documented in status-ssot.md and used in a shipped example, and was
            // in neither the allowlist nor any parser — the example failed validation with
            // "unknown overlay key 'stat'". It lands here WITH its consumer (StatusStatPayload),
            // never before it: an allowlisted key nothing reads is the defect, not the fix.
            "stat",
            "statusId", "periodMs", "durationMs", "status_icd_ms", "statusIcdMs",
            "everyHits", "resetOnBurst", "counterScope", "tickBudget",
            "spread", "immunityTags", "elementPayload"
        },
        [EffectActions.GrantShield] = new(StringComparer.OrdinalIgnoreCase)
        {
            "amount", "element", "priority", "sourceClass", "durationTicks", "refillOnMerge",
            "target", "chance", "icd_ms", "max_stacks", "filters"
        },
        [EffectActions.PresentUi] = new(StringComparer.OrdinalIgnoreCase)
        {
            "op", "amount", "tag", "bannerId", "meterId", "ratio", "durationMs",
            "chance", "icd_ms", "max_stacks", "filters"
        },
        // Found 2026-09-04 while re-verifying E41's own report: this dictionary is not merely an
        // overlay-key allowlist consulted on the Runner path (as AtomCompiler.cs's own nearby comment
        // about a DIFFERENT three kinds might suggest at a glance) — EffectBag.Grant calls
        // EffectOverlayMerge.TryValidateOverlayForDef(def.Actions, ...) UNCONDITIONALLY, for every
        // grant, and TryValidateOverlayForDef fails with "unknown action <X>" the instant ANY action
        // in the def's compiled list is missing from this dictionary, even against an EMPTY overlay.
        // ModifyMatch (E35), WaveControl (E36) and BulletModify (E37) had no entry here, so any real
        // Grant() of match.modify/wave.control/bullet.modify content — the only way any of that
        // shipped work ever runs in a live match — threw at the very first line of Grant, regardless
        // of runtime, regardless of overlay content. Each module's own tests never caught this because
        // they exercise AtomCompiler.Compile / InjectorEffectActionSink.Execute directly, never
        // EffectBag.Grant. Keys mirror each kind's own compiled action params exactly (ToOpcodeShape
        // only rewrites stat.modify/stat.derived, so all three kinds' authored params travel unchanged
        // — confirmed against each kind's own AtomKindRegistry.cs ParamSchema and Compilability.cs
        // comment), plus the same generic chance/icd_ms/max_stacks/filters every other entry carries.
        [EffectActions.ModifyMatch] = new(StringComparer.OrdinalIgnoreCase)
            { "field", "amount", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.WaveControl] = new(StringComparer.OrdinalIgnoreCase)
            { "op", "wave", "timerMs", "enabled", "chance", "icd_ms", "max_stacks", "filters" },
        [EffectActions.BulletModify] = new(StringComparer.OrdinalIgnoreCase)
            { "op", "amount", "bulletType", "moveWay", "chance", "icd_ms", "max_stacks", "filters" },
        // base-defense `siege-construction` 15.3b (2026-09-06): structure.place -> PlaceStructure. The
        // FIFTH time this exact class of bug has bitten (ModifyMatch/WaveControl/BulletModify's own
        // comment above already documents the first three; Compilability.OpcodeKinds' own comment,
        // fixed the same day as this entry, documents a fourth, separate list with the identical shape)
        // — EffectBag.Grant calls TryValidateOverlayForDef UNCONDITIONALLY for every grant, so a
        // structure.place effect would throw "unknown action PlaceStructure" the instant anything ever
        // actually granted it, regardless of overlay content. Found by a real, failing end-to-end test
        // (ConstructionActionsTests), not by inspection. Keys mirror structureId/instant exactly
        // (ToOpcodeShape only rewrites stat.modify/stat.derived, so both travel unchanged).
        [EffectActions.PlaceStructure] = new(StringComparer.OrdinalIgnoreCase)
            { "structureId", "instant", "chance", "icd_ms", "max_stacks", "filters" },
    };

    public static bool TryValidateOverlayForDef(
        IReadOnlyList<EffectActionRow> actions,
        Dictionary<string, object?> overlay,
        out string? error)
    {
        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "filters", "chance", "icd_ms", "max_stacks"
        };
        foreach (var action in actions)
        {
            if (!AllowedByAction.TryGetValue(action.Action, out var allowed))
            {
                error = "unknown action " + action.Action;
                return false;
            }
            foreach (var k in allowed) union.Add(k);
        }

        foreach (var kv in overlay)
        {
            if (!union.Contains(kv.Key))
            {
                error = "unknown overlay key '" + kv.Key + "' for effect actions";
                return false;
            }
        }

        error = null;
        return true;
    }

    public static bool TryMerge(
        string action,
        Dictionary<string, object?> actionParams,
        Dictionary<string, object?> overlay,
        out Dictionary<string, object?> merged,
        out string? error)
    {
        merged = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in actionParams) merged[kv.Key] = kv.Value;

        if (!AllowedByAction.TryGetValue(action, out var allowed))
        {
            error = "unknown action " + action;
            return false;
        }

        foreach (var kv in overlay)
        {
            if (kv.Key.Equals("filters", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("chance", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("icd_ms", StringComparison.OrdinalIgnoreCase) ||
                kv.Key.Equals("max_stacks", StringComparison.OrdinalIgnoreCase))
                continue;

            // Grant-time validated against action union; per-action merge applies only keys for this action.
            if (!allowed.Contains(kv.Key))
                continue;

            merged[kv.Key] = kv.Value;
        }

        // channel-specific magnitudes
        if (string.Equals(action, EffectActions.ModifyStat, StringComparison.OrdinalIgnoreCase) &&
            overlay.TryGetValue("byChannel", out var byCh) && byCh != null)
        {
            var channel = JsonOverlay.GetString(merged, "channel");
            var map = JsonOverlay.FromObject(byCh);
            if (!string.IsNullOrEmpty(channel) && map.TryGetValue(channel, out var chObj) && chObj != null)
            {
                foreach (var kv in JsonOverlay.FromObject(chObj))
                    merged[kv.Key] = kv.Value;
            }
        }
        else if (string.Equals(action, EffectActions.ModifyStat, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var mag in new[] { "flat", "increased", "more" })
            {
                if (overlay.TryGetValue(mag, out var v))
                    merged[mag] = v;
            }
        }

        error = null;
        return true;
    }
}

public sealed class EffectProcPolicy
{
    readonly IEffectClock _clock;
    readonly IEffectRandom _rng;
    readonly Dictionary<string, DateTimeOffset> _lastFire = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _stacks = new(StringComparer.Ordinal);

    public EffectProcPolicy(IEffectClock clock, IEffectRandom rng)
    {
        _clock = clock;
        _rng = rng;
    }

    public void Clear()
    {
        _lastFire.Clear();
        _stacks.Clear();
    }

    public void ClearGrant(string grantId)
    {
        if (string.IsNullOrEmpty(grantId)) return;
        var prefix = grantId + "|";
        foreach (var k in _lastFire.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _lastFire.Remove(k);
        foreach (var k in _stacks.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _stacks.Remove(k);
    }

    public bool TryPass(EffectGrant grant, string trigger, out string? skipReason, int hitCount = 1)
    {
        var overlay = grant.Overlay;
        var chance = overlay.ContainsKey("chance")
            ? JsonOverlay.GetDouble(overlay, "chance", 1)
            : 1.0;
        if (chance < 1.0)
        {
            // v2 merged-record math: P(≥1 of n per-hit rolls) = 1−(1−p)^n — one roll, exactly
            // the per-hit distribution for at-most-one-proc-per-event semantics
            // (event-pipeline-v2-spec.md decision #2). hitCount=1 is byte-identical to v1.
            var effective = hitCount <= 1 ? chance : 1.0 - Math.Pow(1.0 - chance, hitCount);
            if (_rng.NextDouble() > effective)
            {
                skipReason = "chance";
                return false;
            }
        }

        var icdMs = ResolveIcdMs(overlay, trigger);
        var key = grant.GrantId + "|" + grant.EffectId;
        if (icdMs > 0 && _lastFire.TryGetValue(key, out var last))
        {
            var elapsed = (_clock.UtcNow - last).TotalMilliseconds;
            if (elapsed < icdMs)
            {
                skipReason = "icd";
                return false;
            }
        }

        var maxStacks = JsonOverlay.GetInt(overlay, "max_stacks", 0);
        if (maxStacks > 0)
        {
            _stacks.TryGetValue(key, out var s);
            if (s >= maxStacks)
            {
                skipReason = "max_stacks";
                return false;
            }

            // Merged records consume one stack per hit, clamped (spec decision #2).
            _stacks[key] = Math.Min(maxStacks, s + Math.Max(1, hitCount));
        }

        if (icdMs > 0)
            _lastFire[key] = _clock.UtcNow;

        skipReason = null;
        return true;
    }

    public static int ResolveIcdMs(Dictionary<string, object?> overlay, string trigger)
    {
        if (overlay.ContainsKey("icd_ms"))
            return JsonOverlay.GetInt(overlay, "icd_ms", 0);

        if (string.Equals(trigger, EffectTriggers.OnDamageDealt, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trigger, EffectTriggers.OnDamageTaken, StringComparison.OrdinalIgnoreCase))
            return 250;

        return 0;
    }
}

public sealed class EffectEventDedupe
{
    readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    readonly long _windowTicks;

    public EffectEventDedupe(long windowTicks = 1)
    {
        _windowTicks = Math.Max(1, windowTicks);
    }

    public bool ShouldEmit(EffectEventDto ev)
    {
        var key = string.Join("|",
            ev.MatchKey ?? "",
            ev.Trigger,
            ev.ActorPtr ?? "",
            ev.TargetPtr ?? "",
            (ev.Tick / _windowTicks).ToString());
        if (_seen.TryGetValue(key, out var t) && t == ev.Tick / _windowTicks)
            return false;
        _seen[key] = ev.Tick / _windowTicks;
        if (_seen.Count > 4096)
            _seen.Clear();
        return true;
    }

    public void Clear() => _seen.Clear();
}
