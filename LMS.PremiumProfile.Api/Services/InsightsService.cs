using ClickHouse.Client.ADO;
using LMS.PremiumProfile.Api.Models;

namespace LMS.PremiumProfile.Api.Services;

/// <summary>
/// Fetches H2H, Pulse, and Clips data from ClickHouse.
///
/// Table reference:
///   lms.h2h_stats   — SummingMergeTree MV pre-aggregated by bowler_id + striker_id.
///                     Use sum() when reading (parts may not yet be merged).
///                     Only career (no date/league columns) — fall back to ball_events for filtered queries.
///   lms.ball_events — raw; used for filtered H2H queries and Pulse (pulse_after_pct, pulse_change_pct).
///   lms.clips       — highlight clips migrated from the SQL Server Highlights table.
///
/// IMPORTANT: ClickHouseConnection is NOT thread-safe — all queries run sequentially.
/// </summary>
public class InsightsService : IInsightsService
{
    private readonly string _connectionString;

    public InsightsService(string connectionString)
    {
        _connectionString = connectionString;
    }

    // ── H2H ──────────────────────────────────────────────────────────────────
    public async Task<H2HResponse> GetH2HAsync(
        uint bowlerId, uint batterId,
        uint? seasonId, uint? leagueId,
        CancellationToken ct = default)
    {
        using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        bool anyFilter = seasonId.HasValue || leagueId.HasValue;
        H2HStats stats;

        if (!anyFilter)
        {
            // Fast path — pre-aggregated MV (career, no date columns on h2h_stats)
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT
                    sum(legal_balls) AS balls,
                    sum(runs)        AS runs,
                    sum(wickets)     AS wickets,
                    sum(sixes)       AS sixes,
                    sum(boundaries)  AS boundaries,
                    sum(dots)        AS dots
                FROM lms.h2h_stats
                WHERE bowler_id = {bowlerId}
                  AND striker_id = {batterId}";
            stats = await ReadH2HStats(cmd, ct);
        }
        else
        {
            // Filtered path — ball_events
            var parts = new List<string>
            {
                $"bowler_id = {bowlerId}",
                $"striker_id = {batterId}"
            };
            if (seasonId.HasValue) parts.Add($"season_id = {seasonId}");
            if (leagueId.HasValue) parts.Add($"league_id = {leagueId}");
            var where = "WHERE " + string.Join(" AND ", parts);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
                SELECT
                    sum(toUInt64(is_legal_ball)) AS balls,
                    sum(toUInt64(runs_off_bat))  AS runs,
                    sum(toUInt64(is_wicket))     AS wickets,
                    sum(toUInt64(is_six))        AS sixes,
                    sum(toUInt64(is_boundary))   AS boundaries,
                    sum(toUInt64(is_dot_ball))   AS dots
                FROM lms.ball_events
                {where}";
            stats = await ReadH2HStats(cmd, ct);
        }

        return new H2HResponse
        {
            BowlerId = bowlerId,
            BatterId = batterId,
            SeasonId = seasonId,
            LeagueId = leagueId,
            Stats    = stats,
        };
    }

    private static async Task<H2HStats> ReadH2HStats(
        System.Data.Common.DbCommand cmd, CancellationToken ct)
    {
        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return new H2HStats();
        return new H2HStats
        {
            Balls      = Convert.ToUInt64(reader.GetValue(0)),
            Runs       = Convert.ToUInt64(reader.GetValue(1)),
            Wickets    = Convert.ToUInt64(reader.GetValue(2)),
            Sixes      = Convert.ToUInt64(reader.GetValue(3)),
            Boundaries = Convert.ToUInt64(reader.GetValue(4)),
            Dots       = Convert.ToUInt64(reader.GetValue(5)),
        };
    }

