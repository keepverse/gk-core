namespace FusionRpg.Core.Effects.Atoms.Power;

/// <summary>
/// What an actor is worth, memoized (spec-power-vector.md, E9).
///
/// <para><b>It lives in E9, not E10, because the spawn recursion needs it to terminate.</b> An atom
/// that spawns a body prices that body's actor power; without memoization a chain of summoners
/// prices forever, and depth-1 truncation alone would still redo the same actor once per spawn atom
/// that names it.</para>
///
/// <para><b>Actor power aggregates channel totals and prices the composition</b> — it is not the sum
/// of per-atom prices (definitions §7, closing D2). Adding <c>+10 atk</c> twice is one
/// <c>+20 atk</c> actor, and pricing them separately would double-count a channel that composes
/// once.</para>
///
/// <para><b>Base stats contribute nothing.</b> That is what makes E10's "marginal on an empty actor
/// ≈ stored power" true, and it keeps actor power a measure of <i>what was granted</i> rather than of
/// the level curve.</para>
/// </summary>
public sealed class ActorPowerCache
{
    readonly Dictionary<string, PowerVector> _memo = new(StringComparer.Ordinal);
    readonly PowerTables _tables;

    public ActorPowerCache(PowerTables? tables = null) => _tables = tables ?? PowerTables.Current;

    /// <summary>How many times a price was actually computed — the memo, observable.</summary>
    public int Computations { get; private set; }

    public int Entries => _memo.Count;

    /// <summary>
    /// The actor's price, memoized on <c>(actor, catalog_revision, binding-set hash)</c>.
    ///
    /// <para>The catalog revision is in the key because the same atoms priced against a different
    /// catalog are a different answer, and the binding set is in it because that is what actually
    /// changed when an actor gains an item.</para>
    /// </summary>
    public PowerVector Of(string actorKey, long catalogRevision, IReadOnlyList<AtomRow> atoms)
    {
        var key = Key(actorKey, catalogRevision, atoms);
        if (_memo.TryGetValue(key, out var cached)) return cached;

        Computations++;
        var value = Compose(atoms, _tables);
        _memo[key] = value;
        return value;
    }

    /// <summary>
    /// Price a set of atoms as one actor: totals per channel first, then one price per channel.
    /// </summary>
    public static PowerVector Compose(IReadOnlyList<AtomRow> atoms, PowerTables? tables = null) =>
        ComposeWithFindings(atoms, tables).Power;

    /// <summary>
    /// ST4.5e (manager ruling on the ST4.5a diagnosis): the same composition, plus the ids of the atoms
    /// it could NOT price. <see cref="Compose"/> delegates here and discards the list, so there is still
    /// exactly ONE implementation and the price is identical by construction.
    ///
    /// <para><b>Why the list matters.</b> All three of this method's skips are silent: an unknown kind,
    /// an unpriceable atom on the <c>Price</c> path, and a channel whose coefficient row is missing.
    /// Each contributed zero with nothing anywhere to see, which is how the ST4.5a reading showed three
    /// pooled-channel actions at 0 and could not say why. A named id is repriceable; a folded-in zero is
    /// not. This is the delivery vehicle for "a finding, never a silent 0".</para>
    /// </summary>
    public static (PowerVector Power, IReadOnlyList<string> UnpricedAtomIds) ComposeWithFindings(
        IReadOnlyList<AtomRow> atoms, PowerTables? tables = null)
    {
        var t = tables ?? PowerTables.Current;
        var total = PowerVector.Zero;
        var unpriced = new List<string>();

        // Channel-writing kinds are aggregated; everything else is priced per atom, because two
        // status applications are two effects rather than one bigger one.
        var byChannel = new Dictionary<(string Kind, string Channel), long>();
        // Which atoms fed each channel, so the second pass can NAME the ones a missing coefficient
        // drops rather than only skipping their magnitude.
        var channelAtoms = new Dictionary<(string Kind, string Channel), List<string>>();

        foreach (var atom in atoms)
        {
            var kind = AtomKindRegistry.Get(atom.KindId);
            if (kind is null) { unpriced.Add(atom.AtomId); continue; }

            var pars = CostFunction.Read(atom.ParamsJson);
            var channel = pars.TryGetValue("channel", out var chEl)
                          && chEl.ValueKind == System.Text.Json.JsonValueKind.String
                ? chEl.GetString() ?? ""
                : null;

            if (channel is null)
            {
                var priced = CostFunction.Price(atom, t);
                if (priced.Ok) total += priced.Power;
                else unpriced.Add(atom.AtomId);
                continue;
            }

            var key = (atom.KindId, channel);
            // Absolute, for the same reason Price is: the sign is direction, not worth. Two atoms
            // writing opposite directions on one channel are two effects, not a cancellation.
            byChannel[key] = byChannel.GetValueOrDefault(key)
                             + Math.Abs(CostFunction.MeanMagnitude(atom, kind, pars));
            if (!channelAtoms.TryGetValue(key, out var ids)) channelAtoms[key] = ids = new List<string>();
            ids.Add(atom.AtomId);
        }

        // E44/D2: remembers each channel's own priced points (not just their sum into `total`) so the
        // interaction pass below can read "what did THIS channel price to" per side of a named pair —
        // the thing neither refuted attempt had, because neither kept a per-channel figure around
        // after folding it into the total.
        var channelPoints = new Dictionary<(string Kind, string Channel), int>();

        foreach (var ((kindId, channel), magnitude) in byChannel)
        {
            var coeff = t.Find(kindId, channel);
            var kind = AtomKindRegistry.Get(kindId);
            if (coeff is null || kind is null)
            {
                unpriced.AddRange(channelAtoms[(kindId, channel)]);
                continue;
            }

            var normalisedMilli =
                PowerMath.DivRound((long)magnitude * PowerMath.One, Math.Max(1, coeff.ReferenceScale));
            var points = PowerMath.MulMilli(normalisedMilli, coeff.CoeffMilli);
            // standing-coeff-tuning (T16): a per-family override (e.g. dodge -> Survivability only)
            // takes the channel's price where the kind's own categories would otherwise trisect it;
            // absent an override, behavior is byte-identical to before this task.
            var category = t.CategoryOverrideFor(kindId, channel) ?? kind.Categories;
            total += PowerVector.FromCategory(category, points);
            channelPoints[(kindId, channel)] = points;
        }

        return (total + Interaction(channelPoints, t), unpriced);
    }

