using ClickHouse.Client.ADO;
using LMS.PremiumProfile.Api.Models;

namespace LMS.PremiumProfile.Api.Services;

/// <summary>
/// Fetches all bowling profile data from ClickHouse.
///
/// Table/MV reference:
///   lms.player_bowling_phase  — MV: bowling stats by phase (SummingMergeTree, use sum() not sumMerge())
///                               (no game_date column — falls back to ball_events when date filter applied)
///   lms.h2h_stats             — MV: career H2H bowler vs batter
///                               (no date/league columns — falls back to ball_events when any filter applied)
///   lms.ball_events           — raw; used for bowling pattern and filtered queries
///
/// IMPORTANT: ClickHouseConnection is NOT thread-safe — all queries must run sequentially.
/// </summary>
public class BowlingProfileService : IBowlingProfileService
{
    private readonly string _connectionString;

    public BowlingProfileService(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ── Public entry point ────────────────────────────────────────────────────
    public async Task<BowlingProfileResponse> GetBowlingProfileAsync(
        uint playerId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct = default)
    {
        using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Sequential — ClickHouseConnection is not thread-safe for concurrent queries
        var phaseStats      = await GetPhaseStatsAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);
        var bowlingPattern  = await GetBowlingPatternAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);
        var (fav, nem)      = await GetH2HBattersAsync(conn, playerId, seasonId, leagueId, year, fromDate, toDate, ct);

