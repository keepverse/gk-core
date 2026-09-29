namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// Why an atom row, param, or binding was refused. Never a bool — the whole point of this layer
/// is that a refusal says which rule fired, so a content author can fix it without a debugger.
/// </summary>
public enum AtomRejectionReason
{
    None = 0,

    /// <summary>Kind id is not in the registry (G6 sibling: unknown never passes silently).</summary>
    UnknownKind,

    /// <summary>Param key is not declared by the kind's schema.</summary>
    UnknownParam,

    /// <summary>A required param is absent. Never defaulted — see G7.</summary>
    MissingParam,

    /// <summary>
    /// The key is declared but the executor drops it for this configuration (G1).
    /// Accepting it would be a silent no-op, which is exactly what this layer refuses.
    /// </summary>
    ParamNotHonoured,

    /// <summary>Declared in the legacy allowlist but never implemented anywhere (G2, G4).</summary>
    ParamNotImplemented,

    /// <summary>Param present but its value is outside the declared domain.</summary>
    BadParamValue,

    /// <summary>Owner scope cannot carry this kind — e.g. entity-scoped primary defense (G8).</summary>
    ScopeUnsupported,

    /// <summary>No consumer exists for this kind in the target runtime.</summary>
    RuntimeUnsupported,

    /// <summary>Target is empty where "all" would be inferred (G5). "All" must be explicit.</summary>
    AmbiguousTarget,

    /// <summary>Trigger is not one of the 7.</summary>
    UnknownTrigger,

    /// <summary>Trigger is real, but this kind may not carry it.</summary>
    TriggerNotAllowed,

    // The codes below are raised by later modules. They live here because the list is closed:
    // definitions.md §10 fixes it at 33, and a code invented at the point of use is a code no
    // operator can look up. Declaring them together is what keeps E2/E3/E4/E5/E6 from each
    // reopening this file — and what lets one guard test assert the count.

    /// <summary>Value spec is malformed — min > max, or a roll policy that is not one of the three (E2).</summary>
    BadValueSpec,

    /// <summary>Curve id is unknown, or its points are not monotonic in the input (E2).</summary>
    BadCurve,

    /// <summary>A scaled magnitude leaves the integer range the channel can carry (E2).</summary>
    MagnitudeOverflow,

    /// <summary>`atom_id` does not equal `{family_id}[.{variant}].t{tier}` derived from its own columns (E4).</summary>
    IdMismatch,

    /// <summary>A unique key is already taken (E4, E5).</summary>
    DuplicateKey,

    /// <summary>Predicate leaf is not in the closed leaf list — never ignored (E3).</summary>
    UnknownLeaf,

    /// <summary>A leaf omitted its subject. Every leaf declares one; the event's inversion makes it ambiguous (E3).</summary>
    AmbiguousSubject,

    /// <summary>Predicate tree is deeper than the hard limit (E3).</summary>
    DepthExceeded,

    /// <summary>Predicate tree has more nodes than the hard limit (E3).</summary>
    NodeCountExceeded,

    /// <summary>An AND/OR node with no children — silently true or silently false, so refused (E3).</summary>
    EmptyNode,

    /// <summary>Container references an atom id that does not exist (E5).</summary>
    UnknownAtom,

    /// <summary>Binding references a container id that does not exist (E6).</summary>
    UnknownContainer,

    /// <summary>Two container rows share a `seq` — order would not be stable (E5).</summary>
    DuplicateSeq,

    /// <summary>The same atom appears in both the fixed core and the pool (E5).</summary>
    DuplicateAtomInContainer,

    /// <summary>Every pool row has weight 0, so a draw would under-fill the instance (E5).</summary>
    UnsatisfiablePool,

    /// <summary>`pool_rolls` exceeds the number of groups that can actually be drawn from (E5).</summary>
    PoolRollsExceedGroups,

    /// <summary>Atom tier is outside the rarity's `[min_tier, max_tier]` window (E5).</summary>
    TierOutOfWindow,

    /// <summary>A container override tried to change an atom's `kind_id` — an override tunes, it does not rewrite (E5).</summary>
    OverrideChangesKind,

