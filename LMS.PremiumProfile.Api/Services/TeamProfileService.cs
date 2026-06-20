using ClickHouse.Client.ADO;
using LMS.PremiumProfile.Api.Models;

namespace LMS.PremiumProfile.Api.Services;

/// <summary>
/// Fetches team profile data from ClickHouse for GET /api/team/{teamId}.
///
/// Table reference:
///   lms.ball_events       — raw ball-by-ball data; primary source for all phase queries
///   lms.league_avg        — SummingMergeTree MV pre-aggregated by league/season/phase;
///                           used for benchmark baseline when no date filter is active
///   lms.partnerships      — batting partnerships; filtered by batting_team_id
///
/// IMPORTANT: ClickHouseConnection is NOT thread-safe — all queries run sequentially.
///
/// LMS NOTE: Run rate = runs / legal_balls * 5  (5-ball overs, not 6).
/// </summary>
public class TeamProfileService : ITeamProfileService
{
    private readonly string _connectionString;

    public TeamProfileService(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<TeamProfileResponse> GetTeamProfileAsync(
        uint teamId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct = default)
    {
        using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Sequential — ClickHouseConnection is not thread-safe for concurrent queries
        var battingPhase    = await GetBattingPhaseAsync(conn, teamId, seasonId, leagueId, year, fromDate, toDate, ct);
        var bowlingPhase    = await GetBowlingPhaseAsync(conn, teamId, seasonId, leagueId, year, fromDate, toDate, ct);
        var benchmark       = await GetBenchmarkAsync(conn, teamId, seasonId, leagueId, year, fromDate, toDate, battingPhase, bowlingPhase, ct);
        var topPartnerships = await GetTopPartnershipsAsync(conn, teamId, seasonId, leagueId, year, fromDate, toDate, ct);
        var clubGreats      = await GetClubGreatsAsync(conn, teamId, ct);

        return new TeamProfileResponse
        {
            TeamId          = teamId,
            SeasonId        = seasonId,
            LeagueId        = leagueId,
            Year            = year,
            FromDate        = fromDate,
            ToDate          = toDate,
            BattingPhase    = battingPhase,
            BowlingPhase    = bowlingPhase,
            Benchmark       = benchmark,
            TopPartnerships = topPartnerships,
            ClubGreats      = clubGreats,
        };
    }

    // ── 1. Batting phase (team as batting side) ───────────────────────────────
    private static async Task<List<TeamBattingPhaseRow>> GetBattingPhaseAsync(
        ClickHouseConnection conn, uint teamId,
        uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct)
    {
        var where = BuildBattingWhere(teamId, seasonId, leagueId, year, fromDate, toDate);
        var sql = $@"
            SELECT over_phase,
                   sum(toUInt64(runs_off_bat))  AS runs,
                   sum(toUInt64(is_legal_ball)) AS balls,
                   sum(toUInt64(is_wicket))     AS wickets,
                   sum(toUInt64(is_boundary))   AS boundaries,
                   sum(toUInt64(is_six))        AS sixes,
                   sum(toUInt64(is_dot_ball))   AS dots
            FROM lms.ball_events
            {where}
            GROUP BY over_phase
            ORDER BY multiIf(over_phase='Powerplay',1, over_phase='Middle',2, 3)";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = new List<TeamBattingPhaseRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TeamBattingPhaseRow
            {
                Phase      = reader.GetString(0),
                Runs       = Convert.ToUInt64(reader.GetValue(1)),
                Balls      = Convert.ToUInt64(reader.GetValue(2)),
                Wickets    = Convert.ToUInt64(reader.GetValue(3)),
                Boundaries = Convert.ToUInt64(reader.GetValue(4)),
                Sixes      = Convert.ToUInt64(reader.GetValue(5)),
                Dots       = Convert.ToUInt64(reader.GetValue(6)),
            });
        }
        return result;
    }

    // ── 2. Bowling phase (team as bowling side) ───────────────────────────────
    private static async Task<List<TeamBowlingPhaseRow>> GetBowlingPhaseAsync(
        ClickHouseConnection conn, uint teamId,
        uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct)
    {
        var where = BuildBowlingWhere(teamId, seasonId, leagueId, year, fromDate, toDate);
        var sql = $@"
            SELECT over_phase,
                   sum(toUInt64(runs_off_bat + extras_wide + extras_no_ball)) AS runs_conceded,
                   sum(toUInt64(is_legal_ball))   AS balls,
                   sum(toUInt64(is_wicket))       AS wickets,
                   sum(toUInt64(is_dot_ball))     AS dots,
                   countIf(extras_wide > 0)       AS wides,
                   countIf(extras_no_ball > 0)    AS no_balls
            FROM lms.ball_events
            {where}
            GROUP BY over_phase
            ORDER BY multiIf(over_phase='Powerplay',1, over_phase='Middle',2, 3)";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = new List<TeamBowlingPhaseRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TeamBowlingPhaseRow
            {
                Phase        = reader.GetString(0),
                RunsConceded = Convert.ToUInt64(reader.GetValue(1)),
                Balls        = Convert.ToUInt64(reader.GetValue(2)),
                Wickets      = Convert.ToUInt64(reader.GetValue(3)),
                Dots         = Convert.ToUInt64(reader.GetValue(4)),
                Wides        = Convert.ToUInt64(reader.GetValue(5)),
                NoBalls      = Convert.ToUInt64(reader.GetValue(6)),
            });
        }
        return result;
    }

    // ── 3. Benchmark: team vs league average ──────────────────────────────────
    // League avg run rate per phase = league-wide runs_off_bat / legal_balls * 5.
    // This is simultaneously the batting benchmark AND the bowling benchmark baseline
    // (every batting ball is a bowling ball in the same game).
    //
    // BattingEdge = team batting run rate − league avg  (positive = team bats above average)
    // BowlingEdge = league avg − team bowling economy   (positive = team concedes less than average)
    private static async Task<List<PhaseBenchmarkRow>> GetBenchmarkAsync(
        ClickHouseConnection conn, uint teamId,
        uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
        List<TeamBattingPhaseRow> battingPhase, List<TeamBowlingPhaseRow> bowlingPhase,
        CancellationToken ct)
    {
        // Resolve league_id if not supplied — pick the team's most-common league from ball_events
        uint resolvedLeague = leagueId ?? await GetTeamPrimaryLeagueAsync(conn, teamId, ct);
        if (resolvedLeague == 0) return [];

        // Use the fast MV path when there is no date filter
        bool hasDateFilter = year.HasValue || fromDate.HasValue || toDate.HasValue;
        Dictionary<string, (double runs, double balls)> leagueAvg = hasDateFilter
            ? await GetLeagueAvgFromBallEventsAsync(conn, resolvedLeague, seasonId, year, fromDate, toDate, ct)
            : await GetLeagueAvgFromMvAsync(conn, resolvedLeague, seasonId, ct);

        var battingMap  = battingPhase.ToDictionary(r => r.Phase);
        var bowlingMap  = bowlingPhase.ToDictionary(r => r.Phase);

        var result = new List<PhaseBenchmarkRow>();
        foreach (var phase in new[] { "Powerplay", "Middle", "Death" })
        {
            double leagueRate = leagueAvg.TryGetValue(phase, out var la) && la.balls > 0
                ? Math.Round(la.runs / la.balls * 5, 2)
                : 0.0;

            result.Add(new PhaseBenchmarkRow
            {
                Phase              = phase,
                TeamBattingRunRate = battingMap.TryGetValue(phase, out var bat) ? bat.RunRate : 0,
                LeagueAvgRunRate   = leagueRate,
                TeamBowlingEconomy = bowlingMap.TryGetValue(phase, out var bwl) ? bwl.Economy : 0,
                LeagueAvgEconomy   = leagueRate,
            });
        }
        return result;
    }

    private static async Task<uint> GetTeamPrimaryLeagueAsync(
        ClickHouseConnection conn, uint teamId, CancellationToken ct)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT league_id, count() AS cnt
            FROM lms.ball_events
            WHERE batting_team_id = {teamId}
            GROUP BY league_id
            ORDER BY cnt DESC
            LIMIT 1";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Convert.ToUInt32(reader.GetValue(0)) : 0u;
    }

    /// <summary>Fast path — reads from the pre-aggregated lms.league_avg SummingMergeTree MV.</summary>
    private static async Task<Dictionary<string, (double runs, double balls)>> GetLeagueAvgFromMvAsync(
        ClickHouseConnection conn, uint leagueId, uint? seasonId, CancellationToken ct)
    {
        var parts = new List<string> { $"league_id = {leagueId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        var where = "WHERE " + string.Join(" AND ", parts);

        using var cmd = conn.CreateCommand();
        // lms.league_avg MV column names are 'runs' and 'legal_balls'
        // (mapped from ball_events.runs_off_bat and ball_events.is_legal_ball)
        cmd.CommandText = $@"
            SELECT over_phase,
                   sum(runs)        AS runs,
                   sum(legal_balls) AS balls
            FROM lms.league_avg
            {where}
            GROUP BY over_phase";

        return await ReadLeagueAvgDict(cmd, ct);
    }

    /// <summary>Date-filtered path — queries ball_events directly for a consistent time range.</summary>
    private static async Task<Dictionary<string, (double runs, double balls)>> GetLeagueAvgFromBallEventsAsync(
        ClickHouseConnection conn, uint leagueId,
        uint? seasonId, int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct)
    {
        var parts = new List<string> { $"league_id = {leagueId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (year.HasValue)     parts.Add($"toYear(game_date) = {year}");
        if (fromDate.HasValue) parts.Add($"game_date >= '{fromDate:yyyy-MM-dd}'");
        if (toDate.HasValue)   parts.Add($"game_date <= '{toDate:yyyy-MM-dd}'");
        var where = "WHERE " + string.Join(" AND ", parts);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT over_phase,
                   sum(toUInt64(runs_off_bat))  AS runs,
                   sum(toUInt64(is_legal_ball)) AS balls
            FROM lms.ball_events
            {where}
            GROUP BY over_phase";

        return await ReadLeagueAvgDict(cmd, ct);
    }

    private static async Task<Dictionary<string, (double runs, double balls)>> ReadLeagueAvgDict(
        System.Data.Common.DbCommand cmd, CancellationToken ct)
    {
        var result = new Dictionary<string, (double, double)>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result[reader.GetString(0)] = (
                Convert.ToDouble(reader.GetValue(1)),
                Convert.ToDouble(reader.GetValue(2)));
        }
        return result;
    }

    // ── 4. Top batting partnerships (within team) ─────────────────────────────
    private static async Task<List<TeamPartnershipRow>> GetTopPartnershipsAsync(
        ClickHouseConnection conn, uint teamId,
        uint? seasonId, uint? leagueId, int? year, DateOnly? fromDate, DateOnly? toDate,
        CancellationToken ct)
    {
        var parts = new List<string> { $"batting_team_id = {teamId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        if (year.HasValue)     parts.Add($"toYear(game_date) = {year}");
        if (fromDate.HasValue) parts.Add($"game_date >= '{fromDate:yyyy-MM-dd}'");
        if (toDate.HasValue)   parts.Add($"game_date <= '{toDate:yyyy-MM-dd}'");
        var where = "WHERE " + string.Join(" AND ", parts);

        var sql = $@"
            SELECT batter1_id,
                   batter2_id,
                   count()                       AS partnership_count,
                   sum(toUInt64(runs_together))  AS total_runs,
                   sum(toUInt64(balls_together)) AS total_balls,
                   sum(toUInt64(fours_together)) AS total_fours,
                   sum(toUInt64(sixes_together)) AS total_sixes
            FROM lms.partnerships
            {where}
            GROUP BY batter1_id, batter2_id
            HAVING total_balls >= 10
            ORDER BY total_runs DESC
            LIMIT 10";

        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        var result = new List<TeamPartnershipRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            result.Add(new TeamPartnershipRow
            {
                Batter1Id        = Convert.ToUInt32(reader.GetValue(0)),
                Batter2Id        = Convert.ToUInt32(reader.GetValue(1)),
                PartnershipCount = Convert.ToInt64(reader.GetValue(2)),
                TotalRuns        = Convert.ToUInt64(reader.GetValue(3)),
                TotalBalls       = Convert.ToUInt64(reader.GetValue(4)),
                TotalFours       = Convert.ToUInt64(reader.GetValue(5)),
                TotalSixes       = Convert.ToUInt64(reader.GetValue(6)),
            });
        }
        return result;
    }

    // ── 5. Club Greats (all-time, no season/date filter) ─────────────────────
    private static async Task<TeamClubGreats> GetClubGreatsAsync(
        ClickHouseConnection conn, uint teamId, CancellationToken ct)
    {
        return new TeamClubGreats
        {
            MostRuns = await GetGreatAsync(conn, ct, $@"
                SELECT striker_id,
                       sum(toUInt64(runs_off_bat)) AS val,
                       count(DISTINCT fixture_id)  AS games
                FROM lms.ball_events
                WHERE batting_team_id = {teamId}
                GROUP BY striker_id
                ORDER BY val DESC LIMIT 1"),

            MostWickets = await GetGreatAsync(conn, ct, $@"
                SELECT bowler_id,
                       sum(toUInt64(is_wicket))   AS val,
                       count(DISTINCT fixture_id) AS games
                FROM lms.ball_events
                WHERE bowling_team_id = {teamId}
                GROUP BY bowler_id
                ORDER BY val DESC LIMIT 1"),

            // Appearances = distinct fixtures across both batting and bowling roles
            MostAppearances = await GetGreatAsync(conn, ct, $@"
                SELECT player_id,
                       count(DISTINCT fixture_id) AS val,
                       count(DISTINCT fixture_id) AS games
                FROM (
                    SELECT striker_id AS player_id, fixture_id
                    FROM lms.ball_events WHERE batting_team_id = {teamId}
                    UNION ALL
                    SELECT bowler_id AS player_id, fixture_id
                    FROM lms.ball_events WHERE bowling_team_id = {teamId}
                )
                GROUP BY player_id
                ORDER BY val DESC LIMIT 1"),

            MostSixes = await GetGreatAsync(conn, ct, $@"
                SELECT striker_id,
                       sum(toUInt64(is_six))      AS val,
                       count(DISTINCT fixture_id) AS games
                FROM lms.ball_events
                WHERE batting_team_id = {teamId}
                GROUP BY striker_id
                ORDER BY val DESC LIMIT 1"),

            // Catches: bowling_team_id filter; wicket_type case-insensitive 'caught'
            // (exact string depends on event parser — update if a different casing is used)
            MostCatches = await GetGreatAsync(conn, ct, $@"
                SELECT fielder_id,
                       count()                    AS val,
                       count(DISTINCT fixture_id) AS games
                FROM lms.ball_events
                WHERE bowling_team_id = {teamId}
                  AND is_wicket = 1
                  AND lower(wicket_type) = 'caught'
                  AND fielder_id > 0
                GROUP BY fielder_id
                ORDER BY val DESC LIMIT 1"),

            MostHomeRuns = await GetGreatAsync(conn, ct, $@"
                SELECT striker_id,
                       sum(toUInt64(home_runs))   AS val,
                       count(DISTINCT fixture_id) AS games
                FROM lms.ball_events
                WHERE batting_team_id = {teamId}
                GROUP BY striker_id
                ORDER BY val DESC LIMIT 1"),
        };
    }

    private static async Task<ClubGreatEntry?> GetGreatAsync(
        ClickHouseConnection conn, CancellationToken ct, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ClubGreatEntry
        {
            PlayerId = Convert.ToUInt32(reader.GetValue(0)),
            Value    = Convert.ToUInt64(reader.GetValue(1)),
            Games    = Convert.ToUInt64(reader.GetValue(2)),
        };
    }

    // ── WHERE clause builders ─────────────────────────────────────────────────
    private static string BuildBattingWhere(
        uint teamId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate)
    {
        var parts = new List<string> { $"batting_team_id = {teamId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        if (year.HasValue)     parts.Add($"toYear(game_date) = {year}");
        if (fromDate.HasValue) parts.Add($"game_date >= '{fromDate:yyyy-MM-dd}'");
        if (toDate.HasValue)   parts.Add($"game_date <= '{toDate:yyyy-MM-dd}'");
        return "WHERE " + string.Join(" AND ", parts);
    }

    private static string BuildBowlingWhere(
        uint teamId, uint? seasonId, uint? leagueId,
        int? year, DateOnly? fromDate, DateOnly? toDate)
    {
        var parts = new List<string> { $"bowling_team_id = {teamId}" };
        if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
        if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
        if (year.HasValue)     parts.Add($"toYear(game_date) = {year}");
        if (fromDate.HasValue) parts.Add($"game_date >= '{fromDate:yyyy-MM-dd}'");
        if (toDate.HasValue)   parts.Add($"game_date <= '{toDate:yyyy-MM-dd}'");
        return "WHERE " + string.Join(" AND ", parts);
    }
}
