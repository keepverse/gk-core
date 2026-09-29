namespace FusionRpg.Core.Stats.Plugins;

/// <summary>class-system-todo.md P1.9 — a registered plugin/subsystem that is empty ON PURPOSE self-
/// declares it, so the seam-coverage guard can tell "wired, nothing to contribute right now" apart
/// from "silently empty, a bug with a green test beside it" (class-system-map.md §6, distribution-
/// reconcile's own finding). Implemented by the type; never inferred from behaviour.</summary>
public interface IDeclaredInertContributor
{
    string InertReason { get; }
}

/// <summary>
/// class-system-todo.md P1.8 — kept deliberately empty, not deleted and not filled.
///
/// <para><b>What it is for:</b> the primary `StatSystem` pipeline — <c>EntityBaseline.AttackInterval</c>
/// (`StatComposer` → `EntityFinal` → the injector-side stat writer), the overlay-side attack-speed route
/// `Agility` would compose through if a future affix or effect needs to reach it. That pipeline is
/// real and shipped; this plugin is its class-system-scoped seam, registered and order-pinned
/// (`StatSystemTests.cs`), simply with nothing wired into it yet.</para>
///
/// <para><b>What it is NOT for:</b> the twelve aptitudes' own channels. `Might`/`Fortitude`/etc. feed
/// **83 derived** channels through <c>IActorStatSubsystem</c> → `DerivedComposer` (class-system-
/// map.md §2a.0) — a completely different pipeline this plugin has no seam into. An earlier draft
/// mistook this for the aptitude seam because it was found registered and order-tested; it was the
/// wrong pipeline, not an empty one — the lesson recorded is "finding a seam is not the same as
/// reading what flows through it."</para>
/// </summary>
public sealed class ClassStatPlugin : IStatModifierPlugin, IDeclaredInertContributor
{
    public const string Id = "rpg.class";
    public string PluginId => Id;
    public int Order => 100;
    public void Contribute(StatContext ctx, IModifierBagEditor bag) { }
    public string InertReason =>
        "Wrong pipeline for aptitudes (class-system-map.md §2a.0 — those feed IActorStatSubsystem, " +
        "not this). Reserved for EntityBaseline.AttackInterval, unwired until something needs it.";
}

public sealed class AchievementStatPlugin : IStatModifierPlugin, IDeclaredInertContributor
{
    public const string Id = "rpg.achievement";
    public string PluginId => Id;
    public int Order => 200;
    public void Contribute(StatContext ctx, IModifierBagEditor bag) { }
    public string InertReason => "Achievement system unbuilt; seam reserved, not yet a P0 for any program.";
}

public sealed class ItemStatPlugin : IStatModifierPlugin, IDeclaredInertContributor
{
    public const string Id = "rpg.item";
    public string PluginId => Id;
    public int Order => 300;
    public void Contribute(StatContext ctx, IModifierBagEditor bag) { }
    public string InertReason => "Item system unbuilt; seam reserved, not yet a P0 for any program.";
}

public sealed class BuffStatPlugin : IStatModifierPlugin, IDeclaredInertContributor
{
    public const string Id = "rpg.buff";
    public string PluginId => Id;
    public int Order => 400;
    public void Contribute(StatContext ctx, IModifierBagEditor bag) { }
    public string InertReason => "Buff system unbuilt; seam reserved, not yet a P0 for any program.";
}
