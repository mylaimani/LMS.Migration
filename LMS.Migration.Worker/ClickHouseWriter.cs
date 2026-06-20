using ClickHouse.Client.ADO;
using ClickHouse.Client.Copy;
using LMS.Migration.Core;
using LMS.Migration.Core.Models;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// Bulk inserts into ClickHouse. Column lists are explicit and EXCLUDE
    /// MATERIALIZED columns (is_legal_ball, is_dot_ball, is_boundary, is_six,
    /// over_phase) — ClickHouse computes those at insert time.
    /// </summary>
    public class ClickHouseWriter
    {
        private readonly string _connectionString;

        public ClickHouseWriter(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>All fixture ids already in ball_events (for catch-up reconciliation).</summary>
        public async Task<HashSet<uint>> GetAllFixtureIdsAsync()
        {
            var ids = new HashSet<uint>();
            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT fixture_id FROM lms.ball_events";
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                ids.Add(Convert.ToUInt32(reader.GetValue(0)));
            return ids;
        }

        /// <summary>
        /// Safe resume point with buffered inserts: the smallest of the
        /// per-table max fixture_ids (tables are flushed sequentially, so
        /// anything above the smallest max may be partially written).
        /// Returns 0 when the tables are empty.
        /// </summary>
        public async Task<uint> GetResumePointAsync()
        {
            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();

            uint Min(uint a, uint b) => a < b ? a : b;
            uint safe = uint.MaxValue;
            foreach (var table in new[] { "lms.ball_events", "lms.partnerships" })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT max(fixture_id) FROM {table}";
                var result = await cmd.ExecuteScalarAsync();
                var max = result == null || result == DBNull.Value ? 0u : Convert.ToUInt32(result);
                safe = Min(safe, max);
            }
            return safe == uint.MaxValue ? 0u : safe;
        }

        /// <summary>
        /// Truncates lms.partnerships — used before a partnerships-only rerun
        /// so the fixed parser rewrites clean data without MergeTree duplicates.
        /// </summary>
        public async Task TruncatePartnershipsAsync()
        {
            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "TRUNCATE TABLE lms.partnerships";
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>
        /// Removes any rows above the safe resume point (possibly partial
        /// flushes). Lightweight DELETE — requires ClickHouse 23.3+.
        /// </summary>
        public async Task DeleteFixturesAfterAsync(uint safeFixtureId)
        {
            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();
            foreach (var table in new[] { "lms.ball_events", "lms.partnerships", "lms.player_match_stats" })
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"DELETE FROM {table} WHERE fixture_id > {safeFixtureId}";
                await cmd.ExecuteNonQueryAsync();
            }
        }

        private static readonly string[] BallEventColumns =
        {
            "fixture_id", "innings_number", "over_number", "ball_sequence", "ball_timestamp",
            "bowler_id", "striker_id", "non_striker_id",
            "runs_off_bat", "extras_wide", "extras_no_ball", "extras_legbye", "extras_bye",
            "is_wicket", "wicket_type",
            "batting_position", "batting_team_id", "bowling_team_id",
            "score_at_ball", "wickets_at_ball", "game_date",
            "league_id", "division_id", "season_id", "season_name",
            "venue_id", "region_id", "country_id",
            "home_runs", "steal", "double_play", "balls_per_over", "total_overs", "pitch_condition",
            "fielder_id", "keeper_id",
            "pulse_after_pct", "pulse_change_pct"
        };

        public async Task InsertBallEventsAsync(List<BallEvent> balls)
        {
            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();

            var bulkCopy = new ClickHouseBulkCopy(conn)
            {
                DestinationTableName = "lms.ball_events",
                ColumnNames = BallEventColumns,
                BatchSize = 10000
            };
            await bulkCopy.InitAsync();

            var rows = balls.Select(b => new object[]
            {
                b.FixtureId, b.InningsNumber, b.OverNumber, b.BallSequence, b.BallTimestamp,
                b.BowlerId, b.StrikerId, b.NonStrikerId,
                b.RunsOffBat, b.ExtrasWide, b.ExtrasNoBall, b.ExtrasLegBye, b.ExtrasBye,
                (byte)(b.IsWicket ? 1 : 0), b.WicketType ?? "",
                b.BattingPosition, b.BattingTeamId, b.BowlingTeamId,
                b.ScoreAtBall, b.WicketsAtBall, b.GameDate,
                b.LeagueId, b.DivisionId, b.SeasonId, b.SeasonName ?? "",
                b.VenueId, b.RegionId, b.CountryId,
                b.HomeRuns, b.Steal, b.DoublePlay, b.BallsPerOver, b.TotalOvers, b.PitchCondition,
                b.FielderId, b.KeeperId,
                b.PulseAfterPct, b.PulseChangePct
            });

            await bulkCopy.WriteToServerAsync(rows);
        }

        private static readonly string[] PartnershipColumns =
        {
            "fixture_id", "innings_number", "partnership_number",
            "batter1_id", "batter2_id", "batting_team_id", "bowling_team_id",
            "runs_together", "balls_together", "run_rate",
            "fours_together", "sixes_together",
            "start_over", "end_over", "over_phase", "game_date",
            "league_id", "division_id", "season_id", "season_name",
            "venue_id", "region_id"
        };

        public async Task InsertPartnershipsAsync(List<Partnership> partnerships)
        {
            if (partnerships.Count == 0) return;

            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();

            var bulkCopy = new ClickHouseBulkCopy(conn)
            {
                DestinationTableName = "lms.partnerships",
                ColumnNames = PartnershipColumns,
                BatchSize = 5000
            };
            await bulkCopy.InitAsync();

            var rows = partnerships.Select(p => new object[]
            {
                p.FixtureId, p.InningsNumber, p.PartnershipNumber,
                p.Batter1Id, p.Batter2Id, p.BattingTeamId, p.BowlingTeamId,
                p.RunsTogether, p.BallsTogether, p.RunRate,
                p.FoursTogether, p.SixesTogether,
                p.StartOver, p.EndOver, p.OverPhase, p.GameDate,
                p.LeagueId, p.DivisionId, p.SeasonId, p.SeasonName ?? "",
                p.VenueId, p.RegionId
            });

            await bulkCopy.WriteToServerAsync(rows);
        }

        private static readonly string[] PlayerMatchStatsColumns =
        {
            "fixture_id", "player_id", "team_id", "match_result", "is_no_result", "match_rpball",
            "batting_balls_faced", "batting_runs_scored", "batting_is_not_out",
            "batting_player_rpball", "batting_efficiency_ratio", "batting_base_points",
            "batting_after_win", "batting_not_out_bonus", "batting_raw_points",
            "batting_match_points", "batting_rating_impact",
            "bowling_balls_bowled", "bowling_runs_conceded", "bowling_wickets",
            "bowling_rpball", "bowling_improvement", "bowling_base_economy_pts",
            "bowling_scaling_factor", "bowling_weighted_economy_pts", "bowling_wicket_points",
            "bowling_base_points", "bowling_raw_points", "bowling_match_points", "bowling_rating_impact",
            "fielding_catches", "fielding_run_outs", "fielding_stumpings", "fielding_double_plays",
            "fielding_points",
            "all_rounder_match_points", "opposition_strength", "league_strength_weighting",
            "participation_points", "legends_points",
            "league_id", "division_id", "season_id", "season_name",
            "venue_id", "region_id", "country_id", "game_date"
        };

        public async Task InsertPlayerMatchStatsAsync(List<PlayerMatchStats> stats)
        {
            if (stats.Count == 0) return;

            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();

            var bulkCopy = new ClickHouseBulkCopy(conn)
            {
                DestinationTableName = "lms.player_match_stats",
                ColumnNames = PlayerMatchStatsColumns,
                BatchSize = 5000
            };
            await bulkCopy.InitAsync();

            var rows = stats.Select(s => new object?[]
            {
                s.FixtureId, s.PlayerId, s.TeamId, s.MatchResult, s.IsNoResult, s.MatchRpball,
                s.BattingBallsFaced, s.BattingRunsScored, s.BattingIsNotOut,
                s.BattingPlayerRpball, s.BattingEfficiencyRatio, s.BattingBasePoints,
                s.BattingAfterWin, s.BattingNotOutBonus, s.BattingRawPoints,
                s.BattingMatchPoints, s.BattingRatingImpact,
                s.BowlingBallsBowled, s.BowlingRunsConceded, s.BowlingWickets,
                s.BowlingRpball, s.BowlingImprovement, s.BowlingBaseEconomyPts,
                s.BowlingScalingFactor, s.BowlingWeightedEconomyPts, s.BowlingWicketPoints,
                s.BowlingBasePoints, s.BowlingRawPoints, s.BowlingMatchPoints, s.BowlingRatingImpact,
                s.FieldingCatches, s.FieldingRunOuts, s.FieldingStumpings, s.FieldingDoublePlays,
                s.FieldingPoints,
                s.AllRounderMatchPoints, s.OppositionStrength, s.LeagueStrengthWeighting,
                s.ParticipationPoints, s.LegendsPoints,
                s.LeagueId, s.DivisionId, s.SeasonId, s.SeasonName ?? "",
                s.VenueId, s.RegionId, s.CountryId, s.GameDate
            });

            await bulkCopy.WriteToServerAsync(rows);
        }

        private static readonly string[] ClipColumns =
        {
            "clip_id", "fixture_id", "innings_number", "over_number", "ball_sequence",
            "ball_timestamp", "clip_url", "clip_type",
            "bowler_id", "striker_id", "non_striker_id", "keeper_id", "fielder_id",
            "wicket_type", "is_six", "duration_secs",
            "league_id", "season_id", "game_date"
        };

        public async Task InsertClipsAsync(List<ClipRecord> clips)
        {
            if (clips.Count == 0) return;

            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();

            var bulkCopy = new ClickHouseBulkCopy(conn)
            {
                DestinationTableName = "lms.clips",
                ColumnNames = ClipColumns,
                BatchSize = 10000
            };
            await bulkCopy.InitAsync();

            var rows = clips.Select(c => new object[]
            {
                c.ClipId, c.FixtureId, c.InningsNumber, c.OverNumber, c.BallSequence,
                c.BallTimestamp, c.ClipUrl ?? "", c.ClipType ?? "",
                c.BowlerId, c.StrikerId, c.NonStrikerId, c.KeeperId, c.FielderId,
                c.WicketType ?? "", c.IsSix, c.DurationSecs,
                c.LeagueId, c.SeasonId, c.GameDate
            });

            await bulkCopy.WriteToServerAsync(rows);
        }

        private static readonly string[] PlayerRatingColumns =
        {
            "player_id",
            "batting_games_used", "batting_total_adjusted_pts", "batting_apg", "batting_z_score",
            "batting_base_rating", "batting_rating", "batting_max_cap",
            "batting_prev_rating", "batting_rating_change",
            "bowling_games_used", "bowling_total_adjusted_pts", "bowling_apg", "bowling_z_score",
            "bowling_base_rating", "bowling_rating", "bowling_max_cap",
            "bowling_prev_rating", "bowling_rating_change",
            "all_rounder_rating", "games_played", "unique_teams_faced", "reliability_pct",
            "population_mean_batting", "population_stddev_batting",
            "population_mean_bowling", "population_stddev_bowling",
            "last_updated_fixture_id", "last_updated"
        };

        public async Task InsertPlayerRatingsAsync(List<PlayerRating> ratings)
        {
            if (ratings.Count == 0) return;

            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();

            var bulkCopy = new ClickHouseBulkCopy(conn)
            {
                DestinationTableName = "lms.player_ratings",
                ColumnNames = PlayerRatingColumns,
                BatchSize = 10000
            };
            await bulkCopy.InitAsync();

            var rows = ratings.Select(r => new object[]
            {
                r.PlayerId,
                r.BattingGamesUsed, r.BattingTotalAdjustedPts, r.BattingApg, r.BattingZScore,
                r.BattingBaseRating, r.BattingRating, r.BattingMaxCap,
                r.BattingPrevRating, r.BattingRatingChange,
                r.BowlingGamesUsed, r.BowlingTotalAdjustedPts, r.BowlingApg, r.BowlingZScore,
                r.BowlingBaseRating, r.BowlingRating, r.BowlingMaxCap,
                r.BowlingPrevRating, r.BowlingRatingChange,
                r.AllRounderRating, r.GamesPlayed, r.UniqueTeamsFaced, r.ReliabilityPct,
                r.PopulationMeanBatting, r.PopulationStddevBatting,
                r.PopulationMeanBowling, r.PopulationStddevBowling,
                r.LastUpdatedFixtureId, r.LastUpdated
            });

            await bulkCopy.WriteToServerAsync(rows);
        }

        private static readonly string[] LeagueRankingColumns =
        {
            "player_id", "team_id", "league_id", "division_id", "season_id", "season_name",
            "batting_total_points", "bowling_total_points", "fielding_total_points",
            "all_rounder_total_points",
            "batting_rank", "bowling_rank", "all_rounder_rank",
            "games_played", "last_fixture_id", "last_updated"
        };

        public async Task InsertLeagueRankingsAsync(List<LeagueRankingEntry> entries)
        {
            if (entries.Count == 0) return;

            using var conn = new ClickHouseConnection(_connectionString);
            await conn.OpenAsync();

            var bulkCopy = new ClickHouseBulkCopy(conn)
            {
                DestinationTableName = "lms.league_rankings",
                ColumnNames = LeagueRankingColumns,
                BatchSize = 10000
            };
            await bulkCopy.InitAsync();

            var rows = entries.Select(e => new object[]
            {
                e.PlayerId, e.TeamId, e.LeagueId, e.DivisionId, e.SeasonId, e.SeasonName ?? "",
                e.BattingTotalPoints, e.BowlingTotalPoints, e.FieldingTotalPoints,
                e.AllRounderTotalPoints,
                e.BattingRank, e.BowlingRank, e.AllRounderRank,
                e.GamesPlayed, e.LastFixtureId, e.LastUpdated
            });

            await bulkCopy.WriteToServerAsync(rows);
        }
    }
}
