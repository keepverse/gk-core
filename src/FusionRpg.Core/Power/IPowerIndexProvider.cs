using FusionRpg.Core.Commanders;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Power;

/// <summary>
/// Produces Θ — the single integer every other system reads instead of a raw level
/// (spec-power-index.md §2.2). Structurally replaces the old <c>IProgressionPowerProvider</c>
/// (deleted, T1.4 — zero <c>SetLevel</c> callers). Its one real consumer,
/// <see cref="FusionRpg.Core.Stats.Derived.Subsystems.RpgProgressionSubsystem"/>, is not rewired onto
/// this interface yet — that semantic migration (Θ into the ProgressionPower channel) is
/// power-plan.md T3.2, deliberately gated behind Checkpoint 2.
/// </summary>
public interface IPowerIndexProvider
{
    int ActorIndex(StatContext ctx);
    int ContentIndex(ContentContext ctx);
    PowerAxisReport Explain(StatContext ctx);

    /// <summary>
    /// `ai-empire-species` EP4.16/EP4.18 (R23) — Theta for an EMPIRE of a save rather than for a
    /// player context: the read Zomboss's commander pool is sized from.
    ///
    /// <para><b>Only a store-backed implementation can answer it.</b> `ServerPowerIndexProvider` composes
    /// it from that empire's own commander level through the SAME ladder `ActorIndex` uses; the
    /// context-keyed providers here answer it consistently with what they already answer (the stub's
    /// identity 0, the fixed Theta, and the hydrated cache's save's-player snapshot — that cache is keyed
    /// by player only, so it has no per-empire answer to give and says so in its own implementation).
    /// Nothing new is composed by them.</para>
    /// </summary>
    int ActorIndexFor(SaveId save, EmpireId empire);
}

/// <summary>The identity: P(0) = C. No tuning dependency — there is nothing to compose.</summary>
public sealed class StubPowerIndexProvider : IPowerIndexProvider
{
    public int ActorIndex(StatContext ctx) => 0;
    public int ContentIndex(ContentContext ctx) => 0;
    public PowerAxisReport Explain(StatContext ctx) => new(0, Array.Empty<PowerAxisContribution>());
    /// <summary>The identity has no per-empire answer either (P(0) = C).</summary>
    public int ActorIndexFor(SaveId save, EmpireId empire) => 0;
}

/// <summary>A constant Θ for hosts that already resolved it (battle setups carry their own level).
/// Mirrors the private fixed provider <c>TerminationGuard</c> already keeps for the same reason.</summary>
public sealed class FixedPowerIndexProvider : IPowerIndexProvider
{
    readonly int _theta;
    public FixedPowerIndexProvider(int theta) => _theta = theta;
    public int ActorIndex(StatContext ctx) => _theta;
    public int ContentIndex(ContentContext ctx) => _theta;
    public PowerAxisReport Explain(StatContext ctx) => new(_theta, Array.Empty<PowerAxisContribution>());
    /// <summary>One constant Theta for everything this provider is asked — the empire included.</summary>
    public int ActorIndexFor(SaveId save, EmpireId empire) => _theta;
}

/// <summary>
/// Reads an injected snapshot; no I/O (spec-power-index.md §2.2). <see cref="Hydrate"/>/<see cref="Clear"/>
/// are the one mechanism Core owns — caching strategy, refresh cadence, and invalidation are each
/// host's own policy (§2.5), built on top of this, not inside it. Mirrors
/// <c>InjectorProgressionPowerProvider</c>'s existing identity-keyed dictionary shape so a host
/// migrating off the old interface recognises the pattern.
/// </summary>
public sealed class HydratedPowerIndexProvider : IPowerIndexProvider
{
    readonly PowerTuning _tuning;
    readonly Dictionary<string, ActorLadderSnapshot> _actors = new(StringComparer.OrdinalIgnoreCase);

    public HydratedPowerIndexProvider(PowerTuning tuning)
    {
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        PowerIndexComposer.ValidateWeights(_tuning.Weights);
    }

    public void Hydrate(StatContext ctx, ActorLadderSnapshot snapshot) => _actors[Key(ctx)] = snapshot;

    public void Clear() => _actors.Clear();

    public int ActorIndex(StatContext ctx) => Explain(ctx).Total;

    /// <summary>The hydrated cache is keyed by PLAYER (<see cref="Key"/>), so it holds no per-empire
    /// snapshot to read: it answers with the save's own player snapshot rather than inventing one, and
    /// the one caller that needs a real per-empire Theta (a commander pool) takes it from the
    /// store-backed provider instead.</summary>
    public int ActorIndexFor(SaveId save, EmpireId empire) =>
        Explain(new StatContext { PlayerId = save.Value }).Total;

    public int ContentIndex(ContentContext ctx) => PowerIndexComposer.ContentExplain(_tuning, ctx).Total;

    public PowerAxisReport Explain(StatContext ctx)
    {
        var snapshot = _actors.TryGetValue(Key(ctx), out var s) ? s : ActorLadderSnapshot.Empty;
        return PowerIndexComposer.ActorExplain(_tuning, snapshot);
    }

    /// <summary>One ladder bucket per player. Every <see cref="ActorLadderSnapshot"/> axis (daveLevel, realmsAdvanced,
    /// pvzRuns) belongs to the player, matching <c>ServerPowerIndexProvider</c>, which reads Θ from the player id alone.
    /// The key used to include side and type id: a player-level hydration then reached only plants whose type id is 0,
    /// and every other lawn plant and every zombie read Θ = 0, although <c>spec-commander-lawn-bridge.md</c> §1 sends
    /// the commander's level to both lawn sides (lawn-tuning-profile, found 2026-09-16).</summary>
    public static string Key(StatContext ctx) => (ctx.PlayerId ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
}