        return new BowlingProfileResponse
        {
            PlayerId         = playerId,
            SeasonId         = seasonId,
            LeagueId         = leagueId,
            Year             = year,
            FromDate         = fromDate,
            ToDate           = toDate,
            PhaseStats       = phaseStats,
            BowlingPattern   = bowlingPattern,
            FavouriteBatters = fav,
            NemesisBatters   = nem,
        };
    }

    // ── 1. Phase stats ────────────────────────────────────────────────────────
    // Uses lms.player_bowling_phase MV when no date filter (fast).
    // Falls back to ball_events when year/date filter is applied (MV has no game_date).
    private static async Task<List<BowlingPhaseStatRow>> GetPhaseStatsAsync(
        ClickHouseConnection conn, uint playerId,
        uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct)
    {
        string sql;
        bool hasDateFilter = year.HasValue || fromDate.HasValue || toDate.HasValue;

        if (!hasDateFilter)
        {
            // Fast path — pre-aggregated MV (SummingMergeTree, use sum() not sumMerge())
            var where = BuildPhaseWhere(playerId, seasonId, leagueId);
            sql = $@"
                SELECT over_phase,
                       sum(runs_conceded) AS runs_conceded,
                       sum(legal_balls)   AS legal_balls,
                       sum(wickets)       AS wickets,
                       sum(dots)          AS dots,
                       sum(sixes)         AS sixes,
                       sum(fours)         AS fours,
                       sum(threes)        AS threes,
                       sum(twos)          AS twos,
                       sum(ones)          AS ones,
                       sum(wides)         AS wides,
                       sum(no_balls)      AS no_balls
                FROM lms.player_bowling_phase
                {where}
                GROUP BY over_phase
                ORDER BY multiIf(over_phase='Powerplay',1, over_phase='Middle',2, 3)";
        }
        else
        {
            // Date-filtered path — ball_events
            var where = BuildBallEventsWhere(playerId, seasonId, leagueId, year, fromDate, toDate);
            sql = $@"
                SELECT over_phase,
                       sum(toUInt64(runs_off_bat + extras_wide + extras_no_ball)) AS runs_conceded,
                       sum(toUInt64(is_legal_ball))                               AS legal_balls,
                       sum(toUInt64(is_wicket))                                   AS wickets,
                       sum(toUInt64(is_dot_ball))                                 AS dots,
                       countIf(runs_off_bat = 6 AND is_legal_ball = 1)            AS sixes,
                       countIf(runs_off_bat = 4 AND is_legal_ball = 1)            AS fours,
                       countIf(runs_off_bat = 3 AND is_legal_ball = 1)            AS threes,
                       countIf(runs_off_bat = 2 AND is_legal_ball = 1)            AS twos,
                       countIf(runs_off_bat = 1 AND is_legal_ball = 1)            AS ones,
                       countIf(extras_wide > 0)                                   AS wides,
                       countIf(extras_no_ball > 0)                                AS no_balls
                FROM lms.ball_events
                {where}
                GROUP BY over_phase
                ORDER BY multiIf(over_phase='Powerplay',1, over_phase='Middle',2, 3)";
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = new List<BowlingPhaseStatRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new BowlingPhaseStatRow
            {
                Phase        = reader.GetString(0),
                RunsConceded = Convert.ToUInt64(reader.GetValue(1)),
                Balls        = Convert.ToUInt64(reader.GetValue(2)),
                Wickets      = Convert.ToUInt64(reader.GetValue(3)),
                Dots         = Convert.ToUInt64(reader.GetValue(4)),
                Sixes        = Convert.ToUInt64(reader.GetValue(5)),
                Fours        = Convert.ToUInt64(reader.GetValue(6)),
                Threes       = Convert.ToUInt64(reader.GetValue(7)),
                Twos         = Convert.ToUInt64(reader.GetValue(8)),
                Ones         = Convert.ToUInt64(reader.GetValue(9)),
                Wides        = Convert.ToUInt64(reader.GetValue(10)),
                NoBalls      = Convert.ToUInt64(reader.GetValue(11)),
            });
        }
        return result;
    }

    // ── 2. Bowling pattern (totals + over-by-over trend) ──────────────────────
    private static async Task<BowlingPattern> GetBowlingPatternAsync(
        ClickHouseConnection conn, uint playerId,
        uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct)
    {
        var where = BuildBallEventsWhere(playerId, seasonId, leagueId, year, fromDate, toDate);

        // 2a. Career/filtered totals
        var totalsSql = $@"
            SELECT
                sum(toUInt64(runs_off_bat + extras_wide + extras_no_ball)) AS runs_conceded,
                sum(toUInt64(is_legal_ball))                               AS legal_balls,
                sum(toUInt64(is_wicket))                                   AS wickets,
                sum(toUInt64(is_dot_ball))                                 AS dots,
                countIf(runs_off_bat = 6 AND is_legal_ball = 1)            AS sixes,
                countIf(runs_off_bat = 4 AND is_legal_ball = 1)            AS fours,
                countIf(runs_off_bat = 3 AND is_legal_ball = 1)            AS threes,
                countIf(runs_off_bat = 2 AND is_legal_ball = 1)            AS twos,
                countIf(runs_off_bat = 1 AND is_legal_ball = 1)            AS ones,
                countIf(extras_no_ball > 0)                                AS no_balls,
                countIf(extras_wide > 0)                                   AS wides
            FROM lms.ball_events
            {where}";

        BowlingFigures totals = new();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = totalsSql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                totals.RunsConceded = Convert.ToUInt64(reader.GetValue(0));
                totals.Balls        = Convert.ToUInt64(reader.GetValue(1));
                totals.Wickets      = Convert.ToUInt64(reader.GetValue(2));
                totals.Dots         = Convert.ToUInt64(reader.GetValue(3));
                totals.Sixes        = Convert.ToUInt64(reader.GetValue(4));
                totals.Fours        = Convert.ToUInt64(reader.GetValue(5));
                totals.Threes       = Convert.ToUInt64(reader.GetValue(6));
                totals.Twos         = Convert.ToUInt64(reader.GetValue(7));
                totals.Ones         = Convert.ToUInt64(reader.GetValue(8));
                totals.NoBalls      = Convert.ToUInt64(reader.GetValue(9));
                totals.Wides        = Convert.ToUInt64(reader.GetValue(10));
            }
        }

        // 2b. Over trend — over_number is stored 1-indexed (1–20) in ball_events.
        // Cap to BETWEEN 1 AND 20 to exclude any post-innings deliveries
        // the scorer may have recorded beyond over 20.
        var trendWhereWithOverCap = where.Contains("WHERE")
            ? where + " AND over_number BETWEEN 1 AND 20"
            : "WHERE over_number BETWEEN 1 AND 20";

        var trendSql = $@"
            SELECT over_number,
                   sum(toUInt64(runs_off_bat + extras_wide + extras_no_ball)) AS runs_conceded,
                   sum(toUInt64(is_legal_ball))                               AS legal_balls,
                   sum(toUInt64(is_wicket))                                   AS wickets
            FROM lms.ball_events
            {trendWhereWithOverCap}
            GROUP BY over_number
            ORDER BY over_number";

        var trend = new List<BowlingOverTrendRow>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = trendSql;
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                trend.Add(new BowlingOverTrendRow
                {
                    Over         = Convert.ToInt32(reader.GetValue(0)), // stored 1-indexed, no adjustment needed
                    RunsConceded = Convert.ToUInt64(reader.GetValue(1)),
                    LegalBalls   = Convert.ToUInt64(reader.GetValue(2)),
                    Wickets      = Convert.ToUInt64(reader.GetValue(3)),
                });
            }
        }

        return new BowlingPattern { Totals = totals, OverTrend = trend };
    }

    // ── 3. H2H batters (favourite = concedes most runs, nemesis = dismisses most) ──
    // Career only (no filters): use fast lms.h2h_stats MV.
    // Any filter applied: query ball_events directly.
    private static async Task<(List<H2HBatterRow> Favourite, List<H2HBatterRow> Nemesis)>
        GetH2HBattersAsync(
            ClickHouseConnection conn, uint playerId,
            uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
            CancellationToken ct)
    {
        const int MinBalls = 10;
        List<H2HBatterRow> rows;

        bool anyFilter = seasonId.HasValue || leagueId.HasValue || year.HasValue || fromDate.HasValue || toDate.HasValue;

        if (!anyFilter)
        {
            // Fast path — pre-aggregated MV (career, bowler perspective)
            var sql = $@"
                SELECT striker_id,
                       sum(legal_balls) AS balls,
                       sum(runs)        AS runs,
                       sum(wickets)     AS wickets,
                       sum(sixes)       AS sixes,
                       sum(boundaries)  AS boundaries,
                       sum(dots)        AS dots
                FROM lms.h2h_stats
                WHERE bowler_id = {playerId}
                GROUP BY striker_id
                HAVING balls >= {MinBalls}";

            rows = await ReadH2HBatterRows(conn, sql, ct);
        }
        else
        {
            // Filtered path — ball_events
            var where = BuildBallEventsWhere(playerId, seasonId, leagueId, year, fromDate, toDate);
            var sql = $@"
                SELECT striker_id,
                       sum(toUInt64(is_legal_ball)) AS balls,
                       sum(toUInt64(runs_off_bat))  AS runs,
                       sum(toUInt64(is_wicket))     AS wickets,
                       sum(toUInt64(is_six))        AS sixes,
                       sum(toUInt64(is_boundary))   AS boundaries,
                       sum(toUInt64(is_dot_ball))   AS dots
                FROM lms.ball_events
                {where}
                GROUP BY striker_id
                HAVING balls >= {MinBalls}";

            rows = await ReadH2HBatterRows(conn, sql, ct);
        }

        // Favourite = batters where bowler concedes most runs (high economy = struggle)
        var favourite = rows.OrderByDescending(r => r.Economy).Take(10).ToList();
        // Nemesis = batters the bowler dismisses most
        var nemesis   = rows.OrderByDescending(r => r.Wickets).ThenBy(r => r.Economy).Take(10).ToList();

        return (favourite, nemesis);
    }

    private static async Task<List<H2HBatterRow>> ReadH2HBatterRows(
        ClickHouseConnection conn, string sql, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var rows = new List<H2HBatterRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new H2HBatterRow
            {
                StrikerId  = Convert.ToUInt32(reader.GetValue(0)),
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

    // ── WHERE clause builders ─────────────────────────────────────────────────
    // Note: for bowling, filter is on bowler_id (not striker_id)
    private static string BuildPhaseWhere(uint playerId, uint? seasonId, uint? leagueId)
    {
        var parts = new List<string> { $"bowler_id = {playerId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        return "WHERE " + string.Join(" AND ", parts);
    }

    private static string BuildBallEventsWhere(
        uint playerId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate)
    {
        var parts = new List<string> { $"bowler_id = {playerId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        if (year.HasValue)     parts.Add($"toYear(game_date) = {year}");
        if (fromDate.HasValue) parts.Add($"game_date >= '{fromDate:yyyy-MM-dd}'");
        if (toDate.HasValue)   parts.Add($"game_date <= '{toDate:yyyy-MM-dd}'");
        return "WHERE " + string.Join(" AND ", parts);
    }
}
