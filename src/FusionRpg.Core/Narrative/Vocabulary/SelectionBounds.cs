namespace FusionRpg.Core.Narrative.Vocabulary;

/// <summary>
/// npc-story-events `narrative-vocabulary` (`spec-narrative-vocabulary.md` §4, "Structural bounds, not
/// tunables"; map principle 7, `npc-story-events-map.md:94-96`). Every value here is a <c>const</c> rather
/// than a `data/tuning/narrative.v{n}.json` key because changing it would break the design CONTRACT rather
/// than rebalance the design: the tunables SSOT puts a number on the balance surface, and exempts a
/// structural limit only when the exemption says why — each comment below is that "why". None of these is
/// read by a balance pass, and none is a cap on a magnitude (the no-ceiling rule's structural exemption).
/// </summary>
public static class SelectionBounds
{
    /// <summary>
    /// 1 — "one spine beat per pulse" IS the tier rule (`spec-narrative-vocabulary.md` §4). Two beats per
    /// pulse is a different design for the spine, not a tuning of this one.
    /// </summary>
    public const int SpineBeatsPerPulse = 1;

    /// <summary>
    /// 1 — the Hades rule the sanctum hub host is built on (ideal §6.6): one conversation per character per
    /// return. Raising it changes what a return IS, so it is a contract change, not a balance number.
    /// </summary>
    public const int ConversationsPerCharacterPerReturn = 1;

    /// <summary>
    /// 2 — the storylet contract's floor (`narrative-seed-ideal.md:337`). A one-choice storylet is a
    /// cutscene, a different content kind rather than a rebalance.
    /// </summary>
    public const int MinChoices = 2;

    /// <summary>
    /// 4 — the storylet contract's ceiling (`narrative-seed-ideal.md:337`). A fifth choice changes the
    /// card's own layout contract (band width), not a number a pacing pass would move.
    /// </summary>
    public const int MaxChoices = 4;

    /// <summary>
    /// The probability bound `min(CertainMilli, baseMilli + n × stepMilli)` (ideal §6.2 item 3): a bounded
    /// ratio, which the tunables SSOT and the no-cap rule both exempt from the balance surface. `long` per
    /// this spec's numeric table — every `*Milli` is `long`, because `base + n × step` grows with `n` (the
    /// misses since the last fire) and `int` per-mille exceeds its range at Θ=3,213 (AGENTS.md "Numeric
    /// types — overflow is RANGE"). 1000 per-mille IS certainty, so this is the ratio's own end.
    /// </summary>
    public static class FireChance
    {
        public const long CertainMilli = 1000;
    }
}