    // ── Pulse ─────────────────────────────────────────────────────────────────
    public async Task<PulseResponse> GetPulseAsync(uint fixtureId, CancellationToken ct = default)
    {
        using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                innings_number,
                over_number,
                ball_in_over,
                batting_team_id,
                striker_id,
                bowler_id,
                runs_off_bat,
                is_wicket,
                is_six,
                is_boundary,
                home_runs,
                pulse_after_pct,
                pulse_change_pct
            FROM lms.ball_events
            WHERE fixture_id = {fixtureId}
            ORDER BY innings_number, over_number, ball_in_over";

        var balls = new List<PulseBallRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            balls.Add(new PulseBallRow
            {
                InningsNumber = Convert.ToByte(reader.GetValue(0)),
                OverNumber    = Convert.ToInt32(reader.GetValue(1)),
                BallInOver    = Convert.ToInt32(reader.GetValue(2)),
                BattingTeamId = Convert.ToUInt32(reader.GetValue(3)),
                StrikerId     = Convert.ToUInt32(reader.GetValue(4)),
                BowlerId      = Convert.ToUInt32(reader.GetValue(5)),
                RunsOffBat    = Convert.ToInt32(reader.GetValue(6)),
                IsWicket      = Convert.ToBoolean(reader.GetValue(7)),
                IsSix         = Convert.ToBoolean(reader.GetValue(8)),
                IsBoundary    = Convert.ToBoolean(reader.GetValue(9)),
                IsHomeRun     = Convert.ToUInt32(reader.GetValue(10)) > 0,
                PulseAfterPct  = Convert.ToSingle(reader.GetValue(11)),
                PulseChangePct = Convert.ToSingle(reader.GetValue(12)),
            });
        }

        return new PulseResponse { FixtureId = fixtureId, Balls = balls };
    }

    // ── Clips ─────────────────────────────────────────────────────────────────
    // Returns highlight clips for one fixture, optionally filtered by clip type.
    public async Task<ClipsResponse> GetClipsAsync(
        uint fixtureId, string? clipType, CancellationToken ct = default)
    {
        using var conn = new ClickHouseConnection(_connectionString);
        await conn.OpenAsync(ct);

        var parts = new List<string> { $"fixture_id = {fixtureId}" };
        if (!string.IsNullOrWhiteSpace(clipType))
            parts.Add($"lower(clip_type) = '{clipType.Trim().ToLowerInvariant()}'");
        var where = "WHERE " + string.Join(" AND ", parts);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT
                clip_id,
                innings_number,
                over_number,
                ball_sequence,
                ball_timestamp,
                clip_url,
                clip_type,
                bowler_id,
                striker_id,
                non_striker_id,
                keeper_id,
                fielder_id,
                wicket_type,
                is_six,
                duration_secs
            FROM lms.clips
            {where}
            ORDER BY innings_number, over_number, ball_sequence";

        var clips = new List<ClipRow>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            clips.Add(new ClipRow
            {
                ClipId        = Convert.ToUInt64(reader.GetValue(0)),
                InningsNumber = Convert.ToByte(reader.GetValue(1)),
                OverNumber    = Convert.ToByte(reader.GetValue(2)),
                BallSequence  = Convert.ToByte(reader.GetValue(3)),
                BallTimestamp = Convert.ToDateTime(reader.GetValue(4)),
                ClipUrl       = reader.GetString(5),
                ClipType      = reader.GetString(6),
                BowlerId      = Convert.ToUInt32(reader.GetValue(7)),
                StrikerId     = Convert.ToUInt32(reader.GetValue(8)),
                NonStrikerId  = Convert.ToUInt32(reader.GetValue(9)),
                KeeperId      = Convert.ToUInt32(reader.GetValue(10)),
                FielderId     = Convert.ToUInt32(reader.GetValue(11)),
                WicketType    = reader.GetString(12),
                IsSix         = Convert.ToBoolean(reader.GetValue(13)),
                DurationSecs  = Convert.ToUInt16(reader.GetValue(14)),
            });
        }

        return new ClipsResponse { FixtureId = fixtureId, Clips = clips };
    }
}
