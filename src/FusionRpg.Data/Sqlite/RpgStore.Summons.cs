using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using Microsoft.Data.Sqlite;
using FusionRpg.Core.Time;

namespace FusionRpg.Data;

public sealed partial class RpgStore
{
    /// <summary>Test-only: throws mid-pull after the first mint to prove transaction atomicity.</summary>
    internal Action? SummonMidPullTestHook;

    public sealed record SummonOutcome(
        bool Replayed,
        List<CreatureSpecimenDto> Specimens,
        PityState Pity,
        SoulBalanceDto Balance,
        long DiscoverySouls);

    /// <summary>
    /// The summon pull — ONE gate-serialized transaction (spec-creature-summoning.md, hardened):
    /// replay-check → spend → rolls (seeded, pity-aware) → mints → codex + discovery awards →
    /// pity update → log. Atomic or nothing; per-player correlation; replay validates the
    /// stored request and returns the stored results without spending.
    /// </summary>
    public (bool Ok, string Reason, SummonOutcome? Outcome) ExecuteSummon(
        long playerId, string bannerId, int count, string correlationId, ulong rngSeed, string? focusElementId)
    {
        var banner = SummonBannerCatalog.TryGet(bannerId);
        if (banner is null) return (false, "banner.unknown", null);
        if (count is not (1 or 10)) return (false, "count.invalid", null);
        if (string.IsNullOrWhiteSpace(correlationId)) return (false, "correlation.missing", null);
        ElementTypeId? focus = null;
        if (banner.HasElementFocus)
        {
            if (!ElementRoster.TryParse(focusElementId, out var f)) return (false, "focus.unknown", null);
            focus = f;
        }

        var cost = count == 10 ? banner.CostPerTen : banner.CostPerPull * count;
        var corr = correlationId.Trim();

        lock (_gate)
        {
            using var db = OpenUnlocked();
            if (GetPlayerUnlocked(db, playerId) is null) return (false, "player.unknown", null);
            using var tx = db.BeginTransaction();
            var now = ServerClock.UtcNowDateTime.ToString("o");

            // Replay: validate the stored request, return the stored results, spend nothing.
            using (var check = db.CreateCommand())
            {
                check.CommandText = """
                    SELECT banner_id, count, results_json FROM rpg_summon_log
                    WHERE player_id=$p AND correlation_id=$c;
                    """;
                check.Parameters.AddWithValue("$p", playerId);
                check.Parameters.AddWithValue("$c", corr);
                using var r = check.ExecuteReader();
                if (r.Read())
                {
                    if (r.GetString(0) != bannerId || r.GetInt32(1) != count)
                        return (false, "correlation.mismatch", null);
                    var storedIds = JsonSerializer.Deserialize<List<string>>(r.GetString(2)) ?? new List<string>();
                    r.Close();
                    tx.Commit();
                    var stored = storedIds
                        .Select(id => new CreatureSpecimenDto
                        {
                            Actor = ReadUniqueActorUnlocked(db, id)!,
                            Profile = ReadCreatureProfileUnlocked(db, id)!
                        })
                        .Where(s => s.Actor != null && s.Profile != null)
                        .ToList();
                    return (true, "replay", new SummonOutcome(
                        true, stored, ReadPityUnlocked(db, playerId),
                        ReadSoulBalanceUnlocked(db, playerId), 0));
                }
            }

            var balance = ReadSoulBalanceUnlocked(db, playerId);
            if (balance.Balance < cost)
            {
                tx.Rollback();
                return (false, "souls.insufficient", new SummonOutcome(
                    false, new List<CreatureSpecimenDto>(), ReadPityUnlocked(db, playerId), balance, 0));
            }

            if (!AppendSoulLedgerUnlocked(db, playerId, 0, -cost, SoulEarnPolicy.Reasons.Summon, "spend", corr, corr, now))
                throw new InvalidOperationException("summon spend dedupe collision — correlation reused outside the summon log");

            var pity = ReadPityUnlocked(db, playerId);
            var rng = SeededRng.DeriveStream(rngSeed, "gacha");
            var (rolls, newPity) = SummonRoller.Roll(banner, focus, count, pity, rng);

            var specimens = new List<CreatureSpecimenDto>(rolls.Count);
            long discoverySouls = 0;
            var first = true;
            foreach (var roll in rolls)
            {
                var species = CreatureSpeciesCatalog.Get(roll.SpeciesId);
                var specimen = MintCreatureUnlocked(db, playerId, new CreatureMintSpec
                {
                    SpeciesId = species.SpeciesId,
                    Side = species.Side,
                    GameTypeId = species.GameTypeId,
                    Rarity = roll.Rarity.ToId(),
                    Variant = roll.Variant,
                    ElementPrimary = species.ElementPrimary.ToElementId(),
                    ElementSecondary = species.ElementSecondary?.ToElementId(),
                    TraitIds = roll.TraitIds.ToList(),
                    Origin = "summon"
                }, now, out var newlyDiscovered);
                specimens.Add(specimen);

                if (newlyDiscovered)
                {
                    var reward = SoulEarnPolicy.DiscoveryDelta(species.BaseRarity);
                    if (reward > 0 && AppendSoulLedgerUnlocked(
                            db, playerId, 0, reward, SoulEarnPolicy.Reasons.Discovery,
                            "species", species.SpeciesId, "species:" + species.SpeciesId, now))
                        discoverySouls += reward;
                }

                if (first)
                {
                    first = false;
                    SummonMidPullTestHook?.Invoke();
                }
            }

            // Codex milestone faucet (spec-soul-economy): half at ≥50 % discovered, full at ≥90 %
            // (the guardrail lets web-only players claim it once capture-exclusives exist).
            var totalSpecies = CreatureSpeciesCatalog.All.Count;
            if (totalSpecies > 0)
            {
                var discovered = CountDiscoveredUnlocked(db, playerId);
                if (discovered * 2 >= totalSpecies &&
                    AppendSoulLedgerUnlocked(db, playerId, 0, SoulEarnPolicy.CodexHalfMilestone,
                        SoulEarnPolicy.Reasons.Milestone, "codex", "half", "codex:half", now))
                    discoverySouls += SoulEarnPolicy.CodexHalfMilestone;
                if (discovered * 10 >= totalSpecies * 9 &&
                    AppendSoulLedgerUnlocked(db, playerId, 0, SoulEarnPolicy.CodexFullMilestone,
                        SoulEarnPolicy.Reasons.Milestone, "codex", "full", "codex:full", now))
                    discoverySouls += SoulEarnPolicy.CodexFullMilestone;
            }

            WritePityUnlocked(db, playerId, newPity, now);

            using (var log = db.CreateCommand())
            {
                log.CommandText = """
                    INSERT INTO rpg_summon_log(player_id, correlation_id, banner_id, count, focus_element, rng_seed, results_json, t)
                    VALUES($p, $c, $b, $n, $f, $seed, $res, $t);
                    """;
                log.Parameters.AddWithValue("$p", playerId);
                log.Parameters.AddWithValue("$c", corr);
                log.Parameters.AddWithValue("$b", bannerId);
                log.Parameters.AddWithValue("$n", count);
                log.Parameters.AddWithValue("$f", (object?)focus?.ToElementId() ?? DBNull.Value);
                log.Parameters.AddWithValue("$seed", rngSeed.ToString());
                log.Parameters.AddWithValue("$res", JsonSerializer.Serialize(
                    specimens.Select(s => s.Profile.InstanceId).ToList()));
                log.Parameters.AddWithValue("$t", now);
                log.ExecuteNonQuery();
            }

            tx.Commit();
            return (true, "", new SummonOutcome(
                false, specimens, newPity, ReadSoulBalanceUnlocked(db, playerId), discoverySouls));
        }
    }

