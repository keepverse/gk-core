using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items.Activation;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Battle;

/// <summary>
/// Where a specimen's equipped items' static channel mods come from (item-ideal.md, `equip-runtime`
/// — module 5, the payoff). The same shape <see cref="TraitAtomSource"/> already ships (E12): bound
/// `stat.derived` atoms merge at COMPOSE time, a path battle already runs. Equipment differs only in
/// where the bindings come from — the durable assignment projection (module 4), resolved through
/// <c>ResolveBindings</c> at <c>unique-actor:</c> scope, rather than a trait catalog. Nothing new in
/// the pipeline.
///
/// <para><b>Closes the write-only half of a live production defect.</b>
/// <c>ProduceAndBind</c> already binds a `UniqueActor`'s items (`RpgStore.UniqueActors.cs`); this is
/// the first read of them. Before this, <c>UniqueActor</c> bindings existed only to be written.</para>
/// </summary>
/// <summary>One equipped <c>stat.derived</c> atom with GG-49 role/item attribution
/// (<see cref="ContributionSourceIds.Equip"/>), or — when <see cref="SocketIndex"/> carries a value —
/// a socketed insert's atom (<see cref="ContributionSourceIds.Insert"/>), or — when
/// <see cref="ComboId"/> carries a value — a satisfied Strain/Splice's atom
/// (<see cref="ContributionSourceIds.Combo"/>). The index is the socket's
/// own 0-based position: the ONLY thing distinguishing two identical gems in two sockets, so it is
/// data on the input, never recomputed at the mint. A null index means the host's own affix.
/// <para><see cref="Circuit"/> is the combination's own 0-based circuit index
/// (strain-splice-host combo-bind, arm 2): the same comboId can fire in two circuits on one
/// eight-socket host, so the id carries it (defaulting to 0) exactly as an insert carries its
/// socket. Ignored unless <see cref="ComboId"/> is set.</para>
/// <para><see cref="RefKind"/> is the HOST assignment's own <c>ref_kind</c> (<c>"rolled"</c> or
/// <c>"stock"</c>), carried so an activation filter can rebuild the durable assignment's identity
/// (<see cref="EquipmentAssignmentIdentity"/>) from this input alone. It is optional because the
/// pre-module-24 callers never needed it; a source with an activation filter wired REQUIRES it and
/// throws when it is missing rather than guessing a kind.</para></summary>
public readonly record struct EquippedAtomInput(
    string Role, string ItemRefId, AtomRow Atom, int? SocketIndex = null,
    string? ComboId = null, int? Circuit = null, string? RefKind = null);

public sealed class EquipAtomSource
{
    readonly Func<string, IReadOnlyList<EquippedAtomInput>> _resolveEquipped;

    /// <summary>The host's opaque deployment key, or null when no activation filter is wired.</summary>
    readonly string? _deploymentKey;

    /// <summary>item module 24's activation predicate, or null. Null is the pre-module-24 behaviour:
    /// every resolved binding contributes, byte-identically to before.</summary>
    readonly Func<EquipmentAssignmentIdentity, bool>? _isActive;

    EquipAtomSource(
        Func<string, IReadOnlyList<EquippedAtomInput>> resolveEquipped,
        string? deploymentKey = null,
        Func<EquipmentAssignmentIdentity, bool>? isActive = null)
    {
        _resolveEquipped = resolveEquipped;
        _deploymentKey = deploymentKey;
        _isActive = isActive;
    }

    /// <summary>Nothing wired — every specimen resolves to no equipment mods. The pre-module-5 state.</summary>
    public static readonly EquipAtomSource None = new(_ => Array.Empty<EquippedAtomInput>());

    /// <summary>
    /// Production shape with role + item attribution (<c>equip:{role}:{itemRef}</c>).
    /// Server battle and sheet use this via <c>EquippedBoundAtoms</c>. Prefer over
    /// <see cref="FromResolver(Func{string, IReadOnlyList{AtomRow}})"/> (legacy flatten).
    ///
    /// <para><paramref name="deploymentKey"/> and <paramref name="isActive"/> are supplied TOGETHER or
    /// not at all: the filter is a deployment-scoped read state, so a predicate without the key it is
    /// keyed by is not a half-wired feature but an unanswerable question, and it throws.</para>
    /// </summary>
    public static EquipAtomSource FromEquippedResolver(
        Func<string, IReadOnlyList<EquippedAtomInput>> resolveEquipped,
        string? deploymentKey = null,
        Func<EquipmentAssignmentIdentity, bool>? isActive = null)
    {
        if (resolveEquipped is null) throw new ArgumentNullException(nameof(resolveEquipped));
        RequireBothOrNeither(deploymentKey, isActive);
        return new EquipAtomSource(resolveEquipped, deploymentKey, isActive);
    }

