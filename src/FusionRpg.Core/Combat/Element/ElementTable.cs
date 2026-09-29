using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Combat.Element;

/// <summary>One element in the roster (E18).</summary>
/// <param name="Ordinal">
/// <b>Explicit and append-only, never inferred from row order.</b> The ordinal drives the generated
/// channel set — <c>combat.power.fire</c> exists because fire is in the roster — so reordering the
/// roster silently renames every generated channel. An existing element's ordinal may never change
/// and a retired one's is never reused.
/// </param>
public sealed record ElementRow(string ElementId, string DisplayName, int Ordinal, bool Enabled = true);

/// <summary>
/// One matchup cell: what <paramref name="Attacker"/> does to <paramref name="Defender"/>.
/// </summary>
/// <param name="Unit">+1 strong, −1 weak, 0 neutral. K is applied by the caller, once.</param>
public sealed record ElementMatrixRow(string Attacker, string Defender, int Unit);

/// <summary>
/// The element roster and both matchup matrices, as rows.
///
/// <para><b>Why elements are data and channel families are not.</b> The program's rule is that a
/// thing may be data if adding a row changes behaviour without new code. A seventh element
/// regenerates its 12 channels and every consumer picks them up, because
/// <c>CombatDerivedReader</c> reads channels by pattern rather than by name. A thirteenth channel
/// <i>family</i> would have no reader and be dead on arrival, so families stay code.</para>
///
/// <para><b>Two matrices, kept separate — but not for the reason the spec gave.</b> The spec said
/// they "genuinely differ", citing a light/dark asymmetry. Compared exhaustively, the shipped tables
/// are <b>identical across all 36 pairs</b>: light and dark are mutually strong in both. They stay
/// separate because the shield spec makes them independently editable and calls divergence an
/// Ask-first balance decision — and because the combat side distinguishes <c>Same</c> from
/// <c>Neutral</c> while the shield side collapses both to 0.</para>
/// </summary>
public sealed class ElementTable
{
    readonly Dictionary<string, ElementRow> _byId;
    readonly Dictionary<(string, string), int> _combat;
    readonly Dictionary<(string, string), int> _shield;

    public ElementTable(
        IReadOnlyList<ElementRow> elements,
        IReadOnlyList<ElementMatrixRow> combat,
        IReadOnlyList<ElementMatrixRow> shield)
    {
        Elements = elements.OrderBy(e => e.Ordinal).ToList();
        _byId = Elements.ToDictionary(e => e.ElementId, StringComparer.Ordinal);
        _combat = Index(combat);
        _shield = Index(shield);
        CombatRows = combat;
        ShieldRows = shield;
    }

    public IReadOnlyList<ElementRow> Elements { get; }
    public IReadOnlyList<ElementMatrixRow> CombatRows { get; }
    public IReadOnlyList<ElementMatrixRow> ShieldRows { get; }

    static readonly AsyncLocal<ElementTable?> Scoped = new();
    static ElementTable _global = Shipped();

    /// <summary>
    /// What this context runs on. Defaults to <see cref="Shipped"/>, so a host that loads nothing
    /// behaves exactly as it always has; a host with a database replaces it once, at startup.
    /// </summary>
    public static ElementTable Current => Scoped.Value ?? _global;

    /// <summary>Process-wide. What a host calls once, after loading the roster from its store.</summary>
    public static void Use(ElementTable table) =>
        _global = table ?? throw new ArgumentNullException(nameof(table));

    public static void ResetToShipped() => _global = Shipped();

    /// <summary>
    /// Swap the roster for <b>this async context only</b>, and put it back on dispose.
    ///
    /// <para>The roster is process-global, and the generated channel set is read from it — so a test
    /// that swapped the global would be visible to every test running beside it. Test runners execute
    /// classes in parallel, which makes that a rare failure in an unrelated file: the worst kind.</para>
    /// </summary>
    public static IDisposable UseScoped(ElementTable table)
    {
        if (table is null) throw new ArgumentNullException(nameof(table));
        var previous = Scoped.Value;
        Scoped.Value = table;
        return new Restore(previous);
    }

