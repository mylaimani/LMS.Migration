using ClickHouse.Client.ADO;
using LMS.PremiumProfile.Api.Models;

namespace LMS.PremiumProfile.Api.Services;

/// <summary>
/// Fetches all batting profile data from ClickHouse.
///
/// Table/MV reference:
///   lms.player_batting_phase  — MV: batting stats by phase (fast, no season/league for H2H)
///   lms.h2h_stats             — MV: career H2H batter vs bowler (no date/league filter)
///   lms.ball_events           — raw; used when season/league filter is applied for H2H
///   lms.partnerships          — one row per partnership
/// </summary>
public class BattingProfileService : IBattingProfileService
{
    private readonly string _connectionString;

    public BattingProfileService(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ── Public entry point ────────────────────────────────────────────────────
    public async Task<BattingProfileResponse> GetBattingProfileAsync(
        uint playerId, uint? seasonId, uint? leagueId, CancellationToken ct = default)
    {
        using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Sequential — ClickHouseConnection is not thread-safe for concurrent queries
        var phaseStats     = await GetPhaseStatsAsync(conn, playerId, seasonId, leagueId, ct);
        var scoringPattern = await GetScoringPatternAsync(conn, playerId, seasonId, leagueId, ct);
        var (fav, nem)     = await GetH2HBowlersAsync(conn, playerId, seasonId, leagueId, ct);
        var partnerships   = await GetPartnershipsAsync(conn, playerId, seasonId, leagueId, ct);

        return new BattingProfileResponse
        {
            PlayerId         = playerId,
            SeasonId         = seasonId,
            LeagueId         = leagueId,
            PhaseStats       = phaseStats,
            ScoringPattern   = scoringPattern,
            FavouriteBowlers = fav,
            NemesisBowlers   = nem,
            Partnerships     = partnerships,
        };
    }

    // ── 1. Phase stats ────────────────────────────────────────────────────────
    // Uses lms.player_batting_phase (SummingMergeTree MV) — millisecond response.
    private static async Task<List<PhaseStatRow>> GetPhaseStatsAsync(
        ClickHouseConnection conn, uint playerId, uint? seasonId, uint? leagueId, CancellationToken ct)
    {
        var where = BuildPhaseWhere(playerId, seasonId, leagueId);
        var sql = $@"
            SELECT over_phase,
                   sumMerge(runs)        AS runs,
                   sumMerge(legal_balls) AS legal_balls,
                   sumMerge(dismissals)  AS dismissals,
                   sumMerge(boundaries)  AS boundaries,
                   sumMerge(sixes)       AS sixes,
                   sumMerge(dots)        AS dots
            FROM lms.player_batting_phase
            {where}
            GROUP BY over_phase
            ORDER BY multiIf(over_phase='Powerplay',1, over_phase='Middle',2, 3)";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = new List<PhaseStatRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new PhaseStatRow
            {
                Phase      = reader.GetString(0),
                Runs       = Convert.ToUInt64(reader.GetValue(1)),
                Balls      = Convert.ToUInt64(reader.GetValue(2)),
                Dismissals = Convert.ToUInt64(reader.GetValue(3)),
                Boundaries = Convert.ToUInt64(reader.GetValue(4)),
                Sixes      = Convert.ToUInt64(reader.GetValue(5)),
                Dots       = Convert.ToUInt64(reader.GetValue(6)),
            });
        }
        return result;
    }

    // ── 2. Scoring pattern ────────────────────────────────────────────────────
    // Queries ball_events directly (run distribution + over trend).
    private static async Task<ScoringPattern> GetScoringPatternAsync(
        ClickHouseConnection conn, uint playerId, uint? seasonId, uint? leagueId, CancellationToken ct)
    {
        var where = BuildBallEventsWhere(playerId, seasonId, leagueId);

        // 2a. Run distribution (single-pass aggregation)
        var distSql = $@"
            SELECT
                countIf(runs_off_bat = 0 AND is_legal_ball = 1) AS dots,
                countIf(runs_off_bat = 1)                        AS ones,
                countIf(runs_off_bat = 2)                        AS twos,
                countIf(runs_off_bat = 3)                        AS threes,
                countIf(runs_off_bat = 4)                        AS fours,
                countIf(runs_off_bat = 6)                        AS sixes,
                sum(toUInt64(home_runs))                         AS home_runs,
                sum(toUInt64(steal))                             AS steals,
                sum(toUInt64(runs_off_bat))                      AS total_runs,
                sum(toUInt64(is_legal_ball))                     AS total_balls
            FROM lms.ball_events
            {where}";

        RunDistribution dist = new();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = distSql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                dist.Dots       = Convert.ToUInt64(reader.GetValue(0));
                dist.Ones       = Convert.ToUInt64(reader.GetValue(1));
                dist.Twos       = Convert.ToUInt64(reader.GetValue(2));
                dist.Threes     = Convert.ToUInt64(reader.GetValue(3));
                dist.Fours      = Convert.ToUInt64(reader.GetValue(4));
                dist.Sixes      = Convert.ToUInt64(reader.GetValue(5));
                dist.HomeRuns   = Convert.ToUInt64(reader.GetValue(6));
                dist.Steals     = Convert.ToUInt64(reader.GetValue(7));
                dist.TotalRuns  = Convert.ToUInt64(reader.GetValue(8));
                dist.TotalBalls = Convert.ToUInt64(reader.GetValue(9));
            }
        }

        // 2b. Over trend
        var trendSql = $@"
            SELECT over_number,
                   sum(toUInt64(runs_off_bat))  AS runs,
                   sum(toUInt64(is_legal_ball)) AS legal_balls
            FROM lms.ball_events
            {where}
            GROUP BY over_number
            ORDER BY over_number";

        var trend = new List<OverTrendRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = trendSql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                trend.Add(new OverTrendRow
                {
                    Over       = Convert.ToInt32(reader.GetValue(0)) + 1, // 0-indexed → 1-indexed
                    Runs       = Convert.ToUInt64(reader.GetValue(1)),
                    LegalBalls = Convert.ToUInt64(reader.GetValue(2)),
                });
            }
        }

        return new ScoringPattern { Distribution = dist, OverTrend = trend };
    }

    // ── 3. H2H bowlers (favourite + nemesis) ─────────────────────────────────
    // Career (no filter): use fast lms.h2h_stats MV.
    // Filtered: query ball_events (lms.h2h_stats has no season/league columns).
    private static async Task<(List<H2HBowlerRow> Favourite, List<H2HBowlerRow> Nemesis)>
        GetH2HBowlersAsync(
            ClickHouseConnection conn, uint playerId, uint? seasonId, uint? leagueId, CancellationToken ct)
    {
        const int MinBalls = 10;
        List<H2HBowlerRow> rows;

        if (seasonId == null && leagueId == null)
        {
            // Fast path — pre-aggregated MV
            var sql = $@"
                SELECT bowler_id,
                       sum(legal_balls) AS balls,
                       sum(runs)        AS runs,
                       sum(wickets)     AS wickets,
                       sum(sixes)       AS sixes,
                       sum(boundaries)  AS boundaries,
                       sum(dots)        AS dots
                FROM lms.h2h_stats
                WHERE striker_id = {playerId}
                GROUP BY bowler_id
                HAVING balls >= {MinBalls}";

            rows = await ReadH2HRows(conn, sql, ct);
        }
        else
        {
            // Filtered path — ball_events
            var where = BuildBallEventsWhere(playerId, seasonId, leagueId);
            var sql = $@"
                SELECT bowler_id,
                       sum(toUInt64(is_legal_ball))              AS balls,
                       sum(toUInt64(runs_off_bat))               AS runs,
                       sum(toUInt64(is_wicket))                  AS wickets,
                       sum(toUInt64(is_six))                     AS sixes,
                       sum(toUInt64(is_boundary))                AS boundaries,
                       sum(toUInt64(is_dot_ball))                AS dots
                FROM lms.ball_events
                {where}
                GROUP BY bowler_id
                HAVING balls >= {MinBalls}";

            rows = await ReadH2HRows(conn, sql, ct);
        }

        // Favourite = highest strike rate (min balls already filtered)
        var favourite = rows.OrderByDescending(r => r.StrikeRate).Take(10).ToList();

        // Nemesis = most wickets, then lowest strike rate as tiebreak
        var nemesis = rows.OrderByDescending(r => r.Wickets)
                          .ThenBy(r => r.StrikeRate)
                          .Take(10)
                          .ToList();

        return (favourite, nemesis);
    }

    private static async Task<List<H2HBowlerRow>> ReadH2HRows(
        ClickHouseConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<H2HBowlerRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new H2HBowlerRow
            {
                BowlerId   = Convert.ToUInt32(reader.GetValue(0)),
                Balls      = Convert.ToUInt64(reader.GetValue(1)),
                Runs       = Convert.ToUInt64(reader.GetValue(2)),
                Wickets    = Convert.ToUInt64(reader.GetValue(3)),
                Sixes      = Convert.ToUInt64(reader.GetValue(4)),
                Boundaries = Convert.ToUInt64(reader.GetValue(5)),
                Dots       = Convert.ToUInt64(reader.GetValue(6)),
            });
        }
        return rows;
    }

    // ── 4. Partnerships ───────────────────────────────────────────────────────
    private static async Task<List<PartnershipRow>> GetPartnershipsAsync(
        ClickHouseConnection conn, uint playerId, uint? seasonId, uint? leagueId, CancellationToken ct)
    {
        var where = BuildPartnershipWhere(playerId, seasonId, leagueId);
        var sql = $@"
            SELECT
                if(batter1_id = {playerId}, batter2_id, batter1_id) AS partner_id,
                count()                          AS partnership_count,
                sum(toUInt64(runs_together))     AS total_runs,
                sum(toUInt64(balls_together))    AS total_balls,
                sum(toUInt64(fours_together))    AS total_fours,
                sum(toUInt64(sixes_together))    AS total_sixes
            FROM lms.partnerships
            {where}
            GROUP BY partner_id
            HAVING total_balls >= 10
            ORDER BY total_runs DESC
            LIMIT 20";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = new List<PartnershipRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new PartnershipRow
            {
                PartnerId        = Convert.ToUInt32(reader.GetValue(0)),
                PartnershipCount = Convert.ToInt64(reader.GetValue(1)),
                TotalRuns        = Convert.ToUInt64(reader.GetValue(2)),
                TotalBalls       = Convert.ToUInt64(reader.GetValue(3)),
                TotalFours       = Convert.ToUInt64(reader.GetValue(4)),
                TotalSixes       = Convert.ToUInt64(reader.GetValue(5)),
            });
        }
        return result;
    }

    // ── WHERE clause builders ─────────────────────────────────────────────────
    private static string BuildPhaseWhere(uint playerId, uint? seasonId, uint? leagueId)
    {
        var parts = new List<string> { $"striker_id = {playerId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        return "WHERE " + string.Join(" AND ", parts);
    }

    private static string BuildBallEventsWhere(uint playerId, uint? seasonId, uint? leagueId)
    {
        var parts = new List<string> { $"striker_id = {playerId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        return "WHERE " + string.Join(" AND ", parts);
    }

    private static string BuildPartnershipWhere(uint playerId, uint? seasonId, uint? leagueId)
    {
        var parts = new List<string> { $"(batter1_id = {playerId} OR batter2_id = {playerId})" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        return "WHERE " + string.Join(" AND ", parts);
    }
}
