namespace FusionRpg.Core.Status;

/// <summary>Migration golden / parity shim for the 24 locked status ids — status-ssot.md §9.
/// Live hosts inject via <see cref="StatusCatalogFactory"/> + <see cref="StatusCatalogHub"/>
/// (status-rail B1). Prefer the hub's <c>Current</c> in production paths.</summary>
public static class StatusCatalogBootstrap
{
    public static StatusCatalog CreateDefault()
    {
        var catalog = new StatusCatalog();
        RegisterAll(catalog);
        return catalog;
    }

    public static void RegisterAll(StatusCatalog catalog)
    {
        // 9.2 Engine wraps (UnityCc)
        Register(catalog, "butter", StatusKind.UnityCc, "cc", StatusStacking.Replace, StatusPayloadKind.UnityCc);
        Register(catalog, "freeze", StatusKind.UnityCc, "elemental", StatusStacking.Replace, StatusPayloadKind.UnityCc);
        Register(catalog, "cold", StatusKind.UnityCc, "elemental", StatusStacking.Replace, StatusPayloadKind.UnityCc);
        Register(catalog, "poison", StatusKind.UnityCc, "elemental", StatusStacking.Replace, StatusPayloadKind.UnityCc);
        Register(catalog, "hypno", StatusKind.UnityCc, "cc", StatusStacking.Replace, StatusPayloadKind.UnityCc);
        Register(catalog, "ember", StatusKind.UnityCc, "mixer", StatusStacking.Coexist, StatusPayloadKind.UnityCc);
        Register(catalog, "jala", StatusKind.UnityCc, "elemental", StatusStacking.Replace, StatusPayloadKind.UnityCc);
        Register(catalog, "kelp", StatusKind.UnityCc, "slow", StatusStacking.Replace, StatusPayloadKind.UnityCc);

        // 9.3 Overlay-authored
        Register(catalog, "wither", StatusKind.OverTime, "overlay", StatusStacking.Refresh, StatusPayloadKind.PulseHp);
        Register(catalog, "bond", StatusKind.Counter, "overlay", StatusStacking.Refresh);
        Register(catalog, "rally", StatusKind.Buff, "overlay", StatusStacking.Refresh, StatusPayloadKind.ModifyStat);
        // pulseHealsAttacker: true -- spec-healing-pair.md §3, finishing the half the catalog shipped
        // half-built ("damage half only — the heal half was never built").
        RegisterWithOptions(catalog, "leech", StatusKind.OverTime, "overlay", StatusStacking.Refresh,
            new[] { StatusPayloadKind.PulseHp }, pulseHealsAttacker: true);
        Register(catalog, "expose", StatusKind.Debuff, "overlay", StatusStacking.Refresh, StatusPayloadKind.ModifyStat);
        Register(catalog, "command", StatusKind.Meter, "overlay", StatusStacking.Refresh, StatusPayloadKind.ModifyStat);
        Register(catalog, "shatter", StatusKind.Debuff, "overlay", StatusStacking.Refresh, StatusPayloadKind.ModifyStat);
        // E17: corrected from UnityCc. NO vanilla method exists for it — an assembly-metadata sweep
        // of Assembly-CSharp found SetEmbered / SetJalaed / SetKelped but no SetCharm*, only
        // SetZombieWithMindControl / SetZombieMindControlledNode. Declaring UnityCc named an
        // execution path the game does not have, which is a DEF ERROR and not missing wiring.
        //
        // What that cost, concretely: FA2 is emitted only for UnityCc statuses
        // (StatusEffectBridge.cs:315), so every application queued an ApplyStatus action that
        // reached the injector's status switch, matched no case, and did nothing. An inert plan item
        // that looked like a working effect in every trace.
        //
        // ModifyStat is what an overlay-authored status can actually do now that the payload has a
        // consumer. Deliberately NOT faked with a float write — that is the applyFloatSlow path,
        // documented as weak and VFX-less, and it would make the status look implemented while doing
        // something else. It still CC-locks in battle: that reads the `cc` CATEGORY, which is
        // unchanged and is what the status means rather than how it is delivered.
        Register(catalog, "charm_pulse", StatusKind.CrowdControl, "overlay", StatusStacking.Replace, StatusPayloadKind.ModifyStat);

        // 9.4 Contagion
        Register(catalog, "blight", StatusKind.Contagion, "overlay", StatusStacking.Refresh, StatusPayloadKind.Spread, StatusPayloadKind.PulseHp);
        Register(catalog, "rot", StatusKind.Contagion, "overlay", StatusStacking.Refresh, StatusPayloadKind.Spread, StatusPayloadKind.PulseHp);
        Register(catalog, "spark", StatusKind.Contagion, "overlay", StatusStacking.Refresh, StatusPayloadKind.Spread, StatusPayloadKind.PulseHp);
        Register(catalog, "pact_mark", StatusKind.Contagion, "overlay", StatusStacking.Refresh, StatusPayloadKind.Spread, StatusPayloadKind.PulseHp);
        Register(catalog, "spore", StatusKind.Contagion, "overlay", StatusStacking.Refresh, StatusPayloadKind.Spread, StatusPayloadKind.PulseHp);

        // 9.5 Nerve (P3, delve-attrition D2.19) -- the Darkest Dungeon affliction ladder, one id per
        // `nerveStage` registry member (bands.v1.json), in threshold order. The live instance is a
        // PROJECTION of the stack counter in party state (`DelveMemberState.NerveStacks`), never the
        // counter itself -- `NervePolicy.Sync` keeps at most one of these three live per creature.
        Register(catalog, "nerve.unsettled", StatusKind.Debuff, "nerve", StatusStacking.Replace, StatusPayloadKind.ModifyStat);
        Register(catalog, "nerve.shaken", StatusKind.Debuff, "nerve", StatusStacking.Replace, StatusPayloadKind.ModifyStat);
        Register(catalog, "nerve.afflicted", StatusKind.Debuff, "nerve", StatusStacking.Replace, StatusPayloadKind.ModifyStat);
    }

