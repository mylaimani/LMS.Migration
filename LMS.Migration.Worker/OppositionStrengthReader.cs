// By Mani
using ClickHouse.Client.ADO;
using ClickHouse.Client.ADO.Parameters;
using LMS.Migration.Core.OppositionStrength;
using Microsoft.Data.SqlClient;

namespace LMS.Migration.Worker
{
    /// <summary>
    /// Inputs for Category 1 (ball-by-ball opposition uplift):
    ///   - balls per batsman v bowler from ClickHouse ball_events (no FixtureState / SQL Server load)
    ///   - player ratings + reliability from Rep_PlayerSummaryRating (staging DB only for now)
    /// </summary>
    public class OppositionStrengthReader
    {
        // LMS over-counting rule. Same predicate as BallRules.CountsAsBall in LMST20 and
        // BallEvent.CountsAsBall in the parser: the first wide/no-ball in an over is re-bowled
        // (scored 1, not a ball); a later one counts as a ball (scored 3+).
        private const string CountsAsBall = "(is_legal_ball = 1 OR extras_wide >= 3 OR extras_no_ball >= 3)";

        private readonly string _clickHouseConnectionString;
        private readonly string _clickHouseDatabase;
        private readonly string _ratingSqlConnectionString;

        public OppositionStrengthReader(string clickHouseConnectionString, string clickHouseDatabase, string ratingSqlConnectionString)
        {
            _clickHouseConnectionString = clickHouseConnectionString;
            _clickHouseDatabase = clickHouseDatabase;
            _ratingSqlConnectionString = ratingSqlConnectionString;
        }

        /// <summary>Balls each batsman faced from each bowler in one fixture. Balls with no striker/bowler are skipped.</summary>
        public async Task<List<BatsmanBowlerBalls>> ReadBallsPerBatsmanAndBowlerAsync(uint fixtureId)
        {
            using var connection = new ClickHouseConnection(_clickHouseConnectionString);
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = BuildBallsPerBatsmanAndBowlerSql();
            command.Parameters.Add(new ClickHouseDbParameter { ParameterName = "fixtureId", Value = fixtureId });

            var ballsPerPair = new List<BatsmanBowlerBalls>();
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ballsPerPair.Add(new BatsmanBowlerBalls
                {
                    FixtureId = fixtureId,
                    BatsmanId = Convert.ToUInt32(reader.GetValue(reader.GetOrdinal("batsman_id"))),
                    BowlerId = Convert.ToUInt32(reader.GetValue(reader.GetOrdinal("bowler_id"))),
                    BattingTeamId = Convert.ToUInt32(reader.GetValue(reader.GetOrdinal("batting_team_id"))),
                    BowlingTeamId = Convert.ToUInt32(reader.GetValue(reader.GetOrdinal("bowling_team_id"))),
                    GameDate = ReadGameDate(reader.GetValue(reader.GetOrdinal("game_date"))),
                    BallsCounted = Convert.ToInt32(reader.GetValue(reader.GetOrdinal("balls_counted")))
                });
            }
            return ballsPerPair;
        }

        /// <summary>ClickHouse Date can arrive as DateTime or DateOnly depending on the client settings.</summary>
        private static DateTime ReadGameDate(object value)
        {
            if (value is DateOnly dateOnly)
            {
                return dateOnly.ToDateTime(TimeOnly.MinValue);
            }
            return Convert.ToDateTime(value);
        }

        private string BuildBallsPerBatsmanAndBowlerSql()
        {
            return $@"
                SELECT
                    striker_id      AS batsman_id,
                    bowler_id,
                    any(batting_team_id) AS batting_team_id,
                    any(bowling_team_id) AS bowling_team_id,
                    any(game_date)  AS game_date,
                    sum(toUInt64({CountsAsBall})) AS balls_counted
                FROM {_clickHouseDatabase}.ball_events
                WHERE fixture_id = {{fixtureId:UInt32}}
                  AND striker_id != 0
                  AND bowler_id != 0
                GROUP BY striker_id, bowler_id
                HAVING balls_counted > 0";
        }

        /// <summary>
        /// Each player's rating and reliability as it was BEFORE this fixture, from Abser's
        /// Rep_PlayerRatingHistory (one row per player per fixture, holding the values AFTER that match).
        /// "Before" uses the same rule as Abser's procedures: an earlier MatchDate, or the same MatchDate
        /// with a lower FixtureId. A player with no earlier row (first match) is absent, so counts as neutral 5.0.
        /// </summary>
        public async Task<Dictionary<uint, PlayerRatingSnapshot>> ReadRatingsBeforeFixtureAsync(uint fixtureId, List<uint> playerIds)
        {
            var ratingsByPlayerId = new Dictionary<uint, PlayerRatingSnapshot>();
            if (playerIds.Count == 0)
            {
                return ratingsByPlayerId;
            }

            using var connection = new SqlConnection(_ratingSqlConnectionString);
            await connection.OpenAsync();
            using var command = new SqlCommand(BuildRatingsBeforeFixtureSql(playerIds), connection);
            command.Parameters.AddWithValue("@FixtureId", (int)fixtureId);
            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                uint playerId = (uint)reader.GetInt32(0);
                ratingsByPlayerId[playerId] = new PlayerRatingSnapshot
                {
                    PlayerId = playerId,
                    BatRatingLevel = reader.IsDBNull(1) ? OppositionStrengthCalculator.NeutralRating : reader.GetDecimal(1),
                    BowlRatingLevel = reader.IsDBNull(2) ? OppositionStrengthCalculator.NeutralRating : reader.GetDecimal(2),
                    ReliabilityPercent = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3)
                };
            }
            return ratingsByPlayerId;
        }

        private static string BuildRatingsBeforeFixtureSql(List<uint> playerIds)
        {
            // Ids are uint values from ClickHouse, so writing them into the VALUES list is safe.
            string playerIdRows = string.Join(",", playerIds.Select(playerId => $"({playerId})"));

            return $@"
                DECLARE @FixtureDate DATETIME =
                    (SELECT TOP 1 MatchDate FROM dbo.Rep_PlayerRatingHistory
                     WHERE FixtureId = @FixtureId AND MatchDate IS NOT NULL);
                IF @FixtureDate IS NULL
                    SET @FixtureDate = (SELECT [DateTime] FROM dbo.Fixture WHERE Id = @FixtureId);

                SELECT player.UserId,
                       beforeFixture.BatRatingLevel,
                       beforeFixture.BowlRatingLevel,
                       beforeFixture.Reliability
                FROM (VALUES {playerIdRows}) AS player(UserId)
                CROSS APPLY (
                    SELECT TOP 1 history.BatRatingLevel, history.BowlRatingLevel, history.Reliability
                    FROM dbo.Rep_PlayerRatingHistory history
                    WHERE history.UserId = player.UserId
                      AND (ISNULL(history.MatchDate, '19000101') < @FixtureDate
                           OR (ISNULL(history.MatchDate, '19000101') = @FixtureDate AND history.FixtureId < @FixtureId))
                    ORDER BY ISNULL(history.MatchDate, '19000101') DESC, history.FixtureId DESC, history.PlayerRatingHistoryId DESC
                ) AS beforeFixture;";
        }
    }
}
// End of Mani
