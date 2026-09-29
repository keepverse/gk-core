using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Items.Requirements;

/// <summary>The closed outcome vocabulary of a set trial. <see cref="UnmetMember"/> is distinct from
/// <see cref="UnmetEnvelope"/> on purpose: the first says a counted piece is not contributing
/// (module 24's own run status), the second says the wearer does not satisfy the set's frozen
/// conditions — and module 20 renders them differently.</summary>
public enum SetTrialOutcome
{
    Ready = 0,
    UnmetMember = 1,
    UnmetEnvelope = 2,
}

/// <summary>A set trial's verdict plus the FIRST unmet clause, in member order — never a boolean, and
/// never a silent <c>null</c>.</summary>
public readonly record struct SetTrialResult(SetTrialOutcome Outcome, TrialUnmetClause Unmet)
{
    public static readonly SetTrialResult Ready = new(SetTrialOutcome.Ready, TrialUnmetClause.None);

    public static readonly SetTrialResult UnmetMember = new(SetTrialOutcome.UnmetMember, TrialUnmetClause.None);

    public static SetTrialResult UnmetEnvelope(TrialUnmetClause clause) =>
        new(SetTrialOutcome.UnmetEnvelope, clause);
}

/// <summary>
/// A pure witness: an allocation and a resource snapshot DERIVED from the envelope alone, with no
/// player state in it. The spec's rule 7 asks the catalog to prove that such a witness exists before a
/// set seed is accepted — "a matching actor identity plus a pure witness allocation and resource
/// snapshot derived from the envelope satisfies every member profile and the full-set activation
/// evaluation within one deployment".
/// </summary>
public readonly record struct SetTrialWitness(int Level, AptitudeAllocation Allocation, IReadOnlyDictionary<string, long> Resources);

/// <summary>
/// item/spec-set-requirement-reconciliation.md §"Full-set activation" and §Validation rule 7. Pure:
/// it reads the frozen envelope, a wearer's level/allocation, and (for the witness) nothing else. It
/// never mutates an allocation, a resource, a profile, an assignment or a tier binding, and it never
/// turns a trial into an unequip.
/// </summary>
public static class SetTrialEvaluator
{
    /// <summary>
    /// <c>set tier active := tier is threshold-wanted AND every counted required member is active AND
    /// envelope trial and upkeep conditions pass</c>. The threshold-wanted half stays
    /// <see cref="Thresholds.SetEvaluator"/>'s own role-deduped count — this predicate is added beside
    /// it, never in place of it, so partial-set progress is untouched.
    ///
    /// <para>The envelope trial is ONE pass over the members against the same wearer, which is what
    /// "the envelope is evaluated once" means: the members' own trial clauses ARE the envelope's, since
    /// the envelope has no profile of its own.</para>
    /// </summary>
    public static SetTrialResult Evaluate(
        SetRequirementEnvelope envelope, bool allCountedMembersActive, int level, AptitudeAllocation allocation)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));
        if (!allCountedMembersActive) return SetTrialResult.UnmetMember;
        foreach (var member in envelope.Members)
        {
            var trial = RequirementTrialEvaluator.Evaluate(member.Profile, level, allocation);
            if (!trial.Ready) return SetTrialResult.UnmetEnvelope(trial.Unmet);
        }
        return SetTrialResult.Ready;
    }

    /// <summary>
    /// The witness the envelope can prove for itself, or <c>null</c> when it can prove none — a
    /// build clause with no favored aptitude is unwitnessable, and saying so is the answer, not a
    /// fabricated pass.
    ///
    /// <para>The allocation puts every member's fixed floor on the ONE favored aptitude, so the ratio
    /// clauses pass by construction (a share of 1000‰ is at least any legal per-mille floor), and the
    /// resource snapshot carries the largest <c>cost + max(reserve, hpFloor)</c> any single member
    /// needs — the non-lethal HP floor is 1, the same structural floor
    /// <c>CostLedger.TryPayProfileMaintenance</c> applies.</para>
    /// </summary>
    public static SetTrialWitness? TryWitness(SetRequirementEnvelope envelope)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        long points = 1;
        var level = 1;
        foreach (var member in envelope.Members)
        {
            if (member.Profile.MinimumLevel is { } minLevel && minLevel > level) level = minLevel;
            if (member.Profile.BuildTrial is { MinimumPoints: { } floor } && floor > points) points = floor;
            if (member.Profile.BuildTrial is not null && envelope.FavoredAptitudeId is null) return null;
        }

        var allocation = envelope.FavoredAptitudeId is { } aptitude
            ? AptitudeAllocation.Single(AllocationScope.UniqueCreature, aptitude, points)
            : AptitudeAllocation.Empty;

        var resources = new Dictionary<string, long>(StringComparer.Ordinal);
        if (envelope.UpkeepResourceId is { } resourceId)
        {
            long need = 0;
            foreach (var member in envelope.Members)
                if (member.Profile.Upkeep is { } upkeep)
                {
                    var floor = Math.Max(upkeep.Reserve, 1);
                    checked
                    {
                        var required = upkeep.Cost + floor;
                        if (required > need) need = required;
                    }
                }
            resources[resourceId] = need;
        }

        return new SetTrialWitness(level, allocation, resources);
    }

    /// <summary>
    /// Prove the witness actually satisfies every member profile — the trial half through
    /// <see cref="RequirementTrialEvaluator"/>, the upkeep half through the same
    /// <c>current - cost &gt;= max(reserve, hpFloor)</c> rule
    /// <c>CostLedger.TryPayProfileMaintenance</c> enforces — and then the full-set activation itself,
    /// with every counted member active. Returns false rather than throwing: an unwitnessed envelope is
    /// a validation finding, not a crash.
    /// </summary>
    public static bool WitnessSatisfies(SetRequirementEnvelope envelope, in SetTrialWitness witness)
    {
        if (envelope is null) throw new ArgumentNullException(nameof(envelope));

        foreach (var member in envelope.Members)
        {
            if (!RequirementTrialEvaluator.Evaluate(member.Profile, witness.Level, witness.Allocation).Ready)
                return false;

            if (member.Profile.Upkeep is { } upkeep)
            {
                if (!witness.Resources.TryGetValue(upkeep.ResourceId, out var current)) return false;
                var floor = Math.Max(upkeep.Reserve, 1);
                if (current - upkeep.Cost < floor) return false;
            }
        }

        return Evaluate(envelope, allCountedMembersActive: true, witness.Level, witness.Allocation).Outcome
            == SetTrialOutcome.Ready;
    }
}
