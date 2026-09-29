using System.Linq;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Rungs;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Effects.Atoms.Power;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    /// <summary>
    /// aura-skill T19 (audit D3 part two): the correctness half of the owner's "both" answer —
    /// equipped actions resolve properly rather than always hitting T3's degrade path. Compiles every
    /// authored `ActionRow` this store holds into a real `ActionCatalog`, the same
    /// validate-then-compile pipeline `ActionCompiler.Compile` already implements (T30) — this method
    /// adds no new compilation logic, only the bulk "load every row, compile it, collect what
    /// succeeds" loop nothing in production has ever run before.
    ///
    /// <para><b>A row that fails to compile is skipped, not fatal.</b> One bad row (an authoring
    /// mistake, a container that got deleted after the action referenced it) must not take down every
    /// OTHER action's ability to resolve — the same "whole-row rejection, never partial" discipline
    /// `AtomRowValidator` already uses, applied at the catalog-assembly level instead of the
    /// single-row level. <paramref name="onRejected"/> is the caller's own visibility into what got
    /// skipped and why (never silently swallowed).</para>
    ///
    /// <para><c>boardAvailable: false</c> throughout — battle is squad-vs-wave, not cell-based; `A10`
    /// (a real board) has not landed for this bind mode, matching `ActionValidator.ValidateAction`'s
    /// own documented default.</para>
    ///
    /// <para><b>A-G1 (spec-tier-access-gate.md §3.2) adds a power-budget stage after compile
    /// succeeds</b> — the rung-keyed sibling of `ContentValidation.Budget`, checked here because this
    /// is the one real, production, bulk-scale path every authored action already passes through
    /// (`WebMatchService`'s battle-resolve calls). An over-budget container is treated the same as a
    /// compile failure: skipped, reported through <paramref name="onRejected"/>, never silently
    /// included and never clamped.</para>
    /// </summary>
    public ActionCatalog BuildActionCatalog(RungTable rungTable, Action<string, ActionRejection>? onRejected = null)
    {
        var compiled = new List<CompiledAction>();

        foreach (var actionId in ListActionIds())
        {
            var row = GetAction(actionId);
            if (row is null) continue; // raced with a delete between ListActionIds and GetAction

            // ST1: a stored action the importer disabled (its brief now refuses) is skipped. This is
            // Enabled's first reader — a skip, never a delete; grants may still reference the id.
            // Reported through the existing compile-failure channel, never a new rejection member
            // (the enum is closed) — PowerBudgetExceeded already means "known row, not usable".
            if (!row.Enabled)
            {
                onRejected?.Invoke(actionId, ActionRejection.Fail(
                    ActionRejectionReason.PowerBudgetExceeded, $"{actionId}: disabled by the corpus importer (its brief no longer composes)"));
                continue;
            }

            var costs = ListCosts(actionId);
            var scopes = ListScopes(actionId);

            ContainerRow? container = null;
            IReadOnlyCollection<string>? containerAtomIds;
            if (string.IsNullOrEmpty(row.ContainerId))
            {
                containerAtomIds = Array.Empty<string>();
            }
            else
            {
                container = GetContainer(row.ContainerId);
                containerAtomIds = container?.Atoms.Select(a => a.AtomId).ToHashSet();
            }

            // base-defense `siege-positions` §4: true, not false. This is the one production call
            // site that compiles the WHOLE action catalog (once, server-wide) — flipping it is what
            // lets an Area-targeted action exist in the catalog at all, for any battle that might run
            // with a board. It does not make Area targeting incorrect for a boardless battle: runtime
            // resolution (`ActionTargetResolver`/`GridDistance.InRange`) is already null-safe by design
            // — "with no board, every range check passes" — so a compiled Area action degrades
            // gracefully rather than behaving wrongly when the battle it runs in has no board. No
            // shipped content authors an Area-targeted action today, so this flip changes zero existing
            // catalog rows' compiled shape — verified by running the full battle golden suite, not
            // assumed from this reasoning alone.
            var (rejection, action) = ActionCompiler.Compile(
                row, costs, scopes, containerAtomIds, boardAvailable: true, rungTable);

            if (action is null)
            {
                onRejected?.Invoke(actionId, rejection);
                continue;
            }

            // battle-tempo action-timing (D2): the realized-power figure the budget check below
            // computes is exactly the input action-timing's wind-up/recovery formula needs
            // (spec-action-timing.md §2.2a) -- fetched once here, reused for both, rather than a
            // second atom read. ST4.1: the fetch + compose themselves are one shared helper, so the
            // calibration report prices identically (`ListActionPricing`).
            var (containerAtoms, realizedPowerMilli) = PriceContainer(container);

            // A-G1 (spec-tier-access-gate.md §3.2): this is the real production caller for the
            // rung-keyed power budget -- every action this store holds passes through here on the
            // way to a battle-usable catalog (WebMatchService's own three call sites). Only the FIXED
            // core (`container.Atoms`) is priced, matching the atom set this method already uses for
            // scope validation above: action content is authored/generated as a fixed set (A-S1's
            // distribution planner bakes `poolRolls` atoms into the container at generation time),
            // never a runtime-weighted draw the way an item's `Pool` is. A container the loaded rung
            // table cannot price (no `powerBudgetMilli` column -- e.g. `action-rungs.v1.json`) is
            // skipped, not failed, the same "skip, do not guess" rule the check itself already uses
            // for a missing ceiling.
            if (container is not null)
            {
                var localContainer = container;
                var budget = ContentValidation.Budget(
                    new[] { localContainer },
                    _ => containerAtoms,
                    _ => row.Rung,
                    rung => rungTable.TryGet(rung, out var rr) ? rr.PowerBudgetMilli : null);

                if (!budget.Ok)
                {
                    // ContentFinding.ToString() carries the container id (its own `Subject`), never
                    // just `Detail` alone -- the whole point of §3.2's "a finding naming the
                    // container id" is that the id survives into whatever reads the rejection.
                    onRejected?.Invoke(actionId, ActionRejection.Fail(
                        ActionRejectionReason.PowerBudgetExceeded, budget.Failures.First().ToString()));
                    continue;
                }
            }

            // battle-tempo action-timing (D2): derived HERE, at catalog build, never by the seeder --
            // BasicAttack's own token wind-up is exempt from this formula (it has no rung/container)
            // and is seeded separately in BasicAttack.cs itself, so this path only ever touches
            // seeded/authored actions with a real rung.
            var cdMulti = rungTable.TryGet(row.Rung, out var rungRowForTiming) ? rungRowForTiming.CdMulti : 1000;
            var derivedEnvelope = ActionTimingDerivation.Derive(
                action.Envelope, row.Category, realizedPowerMilli, BattleRuleset.RoundDurationMs,
                cdMulti, ActionTimingPolicy.Tuning);
            action = action with { Envelope = derivedEnvelope };

            compiled.Add(action);
        }

        return ActionCatalog.Build(compiled);
    }

    /// <summary>
    /// ST4.1 (`spec-budget-calibration-report.md` contract 1): the ONE place a container's realized
    /// power is priced. <see cref="BuildActionCatalog"/> and <see cref="ListActionPricing"/> both call
    /// it, so the calibration report cannot price differently from the check it calibrates (SOLID S).
    /// The atom fetch comes back alongside the figure because the catalog build already needs those
    /// rows for scope validation and the timing derivation — re-reading them would be a second fetch of
    /// the same content.
    /// </summary>
    (List<AtomRow> Atoms, long RealizedPowerMilli) PriceContainer(ContainerRow? container)
    {
        var priced = PriceContainerWithFindings(container);
        return (priced.Atoms, priced.RealizedPowerMilli);
    }

    /// <summary>
    /// ST4.5e (manager ruling on the ST4.5a diagnosis): the same pricing plus the container atoms it
    /// could NOT price, so the calibration report can name them instead of folding them in as a zero.
    /// Everything that reads the number keeps reading <see cref="PriceContainer"/>; only
    /// <see cref="ListActionPricing"/> asks for the findings.
    ///
    /// <para>The atoms list is still the RESOLVED rows — an atom the store cannot fetch is dropped from
    /// the price exactly as before, and that drop is itself unreported (no row, no id). <b>Measured on
    /// the real corpus 2026-09-19: zero such atoms.</b> The `UnpricedAtomIds` here are the atoms that
    /// ARE in the store and that <c>ActorPowerCache.ComposeWithFindings</c> refused — the pooled-channel
    /// case the reading actually hit.</para>
    /// </summary>
    (List<AtomRow> Atoms, long RealizedPowerMilli, IReadOnlyList<string> UnpricedAtomIds)
        PriceContainerWithFindings(ContainerRow? container)
    {
        var atoms = container?.Atoms
            .Select(a => GetAtom(a.AtomId))
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList() ?? new List<AtomRow>();
        var composed = ActorPowerCache.ComposeWithFindings(atoms);
        return (atoms, (long)composed.Power.Total, composed.UnpricedAtomIds);
    }

    /// <summary>
    /// ST4.1 (contract 3): every action that has a container, priced — INCLUDING the ones the rung
    /// budget rejects. <see cref="BuildActionCatalog"/> drops those on purpose, and that gap is exactly
    /// what the calibration measures: the report's question is where the loaded scalar sits relative to
    /// real content, so the content the check would refuse is the most informative part of the reading,
    /// not something to filter out. An action with no container is skipped — it has no atoms to price,
    /// and the budget check does not run for it either.
    /// </summary>
    public IReadOnlyList<PricedAction> ListActionPricing()
    {
        var priced = new List<PricedAction>();
        foreach (var actionId in ListActionIds())
        {
            var row = GetAction(actionId);
            if (row is null || string.IsNullOrEmpty(row.ContainerId)) continue;

            var container = GetContainer(row.ContainerId);
            if (container is null) continue;

            // ST4.5e: the findings ride along, so an action whose container holds an atom the pricing
            // refused is NAMED in the report instead of reading as an action worth nothing.
            var containerPricing = PriceContainerWithFindings(container);
            priced.Add(new PricedAction(
                actionId, row.Rung, containerPricing.RealizedPowerMilli, containerPricing.UnpricedAtomIds));
        }

        return priced;
    }
}