    /// <summary>A stored power override arrived without the required note explaining the gap (E9).</summary>
    MissingPowerNote,

    /// <summary>Owner key does not match its scope's grammar — see definitions.md §6 (E6).</summary>
    BadOwnerKey,

    /// <summary>Instance was rolled against a different `catalog_revision` than the one now loaded (E6).</summary>
    StaleInstance,

    /// <summary>Owner does not meet the binding's `level_req` (E6).</summary>
    LevelTooLow,

    /// <summary>
    /// One content-authoring rule outside the 33 above, carried as a namespaced rule id in
    /// <see cref="AtomRejection.Detail"/> — e.g. <c>"atom.empty-name: 'atom.foo.t1' has no display
    /// name"</c>. Chosen over minting a new enum member per rule (item-ideal.md §2b.1): 101 discrete
    /// codes is a vocabulary no operator can hold, and every lane that would have needed its own code
    /// instead registers a namespace via <see cref="ContentRuleNamespaces.Register"/> and raises this
    /// one value. <b>This is the 34th and last member by design</b> — a caller that wants a new rule
    /// registers a namespace, it never mints a 35th code. First wired by <c>durable-ownership</c>
    /// (item module 1), the program's first schema-validation consumer of it.
    /// </summary>
    ContentRuleViolated,
}

/// <summary>
/// The namespace prefixes <see cref="AtomRejectionReason.ContentRuleViolated"/> may carry, one
/// registration per lane that raises it. A rule id under an unregistered prefix is a bug in the
/// caller, not a data problem — <see cref="AtomRejection.ContentRule"/> throws rather than silently
/// accepting an unregistered vocabulary, exactly as an unlisted
/// <see cref="FusionRpg.Core.Scope.ScopeCompatibility"/> combination throws rather than guessing.
/// </summary>
public static class ContentRuleNamespaces
{
    static readonly HashSet<string> Registered = new(StringComparer.Ordinal)
    {
        // durable-ownership (item module 1) registers its own namespace rather than leaving
        // registration to whichever lane happens to load first — C3's empty-name check is the first
        // real consumer (AtomRowValidator).
        "atom",
    };

    /// <summary>Called once per lane, at the point it starts raising rule ids under its prefix.</summary>
    public static void Register(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("a content-rule namespace prefix must be non-empty", nameof(prefix));
        Registered.Add(prefix);
    }

    /// <summary>True when <paramref name="ruleId"/>'s dot-prefix (e.g. <c>"atom"</c> of <c>"atom.empty-name"</c>) is registered.</summary>
    public static bool IsRegistered(string ruleId)
    {
        var dot = ruleId?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        return dot > 0 && Registered.Contains(ruleId!.Substring(0, dot));
    }

    public static IReadOnlyCollection<string> All => Registered;
}

/// <summary>One refusal: the rule that fired plus enough detail to fix the row.</summary>
public readonly record struct AtomRejection(AtomRejectionReason Reason, string Detail)
{
    public static AtomRejection Ok => new(AtomRejectionReason.None, "");

    public bool IsOk => Reason == AtomRejectionReason.None;

    public static AtomRejection Fail(AtomRejectionReason reason, string detail) => new(reason, detail);

    /// <summary>
    /// One namespaced content rule, raised as the single <see cref="AtomRejectionReason.ContentRuleViolated"/>
    /// code (item-ideal.md §2b.1 — "one code with a namespaced payload", never a second code family).
    /// <paramref name="ruleId"/> must be under a namespace some lane has registered via
    /// <see cref="ContentRuleNamespaces.Register"/>.
    /// </summary>
    public static AtomRejection ContentRule(string ruleId, string detail)
    {
        if (!ContentRuleNamespaces.IsRegistered(ruleId))
            throw new InvalidOperationException(
                $"content rule id '{ruleId}' is not under a namespace registered via " +
                $"{nameof(ContentRuleNamespaces)}.{nameof(ContentRuleNamespaces.Register)}");
        return new AtomRejection(AtomRejectionReason.ContentRuleViolated, $"{ruleId}: {detail}");
    }

    public override string ToString() => IsOk ? "ok" : $"{Reason}: {Detail}";
}