    /// <summary>
    /// This source, additionally filtered through a deployment's activation status. The same filter
    /// the deployment's <c>EquipmentActivationService</c> answers, applied at the ONE existing
    /// equipment read — never a second effect path and never a change to the durable binding.
    /// </summary>
    public EquipAtomSource WithActivationFilter(
        string deploymentKey, Func<EquipmentAssignmentIdentity, bool> isActive)
    {
        RequireBothOrNeither(deploymentKey, isActive);
        return new EquipAtomSource(_resolveEquipped, deploymentKey, isActive);
    }

    static void RequireBothOrNeither(string? deploymentKey, Func<EquipmentAssignmentIdentity, bool>? isActive)
    {
        if (string.IsNullOrWhiteSpace(deploymentKey) != (isActive is null))
            throw new ArgumentException(
                "an activation filter needs its deployment key, and a deployment key needs its filter");
    }

    /// <summary>
    /// <b>Legacy</b> flatten shape — not the Server production path. Prefer
    /// <see cref="FromEquippedResolver"/>. SourceId becomes <c>equip:unknown:{atomId}</c>
    /// (still attributable, no slot fiction). Production battle uses
    /// <c>EquippedBoundAtoms.SourceFromStore</c> → <see cref="FromEquippedResolver"/>.
    /// </summary>
    public static EquipAtomSource FromResolver(Func<string, IReadOnlyList<AtomRow>> resolveEquippedAtoms)
    {
        if (resolveEquippedAtoms is null) throw new ArgumentNullException(nameof(resolveEquippedAtoms));
        return new(specimenId =>
        {
            var rows = resolveEquippedAtoms(specimenId);
            if (rows is null || rows.Count == 0) return Array.Empty<EquippedAtomInput>();
            var list = new List<EquippedAtomInput>(rows.Count);
            foreach (var atom in rows)
            {
                var id = string.IsNullOrWhiteSpace(atom.AtomId) ? atom.FamilyId : atom.AtomId;
                list.Add(new EquippedAtomInput("unknown", id, atom));
            }
            return list;
        });
    }

    /// <summary>
    /// This specimen's equipped <c>stat.derived</c> atoms, projected for <see cref="ActorHub"/> /
    /// <see cref="AtomDerivedSubsystem"/> — the ONE consumer now that battle-hub-fuse (T6) deleted
    /// <c>BattleStatComposer</c> and battle-ops-parity (T7) deleted the battle-only <c>ModsFor</c>
    /// ignore-op fold this doc comment used to also describe. Battle reads this exact method too now
    /// (via <see cref="BattleHubInputs.BoundAtoms"/>), so there is only one projection to drift from,
    /// not two to keep in sync.
    ///
    /// <para><b>The op IS honoured here</b>, through <see cref="AtomDerivedSubsystem.TryParseOp"/> —
    /// the shipped parser whose own contract is that an unknown or `more` op is a content error to
    /// skip, never something to coerce into a wrong-but-plausible `flat`. Skipping rather than
    /// coercing is what keeps a bad row visible at the bind gate instead of shipping a silently wrong
    /// number.</para>
    /// </summary>
    public IReadOnlyList<BoundDerivedAtom> DerivedAtomsFor(string specimenId)
    {
        var atoms = new List<BoundDerivedAtom>();
        foreach (var parsed in EquippedDerived(specimenId))
        {
            if (!AtomDerivedSubsystem.TryParseOp(parsed.Op, out var op)) continue;
            atoms.Add(new BoundDerivedAtom(parsed.Channel, op, parsed.Amount, parsed.SourceId));
        }
        return atoms;
    }

