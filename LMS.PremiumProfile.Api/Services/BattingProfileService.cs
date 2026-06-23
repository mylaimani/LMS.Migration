using ClickHouse.Client.ADO;
using LMS.PremiumProfile.Api.Models;

namespace LMS.PremiumProfile.Api.Services;

/// <summary>
/// Fetches all batting profile data from ClickHouse.
///
/// Table/MV reference:
///   lms.player_batting_phase  — MV: batting stats by phase
///                               (no game_date column — falls back to ball_events when date filter applied)
///   lms.h2h_stats             — MV: career H2H batter vs bowler
///                               (no date/league columns — falls back to ball_events when any filter applied)
///   lms.ball_events           — raw; used for scoring pattern and filtered queries
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
        uint playerId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct = default)
    {
        using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Sequential — ClickHouseConnection is not thread-safe for concurrent queries
        var phaseStats     = await GetPhaseStatsAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);
        var scoringPattern = await GetScoringPatternAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);
        var (fav, nem)     = await GetH2HBowlersAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);
        var partnerships   = await GetPartnershipsAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);

        return new BattingProfileResponse
        {
            PlayerId         = playerId,
            SeasonId         = seasonId,
            LeagueId         = leagueId,
            Year             = year,
            FromDate         = fromDate,
            ToDate           = toDate,
            PhaseStats       = phaseStats,
            ScoringPattern   = scoringPattern,
            FavouriteBowlers = fav,
            NemesisBowlers   = nem,
            Partnerships     = partnerships,
        };
    }

    // ── 1. Phase stats ────────────────────────────────────────────────────────
    // Always queries ball_events directly so that:
    //   (a) penalty deliveries are included in the ball count per LMS Rule 8
    //   (b) date filters are supported natively
    //
    // NOTE: player_batting_phase MV only stores legal_balls (pre-penalty-fix).
    //       Re-enable MV fast path once total_balls column is added to the MV
    //       and the migration has been rerun.
    private static async Task<List<PhaseStatRow>> GetPhaseStatsAsync(
        ClickHouseConnection conn, uint playerId,
        uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct)
    {
        string sql;

        {
            // ball_events path — count() gives total balls (legal + penalty) per Rule 8
            var where = BuildBallEventsWhere(playerId, seasonId, leagueId, year, fromDate, toDate);
            sql = $@"
                SELECT over_phase,
                       sum(toUInt64(runs_off_bat))  AS runs,
                       count()                      AS total_balls,
                       sum(toUInt64(is_wicket))     AS dismissals,
                       sum(toUInt64(is_boundary))   AS boundaries,
                       sum(toUInt64(is_six))        AS sixes,
                       sum(toUInt64(is_dot_ball))   AS dots
                FROM lms.ball_events
                {where}
                GROUP BY over_phase
                ORDER BY multiIf(over_phase='Powerplay',1, over_phase='Middle',2, 3)";
        }

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
    private static async Task<ScoringPattern> GetScoringPatternAsync(
        ClickHouseConnection conn, uint playerId,
        uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct)
    {
        var where = BuildBallEventsWhere(playerId, seasonId, leagueId, year, fromDate, toDate);

        // 2a. Run distribution
        // Run distribution notes:
        //
        // DOTS:    Legal deliveries only (a no-ball dot is not a batting dot).
        //
        // ONES–SIXES: Include no-ball deliveries (extras_wide = 0) so runs scored
        //             off no-balls appear in the correct bucket.
        //             Wides excluded (runs_off_bat = 0 on wide in LMS data).
        //             Sixes exclude home_runs > 0 — when a batter hits 6 on the last
        //             ball of the last over and qualifies for a home run, the parser
        //             stores runs_off_bat = 12 (not 6), so the sixes bucket would miss
        //             it and home_runs would double-count it. Use home_run_runs instead.
        //
        // HOME_RUNS:  Count of home run events (home_runs flag set in ball_events).
        // HOME_RUN_RUNS: Total runs_off_bat from home run deliveries (typically 12
        //             per event from the Runs-case parser path). Separate from sixes
        //             so the distribution reconciles: sum(buckets) == total_runs.
        //
        // STEALS:   Count of steal events (runs go to non-striker, counted in total_runs).
        //
        // PENALTY:  penaltyRuns = all value on illegal deliveries (wide extras +
        //           no-ball extras + runs scored off the bat on no-balls).
        //           penaltyBalls = count of illegal deliveries faced.
        var distSql = $@"
            SELECT
                countIf(runs_off_bat = 0 AND is_legal_ball = 1)                           AS dots,
                countIf(runs_off_bat = 1 AND extras_wide = 0)                             AS ones,
                countIf(runs_off_bat = 2 AND extras_wide = 0)                             AS twos,
                countIf(runs_off_bat = 3 AND extras_wide = 0)                             AS threes,
                countIf(runs_off_bat = 4 AND extras_wide = 0)                             AS fours,
                countIf(runs_off_bat = 5 AND extras_wide = 0)                             AS fives,
                -- Exclude home run deliveries (runs_off_bat=12) from sixes bucket.
                -- NOTE: alias must NOT be 'home_runs' -- that shadows the column name and
                -- causes ClickHouse ILLEGAL_AGGREGATION when it resolves home_runs in
                -- other aggregate expressions as the alias instead of the column.
                countIf(runs_off_bat = 6 AND extras_wide = 0 AND home_runs = 0)           AS sixes,
                countIf(home_runs > 0)                                                     AS home_run_count,
                sum(if(home_runs > 0, toUInt64(runs_off_bat), 0))                         AS home_run_runs,
                sum(toUInt64(steal))                                                       AS steals,
                sum(toUInt64(runs_off_bat))                                                AS total_runs,
                count()                                                                    AS total_balls,
                sumIf(toUInt64(runs_off_bat) + toUInt64(extras_wide) + toUInt64(extras_no_ball),
                      is_legal_ball = 0)                                                   AS penalty_runs,
                countIf(is_legal_ball = 0)                                                 AS penalty_balls
            FROM lms.ball_events
            {where}";

        RunDistribution dist = new();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = distSql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                dist.Dots         = Convert.ToUInt64(reader.GetValue(0));
                dist.Ones         = Convert.ToUInt64(reader.GetValue(1));
                dist.Twos         = Convert.ToUInt64(reader.GetValue(2));
                dist.Threes       = Convert.ToUInt64(reader.GetValue(3));
                dist.Fours        = Convert.ToUInt64(reader.GetValue(4));
                dist.Fives        = Convert.ToUInt64(reader.GetValue(5));
                dist.Sixes        = Convert.ToUInt64(reader.GetValue(6));
                dist.HomeRuns     = Convert.ToUInt64(reader.GetValue(7));
                dist.HomeRunRuns  = Convert.ToUInt64(reader.GetValue(8));
                dist.Steals       = Convert.ToUInt64(reader.GetValue(9));
                dist.TotalRuns    = Convert.ToUInt64(reader.GetValue(10));
                dist.TotalBalls   = Convert.ToUInt64(reader.GetValue(11));
                dist.PenaltyRuns  = Convert.ToUInt64(reader.GetValue(12));
                dist.PenaltyBalls = Convert.ToUInt64(reader.GetValue(13));
            }
        }

        // 2b. Over trend — over_number is stored 1-indexed (1–20) in ball_events.
        // Cap to BETWEEN 1 AND 20 to exclude any post-innings penalty deliveries
        // that the scorer recorded beyond over 20.
        var trendWhereWithOverCap = where.Contains("WHERE")
            ? where + " AND over_number BETWEEN 1 AND 20"
            : "WHERE over_number BETWEEN 1 AND 20";
        var trendSql = $@"
            SELECT over_number,
                   sum(toUInt64(runs_off_bat)) AS runs,
                   count()                     AS total_balls  -- all deliveries per Rule 8
            FROM lms.ball_events
            {trendWhereWithOverCap}
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
                    Over       = Convert.ToInt32(reader.GetValue(0)), // stored 1-indexed, no adjustment needed
                    Runs       = Convert.ToUInt64(reader.GetValue(1)),
                    TotalBalls = Convert.ToUInt64(reader.GetValue(2)),
                });
            }
        }

        return new ScoringPattern { Distribution = dist, OverTrend = trend };
    }

    // ── 3. H2H bowlers (favourite + nemesis) ─────────────────────────────────
    // Career only (no filters): use fast lms.h2h_stats MV.
    // Any filter applied: query ball_events directly.
    private static async Task<(List<H2HBowlerRow> Favourite, List<H2HBowlerRow> Nemesis)>
        GetH2HBowlersAsync(
            ClickHouseConnection conn, uint playerId,
            uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
            CancellationToken ct)
    {
        const int MinBalls = 10;
        List<H2HBowlerRow> rows;

        bool anyFilter = seasonId.HasValue || leagueId.HasValue || year.HasValue || fromDate.HasValue || toDate.HasValue;

        if (!anyFilter)
        {
            // Fast path — pre-aggregated MV (career stats)
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
            var where = BuildBallEventsWhere(playerId, seasonId, leagueId, year, fromDate, toDate);
            var sql = $@"
                SELECT bowler_id,
                       sum(toUInt64(is_legal_ball)) AS balls,
                       sum(toUInt64(runs_off_bat))  AS runs,
                       sum(toUInt64(is_wicket))     AS wickets,
                       sum(toUInt64(is_six))        AS sixes,
                       sum(toUInt64(is_boundary))   AS boundaries,
                       sum(toUInt64(is_dot_ball))   AS dots
                FROM lms.ball_events
                {where}
                GROUP BY bowler_id
                HAVING balls >= {MinBalls}";

            rows = await ReadH2HRows(conn, sql, ct);
        }

        var favourite = rows.OrderByDescending(r => r.StrikeRate).Take(10).ToList();
        var nemesis   = rows.OrderByDescending(r => r.Wickets).ThenBy(r => r.StrikeRate).Take(10).ToList();

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
        ClickHouseConnection conn, uint playerId,
        uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct)
    {
        var where = BuildPartnershipWhere(playerId, seasonId, leagueId, year, fromDate, toDate);
        var sql = $@"
            SELECT
                if(batter1_id = {playerId}, batter2_id, batter1_id) AS partner_id,
                count()                       AS partnership_count,
                sum(toUInt64(runs_together))  AS total_runs,
                sum(toUInt64(balls_together)) AS total_balls,
                sum(toUInt64(fours_together)) AS total_fours,
                sum(toUInt64(sixes_together)) AS total_sixes
            FROM lms.partnerships
            {where}
            GROUP BY partner_id
            HAVING total_balls >= 10
            ORDER BY total_runs DESC
            LIMIT 50";

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

    // BuildPhaseWhere is reserved for the player_batting_phase MV fast path.
    // It is not called today because the MV lacks the total_balls column needed to
    // satisfy LMS Rule 8 (penalty ball counting). Re-enable once the MV is updated.
#pragma warning disable IDE0051 // Remove unused private member
    private static string BuildPhaseWhere(uint playerId, uint? seasonId, uint? leagueId)
#pragma warning restore IDE0051
    {
        var parts = new List<string> { $"striker_id = {playerId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        return "WHERE " + string.Join(" AND ", parts);
    }

    private static string BuildBallEventsWhere(
        uint playerId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate)
    {
        var parts = new List<string> { $"striker_id = {playerId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        if (year.HasValue)     parts.Add($"toYear(game_date) = {year}");
        if (fromDate.HasValue) parts.Add($"game_date >= '{fromDate:yyyy-MM-dd}'");
        if (toDate.HasValue)   parts.Add($"game_date <= '{toDate:yyyy-MM-dd}'");
        return "WHERE " + string.Join(" AND ", parts);
    }

    private static string BuildPartnershipWhere(
        uint playerId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate)
    {
        var parts = new List<string> { $"(batter1_id = {playerId} OR batter2_id = {playerId})" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        if (year.HasValue)     parts.Add($"toYear(game_date) = {year}");
        if (fromDate.HasValue) parts.Add($"game_date >= '{fromDate:yyyy-MM-dd}'");
        if (toDate.HasValue)   parts.Add($"game_date <= '{toDate:yyyy-MM-dd}'");
        return "WHERE " + string.Join(" AND ", parts);
    }
}