    public PityState GetSummonPity(long playerId)
    {
        lock (_gate)
        {
            using var db = OpenUnlocked();
            return ReadPityUnlocked(db, playerId);
        }
    }

    long CountDiscoveredUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM rpg_creature_codex WHERE player_id=$p AND state='discovered';";
        cmd.Parameters.AddWithValue("$p", playerId);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    // Columns keep their old names (pulls_since_epic/pulls_since_legendary) after seed-to-concrete
    // T4.1 renamed the C# concepts to PullsSinceHeirloom/PullsSinceSunwoven (ordinals 70/90,
    // ssot-rarity.md §3.3) — the counters' function is unchanged, only which rung they guarantee
    // moved, so a schema migration for a label-only rename was not warranted.
    PityState ReadPityUnlocked(SqliteConnection db, long playerId)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT pulls_since_epic, pulls_since_legendary FROM rpg_summon_pity WHERE player_id=$p;";
        cmd.Parameters.AddWithValue("$p", playerId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new PityState(r.GetInt32(0), r.GetInt32(1)) : PityState.Fresh;
    }

    void WritePityUnlocked(SqliteConnection db, long playerId, PityState pity, string now)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_summon_pity(player_id, pulls_since_epic, pulls_since_legendary, updated_utc)
            VALUES($p, $e, $l, $t)
            ON CONFLICT(player_id) DO UPDATE SET
              pulls_since_epic = excluded.pulls_since_epic,
              pulls_since_legendary = excluded.pulls_since_legendary,
              updated_utc = excluded.updated_utc;
            """;
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.Parameters.AddWithValue("$e", pity.PullsSinceHeirloom);
        cmd.Parameters.AddWithValue("$l", pity.PullsSinceSunwoven);
        cmd.Parameters.AddWithValue("$t", now);
        cmd.ExecuteNonQuery();
    }
}