    /// <summary>
    /// The ONE parse of a specimen's equipped `stat.derived` atoms, feeding <see cref="DerivedAtomsFor"/>.
    ///
    /// <para><b><c>amount</c> is read as <c>long</c>, not <c>int</c>.</b> The first cut of this class
    /// used <c>TryGetInt32</c>, which does not throw on a larger magnitude — it returns false, so the
    /// atom was silently DROPPED. That is the exact shape docs/architecture/numeric-types.md's binding numeric rule forbids
    /// ("`long` for any magnitude... overflow throws, never wraps"), and a dropped equip line is a
    /// defect with no symptom. Every value the shipped corpus carries fits either width, so no number
    /// anywhere moves; what changes is that a magnitude above <c>int.MaxValue</c> now applies instead
    /// of vanishing.</para>
    /// </summary>
    IEnumerable<(string Channel, string? Op, long Amount, string SourceId)> EquippedDerived(string specimenId)
    {
        foreach (var input in _resolveEquipped(specimenId))
        {
            // item module 24: an item whose deployment-run status is not Active contributes NOTHING —
            // not one atom of it. The durable binding is untouched; only this read is filtered, so
            // the item disappears from composition as ONE contribution and returns the same way.
            if (_isActive is not null && !ContributesNow(specimenId, input)) continue;

            var atom = input.Atom;
            if (!string.Equals(atom.KindId, "stat.derived", StringComparison.Ordinal)) continue;

            var pars = Effects.Atoms.Power.CostFunction.Read(atom.ParamsJson);
            if (!pars.TryGetValue("channel", out var chEl)
                || chEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
            if (!pars.TryGetValue("amount", out var amtEl)) continue;
            // `amount` is ParamKind.Value: a plain number OR a ValueSpec OBJECT (a curve reference,
            // or `patron-absorption`'s `externalRef`). Guarding the kind is not optional —
            // JsonElement.TryGetInt64 THROWS on an object, it does not return false, and
            // `gk-data/packs/fusion/data/seed/atoms/patron-aura.json` ships twelve such `stat.derived` rows. Without this
            // line a single unresolvable row took the whole compose with it (measured 2026-09-06: the
            // geared corner run, which sweeps every `stat.derived` row in the corpus, died with an
            // unhandled InvalidOperationException). Skip the row and keep walking, exactly as an
            // unparseable `op` is skipped: this seam has no ValueSpec resolver — AtomCompiler owns
            // that — so resolving one here would be a second, divergent evaluation of the same spec.
            if (amtEl.ValueKind != System.Text.Json.JsonValueKind.Number) continue;
            if (!amtEl.TryGetInt64(out var amount)) continue;

            var op = pars.TryGetValue("op", out var opEl) && opEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? opEl.GetString()
                : null;

            // GG-49: equip:{role}:{itemRef} — sheet fiction via ContributionSourceIds.FictionLabel.
            // An insert input (SocketIndex set) mints insert:{role}:{hostItemRef}#{socketIndex}
            // instead: same host role (owner decision, Round-3 #5), index in the id only. Reusing
            // Equip() here would attribute the gem's number to the host's own affixes — the
            // wrong-but-plausible defect the Insert arm exists to prevent. A combination input
            // (ComboId set) mints combo:{role}:{hostItemRef}:{comboId}#c{circuit}, the reserved §8.1
            // row plus the circuit suffix — an eight-socket host can fire the same comboId in two
            // circuits, and one id would collapse the two.
            yield return (chEl.GetString()!, op, amount, MintSourceId(input));
        }
    }

    /// <summary>
    /// Whether this input's durable assignment is active in the wired deployment. The identity is
    /// rebuilt from the input and the source's own deployment key — the host's assignment refKind is
    /// carried on the input for exactly this reason. A missing refKind while a filter IS wired is a
    /// host bug: guessing a kind would either hide a contributing item or show a suspended one, so it
    /// throws.
    /// </summary>
    bool ContributesNow(string specimenId, in EquippedAtomInput input)
    {
        if (string.IsNullOrWhiteSpace(input.RefKind))
            throw new InvalidOperationException(
                $"an activation filter is wired but the equipped input for specimen '{specimenId}' " +
                $"role '{input.Role}' carries no refKind, so its assignment identity cannot be rebuilt");

        var identity = new EquipmentAssignmentIdentity(
            _deploymentKey!, specimenId, input.Role, input.RefKind!, input.ItemRefId);
        return _isActive!(identity);
    }

    static string MintSourceId(EquippedAtomInput input)
    {
        if (input.ComboId is { } comboId)
            return ContributionSourceIds.Combo(input.Role, input.ItemRefId, comboId, input.Circuit ?? 0);
        return input.SocketIndex is { } socketIndex
            ? ContributionSourceIds.Insert(input.Role, input.ItemRefId, socketIndex)
            : ContributionSourceIds.Equip(input.Role, input.ItemRefId);
    }
}