    sealed class Restore : IDisposable
    {
        readonly ElementTable? _previous;
        public Restore(ElementTable? previous) => _previous = previous;
        public void Dispose() => Scoped.Value = _previous;
    }

    public bool Knows(string elementId) => _byId.ContainsKey(elementId);

    public ElementRow? Find(string elementId) => _byId.TryGetValue(elementId, out var r) ? r : null;

    /// <summary>Unit relation for the combat ring. Unknown pair reads neutral, as the switch did.</summary>
    public int CombatUnit(string attacker, string defender) =>
        _combat.TryGetValue((attacker, defender), out var u) ? u : 0;

    public int ShieldUnit(string attacker, string defender) =>
        _shield.TryGetValue((attacker, defender), out var u) ? u : 0;

    static Dictionary<(string, string), int> Index(IReadOnlyList<ElementMatrixRow> rows)
    {
        var d = new Dictionary<(string, string), int>();
        foreach (var r in rows) d[(r.Attacker, r.Defender)] = r.Unit;
        return d;
    }

    // ---- the shipped content --------------------------------------------------------------------

    /// <summary>
    /// The six elements and both 36-cell matrices exactly as they shipped in code. This is the seed
    /// an import writes and the fallback a host without a database runs on — the roster is unchanged
    /// by this module, so no generated channel and no golden moves.
    /// </summary>
    public static ElementTable Shipped()
    {
        var elements = new[]
        {
            new ElementRow("fire", "Fire", 0),
            new ElementRow("ice", "Ice", 1),
            new ElementRow("air", "Air", 2),
            new ElementRow("earth", "Earth", 3),
            new ElementRow("light", "Light", 4),
            new ElementRow("dark", "Dark", 5),
        };

        // Ring: fire → ice → earth → air → fire. Light ⇄ dark mutually strong, neutral vs the ring.
        // Every unlisted pair is neutral; the pair-complete test is what proves none falls through.
        var ring = new[]
        {
            new ElementMatrixRow("light", "dark", 1),
            new ElementMatrixRow("dark", "light", 1),

            new ElementMatrixRow("fire", "ice", 1),
            new ElementMatrixRow("fire", "air", -1),

            new ElementMatrixRow("ice", "earth", 1),
            new ElementMatrixRow("ice", "fire", -1),

            new ElementMatrixRow("earth", "air", 1),
            new ElementMatrixRow("earth", "ice", -1),

            new ElementMatrixRow("air", "fire", 1),
            new ElementMatrixRow("air", "earth", -1),
        };

        // Seeded identical to the ring, and separately editable. Diverging is the shield stream's
        // call (shield-system-spec.md §8), which is why the rows are duplicated rather than shared.
        return new ElementTable(elements, ring, ring.Select(r => r with { }).ToArray());
    }

    /// <summary>
    /// The roster as the enum spells it — the bridge while <see cref="ElementTypeId"/> is code.
    ///
    /// <para><b>solid-remediation T5.2 (X2): this is now an alias, not a second declaration.</b> It used
    /// to carry its own copy of the six-way switch ending in <c>_ =&gt; ""</c>, while
    /// <see cref="Stats.Derived.ActorElementTypes.ToElementId"/> carried the same mapping ending in a
    /// throw. One vocabulary, two owners, and — worse — two different failure modes.</para>
    ///
    /// <para><b>The empty string was the actual defect.</b> This method feeds
    /// <see cref="ElementRingMatrix"/> and <see cref="Shield.ShieldElementMatrix"/>, which look the id up
    /// in the matchup table. An id of <c>""</c> matches no row, so a missing element resolved to a
    /// NEUTRAL matchup and no error — a plausible-looking number with nothing reporting it. A crash is
    /// strictly better here, because a silent wrong answer in a damage multiplier is invisible until
    /// someone reconciles totals by hand.</para>
    ///
    /// <para>Routed at the other declaration rather than deleted and re-derived: the mapping is content,
    /// and re-typing six strings is how content quietly changes.</para>
    /// </summary>
    public static string IdOf(ElementTypeId id) => id.ToElementId();
}