    /// <summary>
    /// E44 (spec-power-sweep.md §4.2, closing definitions.md §13 D2): the genuinely non-linear term
    /// the two prior attempts lacked. For every named pair present on the actor (both sides), adds
    /// <c>coeffMilli × pointsA × pointsB / 1,000,000</c> — proportional to the PRODUCT of the two
    /// sides' own priced points, not their sum, which is the one shape with an actual cross term:
    /// <c>marginal(x, A)</c> now depends on what else is in <c>A</c>, because this term does.
    ///
    /// <para><b>Why /1,000,000 in one division, not two.</b> Both <c>pointsA</c> and <c>pointsB</c>
    /// are already-realized "points" (each is itself a per-mille factor already divided down once by
    /// <see cref="PowerMath.MulMilli"/>), so their raw product carries two implied per-mille scales —
    /// one from <c>coeffMilli</c> being a per-mille dial, one from treating 1000 points as "one
    /// reference unit" the way <c>RungPowerBudgetTests.ReferencePower</c> already does. Collapsing
    /// both into a single <c>DivRound</c> at the end is <i>more</i> exact than two chained
    /// <see cref="PowerMath.CombineMilli"/> calls would be, not less — one widened multiply, one
    /// rounding, matching the "divide by 1000 last, exactly once" discipline's actual intent (avoid
    /// compounding intermediate rounding error), not a literal count of divisions in the file.</para>
    ///
    /// <para><b>Overflow throws.</b> <c>checked</c>, matching <see cref="CostFunction.PricePooled"/>'s
    /// own precedent for new arithmetic in this namespace — a term large enough to overflow a widened
    /// <c>long</c> product must fail loudly, never wrap or silently clamp.</para>
    /// </summary>
    static PowerVector Interaction(
        IReadOnlyDictionary<(string Kind, string Channel), int> channelPoints, PowerTables t)
    {
        var correction = PowerVector.Zero;

        foreach (var row in t.Interactions)
        {
            if (!channelPoints.TryGetValue((row.KindA, row.ChannelA), out var pointsA)) continue;
            if (!channelPoints.TryGetValue((row.KindB, row.ChannelB), out var pointsB)) continue;
            if (pointsA == 0 || pointsB == 0) continue;

            checked
            {
                var product = (long)pointsA * pointsB;                    // widened before multiplying
                var scaled = product * row.CoeffMilli;                    // still widened
                var points = (int)PowerMath.DivRound(scaled, PowerMath.One * PowerMath.One); // divides once
                correction += PowerVector.FromCategory(row.Category, points);
            }
        }

        return correction;
    }

    /// <summary>
    /// A spawned body, priced from the stats it is given.
    ///
    /// <para>Not "base stats are worth nothing" — that rule is about the <i>actor being measured</i>,
    /// not about a body an effect conjures. A 5000 hp summon is worth 5000 hp of survivability to
    /// whoever summoned it, and treating it as base would price the whole spawn at zero (D3).</para>
    /// </summary>
    public static PowerVector PriceBody(long hp, long atk, PowerTables? tables = null)
    {
        var t = tables ?? PowerTables.Current;
        var body = PowerVector.Zero;

        if (hp > 0 && t.Find("stat.modify", "maxHp") is { } hpCoeff)
            body = body.With(PowerCategory.Survivability,
                PowerMath.MulMilli(
                    PowerMath.DivRound((long)hp * PowerMath.One, Math.Max(1, hpCoeff.ReferenceScale)),
                    hpCoeff.CoeffMilli));

        if (atk > 0 && t.Find("stat.modify", "atk") is { } atkCoeff)
            body = body.With(PowerCategory.Offense,
                PowerMath.MulMilli(
                    PowerMath.DivRound((long)atk * PowerMath.One, Math.Max(1, atkCoeff.ReferenceScale)),
                    atkCoeff.CoeffMilli));

        return body;
    }

    static string Key(string actorKey, long revision, IReadOnlyList<AtomRow> atoms)
    {
        // Content-derived and order-independent: the same binding set reached in a different order is
        // the same actor, and a generated id in the key would defeat the memo entirely.
        var ids = atoms.Select(a => a.AtomId).OrderBy(a => a, StringComparer.Ordinal);
        return actorKey + "|" + revision + "|" + string.Join(",", ids);
    }
}