    /// <summary>
    /// solid-remediation T5.1 (X1): <paramref name="statusId"/>'s L2b category is no longer passed in
    /// here. It used to be, which meant the status-id to resist-category vocabulary had TWO owners —
    /// this file's 23 literals and <see cref="StatusCategoryRegistry"/>'s map — that could drift with
    /// nothing refusing the drift. The registry is the owner; this reads from it.
    ///
    /// <para><see cref="StatusCategoryRegistry.GetRequiredCategory"/> throws for an unknown id, so a
    /// status registered here without a category entry fails loudly at bootstrap instead of shipping a
    /// catalog whose entry disagrees with the resist channel that is supposed to answer it.</para>
    /// </summary>
    static void Register(
        StatusCatalog catalog,
        string statusId,
        StatusKind kind,
        string family,
        StatusStacking stacking,
        params StatusPayloadKind[] payloadKinds)
    {
        var primaryCategory = StatusCategoryRegistry.GetRequiredCategory(statusId);

        catalog.Register(new StatusDef(
            statusId,
            kind,
            family,
            new[] { primaryCategory },
            Array.Empty<string>(),
            stacking,
            payloadKinds));
    }

    /// <summary>
    /// A separate name, not an overload of <see cref="Register"/> — <c>params</c> must be a method's
    /// last parameter, so it cannot sit before trailing optional ones, and giving both methods the
    /// same name at the same arity would make every existing 7-argument call site ambiguous between
    /// them. Only <c>leech</c> uses this one.
    ///
    /// <para>combat-math-dedup T1: the L2b category is read from <see cref="StatusCategoryRegistry"/>
    /// exactly as <see cref="Register"/> reads it — <c>leech</c> was the last call site that passed a
    /// category of its own, and that second declaration could drift from the registry's. This file now
    /// names no <c>StatusL2bCategory</c> value at all.</para>
    /// </summary>
    static void RegisterWithOptions(
        StatusCatalog catalog,
        string statusId,
        StatusKind kind,
        string family,
        StatusStacking stacking,
        StatusPayloadKind[] payloadKinds,
        string? element = null,
        bool pulseHealsAttacker = false)
    {
        catalog.Register(new StatusDef(
            statusId,
            kind,
            family,
            new[] { StatusCategoryRegistry.GetRequiredCategory(statusId) },
            Array.Empty<string>(),
            stacking,
            payloadKinds,
            Element: element,
            PulseHealsAttacker: pulseHealsAttacker));
    }
}
